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

public interface IStrategyGenerationScheduler
{
    Task ExecutePollAsync(Func<CancellationToken, Task> runCycleAsync, CancellationToken stoppingToken);
}

public interface IStrategyGenerationCycleRunner
{
    Task RunAsync(CancellationToken ct);
}

internal interface IStrategyGenerationScreeningCoordinator
{
    Dictionary<int, int> BuildInitialCorrelationGroupCounts(IReadOnlyList<string> activeSymbols);

    Task<StrategyGenerationScreeningResult> ScreenAllCandidatesAsync(
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationScreeningContext context,
        CancellationToken ct);
}

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

internal interface IStrategyGenerationFeedbackCoordinator
{
    Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct);

    Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct);

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

internal interface IStrategyGenerationDynamicTemplateRefreshService
{
    Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct);
}

internal interface IStrategyGenerationFeedbackSummaryProvider
{
    Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct);
}

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

internal interface IStrategyGenerationPruningCoordinator
{
    Task<int> PruneStaleStrategiesAsync(
        IReadApplicationDbContext readCtx,
        IWriteApplicationDbContext writeCtx,
        ScreeningAuditLogger auditLogger,
        int pruneAfterFailed,
        CancellationToken ct);
}

internal interface IStrategyGenerationScheduleStateStore
{
    Task<StrategyGenerationScheduleStateSnapshot> LoadAsync(DbContext readDb, CancellationToken ct);

    Task SaveAsync(
        DbContext writeDb,
        StrategyGenerationScheduleStateSnapshot snapshot,
        CancellationToken ct);
}

internal interface IStrategyGenerationCycleRunStore
{
    Task StartAsync(DbContext writeDb, string cycleId, string? fingerprint, CancellationToken ct);

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

    Task<StrategyGenerationCycleRun?> LoadPreviousCompletedAsync(
        DbContext readDb,
        string currentCycleId,
        CancellationToken ct);
}

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

internal interface IStrategyGenerationPendingArtifactStore
{
    Task<StrategyGenerationPendingArtifactLoadResult> LoadPendingArtifactsAsync(
        DbContext readDb,
        CancellationToken ct);

    Task ReplacePendingArtifactsAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationPendingArtifactRecord> pendingArtifacts,
        CancellationToken ct);
}

internal interface IStrategyGenerationFailureStore
{
    Task<IReadOnlyList<StrategyGenerationFailure>> LoadUnreportedFailuresAsync(
        DbContext readDb,
        CancellationToken ct);

    Task MarkFailuresReportedAsync(
        DbContext writeDb,
        IReadOnlyCollection<long> failureIds,
        CancellationToken ct);

    Task MarkFailuresResolvedAsync(
        DbContext writeDb,
        IReadOnlyCollection<string> candidateIds,
        CancellationToken ct);

    Task RecordFailuresAsync(
        DbContext writeDb,
        IReadOnlyCollection<StrategyGenerationFailureRecord> failures,
        CancellationToken ct);
}

internal interface IStrategyGenerationCorrelationCoordinator
{
    Dictionary<int, int> BuildInitialCounts(IReadOnlyList<string> activeSymbols);
    bool IsSaturated(string symbol, Dictionary<int, int> groupCounts, int maxPerGroup);
    void IncrementCount(string symbol, Dictionary<int, int> groupCounts);
}

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

internal interface IStrategyGenerationCalendarPolicy
{
    bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols, DateTime utcNow);

    bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone, DateTime utcNow);
}

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

internal interface IStrategyGenerationEventFactory
{
    StrategyCandidateCreatedIntegrationEvent BuildCandidateCreatedEvent(ScreeningOutcome candidate, long strategyId);

    StrategyAutoPromotedIntegrationEvent BuildAutoPromotedEvent(ScreeningOutcome candidate, long strategyId);
}

internal interface IStrategyScreeningEngineFactory
{
    StrategyScreeningEngine Create(Action<string>? onGateRejection = null);
}

public interface IStrategyScreeningArtifactFactory
{
    int ResolveMonteCarloSeed(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string enrichedParams,
        IReadOnlyList<Candle> allCandles,
        DateTime utcNow);

    ScreeningMetrics BuildMetrics(
        BacktestResult trainResult,
        BacktestResult oosResult,
        double? r2,
        double pValue,
        double shufflePValue,
        int walkForwardPassed,
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
    IReadOnlyList<string> LowConfidenceSymbols);

internal sealed record StrategyGenerationFeedbackStateRecord(
    string StateKey,
    string PayloadJson,
    DateTime LastUpdatedAtUtc);
