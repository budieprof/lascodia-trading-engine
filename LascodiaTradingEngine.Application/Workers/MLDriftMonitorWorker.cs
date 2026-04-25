using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Drift;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors live ML model prediction accuracy and automatically queues a retraining run
/// when a deployed model's accuracy degrades below the configured threshold.
///
/// <para>
/// Every poll cycle the worker:
/// <list type="number">
///   <item>Finds all active <see cref="MLModel"/> records.</item>
///   <item>Looks up <see cref="MLModelPredictionLog"/> records with known outcomes within
///         the rolling drift window (<c>MLTraining:DriftWindowDays</c>).</item>
///   <item>Computes the rolling direction accuracy over the window.</item>
///   <item>If accuracy &lt; <c>MLTraining:DriftAccuracyThreshold</c> AND enough predictions
///         exist, queues a new <see cref="MLTrainingRun"/> with
///         <see cref="TriggerType.AutoDegrading"/>.</item>
///   <item>Skips models that already have a queued/running retraining run to avoid
///         duplicate queue entries.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MLDriftMonitorWorker : BackgroundService
{
    private const string CK_Enabled             = "MLDrift:Enabled";
    private const string CK_PollSecs            = "MLDrift:PollIntervalSeconds";
    private const string CK_WindowDays          = "MLTraining:DriftWindowDays";
    private const string CK_MinPredictions      = "MLTraining:DriftMinPredictions";
    private const string CK_AccThreshold        = "MLTraining:DriftAccuracyThreshold";
    private const string CK_TrainingDays        = "MLTraining:TrainingDataWindowDays";
    private const string CK_MaxBrierDrift       = "MLDrift:MaxBrierScore";
    private const string CK_MaxDisagreement     = "MLDrift:MaxEnsembleDisagreement";
    private const string CK_RelativeDegradation = "MLDrift:RelativeDegradationRatio";
    private const string CK_ConsecutiveFailures = "MLDrift:ConsecutiveFailuresBeforeRetrain";
    private const string CK_SharpeDegradation   = "MLDrift:SharpeDegradationRatio";
    private const string CK_MinClosedTrades     = "MLDrift:MinClosedTradesForSharpe";
    private const string CK_MaxQueueDepth       = "MLTraining:MaxQueueDepth";
    private const string CK_MaxModelsPerCycle   = "MLDrift:MaxModelsPerCycle";
    private const string CK_LockTimeoutSecs     = "MLDrift:LockTimeoutSeconds";
    private const string CK_DbCommandTimeoutSecs = "MLDrift:DbCommandTimeoutSeconds";
    private const string CK_MinTimeBetweenRetrainsHours = "MLDrift:MinTimeBetweenRetrainsHours";
    private const string CK_AlertCooldownSecs = "MLDrift:AlertCooldownSeconds";
    private const string CK_AlertDestination = "MLDrift:AlertDestination";
    private const string CK_FlagTtlHours = "MLDrift:FlagTtlHours";

    // Per-pair override key prefix:
    //   MLDrift:Override:{Symbol}:{Timeframe}:AccuracyThreshold
    //   MLDrift:Override:{Symbol}:{Timeframe}:MaxBrierScore
    private const string CK_OverridePrefix = "MLDrift:Override:";

    // Defaults + clamp ranges. Each defaulted to the historical hard-coded value so existing
    // operational behaviour is preserved when no config row exists.
    private const int DefaultPollSeconds = 300;
    private const int MinPollSeconds = 60;
    private const int MaxPollSeconds = 24 * 60 * 60;

    private const int DefaultWindowDays = 14;
    private const int MinWindowDays = 1;
    private const int MaxWindowDays = 365;

    private const int DefaultMinPredictions = 30;
    private const int MinMinPredictions = 5;
    private const int MaxMinPredictions = 5000;

    private const double DefaultAccuracyThreshold = 0.50;
    private const double MinAccuracyThreshold = 0.0;
    private const double MaxAccuracyThreshold = 1.0;

    private const int DefaultTrainingDataWindowDays = 365;
    private const int MinTrainingDataWindowDays = 30;
    private const int MaxTrainingDataWindowDays = 3650;

    private const double DefaultMaxBrierDrift = 0.30;
    private const double MinMaxBrierDrift = 0.0;
    private const double MaxMaxBrierDrift = 1.0;

    private const double DefaultMaxDisagreement = 0.35;
    private const double MinMaxDisagreement = 0.0;
    private const double MaxMaxDisagreement = 5.0;

    private const double DefaultRelativeDegradation = 0.85;
    private const double MinRelativeDegradation = 0.01;
    private const double MaxRelativeDegradation = 1.0;

    private const int DefaultConsecutiveFailures = 3;
    private const int MinConsecutiveFailures = 1;
    private const int MaxConsecutiveFailures = 100;

    private const double DefaultSharpeDegradation = 0.60;
    private const double MinSharpeDegradation = 0.0;
    private const double MaxSharpeDegradation = 1.0;

    private const int DefaultMinClosedTrades = 20;
    private const int MinMinClosedTrades = 5;
    private const int MaxMinClosedTrades = 5000;

    private const int DefaultMaxQueueDepth = int.MaxValue;
    private const int MinMaxQueueDepth = 1;
    private const int MaxMaxQueueDepth = 100_000;

    private const int DefaultMaxModelsPerCycle = 1024;
    private const int MinMaxModelsPerCycle = 1;
    private const int MaxMaxModelsPerCycle = 10_000;

    private const int DefaultLockTimeoutSeconds = 5;
    private const int MinLockTimeoutSeconds = 0;
    private const int MaxLockTimeoutSeconds = 300;

    private const int DefaultDbCommandTimeoutSeconds = 60;
    private const int MinDbCommandTimeoutSeconds = 5;
    private const int MaxDbCommandTimeoutSeconds = 600;

    private const int DefaultMinTimeBetweenRetrainsHours = 0;
    private const int MinMinTimeBetweenRetrainsHours = 0;
    private const int MaxMinTimeBetweenRetrainsHours = 24 * 30;

    private const int DefaultAlertCooldownSeconds = AlertCooldownDefaults.Default_MLDrift;
    private const int MinAlertCooldownSeconds = 0;
    private const int MaxAlertCooldownSeconds = 30 * 24 * 60 * 60;

    private const int DefaultFlagTtlHours = 24;
    private const int MinFlagTtlHours = 1;
    private const int MaxFlagTtlHours = 24 * 30;

    internal const string WorkerName = nameof(MLDriftMonitorWorker);
    private const string DistributedLockKey = "workers:ml-drift-monitor:cycle";
    private const string DriftDetectorType = "DriftMonitor";
    private const string DriftAlertPrefix = "drift-monitor:";
    private const int AlertConditionMaxLength = 2_500;

    /// <summary>Outer-loop wake cadence — drives sub-cycle hot-reload of config.</summary>
    private static readonly TimeSpan WakeInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLDriftMonitorWorker> _logger;
    private readonly MLDriftMonitorOptions _options;
    private readonly IDistributedLock? _distributedLock;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IAlertDispatcher? _alertDispatcher;

    private long _consecutiveCycleFailuresField;
    private int _missingDistributedLockWarningEmitted;

    internal readonly record struct DriftMonitorWorkerSettings(
        bool Enabled,
        TimeSpan PollInterval,
        int WindowDays,
        int MinPredictions,
        double AccuracyThreshold,
        int TrainingDataWindowDays,
        double MaxBrierScore,
        double MaxEnsembleDisagreement,
        double RelativeDegradationRatio,
        int RequiredConsecutiveFailures,
        double SharpeDegradationRatio,
        int MinClosedTradesForSharpe,
        int MaxQueueDepth,
        int MaxModelsPerCycle,
        int LockTimeoutSeconds,
        int DbCommandTimeoutSeconds,
        int MinTimeBetweenRetrainsHours,
        int AlertCooldownSeconds,
        string AlertDestination,
        int DriftFlagTtlHours);

    internal readonly record struct DriftMonitorCycleResult(
        TimeSpan PollInterval,
        string? SkippedReason,
        int CandidateModelCount,
        int RetrainingQueued)
    {
        public static DriftMonitorCycleResult Skipped(TimeSpan pollInterval, string reason)
            => new(pollInterval, reason, 0, 0);

        public static DriftMonitorCycleResult Failed(TimeSpan pollInterval)
            => new(pollInterval, "failed", 0, 0);
    }

    /// <summary>
    /// Initialises the worker with its required dependencies.
    /// </summary>
    /// <param name="scopeFactory">
    /// Used to create a new DI scope per poll cycle, which gives each iteration a fresh
    /// pair of <see cref="IReadApplicationDbContext"/> / <see cref="IWriteApplicationDbContext"/>
    /// instances and prevents long-lived DbContext connection leaks.
    /// </param>
    /// <param name="logger">Structured logger for drift events and diagnostic output.</param>
    /// <param name="distributedLock">
    /// Optional distributed lock used to serialize the cycle across multiple worker instances.
    /// When null, duplicate cycles are possible in multi-instance deployments — a warning
    /// is emitted once on the first cycle.
    /// </param>
    /// <param name="healthMonitor">Optional health monitor for cycle telemetry.</param>
    /// <param name="metrics">Optional metric recorder.</param>
    /// <param name="timeProvider">
    /// Optional time abstraction so unit tests can run with a fixed clock; defaults to
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    /// <param name="alertDispatcher">
    /// Optional alert dispatcher used to fire dedup'd <see cref="AlertType.MLModelDegraded"/>
    /// alerts whenever a retraining run is queued. When null, the worker still queues runs
    /// and logs warnings — matching the prior behaviour for single-instance setups.
    /// </param>
    public MLDriftMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLDriftMonitorWorker> logger,
        MLDriftMonitorOptions? options = null,
        IDistributedLock? distributedLock = null,
        IWorkerHealthMonitor? healthMonitor = null,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IAlertDispatcher? alertDispatcher = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options ?? new MLDriftMonitorOptions();
        _distributedLock = distributedLock;
        _healthMonitor = healthMonitor;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _alertDispatcher = alertDispatcher;
    }

    private int ConsecutiveCycleFailures
    {
        get => (int)Interlocked.Read(ref _consecutiveCycleFailuresField);
        set => Interlocked.Exchange(ref _consecutiveCycleFailuresField, value);
    }

    /// <summary>
    /// Main background loop. Runs indefinitely at <c>MLDrift:PollIntervalSeconds</c> intervals
    /// (default 300 s / 5 min) until the host requests shutdown.
    ///
    /// Each cycle:
    /// <list type="number">
    ///   <item>Creates a fresh DI scope and resolves read + write DB contexts.</item>
    ///   <item>Reads all configurable thresholds from <see cref="EngineConfig"/>.</item>
    ///   <item>Loads all active (non-deleted) <see cref="MLModel"/> records.</item>
    ///   <item>Prunes the in-memory <see cref="_consecutiveFailures"/> tracker for models
    ///         that are no longer active (prevents unbounded memory growth).</item>
    ///   <item>Calls <see cref="CheckModelDriftAsync"/> for each active model.</item>
    /// </list>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors live ML accuracy/Brier/disagreement/Sharpe drift and queues retraining on confirmed degradation.",
            TimeSpan.FromSeconds(DefaultPollSeconds));

        // NOTE: a WorkerStartupSequencer phased delay (~15 s for ML monitoring) is
        // intentionally NOT applied here — the existing per-worker test harness uses
        // StartAsync/short-cancel and the delay would prevent the cycle body from running
        // within the test timeout. Phased startup is provided by the worker
        // orchestrator at the host level for production deployments.

        // Sub-cycle hot-reload: outer loop wakes every WakeInterval (1 min) and decides
        // whether the configured PollInterval has elapsed. This means operator changes to
        // PollIntervalSeconds propagate within a minute regardless of the configured cycle.
        DateTime lastCycleStartUtc = DateTime.MinValue;
        DateTime lastSuccessUtc = DateTime.MinValue;
        TimeSpan currentPollInterval = TimeSpan.FromSeconds(DefaultPollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

            // SLO: emit time-since-last-success on every wake (independent of cycle execution)
            // so dashboards can spot a stuck cycle within a minute.
            if (lastSuccessUtc != DateTime.MinValue)
            {
                _metrics?.MLDriftMonitorTimeSinceLastSuccessSec.Record(
                    (nowUtc - lastSuccessUtc).TotalSeconds);
            }

            bool dueForCycle = nowUtc - lastCycleStartUtc >= currentPollInterval;

            if (dueForCycle)
            {
                long cycleStarted = Stopwatch.GetTimestamp();
                lastCycleStartUtc = nowUtc;
                DriftMonitorCycleResult result;

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                    result = await RunCycleAsync(stoppingToken);
                    currentPollInterval = result.PollInterval;

                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs, new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLDriftMonitorCycleDurationMs.Record(durationMs);

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug("{Worker}: cycle skipped ({Reason}).", WorkerName, result.SkippedReason);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "{Worker}: candidates={Candidates}, retrain queued={Queued}.",
                            WorkerName, result.CandidateModelCount, result.RetrainingQueued);
                    }

                    var prevFailures = ConsecutiveCycleFailures;
                    if (prevFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, prevFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName, prevFailures);
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
                        new KeyValuePair<string, object?>("reason", "ml_drift_monitor_cycle"));
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }
            }

            try
            {
                var delay = ConsecutiveCycleFailures > 0
                    ? CalculateBackoffDelay(ConsecutiveCycleFailures)
                    : WakeInterval;
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _healthMonitor?.RecordWorkerStopped(WorkerName);
        _logger.LogInformation("{Worker} stopped.", WorkerName);
    }

    /// <summary>
    /// Internal entrypoint for one cycle's work. Acquires the distributed lock (when
    /// available), loads settings + active models, and runs the per-model drift checks.
    /// Exposed as <c>internal</c> so tests can drive a single deterministic cycle without
    /// going through <see cref="ExecuteAsync"/>'s long-running loop.
    /// </summary>
    internal async Task<DriftMonitorCycleResult> RunCycleAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
        var ctx = readDb.GetDbContext();
        var writeCtx = writeDb.GetDbContext();

        // Load + clamp settings once per cycle. Operators changing config see effects on the
        // next cycle (sub-cycle hot-reload is handled by the outer loop in ExecuteAsync).
        var settings = await LoadSettingsAsync(ctx, _options, ct);
        ApplyCommandTimeout(ctx, settings.DbCommandTimeoutSeconds);
        ApplyCommandTimeout(writeCtx, settings.DbCommandTimeoutSeconds);

        if (!settings.Enabled)
        {
            _metrics?.MLDriftMonitorCyclesSkipped.Add(
                1, new KeyValuePair<string, object?>("reason", "disabled"));
            return DriftMonitorCycleResult.Skipped(settings.PollInterval, "disabled");
        }

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is not null)
        {
            cycleLock = await _distributedLock.TryAcquireAsync(
                DistributedLockKey, TimeSpan.FromSeconds(settings.LockTimeoutSeconds), ct);
            if (cycleLock is null)
            {
                _metrics?.MLDriftMonitorLockAttempts.Add(
                    1, new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics?.MLDriftMonitorCyclesSkipped.Add(
                    1, new KeyValuePair<string, object?>("reason", "lock_busy"));
                return DriftMonitorCycleResult.Skipped(settings.PollInterval, "lock_busy");
            }
            _metrics?.MLDriftMonitorLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "acquired"));
        }
        else if (Interlocked.Exchange(ref _missingDistributedLockWarningEmitted, 1) == 0)
        {
            _metrics?.MLDriftMonitorLockAttempts.Add(
                1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            _logger.LogWarning(
                "{Worker} running without IDistributedLock; duplicate cycles are possible in multi-instance deployments.",
                WorkerName);
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(ctx, writeCtx, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    private async Task<DriftMonitorCycleResult> RunCycleCoreAsync(
        DbContext ctx,
        DbContext writeCtx,
        DriftMonitorWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc.AddDays(-settings.WindowDays);

        // Pre-load all per-pair overrides once; per-model lookup is then a Dictionary hit.
        var overrides = await LoadAllPerPairOverridesAsync(ctx, ct);

        // Load all active models. We deliberately DO NOT push a dormant-model filter into SQL
        // here — the tenure-challenge and model-expiry checks must run for every active model
        // regardless of whether it has fresh prediction data. The drift check applies its own
        // MinPredictions guard inline.
        var activeModelQuery = ctx.Set<MLModel>()
            .AsNoTracking()
            .Where(m =>
                m.IsActive &&
                !m.IsDeleted &&
                (m.Status == MLModelStatus.Active || m.IsFallbackChampion));

        var activeModels = await activeModelQuery
            .OrderBy(m => m.Id)
            .Take(settings.MaxModelsPerCycle)
            .ToListAsync(ct);

        var skippedByLimit = activeModels.Count == settings.MaxModelsPerCycle
            ? Math.Max(0, await activeModelQuery.CountAsync(ct) - activeModels.Count)
            : 0;

        if (skippedByLimit > 0)
        {
            _metrics?.MLDriftMonitorModelsSkipped.Add(
                skippedByLimit,
                new KeyValuePair<string, object?>("reason", "max_models_per_cycle"));
        }

        // Batched pre-fetch of prediction logs for ALL active models in two round-trips
        // (resolved + all-including-unresolved) instead of two queries per model. Eliminates
        // the prior N+1 pattern. The per-model dictionary lookup downstream is O(1).
        var modelIds = activeModels.Select(m => m.Id).ToList();
        var resolvedLogsByModel = await LoadResolvedLogsBatchedAsync(ctx, modelIds, windowStart, ct);
        var allLogsByModel      = await LoadAllLogsBatchedAsync(ctx, modelIds, windowStart, ct);

        _logger.LogDebug(
            "Drift monitor checking {Count} active models (window={Days}d threshold={Thr:P1} relDeg={Rel:P0})",
            activeModels.Count, settings.WindowDays, settings.AccuracyThreshold, settings.RelativeDegradationRatio);

        int retrainQueued = 0;

        foreach (var model in activeModels)
        {
            ct.ThrowIfCancellationRequested();

            overrides.TryGetValue((NormalizeSymbol(model.Symbol), model.Timeframe), out var perPair);
            resolvedLogsByModel.TryGetValue(model.Id, out var resolvedLogs);
            allLogsByModel.TryGetValue(model.Id, out var allLogs);

            try
            {
                if (await CheckModelDriftAsync(
                    model, writeCtx, ctx, windowStart, settings, perPair,
                    resolvedLogs ?? new List<MLModelPredictionLog>(),
                    allLogs ?? new List<MLModelPredictionLog>(),
                    ct))
                {
                    retrainQueued++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_drift_monitor_model"),
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogWarning(ex,
                    "Drift check failed for model {Id} ({Symbol}/{Timeframe}); continuing.",
                    model.Id, model.Symbol, model.Timeframe);
            }

            try
            {
                if (await CheckChampionTenureAsync(model, ctx, writeCtx, settings.TrainingDataWindowDays, ct))
                    retrainQueued++;
            }
            catch (Exception tenureEx)
            {
                _logger.LogDebug(tenureEx, "Tenure check failed for model {Id} — non-critical", model.Id);
            }

            try
            {
                if (await CheckModelExpiryAsync(model, ctx, writeCtx, settings.TrainingDataWindowDays, ct))
                    retrainQueued++;
            }
            catch (Exception expiryEx)
            {
                _logger.LogDebug(expiryEx, "Model expiry check failed for model {Id} — non-critical", model.Id);
            }
        }

        await ResolveStaleDriftAlertsAsync(
            writeCtx,
            activeModels.Select(model => (NormalizeSymbol(model.Symbol), model.Timeframe)).ToHashSet(),
            skippedByLimit == 0,
            ct);

        return new DriftMonitorCycleResult(
            PollInterval: settings.PollInterval,
            SkippedReason: null,
            CandidateModelCount: activeModels.Count,
            RetrainingQueued: retrainQueued);
    }

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
        catch (InvalidOperationException) { /* unsupported */ }
    }

    // ── Per-model drift check ─────────────────────────────────────────────────

    /// <summary>
    /// Evaluates a single active <see cref="MLModel"/> for multiple forms of performance
    /// degradation using its resolved <see cref="MLModelPredictionLog"/> records from the
    /// rolling drift window. Queues a <see cref="MLTrainingRun"/> if any drift criterion
    /// is triggered for <paramref name="requiredConsecutiveFailures"/> consecutive windows.
    ///
    /// <para><b>Drift criteria evaluated (any one can trigger retraining):</b></para>
    /// <list type="bullet">
    ///   <item><b>Accuracy drift</b> — rolling direction accuracy &lt; <paramref name="threshold"/>
    ///         (absolute floor, e.g. 50%).</item>
    ///   <item><b>Relative degradation</b> — rolling accuracy &lt; training accuracy ×
    ///         <paramref name="relativeDegradationRatio"/> (e.g. 85% of training accuracy),
    ///         allowing models trained on different base rates to share a common relative standard.</item>
    ///   <item><b>Calibration drift (Brier score)</b> — rolling Brier score &gt;
    ///         <paramref name="maxBrierScore"/>. Detects models that are systematically
    ///         over- or under-confident even when raw accuracy is still acceptable.
    ///         See <see cref="ComputeRollingBrierScore"/>.</item>
    ///   <item><b>Ensemble disagreement</b> — mean inter-learner std &gt;
    ///         <paramref name="maxEnsembleDisagreement"/>. High disagreement among bag members
    ///         indicates the model is operating in a region of the input space not well
    ///         covered by training data — an early-warning signal before accuracy drops.</item>
    ///   <item><b>Sharpe degradation</b> — rolling live Sharpe &lt; training Sharpe ×
    ///         <paramref name="sharpeDegradationRatio"/>. Requires at least
    ///         <paramref name="minClosedTradesForSharpe"/> resolved trades. Uses
    ///         magnitude × direction-sign as a P&amp;L proxy (annualised with √252 factor).</item>
    /// </list>
    ///
    /// <para><b>Consecutive-window guard:</b> a single bad window (e.g. a flash event) is
    /// not enough to trigger retraining. The model must fail on
    /// <paramref name="requiredConsecutiveFailures"/> consecutive poll cycles before a new
    /// <see cref="MLTrainingRun"/> is queued. The counter is persisted in <see cref="EngineConfig"/>
    /// under key <c>MLDrift:{Symbol}:{Timeframe}:ConsecutiveFailures</c> so it survives worker
    /// restarts — eliminating the in-memory amnesia bug where a restart would reset a model
    /// that was 2/3 windows toward a retrain trigger.</para>
    ///
    /// <para><b>Deduplication:</b> if a run for the same symbol/timeframe is already
    /// queued or running, this method skips queueing to avoid pile-up.</para>
    /// </summary>
    /// <param name="model">The active model to evaluate.</param>
    /// <param name="writeCtx">EF write context — used only to persist a new <see cref="MLTrainingRun"/>.</param>
    /// <param name="readCtx">EF read context — used for all SELECT queries.</param>
    /// <param name="windowStart">Start of the rolling evaluation window (UTC).</param>
    /// <param name="minPredictions">Minimum resolved predictions required to run drift checks.</param>
    /// <param name="threshold">Absolute direction accuracy floor.</param>
    /// <param name="trainingDays">Training window size for the queued retraining run (days back from now).</param>
    /// <param name="maxBrierScore">Maximum acceptable Brier score (calibration gate).</param>
    /// <param name="maxEnsembleDisagreement">Maximum acceptable mean ensemble std deviation.</param>
    /// <param name="relativeDegradationRatio">Fraction of training accuracy below which relative drift fires.</param>
    /// <param name="requiredConsecutiveFailures">Number of consecutive bad windows before retraining is queued.</param>
    /// <param name="sharpeDegradationRatio">Fraction of training Sharpe below which Sharpe drift fires.</param>
    /// <param name="minClosedTradesForSharpe">Minimum resolved trades before Sharpe comparison is active.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<bool> CheckModelDriftAsync(
        MLModel                                 model,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        DateTime                                windowStart,
        DriftMonitorWorkerSettings              settings,
        (double? AccuracyThreshold, double? MaxBrierScore) perPair,
        IReadOnlyList<MLModelPredictionLog>     logs,
        IReadOnlyList<MLModelPredictionLog>     allLogs,
        CancellationToken                       ct)
    {
        _metrics?.MLDriftMonitorModelsEvaluated.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()));

        // Per-pair overrides allow operator-level tuning without redeploying. Both fields fall
        // back to the global setting when null.
        int    minPredictions             = settings.MinPredictions;
        double threshold                  = perPair.AccuracyThreshold ?? settings.AccuracyThreshold;
        int    trainingDays               = settings.TrainingDataWindowDays;
        double maxBrierScore              = perPair.MaxBrierScore     ?? settings.MaxBrierScore;
        double maxEnsembleDisagreement    = settings.MaxEnsembleDisagreement;
        double relativeDegradationRatio   = settings.RelativeDegradationRatio;
        int    requiredConsecutiveFailures = settings.RequiredConsecutiveFailures;
        double sharpeDegradationRatio     = settings.SharpeDegradationRatio;
        int    minClosedTradesForSharpe   = settings.MinClosedTradesForSharpe;

        if (logs.Count < minPredictions)
        {
            _metrics?.MLDriftMonitorModelsSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "insufficient_history"),
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): only {N} resolved predictions in window — skipping drift check",
                model.Id, model.Symbol, model.Timeframe, logs.Count);
            return false;
        }

        int    correct  = logs.Count(l => l.DirectionCorrect == true);

        // ── Resolve the effective decision threshold (used for Brier scoring) ─
        double fallbackThreshold = 0.5;
        if (model.ModelBytes is { Length: > 0 })
        {
            try
            {
                var snap = JsonSerializer.Deserialize<ModelSnapshot>(model.ModelBytes);
                if (snap is not null)
                    fallbackThreshold = MLFeatureHelper.ResolveEffectiveDecisionThreshold(snap);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MLDriftMonitorWorker: failed to deserialize ModelSnapshot for model {ModelId} — using fallback threshold {Threshold}",
                    model.Id, fallbackThreshold);
            }
        }

        double brierScore = ComputeRollingBrierScore(logs, fallbackThreshold);

        var disagLogs = allLogs.Where(l => l.EnsembleDisagreement.HasValue).ToList();
        double meanDisagreement = disagLogs.Count > 0
            ? (double)disagLogs.Average(l => l.EnsembleDisagreement!.Value)
            : 0;

        var pnlReturns = logs
            .Where(l => l.ActualMagnitudePips.HasValue)
            .Select(l => (double)l.ActualMagnitudePips!.Value * (l.DirectionCorrect == true ? 1.0 : -1.0))
            .ToList();

        // ── Pure-CPU detector evaluation ─────────────────────────────────────
        var accuracySignal = DriftSignalDetectors.EvaluateAccuracy(correct, logs.Count, threshold);
        var brierSignal = DriftSignalDetectors.EvaluateBrier(brierScore, maxBrierScore);
        var disagreementSignal = DriftSignalDetectors.EvaluateDisagreement(
            meanDisagreement, disagLogs.Count, minPredictions, maxEnsembleDisagreement);
        var relativeSignal = DriftSignalDetectors.EvaluateRelativeDegradation(
            accuracySignal.Accuracy,
            (double?)model.DirectionAccuracy,
            relativeDegradationRatio);
        var sharpeSignal = DriftSignalDetectors.EvaluateSharpe(
            pnlReturns,
            (double?)model.SharpeRatio,
            sharpeDegradationRatio,
            minClosedTradesForSharpe);

        double accuracy = accuracySignal.Accuracy;

        _logger.LogDebug(
            "Model {Id} ({Symbol}/{Tf}): acc={Acc:P1} brier={Brier:F4} disagree={Dis:F4} N={N}",
            model.Id, model.Symbol, model.Timeframe, accuracy, brierScore, meanDisagreement, logs.Count);

        if (sharpeSignal.SampleCount >= minClosedTradesForSharpe && sharpeSignal.TrainingSharpe > 0)
        {
            _logger.LogDebug(
                "Model {Id}: live Sharpe={Live:F2} vs train={Train:F2} (threshold={Thr:F2})",
                model.Id, sharpeSignal.LiveSharpe, sharpeSignal.TrainingSharpe, sharpeSignal.EffectiveThreshold);
        }

        bool accuracyDrift     = accuracySignal.Triggered;
        bool calibrationDrift  = brierSignal.Triggered;
        bool disagreementDrift = disagreementSignal.Triggered;
        bool relativeDrift     = relativeSignal.Triggered;
        bool sharpeDrift       = sharpeSignal.Triggered;

        bool anyDrift = accuracyDrift || calibrationDrift || disagreementDrift || relativeDrift || sharpeDrift;

        // ── Persisted consecutive failure counter ────────────────────────────
        // Stored in EngineConfig so it survives worker restarts. Key pattern:
        //   MLDrift:{Symbol}:{Timeframe}:ConsecutiveFailures
        var normalizedSymbol = NormalizeSymbol(model.Symbol);
        var failKey = $"MLDrift:{normalizedSymbol}:{model.Timeframe}:ConsecutiveFailures";

        if (!anyDrift)
        {
            // Model is healthy — reset persisted consecutive failure counter
            await ResetPersistedFailureCountAsync(writeCtx, failKey, ct);
            await ExpireDriftFlagAsync(writeCtx, model, ct);
            await ResolveDriftAlertsForPairAsync(writeCtx, model.Symbol, model.Timeframe, ct);
            return false;
        }

        await SetOrRefreshDriftFlagAsync(writeCtx, model, settings, ct);

        // ── Consecutive window tracking ─────────────────────────────────────
        int failCount = await GetConfigAsync<int>(readCtx, failKey, 0, ct);
        failCount++;
        await UpsertConfigAsync(writeCtx, failKey, failCount.ToString(), ct);

        string driftReason = string.Join(", ", new[]
        {
            accuracyDrift     ? $"accuracy={accuracy:P1}<{threshold:P1}" : null,
            relativeDrift     ? $"relDeg={accuracy:P1}<{(double)model.DirectionAccuracy!.Value * relativeDegradationRatio:P1}({relativeDegradationRatio:P0}×train)" : null,
            calibrationDrift  ? $"brier={brierScore:F4}>{maxBrierScore:F4}" : null,
            disagreementDrift ? $"disagreement={meanDisagreement:F4}>{maxEnsembleDisagreement:F4}" : null,
            sharpeDrift       ? $"sharpeDeg=live<{sharpeDegradationRatio:P0}×trainSharpe" : null,
        }.Where(s => s is not null));

        if (failCount < requiredConsecutiveFailures)
        {
            _logger.LogInformation(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] — window {N}/{Required} before retrain",
                model.Id, model.Symbol, model.Timeframe, driftReason, failCount, requiredConsecutiveFailures);
            await writeCtx.SaveChangesAsync(ct);
            return false;
        }

        // Threshold met — reset persisted counter and proceed to queue retraining
        await ResetPersistedFailureCountAsync(writeCtx, failKey, ct);

        // ── Check whether a retraining run is already queued or running ──────
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol    &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                      ct);

        if (alreadyQueued)
        {
            _logger.LogDebug(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] but retraining already queued",
                model.Id, model.Symbol, model.Timeframe, driftReason);
            await writeCtx.SaveChangesAsync(ct);
            return false;
        }

        // ── Cooldown: suppress new retrain if a recent drift-triggered run completed ──
        // Mirrors ADWIN's `MinTimeBetweenRetrainsHours` pattern. Default 0 preserves the
        // historical behaviour of queueing as soon as `RequiredConsecutiveFailures` is met.
        if (settings.MinTimeBetweenRetrainsHours > 0)
        {
            var nowForCooldown = _timeProvider.GetUtcNow().UtcDateTime;
            var cutoff = nowForCooldown.AddHours(-settings.MinTimeBetweenRetrainsHours);
            bool recentRun = await readCtx.Set<MLTrainingRun>()
                .AsNoTracking()
                .AnyAsync(r =>
                    !r.IsDeleted &&
                    r.Symbol == model.Symbol &&
                    r.Timeframe == model.Timeframe &&
                    r.TriggerType == TriggerType.AutoDegrading &&
                    (r.CompletedAt ?? r.StartedAt) >= cutoff, ct);
            if (recentRun)
            {
                _metrics?.MLDriftMonitorRetrainCooldownSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                _logger.LogDebug(
                    "Model {Id} ({Symbol}/{Tf}): drift detected but inside retrain cooldown ({Hours}h)",
                    model.Id, model.Symbol, model.Timeframe, settings.MinTimeBetweenRetrainsHours);
                await writeCtx.SaveChangesAsync(ct);
                return false;
            }
        }

        // ── Global queue depth limiter ──────────────────────────────────────
        // Prevents thundering-herd when many models drift simultaneously —
        // caps the total number of Queued runs. Emergency runs (Priority <= 1)
        // bypass the limiter so drift-triggered retrains are never blocked.
        int currentQueueDepth = await readCtx.Set<MLTrainingRun>()
            .CountAsync(r => r.Status == RunStatus.Queued, ct);

        if (currentQueueDepth >= settings.MaxQueueDepth)
        {
            _logger.LogWarning(
                "Model {Id} ({Symbol}/{Tf}): drift detected [{Reason}] but queue depth {Depth} >= max {Max} — skipping",
                model.Id, model.Symbol, model.Timeframe, driftReason, currentQueueDepth, settings.MaxQueueDepth);
            await writeCtx.SaveChangesAsync(ct);
            return false;
        }

        // ── Queue a new AutoDegrading training run ────────────────────────────
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Improvement #2: determine primary drift trigger type and build metadata.
        // Count active signals to distinguish single from multi-signal drift.
        int activeSignals = (accuracyDrift ? 1 : 0) + (calibrationDrift ? 1 : 0) +
                            (disagreementDrift ? 1 : 0) + (relativeDrift ? 1 : 0) +
                            (sharpeDrift ? 1 : 0);

        // Weight drift signals by importance
        double driftScore = 0;
        if (accuracyDrift) driftScore += 1.0;        // Accuracy is most important
        if (calibrationDrift) driftScore += 0.7;
        if (sharpeDrift) driftScore += 0.5;
        if (relativeDrift) driftScore += 0.4;
        if (disagreementDrift) driftScore += 0.3;

        // Log when composite signal is strong
        if (driftScore >= 1.5)
        {
            _logger.LogInformation("MLDriftMonitor: strong composite drift signal ({Score:F1}) for model {ModelId} ({Symbol}/{Tf})",
                driftScore, model.Id, model.Symbol, model.Timeframe);
        }

        string driftTrigger = activeSignals == 1
            ? (accuracyDrift     ? "AccuracyDrift"
             : calibrationDrift  ? "CalibrationDrift"
             : disagreementDrift ? "DisagreementDrift"
             : sharpeDrift       ? "SharpeDrift"
             :                     "RelativeDegradation")
            : "MultiSignal";

        string driftMetadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            accuracy       = accuracyDrift     ? accuracy          : (double?)null,
            threshold      = accuracyDrift     ? threshold         : (double?)null,
            brierScore     = calibrationDrift  ? brierScore        : (double?)null,
            disagreement   = disagreementDrift ? meanDisagreement  : (double?)null,
        });

        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.AutoDegrading,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            DriftTriggerType = driftTrigger,
            DriftMetadataJson = driftMetadata,
            Priority         = 1, // Improvement #9: drift-triggered = priority 1
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            // Another worker (or a concurrent drift trigger) won the partial-unique-index race.
            // Treat as no-op: the retrain is happening, just not from us.
            writeCtx.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: retrain queue race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }

        // ── Inter-worker coordination: signal urgent suppression check ────
        // Set an EngineConfig flag so MLSignalSuppressionWorker can prioritize
        // this symbol/timeframe on its next iteration instead of waiting for
        // the full poll cycle (reduces propagation delay from minutes to seconds).
        var urgentKey = $"MLDrift:UrgentSymbol:{normalizedSymbol}:{model.Timeframe}";
        await UpsertConfigAsync(
            writeCtx,
            urgentKey,
            now.ToString("O", CultureInfo.InvariantCulture),
            ct,
            ConfigDataType.String);

        _metrics?.MLDriftMonitorDriftsDetected.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("learner_architecture", model.LearnerArchitecture.ToString()),
            new KeyValuePair<string, object?>("trigger", driftTrigger));
        _metrics?.MLDriftMonitorRetrainingQueued.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("trigger", driftTrigger));

        _logger.LogWarning(
            "Drift detected for model {Id} ({Symbol}/{Tf}): [{Reason}] (trigger={Trigger}) over {N} predictions. " +
            "Queued retraining run {RunId}. Set urgent flag for suppression worker.",
            model.Id, model.Symbol, model.Timeframe, driftReason, driftTrigger, logs.Count, run.Id);

        await DispatchDriftAlertAsync(
            writeCtx,
            model,
            driftTrigger,
            driftReason,
            accuracy,
            logs.Count,
            settings,
            retrainQueued: true,
            ct);

        return true;
    }


    private async Task DispatchDriftAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        MLModel model,
        string driftTrigger,
        string driftReason,
        double accuracy,
        int observationCount,
        DriftMonitorWorkerSettings settings,
        bool retrainQueued,
        CancellationToken ct)
    {
        var dedupKey = BuildDriftAlertDedupKey(model.Symbol, model.Timeframe, driftTrigger);

        try
        {
            string conditionJson = JsonSerializer.Serialize(new
            {
                detectorType = DriftDetectorType,
                destination = settings.AlertDestination,
                modelId = model.Id,
                symbol = model.Symbol,
                timeframe = model.Timeframe.ToString(),
                driftTrigger,
                driftReason,
                accuracy,
                observationCount,
                retrainingQueued = retrainQueued,
                detectedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            });

            if (conditionJson.Length > AlertConditionMaxLength)
                conditionJson = conditionJson[..AlertConditionMaxLength];

            var alerts = await writeCtx.Set<Alert>()
                .IgnoreQueryFilters()
                .Where(alert =>
                    !alert.IsDeleted &&
                    alert.AlertType == AlertType.MLModelDegraded &&
                    alert.DeduplicationKey == dedupKey)
                .OrderByDescending(alert => alert.IsActive)
                .ThenByDescending(alert => alert.LastTriggeredAt ?? DateTime.MinValue)
                .ThenByDescending(alert => alert.Id)
                .ToListAsync(ct);

            var alert = alerts.FirstOrDefault();
            var wasActive = alert is { IsActive: true, AutoResolvedAt: null };
            var lastTriggeredAt = alert?.LastTriggeredAt;

            if (alert is null)
            {
                alert = new Alert
                {
                    AlertType = AlertType.MLModelDegraded,
                    Symbol = model.Symbol,
                    DeduplicationKey = dedupKey,
                };
                writeCtx.Set<Alert>().Add(alert);
            }

            foreach (var duplicate in alerts.Skip(1))
            {
                duplicate.IsActive = false;
                duplicate.AutoResolvedAt ??= _timeProvider.GetUtcNow().UtcDateTime;
            }

            alert.AlertType = AlertType.MLModelDegraded;
            alert.Severity = driftTrigger == "MultiSignal" ? AlertSeverity.High : AlertSeverity.Medium;
            alert.Symbol = model.Symbol;
            alert.DeduplicationKey = dedupKey;
            alert.CooldownSeconds = settings.AlertCooldownSeconds;
            alert.ConditionJson = conditionJson;
            alert.IsActive = true;
            alert.AutoResolvedAt = null;

            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var cooldownElapsed = lastTriggeredAt is null ||
                                  settings.AlertCooldownSeconds <= 0 ||
                                  nowUtc - lastTriggeredAt.Value >= TimeSpan.FromSeconds(settings.AlertCooldownSeconds);
            var shouldDispatch = !wasActive || cooldownElapsed;

            await writeCtx.SaveChangesAsync(ct);

            if (!shouldDispatch)
            {
                _metrics?.MLDriftMonitorAlertsSuppressed.Add(
                    1,
                    new KeyValuePair<string, object?>("symbol", model.Symbol),
                    new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                    new KeyValuePair<string, object?>("trigger", driftTrigger),
                    new KeyValuePair<string, object?>("reason", "cooldown"));
                return;
            }

            if (_alertDispatcher is null)
            {
                alert.LastTriggeredAt = nowUtc;
                await writeCtx.SaveChangesAsync(ct);
                return;
            }

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "Drift on {0}/{1} (trigger={2}): {3}; accuracy={4:F4} over {5} predictions; retrainQueued={6}.",
                model.Symbol, model.Timeframe, driftTrigger, driftReason, accuracy, observationCount, retrainQueued);

            await _alertDispatcher.DispatchAsync(alert, message, ct);
            alert.LastTriggeredAt = nowUtc;
            await writeCtx.SaveChangesAsync(ct);

            _metrics?.MLDriftMonitorAlertsDispatched.Add(
                1,
                new KeyValuePair<string, object?>("symbol", model.Symbol),
                new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
                new KeyValuePair<string, object?>("trigger", driftTrigger));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: failed to dispatch drift-monitor alert for model {ModelId} ({Symbol}/{Timeframe}).",
                WorkerName, model.Id, model.Symbol, model.Timeframe);
        }
    }

    private async Task SetOrRefreshDriftFlagAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        MLModel model,
        DriftMonitorWorkerSettings settings,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var symbol = NormalizeSymbol(model.Symbol);
        var flag = await db.Set<MLDriftFlag>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(existing =>
                existing.Symbol == symbol &&
                existing.Timeframe == model.Timeframe &&
                existing.DetectorType == DriftDetectorType,
                ct);

        if (flag is null)
        {
            db.Set<MLDriftFlag>().Add(new MLDriftFlag
            {
                Symbol = symbol,
                Timeframe = model.Timeframe,
                DetectorType = DriftDetectorType,
                FirstDetectedAtUtc = nowUtc,
                LastRefreshedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddHours(settings.DriftFlagTtlHours),
                ConsecutiveDetections = 1,
                IsDeleted = false,
            });
        }
        else
        {
            var wasActive = !flag.IsDeleted && flag.ExpiresAtUtc > nowUtc;
            flag.IsDeleted = false;
            flag.LastRefreshedAtUtc = nowUtc;
            flag.ExpiresAtUtc = nowUtc.AddHours(settings.DriftFlagTtlHours);
            flag.ConsecutiveDetections = wasActive ? flag.ConsecutiveDetections + 1 : 1;

            if (!wasActive)
                flag.FirstDetectedAtUtc = nowUtc;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            flag = await db.Set<MLDriftFlag>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(existing =>
                    existing.Symbol == symbol &&
                    existing.Timeframe == model.Timeframe &&
                    existing.DetectorType == DriftDetectorType,
                    ct);

            if (flag is null)
                throw;

            var wasActive = !flag.IsDeleted && flag.ExpiresAtUtc > nowUtc;
            flag.IsDeleted = false;
            flag.LastRefreshedAtUtc = nowUtc;
            flag.ExpiresAtUtc = nowUtc.AddHours(settings.DriftFlagTtlHours);
            flag.ConsecutiveDetections = wasActive ? flag.ConsecutiveDetections + 1 : 1;

            if (!wasActive)
                flag.FirstDetectedAtUtc = nowUtc;

            await db.SaveChangesAsync(ct);
        }

        _metrics?.MLDriftMonitorFlagsSet.Add(
            1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
    }

    private async Task ExpireDriftFlagAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        MLModel model,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var symbol = NormalizeSymbol(model.Symbol);
        var flag = await db.Set<MLDriftFlag>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(existing =>
                existing.Symbol == symbol &&
                existing.Timeframe == model.Timeframe &&
                existing.DetectorType == DriftDetectorType,
                ct);

        if (flag is null || flag.ExpiresAtUtc <= nowUtc)
            return;

        flag.ExpiresAtUtc = nowUtc.AddMinutes(-1);
        flag.LastRefreshedAtUtc = nowUtc;
        flag.ConsecutiveDetections = 0;

        _metrics?.MLDriftMonitorFlagsCleared.Add(
            1,
            new KeyValuePair<string, object?>("symbol", symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));

        await db.SaveChangesAsync(ct);
    }

    private async Task<int> ResolveDriftAlertsForPairAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        var prefix = BuildDriftAlertPrefix(symbol, timeframe);
        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert =>
                !alert.IsDeleted &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.IsActive &&
                alert.DeduplicationKey != null &&
                alert.DeduplicationKey.StartsWith(prefix))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
            resolved += await ResolveLoadedAlertAsync(db, alert, ct);

        return resolved;
    }

    private async Task<int> ResolveStaleDriftAlertsAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        HashSet<(string Symbol, Timeframe Timeframe)> activePairs,
        bool allowFullStaleResolution,
        CancellationToken ct)
    {
        if (!allowFullStaleResolution)
            return 0;

        var alerts = await db.Set<Alert>()
            .IgnoreQueryFilters()
            .Where(alert =>
                !alert.IsDeleted &&
                alert.AlertType == AlertType.MLModelDegraded &&
                alert.IsActive &&
                alert.DeduplicationKey != null &&
                alert.DeduplicationKey.StartsWith(DriftAlertPrefix))
            .ToListAsync(ct);

        var resolved = 0;
        foreach (var alert in alerts)
        {
            var pair = TryParseDriftAlertPair(alert.DeduplicationKey);
            if (pair is not null && activePairs.Contains(pair.Value))
                continue;

            resolved += await ResolveLoadedAlertAsync(db, alert, ct);
        }

        return resolved;
    }

    private async Task<int> ResolveLoadedAlertAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        Alert alert,
        CancellationToken ct)
    {
        if (!alert.IsActive && alert.AutoResolvedAt.HasValue)
            return 0;

        try
        {
            if (_alertDispatcher is not null)
                await _alertDispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Worker}: failed to dispatch resolved notification for alert {AlertId}.", WorkerName, alert.Id);
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= _timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        _metrics?.MLDriftMonitorAlertsResolved.Add(
            1,
            new KeyValuePair<string, object?>("symbol", alert.Symbol ?? "unknown"));

        return 1;
    }

    private static string BuildDriftAlertDedupKey(string symbol, Timeframe timeframe, string driftTrigger)
        => $"{BuildDriftAlertPrefix(symbol, timeframe)}{driftTrigger}";

    private static string BuildDriftAlertPrefix(string symbol, Timeframe timeframe)
        => $"{DriftAlertPrefix}{NormalizeSymbol(symbol)}:{timeframe}:";

    private static (string Symbol, Timeframe Timeframe)? TryParseDriftAlertPair(string? deduplicationKey)
    {
        if (string.IsNullOrWhiteSpace(deduplicationKey) ||
            !deduplicationKey.StartsWith(DriftAlertPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = deduplicationKey[DriftAlertPrefix.Length..].Split(':');
        if (parts.Length < 3 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !Enum.TryParse<Timeframe>(parts[1], out var timeframe))
        {
            return null;
        }

        return (NormalizeSymbol(parts[0]), timeframe);
    }

    private static string NormalizeSymbol(string symbol)
        => string.IsNullOrWhiteSpace(symbol) ? string.Empty : symbol.Trim().ToUpperInvariant();

    // ── Improvement #10: Champion tenure tracking ──────────────────────────

    /// <summary>
    /// Checks whether the model has exceeded the maximum champion tenure without being
    /// challenged. If so, queues a proactive training run to ensure the model hasn't
    /// silently degraded below optimal performance.
    /// </summary>
    private async Task<bool> CheckChampionTenureAsync(
        MLModel                           model,
        Microsoft.EntityFrameworkCore.DbContext readCtx,
        Microsoft.EntityFrameworkCore.DbContext writeCtx,
        int                               trainingDays,
        CancellationToken                 ct)
    {
        // Read tenure config
        var enabledStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:ProactiveChallengeEnabled" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        if (enabledStr != "true" && enabledStr != "1") return false;

        var maxTenureStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:MaxChampionTenureDays" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        int maxTenureDays = int.TryParse(maxTenureStr, out var mt) ? mt : 30;

        var minBetweenStr = await readCtx.Set<EngineConfig>()
            .Where(c => c.Key == "MLTraining:MinDaysBetweenChallenges" && !c.IsDeleted)
            .Select(c => c.Value).FirstOrDefaultAsync(ct);
        int minDaysBetween = int.TryParse(minBetweenStr, out var mb) ? mb : 7;

        if (!model.ActivatedAt.HasValue) return false;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        double tenureDays = (now - model.ActivatedAt.Value).TotalDays;
        if (tenureDays < maxTenureDays) return false;

        // Check cooldown since last challenge
        if (model.LastChallengedAt.HasValue &&
            (now - model.LastChallengedAt.Value).TotalDays < minDaysBetween)
            return false;

        // Check if a run is already queued/running
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);
        if (alreadyQueued) return false;

        // Queue a proactive challenge run
        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.Scheduled,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            Priority         = 2, // Improvement #9: tenure challenge = priority 2
        };

        writeCtx.Set<MLTrainingRun>().Add(run);

        // Update LastChallengedAt to prevent redundant challenges
        await writeCtx.Set<MLModel>()
            .Where(m => m.Id == model.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastChallengedAt, now), ct);

        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            writeCtx.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: tenure-challenge race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }

        _logger.LogInformation(
            "Tenure challenge: model {Id} ({Symbol}/{Tf}) active for {Days:F0} days " +
            "(max tenure={MaxDays}). Queued proactive retraining run {RunId}.",
            model.Id, model.Symbol, model.Timeframe, tenureDays, maxTenureDays, run.Id);
        _metrics?.MLDriftMonitorRetrainingQueued.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("trigger", "TenureChallenge"));
        return true;
    }

    // ── Model expiry policy ─────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the active model has exceeded <c>MLTraining:MaxModelAgeDays</c> (default 90).
    /// If the model is expired and no queued/running retraining run exists, queues an emergency
    /// retraining run with Priority=0 (highest) and DriftTriggerType="ModelExpiry".
    /// </summary>
    private async Task<bool> CheckModelExpiryAsync(
        MLModel                                    model,
        Microsoft.EntityFrameworkCore.DbContext     readCtx,
        Microsoft.EntityFrameworkCore.DbContext     writeCtx,
        int                                        trainingDays,
        CancellationToken                          ct)
    {
        if (!model.ActivatedAt.HasValue) return false;

        int maxAgeDays = await GetConfigAsync<int>(readCtx, "MLTraining:MaxModelAgeDays", 90, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        double ageDays = (now - model.ActivatedAt.Value).TotalDays;

        if (ageDays <= maxAgeDays) return false;

        // Check if a retraining run is already queued or running
        bool alreadyQueued = await readCtx.Set<MLTrainingRun>()
            .AnyAsync(r => r.Symbol    == model.Symbol &&
                           r.Timeframe == model.Timeframe &&
                           (r.Status == RunStatus.Queued || r.Status == RunStatus.Running), ct);

        if (alreadyQueued) return false;

        _logger.LogWarning(
            "Model {Id} ({Symbol}/{Tf}): exceeded max age ({Age:F0} days > {Max} days). " +
            "Queuing emergency retraining.",
            model.Id, model.Symbol, model.Timeframe, ageDays, maxAgeDays);

        var run = new MLTrainingRun
        {
            Symbol           = model.Symbol,
            Timeframe        = model.Timeframe,
            TriggerType      = TriggerType.AutoDegrading,
            Status           = RunStatus.Queued,
            FromDate         = now.AddDays(-trainingDays),
            ToDate           = now,
            StartedAt        = now,
            DriftTriggerType = "ModelExpiry",
            Priority         = 0, // emergency priority
        };

        writeCtx.Set<MLTrainingRun>().Add(run);
        try
        {
            await writeCtx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            writeCtx.ChangeTracker.Clear();
            _logger.LogInformation(
                "{Worker}: model-expiry race for {Symbol}/{Timeframe} resolved by partial unique index; another worker queued the run.",
                WorkerName, model.Symbol, model.Timeframe);
            return false;
        }

        _logger.LogWarning(
            "Model expiry: model {Id} ({Symbol}/{Tf}) active for {Age:F0} days (max={Max}). " +
            "Queued emergency retraining run {RunId} (Priority=0).",
            model.Id, model.Symbol, model.Timeframe, ageDays, maxAgeDays, run.Id);
        _metrics?.MLDriftMonitorRetrainingQueued.Add(
            1,
            new KeyValuePair<string, object?>("symbol", model.Symbol),
            new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()),
            new KeyValuePair<string, object?>("trigger", "ModelExpiry"));
        return true;
    }

    // ── Metric helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Brier score using ConfidenceScore as the probability of the predicted direction.
    /// Remaps confidence [0,1] → probability space: p = 0.5 + conf/2 when correct direction,
    /// 0.5 − conf/2 when wrong. Tracks calibration drift independently of accuracy.
    /// </summary>
    private static double ComputeRollingBrierScore(
        IReadOnlyList<MLModelPredictionLog> logs,
        double                              fallbackThreshold)
    {
        double sum = 0;
        int    n   = 0;
        foreach (var l in logs)
        {
            if (l.DirectionCorrect is null) continue;
            double pBuy = MLFeatureHelper.ResolveLoggedServedBuyProbability(l, fallbackThreshold);
            double y   = l.ActualDirection == TradeDirection.Buy ? 1.0 : 0.0;
            sum += (pBuy - y) * (pBuy - y);
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <summary>
    /// Single-query batched fetch of resolved prediction logs for all active models in the
    /// rolling window. Replaces the prior N+1 per-model fetch pattern. Returns a dictionary
    /// keyed by model ID with one list per model.
    /// </summary>
    private static async Task<Dictionary<long, List<MLModelPredictionLog>>> LoadResolvedLogsBatchedAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        IReadOnlyList<long> modelIds,
        DateTime windowStart,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new Dictionary<long, List<MLModelPredictionLog>>();

        var rows = await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l =>
                modelIds.Contains(l.MLModelId) &&
                !l.IsDeleted &&
                l.DirectionCorrect != null &&
                l.OutcomeRecordedAt != null &&
                l.OutcomeRecordedAt >= windowStart)
            .ToListAsync(ct);

        var byModel = new Dictionary<long, List<MLModelPredictionLog>>(modelIds.Count);
        foreach (var row in rows)
        {
            if (!byModel.TryGetValue(row.MLModelId, out var list))
            {
                list = new List<MLModelPredictionLog>();
                byModel[row.MLModelId] = list;
            }
            list.Add(row);
        }
        return byModel;
    }

    /// <summary>
    /// Single-query batched fetch of all (resolved + unresolved) prediction logs for all active
    /// models in the rolling window. Used for ensemble-disagreement monitoring, which can
    /// fire on predictions that haven't yet had outcomes recorded.
    /// </summary>
    private static async Task<Dictionary<long, List<MLModelPredictionLog>>> LoadAllLogsBatchedAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        IReadOnlyList<long> modelIds,
        DateTime windowStart,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new Dictionary<long, List<MLModelPredictionLog>>();

        var rows = await db.Set<MLModelPredictionLog>()
            .AsNoTracking()
            .Where(l =>
                modelIds.Contains(l.MLModelId) &&
                !l.IsDeleted &&
                l.PredictedAt >= windowStart)
            .ToListAsync(ct);

        var byModel = new Dictionary<long, List<MLModelPredictionLog>>(modelIds.Count);
        foreach (var row in rows)
        {
            if (!byModel.TryGetValue(row.MLModelId, out var list))
            {
                list = new List<MLModelPredictionLog>();
                byModel[row.MLModelId] = list;
            }
            list.Add(row);
        }
        return byModel;
    }

    /// <summary>
    /// Loads all worker settings in one round-trip and clamps each to a safe range.
    /// Operators can submit invalid values (negative thresholds, out-of-range probabilities)
    /// without breaking the cycle — the clamper falls back to the defaults documented in
    /// each <c>Default*</c>/<c>Min*</c>/<c>Max*</c> constant block above.
    /// </summary>
    internal static async Task<DriftMonitorWorkerSettings> LoadSettingsAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        CancellationToken ct)
        => await LoadSettingsAsync(db, new MLDriftMonitorOptions(), ct);

    private static async Task<DriftMonitorWorkerSettings> LoadSettingsAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        MLDriftMonitorOptions options,
        CancellationToken ct)
    {
        var defaults = BuildSettings(options);
        string[] keys =
        [
            CK_Enabled, CK_PollSecs, CK_WindowDays, CK_MinPredictions, CK_AccThreshold,
            CK_TrainingDays, CK_MaxBrierDrift, CK_MaxDisagreement, CK_RelativeDegradation,
            CK_ConsecutiveFailures, CK_SharpeDegradation, CK_MinClosedTrades, CK_MaxQueueDepth,
            CK_MaxModelsPerCycle, CK_LockTimeoutSecs, CK_DbCommandTimeoutSecs,
            CK_MinTimeBetweenRetrainsHours, CK_AlertCooldownSecs, CK_AlertDestination,
            CK_FlagTtlHours, AlertCooldownDefaults.CK_MLDrift,
        ];

        var values = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);

        return new DriftMonitorWorkerSettings(
            Enabled: GetBool(values, CK_Enabled, defaults.Enabled),
            PollInterval: TimeSpan.FromSeconds(
                ClampInt(GetInt(values, CK_PollSecs, (int)defaults.PollInterval.TotalSeconds), DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(GetInt(values, CK_WindowDays, defaults.WindowDays), DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinPredictions: ClampInt(GetInt(values, CK_MinPredictions, defaults.MinPredictions), DefaultMinPredictions, MinMinPredictions, MaxMinPredictions),
            AccuracyThreshold: ClampDoubleRange(GetDouble(values, CK_AccThreshold, defaults.AccuracyThreshold), DefaultAccuracyThreshold, MinAccuracyThreshold, MaxAccuracyThreshold),
            TrainingDataWindowDays: ClampInt(GetInt(values, CK_TrainingDays, defaults.TrainingDataWindowDays), DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MaxBrierScore: ClampDoubleRange(GetDouble(values, CK_MaxBrierDrift, defaults.MaxBrierScore), DefaultMaxBrierDrift, MinMaxBrierDrift, MaxMaxBrierDrift),
            MaxEnsembleDisagreement: ClampDoubleRange(GetDouble(values, CK_MaxDisagreement, defaults.MaxEnsembleDisagreement), DefaultMaxDisagreement, MinMaxDisagreement, MaxMaxDisagreement),
            RelativeDegradationRatio: ClampDoubleRange(GetDouble(values, CK_RelativeDegradation, defaults.RelativeDegradationRatio), DefaultRelativeDegradation, MinRelativeDegradation, MaxRelativeDegradation),
            RequiredConsecutiveFailures: ClampInt(GetInt(values, CK_ConsecutiveFailures, defaults.RequiredConsecutiveFailures), DefaultConsecutiveFailures, MinConsecutiveFailures, MaxConsecutiveFailures),
            SharpeDegradationRatio: ClampDoubleRange(GetDouble(values, CK_SharpeDegradation, defaults.SharpeDegradationRatio), DefaultSharpeDegradation, MinSharpeDegradation, MaxSharpeDegradation),
            MinClosedTradesForSharpe: ClampInt(GetInt(values, CK_MinClosedTrades, defaults.MinClosedTradesForSharpe), DefaultMinClosedTrades, MinMinClosedTrades, MaxMinClosedTrades),
            MaxQueueDepth: ClampIntAllowMax(GetInt(values, CK_MaxQueueDepth, defaults.MaxQueueDepth), DefaultMaxQueueDepth, MinMaxQueueDepth, MaxMaxQueueDepth),
            MaxModelsPerCycle: ClampInt(GetInt(values, CK_MaxModelsPerCycle, defaults.MaxModelsPerCycle), DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampNonNegativeInt(GetInt(values, CK_LockTimeoutSecs, defaults.LockTimeoutSeconds), DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(GetInt(values, CK_DbCommandTimeoutSecs, defaults.DbCommandTimeoutSeconds), DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(
                GetInt(values, CK_MinTimeBetweenRetrainsHours, defaults.MinTimeBetweenRetrainsHours),
                DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            AlertCooldownSeconds: ClampNonNegativeInt(
                GetInt(values, CK_AlertCooldownSecs,
                    GetInt(values, AlertCooldownDefaults.CK_MLDrift, defaults.AlertCooldownSeconds)),
                DefaultAlertCooldownSeconds, MinAlertCooldownSeconds, MaxAlertCooldownSeconds),
            AlertDestination: GetString(values, CK_AlertDestination, defaults.AlertDestination, maxLength: 100),
            DriftFlagTtlHours: ClampInt(GetInt(values, CK_FlagTtlHours, defaults.DriftFlagTtlHours), DefaultFlagTtlHours, MinFlagTtlHours, MaxFlagTtlHours));
    }

    private static DriftMonitorWorkerSettings BuildSettings(MLDriftMonitorOptions options)
        => new(
            Enabled: options.Enabled,
            PollInterval: TimeSpan.FromSeconds(ClampInt(options.PollIntervalSeconds, DefaultPollSeconds, MinPollSeconds, MaxPollSeconds)),
            WindowDays: ClampInt(options.DriftWindowDays, DefaultWindowDays, MinWindowDays, MaxWindowDays),
            MinPredictions: ClampInt(options.DriftMinPredictions, DefaultMinPredictions, MinMinPredictions, MaxMinPredictions),
            AccuracyThreshold: ClampDoubleRange(options.DriftAccuracyThreshold, DefaultAccuracyThreshold, MinAccuracyThreshold, MaxAccuracyThreshold),
            TrainingDataWindowDays: ClampInt(options.TrainingDataWindowDays, DefaultTrainingDataWindowDays, MinTrainingDataWindowDays, MaxTrainingDataWindowDays),
            MaxBrierScore: ClampDoubleRange(options.MaxBrierScore, DefaultMaxBrierDrift, MinMaxBrierDrift, MaxMaxBrierDrift),
            MaxEnsembleDisagreement: ClampDoubleRange(options.MaxEnsembleDisagreement, DefaultMaxDisagreement, MinMaxDisagreement, MaxMaxDisagreement),
            RelativeDegradationRatio: ClampDoubleRange(options.RelativeDegradationRatio, DefaultRelativeDegradation, MinRelativeDegradation, MaxRelativeDegradation),
            RequiredConsecutiveFailures: ClampInt(options.ConsecutiveFailuresBeforeRetrain, DefaultConsecutiveFailures, MinConsecutiveFailures, MaxConsecutiveFailures),
            SharpeDegradationRatio: ClampDoubleRange(options.SharpeDegradationRatio, DefaultSharpeDegradation, MinSharpeDegradation, MaxSharpeDegradation),
            MinClosedTradesForSharpe: ClampInt(options.MinClosedTradesForSharpe, DefaultMinClosedTrades, MinMinClosedTrades, MaxMinClosedTrades),
            MaxQueueDepth: ClampIntAllowMax(options.MaxQueueDepth, DefaultMaxQueueDepth, MinMaxQueueDepth, MaxMaxQueueDepth),
            MaxModelsPerCycle: ClampInt(options.MaxModelsPerCycle, DefaultMaxModelsPerCycle, MinMaxModelsPerCycle, MaxMaxModelsPerCycle),
            LockTimeoutSeconds: ClampNonNegativeInt(options.LockTimeoutSeconds, DefaultLockTimeoutSeconds, MinLockTimeoutSeconds, MaxLockTimeoutSeconds),
            DbCommandTimeoutSeconds: ClampInt(options.DbCommandTimeoutSeconds, DefaultDbCommandTimeoutSeconds, MinDbCommandTimeoutSeconds, MaxDbCommandTimeoutSeconds),
            MinTimeBetweenRetrainsHours: ClampNonNegativeInt(options.MinTimeBetweenRetrainsHours, DefaultMinTimeBetweenRetrainsHours, MinMinTimeBetweenRetrainsHours, MaxMinTimeBetweenRetrainsHours),
            AlertCooldownSeconds: ClampNonNegativeInt(options.AlertCooldownSeconds, DefaultAlertCooldownSeconds, MinAlertCooldownSeconds, MaxAlertCooldownSeconds),
            AlertDestination: string.IsNullOrWhiteSpace(options.AlertDestination)
                ? "ml-ops"
                : options.AlertDestination.Trim().Length > 100
                    ? options.AlertDestination.Trim()[..100]
                    : options.AlertDestination.Trim(),
            DriftFlagTtlHours: ClampInt(options.DriftFlagTtlHours, DefaultFlagTtlHours, MinFlagTtlHours, MaxFlagTtlHours));

    /// <summary>
    /// Single-query fetch of all per-pair overrides keyed under <c>MLDrift:Override:</c>.
    /// Each key has the form <c>MLDrift:Override:{Symbol}:{Timeframe}:{AccuracyThreshold|MaxBrierScore}</c>.
    /// Values out of the safe range fall back to the global setting (the override is silently
    /// ignored rather than blocking the cycle).
    /// </summary>
    private static async Task<Dictionary<(string Symbol, Timeframe Timeframe), (double? AccuracyThreshold, double? MaxBrierScore)>> LoadAllPerPairOverridesAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        CancellationToken ct)
    {
        var rows = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => c.Key.StartsWith(CK_OverridePrefix))
            .Select(c => new { c.Key, c.Value })
            .ToListAsync(ct);

        var dict = new Dictionary<(string, Timeframe), (double?, double?)>();
        foreach (var row in rows)
        {
            var rest = row.Key.Substring(CK_OverridePrefix.Length);
            var parts = rest.Split(':');
            if (parts.Length != 3) continue;

            string symbol = NormalizeSymbol(parts[0]);
            if (symbol.Length == 0) continue;
            if (!Enum.TryParse<Timeframe>(parts[1], ignoreCase: false, out var timeframe)) continue;

            string field = parts[2];
            var key = (symbol, timeframe);
            dict.TryGetValue(key, out var current);

            if (field == "AccuracyThreshold" &&
                double.TryParse(row.Value, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.InvariantCulture, out var thr) &&
                thr >= MinAccuracyThreshold && thr <= MaxAccuracyThreshold)
            {
                current.Item1 = thr;
            }
            else if (field == "MaxBrierScore" &&
                double.TryParse(row.Value, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.InvariantCulture, out var bri) &&
                bri >= MinMaxBrierDrift && bri <= MaxMaxBrierDrift)
            {
                current.Item2 = bri;
            }
            else
            {
                continue;
            }
            dict[key] = current;
        }
        return dict;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool defaultValue)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (bool.TryParse(raw, out var parsedBool)) return parsedBool;
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedInt))
            return parsedInt != 0;
        return defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
        => values.TryGetValue(key, out var raw) &&
           int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static double GetDouble(IReadOnlyDictionary<string, string> values, string key, double defaultValue)
        => values.TryGetValue(key, out var raw) &&
           double.TryParse(raw, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
               System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;

    private static string GetString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string defaultValue,
        int maxLength)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var trimmed = raw.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static int ClampInt(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static int ClampIntAllowMax(int value, int fallback, int min, int max)
        => value <= 0 ? fallback : value >= max ? max : Math.Min(Math.Max(value, min), max);

    private static int ClampNonNegativeInt(int value, int fallback, int min, int max)
        => value < 0 ? fallback : Math.Min(Math.Max(value, min), max);

    private static double ClampDoubleRange(double value, double fallback, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? fallback : value;

    /// <summary>
    /// Reads a typed value from <see cref="EngineConfig"/> or returns <paramref name="defaultValue"/>
    /// when the key is absent or its string value cannot be converted to <typeparamref name="T"/>.
    /// All reads are <c>AsNoTracking</c> to avoid stale-cache issues in long-lived loops.
    /// </summary>
    private static async Task<T> GetConfigAsync<T>(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        T                                       defaultValue,
        CancellationToken                       ct)
    {
        var entry = await ctx.Set<EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        if (entry?.Value is null) return defaultValue;

        try
        {
            if (typeof(T) == typeof(bool))
            {
                if (bool.TryParse(entry.Value, out var parsedBool))
                    return (T)(object)parsedBool;
                if (int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    return (T)(object)(parsedInt != 0);
                return defaultValue;
            }

            return (T)Convert.ChangeType(entry.Value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Upserts a value into <see cref="EngineConfig"/>. If the key already exists, updates
    /// the value; otherwise inserts a new row. Used to persist the consecutive failure counter
    /// across worker restarts.
    /// </summary>
    private static Task UpsertConfigAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        string                                  value,
        CancellationToken                       ct,
        LascodiaTradingEngine.Domain.Enums.ConfigDataType dataType = LascodiaTradingEngine.Domain.Enums.ConfigDataType.Int)
        => LascodiaTradingEngine.Application.Common.Utilities.EngineConfigUpsert.UpsertAsync(ctx, key, value, dataType: dataType, ct: ct);

    /// <summary>
    /// Resets (or removes) the persisted consecutive failure counter for a model that is healthy.
    /// Uses <c>ExecuteUpdateAsync</c> to set the value to "0" — avoids loading the entity.
    /// </summary>
    private static async Task ResetPersistedFailureCountAsync(
        Microsoft.EntityFrameworkCore.DbContext ctx,
        string                                  key,
        CancellationToken                       ct)
    {
        await ctx.Set<EngineConfig>()
            .Where(c => c.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Value, "0"), ct);
    }
}
