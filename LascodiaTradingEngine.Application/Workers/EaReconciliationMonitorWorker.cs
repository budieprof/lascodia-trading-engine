using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Aggregates recent <see cref="ReconciliationRun"/> rows per EA instance and maintains
/// durable data-quality alerts when reconciliation drift persists above the configured
/// mean-drift threshold.
/// </summary>
public sealed class EaReconciliationMonitorWorker : BackgroundService
{
    internal const string WorkerName = nameof(EaReconciliationMonitorWorker);

    private const string CK_PollMinutes = "Recon:MonitorIntervalMinutes";
    private const string CK_WindowMinutes = "Recon:MonitorWindowMinutes";
    private const string CK_MeanThreshold = "Recon:MeanDriftAlertThreshold";
    private const int DefaultPollMinutes = 5;
    private const int MinPollMinutes = 1;
    private const int MaxPollMinutes = 60;
    private const int DefaultWindowMinutes = 30;
    private const int MinWindowMinutes = 5;
    private const int MaxWindowMinutes = 1440;
    private const int DefaultMeanThreshold = 3;
    private const int MinMeanThreshold = 1;
    private const int MaxMeanThreshold = 1000;
    private const int MaxBackoffMinutes = 60;
    private const string DistributedLockKey = "workers:ea-reconciliation-monitor:cycle";
    private const string AlertDeduplicationPrefix = "EAReconciliation:";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(DefaultPollMinutes);
    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EaReconciliationMonitorWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    public EaReconciliationMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EaReconciliationMonitorWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} starting", WorkerName);

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Aggregates rolling EA reconciliation drift per instance, persists durable data-quality alerts for sustained divergence, and auto-resolves them when the drift clears.",
            DefaultPollInterval);

        var currentPollInterval = DefaultPollInterval;

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
                    if (result.SkippedReason is null)
                        _healthMonitor?.RecordBacklogDepth(WorkerName, result.AlertingInstanceCount);

                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else if (result.AlertingInstanceCount > 0 || result.ResolvedAlertCount > 0)
                    {
                        var logLevel = result.DispatchedAlertCount > 0 ? LogLevel.Warning : LogLevel.Information;
                        _logger.Log(
                            logLevel,
                            "{Worker}: evaluated {RunCount} reconciliation run(s) across {InstanceCount} EA instance(s) over {WindowMinutes}m; alertingInstances={AlertingInstances}, dispatched={Dispatched}, resolved={Resolved}, worstInstance={WorstInstance}, worstMeanDrift={WorstMeanDrift:F2}.",
                            WorkerName,
                            result.WindowRunCount,
                            result.InstanceCount,
                            result.Settings.WindowMinutes,
                            result.AlertingInstanceCount,
                            result.DispatchedAlertCount,
                            result.ResolvedAlertCount,
                            result.WorstInstanceId ?? "n/a",
                            result.MaxMeanDriftPerRun);
                    }
                    else if (result.WindowRunCount == 0)
                    {
                        _logger.LogDebug(
                            "{Worker}: no reconciliation runs found in the last {WindowMinutes} minute(s).",
                            WorkerName,
                            result.Settings.WindowMinutes);
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
                        new KeyValuePair<string, object?>("reason", "ea_reconciliation_monitor_cycle"));
                    _logger.LogError(
                        ex,
                        "{Worker}: cycle failed.",
                        WorkerName);
                }

                try
                {
                    await Task.Delay(
                        CalculateDelay(_consecutiveFailures, currentPollInterval),
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
            _logger.LogInformation("{Worker} stopped", WorkerName);
        }
    }

    internal static TimeSpan CalculateDelay(int consecutiveFailures, TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
            baseDelay = DefaultPollInterval;

        if (consecutiveFailures <= 0)
            return baseDelay;

        double delayMinutes = Math.Min(
            baseDelay.TotalMinutes * Math.Pow(2, consecutiveFailures - 1),
            MaxBackoffMinutes);

        return TimeSpan.FromMinutes(delayMinutes);
    }

    internal async Task<EaReconciliationMonitorCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate reconciliation evaluations are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }

            return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
        }

        var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
        if (cycleLock is null)
            return EaReconciliationMonitorCycleResult.Skipped(settings, "lock_busy");

        await using (cycleLock)
        {
            return await RunCycleCoreAsync(serviceProvider, writeContext, db, settings, ct);
        }
    }

    private async Task<EaReconciliationMonitorCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ReconciliationMonitorSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc.AddMinutes(-settings.WindowMinutes);

        var aggregates = await db.Set<ReconciliationRun>()
            .AsNoTracking()
            .Where(run => run.RunAt >= windowStart)
            .GroupBy(run => run.InstanceId)
            .Select(group => new ReconciliationInstanceAggregate(
                group.Key,
                group.Count(),
                group.Sum(run => run.TotalDrift),
                group.Sum(run => run.OrphanedEnginePositions),
                group.Sum(run => run.UnknownBrokerPositions),
                group.Sum(run => run.MismatchedPositions),
                group.Sum(run => run.OrphanedEngineOrders),
                group.Sum(run => run.UnknownBrokerOrders),
                group.Min(run => run.RunAt),
                group.Max(run => run.RunAt)))
            .ToListAsync(ct);

        if (aggregates.Count == 0)
        {
            int resolvedWithoutRuns = await ResolveInactiveAlertsAsync(
                serviceProvider,
                writeContext,
                db,
                [],
                nowUtc,
                ct);

            if (resolvedWithoutRuns > 0)
            {
                _metrics?.EaReconciliationAlertTransitions.Add(
                    resolvedWithoutRuns,
                    new KeyValuePair<string, object?>("transition", "resolved"));
            }

            return new EaReconciliationMonitorCycleResult(
                settings,
                WindowRunCount: 0,
                InstanceCount: 0,
                AlertingInstanceCount: 0,
                DispatchedAlertCount: 0,
                ResolvedAlertCount: resolvedWithoutRuns,
                MaxMeanDriftPerRun: 0,
                WorstInstanceId: null,
                SkippedReason: null);
        }

        EmitWindowMetrics(aggregates, settings.MeanDriftAlertThreshold);

        var instanceIds = aggregates
            .Select(aggregate => aggregate.InstanceId)
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var accountLookup = await LoadTradingAccountLookupAsync(db, instanceIds, ct);
        var evaluatedAggregates = aggregates
            .Select(aggregate => new EvaluatedReconciliationInstanceAggregate(
                aggregate,
                accountLookup.TryGetValue(aggregate.InstanceId, out var tradingAccountId)
                    ? tradingAccountId
                    : null))
            .OrderByDescending(aggregate => aggregate.MeanDriftPerRun)
            .ThenBy(aggregate => aggregate.InstanceId, StringComparer.Ordinal)
            .ToList();

        var activeDeduplicationKeys = new List<string>(evaluatedAggregates.Count);
        int dispatchedAlertCount = 0;

        foreach (var aggregate in evaluatedAggregates.Where(a => a.MeanDriftPerRun >= settings.MeanDriftAlertThreshold))
        {
            string deduplicationKey = BuildDeduplicationKey(aggregate.InstanceId);
            activeDeduplicationKeys.Add(deduplicationKey);

            bool dispatched = await UpsertAndDispatchAlertAsync(
                serviceProvider,
                writeContext,
                db,
                deduplicationKey,
                aggregate,
                settings,
                windowStart,
                nowUtc,
                ct);

            if (dispatched)
                dispatchedAlertCount++;
        }

        int resolvedAlertCount = await ResolveInactiveAlertsAsync(
            serviceProvider,
            writeContext,
            db,
            activeDeduplicationKeys,
            nowUtc,
            ct);

        if (dispatchedAlertCount > 0)
        {
            _metrics?.EaReconciliationAlertTransitions.Add(
                dispatchedAlertCount,
                new KeyValuePair<string, object?>("transition", "dispatched"));
        }

        if (resolvedAlertCount > 0)
        {
            _metrics?.EaReconciliationAlertTransitions.Add(
                resolvedAlertCount,
                new KeyValuePair<string, object?>("transition", "resolved"));
        }

        var worstAggregate = evaluatedAggregates[0];

        return new EaReconciliationMonitorCycleResult(
            settings,
            WindowRunCount: evaluatedAggregates.Sum(aggregate => aggregate.RunCount),
            InstanceCount: evaluatedAggregates.Count,
            AlertingInstanceCount: activeDeduplicationKeys.Count,
            DispatchedAlertCount: dispatchedAlertCount,
            ResolvedAlertCount: resolvedAlertCount,
            MaxMeanDriftPerRun: worstAggregate.MeanDriftPerRun,
            WorstInstanceId: worstAggregate.InstanceId,
            SkippedReason: null);
    }

    private void EmitWindowMetrics(
        IReadOnlyCollection<ReconciliationInstanceAggregate> aggregates,
        int meanDriftAlertThreshold)
    {
        int totalOrphanedPositions = 0;
        int totalUnknownPositions = 0;
        int totalMismatched = 0;
        int totalOrphanedOrders = 0;
        int totalUnknownOrders = 0;

        foreach (var aggregate in aggregates)
        {
            bool breached = aggregate.MeanDriftPerRun >= meanDriftAlertThreshold;

            _metrics?.EaReconciliationMeanDriftPerRun.Record(
                aggregate.MeanDriftPerRun,
                new KeyValuePair<string, object?>("state", breached ? "breached" : "healthy"));
            _metrics?.EaReconciliationWindowRunCount.Record(
                aggregate.RunCount,
                new KeyValuePair<string, object?>("state", breached ? "breached" : "healthy"));

            totalOrphanedPositions += aggregate.TotalOrphanedPositions;
            totalUnknownPositions += aggregate.TotalUnknownPositions;
            totalMismatched += aggregate.TotalMismatchedPositions;
            totalOrphanedOrders += aggregate.TotalOrphanedOrders;
            totalUnknownOrders += aggregate.TotalUnknownOrders;
        }

        if (totalOrphanedPositions > 0)
        {
            _metrics?.EaReconciliationDrift.Add(
                totalOrphanedPositions,
                new KeyValuePair<string, object?>("kind", "orphaned_engine_positions"));
        }

        if (totalUnknownPositions > 0)
        {
            _metrics?.EaReconciliationDrift.Add(
                totalUnknownPositions,
                new KeyValuePair<string, object?>("kind", "unknown_broker_positions"));
        }

        if (totalMismatched > 0)
        {
            _metrics?.EaReconciliationDrift.Add(
                totalMismatched,
                new KeyValuePair<string, object?>("kind", "mismatched_positions"));
        }

        if (totalOrphanedOrders > 0)
        {
            _metrics?.EaReconciliationDrift.Add(
                totalOrphanedOrders,
                new KeyValuePair<string, object?>("kind", "orphaned_engine_orders"));
        }

        if (totalUnknownOrders > 0)
        {
            _metrics?.EaReconciliationDrift.Add(
                totalUnknownOrders,
                new KeyValuePair<string, object?>("kind", "unknown_broker_orders"));
        }
    }

    private async Task<Dictionary<string, long?>> LoadTradingAccountLookupAsync(
        DbContext db,
        IReadOnlyCollection<string> instanceIds,
        CancellationToken ct)
    {
        if (instanceIds.Count == 0)
            return new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

        var instances = await db.Set<EAInstance>()
            .AsNoTracking()
            .Where(instance => !instance.IsDeleted && instanceIds.Contains(instance.InstanceId))
            .Select(instance => new InstanceAccountProjection(
                instance.InstanceId,
                instance.TradingAccountId,
                instance.LastHeartbeat,
                instance.Id))
            .ToListAsync(ct);

        return instances
            .GroupBy(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (long?)group
                    .OrderByDescending(instance => instance.LastHeartbeat)
                    .ThenByDescending(instance => instance.Id)
                    .First()
                    .TradingAccountId,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> UpsertAndDispatchAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        string deduplicationKey,
        EvaluatedReconciliationInstanceAggregate aggregate,
        ReconciliationMonitorSettings settings,
        DateTime windowStartUtc,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_Infrastructure,
            AlertCooldownDefaults.Default_Infrastructure,
            ct);

        string conditionJson = BuildConditionJson(aggregate, settings, windowStartUtc, nowUtc);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(
                candidate => !candidate.IsDeleted
                          && candidate.IsActive
                          && candidate.DeduplicationKey == deduplicationKey,
                ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };

            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.DataQualityIssue;
        }

        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = Truncate(conditionJson, 1000);

        try
        {
            await writeContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(serviceProvider, ex))
        {
            DetachIfAdded(db, alert);

            alert = await db.Set<Alert>()
                .FirstAsync(
                    candidate => !candidate.IsDeleted
                              && candidate.IsActive
                              && candidate.DeduplicationKey == deduplicationKey,
                    ct);

            alert.AlertType = AlertType.DataQualityIssue;
            alert.Severity = AlertSeverity.High;
            alert.CooldownSeconds = cooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = Truncate(conditionJson, 1000);
            await writeContext.SaveChangesAsync(ct);
        }

        if (alert.LastTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(cooldownSeconds))
        {
            return false;
        }

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        try
        {
            await dispatcher.DispatchAsync(alert, BuildAlertMessage(aggregate, settings), ct);
            await writeContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: alert dispatch failed for instance {InstanceId}.",
                WorkerName,
                aggregate.InstanceId);
            return false;
        }
    }

    private async Task<int> ResolveInactiveAlertsAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IReadOnlyCollection<string> activeDeduplicationKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var query = db.Set<Alert>()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey != null
                         && alert.DeduplicationKey.StartsWith(AlertDeduplicationPrefix));

        if (activeDeduplicationKeys.Count > 0)
        {
            query = query.Where(alert => !activeDeduplicationKeys.Contains(alert.DeduplicationKey!));
        }

        var alerts = await query.ToListAsync(ct);
        if (alerts.Count == 0)
            return 0;

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();
        foreach (var alert in alerts)
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
                        "{Worker}: auto-resolve dispatch failed for dedup key {DeduplicationKey}.",
                        WorkerName,
                        alert.DeduplicationKey);
                }
            }

            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        await writeContext.SaveChangesAsync(ct);
        return alerts.Count;
    }

    private async Task<ReconciliationMonitorSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        int pollMinutes = Clamp(
            await ReadIntConfigAsync(db, CK_PollMinutes, DefaultPollMinutes, ct),
            MinPollMinutes,
            MaxPollMinutes);
        int windowMinutes = Clamp(
            await ReadIntConfigAsync(db, CK_WindowMinutes, DefaultWindowMinutes, ct),
            MinWindowMinutes,
            MaxWindowMinutes);
        int meanThreshold = Clamp(
            await ReadIntConfigAsync(db, CK_MeanThreshold, DefaultMeanThreshold, ct),
            MinMeanThreshold,
            MaxMeanThreshold);

        windowMinutes = Math.Max(windowMinutes, pollMinutes);

        return new ReconciliationMonitorSettings(
            TimeSpan.FromMinutes(pollMinutes),
            pollMinutes,
            windowMinutes,
            meanThreshold);
    }

    private static async Task<int> ReadIntConfigAsync(DbContext db, string key, int defaultValue, CancellationToken ct)
    {
        var entry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Key == key, ct);

        return entry?.Value is not null && int.TryParse(entry.Value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string BuildDeduplicationKey(string instanceId)
        => $"{AlertDeduplicationPrefix}{NormalizeInstanceId(instanceId)}";

    private static string BuildAlertMessage(
        EvaluatedReconciliationInstanceAggregate aggregate,
        ReconciliationMonitorSettings settings)
        => aggregate.TradingAccountId.HasValue
            ? $"EA reconciliation drift persisted for instance {aggregate.InstanceId} (account {aggregate.TradingAccountId.Value}) — mean drift per run {aggregate.MeanDriftPerRun:F2} >= {settings.MeanDriftAlertThreshold} across {aggregate.RunCount} runs in the last {settings.WindowMinutes}m. OrphanedEnginePositions={aggregate.TotalOrphanedPositions}, UnknownBrokerPositions={aggregate.TotalUnknownPositions}, MismatchedPositions={aggregate.TotalMismatchedPositions}, OrphanedEngineOrders={aggregate.TotalOrphanedOrders}, UnknownBrokerOrders={aggregate.TotalUnknownOrders}."
            : $"EA reconciliation drift persisted for instance {aggregate.InstanceId} — mean drift per run {aggregate.MeanDriftPerRun:F2} >= {settings.MeanDriftAlertThreshold} across {aggregate.RunCount} runs in the last {settings.WindowMinutes}m. OrphanedEnginePositions={aggregate.TotalOrphanedPositions}, UnknownBrokerPositions={aggregate.TotalUnknownPositions}, MismatchedPositions={aggregate.TotalMismatchedPositions}, OrphanedEngineOrders={aggregate.TotalOrphanedOrders}, UnknownBrokerOrders={aggregate.TotalUnknownOrders}.";

    private static string BuildConditionJson(
        EvaluatedReconciliationInstanceAggregate aggregate,
        ReconciliationMonitorSettings settings,
        DateTime windowStartUtc,
        DateTime observedAtUtc)
        => JsonSerializer.Serialize(new
        {
            source = WorkerName,
            instanceId = aggregate.InstanceId,
            tradingAccountId = aggregate.TradingAccountId,
            windowStartUtc = NormalizeUtc(windowStartUtc),
            observedAtUtc = NormalizeUtc(observedAtUtc),
            windowMinutes = settings.WindowMinutes,
            meanDriftThreshold = settings.MeanDriftAlertThreshold,
            runCount = aggregate.RunCount,
            meanDriftPerRun = aggregate.MeanDriftPerRun,
            totalDrift = aggregate.TotalDrift,
            orphanedEnginePositions = aggregate.TotalOrphanedPositions,
            unknownBrokerPositions = aggregate.TotalUnknownPositions,
            mismatchedPositions = aggregate.TotalMismatchedPositions,
            orphanedEngineOrders = aggregate.TotalOrphanedOrders,
            unknownBrokerOrders = aggregate.TotalUnknownOrders,
            firstObservedRunAtUtc = NormalizeUtc(aggregate.FirstObservedRunAtUtc),
            lastObservedRunAtUtc = NormalizeUtc(aggregate.LastObservedRunAtUtc)
        });

    private static string NormalizeInstanceId(string? instanceId)
        => string.IsNullOrWhiteSpace(instanceId)
            ? "unknown"
            : instanceId.Trim();

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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

    internal readonly record struct EaReconciliationMonitorCycleResult(
        ReconciliationMonitorSettings Settings,
        int WindowRunCount,
        int InstanceCount,
        int AlertingInstanceCount,
        int DispatchedAlertCount,
        int ResolvedAlertCount,
        double MaxMeanDriftPerRun,
        string? WorstInstanceId,
        string? SkippedReason)
    {
        public static EaReconciliationMonitorCycleResult Skipped(
            ReconciliationMonitorSettings settings,
            string reason)
            => new(
                settings,
                WindowRunCount: 0,
                InstanceCount: 0,
                AlertingInstanceCount: 0,
                DispatchedAlertCount: 0,
                ResolvedAlertCount: 0,
                MaxMeanDriftPerRun: 0,
                WorstInstanceId: null,
                SkippedReason: reason);
    }

    internal readonly record struct ReconciliationMonitorSettings(
        TimeSpan PollInterval,
        int PollMinutes,
        int WindowMinutes,
        int MeanDriftAlertThreshold);

    private sealed record ReconciliationInstanceAggregate(
        string InstanceId,
        int RunCount,
        int TotalDrift,
        int TotalOrphanedPositions,
        int TotalUnknownPositions,
        int TotalMismatchedPositions,
        int TotalOrphanedOrders,
        int TotalUnknownOrders,
        DateTime FirstObservedRunAtUtc,
        DateTime LastObservedRunAtUtc)
    {
        public double MeanDriftPerRun => RunCount == 0 ? 0 : TotalDrift / (double)RunCount;
    }

    private sealed record EvaluatedReconciliationInstanceAggregate(
        ReconciliationInstanceAggregate Aggregate,
        long? TradingAccountId)
    {
        public string InstanceId => NormalizeInstanceId(Aggregate.InstanceId);
        public int RunCount => Aggregate.RunCount;
        public int TotalDrift => Aggregate.TotalDrift;
        public int TotalOrphanedPositions => Aggregate.TotalOrphanedPositions;
        public int TotalUnknownPositions => Aggregate.TotalUnknownPositions;
        public int TotalMismatchedPositions => Aggregate.TotalMismatchedPositions;
        public int TotalOrphanedOrders => Aggregate.TotalOrphanedOrders;
        public int TotalUnknownOrders => Aggregate.TotalUnknownOrders;
        public DateTime FirstObservedRunAtUtc => Aggregate.FirstObservedRunAtUtc;
        public DateTime LastObservedRunAtUtc => Aggregate.LastObservedRunAtUtc;
        public double MeanDriftPerRun => Aggregate.MeanDriftPerRun;
    }

    private sealed record InstanceAccountProjection(
        string InstanceId,
        long TradingAccountId,
        DateTime LastHeartbeat,
        long Id);
}
