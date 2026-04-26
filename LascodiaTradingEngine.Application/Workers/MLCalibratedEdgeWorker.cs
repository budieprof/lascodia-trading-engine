using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Measures the live realized edge of each active production ML model using the same
/// threshold-relative edge contract the trainers optimize on holdout data.
/// </summary>
/// <remarks>
/// The legacy implementation rebuilt a coarse expected-value proxy by temporally joining
/// prediction timestamps to closed positions. That can mismatch trades, ignore the exact
/// served probability/threshold, and drift away from the model's actual decision surface.
///
/// This worker instead evaluates resolved <see cref="MLModelPredictionLog"/> rows directly:
/// <code>
/// edge_i = sign_i × |served_probability_i − threshold_i| × |actual_magnitude_pips_i|
/// liveEV = mean(edge_i)
/// </code>
/// where <c>sign_i</c> is +1 when the thresholded served probability matched the actual
/// market direction, and −1 otherwise. The result is a live economic edge measure in
/// pips-weighted probability-margin units, consistent with the trainer's expected-value
/// objective while remaining grounded in real resolved outcomes.
///
/// Only logs with real threshold-driving evidence are allowed to influence the metric:
/// rows must carry at least one of the exact probability fields or the exact logged
/// decision threshold. Confidence-only legacy rows are excluded because they cannot
/// reconstruct the served edge honestly once thresholds drift over time.
///
/// When live edge is non-positive the worker raises a durable <see cref="AlertType.MLModelDegraded"/>
/// alert and queues an auto-degrading retrain unless one is already active, a recent
/// auto-degrading retrain is still inside cooldown, or the cycle has exhausted its
/// global retrain budget. Alerts auto-resolve when fresh live edge rises back above the
/// warning floor.
///
/// A separate <see cref="AlertType.MLMonitoringStale"/> alert fires when a model is
/// repeatedly skipped due to missing informative logs, which surfaces broken prediction
/// -logging pipelines or silently-retired models that would otherwise drop off the
/// dashboard unnoticed. Distinct alert type so dashboards can route "model is degrading"
/// and "we cannot evaluate the model anymore" to different operator queues.
/// </remarks>
public sealed partial class MLCalibratedEdgeWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLCalibratedEdgeWorker);

    private const string DistributedLockKey = "workers:ml-calibrated-edge:cycle";
    private const string ModelLockKeyPrefix = "workers:ml-calibrated-edge:model:";
    private const string AlertDeduplicationPrefix = "ml-calibrated-edge:";
    private const string StaleMonitoringDeduplicationPrefix = "ml-calibrated-edge-stale:";
    private const string ChronicAlertDeduplicationPrefix = "ml-calibrated-edge-chronic:";
    private const int AlertConditionMaxLength = 1000;
    private const string DriftTriggerType = "CalibratedEdge";

    // AutoDegrading retrains are urgent (live model is losing money on its own decision
    // surface) but should not preempt manual or strategy-driven training. Priority 2 sits
    // above background retrains (5+) and below explicit operator queues (1).
    private const int AutoDegradingRetrainPriority = 2;

    // Knob names that are overridable per (Symbol, Timeframe[, Regime]) or per Model:{id}.
    // The override-token validator flags any override key whose final segment isn't in
    // this set so operators learn about typos like "WarnEvPipps" instead of seeing the
    // row silently fall through every tier.
    private static readonly string[] ValidOverrideKnobs =
    [
        "WarnEvPips",
        "MinSamples",
        "MaxResolvedPerModel",
        "TrainingDataWindowDays",
        "MinTimeBetweenRetrainsHours",
        "ConsecutiveSkipAlertThreshold",
        "MaxRetrainsPerCycle",
        "MaxAlertsPerCycle",
        "RegressionGuardK",
        "BootstrapResamples",
        "ChronicCriticalThreshold",
        "SuppressRetrainOnChronic",
    ];

    private const string CK_Enabled = "MLEdge:Enabled";
    private const string CK_PollSecs = "MLEdge:PollIntervalSeconds";
    private const string CK_WindowDays = "MLEdge:WindowDays";
    private const string CK_MinSamples = "MLEdge:MinSamples";
    private const string CK_WarnEv = "MLEdge:WarnEvPips";
    private const string CK_MaxResolvedPerModel = "MLEdge:MaxResolvedPerModel";
    private const string CK_LockTimeoutSeconds = "MLEdge:LockTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLEdge:MinTimeBetweenRetrainsHours";
    private const string CK_TrainingDataWindowDays = "MLTraining:TrainingDataWindowDays";
    private const string CK_MaxRetrainsPerCycle = "MLEdge:MaxRetrainsPerCycle";
    private const string CK_ConsecutiveSkipAlertThreshold = "MLEdge:ConsecutiveSkipAlertThreshold";
    private const string CK_MaxDegreeOfParallelism = "MLEdge:MaxDegreeOfParallelism";
    private const string CK_LongCycleWarnSeconds = "MLEdge:LongCycleWarnSeconds";
    private const string CK_ChronicCriticalThreshold = "MLEdge:ChronicCriticalThreshold";
    private const string CK_SuppressRetrainOnChronic = "MLEdge:SuppressRetrainOnChronic";
    private const string CK_MaxAlertsPerCycle = "MLEdge:MaxAlertsPerCycle";
    private const string CK_RegressionGuardK = "MLEdge:RegressionGuardK";
    private const string CK_BootstrapResamples = "MLEdge:BootstrapResamples";

    private const int DefaultPollSeconds = 60 * 60;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 7 * 24 * 60 * 60;

    private const int DefaultWindowDays = 30;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 3650;

    private const int DefaultMinSamples = 10;
    private const int MinMinSamples = 3;
    private const int MaxMinSamples = 10_000;

    private const double DefaultWarnEvPips = 0.5;
    private const double MinWarnEvPips = 0.0;
    private const double MaxWarnEvPips = 10_000.0;

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

    private const int DefaultMaxRetrainsPerCycle = 5;
    private const int MinMaxRetrainsPerCycle = 0;
    private const int MaxMaxRetrainsPerCycle = 1000;

    private const int DefaultConsecutiveSkipAlertThreshold = 5;
    private const int MinConsecutiveSkipAlertThreshold = 1;
    private const int MaxConsecutiveSkipAlertThreshold = 1000;

    // Bounded in-process concurrency for per-model evaluation. Default 1 preserves
    // historical strictly-sequential semantics; bumping fans out to N concurrent
    // (model, lock-acquire, query, audit-flush) chains, each in its own DI scope.
    private const int DefaultMaxDegreeOfParallelism = 1;
    private const int MinMaxDegreeOfParallelism = 1;
    private const int MaxMaxDegreeOfParallelism = 16;

    // Wall-clock cycle warning threshold. Cycle-level distributed lock is held for
    // the duration of one cycle; if cycle wall-time approaches the lock TTL the
    // lock can be re-acquired by another replica before this one finishes.
    private const int DefaultLongCycleWarnSeconds = 300;
    private const int MinLongCycleWarnSeconds = 0;
    private const int MaxLongCycleWarnSeconds = 24 * 60 * 60;

    // Number of consecutive Critical-state cycles before a model is flagged as a
    // retirement candidate. At that point a separate alert fires and (when
    // SuppressRetrainOnChronic is true, default) further auto-retraining is blocked
    // so the pipeline doesn't burn cycles on a model that won't converge.
    private const int DefaultChronicCriticalThreshold = 4;
    private const int MinChronicCriticalThreshold = 1;
    private const int MaxChronicCriticalThreshold = 1000;

    // Per-cycle trip-alert dispatch cap. Protects against alert storms during
    // fleet-wide events (label pipeline outage, prediction-log outage). 0 disables
    // the budget. Auto-resolves are NOT counted — recovery signals always reach
    // operators.
    private const int DefaultMaxAlertsPerCycle = 50;
    private const int MinMaxAlertsPerCycle = 0;
    private const int MaxMaxAlertsPerCycle = 10_000;

    // K-sigma significance bar on the EV signal. With non-zero bootstrap stderr this
    // gates Critical state on `EV + K·stderr ≤ 0` — i.e. EV negative WITH confidence.
    // Default 1.0 (one-sigma); set ~3.0 for stricter Bonferroni-like coverage. Auto-
    // bypassed when bootstrap is disabled (BootstrapResamples = 0) or stderr lands at 0.
    private const double DefaultRegressionGuardK = 1.0;
    private const double MinRegressionGuardK = 0.0;
    private const double MaxRegressionGuardK = 5.0;

    private const int DefaultBootstrapResamples = 200;
    private const int MinBootstrapResamples = 0;
    private const int MaxBootstrapResamples = 5_000;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCalibratedEdgeWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly IMLCalibratedEdgeEvaluator _evaluator;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    // Hashed signature of the unmatched-tokens set last reported by the override-key
    // validator. Same dedup primitive as MLCalibrationMonitorWorker. 0 = empty/clean.
    private long _lastUnmatchedTokensSignature;

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture);

    private readonly record struct ModelEvaluationOutcome(
        bool Evaluated,
        MLCalibratedEdgeAlertState AlertState,
        bool RetrainingQueued,
        bool AlertDispatched,
        bool AlertResolved,
        bool RetrainBackpressureSkipped,
        bool StaleMonitoringAlertDispatched,
        string? SkipReason)
    {
        public static ModelEvaluationOutcome Skipped(string reason, bool staleMonitoringAlertDispatched = false)
            => new(false, MLCalibratedEdgeAlertState.None, false, false, false, false, staleMonitoringAlertDispatched, reason);
    }

    private sealed class CycleContext
    {
        public required IReadOnlyDictionary<long, List<MLModelPredictionLog>> LogsByModelId { get; init; }
        public required Dictionary<long, int> SkipStreaksByModelId { get; init; }
        public required HashSet<long> LegacyAliasModelIds { get; init; }
        public required HashSet<long> ActiveStaleMonitoringAlertModelIds { get; init; }
        public required RetrainBudget RetrainBudget { get; init; }
    }

    private sealed class RetrainBudget(int maxRetrains)
    {
        private int _remaining = Math.Max(0, maxRetrains);

        public bool HasCapacity => _remaining > 0;

        public bool TryConsume()
        {
            if (_remaining <= 0)
                return false;

            _remaining--;
            return true;
        }
    }

    /// <summary>
    /// Mutable per-cycle state shared by every iteration of the parallel model loop.
    /// Wrapped in one heap object so the parallel lambda captures `ctx` instead of N
    /// individual locals; counters atomic-incremented through `ref ctx.Field`.
    /// </summary>
    /// <remarks>
    /// Public mutable fields are deliberate — the only way to provide stable addresses
    /// for <c>Interlocked.Increment(ref ctx.Field)</c> from outside. Class is private
    /// and scoped to one in-flight cycle (cycles are serialised by the cycle-level
    /// distributed lock), so the open-mutable shape never escapes.
    /// </remarks>
    private sealed class CycleIteration
    {
        public required MLCalibratedEdgeWorkerSettings Settings;
        public DateTime NowUtc;
        public required IReadOnlyDictionary<long, List<MLModelPredictionLog>> LogsByModelId;
        public required IReadOnlyDictionary<long, int> SkipStreaksByModelId;
        public required IReadOnlySet<long> LegacyAliasModelIds;
        public ConcurrentDictionary<long, byte> ActiveStaleMonitoringAlertModelIds = new();
        public required IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>> OverridesByContext;

        // Counters mutated atomically by Interlocked through `ref ctx.Field`.
        public int EvaluatedModels;
        public int WarningModels;
        public int CriticalModels;
        public int RetrainingQueued;
        public int RetrainBackpressureSkipped;
        public int DispatchedAlerts;
        public int ResolvedAlerts;
        public int StaleMonitoringAlerts;
        public int FailedModels;
        public int RemainingModels;

        // Per-cycle retrain budget (existing behaviour) and alert budget (new).
        public int RemainingRetrainBudget;
        public int RemainingAlertBudget;
        public int AlertsSuppressedByBudget;
    }

    public MLCalibratedEdgeWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCalibratedEdgeWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null,
        IMLCalibratedEdgeEvaluator? evaluator = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
        // Default to the standard evaluator when DI doesn't supply one. Tests that
        // construct the worker directly inherit the production math without ceremony.
        _evaluator = evaluator ?? new MLCalibratedEdgeEvaluator();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Measures live ML calibrated edge from resolved prediction logs, raises durable degradation alerts when edge turns negative or marginal, and queues retraining for truly negative edge.",
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
                    _metrics?.MLCalibratedEdgeCycleDurationMs.Record(durationMs);

                    // Long-cycle guard: warn when wall-time approaches the lock TTL window.
                    // The cycle-level distributed lock is held for the entire cycle, so a
                    // long cycle risks the lock expiring and another replica re-acquiring
                    // before this one finishes. The duration histogram with the parallelism
                    // tag is the source-of-truth alerting signal; this log is the operator's
                    // prompt to verify the IDistributedLock TTL is at least this long.
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
                            "{Worker}: candidates={Candidates}, evaluated={Evaluated}, warning={Warning}, critical={Critical}, retrainingQueued={Queued}, retrainBackpressureSkipped={BackpressureSkipped}, alertsDispatched={Dispatched}, alertsResolved={Resolved}, staleMonitoringAlerts={StaleMonitoringAlerts}, failed={Failed}.",
                            WorkerName,
                            result.CandidateModelCount,
                            result.EvaluatedModelCount,
                            result.WarningModelCount,
                            result.CriticalModelCount,
                            result.RetrainingQueuedCount,
                            result.RetrainBackpressureSkippedCount,
                            result.DispatchedAlertCount,
                            result.ResolvedAlertCount,
                            result.StaleMonitoringAlertCount,
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
                        new KeyValuePair<string, object?>("reason", "ml_calibrated_edge_cycle"));
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

    internal async Task<MLCalibratedEdgeCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var writeContext = serviceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();
        var settings = await LoadSettingsAsync(db, ct);

        if (!settings.Enabled)
        {
            _metrics?.MLCalibratedEdgeCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "disabled"));
            return MLCalibratedEdgeCycleResult.Skipped(settings, "disabled");
        }

        if (_distributedLock is null)
        {
            _metrics?.MLCalibratedEdgeLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate edge-monitor cycles are possible in multi-instance deployments.",
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
                _metrics?.MLCalibratedEdgeLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLCalibratedEdgeCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLCalibratedEdgeCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics?.MLCalibratedEdgeLockAttempts.Add(
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

    private async Task<MLCalibratedEdgeCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        MLCalibratedEdgeWorkerSettings settings,
        CancellationToken ct)
    {
        var models = await db.Set<MLModel>()
            .AsNoTracking()
            .Where(model =>
                model.IsActive &&
                !model.IsDeleted &&
                !model.IsMamlInitializer &&
                !model.IsMetaLearner)
            .Select(model => new ActiveModelCandidate(
                model.Id,
                model.Symbol,
                model.Timeframe,
                model.LearnerArchitecture))
            .ToListAsync(ct);

        if (models.Count == 0)
        {
            _metrics?.MLCalibratedEdgeCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return MLCalibratedEdgeCycleResult.Skipped(settings, "no_active_models");
        }

        var legacyAliasModelIds = ResolveLegacyAliasModelIds(models);
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        // Single broad-prefix scan over override rows; bucket per (Symbol, Timeframe)
        // in-memory and run the override-token validator over the same list.
        var allOverrideRows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLEdge:Override:"))
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value))
            .ToListAsync(ct);
        ValidateOverrideTokens(allOverrideRows);
        var overridesByContext = BucketOverridesByContext(models, allOverrideRows);

        var logsByModelId = await BatchLoadResolvedLogsAsync(db, models, settings, nowUtc, ct);
        var skipStreaks = await BatchLoadSkipStreaksAsync(db, models, ct);
        var activeStaleAlertModelIds = await BatchLoadActiveStaleMonitoringAlertModelIdsAsync(db, models, ct);

        var ctx = new CycleIteration
        {
            Settings = settings,
            NowUtc = nowUtc,
            LogsByModelId = logsByModelId,
            SkipStreaksByModelId = skipStreaks,
            LegacyAliasModelIds = legacyAliasModelIds,
            OverridesByContext = overridesByContext,
            RemainingModels = models.Count,
            RemainingRetrainBudget = settings.MaxRetrainsPerCycle,
            // 0 = "disabled" (effectively unlimited budget); int.MaxValue removes the
            // gate without branching at the dispatcher call site.
            RemainingAlertBudget = settings.MaxAlertsPerCycle > 0
                ? settings.MaxAlertsPerCycle
                : int.MaxValue,
        };
        foreach (var id in activeStaleAlertModelIds) ctx.ActiveStaleMonitoringAlertModelIds.TryAdd(id, 0);

        int parallelism = Math.Clamp(settings.MaxDegreeOfParallelism, 1, MaxMaxDegreeOfParallelism);

        await Parallel.ForEachAsync(
            models,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (model, modelCt) => await EvaluateOneIterationAsync(ctx, model, modelCt))
            .ConfigureAwait(false);

        int evaluatedModels = ctx.EvaluatedModels;
        int warningModels = ctx.WarningModels;
        int criticalModels = ctx.CriticalModels;
        int retrainingQueued = ctx.RetrainingQueued;
        int retrainBackpressureSkipped = ctx.RetrainBackpressureSkipped;
        int dispatchedAlerts = ctx.DispatchedAlerts;
        int resolvedAlerts = ctx.ResolvedAlerts;
        int staleMonitoringAlerts = ctx.StaleMonitoringAlerts;
        int failedModels = ctx.FailedModels;

        return new MLCalibratedEdgeCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: models.Count,
            EvaluatedModelCount: evaluatedModels,
            WarningModelCount: warningModels,
            CriticalModelCount: criticalModels,
            RetrainingQueuedCount: retrainingQueued,
            RetrainBackpressureSkippedCount: retrainBackpressureSkipped,
            DispatchedAlertCount: dispatchedAlerts,
            ResolvedAlertCount: resolvedAlerts,
            StaleMonitoringAlertCount: staleMonitoringAlerts,
            FailedModelCount: failedModels);
    }

    private static HashSet<long> ResolveLegacyAliasModelIds(IReadOnlyList<ActiveModelCandidate> models)
    {
        return models
            .GroupBy(model => (model.Symbol, model.Timeframe))
            .Select(group => group.OrderByDescending(model => model.Id).First().Id)
            .ToHashSet();
    }

    private static async Task<Dictionary<long, List<MLModelPredictionLog>>> BatchLoadResolvedLogsAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        MLCalibratedEdgeWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var modelIds = models.Select(model => model.Id).ToList();
        var lookbackCutoff = nowUtc.AddDays(-settings.WindowDays);

        var rows = await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(log =>
                modelIds.Contains(log.MLModelId) &&
                !log.IsDeleted &&
                log.OutcomeRecordedAt != null &&
                log.OutcomeRecordedAt >= lookbackCutoff &&
                log.ActualDirection != null &&
                log.ActualMagnitudePips != null)
            .ToListAsync(ct);

        var byModel = new Dictionary<long, List<MLModelPredictionLog>>();
        foreach (var row in rows)
        {
            if (!byModel.TryGetValue(row.MLModelId, out var bucket))
            {
                bucket = [];
                byModel[row.MLModelId] = bucket;
            }
            bucket.Add(row);
        }

        foreach (var bucket in byModel.Values)
        {
            bucket.Sort((a, b) =>
            {
                int cmp = Nullable.Compare(b.OutcomeRecordedAt, a.OutcomeRecordedAt);
                return cmp != 0 ? cmp : b.Id.CompareTo(a.Id);
            });
            if (bucket.Count > settings.MaxResolvedPerModel)
                bucket.RemoveRange(settings.MaxResolvedPerModel, bucket.Count - settings.MaxResolvedPerModel);
        }

        return byModel;
    }

    private static async Task<Dictionary<long, int>> BatchLoadSkipStreaksAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        CancellationToken ct)
    {
        var keys = models.Select(model => SkipStreakKey(model.Id)).ToList();
        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(config => keys.Contains(config.Key))
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(ct);

        var streaks = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            if (TryParseModelIdFromSkipKey(row.Key, out var modelId)
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var streak))
            {
                streaks[modelId] = streak;
            }
        }
        return streaks;
    }

    private static async Task<HashSet<long>> BatchLoadActiveStaleMonitoringAlertModelIdsAsync(
        DbContext db,
        IReadOnlyList<ActiveModelCandidate> models,
        CancellationToken ct)
    {
        var dedupKeys = models
            .Select(model => StaleMonitoringDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture))
            .ToList();

        var rows = await db.Set<Alert>()
            .AsNoTracking()
            .Where(alert => !alert.IsDeleted
                         && alert.IsActive
                         && alert.DeduplicationKey != null
                         && dedupKeys.Contains(alert.DeduplicationKey))
            .Select(alert => alert.DeduplicationKey!)
            .ToListAsync(ct);

        var modelIds = new HashSet<long>();
        foreach (var key in rows)
        {
            if (key.Length <= StaleMonitoringDeduplicationPrefix.Length)
                continue;
            var span = key.AsSpan(StaleMonitoringDeduplicationPrefix.Length);
            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                modelIds.Add(id);
        }
        return modelIds;
    }

    /// <summary>
    /// Per-iteration entry point invoked by the parallel loop. Owns one DI scope per
    /// iteration so the model evaluation never crosses an EF state boundary, and
    /// re-throws cancellation cleanly so shutdown doesn't masquerade as model failure.
    /// </summary>
    private async ValueTask EvaluateOneIterationAsync(
        CycleIteration ctx, ActiveModelCandidate model, CancellationToken modelCt)
    {
        await using var modelScope = _scopeFactory.CreateAsyncScope();
        var modelWriteCtx = modelScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var modelDb = modelWriteCtx.GetDbContext();

        try
        {
            // Refresh the worker heartbeat before each model evaluation. Long cycles
            // (large fleet / DOP=1) would otherwise leave the health monitor without
            // a signal until cycle end.
            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

            var outcome = await EvaluateModelAsync(
                modelScope.ServiceProvider, modelWriteCtx, modelDb, model, ctx, modelCt);

            if (outcome.Evaluated)
            {
                Interlocked.Increment(ref ctx.EvaluatedModels);
                if (outcome.AlertState == MLCalibratedEdgeAlertState.Warning)
                    Interlocked.Increment(ref ctx.WarningModels);
                else if (outcome.AlertState == MLCalibratedEdgeAlertState.Critical)
                    Interlocked.Increment(ref ctx.CriticalModels);
                if (outcome.RetrainingQueued) Interlocked.Increment(ref ctx.RetrainingQueued);
                if (outcome.RetrainBackpressureSkipped) Interlocked.Increment(ref ctx.RetrainBackpressureSkipped);
                if (outcome.AlertDispatched) Interlocked.Increment(ref ctx.DispatchedAlerts);
                if (outcome.AlertResolved) Interlocked.Increment(ref ctx.ResolvedAlerts);
            }
            else
            {
                _metrics?.MLCalibratedEdgeModelsSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", outcome.SkipReason ?? "skipped"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                if (outcome.StaleMonitoringAlertDispatched)
                    Interlocked.Increment(ref ctx.StaleMonitoringAlerts);
            }
        }
        catch (OperationCanceledException) when (modelCt.IsCancellationRequested)
        {
            // Shutdown propagation, not a model failure. Re-throw so Parallel.ForEachAsync
            // surfaces it and the ExecuteAsync loop honours stoppingToken.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ctx.FailedModels);
            _metrics?.MLCalibratedEdgeModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "model_error"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("exception_type", ex.GetType().Name));
            _logger.LogWarning(
                ex,
                "{Worker}: failed to evaluate live edge for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref ctx.RemainingModels);
            _healthMonitor?.RecordBacklogDepth(WorkerName, remaining);
        }
    }

    private async Task<ModelEvaluationOutcome> EvaluateModelAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        CycleIteration ctx,
        CancellationToken ct)
    {
        // Apply per-context overrides (9-tier hierarchy: Model:{id} → 4 regime-scoped
        // tiers (when applicable; this worker has no regime concept yet) → 4 regime-
        // agnostic tiers → defaults). Only the regime-agnostic + Model:{id} tiers
        // apply here today; the helper supports the regime form for forward compat.
        var overrides = ctx.OverridesByContext.TryGetValue((model.Symbol, model.Timeframe), out var ctxOverrides)
            ? ctxOverrides
            : new Dictionary<string, string>();
        var settings = ApplyPerContextOverrides(ctx.Settings, overrides, model.Symbol, model.Timeframe, modelId: model.Id);
        var nowUtc = ctx.NowUtc;

        ctx.LogsByModelId.TryGetValue(model.Id, out var resolvedLogs);

        if (resolvedLogs is null || resolvedLogs.Count == 0)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, ctx, "no_recent_resolved_predictions", nowUtc, ct);

        var informativeLogs = new List<MLModelPredictionLog>(resolvedLogs.Count);
        foreach (var log in resolvedLogs)
        {
            if (_evaluator.IsEdgeInformative(log)) informativeLogs.Add(log);
        }

        if (informativeLogs.Count < settings.MinSamples)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, ctx, "insufficient_informative_history", nowUtc, ct);

        var samples = new List<CalibratedEdgeSample>(informativeLogs.Count);
        foreach (var log in informativeLogs)
        {
            if (_evaluator.TryCreateSample(log, out var sample)) samples.Add(sample);
        }

        if (samples.Count < settings.MinSamples)
            return await HandleSkipAsync(
                serviceProvider, writeContext, db, model, settings, ctx, "insufficient_informative_history", nowUtc, ct);

        var summary = _evaluator.ComputeSummary(samples, settings.BootstrapResamples, model.Id);
        var alertState = _evaluator.ResolveAlertState(
            summary.ExpectedValuePips,
            summary.EvStderr,
            settings.WarnExpectedValuePips,
            settings.RegressionGuardK);
        string stateTag = alertState switch
        {
            MLCalibratedEdgeAlertState.Critical => "critical",
            MLCalibratedEdgeAlertState.Warning => "warning",
            _ => "healthy"
        };

        _metrics?.MLCalibratedEdgeModelsEvaluated.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("state", stateTag));
        _metrics?.MLCalibratedEdgeExpectedValuePips.Record(
            summary.ExpectedValuePips,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("state", stateTag));
        _metrics?.MLCalibratedEdgeResolvedSamples.Record(
            summary.ResolvedCount,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

        bool writeLegacyAlias = ctx.LegacyAliasModelIds.Contains(model.Id);
        await PersistSummaryAsync(db, model, summary, writeLegacyAlias, resetSkipStreak: true, nowUtc, ct);

        // Track per-model consecutive Critical-state cycles. Returns the new streak
        // length (0 if state isn't Critical). When the threshold is crossed, dispatches
        // a retirement-candidate alert; recovery to non-Critical resets and auto-resolves.
        int chronicStreak = await TrackChronicCriticalAndAlertIfNeededAsync(
            serviceProvider, writeContext, db, model, settings, alertState, summary, nowUtc, ct);
        bool inChronicState = chronicStreak >= settings.ChronicCriticalThreshold;

        bool retrainingQueued = false;
        bool retrainBackpressureSkipped = false;
        bool alertDispatched = false;
        bool alertResolved = false;

        if (alertState == MLCalibratedEdgeAlertState.Critical)
        {
            // Chronic-tripper retrain suppression: model has been Critical for ≥
            // ChronicCriticalThreshold cycles, repeated retraining is unlikely to
            // recover, so block further automatic retrains until operator intervenes.
            if (settings.SuppressRetrainOnChronic && inChronicState)
            {
                retrainBackpressureSkipped = true;
                _metrics?.MLCalibratedEdgeRetrainingQueued.Add(
                    0,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("outcome", "chronic_suppressed"));
            }
            else if (Interlocked.Decrement(ref ctx.RemainingRetrainBudget) < 0)
            {
                // Restore the budget so other models still see capacity correctly.
                Interlocked.Increment(ref ctx.RemainingRetrainBudget);
                retrainBackpressureSkipped = true;
                _metrics?.MLCalibratedEdgeRetrainingQueued.Add(
                    0,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("outcome", "backpressure"));
            }
            else
            {
                retrainingQueued = await QueueRetrainingIfNeededAsync(
                    db, model, settings, summary, nowUtc, ct);
                if (retrainingQueued)
                {
                    _metrics?.MLCalibratedEdgeRetrainingQueued.Add(
                        1,
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                        new KeyValuePair<string, object?>("outcome", "queued"));
                }
                else
                {
                    // Didn't actually queue (active run, cooldown, race) — restore the
                    // budget consumption so it's available for a later model this cycle.
                    Interlocked.Increment(ref ctx.RemainingRetrainBudget);
                }
            }
        }

        if (alertState != MLCalibratedEdgeAlertState.None)
        {
            alertDispatched = await UpsertAndDispatchAlertAsync(
                serviceProvider,
                writeContext,
                db,
                model,
                settings,
                summary,
                alertState,
                ctx,
                nowUtc,
                ct);

            if (alertDispatched)
            {
                _metrics?.MLCalibratedEdgeAlertsDispatched.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("state", stateTag));
                _metrics?.MLCalibratedEdgeAlertTransitions.Add(
                    1,
                    new KeyValuePair<string, object?>("transition", "dispatched"));
            }
        }
        else
        {
            alertResolved = await ResolveAlertAsync(
                serviceProvider, writeContext, db, model, AlertDeduplicationPrefix, nowUtc, ct);
            if (alertResolved)
            {
                _metrics?.MLCalibratedEdgeAlertTransitions.Add(
                    1,
                    new KeyValuePair<string, object?>("transition", "resolved"));
            }
        }

        if (ctx.ActiveStaleMonitoringAlertModelIds.ContainsKey(model.Id))
        {
            bool staleResolved = await ResolveAlertAsync(
                serviceProvider, writeContext, db, model, StaleMonitoringDeduplicationPrefix, nowUtc, ct);
            if (staleResolved)
            {
                ctx.ActiveStaleMonitoringAlertModelIds.TryRemove(model.Id, out _);
                _metrics?.MLCalibratedEdgeAlertTransitions.Add(
                    1,
                    new KeyValuePair<string, object?>("transition", "stale_monitoring_resolved"));
            }
        }

        _logger.LogDebug(
            "{Worker}: model {ModelId} ({Symbol}/{Timeframe}) liveEdge={ExpectedValue:F4}, winRate={WinRate:P1}, meanGap={MeanGap:F4}, meanAbsMagnitude={MeanMagnitude:F2}, samples={Samples}, state={State}.",
            WorkerName,
            model.Id,
            model.Symbol,
            model.Timeframe,
            summary.ExpectedValuePips,
            summary.WinRate,
            summary.MeanProbabilityGap,
            summary.MeanAbsMagnitudePips,
            summary.ResolvedCount,
            stateTag);

        return new ModelEvaluationOutcome(
            Evaluated: true,
            AlertState: alertState,
            RetrainingQueued: retrainingQueued,
            AlertDispatched: alertDispatched,
            AlertResolved: alertResolved,
            RetrainBackpressureSkipped: retrainBackpressureSkipped,
            StaleMonitoringAlertDispatched: false,
            SkipReason: null);
    }

    private async Task<ModelEvaluationOutcome> HandleSkipAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        CycleIteration ctx,
        string skipReason,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int newStreak = ctx.SkipStreaksByModelId.TryGetValue(model.Id, out var existing) ? existing + 1 : 1;

        await EngineConfigUpsert.BatchUpsertAsync(
            db,
            new List<EngineConfigUpsertSpec>
            {
                new(SkipStreakKey(model.Id),
                    newStreak.ToString(CultureInfo.InvariantCulture),
                    ConfigDataType.Int,
                    "Consecutive cycles in which the calibrated-edge worker could not evaluate this model due to missing or insufficient informative prediction logs.",
                    false),
                new($"MLEdge:Model:{model.Id}:LastSkipReason",
                    skipReason,
                    ConfigDataType.String,
                    "Reason the calibrated-edge worker last skipped this model.",
                    false),
                new($"MLEdge:Model:{model.Id}:LastEvaluatedAt",
                    nowUtc.ToString("O", CultureInfo.InvariantCulture),
                    ConfigDataType.String,
                    "UTC timestamp of the latest MLCalibratedEdgeWorker evaluation attempt for this model.",
                    false),
            },
            ct);

        bool staleAlertDispatched = false;
        if (newStreak >= settings.ConsecutiveSkipAlertThreshold)
        {
            staleAlertDispatched = await UpsertAndDispatchStaleMonitoringAlertAsync(
                serviceProvider, writeContext, db, model, settings, skipReason, newStreak, nowUtc, ct);
        }

        return ModelEvaluationOutcome.Skipped(skipReason, staleAlertDispatched);
    }

    private static Task PersistSummaryAsync(
        DbContext db,
        ActiveModelCandidate model,
        LiveEdgeSummary summary,
        bool writeLegacyAlias,
        bool resetSkipStreak,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string modelPrefix = $"MLEdge:Model:{model.Id}";
        var specs = new List<EngineConfigUpsertSpec>
        {
            new($"{modelPrefix}:ExpectedValue",
                summary.ExpectedValuePips.ToString("F4", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Live realized calibrated edge for this ML model in pips-weighted probability-margin units.",
                false),
            new($"{modelPrefix}:ResolvedCount",
                summary.ResolvedCount.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Resolved informative prediction log count contributing to the latest ML calibrated edge measurement.",
                false),
            new($"{modelPrefix}:WinRate",
                summary.WinRate.ToString("F4", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Win rate implied by thresholding the served probability against the logged decision threshold on informative resolved logs.",
                false),
            new($"{modelPrefix}:MeanProbabilityGap",
                summary.MeanProbabilityGap.ToString("F4", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Mean absolute served-probability distance from the logged decision threshold on informative resolved logs.",
                false),
            new($"{modelPrefix}:MeanAbsMagnitudePips",
                summary.MeanAbsMagnitudePips.ToString("F4", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Mean absolute realized move magnitude in pips across informative resolved logs used by MLCalibratedEdgeWorker.",
                false),
            new($"{modelPrefix}:LastEvaluatedAt",
                nowUtc.ToString("O", CultureInfo.InvariantCulture),
                ConfigDataType.String,
                "UTC timestamp of the latest MLCalibratedEdgeWorker evaluation for this model.",
                false),
        };

        if (resetSkipStreak)
        {
            specs.Add(new EngineConfigUpsertSpec(
                SkipStreakKey(model.Id),
                "0",
                ConfigDataType.Int,
                "Consecutive cycles in which the calibrated-edge worker could not evaluate this model due to missing or insufficient informative prediction logs.",
                false));
        }

        if (writeLegacyAlias)
        {
            string legacyPrefix = $"MLEdge:{model.Symbol}:{model.Timeframe}";
            specs.Add(new EngineConfigUpsertSpec(
                $"{legacyPrefix}:ExpectedValue",
                summary.ExpectedValuePips.ToString("F4", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Legacy alias for the most recently created active model's live realized calibrated edge in this symbol/timeframe context.",
                false));
            specs.Add(new EngineConfigUpsertSpec(
                $"{legacyPrefix}:ResolvedCount",
                summary.ResolvedCount.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Legacy alias for the most recently created active model's informative resolved-sample count in this symbol/timeframe context.",
                false));
            specs.Add(new EngineConfigUpsertSpec(
                $"{legacyPrefix}:ModelId",
                model.Id.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "ML model id currently backing the legacy MLEdge symbol/timeframe aliases (most recently created active model).",
                false));
        }

        return EngineConfigUpsert.BatchUpsertAsync(db, specs, ct);
    }

    private async Task<bool> QueueRetrainingIfNeededAsync(
        DbContext db,
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        LiveEdgeSummary summary,
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
                detector = "MLCalibratedEdge",
                expectedValuePips = Math.Round(summary.ExpectedValuePips, 6),
                winRate = Math.Round(summary.WinRate, 6),
                meanProbabilityGap = Math.Round(summary.MeanProbabilityGap, 6),
                meanAbsMagnitudePips = Math.Round(summary.MeanAbsMagnitudePips, 6),
                resolvedCount = summary.ResolvedCount,
                oldestOutcomeAt = summary.OldestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
                newestOutcomeAt = summary.NewestOutcomeAt.ToString("O", CultureInfo.InvariantCulture)
            }),
            Priority = AutoDegradingRetrainPriority,
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
        MLCalibratedEdgeWorkerSettings settings,
        LiveEdgeSummary summary,
        MLCalibratedEdgeAlertState alertState,
        CycleIteration? ctx,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = AlertDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        AlertSeverity severity = _evaluator.DetermineSeverity(alertState, summary, settings.WarnExpectedValuePips);
        DateTime? previousTriggeredAt = alert?.LastTriggeredAt;
        AlertSeverity? previousSeverity = alert?.Severity;
        string conditionJson = Truncate(BuildEdgeAlertConditionJson(model, settings, summary, alertState, nowUtc), AlertConditionMaxLength);

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

        ApplyEdgeAlertFields(alert, model.Symbol, severity, settings.CooldownSeconds, conditionJson);

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
            ApplyEdgeAlertFields(alert, model.Symbol, severity, settings.CooldownSeconds, conditionJson);
            await writeContext.SaveChangesAsync(ct);
        }

        bool severityEscalated = previousSeverity.HasValue && severity > previousSeverity.Value;
        if (previousTriggeredAt.HasValue &&
            !severityEscalated &&
            nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(settings.CooldownSeconds))
        {
            return false;
        }

        string message = alertState == MLCalibratedEdgeAlertState.Critical
            ? $"ML calibrated edge is non-positive for model {model.Id} ({model.Symbol}/{model.Timeframe}): EV={summary.ExpectedValuePips:F4}, winRate={summary.WinRate:P1}, meanGap={summary.MeanProbabilityGap:F4}, n={summary.ResolvedCount}. Auto-degrading retrain review is recommended."
            : $"ML calibrated edge is marginal for model {model.Id} ({model.Symbol}/{model.Timeframe}): EV={summary.ExpectedValuePips:F4} below warning floor {settings.WarnExpectedValuePips:F4}, winRate={summary.WinRate:P1}, meanGap={summary.MeanProbabilityGap:F4}, n={summary.ResolvedCount}.";

        var dispatcher = serviceProvider.GetRequiredService<IAlertDispatcher>();

        // Per-cycle alert budget: the Alert row is already upserted (so dashboards see
        // the state) but dispatching the notification is rate-limited to protect
        // against alert storms during fleet-wide degradation events. Auto-resolves
        // are exempt — recovery signals always reach operators.
        if (ctx is not null && Interlocked.Decrement(ref ctx.RemainingAlertBudget) < 0)
        {
            Interlocked.Increment(ref ctx.AlertsSuppressedByBudget);
            _metrics?.MLCalibratedEdgeCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "alert_budget_exhausted"));
            return false;
        }

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
                "{Worker}: failed to dispatch calibrated-edge alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return false;
        }
    }

    private async Task<bool> UpsertAndDispatchStaleMonitoringAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        string skipReason,
        int consecutiveSkips,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = StaleMonitoringDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        DateTime? previousTriggeredAt = alert?.LastTriggeredAt;
        string conditionJson = Truncate(JsonSerializer.Serialize(new
        {
            detector = "MLCalibratedEdge",
            reason = "stale_monitoring",
            modelId = model.Id,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            consecutiveSkips,
            lastSkipReason = skipReason,
            consecutiveSkipAlertThreshold = settings.ConsecutiveSkipAlertThreshold,
            evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
        }), AlertConditionMaxLength);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.MLMonitoringStale,
                DeduplicationKey = deduplicationKey,
                IsActive = true
            };
            db.Set<Alert>().Add(alert);
        }
        else
        {
            alert.AlertType = AlertType.MLMonitoringStale;
        }

        ApplyEdgeAlertFields(alert, model.Symbol, AlertSeverity.High, settings.CooldownSeconds, conditionJson);

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
            alert.AlertType = AlertType.MLMonitoringStale;
            ApplyEdgeAlertFields(alert, model.Symbol, AlertSeverity.High, settings.CooldownSeconds, conditionJson);
            await writeContext.SaveChangesAsync(ct);
        }

        if (previousTriggeredAt.HasValue &&
            nowUtc - NormalizeUtc(previousTriggeredAt.Value) < TimeSpan.FromSeconds(settings.CooldownSeconds))
        {
            return false;
        }

        string message = $"ML calibrated-edge monitoring stale for model {model.Id} ({model.Symbol}/{model.Timeframe}): {consecutiveSkips} consecutive skipped cycles ({skipReason}). The prediction-logging pipeline may be broken or the model is no longer being served.";
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
                "{Worker}: failed to dispatch stale-monitoring alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName,
                model.Id,
                model.Symbol,
                model.Timeframe);
            return false;
        }
    }

    private async Task<bool> ResolveAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        string deduplicationPrefix,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = deduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        if (alert is null)
            return false;

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
                    "{Worker}: failed to auto-resolve alert {DeduplicationKey} for model {ModelId}.",
                    WorkerName,
                    deduplicationKey,
                    model.Id);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

    private static string BuildEdgeAlertConditionJson(
        ActiveModelCandidate model,
        MLCalibratedEdgeWorkerSettings settings,
        LiveEdgeSummary summary,
        MLCalibratedEdgeAlertState alertState,
        DateTime nowUtc)
        => JsonSerializer.Serialize(new
        {
            detector = "MLCalibratedEdge",
            reason = alertState == MLCalibratedEdgeAlertState.Critical ? "edge_negative" : "edge_warning",
            modelId = model.Id,
            symbol = model.Symbol,
            timeframe = model.Timeframe.ToString(),
            expectedValuePips = Math.Round(summary.ExpectedValuePips, 6),
            warnExpectedValuePips = Math.Round(settings.WarnExpectedValuePips, 6),
            winRate = Math.Round(summary.WinRate, 6),
            meanProbabilityGap = Math.Round(summary.MeanProbabilityGap, 6),
            meanAbsMagnitudePips = Math.Round(summary.MeanAbsMagnitudePips, 6),
            resolvedCount = summary.ResolvedCount,
            oldestOutcomeAt = summary.OldestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
            newestOutcomeAt = summary.NewestOutcomeAt.ToString("O", CultureInfo.InvariantCulture),
            evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
        });

    private static void ApplyEdgeAlertFields(
        Alert alert,
        string symbol,
        AlertSeverity severity,
        int cooldownSeconds,
        string conditionJson)
    {
        alert.Symbol = symbol;
        alert.Severity = severity;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = conditionJson;
    }

    private async Task<MLCalibratedEdgeWorkerSettings> LoadSettingsAsync(DbContext db, CancellationToken ct)
    {
        string[] keys =
        [
            CK_Enabled,
            CK_PollSecs,
            CK_WindowDays,
            CK_MinSamples,
            CK_WarnEv,
            CK_MaxResolvedPerModel,
            CK_LockTimeoutSeconds,
            CK_MinTimeBetweenRetrainsHours,
            CK_TrainingDataWindowDays,
            CK_MaxRetrainsPerCycle,
            CK_ConsecutiveSkipAlertThreshold,
            CK_MaxDegreeOfParallelism,
            CK_LongCycleWarnSeconds,
            CK_ChronicCriticalThreshold,
            CK_SuppressRetrainOnChronic,
            CK_MaxAlertsPerCycle,
            CK_RegressionGuardK,
            CK_BootstrapResamples,
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

        return new MLCalibratedEdgeWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, true),
            PollInterval: TimeSpan.FromSeconds(ClampInt(
                GetInt(values, CK_PollSecs, DefaultPollSeconds),
                DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(
                GetInt(values, CK_WindowDays, DefaultWindowDays),
                DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinSamples: ClampInt(
                GetInt(values, CK_MinSamples, DefaultMinSamples),
                DefaultMinSamples, MinMinSamples, MaxMinSamples),
            WarnExpectedValuePips: ClampDoubleAllowingZero(
                GetDouble(values, CK_WarnEv, DefaultWarnEvPips),
                DefaultWarnEvPips, MinWarnEvPips, MaxWarnEvPips),
            MaxResolvedPerModel: ClampInt(
                GetInt(values, CK_MaxResolvedPerModel, DefaultMaxResolvedPerModel),
                DefaultMaxResolvedPerModel, MinMaxResolvedPerModel, MaxMaxResolvedPerModel),
            LockTimeoutSeconds: ClampIntAllowingZero(
                GetInt(values, CK_LockTimeoutSeconds, DefaultLockTimeoutSeconds),
                DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            TrainingDataWindowDays: ClampInt(
                GetInt(values, CK_TrainingDataWindowDays, DefaultTrainingDataWindowDays),
                DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MinTimeBetweenRetrainsHours: ClampIntAllowingZero(
                GetInt(values, CK_MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            MaxRetrainsPerCycle: ClampIntAllowingZero(
                GetInt(values, CK_MaxRetrainsPerCycle, DefaultMaxRetrainsPerCycle),
                DefaultMaxRetrainsPerCycle, MinMaxRetrainsPerCycle, MaxMaxRetrainsPerCycle),
            ConsecutiveSkipAlertThreshold: ClampInt(
                GetInt(values, CK_ConsecutiveSkipAlertThreshold, DefaultConsecutiveSkipAlertThreshold),
                DefaultConsecutiveSkipAlertThreshold, MinConsecutiveSkipAlertThreshold, MaxConsecutiveSkipAlertThreshold),
            CooldownSeconds: Math.Max(1, cooldownSeconds),
            MaxDegreeOfParallelism: ClampInt(
                GetInt(values, CK_MaxDegreeOfParallelism, DefaultMaxDegreeOfParallelism),
                DefaultMaxDegreeOfParallelism, MinMaxDegreeOfParallelism, MaxMaxDegreeOfParallelism),
            LongCycleWarnSeconds: ClampIntAllowingZero(
                GetInt(values, CK_LongCycleWarnSeconds, DefaultLongCycleWarnSeconds),
                DefaultLongCycleWarnSeconds, MinLongCycleWarnSeconds, MaxLongCycleWarnSeconds),
            ChronicCriticalThreshold: ClampInt(
                GetInt(values, CK_ChronicCriticalThreshold, DefaultChronicCriticalThreshold),
                DefaultChronicCriticalThreshold, MinChronicCriticalThreshold, MaxChronicCriticalThreshold),
            SuppressRetrainOnChronic: GetBool(values, CK_SuppressRetrainOnChronic, true),
            MaxAlertsPerCycle: ClampIntAllowingZero(
                GetInt(values, CK_MaxAlertsPerCycle, DefaultMaxAlertsPerCycle),
                DefaultMaxAlertsPerCycle, MinMaxAlertsPerCycle, MaxMaxAlertsPerCycle),
            RegressionGuardK: ClampDoubleAllowingZero(
                GetDouble(values, CK_RegressionGuardK, DefaultRegressionGuardK),
                DefaultRegressionGuardK, MinRegressionGuardK, MaxRegressionGuardK),
            BootstrapResamples: ClampIntAllowingZero(
                GetInt(values, CK_BootstrapResamples, DefaultBootstrapResamples),
                DefaultBootstrapResamples, MinBootstrapResamples, MaxBootstrapResamples));
    }

    private static string SkipStreakKey(long modelId)
        => $"MLEdge:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:ConsecutiveSkips";

    private static bool TryParseModelIdFromSkipKey(string key, out long modelId)
    {
        modelId = 0;
        if (!key.StartsWith("MLEdge:Model:", StringComparison.Ordinal)
            || !key.EndsWith(":ConsecutiveSkips", StringComparison.Ordinal))
            return false;

        int idStart = "MLEdge:Model:".Length;
        int idEnd = key.Length - ":ConsecutiveSkips".Length;
        return long.TryParse(
            key.AsSpan(idStart, idEnd - idStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out modelId);
    }

    private static async Task<int?> LoadExistingIntConfigAsync(DbContext db, string key, CancellationToken ct)
    {
        var entry = await db.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Key == key, ct);

        return entry?.Value is not null
            && int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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

internal sealed record MLCalibratedEdgeWorkerSettings(
    bool Enabled,
    TimeSpan PollInterval,
    int WindowDays,
    int MinSamples,
    double WarnExpectedValuePips,
    int MaxResolvedPerModel,
    int LockTimeoutSeconds,
    int TrainingDataWindowDays,
    int MinTimeBetweenRetrainsHours,
    int MaxRetrainsPerCycle,
    int ConsecutiveSkipAlertThreshold,
    int CooldownSeconds,
    int MaxDegreeOfParallelism,
    int LongCycleWarnSeconds,
    int ChronicCriticalThreshold,
    bool SuppressRetrainOnChronic,
    int MaxAlertsPerCycle,
    double RegressionGuardK,
    int BootstrapResamples);

internal sealed record MLCalibratedEdgeCycleResult(
    MLCalibratedEdgeWorkerSettings Settings,
    string? SkippedReason,
    int CandidateModelCount,
    int EvaluatedModelCount,
    int WarningModelCount,
    int CriticalModelCount,
    int RetrainingQueuedCount,
    int RetrainBackpressureSkippedCount,
    int DispatchedAlertCount,
    int ResolvedAlertCount,
    int StaleMonitoringAlertCount,
    int FailedModelCount)
{
    public static MLCalibratedEdgeCycleResult Skipped(MLCalibratedEdgeWorkerSettings settings, string reason)
        => new(
            settings,
            reason,
            CandidateModelCount: 0,
            EvaluatedModelCount: 0,
            WarningModelCount: 0,
            CriticalModelCount: 0,
            RetrainingQueuedCount: 0,
            RetrainBackpressureSkippedCount: 0,
            DispatchedAlertCount: 0,
            ResolvedAlertCount: 0,
            StaleMonitoringAlertCount: 0,
            FailedModelCount: 0);
}

public enum MLCalibratedEdgeAlertState
{
    None = 0,
    Warning = 1,
    Critical = 2
}
