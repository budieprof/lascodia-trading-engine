using System.Diagnostics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Common.WorkerGroups;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Conformal Temporal Circuit Breaker. Monitors realised conformal coverage for active models
/// and temporarily suppresses models whose prediction sets repeatedly fail to contain the
/// eventual outcome.
/// </summary>
/// <remarks>
/// The previous implementation had three worker-local gaps:
/// it treated expired breakers as still-active during evaluation, which could suppress an
/// immediate re-trip in the same cycle; it reconstructed legacy coverage using the current
/// calibration threshold even when the served calibration identity was known; and it created
/// breaker alerts without a durable resolution/upsert path, leaving active alert rows behind
/// and never persisting dispatcher-updated trigger timestamps.
///
/// This worker now evaluates on the authoritative write side, ignores expired breakers during
/// model evaluation, reconstructs fallback coverage using the served calibration record when
/// available, runs under the ML-monitoring bulkhead with backoff/startup staggering, and keeps
/// conformal breaker alerts in a proper trip/resolve lifecycle.
/// </remarks>
public sealed class MLConformalBreakerWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLConformalBreakerWorker);
    private const string DistributedLockKey = "ml:conformal-breaker:cycle";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MLConformalBreakerWorker> _logger;
    private readonly MLConformalBreakerOptions _options;
    private readonly TradingMetrics _metrics;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly IMLConformalCoverageEvaluator _coverageEvaluator;
    private readonly IMLConformalPredictionLogReader _predictionLogReader;
    private readonly IMLConformalCalibrationReader _calibrationReader;
    private readonly IMLConformalBreakerStateStore _stateStore;
    private readonly IDistributedLock? _distributedLock;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;

    private int _consecutiveFailures;
    private bool _missingDistributedLockWarningEmitted;

    internal readonly record struct MLConformalBreakerWorkerSettings(
        TimeSpan InitialDelay,
        TimeSpan PollInterval,
        int PollJitterSeconds,
        int MaxLogs,
        int MinLogs,
        int ConsecutiveUncoveredTrigger,
        double CoverageTolerance,
        int MaxSuspensionBars,
        int ModelBatchSize,
        int MaxCycleModels,
        int MaxCalibrationAgeDays,
        bool RequireCalibrationAfterModelActivation,
        int LockTimeoutSeconds,
        double ThresholdMismatchEpsilon,
        bool UseWilsonCoverageFloor,
        double WilsonConfidenceLevel,
        double StatisticalAlpha);

    internal readonly record struct MLConformalBreakerCycleResult(
        MLConformalBreakerWorkerSettings Settings,
        string? SkippedReason,
        int CandidateModelCount,
        int EvaluatedModelCount,
        int SkippedNoCalibrationCount,
        int SkippedInsufficientCount,
        int TrippedCount,
        int RefreshedCount,
        int RecoveredCount,
        int ExpiredCount,
        int ActiveBreakers,
        int AlertDispatchCount)
    {
        public static MLConformalBreakerCycleResult Skipped(
            MLConformalBreakerWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public MLConformalBreakerWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MLConformalBreakerWorker> logger,
        MLConformalBreakerOptions options,
        TradingMetrics metrics,
        IAlertDispatcher alertDispatcher,
        IMLConformalCoverageEvaluator coverageEvaluator,
        IMLConformalPredictionLogReader predictionLogReader,
        IMLConformalCalibrationReader calibrationReader,
        IMLConformalBreakerStateStore stateStore,
        IDistributedLock? distributedLock = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
        _metrics = metrics;
        _alertDispatcher = alertDispatcher;
        _coverageEvaluator = coverageEvaluator;
        _predictionLogReader = predictionLogReader;
        _calibrationReader = calibrationReader;
        _stateStore = stateStore;
        _distributedLock = distributedLock;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = BuildSettings(_options);

        _logger.LogInformation("{Worker} started.", WorkerName);
        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Monitors realised conformal coverage and suppresses miscalibrated active ML models.",
            settings.PollInterval);

        try
        {
            var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName) + settings.InitialDelay;
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, _timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                _healthMonitor?.RecordWorkerHeartbeat(WorkerName);
                var cycleStopwatch = Stopwatch.StartNew();

                try
                {
                    var result = await RunCycleAsync(stoppingToken);
                    long durationMs = (long)cycleStopwatch.Elapsed.TotalMilliseconds;

                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.CandidateModelCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _consecutiveFailures = 0;
                    }

                    if (!string.IsNullOrWhiteSpace(result.SkippedReason))
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _metrics.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName));
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _logger.LogError(ex, "{Worker}: cycle failed.", WorkerName);
                }

                var currentSettings = BuildSettings(_options);
                await Task.Delay(
                    CalculateDelay(GetIntervalWithJitter(currentSettings), _consecutiveFailures),
                    _timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("{Worker} stopping.", WorkerName);
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
        }
    }

    internal Task RunAsync(CancellationToken ct) => RunCycleAsync(ct);

    internal async Task<MLConformalBreakerCycleResult> RunCycleAsync(CancellationToken ct)
    {
        var settings = BuildSettings(_options);
        string cycleId = Guid.NewGuid().ToString("N");

        using var logScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["Worker"] = WorkerName,
            ["WorkerCycleId"] = cycleId
        });

        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var writeDb = writeContext.GetDbContext();

        IAsyncDisposable? cycleLock = null;
        if (_distributedLock is null)
        {
            _metrics.MLConformalBreakerLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "unavailable"));

            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate conformal-breaker cycles are possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
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
                _metrics.MLConformalBreakerLockAttempts.Add(
                    1,
                    new KeyValuePair<string, object?>("outcome", "busy"));
                _metrics.MLConformalBreakerCyclesSkipped.Add(
                    1,
                    new KeyValuePair<string, object?>("reason", "lock_busy"));
                return MLConformalBreakerCycleResult.Skipped(settings, "lock_busy");
            }

            _metrics.MLConformalBreakerLockAttempts.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "acquired"));
        }

        await using (cycleLock)
        {
            await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
            try
            {
                return await RunCycleCoreAsync(cycleId, writeContext, writeDb, settings, ct);
            }
            finally
            {
                WorkerBulkhead.MLMonitoring.Release();
            }
        }
    }

    internal static TimeSpan CalculateDelay(TimeSpan baseInterval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return baseInterval <= TimeSpan.Zero ? TimeSpan.FromHours(24) : baseInterval;

        var cappedExponent = Math.Min(consecutiveFailures - 1, 30);
        var delayedSeconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, cappedExponent);
        return TimeSpan.FromSeconds(Math.Min(delayedSeconds, MaxRetryDelay.TotalSeconds));
    }

    private async Task<MLConformalBreakerCycleResult> RunCycleCoreAsync(
        string cycleId,
        IWriteApplicationDbContext writeContext,
        DbContext writeDb,
        MLConformalBreakerWorkerSettings settings,
        CancellationToken ct)
    {
        var cycleStart = Stopwatch.GetTimestamp();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var calibrationOptions = new ConformalCalibrationSelectionOptions(
            settings.MinLogs,
            nowUtc,
            settings.MaxCalibrationAgeDays,
            settings.RequireCalibrationAfterModelActivation);

        var activeModels = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer)
            .OrderBy(m => m.Id)
            .Take(settings.MaxCycleModels)
            .ToListAsync(ct);

        _healthMonitor?.RecordBacklogDepth(WorkerName, activeModels.Count);

        if (activeModels.Count == 0)
        {
            _metrics.MLConformalBreakerCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return MLConformalBreakerCycleResult.Skipped(settings, "no_active_models");
        }

        var tripCandidates = new List<BreakerTripCandidate>();
        var recoveryCandidates = new List<BreakerRecoveryCandidate>();
        var refreshCandidates = new List<BreakerRefreshCandidate>();
        int evaluated = 0;
        int skippedNoCalibration = 0;
        int skippedInsufficient = 0;
        int duplicateBreakerCount = 0;

        foreach (var modelBatch in activeModels.Chunk(settings.ModelBatchSize))
        {
            var batchModels = modelBatch.ToArray();
            var modelIds = batchModels.Select(m => m.Id).ToArray();

            var activeBreakers = await writeDb.Set<MLConformalBreakerLog>()
                .AsNoTracking()
                .Where(b => modelIds.Contains(b.MLModelId)
                            && b.IsActive
                            && !b.IsDeleted
                            && b.ResumeAt > nowUtc)
                .OrderByDescending(b => b.SuspendedAt)
                .ThenByDescending(b => b.Id)
                .ToListAsync(ct);

            duplicateBreakerCount += activeBreakers.Count
                - activeBreakers.GroupBy(b => new { b.MLModelId, b.Symbol, b.Timeframe }).Count();

            var activeBreakerByModelId = activeBreakers
                .GroupBy(b => b.MLModelId)
                .ToDictionary(g => g.Key, g => g.First());

            var calibrationByModelId = await _calibrationReader.LoadLatestUsableByModelAsync(
                writeDb,
                batchModels,
                calibrationOptions,
                ct);
            var latestCalibrationCandidateByModelId = await LoadLatestCalibrationCandidatesAsync(writeDb, modelIds, ct);
            var logsByModelId = await _predictionLogReader.LoadRecentResolvedLogsByModelAsync(writeDb, modelIds, settings.MaxLogs, ct);
            var calibrationThresholdById = await LoadCalibrationThresholdsByIdAsync(writeDb, logsByModelId, ct);

            foreach (var model in batchModels)
            {
                ct.ThrowIfCancellationRequested();

                if (!calibrationByModelId.TryGetValue(model.Id, out var calibration))
                {
                    skippedNoCalibration++;
                    latestCalibrationCandidateByModelId.TryGetValue(model.Id, out var latestCalibration);
                    var skipReason = MLConformalCalibrationReader.GetSkipReason(model, latestCalibration, calibrationOptions)
                        ?? ConformalCalibrationSkipReason.Missing;

                    _metrics.MLConformalBreakerModelsSkipped.Add(
                        1,
                        new("reason", skipReason.ToString()),
                        new("symbol", model.Symbol),
                        new("timeframe", model.Timeframe.ToString()));
                    _logger.LogDebug(
                        "{Worker}: skipped model {ModelId} {Symbol}/{Timeframe}; unusable conformal calibration reason={Reason}.",
                        WorkerName,
                        model.Id,
                        model.Symbol,
                        model.Timeframe,
                        skipReason);
                    continue;
                }

                if (!logsByModelId.TryGetValue(model.Id, out var recentLogs))
                    recentLogs = [];

                activeBreakerByModelId.TryGetValue(model.Id, out var activeBreaker);
                var eligibleLogs = recentLogs
                    .Where(l => activeBreaker is null || l.OutcomeRecordedAt > activeBreaker.SuspendedAt)
                    .OrderBy(l => l.OutcomeRecordedAt)
                    .ThenBy(l => l.Id)
                    .ToList();

                RecordThresholdMismatchRate(model, calibration, eligibleLogs, settings);

                var observations = eligibleLogs
                    .Select(l => TryCreateObservation(l, calibration.CoverageThreshold, calibrationThresholdById))
                    .Where(o => o.HasValue)
                    .Select(o => o!.Value)
                    .ToList();

                var evaluation = _coverageEvaluator.Evaluate(
                    observations,
                    new ConformalCoverageEvaluationOptions(
                        calibration.TargetCoverage,
                        settings.CoverageTolerance,
                        settings.MinLogs,
                        settings.ConsecutiveUncoveredTrigger,
                        settings.UseWilsonCoverageFloor,
                        settings.WilsonConfidenceLevel,
                        settings.StatisticalAlpha));

                if (!evaluation.HasEnoughSamples)
                {
                    skippedInsufficient++;
                    _metrics.MLConformalBreakerModelsSkipped.Add(
                        1,
                        new("reason", "insufficient_logs"),
                        new("symbol", model.Symbol),
                        new("timeframe", model.Timeframe.ToString()));
                    _logger.LogDebug(
                        "{Worker}: skipped model {ModelId} {Symbol}/{Timeframe}; only {Count} usable conformal logs.",
                        WorkerName,
                        model.Id,
                        model.Symbol,
                        model.Timeframe,
                        evaluation.SampleCount);
                    continue;
                }

                evaluated++;
                _metrics.MLConformalBreakerModelsEvaluated.Add(
                    1,
                    new("symbol", model.Symbol),
                    new("timeframe", model.Timeframe.ToString()));
                _metrics.MLConformalBreakerEmpiricalCoverage.Record(
                    evaluation.EmpiricalCoverage,
                    new("symbol", model.Symbol),
                    new("timeframe", model.Timeframe.ToString()));

                if (activeBreaker is not null)
                {
                    if (!evaluation.ShouldTrip)
                    {
                        recoveryCandidates.Add(new BreakerRecoveryCandidate(
                            activeBreaker.Id,
                            model.Id,
                            model.Symbol,
                            model.Timeframe,
                            evaluation));
                    }
                    else
                    {
                        refreshCandidates.Add(new BreakerRefreshCandidate(
                            activeBreaker.Id,
                            model.Id,
                            model.Symbol,
                            model.Timeframe,
                            evaluation,
                            calibration.CoverageThreshold,
                            calibration.TargetCoverage));
                    }

                    continue;
                }

                if (!evaluation.ShouldTrip)
                    continue;

                int severityBars = Math.Max(
                    evaluation.ConsecutivePoorCoverageBars,
                    evaluation.TrippedByCoverageFloor ? settings.ConsecutiveUncoveredTrigger : 0);
                int suspensionBars = Math.Min(Math.Max(severityBars * 2, 1), settings.MaxSuspensionBars);

                tripCandidates.Add(new BreakerTripCandidate(
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    evaluation,
                    calibration.CoverageThreshold,
                    calibration.TargetCoverage,
                    suspensionBars));
            }
        }

        if (duplicateBreakerCount > 0)
        {
            _logger.LogWarning(
                "{Worker}: detected {DuplicateCount} duplicate active breaker rows across active models; state store will repair duplicates.",
                WorkerName,
                duplicateBreakerCount);
        }

        var stateResult = await _stateStore.ApplyAsync(
            writeDb,
            DeduplicateTripCandidates(tripCandidates),
            DeduplicateRecoveryCandidates(recoveryCandidates),
            DeduplicateRefreshCandidates(refreshCandidates),
            ct);

        int alertDispatches = 0;
        foreach (var dispatch in stateResult.Alerts)
        {
            bool saveChanges = false;

            try
            {
                if (dispatch.Kind == BreakerAlertDispatchKind.Trip)
                {
                    await _alertDispatcher.DispatchAsync(dispatch.Alert, dispatch.Message, ct);
                    _metrics.MLConformalBreakerAlertsDispatched.Add(1);
                    alertDispatches++;
                    saveChanges = true;
                }
                else
                {
                    if (dispatch.Alert.LastTriggeredAt.HasValue)
                        await _alertDispatcher.TryAutoResolveAsync(dispatch.Alert, conditionStillActive: false, ct);

                    dispatch.Alert.AutoResolvedAt ??= nowUtc;
                    saveChanges = true;
                }
            }
            catch (Exception ex)
            {
                _metrics.MLConformalBreakerAlertDispatchFailures.Add(1);
                _logger.LogWarning(
                    ex,
                    "{Worker}: alert dispatch failed for kind {Kind}, symbol {Symbol}, dedup {DeduplicationKey}.",
                    WorkerName,
                    dispatch.Kind,
                    dispatch.Alert.Symbol,
                    dispatch.Alert.DeduplicationKey);

                if (dispatch.Kind == BreakerAlertDispatchKind.Resolve)
                {
                    dispatch.Alert.AutoResolvedAt ??= nowUtc;
                    saveChanges = true;
                }
            }

            if (saveChanges)
                await writeContext.SaveChangesAsync(ct);
        }

        _metrics.MLConformalBreakerActive.Record(stateResult.ActiveBreakers);

        double durationMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
        _metrics.MLConformalBreakerCycleDurationMs.Record(durationMs);
        _metrics.WorkerCycleDurationMs.Record(
            durationMs,
            new KeyValuePair<string, object?>("worker", WorkerName));

        _logger.LogInformation(
            "{Worker}: cycle {CycleId} complete. evaluated={Evaluated} skippedNoCalibration={SkippedCalibration} skippedInsufficient={SkippedInsufficient} tripped={Tripped} refreshed={Refreshed} recovered={Recovered} expired={Expired} duplicateRepairs={DuplicateRepairs} alerts={Alerts} active={Active}",
            WorkerName,
            cycleId,
            evaluated,
            skippedNoCalibration,
            skippedInsufficient,
            stateResult.TrippedCount,
            stateResult.RefreshedCount,
            stateResult.RecoveredCount,
            stateResult.ExpiredCount,
            stateResult.DuplicateActiveBreakersDeactivated,
            alertDispatches,
            stateResult.ActiveBreakers);

        return new MLConformalBreakerCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: activeModels.Count,
            EvaluatedModelCount: evaluated,
            SkippedNoCalibrationCount: skippedNoCalibration,
            SkippedInsufficientCount: skippedInsufficient,
            TrippedCount: stateResult.TrippedCount,
            RefreshedCount: stateResult.RefreshedCount,
            RecoveredCount: stateResult.RecoveredCount,
            ExpiredCount: stateResult.ExpiredCount,
            ActiveBreakers: stateResult.ActiveBreakers,
            AlertDispatchCount: alertDispatches);
    }

    private static bool IsFiniteProbability(double value)
        => double.IsFinite(value) && value >= 0.0 && value <= 1.0;

    private static async Task<IReadOnlyDictionary<long, MLConformalCalibration>> LoadLatestCalibrationCandidatesAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new Dictionary<long, MLConformalCalibration>();

        var calibrations = await db.Set<MLConformalCalibration>()
            .AsNoTracking()
            .Where(c => modelIds.Contains(c.MLModelId) && !c.IsDeleted)
            .OrderByDescending(c => c.CalibratedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        return calibrations
            .GroupBy(c => c.MLModelId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private static async Task<IReadOnlyDictionary<long, double>> LoadCalibrationThresholdsByIdAsync(
        DbContext db,
        IReadOnlyDictionary<long, List<MLModelPredictionLog>> logsByModelId,
        CancellationToken ct)
    {
        var calibrationIds = logsByModelId.Values
            .SelectMany(logs => logs)
            .Where(log => !log.ConformalThresholdUsed.HasValue && log.MLConformalCalibrationId.HasValue)
            .Select(log => log.MLConformalCalibrationId!.Value)
            .Distinct()
            .ToArray();

        if (calibrationIds.Length == 0)
            return new Dictionary<long, double>();

        var calibrations = await db.Set<MLConformalCalibration>()
            .AsNoTracking()
            .Where(c => calibrationIds.Contains(c.Id) && !c.IsDeleted)
            .ToListAsync(ct);

        return calibrations
            .Where(c => IsFiniteProbability(c.CoverageThreshold))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().CoverageThreshold);
    }

    private void RecordThresholdMismatchRate(
        MLModel model,
        MLConformalCalibration calibration,
        IReadOnlyCollection<MLModelPredictionLog> logs,
        MLConformalBreakerWorkerSettings settings)
    {
        var thresholdedLogs = logs
            .Where(l => l.ConformalThresholdUsed.HasValue && IsFiniteProbability(l.ConformalThresholdUsed.Value))
            .ToArray();
        if (thresholdedLogs.Length == 0)
            return;

        int mismatches = thresholdedLogs.Count(l =>
            Math.Abs(l.ConformalThresholdUsed!.Value - calibration.CoverageThreshold)
                > settings.ThresholdMismatchEpsilon);
        double rate = mismatches / (double)thresholdedLogs.Length;
        _metrics.MLConformalBreakerThresholdMismatchRate.Record(
            rate,
            new("symbol", model.Symbol),
            new("timeframe", model.Timeframe.ToString()));

        if (rate > 0)
        {
            _logger.LogDebug(
                "{Worker}: threshold mismatch rate {Rate:P2} for model {ModelId} {Symbol}/{Timeframe}.",
                WorkerName,
                rate,
                model.Id,
                model.Symbol,
                model.Timeframe);
        }
    }

    private static IReadOnlyCollection<BreakerTripCandidate> DeduplicateTripCandidates(
        IReadOnlyCollection<BreakerTripCandidate> candidates)
        => candidates
            .GroupBy(c => new { c.MLModelId, c.Symbol, c.Timeframe })
            .Select(g => g.OrderByDescending(c => c.Evaluation.LastEvaluatedOutcomeAt ?? DateTime.MinValue).First())
            .ToArray();

    private static IReadOnlyCollection<BreakerRecoveryCandidate> DeduplicateRecoveryCandidates(
        IReadOnlyCollection<BreakerRecoveryCandidate> candidates)
        => candidates
            .GroupBy(c => new { c.MLModelId, c.Symbol, c.Timeframe })
            .Select(g => g.OrderByDescending(c => c.Evaluation.LastEvaluatedOutcomeAt ?? DateTime.MinValue).First())
            .ToArray();

    private static IReadOnlyCollection<BreakerRefreshCandidate> DeduplicateRefreshCandidates(
        IReadOnlyCollection<BreakerRefreshCandidate> candidates)
        => candidates
            .GroupBy(c => new { c.MLModelId, c.Symbol, c.Timeframe })
            .Select(g => g.OrderByDescending(c => c.Evaluation.LastEvaluatedOutcomeAt ?? DateTime.MinValue).First())
            .ToArray();

    private static ConformalObservation? TryCreateObservation(
        MLModelPredictionLog log,
        double fallbackThreshold,
        IReadOnlyDictionary<long, double> calibrationThresholdById)
    {
        if (log.WasConformalCovered.HasValue)
            return new ConformalObservation(log.WasConformalCovered.Value, log.OutcomeRecordedAt);

        bool? coveredBySet = log.ActualDirection.HasValue
            ? MLFeatureHelper.WasActualDirectionInConformalSet(
                log.ConformalPredictionSetJson,
                log.ActualDirection.Value)
            : null;
        if (coveredBySet.HasValue)
            return new ConformalObservation(coveredBySet.Value, log.OutcomeRecordedAt);

        double threshold = log.ConformalThresholdUsed
            ?? (log.MLConformalCalibrationId.HasValue
                && calibrationThresholdById.TryGetValue(log.MLConformalCalibrationId.Value, out var calibrationThreshold)
                    ? calibrationThreshold
                    : fallbackThreshold);

        double score = log.ConformalNonConformityScore
            ?? (log.ActualDirection.HasValue
                ? MLFeatureHelper.ComputeLoggedConformalNonConformityScore(
                    log,
                    log.ActualDirection.Value,
                    threshold)
                : double.NaN);

        return IsFiniteProbability(score) && IsFiniteProbability(threshold)
            ? new ConformalObservation(score <= threshold, log.OutcomeRecordedAt)
            : null;
    }

    internal static TimeSpan GetBarDuration(Timeframe timeframe)
        => TimeframeDurationHelper.BarDuration(timeframe);

    private static MLConformalBreakerWorkerSettings BuildSettings(MLConformalBreakerOptions options)
    {
        int minLogs = ClampInt(options.MinLogs, 30, 10, 100_000);
        int modelBatchSize = ClampInt(options.ModelBatchSize, 250, 1, 10_000);

        return new MLConformalBreakerWorkerSettings(
            InitialDelay: TimeSpan.FromMinutes(ClampInt(options.InitialDelayMinutes, 35, 0, 24 * 60)),
            PollInterval: TimeSpan.FromHours(ClampInt(options.PollIntervalHours, 24, 1, 24 * 7)),
            PollJitterSeconds: ClampInt(options.PollJitterSeconds, 300, 0, 24 * 60 * 60),
            MaxLogs: Math.Max(minLogs, ClampInt(options.MaxLogs, 200, minLogs, 100_000)),
            MinLogs: minLogs,
            ConsecutiveUncoveredTrigger: ClampInt(options.ConsecutiveUncoveredTrigger, 8, 1, 1_000),
            CoverageTolerance: ClampDouble(options.CoverageTolerance, 0.05, 0.0, 0.5),
            MaxSuspensionBars: ClampInt(options.MaxSuspensionBars, 96, 1, 10_000),
            ModelBatchSize: modelBatchSize,
            MaxCycleModels: Math.Max(modelBatchSize, ClampInt(options.MaxCycleModels, 10_000, modelBatchSize, 100_000)),
            MaxCalibrationAgeDays: ClampInt(options.MaxCalibrationAgeDays, 30, 1, 3_650),
            RequireCalibrationAfterModelActivation: options.RequireCalibrationAfterModelActivation,
            LockTimeoutSeconds: ClampInt(options.LockTimeoutSeconds, 5, 0, 300),
            ThresholdMismatchEpsilon: ClampDouble(options.ThresholdMismatchEpsilon, 0.000001, 0.0, 1.0),
            UseWilsonCoverageFloor: options.UseWilsonCoverageFloor,
            WilsonConfidenceLevel: ClampDouble(options.WilsonConfidenceLevel, 0.95, 0.51, 0.999999),
            StatisticalAlpha: ClampDouble(options.StatisticalAlpha, 0.01, 1e-12, 0.49));
    }

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => value < min || value > max ? defaultValue : value;

    private static double ClampDouble(double value, double defaultValue, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? defaultValue : value;

    private static TimeSpan GetIntervalWithJitter(MLConformalBreakerWorkerSettings settings)
        => settings.PollJitterSeconds == 0
            ? settings.PollInterval
            : settings.PollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, settings.PollJitterSeconds + 1));
}
