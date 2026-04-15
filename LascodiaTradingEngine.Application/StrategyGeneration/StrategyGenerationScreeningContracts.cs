using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Restored mutable screening state recovered from a saved checkpoint.
/// </summary>
internal sealed record StrategyGenerationCheckpointResumeState(
    HashSet<string> CompletedSymbolSet,
    int CandidatesCreated,
    int ReserveCreated,
    int CandidatesScreened,
    int SymbolsProcessed,
    int SymbolsSkipped,
    List<ScreeningOutcome> PendingCandidates,
    Dictionary<string, int> CandidatesPerCurrency,
    Dictionary<MarketRegimeEnum, int> RegimeCandidatesCreated,
    Dictionary<string, int> GeneratedCountBySymbol,
    Dictionary<string, Dictionary<StrategyType, int>> GeneratedTypeCountsBySymbol);

/// <summary>
/// Snapshot of screening progress persisted when saving a checkpoint.
/// </summary>
internal sealed record StrategyGenerationCheckpointProgressSnapshot(
    HashSet<string> CompletedSymbolSet,
    int CandidatesCreated,
    int ReserveCreated,
    int CandidatesScreened,
    int SymbolsProcessed,
    int SymbolsSkipped,
    List<ScreeningOutcome> PendingCandidates,
    Dictionary<string, int> CandidatesPerCurrency,
    Dictionary<MarketRegimeEnum, int> RegimeCandidatesCreated,
    Dictionary<int, int> CorrelationGroupCounts);

/// <summary>
/// Aggregate result returned by the primary screening planner.
/// </summary>
internal sealed record StrategyGenerationPrimaryScreeningResult(
    List<ScreeningOutcome> PendingCandidates,
    int CandidatesCreated,
    int CandidatesScreened,
    int SymbolsProcessed,
    int SymbolsSkipped,
    Dictionary<string, int> CandidatesPerCurrency,
    Dictionary<MarketRegimeEnum, int> RegimeCandidatesCreated,
    Dictionary<string, int> GeneratedCountBySymbol,
    Dictionary<string, Dictionary<StrategyType, int>> GeneratedTypeCountsBySymbol,
    HashSet<string> CompletedSymbolSet);
