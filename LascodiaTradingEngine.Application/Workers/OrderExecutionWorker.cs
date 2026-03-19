using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Orders.Commands.SubmitOrder;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

// Rate-limit key used to guard all outgoing broker order-submission calls.
// The budget (default 30 per minute) should be kept well below the broker's
// published API rate limit to leave headroom for other API calls (pricing, account).
// Adjust via EngineConfig key "RateLimit:BrokerOrdersPerMinute".

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Background service that polls for Pending orders every 5 seconds, submits them to the
/// broker via <c>SubmitOrderCommand</c>, and — for any order that fills immediately —
/// automatically creates an <c>ExecutionQualityLog</c> and publishes an
/// <c>OrderFilledIntegrationEvent</c> so downstream consumers can react without polling.
/// </summary>
public class OrderExecutionWorker : BackgroundService
{
    private const string RateLimitKey      = "broker:orders";
    private const int    DefaultMaxPerMin  = 30;

    private readonly ILogger<OrderExecutionWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly IRateLimiter _rateLimiter;

    public OrderExecutionWorker(
        ILogger<OrderExecutionWorker> logger,
        IServiceScopeFactory scopeFactory,
        IEventBus eventBus,
        IRateLimiter rateLimiter)
    {
        _logger      = logger;
        _scopeFactory = scopeFactory;
        _eventBus    = eventBus;
        _rateLimiter = rateLimiter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderExecutionWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await ProcessPendingOrdersAsync(stoppingToken);
        }
    }

    private async Task ProcessPendingOrdersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope      = _scopeFactory.CreateScope();
            var readContext      = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var writeContext     = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var mediator         = scope.ServiceProvider.GetRequiredService<IMediator>();

            // Read configurable rate-limit budget (falls back to 30/min if not set)
            int maxPerMin = DefaultMaxPerMin;
            var cfgEntry  = await readContext.GetDbContext()
                .Set<EngineConfig>()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == "RateLimit:BrokerOrdersPerMinute", cancellationToken);
            if (cfgEntry?.Value is not null && int.TryParse(cfgEntry.Value, out var parsed))
                maxPerMin = parsed;

            var pendingIds = await readContext.GetDbContext()
                .Set<Order>()
                .Where(x => x.Status == OrderStatus.Pending && !x.IsDeleted)
                .Select(x => x.Id)
                .Take(20)
                .ToListAsync(cancellationToken);

            foreach (var id in pendingIds)
            {
                try
                {
                    // ── Rate-limit guard ────────────────────────────────────────
                    bool allowed = await _rateLimiter.TryAcquireAsync(
                        RateLimitKey, maxPerMin, TimeSpan.FromMinutes(1), cancellationToken);

                    if (!allowed)
                    {
                        _logger.LogWarning(
                            "OrderExecutionWorker: broker rate limit reached ({Max}/min) — " +
                            "deferring remaining {Count} order(s) to next cycle.",
                            maxPerMin, pendingIds.Count - pendingIds.IndexOf(id));
                        break;
                    }

                    var submitAt = DateTime.UtcNow;
                    await mediator.Send(new SubmitOrderCommand { Id = id }, cancellationToken);

                    // Reload from write-side to inspect post-fill state
                    var order = await writeContext.GetDbContext()
                        .Set<Order>()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);

                    if (order is { Status: OrderStatus.Filled, FilledPrice: not null })
                    {
                        await RecordExecutionQualityAsync(writeContext, order, submitAt, cancellationToken);
                        PublishOrderFilledEvent(order, submitAt);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error submitting order {OrderId}", id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OrderExecutionWorker.ProcessPendingOrdersAsync");
        }
    }

    /// <summary>
    /// Creates an <see cref="ExecutionQualityLog"/> record for the filled order, capturing
    /// slippage (in pips) and fill latency.
    /// </summary>
    private async Task RecordExecutionQualityAsync(
        IWriteApplicationDbContext writeContext,
        Order order,
        DateTime submitAt,
        CancellationToken ct)
    {
        var fillMs     = (long)(DateTime.UtcNow - submitAt).TotalMilliseconds;
        var fillRate   = order.FilledQuantity.HasValue && order.Quantity > 0
            ? order.FilledQuantity.Value / order.Quantity
            : 1m;

        // Standard 5-decimal FX pip factor (10,000); direction sign: Buy = +1, Sell = −1
        const decimal pipFactor   = 10_000m;
        decimal directionSign     = order.OrderType == OrderType.Buy ? 1m : -1m;
        decimal slippagePips      = (order.FilledPrice!.Value - order.Price) * pipFactor * directionSign;

        var qualityLog = new ExecutionQualityLog
        {
            OrderId        = order.Id,
            StrategyId     = order.StrategyId > 0 ? order.StrategyId : null,
            Symbol         = order.Symbol,
            Session        = order.Session,
            RequestedPrice = order.Price,
            FilledPrice    = order.FilledPrice.Value,
            SlippagePips   = slippagePips,
            SubmitToFillMs = fillMs,
            WasPartialFill = fillRate < 1m,
            FillRate       = fillRate,
            RecordedAt     = DateTime.UtcNow
        };

        await writeContext.GetDbContext().Set<ExecutionQualityLog>().AddAsync(qualityLog, ct);
        await writeContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "OrderExecutionWorker: execution quality recorded for order {OrderId} — Slippage={Slippage:F1} pips, FillMs={FillMs}ms",
            order.Id, slippagePips, fillMs);
    }

    private void PublishOrderFilledEvent(Order order, DateTime submitAt)
    {
        var fillMs   = (long)(DateTime.UtcNow - submitAt).TotalMilliseconds;
        var fillRate = order.FilledQuantity.HasValue && order.Quantity > 0
            ? order.FilledQuantity.Value / order.Quantity
            : 1m;

        _eventBus.Publish(new OrderFilledIntegrationEvent
        {
            OrderId        = order.Id,
            StrategyId     = order.StrategyId > 0 ? order.StrategyId : null,
            Symbol         = order.Symbol,
            Session        = order.Session,
            RequestedPrice = order.Price,
            FilledPrice    = order.FilledPrice!.Value,
            SubmitToFillMs = fillMs,
            WasPartialFill = fillRate < 1m,
            FillRate       = fillRate,
            FilledAt       = order.FilledAt ?? DateTime.UtcNow
        });
    }
}
