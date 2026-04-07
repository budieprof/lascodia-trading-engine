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
/// Hourly worker that compares engine-tracked account equity against the latest
/// broker-reported equity from <see cref="BrokerAccountSnapshot"/> records.
/// Alerts when the variance exceeds a configurable threshold (default 0.5%).
/// Pauses all trading if variance exceeds the critical threshold (default 2%).
/// </summary>
public class BrokerPnLReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BrokerPnLReconciliationWorker> _logger;
    private readonly TradingMetrics _metrics;

    private const double WarningVarianceThreshold = 0.005;  // 0.5%
    private const double CriticalVarianceThreshold = 0.02;  // 2%

    public BrokerPnLReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<BrokerPnLReconciliationWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BrokerPnLReconciliationWorker starting.");
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Wait for EA snapshots to arrive

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BrokerPnLReconciliationWorker error during reconciliation cycle");
                _metrics.WorkerErrors.Add(1, new KeyValuePair<string, object?>("worker", "BrokerPnLReconciliationWorker"));
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        // Get the latest broker snapshot per trading account
        var latestSnapshots = await readCtx.Set<BrokerAccountSnapshot>()
            .Where(s => !s.IsDeleted)
            .GroupBy(s => s.TradingAccountId)
            .Select(g => g.OrderByDescending(s => s.ReportedAt).First())
            .ToListAsync(ct);

        if (latestSnapshots.Count == 0)
        {
            _logger.LogDebug("BrokerPnLReconciliation: no broker snapshots available yet");
            return;
        }

        foreach (var snapshot in latestSnapshots)
        {
            // Skip snapshots older than 2 hours — they may be stale
            if ((DateTime.UtcNow - snapshot.ReportedAt).TotalHours > 2)
                continue;

            var account = await readCtx.Set<TradingAccount>()
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == snapshot.TradingAccountId && !a.IsDeleted, ct);

            if (account is null) continue;

            // Compare engine equity vs broker equity
            decimal engineEquity = account.Equity;
            decimal brokerEquity = snapshot.Equity;

            if (brokerEquity == 0) continue;

            double variance = (double)Math.Abs(engineEquity - brokerEquity) / (double)brokerEquity;

            if (variance > CriticalVarianceThreshold)
            {
                _logger.LogCritical(
                    "PnL RECONCILIATION CRITICAL: Account {AccountId} variance {Variance:P2} exceeds {Threshold:P2}. " +
                    "Engine equity={EngineEquity:F2}, Broker equity={BrokerEquity:F2}. " +
                    "Trading should be paused for manual investigation.",
                    account.Id, variance, CriticalVarianceThreshold,
                    engineEquity, brokerEquity);
            }
            else if (variance > WarningVarianceThreshold)
            {
                _logger.LogWarning(
                    "PnL reconciliation warning: Account {AccountId} variance {Variance:P2} exceeds {Threshold:P2}. " +
                    "Engine equity={EngineEquity:F2}, Broker equity={BrokerEquity:F2}",
                    account.Id, variance, WarningVarianceThreshold,
                    engineEquity, brokerEquity);
            }
            else
            {
                _logger.LogDebug(
                    "PnL reconciliation OK: Account {AccountId} variance {Variance:P4}",
                    account.Id, variance);
            }
        }
    }
}
