using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

// ── Worker-facing orchestration entry points ────────────────────────────────

/// <summary>
/// Coordinates when the strategy-generation pipeline is allowed to execute and serializes
/// access to the shared generation cycle.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler does not implement the generation workflow itself. Instead, callers provide
/// the cycle body via <see cref="ExecutePollAsync(Func{CancellationToken, Task}, CancellationToken)"/>
/// or <see cref="ExecuteManualRunAsync(Func{CancellationToken, Task}, CancellationToken)"/>,
/// and the scheduler decides whether that callback should run now.
/// </para>
///
/// <para>
/// Scheduled polling and manual execution intentionally share this abstraction so both paths
/// flow through the same distributed-lock boundary and persisted scheduling state. That keeps
/// duplicate-run prevention, skip reporting, and run-success bookkeeping centralized instead
/// of being reimplemented in the worker.
/// </para>
///
/// <para>
/// Inference from the current implementation: scheduled polling applies the configured
/// hourly window, retry budget, and circuit-breaker policy, while manual execution bypasses
/// the clock-based schedule but still respects the generation lock so operators cannot start
/// a concurrent cycle on top of an already-running one.
/// </para>
/// </remarks>
public interface IStrategyGenerationScheduler
{
    // Scheduled entry point used by the hosted worker's normal polling loop.

    /// <summary>
    /// Evaluates the configured schedule for the current polling window and executes the
    /// provided generation callback only when the scheduler decides a run is allowed.
    /// </summary>
    /// <param name="runCycleAsync">
    /// Callback that performs the actual generation cycle once schedule checks, distributed
    /// locking, and other gating logic have passed.
    /// </param>
    /// <param name="stoppingToken">Cancellation token propagated from the hosting worker.</param>
    /// <remarks>
    /// <para>
    /// Callers should treat this as a conditional execution method, not a guaranteed run. The
    /// scheduler may skip invocation because generation is disabled, the current UTC hour is
    /// outside the configured schedule window, the run already happened today, the retry budget
    /// for the window is exhausted, the circuit breaker is active, or another node already owns
    /// the distributed generation lock.
    /// </para>
    ///
    /// <para>
    /// When the callback is invoked, it should execute exactly one generation cycle and honor
    /// the supplied cancellation token promptly. The scheduler owns all policy decisions around
    /// whether that callback should be entered at all.
    /// </para>
    /// </remarks>
    Task ExecutePollAsync(Func<CancellationToken, Task> runCycleAsync, CancellationToken stoppingToken);

    // Manual entry point used by operator-triggered or test-driven generation requests.

    /// <summary>
    /// Attempts to run the generation callback immediately while still flowing through the
    /// scheduler's shared concurrency and bookkeeping boundary.
    /// </summary>
    /// <param name="runCycleAsync">
    /// Callback that performs the actual generation cycle after the manual run has acquired
    /// the scheduler's execution right.
    /// </param>
    /// <param name="stoppingToken">Cancellation token for the manual invocation.</param>
    /// <remarks>
    /// <para>
    /// Manual execution is intended for explicit operator or integration-test initiated runs.
    /// Unlike <see cref="ExecutePollAsync(Func{CancellationToken, Task}, CancellationToken)"/>,
    /// it should not be blocked by the normal time-of-day schedule.
    /// </para>
    ///
    /// <para>
    /// It is still not an unconditional run: implementations are expected to honor the same
    /// distributed lock used by scheduled polling so only one generation cycle can own the
    /// pipeline at a time.
    /// </para>
    /// </remarks>
    Task ExecuteManualRunAsync(Func<CancellationToken, Task> runCycleAsync, CancellationToken stoppingToken);
}

/// <summary>
/// Executes one full end-to-end strategy-generation cycle after the scheduler has granted
/// permission to run.
/// </summary>
/// <remarks>
/// This abstraction owns the heavyweight workflow itself: replaying deferred artifacts,
/// loading cycle context, screening candidates, persisting accepted strategies, and
/// publishing cycle summary information.
/// </remarks>
public interface IStrategyGenerationCycleRunner
{
    /// <summary>
    /// Runs exactly one generation cycle.
    /// </summary>
    /// <param name="ct">Cancellation token for the current cycle execution.</param>
    Task RunAsync(CancellationToken ct);
}

// ── Screening orchestration and planning contracts ─────────────────────────

/// <summary>
/// Coordinates the full candidate-screening pass for a generation cycle.
/// </summary>
/// <remarks>
/// Implementations typically compose the primary screening planner, reserve planner,
/// checkpoint recovery, and correlation-budget tracking into a single cycle-level result.
/// </remarks>
internal interface IStrategyGenerationScreeningCoordinator
{
    Dictionary<int, int> BuildInitialCorrelationGroupCounts(IReadOnlyList<string> activeSymbols);

    Task<StrategyGenerationScreeningResult> ScreenAllCandidatesAsync(
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationScreeningContext context,
        CancellationToken ct);
}

/// <summary>
/// Screens the main candidate stream for a cycle before any reserve-only pass is attempted.
/// </summary>
/// <remarks>
/// The primary planner is responsible for the bulk of normal symbol and timeframe traversal,
/// checkpoint-aware resumption, and accumulation of candidates that survive the standard gate
/// pipeline.
/// </remarks>
internal interface IStrategyGenerationPrimaryScreeningPlanner
{
    Task<StrategyGenerationPrimaryScreeningResult> ScreenAsync(
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationScreeningContext context,
        IReadOnlyList<string> prioritisedSymbols,
        IReadOnlyCollection<string> skippedNoRegimeSymbols,
        IReadOnlySet<string> lowConfidenceSymbolSet,
        ScreeningConfig screeningConfig,
        CandleLruCache candleCache,
        StrategyGenerationCheckpointResumeState resumeState,
        CancellationToken ct);
}

/// <summary>
/// Orchestrates creation of reserve candidates when the primary pass leaves unused capacity.
/// </summary>
/// <remarks>
/// Reserve generation is distinct from the primary pass because it targets strategic diversity
/// or fallback regime coverage rather than the normal first-choice candidate budget.
/// </remarks>
internal interface IStrategyGenerationReservePlanner
{
    Task<int> ScreenReserveCandidatesAsync(
        DbContext db,
        StrategyGenerationScreeningContext context,
        CandleLruCache candleCache,
        List<ScreeningOutcome> pendingCandidates,
        Dictionary<string, int> candidatesPerCurrency,
        int candidatesCreated,
        Dictionary<string, int> generatedCountBySymbol,
        Dictionary<string, Dictionary<StrategyType, int>> generatedTypeCountsBySymbol,
        Action onCandidateScreened,
        Func<int, Task> saveCheckpointAsync,
        CancellationToken ct);
}

/// <summary>
/// Performs the reserve-only screening traversal and applies reserve-specific acceptance rules.
/// </summary>
/// <remarks>
/// This narrower contract exists so reserve screening logic can evolve independently from the
/// higher-level reserve orchestration that decides whether a reserve pass should happen at all.
/// </remarks>
internal interface IStrategyGenerationReserveScreeningPlanner
{
    Task<int> ScreenReserveCandidatesAsync(
        DbContext db,
        StrategyGenerationScreeningContext context,
        CandleLruCache candleCache,
        List<ScreeningOutcome> pendingCandidates,
        Dictionary<string, int> candidatesPerCurrency,
        int candidatesCreated,
        Dictionary<string, int> generatedCountBySymbol,
        Dictionary<string, Dictionary<StrategyType, int>> generatedTypeCountsBySymbol,
        Action onCandidateScreened,
        Func<int, Task> saveCheckpointAsync,
        CancellationToken ct);
}

/// <summary>
/// Coordinates persistence of accepted candidates and replay of post-persist side effects.
/// </summary>
/// <remarks>
/// The generation pipeline can successfully create database state before all downstream audit
/// and event artifacts are fully published. This coordinator centralizes the durable replay
/// path for those deferred artifacts.
/// </remarks>
internal interface IStrategyGenerationPersistenceCoordinator
{
    Task<PersistCandidatesResult> PersistCandidatesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        List<ScreeningOutcome> candidates,
        GenerationConfig config,
        CancellationToken ct);

    Task ReplayPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        CancellationToken ct);
}

/// <summary>
/// Aggregates all feedback-driven adaptation used by the generation pipeline.
/// </summary>
/// <remarks>
/// This includes refreshing dynamic templates, loading historical performance feedback, and
/// reconciling adaptive threshold adjustments before screening begins.
/// </remarks>
internal interface IStrategyGenerationFeedbackCoordinator
{
    Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct);

    Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct);

    /// <summary>
    /// Extended variant that also returns per-template sample counts for UCB1 template
    /// selection. Default implementation delegates to <see cref="LoadPerformanceFeedbackAsync"/>.
    /// </summary>
    async Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates,
                Dictionary<string, double> TemplateRates,
                Dictionary<string, int> TemplateSampleCounts)>
        LoadPerformanceFeedbackWithCountsAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct)
    {
        var (typeRates, templateRates) = await LoadPerformanceFeedbackAsync(db, writeCtx, halfLifeDays, ct);
        return (typeRates, templateRates, new Dictionary<string, int>(StringComparer.Ordinal));
    }

    IReadOnlyList<StrategyType> ApplyPerformanceFeedback(
        IReadOnlyList<StrategyType> types,
        MarketRegimeEnum regime,
        Timeframe timeframe,
        Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> rates);

    void DetectFeedbackAdaptiveContradictions(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates,
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adaptiveAdjustmentsByContext);

    Task<IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>> ComputeAdaptiveThresholdsAsync(
        DbContext db,
        GenerationConfig config,
        CancellationToken ct);
}

/// <summary>
/// Refreshes dynamic strategy templates before a screening cycle starts.
/// </summary>
internal interface IStrategyGenerationDynamicTemplateRefreshService
{
    Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct);
}

/// <summary>
/// Loads historical feedback summaries used to bias candidate generation toward strategies
/// and templates that have aged well in live or validation conditions.
/// </summary>
internal interface IStrategyGenerationFeedbackSummaryProvider
{
    Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct);

    /// <summary>
    /// Extended variant that also returns per-template sample counts. Used by the UCB1
    /// template selector in <c>StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1</c>.
    /// Default implementation delegates to <see cref="LoadPerformanceFeedbackAsync"/> and
    /// returns an empty counts dictionary so existing fakes/tests don't need to change.
    /// </summary>
    async Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates,
                Dictionary<string, double> TemplateRates,
                Dictionary<string, int> TemplateSampleCounts)>
        LoadPerformanceFeedbackWithCountsAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct)
    {
        var (typeRates, templateRates) = await LoadPerformanceFeedbackAsync(db, writeCtx, halfLifeDays, ct);
        return (typeRates, templateRates, new Dictionary<string, int>(StringComparer.Ordinal));
    }
}

/// <summary>
/// Computes adaptive gate adjustments and detects contradictions between adaptive thresholds
/// and observed feedback signals.
/// </summary>
internal interface IStrategyGenerationAdaptiveThresholdService
{
    void DetectFeedbackAdaptiveContradictions(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates,
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adaptiveAdjustmentsByContext);

    Task<IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>> ComputeAdaptiveThresholdsAsync(
        DbContext db,
        GenerationConfig config,
        CancellationToken ct);
}

/// <summary>
/// Removes stale or repeatedly failed auto-generated strategies after a cycle completes.
/// </summary>
internal interface IStrategyGenerationPruningCoordinator
{
    Task<int> PruneStaleStrategiesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        ScreeningAuditLogger auditLogger,
        int pruneAfterFailed,
        CancellationToken ct);
}

// ── Durable scheduling, cycle, checkpoint, and failure state stores ───────

/// <summary>
/// Persists the scheduler's lightweight day-level execution state.
/// </summary>
/// <remarks>
/// This is intentionally separate from full cycle-run storage so schedule gating can resume
/// correctly after process restarts without loading the entire cycle history.
/// </remarks>
internal interface IStrategyGenerationScheduleStateStore
{
    Task<StrategyGenerationScheduleStateSnapshot> LoadAsync(DbContext readDb, CancellationToken ct);

    Task SaveAsync(
        DbContext writeDb,
        StrategyGenerationScheduleStateSnapshot snapshot,
        CancellationToken ct);
}

/// <summary>
/// Persists the lifecycle of a generation cycle from start through summary publication.
/// </summary>
/// <remarks>
/// The cycle-run record acts as the durable audit spine for each cycle, including completion
/// metadata and the separate state machine for dispatching the cycle summary event.
/// </remarks>
internal interface IStrategyGenerationCycleRunStore
{
    // Cycle lifecycle persistence.

    Task StartAsync(DbContext writeDb, string cycleId, string? fingerprint, CancellationToken ct);

    Task AttachFingerprintAsync(DbContext writeDb, string cycleId, string fingerprint, CancellationToken ct);

    Task StageCompletionAsync(
        DbContext writeDb,
        string cycleId,
        StrategyGenerationCycleRunCompletion completion,
        CancellationToken ct);

    // Summary event dispatch staging and reconciliation.

    Task StageSummaryDispatchAttemptAsync(
        DbContext writeDb,
        string cycleId,
        Guid eventId,
        string payloadJson,
        DateTime attemptedAtUtc,
        CancellationToken ct);

    Task MarkSummaryDispatchPublishedAsync(
        DbContext writeDb,
        string cycleId,
        DateTime dispatchedAtUtc,
        CancellationToken ct);

    Task RecordSummaryDispatchFailureAsync(
        DbContext writeDb,
        string cycleId,
        Guid eventId,
        string payloadJson,
        string errorMessage,
        DateTime failedAtUtc,
        CancellationToken ct);

    // Terminal cycle states.

    Task CompleteAsync(
        DbContext writeDb,
        string cycleId,
        StrategyGenerationCycleRunCompletion completion,
        CancellationToken ct);

    Task FailAsync(
        DbContext writeDb,
        string cycleId,
        string failureStage,
        string failureMessage,
        CancellationToken ct);

    // Recovery queries used by replay/reconciliation paths.

    Task<StrategyGenerationCycleRun?> LoadPreviousCompletedAsync(
        DbContext readDb,
        string currentCycleId,
        CancellationToken ct);

    Task<IReadOnlyList<StrategyGenerationSummaryDispatchRecord>> LoadPendingSummaryDispatchesAsync(
        DbContext readDb,
        CancellationToken ct);
}

/// <summary>
/// Stores resumable progress for the screening phase of the current cycle.
/// </summary>
/// <remarks>
/// Checkpoints let long or partially failed cycles resume without re-screening every symbol
/// and without duplicating already-accepted pending candidates.
/// </remarks>
internal interface IStrategyGenerationCheckpointStore
{
    Task<GenerationCheckpointStore.State?> LoadCheckpointAsync(
        DbContext readDb,
        DateTime cycleDateUtc,
        string expectedFingerprint,
        CancellationToken ct);

    Task SaveCheckpointAsync(
        DbContext writeDb,
        string cycleId,
        GenerationCheckpointStore.State state,
        Microsoft.Extensions.Logging.ILogger? logger,
        CancellationToken ct);

    Task ClearCheckpointAsync(DbContext writeDb, CancellationToken ct);
}

/// <summary>
/// Persists deferred post-persist artifacts that could not be fully replayed inline.
/// </summary>
/// <remarks>
/// These records keep the pipeline idempotent when a strategy row has already been written
/// but related decision logs or integration events still need durable replay.
/// </remarks>
internal interface IStrategyGenerationPendingArtifactStore
{
    // Load the current replay backlog for the next recovery pass.
    Task<StrategyGenerationPendingArtifactLoadResult> LoadPendingArtifactsAsync(
        DbContext readDb,
        CancellationToken ct);

    // Permanently quarantine corrupt payloads so replay can continue around them.
    Task QuarantineCorruptArtifactsAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationCorruptArtifactRecord> corruptArtifacts,
        CancellationToken ct);

    // Replace the active pending-artifact set after a replay attempt.
    Task ReplacePendingArtifactsAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct);
}

/// <summary>
/// Persists hard failures that could not be repaired during candidate persistence or replay.
/// </summary>
/// <remarks>
/// These failures back both operator visibility and later reconciliation when a candidate is
/// eventually reported or resolved by a subsequent cycle.
/// </remarks>
internal interface IStrategyGenerationFailureStore
{
    // Read failures that still need operator-facing reporting.
    Task<IReadOnlyList<StrategyGenerationFailure>> LoadUnreportedFailuresAsync(
        DbContext readDb,
        CancellationToken ct);

    // Mark a batch as already surfaced so future cycles do not log them repeatedly.
    Task MarkFailuresReportedAsync(
        DbContext writeDb,
        IReadOnlyCollection<long> failureIds,
        CancellationToken ct);

    // Mark failures resolved after successful replay or later persistence success.
    Task MarkFailuresResolvedAsync(
        DbContext writeDb,
        IReadOnlyCollection<string> candidateIds,
        CancellationToken ct);

    // Record newly discovered terminal failures.
    Task RecordFailuresAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationFailureRecord> failures,
        CancellationToken ct);
}

// ── Coordination helpers used inside a single cycle ────────────────────────

/// <summary>
/// Tracks correlation-group occupancy so the generator does not over-concentrate candidates
/// into the same correlated asset cluster.
/// </summary>
internal interface IStrategyGenerationCorrelationCoordinator
{
    Dictionary<int, int> BuildInitialCounts(IReadOnlyList<string> activeSymbols);
    bool IsSaturated(string symbol, Dictionary<int, int> groupCounts, int maxPerGroup);
    void IncrementCount(string symbol, Dictionary<int, int> groupCounts);
}

/// <summary>
/// Bridges the in-memory screening state with the durable checkpoint store.
/// </summary>
/// <remarks>
/// The coordinator computes a fingerprint for the current screening context, restores any
/// compatible checkpoint, and saves progress snapshots at key boundaries during the cycle.
/// </remarks>
internal interface IStrategyGenerationCheckpointCoordinator
{
    string ComputeFingerprint(StrategyGenerationScreeningContext context);

    Task<StrategyGenerationCheckpointResumeState> RestoreAsync(
        DbContext db,
        StrategyGenerationScreeningContext context,
        Dictionary<string, int> candidatesPerCurrency,
        Dictionary<MarketRegimeEnum, int> regimeCandidatesCreated,
        CancellationToken ct);

    Task SaveAsync(
        IWriteApplicationDbContext writeCtx,
        string cycleId,
        string checkpointFingerprint,
        StrategyGenerationCheckpointProgressSnapshot snapshot,
        CancellationToken ct,
        string checkpointLabel);
}

// ── Lower-level persistence and replay services ────────────────────────────

/// <summary>
/// Writes newly accepted candidates and their first-order side effects.
/// </summary>
internal interface IStrategyGenerationCandidatePersistenceService
{
    Task<PersistCandidatesResult> PersistCandidatesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        List<ScreeningOutcome> candidates,
        GenerationConfig config,
        CancellationToken ct);
}

/// <summary>
/// Replays deferred candidate-created artifacts after the main persistence path succeeds.
/// </summary>
/// <remarks>
/// This service exists separately from the candidate persistence path so recovery can be
/// retried independently of the original cycle that created the strategies.
/// </remarks>
internal interface IStrategyGenerationArtifactReplayService
{
    Task PersistAndDrainPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct);

    Task ReplayPendingPostPersistArtifactsAsync(
        DbContext readDb,
        IWriteApplicationDbContext writeCtx,
        IIntegrationEventService eventService,
        ScreeningAuditLogger auditLogger,
        CancellationToken ct);
}

/// <summary>
/// Stores small, durable feedback-related state blobs keyed by logical purpose.
/// </summary>
internal interface IStrategyGenerationFeedbackStateStore
{
    Task<StrategyGenerationFeedbackStateRecord?> LoadAsync(
        DbContext readDb,
        string stateKey,
        CancellationToken ct);

    Task SaveAsync(
        DbContext writeDb,
        string stateKey,
        string payloadJson,
        CancellationToken ct);
}

// ── Cycle environment and policy providers ─────────────────────────────────

/// <summary>
/// Loads the market, regime, pruning, and portfolio snapshot needed to start a cycle.
/// </summary>
/// <remarks>
/// This abstraction gathers the expensive, read-heavy context once so the cycle runner can
/// build a screening context from a single coherent data snapshot.
/// </remarks>
internal interface IStrategyGenerationCycleDataService
{
    Task<int> CountRecentAutoCandidatesAsync(DbContext db, DateTime createdAfterUtc, CancellationToken ct);

    Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct);

    Task<StrategyGenerationCycleDataSnapshot> LoadCycleDataAsync(
        DbContext db,
        GenerationConfig config,
        DateTime nowUtc,
        CancellationToken ct);
}

/// <summary>
/// Applies calendar-level gating rules such as weekends and configured blackout periods.
/// </summary>
internal interface IStrategyGenerationCalendarPolicy
{
    bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols, DateTime utcNow);

    bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone, DateTime utcNow);
}

/// <summary>
/// Computes market-data-derived screening inputs such as recency weighting, candle freshness,
/// and synthetic backtest options for a symbol.
/// </summary>
internal interface IStrategyGenerationMarketDataPolicy
{
    double ComputeEffectiveCandleAgeHours(DateTime lastCandleTimestampUtc, string? tradingHoursJson, DateTime utcNow);

    BacktestOptions BuildScreeningOptions(
        string symbol,
        CurrencyPair? pairInfo,
        StrategyGenerationHelpers.AssetClass assetClass,
        double screeningSpreadPoints,
        double screeningCommissionPerLot,
        double screeningSlippagePips,
        ILivePriceCache livePriceCache,
        DateTime utcNow);

    double ComputeRegimeDurationFactor(DateTime regimeDetectedAt, DateTime utcNow);

    double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies,
        double halfLifeDays,
        DateTime utcNow);
}

/// <summary>
/// Applies spread-based acceptance rules and derives representative spread values for a
/// symbol's screening run.
/// </summary>
internal interface IStrategyGenerationSpreadPolicy
{
    bool PassesSpreadFilter(
        decimal atr,
        BacktestOptions options,
        IReadOnlyList<Candle> candles,
        StrategyGenerationHelpers.AssetClass assetClass,
        double maxRatio);

    decimal ResolveRepresentativeSpread(BacktestOptions options, IReadOnlyList<Candle> candles);
}

/// <summary>
/// Creates the integration events emitted when a screened candidate is persisted.
/// </summary>
internal interface IStrategyGenerationEventFactory
{
    StrategyCandidateCreatedIntegrationEvent BuildCandidateCreatedEvent(ScreeningOutcome candidate, long strategyId);

    StrategyAutoPromotedIntegrationEvent BuildAutoPromotedEvent(ScreeningOutcome candidate, long strategyId);
}

/// <summary>
/// Produces configured <see cref="StrategyScreeningEngine"/> instances for a cycle.
/// </summary>
/// <remarks>
/// The optional rejection callback lets callers observe why a candidate failed a gate without
/// coupling the engine itself to a specific audit sink.
/// </remarks>
internal interface IStrategyScreeningEngineFactory
{
    StrategyScreeningEngine Create(Action<string>? onGateRejection = null);
}

/// <summary>
/// Builds deterministic screening artifacts that must be reproduced consistently across runs.
/// </summary>
/// <remarks>
/// This includes Monte Carlo seeding, metrics materialization, and final strategy entity
/// creation from already-screened candidate results.
/// </remarks>
public interface IStrategyScreeningArtifactFactory
{
    // Deterministic seed generation keeps stochastic screening steps reproducible for a
    // given candidate shape and candle history.
    int ResolveMonteCarloSeed(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string enrichedParams,
        IReadOnlyList<Candle> allCandles,
        DateTime utcNow);

    // Materialize the full screening metadata payload stored alongside accepted strategies.
    ScreeningMetrics BuildMetrics(
        BacktestResult trainResult,
        BacktestResult oosResult,
        double? r2,
        double? pValue,
        double? shufflePValue,
        int walkForwardPassed,
        int? walkForwardRequiredForScore,
        int walkForwardMask,
        double maxConcentration,
        MarketRegimeEnum targetRegime,
        MarketRegimeEnum observedRegime,
        string generationSource,
        string? reserveTargetRegime,
        int monteCarloSeed,
        double? marginalSharpeContribution,
        double kellySharpe,
        double fixedLotSharpe,
        HaircutRatios? appliedHaircuts,
        IReadOnlyList<ScreeningGateTrace> gateTrace,
        DateTime screenedAtUtc);

    // Construct the domain strategy entity from already-screened candidate data.
    Strategy BuildStrategy(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string enrichedParams,
        int templateIndex,
        string generationSource,
        MarketRegimeEnum targetRegime,
        MarketRegimeEnum observedRegime,
        BacktestResult trainResult,
        BacktestResult oosResult,
        ScreeningMetrics metrics,
        DateTime createdAtUtc);
}

/// <summary>
/// Snapshot of the environment a cycle needs before screening begins.
/// </summary>
/// <remarks>
/// The cycle runner uses this immutable bundle to construct a richer
/// <see cref="StrategyGenerationScreeningContext"/> without repeatedly re-querying the same
/// underlying market, regime, and existing-strategy state.
/// </remarks>
internal sealed record StrategyGenerationDataHealthTimeframeSnapshot(
    Timeframe Timeframe,
    int CandleCount,
    DateTime? LatestClosedCandleUtc,
    double? EffectiveAgeHours,
    bool IsEligible,
    string Reason);

internal sealed record StrategyGenerationDataHealthSnapshot(
    string Symbol,
    double Score,
    IReadOnlyList<StrategyGenerationDataHealthTimeframeSnapshot> Timeframes)
{
    public bool HasEligibleTimeframe => Timeframes.Any(t => t.IsEligible);
}

internal sealed record StrategyGenerationCycleDataSnapshot(
    List<string> ActivePairs,
    Dictionary<string, CurrencyPair> PairDataBySymbol,
    IReadOnlyList<StrategyGenerationExistingStrategyInfo> ExistingStrategies,
    Dictionary<string, int> ActiveCountBySymbol,
    Dictionary<CandidateCombo, HashSet<string>> PrunedTemplates,
    HashSet<CandidateCombo> FullyPrunedCombos,
    Dictionary<string, MarketRegimeEnum> RegimeBySymbol,
    Dictionary<(string, Timeframe), MarketRegimeEnum> RegimeBySymbolTf,
    Dictionary<string, double> RegimeConfidenceBySymbol,
    Dictionary<string, MarketRegimeEnum> RegimeTransitions,
    Dictionary<string, DateTime> RegimeDetectedAtBySymbol,
    HashSet<string> TransitionSymbols,
    IReadOnlyList<string> LowConfidenceSymbols,
    IReadOnlyDictionary<string, StrategyGenerationDataHealthSnapshot> DataHealthBySymbol);

/// <summary>
/// Durable keyed payload used for feedback-related state that must survive process restarts.
/// </summary>
internal sealed record StrategyGenerationFeedbackStateRecord(
    string StateKey,
    string PayloadJson,
    DateTime LastUpdatedAtUtc);
