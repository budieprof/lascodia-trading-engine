using System.Collections.Concurrent;
using System.Collections.Frozen;
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
/// <remarks>
/// <para><b>File layout (partial-class split).</b></para>
/// <list type="bullet">
///   <item><c>MLCalibrationMonitorWorker.cs</c> — cycle orchestration, settings, persist, alerts, retraining, fleet alert, this docblock.</item>
///   <item><c>MLCalibrationMonitorWorker.Overrides.cs</c> — per-context override loading, resolution, application, validation.</item>
///   <item><c>MLCalibrationMonitorWorker.Signals.cs</c> — sample creation, calibration math, signal/severity classification.</item>
///   <item><c>MLCalibrationMonitorWorker.Bootstrap.cs</c> — bootstrap-stderr cache + computation.</item>
///   <item><c>MLCalibrationMonitorWorker.Audit.cs</c> — audit pipeline + diagnostics builders.</item>
/// </list>
/// <para>
/// All five files share one <c>sealed partial class</c>. Field declarations and the
/// <c>CycleIteration</c> nested class live in this file. Add new helpers to the partial
/// whose concern matches; resist creating a sixth file unless a clearly distinct concern
/// emerges.
/// </para>
/// </remarks>
public sealed partial class MLCalibrationMonitorWorker : BackgroundService
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
    private const string CK_ChronicCriticalThreshold = "MLCalibration:ChronicCriticalThreshold";
    private const string CK_SuppressRetrainOnChronic = "MLCalibration:SuppressRetrainOnChronic";
    private const string CK_MaxAlertsPerCycle = "MLCalibration:MaxAlertsPerCycle";
    private const string ChronicAlertDeduplicationPrefix = "ml-calibration-monitor-chronic:";
    private const string CK_MaxDegreeOfParallelism = "MLCalibration:MaxDegreeOfParallelism";
    private const string CK_LongCycleWarnSeconds = "MLCalibration:LongCycleWarnSeconds";
    private const string CK_AuditFlushMode = "MLCalibration:AuditFlushMode";
    private const AuditFlushMode DefaultAuditFlushMode = AuditFlushMode.PerModel;
    // Operator-facing wire-format aliases. Add an alias by extending the source dict;
    // legacy values continue to resolve. Case-insensitive comparison so "Cycle" /
    // "CYCLE" / "cycle" all work. FrozenDictionary trades higher init cost for faster
    // lookup — appropriate for a read-only table consulted on every settings load.
    private static readonly FrozenDictionary<string, AuditFlushMode> AuditFlushModeAliases =
        new Dictionary<string, AuditFlushMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["per_model"] = AuditFlushMode.PerModel,
            ["cycle"] = AuditFlushMode.Cycle,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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

    // Number of consecutive Critical-state cycles before a model is flagged as a
    // retirement candidate (chronic-tripper). At that point a separate alert fires and
    // SuppressRetrainOnChronic (default true) blocks further automatic retrains so the
    // pipeline doesn't burn cycles re-training a model that won't converge.
    private const int DefaultChronicCriticalThreshold = 4;
    private const int MinChronicCriticalThreshold = 1;
    private const int MaxChronicCriticalThreshold = 1000;

    // Maximum trip-alert dispatches per cycle. Protects against alert storms during
    // fleet-wide events (data outage, label-pipeline failure). 0 disables the budget.
    // Auto-resolve dispatches are NOT counted against this budget — recovery signals
    // should always reach operators.
    private const int DefaultMaxAlertsPerCycle = 50;
    private const int MinMaxAlertsPerCycle = 0;
    private const int MaxMaxAlertsPerCycle = 10_000;

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLCalibrationMonitorWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;
    private readonly IAlertDispatcher? _alertDispatcher;
    private readonly IMLCalibrationSignalEvaluator _signalEvaluator;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;
    // Hashed signature of the unmatched-tokens (regime + knob) set last reported. Used
    // to dedup the per-cycle validator's Warning logs: same signature = same situation
    // = no log. 0 is reserved for the empty (clean) state. Resets to 0 on worker
    // restart so operators see the typo on first cycle after restart even if the same
    // set was reported before. Hashed (vs. joined-string) to avoid per-cycle string
    // allocation in the steady state where the set rarely changes.
    private long _lastUnmatchedTokensSignature;

    // FNV-1a signature of the last unrecognized MLCalibration:AuditFlushMode value
    // observed. Symmetric with `_lastUnmatchedTokensSignature`: same dedup primitive
    // serves both validators. 0 = "no unknown value ever seen" (or recovered).
    private long _lastUnknownAuditFlushModeSignature;

    // Knob names that are overridable per (Symbol, Timeframe[, Regime]). The validator
    // flags any override key whose final segment isn't in this set so operators learn
    // about typos like `MaxEcce` instead of seeing the row silently fall through every
    // tier. Update this set whenever a new knob is added to ApplyPerContextOverrides.
    private static readonly string[] ValidOverrideKnobs =
    [
        "MaxEce",
        "DegradationDelta",
        "RegressionGuardK",
        "BootstrapCacheStaleHours",
        "RetrainOnBaselineCritical",
    ];

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture,
        byte[]? ModelBytes,
        uint RowVersion);

    // CalibrationSample / CalibrationSummary / CalibrationSignals live in
    // MLCalibrationSignalTypes.cs (internal namespace types) so IMLCalibrationSignalEvaluator
    // can return them without leaking through worker-private scope.

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

    /// <summary>
    /// Mutable per-cycle state shared by every iteration of the parallel model loop.
    /// Wrapped in one heap object so the parallel lambda captures `ctx` instead of N
    /// individual locals; counters are atomic-incremented through `ref ctx.Field`.
    /// </summary>
    /// <remarks>
    /// Public mutable fields are deliberate — they're the only way to provide stable
    /// addresses for <c>Interlocked.Increment(ref ctx.Field)</c> from the iteration
    /// body. Wrapping them in property accessors that internally Interlocked would
    /// produce uglier call sites without changing semantics. The class is private and
    /// scoped to one in-flight cycle (cycles are serialised by the cycle-level
    /// distributed lock), so the open-mutable shape never escapes the worker.
    /// </remarks>
    private sealed class CycleIteration
    {
        public required MLCalibrationMonitorWorkerSettings Settings;
        public DateTime NowUtc;
        public required IReadOnlyDictionary<long, DateTime?> LastNewestOutcome;
        public required IReadOnlyDictionary<(string Symbol, Timeframe Timeframe), IReadOnlyDictionary<string, string>> OverridesByContext;
        public ConcurrentBag<MLCalibrationLog>? CycleAuditCollector;

        // Counters mutated atomically by Interlocked through `ref ctx.Field`.
        public int EvaluatedModels;
        public int WarningModels;
        public int CriticalModels;
        public int RetrainingQueued;
        public int DispatchedAlerts;
        public int ResolvedAlerts;
        public int FailedModels;
        public int RemainingModels;

        // Alert budget counter for the per-cycle dispatch cap. Initialised to
        // settings.MaxAlertsPerCycle (or int.MaxValue when MaxAlertsPerCycle = 0).
        // Each trip-alert dispatch atomically decrements; when the result drops below
        // zero, downstream skips the dispatcher.DispatchAsync call but still upserts
        // the Alert row so dashboards see the state. Auto-resolves never consume budget.
        public int RemainingAlertBudget;
        public int AlertsSuppressedByBudget;
    }

    public MLCalibrationMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLCalibrationMonitorWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null,
        IAlertDispatcher? alertDispatcher = null,
        IMLCalibrationSignalEvaluator? signalEvaluator = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
        _alertDispatcher = alertDispatcher;
        // Default to the standard evaluator when DI doesn't supply one. Tests that
        // construct the worker directly inherit the production math without ceremony;
        // tests of the evaluator itself can construct it standalone.
        _signalEvaluator = signalEvaluator ?? new MLCalibrationSignalEvaluator();
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

        // Single round-trip for everything override-related. Both the per-cycle token
        // validator (regime + knob typos) and the per-context bucketer operate on this
        // pre-loaded list in-memory, so the cycle pays one query instead of two.
        var allOverrideRows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith("MLCalibration:Override:"))
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value))
            .ToListAsync(ct);

        // Per-cycle audit of override-key tokens (regime names + knob names). Catches
        // typos that operators introduce mid-flight, dedup'd via a hashed signature so
        // a persistent unmatched set logs once and transitions re-log.
        ValidateOverrideTokens(allOverrideRows);

        // Pre-load per-model latest NewestOutcomeAt across all prior cycles. Survives restarts
        // and is shared across replicas via the audit table.
        var modelIds = models.Select(model => model.Id).ToList();
        var lastNewestOutcome = await LoadLastNewestOutcomeMapAsync(db, modelIds, ct);

        // Per-context buckets for parallel iterations to share. With one model per pair
        // (typical) this is identical to the previous per-model load; with multiple
        // variants per pair it collapses N reads to one. The bucket dicts are immutable
        // plain data and safe to share.
        var overridesByContext = BucketOverridesByContext(models, allOverrideRows);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        int parallelism = Math.Clamp(settings.MaxDegreeOfParallelism, 1, MaxMaxDegreeOfParallelism);

        // All per-iteration mutable state and shared inputs go into one heap object so
        // the parallel loop's lambda captures only `this` and `ctx` (not 12 separate
        // locals). Counters are atomic-incremented through `ref ctx.Field`. Cycle-level
        // audit collector is non-null only when AuditFlushMode = Cycle (then iterations
        // transfer their audits to it; a single DI scope flushes after the loop).
        var ctx = new CycleIteration
        {
            Settings = settings,
            NowUtc = nowUtc,
            LastNewestOutcome = lastNewestOutcome,
            OverridesByContext = overridesByContext,
            CycleAuditCollector = settings.AuditFlushMode == AuditFlushMode.Cycle
                ? new ConcurrentBag<MLCalibrationLog>()
                : null,
            RemainingModels = models.Count,
            // Budget = MaxAlertsPerCycle, with 0 meaning "disabled" (effectively
            // unlimited). int.MaxValue removes the gate without branching at the
            // call site.
            RemainingAlertBudget = settings.MaxAlertsPerCycle > 0
                ? settings.MaxAlertsPerCycle
                : int.MaxValue,
        };

        // The lambda captures `this` and `ctx` only — two refs vs. the twelve before
        // CycleIteration consolidated state. A true method-group reference would require
        // ctx to be a closure-free instance member, which would unsafely couple cycles
        // (cycles are serialised by the cycle lock today, but storing per-cycle state
        // on the worker would conflate that boundary). Two captures is the floor.
        await Parallel.ForEachAsync(
            models,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (model, modelCt) => await EvaluateOneIterationAsync(ctx, model, modelCt))
            .ConfigureAwait(false);

        var cycleAuditCollector = ctx.CycleAuditCollector;
        int evaluatedModels = ctx.EvaluatedModels;
        int warningModels = ctx.WarningModels;
        int criticalModels = ctx.CriticalModels;
        int retrainingQueued = ctx.RetrainingQueued;
        int dispatchedAlerts = ctx.DispatchedAlerts;
        int resolvedAlerts = ctx.ResolvedAlerts;
        int failedModels = ctx.FailedModels;

        // One flush of every iteration's audits when AuditFlushMode = Cycle. Pass the
        // bag directly; FlushAuditsAsync enumerates it as a snapshot.
        if (cycleAuditCollector is { IsEmpty: false })
        {
            await FlushAuditsAsync(cycleAuditCollector, ct);
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

    private async ValueTask EvaluateOneIterationAsync(
        CycleIteration ctx, ActiveModelCandidate model, CancellationToken modelCt)
    {
        ctx.LastNewestOutcome.TryGetValue(model.Id, out var lastSeen);
        var overrides = ctx.OverridesByContext[(model.Symbol, model.Timeframe)];

        await using var modelScope = _scopeFactory.CreateAsyncScope();
        var modelWriteCtx = modelScope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var modelDb = modelWriteCtx.GetDbContext();

        try
        {
            // Refresh the worker heartbeat before each model evaluation. Long cycles
            // (large fleet / DOP=1) would otherwise leave the health monitor without
            // a signal until cycle end.
            _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

            var outcome = await EvaluateModelWithLockAsync(
                modelScope.ServiceProvider,
                modelWriteCtx,
                modelDb,
                model,
                ctx.Settings,
                overrides,
                ctx,
                lastSeen,
                ctx.NowUtc,
                modelCt);

            if (outcome.Evaluated)
            {
                Interlocked.Increment(ref ctx.EvaluatedModels);
                if (outcome.AlertState == MLCalibrationMonitorAlertState.Warning)
                    Interlocked.Increment(ref ctx.WarningModels);
                else if (outcome.AlertState == MLCalibrationMonitorAlertState.Critical)
                    Interlocked.Increment(ref ctx.CriticalModels);
                if (outcome.RetrainingQueued) Interlocked.Increment(ref ctx.RetrainingQueued);
                if (outcome.AlertDispatched) Interlocked.Increment(ref ctx.DispatchedAlerts);
                if (outcome.AlertResolved) Interlocked.Increment(ref ctx.ResolvedAlerts);
            }
            else
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
            // Shutdown propagation, not a model failure. Re-throw so Parallel.ForEachAsync
            // surfaces it and the ExecuteAsync loop honours stoppingToken.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ctx.FailedModels);
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
        finally
        {
            int remaining = Interlocked.Decrement(ref ctx.RemainingModels);
            _healthMonitor?.RecordBacklogDepth(WorkerName, remaining);
        }
    }

    private async Task<ModelEvaluationOutcome> EvaluateModelWithLockAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        IReadOnlyDictionary<string, string> overrides,
        CycleIteration? ctx,
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
                overrides, ctx, lastSeenOutcomeAt, nowUtc, ct);
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
        CycleIteration? ctx,
        DateTime? lastSeenOutcomeAt,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var cycleAuditCollector = ctx?.CycleAuditCollector;
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
        settings = ApplyPerContextOverrides(settings, overrides, model.Symbol, model.Timeframe, regime: null, modelId: model.Id);

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
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                // Symmetric with the regime-scope path so dashboards grouping on `regime`
                // see a uniform schema: global lookups land in a dedicated bucket instead
                // of an unlabeled phantom one.
                new KeyValuePair<string, object?>("regime", "global"));

            var summary = _signalEvaluator.ComputeSummary(
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
            var signals = _signalEvaluator.BuildSignals(
                summary.CurrentEce,
                summary.EceStderr,
                previousEce,
                baselineEce,
                settings.MaxEce,
                settings.DegradationDelta,
                settings.RegressionGuardK);
            var alertState = _signalEvaluator.ResolveAlertState(
                summary.CurrentEce, signals, settings.MaxEce, settings.DegradationDelta);

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
            // Update chronic-critical streak counter and dispatch retirement-candidate
            // alert if the threshold is crossed. Returns the new streak (0 if alertState
            // isn't Critical). Recovery to non-Critical resets the counter and
            // auto-resolves any active chronic alert.
            int chronicStreak = await TrackChronicCriticalAndAlertIfNeededAsync(
                serviceProvider, writeContext, db, model, settings, alertState, summary, signals, nowUtc, ct);
            bool inChronicState = chronicStreak >= settings.ChronicCriticalThreshold;

            bool retrainEligible =
                (signals.ThresholdExceeded && summary.CurrentEce > settings.MaxEce * SevereThresholdMultiplier) ||
                (signals.TrendExceeded && signals.TrendDelta > settings.DegradationDelta * SevereThresholdMultiplier) ||
                (settings.RetrainOnBaselineCritical && signals.BaselineExceeded
                    && signals.BaselineDelta > settings.DegradationDelta * SevereThresholdMultiplier);

            // Chronic-tripper retrain suppression: if the model has been Critical for
            // ≥ChronicCriticalThreshold cycles, repeated retraining is unlikely to recover.
            // SuppressRetrainOnChronic (default true) blocks the queue so the pipeline
            // doesn't burn capacity on a model that's a retirement candidate.
            if (settings.SuppressRetrainOnChronic && inChronicState)
            {
                retrainEligible = false;
            }

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
                    alertState, ctx, nowUtc, ct);

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
            {
                if (cycleAuditCollector is not null)
                {
                    // Cycle mode: hand off to the shared collector. The cycle's outer
                    // RunCycleCoreAsync flushes the entire batch in one round-trip after
                    // the parallel loop completes. Fewer DI scopes per cycle; trade-off
                    // is that an audit-flush failure loses *all* audit rows for the
                    // cycle instead of just one model's.
                    foreach (var entry in pendingAudits) cycleAuditCollector.Add(entry);
                }
                else
                {
                    // Default: per-model flush. Each model owns its own DI scope so an
                    // individual flush failure is contained to one model's audit rows.
                    await FlushAuditsAsync(pendingAudits, ct);
                }
            }
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
                settings, overrides, model.Symbol, model.Timeframe, regime, model.Id);

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

            var regimeSummary = _signalEvaluator.ComputeSummary(
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
            var regimeSignals = _signalEvaluator.BuildSignals(
                regimeSummary.CurrentEce,
                regimeSummary.EceStderr,
                previousEce: regimePreviousEce,
                baselineEce: TryResolveBaselineEce(model.ModelBytes, regime),
                regimeSettings.MaxEce,
                regimeSettings.DegradationDelta,
                regimeSettings.RegressionGuardK);
            var regimeState = _signalEvaluator.ResolveAlertState(
                regimeSummary.CurrentEce, regimeSignals, regimeSettings.MaxEce, regimeSettings.DegradationDelta);

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

    // Calibration math (sample creation, regime assignment, summary, time decay, ECE,
    // signals, alert state, severity, baseline lookup) lives in
    // MLCalibrationMonitorWorker.Signals.cs.
    // Bootstrap stderr cache + computation live in MLCalibrationMonitorWorker.Bootstrap.cs.

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
        CycleIteration? ctx,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string deduplicationKey = BuildDeduplicationKey(model.Id);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(candidate => !candidate.IsDeleted
                                           && candidate.IsActive
                                           && candidate.DeduplicationKey == deduplicationKey, ct);

        AlertSeverity severity = _signalEvaluator.DetermineSeverity(
            alertState, summary, signals, settings.MaxEce, settings.DegradationDelta);
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

        // Per-cycle alert budget: the Alert row is already upserted (so dashboards see
        // the state) but dispatching the notification is rate-limited to protect against
        // alert storms during fleet-wide degradation events. Auto-resolves are exempt.
        if (ctx is not null && Interlocked.Decrement(ref ctx.RemainingAlertBudget) < 0)
        {
            Interlocked.Increment(ref ctx.AlertsSuppressedByBudget);
            _metrics?.MLCalibrationMonitorCyclesSkipped.Add(
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

    /// <summary>
    /// Tracks per-model consecutive Critical-state cycles and dispatches a one-time
    /// chronic-tripper alert when the streak hits <c>ChronicCriticalThreshold</c>.
    /// Returns the new streak length (0 if state isn't Critical). Operators see the
    /// alert as a signal to retire / replace the model rather than keep retraining.
    /// Symmetric with Conformal worker's chronic-trip flow.
    /// </summary>
    private async Task<int> TrackChronicCriticalAndAlertIfNeededAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        MLCalibrationMonitorAlertState alertState,
        CalibrationSummary summary,
        CalibrationSignals signals,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string counterKey = $"MLCalibration:Model:{model.Id}:ConsecutiveCriticalCycles";
        int previous = (int)(await LoadExistingMetricAsync(db, counterKey, ct) ?? 0);

        if (alertState != MLCalibrationMonitorAlertState.Critical)
        {
            // Recovery path: counter resets, active chronic alert auto-resolves.
            if (previous > 0)
            {
                await EngineConfigUpsert.UpsertAsync(
                    db, counterKey, "0",
                    ConfigDataType.Int,
                    "Consecutive cycles where this model's calibration was Critical.",
                    isHotReloadable: false, ct);

                if (previous >= settings.ChronicCriticalThreshold)
                {
                    await TryAutoResolveChronicAlertAsync(serviceProvider, writeContext, db, model, nowUtc, ct);
                }
            }
            return 0;
        }

        int next = previous + 1;
        await EngineConfigUpsert.UpsertAsync(
            db, counterKey, next.ToString(CultureInfo.InvariantCulture),
            ConfigDataType.Int,
            "Consecutive cycles where this model's calibration was Critical.",
            isHotReloadable: false, ct);

        // Threshold cross: dispatch the retirement-candidate alert exactly once. Earlier
        // cycles below threshold and later cycles above it both no-op the alert path.
        if (previous < settings.ChronicCriticalThreshold && next >= settings.ChronicCriticalThreshold)
        {
            await DispatchChronicAlertAsync(serviceProvider, writeContext, db, model, settings, summary, signals, next, nowUtc, ct);
        }
        return next;
    }

    private async Task DispatchChronicAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        CalibrationSummary summary,
        CalibrationSignals signals,
        int streak,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var dispatcher = ResolveAlertDispatcher(serviceProvider);
        if (dispatcher is null) return;

        try
        {
            string dedupKey = ChronicAlertDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
            bool exists = await db.Set<Alert>()
                .AnyAsync(a => a.DeduplicationKey == dedupKey && a.IsActive && !a.IsDeleted, ct);
            if (exists) return;

            int cooldownSec = await AlertCooldownDefaults.GetCooldownAsync(
                db, AlertCooldownDefaults.CK_MLEscalation, AlertCooldownDefaults.Default_MLEscalation, ct);

            string conditionJson = JsonSerializer.Serialize(new
            {
                detector = "MLCalibrationMonitor",
                kind = "chronic_critical",
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                consecutiveCriticalCycles = streak,
                threshold = settings.ChronicCriticalThreshold,
                currentEce = Math.Round(summary.CurrentEce, 6),
                trendDelta = Math.Round(signals.TrendDelta, 6),
                baselineDelta = Math.Round(signals.BaselineDelta, 6),
                detectedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
            });

            var alert = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                Severity = AlertSeverity.Critical,
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
                "MLCalibrationMonitor: model {0} ({1}/{2}) has been Critical for {3} consecutive cycles (threshold {4}). Repeated retraining is unlikely to recover; the model is a retirement candidate.",
                model.Id, model.Symbol, model.Timeframe, streak, settings.ChronicCriticalThreshold);

            await dispatcher.DispatchAsync(alert, message, ct);

            _metrics?.MLCalibrationMonitorAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("state", "chronic"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch chronic-critical alert for model {ModelId}.",
                WorkerName, model.Id);
        }
    }

    private async Task TryAutoResolveChronicAlertAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
        DbContext db,
        ActiveModelCandidate model,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string dedupKey = ChronicAlertDeduplicationPrefix + model.Id.ToString(CultureInfo.InvariantCulture);
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(a => a.DeduplicationKey == dedupKey && a.IsActive && !a.IsDeleted, ct);
        if (alert is null) return;

        var dispatcher = ResolveAlertDispatcher(serviceProvider);
        if (dispatcher is not null && alert.LastTriggeredAt.HasValue)
        {
            try
            {
                await dispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "{Worker}: failed to auto-resolve chronic-critical alert for model {ModelId}.",
                    WorkerName, model.Id);
            }
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
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
            CK_AuditFlushMode,
            CK_ChronicCriticalThreshold, CK_SuppressRetrainOnChronic, CK_MaxAlertsPerCycle,
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
                DefaultLongCycleWarnSeconds, MinLongCycleWarnSeconds, MaxLongCycleWarnSeconds),
            AuditFlushMode: ParseAuditFlushMode(GetString(values, CK_AuditFlushMode, defaultValue: null)),
            ChronicCriticalThreshold: ClampInt(
                GetInt(values, CK_ChronicCriticalThreshold, DefaultChronicCriticalThreshold),
                DefaultChronicCriticalThreshold, MinChronicCriticalThreshold, MaxChronicCriticalThreshold),
            SuppressRetrainOnChronic: GetBool(values, CK_SuppressRetrainOnChronic, true),
            MaxAlertsPerCycle: ClampIntAllowingZero(
                GetInt(values, CK_MaxAlertsPerCycle, DefaultMaxAlertsPerCycle),
                DefaultMaxAlertsPerCycle, MinMaxAlertsPerCycle, MaxMaxAlertsPerCycle));
    }

    private static string? GetString(IReadOnlyDictionary<string, string> values, string key, string? defaultValue)
        => values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : defaultValue;

    private AuditFlushMode ParseAuditFlushMode(string? raw)
    {
        if (raw is null)
        {
            ResetUnknownAuditFlushModeStateOnRecovery();
            return DefaultAuditFlushMode;
        }
        if (AuditFlushModeAliases.TryGetValue(raw, out var mode))
        {
            ResetUnknownAuditFlushModeStateOnRecovery();
            return mode;
        }

        // Unknown value (e.g., operator typed "Cycel"). Default to PerModel and emit a
        // one-time Warning per distinct unknown value so silent fall-through doesn't
        // hide a misconfiguration. Dedup uses an FNV-1a signature symmetric with the
        // override-tokens validator (_lastUnmatchedTokensSignature): persistent same-
        // value typo isn't re-logged; transitions re-log the new state. Recovery to a
        // known alias (above) resets the signature so a re-introduced typo logs again.
        long signature = FnvHashFold(FnvHashChars(raw, FnvOffsetBasis));
        if (signature != _lastUnknownAuditFlushModeSignature)
        {
            _lastUnknownAuditFlushModeSignature = signature;
            _logger.LogWarning(
                "{Worker}: AuditFlushMode value '{Raw}' is not recognized (expected one of {Aliases}). Falling back to {Default}.",
                WorkerName, raw, string.Join(", ", AuditFlushModeAliases.Keys), DefaultAuditFlushMode);
        }
        return DefaultAuditFlushMode;
    }

    private void ResetUnknownAuditFlushModeStateOnRecovery()
    {
        if (_lastUnknownAuditFlushModeSignature == 0) return;
        _lastUnknownAuditFlushModeSignature = 0;
        _logger.LogInformation(
            "{Worker}: AuditFlushMode value now resolves to a recognized alias; previously reported typo appears to have been corrected.",
            WorkerName);
    }

    // Shared FNV-1a 64-bit primitives for dedup signatures. Two callers:
    //   - ParseAuditFlushMode: hashes a single raw string.
    //   - ComputeUnmatchedSignature (Overrides.cs): folds a sorted set of strings,
    //     separator-delimited, by chaining FnvHashChars over each entry + a "|".
    // Reserve 0 for the empty/clean state; FnvHashFold nudges 0 to 1 if FNV ever lands there.
    private const long FnvOffsetBasis = 1469598103934665603L;
    private const long FnvPrime = 1099511628211L;

    private static long FnvHashChars(string s, long state)
    {
        unchecked
        {
            foreach (char c in s) state = (state ^ c) * FnvPrime;
            return state;
        }
    }

    private static long FnvHashFold(long state) => state == 0 ? 1 : state;

    // ResolveAlertState / DetermineSeverity / HasExplicitProbability / TryResolveBaselineEce
    // live in MLCalibrationMonitorWorker.Signals.cs.

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
    // LoadFreshBootstrapStderrAsync + AppendBootstrapCacheSpecs live in
    // MLCalibrationMonitorWorker.Bootstrap.cs.

    // Override loading, resolution, validation, and per-context application live in
    // MLCalibrationMonitorWorker.Overrides.cs. Field declarations (_lastUnmatchedTokensSignature
    // and ValidOverrideKnobs) stay in this file alongside the rest of the worker state.

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

    // Audit pipeline + diagnostics builders live in MLCalibrationMonitorWorker.Audit.cs.

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
    int LongCycleWarnSeconds,
    AuditFlushMode AuditFlushMode,
    int ChronicCriticalThreshold,
    bool SuppressRetrainOnChronic,
    int MaxAlertsPerCycle);

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

public enum MLCalibrationMonitorAlertState
{
    None = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>
/// How <see cref="MLCalibrationMonitorWorker"/> persists per-model audit rows.
/// <list type="bullet">
///   <item><description><c>PerModel</c> (default): each iteration opens its own DI scope and flushes immediately. Per-model isolation — one flush failure loses just that model's audit rows.</description></item>
///   <item><description><c>Cycle</c>: iterations transfer audit rows to a shared bag; the cycle flushes once at end via a single DI scope. Cuts N scope+DbContext creations to one, at the cost of bundling all rows under one flush.</description></item>
/// </list>
/// </summary>
internal enum AuditFlushMode
{
    PerModel = 0,
    Cycle = 1,
}
