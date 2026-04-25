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
/// Detects active ML models whose most recent predictions have collapsed into a
/// one-sided direction streak.
/// </summary>
public sealed class MLDirectionStreakWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLDirectionStreakWorker);

    private const string DistributedLockKey = "ml:direction-streak:cycle";
    private const string AlertDeduplicationPrefix = "ml-direction-streak:";
    private const int AlertConditionMaxLength = 1_500;

    private const string CK_Enabled = "MLStreak:Enabled";
    private const string CK_PollSecs = "MLStreak:PollIntervalSeconds";
    private const string CK_PollJitterSecs = "MLStreak:PollJitterSeconds";
    private const string CK_Window = "MLStreak:WindowSize";
    private const string CK_MaxFrac = "MLStreak:MaxSameDirectionFraction";
    private const string CK_EntropyThreshold = "MLStreak:EntropyThreshold";
    private const string CK_RunsZThreshold = "MLStreak:RunsZScoreThreshold";
    private const string CK_LongestRunFraction = "MLStreak:LongestRunFraction";
    private const string CK_MinFailedTestsToAlert = "MLStreak:MinFailedTestsToAlert";
    private const string CK_MinFailedTestsToRetrain = "MLStreak:MinFailedTestsToRetrain";
    private const string CK_AutoQueueRetrain = "MLStreak:AutoQueueRetrain";
    private const string CK_RetrainLookbackDays = "MLStreak:RetrainLookbackDays";
    private const string CK_MaxModelsPerCycle = "MLStreak:MaxModelsPerCycle";
    private const string CK_MaxRetrainsPerCycle = "MLStreak:MaxRetrainsPerCycle";
    private const string CK_AlertCooldown = "MLStreak:AlertCooldownSeconds";
    private const string CK_LockTimeout = "MLStreak:LockTimeoutSeconds";
    private const string CK_AlertDest = "MLStreak:AlertDestination";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDirectionStreakWorker> _logger;
    private readonly MLDirectionStreakOptions _options;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private bool _missingAlertDispatcherWarningEmitted;

    public MLDirectionStreakWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDirectionStreakWorker> logger,
        MLDirectionStreakOptions? options = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDirectionStreakOptions();
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
            "Detects active ML models whose recent predictions have collapsed into a one-sided direction streak.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.ModelsEvaluated);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(durationMs, Tag("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                    }
                    else if (result.StreaksDetected > 0 || result.AlertsResolved > 0 || result.RetrainsQueued > 0)
                    {
                        _logger.LogInformation(
                            "{Worker}: evaluated={Evaluated}, skipped={Skipped}, detected={Detected}, severe={Severe}, alertsDispatched={AlertsDispatched}, alertsResolved={AlertsResolved}, retrainsQueued={RetrainsQueued}.",
                            WorkerName,
                            result.ModelsEvaluated,
                            result.ModelsSkipped,
                            result.StreaksDetected,
                            result.SevereStreaks,
                            result.AlertsDispatched,
                            result.AlertsResolved,
                            result.RetrainsQueued);
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

    internal async Task<MLDirectionStreakCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = BuildSettings(_options);

        try
        {
            if (!settings.Enabled)
            {
                RecordCycleSkipped("disabled");
                return MLDirectionStreakCycleResult.Skipped(settings, "disabled");
            }

            IAsyncDisposable? cycleLock = null;
            if (_distributedLock is null)
            {
                _metrics?.MLDirectionStreakLockAttempts.Add(1, Tag("outcome", "unavailable"));
                if (!_missingDistributedLockWarningEmitted)
                {
                    _logger.LogWarning(
                        "{Worker} running without IDistributedLock; duplicate direction-streak alerting/retraining is possible in multi-instance deployments.",
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
                    _metrics?.MLDirectionStreakLockAttempts.Add(1, Tag("outcome", "busy"));
                    RecordCycleSkipped("lock_busy");
                    return MLDirectionStreakCycleResult.Skipped(settings, "lock_busy");
                }

                _metrics?.MLDirectionStreakLockAttempts.Add(1, Tag("outcome", "acquired"));
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
                            "{Worker} could not resolve IAlertDispatcher; direction-streak alerts will be persisted but not notified.",
                            WorkerName);
                        _missingAlertDispatcherWarningEmitted = true;
                    }

                    var runtimeSettings = await LoadRuntimeSettingsAsync(db, settings, ct);
                    if (!runtimeSettings.Enabled)
                    {
                        RecordCycleSkipped("disabled");
                        return MLDirectionStreakCycleResult.Skipped(runtimeSettings, "disabled");
                    }

                    return await CheckStreaksAsync(writeContext, db, dispatcher, runtimeSettings, ct);
                }
                finally
                {
                    WorkerBulkhead.MLMonitoring.Release();
                }
            }
        }
        finally
        {
            _metrics?.MLDirectionStreakCycleDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task<MLDirectionStreakCycleResult> CheckStreaksAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDirectionStreakWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var modelLoad = await LoadActiveModelsAsync(db, settings, ct);

        var modelsEvaluated = 0;
        var modelsSkipped = modelLoad.ModelsSkipped;
        var streaksDetected = 0;
        var severeStreaks = 0;
        var alertsDispatched = 0;
        var alertsSuppressedByCooldown = 0;
        var retrainsQueued = 0;
        var alertsResolved = 0;

        for (var i = 0; i < modelLoad.ModelsSkipped; i++)
            RecordModelSkipped("model_not_eligible");
        if (modelLoad.Truncated)
            RecordModelSkipped("max_models_truncated");

        foreach (var model in modelLoad.ModelsToEvaluate)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var canQueueRetrain = retrainsQueued < settings.MaxRetrainsPerCycle;
                var result = await CheckModelStreakAsync(
                    writeContext,
                    db,
                    dispatcher,
                    settings,
                    model,
                    canQueueRetrain,
                    nowUtc,
                    ct);

                modelsEvaluated++;
                _metrics?.MLDirectionStreakModelsEvaluated.Add(
                    1,
                    Tag("symbol", model.Symbol),
                    Tag("timeframe", model.Timeframe));

                if (result.SkippedReason is { Length: > 0 })
                {
                    modelsSkipped++;
                    RecordModelSkipped(result.SkippedReason);
                }

                if (result.StreakDetected)
                {
                    streaksDetected++;
                    _metrics?.MLDirectionStreakDetections.Add(
                        1,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe));
                }

                if (result.SevereStreak)
                {
                    severeStreaks++;
                    _metrics?.MLDirectionStreakSevereDetections.Add(
                        1,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe));
                }

                if (result.AlertDispatched)
                    alertsDispatched++;
                if (result.AlertSuppressedByCooldown)
                    alertsSuppressedByCooldown++;
                if (result.RetrainQueued)
                    retrainsQueued++;
                if (result.AlertResolved)
                    alertsResolved++;

                if (result.Diagnostics is not null)
                {
                    _metrics?.MLDirectionStreakDominantFraction.Record(
                        result.Diagnostics.DominantFraction,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe));
                    _metrics?.MLDirectionStreakEntropy.Record(
                        result.Diagnostics.Entropy,
                        Tag("symbol", model.Symbol),
                        Tag("timeframe", model.Timeframe));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                modelsSkipped++;
                RecordModelSkipped("model_error");
                _logger.LogWarning(
                    ex,
                    "{Worker}: failed to process model {ModelId} ({Symbol}/{Timeframe}); skipping.",
                    WorkerName,
                    model.Id,
                    model.Symbol,
                    model.Timeframe);
            }
        }

        var staleResolved = await ResolveStaleAlertsAsync(
            writeContext,
            db,
            dispatcher,
            modelLoad.ActiveAlertKeys,
            nowUtc,
            ct);
        alertsResolved += staleResolved;

        if (modelsEvaluated == 0 && modelLoad.ActiveAlertKeys.Count == 0)
        {
            RecordCycleSkipped("no_active_models");
            return new MLDirectionStreakCycleResult(
                settings,
                "no_active_models",
                0,
                modelsSkipped,
                0,
                0,
                0,
                0,
                0,
                alertsResolved,
                modelLoad.Truncated);
        }

        return new MLDirectionStreakCycleResult(
            settings,
            null,
            modelsEvaluated,
            modelsSkipped,
            streaksDetected,
            severeStreaks,
            alertsDispatched,
            alertsSuppressedByCooldown,
            retrainsQueued,
            alertsResolved,
            modelLoad.Truncated);
    }

    private async Task<ModelStreakProcessResult> CheckModelStreakAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        MLDirectionStreakWorkerSettings settings,
        ModelSnapshot model,
        bool canQueueRetrain,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var recentDirections = await db.Set<MLModelPredictionLog>()
            .Where(log => log.MLModelId == model.Id && !log.IsDeleted)
            .OrderByDescending(log => log.PredictedAt)
            .ThenByDescending(log => log.Id)
            .Take(settings.WindowSize)
            .AsNoTracking()
            .Select(log => log.PredictedDirection)
            .ToListAsync(ct);

        if (recentDirections.Count < settings.WindowSize)
        {
            var resolved = await ResolveModelAlertAsync(
                writeContext,
                db,
                dispatcher,
                model,
                nowUtc,
                ct);

            return new ModelStreakProcessResult(
                SkippedReason: "insufficient_predictions",
                StreakDetected: false,
                SevereStreak: false,
                AlertDispatched: false,
                AlertSuppressedByCooldown: false,
                AlertResolved: resolved,
                RetrainQueued: false,
                Diagnostics: null);
        }

        var diagnostics = CalculateDiagnostics(recentDirections, settings);
        var failCount = diagnostics.FailedTestCount(settings);
        if (failCount < settings.MinFailedTestsToAlert)
        {
            var resolved = await ResolveModelAlertAsync(
                writeContext,
                db,
                dispatcher,
                model,
                nowUtc,
                ct);

            return new ModelStreakProcessResult(
                SkippedReason: null,
                StreakDetected: false,
                SevereStreak: false,
                AlertDispatched: false,
                AlertSuppressedByCooldown: false,
                AlertResolved: resolved,
                RetrainQueued: false,
                diagnostics);
        }

        var severe = failCount >= settings.MinFailedTestsToRetrain;
        var alertResult = await UpsertAndDispatchAlertAsync(
            writeContext,
            db,
            dispatcher,
            model,
            diagnostics,
            severe,
            settings,
            nowUtc,
            ct);

        var retrainQueued = false;
        if (severe && settings.AutoQueueRetrain)
        {
            if (canQueueRetrain)
            {
                retrainQueued = await QueueRetrainIfNeededAsync(
                    db,
                    model,
                    diagnostics,
                    failCount,
                    settings,
                    nowUtc,
                    ct);
            }
            else
            {
                RecordModelSkipped("retrain_budget_exhausted");
            }
        }

        await writeContext.SaveChangesAsync(ct);

        _logger.LogWarning(
            "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) direction streak detected. failedTests={FailedTests}, severe={Severe}, dominant={DominantDirection} {DominantFraction:P1}, entropy={Entropy:F3}, runsZ={RunsZ:F2}, longestRun={LongestRun}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            failCount,
            severe,
            diagnostics.DominantDirection,
            diagnostics.DominantFraction,
            diagnostics.Entropy,
            diagnostics.RunsZScore,
            diagnostics.LongestRun);

        return new ModelStreakProcessResult(
            SkippedReason: null,
            StreakDetected: true,
            SevereStreak: severe,
            AlertDispatched: alertResult.Dispatched,
            AlertSuppressedByCooldown: alertResult.SuppressedByCooldown,
            AlertResolved: false,
            RetrainQueued: retrainQueued,
            diagnostics);
    }

    private async Task<AlertDispatchResult> UpsertAndDispatchAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        ModelSnapshot model,
        DirectionStreakDiagnostics diagnostics,
        bool severe,
        MLDirectionStreakWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var deduplicationKey = BuildAlertKey(model);
        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert => alert.DeduplicationKey == deduplicationKey)
            .OrderByDescending(alert => alert.Id)
            .ToListAsync(ct);

        var alert = alerts.FirstOrDefault(candidate => !candidate.IsDeleted);
        var previousTriggeredAt = alert?.LastTriggeredAt;
        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = deduplicationKey
            };
            db.Set<Alert>().Add(alert);
        }

        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = model.Symbol;
        alert.Severity = severe ? AlertSeverity.High : AlertSeverity.Medium;
        alert.CooldownSeconds = (int)settings.AlertCooldown.TotalSeconds;
        alert.ConditionJson = BuildAlertConditionJson(model, diagnostics, severe, settings, nowUtc);
        alert.IsActive = true;
        alert.AutoResolvedAt = null;
        alert.IsDeleted = false;

        foreach (var duplicate in alerts.Where(candidate => candidate.Id != alert.Id && !candidate.IsDeleted))
        {
            duplicate.IsActive = false;
            duplicate.AutoResolvedAt ??= nowUtc;
        }

        await writeContext.SaveChangesAsync(ct);

        if (IsWithinCooldown(previousTriggeredAt, nowUtc, settings.AlertCooldown))
            return new AlertDispatchResult(false, true);

        if (dispatcher is null)
            return new AlertDispatchResult(false, false);

        var lastTriggeredBeforeDispatch = alert.LastTriggeredAt;
        try
        {
            await dispatcher.DispatchAsync(alert, BuildAlertMessage(model, diagnostics, severe, settings), ct);
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
                "{Worker}: failed to dispatch direction-streak alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return new AlertDispatchResult(false, false);
        }

        var dispatched = alert.LastTriggeredAt.HasValue
                         && alert.LastTriggeredAt != lastTriggeredBeforeDispatch;
        if (dispatched)
        {
            _metrics?.MLDirectionStreakAlertsDispatched.Add(
                1,
                Tag("symbol", model.Symbol),
                Tag("timeframe", model.Timeframe),
                Tag("severity", alert.Severity.ToString()));
        }

        return new AlertDispatchResult(dispatched, false);
    }

    private async Task<bool> QueueRetrainIfNeededAsync(
        DbContext db,
        ModelSnapshot model,
        DirectionStreakDiagnostics diagnostics,
        int failCount,
        MLDirectionStreakWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var retrainExists = await db.Set<MLTrainingRun>()
            .AnyAsync(run => run.Symbol == model.Symbol
                          && run.Timeframe == model.Timeframe
                          && (run.Status == RunStatus.Queued || run.Status == RunStatus.Running)
                          && !run.IsDeleted,
                ct);

        if (retrainExists)
            return false;

        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Status = RunStatus.Queued,
            TriggerType = TriggerType.AutoDegrading,
            FromDate = nowUtc.AddDays(-settings.RetrainLookbackDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            ErrorMessage = $"[DirectionStreak] Auto-retrain: {failCount}/4 tests failed " +
                           $"(dominant={diagnostics.DominantDirection} {diagnostics.DominantFraction:P0}, " +
                           $"entropy={diagnostics.Entropy:F3}, runsZ={diagnostics.RunsZScore:F2}, " +
                           $"longestRun={diagnostics.LongestRun}). Recommend class rebalancing and regularisation.",
            HyperparamConfigJson = JsonSerializer.Serialize(new
            {
                triggeredBy = WorkerName,
                reason = "direction_streak",
                classRebalance = true,
                sourceModelId = model.Id,
                model.Symbol,
                timeframe = model.Timeframe.ToString(),
                dominantDirection = diagnostics.DominantDirection.ToString(),
                diagnostics.DominantFraction,
                diagnostics.Entropy,
                diagnostics.RunsZScore,
                diagnostics.LongestRun,
                failCount,
                settings.WindowSize
            }, JsonOptions)
        });

        _metrics?.MLDirectionStreakRetrainsQueued.Add(
            1,
            Tag("symbol", model.Symbol),
            Tag("timeframe", model.Timeframe));

        return true;
    }

    private async Task<bool> ResolveModelAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        ModelSnapshot model,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => candidate.AlertType == AlertType.MLModelDegraded
                                           && candidate.DeduplicationKey == BuildAlertKey(model)
                                           && candidate.IsActive
                                           && !candidate.IsDeleted,
                ct);

        if (alert is null)
            return false;

        await ResolveAlertAsync(writeContext, dispatcher, alert, nowUtc, ct);
        _metrics?.MLDirectionStreakAlertsResolved.Add(1, Tag("symbol", model.Symbol), Tag("timeframe", model.Timeframe));
        return true;
    }

    private async Task<int> ResolveStaleAlertsAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        IAlertDispatcher? dispatcher,
        IReadOnlySet<string> activeAlertKeys,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alerts = await db.Set<Alert>()
            .Where(alert => alert.AlertType == AlertType.MLModelDegraded
                         && alert.DeduplicationKey != null
                         && alert.DeduplicationKey.StartsWith(AlertDeduplicationPrefix)
                         && alert.IsActive
                         && !alert.IsDeleted)
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
        {
            if (alert.DeduplicationKey is not null && activeAlertKeys.Contains(alert.DeduplicationKey))
                continue;

            await ResolveAlertAsync(writeContext, dispatcher, alert, nowUtc, ct);
            resolved++;
        }

        if (resolved > 0)
            _metrics?.MLDirectionStreakAlertsResolved.Add(resolved, Tag("scope", "stale"));

        return resolved;
    }

    private async Task ResolveAlertAsync(
        IWriteApplicationDbContext writeContext,
        IAlertDispatcher? dispatcher,
        Alert alert,
        DateTime nowUtc,
        CancellationToken ct)
    {
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
                    "{Worker}: failed to dispatch direction-streak recovery for {DeduplicationKey}.",
                    WorkerName,
                    alert.DeduplicationKey);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
    }

    private async Task<LoadModelsResult> LoadActiveModelsAsync(
        DbContext db,
        MLDirectionStreakWorkerSettings settings,
        CancellationToken ct)
    {
        var rows = await db.Set<MLModel>()
            .Where(model => model.IsActive
                         && !model.IsSuppressed
                         && !model.IsDeleted
                         && model.Status == MLModelStatus.Active
                         && model.ModelBytes != null)
            .AsNoTracking()
            .Select(model => new ModelSnapshot(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.ModelBytes != null && model.ModelBytes.Length > 0))
            .ToListAsync(ct);

        var skipped = 0;
        var eligible = rows
            .Select(row => row with { Symbol = NormalizeSymbol(row.Symbol) })
            .Where(row =>
            {
                var valid = IsValidSymbol(row.Symbol) && row.HasModelBytes;
                if (!valid)
                    skipped++;
                return valid;
            })
            .OrderBy(row => row.Symbol, StringComparer.Ordinal)
            .ThenBy(row => row.Timeframe)
            .ThenBy(row => row.Id)
            .ToList();

        var activeAlertKeys = eligible
            .Select(BuildAlertKey)
            .ToHashSet(StringComparer.Ordinal);

        var truncated = eligible.Count > settings.MaxModelsPerCycle;
        if (truncated)
            eligible = eligible.Take(settings.MaxModelsPerCycle).ToList();

        return new LoadModelsResult(eligible, activeAlertKeys, skipped, truncated);
    }

    private async Task<MLDirectionStreakWorkerSettings> LoadRuntimeSettingsAsync(
        DbContext db,
        MLDirectionStreakWorkerSettings defaults,
        CancellationToken ct)
    {
        var keys = new[]
        {
            CK_Enabled,
            CK_PollSecs,
            CK_PollJitterSecs,
            CK_Window,
            CK_MaxFrac,
            CK_EntropyThreshold,
            CK_RunsZThreshold,
            CK_LongestRunFraction,
            CK_MinFailedTestsToAlert,
            CK_MinFailedTestsToRetrain,
            CK_AutoQueueRetrain,
            CK_RetrainLookbackDays,
            CK_MaxModelsPerCycle,
            CK_MaxRetrainsPerCycle,
            CK_AlertCooldown,
            CK_LockTimeout,
            CK_AlertDest
        };

        var config = await db.Set<EngineConfig>()
            .Where(entry => keys.Contains(entry.Key) && !entry.IsDeleted)
            .AsNoTracking()
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Value, ct);

        var minFailedToAlert = GetInt(config, CK_MinFailedTestsToAlert, defaults.MinFailedTestsToAlert, 1, 4);
        var minFailedToRetrain = GetInt(config, CK_MinFailedTestsToRetrain, defaults.MinFailedTestsToRetrain, minFailedToAlert, 4);

        return defaults with
        {
            Enabled = GetBool(config, CK_Enabled, defaults.Enabled),
            PollInterval = TimeSpan.FromSeconds(GetInt(config, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds, 30, 86_400)),
            PollJitter = TimeSpan.FromSeconds(GetInt(config, CK_PollJitterSecs, (int)defaults.PollJitter.TotalSeconds, 0, 86_400)),
            WindowSize = GetInt(config, CK_Window, defaults.WindowSize, 10, 500),
            MaxSameDirectionFraction = GetDouble(config, CK_MaxFrac, defaults.MaxSameDirectionFraction, 0.55, 0.99),
            EntropyThreshold = GetDouble(config, CK_EntropyThreshold, defaults.EntropyThreshold, 0.0, 1.0),
            RunsZScoreThreshold = GetDouble(config, CK_RunsZThreshold, defaults.RunsZScoreThreshold, -10.0, 0.0),
            LongestRunFraction = GetDouble(config, CK_LongestRunFraction, defaults.LongestRunFraction, 0.10, 1.0),
            MinFailedTestsToAlert = minFailedToAlert,
            MinFailedTestsToRetrain = minFailedToRetrain,
            AutoQueueRetrain = GetBool(config, CK_AutoQueueRetrain, defaults.AutoQueueRetrain),
            RetrainLookbackDays = GetInt(config, CK_RetrainLookbackDays, defaults.RetrainLookbackDays, 30, 3_650),
            MaxModelsPerCycle = GetInt(config, CK_MaxModelsPerCycle, defaults.MaxModelsPerCycle, 1, 100_000),
            MaxRetrainsPerCycle = GetInt(config, CK_MaxRetrainsPerCycle, defaults.MaxRetrainsPerCycle, 0, 1_000),
            AlertCooldown = TimeSpan.FromSeconds(GetInt(config, CK_AlertCooldown, (int)defaults.AlertCooldown.TotalSeconds, 0, 2_592_000)),
            LockTimeout = TimeSpan.FromSeconds(GetInt(config, CK_LockTimeout, (int)defaults.LockTimeout.TotalSeconds, 0, 300)),
            AlertDestination = GetString(config, CK_AlertDest, defaults.AlertDestination, 100)
        };
    }

    private static MLDirectionStreakWorkerSettings BuildSettings(MLDirectionStreakOptions options)
    {
        var minFailedToAlert = Clamp(options.MinFailedTestsToAlert, 1, 4);
        var minFailedToRetrain = Clamp(options.MinFailedTestsToRetrain, minFailedToAlert, 4);

        return new MLDirectionStreakWorkerSettings
        {
            Enabled = options.Enabled,
            InitialDelay = TimeSpan.FromSeconds(Clamp(options.InitialDelaySeconds, 0, 86_400)),
            PollInterval = TimeSpan.FromSeconds(Clamp(options.PollIntervalSeconds, 30, 86_400)),
            PollJitter = TimeSpan.FromSeconds(Clamp(options.PollJitterSeconds, 0, 86_400)),
            WindowSize = Clamp(options.WindowSize, 10, 500),
            MaxSameDirectionFraction = Clamp(options.MaxSameDirectionFraction, 0.55, 0.99),
            EntropyThreshold = Clamp(options.EntropyThreshold, 0.0, 1.0),
            RunsZScoreThreshold = Clamp(options.RunsZScoreThreshold, -10.0, 0.0),
            LongestRunFraction = Clamp(options.LongestRunFraction, 0.10, 1.0),
            MinFailedTestsToAlert = minFailedToAlert,
            MinFailedTestsToRetrain = minFailedToRetrain,
            AutoQueueRetrain = options.AutoQueueRetrain,
            RetrainLookbackDays = Clamp(options.RetrainLookbackDays, 30, 3_650),
            MaxModelsPerCycle = Clamp(options.MaxModelsPerCycle, 1, 100_000),
            MaxRetrainsPerCycle = Clamp(options.MaxRetrainsPerCycle, 0, 1_000),
            AlertCooldown = TimeSpan.FromSeconds(Clamp(options.AlertCooldownSeconds, 0, 2_592_000)),
            LockTimeout = TimeSpan.FromSeconds(Clamp(options.LockTimeoutSeconds, 0, 300)),
            AlertDestination = NormalizeDestination(options.AlertDestination, "ml-ops")
        };
    }

    internal static DirectionStreakDiagnostics CalculateDiagnostics(
        IReadOnlyList<TradeDirection> recentDirections,
        MLDirectionStreakWorkerSettings settings)
    {
        var n = recentDirections.Count;
        var buyCount = recentDirections.Count(direction => direction == TradeDirection.Buy);
        var sellCount = n - buyCount;
        var dominantCount = Math.Max(buyCount, sellCount);
        var dominantFraction = n > 0 ? (double)dominantCount / n : 0.0;
        var dominantDirection = buyCount >= sellCount ? TradeDirection.Buy : TradeDirection.Sell;

        var pBuy = n > 0 ? (double)buyCount / n : 0.0;
        var entropy = 0.0;
        if (pBuy > 0.0 && pBuy < 1.0)
            entropy = -(pBuy * Math.Log2(pBuy) + (1.0 - pBuy) * Math.Log2(1.0 - pBuy));

        var runs = n > 0 ? 1 : 0;
        var longestRun = n > 0 ? 1 : 0;
        var currentRun = n > 0 ? 1 : 0;
        for (var i = 1; i < n; i++)
        {
            if (recentDirections[i] == recentDirections[i - 1])
            {
                currentRun++;
            }
            else
            {
                runs++;
                currentRun = 1;
            }

            longestRun = Math.Max(longestRun, currentRun);
        }

        var n1 = (double)buyCount;
        var n2 = (double)sellCount;
        var expectedRuns = n > 0 ? 1.0 + (2.0 * n1 * n2) / (n1 + n2) : 0.0;
        var varRuns = (n1 + n2) > 1.0
            ? (2.0 * n1 * n2 * (2.0 * n1 * n2 - n1 - n2)) /
              ((n1 + n2) * (n1 + n2) * ((n1 + n2) - 1.0))
            : 0.0;
        var runsZScore = varRuns > 0.0 ? (runs - expectedRuns) / Math.Sqrt(varRuns) : 0.0;

        var longestRunFraction = n > 0 ? (double)longestRun / n : 0.0;
        return new DirectionStreakDiagnostics(
            BuyCount: buyCount,
            SellCount: sellCount,
            DominantDirection: dominantDirection,
            DominantFraction: dominantFraction,
            Entropy: entropy,
            Runs: runs,
            ExpectedRuns: expectedRuns,
            RunsZScore: runsZScore,
            LongestRun: longestRun,
            LongestRunFraction: longestRunFraction,
            FractionFailed: dominantFraction >= settings.MaxSameDirectionFraction,
            EntropyFailed: entropy <= settings.EntropyThreshold,
            RunsFailed: runsZScore <= settings.RunsZScoreThreshold,
            LongestRunFailed: longestRunFraction >= settings.LongestRunFraction);
    }

    private static string BuildAlertConditionJson(
        ModelSnapshot model,
        DirectionStreakDiagnostics diagnostics,
        bool severe,
        MLDirectionStreakWorkerSettings settings,
        DateTime nowUtc)
        => Truncate(JsonSerializer.Serialize(new
        {
            reason = "direction_streak",
            severity = severe ? "severe" : "warning",
            destination = settings.AlertDestination,
            worker = WorkerName,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            modelId = model.Id,
            dominantDirection = diagnostics.DominantDirection.ToString(),
            dominantFraction = Math.Round(diagnostics.DominantFraction, 4),
            buyCount = diagnostics.BuyCount,
            sellCount = diagnostics.SellCount,
            entropy = Math.Round(diagnostics.Entropy, 4),
            runsZScore = Math.Round(diagnostics.RunsZScore, 4),
            runs = diagnostics.Runs,
            expectedRuns = Math.Round(diagnostics.ExpectedRuns, 2),
            longestConsecutiveRun = diagnostics.LongestRun,
            longestRunFraction = Math.Round(diagnostics.LongestRunFraction, 4),
            windowSize = settings.WindowSize,
            testsFailedCount = diagnostics.FailedTestCount(settings),
            fractionFailed = diagnostics.FractionFailed,
            entropyFailed = diagnostics.EntropyFailed,
            runsFailed = diagnostics.RunsFailed,
            longestRunFailed = diagnostics.LongestRunFailed,
            detectedAt = NormalizeUtc(nowUtc)
        }, JsonOptions), AlertConditionMaxLength);

    private static string BuildAlertMessage(
        ModelSnapshot model,
        DirectionStreakDiagnostics diagnostics,
        bool severe,
        MLDirectionStreakWorkerSettings settings)
        => $"ML direction streak {(severe ? "severe" : "warning")} for {model.Symbol}/{model.Timeframe} model {model.Id}: " +
           $"{diagnostics.DominantDirection} is {diagnostics.DominantFraction:P1} of the last {settings.WindowSize} predictions. " +
           $"Destination={settings.AlertDestination}.";

    private static string BuildAlertKey(ModelSnapshot model)
        => $"{AlertDeduplicationPrefix}{model.Symbol}:{model.Timeframe}:{model.Id}";

    private static string NormalizeSymbol(string? symbol)
        => string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();

    private static bool IsValidSymbol(string symbol)
        => symbol.Length is > 0 and <= 20;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    private static string NormalizeDestination(string? destination, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(destination)
            ? fallback
            : destination.Trim();

        return value.Length > 100 ? value[..100] : value;
    }

    private static bool IsWithinCooldown(DateTime? lastTriggeredAt, DateTime nowUtc, TimeSpan cooldown)
    {
        if (!lastTriggeredAt.HasValue || cooldown <= TimeSpan.Zero)
            return false;

        return nowUtc - NormalizeUtc(lastTriggeredAt.Value) < cooldown;
    }

    private static TimeSpan GetIntervalWithJitter(MLDirectionStreakWorkerSettings settings)
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

    private void RecordModelSkipped(string reason)
        => _metrics?.MLDirectionStreakModelsSkipped.Add(1, Tag("reason", reason));

    private void RecordCycleSkipped(string reason)
        => _metrics?.MLDirectionStreakCyclesSkipped.Add(1, Tag("reason", reason));

    private static bool GetBool(
        IReadOnlyDictionary<string, string> config,
        string key,
        bool defaultValue)
    {
        if (!config.TryGetValue(key, out var value))
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

    private static double GetDouble(
        IReadOnlyDictionary<string, string> config,
        string key,
        double defaultValue,
        double min,
        double max)
    {
        if (!config.TryGetValue(key, out var value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
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

    private static double Clamp(double value, double min, double max) => Math.Clamp(value, min, max);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private sealed record ModelSnapshot(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        bool HasModelBytes);

    private sealed record LoadModelsResult(
        IReadOnlyList<ModelSnapshot> ModelsToEvaluate,
        IReadOnlySet<string> ActiveAlertKeys,
        int ModelsSkipped,
        bool Truncated);

    private sealed record AlertDispatchResult(bool Dispatched, bool SuppressedByCooldown);

    private sealed record ModelStreakProcessResult(
        string? SkippedReason,
        bool StreakDetected,
        bool SevereStreak,
        bool AlertDispatched,
        bool AlertSuppressedByCooldown,
        bool AlertResolved,
        bool RetrainQueued,
        DirectionStreakDiagnostics? Diagnostics);
}

internal sealed record MLDirectionStreakWorkerSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan PollJitter { get; init; } = TimeSpan.FromMinutes(2);
    public int WindowSize { get; init; } = 30;
    public double MaxSameDirectionFraction { get; init; } = 0.85;
    public double EntropyThreshold { get; init; } = 0.50;
    public double RunsZScoreThreshold { get; init; } = -2.0;
    public double LongestRunFraction { get; init; } = 0.60;
    public int MinFailedTestsToAlert { get; init; } = 2;
    public int MinFailedTestsToRetrain { get; init; } = 3;
    public bool AutoQueueRetrain { get; init; } = true;
    public int RetrainLookbackDays { get; init; } = 365;
    public int MaxModelsPerCycle { get; init; } = 1_000;
    public int MaxRetrainsPerCycle { get; init; } = 25;
    public TimeSpan AlertCooldown { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string AlertDestination { get; init; } = "ml-ops";
}

internal sealed record MLDirectionStreakCycleResult(
    MLDirectionStreakWorkerSettings Settings,
    string? SkippedReason,
    int ModelsEvaluated,
    int ModelsSkipped,
    int StreaksDetected,
    int SevereStreaks,
    int AlertsDispatched,
    int AlertsSuppressedByCooldown,
    int RetrainsQueued,
    int AlertsResolved,
    bool Truncated)
{
    public static MLDirectionStreakCycleResult Skipped(
        MLDirectionStreakWorkerSettings settings,
        string reason)
        => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, false);
}

internal sealed record DirectionStreakDiagnostics(
    int BuyCount,
    int SellCount,
    TradeDirection DominantDirection,
    double DominantFraction,
    double Entropy,
    int Runs,
    double ExpectedRuns,
    double RunsZScore,
    int LongestRun,
    double LongestRunFraction,
    bool FractionFailed,
    bool EntropyFailed,
    bool RunsFailed,
    bool LongestRunFailed)
{
    public int FailedTestCount(MLDirectionStreakWorkerSettings settings)
        => (FractionFailed ? 1 : 0)
           + (EntropyFailed ? 1 : 0)
           + (RunsFailed ? 1 : 0)
           + (LongestRunFailed ? 1 : 0);
}
