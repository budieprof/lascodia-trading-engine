using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically scans for orders in <see cref="OrderStatus.PartialFill"/> state where the
/// filled quantity is less than the requested quantity. For each, it creates a new residual
/// order for the unfilled remainder so the EA can attempt execution of the remaining lots.
/// </summary>
public class PartialFillResubmissionWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPartialFillAge = TimeSpan.FromMinutes(30);
    private const int MaxBackoffSeconds = 300; // 5 minutes

    private readonly ILogger<PartialFillResubmissionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics _metrics;
    private int _consecutiveErrors;

    public PartialFillResubmissionWorker(
        ILogger<PartialFillResubmissionWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics metrics)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PartialFillResubmissionWorker starting (poll={Poll}s, maxAge={Age}m)",
            PollInterval.TotalSeconds, MaxPartialFillAge.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPartialFillsAsync(stoppingToken);
                _consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                int backoffSecs = Math.Min(
                    (int)(PollInterval.TotalSeconds * Math.Pow(2, _consecutiveErrors - 1)),
                    MaxBackoffSeconds);
                _logger.LogError(ex,
                    "PartialFillResubmissionWorker: error in polling loop (consecutive={Count}), backing off {Backoff}s",
                    _consecutiveErrors, backoffSecs);
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "PartialFillResubmission"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
                await Task.Delay(TimeSpan.FromSeconds(backoffSecs), stoppingToken);
                continue;
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("PartialFillResubmissionWorker stopped");
    }

    private async Task ProcessPartialFillsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();

        var cutoff = DateTime.UtcNow - MaxPartialFillAge;

        // Find orders that are partially filled and not yet resubmitted
        var partialOrders = await db.Set<Order>()
            .Where(o => o.Status == OrderStatus.PartialFill
                      && o.FilledQuantity != null
                      && o.FilledQuantity > 0
                      && o.FilledQuantity < o.Quantity
                      && o.CreatedAt >= cutoff
                      && !o.IsDeleted)
            .ToListAsync(ct);

        if (partialOrders.Count == 0)
            return;

        int resubmitted = 0;

        foreach (var original in partialOrders)
        {
            try
            {
                var remainingQty = original.Quantity - (original.FilledQuantity ?? 0);
                if (remainingQty <= 0)
                    continue;

                // Check if a residual order already exists for this parent
                var alreadyResubmitted = await db.Set<Order>()
                    .AnyAsync(o => o.ParentOrderId == original.Id
                                && o.Status != OrderStatus.Cancelled
                                && o.Status != OrderStatus.Rejected
                                && !o.IsDeleted, ct);

                if (alreadyResubmitted)
                    continue;

                // Optimistic concurrency check: re-read the order status immediately
                // before creating the residual to prevent a race where the order was
                // fully filled between the initial query and now.
                var currentOrder = await db.Set<Order>().FindAsync(new object[] { original.Id }, ct);
                if (currentOrder == null || currentOrder.Status == OrderStatus.Filled)
                    continue;

                var residualOrder = new Order
                {
                    Symbol        = original.Symbol,
                    OrderType     = original.OrderType,
                    ExecutionType = original.ExecutionType,
                    Quantity      = remainingQty,
                    Price         = original.Price,
                    StopLoss      = original.StopLoss,
                    TakeProfit    = original.TakeProfit,
                    Status        = OrderStatus.Pending,
                    ParentOrderId = original.Id,
                    StrategyId    = original.StrategyId,
                    TradeSignalId = original.TradeSignalId,
                    TradingAccountId = original.TradingAccountId,
                    CreatedAt     = DateTime.UtcNow,
                };

                await db.Set<Order>().AddAsync(residualOrder, ct);
                resubmitted++;

                _logger.LogInformation(
                    "PartialFillResubmissionWorker: created residual order for {Symbol} " +
                    "(original={OriginalId}, filled={Filled}/{Total}, remaining={Remaining})",
                    original.Symbol, original.Id, original.FilledQuantity, original.Quantity, remainingQty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PartialFillResubmissionWorker: failed to create residual order for {OrderId} ({Symbol}) — skipping",
                    original.Id, original.Symbol);
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "PartialFillResubmission"),
                    new KeyValuePair<string, object?>("reason", "per_order_error"));
            }
        }

        if (resubmitted > 0)
        {
            await writeContext.SaveChangesAsync(ct);
            _metrics.OrdersSubmitted.Add(resubmitted,
                new KeyValuePair<string, object?>("source", "partial_fill_resubmission"));
        }
    }
}
