using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Maintains an exponentially weighted moving-average accuracy signal for active
/// production ML models and owns the corresponding worker-scoped degradation alert.
/// </summary>
public sealed class MLEwmaAccuracyWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLEwmaAccuracyWorker);

    private const string DistributedLockKey = "workers:ml-ewma-accuracy:cycle";
    private const string AlertDedupPrefix = "MLEwma:";

    private const string CK_Enabled = "MLEwma:Enabled";
    private const string CK_InitialDelaySeconds = "MLEwma:InitialDelaySeconds";
    private const string CK_PollSecs = "MLEwma:PollIntervalSeconds";
    private const string CK_Alpha = "MLEwma:Alpha";
    private const string CK_MinPreds = "MLEwma:MinPredictions";
    private const string CK_WarnThr = "MLEwma:WarnThreshold";
    private const string CK_CritThr = "MLEwma:CriticalThreshold";
    private const string CK_AlertDest = "MLEwma:AlertDestination";
    private const string CK_MaxModelsPerCycle = "MLEwma:MaxModelsPerCycle";
    private const string CK_PredictionLogBatchSize = "MLEwma:PredictionLogBatchSize";
    private const string CK_LockTimeoutSeconds = "MLEwma:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSeconds = "MLEwma:DbCommandTimeoutSeconds";

    private static readonly string[] ConfigKeys =
    [
        CK_Enabled,
        CK_InitialDelaySeconds,
        CK_PollSecs,
        CK_Alpha,
        CK_MinPreds,
        CK_WarnThr,
        CK_CritThr,
        CK_AlertDest,
        CK_MaxModelsPerCycle,
        CK_PredictionLogBatchSize,
        CK_LockTimeoutSeconds,
        CK_DbCommandTimeoutSeconds
    ];

    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLEwmaAccuracyWorker> _logger;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly MLEwmaAccuracyOptions _options;
    private int _missingDistributedLockWarningEmitted;
    private int _consecutiveCycleFailuresField;

    private int ConsecutiveCycleFailures
    {
        get => Volatile.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    public MLEwmaAccuracyWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLEwmaAccuracyWorker> logger,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        MLEwmaAccuracyOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new MLEwmaAccuracyOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Maintains EWMA live accuracy for active production ML models.",
            TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds)));

        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(NormalizePollSeconds(_options.PollIntervalSeconds));

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName)
                               + TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(_options.InitialDelaySeconds));
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                if (lastSuccessUtc != DateTime.MinValue)
                    _metrics?.MLEwmaTimeSinceLastSuccessSec.Record((nowUtc - lastSuccessUtc).TotalSeconds);

                if (nowUtc - lastCycleStartUtc >= currentPollInterval)
                {
                    lastCycleStartUtc = nowUtc;
                    var started = Stopwatch.GetTimestamp();

                    try
                    {
                        _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                        var result = await RunCycleAsync(stoppingToken);
                        currentPollInterval = result.Settings.PollInterval;

                        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        _healthMonitor?.RecordCycleSuccess(WorkerName, elapsedMs);
                        _metrics?.WorkerCycleDurationMs.Record(
                            elapsedMs,
                            new KeyValuePair<string, object?>("worker", WorkerName));
                        _metrics?.MLEwmaCycleDurationMs.Record(elapsedMs);

                        if (result.SkippedReason is { Length: > 0 })
                        {
                            _logger.LogDebug(
                                "{Worker}: cycle skipped ({Reason}).",
                                WorkerName,
                                result.SkippedReason);
                        }
                        else
                        {
                            _logger.LogDebug(
                                "{Worker}: candidates={Candidates}, evaluated={Evaluated}, skipped={Skipped}, logs={Logs}, alertsDispatched={Dispatched}, alertsResolved={Resolved}, alertsEscalated={Escalated}.",
                                WorkerName,
                                result.CandidateModelCount,
                                result.ModelsEvaluated,
                                result.ModelsSkipped,
                                result.PredictionLogsProcessed,
                                result.AlertsDispatched,
                                result.AlertsResolved,
                                result.AlertsEscalated);
                        }

                        var previousFailures = ConsecutiveCycleFailures;
                        if (previousFailures > 0)
                        {
                            _healthMonitor?.RecordRecovery(WorkerName, previousFailures);
                            _logger.LogInformation(
                                "{Worker}: recovered after {Failures} consecutive failure(s).",
                                WorkerName,
                                previousFailures);
                        }

                        ConsecutiveCycleFailures = 0;
                        lastSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _consecutiveCycleFailuresField);
                        _healthMonitor?.RecordRetry(WorkerName);
                        _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                        _metrics?.WorkerErrors.Add(
                            1,
                            new KeyValuePair<string, object?>("worker", WorkerName),
                            new KeyValuePair<string, object?>("reason", "ml_ewma_accuracy_cycle"));
                        _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                    }
                }

                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped.", WorkerName);
        }
    }

    internal async Task<EwmaCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readCtx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        var settings = await LoadSettingsAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            RecordCycleSkipped("disabled");
            return EwmaCycleResult.Skipped(settings, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics?.MLEwmaLockAttempts.Add(1, Tag("outcome", "unavailable"));
            if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate EWMA cycles are possible in multi-instance deployments.",
                    WorkerName);
            }
        }
        else
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey,
                TimeSpan.FromSeconds(settings.LockTimeoutSeconds),
                ct);

            if (cycleLock is null)
            {
                _metrics?.MLEwmaLockAttempts.Add(1, Tag("outcome", "busy"));
                RecordCycleSkipped("lock_busy");
                return EwmaCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLEwmaLockAttempts.Add(1, Tag("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await UpdateEwmaCoreAsync(readCtx, writeCtx, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal async Task UpdateEwmaAsync(
        DbContext readCtx,
        DbContext writeCtx,
        CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(readCtx, _options, ct);
        ApplyCommandTimeout(readCtx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
            return;

        await UpdateEwmaCoreAsync(readCtx, writeCtx, settings, ct);
    }

    private async Task<EwmaCycleResult> UpdateEwmaCoreAsync(
        DbContext readCtx,
        DbContext writeCtx,
        EwmaWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var activeModelQuery = readCtx.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && (m.Status == MLModelStatus.Active || m.IsFallbackChampion)
                        && !m.IsSuppressed
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer);

        var activeModels = await activeModelQuery
            .OrderBy(m => m.Id)
            .Take(settings.MaxModelsPerCycle + 1)
            .Select(m => new ActiveModelSnapshot(m.Id, m.Symbol, m.Timeframe))
            .ToListAsync(ct);

        var truncated = activeModels.Count > settings.MaxModelsPerCycle;
        var skippedByLimit = 0;
        if (truncated)
        {
            activeModels.RemoveAt(activeModels.Count - 1);
            var totalActive = await activeModelQuery.CountAsync(ct);
            skippedByLimit = Math.Max(0, totalActive - settings.MaxModelsPerCycle);
            if (skippedByLimit > 0)
                _metrics?.MLEwmaModelsSkipped.Add(skippedByLimit, Tag("reason", "cycle_limit"));
        }

        var evaluated = 0;
        var skipped = skippedByLimit;
        var logsProcessed = 0;
        var alertsDispatched = 0;
        var alertsResolved = 0;
        var alertsEscalated = 0;
        var activeDedupKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();
            activeDedupKeys.Add(BuildDeduplicationKey(model.Id, model.Symbol, model.Timeframe));

            try
            {
                var outcome = await UpdateModelEwmaAsync(model, settings, readCtx, writeCtx, nowUtc, ct);

                if (outcome.Evaluated)
                    evaluated++;
                if (outcome.SkipReason is { Length: > 0 })
                {
                    skipped++;
                    _metrics?.MLEwmaModelsSkipped.Add(1, Tag("reason", outcome.SkipReason));
                }

                if (outcome.NewPredictionLogs > 0)
                    _metrics?.MLEwmaPredictionLogsProcessed.Add(outcome.NewPredictionLogs);
                if (outcome.LatestEwma.HasValue)
                    _metrics?.MLEwmaAccuracy.Record(outcome.LatestEwma.Value);
                if (outcome.AlertDispatched)
                    alertsDispatched++;
                if (outcome.AlertResolved)
                    alertsResolved++;
                if (outcome.AlertEscalated)
                    alertsEscalated++;

                logsProcessed += outcome.NewPredictionLogs;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _metrics?.MLEwmaModelsSkipped.Add(1, Tag("reason", "model_error"));
                _logger.LogWarning(
                    ex,
                    "{Worker}: update failed for model {ModelId} ({Symbol}/{Timeframe}); skipping.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        if (!truncated)
        {
            var staleResolved = await ResolveStaleAlertsAsync(writeCtx, activeDedupKeys, nowUtc, ct);
            alertsResolved += staleResolved;
        }

        if (evaluated > 0)
            _metrics?.MLEwmaModelsEvaluated.Add(evaluated);
        RecordAlertTransitions(alertsDispatched, alertsResolved, alertsEscalated);

        return new EwmaCycleResult(
            settings,
            CandidateModelCount: activeModels.Count + skippedByLimit,
            ModelsEvaluated: evaluated,
            ModelsSkipped: skipped,
            PredictionLogsProcessed: logsProcessed,
            AlertsDispatched: alertsDispatched,
            AlertsResolved: alertsResolved,
            AlertsEscalated: alertsEscalated,
            SkippedReason: null);
    }

    private async Task<ModelEwmaOutcome> UpdateModelEwmaAsync(
        ActiveModelSnapshot model,
        EwmaWorkerSettings settings,
        DbContext readCtx,
        DbContext writeCtx,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var existing = await readCtx.Set<MLModelEwmaAccuracy>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.MLModelId == model.Id, ct);

        var lastResolvedAt = existing?.LastOutcomeRecordedAt
                             ?? existing?.LastPredictionAt
                             ?? DateTime.MinValue;
        var lastPredictionLogId = existing?.LastPredictionLogId ?? 0L;
        var ewma = ClampProbability(existing?.EwmaAccuracy ?? 0.5, 0.5);
        var total = existing?.TotalPredictions ?? 0;
        var lastPredictionAt = existing?.LastPredictionAt ?? DateTime.MinValue;
        var lastOutcomeAt = existing?.LastOutcomeRecordedAt
                            ?? existing?.LastPredictionAt
                            ?? DateTime.MinValue;
        var lastLogId = existing?.LastPredictionLogId ?? 0L;
        var processed = 0;

        while (true)
        {
            var batch = await LoadResolvedPredictionBatchAsync(
                readCtx,
                model.Id,
                lastResolvedAt,
                lastPredictionLogId,
                settings.PredictionLogBatchSize,
                ct);

            if (batch.Count == 0)
                break;

            foreach (var log in batch)
            {
                ewma = settings.Alpha * (log.Correct ? 1.0 : 0.0) + (1.0 - settings.Alpha) * ewma;
                ewma = Math.Clamp(ewma, 0.0, 1.0);
                total++;
                processed++;
                lastPredictionAt = log.PredictedAt;
                lastOutcomeAt = log.ResolvedAt;
                lastResolvedAt = log.ResolvedAt;
                lastLogId = log.Id;
                lastPredictionLogId = log.Id;
            }

            if (batch.Count < settings.PredictionLogBatchSize)
                break;
        }

        if (processed == 0)
        {
            if (existing is null)
                return ModelEwmaOutcome.Skipped("no_resolved_predictions");

            var existingAlertOutcome = await EvaluateAlertStateAsync(
                writeCtx,
                model,
                ewma,
                total,
                settings,
                existing.LastOutcomeRecordedAt ?? existing.LastPredictionAt,
                existing.LastPredictionLogId,
                nowUtc,
                ct);

            return new ModelEwmaOutcome(
                Evaluated: true,
                NewPredictionLogs: 0,
                LatestEwma: ewma,
                AlertDispatched: existingAlertOutcome.AlertDispatched,
                AlertResolved: existingAlertOutcome.AlertResolved,
                AlertEscalated: existingAlertOutcome.AlertEscalated,
                SkipReason: null);
        }

        await UpsertEwmaStateAsync(
            writeCtx,
            model,
            ewma,
            total,
            lastPredictionAt,
            lastOutcomeAt,
            lastLogId,
            settings.Alpha,
            nowUtc,
            ct);

        _logger.LogDebug(
            "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) ewma={Ewma:P2}, total={Total}, new={New}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            ewma,
            total,
            processed);

        var alertOutcome = await EvaluateAlertStateAsync(
            writeCtx,
            model,
            ewma,
            total,
            settings,
            lastOutcomeAt,
            lastLogId,
            nowUtc,
            ct);

        return new ModelEwmaOutcome(
            Evaluated: true,
            NewPredictionLogs: processed,
            LatestEwma: ewma,
            AlertDispatched: alertOutcome.AlertDispatched,
            AlertResolved: alertOutcome.AlertResolved,
            AlertEscalated: alertOutcome.AlertEscalated,
            SkipReason: null);
    }

    private static async Task<List<PredictionOutcomeSnapshot>> LoadResolvedPredictionBatchAsync(
        DbContext readCtx,
        long modelId,
        DateTime lastResolvedAt,
        long lastPredictionLogId,
        int batchSize,
        CancellationToken ct)
    {
        return await readCtx.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l => l.MLModelId == modelId
                        && !l.IsDeleted
                        && l.ModelRole == ModelRole.Champion
                        && l.DirectionCorrect != null
                        && ((l.OutcomeRecordedAt ?? l.PredictedAt) > lastResolvedAt
                            || ((l.OutcomeRecordedAt ?? l.PredictedAt) == lastResolvedAt
                                && l.Id > lastPredictionLogId)))
            .OrderBy(l => l.OutcomeRecordedAt ?? l.PredictedAt)
            .ThenBy(l => l.Id)
            .Take(batchSize)
            .Select(l => new PredictionOutcomeSnapshot(
                l.Id,
                l.PredictedAt,
                l.OutcomeRecordedAt ?? l.PredictedAt,
                l.DirectionCorrect!.Value))
            .ToListAsync(ct);
    }

    private static async Task UpsertEwmaStateAsync(
        DbContext writeCtx,
        ActiveModelSnapshot model,
        double ewma,
        int total,
        DateTime lastPredictionAt,
        DateTime lastOutcomeAt,
        long lastLogId,
        double alpha,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var rows = await writeCtx.Set<MLModelEwmaAccuracy>()
            .Where(r => r.MLModelId == model.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Symbol, model.Symbol)
                .SetProperty(r => r.Timeframe, model.Timeframe)
                .SetProperty(r => r.EwmaAccuracy, ewma)
                .SetProperty(r => r.Alpha, alpha)
                .SetProperty(r => r.TotalPredictions, total)
                .SetProperty(r => r.LastPredictionAt, lastPredictionAt)
                .SetProperty(r => r.LastOutcomeRecordedAt, lastOutcomeAt)
                .SetProperty(r => r.LastPredictionLogId, lastLogId)
                .SetProperty(r => r.ComputedAt, nowUtc)
                .SetProperty(r => r.IsDeleted, false),
                ct);

        if (rows > 0)
            return;

        writeCtx.Set<MLModelEwmaAccuracy>().Add(new MLModelEwmaAccuracy
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            EwmaAccuracy = ewma,
            Alpha = alpha,
            TotalPredictions = total,
            LastPredictionAt = lastPredictionAt,
            LastOutcomeRecordedAt = lastOutcomeAt,
            LastPredictionLogId = lastLogId,
            ComputedAt = nowUtc,
        });

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            foreach (var entry in writeCtx.ChangeTracker.Entries<MLModelEwmaAccuracy>())
            {
                if (entry.Entity.MLModelId == model.Id && entry.State == EntityState.Added)
                    entry.State = EntityState.Detached;
            }

            var retryRows = await writeCtx.Set<MLModelEwmaAccuracy>()
                .Where(r => r.MLModelId == model.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Symbol, model.Symbol)
                    .SetProperty(r => r.Timeframe, model.Timeframe)
                    .SetProperty(r => r.EwmaAccuracy, ewma)
                    .SetProperty(r => r.Alpha, alpha)
                    .SetProperty(r => r.TotalPredictions, total)
                    .SetProperty(r => r.LastPredictionAt, lastPredictionAt)
                    .SetProperty(r => r.LastOutcomeRecordedAt, lastOutcomeAt)
                    .SetProperty(r => r.LastPredictionLogId, lastLogId)
                    .SetProperty(r => r.ComputedAt, nowUtc)
                    .SetProperty(r => r.IsDeleted, false),
                    ct);

            if (retryRows == 0)
            {
                throw new InvalidOperationException(
                    $"Unable to insert or update EWMA accuracy state for ML model {model.Id}.",
                    ex);
            }
        }
    }

    private async Task<AlertOutcome> EvaluateAlertStateAsync(
        DbContext writeCtx,
        ActiveModelSnapshot model,
        double ewma,
        int totalPredictions,
        EwmaWorkerSettings settings,
        DateTime lastOutcomeAt,
        long lastLogId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dedupKey = BuildDeduplicationKey(model.Id, model.Symbol, model.Timeframe);

        if (totalPredictions < settings.MinPredictions || ewma >= settings.WarnThreshold)
        {
            var resolved = await ResolveAlertAsync(writeCtx, dedupKey, nowUtc, ct);
            return new AlertOutcome(false, resolved, false);
        }

        var severityText = ewma < settings.CriticalThreshold ? "critical" : "warning";
        var severity = ewma < settings.CriticalThreshold
            ? AlertSeverity.Critical
            : AlertSeverity.Medium;

        var alert = await writeCtx.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey && !a.IsDeleted, ct);

        var wasActive = alert is { IsActive: true, AutoResolvedAt: null };
        var escalated = wasActive && alert!.Severity != AlertSeverity.Critical && severity == AlertSeverity.Critical;
        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = dedupKey,
                CooldownSeconds = settings.PollIntervalSeconds,
            };
            writeCtx.Set<Alert>().Add(alert);
        }

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = model.Symbol;
        alert.ConditionJson = BuildAlertPayload(
            model,
            ewma,
            settings,
            totalPredictions,
            severityText,
            lastOutcomeAt,
            lastLogId);
        alert.Severity = severity;
        alert.CooldownSeconds = settings.PollIntervalSeconds;
        alert.IsActive = true;
        alert.AutoResolvedAt = null;

        await writeCtx.SaveChangesAsync(ct);

        if (!wasActive)
        {
            _logger.LogWarning(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) EWMA={Ewma:P2} below {Severity} threshold after {Total} outcomes.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                ewma,
                severityText,
                totalPredictions);
        }

        return new AlertOutcome(AlertDispatched: !wasActive, AlertResolved: false, AlertEscalated: escalated);
    }

    private static async Task<bool> ResolveAlertAsync(
        DbContext writeCtx,
        string dedupKey,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await writeCtx.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey
                                      && a.IsActive
                                      && !a.IsDeleted, ct);
        if (alert is null)
            return false;

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeCtx.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<int> ResolveStaleAlertsAsync(
        DbContext writeCtx,
        IReadOnlySet<string> activeDedupKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var activeWorkerAlerts = await writeCtx.Set<Alert>()
            .Where(a => a.IsActive
                        && !a.IsDeleted
                        && a.DeduplicationKey != null
                        && a.DeduplicationKey.StartsWith(AlertDedupPrefix))
            .ToListAsync(ct);

        var staleAlerts = activeWorkerAlerts
            .Where(a => a.DeduplicationKey is not null
                        && !activeDedupKeys.Contains(a.DeduplicationKey))
            .ToList();

        if (staleAlerts.Count == 0)
            return 0;

        foreach (var alert in staleAlerts)
        {
            alert.IsActive = false;
            alert.AutoResolvedAt ??= nowUtc;
        }

        await writeCtx.SaveChangesAsync(ct);
        return staleAlerts.Count;
    }

    internal static async Task<EwmaWorkerSettings> LoadSettingsAsync(
        DbContext db,
        MLEwmaAccuracyOptions options,
        CancellationToken ct)
    {
        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => ConfigKeys.Contains(c.Key) && !c.IsDeleted)
            .Select(c => new { c.Id, c.Key, c.Value, c.LastUpdatedAt })
            .ToListAsync(ct);

        var values = rows
            .Where(c => c.Value is not null)
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(c => c.LastUpdatedAt)
                    .ThenBy(c => c.Id)
                    .Last().Value!,
                StringComparer.Ordinal);

        var warnThreshold = NormalizeProbability(
            GetConfig(values, CK_WarnThr, options.WarnThreshold),
            0.50);
        var criticalThreshold = NormalizeProbability(
            GetConfig(values, CK_CritThr, options.CriticalThreshold),
            0.48);
        if (criticalThreshold > warnThreshold)
            criticalThreshold = warnThreshold;

        var pollSeconds = NormalizePollSeconds(
            GetConfig(values, CK_PollSecs, options.PollIntervalSeconds));

        return new EwmaWorkerSettings(
            Enabled: GetConfig(values, CK_Enabled, options.Enabled),
            InitialDelay: TimeSpan.FromSeconds(NormalizeInitialDelaySeconds(
                GetConfig(values, CK_InitialDelaySeconds, options.InitialDelaySeconds))),
            PollInterval: TimeSpan.FromSeconds(pollSeconds),
            PollIntervalSeconds: pollSeconds,
            Alpha: NormalizeAlpha(GetConfig(values, CK_Alpha, options.Alpha)),
            MinPredictions: NormalizeMinPredictions(GetConfig(values, CK_MinPreds, options.MinPredictions)),
            WarnThreshold: warnThreshold,
            CriticalThreshold: criticalThreshold,
            AlertDestination: NormalizeDestination(GetConfig(values, CK_AlertDest, options.AlertDestination)),
            MaxModelsPerCycle: NormalizeMaxModelsPerCycle(
                GetConfig(values, CK_MaxModelsPerCycle, options.MaxModelsPerCycle)),
            PredictionLogBatchSize: NormalizePredictionLogBatchSize(
                GetConfig(values, CK_PredictionLogBatchSize, options.PredictionLogBatchSize)),
            LockTimeoutSeconds: NormalizeLockTimeoutSeconds(
                GetConfig(values, CK_LockTimeoutSeconds, options.LockTimeoutSeconds)),
            DbCommandTimeoutSeconds: NormalizeDbCommandTimeoutSeconds(
                GetConfig(values, CK_DbCommandTimeoutSeconds, options.DbCommandTimeoutSeconds)));
    }

    private static T GetConfig<T>(
        IReadOnlyDictionary<string, string> values,
        string key,
        T defaultValue)
    {
        if (!values.TryGetValue(key, out var raw))
            return defaultValue;

        return TryConvertConfig(raw, out T parsed)
            ? parsed
            : defaultValue;
    }

    private static bool TryConvertConfig<T>(string value, out T result)
    {
        object? parsed = null;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
        {
            parsed = value;
        }
        else if (targetType == typeof(int)
                 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            parsed = intValue;
        }
        else if (targetType == typeof(double)
                 && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parsed = doubleValue;
        }
        else if (targetType == typeof(bool)
                 && TryParseBool(value, out var boolValue))
        {
            parsed = boolValue;
        }

        if (parsed is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }

    internal static double NormalizeAlpha(double value)
        => double.IsFinite(value) && value > 0.0 && value <= 1.0 ? value : 0.05;

    internal static double NormalizeProbability(double value, double defaultValue)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0 ? value : defaultValue;

    internal static int NormalizeMinPredictions(int value)
        => value is >= 1 and <= 1_000_000 ? value : 20;

    internal static int NormalizePollSeconds(int value)
        => value is >= 1 and <= 86_400 ? value : 600;

    internal static string NormalizeDestination(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "ml-ops";

        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    internal static int NormalizeInitialDelaySeconds(int value)
        => value is >= 0 and <= 86_400 ? value : 0;

    internal static int NormalizeMaxModelsPerCycle(int value)
        => value is >= 1 and <= 250_000 ? value : 10_000;

    internal static int NormalizePredictionLogBatchSize(int value)
        => value is >= 1 and <= 10_000 ? value : 1_000;

    internal static int NormalizeLockTimeoutSeconds(int value)
        => value is >= 0 and <= 300 ? value : 5;

    internal static int NormalizeDbCommandTimeoutSeconds(int value)
        => value is >= 1 and <= 600 ? value : 30;

    private static double ClampProbability(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : fallback;

    private static bool TryParseBool(string value, out bool result)
    {
        var normalized = value.Trim();
        if (bool.TryParse(normalized, out result))
            return true;

        if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private static string BuildDeduplicationKey(long modelId, string symbol, Timeframe timeframe)
        => $"{AlertDedupPrefix}{modelId}:{symbol}:{timeframe}";

    private static string BuildAlertPayload(
        ActiveModelSnapshot model,
        double ewma,
        EwmaWorkerSettings settings,
        int totalPredictions,
        string severity,
        DateTime lastOutcomeAt,
        long lastLogId)
        => JsonSerializer.Serialize(new
        {
            reason = "ewma_accuracy_degraded",
            severity,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            modelId = model.Id,
            ewmaAccuracy = ewma,
            alpha = settings.Alpha,
            warnThreshold = settings.WarnThreshold,
            criticalThreshold = settings.CriticalThreshold,
            totalPredictions,
            alertDestination = settings.AlertDestination,
            lastOutcomeRecordedAt = lastOutcomeAt,
            lastPredictionLogId = lastLogId,
        });

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLEwmaCyclesSkipped.Add(1, Tag("reason", reason));

    private void RecordAlertTransitions(int dispatched, int resolved, int escalated)
    {
        if (dispatched > 0)
            _metrics?.MLEwmaAlertTransitions.Add(dispatched, Tag("transition", "dispatched"));
        if (resolved > 0)
            _metrics?.MLEwmaAlertTransitions.Add(resolved, Tag("transition", "resolved"));
        if (escalated > 0)
            _metrics?.MLEwmaAlertTransitions.Add(escalated, Tag("transition", "escalated"));
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value)
        => new(key, value);

    private static TimeSpan CalculateBackoffDelay(int consecutiveFailures)
    {
        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private static void ApplyCommandTimeout(DbContext db, int seconds)
    {
        try
        {
            if (db.Database.IsRelational())
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(seconds));
        }
        catch (InvalidOperationException)
        {
            // Some providers do not expose relational command timeout configuration.
        }
    }

    private sealed record ActiveModelSnapshot(long Id, string Symbol, Timeframe Timeframe);

    private sealed record PredictionOutcomeSnapshot(
        long Id,
        DateTime PredictedAt,
        DateTime ResolvedAt,
        bool Correct);

    private sealed record ModelEwmaOutcome(
        bool Evaluated,
        int NewPredictionLogs,
        double? LatestEwma,
        bool AlertDispatched,
        bool AlertResolved,
        bool AlertEscalated,
        string? SkipReason)
    {
        public static ModelEwmaOutcome Skipped(string reason)
            => new(false, 0, null, false, false, false, reason);
    }

    private sealed record AlertOutcome(
        bool AlertDispatched,
        bool AlertResolved,
        bool AlertEscalated);

    internal sealed record EwmaWorkerSettings(
        bool Enabled,
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollIntervalSeconds,
        double Alpha,
        int MinPredictions,
        double WarnThreshold,
        double CriticalThreshold,
        string AlertDestination,
        int MaxModelsPerCycle,
        int PredictionLogBatchSize,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds);

    internal sealed record EwmaCycleResult(
        EwmaWorkerSettings Settings,
        int CandidateModelCount,
        int ModelsEvaluated,
        int ModelsSkipped,
        int PredictionLogsProcessed,
        int AlertsDispatched,
        int AlertsResolved,
        int AlertsEscalated,
        string? SkippedReason)
    {
        public static EwmaCycleResult Skipped(EwmaWorkerSettings settings, string reason)
            => new(settings, 0, 0, 0, 0, 0, 0, 0, reason);
    }
}
