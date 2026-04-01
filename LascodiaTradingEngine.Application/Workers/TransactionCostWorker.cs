using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Computes Transaction Cost Analysis for each newly filled order that doesn't yet have
/// a TCA record. Runs on a configurable polling interval.
/// </summary>
public class TransactionCostWorker : BackgroundService
{
    private readonly ILogger<TransactionCostWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TransactionCostOptions _options;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    public TransactionCostWorker(
        ILogger<TransactionCostWorker> logger,
        IServiceScopeFactory scopeFactory,
        TransactionCostOptions options)
    {
        _logger       = logger;
        _scopeFactory = scopeFactory;
        _options      = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TransactionCostWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AnalyzeNewFillsAsync(stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "TransactionCostWorker error (failure #{Count})", _consecutiveFailures);
            }

            var baseInterval = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            var delay = _consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(
                    baseInterval.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1),
                    MaxBackoff.TotalSeconds))
                : baseInterval;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task AnalyzeNewFillsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx   = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeCtx  = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var analyzer  = scope.ServiceProvider.GetRequiredService<ITransactionCostAnalyzer>();

        // Find filled orders without a TCA record
        var existingOrderIds = await readCtx.GetDbContext()
            .Set<TransactionCostAnalysis>()
            .Select(t => t.OrderId)
            .ToListAsync(ct);

        var existingSet = new HashSet<long>(existingOrderIds);

        var filledOrders = await readCtx.GetDbContext()
            .Set<Order>()
            .Where(o => o.Status == OrderStatus.Filled && o.FilledPrice != null && !o.IsDeleted)
            .OrderByDescending(o => o.FilledAt)
            .Take(100)
            .ToListAsync(ct);

        var newFills = filledOrders.Where(o => !existingSet.Contains(o.Id)).ToList();

        if (newFills.Count == 0) return;

        foreach (var order in newFills)
        {
            try
            {
                var signal = order.TradeSignalId.HasValue
                    ? await readCtx.GetDbContext()
                        .Set<TradeSignal>()
                        .FirstOrDefaultAsync(s => s.Id == order.TradeSignalId.Value, ct)
                    : null;

                var tca = await analyzer.AnalyzeAsync(order, signal, ct);

                await writeCtx.GetDbContext().Set<TransactionCostAnalysis>().AddAsync(tca, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TransactionCostWorker: failed to analyze order {OrderId}", order.Id);
            }
        }

        await writeCtx.GetDbContext().SaveChangesAsync(ct);

        _logger.LogInformation("TransactionCostWorker: analyzed {Count} new fills", newFills.Count);
    }
}
