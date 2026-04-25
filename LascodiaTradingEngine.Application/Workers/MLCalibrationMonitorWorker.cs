using System.Collections.Concurrent;
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
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors live probability calibration for active production ML models with bootstrap-derived
/// ECE stderr, regression-guard significance gating on the trend signal, per-bin diagnostics,
/// per-regime breakdown, fleet-level dampening, and per-decision audit logging via
/// <see cref="MLCalibrationLog"/>.
/// </summary>
public sealed class MLCalibrationMonitorWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLCalibrationMonitorWorker);

    private const string DistributedLockKey = "workers:ml-calibration-monitor:cycle";
    private const string ModelLockKeyPrefix = "workers:ml-calibration-monitor:model:";
    private const string AlertDeduplicationPrefix = "ml-calibration-monitor:";
    private const string FleetAlertDeduplicationKey = "ml-calibration-monitor-fleet";
    private const string DriftTriggerType = "CalibrationMonitor";
    private const int AlertConditionMaxLength = 1000;
    private const int NumBins = 10;
    private const double SevereThresholdMultiplier = 2.0;
    private const int MaxAuditDiagnosticsLength = 4_000;
    private const int FleetAlertMinModels = 5;

    // Cached once at type-load time. Used to size the per-cycle audit list so it doesn't
    // resize on the first regime that appears past the literal capacity hint. Enum.GetValues
    // allocates a fresh array each call, so we cache the length only.
    private static readonly int RegimeCount = Enum.GetValues<MarketRegimeEnum>().Length;

    private const string CK_Enabled = "MLCalibration:Enabled";
    private const string CK_PollSecs = "MLCalibration:PollIntervalSeconds";
    private const string CK_WindowDays = "MLCalibration:WindowDays";
    private const string CK_MinSamples = "MLCalibration:MinSamples";
    private const string CK_MaxEce = "MLCalibration:MaxEce";
    private const string CK_DegradationDelta = "MLCalibration:DegradationDelta";
    private const string CK_MaxResolvedPerModel = "MLCalibration:MaxResolvedPerModel";
    private const string CK_LockTimeoutSeconds = "MLCalibration:LockTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLCalibration:MinTimeBetweenRetrainsHours";
    private const string CK_TrainingDataWindowDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_ModelLockTimeoutSeconds = "MLCalibration:ModelLockTimeoutSeconds";
    private const string CK_RegressionGuardK = "MLCalibration:RegressionGuardK";
    private const string CK_BootstrapResamples = "MLCalibration:BootstrapResamples";
    private const string CK_FleetDegradationRatio = "MLCalibration:FleetDegradationRatio";
    private const string CK_PerRegimeMinSamples = "MLCalibration:PerRegimeMinSamples";
    private const string CK_PerRegimeMaxSnapshots = "MLCalibration:PerRegimeMaxSnapshots";
    private const string CK_BootstrapCacheStaleHours = "MLCalibration:BootstrapCacheStaleHours";
    private const string CK_RetrainOnBaselineCritical = "MLCalibration:RetrainOnBaselineCritical";
    private const string CK_TimeDecayHalfLifeDays = "MLCalibration:TimeDecayHalfLifeDays";
    private const string CK_MinSamplesForTimeDecay = "MLCalibration:MinSamplesForTimeDecay";
    private const string CK_TrendSmoothingWindow = "MLCalibration:TrendSmoothingWindow";
    private const string CK_StaleSkipAlertThreshold = "MLCalibration:StaleSkipAlertThreshold";
    private const string CK_MaxDegreeOfParallelism = "MLCalibration:MaxDegreeOfParallelism";
    private const string CK_LongCycleWarnSeconds = "MLCalibration:LongCycleWarnSeconds";
    private const string StaleAlertDeduplicationPrefix = "ml-calibration-monitor-stale:";

    private const int DefaultPollSeconds = 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowDays = 14;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 3650;

    private const int DefaultMinSamples = 30;
    private const int MinMinSamples = 5;
    private const int MaxMinSamples = 10_000;

    private const double DefaultMaxEce = 0.15;
    private const double MinMaxEce = 0.0;
    private const double MaxMaxEce = 1.0;

    private const double DefaultDegradationDelta = 0.05;
    private const double MinDegradationDelta = 0.0;
    private const double MaxDegradationDelta = 1.0;

    private const int DefaultMaxResolvedPerModel = 512;
    private const int MinMaxResolvedPerModel = 10;
    private const int MaxMaxResolvedPerModel = 10_000;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultTrainingDataWindowDays = 365;
    private const int MinTrainingDataWindowDays = 30;
    private const int MaxTrainingDataWindowDays = 3650;

    private const int DefaultMinTimeBetweenRetrainsHours = 24;
    private const int MinMinTimeBetweenRetrainsHours = 0;
    private const int MaxMinTimeBetweenRetrainsHours = 24 * 30;

    private const int DefaultModelLockTimeoutSeconds = 30;
    private const int MinModelLockTimeoutSeconds = 1;
    private const int MaxModelLockTimeoutSeconds = 600;

    // One-sigma improvement bar by default. Set to ~3.0 for true Bonferroni-like coverage on
    // the trend signal. Auto-bypassed when bootstrap stderr is zero.
    private const double DefaultRegressionGuardK = 1.0;
    private const double MinRegressionGuardK = 0.0;
    private const double MaxRegressionGuardK = 5.0;

    private const int DefaultBootstrapResamples = 200;
    private const int MinBootstrapResamples = 0;
    private const int MaxBootstrapResamples = 5_000;

    private const double DefaultFleetDegradationRatio = 0.25;
    private const double MinFleetDegradationRatio = 0.0;
    private const double MaxFleetDegradationRatio = 1.0;

    private const int DefaultPerRegimeMinSamples = 30;
    private const int MinPerRegimeMinSamples = 5;
    private const int MaxPerRegimeMinSamples = 10_000;

    private const int DefaultPerRegimeMaxSnapshots = 5_000;
    private const int MinPerRegimeMaxSnapshots = 100;
    private const int MaxPerRegimeMaxSnapshots = 50_000;

    // Time decay defaults to off (auto-disabled below MinSamplesForTimeDecay regardless).
    // Calibration is less time-sensitive than threshold tuning, so most deployments leave
    // this at 0; turn on (e.g. 30d half-life) for fast-moving regimes.
    private const double DefaultTimeDecayHalfLifeDays = 0.0;
    private const double MinTimeDecayHalfLifeDays = 0.0;
    private const double MaxTimeDecayHalfLifeDays = 365.0;

    private const int DefaultMinSamplesForTimeDecay = 200;
    private const int MinMinSamplesForTimeDecay = 0;
    private const int MaxMinSamplesForTimeDecay = 5_000;

    // Smoothing window: 3 = average over last 3 cycles' ECE before computing trend delta.
    // Default raised from 1 because single-cycle deltas are objectively noisy on small
    // resolved-log windows; the median-of-3 absorbs transient one-cycle spikes without
    // meaningfully delaying detection of a real shift.
    private const int DefaultTrendSmoothingWindow = 3;
    private const int MinTrendSmoothingWindow = 1;
    private const int MaxTrendSmoothingWindow = 30;

    // Number of consecutive `no_recent_resolved_predictions` skips before the staleness
    // alert fires. Default 5 ≈ 5 hours at the default 1h cycle.
    private const int DefaultStaleSkipAlertThreshold = 5;
    private const int MinStaleSkipAlertThreshold = 1;
    private const int MaxStaleSkipAlertThreshold = 1000;

    // Bootstrap is cached per-model with this staleness window. Calibration drifts on the
    // scale of days, not hours; recomputing the stderr every cycle wastes CPU. The cached
    // value lives in EngineConfig and is invalidated when the cache age exceeds the bound.
    private const int DefaultBootstrapCacheStaleHours = 24;
    private const int MinBootstrapCacheStaleHours = 0;
    private const int MaxBootstrapCacheStaleHours = 24 * 30;

    // Bounded in-process concurrency for per-model evaluation. Default 1 preserves
    // historical strictly-sequential semantics; bumping this fans out to N concurrent
    // (model, lock-acquire, query, audit-flush) chains, each in its own DI scope. The
    // cycle-level distributed lock and bulkhead semaphore still gate the whole cycle.
    private const int DefaultMaxDegreeOfParallelism = 1;
    private const int MinMaxDegreeOfParallelism = 1;
    private const int MaxMaxDegreeOfParallelism = 16;

    // Wall-clock-cycle warning threshold. The cycle-level distributed lock is held for the
    // duration of one cycle; if the cycle wall-time approaches or exceeds the lock TTL the
    // lock can be re-acquired by another replica before this one finishes. Default 300s
    // (5 minutes); set to 0 to disable. Operators should keep this below the IDistributedLock
    // implementation's TTL.
    private const int DefaultLongCycleWarnSeconds = 300;
    private const int MinLongCycleWarnSeconds = 0;
    private const int MaxLongCycleWarnSeconds = 24 * 60 * 60;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCalibrationMonitorWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly IAlertDispatcher? _alertDispatcher;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    private int _overrideRegimeValidationDone;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture,
        byte[]? ModelBytes,
        uint RowVersion);

    private readonly record struct CalibrationSample(
        double Confidence,
        bool Correct,
        DateTime OutcomeAt,
        DateTime PredictedAt);

    private readonly record struct CalibrationSummary(
        int ResolvedCount,
        double CurrentEce,
        double Accuracy,
        double MeanConfidence,
        DateTime OldestOutcomeAt,
        DateTime NewestOutcomeAt,
        int[] BinCounts,
        double[] BinAccuracy,
        double[] BinMeanConfidence,
        double EceStderr);

    private readonly record struct CalibrationSignals(
        double? PreviousEce,
        double? BaselineEce,
        double TrendDelta,
        double BaselineDelta,
        bool ThresholdExceeded,
        bool TrendExceeded,
        bool BaselineExceeded,
        bool TrendStderrPasses);

    private readonly record struct RegimeSlice(
        DateTime DetectedAt,
        MarketRegimeEnum Regime);

    private readonly record struct ModelEvaluationOutcome(
        bool Evaluated,
        MLCalibrationMonitorAlertState AlertState,
        bool RetrainingQueued,
        bool AlertDispatched,
        bool AlertResolved,
        string? SkipReason)
    {
        public static ModelEvaluationOutcome Skipped(string reason)
            => new(false, MLCalibrationMonitorAlertState.None, false, false, false, reason);
    }

    public MLCalibrationMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCalibrationMonitorWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
        _alertDispatcher = alertDispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Live ML probability calibration monitor with bootstrap stderr, K-sigma trend gating, per-bin diagnostics, per-regime breakdown, audit logging, and fleet-level dampening.",
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
                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("parallelism", result.Settings.MaxDegreeOfParallelism));
                    _metrics?.MLCalibrationMonitorCycleDurationMs.Record(durationMs);

                    // Long-cycle guard: warn when wall-time approaches the lock TTL window.
                    // Cycle-level distributed lock is held for the whole cycle, so a long
                    // cycle risks the lock expiring and another replica re-acquiring before
                    // this one finishes flushing audits. The duration histogram (with the
                    // parallelism tag above) is the source of truth for alerting; this log
                    // is the operator's prompt to verify the IDistributedLock TTL.
                    int warnSec = result.Settings.LongCycleWarnSeconds;
                    if (warnSec > 0 && durationMs > warnSec * 1000L)
                    {
                        _logger.LogWarning(
                            "{Worker}: cycle wall-time {DurationMs}ms exceeded LongCycleWarnSeconds={WarnSec}s. Verify the IDistributedLock TTL is at least this long; otherwise another replica may re-acquire the cycle lock mid-flight.",
                            WorkerName, durationMs, warnSec);
                    }

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
                            "{Worker}: candidates={Candidates}, evaluated={Evaluated}, warning={Warning}, critical={Critical}, retrainingQueued={Queued}, alertsDispatched={Dispatched}, alertsResolved={Resolved}, fleetAlertDispatched={Fleet}, failed={Failed}.",
                            WorkerName,
                            result.CandidateModelCount,
                            result.EvaluatedModelCount,
                            result.WarningModelCount,
                            result.CriticalModelCount,
                            result.RetrainingQueuedCount,
                            result.DispatchedAlertCount,
                            result.ResolvedAlertCount,
                            result.FleetAlertDispatched,
                            result.FailedModelCount);
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
                        new KeyValuePair<string, object?>("reason", "ml_calibration_monitor_cycle"));
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

    internal async Task<MLCalibrationMonitorCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLCalibrationMonitorCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLCalibrationMonitorCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLCalibrationMonitorLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate calibration-monitor cycles are possible in multi-instance deployments.",
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
                _metrics?.MLCalibrationMonitorLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLCalibrationMonitorCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLCalibrationMonitorCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLCalibrationMonitorLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));

            await using (cycleLock)
            {
                await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
                try
                {
                    return await RunCycleCoreAsync(db, settings, ct);
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
            return await RunCycleCoreAsync(db, settings, ct);
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

    private async Task<MLCalibrationMonitorCycleResult> RunCycleCoreAsync(
        DbContext db,
        MLCalibrationMonitorWorkerSettings settings,
        CancellationToken ct)
    {
        var models = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model =>
                model.IsActive &&
                !model.IsDeleted &&
                !model.IsMetaLearner &&
                !model.IsMamlInitializer)
            .Select(model => new ActiveModelCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.LearnerArchitecture,
                model.ModelBytes,
                model.RowVersion))
            .ToListAsync(ct);

        if (models.Count == 0)
        {
            _metrics?.MLCalibrationMonitorCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return MLCalibrationMonitorCycleResult.Skipped(settings, "no_active_models");
        }

        // Once-per-process audit of override-key regime tokens. Helps operators catch typos
        // early instead of having keys silently fall through tiers. Gated via Interlocked
        // CompareExchange so concurrent first cycles still run it once.
        if (Interlocked.CompareExchange(ref _overrideRegimeValidationDone, 1, 0) == 0)
        {
            await ValidateOverrideRegimeNamesAsync(db, ct);
        }

        // Pre-load per-model latest NewestOutcomeAt across all prior cycles. Survives restarts
        // and is shared across replicas via the audit table.
        var modelIds = models.Select(model => model.Id).ToList();
        var lastNewestOutcome = await LoadLastNewestOutcomeMapAsync(db, modelIds, ct);

        // Pre-load per-context overrides once per unique (Symbol, Timeframe) so parallel
        // model iterations share a single read. With one model per pair (typical) this is
        // identical to the previous per-model load; with multiple variants per pair it
        // collapses N reads to one. The dict is immutable plain data and safe to share.
        var overridesByContext = await LoadOverridesByContextAsync(db, models, ct);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        int parallelism = Math.Clamp(settings.MaxDegreeOfParallelism, 1, MaxMaxDegreeOfParallelism);

        // Per-model evaluation runs under bounded parallelism. Each iteration owns its
        // DI scope (and therefore its own DbContext), so EF state never crosses the
        // boundary. Outcome aggregation happens after the loop from a thread-safe bag;
        // counters that need to fire mid-iteration use Interlocked.
        var outcomes = new ConcurrentBag<ModelEvaluationOutcome>();
        int failedModels = 0;

        await Parallel.ForEachAsync(
            models,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (model, modelCt) =>
            {
                lastNewestOutcome.TryGetValue(model.Id, out var lastSeen);
                var overrides = overridesByContext[(model.Symbol, model.Timeframe)];

                await using var modelScope = _scopeFactory.CreateAsyncScope();
                var modelWriteCtx = modelScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
                var modelDb = modelWriteCtx.GetDbContext();

                try
                {
                    // Refresh the worker heartbeat before each model evaluation. Long
                    // cycles (large fleet / DOP=1) would otherwise leave the health
                    // monitor without a signal until the cycle ends.
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var outcome = await EvaluateModelWithLockAsync(
                        modelScope.ServiceProvider,
                        modelWriteCtx,
                        modelDb,
                        model,
                        settings,
                        overrides,
                        lastSeen,
                        nowUtc,
                        modelCt);

                    outcomes.Add(outcome);

                    if (!outcome.Evaluated)
                    {
                        _metrics?.MLCalibrationMonitorModelsSkipped.Add(
                            1,
                            new KeyValuePair<string, object?>("reason", outcome.SkipReason ?? "skipped"),
                            new KeyValuePair<string, object?>("symbol", model.Symbol),
                            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                    }
                }
                catch (OperationCanceledException) when (modelCt.IsCancellationRequested)
                {
                    // Shutdown propagation, not a model failure. Re-throw so
                    // Parallel.ForEachAsync surfaces it; the ExecuteAsync loop will
                    // honour stoppingToken and exit cleanly.
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedModels);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "ml_calibration_monitor_model"),
                        new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
                    _logger.LogWarning(
                        ex,
                        "{Worker}: failed to evaluate calibration for model {ModelId} ({Symbol}/{Timeframe}).",
                        WorkerName,
                        model.Id,
                        model.Symbol,
                        model.Timeframe);
                }
            }).ConfigureAwait(false);

        int evaluatedModels = 0;
        int warningModels = 0;
        int criticalModels = 0;
        int retrainingQueued = 0;
        int dispatchedAlerts = 0;
        int resolvedAlerts = 0;
        foreach (var outcome in outcomes)
        {
            if (!outcome.Evaluated) continue;
            evaluatedModels++;
            if (outcome.AlertState == MLCalibrationMonitorAlertState.Warning) warningModels++;
            else if (outcome.AlertState == MLCalibrationMonitorAlertState.Critical) criticalModels++;
            if (outcome.RetrainingQueued) retrainingQueued++;
            if (outcome.AlertDispatched) dispatchedAlerts++;
            if (outcome.AlertResolved) resolvedAlerts++;
        }

        bool fleetAlertDispatched = false;
        if (evaluatedModels >= FleetAlertMinModels)
        {
            int degraded = warningModels + criticalModels;
            double ratio = (double)degraded / evaluatedModels;
            if (ratio >= settings.FleetDegradationRatio)
            {
                fleetAlertDispatched = await RaiseFleetDegradationAlertAsync(
                    evaluatedModels, warningModels, criticalModels, ratio, nowUtc, ct);
            }
        }

        return new MLCalibrationMonitorCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: models.Count,
            EvaluatedModelCount: evaluatedModels,
            WarningModelCount: warningModels,
            CriticalModelCount: criticalModels,
            RetrainingQueuedCount: retrainingQueued,
            DispatchedAlertCount: dispatchedAlerts,
            ResolvedAlertCount: resolvedAlerts,
            FailedModelCount: failedModels,
            FleetAlertDispatched: fleetAlertDispatched);
    }

    private static async Task<Dictionary<long, DateTime?>> LoadLastNewestOutcomeMapAsync(
        DbContext db, List<long> modelIds, CancellationToken ct)
    {
        if (modelIds.Count == 0) return [];

        var rows = await db.Set<MLCalibrationLog>()
            .AsNoTracking()
            .Where(log => modelIds.Contains(log.MLModelId)
                       && !log.IsDeleted
                       && log.NewestOutcomeAt != null
                       && log.Regime == null)
            .GroupBy(log => log.MLModelId)
            .Select(group => new { ModelId = group.Key, MaxAt = group.Max(log => log.NewestOutcomeAt) })
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.ModelId, row => row.MaxAt);
    }

    private async Task<ModelEvaluationOutcome> EvaluateModelWithLockAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        DateTime? lastSeenOutcomeAt,
        DateTime nowUtc,
        CancellationToken ct)
    {
        IAsyncDisposable? modelLock = null;
        if (_distributedLock is not null)
        {
            modelLock = await _distributedLock.TryAcquireAsync(
                ModelLockKeyPrefix + model.Id.ToString(CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(settings.ModelLockTimeoutSeconds),
                ct);

            if (modelLock is null)
            {
                return ModelEvaluationOutcome.Skipped("model_lock_busy");
            }
        }

        try
        {
            return await EvaluateModelAsync(serviceProvider, writeContext, db, model, settings,
                overrides, lastSeenOutcomeAt, nowUtc, ct);
        }
        finally
        {
            if (modelLock is not null)
                await modelLock.DisposeAsync();
        }
    }

    private async Task<ModelEvaluationOutcome> EvaluateModelAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        DateTime? lastSeenOutcomeAt,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Audit rows accumulate locally and flush in a dedicated DI scope at the end. Keeps
        // audit IO from implicitly committing pending changes on the snapshot scope and gives
        // operators a durable trail regardless of failure mode. Capacity = 1 global + one per
        // possible regime, derived from the enum so it self-adjusts if MarketRegime grows.
        var pendingAudits = new List<MLCalibrationLog>(1 + RegimeCount);
        // Bootstrap-cache writes accumulate across the global + per-regime evaluation paths
        // and flush in a single batched upsert at the end of the cycle. With N matched
        // regimes and a full cache miss this collapses (1 + N) round-trips into 1.
        var pendingCacheSpecs = new List<EngineConfigUpsertSpec>(8);

        // Apply per-context overrides on top of the cycle-wide defaults. The `overrides`
        // dict is pre-loaded once per unique (Symbol, Timeframe) at the cycle level and
        // shared across iterations, so two models on the same context hit a single read.
        // ApplyPerContextOverrides walks the 8-tier hierarchy per knob in memory:
        // regime-scoped tiers first when a regime is supplied (here, null for the global
        // path), then the four regime-agnostic tiers
        // (Symbol+TF → Symbol-only → TF-only → fleet-wide).
        settings = ApplyPerContextOverrides(settings, overrides, model.Symbol, model.Timeframe);

        try
        {
            var lookbackCutoff = nowUtc.AddDays(-settings.WindowDays);

            var resolvedLogs = await db.Set<MLModelPredictionLog>()
                .AsNoTracking()
                .Where(log =>
                    log.MLModelId == model.Id &&
                    !log.IsDeleted &&
                    (log.DirectionCorrect != null || log.ActualDirection != null) &&
                    (log.OutcomeRecordedAt ?? log.PredictedAt) >= lookbackCutoff)
                .OrderByDescending(log => log.OutcomeRecordedAt ?? log.PredictedAt)
                .ThenByDescending(log => log.Id)
                .Take(settings.MaxResolvedPerModel)
                .ToListAsync(ct);

            if (resolvedLogs.Count == 0)
            {
                await TrackStaleAndAlertIfNeededAsync(serviceProvider, db, writeContext, model, settings, nowUtc, ct);
                return ModelEvaluationOutcome.Skipped("no_recent_resolved_predictions");
            }

            var samples = new List<CalibrationSample>(resolvedLogs.Count);
            foreach (var log in resolvedLogs)
            {
                if (TryCreateCalibrationSample(log, out var sample))
                    samples.Add(sample);
            }

            if (samples.Count < settings.MinSamples)
            {
                await TrackStaleAndAlertIfNeededAsync(serviceProvider, db, writeContext, model, settings, nowUtc, ct);
                EnqueueAudit(pendingAudits, model, regime: null,
                    outcome: "skipped_data",
                    reason: "insufficient_resolved_samples",
                    summary: default,
                    signals: default,
                    alertState: MLCalibrationMonitorAlertState.None,
                    newestOutcomeAt: null,
                    diagnostics: BuildDiagnostics(("availableSamples", samples.Count), ("required", settings.MinSamples)),
                    evaluatedAt: nowUtc);
                return ModelEvaluationOutcome.Skipped("insufficient_resolved_calibration_history");
            }

            // Reset the consecutive-skip counter: this model has fresh resolved logs.
            await ResetStaleSkipCounterAsync(db, model.Id, ct);

            DateTime newestOutcomeAt = samples.Max(sample => sample.OutcomeAt);

            // Cross-restart short-circuit: if no new resolved logs since the last cycle, the
            // ECE measurement is unchanged. Skip without auditing — repeat rows would just be
            // duplicates without information.
            if (lastSeenOutcomeAt.HasValue && newestOutcomeAt <= lastSeenOutcomeAt.Value)
                return ModelEvaluationOutcome.Skipped("no_new_outcomes");

            double? cachedStderr = await LoadFreshBootstrapStderrAsync(
                db, model.Id, regime: null, model.RowVersion, nowUtc,
                settings.BootstrapCacheStaleHours, ct);
            bool globalBootstrapCacheHit = cachedStderr.HasValue;
            _metrics?.MLCalibrationMonitorBootstrapCacheLookups.Add(
                1,
                new KeyValuePair<string, object?>("outcome", globalBootstrapCacheHit ? "hit" : "miss"),
                new KeyValuePair<string, object?>("scope", "global"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

            var summary = ComputeCalibrationSummary(
                samples, settings.BootstrapResamples, nowUtc,
                settings.TimeDecayHalfLifeDays, settings.MinSamplesForTimeDecay,
                cachedStderr, model.Id);
            // When we recomputed the stderr (cache was missing or stale), append the cache
            // refresh specs to the pending batch — they flush together with the summary keys.
            if (!globalBootstrapCacheHit && summary.EceStderr > 0)
            {
                AppendBootstrapCacheSpecs(
                    pendingCacheSpecs, model.Id, regime: null,
                    summary.EceStderr, model.RowVersion, nowUtc);
            }
            // Smoothed previous-ECE: average over the last N global audit rows for this model.
            // With TrendSmoothingWindow = 1 (default) this collapses to single-cycle behavior;
            // higher values dampen transient one-cycle spikes that auto-resolve next cycle.
            double? previousEce = await LoadSmoothedPreviousEceAsync(
                db, model.Id, regime: null, settings.TrendSmoothingWindow, ct)
                ?? await LoadExistingMetricAsync(db, $"MLCalibration:Model:{model.Id}:CurrentEce", ct);
            double? baselineEce = TryResolveBaselineEce(model.ModelBytes);
            var signals = BuildSignals(
                summary.CurrentEce,
                summary.EceStderr,
                previousEce,
                baselineEce,
                settings.MaxEce,
                settings.DegradationDelta,
                settings.RegressionGuardK);
            var alertState = ResolveAlertState(summary.CurrentEce, signals, settings);

            string stateTag = alertState switch
            {
                MLCalibrationMonitorAlertState.Critical => "critical",
                MLCalibrationMonitorAlertState.Warning => "warning",
                _ => "healthy"
            };

            _metrics?.MLCalibrationMonitorModelsEvaluated.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("state", stateTag));
            _metrics?.MLCalibrationMonitorCurrentEce.Record(
                summary.CurrentEce,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("state", stateTag));
            _metrics?.MLCalibrationMonitorResolvedSamples.Record(
                summary.ResolvedCount,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

            if (signals.PreviousEce.HasValue)
            {
                _metrics?.MLCalibrationMonitorEceDelta.Record(
                    signals.TrendDelta,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("source", "trend"));
            }

            if (signals.BaselineEce.HasValue)
            {
                _metrics?.MLCalibrationMonitorEceDelta.Record(
                    signals.BaselineDelta,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("source", "baseline"));
            }

            await PersistSummaryAsync(db, model, summary, signals, nowUtc, pendingCacheSpecs, ct);

            bool retrainingQueued = false;
            bool alertDispatched = false;
            bool alertResolved = false;

            // Critical state can be reached via three different signals; only two of them
            // suggest retraining will help by default.
            //
            // - Threshold critical: model is way off the absolute ECE ceiling → retrain
            // - Trend critical: model is rapidly decalibrating from its own recent past → retrain
            // - Baseline critical (only): live ECE has always been worse than training-time ECE.
            //   Retraining on the same data window is usually unhelpful — the gap is typically
            //   distributional, not noise — so by default we alert but suppress the retrain.
            //   Operators who believe their training-time baseline is stale (e.g. labels were
            //   later corrected) can flip RetrainOnBaselineCritical = true (globally or per
            //   Symbol/Timeframe via the MLCalibration:Override:{Symbol}:{Timeframe}: pattern).
            bool retrainEligible =
                (signals.ThresholdExceeded && summary.CurrentEce > settings.MaxEce * SevereThresholdMultiplier) ||
                (signals.TrendExceeded && signals.TrendDelta > settings.DegradationDelta * SevereThresholdMultiplier) ||
                (settings.RetrainOnBaselineCritical && signals.BaselineExceeded
                    && signals.BaselineDelta > settings.DegradationDelta * SevereThresholdMultiplier);

            if (alertState == MLCalibrationMonitorAlertState.Critical && retrainEligible)
            {
                retrainingQueued = await QueueRetrainingIfNeededAsync(
                    db, model, settings, summary, signals, nowUtc, ct);

                if (retrainingQueued)
                {
                    _metrics?.MLCalibrationMonitorRetrainingQueued.Add(
                        1,
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                }
            }

            if (alertState != MLCalibrationMonitorAlertState.None)
            {
                alertDispatched = await UpsertAndDispatchAlertAsync(
                    serviceProvider, writeContext, db, model, settings, summary, signals,
                    alertState, nowUtc, ct);

                if (alertDispatched)
                {
                    _metrics?.MLCalibrationMonitorAlertsDispatched.Add(
                        1,
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                        new KeyValuePair<string, object?>("state", stateTag));
                    _metrics?.MLCalibrationMonitorAlertTransitions.Add(
                        1,
                        new KeyValuePair<string, object?>("transition", "dispatched"));
                }
            }
            else
            {
                alertResolved = await ResolveAlertAsync(serviceProvider, writeContext, db, model, nowUtc, ct);
                if (alertResolved)
                {
                    _metrics?.MLCalibrationMonitorAlertTransitions.Add(
                        1,
                        new KeyValuePair<string, object?>("transition", "resolved"));
                }
            }

            // Global audit row records the canonical decision for this cycle.
            string globalOutcome = retrainingQueued
                ? "retrain_queued"
                : alertState switch
                {
                    MLCalibrationMonitorAlertState.Critical => "alert_critical",
                    MLCalibrationMonitorAlertState.Warning => "alert_warning",
                    _ => alertResolved ? "auto_resolved" : "evaluated",
                };

            string globalReason = signals.ThresholdExceeded ? "threshold_exceeded"
                : signals.TrendExceeded ? "trend_exceeded"
                : signals.BaselineExceeded ? "baseline_exceeded"
                : "healthy";

            EnqueueAudit(pendingAudits, model, regime: null,
                outcome: globalOutcome,
                reason: globalReason,
                summary: summary,
                signals: signals,
                alertState: alertState,
                newestOutcomeAt: newestOutcomeAt,
                diagnostics: BuildDiagnosticsWithBins(summary, signals, settings, globalBootstrapCacheHit),
                evaluatedAt: nowUtc);

            // Per-regime breakdown: pool samples by the active regime at PredictedAt and
            // measure ECE per regime. Each regime gets its own audit row so dashboards can
            // see whether miscalibration is regime-localised. The same `overrides` dict is
            // re-used so regime-scoped tiers can tighten knobs only in specific regimes
            // without a second round-trip per regime.
            await EvaluatePerRegimeAsync(db, model, samples, settings, overrides, nowUtc, pendingAudits, pendingCacheSpecs, ct);

            _logger.LogDebug(
                "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) ece={Ece:F6}±{Stderr:F6}, accuracy={Accuracy:P1}, meanConfidence={MeanConfidence:F4}, previous={PreviousEce}, baseline={BaselineEce}, trendDelta={TrendDelta:F6}, baselineDelta={BaselineDelta:F6}, samples={Samples}, state={State}.",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe,
                summary.CurrentEce,
                summary.EceStderr,
                summary.Accuracy,
                summary.MeanConfidence,
                signals.PreviousEce?.ToString("F6", CultureInfo.InvariantCulture) ?? "n/a",
                signals.BaselineEce?.ToString("F6", CultureInfo.InvariantCulture) ?? "n/a",
                signals.TrendDelta,
                signals.BaselineDelta,
                summary.ResolvedCount,
                stateTag);

            return new ModelEvaluationOutcome(
                Evaluated: true,
                AlertState: alertState,
                RetrainingQueued: retrainingQueued,
                AlertDispatched: alertDispatched,
                AlertResolved: alertResolved,
                SkipReason: null);
        }
        finally
        {
            if (pendingAudits.Count > 0)
                await FlushAuditsAsync(pendingAudits, ct);
        }
    }

    private async Task EvaluatePerRegimeAsync(
        DbContext db,
        ActiveModelCandidate model,
        List<CalibrationSample> samples,
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        DateTime nowUtc,
        List<MLCalibrationLog> pendingAudits,
        List<EngineConfigUpsertSpec> pendingCacheSpecs,
        CancellationToken ct)
    {
        if (samples.Count == 0) return;

        var sortedAsc = samples.OrderBy(sample => sample.PredictedAt).ToList();
        var earliest = sortedAsc[0].PredictedAt;
        var latest = sortedAsc[^1].PredictedAt;

        var regimeTimeline = await db.Set<MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(snapshot => snapshot.Symbol == model.Symbol
                            && snapshot.Timeframe == model.Timeframe
                            && !snapshot.IsDeleted
                            && snapshot.DetectedAt >= earliest.AddDays(-1)
                            && snapshot.DetectedAt <= latest)
            .OrderBy(snapshot => snapshot.DetectedAt)
            .Take(settings.PerRegimeMaxSnapshots)
            .Select(snapshot => new RegimeSlice(snapshot.DetectedAt, snapshot.Regime))
            .ToListAsync(ct);

        if (regimeTimeline.Count == 0) return;

        var groups = AssignRegimes(sortedAsc, regimeTimeline);

        foreach (var (regime, regimeSamples) in groups)
        {
            if (regimeSamples.Count < settings.PerRegimeMinSamples) continue;

            // Apply regime-scoped overrides on top of the regime-agnostic settings clone.
            // ResolveOverride walks the 8 tiers (4 regime-scoped → 4 regime-agnostic) so a
            // row like `*:*:Regime:HighVolatility:DegradationDelta` tightens that knob in
            // exactly the regimes operators care about, without affecting the global path.
            var regimeSettings = ApplyPerContextOverrides(
                settings, overrides, model.Symbol, model.Timeframe, regime);

            // Per-regime stderr is cached under its own scope key so each regime amortises
            // bootstrap CPU separately. RowVersion check ensures a model swap invalidates
            // every regime's cache simultaneously.
            double? regimeCachedStderr = await LoadFreshBootstrapStderrAsync(
                db, model.Id, regime, model.RowVersion, nowUtc, regimeSettings.BootstrapCacheStaleHours, ct);
            bool regimeBootstrapCacheHit = regimeCachedStderr.HasValue;
            _metrics?.MLCalibrationMonitorBootstrapCacheLookups.Add(
                1,
                new KeyValuePair<string, object?>("outcome", regimeBootstrapCacheHit ? "hit" : "miss"),
                new KeyValuePair<string, object?>("scope", "regime"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("regime", regime.ToString()));

            var regimeSummary = ComputeCalibrationSummary(
                regimeSamples, regimeSettings.BootstrapResamples, nowUtc,
                regimeSettings.TimeDecayHalfLifeDays, regimeSettings.MinSamplesForTimeDecay,
                regimeCachedStderr, model.Id);
            if (!regimeBootstrapCacheHit && regimeSummary.EceStderr > 0)
            {
                AppendBootstrapCacheSpecs(
                    pendingCacheSpecs, model.Id, regime,
                    regimeSummary.EceStderr, model.RowVersion, nowUtc);
            }
            // Per-regime trend signal reads the prior per-regime ECE from the audit log so
            // regime drift is detected even when the global trend is flat. Returns null on
            // first cycle for a given regime, in which case the trend signal stays inert.
            double? regimePreviousEce = await LoadSmoothedPreviousEceAsync(
                db, model.Id, regime, regimeSettings.TrendSmoothingWindow, ct);
            var regimeSignals = BuildSignals(
                regimeSummary.CurrentEce,
                regimeSummary.EceStderr,
                previousEce: regimePreviousEce,
                baselineEce: TryResolveBaselineEce(model.ModelBytes, regime),
                regimeSettings.MaxEce,
                regimeSettings.DegradationDelta,
                regimeSettings.RegressionGuardK);
            var regimeState = ResolveAlertState(regimeSummary.CurrentEce, regimeSignals, regimeSettings);

            string regimeOutcome = regimeState switch
            {
                MLCalibrationMonitorAlertState.Critical => "alert_critical",
                MLCalibrationMonitorAlertState.Warning => "alert_warning",
                _ => "evaluated",
            };

            string regimeReason = regimeSignals.ThresholdExceeded ? "threshold_exceeded"
                : regimeSignals.BaselineExceeded ? "baseline_exceeded"
                : "healthy";

            EnqueueAudit(pendingAudits, model, regime: regime,
                outcome: regimeOutcome,
                reason: regimeReason,
                summary: regimeSummary,
                signals: regimeSignals,
                alertState: regimeState,
                newestOutcomeAt: regimeSummary.NewestOutcomeAt,
                diagnostics: BuildDiagnosticsWithBins(regimeSummary, regimeSignals, regimeSettings, regimeBootstrapCacheHit),
                evaluatedAt: nowUtc);
        }
    }

    private static Dictionary<MarketRegimeEnum, List<CalibrationSample>> AssignRegimes(
        List<CalibrationSample> chronological, List<RegimeSlice> ascendingTimeline)
    {
        var groups = new Dictionary<MarketRegimeEnum, List<CalibrationSample>>();
        if (ascendingTimeline.Count == 0) return groups;

        // Binary search per sample to find the regime that was active at PredictedAt.
        var detectedAtArray = ascendingTimeline.Select(slice => slice.DetectedAt).ToArray();

        foreach (var sample in chronological)
        {
            int idx = Array.BinarySearch(detectedAtArray, sample.PredictedAt);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;

            var regime = ascendingTimeline[idx].Regime;
            if (!groups.TryGetValue(regime, out var bucket))
            {
                bucket = [];
                groups[regime] = bucket;
            }
            bucket.Add(sample);
        }

        return groups;
    }

    private static bool TryCreateCalibrationSample(
        MLModelPredictionLog log,
        out CalibrationSample sample)
    {
        sample = default;

        bool? correct = log.DirectionCorrect;
        if (!correct.HasValue && log.ActualDirection.HasValue)
            correct = log.ActualDirection.Value == log.PredictedDirection;

        if (!correct.HasValue)
            return false;

        double confidence;
        if (HasExplicitProbability(log))
        {
            double threshold = MLFeatureHelper.ResolveLoggedDecisionThreshold(log, 0.5);
            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(log, threshold);
            confidence = log.PredictedDirection == TradeDirection.Buy
                ? pBuy
                : 1.0 - pBuy;
        }
        else
        {
            // Legacy logs: ConfidenceScore is the predicted-class confidence by convention
            // (matches how scorers populate the field). Direct assignment is correct.
            confidence = (double)log.ConfidenceScore;
        }

        if (!double.IsFinite(confidence))
            return false;

        sample = new CalibrationSample(
            Confidence: Math.Clamp(confidence, 0.0, 1.0),
            Correct: correct.Value,
            OutcomeAt: log.OutcomeRecordedAt ?? log.PredictedAt,
            PredictedAt: log.PredictedAt);
        return true;
    }

    private static CalibrationSummary ComputeCalibrationSummary(
        IReadOnlyList<CalibrationSample> samples,
        int bootstrapResamples,
        DateTime nowUtc,
        double timeDecayHalfLifeDays,
        int minSamplesForTimeDecay,
        double? cachedStderr,
        long modelId)
    {
        // Time decay is auto-disabled below MinSamplesForTimeDecay so the tilt cannot
        // dominate floating-point noise on small samples.
        double effectiveHalfLife = samples.Count >= minSamplesForTimeDecay
            ? timeDecayHalfLifeDays
            : 0.0;

        var binCounts = new double[NumBins];
        var binCorrect = new double[NumBins];
        var binConfidenceSum = new double[NumBins];

        double correctSum = 0.0;
        double confidenceSum = 0.0;
        double totalWeight = 0.0;
        DateTime oldestOutcomeAt = DateTime.MaxValue;
        DateTime newestOutcomeAt = DateTime.MinValue;

        foreach (var sample in samples)
        {
            double weight = ComputeTimeDecayWeight(sample.OutcomeAt, nowUtc, effectiveHalfLife);
            if (!double.IsFinite(weight) || weight <= 0) continue;

            int bin = Math.Clamp((int)(sample.Confidence * NumBins), 0, NumBins - 1);
            binCounts[bin] += weight;
            binConfidenceSum[bin] += sample.Confidence * weight;
            confidenceSum += sample.Confidence * weight;
            totalWeight += weight;

            if (sample.Correct)
            {
                binCorrect[bin] += weight;
                correctSum += weight;
            }

            if (sample.OutcomeAt < oldestOutcomeAt) oldestOutcomeAt = sample.OutcomeAt;
            if (sample.OutcomeAt > newestOutcomeAt) newestOutcomeAt = sample.OutcomeAt;
        }

        double ece = ComputeEceFromBins(binCounts, binCorrect, binConfidenceSum, totalWeight);

        var binAccuracy = new double[NumBins];
        var binMeanConfidence = new double[NumBins];
        var binCountsForAudit = new int[NumBins];
        for (int i = 0; i < NumBins; i++)
        {
            // Round effective counts for the audit-log integer bin counts; the weighted ECE
            // calculation above uses the doubles directly.
            binCountsForAudit[i] = (int)Math.Round(binCounts[i]);
            if (binCounts[i] <= 0) continue;
            binAccuracy[i] = binCorrect[i] / binCounts[i];
            binMeanConfidence[i] = binConfidenceSum[i] / binCounts[i];
        }

        // Bootstrap caching: calibration drifts on the scale of days, not hours; recomputing
        // the stderr every cycle wastes CPU. The caller supplies the cached value when fresh
        // (within BootstrapCacheStaleHours); we recompute only when the cache is stale or
        // missing.
        double eceStderr = cachedStderr
            ?? ComputeBootstrapEceStderr(samples, bootstrapResamples, nowUtc, effectiveHalfLife, modelId);

        return new CalibrationSummary(
            ResolvedCount: samples.Count,
            CurrentEce: ece,
            Accuracy: totalWeight > 0 ? correctSum / totalWeight : 0,
            MeanConfidence: totalWeight > 0 ? confidenceSum / totalWeight : 0,
            OldestOutcomeAt: oldestOutcomeAt,
            NewestOutcomeAt: newestOutcomeAt,
            BinCounts: binCountsForAudit,
            BinAccuracy: binAccuracy,
            BinMeanConfidence: binMeanConfidence,
            EceStderr: eceStderr);
    }

    private static double ComputeTimeDecayWeight(DateTime outcomeAt, DateTime nowUtc, double halfLifeDays)
    {
        if (halfLifeDays <= 0) return 1.0;
        double ageDays = Math.Max(0, (nowUtc - outcomeAt).TotalDays);
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    private static double ComputeEceFromBins(
        double[] binCounts, double[] binCorrect, double[] binConfidenceSum, double total)
    {
        if (total <= 0) return 0.0;
        double ece = 0.0;
        for (int i = 0; i < binCounts.Length; i++)
        {
            if (binCounts[i] <= 0) continue;
            double accuracy = binCorrect[i] / binCounts[i];
            double meanConfidence = binConfidenceSum[i] / binCounts[i];
            ece += (binCounts[i] / total) * Math.Abs(accuracy - meanConfidence);
        }
        return ece;
    }

    private static double ComputeBootstrapEceStderr(
        IReadOnlyList<CalibrationSample> samples,
        int resamples,
        DateTime nowUtc,
        double effectiveHalfLifeDays,
        long modelId)
    {
        if (resamples <= 0 || samples.Count < 2) return 0.0;

        // Deterministic, fixed-mix seed: FNV-1a 64 over (modelId, count, firstTick, lastTick)
        // folded to int. Identical inputs reproduce the same stderr across runs and replicas.
        // We avoid HashCode.Combine here because that uses a process-randomized seed, which
        // would make two replicas of the same model disagree on a cold-cache stderr.
        // Including modelId in the mix prevents collisions between two models that happen to
        // share sample-boundary timestamps.
        long seed;
        unchecked
        {
            seed = 1469598103934665603L; // FNV-1a 64-bit offset basis
            const long fnvPrime = 1099511628211L;
            seed = (seed ^ modelId) * fnvPrime;
            seed = (seed ^ samples.Count) * fnvPrime;
            seed = (seed ^ samples[0].OutcomeAt.Ticks) * fnvPrime;
            seed = (seed ^ samples[^1].OutcomeAt.Ticks) * fnvPrime;
        }
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));

        var binCounts = new double[NumBins];
        var binCorrect = new double[NumBins];
        var binConfidenceSum = new double[NumBins];

        double sum = 0.0;
        double sumSq = 0.0;

        int n = samples.Count;
        for (int r = 0; r < resamples; r++)
        {
            Array.Clear(binCounts);
            Array.Clear(binCorrect);
            Array.Clear(binConfidenceSum);
            double total = 0.0;

            for (int i = 0; i < n; i++)
            {
                var sample = samples[rng.Next(n)];
                double weight = ComputeTimeDecayWeight(sample.OutcomeAt, nowUtc, effectiveHalfLifeDays);
                if (!double.IsFinite(weight) || weight <= 0) continue;

                int bin = Math.Clamp((int)(sample.Confidence * NumBins), 0, NumBins - 1);
                binCounts[bin] += weight;
                binConfidenceSum[bin] += sample.Confidence * weight;
                total += weight;
                if (sample.Correct) binCorrect[bin] += weight;
            }

            double ece = ComputeEceFromBins(binCounts, binCorrect, binConfidenceSum, total);
            sum += ece;
            sumSq += ece * ece;
        }

        double mean = sum / resamples;
        double variance = (sumSq / resamples) - (mean * mean);
        if (variance < 0 || !double.IsFinite(variance)) variance = 0;
        return Math.Sqrt(variance);
    }

    private static CalibrationSignals BuildSignals(
        double currentEce,
        double eceStderr,
        double? previousEce,
        double? baselineEce,
        double maxEce,
        double degradationDelta,
        double regressionGuardK)
    {
        double trendDelta = previousEce.HasValue ? currentEce - previousEce.Value : 0.0;
        double baselineDelta = baselineEce.HasValue ? currentEce - baselineEce.Value : 0.0;
        bool thresholdExceeded = maxEce > 0.0 && currentEce > maxEce;

        // Trend signal must clear BOTH the absolute degradation delta AND the K-sigma stderr
        // bar. With non-zero stderr this rejects single-cycle drift inside the noise band.
        // With zero stderr (small samples, perfectly homogeneous outcomes) the K-sigma bar
        // collapses to the absolute delta.
        bool trendDeltaExceeded = degradationDelta > 0.0 && previousEce.HasValue && trendDelta > degradationDelta;
        bool trendStderrPasses = trendDelta > regressionGuardK * eceStderr;
        bool trendExceeded = trendDeltaExceeded && trendStderrPasses;

        bool baselineExceeded = degradationDelta > 0.0 && baselineEce.HasValue && baselineDelta > degradationDelta;

        return new CalibrationSignals(
            previousEce,
            baselineEce,
            trendDelta,
            baselineDelta,
            thresholdExceeded,
            trendExceeded,
            baselineExceeded,
            trendStderrPasses);
    }

    /// <summary>
    /// Persists the four current-state keys this worker writes to <c>EngineConfig</c>.
    /// </summary>
    /// <remarks>
    /// Writes only the four current-state hot-reload keys
    /// (<c>:CurrentEce</c>, <c>:EceStderr</c>, <c>:CalibrationDegrading</c>, <c>:LastEvaluatedAt</c>)
    /// plus internal bootstrap-cache scaffolding. Time-series data lives in
    /// <c>MLCalibrationLog</c>. For the deleted-key migration mapping, see
    /// <c>docs/migrations/2026-04-mlcalibrationmonitor-engineconfig-cleanup.md</c>.
    /// </remarks>
    private static async Task PersistSummaryAsync(
        DbContext db,
        ActiveModelCandidate model,
        CalibrationSummary summary,
        CalibrationSignals signals,
        DateTime nowUtc,
        List<EngineConfigUpsertSpec> pendingCacheSpecs,
        CancellationToken ct)
    {
        string modelPrefix = $"MLCalibration:Model:{model.Id}";

        // Combine the four hot-reload summary keys with any bootstrap-cache refresh specs
        // accumulated during this cycle (global + per-regime). Single round-trip per model.
        var specs = new List<EngineConfigUpsertSpec>(4 + pendingCacheSpecs.Count)
        {
            new($"{modelPrefix}:CurrentEce",
                summary.CurrentEce.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Current live Expected Calibration Error for this ML model.",
                false),
            new($"{modelPrefix}:CalibrationDegrading",
                (signals.ThresholdExceeded || signals.TrendExceeded || signals.BaselineExceeded).ToString(),
                ConfigDataType.Bool,
                "Whether the model currently breaches any live calibration alert condition.",
                false),
            new($"{modelPrefix}:LastEvaluatedAt",
                nowUtc.ToString("O", CultureInfo.InvariantCulture),
                ConfigDataType.String,
                "UTC timestamp of the latest MLCalibrationMonitorWorker evaluation for this model.",
                false),
            new($"{modelPrefix}:EceStderr",
                summary.EceStderr.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Bootstrap-derived ECE stderr used to gate the trend signal.",
                false),
        };
        specs.AddRange(pendingCacheSpecs);

        await EngineConfigUpsert.BatchUpsertAsync(db, specs, ct);
    }

    private async Task<bool> QueueRetrainingIfNeededAsync(
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        CalibrationSummary summary,
        CalibrationSignals signals,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool activeRetrainExists = await db.Set<MLTrainingRun>()
            .AsNoTracking()
            .AnyAsync(run =>
                !run.IsDeleted &&
                run.Symbol == model.Symbol &&
                run.Timeframe == model.Timeframe &&
                (run.Status == RunStatus.Queued || run.Status == RunStatus.Running), ct);

        if (activeRetrainExists)
            return false;

        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var cooldownCutoff = nowUtc.AddHours(-settings.MinTimeBetweenRetrainsHours);
            bool recentAutoRetrain = await db.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(run =>
                    !run.IsDeleted &&
                    run.Symbol == model.Symbol &&
                    run.Timeframe == model.Timeframe &&
                    run.TriggerType == TriggerType.AutoDegrading &&
                    (run.CompletedAt ?? run.StartedAt) >= cooldownCutoff, ct);

            if (recentAutoRetrain)
                return false;
        }

        db.Set<MLTrainingRun>().Add(new MLTrainingRun
        {
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            TriggerType = TriggerType.AutoDegrading,
            Status = RunStatus.Queued,
            FromDate = nowUtc.AddDays(-settings.TrainingDataWindowDays),
            ToDate = nowUtc,
            StartedAt = nowUtc,
            LearnerArchitecture = model.LearnerArchitecture,
            DriftTriggerType = DriftTriggerType,
            DriftMetadataJson = JsonSerializer.Serialize(new
            {
                detector = "MLCalibrationMonitor",
                currentEce = Math.Round(summary.CurrentEce, 6),
                eceStderr = Math.Round(summary.EceStderr, 6),
                maxEce = Math.Round(settings.MaxEce, 6),
                previousEce = signals.PreviousEce is null ? (double?)null : Math.Round(signals.PreviousEce.Value, 6),
                baselineEce = signals.BaselineEce is null ? (double?)null : Math.Round(signals.BaselineEce.Value, 6),
                trendDelta = Math.Round(signals.TrendDelta, 6),
                baselineDelta = Math.Round(signals.BaselineDelta, 6),
                accuracy = Math.Round(summary.Accuracy, 6),
                meanConfidence = Math.Round(summary.MeanConfidence, 6),
                resolvedCount = summary.ResolvedCount,
                oldestOutcomeAt = summary.OldestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
                newestOutcomeAt = summary.NewestOutcomeAt.ToString("O", CultureInfo.InvariantCulture)
            }),
            Priority = 2,
            IsDeleted = false
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsLikelyUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: retrain queue race for {Symbol}/{Timeframe} resolved by the active-run unique index; another worker already queued the run.",
                WorkerName,
                model.Symbol,
                model.Timeframe);
            return false;
        }
    }

    private async Task<bool> UpsertAndDispatchAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorAlertState alertState,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = BuildDeduplicationKey(model.Id);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        AlertSeverity severity = DetermineSeverity(alertState, summary, signals, settings);
        DateTime? previousTriggeredAt = alert?.LastTriggeredAt;
        AlertSeverity? previousSeverity = alert?.Severity;

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
        else
        {
            alert.AlertType = AlertType.MLModelDegraded;
        }

        alert.Symbol = model.Symbol;
        alert.Severity = severity;
        alert.CooldownSeconds = settings.CooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = BuildAlertConditionJson(model, settings, summary, signals, alertState, nowUtc);

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
            previousTriggeredAt ??= alert.LastTriggeredAt;
            previousSeverity ??= alert.Severity;
            alert.AlertType = AlertType.MLModelDegraded;
            alert.Symbol = model.Symbol;
            alert.Severity = severity;
            alert.CooldownSeconds = settings.CooldownSeconds;
            alert.AutoResolvedAt = null;
            alert.ConditionJson = BuildAlertConditionJson(model, settings, summary, signals, alertState, nowUtc);
            await writeContext.SaveChangesAsync(ct);
        }

        bool severityEscalated = previousSeverity.HasValue && severity > previousSeverity.Value;
        if (previousTriggeredAt.HasValue &&
            !severityEscalated &&
            nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(settings.CooldownSeconds))
        {
            return false;
        }

        string message = alertState == MLCalibrationMonitorAlertState.Critical
            ? $"ML calibration is severely degraded for model {model.Id} ({model.Symbol}/{model.Timeframe}): ECE={summary.CurrentEce:F4}±{summary.EceStderr:F4}, accuracy={summary.Accuracy:P1}, meanConfidence={summary.MeanConfidence:F4}, trendDelta={signals.TrendDelta:F4}, baselineDelta={signals.BaselineDelta:F4}, n={summary.ResolvedCount}. Auto-degrading retrain review is recommended."
            : $"ML calibration is degraded for model {model.Id} ({model.Symbol}/{model.Timeframe}): ECE={summary.CurrentEce:F4}±{summary.EceStderr:F4}, accuracy={summary.Accuracy:P1}, meanConfidence={summary.MeanConfidence:F4}, trendDelta={signals.TrendDelta:F4}, baselineDelta={signals.BaselineDelta:F4}, n={summary.ResolvedCount}.";

        var dispatcher = ResolveAlertDispatcher(serviceProvider);
        if (dispatcher is null) return false;

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
                "{Worker}: failed to dispatch calibration-monitor alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return false;
        }
    }

    private static string BuildAlertConditionJson(
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorAlertState alertState,
        DateTime nowUtc)
    {
        return Truncate(
            JsonSerializer.Serialize(new
            {
                detector = "MLCalibrationMonitor",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                state = alertState == MLCalibrationMonitorAlertState.Critical ? "critical" : "warning",
                currentEce = Math.Round(summary.CurrentEce, 6),
                eceStderr = Math.Round(summary.EceStderr, 6),
                maxEce = Math.Round(settings.MaxEce, 6),
                previousEce = signals.PreviousEce is null ? (double?)null : Math.Round(signals.PreviousEce.Value, 6),
                baselineEce = signals.BaselineEce is null ? (double?)null : Math.Round(signals.BaselineEce.Value, 6),
                trendDelta = Math.Round(signals.TrendDelta, 6),
                baselineDelta = Math.Round(signals.BaselineDelta, 6),
                degradationDelta = Math.Round(settings.DegradationDelta, 6),
                regressionGuardK = Math.Round(settings.RegressionGuardK, 6),
                accuracy = Math.Round(summary.Accuracy, 6),
                meanConfidence = Math.Round(summary.MeanConfidence, 6),
                resolvedCount = summary.ResolvedCount,
                oldestOutcomeAt = summary.OldestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
                newestOutcomeAt = summary.NewestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
                thresholdExceeded = signals.ThresholdExceeded,
                trendExceeded = signals.TrendExceeded,
                baselineExceeded = signals.BaselineExceeded,
                trendStderrPasses = signals.TrendStderrPasses,
                evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            }),
            AlertConditionMaxLength);
    }

    private async Task<bool> ResolveAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = BuildDeduplicationKey(model.Id);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return false;

        var dispatcher = ResolveAlertDispatcher(serviceProvider);
        if (dispatcher is not null && alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "{Worker}: failed to auto-resolve calibration-monitor alert for model {ModelId}.",
                    WorkerName,
                    model.Id);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Increments the per-model consecutive-skip counter and dispatches a one-time
    /// <see cref="AlertType.DataQualityIssue"/> alert when the configured threshold is reached.
    /// Surfaces broken outcome-resolution pipelines that would otherwise leave a model silently
    /// skipped cycle after cycle. Counter is reset by <see cref="ResetStaleSkipCounterAsync"/>
    /// the moment fresh resolved logs return.
    /// </summary>
    private async Task TrackStaleAndAlertIfNeededAsync(
        IServiceProvider serviceProvider,
        DbContext db,
        IWriteApplicationDbContext writeContext,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string counterKey = $"MLCalibration:Model:{model.Id}:ConsecutiveSkips";
        int current = (int)(await LoadExistingMetricAsync(db, counterKey, ct) ?? 0);
        int next = current + 1;

        await EngineConfigUpsert.UpsertAsync(
            db,
            counterKey,
            next.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Consecutive cycles where MLCalibrationMonitorWorker found no fresh resolved logs for this model.",
            isHotReloadable: false,
            ct);

        if (next < settings.StaleSkipAlertThreshold) return;

        // Threshold reached: dispatch a single dedup'd alert. The dedup key prevents flooding
        // while the condition persists; the calibration-monitor's auto-resolve path clears it
        // once fresh logs return.
        var dispatcher = ResolveAlertDispatcher(serviceProvider);
        if (dispatcher is null) return;

        try
        {
            string dedupKey = StaleAlertDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
            bool exists = await db.Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == dedupKey && a.IsActive && !a.IsDeleted, ct);
            if (exists) return;

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                db, AlertCooldownDefaults.CK_MLMonitoring, AlertCooldownDefaults.Default_MLMonitoring, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                detector = "MLCalibrationMonitor",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                consecutiveSkips = next,
                threshold = settings.StaleSkipAlertThreshold,
                detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            });

            var alert = new Alert
            {
                AlertType = AlertType.DataQualityIssue,
                Severity = AlertSeverity.High,
                DeduplicationKey = dedupKey,
                CooldownSeconds = cooldownSec,
                ConditionJson = Truncate(conditionJson, AlertConditionMaxLength),
                Symbol = model.Symbol,
                IsActive = true,
            };

            db.Set<Alert>().Add(alert);
            await writeContext.SaveChangesAsync(ct);

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "MLCalibrationMonitor: model {0} ({1}/{2}) has been skipped {3} consecutive cycles (no fresh resolved prediction logs). Outcome-resolution pipeline may be stalled.",
                model.Id, model.Symbol, model.Timeframe, next);

            await dispatcher.DispatchAsync(alert, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to dispatch staleness alert for model {ModelId}.",
                WorkerName, model.Id);
        }
    }

    /// <summary>
    /// Resets the consecutive-skip counter and auto-resolves any active staleness alert when
    /// fresh resolved logs return. Called from the success path of <c>EvaluateModelAsync</c>.
    /// </summary>
    private static async Task ResetStaleSkipCounterAsync(DbContext db, long modelId, CancellationToken ct)
    {
        string counterKey = $"MLCalibration:Model:{modelId}:ConsecutiveSkips";
        int current = (int)(await LoadExistingMetricAsync(db, counterKey, ct) ?? 0);
        if (current <= 0) return;

        await EngineConfigUpsert.UpsertAsync(
            db,
            counterKey,
            "0",
            ConfigDataType.Int,
            "Consecutive cycles where MLCalibrationMonitorWorker found no fresh resolved logs for this model.",
            isHotReloadable: false,
            ct);
    }

    private async Task<bool> RaiseFleetDegradationAlertAsync(
        int evaluated, int warningCount, int criticalCount, double ratio, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            // Prefer the constructor-injected dispatcher; fall back to the DI-resolved one
            // for setups that register IAlertDispatcher in the service collection only.
            var dispatcher = _alertDispatcher ?? scope.ServiceProvider.GetService<IAlertDispatcher>();
            if (dispatcher is null) return false;

            var writeCtx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            bool exists = await writeCtx.Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == FleetAlertDeduplicationKey
                            && a.IsActive
                            && !a.IsDeleted, ct);
            if (exists) return false;

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                writeCtx, AlertCooldownDefaults.CK_MLEscalation, AlertCooldownDefaults.Default_MLEscalation, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                detector = "MLCalibrationMonitor",
                evaluated,
                warningCount,
                criticalCount,
                ratio = Math.Round(ratio, 4),
                detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            });

            var alert = new Alert
            {
                AlertType = AlertType.SystemicMLDegradation,
                Severity = AlertSeverity.High,
                DeduplicationKey = FleetAlertDeduplicationKey,
                CooldownSeconds = cooldownSec,
                ConditionJson = conditionJson,
                IsActive = true,
            };

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "MLCalibrationMonitor: {0}/{1} active models are degraded ({2:P1}). Investigate upstream calibration or labelling pipelines before relying on individual-model alerts.",
                warningCount + criticalCount, evaluated, ratio);

            // Persist the alert before dispatching so a queryable row is created even if the
            // dispatcher implementation is async-only and doesn't itself write to the DB.
            writeCtx.Set<Alert>().Add(alert);
            await writeCtx.SaveChangesAsync(ct);
            await dispatcher.DispatchAsync(alert, message, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to dispatch fleet degradation alert.", WorkerName);
            return false;
        }
    }

    private async Task<MLCalibrationMonitorWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_WindowDays, CK_MinSamples, CK_MaxEce,
            CK_DegradationDelta, CK_MaxResolvedPerModel, CK_LockTimeoutSeconds,
            CK_MinTimeBetweenRetrainsHours, CK_TrainingDataWindowDays,
            CK_ModelLockTimeoutSeconds, CK_RegressionGuardK, CK_BootstrapResamples,
            CK_FleetDegradationRatio, CK_PerRegimeMinSamples, CK_PerRegimeMaxSnapshots,
            CK_TimeDecayHalfLifeDays, CK_MinSamplesForTimeDecay,
            CK_TrendSmoothingWindow, CK_StaleSkipAlertThreshold,
            CK_BootstrapCacheStaleHours, CK_RetrainOnBaselineCritical,
            CK_MaxDegreeOfParallelism, CK_LongCycleWarnSeconds,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(config => keys.Contains(config.Key))
            .ToDictionaryAsync(config => config.Key, config => config.Value, ct);

        int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_MLMonitoring,
            AlertCooldownDefaults.Default_MLMonitoring,
            ct);

        return new MLCalibrationMonitorWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(ClampInt(
                GetInt(values, CK_PollSecs, DefaultPollSeconds),
                DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(GetInt(values, CK_WindowDays, DefaultWindowDays),
                DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinSamples: ClampInt(GetInt(values, CK_MinSamples, DefaultMinSamples),
                DefaultMinSamples, MinMinSamples, MaxMinSamples),
            MaxEce: ClampDoubleAllowingZero(GetDouble(values, CK_MaxEce, DefaultMaxEce),
                DefaultMaxEce, MinMaxEce, MaxMaxEce),
            DegradationDelta: ClampDoubleAllowingZero(GetDouble(values, CK_DegradationDelta, DefaultDegradationDelta),
                DefaultDegradationDelta, MinDegradationDelta, MaxDegradationDelta),
            MaxResolvedPerModel: ClampInt(GetInt(values, CK_MaxResolvedPerModel, DefaultMaxResolvedPerModel),
                DefaultMaxResolvedPerModel, MinMaxResolvedPerModel, MaxMaxResolvedPerModel),
            LockTimeoutSeconds: ClampIntAllowingZero(GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainingDataWindowDays, DefaultTrainingDataWindowDays),
                DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MinTimeBetweenRetrainsHours: ClampIntAllowingZero(GetInt(values, CK_MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            CooldownSeconds: Math.Max(1, cooldownSeconds),
            ModelLockTimeoutSeconds: ClampInt(GetInt(values, CK_ModelLockTimeoutSeconds, DefaultModelLockTimeoutSeconds),
                DefaultModelLockTimeoutSeconds, MinModelLockTimeoutSeconds, MaxModelLockTimeoutSeconds),
            RegressionGuardK: ClampDoubleAllowingZero(GetDouble(values, CK_RegressionGuardK, DefaultRegressionGuardK),
                DefaultRegressionGuardK, MinRegressionGuardK, MaxRegressionGuardK),
            BootstrapResamples: ClampIntAllowingZero(GetInt(values, CK_BootstrapResamples, DefaultBootstrapResamples),
                DefaultBootstrapResamples, MinBootstrapResamples, MaxBootstrapResamples),
            FleetDegradationRatio: ClampDoubleAllowingZero(GetDouble(values, CK_FleetDegradationRatio, DefaultFleetDegradationRatio),
                DefaultFleetDegradationRatio, MinFleetDegradationRatio, MaxFleetDegradationRatio),
            PerRegimeMinSamples: ClampInt(GetInt(values, CK_PerRegimeMinSamples, DefaultPerRegimeMinSamples),
                DefaultPerRegimeMinSamples, MinPerRegimeMinSamples, MaxPerRegimeMinSamples),
            PerRegimeMaxSnapshots: ClampInt(GetInt(values, CK_PerRegimeMaxSnapshots, DefaultPerRegimeMaxSnapshots),
                DefaultPerRegimeMaxSnapshots, MinPerRegimeMaxSnapshots, MaxPerRegimeMaxSnapshots),
            TimeDecayHalfLifeDays: ClampDoubleAllowingZero(GetDouble(values, CK_TimeDecayHalfLifeDays, DefaultTimeDecayHalfLifeDays),
                DefaultTimeDecayHalfLifeDays, MinTimeDecayHalfLifeDays, MaxTimeDecayHalfLifeDays),
            MinSamplesForTimeDecay: ClampIntAllowingZero(GetInt(values, CK_MinSamplesForTimeDecay, DefaultMinSamplesForTimeDecay),
                DefaultMinSamplesForTimeDecay, MinMinSamplesForTimeDecay, MaxMinSamplesForTimeDecay),
            TrendSmoothingWindow: ClampInt(GetInt(values, CK_TrendSmoothingWindow, DefaultTrendSmoothingWindow),
                DefaultTrendSmoothingWindow, MinTrendSmoothingWindow, MaxTrendSmoothingWindow),
            StaleSkipAlertThreshold: ClampInt(GetInt(values, CK_StaleSkipAlertThreshold, DefaultStaleSkipAlertThreshold),
                DefaultStaleSkipAlertThreshold, MinStaleSkipAlertThreshold, MaxStaleSkipAlertThreshold),
            BootstrapCacheStaleHours: ClampInt(GetInt(values, CK_BootstrapCacheStaleHours, DefaultBootstrapCacheStaleHours),
                DefaultBootstrapCacheStaleHours, MinBootstrapCacheStaleHours, MaxBootstrapCacheStaleHours),
            // Default off — baseline-only Critical alerts but does not retrain. Operators
            // who believe their training-time baseline is stale (or want all-Critical-retrains
            // for safety) can flip this to true.
            RetrainOnBaselineCritical: GetBool(values, CK_RetrainOnBaselineCritical, false),
            MaxDegreeOfParallelism: ClampInt(
                GetInt(values, CK_MaxDegreeOfParallelism, DefaultMaxDegreeOfParallelism),
                DefaultMaxDegreeOfParallelism, MinMaxDegreeOfParallelism, MaxMaxDegreeOfParallelism),
            LongCycleWarnSeconds: ClampIntAllowingZero(
                GetInt(values, CK_LongCycleWarnSeconds, DefaultLongCycleWarnSeconds),
                DefaultLongCycleWarnSeconds, MinLongCycleWarnSeconds, MaxLongCycleWarnSeconds));
    }

    private static MLCalibrationMonitorAlertState ResolveAlertState(
        double currentEce,
        CalibrationSignals signals,
        MLCalibrationMonitorWorkerSettings settings)
    {
        if ((settings.MaxEce > 0.0 && signals.ThresholdExceeded && currentEce > settings.MaxEce * SevereThresholdMultiplier) ||
            (settings.DegradationDelta > 0.0 && signals.TrendExceeded && signals.TrendDelta > settings.DegradationDelta * SevereThresholdMultiplier) ||
            (settings.DegradationDelta > 0.0 && signals.BaselineExceeded && signals.BaselineDelta > settings.DegradationDelta * SevereThresholdMultiplier))
        {
            return MLCalibrationMonitorAlertState.Critical;
        }

        return signals.ThresholdExceeded || signals.TrendExceeded || signals.BaselineExceeded
            ? MLCalibrationMonitorAlertState.Warning
            : MLCalibrationMonitorAlertState.None;
    }

    private static AlertSeverity DetermineSeverity(
        MLCalibrationMonitorAlertState alertState,
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorWorkerSettings settings)
    {
        if (alertState == MLCalibrationMonitorAlertState.Critical)
            return AlertSeverity.Critical;

        if ((settings.MaxEce > 0.0 && summary.CurrentEce >= settings.MaxEce * 1.25) ||
            (settings.DegradationDelta > 0.0 && Math.Max(signals.TrendDelta, signals.BaselineDelta) >= settings.DegradationDelta * 1.5))
        {
            return AlertSeverity.High;
        }

        return AlertSeverity.Medium;
    }

    private static bool HasExplicitProbability(MLModelPredictionLog log)
        => log.ServedCalibratedProbability.HasValue
        || log.CalibratedProbability.HasValue
        || log.RawProbability.HasValue;

    private static double? TryResolveBaselineEce(byte[]? modelBytes, MarketRegimeEnum? regime = null)
    {
        if (modelBytes is not { Length: > 0 })
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes);
            if (snapshot is null) return null;

            // Per-regime baseline takes precedence when training populated it; otherwise the
            // global ECE is the honest fallback. Operators see the same baseline for the
            // global row and any regimes the training pipeline didn't measure.
            if (regime is not null && snapshot.RegimeEce is { Count: > 0 } regimeMap &&
                regimeMap.TryGetValue(regime.Value.ToString(), out double regimeEce) &&
                double.IsFinite(regimeEce) && regimeEce >= 0.0)
            {
                return regimeEce;
            }

            if (!double.IsFinite(snapshot.Ece) || snapshot.Ece < 0.0)
                return null;

            return snapshot.Ece;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDeduplicationKey(long modelId)
        => AlertDeduplicationPrefix + modelId.ToString(CultureInfo.InvariantCulture);

    private static async Task<double?> LoadExistingMetricAsync(
        DbContext db,
        string key,
        CancellationToken ct)
    {
        var entry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Key == key, ct);

        if (entry?.Value is null)
            return null;

        return double.TryParse(
            entry.Value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Reads the most recent <paramref name="window"/> global (or per-regime) calibration
    /// audit rows for the given model and returns the mean of their <c>CurrentEce</c>. With
    /// <paramref name="window"/> = 1 this collapses to the prior cycle's ECE; higher values
    /// dampen single-cycle noise. Returns <c>null</c> when no rows exist (first cycle for
    /// that scope), so the caller can fall back to the legacy EngineConfig scalar or treat
    /// the trend signal as inert.
    /// </summary>
    /// <summary>
    /// Returns the cached bootstrap stderr for this (model, regime) scope when both the
    /// wall-clock staleness window AND the model's <c>RowVersion</c> match. Returns
    /// <c>null</c> on any mismatch (cache missing, time-stale, or model bytes replaced via
    /// retrain promotion) so the caller recomputes. Per-regime cache lives under
    /// <c>:Regime:{name}:</c> keys keyed identically to the global path.
    /// </summary>
    private static async Task<double?> LoadFreshBootstrapStderrAsync(
        DbContext db,
        long modelId,
        MarketRegimeEnum? regime,
        uint currentRowVersion,
        DateTime nowUtc,
        int staleHours,
        CancellationToken ct)
    {
        if (staleHours <= 0) return null;

        // Use the integer enum value, not the string name — renaming a regime enum member
        // (e.g. Trending → TrendingUp) keeps the underlying integer stable so cached entries
        // survive the rename. Stable across enum reordering only if the int values are
        // explicit; the codebase's MarketRegime is explicitly numbered for this reason.
        string scope = regime is null
            ? $"MLCalibration:Model:{modelId}"
            : $"MLCalibration:Model:{modelId}:Regime:{(int)regime.Value}";

        string stderrKey = $"{scope}:EceStderr";
        string computedAtKey = $"{scope}:EceStderrComputedAt";
        string rowVersionKey = $"{scope}:EceStderrModelRowVersion";

        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key == stderrKey || c.Key == computedAtKey || c.Key == rowVersionKey)
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        // RowVersion check: invalidate cache when model bytes change (champion swap, retrain
        // promotion). A wall-clock-fresh cache from a stale snapshot is worse than no cache.
        if (!rows.TryGetValue(rowVersionKey, out var rvRaw) ||
            !uint.TryParse(rvRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cachedRowVersion) ||
            cachedRowVersion != currentRowVersion)
        {
            return null;
        }

        // RoundtripKind on its own is sufficient for ISO-8601 "O" format strings written by
        // PersistBootstrapCacheAsync; combining with AssumeUniversal throws ArgumentException.
        if (!rows.TryGetValue(computedAtKey, out var atRaw) ||
            !DateTime.TryParse(atRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var computedAt))
        {
            return null;
        }

        if ((nowUtc - computedAt.ToUniversalTime()).TotalHours > staleHours)
            return null;

        if (!rows.TryGetValue(stderrKey, out var stderrRaw) ||
            !double.TryParse(stderrRaw, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var stderr) ||
            !double.IsFinite(stderr) || stderr < 0)
        {
            return null;
        }

        return stderr;
    }

    /// <summary>
    /// Appends bootstrap-cache specs (stderr, computed-at, RowVersion fingerprint) to the
    /// caller-supplied accumulator. Caller flushes the full set in one batched upsert at the
    /// end of the cycle, so a model with N matched regimes produces a single round-trip
    /// instead of (1 + N) per-scope round-trips.
    /// </summary>
    private static void AppendBootstrapCacheSpecs(
        List<EngineConfigUpsertSpec> pending,
        long modelId,
        MarketRegimeEnum? regime,
        double stderr,
        uint rowVersion,
        DateTime nowUtc)
    {
        // Use the integer enum value, not the string name — renaming a regime enum member
        // (e.g. Trending → TrendingUp) keeps the underlying integer stable so cached entries
        // survive the rename. Stable across enum reordering only if the int values are
        // explicit; the codebase's MarketRegime is explicitly numbered for this reason.
        string scope = regime is null
            ? $"MLCalibration:Model:{modelId}"
            : $"MLCalibration:Model:{modelId}:Regime:{(int)regime.Value}";

        pending.Add(new($"{scope}:EceStderr",
            stderr.ToString("F6", CultureInfo.InvariantCulture),
            ConfigDataType.Decimal,
            "Bootstrap-derived ECE stderr cached for the trend-signal stderr gate.",
            false));
        pending.Add(new($"{scope}:EceStderrComputedAt",
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            ConfigDataType.String,
            "UTC timestamp when the bootstrap-derived ECE stderr was last recomputed.",
            false));
        pending.Add(new($"{scope}:EceStderrModelRowVersion",
            rowVersion.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "MLModel.RowVersion at the time the cached stderr was computed; mismatches invalidate the cache.",
            false));
    }

    /// <summary>
    /// Loads every per-context override row that could apply to <paramref name="symbol"/>/
    /// <paramref name="timeframe"/> in a single round-trip. The four base wildcard tiers
    /// (<c>{symbol}:{tf}</c>, <c>{symbol}:*</c>, <c>*:{tf}</c>, <c>*:*</c>) are OR'd into
    /// one prefix scan; this also captures any regime-scoped variants that share those
    /// base prefixes (e.g. <c>{symbol}:{tf}:Regime:HighVolatility:{knob}</c>). Caller
    /// resolves precedence in memory via <see cref="ResolveOverride{T}"/>.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> LoadAllPerContextOverridesAsync(
        DbContext db, string symbol, Timeframe timeframe, CancellationToken ct)
    {
        string tfStr = timeframe.ToString();
        string p1 = $"MLCalibration:Override:{symbol}:{tfStr}:";
        string p2 = $"MLCalibration:Override:{symbol}:*:";
        string p3 = $"MLCalibration:Override:*:{tfStr}:";
        string p4 = "MLCalibration:Override:*:*:";

        return await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith(p1)
                     || c.Key.StartsWith(p2)
                     || c.Key.StartsWith(p3)
                     || c.Key.StartsWith(p4))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
    }

    /// <summary>
    /// Pre-loads per-context overrides once per unique <c>(Symbol, Timeframe)</c> in the
    /// candidate set, before fanning out to parallel model evaluation. Two model variants
    /// on the same context now share a single read instead of duplicating the prefix scan
    /// per iteration.
    /// </summary>
    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>>
        LoadOverridesByContextAsync(
            DbContext db, IReadOnlyList<ActiveModelCandidate> models, CancellationToken ct)
    {
        var contexts = new HashSet<(string, Timeframe)>(models.Count);
        foreach (var model in models)
        {
            contexts.Add((model.Symbol, model.Timeframe));
        }

        var result = new Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>>(contexts.Count);
        foreach (var (symbol, timeframe) in contexts)
        {
            result[(symbol, timeframe)] = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        }
        return result;
    }

    /// <summary>
    /// Once-per-process audit of override-key regime tokens. Scans every override key with
    /// a <c>:Regime:</c> segment and logs a warning when the segment doesn't parse to a
    /// <see cref="MarketRegimeEnum"/> value. Catches typos like <c>Regime:HighVol:</c>
    /// (vs. <c>HighVolatility</c>) at startup instead of having them silently fall through
    /// override tiers.
    /// </summary>
    private async Task ValidateOverrideRegimeNamesAsync(DbContext db, CancellationToken ct)
    {
        var keys = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLCalibration:Override:") && c.Key.Contains(":Regime:"))
            .Select(c => c.Key)
            .ToListAsync(ct);

        if (keys.Count == 0) return;

        var unmatched = new List<string>();
        foreach (var key in keys)
        {
            // Format: MLCalibration:Override:{Symbol}:{TF}:Regime:{Regime}:{Knob}
            // Find the segment immediately after "Regime" and try to parse it.
            var parts = key.Split(':');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] != "Regime") continue;
                if (!Enum.TryParse<MarketRegimeEnum>(parts[i + 1], ignoreCase: false, out _))
                    unmatched.Add(key);
                break;
            }
        }

        if (unmatched.Count > 0)
        {
            _logger.LogWarning(
                "{Worker}: {Count} override key(s) reference a regime name that doesn't match any MarketRegime enum value. These rows will silently fall through to the next override tier. Keys: {Keys}",
                WorkerName, unmatched.Count, string.Join(", ", unmatched));
        }
    }

    /// <summary>
    /// Walks the override-precedence chain (most-specific → least-specific) for a single
    /// setting and returns the first row that parses cleanly and clears <paramref name="validate"/>.
    /// When <paramref name="regime"/> is non-null the four regime-scoped tiers run first
    /// (<c>:Regime:{r}:</c> form) before the four regime-agnostic tiers, giving an 8-tier
    /// lookup that lets operators tighten knobs only in specific regimes (e.g.
    /// <c>*:*:Regime:HighVolatility:DegradationDelta</c>). Parsing or validation failures
    /// fall through to the next tier, not silent acceptance of bad data.
    /// </summary>
    internal static T ResolveOverride<T>(
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime,
        string settingName,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        T globalDefault)
        where T : struct
    {
        string tfStr = timeframe.ToString();
        // Regime-scoped tiers first, most specific to least specific. Each tier is built and
        // probed lazily — no upfront array allocation, and unused tiers never construct the
        // string at all.
        if (regime is not null)
        {
            string r = regime.Value.ToString();
            if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:{tfStr}:Regime:{r}:{settingName}", tryParse, validate, out var v1)) return v1;
            if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:*:Regime:{r}:{settingName}", tryParse, validate, out var v2)) return v2;
            if (TryResolveTier(overrides, $"MLCalibration:Override:*:{tfStr}:Regime:{r}:{settingName}", tryParse, validate, out var v3)) return v3;
            if (TryResolveTier(overrides, $"MLCalibration:Override:*:*:Regime:{r}:{settingName}", tryParse, validate, out var v4)) return v4;
        }

        if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:{tfStr}:{settingName}", tryParse, validate, out var v5)) return v5;
        if (TryResolveTier(overrides, $"MLCalibration:Override:{symbol}:*:{settingName}", tryParse, validate, out var v6)) return v6;
        if (TryResolveTier(overrides, $"MLCalibration:Override:*:{tfStr}:{settingName}", tryParse, validate, out var v7)) return v7;
        if (TryResolveTier(overrides, "MLCalibration:Override:*:*:" + settingName, tryParse, validate, out var v8)) return v8;

        return globalDefault;
    }

    private static bool TryResolveTier<T>(
        IReadOnlyDictionary<string, string> overrides,
        string key,
        Func<string, (bool ok, T value)> tryParse,
        Func<T, bool> validate,
        out T value)
        where T : struct
    {
        if (overrides.TryGetValue(key, out var raw) && raw is not null)
        {
            var (ok, parsed) = tryParse(raw);
            if (ok && validate(parsed))
            {
                value = parsed;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Clones <paramref name="settings"/> with every per-context overrideable knob resolved
    /// against <paramref name="overrides"/>. Pass a non-null <paramref name="regime"/> from
    /// per-regime evaluation paths so regime-scoped tiers take precedence.
    /// </summary>
    private static MLCalibrationMonitorWorkerSettings ApplyPerContextOverrides(
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        string symbol,
        Timeframe timeframe,
        MarketRegimeEnum? regime = null)
    {
        return settings with
        {
            MaxEce = ResolveOverride(overrides, symbol, timeframe, regime, "MaxEce",
                TryParseFiniteDouble,
                v => v >= MinMaxEce && v <= MaxMaxEce,
                settings.MaxEce),
            DegradationDelta = ResolveOverride(overrides, symbol, timeframe, regime, "DegradationDelta",
                TryParseFiniteDouble,
                v => v >= MinDegradationDelta && v <= MaxDegradationDelta,
                settings.DegradationDelta),
            RegressionGuardK = ResolveOverride(overrides, symbol, timeframe, regime, "RegressionGuardK",
                TryParseFiniteDouble,
                v => v >= MinRegressionGuardK && v <= MaxRegressionGuardK,
                settings.RegressionGuardK),
            BootstrapCacheStaleHours = ResolveOverride(overrides, symbol, timeframe, regime, "BootstrapCacheStaleHours",
                TryParseStrictInt,
                v => v >= 0 && v <= MaxBootstrapCacheStaleHours,
                settings.BootstrapCacheStaleHours),
            RetrainOnBaselineCritical = ResolveOverride(overrides, symbol, timeframe, regime, "RetrainOnBaselineCritical",
                TryParseBoolish,
                _ => true,
                settings.RetrainOnBaselineCritical),
        };
    }

    // Shared parsers for the override resolvers — strict (no decimal-to-int truncation),
    // invariant culture, finite-double-only.
    private static (bool ok, int value) TryParseStrictInt(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (true, v) : (false, 0);

    private static (bool ok, double value) TryParseFiniteDouble(string raw)
        => double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? (true, v) : (false, 0d);

    private static (bool ok, bool value) TryParseBoolish(string raw)
    {
        if (bool.TryParse(raw, out var b)) return (true, b);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return (true, i != 0);
        return (false, false);
    }

    /// <summary>Resolves the effective <c>BootstrapCacheStaleHours</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<int> ResolveBootstrapCacheStaleHoursAsync(
        DbContext db, string symbol, Timeframe timeframe, int globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "BootstrapCacheStaleHours",
            TryParseStrictInt,
            v => v >= 0 && v <= MaxBootstrapCacheStaleHours,
            globalDefault);
    }

    /// <summary>Resolves the effective <c>RetrainOnBaselineCritical</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<bool> ResolveRetrainOnBaselineCriticalAsync(
        DbContext db, string symbol, Timeframe timeframe, bool globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "RetrainOnBaselineCritical",
            TryParseBoolish,
            _ => true,
            globalDefault);
    }

    /// <summary>Resolves the effective <c>MaxEce</c> ceiling for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveMaxEceAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "MaxEce",
            TryParseFiniteDouble,
            v => v >= MinMaxEce && v <= MaxMaxEce,
            globalDefault);
    }

    /// <summary>Resolves the effective <c>DegradationDelta</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveDegradationDeltaAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "DegradationDelta",
            TryParseFiniteDouble,
            v => v >= MinDegradationDelta && v <= MaxDegradationDelta,
            globalDefault);
    }

    /// <summary>Resolves the effective <c>RegressionGuardK</c> for a (Symbol, Timeframe[, Regime]) tuple.</summary>
    internal static async Task<double> ResolveRegressionGuardKAsync(
        DbContext db, string symbol, Timeframe timeframe, double globalDefault, CancellationToken ct,
        MarketRegimeEnum? regime = null)
    {
        var overrides = await LoadAllPerContextOverridesAsync(db, symbol, timeframe, ct);
        return ResolveOverride(
            overrides, symbol, timeframe, regime, "RegressionGuardK",
            TryParseFiniteDouble,
            v => v >= MinRegressionGuardK && v <= MaxRegressionGuardK,
            globalDefault);
    }

    private static async Task<double?> LoadSmoothedPreviousEceAsync(
        DbContext db,
        long modelId,
        MarketRegimeEnum? regime,
        int window,
        CancellationToken ct)
    {
        if (window <= 0) return null;

        var query = db.Set<MLCalibrationLog>()
            .AsNoTracking()
            .Where(log => log.MLModelId == modelId
                       && !log.IsDeleted
                       && log.Outcome != "skipped_data"
                       && log.Outcome != "skipped_lock");

        query = regime is null
            ? query.Where(log => log.Regime == null)
            : query.Where(log => log.Regime == regime);

        var rows = await query
            .OrderByDescending(log => log.EvaluatedAt)
            .Take(window)
            .Select(log => log.CurrentEce)
            .ToListAsync(ct);

        if (rows.Count == 0) return null;
        double sum = 0.0;
        foreach (var v in rows) sum += v;
        return sum / rows.Count;
    }

    private static void EnqueueAudit(
        List<MLCalibrationLog> pending,
        ActiveModelCandidate model,
        MarketRegimeEnum? regime,
        string outcome,
        string reason,
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorAlertState alertState,
        DateTime? newestOutcomeAt,
        string diagnostics,
        DateTime evaluatedAt)
    {
        pending.Add(new MLCalibrationLog
        {
            MLModelId = model.Id,
            Symbol = model.Symbol,
            Timeframe = model.Timeframe,
            Regime = regime,
            EvaluatedAt = evaluatedAt,
            Outcome = Truncate(outcome, 32),
            Reason = Truncate(reason, 64),
            ResolvedSampleCount = summary.ResolvedCount,
            CurrentEce = summary.CurrentEce,
            PreviousEce = signals.PreviousEce,
            BaselineEce = signals.BaselineEce,
            TrendDelta = signals.TrendDelta,
            BaselineDelta = signals.BaselineDelta,
            Accuracy = summary.Accuracy,
            MeanConfidence = summary.MeanConfidence,
            EceStderr = summary.EceStderr,
            ThresholdExceeded = signals.ThresholdExceeded,
            TrendExceeded = signals.TrendExceeded,
            BaselineExceeded = signals.BaselineExceeded,
            AlertState = alertState switch
            {
                MLCalibrationMonitorAlertState.Critical => "critical",
                MLCalibrationMonitorAlertState.Warning => "warning",
                _ => "none",
            },
            NewestOutcomeAt = newestOutcomeAt,
            DiagnosticsJson = Truncate(diagnostics, MaxAuditDiagnosticsLength),
        });
    }

    private async Task FlushAuditsAsync(List<MLCalibrationLog> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();
            await db.Set<MLCalibrationLog>().AddRangeAsync(pending, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to persist {Count} calibration audit row(s); rows discarded.",
                WorkerName, pending.Count);
        }
        finally
        {
            pending.Clear();
        }
    }

    private static string BuildDiagnostics(params (string Key, object Value)[] pairs)
    {
        var dict = pairs.ToDictionary(p => p.Key, p => p.Value);
        return JsonSerializer.Serialize(dict);
    }

    private static string BuildDiagnosticsWithBins(
        CalibrationSummary summary,
        CalibrationSignals signals,
        MLCalibrationMonitorWorkerSettings settings,
        bool bootstrapCacheHit)
    {
        var bins = new List<object>(NumBins);
        for (int i = 0; i < NumBins; i++)
        {
            bins.Add(new
            {
                index = i,
                count = summary.BinCounts?[i] ?? 0,
                accuracy = Math.Round(summary.BinAccuracy?[i] ?? 0, 6),
                meanConfidence = Math.Round(summary.BinMeanConfidence?[i] ?? 0, 6),
            });
        }

        return JsonSerializer.Serialize(new
        {
            ece = Math.Round(summary.CurrentEce, 6),
            eceStderr = Math.Round(summary.EceStderr, 6),
            accuracy = Math.Round(summary.Accuracy, 6),
            meanConfidence = Math.Round(summary.MeanConfidence, 6),
            trendDelta = Math.Round(signals.TrendDelta, 6),
            baselineDelta = Math.Round(signals.BaselineDelta, 6),
            regressionGuardK = Math.Round(settings.RegressionGuardK, 6),
            trendStderrPasses = signals.TrendStderrPasses,
            thresholdExceeded = signals.ThresholdExceeded,
            trendExceeded = signals.TrendExceeded,
            baselineExceeded = signals.BaselineExceeded,
            bootstrapCacheHit,
            bins,
        });
    }

    private IAlertDispatcher? ResolveAlertDispatcher(IServiceProvider serviceProvider)
    {
        if (_alertDispatcher is not null) return _alertDispatcher;
        try
        {
            return serviceProvider.GetService<IAlertDispatcher>();
        }
        catch
        {
            return null;
        }
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
        if (value <= 0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampIntAllowingZero(int value, int fallback, int min, int max)
    {
        if (value < 0) return fallback;
        return Math.Min(Math.Max(value, min), max);
    }

    private static double ClampDoubleAllowingZero(double value, double fallback, double min, double max)
    {
        if (!double.IsFinite(value) || value < 0.0) return fallback;
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

    private static bool IsLikelyUniqueViolation(DbUpdateException ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private static void DetachIfAdded(DbContext db, Alert alert)
    {
        var entry = db.Entry(alert);
        if (entry.State is EntityState.Added or EntityState.Modified)
            entry.State = EntityState.Detached;
    }
}

internal sealed record MLCalibrationMonitorWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int WindowDays,
    int MinSamples,
    double MaxEce,
    double DegradationDelta,
    int MaxResolvedPerModel,
    int LockTimeoutSeconds,
    int TrainingDataWindowDays,
    int MinTimeBetweenRetrainsHours,
    int CooldownSeconds,
    int ModelLockTimeoutSeconds,
    double RegressionGuardK,
    int BootstrapResamples,
    double FleetDegradationRatio,
    int PerRegimeMinSamples,
    int PerRegimeMaxSnapshots,
    double TimeDecayHalfLifeDays,
    int MinSamplesForTimeDecay,
    int TrendSmoothingWindow,
    int StaleSkipAlertThreshold,
    int BootstrapCacheStaleHours,
    bool RetrainOnBaselineCritical,
    int MaxDegreeOfParallelism,
    int LongCycleWarnSeconds);

internal sealed record MLCalibrationMonitorCycleResult(
    MLCalibrationMonitorWorkerSettings Settings,
    string? SkippedReason,
    int CandidateModelCount,
    int EvaluatedModelCount,
    int WarningModelCount,
    int CriticalModelCount,
    int RetrainingQueuedCount,
    int DispatchedAlertCount,
    int ResolvedAlertCount,
    int FailedModelCount,
    bool FleetAlertDispatched)
{
    public static MLCalibrationMonitorCycleResult Skipped(MLCalibrationMonitorWorkerSettings settings, string reason)
        => new(
            settings,
            reason,
            CandidateModelCount: 0,
            EvaluatedModelCount: 0,
            WarningModelCount: 0,
            CriticalModelCount: 0,
            RetrainingQueuedCount: 0,
            DispatchedAlertCount: 0,
            ResolvedAlertCount: 0,
            FailedModelCount: 0,
            FleetAlertDispatched: false);
}

internal enum MLCalibrationMonitorAlertState
{
    None = 0,
    Warning = 1,
    Critical = 2
}
