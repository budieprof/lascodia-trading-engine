using System.Diagnostics;
using System.Globalization;
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
/// Correctness fixes from the prior implementation:
/// expired breakers are filtered at evaluation time so a model can re-trip the same cycle its
/// suspension expires; coverage is reconstructed using the served calibration record (by id)
/// rather than the current threshold for legacy logs that lack their own threshold; alerts go
/// through a proper trip/resolve lifecycle with dispatcher-updated trigger timestamps
/// persisted exactly once at end of cycle.
///
/// <para><b>Cycle structure:</b> all DB loads (active models, active breakers, latest usable
/// calibrations, latest calibration candidates, prediction logs, served-calibration thresholds,
/// per-model rotation cursor + trip-streak counters) happen up-front in a fixed number of
/// round-trips, regardless of how many models are evaluated. Per-batch evaluation is purely
/// in-memory. The ML-monitoring bulkhead is held for the heavy DB phase only; alert dispatch
/// (which can block on slow webhooks) runs outside the bulkhead.</para>
///
/// <para><b>Fair rotation:</b> when active models exceed <c>MaxCycleModels</c>, the worker
/// orders by ascending <c>MLConformal:Model:{id}:LastEvaluatedAt</c> so the
/// least-recently-evaluated models are picked next cycle. Models without a stored cursor sort
/// first (DateTime.MinValue), so newly-activated models are picked up immediately.</para>
///
/// <para><b>Alert backpressure:</b> trip alerts are gated by <c>MaxAlertsPerCycle</c>. Resolves
/// always dispatch — operators always want to know about cleared suspensions, and the volume
/// is naturally bounded by the trip volume.</para>
///
/// <para><b>Chronic-tripper escalation:</b> the worker tracks consecutive trip cycles per
/// <c>(model, symbol, timeframe)</c>. When the streak crosses <c>ChronicTripThreshold</c>,
/// a separate <see cref="AlertType.MLModelDegraded"/> alert with prefix
/// <c>ml-conformal-chronic-trip:</c> fires once. The alert auto-resolves on the first cycle
/// the model recovers. Use this signal to drive model-retirement workflows rather than
/// letting the breaker re-trip indefinitely.</para>
/// </remarks>
public sealed class MLConformalBreakerWorker : BackgroundService
{
    internal const string WorkerName = nameof(MLConformalBreakerWorker);
    private const string DistributedLockKey = "ml:conformal-breaker:cycle";
    private const string ChronicTripDeduplicationPrefix = "ml-conformal-chronic-trip:";
    private const string PostgresProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    // Pre-cached string forms of the enum values used by the atomic-INSERT SQL builder.
    // Avoids per-call .ToString() allocations on the chronic-trip alert path.
    private static readonly string AlertTypeMLModelDegradedName = AlertType.MLModelDegraded.ToString();
    private static readonly string AlertSeverityHighName = AlertSeverity.High.ToString();

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
    private readonly IDatabaseExceptionClassifier? _databaseExceptionClassifier;

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
        double StatisticalAlpha,
        int MaxAlertsPerCycle,
        int ChronicTripThreshold);

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
        int AlertDispatchCount,
        int TripAlertBackpressureSkippedCount,
        int ChronicAlertBackpressureSkippedCount,
        int ChronicTripAlertCount)
    {
        public int AlertBackpressureSkippedCount
            => TripAlertBackpressureSkippedCount + ChronicAlertBackpressureSkippedCount;

        public static MLConformalBreakerCycleResult Skipped(
            MLConformalBreakerWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private readonly record struct ActiveBreakerSnapshot(
        long Id,
        long MLModelId,
        string Symbol,
        Timeframe Timeframe,
        DateTime SuspendedAt,
        DateTime ResumeAt);

    private readonly record struct PerModelState(
        Dictionary<long, DateTime> LastEvaluatedAt,
        Dictionary<long, int> TripStreak,
        HashSet<long> ActiveChronicTripAlertModelIds);

    private sealed class AlertBudget
    {
        private int _remaining;

        public AlertBudget(int capacity)
        {
            _remaining = capacity;
        }

        public bool HasCapacity => _remaining > 0;

        public bool TryConsume()
        {
            if (_remaining <= 0)
                return false;

            _remaining--;
            return true;
        }
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
        IWorkerHealthMonitor? healthMonitor = null,
        IDatabaseExceptionClassifier? databaseExceptionClassifier = null)
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
        _databaseExceptionClassifier = databaseExceptionClassifier;
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
            return await RunCycleCoreAsync(cycleId, writeContext, writeDb, settings, ct);
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

        // ── Phase 1: cycle-wide DB loads under the bulkhead (heavy queries). ──
        BreakerStateResult stateResult;
        EvaluationOutcome evaluationOutcome;
        await WorkerBulkhead.MLMonitoring.WaitAsync(ct);
        try
        {
            evaluationOutcome = await EvaluateActiveModelsAsync(
                writeDb, settings, calibrationOptions, nowUtc, ct);

            if (evaluationOutcome.SkipReason is not null)
                return MLConformalBreakerCycleResult.Skipped(settings, evaluationOutcome.SkipReason);

            stateResult = await _stateStore.ApplyAsync(
                writeDb,
                evaluationOutcome.TripCandidates,
                evaluationOutcome.RecoveryCandidates,
                evaluationOutcome.RefreshCandidates,
                ct);

            await PersistPerModelCursorsAsync(
                writeDb,
                evaluationOutcome.EvaluatedOrSkippedModelIds,
                evaluationOutcome.UpdatedTripStreaks,
                evaluationOutcome.PerModelState.TripStreak,
                nowUtc,
                ct);
        }
        finally
        {
            WorkerBulkhead.MLMonitoring.Release();
        }

        // ── Phase 2: alert dispatch outside the bulkhead so slow webhooks don't ──
        // ── block other ML-monitoring workers. ──
        var dispatchOutcome = await DispatchAlertsAsync(
            writeContext,
            stateResult.Alerts,
            evaluationOutcome.UpdatedTripStreaks,
            evaluationOutcome.PerModelState.ActiveChronicTripAlertModelIds,
            evaluationOutcome.ContextByModelId,
            settings,
            nowUtc,
            ct);

        _metrics.MLConformalBreakerActive.Record(stateResult.ActiveBreakers);

        double durationMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
        _metrics.MLConformalBreakerCycleDurationMs.Record(durationMs);
        _metrics.WorkerCycleDurationMs.Record(
            durationMs,
            new KeyValuePair<string, object?>("worker", WorkerName));

        _logger.LogInformation(
            "{Worker}: cycle {CycleId} complete. evaluated={Evaluated} skippedNoCalibration={SkippedCalibration} skippedInsufficient={SkippedInsufficient} tripped={Tripped} refreshed={Refreshed} recovered={Recovered} expired={Expired} duplicateRepairs={DuplicateRepairs} alerts={Alerts} tripAlertBackpressureSkipped={TripBackpressure} chronicAlertBackpressureSkipped={ChronicBackpressure} chronicTripAlerts={ChronicTripAlerts} active={Active}",
            WorkerName,
            cycleId,
            evaluationOutcome.EvaluatedCount,
            evaluationOutcome.SkippedNoCalibrationCount,
            evaluationOutcome.SkippedInsufficientCount,
            stateResult.TrippedCount,
            stateResult.RefreshedCount,
            stateResult.RecoveredCount,
            stateResult.ExpiredCount,
            stateResult.DuplicateActiveBreakersDeactivated,
            dispatchOutcome.AlertDispatchCount,
            dispatchOutcome.TripAlertBackpressureSkippedCount,
            dispatchOutcome.ChronicAlertBackpressureSkippedCount,
            dispatchOutcome.ChronicTripAlertCount,
            stateResult.ActiveBreakers);

        return new MLConformalBreakerCycleResult(
            settings,
            SkippedReason: null,
            CandidateModelCount: evaluationOutcome.CandidateCount,
            EvaluatedModelCount: evaluationOutcome.EvaluatedCount,
            SkippedNoCalibrationCount: evaluationOutcome.SkippedNoCalibrationCount,
            SkippedInsufficientCount: evaluationOutcome.SkippedInsufficientCount,
            TrippedCount: stateResult.TrippedCount,
            RefreshedCount: stateResult.RefreshedCount,
            RecoveredCount: stateResult.RecoveredCount,
            ExpiredCount: stateResult.ExpiredCount,
            ActiveBreakers: stateResult.ActiveBreakers,
            AlertDispatchCount: dispatchOutcome.AlertDispatchCount,
            TripAlertBackpressureSkippedCount: dispatchOutcome.TripAlertBackpressureSkippedCount,
            ChronicAlertBackpressureSkippedCount: dispatchOutcome.ChronicAlertBackpressureSkippedCount,
            ChronicTripAlertCount: dispatchOutcome.ChronicTripAlertCount);
    }

    private sealed class EvaluationOutcome
    {
        public required string? SkipReason { get; init; }
        public required int CandidateCount { get; init; }
        public required int EvaluatedCount { get; init; }
        public required int SkippedNoCalibrationCount { get; init; }
        public required int SkippedInsufficientCount { get; init; }
        public required IReadOnlyCollection<BreakerTripCandidate> TripCandidates { get; init; }
        public required IReadOnlyCollection<BreakerRecoveryCandidate> RecoveryCandidates { get; init; }
        public required IReadOnlyCollection<BreakerRefreshCandidate> RefreshCandidates { get; init; }
        public required Dictionary<long, int> UpdatedTripStreaks { get; init; }
        public required HashSet<long> EvaluatedOrSkippedModelIds { get; init; }
        public required PerModelState PerModelState { get; init; }
        public required Dictionary<long, ModelContext> ContextByModelId { get; init; }
    }

    internal readonly record struct ModelContext(string Symbol, Timeframe Timeframe);

    private sealed class DispatchOutcome
    {
        public int AlertDispatchCount;
        public int TripAlertBackpressureSkippedCount;
        public int ChronicAlertBackpressureSkippedCount;
        public int ChronicTripAlertCount;
    }

    private async Task<EvaluationOutcome> EvaluateActiveModelsAsync(
        DbContext writeDb,
        MLConformalBreakerWorkerSettings settings,
        ConformalCalibrationSelectionOptions calibrationOptions,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Load just the IDs of the full active set so we can apply fair-rotation ordering
        // before deciding which models to keep in this cycle. At 50k+ active models this
        // projected query stays under ~5 MB in memory; we then load the full MLModel rows
        // only for the cropped subset (capped by MaxCycleModels). The double-query saves
        // memory at the cost of one extra round-trip — important when active model counts
        // are large.
        var allActiveModelIds = await writeDb.Set<MLModel>()
            .AsNoTracking()
            .Where(m => m.IsActive
                        && !m.IsDeleted
                        && !m.IsMetaLearner
                        && !m.IsMamlInitializer)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (allActiveModelIds.Count == 0)
        {
            _metrics.MLConformalBreakerCyclesSkipped.Add(
                1,
                new KeyValuePair<string, object?>("reason", "no_active_models"));
            return EvaluationEmpty(skipReason: "no_active_models", candidateCount: 0);
        }

        // Fair-rotation cursor + chronic-trip state, batch-loaded for the full active set.
        var perModelState = await LoadPerModelStateAsync(writeDb, allActiveModelIds, ct);

        // Sort by least-recently-evaluated first, then take the cycle cap. Models without a
        // stored cursor (DateTime.MinValue) sort first so newly-activated models get picked
        // up immediately.
        var croppedIds = allActiveModelIds
            .OrderBy(id => perModelState.LastEvaluatedAt.GetValueOrDefault(id, DateTime.MinValue))
            .ThenBy(id => id)
            .Take(settings.MaxCycleModels)
            .ToList();

        // Now load full MLModel rows for the cropped set only (chunked for large caps).
        var croppedModels = await LoadInChunksAsync(croppedIds, async chunk =>
            await writeDb.Set<MLModel>()
                .AsNoTracking()
                .Where(m => chunk.Contains(m.Id))
                .ToListAsync(ct));

        // Preserve the rotation order from croppedIds when iterating.
        var croppedModelById = croppedModels.ToDictionary(m => m.Id);
        var orderedCroppedModels = croppedIds
            .Select(id => croppedModelById.TryGetValue(id, out var m) ? m : null)
            .Where(m => m is not null)
            .Cast<MLModel>()
            .ToList();
        croppedModels = orderedCroppedModels;

        _healthMonitor?.RecordBacklogDepth(WorkerName, croppedModels.Count);

        // ── Cycle-wide batched loads (one round-trip each, regardless of model count). ──
        var activeBreakerLoad = await LoadActiveBreakerSnapshotsAsync(
            writeDb, croppedIds, nowUtc, ct);
        var activeBreakerByModelId = activeBreakerLoad.ByModelId;
        var calibrationByModelId = await _calibrationReader.LoadLatestUsableByModelAsync(
            writeDb, croppedModels, calibrationOptions, ct);
        var latestCalibrationCandidateByModelId = await LoadLatestCalibrationCandidatesAsync(
            writeDb, croppedIds, ct);
        var logsByModelId = await _predictionLogReader.LoadRecentResolvedLogsByModelAsync(
            writeDb, croppedIds, settings.MaxLogs, ct);
        var calibrationThresholdById = await LoadCalibrationThresholdsByIdAsync(
            writeDb, logsByModelId, ct);

        // Count duplicate active breakers from the RAW row list — the by-modelId dictionary
        // has already collapsed duplicates by construction, so we have to count before that
        // collapse to surface them. The state store handles the actual repair.
        int duplicateBreakerCount = activeBreakerLoad.RawRows
            .GroupBy(b => new { b.MLModelId, b.Symbol, b.Timeframe })
            .Sum(g => Math.Max(0, g.Count() - 1));
        if (duplicateBreakerCount > 0)
        {
            _logger.LogWarning(
                "{Worker}: detected {DuplicateCount} duplicate active breaker rows across active models; state store will repair duplicates.",
                WorkerName,
                duplicateBreakerCount);
        }

        var tripCandidates = new List<BreakerTripCandidate>();
        var recoveryCandidates = new List<BreakerRecoveryCandidate>();
        var refreshCandidates = new List<BreakerRefreshCandidate>();
        var updatedTripStreaks = new Dictionary<long, int>(perModelState.TripStreak);
        var evaluatedOrSkippedModelIds = new HashSet<long>();
        var contextByModelId = new Dictionary<long, ModelContext>(croppedIds.Count);

        int evaluated = 0;
        int skippedNoCalibration = 0;
        int skippedInsufficient = 0;

        // ── Per-batch in-memory iteration only — no DB calls inside this loop. ──
        foreach (var modelBatch in croppedModels.Chunk(settings.ModelBatchSize))
        {
            foreach (var model in modelBatch)
            {
                ct.ThrowIfCancellationRequested();

                contextByModelId[model.Id] = new ModelContext(model.Symbol, model.Timeframe);
                evaluatedOrSkippedModelIds.Add(model.Id);

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
                    .Where(l => activeBreaker.MLModelId == 0 || l.OutcomeRecordedAt > activeBreaker.SuspendedAt)
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

                bool hasActiveBreaker = activeBreaker.MLModelId != 0;
                if (hasActiveBreaker)
                {
                    if (!evaluation.ShouldTrip)
                    {
                        recoveryCandidates.Add(new BreakerRecoveryCandidate(
                            activeBreaker.Id,
                            model.Id,
                            model.Symbol,
                            model.Timeframe,
                            evaluation));
                        updatedTripStreaks[model.Id] = 0;
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
                        // Refresh-of-active-breaker counts as a continuing trip cycle for
                        // chronic-tripper detection.
                        updatedTripStreaks[model.Id] = updatedTripStreaks.GetValueOrDefault(model.Id) + 1;
                    }

                    continue;
                }

                if (!evaluation.ShouldTrip)
                {
                    // Healthy with no active breaker — reset the streak.
                    updatedTripStreaks[model.Id] = 0;
                    continue;
                }

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
                updatedTripStreaks[model.Id] = updatedTripStreaks.GetValueOrDefault(model.Id) + 1;
            }
        }

        return new EvaluationOutcome
        {
            SkipReason = null,
            CandidateCount = croppedModels.Count,
            EvaluatedCount = evaluated,
            SkippedNoCalibrationCount = skippedNoCalibration,
            SkippedInsufficientCount = skippedInsufficient,
            TripCandidates = DeduplicateTripCandidates(tripCandidates),
            RecoveryCandidates = DeduplicateRecoveryCandidates(recoveryCandidates),
            RefreshCandidates = DeduplicateRefreshCandidates(refreshCandidates),
            UpdatedTripStreaks = updatedTripStreaks,
            EvaluatedOrSkippedModelIds = evaluatedOrSkippedModelIds,
            PerModelState = perModelState,
            ContextByModelId = contextByModelId,
        };
    }

    private static EvaluationOutcome EvaluationEmpty(string? skipReason, int candidateCount) => new()
    {
        SkipReason = skipReason,
        CandidateCount = candidateCount,
        EvaluatedCount = 0,
        SkippedNoCalibrationCount = 0,
        SkippedInsufficientCount = 0,
        TripCandidates = Array.Empty<BreakerTripCandidate>(),
        RecoveryCandidates = Array.Empty<BreakerRecoveryCandidate>(),
        RefreshCandidates = Array.Empty<BreakerRefreshCandidate>(),
        UpdatedTripStreaks = new Dictionary<long, int>(),
        EvaluatedOrSkippedModelIds = new HashSet<long>(),
        PerModelState = new PerModelState(
            new Dictionary<long, DateTime>(),
            new Dictionary<long, int>(),
            new HashSet<long>()),
        ContextByModelId = new Dictionary<long, ModelContext>(),
    };

    private async Task<DispatchOutcome> DispatchAlertsAsync(
        IWriteApplicationDbContext writeContext,
        IReadOnlyCollection<BreakerAlertDispatch> stateAlerts,
        IReadOnlyDictionary<long, int> updatedTripStreaks,
        HashSet<long> activeChronicAlertModelIds,
        IReadOnlyDictionary<long, ModelContext> contextByModelId,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var outcome = new DispatchOutcome();
        var budget = new AlertBudget(settings.MaxAlertsPerCycle);
        bool anyMutations = false;

        foreach (var dispatch in stateAlerts)
        {
            try
            {
                if (dispatch.Kind == BreakerAlertDispatchKind.Trip)
                {
                    if (!budget.TryConsume())
                    {
                        outcome.TripAlertBackpressureSkippedCount++;
                        _metrics.MLConformalBreakerCyclesSkipped.Add(
                            1,
                            new KeyValuePair<string, object?>("reason", "alert_backpressure"));
                        continue;
                    }

                    await _alertDispatcher.DispatchAsync(dispatch.Alert, dispatch.Message, ct);
                    _metrics.MLConformalBreakerAlertsDispatched.Add(1);
                    outcome.AlertDispatchCount++;
                    anyMutations = true;
                }
                else
                {
                    if (dispatch.Alert.LastTriggeredAt.HasValue)
                        await _alertDispatcher.TryAutoResolveAsync(dispatch.Alert, conditionStillActive: false, ct);

                    dispatch.Alert.AutoResolvedAt ??= nowUtc;
                    anyMutations = true;
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
                    // Fail-safe: still mark the alert resolved in DB even if external
                    // notification failed. Otherwise the alert stays "active" and ops sees
                    // a stuck row.
                    dispatch.Alert.AutoResolvedAt ??= nowUtc;
                    anyMutations = true;
                }
            }
        }

        // Chronic-trip escalation: raise once when the streak crosses the threshold; resolve
        // when the model recovers (streak drops back to 0 after having an active alert).
        var dbContext = writeContext.GetDbContext();
        foreach (var (modelId, streak) in updatedTripStreaks)
        {
            if (!contextByModelId.TryGetValue(modelId, out var context))
                continue;

            string dedupKey = ChronicTripDeduplicationPrefix + modelId.ToString(CultureInfo.InvariantCulture);
            bool hasActiveAlert = activeChronicAlertModelIds.Contains(modelId);

            if (streak >= settings.ChronicTripThreshold && !hasActiveAlert)
            {
                if (!budget.TryConsume())
                {
                    outcome.ChronicAlertBackpressureSkippedCount++;
                    continue;
                }

                var chronicAlert = await UpsertChronicTripAlertAsync(
                    dbContext, dedupKey, context, modelId, streak, settings, nowUtc, ct);

                // Per-alert save with dedup-race recovery. Two replicas racing past the
                // distributed lock (or running with no lock available) can both attempt to
                // INSERT the same DeduplicationKey; one will hit the unique-index violation.
                // Detach the doomed Add, re-read the row that won, re-apply our fields, save
                // again. After this, the entity is tracked as Unchanged and any subsequent
                // dispatcher mutation (LastTriggeredAt) gets persisted via the end-of-cycle
                // save naturally.
                try
                {
                    await dbContext.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IsLikelyAlertDeduplicationRace(ex))
                {
                    DetachIfAdded(dbContext, chronicAlert);
                    chronicAlert = await dbContext.Set<Alert>()
                        .FirstAsync(a => !a.IsDeleted
                                      && a.IsActive
                                      && a.DeduplicationKey == dedupKey, ct);
                    ApplyChronicTripAlertFields(chronicAlert, context, modelId, streak, settings, nowUtc);
                    await dbContext.SaveChangesAsync(ct);
                }

                // Membership is now correct: regardless of whether we inserted or recovered
                // from the race, an active chronic-trip alert exists for this model.
                activeChronicAlertModelIds.Add(modelId);

                try
                {
                    await _alertDispatcher.DispatchAsync(
                        chronicAlert,
                        $"ML conformal breaker has tripped for {streak} consecutive cycles on model {modelId} ({context.Symbol}/{context.Timeframe}). Calibration drift may be persistent; consider retiring or recalibrating the model.",
                        ct);
                    outcome.ChronicTripAlertCount++;
                    // Dispatcher mutated LastTriggeredAt → tracked entity is now Modified;
                    // end-of-cycle save will pick it up.
                    anyMutations = true;
                }
                catch (Exception ex)
                {
                    _metrics.MLConformalBreakerAlertDispatchFailures.Add(1);
                    _logger.LogWarning(
                        ex,
                        "{Worker}: chronic-trip alert dispatch failed for model {ModelId} ({Symbol}/{Timeframe}).",
                        WorkerName,
                        modelId,
                        context.Symbol,
                        context.Timeframe);
                }
            }
            else if (streak == 0 && hasActiveAlert)
            {
                // Recovered → auto-resolve the chronic alert.
                var existing = await dbContext.Set<Alert>()
                    .FirstOrDefaultAsync(a => !a.IsDeleted
                                           && a.IsActive
                                           && a.DeduplicationKey == dedupKey, ct);
                if (existing is not null)
                {
                    try
                    {
                        if (existing.LastTriggeredAt.HasValue)
                            await _alertDispatcher.TryAutoResolveAsync(existing, conditionStillActive: false, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            ex,
                            "{Worker}: failed to dispatch chronic-trip resolution for model {ModelId}.",
                            WorkerName,
                            modelId);
                    }
                    existing.IsActive = false;
                    existing.AutoResolvedAt ??= nowUtc;
                    anyMutations = true;
                }
            }
        }

        if (anyMutations)
            await dbContext.SaveChangesAsync(ct);

        return outcome;
    }

    internal static async Task<Alert> UpsertChronicTripAlertAsync(
        DbContext dbContext,
        string dedupKey,
        ModelContext context,
        long modelId,
        int streak,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool isPostgres = string.Equals(dbContext.Database.ProviderName, PostgresProviderName, StringComparison.Ordinal);

        // Postgres: single-statement atomic upsert. INSERT ... ON CONFLICT DO UPDATE SET
        // writes the latest field values whether we win the insert or another replica
        // already inserted. We then re-fetch into the EF tracker so the dispatcher can
        // mutate LastTriggeredAt and the end-of-cycle save can persist that one column —
        // no redundant UPDATE for already-current fields.
        if (isPostgres)
        {
            string conditionJson = BuildChronicTripConditionJson(context, modelId, streak, settings, nowUtc);
            string symbol = context.Symbol;

            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""Alert""
                    (""OutboxId"", ""AlertType"", ""DeduplicationKey"", ""Symbol"", ""Severity"",
                     ""CooldownSeconds"", ""ConditionJson"", ""IsActive"", ""IsDeleted"")
                VALUES
                    (gen_random_uuid(), {AlertTypeMLModelDegradedName}, {dedupKey}, {symbol}, {AlertSeverityHighName},
                     3600, {conditionJson}, true, false)
                ON CONFLICT (""DeduplicationKey"")
                    WHERE ""IsActive"" = TRUE AND ""IsDeleted"" = FALSE AND ""DeduplicationKey"" IS NOT NULL
                DO UPDATE SET
                    ""AlertType"" = EXCLUDED.""AlertType"",
                    ""Symbol"" = EXCLUDED.""Symbol"",
                    ""Severity"" = EXCLUDED.""Severity"",
                    ""CooldownSeconds"" = EXCLUDED.""CooldownSeconds"",
                    ""ConditionJson"" = EXCLUDED.""ConditionJson"",
                    ""AutoResolvedAt"" = NULL",
                ct);
        }

        var existing = await dbContext.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted
                                   && a.IsActive
                                   && a.DeduplicationKey == dedupKey, ct);

        // Non-Postgres providers (InMemoryDatabase tests, Sqlite, etc.) take the
        // read-then-add path with field-apply. The dedup-race recovery in the caller
        // still handles the unlikely concurrent-add race for these providers.
        if (existing is null)
        {
            existing = new Alert
            {
                AlertType = AlertType.MLModelDegraded,
                DeduplicationKey = dedupKey,
                IsActive = true,
            };
            dbContext.Set<Alert>().Add(existing);
            ApplyChronicTripAlertFields(existing, context, modelId, streak, settings, nowUtc);
        }
        else if (!isPostgres)
        {
            // Existing row found on a non-Postgres provider — the SQL upsert path didn't
            // refresh fields; do it here.
            ApplyChronicTripAlertFields(existing, context, modelId, streak, settings, nowUtc);
        }
        // Postgres path skips the field-apply: the SQL already set every relevant column,
        // and re-applying them through the tracker would emit a redundant UPDATE.

        return existing;
    }

    private static string BuildChronicTripConditionJson(
        ModelContext context,
        long modelId,
        int streak,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            detector = "MLConformalBreaker",
            reason = "chronic_trip",
            modelId,
            symbol = context.Symbol,
            timeframe = context.Timeframe.ToString(),
            consecutiveTrips = streak,
            chronicTripThreshold = settings.ChronicTripThreshold,
            evaluatedAt = nowUtc.ToString("O", CultureInfo.InvariantCulture)
        });

    private static void ApplyChronicTripAlertFields(
        Alert alert,
        ModelContext context,
        long modelId,
        int streak,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc)
    {
        alert.AlertType = AlertType.MLModelDegraded;
        alert.Symbol = context.Symbol;
        alert.Severity = AlertSeverity.High;
        alert.CooldownSeconds = 3600;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = BuildChronicTripConditionJson(context, modelId, streak, settings, nowUtc);
    }

    private bool IsLikelyAlertDeduplicationRace(DbUpdateException ex)
    {
        // Prefer the structured classifier when one is registered — it inspects the
        // Postgres error code (23505 unique_violation) and is robust to driver/locale
        // changes in the human-readable message.
        if (_databaseExceptionClassifier?.IsUniqueConstraintViolation(ex) == true)
            return true;

        // Fallback: message-based heuristic for environments without the classifier
        // (e.g., InMemoryDatabase tests). The unique index on Alert.DeduplicationKey is
        // the only place we routinely race when the cycle lock is unavailable, so this is
        // precise enough in practice. PostgreSQL surfaces "duplicate key value violates
        // unique constraint ... DeduplicationKey ..." for this case.
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

    /// <summary>
    /// PostgreSQL caps query parameters around 32k. Queries that pass <c>modelIds</c> via
    /// EF's <c>.Contains</c> can exceed that on engines with very large active model sets.
    /// Splits the input into chunks of <see cref="QueryParameterChunkSize"/> ids, runs the
    /// loader per chunk, and concatenates the results. Order across chunks is not preserved,
    /// so callers that depend on ordering should impose it after concatenation.
    /// </summary>
    private const int QueryParameterChunkSize = 2048;

    private readonly record struct ConfigKeyValue(string Key, string Value);

    private static async Task<List<T>> LoadInChunksAsync<TKey, T>(
        IReadOnlyCollection<TKey> keys,
        Func<TKey[], Task<List<T>>> loader)
    {
        if (keys.Count == 0)
            return new List<T>();
        if (keys.Count <= QueryParameterChunkSize)
            return await loader(keys.ToArray());

        var results = new List<T>(keys.Count);
        foreach (var chunk in keys.Chunk(QueryParameterChunkSize))
            results.AddRange(await loader(chunk));
        return results;
    }

    private readonly record struct ActiveBreakerLoadResult(
        IReadOnlyList<ActiveBreakerSnapshot> RawRows,
        Dictionary<long, ActiveBreakerSnapshot> ByModelId);

    private static async Task<ActiveBreakerLoadResult> LoadActiveBreakerSnapshotsAsync(
        DbContext db,
        IReadOnlyCollection<long> modelIds,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
            return new ActiveBreakerLoadResult(
                Array.Empty<ActiveBreakerSnapshot>(),
                new Dictionary<long, ActiveBreakerSnapshot>());

        // Project to a slim DTO so we don't load any large JSON metadata columns on the
        // breaker row — only the fields the worker needs for evaluation.
        var rows = await LoadInChunksAsync(modelIds, async chunk =>
            await db.Set<MLConformalBreakerLog>()
                .AsNoTracking()
                .Where(b => chunk.Contains(b.MLModelId)
                            && b.IsActive
                            && !b.IsDeleted
                            && b.ResumeAt > nowUtc)
                .Select(b => new ActiveBreakerSnapshot(
                    b.Id,
                    b.MLModelId,
                    b.Symbol,
                    b.Timeframe,
                    b.SuspendedAt,
                    b.ResumeAt))
                .ToListAsync(ct));

        // Order across chunks is preserved by sorting after concatenation. Most-recent
        // breaker per model wins via OrderByDescending on SuspendedAt + Id. Duplicates are
        // rare and the state store handles their cleanup; this just ensures we evaluate once.
        var ordered = rows
            .OrderByDescending(b => b.SuspendedAt)
            .ThenByDescending(b => b.Id)
            .ToList();

        var byModelId = ordered
            .GroupBy(b => b.MLModelId)
            .ToDictionary(g => g.Key, g => g.First());
        return new ActiveBreakerLoadResult(ordered, byModelId);
    }

    private async Task<PerModelState> LoadPerModelStateAsync(
        DbContext writeDb,
        IReadOnlyCollection<long> modelIds,
        CancellationToken ct)
    {
        if (modelIds.Count == 0)
        {
            return new PerModelState(
                new Dictionary<long, DateTime>(),
                new Dictionary<long, int>(),
                new HashSet<long>());
        }

        var keys = new List<string>(modelIds.Count * 2);
        foreach (var id in modelIds)
        {
            keys.Add(LastEvaluatedAtKey(id));
            keys.Add(TripStreakKey(id));
        }

        // Chunked load — at 10k+ models the keys list approaches Postgres' 32k-parameter
        // ceiling (2 keys per model). Chunking preserves correctness under any model count.
        var configRows = await LoadInChunksAsync(keys, async chunk =>
            await writeDb.Set<EngineConfig>()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(c => chunk.Contains(c.Key))
                .Select(c => new ConfigKeyValue(c.Key, c.Value))
                .ToListAsync(ct));

        var lastEvaluatedAt = new Dictionary<long, DateTime>();
        var tripStreak = new Dictionary<long, int>();
        foreach (var row in configRows)
        {
            if (TryParseModelIdFromConfigKey(row.Key, ":LastEvaluatedAt", out var lastModelId)
                && DateTime.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAt))
            {
                lastEvaluatedAt[lastModelId] = lastAt;
            }
            else if (TryParseModelIdFromConfigKey(row.Key, ":TripStreak", out var streakModelId)
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var streak))
            {
                tripStreak[streakModelId] = streak;
            }
        }

        // Cycle-wide cache of which models have an active chronic-trip alert, so the
        // dispatch phase can skip per-model alert lookups when the state hasn't changed.
        var dedupKeys = modelIds
            .Select(id => ChronicTripDeduplicationPrefix + id.ToString(CultureInfo.InvariantCulture))
            .ToList();
        var activeChronicKeys = await LoadInChunksAsync(dedupKeys, async chunk =>
            await writeDb.Set<Alert>()
                .AsNoTracking()
                .Where(a => !a.IsDeleted
                            && a.IsActive
                            && a.DeduplicationKey != null
                            && chunk.Contains(a.DeduplicationKey))
                .Select(a => a.DeduplicationKey!)
                .ToListAsync(ct));

        var activeChronicModelIds = new HashSet<long>();
        foreach (var key in activeChronicKeys)
        {
            if (key.Length <= ChronicTripDeduplicationPrefix.Length)
                continue;
            var span = key.AsSpan(ChronicTripDeduplicationPrefix.Length);
            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                activeChronicModelIds.Add(id);
        }

        return new PerModelState(lastEvaluatedAt, tripStreak, activeChronicModelIds);
    }

    private static async Task PersistPerModelCursorsAsync(
        DbContext writeDb,
        HashSet<long> evaluatedOrSkippedModelIds,
        IReadOnlyDictionary<long, int> updatedTripStreaks,
        IReadOnlyDictionary<long, int> priorTripStreaks,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (evaluatedOrSkippedModelIds.Count == 0)
            return;

        // LastEvaluatedAt is always written — it's the fair-rotation cursor and must
        // advance on every evaluation. TripStreak is only written when it changes from
        // the value we loaded at cycle start; this typically halves the row writes since
        // most models have a stable streak (0 for healthy, refreshed for ongoing trips).
        var specs = new List<EngineConfigUpsertSpec>(evaluatedOrSkippedModelIds.Count + 8);
        var nowIso = nowUtc.ToString("O", CultureInfo.InvariantCulture);

        foreach (var modelId in evaluatedOrSkippedModelIds)
        {
            specs.Add(new EngineConfigUpsertSpec(
                LastEvaluatedAtKey(modelId),
                nowIso,
                ConfigDataType.String,
                "UTC timestamp of the latest MLConformalBreakerWorker evaluation attempt for this model. Used as the fair-rotation cursor when active models exceed MaxCycleModels.",
                false));

            int newStreak = updatedTripStreaks.GetValueOrDefault(modelId);
            int priorStreak = priorTripStreaks.GetValueOrDefault(modelId);
            if (newStreak == priorStreak && priorTripStreaks.ContainsKey(modelId))
                continue;

            specs.Add(new EngineConfigUpsertSpec(
                TripStreakKey(modelId),
                newStreak.ToString(CultureInfo.InvariantCulture),
                ConfigDataType.Int,
                "Consecutive trip cycles for this model; resets to 0 on recovery. Drives the chronic-trip escalation alert.",
                false));
        }

        await EngineConfigUpsert.BatchUpsertAsync(writeDb, specs, ct);
    }

    private static string LastEvaluatedAtKey(long modelId)
        => $"MLConformal:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:LastEvaluatedAt";

    private static string TripStreakKey(long modelId)
        => $"MLConformal:Model:{modelId.ToString(CultureInfo.InvariantCulture)}:TripStreak";

    private static bool TryParseModelIdFromConfigKey(string key, string suffix, out long modelId)
    {
        modelId = 0;
        const string prefix = "MLConformal:Model:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !key.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        int idStart = prefix.Length;
        int idEnd = key.Length - suffix.Length;
        if (idEnd <= idStart) return false;

        return long.TryParse(
            key.AsSpan(idStart, idEnd - idStart),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out modelId);
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

        var calibrations = await LoadInChunksAsync(modelIds, async chunk =>
            await db.Set<MLConformalCalibration>()
                .AsNoTracking()
                .Where(c => chunk.Contains(c.MLModelId) && !c.IsDeleted)
                .ToListAsync(ct));

        return calibrations
            .OrderByDescending(c => c.CalibratedAt)
            .ThenByDescending(c => c.Id)
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
            .ToList();

        if (calibrationIds.Count == 0)
            return new Dictionary<long, double>();

        var calibrations = await LoadInChunksAsync(calibrationIds, async chunk =>
            await db.Set<MLConformalCalibration>()
                .AsNoTracking()
                .Where(c => chunk.Contains(c.Id) && !c.IsDeleted)
                .ToListAsync(ct));

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
        // Priority 1: explicit pre-computed coverage flag (most authoritative).
        if (log.WasConformalCovered.HasValue)
            return new ConformalObservation(log.WasConformalCovered.Value, log.OutcomeRecordedAt);

        // Priority 2: reconstruct from the served prediction set + actual direction.
        var coveredBySet = TryCoverageFromPredictionSet(log);
        if (coveredBySet.HasValue)
            return new ConformalObservation(coveredBySet.Value, log.OutcomeRecordedAt);

        // Priority 3: reconstruct from the non-conformity score against the served threshold.
        // Threshold is taken from the log itself, then the served calibration record by id,
        // then the current calibration as last resort.
        return TryCoverageFromNonConformityScore(log, fallbackThreshold, calibrationThresholdById);
    }

    private static bool? TryCoverageFromPredictionSet(MLModelPredictionLog log)
    {
        if (!log.ActualDirection.HasValue)
            return null;

        return MLFeatureHelper.WasActualDirectionInConformalSet(
            log.ConformalPredictionSetJson,
            log.ActualDirection.Value);
    }

    private static ConformalObservation? TryCoverageFromNonConformityScore(
        MLModelPredictionLog log,
        double fallbackThreshold,
        IReadOnlyDictionary<long, double> calibrationThresholdById)
    {
        double threshold = ResolveServedThreshold(log, fallbackThreshold, calibrationThresholdById);

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

    private static double ResolveServedThreshold(
        MLModelPredictionLog log,
        double fallbackThreshold,
        IReadOnlyDictionary<long, double> calibrationThresholdById)
    {
        if (log.ConformalThresholdUsed.HasValue)
            return log.ConformalThresholdUsed.Value;

        if (log.MLConformalCalibrationId.HasValue
            && calibrationThresholdById.TryGetValue(log.MLConformalCalibrationId.Value, out var calibrationThreshold))
        {
            return calibrationThreshold;
        }

        return fallbackThreshold;
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
            StatisticalAlpha: ClampDouble(options.StatisticalAlpha, 0.01, 1e-12, 0.49),
            MaxAlertsPerCycle: ClampInt(options.MaxAlertsPerCycle, 50, 0, 100_000),
            ChronicTripThreshold: ClampInt(options.ChronicTripThreshold, 4, 1, 1_000));
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
