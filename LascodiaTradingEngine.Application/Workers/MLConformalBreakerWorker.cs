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
    private readonly IMLConformalChronicTripAlertUpserter _chronicTripAlertUpserter;
    private readonly IMLConformalRegimeResolver _regimeResolver;
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
        int ChronicTripThreshold,
        int ChronicTripAlertCooldownSeconds,
        int TimeDecayHalfLifeDays,
        int BootstrapResamples,
        double RegressionGuardK,
        int FleetSystemicMinTrippedModels,
        double FleetSystemicTripRatioThreshold,
        int StalenessHours,
        bool OverridesEnabled,
        bool VerboseAuditDiagnostics,
        bool EnablePerRegimeDecomposition,
        int EvaluationParallelism);

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
        int ChronicTripAlertCount,
        int FleetSystemicAlertDispatched,
        int StalenessAlertCount)
    {
        public int AlertBackpressureSkippedCount
            => TripAlertBackpressureSkippedCount + ChronicAlertBackpressureSkippedCount;

        public static MLConformalBreakerCycleResult Skipped(
            MLConformalBreakerWorkerSettings settings,
            string reason)
            => new(settings, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
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
        IMLConformalChronicTripAlertUpserter chronicTripAlertUpserter,
        IMLConformalRegimeResolver regimeResolver,
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
        _chronicTripAlertUpserter = chronicTripAlertUpserter;
        _regimeResolver = regimeResolver;
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

        // ── Phase 3: fleet-level systemic alert ──
        // When the count and ratio of tripped+refreshed models in this cycle cross the
        // configured thresholds, fire SystemicMLDegradation. Likely upstream cause
        // (broken data feed, calibration regression, etc.) rather than independent model
        // failures.
        int fleetTrippingCount = stateResult.TrippedCount + stateResult.RefreshedCount;
        bool fleetSystemicAlertDispatched = await ApplyFleetSystemicAlertAsync(
            writeContext,
            fleetTrippingCount,
            evaluationOutcome.EvaluatedCount,
            settings,
            nowUtc,
            ct);

        // ── Phase 4: staleness detection ──
        // Models whose most-recent resolved outcome is older than StalenessHours get a
        // dedicated MLMonitoringStale alert. Distinct from chronic-trip (which is repeated
        // trips, not absent data).
        int staleAlertCount = await ApplyStalenessAlertsAsync(
            writeContext,
            evaluationOutcome.LatestOutcomeByModelId,
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
            "{Worker}: cycle {CycleId} complete. evaluated={Evaluated} skippedNoCalibration={SkippedCalibration} skippedInsufficient={SkippedInsufficient} tripped={Tripped} refreshed={Refreshed} recovered={Recovered} expired={Expired} duplicateRepairs={DuplicateRepairs} alerts={Alerts} tripAlertBackpressureSkipped={TripBackpressure} chronicAlertBackpressureSkipped={ChronicBackpressure} chronicTripAlerts={ChronicTripAlerts} fleetSystemicAlert={FleetSystemic} stalenessAlerts={StalenessAlerts} active={Active}",
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
            fleetSystemicAlertDispatched ? 1 : 0,
            staleAlertCount,
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
            ChronicTripAlertCount: dispatchOutcome.ChronicTripAlertCount,
            FleetSystemicAlertDispatched: fleetSystemicAlertDispatched ? 1 : 0,
            StalenessAlertCount: staleAlertCount);
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
        public required Dictionary<long, DateTime> LatestOutcomeByModelId { get; init; }
    }

    private readonly record struct ModelContext(string Symbol, Timeframe Timeframe);

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

        // Per-context override hierarchy: load all override keys once and resolve per
        // model in memory below. Empty when the feature is disabled, so the resolver
        // becomes a pass-through.
        var overridesByContext = settings.OverridesEnabled
            ? await LoadOverridesAsync(writeDb, croppedModels, ct)
            : new ContextOverrideMap();

        // Per-regime decomposition: load regime snapshots covering the eval window for
        // each (Symbol, Timeframe) tuple in this cycle. The resolver returns an empty
        // timeline when the feature is disabled so observation construction is a
        // pass-through (regime stays null and the evaluator skips the breakdown).
        IRegimeTimeline regimeTimeline = await LoadRegimeTimelineIfEnabledAsync(
            writeDb, croppedModels, logsByModelId, settings, nowUtc, ct);

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
        var latestOutcomeByModelId = new Dictionary<long, DateTime>(croppedIds.Count);

        // Pre-populate from logs (independent of evaluation outcome — staleness is "any
        // recent resolved log", not "any successful evaluation"). A model whose logs are
        // all malformed should still be detected as not-stale for staleness purposes.
        foreach (var (modelId, logs) in logsByModelId)
        {
            DateTime? latest = null;
            foreach (var log in logs)
            {
                if (log.OutcomeRecordedAt.HasValue
                    && (latest is null || log.OutcomeRecordedAt.Value > latest.Value))
                    latest = log.OutcomeRecordedAt;
            }
            if (latest.HasValue) latestOutcomeByModelId[modelId] = latest.Value;
        }

        // The accumulators above (lists, dicts, counters) are mutated by the per-model
        // helper. With parallelism > 1 we serialize append/increment under the sink's
        // internal lock — the heavy work (Wilson + p-value + bootstrap) happens outside
        // the lock, so contention stays low even at high fan-out.
        var sink = new EvaluationSink(
            tripCandidates,
            recoveryCandidates,
            refreshCandidates,
            updatedTripStreaks,
            evaluatedOrSkippedModelIds,
            contextByModelId);

        // ── Per-batch parallel in-memory iteration. No DB calls inside this loop. ──
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = settings.EvaluationParallelism,
        };

        foreach (var modelBatch in croppedModels.Chunk(settings.ModelBatchSize))
        {
            Parallel.ForEach(modelBatch, parallelOptions, model =>
                EvaluateOneModel(
                    model,
                    activeBreakerByModelId,
                    calibrationByModelId,
                    latestCalibrationCandidateByModelId,
                    calibrationOptions,
                    logsByModelId,
                    calibrationThresholdById,
                    overridesByContext,
                    regimeTimeline,
                    settings,
                    nowUtc,
                    sink));
        }

        int evaluated = sink.EvaluatedCount;
        int skippedNoCalibration = sink.SkippedNoCalibrationCount;
        int skippedInsufficient = sink.SkippedInsufficientCount;

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
            LatestOutcomeByModelId = latestOutcomeByModelId,
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
        LatestOutcomeByModelId = new Dictionary<long, DateTime>(),
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

                var chronicTripContext = new ChronicTripAlertContext(
                    DeduplicationKey: dedupKey,
                    Symbol: context.Symbol,
                    Timeframe: context.Timeframe,
                    ModelId: modelId,
                    ConsecutiveTripStreak: streak,
                    ChronicTripThreshold: settings.ChronicTripThreshold,
                    CooldownSeconds: settings.ChronicTripAlertCooldownSeconds,
                    EvaluatedAtUtc: nowUtc);

                var chronicAlert = await _chronicTripAlertUpserter.UpsertAsync(dbContext, chronicTripContext, ct);

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
                    // Non-Postgres race recovery (Postgres already serializes via the
                    // atomic upsert, so this catch is dead on that provider): detach the
                    // doomed Add and re-invoke the upserter, which will now find the row
                    // the racing replica inserted and refresh its fields in place.
                    DetachIfAdded(dbContext, chronicAlert);
                    chronicAlert = await _chronicTripAlertUpserter.UpsertAsync(dbContext, chronicTripContext, ct);
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
            ChronicTripThreshold: ClampInt(options.ChronicTripThreshold, 4, 1, 1_000),
            ChronicTripAlertCooldownSeconds: ClampInt(options.ChronicTripAlertCooldownSeconds, 3600, 60, 7 * 24 * 3600),
            TimeDecayHalfLifeDays: ClampInt(options.TimeDecayHalfLifeDays, 7, 0, 3_650),
            BootstrapResamples: ClampInt(options.BootstrapResamples, 200, 0, 10_000),
            RegressionGuardK: ClampDouble(options.RegressionGuardK, 1.0, 0.0, 10.0),
            FleetSystemicMinTrippedModels: ClampInt(options.FleetSystemicMinTrippedModels, 5, 1, 10_000),
            FleetSystemicTripRatioThreshold: ClampDouble(options.FleetSystemicTripRatioThreshold, 0.25, 0.0, 1.0),
            StalenessHours: ClampInt(options.StalenessHours, 48, 1, 24 * 30),
            OverridesEnabled: options.OverridesEnabled,
            VerboseAuditDiagnostics: options.VerboseAuditDiagnostics,
            EnablePerRegimeDecomposition: options.EnablePerRegimeDecomposition,
            EvaluationParallelism: ResolveEvaluationParallelism(options.EvaluationParallelism));
    }

    private static int ResolveEvaluationParallelism(int configured)
    {
        if (configured <= 0) return Math.Clamp(Environment.ProcessorCount, 1, 32);
        return Math.Clamp(configured, 1, 32);
    }

    private static int ClampInt(int value, int defaultValue, int min, int max)
        => value < min || value > max ? defaultValue : value;

    private static double ClampDouble(double value, double defaultValue, double min, double max)
        => !double.IsFinite(value) || value < min || value > max ? defaultValue : value;

    // ── Fleet-level systemic alert ──────────────────────────────────────────────────
    //
    // When the count and ratio of tripped+refreshed models cross the configured
    // thresholds, the worker fires a SystemicMLDegradation alert. The signal targets
    // upstream causes — broken data feed, calibration regression, model-server crash —
    // that wouldn't be visible from individual breaker trips. Auto-resolves when fleet
    // health recovers below the thresholds.

    private const string FleetSystemicDeduplicationKey = "ml-conformal-fleet-systemic";

    private async Task<bool> ApplyFleetSystemicAlertAsync(
        IWriteApplicationDbContext writeContext,
        int trippingModelCount,
        int evaluatedModelCount,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool overCount = trippingModelCount >= settings.FleetSystemicMinTrippedModels;
        double ratio = evaluatedModelCount > 0
            ? (double)trippingModelCount / evaluatedModelCount
            : 0.0;
        bool overRatio = ratio >= settings.FleetSystemicTripRatioThreshold;
        bool shouldAlert = overCount && overRatio;

        var dbContext = writeContext.GetDbContext();
        var existing = await dbContext.Set<Alert>()
            .FirstOrDefaultAsync(a => !a.IsDeleted
                                   && a.IsActive
                                   && a.DeduplicationKey == FleetSystemicDeduplicationKey, ct);

        if (shouldAlert)
        {
            string conditionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                detector = "MLConformalBreaker",
                reason = "fleet_systemic_degradation",
                trippingModelCount,
                evaluatedModelCount,
                ratio = Math.Round(ratio, 4),
                minTrippedModels = settings.FleetSystemicMinTrippedModels,
                ratioThreshold = settings.FleetSystemicTripRatioThreshold,
                evaluatedAt = nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            });

            if (existing is null)
            {
                existing = new Alert
                {
                    AlertType = AlertType.SystemicMLDegradation,
                    DeduplicationKey = FleetSystemicDeduplicationKey,
                    Severity = AlertSeverity.High,
                    CooldownSeconds = settings.ChronicTripAlertCooldownSeconds,
                    ConditionJson = conditionJson,
                    IsActive = true,
                    AutoResolvedAt = null,
                };
                dbContext.Set<Alert>().Add(existing);
            }
            else
            {
                existing.AlertType = AlertType.SystemicMLDegradation;
                existing.Severity = AlertSeverity.High;
                existing.CooldownSeconds = settings.ChronicTripAlertCooldownSeconds;
                existing.ConditionJson = conditionJson;
                existing.AutoResolvedAt = null;
            }
            await dbContext.SaveChangesAsync(ct);

            try
            {
                string message = $"ML conformal breaker: fleet-wide degradation detected — {trippingModelCount} of {evaluatedModelCount} evaluated models are in trip-or-refresh state ({ratio:P1}). Investigate upstream causes (data feed, calibration pipeline, model serving).";
                await _alertDispatcher.DispatchAsync(existing, message, ct);
                await dbContext.SaveChangesAsync(ct);
                _metrics.MLConformalBreakerAlertsDispatched.Add(1);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "{Worker}: fleet-systemic alert dispatch failed (count={Count}, ratio={Ratio:P1}).",
                    WorkerName,
                    trippingModelCount,
                    ratio);
                return false;
            }
        }

        // Healthy: resolve any existing fleet alert.
        if (existing is not null)
        {
            try { await _alertDispatcher.TryAutoResolveAsync(existing, conditionStillActive: false, ct); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Worker}: fleet-systemic auto-resolve dispatch failed.", WorkerName);
            }
            existing.IsActive = false;
            existing.AutoResolvedAt ??= nowUtc;
            await dbContext.SaveChangesAsync(ct);
        }
        return false;
    }

    // ── Staleness detection ─────────────────────────────────────────────────────────
    //
    // A model whose most recent resolved outcome is older than StalenessHours has
    // effectively gone silent — not enough fresh predictions reaching the worker to do
    // a meaningful coverage evaluation. Distinct from chronic-trip (which is repeated
    // *trips*, not absent data) and from the calibrated-edge worker's stale-monitoring
    // alarm (which targets calibrated edge, not coverage). Auto-resolves when fresh
    // outcomes arrive.

    private const string StalenessDeduplicationPrefix = "ml-conformal-stale:";

    private async Task<int> ApplyStalenessAlertsAsync(
        IWriteApplicationDbContext writeContext,
        IReadOnlyDictionary<long, DateTime> latestOutcomeByModelId,
        IReadOnlyDictionary<long, ModelContext> contextByModelId,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var stalenessThreshold = TimeSpan.FromHours(settings.StalenessHours);
        var dbContext = writeContext.GetDbContext();
        int dispatched = 0;

        foreach (var (modelId, ctx) in contextByModelId)
        {
            string dedupKey = StalenessDeduplicationPrefix + modelId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool isStale = !latestOutcomeByModelId.TryGetValue(modelId, out var latest)
                || (nowUtc - latest) > stalenessThreshold;

            var existing = await dbContext.Set<Alert>()
                .FirstOrDefaultAsync(a => !a.IsDeleted
                                       && a.IsActive
                                       && a.DeduplicationKey == dedupKey, ct);

            if (isStale)
            {
                if (existing is not null) continue; // already alerted for this model

                string conditionJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    detector = "MLConformalBreaker",
                    reason = "stale_predictions",
                    modelId,
                    symbol = ctx.Symbol,
                    timeframe = ctx.Timeframe.ToString(),
                    latestOutcomeAt = latestOutcomeByModelId.TryGetValue(modelId, out var l)
                        ? l.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                    stalenessThresholdHours = settings.StalenessHours,
                    evaluatedAt = nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                });

                var alert = new Alert
                {
                    AlertType = AlertType.MLMonitoringStale,
                    DeduplicationKey = dedupKey,
                    Symbol = ctx.Symbol,
                    Severity = AlertSeverity.Medium,
                    CooldownSeconds = settings.ChronicTripAlertCooldownSeconds,
                    ConditionJson = conditionJson,
                    IsActive = true,
                };
                dbContext.Set<Alert>().Add(alert);
                await dbContext.SaveChangesAsync(ct);

                try
                {
                    string message = $"ML conformal breaker: model {modelId} ({ctx.Symbol}/{ctx.Timeframe}) has gone stale — no resolved predictions in the last {settings.StalenessHours}h. Investigate prediction logging pipeline or whether the model is still being served.";
                    await _alertDispatcher.DispatchAsync(alert, message, ct);
                    await dbContext.SaveChangesAsync(ct);
                    _metrics.MLConformalBreakerAlertsDispatched.Add(1);
                    dispatched++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Worker}: staleness alert dispatch failed for model {ModelId}.", WorkerName, modelId);
                }
            }
            else if (existing is not null)
            {
                // Fresh data arrived → resolve the staleness alert.
                try { await _alertDispatcher.TryAutoResolveAsync(existing, conditionStillActive: false, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "{Worker}: staleness auto-resolve dispatch failed for model {ModelId}.", WorkerName, modelId); }
                existing.IsActive = false;
                existing.AutoResolvedAt ??= nowUtc;
                await dbContext.SaveChangesAsync(ct);
            }
        }

        return dispatched;
    }

    // ── Per-context override hierarchy ──────────────────────────────────────────────
    //
    // Operators can pin per-(model | symbol+timeframe | symbol | timeframe) overrides for
    // five knobs by writing keys to EngineConfig under the MLConformal:Override:* prefix.
    // The hierarchy is resolved with first-hit-wins semantics; the global setting is the
    // final fallback. Knobs that don't have an override use the cycle-wide default.

    private const string OverridePrefix = "MLConformal:Override:";
    private const string KnobMaxLogs = "MaxLogs";
    private const string KnobMinLogs = "MinLogs";
    private const string KnobCoverageTolerance = "CoverageTolerance";
    private const string KnobConsecutiveUncoveredTrigger = "ConsecutiveUncoveredTrigger";
    private const string KnobRegressionGuardK = "RegressionGuardK";

    private static readonly string[] OverrideKnobs =
    [
        KnobMaxLogs,
        KnobMinLogs,
        KnobCoverageTolerance,
        KnobConsecutiveUncoveredTrigger,
        KnobRegressionGuardK,
    ];

    private readonly record struct EffectiveSettings(
        int MaxLogs,
        int MinLogs,
        double CoverageTolerance,
        int ConsecutiveUncoveredTrigger,
        double RegressionGuardK);

    private sealed class ContextOverrideMap
    {
        // key → value, where key is one of the override scopes the worker recognises.
        // Empty map = no overrides (feature disabled or no matching keys exist).
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    private async Task<ContextOverrideMap> LoadOverridesAsync(
        DbContext db,
        IReadOnlyCollection<MLModel> models,
        CancellationToken ct)
    {
        // Build the set of EngineConfig keys we'd consult for any model in this cycle.
        // For 10k models that's ~50k keys (5 knobs × ~10 scope variations), but the
        // chunking helper keeps each query under the 32k-parameter ceiling.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            foreach (var knob in OverrideKnobs)
            {
                keys.Add($"{OverridePrefix}Model:{model.Id}:{knob}");
                keys.Add($"{OverridePrefix}Symbol:{model.Symbol}:Timeframe:{model.Timeframe}:{knob}");
                keys.Add($"{OverridePrefix}Symbol:{model.Symbol}:{knob}");
                keys.Add($"{OverridePrefix}Timeframe:{model.Timeframe}:{knob}");
            }
        }

        var rows = await LoadInChunksAsync(keys.ToList(), async chunk =>
            await db.Set<EngineConfig>()
                .AsNoTracking()
                .Where(c => !c.IsDeleted && chunk.Contains(c.Key))
                .Select(c => new ConfigKeyValue(c.Key, c.Value))
                .ToListAsync(ct));

        // Override validation: any key under MLConformal:Override:* that doesn't end with a
        // known knob is almost certainly a typo. We log one warning per typo per cycle so
        // operators see them surfaced in dashboards. Distinct from valid-but-missing
        // overrides (which are silent — that's the whole point of an override).
        var allOverrideKeys = await db.Set<EngineConfig>()
            .AsNoTracking()
            .Where(c => !c.IsDeleted && c.Key.StartsWith(OverridePrefix))
            .Select(c => c.Key)
            .ToListAsync(ct);
        foreach (var key in allOverrideKeys)
        {
            if (!OverrideKnobs.Any(knob => key.EndsWith(":" + knob, StringComparison.Ordinal)))
            {
                _logger.LogWarning(
                    "{Worker}: ignoring override key with unrecognised knob suffix: {Key}. Valid suffixes: {Knobs}",
                    WorkerName,
                    key,
                    string.Join(", ", OverrideKnobs));
            }
        }

        var map = new ContextOverrideMap();
        foreach (var row in rows)
            map.Values[row.Key] = row.Value;
        return map;
    }

    private static EffectiveSettings ResolveEffectiveSettings(
        ContextOverrideMap overrides,
        long modelId,
        string symbol,
        Timeframe timeframe,
        MLConformalBreakerWorkerSettings settings)
    {
        return new EffectiveSettings(
            MaxLogs: ResolveIntOverride(overrides, modelId, symbol, timeframe, KnobMaxLogs, settings.MaxLogs),
            MinLogs: ResolveIntOverride(overrides, modelId, symbol, timeframe, KnobMinLogs, settings.MinLogs),
            CoverageTolerance: ResolveDoubleOverride(overrides, modelId, symbol, timeframe, KnobCoverageTolerance, settings.CoverageTolerance),
            ConsecutiveUncoveredTrigger: ResolveIntOverride(overrides, modelId, symbol, timeframe, KnobConsecutiveUncoveredTrigger, settings.ConsecutiveUncoveredTrigger),
            RegressionGuardK: ResolveDoubleOverride(overrides, modelId, symbol, timeframe, KnobRegressionGuardK, settings.RegressionGuardK));
    }

    private static int ResolveIntOverride(
        ContextOverrideMap overrides,
        long modelId,
        string symbol,
        Timeframe timeframe,
        string knob,
        int defaultValue)
    {
        foreach (var key in EnumerateOverrideKeys(modelId, symbol, timeframe, knob))
        {
            if (overrides.Values.TryGetValue(key, out var raw)
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static double ResolveDoubleOverride(
        ContextOverrideMap overrides,
        long modelId,
        string symbol,
        Timeframe timeframe,
        string knob,
        double defaultValue)
    {
        foreach (var key in EnumerateOverrideKeys(modelId, symbol, timeframe, knob))
        {
            if (overrides.Values.TryGetValue(key, out var raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static IEnumerable<string> EnumerateOverrideKeys(long modelId, string symbol, Timeframe timeframe, string knob)
    {
        // First-hit-wins ordering: most specific to least specific. Per-model wins over
        // symbol+timeframe wins over per-symbol wins over per-timeframe.
        yield return $"{OverridePrefix}Model:{modelId}:{knob}";
        yield return $"{OverridePrefix}Symbol:{symbol}:Timeframe:{timeframe}:{knob}";
        yield return $"{OverridePrefix}Symbol:{symbol}:{knob}";
        yield return $"{OverridePrefix}Timeframe:{timeframe}:{knob}";
    }

    private static TimeSpan GetIntervalWithJitter(MLConformalBreakerWorkerSettings settings)
        => settings.PollJitterSeconds == 0
            ? settings.PollInterval
            : settings.PollInterval + TimeSpan.FromSeconds(Random.Shared.Next(0, settings.PollJitterSeconds + 1));

    // ── Per-model evaluation (extracted so the loop can fan out via Parallel.ForEach) ──

    /// <summary>
    /// Thread-safe accumulator for per-model evaluation results. The heavy work (Wilson +
    /// p-value + bootstrap) runs outside the lock; only the small append/increment
    /// portion serialises, so contention remains low even at high parallelism.
    /// </summary>
    private sealed class EvaluationSink
    {
        private readonly object _gate = new();
        private readonly List<BreakerTripCandidate> _trips;
        private readonly List<BreakerRecoveryCandidate> _recoveries;
        private readonly List<BreakerRefreshCandidate> _refreshes;
        private readonly Dictionary<long, int> _updatedTripStreaks;
        private readonly HashSet<long> _evaluatedOrSkipped;
        private readonly Dictionary<long, ModelContext> _contextByModelId;

        public int EvaluatedCount;
        public int SkippedNoCalibrationCount;
        public int SkippedInsufficientCount;

        public EvaluationSink(
            List<BreakerTripCandidate> trips,
            List<BreakerRecoveryCandidate> recoveries,
            List<BreakerRefreshCandidate> refreshes,
            Dictionary<long, int> updatedTripStreaks,
            HashSet<long> evaluatedOrSkipped,
            Dictionary<long, ModelContext> contextByModelId)
        {
            _trips = trips;
            _recoveries = recoveries;
            _refreshes = refreshes;
            _updatedTripStreaks = updatedTripStreaks;
            _evaluatedOrSkipped = evaluatedOrSkipped;
            _contextByModelId = contextByModelId;
        }

        public void RegisterContext(long modelId, ModelContext context)
        {
            lock (_gate)
            {
                _contextByModelId[modelId] = context;
                _evaluatedOrSkipped.Add(modelId);
            }
        }

        public void RecordSkippedNoCalibration() => Interlocked.Increment(ref SkippedNoCalibrationCount);
        public void RecordSkippedInsufficient() => Interlocked.Increment(ref SkippedInsufficientCount);
        public void RecordEvaluated() => Interlocked.Increment(ref EvaluatedCount);

        public void AddTrip(BreakerTripCandidate candidate, int newStreak)
        {
            lock (_gate)
            {
                _trips.Add(candidate);
                _updatedTripStreaks[candidate.MLModelId] = newStreak;
            }
        }

        public void AddRecovery(BreakerRecoveryCandidate candidate, int newStreak)
        {
            lock (_gate)
            {
                _recoveries.Add(candidate);
                _updatedTripStreaks[candidate.MLModelId] = newStreak;
            }
        }

        public void AddRefresh(BreakerRefreshCandidate candidate, int newStreak)
        {
            lock (_gate)
            {
                _refreshes.Add(candidate);
                _updatedTripStreaks[candidate.MLModelId] = newStreak;
            }
        }

        public void ResetStreak(long modelId)
        {
            lock (_gate)
                _updatedTripStreaks[modelId] = 0;
        }

        public int GetCurrentStreak(long modelId)
        {
            lock (_gate)
                return _updatedTripStreaks.GetValueOrDefault(modelId);
        }
    }

    private void EvaluateOneModel(
        MLModel model,
        IReadOnlyDictionary<long, ActiveBreakerSnapshot> activeBreakerByModelId,
        IReadOnlyDictionary<long, MLConformalCalibration> calibrationByModelId,
        IReadOnlyDictionary<long, MLConformalCalibration> latestCalibrationCandidateByModelId,
        ConformalCalibrationSelectionOptions calibrationOptions,
        IReadOnlyDictionary<long, List<MLModelPredictionLog>> logsByModelId,
        IReadOnlyDictionary<long, double> calibrationThresholdById,
        ContextOverrideMap overridesByContext,
        IRegimeTimeline regimeTimeline,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        EvaluationSink sink)
    {
        sink.RegisterContext(model.Id, new ModelContext(model.Symbol, model.Timeframe));

        if (!calibrationByModelId.TryGetValue(model.Id, out var calibration))
        {
            sink.RecordSkippedNoCalibration();
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
            return;
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
            .Select(o => StampRegime(o!.Value, model.Symbol, model.Timeframe, regimeTimeline))
            .ToList();

        // Per-context override resolution: merges in any operator-set overrides for
        // this (model, symbol, timeframe) before passing to the evaluator. Falls
        // through to settings defaults when no overrides are present or
        // OverridesEnabled is false.
        var effective = ResolveEffectiveSettings(
            overridesByContext, model.Id, model.Symbol, model.Timeframe, settings);

        var evaluation = _coverageEvaluator.Evaluate(
            observations,
            new ConformalCoverageEvaluationOptions(
                TargetCoverage: calibration.TargetCoverage,
                CoverageTolerance: effective.CoverageTolerance,
                MinLogs: effective.MinLogs,
                TriggerRunLength: effective.ConsecutiveUncoveredTrigger,
                UseWilsonCoverageFloor: settings.UseWilsonCoverageFloor,
                WilsonConfidenceLevel: settings.WilsonConfidenceLevel,
                StatisticalAlpha: settings.StatisticalAlpha,
                TimeDecayHalfLifeDays: settings.TimeDecayHalfLifeDays,
                BootstrapResamples: settings.BootstrapResamples,
                RegressionGuardK: effective.RegressionGuardK,
                ModelId: model.Id,
                NowUtc: nowUtc));

        if (!evaluation.HasEnoughSamples)
        {
            sink.RecordSkippedInsufficient();
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
            return;
        }

        sink.RecordEvaluated();
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
                sink.AddRecovery(new BreakerRecoveryCandidate(
                    activeBreaker.Id,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    evaluation), newStreak: 0);
            }
            else
            {
                // Refresh-of-active-breaker counts as a continuing trip cycle for
                // chronic-tripper detection.
                sink.AddRefresh(new BreakerRefreshCandidate(
                    activeBreaker.Id,
                    model.Id,
                    model.Symbol,
                    model.Timeframe,
                    evaluation,
                    calibration.CoverageThreshold,
                    calibration.TargetCoverage), newStreak: sink.GetCurrentStreak(model.Id) + 1);
            }
            return;
        }

        if (!evaluation.ShouldTrip)
        {
            // Healthy with no active breaker — reset the streak.
            sink.ResetStreak(model.Id);
            return;
        }

        int severityBars = Math.Max(
            evaluation.ConsecutivePoorCoverageBars,
            evaluation.TrippedByCoverageFloor ? settings.ConsecutiveUncoveredTrigger : 0);
        int suspensionBars = Math.Min(Math.Max(severityBars * 2, 1), settings.MaxSuspensionBars);

        sink.AddTrip(new BreakerTripCandidate(
            model.Id,
            model.Symbol,
            model.Timeframe,
            evaluation,
            calibration.CoverageThreshold,
            calibration.TargetCoverage,
            suspensionBars), newStreak: sink.GetCurrentStreak(model.Id) + 1);
    }

    // ── Per-regime decomposition wiring ─────────────────────────────────────────────

    private async Task<IRegimeTimeline> LoadRegimeTimelineIfEnabledAsync(
        DbContext db,
        IReadOnlyCollection<MLModel> models,
        IReadOnlyDictionary<long, List<MLModelPredictionLog>> logsByModelId,
        MLConformalBreakerWorkerSettings settings,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!settings.EnablePerRegimeDecomposition || models.Count == 0)
            return EmptyRegimeTimeline.Instance;

        var contexts = models
            .Select(m => (m.Symbol, m.Timeframe))
            .Distinct()
            .ToArray();

        // Window starts at the oldest log timestamp we'll evaluate, falling back to a
        // reasonable lookback if no logs are available yet (e.g. all models hit the
        // skip-no-calibration path). The resolver itself adds a buffer to handle gaps.
        DateTime windowStart = nowUtc.AddDays(-30);
        foreach (var (_, logs) in logsByModelId)
        {
            foreach (var log in logs)
            {
                if (log.OutcomeRecordedAt is { } when && when < windowStart)
                    windowStart = when;
            }
        }

        try
        {
            return await _regimeResolver.LoadAsync(db, contexts, windowStart, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Worker}: regime timeline load failed; per-regime decomposition disabled for this cycle.",
                WorkerName);
            return EmptyRegimeTimeline.Instance;
        }
    }

    private static ConformalObservation StampRegime(
        ConformalObservation observation,
        string symbol,
        Timeframe timeframe,
        IRegimeTimeline timeline)
    {
        if (observation.OutcomeRecordedAt is not { } when) return observation;
        var regime = timeline.RegimeAt(symbol, timeframe, when);
        return regime is null
            ? observation
            : observation with { Regime = regime };
    }

    private sealed class EmptyRegimeTimeline : IRegimeTimeline
    {
        public static readonly EmptyRegimeTimeline Instance = new();
        public global::LascodiaTradingEngine.Domain.Enums.MarketRegime? RegimeAt(string symbol, Timeframe timeframe, DateTime whenUtc) => null;
    }
}
