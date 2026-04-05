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
/// Monitors P99 latencies across the full critical trading path (tick-to-signal,
/// signal-to-Tier1, Tier2 risk check, EA poll-to-submit, total tick-to-fill) and
/// dispatches severity-tiered alerts when SLA targets are breached for consecutive minutes.
/// Persists breach history to EngineConfig for historical tracking and dashboards.
/// </summary>
public class LatencySlaWorker : BackgroundService
{
    private readonly ILogger<LatencySlaWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly LatencySlaOptions _options;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, int> _consecutiveBreaches = new();
    private readonly Dictionary<string, long> _peakP99PerBreach = new();

    /// <summary>SLA segment definitions: (worker name, options accessor, segment display name).</summary>
    private record SlaSegment(string WorkerName, Func<LatencySlaOptions, int> TargetAccessor, string SegmentName);

    private static readonly SlaSegment[] Segments =
    [
        new("StrategyWorker",            o => o.TickToSignalP99Ms,      "TickToSignal"),
        new("SignalOrderBridgeWorker",   o => o.SignalToTier1P99Ms,     "SignalToTier1"),
        new("OrderExecutionWorker",      o => o.Tier2RiskCheckP99Ms,    "Tier2RiskCheck"),
        new("EACommandPushWorker",       o => o.EaPollToSubmitP99Ms,    "EaPollToSubmit"),
    ];

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
                await CheckSlaBreachesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LatencySlaWorker error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task CheckSlaBreachesAsync(CancellationToken ct)
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();

        // Check all defined SLA segments
        foreach (var segment in Segments)
        {
            int targetMs = segment.TargetAccessor(_options);
            await CheckWorkerSlaAsync(snapshots, segment.WorkerName, targetMs, segment.SegmentName, ct);
        }

        // Check aggregate tick-to-fill by summing contributing segments
        await CheckAggregateSlaAsync(snapshots, ct);

        // General worker health: flag any worker whose P99 exceeds 2x its configured interval
        foreach (var snapshot in snapshots)
        {
            if (snapshot.ConfiguredIntervalSeconds > 0 &&
                snapshot.CycleDurationP99Ms > snapshot.ConfiguredIntervalSeconds * 1000 * 2)
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
        string slaName,
        CancellationToken ct)
    {
        var snapshot = snapshots.FirstOrDefault(s => s.WorkerName == workerName);
        if (snapshot is null) return;

        if (snapshot.CycleDurationP99Ms > targetP99Ms)
        {
            _consecutiveBreaches.TryGetValue(slaName, out var count);
            _consecutiveBreaches[slaName] = count + 1;

            // Track the peak P99 during this breach window for the alert
            _peakP99PerBreach.TryGetValue(slaName, out var peak);
            _peakP99PerBreach[slaName] = Math.Max(peak, snapshot.CycleDurationP99Ms);

            if (_consecutiveBreaches[slaName] >= _options.ConsecutiveBreachMinutesBeforeAlert)
            {
                var peakP99 = _peakP99PerBreach[slaName];
                var severity = DetermineSeverity(peakP99, targetP99Ms);

                _logger.LogCritical(
                    "LatencySLA BREACH: {Sla} P99={P99}ms (peak={Peak}ms) exceeds target {Target}ms for {Count} consecutive minutes. Severity={Severity}",
                    slaName, snapshot.CycleDurationP99Ms, peakP99, targetP99Ms,
                    _consecutiveBreaches[slaName], severity);

                await DispatchBreachAlertAsync(slaName, snapshot.CycleDurationP99Ms, peakP99, targetP99Ms, severity, ct);
                await PersistBreachRecordAsync(slaName, peakP99, targetP99Ms, _consecutiveBreaches[slaName], severity, ct);

                _consecutiveBreaches[slaName] = 0;
                _peakP99PerBreach[slaName] = 0;
            }
        }
        else
        {
            if (_consecutiveBreaches.GetValueOrDefault(slaName) > 0)
            {
                _logger.LogInformation("LatencySLA: {Sla} returned to compliance (P99={P99}ms <= {Target}ms)",
                    slaName, snapshot.CycleDurationP99Ms, targetP99Ms);
            }
            _consecutiveBreaches[slaName] = 0;
            _peakP99PerBreach[slaName] = 0;
        }
    }

    /// <summary>
    /// Checks the aggregate tick-to-fill latency by summing the P99 of each
    /// contributing segment in the critical path.
    /// </summary>
    private async Task CheckAggregateSlaAsync(
        IReadOnlyList<WorkerHealthSnapshot> snapshots,
        CancellationToken ct)
    {
        const string slaName = "TotalTickToFill";
        long aggregateP99 = 0;
        int foundSegments = 0;

        foreach (var segment in Segments)
        {
            var snap = snapshots.FirstOrDefault(s => s.WorkerName == segment.WorkerName);
            if (snap is not null)
            {
                aggregateP99 += snap.CycleDurationP99Ms;
                foundSegments++;
            }
        }

        if (foundSegments < 2) return; // Not enough segments to make a meaningful aggregate

        if (aggregateP99 > _options.TotalTickToFillP99Ms)
        {
            _consecutiveBreaches.TryGetValue(slaName, out var count);
            _consecutiveBreaches[slaName] = count + 1;

            _peakP99PerBreach.TryGetValue(slaName, out var peak);
            _peakP99PerBreach[slaName] = Math.Max(peak, aggregateP99);

            if (_consecutiveBreaches[slaName] >= _options.ConsecutiveBreachMinutesBeforeAlert)
            {
                var peakVal = _peakP99PerBreach[slaName];
                var severity = DetermineSeverity(peakVal, _options.TotalTickToFillP99Ms);

                await DispatchBreachAlertAsync(slaName, aggregateP99, peakVal, _options.TotalTickToFillP99Ms, severity, ct);
                await PersistBreachRecordAsync(slaName, peakVal, _options.TotalTickToFillP99Ms,
                    _consecutiveBreaches[slaName], severity, ct);

                _consecutiveBreaches[slaName] = 0;
                _peakP99PerBreach[slaName] = 0;
            }
        }
        else
        {
            _consecutiveBreaches[slaName] = 0;
            _peakP99PerBreach[slaName] = 0;
        }
    }

    /// <summary>
    /// Determines alert severity based on how far the P99 exceeds the target.
    /// &gt;3x target = Critical, &gt;2x = High, else Medium.
    /// </summary>
    private static AlertSeverity DetermineSeverity(long actualP99Ms, int targetP99Ms)
    {
        if (targetP99Ms <= 0) return AlertSeverity.Medium;
        double ratio = (double)actualP99Ms / targetP99Ms;
        return ratio switch
        {
            > 3.0 => AlertSeverity.Critical,
            > 2.0 => AlertSeverity.High,
            _     => AlertSeverity.Medium
        };
    }

    private async Task DispatchBreachAlertAsync(
        string slaName, long actualP99Ms, long peakP99Ms, int targetP99Ms,
        AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var alertDispatcher = scope.ServiceProvider.GetRequiredService<IAlertDispatcher>();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();

            string dedupKey = $"latency-sla:{slaName}";

            // Check for existing active alert with same dedup key to avoid flooding
            bool alertExists = await writeCtx.GetDbContext().Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == dedupKey
                            && a.IsActive && !a.IsDeleted, ct);

            if (alertExists) return;

            var conditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                slaSegment               = slaName,
                actualP99Ms,
                peakP99Ms,
                targetP99Ms,
                consecutiveMinutes       = _options.ConsecutiveBreachMinutesBeforeAlert,
                severity                 = severity.ToString(),
                breachRatio              = targetP99Ms > 0 ? Math.Round((double)actualP99Ms / targetP99Ms, 2) : 0,
                detectedAt               = DateTime.UtcNow.ToString("O")
            });

            var alert = new Alert
            {
                AlertType        = AlertType.LatencySla,
                Symbol           = slaName,
                Channel          = severity >= AlertSeverity.High ? AlertChannel.Telegram : AlertChannel.Webhook,
                Destination      = "ops-latency",
                Severity         = severity,
                DeduplicationKey = dedupKey,
                CooldownSeconds  = 600,
                ConditionJson    = conditionJson,
                IsActive         = true,
            };

            string message = $"Latency SLA breach: {slaName} P99={actualP99Ms}ms (peak={peakP99Ms}ms) " +
                             $"exceeds target {targetP99Ms}ms for {_options.ConsecutiveBreachMinutesBeforeAlert} " +
                             $"consecutive minutes. Severity={severity}.";

            await alertDispatcher.DispatchBySeverityAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LatencySlaWorker: failed to dispatch breach alert for {Sla}", slaName);
        }
    }

    /// <summary>
    /// Persists breach records to EngineConfig for historical tracking and dashboards.
    /// Each breach is recorded with its peak latency, duration, and severity.
    /// </summary>
    private async Task PersistBreachRecordAsync(
        string slaName, long peakP99Ms, int targetP99Ms,
        int consecutiveMinutes, AlertSeverity severity, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
            var writeDb = writeCtx.GetDbContext();

            string configKey = $"LatencySLA:LastBreach:{slaName}";
            string value = System.Text.Json.JsonSerializer.Serialize(new
            {
                peakP99Ms,
                targetP99Ms,
                consecutiveMinutes,
                severity = severity.ToString(),
                breachedAt = DateTime.UtcNow.ToString("O")
            });

            int rows = await writeDb.Set<EngineConfig>()
                .Where(c => c.Key == configKey)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Value, value)
                    .SetProperty(c => c.LastUpdatedAt, DateTime.UtcNow), ct);

            if (rows == 0)
            {
                writeDb.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key             = configKey,
                    Value           = value,
                    DataType        = ConfigDataType.Json,
                    Description     = $"Last SLA breach record for {slaName}.",
                    IsHotReloadable = false,
                    LastUpdatedAt   = DateTime.UtcNow,
                });
                await writeDb.SaveChangesAsync(ct);
            }

            // Also maintain a breach counter for observability
            string counterKey = $"LatencySLA:BreachCount:{slaName}";
            var counterEntry = await writeDb.Set<EngineConfig>()
                .FirstOrDefaultAsync(c => c.Key == counterKey, ct);

            if (counterEntry is not null)
            {
                int current = int.TryParse(counterEntry.Value, out var v) ? v : 0;
                counterEntry.Value = (current + 1).ToString();
                counterEntry.LastUpdatedAt = DateTime.UtcNow;
            }
            else
            {
                writeDb.Set<EngineConfig>().Add(new EngineConfig
                {
                    Key             = counterKey,
                    Value           = "1",
                    DataType        = ConfigDataType.Int,
                    Description     = $"Cumulative SLA breach count for {slaName}.",
                    IsHotReloadable = false,
                    LastUpdatedAt   = DateTime.UtcNow,
                });
            }

            await writeDb.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LatencySlaWorker: failed to persist breach record for {Sla}", slaName);
        }
    }
}
