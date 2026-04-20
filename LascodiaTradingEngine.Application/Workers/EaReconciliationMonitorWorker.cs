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
/// Periodically aggregates recent <see cref="ReconciliationRun"/> rows and
/// alerts when drift between the engine's internal state and the broker's
/// actual state exceeds configurable thresholds.
///
/// <para>
/// <b>Why:</b> EA reconnect cycles accumulate drift silently — orphaned
/// engine positions (broker closed an order we still show open),
/// unknown broker positions (EA opened something the engine didn't
/// track), or volume / SL / TP divergences. Each individual snapshot
/// might look benign, but a sustained pattern is a leading indicator
/// that the EA-engine handshake is broken. This worker turns the
/// per-snapshot counts into a time-averaged signal.
/// </para>
///
/// <para>
/// <b>Cadence:</b> poll every <c>Recon:MonitorIntervalMinutes</c> (default
/// 5 minutes); aggregate runs from the last
/// <c>Recon:MonitorWindowMinutes</c> (default 30 minutes); alert when
/// mean total-drift per run exceeds <c>Recon:MeanDriftAlertThreshold</c>
/// (default 3). Alerts are deduplicated by the underlying
/// <c>IAlertDispatcher</c>'s dedup behaviour.
/// </para>
///
/// <para>
/// <b>Observability:</b> emits <c>trading.ea.reconciliation_drift</c> counter
/// tagged with the drift kind (orphaned_engine / unknown_broker /
/// mismatched) so dashboards can surface which category is drifting.
/// </para>
/// </summary>
public sealed class EaReconciliationMonitorWorker : BackgroundService
{
    private const string CK_PollMinutes    = "Recon:MonitorIntervalMinutes";
    private const string CK_WindowMinutes  = "Recon:MonitorWindowMinutes";
    private const string CK_MeanThreshold  = "Recon:MeanDriftAlertThreshold";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EaReconciliationMonitorWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public EaReconciliationMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EaReconciliationMonitorWorker> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so the monitor doesn't race startup hydration for DB
        // connections.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            int pollMinutes = 5;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

                int windowMinutes     = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_WindowMinutes,   30, stoppingToken);
                int meanAlertThreshold = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_MeanThreshold,   3, stoppingToken);
                pollMinutes           = await ReadIntConfigAsync(readCtx.GetDbContext(), CK_PollMinutes,     5, stoppingToken);

                await RunCycleAsync(scope, windowMinutes, meanAlertThreshold, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EaReconciliationMonitorWorker: cycle failed");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "EaReconciliationMonitorWorker"));
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, pollMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Internal for unit-test access. Aggregates reconciliation runs in the
    /// configured window and alerts when mean per-run drift exceeds the
    /// threshold. Returns the aggregated result so tests can inspect.
    /// </summary>
    internal async Task<AggregateResult> RunCycleAsync(
        IServiceScope scope,
        int windowMinutes,
        int meanAlertThreshold,
        CancellationToken ct)
    {
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var db = readCtx.GetDbContext();

        var windowStart = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-Math.Max(1, windowMinutes));

        // Pull every reconciliation run in the window; aggregate client-side
        // because the volume is low (one row per EA snapshot per minute-ish)
        // and the per-kind counters need summing individually.
        var runs = await db.Set<ReconciliationRun>()
            .AsNoTracking()
            .Where(r => r.RunAt >= windowStart)
            .ToListAsync(ct);

        if (runs.Count == 0)
            return AggregateResult.Empty;

        int totalOrphanedPos   = runs.Sum(r => r.OrphanedEnginePositions);
        int totalUnknownPos    = runs.Sum(r => r.UnknownBrokerPositions);
        int totalMismatched    = runs.Sum(r => r.MismatchedPositions);
        int totalOrphanedOrd   = runs.Sum(r => r.OrphanedEngineOrders);
        int totalUnknownOrd    = runs.Sum(r => r.UnknownBrokerOrders);
        int totalDrift         = runs.Sum(r => r.TotalDrift);
        double meanDriftPerRun = totalDrift / (double)runs.Count;

        if (totalOrphanedPos > 0)
            _metrics.EaReconciliationDrift.Add(totalOrphanedPos, new KeyValuePair<string, object?>("kind", "orphaned_engine_positions"));
        if (totalUnknownPos > 0)
            _metrics.EaReconciliationDrift.Add(totalUnknownPos,  new KeyValuePair<string, object?>("kind", "unknown_broker_positions"));
        if (totalMismatched > 0)
            _metrics.EaReconciliationDrift.Add(totalMismatched,  new KeyValuePair<string, object?>("kind", "mismatched_positions"));
        if (totalOrphanedOrd > 0)
            _metrics.EaReconciliationDrift.Add(totalOrphanedOrd, new KeyValuePair<string, object?>("kind", "orphaned_engine_orders"));
        if (totalUnknownOrd > 0)
            _metrics.EaReconciliationDrift.Add(totalUnknownOrd,  new KeyValuePair<string, object?>("kind", "unknown_broker_orders"));

        var aggregate = new AggregateResult(
            RunCount:             runs.Count,
            MeanDriftPerRun:      meanDriftPerRun,
            TotalOrphanedPositions: totalOrphanedPos,
            TotalUnknownPositions:  totalUnknownPos,
            TotalMismatched:        totalMismatched,
            TotalOrphanedOrders:    totalOrphanedOrd,
            TotalUnknownOrders:     totalUnknownOrd);

        if (meanDriftPerRun >= meanAlertThreshold)
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
            var message =
                $"EA reconciliation drift alert — mean drift per run {meanDriftPerRun:F2} ≥ {meanAlertThreshold} " +
                $"over {runs.Count} runs in last {windowMinutes}m. " +
                $"OrphanedEnginePositions={totalOrphanedPos}, UnknownBrokerPositions={totalUnknownPos}, " +
                $"Mismatched={totalMismatched}, OrphanedEngineOrders={totalOrphanedOrd}, " +
                $"UnknownBrokerOrders={totalUnknownOrd}.";
            try
            {
                await dispatcher.DispatchAsync(
                    new Alert { AlertType = AlertType.DataQualityIssue, IsActive = true },
                    message, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EaReconciliationMonitorWorker: alert dispatch failed");
            }

            _logger.LogWarning(message);
        }

        return aggregate;
    }

    private static async Task<int> ReadIntConfigAsync(DbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await ctx.Set<EngineConfig>().AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct);
        if (entry?.Value is null || !int.TryParse(entry.Value, out var parsed)) return defaultValue;
        return parsed;
    }

    internal sealed record AggregateResult(
        int    RunCount,
        double MeanDriftPerRun,
        int    TotalOrphanedPositions,
        int    TotalUnknownPositions,
        int    TotalMismatched,
        int    TotalOrphanedOrders,
        int    TotalUnknownOrders)
    {
        public static readonly AggregateResult Empty =
            new(0, 0, 0, 0, 0, 0, 0);
    }
}
