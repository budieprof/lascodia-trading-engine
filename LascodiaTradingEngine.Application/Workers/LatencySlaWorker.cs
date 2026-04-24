using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors end-to-end latency SLOs for the core trading path and maintains durable alert
/// lifecycle for sustained breaches.
/// </summary>
public sealed class LatencySlaWorker : BackgroundService
{
    internal const string WorkerName = nameof(LatencySlaWorker);

    private const string DistributedLockKey = "workers:latency-sla:cycle";
    private const string AlertDeduplicationPrefix = "latency-sla:";
    private const int AlertConditionMaxLength = 1000;

    private const int DefaultPollIntervalMinutes = 1;
    private const int MinPollIntervalMinutes = 1;
    private const int MaxPollIntervalMinutes = 60;

    private const int DefaultConsecutiveBreachMinutes = 5;
    private const int MinConsecutiveBreachMinutes = 1;
    private const int MaxConsecutiveBreachMinutes = 60;

    private const int DefaultMinimumSegmentSamples = 5;
    private const int MinMinimumSegmentSamples = 1;
    private const int MaxMinimumSegmentSamples = 1000;

    private const int DefaultTotalTickToFillLookbackHours = 24;
    private const int MinTotalTickToFillLookbackHours = 1;
    private const int MaxTotalTickToFillLookbackHours = 24 * 30;

    private const int DefaultMinimumTotalTickToFillSamples = 10;
    private const int MinMinimumTotalTickToFillSamples = 1;
    private const int MaxMinimumTotalTickToFillSamples = 10_000;

    private const int DefaultTickToSignalP99Ms = 500;
    private const int DefaultSignalToTier1P99Ms = 200;
    private const int DefaultTier2RiskCheckP99Ms = 100;
    private const int DefaultEaPollToSubmitP99Ms = 1000;
    private const int DefaultTotalTickToFillP99Ms = 3000;
    private const int MaxAllowedLatencyTargetMs = 10 * 60 * 1000;

    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private static readonly SlaSegmentDefinition[] SegmentDefinitions =
    [
        new(LatencySlaSegments.TickToSignal, o => o.TickToSignalP99Ms),
        new(LatencySlaSegments.SignalToTier1, o => o.SignalToTier1P99Ms),
        new(LatencySlaSegments.Tier2RiskCheck, o => o.Tier2RiskCheckP99Ms),
        new(LatencySlaSegments.EaPollToSubmit, o => o.EaPollToSubmitP99Ms),
    ];

    private readonly ILogger<LatencySlaWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LatencySlaOptions _options;
    private readonly ILatencySlaRecorder _latencySlaRecorder;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private readonly Dictionary<string, int> _consecutiveBreaches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _peakP99PerBreach = new(StringComparer.Ordinal);

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    private readonly record struct SlaSegmentDefinition(
        string SegmentName,
        Func<LatencySlaWorkerSettings, int> TargetAccessor);

    public LatencySlaWorker(
        ILogger<LatencySlaWorker> logger,
        IServiceScopeFactory scopeFactory,
        LatencySlaOptions options,
        ILatencySlaRecorder latencySlaRecorder,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options;
        _latencySlaRecorder = latencySlaRecorder;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors P99 latency SLOs across the core signal, validation, submission, and fill path; persists durable alerts for sustained breaches; and auto-resolves alerts when fresh compliant samples return.",
            TimeSpan.FromMinutes(DefaultPollIntervalMinutes));

        var currentPollInterval = TimeSpan.FromMinutes(DefaultPollIntervalMinutes);

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    currentPollInterval = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.EvaluatedSegmentCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.LatencySlaCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "{Worker}: segmentsEvaluated={Segments}, insufficientSamples={Insufficient}, breachesDispatched={Breaches}, alertsResolved={Resolved}.",
                            WorkerName,
                            result.EvaluatedSegmentCount,
                            result.InsufficientSampleSegmentCount,
                            result.DispatchedAlertCount,
                            result.ResolvedAlertCount);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "latency_sla_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(currentPollInterval, _consecutiveFailures),
                        _timeProvider,
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<LatencySlaCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = BuildSettings();

        if (!settings.Enabled)
        {
            _metrics?.LatencySlaCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return LatencySlaCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.LatencySlaLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate multi-instance cycles are possible.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
            if (cycleLock is null)
            {
                _metrics?.LatencySlaLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.LatencySlaCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return LatencySlaCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.LatencySlaLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
            }
        }

        return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromMinutes(DefaultPollIntervalMinutes)
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<LatencySlaCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        LatencySlaWorkerSettings settings,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var segmentSnapshots = _latencySlaRecorder.GetCurrentSnapshots(now);
        var snapshotBySegment = segmentSnapshots.ToDictionary(snapshot => snapshot.SegmentName, StringComparer.Ordinal);

        int evaluatedSegments = 0;
        int insufficientSamples = 0;
        int dispatchedAlerts = 0;
        int resolvedAlerts = 0;

        foreach (var segment in SegmentDefinitions)
        {
            if (!snapshotBySegment.TryGetValue(segment.SegmentName, out var snapshot)
                || snapshot.SampleCount < settings.MinimumSegmentSamples)
            {
                insufficientSamples++;
                continue;
            }

            evaluatedSegments++;
            int targetP99Ms = segment.TargetAccessor(settings);
            _metrics?.LatencySlaObservedP99Ms.Record(
                snapshot.P99Ms,
                new KeyValuePair<string, object?>("segment", segment.SegmentName));

            var outcome = await EvaluateObservationAsync(
                serviceProvider,
                writeContext,
                db,
                settings,
                segment.SegmentName,
                snapshot.P99Ms,
                targetP99Ms,
                snapshot.SampleCount,
                now.UtcDateTime,
                ct);

            if (outcome.DispatchedAlert)
                dispatchedAlerts++;
            if (outcome.ResolvedAlert)
                resolvedAlerts++;
        }

        var totalTickToFill = await LoadTotalTickToFillSnapshotAsync(db, now.UtcDateTime, settings, ct);
        if (totalTickToFill is null || totalTickToFill.Value.SampleCount < settings.MinimumTotalTickToFillSamples)
        {
            insufficientSamples++;
        }
        else
        {
            evaluatedSegments++;
            _metrics?.LatencySlaObservedP99Ms.Record(
                totalTickToFill.Value.P99Ms,
                new KeyValuePair<string, object?>("segment", LatencySlaSegments.TotalTickToFill));

            var outcome = await EvaluateObservationAsync(
                serviceProvider,
                writeContext,
                db,
                settings,
                LatencySlaSegments.TotalTickToFill,
                totalTickToFill.Value.P99Ms,
                settings.TotalTickToFillP99Ms,
                totalTickToFill.Value.SampleCount,
                now.UtcDateTime,
                ct);

            if (outcome.DispatchedAlert)
                dispatchedAlerts++;
            if (outcome.ResolvedAlert)
                resolvedAlerts++;
        }

        return new LatencySlaCycleResult(
            settings,
            SkippedReason: null,
            EvaluatedSegmentCount: evaluatedSegments,
            InsufficientSampleSegmentCount: insufficientSamples,
            DispatchedAlertCount: dispatchedAlerts,
            ResolvedAlertCount: resolvedAlerts);
    }

    private async Task<LatencySlaEvaluationOutcome> EvaluateObservationAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        LatencySlaWorkerSettings settings,
        string slaName,
        long observedP99Ms,
        int targetP99Ms,
        int sampleCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (observedP99Ms > targetP99Ms)
        {
            _consecutiveBreaches.TryGetValue(slaName, out var breachCount);
            breachCount++;
            _consecutiveBreaches[slaName] = breachCount;

            _peakP99PerBreach.TryGetValue(slaName, out var peakP99);
            peakP99 = Math.Max(peakP99, observedP99Ms);
            _peakP99PerBreach[slaName] = peakP99;

            if (breachCount < settings.ConsecutiveBreachMinutesBeforeAlert)
                return default;

            var severity = DetermineSeverity(peakP99, targetP99Ms);
            var conditionJson = JsonSerializer.Serialize(new
            {
                slaSegment = slaName,
                actualP99Ms = observedP99Ms,
                peakP99Ms = peakP99,
                targetP99Ms,
                sampleCount,
                consecutiveMinutes = breachCount,
                breachRatio = targetP99Ms > 0 ? Math.Round((double)peakP99 / targetP99Ms, 3) : 0d,
                detectedAt = nowUtc.ToString("O")
            });
            var message =
                $"Latency SLA breach: {slaName} P99={observedP99Ms}ms (peak={peakP99}ms, samples={sampleCount}) exceeds target {targetP99Ms}ms for {breachCount} consecutive minute(s). Severity={severity}.";

            await UpsertAndDispatchAlertAsync(
                serviceProvider,
                writeContext,
                db,
                slaName,
                severity,
                conditionJson,
                message,
                ct);
            await PersistBreachRecordAsync(
                db,
                slaName,
                peakP99,
                targetP99Ms,
                sampleCount,
                breachCount,
                severity,
                nowUtc,
                ct);
            _metrics?.LatencySlaAlertTransitions.Add(
                1,
                new KeyValuePair<string, object?>("segment", slaName),
                new KeyValuePair<string, object?>("transition", "dispatched"));

            _consecutiveBreaches[slaName] = 0;
            _peakP99PerBreach[slaName] = 0;
            return new LatencySlaEvaluationOutcome(DispatchedAlert: true, ResolvedAlert: false);
        }

        if (_consecutiveBreaches.GetValueOrDefault(slaName) > 0)
        {
            _logger.LogInformation(
                "{Worker}: {Sla} returned to compliance (P99={P99}ms <= {Target}ms).",
                WorkerName,
                slaName,
                observedP99Ms,
                targetP99Ms);
        }

        _consecutiveBreaches[slaName] = 0;
        _peakP99PerBreach[slaName] = 0;

        var resolved = await ResolveAlertAsync(serviceProvider, writeContext, db, slaName, nowUtc, ct);
        if (resolved > 0)
        {
            _metrics?.LatencySlaAlertTransitions.Add(
                1,
                new KeyValuePair<string, object?>("segment", slaName),
                new KeyValuePair<string, object?>("transition", "resolved"));
        }

        return new LatencySlaEvaluationOutcome(DispatchedAlert: false, ResolvedAlert: resolved > 0);
    }

    private async Task<TotalTickToFillSnapshot?> LoadTotalTickToFillSnapshotAsync(
        DbContext db,
        DateTime nowUtc,
        LatencySlaWorkerSettings settings,
        CancellationToken ct)
    {
        var lookbackCutoff = nowUtc.AddHours(-settings.TotalTickToFillLookbackHours);
        var samples = await db.Set<TransactionCostAnalysis>()
            .AsNoTracking()
            .Where(row => !row.IsDeleted
                       && row.SignalToFillMs > 0
                       && row.AnalyzedAt >= lookbackCutoff)
            .OrderBy(row => row.SignalToFillMs)
            .Select(row => row.SignalToFillMs)
            .ToListAsync(ct);

        if (samples.Count == 0)
            return null;

        return new TotalTickToFillSnapshot(
            samples.Count,
            GetPercentile(samples, 0.99));
    }

    private async Task UpsertAndDispatchAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        string slaName,
        AlertSeverity severity,
        string conditionJson,
        string message,
        CancellationToken ct)
    {
        var deduplicationKey = BuildDeduplicationKey(slaName);
        int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_Infrastructure,
            AlertCooldownDefaults.Default_Infrastructure,
            ct);

        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.LatencySla,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.LatencySla;
        }

        alert.Severity = severity;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = Truncate(conditionJson, AlertConditionMaxLength);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(db, alert);
            alert = await db.Set<Alert>()
                .FirstAsync(candidate => !candidate.IsDeleted
                                      && candidate.IsActive
                                      && candidate.DeduplicationKey == deduplicationKey, ct);

            alert.AlertType = AlertType.LatencySla;
            alert.Severity = severity;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = Truncate(conditionJson, AlertConditionMaxLength);
            await writeContext.SaveChangesAsync(ct);
        }

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch latency SLA alert for {Sla}.",
                WorkerName,
                slaName);
        }
    }

    private async Task<int> ResolveAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        string slaName,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var deduplicationKey = BuildDeduplicationKey(slaName);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return 0;

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        if (alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{Worker}: failed to auto-resolve latency SLA alert for {Sla}.",
                    WorkerName,
                    slaName);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return 1;
    }

    private async Task PersistBreachRecordAsync(
        DbContext db,
        string slaName,
        long peakP99Ms,
        int targetP99Ms,
        int sampleCount,
        int consecutiveMinutes,
        AlertSeverity severity,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var breachRecordJson = JsonSerializer.Serialize(new
        {
            peakP99Ms,
            targetP99Ms,
            sampleCount,
            consecutiveMinutes,
            severity = severity.ToString(),
            breachedAt = nowUtc.ToString("O")
        });

        await UpsertEngineConfigAsync(
            db,
            $"LatencySLA:LastBreach:{slaName}",
            breachRecordJson,
            ConfigDataType.Json,
            $"Last SLA breach record for {slaName}.",
            nowUtc,
            ct);

        var counterKey = $"LatencySLA:BreachCount:{slaName}";
        var counterConfig = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(config => config.Key == counterKey, ct);
        var nextCount = (counterConfig is not null && int.TryParse(counterConfig.Value, out var currentCount)
            ? currentCount
            : 0) + 1;

        await UpsertEngineConfigAsync(
            db,
            counterKey,
            nextCount.ToString(),
            ConfigDataType.Int,
            $"Cumulative SLA breach count for {slaName}.",
            nowUtc,
            ct);
    }

    private async Task UpsertEngineConfigAsync(
        DbContext db,
        string key,
        string value,
        ConfigDataType dataType,
        string description,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var existing = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(config => config.Key == key, ct);

        if (existing is null)
        {
            db.Set<EngineConfig>().Add(new EngineConfig
            {
                Key = key,
                Value = value,
                DataType = dataType,
                Description = description,
                IsHotReloadable = false,
                LastUpdatedAt = nowUtc,
                IsDeleted = false
            });
        }
        else
        {
            existing.Value = value;
            existing.DataType = dataType;
            existing.Description = description;
            existing.IsHotReloadable = false;
            existing.LastUpdatedAt = nowUtc;
            existing.IsDeleted = false;
        }

        await db.SaveChangesAsync(ct);
    }

    private LatencySlaWorkerSettings BuildSettings()
    {
        return new LatencySlaWorkerSettings(
            Enabled: _options.Enabled,
            PollInterval: TimeSpan.FromMinutes(Clamp(_options.PollIntervalMinutes, DefaultPollIntervalMinutes, MinPollIntervalMinutes, MaxPollIntervalMinutes)),
            TickToSignalP99Ms: NormalizeTarget(_options.TickToSignalP99Ms, DefaultTickToSignalP99Ms),
            SignalToTier1P99Ms: NormalizeTarget(_options.SignalToTier1P99Ms, DefaultSignalToTier1P99Ms),
            Tier2RiskCheckP99Ms: NormalizeTarget(_options.Tier2RiskCheckP99Ms, DefaultTier2RiskCheckP99Ms),
            EaPollToSubmitP99Ms: NormalizeTarget(_options.EaPollToSubmitP99Ms, DefaultEaPollToSubmitP99Ms),
            TotalTickToFillP99Ms: NormalizeTarget(_options.TotalTickToFillP99Ms, DefaultTotalTickToFillP99Ms),
            ConsecutiveBreachMinutesBeforeAlert: Clamp(_options.ConsecutiveBreachMinutesBeforeAlert, DefaultConsecutiveBreachMinutes, MinConsecutiveBreachMinutes, MaxConsecutiveBreachMinutes),
            MinimumSegmentSamples: Clamp(_options.MinimumSegmentSamples, DefaultMinimumSegmentSamples, MinMinimumSegmentSamples, MaxMinimumSegmentSamples),
            TotalTickToFillLookbackHours: Clamp(_options.TotalTickToFillLookbackHours, DefaultTotalTickToFillLookbackHours, MinTotalTickToFillLookbackHours, MaxTotalTickToFillLookbackHours),
            MinimumTotalTickToFillSamples: Clamp(_options.MinimumTotalTickToFillSamples, DefaultMinimumTotalTickToFillSamples, MinMinimumTotalTickToFillSamples, MaxMinimumTotalTickToFillSamples));
    }

    private static AlertSeverity DetermineSeverity(long actualP99Ms, int targetP99Ms)
    {
        if (targetP99Ms <= 0)
            return AlertSeverity.Medium;

        var ratio = (double)actualP99Ms / targetP99Ms;
        return ratio switch
        {
            > 3.0 => AlertSeverity.Critical,
            > 2.0 => AlertSeverity.High,
            _ => AlertSeverity.Medium
        };
    }

    private static string BuildDeduplicationKey(string slaName)
        => AlertDeduplicationPrefix + slaName;

    private static int Clamp(int value, int fallback, int min, int max)
    {
        if (value <= 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static int NormalizeTarget(int value, int fallback)
        => Clamp(value, fallback, 1, MaxAllowedLatencyTargetMs);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static long GetPercentile(IReadOnlyList<long> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * orderedValues.Count) - 1;
        index = Math.Clamp(index, 0, orderedValues.Count - 1);
        return orderedValues[index];
    }

    private static bool IsLikelyAlertDeduplicationRace(IServiceProvider serviceProvider, DbUpdateException ex)
    {
        var classifier = serviceProvider.GetService<IDatabaseExceptionClassifier>();
        if (classifier?.IsUniqueConstraintViolation(ex) == true)
            return true;

        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("DeduplicationKey", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    private static void DetachIfAdded(DbContext db, object entity)
    {
        var entry = db.Entry(entity);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }

    internal readonly record struct LatencySlaWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int TickToSignalP99Ms,
        int SignalToTier1P99Ms,
        int Tier2RiskCheckP99Ms,
        int EaPollToSubmitP99Ms,
        int TotalTickToFillP99Ms,
        int ConsecutiveBreachMinutesBeforeAlert,
        int MinimumSegmentSamples,
        int TotalTickToFillLookbackHours,
        int MinimumTotalTickToFillSamples);

    internal readonly record struct LatencySlaCycleResult(
        LatencySlaWorkerSettings Settings,
        string? SkippedReason,
        int EvaluatedSegmentCount,
        int InsufficientSampleSegmentCount,
        int DispatchedAlertCount,
        int ResolvedAlertCount)
    {
        public static LatencySlaCycleResult Skipped(LatencySlaWorkerSettings settings, string reason)
            => new(
                settings,
                reason,
                EvaluatedSegmentCount: 0,
                InsufficientSampleSegmentCount: 0,
                DispatchedAlertCount: 0,
                ResolvedAlertCount: 0);
    }

    private readonly record struct LatencySlaEvaluationOutcome(bool DispatchedAlert, bool ResolvedAlert);

    private readonly record struct TotalTickToFillSnapshot(int SampleCount, long P99Ms);
}
