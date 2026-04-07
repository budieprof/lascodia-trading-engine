using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

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
