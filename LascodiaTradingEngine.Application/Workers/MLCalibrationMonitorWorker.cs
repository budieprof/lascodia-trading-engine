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
    private const string CK_WriteLegacyAlias = "MLCalibration:WriteLegacyAlias";
    private const string CK_TimeDecayHalfLifeDays = "MLCalibration:TimeDecayHalfLifeDays";
    private const string CK_MinSamplesForTimeDecay = "MLCalibration:MinSamplesForTimeDecay";
    private const string CK_TrendSmoothingWindow = "MLCalibration:TrendSmoothingWindow";
    private const string CK_StaleSkipAlertThreshold = "MLCalibration:StaleSkipAlertThreshold";
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

    // Smoothing window: 1 = single-cycle delta (current behavior), 3 = average over last 3
    // cycles. Higher values dampen transient single-cycle noise at the cost of slower
    // response to a real shift.
    private const int DefaultTrendSmoothingWindow = 1;
    private const int MinTrendSmoothingWindow = 1;
    private const int MaxTrendSmoothingWindow = 30;

    // Number of consecutive `no_recent_resolved_predictions` skips before the staleness
    // alert fires. Default 5 ≈ 5 hours at the default 1h cycle.
    private const int DefaultStaleSkipAlertThreshold = 5;
    private const int MinStaleSkipAlertThreshold = 1;
    private const int MaxStaleSkipAlertThreshold = 1000;

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

    private readonly record struct ActiveModelCandidate(
        long Id,
        string Symbol,
        Timeframe Timeframe,
        LearnerArchitecture LearnerArchitecture,
        byte[]? ModelBytes);

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
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _metrics?.MLCalibrationMonitorCycleDurationMs.Record(durationMs);

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

    private async Task<MLCalibrationMonitorCycleResult> RunCycleCoreAsync(
        IServiceProvider serviceProvider,
        IWriteApplicationDbContext writeContext,
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
                model.ModelBytes))
            .ToListAsync(ct);

        if (models.Count == 0)
        {
            _metrics?.MLCalibrationMonitorCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return MLCalibrationMonitorCycleResult.Skipped(settings, "no_active_models");
        }

        // Pre-load per-model latest NewestOutcomeAt across all prior cycles. Survives restarts
        // and is shared across replicas via the audit table.
        var modelIds = models.Select(model => model.Id).ToList();
        var lastNewestOutcome = await LoadLastNewestOutcomeMapAsync(db, modelIds, ct);

        int evaluatedModels = 0;
        int warningModels = 0;
        int criticalModels = 0;
        int retrainingQueued = 0;
        int dispatchedAlerts = 0;
        int resolvedAlerts = 0;
        int failedModels = 0;

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                lastNewestOutcome.TryGetValue(model.Id, out var lastSeen);
                var outcome = await EvaluateModelWithLockAsync(
                    serviceProvider,
                    writeContext,
                    db,
                    model,
                    settings,
                    lastSeen,
                    nowUtc,
                    ct);

                if (!outcome.Evaluated)
                {
                    _metrics?.MLCalibrationMonitorModelsSkipped.Add(
                        1,
                        new KeyValuePair<string, object?>("reason", outcome.SkipReason ?? "skipped"),
                        new KeyValuePair<string, object?>("symbol", model.Symbol),
                        new KeyValuePair<string, object?>("timeframe", model.Timeframe.ToString()));
                    continue;
                }

                evaluatedModels++;
                if (outcome.AlertState == MLCalibrationMonitorAlertState.Warning)
                    warningModels++;
                else if (outcome.AlertState == MLCalibrationMonitorAlertState.Critical)
                    criticalModels++;

                if (outcome.RetrainingQueued)
                    retrainingQueued++;
                if (outcome.AlertDispatched)
                    dispatchedAlerts++;
                if (outcome.AlertResolved)
                    resolvedAlerts++;
            }
            catch (Exception ex)
            {
                failedModels++;
                _metrics?.WorkerErrors.Add(
                    1,
                    new KeyValuePair<string, object?>("worker", WorkerName),
                    new KeyValuePair<string, object?>("reason", "ml_calibration_monitor_model"));
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
                db.ChangeTracker.Clear();
            }
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
                lastSeenOutcomeAt, nowUtc, ct);
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
        DateTime? lastSeenOutcomeAt,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Audit rows accumulate locally and flush in a dedicated DI scope at the end. Keeps
        // audit IO from implicitly committing pending changes on the snapshot scope and gives
        // operators a durable trail regardless of failure mode.
        var pendingAudits = new List<MLCalibrationLog>(4);

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

            var summary = ComputeCalibrationSummary(
                samples, settings.BootstrapResamples, nowUtc,
                settings.TimeDecayHalfLifeDays, settings.MinSamplesForTimeDecay);
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

            await PersistSummaryAsync(db, model, settings, summary, signals, nowUtc, ct);

            bool retrainingQueued = false;
            bool alertDispatched = false;
            bool alertResolved = false;

            if (alertState == MLCalibrationMonitorAlertState.Critical)
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
                diagnostics: BuildDiagnosticsWithBins(summary, signals, settings),
                evaluatedAt: nowUtc);

            // Per-regime breakdown: pool samples by the active regime at PredictedAt and
            // measure ECE per regime. Each regime gets its own audit row so dashboards can
            // see whether miscalibration is regime-localised.
            await EvaluatePerRegimeAsync(db, model, samples, settings, nowUtc, pendingAudits, ct);

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
        DateTime nowUtc,
        List<MLCalibrationLog> pendingAudits,
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

            var regimeSummary = ComputeCalibrationSummary(
                regimeSamples, settings.BootstrapResamples, nowUtc,
                settings.TimeDecayHalfLifeDays, settings.MinSamplesForTimeDecay);
            // Per-regime trend signal reads the prior per-regime ECE from the audit log so
            // regime drift is detected even when the global trend is flat. Returns null on
            // first cycle for a given regime, in which case the trend signal stays inert.
            double? regimePreviousEce = await LoadSmoothedPreviousEceAsync(
                db, model.Id, regime, settings.TrendSmoothingWindow, ct);
            var regimeSignals = BuildSignals(
                regimeSummary.CurrentEce,
                regimeSummary.EceStderr,
                previousEce: regimePreviousEce,
                baselineEce: TryResolveBaselineEce(model.ModelBytes),
                settings.MaxEce,
                settings.DegradationDelta,
                settings.RegressionGuardK);
            var regimeState = ResolveAlertState(regimeSummary.CurrentEce, regimeSignals, settings);

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
                diagnostics: BuildDiagnosticsWithBins(regimeSummary, regimeSignals, settings),
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
        int minSamplesForTimeDecay)
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

        double eceStderr = ComputeBootstrapEceStderr(samples, bootstrapResamples, nowUtc, effectiveHalfLife);

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
        double effectiveHalfLifeDays)
    {
        if (resamples <= 0 || samples.Count < 2) return 0.0;

        // Deterministic seed: derived from sample count + first/last outcome timestamps so
        // identical inputs yield identical stderr across runs.
        long seed = samples.Count;
        if (samples.Count > 0) seed ^= samples[0].OutcomeAt.Ticks;
        if (samples.Count > 1) seed ^= samples[^1].OutcomeAt.Ticks;
        var rng = new Random(unchecked((int)seed));

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

    private async Task PersistSummaryAsync(
        DbContext db,
        ActiveModelCandidate model,
        MLCalibrationMonitorWorkerSettings settings,
        CalibrationSummary summary,
        CalibrationSignals signals,
        DateTime nowUtc,
        CancellationToken ct)
    {
        string modelPrefix = $"MLCalibration:Model:{model.Id}";

        var specs = new List<EngineConfigUpsertSpec>(12)
        {
            new($"{modelPrefix}:CurrentEce",
                summary.CurrentEce.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Current live Expected Calibration Error for this ML model.",
                false),
            new($"{modelPrefix}:ResolvedCount",
                summary.ResolvedCount.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Resolved prediction count contributing to the latest ML calibration measurement.",
                false),
            new($"{modelPrefix}:Accuracy",
                summary.Accuracy.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Observed correctness rate across the latest resolved predictions used by MLCalibrationMonitorWorker.",
                false),
            new($"{modelPrefix}:MeanConfidence",
                summary.MeanConfidence.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Mean predicted-class confidence across the latest resolved predictions used by MLCalibrationMonitorWorker.",
                false),
            new($"{modelPrefix}:TrendDelta",
                signals.TrendDelta.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Current minus previous live ECE delta for this ML model.",
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

        if (signals.PreviousEce.HasValue)
        {
            specs.Add(new($"{modelPrefix}:PreviousEce",
                signals.PreviousEce.Value.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Previous live Expected Calibration Error measurement for this ML model.",
                false));
        }

        if (signals.BaselineEce.HasValue)
        {
            specs.Add(new($"{modelPrefix}:BaselineEce",
                signals.BaselineEce.Value.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Training-time baseline ECE loaded from the persisted model snapshot.",
                false));
            specs.Add(new($"{modelPrefix}:BaselineDelta",
                signals.BaselineDelta.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Current live ECE minus training-time snapshot ECE for this ML model.",
                false));
        }

        if (settings.WriteLegacyAlias)
        {
            string legacyPrefix = $"MLCalibration:{model.Symbol}:{model.Timeframe}";
            specs.Add(new($"{legacyPrefix}:CurrentEce",
                summary.CurrentEce.ToString("F6", CultureInfo.InvariantCulture),
                ConfigDataType.Decimal,
                "Legacy alias for the active model's current live ECE in this symbol/timeframe context.",
                false));
            specs.Add(new($"{legacyPrefix}:ModelId",
                model.Id.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "ML model id currently backing the legacy MLCalibration symbol/timeframe aliases.",
                false));
        }

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
            CK_WriteLegacyAlias,
            CK_TimeDecayHalfLifeDays, CK_MinSamplesForTimeDecay,
            CK_TrendSmoothingWindow, CK_StaleSkipAlertThreshold,
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
            WriteLegacyAlias: GetBool(values, CK_WriteLegacyAlias, true),
            TimeDecayHalfLifeDays: ClampDoubleAllowingZero(GetDouble(values, CK_TimeDecayHalfLifeDays, DefaultTimeDecayHalfLifeDays),
                DefaultTimeDecayHalfLifeDays, MinTimeDecayHalfLifeDays, MaxTimeDecayHalfLifeDays),
            MinSamplesForTimeDecay: ClampIntAllowingZero(GetInt(values, CK_MinSamplesForTimeDecay, DefaultMinSamplesForTimeDecay),
                DefaultMinSamplesForTimeDecay, MinMinSamplesForTimeDecay, MaxMinSamplesForTimeDecay),
            TrendSmoothingWindow: ClampInt(GetInt(values, CK_TrendSmoothingWindow, DefaultTrendSmoothingWindow),
                DefaultTrendSmoothingWindow, MinTrendSmoothingWindow, MaxTrendSmoothingWindow),
            StaleSkipAlertThreshold: ClampInt(GetInt(values, CK_StaleSkipAlertThreshold, DefaultStaleSkipAlertThreshold),
                DefaultStaleSkipAlertThreshold, MinStaleSkipAlertThreshold, MaxStaleSkipAlertThreshold));
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

    private static double? TryResolveBaselineEce(byte[]? modelBytes)
    {
        if (modelBytes is not { Length: > 0 })
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(modelBytes);
            if (snapshot is null || !double.IsFinite(snapshot.Ece) || snapshot.Ece < 0.0)
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
        return rows.Average();
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
        MLCalibrationMonitorWorkerSettings settings)
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
    bool WriteLegacyAlias,
    double TimeDecayHalfLifeDays,
    int MinSamplesForTimeDecay,
    int TrendSmoothingWindow,
    int StaleSkipAlertThreshold);

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
