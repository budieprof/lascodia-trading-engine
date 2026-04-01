using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors P99 latencies across the critical trading path and dispatches Critical-severity
/// alerts when SLA targets are breached for consecutive minutes.
/// Reads from the WorkerHealthMonitor snapshots and TradingMetrics.
/// </summary>
public class LatencySlaWorker : BackgroundService
{
    private readonly ILogger<LatencySlaWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly LatencySlaOptions _options;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>Consecutive breach count per SLA segment.</summary>
    private readonly Dictionary<string, int> _consecutiveBreaches = new();

    public LatencySlaWorker(
        ILogger<LatencySlaWorker> logger,
        IServiceScopeFactory scopeFactory,
        IWorkerHealthMonitor healthMonitor,
        LatencySlaOptions options)
    {
        _logger        = logger;
        _scopeFactory  = scopeFactory;
        _healthMonitor = healthMonitor;
        _options       = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LatencySlaWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSlaBreachesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LatencySlaWorker error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CheckSlaBreachesAsync()
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();

        // Check StrategyWorker P99 (tick -> signal)
        await CheckWorkerSlaAsync(snapshots, "StrategyWorker", _options.TickToSignalP99Ms, "TickToSignal");

        // Check SignalOrderBridgeWorker P99 (signal -> Tier 1)
        await CheckWorkerSlaAsync(snapshots, "SignalOrderBridgeWorker", _options.SignalToTier1P99Ms, "SignalToTier1");

        // Check general worker health for latency drift
        foreach (var snapshot in snapshots)
        {
            if (snapshot.CycleDurationP99Ms > snapshot.ConfiguredIntervalSeconds * 1000 * 2)
            {
                _logger.LogWarning(
                    "LatencySLA: {Worker} P99 cycle duration ({P99}ms) exceeds 2x configured interval ({Interval}s)",
                    snapshot.WorkerName, snapshot.CycleDurationP99Ms, snapshot.ConfiguredIntervalSeconds);
            }
        }
    }

    private async Task CheckWorkerSlaAsync(
        IReadOnlyList<WorkerHealthSnapshot> snapshots,
        string workerName,
        int targetP99Ms,
        string slaName)
    {
        var snapshot = snapshots.FirstOrDefault(s => s.WorkerName == workerName);
        if (snapshot is null) return;

        if (snapshot.CycleDurationP99Ms > targetP99Ms)
        {
            _consecutiveBreaches.TryGetValue(slaName, out var count);
            _consecutiveBreaches[slaName] = count + 1;

            if (_consecutiveBreaches[slaName] >= _options.ConsecutiveBreachMinutesBeforeAlert)
            {
                _logger.LogCritical(
                    "LatencySLA BREACH: {Sla} P99={P99}ms exceeds target {Target}ms for {Count} consecutive minutes",
                    slaName, snapshot.CycleDurationP99Ms, targetP99Ms, _consecutiveBreaches[slaName]);

                // Dispatch alert via IAlertDispatcher
                await DispatchBreachAlertAsync(slaName, snapshot.CycleDurationP99Ms, targetP99Ms);

                // Reset counter after alerting to avoid repeated alerts
                _consecutiveBreaches[slaName] = 0;
            }
        }
        else
        {
            _consecutiveBreaches[slaName] = 0; // Reset on compliance
        }
    }

    private async Task DispatchBreachAlertAsync(string slaName, long actualP99Ms, int targetP99Ms)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();

            var alert = new Alert
            {
                AlertType = AlertType.LatencySla,
                Channel   = AlertChannel.Webhook,
                IsActive  = true,
            };

            await alertDispatcher.DispatchBySeverityAsync(
                alert,
                $"Latency SLA breach: {slaName} P99={actualP99Ms}ms exceeds target {targetP99Ms}ms " +
                $"for {_options.ConsecutiveBreachMinutesBeforeAlert} consecutive minutes",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LatencySlaWorker: failed to dispatch breach alert for {Sla}", slaName);
        }
    }
}
