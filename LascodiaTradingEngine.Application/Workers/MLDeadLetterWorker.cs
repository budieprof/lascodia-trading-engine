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
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Safely recovers ML training runs that have exhausted normal retries and remained
/// failed beyond the configured dead-letter window.
/// </summary>
public sealed class MLDeadLetterWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLDeadLetterWorker);

    private const string DistributedLockKey = "ml:dead-letter:cycle";
    private const string RetryCapAlertPrefix = "ml-dead-letter:retry-cap:";
    private const int AlertConditionMaxLength = 1_000;
    private const int MaxErrorMessageLength = 4_000;

    private const string CK_Enabled = "MLDeadLetter:Enabled";
    private const string CK_PollSecs = "MLDeadLetter:PollIntervalSeconds";
    private const string CK_PollJitterSecs = "MLDeadLetter:PollJitterSeconds";
    private const string CK_RetryDays = "MLDeadLetter:RetryAfterDays";
    private const string CK_MaxRetries = "MLDeadLetter:MaxRetries";
    private const string CK_MaxRunsPerCycle = "MLDeadLetter:MaxRunsPerCycle";
    private const string CK_MaxRequeuesPerCycle = "MLDeadLetter:MaxRequeuesPerCycle";
    private const string CK_LockTimeout = "MLDeadLetter:LockTimeoutSeconds";
    private const string CK_AlertCooldown = "MLDeadLetter:AlertCooldownSeconds";
    private const string CK_AlertDest = "MLDeadLetter:AlertDestination";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDeadLetterWorker> _logger;
    private readonly MLDeadLetterOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private bool _missingAlertDispatcherWarningEmitted;

    public MLDeadLetterWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDeadLetterWorker> logger,
        MLDeadLetterOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDeadLetterOptions();
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialSettings = BuildSettings(_options);
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Recovers aged failed MLTrainingRun rows after normal retries are exhausted and alerts when manual intervention is required.",
            initialSettings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + initialSettings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var delaySettings = BuildSettings(_options);

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    var result = await RunCycleAsync(stoppingToken);
                    delaySettings = result.Settings;

                    var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidatesScanned);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        Tag("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                    }
                    else if (result.RunsRequeued > 0 || result.RetryCapsReached > 0 || result.AlertsResolved > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: scanned={Scanned}, requeued={Requeued}, skipped={Skipped}, retryCaps={RetryCaps}, alertsDispatched={AlertsDispatched}, alertsResolved={AlertsResolved}.",
                            WorkerName,
                            result.CandidatesScanned,
                            result.RunsRequeued,
                            result.RunsSkipped,
                            result.RetryCapsReached,
                            result.AlertsDispatched,
                            result.AlertsResolved);
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics?.WorkerErrors.Add(1, Tag("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(delaySettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
    }

    internal async Task<MLDeadLetterCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return MLDeadLetterCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLDeadLetterLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate dead-letter recovery is possible in multi-instance deployments.",
                        WorkerName);
                    _missingDistributedLockWarningEmitted = true;
                }
            }
            else
            {
                cycleLock = await _distributedLock.TryAcquireAsync(
                    DistributedLockKey,
                    settings.LockTimeout,
                    ct);

                if (cycleLock is null)
                {
                    _metrics?.MLDeadLetterLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLDeadLetterCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLDeadLetterLockAttempts.Add(1, Tag("outcome", "acquired"));
            }

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                    var db = writeContext.GetDbContext();
                    var dispatcher = scope.ServiceProvider.GetService<IAlertDispatcher>();

                    if (dispatcher is null && !_missingAlertDispatcherWarningEmitted)
                    {
                        _logger.LogWarning(
                            "{Worker} could not resolve IAlertDispatcher; retry-cap alerts will be persisted but not notified.",
                            WorkerName);
                        _missingAlertDispatcherWarningEmitted = true;
                    }

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return MLDeadLetterCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await ScanDeadLetterRunsAsync(writeContext, db, dispatcher, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLDeadLetterCycleDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<MLDeadLetterCycleResult> ScanDeadLetterRunsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDeadLetterWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc - settings.RetryAfter;
        var load = await LoadEligibleCandidatesAsync(db, settings, cutoff, nowUtc, ct);

        var candidatesScanned = load.Candidates.Count;
        var runsSkipped = load.InvalidCandidatesSkipped + load.DuplicatePairCandidatesSkipped;
        var runsRequeued = 0;
        var retryCapsReached = 0;
        var alertsDispatched = 0;
        var alertsResolved = 0;
        var countersReset = 0;

        if (load.Truncated)
            RecordRunSkipped("max_runs_truncated");

        for (var i = 0; i < load.InvalidCandidatesSkipped; i++)
            RecordRunSkipped("invalid_candidate");
        for (var i = 0; i < load.DuplicatePairCandidatesSkipped; i++)
            RecordRunSkipped("duplicate_pair");

        foreach (var candidate in load.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            _metrics?.MLDeadLetterRunsScanned.Add(
                1,
                Tag("symbol", candidate.Symbol),
                Tag("timeframe", candidate.Timeframe));
            _metrics?.MLDeadLetterCandidateAgeDays.Record(
                Math.Max(0, (nowUtc - candidate.CompletedAt).TotalDays),
                Tag("symbol", candidate.Symbol),
                Tag("timeframe", candidate.Timeframe));

            RunProcessResult result;
            try
            {
                result = await ProcessCandidateAsync(
                    writeContext,
                    db,
                    dispatcher,
                    candidate,
                    settings,
                    nowUtc,
                    requeuesAlreadyIssued: runsRequeued,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                runsSkipped++;
                RecordRunSkipped("candidate_error");
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to process dead-letter run {RunId} ({Symbol}/{Timeframe}); skipping.",
                    WorkerName,
                    candidate.Id,
                    candidate.Symbol,
                    candidate.Timeframe);
                continue;
            }

            if (result.SkippedReason is { Length: > 0 })
            {
                runsSkipped++;
                RecordRunSkipped(result.SkippedReason);
            }

            if (result.Requeued)
            {
                runsRequeued++;
                _metrics?.MLDeadLetterRunsRequeued.Add(
                    1,
                    Tag("symbol", candidate.Symbol),
                    Tag("timeframe", candidate.Timeframe));
            }

            if (result.RetryCapReached)
            {
                retryCapsReached++;
                _metrics?.MLDeadLetterRetryCapsReached.Add(
                    1,
                    Tag("symbol", candidate.Symbol),
                    Tag("timeframe", candidate.Timeframe));
            }

            if (result.AlertDispatched)
                alertsDispatched++;
            if (result.AlertResolved)
                alertsResolved++;
            if (result.CounterReset)
                countersReset++;
        }

        var recoveredAlerts = await ResolveRecoveredRetryCapAlertsAsync(
            writeContext,
            db,
            dispatcher,
            settings,
            ct);
        alertsResolved += recoveredAlerts.AlertsResolved;
        countersReset += recoveredAlerts.CountersReset;

        if (load.Candidates.Count == 0 && alertsResolved == 0)
        {
            RecordCycleSkipped("no_eligible_runs");
            return new MLDeadLetterCycleResult(
                settings,
                "no_eligible_runs",
                0,
                runsSkipped,
                0,
                0,
                0,
                0,
                countersReset,
                load.Truncated);
        }

        return new MLDeadLetterCycleResult(
            settings,
            null,
            candidatesScanned,
            runsSkipped,
            runsRequeued,
            retryCapsReached,
            alertsDispatched,
            alertsResolved,
            countersReset,
            load.Truncated);
    }

    private async Task<RunProcessResult> ProcessCandidateAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        DeadLetterRunCandidate candidate,
        MLDeadLetterWorkerSettings settings,
        DateTime nowUtc,
        int requeuesAlreadyIssued,
        CancellationToken ct)
    {
        var successSinceFailure = await HasCompletedSuccessSinceAsync(db, candidate.Symbol, candidate.Timeframe, candidate.CompletedAt, ct);
        if (successSinceFailure)
        {
            var counterReset = await ResetRetryCounterAsync(db, candidate.Symbol, candidate.Timeframe, ct);
            var alertResolved = await ResolveRetryCapAlertAsync(
                writeContext,
                db,
                dispatcher,
                settings,
                candidate.Symbol,
                candidate.Timeframe,
                ct);

            return new RunProcessResult("success_since_failure", false, false, false, alertResolved, counterReset);
        }

        var activeRunExists = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run => run.Symbol != null
                          && run.Symbol.ToUpper() == candidate.Symbol
                          && run.Timeframe == candidate.Timeframe
                          && !run.IsDeleted
                          && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running),
                ct);

        if (activeRunExists)
            return RunProcessResult.Skipped("active_run_exists");

        var retryCount = await GetRetryCountAsync(db, candidate.Symbol, candidate.Timeframe, ct);
        if (retryCount >= settings.MaxRetries)
        {
            var dispatched = await UpsertAndDispatchRetryCapAlertAsync(
                writeContext,
                db,
                dispatcher,
                candidate,
                retryCount,
                settings,
                nowUtc,
                ct);

            return new RunProcessResult(null, false, true, dispatched, false, false);
        }

        if (requeuesAlreadyIssued >= settings.MaxRequeuesPerCycle)
            return RunProcessResult.Skipped("max_requeues_reached");

        var requeued = await TryRequeueRunAsync(
            writeContext,
            db,
            candidate,
            retryCount,
            settings,
            nowUtc,
            ct);

        return requeued
            ? new RunProcessResult(null, true, false, false, false, false)
            : RunProcessResult.Skipped("stale_state");
    }

    private async Task<bool> TryRequeueRunAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        DeadLetterRunCandidate candidate,
        int retryCount,
        MLDeadLetterWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var run = await db.Set<MLTrainingRun>()
            .FirstOrDefaultAsync(item => item.Id == candidate.Id, ct);

        if (run is null
            || run.IsDeleted
            || run.Status != RunStatus.Failed
            || !run.CompletedAt.HasValue
            || run.CompletedAt.Value != candidate.CompletedAt)
        {
            return false;
        }

        run.Status = RunStatus.Queued;
        run.AttemptCount = 0;
        run.NextRetryAt = null;
        run.CompletedAt = null;
        run.PickedUpAt = null;
        run.WorkerInstanceId = null;
        run.TrainingDurationMs = null;
        run.StartedAt = nowUtc;
        run.ErrorMessage = AppendRetryNote(candidate.ErrorMessage, nowUtc, retryCount + 1, settings.MaxRetries);

        await writeContext.SaveChangesAsync(ct);

        await EngineConfigUpsert.UpsertAsync(
            db,
            BuildRetryCountKey(candidate.Symbol, candidate.Timeframe),
            (retryCount + 1).ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            $"Dead-letter recovery retry counter for {candidate.Symbol}/{candidate.Timeframe}.",
            isHotReloadable: true,
            ct: ct);

        _logger.LogWarning(
            "{Worker}: reset failed MLTrainingRun {RunId} ({Symbol}/{Timeframe}) to Queued; dead-letter retry {Retry}/{MaxRetries}.",
            WorkerName,
            candidate.Id,
            candidate.Symbol,
            candidate.Timeframe,
            retryCount + 1,
            settings.MaxRetries);

        return true;
    }

    private async Task<bool> UpsertAndDispatchRetryCapAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        DeadLetterRunCandidate candidate,
        int retryCount,
        MLDeadLetterWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var deduplicationKey = BuildRetryCapAlertKey(candidate.Symbol, candidate.Timeframe);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(existing => existing.AlertType == AlertType.MLModelDegraded
                                          && existing.IsActive
                                          && !existing.IsDeleted
                                          && existing.DeduplicationKey == deduplicationKey,
                ct);

        var previousTriggeredAt = alert?.LastTriggeredAt;
        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = candidate.Symbol;
        alert.Severity = AlertSeverity.Critical;
        alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;
        alert.IsActive = true;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = BuildRetryCapConditionJson(candidate, retryCount, settings, nowUtc);

        await writeContext.SaveChangesAsync(ct);

        if (IsWithinCooldown(previousTriggeredAt, nowUtc, settings.AlertCooldown))
            return false;

        if (dispatcher is null)
            return false;

        var lastTriggeredBeforeDispatch = alert.LastTriggeredAt;
        var message =
            $"ML dead-letter retry cap reached for {candidate.Symbol}/{candidate.Timeframe}: run {candidate.Id} remains failed after {retryCount}/{settings.MaxRetries} recovery attempt(s). Destination={settings.AlertDestination}. Manual intervention required.";

        try
        {
            await dispatcher.DispatchAsync(alert, message, ct);
            await writeContext.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "{Worker}: failed to dispatch retry-cap alert {DeduplicationKey}.",
                WorkerName,
                deduplicationKey);
            return false;
        }

        var dispatched = alert.LastTriggeredAt.HasValue
                         && alert.LastTriggeredAt != lastTriggeredBeforeDispatch;
        if (dispatched)
        {
            _metrics?.MLDeadLetterAlertsDispatched.Add(
                1,
                Tag("symbol", candidate.Symbol),
                Tag("timeframe", candidate.Timeframe));
        }

        return dispatched;
    }

    private async Task<RecoveredAlertsResult> ResolveRecoveredRetryCapAlertsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDeadLetterWorkerSettings settings,
        CancellationToken ct)
    {
        var alerts = await db.Set<Alert>()
            .Where(alert => alert.AlertType == AlertType.MLModelDegraded
                         && alert.IsActive
                         && !alert.IsDeleted
                         && alert.DeduplicationKey != null
                         && alert.DeduplicationKey.StartsWith(RetryCapAlertPrefix))
            .Take(settings.MaxRunsPerCycle)
            .ToListAsync(ct);

        var alertsResolved = 0;
        var countersReset = 0;

        foreach (var alert in alerts)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryParseRetryCapAlertKey(alert.DeduplicationKey!, out var symbol, out var timeframe))
                continue;

            var referenceTime = alert.LastTriggeredAt.HasValue
                ? NormalizeUtc(alert.LastTriggeredAt.Value)
                : DateTime.MinValue;

            if (!await HasCompletedSuccessSinceAsync(db, symbol, timeframe, referenceTime, ct))
                continue;

            if (await ResetRetryCounterAsync(db, symbol, timeframe, ct))
                countersReset++;

            if (await ResolveRetryCapAlertAsync(writeContext, db, dispatcher, settings, symbol, timeframe, ct))
                alertsResolved++;
        }

        return new RecoveredAlertsResult(alertsResolved, countersReset);
    }

    private async Task<bool> ResolveRetryCapAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDeadLetterWorkerSettings settings,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var deduplicationKey = BuildRetryCapAlertKey(symbol, timeframe);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(existing => existing.AlertType == AlertType.MLModelDegraded
                                          && existing.IsActive
                                          && !existing.IsDeleted
                                          && existing.DeduplicationKey == deduplicationKey,
                ct);

        if (alert is null)
            return false;

        alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;

        if (dispatcher is not null)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to dispatch retry-cap resolution for {DeduplicationKey}.",
                    WorkerName,
                    deduplicationKey);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= _timeProvider.GetUtcNow().UtcDateTime;
        await writeContext.SaveChangesAsync(ct);
        _metrics?.MLDeadLetterAlertsResolved.Add(1, Tag("symbol", symbol), Tag("timeframe", timeframe));
        return true;
    }

    private async Task<bool> ResetRetryCounterAsync(
        DbContext db,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var key = BuildRetryCountKey(symbol, timeframe);
        var current = await GetRetryCountAsync(db, symbol, timeframe, ct);
        if (current <= 0)
            return false;

        await EngineConfigUpsert.UpsertAsync(
            db,
            key,
            "0",
            ConfigDataType.Int,
            $"Dead-letter recovery retry counter for {symbol}/{timeframe}.",
            isHotReloadable: true,
            ct: ct);

        _metrics?.MLDeadLetterRetryCountersReset.Add(1, Tag("symbol", symbol), Tag("timeframe", timeframe));
        return true;
    }

    private async Task<LoadCandidatesResult> LoadEligibleCandidatesAsync(
        DbContext db,
        MLDeadLetterWorkerSettings settings,
        DateTime cutoff,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var rows = await db.Set<MLTrainingRun>()
            .Where(run => run.Status == RunStatus.Failed
                       && run.CompletedAt != null
                       && run.CompletedAt < cutoff
                       && !run.IsDeleted)
            .OrderBy(run => run.Symbol)
            .ThenBy(run => run.Timeframe)
            .ThenByDescending(run => run.CompletedAt)
            .Take(settings.MaxRunsPerCycle + 1)
            .AsNoTracking()
            .Select(run => new DeadLetterRunCandidate(
                run.Id,
                run.Symbol,
                run.Timeframe,
                run.CompletedAt!.Value,
                run.ErrorMessage))
            .ToListAsync(ct);

        var truncated = rows.Count > settings.MaxRunsPerCycle;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        var candidates = new List<DeadLetterRunCandidate>(rows.Count);
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        var invalidSkipped = 0;
        var duplicateSkipped = 0;

        foreach (var row in rows)
        {
            var symbol = NormalizeSymbol(row.Symbol);
            if (symbol.Length == 0 || symbol.Length > 10)
            {
                invalidSkipped++;
                continue;
            }

            var key = PairKey(symbol, row.Timeframe);
            if (!seenPairs.Add(key))
            {
                duplicateSkipped++;
                continue;
            }

            candidates.Add(row with { Symbol = symbol });
        }

        return new LoadCandidatesResult(candidates, invalidSkipped, duplicateSkipped, truncated);
    }

    private async Task<MLDeadLetterWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLDeadLetterWorkerSettings defaults,
        CancellationToken ct)
    {
        var keys = new[]
        {
            CK_Enabled,
            CK_PollSecs,
            CK_PollJitterSecs,
            CK_RetryDays,
            CK_MaxRetries,
            CK_MaxRunsPerCycle,
            CK_MaxRequeuesPerCycle,
            CK_LockTimeout,
            CK_AlertCooldown,
            CK_AlertDest
        };

        var config = await db.Set<EngineConfig>()
            .Where(entry => keys.Contains(entry.Key) && !entry.IsDeleted)
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Value, ct);

        var maxRunsPerCycle = GetInt(config, CK_MaxRunsPerCycle, defaults.MaxRunsPerCycle, 1, 10_000);
        var maxRequeuesPerCycle = Math.Min(
            GetInt(config, CK_MaxRequeuesPerCycle, defaults.MaxRequeuesPerCycle, 0, 10_000),
            maxRunsPerCycle);

        return defaults with
        {
            Enabled = GetBool(config, CK_Enabled, defaults.Enabled),
            PollInterval = TimeSpan.FromSeconds(GetInt(config, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, 60, 2_592_000)),
            PollJitter = TimeSpan.FromSeconds(GetInt(config, CK_PollJitterSecs, (int)defaults.PollJitter.TotalSeconds, 0, 86_400)),
            RetryAfter = TimeSpan.FromDays(GetInt(config, CK_RetryDays, (int)defaults.RetryAfter.TotalDays, 1, 3_650)),
            MaxRetries = GetInt(config, CK_MaxRetries, defaults.MaxRetries, 0, 100),
            MaxRunsPerCycle = maxRunsPerCycle,
            MaxRequeuesPerCycle = maxRequeuesPerCycle,
            LockTimeout = TimeSpan.FromSeconds(GetInt(config, CK_LockTimeout, (int)defaults.LockTimeout.TotalSeconds, 0, 300)),
            AlertCooldown = TimeSpan.FromSeconds(GetInt(config, CK_AlertCooldown, (int)defaults.AlertCooldown.TotalSeconds, 0, 2_592_000)),
            AlertDestination = GetString(config, CK_AlertDest, defaults.AlertDestination, 100)
        };
    }

    private static MLDeadLetterWorkerSettings BuildSettings(MLDeadLetterOptions options)
    {
        var maxRunsPerCycle = Clamp(options.MaxRunsPerCycle, 1, 10_000);
        var maxRequeuesPerCycle = Math.Min(
            Clamp(options.MaxRequeuesPerCycle, 0, 10_000),
            maxRunsPerCycle);

        return new MLDeadLetterWorkerSettings
        {
            Enabled = options.Enabled,
            InitialDelay = TimeSpan.FromSeconds(Clamp(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval = TimeSpan.FromSeconds(Clamp(options.PollIntervalSeconds, 60, 2_592_000)),
            PollJitter = TimeSpan.FromSeconds(Clamp(options.PollJitterSeconds, 0, 86_400)),
            RetryAfter = TimeSpan.FromDays(Clamp(options.RetryAfterDays, 1, 3_650)),
            MaxRetries = Clamp(options.MaxRetries, 0, 100),
            MaxRunsPerCycle = maxRunsPerCycle,
            MaxRequeuesPerCycle = maxRequeuesPerCycle,
            LockTimeout = TimeSpan.FromSeconds(Clamp(options.LockTimeoutSeconds, 0, 300)),
            AlertCooldown = TimeSpan.FromSeconds(Clamp(options.AlertCooldownSeconds, 0, 2_592_000)),
            AlertDestination = NormalizeDestination(options.AlertDestination)
        };
    }

    private async Task<int> GetRetryCountAsync(
        DbContext db,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var key = BuildRetryCountKey(symbol, timeframe);
        var value = await db.Set<EngineConfig>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(config => config.Key == key && !config.IsDeleted)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static Task<bool> HasCompletedSuccessSinceAsync(
        DbContext db,
        string symbol,
        Timeframe timeframe,
        DateTime completedAfterUtc,
        CancellationToken ct)
        => db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run => run.Symbol != null
                          && run.Symbol.ToUpper() == symbol
                          && run.Timeframe == timeframe
                          && run.Status == RunStatus.Completed
                          && run.CompletedAt != null
                          && run.CompletedAt > completedAfterUtc
                          && !run.IsDeleted,
                ct);

    private static string BuildRetryCapConditionJson(
        DeadLetterRunCandidate candidate,
        int retryCount,
        MLDeadLetterWorkerSettings settings,
        DateTime nowUtc)
    {
        var payload = new
        {
            reason = "dead_letter_retry_cap_exceeded",
            worker = WorkerName,
            severity = AlertSeverity.Critical.ToString(),
            destination = settings.AlertDestination,
            symbol = candidate.Symbol,
            timeframe = candidate.Timeframe.ToString(),
            runId = candidate.Id,
            failedAt = candidate.CompletedAt,
            detectedAt = nowUtc,
            deadLetterRetries = retryCount,
            maxRetries = settings.MaxRetries,
            lastError = Truncate(candidate.ErrorMessage ?? "unknown", 500)
        };

        return Truncate(JsonSerializer.Serialize(payload, JsonOptions), AlertConditionMaxLength);
    }

    private static string AppendRetryNote(
        string? errorMessage,
        DateTime nowUtc,
        int retry,
        int maxRetries)
    {
        var note = $"[DeadLetter retry {retry}/{maxRetries} at {nowUtc:O}]";
        var updated = string.IsNullOrWhiteSpace(errorMessage)
            ? note
            : $"{errorMessage} {note}";

        return Truncate(updated, MaxErrorMessageLength);
    }

    private static bool TryParseRetryCapAlertKey(
        string deduplicationKey,
        out string symbol,
        out Timeframe timeframe)
    {
        symbol = string.Empty;
        timeframe = default;

        if (!deduplicationKey.StartsWith(RetryCapAlertPrefix, StringComparison.Ordinal))
            return false;

        var rest = deduplicationKey[RetryCapAlertPrefix.Length..];
        var parts = rest.Split(':');
        if (parts.Length != 2)
            return false;

        symbol = NormalizeSymbol(parts[0]);
        return symbol.Length > 0
               && Enum.TryParse(parts[1], ignoreCase: false, out timeframe);
    }

    private static bool IsWithinCooldown(DateTime? lastTriggeredAt, DateTime nowUtc, TimeSpan cooldown)
    {
        if (!lastTriggeredAt.HasValue || cooldown <= TimeSpan.Zero)
            return false;

        return nowUtc - NormalizeUtc(lastTriggeredAt.Value) < cooldown;
    }

    private static string BuildRetryCountKey(string symbol, Timeframe timeframe)
        => $"MLDeadLetter:{symbol}:{timeframe}:RetryCount";

    private static string BuildRetryCapAlertKey(string symbol, Timeframe timeframe)
        => $"{RetryCapAlertPrefix}{symbol}:{timeframe}";

    private static string PairKey(string symbol, Timeframe timeframe)
        => $"{symbol}:{timeframe}";

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();

    private static string NormalizeDestination(string? destination)
    {
        var value = string.IsNullOrWhiteSpace(destination)
            ? "ml-ops"
            : destination.Trim();

        return value.Length > 100 ? value[..100] : value;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private static TimeSpan GetIntervalWithJitter(MLDeadLetterWorkerSettings settings)
    {
        if (settings.PollJitter <= TimeSpan.Zero)
            return settings.PollInterval;

        var jitterMs = Random.Shared.NextDouble() * settings.PollJitter.TotalMilliseconds;
        return settings.PollInterval + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static TimeSpan CalculateDelay(TimeSpan baseDelay, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseDelay;

        var multiplier = Math.Min(8, 1 << Math.Min(consecutiveFailures, 3));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier);
    }

    private void RecordRunSkipped(string reason)
        => _metrics?.MLDeadLetterRunsSkipped.Add(1, Tag("reason", reason));

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLDeadLetterCyclesSkipped.Add(1, Tag("reason", reason));

    private static bool GetBool(
        IReadOnlyDictionary<string, string> config,
        string key,
        bool defaultValue)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    private static int GetInt(
        IReadOnlyDictionary<string, string> config,
        string key,
        int defaultValue,
        int min,
        int max)
    {
        if (!config.TryGetValue(key, out var value)
            || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return Clamp(parsed, min, max);
    }

    private static string GetString(
        IReadOnlyDictionary<string, string> config,
        string key,
        string defaultValue,
        int maxLength)
    {
        if (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private sealed record DeadLetterRunCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        DateTime CompletedAt,
        string? ErrorMessage);

    private sealed record LoadCandidatesResult(
        IReadOnlyList<DeadLetterRunCandidate> Candidates,
        int InvalidCandidatesSkipped,
        int DuplicatePairCandidatesSkipped,
        bool Truncated);

    private sealed record RunProcessResult(
        string? SkippedReason,
        bool Requeued,
        bool RetryCapReached,
        bool AlertDispatched,
        bool AlertResolved,
        bool CounterReset)
    {
        public static RunProcessResult Skipped(string reason)
            => new(reason, false, false, false, false, false);
    }

    private sealed record RecoveredAlertsResult(int AlertsResolved, int CountersReset);
}

internal sealed record MLDeadLetterWorkerSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan PollJitter { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromDays(7);
    public int MaxRetries { get; init; } = 3;
    public int MaxRunsPerCycle { get; init; } = 1_000;
    public int MaxRequeuesPerCycle { get; init; } = 100;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AlertCooldown { get; init; } = TimeSpan.FromDays(1);
    public string AlertDestination { get; init; } = "ml-ops";
}

internal sealed record MLDeadLetterCycleResult(
    MLDeadLetterWorkerSettings Settings,
    string? SkippedReason,
    int CandidatesScanned,
    int RunsSkipped,
    int RunsRequeued,
    int RetryCapsReached,
    int AlertsDispatched,
    int AlertsResolved,
    int RetryCountersReset,
    bool Truncated)
{
    public static MLDeadLetterCycleResult Skipped(
        MLDeadLetterWorkerSettings settings,
        string reason)
        => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, false);
}
