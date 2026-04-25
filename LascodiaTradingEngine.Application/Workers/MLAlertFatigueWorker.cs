using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Detects fatigue in the recent ML degradation alert stream and raises a durable
/// meta-alert when recent degradation alerts accumulate faster than they are remediated.
/// </summary>
/// <remarks>
/// The worker intentionally measures remediation state rather than operator acknowledgements.
/// The current <see cref="Alert"/> model does not persist a first-class acknowledgement flag,
/// so the durable and auditable signal available here is whether recently triggered
/// <see cref="AlertType.MLModelDegraded"/> alerts remain active or have been cleared.
/// </remarks>
public sealed class MLAlertFatigueWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLAlertFatigueWorker);

    private const string DistributedLockKey = "workers:ml-alert-fatigue:cycle";
    private const string FatigueAlertDeduplicationKey = "ml-alert-fatigue";
    private const int AlertConditionMaxLength = 1000;

    private const string CK_Enabled = "MLAlertFatigue:Enabled";
    private const string CK_PollSecs = "MLAlertFatigue:PollIntervalSeconds";
    private const string CK_WindowDays = "MLAlertFatigue:WindowDays";
    private const string CK_MinAlerts = "MLAlertFatigue:MinAlertThreshold";
    private const string CK_MinRemediatedRatio = "MLAlertFatigue:MinRemediatedRatio";
    private const string CK_LegacyMinActionRatio = "MLAlertFatigue:MinActionRatio";
    private const string CK_LockTimeoutSeconds = "MLAlertFatigue:LockTimeoutSeconds";

    private const string CK_TotalTriggeredAlerts = "MLAlertFatigue:TotalTriggeredAlerts";
    private const string CK_RemediatedAlerts = "MLAlertFatigue:RemediatedAlerts";
    private const string CK_ActiveAlerts = "MLAlertFatigue:ActiveAlerts";
    private const string CK_RemediatedRatio = "MLAlertFatigue:RemediatedRatio";

    private const string CK_LegacyTotalAlerts = "MLAlertFatigue:TotalAlerts7d";
    private const string CK_LegacyAcknowledgedAlerts = "MLAlertFatigue:AcknowledgedAlerts7d";
    private const string CK_LegacyActionRatio = "MLAlertFatigue:ActionRatio";

    private const int DefaultPollSeconds = 24 * 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowDays = 7;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 365;

    private const int DefaultMinAlertThreshold = 20;
    private const int MinMinAlertThreshold = 1;
    private const int MaxMinAlertThreshold = 100_000;

    private const double DefaultMinRemediatedRatio = 0.20;
    private const double MinMinRemediatedRatio = 0.0;
    private const double MaxMinRemediatedRatio = 1.0;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly ILogger<MLAlertFatigueWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public MLAlertFatigueWorker(
        ILogger<MLAlertFatigueWorker> logger,
        IServiceScopeFactory scopeFactory,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
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
            "Monitors recent ML degradation alerts, measures remediation ratio, and raises a durable alert-fatigue meta-alert when degradation alerts accumulate faster than they are cleared.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        var currentDelay = TimeSpan.FromSeconds(DefaultPollSeconds);

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
                    currentDelay = result.Settings.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.ActiveAlerts);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLAlertFatigueCycleDurationMs.Record(durationMs);

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
                            "{Worker}: totalTriggered={Total}, remediated={Remediated}, active={Active}, ratio={Ratio:P1}, fatigueDetected={Detected}, dispatched={Dispatched}, resolved={Resolved}.",
                            WorkerName,
                            result.TotalTriggeredAlerts,
                            result.RemediatedAlerts,
                            result.ActiveAlerts,
                            result.RemediatedRatio,
                            result.FatigueDetected,
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
                        new KeyValuePair<string, object?>("reason", "ml_alert_fatigue_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(CalculateDelay(currentDelay, _consecutiveFailures), _timeProvider, stoppingToken);
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

    internal async Task<MLAlertFatigueCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLAlertFatigueCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLAlertFatigueCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLAlertFatigueLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate alert-fatigue cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }
        }
        else
        {
            var cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLAlertFatigueLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLAlertFatigueCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLAlertFatigueCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLAlertFatigueLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }

        await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
        try
        {
            return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
        }
        finally
        {
            WorkerBulkhead.MLMonitoring.Release();
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return baseInterval <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(DefaultPollSeconds)
                : baseInterval;
        }

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<MLAlertFatigueCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLAlertFatigueWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var summary = await BuildWindowSummaryAsync(db, settings, nowUtc, ct);

        _metrics?.MLAlertFatigueEvaluations.Add(1);
        _metrics?.MLAlertFatigueTriggeredAlerts.Record(summary.TotalTriggeredAlerts);
        _metrics?.MLAlertFatigueActiveAlerts.Record(summary.ActiveAlerts);
        _metrics?.MLAlertFatigueRemediatedRatio.Record(summary.RemediatedRatio);

        await PersistSummaryAsync(db, summary, nowUtc, ct);

        int dispatchedAlerts = 0;
        int resolvedAlerts = 0;

        if (summary.FatigueDetected)
        {
            if (await UpsertAndDispatchFatigueAlertAsync(
                    serviceProvider,
                    writeContext,
                    db,
                    settings,
                    summary,
                    nowUtc,
                    ct))
            {
                dispatchedAlerts++;
                _metrics?.MLAlertFatigueAlertTransitions.Add(
                    1,
                    new KeyValuePair<string, object?>("transition", "dispatched"));
            }
        }
        else
        {
            resolvedAlerts = await ResolveFatigueAlertsAsync(
                serviceProvider,
                writeContext,
                db,
                nowUtc,
                ct);

            if (resolvedAlerts > 0)
            {
                _metrics?.MLAlertFatigueAlertTransitions.Add(
                    resolvedAlerts,
                    new KeyValuePair<string, object?>("transition", "resolved"));
            }
        }

        return new MLAlertFatigueCycleResult(
            settings,
            SkippedReason: null,
            TotalTriggeredAlerts: summary.TotalTriggeredAlerts,
            RemediatedAlerts: summary.RemediatedAlerts,
            ActiveAlerts: summary.ActiveAlerts,
            RemediatedRatio: summary.RemediatedRatio,
            FatigueDetected: summary.FatigueDetected,
            DispatchedAlertCount: dispatchedAlerts,
            ResolvedAlertCount: resolvedAlerts);
    }

    private static async Task<FatigueWindowSummary> BuildWindowSummaryAsync(
        DbContext db,
        MLAlertFatigueWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var windowStartUtc = nowUtc.AddDays(-settings.WindowDays);

        var projection = await db.Set<Alert>()
            .AsNoTracking()
            .Where(alert => !alert.IsDeleted
                         && alert.AlertType == AlertType.MLModelDegraded
                         && (alert.DeduplicationKey == null || alert.DeduplicationKey != FatigueAlertDeduplicationKey)
                         && alert.LastTriggeredAt.HasValue
                         && alert.LastTriggeredAt >= windowStartUtc)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Remediated = group.Count(alert => !alert.IsActive || alert.AutoResolvedAt.HasValue),
            })
            .SingleOrDefaultAsync(ct);

        int totalTriggeredAlerts = projection?.Total ?? 0;
        int remediatedAlerts = projection?.Remediated ?? 0;
        int activeAlerts = Math.Max(0, totalTriggeredAlerts - remediatedAlerts);
        double remediatedRatio = totalTriggeredAlerts > 0
            ? (double)remediatedAlerts / totalTriggeredAlerts
            : 1.0;

        return new FatigueWindowSummary(
            TotalTriggeredAlerts: totalTriggeredAlerts,
            RemediatedAlerts: remediatedAlerts,
            ActiveAlerts: activeAlerts,
            RemediatedRatio: remediatedRatio,
            FatigueDetected: totalTriggeredAlerts >= settings.MinAlertThreshold
                             && remediatedRatio < settings.MinRemediatedRatio);
    }

    private async Task PersistSummaryAsync(
        DbContext db,
        FatigueWindowSummary summary,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string[] keys =
        [
            CK_TotalTriggeredAlerts,
            CK_RemediatedAlerts,
            CK_ActiveAlerts,
            CK_RemediatedRatio,
            CK_LegacyTotalAlerts,
            CK_LegacyAcknowledgedAlerts,
            CK_LegacyActionRatio,
        ];

        var existing = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, ct);

        UpsertEngineConfig(
            db,
            existing,
            CK_TotalTriggeredAlerts,
            summary.TotalTriggeredAlerts.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Count of recently triggered MLModelDegraded alerts inside the fatigue analysis window.",
            nowUtc);
        UpsertEngineConfig(
            db,
            existing,
            CK_RemediatedAlerts,
            summary.RemediatedAlerts.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Count of recent ML degradation alerts that are no longer active or were auto-resolved.",
            nowUtc);
        UpsertEngineConfig(
            db,
            existing,
            CK_ActiveAlerts,
            summary.ActiveAlerts.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Count of recent ML degradation alerts still active inside the fatigue analysis window.",
            nowUtc);
        UpsertEngineConfig(
            db,
            existing,
            CK_RemediatedRatio,
            summary.RemediatedRatio.ToString("F4", CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            "Remediated / triggered ratio for recent ML degradation alerts.",
            nowUtc);

        // Legacy aliases retained for dashboard continuity. These values now mirror
        // remediation semantics rather than operator acknowledgement semantics.
        UpsertEngineConfig(
            db,
            existing,
            CK_LegacyTotalAlerts,
            summary.TotalTriggeredAlerts.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Legacy alias for MLAlertFatigue:TotalTriggeredAlerts.",
            nowUtc);
        UpsertEngineConfig(
            db,
            existing,
            CK_LegacyAcknowledgedAlerts,
            summary.RemediatedAlerts.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Legacy alias for MLAlertFatigue:RemediatedAlerts. This reflects cleared/remediated alerts, not operator acknowledgements.",
            nowUtc);
        UpsertEngineConfig(
            db,
            existing,
            CK_LegacyActionRatio,
            summary.RemediatedRatio.ToString("F4", CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            "Legacy alias for MLAlertFatigue:RemediatedRatio. This reflects remediation ratio, not operator acknowledgement ratio.",
            nowUtc);

        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> UpsertAndDispatchFatigueAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLAlertFatigueWorkerSettings settings,
        FatigueWindowSummary summary,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == FatigueAlertDeduplicationKey, ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.ConfigurationDrift,
                DeduplicationKey = FatigueAlertDeduplicationKey,
                IsActive = true,
            };

            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.ConfigurationDrift;
        }

        alert.Symbol = null;
        alert.Severity = DetermineSeverity(summary, settings);
        alert.CooldownSeconds = settings.CooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = Truncate(
            JsonSerializer.Serialize(new
            {
                detector = "MLAlertFatigue",
                totalTriggeredAlerts = summary.TotalTriggeredAlerts,
                remediatedAlerts = summary.RemediatedAlerts,
                activeAlerts = summary.ActiveAlerts,
                remediatedRatio = Math.Round(summary.RemediatedRatio, 4),
                minAlertThreshold = settings.MinAlertThreshold,
                minRemediatedRatio = Math.Round(settings.MinRemediatedRatio, 4),
                windowDays = settings.WindowDays,
                evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            }),
            AlertConditionMaxLength);

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
                                      && candidate.DeduplicationKey == FatigueAlertDeduplicationKey, ct);
            alert.AlertType = AlertType.ConfigurationDrift;
            alert.Symbol = null;
            alert.Severity = DetermineSeverity(summary, settings);
            alert.CooldownSeconds = settings.CooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = Truncate(
                JsonSerializer.Serialize(new
                {
                    detector = "MLAlertFatigue",
                    totalTriggeredAlerts = summary.TotalTriggeredAlerts,
                    remediatedAlerts = summary.RemediatedAlerts,
                    activeAlerts = summary.ActiveAlerts,
                    remediatedRatio = Math.Round(summary.RemediatedRatio, 4),
                    minAlertThreshold = settings.MinAlertThreshold,
                    minRemediatedRatio = Math.Round(settings.MinRemediatedRatio, 4),
                    windowDays = settings.WindowDays,
                    evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
                }),
                AlertConditionMaxLength);
            await writeContext.SaveChangesAsync(ct);
        }

        if (alert.LastTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(settings.CooldownSeconds))
        {
            return false;
        }

        string message =
            $"ML alert fatigue detected: {summary.TotalTriggeredAlerts} recent ML degradation alerts in {settings.WindowDays} day(s), {summary.RemediatedAlerts} remediated ({summary.RemediatedRatio:P0}), {summary.ActiveAlerts} still active. Review drift thresholds, suppression policy, and escalation hygiene.";

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch ML alert fatigue alert.",
                WorkerName);
            return false;
        }
    }

    private async Task<int> ResolveFatigueAlertsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeAlerts = await db.Set<Alert>()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey == FatigueAlertDeduplicationKey)
            .ToListAsync(ct);

        if (activeAlerts.Count == 0)
            return 0;

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        foreach (var alert in activeAlerts)
        {
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
                        "{Worker}: failed to auto-resolve ML alert fatigue alert {AlertId}.",
                        WorkerName,
                        alert.Id);
                }
            }

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        await writeContext.SaveChangesAsync(ct);
        return activeAlerts.Count;
    }

    private async Task<MLAlertFatigueWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_WindowDays,
            CK_MinAlerts,
            CK_MinRemediatedRatio,
            CK_LegacyMinActionRatio,
            CK_LockTimeoutSeconds,
            AlertCooldownDefaults.CK_MLEscalation,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        double minRemediatedRatio = values.ContainsKey(CK_MinRemediatedRatio)
            ? GetDouble(values, CK_MinRemediatedRatio, DefaultMinRemediatedRatio)
            : GetDouble(values, CK_LegacyMinActionRatio, DefaultMinRemediatedRatio);

        return new MLAlertFatigueWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, DefaultPollSeconds),
                    DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(GetInt(values, CK_WindowDays, DefaultWindowDays),
                DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinAlertThreshold: ClampInt(GetInt(values, CK_MinAlerts, DefaultMinAlertThreshold),
                DefaultMinAlertThreshold, MinMinAlertThreshold, MaxMinAlertThreshold),
            MinRemediatedRatio: ClampDoubleAllowingZero(
                minRemediatedRatio,
                DefaultMinRemediatedRatio,
                MinMinRemediatedRatio,
                MaxMinRemediatedRatio),
            LockTimeoutSeconds: ClampIntAllowingZero(
                GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds,
                MinLockTimeoutSeconds,
                MaxLockTimeoutSeconds),
            CooldownSeconds: ClampInt(
                GetInt(values, AlertCooldownDefaults.CK_MLEscalation, AlertCooldownDefaults.Default_MLEscalation),
                AlertCooldownDefaults.Default_MLEscalation,
                1,
                7 * 24 * 60 * 60));
    }

    private static void UpsertEngineConfig(
        DbContext db,
        IDictionary<string, EngineConfig> existing,
        string key,
        string value,
        ConfigDataType dataType,
        string description,
        DateTime nowUtc)
    {
        if (!existing.TryGetValue(key, out var config))
        {
            config = new EngineConfig
            {
                Key = key,
                IsDeleted = false
            };

            existing[key] = config;
            db.Set<EngineConfig>().Add(config);
        }

        config.Value = value;
        config.DataType = dataType;
        config.Description = description;
        config.IsHotReloadable = false;
        config.LastUpdatedAt = nowUtc;
        config.IsDeleted = false;
    }

    private static AlertSeverity DetermineSeverity(
        FatigueWindowSummary summary,
        MLAlertFatigueWorkerSettings settings)
    {
        bool severeRatioBreach = settings.MinRemediatedRatio > 0
            && summary.RemediatedRatio <= settings.MinRemediatedRatio / 2.0;

        if (summary.TotalTriggeredAlerts >= settings.MinAlertThreshold * 3 || severeRatioBreach)
            return AlertSeverity.Critical;

        if (summary.TotalTriggeredAlerts >= settings.MinAlertThreshold * 2
            || summary.ActiveAlerts >= settings.MinAlertThreshold)
            return AlertSeverity.High;

        return AlertSeverity.Medium;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (bool.TryParse(raw, out var parsedBool))
            return parsedBool;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;

        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
    {
        return values.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
    {
        return values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ClampInt(int value, int fallback, int min, int max)
    {
        if (value <= 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampIntAllowingZero(int value, int fallback, int min, int max)
    {
        if (value < 0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDoubleAllowingZero(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value < 0.0)
            return fallback;

        return Math.Min(Math.Max(value, min), max);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static DateTime NormalizeUtc(DateTime timestamp)
        => timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

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

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }
}

internal sealed record MLAlertFatigueWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int WindowDays,
    int MinAlertThreshold,
    double MinRemediatedRatio,
    int LockTimeoutSeconds,
    int CooldownSeconds);

internal sealed record MLAlertFatigueCycleResult(
    MLAlertFatigueWorkerSettings Settings,
    string? SkippedReason,
    int TotalTriggeredAlerts,
    int RemediatedAlerts,
    int ActiveAlerts,
    double RemediatedRatio,
    bool FatigueDetected,
    int DispatchedAlertCount,
    int ResolvedAlertCount)
{
    public static MLAlertFatigueCycleResult Skipped(MLAlertFatigueWorkerSettings settings, string reason)
        => new(
            settings,
            reason,
            TotalTriggeredAlerts: 0,
            RemediatedAlerts: 0,
            ActiveAlerts: 0,
            RemediatedRatio: 0,
            FatigueDetected: false,
            DispatchedAlertCount: 0,
            ResolvedAlertCount: 0);
}

internal sealed record FatigueWindowSummary(
    int TotalTriggeredAlerts,
    int RemediatedAlerts,
    int ActiveAlerts,
    double RemediatedRatio,
    bool FatigueDetected);
