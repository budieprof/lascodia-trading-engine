using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically persists worker health snapshots from the in-memory monitor to the database.
/// Alerts when any worker's last success exceeds 2x its configured interval.
/// Triggers degradation mode transitions when critical workers are detected as stale.
/// </summary>
public class WorkerHealthWorker : BackgroundService
{
    private readonly ILogger<WorkerHealthWorker> _logger;
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly IDegradationModeManager _degradationManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private int _consecutiveFailures;

    /// <summary>
    /// Worker names that trigger degradation when stale. Configurable via EngineConfig
    /// key <c>WorkerHealth:CriticalWorkers</c> (comma-separated).
    /// </summary>
    private static readonly HashSet<string> DefaultCriticalWorkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "StrategyWorker", "SignalOrderBridgeWorker", "PositionWorker", "EAHealthMonitorWorker"
    };

    // EngineConfig keys
    private const string CK_PersistIntervalMinutes = "WorkerHealth:PersistIntervalMinutes";
    private const string CK_CriticalWorkers        = "WorkerHealth:CriticalWorkers";

    public WorkerHealthWorker(
        ILogger<WorkerHealthWorker> logger,
        IWorkerHealthMonitor healthMonitor,
        IDegradationModeManager degradationManager,
        IServiceScopeFactory scopeFactory)
    {
        _logger              = logger;
        _healthMonitor       = healthMonitor;
        _degradationManager  = degradationManager;
        _scopeFactory        = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkerHealthWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int persistIntervalMinutes = 5;
            try
            {
                // Read configurable interval from EngineConfig
                using var scope = _scopeFactory.CreateScope();
                var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
                persistIntervalMinutes = await ReadIntConfigAsync(readCtx, CK_PersistIntervalMinutes, 5, stoppingToken);

                // Also update degradation thresholds from config
                var dmMlTimeout  = await ReadIntConfigAsync(readCtx, "Degradation:MLScorerTimeoutSeconds", 120, stoppingToken);
                var dmEbTimeout  = await ReadIntConfigAsync(readCtx, "Degradation:EventBusTimeoutSeconds", 60, stoppingToken);
                var dmDbTimeout  = await ReadIntConfigAsync(readCtx, "Degradation:ReadDbTimeoutSeconds", 30, stoppingToken);
                var persistedMode = await ReadStringConfigAsync(readCtx, "Degradation:ActiveMode", stoppingToken);

                if (_degradationManager is DegradationModeManager dmm)
                    dmm.UpdateThresholdsFromConfig(dmMlTimeout, dmEbTimeout, dmDbTimeout, persistedMode);

                await _healthMonitor.PersistSnapshotsAsync(stoppingToken);
                await CheckForStaleWorkersAsync(readCtx, stoppingToken);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "WorkerHealthWorker error (failure #{Count})", _consecutiveFailures);
            }

            await Task.Delay(TimeSpan.FromMinutes(persistIntervalMinutes), stoppingToken);
        }
    }

    private async Task CheckForStaleWorkersAsync(IReadApplicationDbContext readCtx, CancellationToken ct)
    {
        // Load configurable critical worker list
        var criticalWorkersStr = await ReadStringConfigAsync(readCtx, CK_CriticalWorkers, ct);
        var criticalWorkers = !string.IsNullOrWhiteSpace(criticalWorkersStr)
            ? new HashSet<string>(criticalWorkersStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : DefaultCriticalWorkers;

        var snapshots = _healthMonitor.GetCurrentSnapshots();
        bool anyCriticalStale = false;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.LastSuccessAt.HasValue && snapshot.ConfiguredIntervalSeconds > 0)
            {
                var staleness = DateTime.UtcNow - snapshot.LastSuccessAt.Value;
                var threshold = TimeSpan.FromSeconds(snapshot.ConfiguredIntervalSeconds * 2);

                if (staleness > threshold)
                {
                    _logger.LogWarning(
                        "Worker {Name} is stale: last success {Ago:F0}s ago (threshold={Threshold:F0}s)",
                        snapshot.WorkerName, staleness.TotalSeconds, threshold.TotalSeconds);

                    // Trigger degradation if this is a critical worker
                    if (criticalWorkers.Contains(snapshot.WorkerName))
                        anyCriticalStale = true;
                }
            }

            if (snapshot.ConsecutiveFailures >= 3)
            {
                _logger.LogWarning(
                    "Worker {Name} has {Failures} consecutive failures. Last error: {Error}",
                    snapshot.WorkerName, snapshot.ConsecutiveFailures, snapshot.LastErrorMessage);
            }
        }

        // Transition to degraded mode if critical workers are stale
        if (anyCriticalStale && _degradationManager.CurrentMode == DegradationMode.Normal)
        {
            await _degradationManager.TransitionToAsync(
                DegradationMode.EventBusDegraded,
                "Critical worker(s) detected as stale by WorkerHealthWorker",
                ct);
        }
    }

    private static async Task<int> ReadIntConfigAsync(IReadApplicationDbContext ctx, string key, int defaultValue, CancellationToken ct)
    {
        var value = await ctx.GetDbContext().Set<EngineConfig>()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
        return value is not null && int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static async Task<string?> ReadStringConfigAsync(IReadApplicationDbContext ctx, string key, CancellationToken ct)
    {
        return await ctx.GetDbContext().Set<EngineConfig>()
            .Where(c => c.Key == key && !c.IsDeleted)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);
    }
}
