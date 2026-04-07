using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

internal sealed record StrategyGenerationExistingStrategyInfo(
    long Id,
    StrategyType StrategyType,
    string Symbol,
    Timeframe Timeframe,
    StrategyStatus Status,
    StrategyLifecycleStage LifecycleStage);

internal sealed class StrategyGenerationScreeningContext
{
    public required string CycleId { get; init; }
    public required GenerationConfig Config { get; init; }
    public required Dictionary<string, string> RawConfigs { get; init; }
    public required IReadOnlyDictionary<string, StrategyGenerationSymbolOverrides> SymbolOverridesBySymbol { get; init; }
    public required StrategyScreeningEngine ScreeningEngine { get; init; }
    public required IReadOnlyList<StrategyGenerationExistingStrategyInfo> Existing { get; init; }
    public required HashSet<CandidateCombo> ExistingSet { get; init; }
    public required Dictionary<CandidateCombo, HashSet<string>> PrunedTemplates { get; init; }
    public required HashSet<CandidateCombo> FullyPrunedCombos { get; init; }
    public required Dictionary<string, int> ActiveCountBySymbol { get; init; }
    public required Dictionary<string, MarketRegimeEnum> RegimeBySymbol { get; init; }
    public required Dictionary<(string, Timeframe), MarketRegimeEnum> RegimeBySymbolTf { get; init; }
    public required Dictionary<string, CurrencyPair> PairDataBySymbol { get; init; }
    public required Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> FeedbackRates { get; init; }
    public required IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> AdaptiveAdjustmentsByContext { get; init; }
    public required Dictionary<int, int> CorrelationGroupCounts { get; init; }
    public required Dictionary<MarketRegimeEnum, int> RegimeBudget { get; init; }
    public required List<string> ActivePairs { get; init; }
    public required ScreeningAuditLogger AuditLogger { get; init; }
    public required Dictionary<string, double> RegimeConfidenceBySymbol { get; init; }
    public required StrategyGenerationFaultTracker FaultTracker { get; init; }
    public required IReadOnlyDictionary<string, double> TemplateSurvivalRates { get; init; }
    public required Dictionary<string, MarketRegimeEnum> RegimeTransitions { get; init; }
    public required Dictionary<string, DateTime> RegimeDetectedAtBySymbol { get; init; }
    public required HashSet<string> TransitionSymbols { get; init; }
    public required IReadOnlyList<string> LowConfidenceSymbols { get; init; }
    public HaircutRatios? Haircuts { get; init; }
    public IReadOnlyList<(DateTime Date, decimal Equity)>? PortfolioEquityCurve { get; init; }
    public ISpreadProfileProvider? SpreadProfileProvider { get; init; }
}

internal sealed record StrategyGenerationScreeningResult(
    List<ScreeningOutcome> Candidates,
    int ReserveCreated,
    int CandidatesScreened,
    int SymbolsProcessed,
    int SymbolsSkipped);

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationScreeningCoordinator))]
internal sealed class StrategyGenerationScreeningCoordinator : IStrategyGenerationScreeningCoordinator
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly IStrategyGenerationReservePlanner _reservePlanner;
    private readonly IStrategyGenerationPrimaryScreeningPlanner _primaryPlanner;
    private readonly IStrategyGenerationCorrelationCoordinator _correlationCoordinator;
    private readonly IStrategyGenerationCheckpointCoordinator _checkpointCoordinator;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationScreeningCoordinator(
        ILogger<StrategyGenerationWorker> logger,
        TradingMetrics metrics,
        IStrategyGenerationReservePlanner reservePlanner,
        IStrategyGenerationPrimaryScreeningPlanner primaryPlanner,
        IStrategyGenerationCorrelationCoordinator correlationCoordinator,
        IStrategyGenerationCheckpointCoordinator checkpointCoordinator,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _reservePlanner = reservePlanner;
        _primaryPlanner = primaryPlanner;
        _correlationCoordinator = correlationCoordinator;
        _checkpointCoordinator = checkpointCoordinator;
        _timeProvider = timeProvider;
    }

    public Dictionary<int, int> BuildInitialCorrelationGroupCounts(IReadOnlyList<string> activeSymbols)
        => _correlationCoordinator.BuildInitialCounts(activeSymbols);

    public async Task<StrategyGenerationScreeningResult> ScreenAllCandidatesAsync(
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationScreeningContext context,
        CancellationToken ct)
    {
        var config = context.Config;
        var totalCountBySymbol = context.Existing
            .GroupBy(e => e.Symbol)
            .ToDictionary(g => g.Key, g => g.Count());

        var prioritisedSymbols = context.ActivePairs
            .Where(sym => context.RegimeBySymbol.ContainsKey(sym))
            .OrderBy(sym => totalCountBySymbol.TryGetValue(sym, out var count) ? count : 0)
            .ToList();

        var candidatesPerCurrency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in context.Existing.Where(e => e.Status == StrategyStatus.Active))
        {
            var pair = context.PairDataBySymbol.GetValueOrDefault(existing.Symbol);
            string baseCurrency = pair?.BaseCurrency ?? (existing.Symbol.Length >= 6 ? existing.Symbol[..3] : existing.Symbol);
            string quoteCurrency = pair?.QuoteCurrency ?? (existing.Symbol.Length >= 6 ? existing.Symbol[3..6] : "");
            candidatesPerCurrency[baseCurrency] = candidatesPerCurrency.GetValueOrDefault(baseCurrency) + 1;
            if (quoteCurrency.Length > 0)
                candidatesPerCurrency[quoteCurrency] = candidatesPerCurrency.GetValueOrDefault(quoteCurrency) + 1;
        }

        var regimeCounts = context.RegimeBySymbol.Values.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        int totalRegimeSymbols = regimeCounts.Values.Sum();
        var regimeBudget = new Dictionary<MarketRegimeEnum, int>();
        foreach (var (regime, count) in regimeCounts)
        {
            double cappedShare = Math.Min((double)count / totalRegimeSymbols, config.RegimeBudgetDiversityPct);
            regimeBudget[regime] = Math.Max(1, (int)(config.MaxCandidates * cappedShare));
        }

        context.RegimeBudget.Clear();
        foreach (var (regime, count) in regimeBudget)
            context.RegimeBudget[regime] = count;

        var regimeCandidatesCreated = regimeCounts.Keys.ToDictionary(r => r, _ => 0);
        var candleCache = new CandleLruCache(config.MaxCandleCacheSize);
        var screeningConfig = BuildScreeningConfig(config);
        var lowConfidenceSymbolSet = context.LowConfidenceSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedNoRegime = context.ActivePairs
            .Where(sym => !context.RegimeBySymbol.ContainsKey(sym) && !lowConfidenceSymbolSet.Contains(sym))
            .ToList();

        var resumeState = await _checkpointCoordinator.RestoreAsync(
            db,
            context,
            candidatesPerCurrency,
            regimeCandidatesCreated,
            ct);

        foreach (var timeframe in config.CandidateTimeframes)
        {
            await ChunkedCandleLoader.LoadChunkedAsync(
                db,
                candleCache,
                config.ScreeningMonths,
                prioritisedSymbols,
                timeframe,
                config.CandleChunkSize > 0 ? config.CandleChunkSize : ChunkedCandleLoader.DefaultChunkSize,
                () => _metrics.StrategyGenCandleCacheEvictions.Add(1),
                _logger,
                _timeProvider,
                ct);
        }

        var primaryResult = await _primaryPlanner.ScreenAsync(
            db,
            writeCtx,
            context,
            prioritisedSymbols,
            skippedNoRegime,
            lowConfidenceSymbolSet,
            screeningConfig,
            candleCache,
            resumeState,
            ct);

        int reserveCreated = resumeState.ReserveCreated;
        int candidatesScreened = primaryResult.CandidatesScreened;
        if (primaryResult.CandidatesCreated < config.MaxCandidates && config.StrategicReserveQuota > 0)
        {
            string checkpointFingerprint = _checkpointCoordinator.ComputeFingerprint(context);
            reserveCreated += await _reservePlanner.ScreenReserveCandidatesAsync(
                db,
                context,
                candleCache,
                primaryResult.PendingCandidates,
                primaryResult.CandidatesPerCurrency,
                primaryResult.CandidatesCreated,
                primaryResult.GeneratedCountBySymbol,
                primaryResult.GeneratedTypeCountsBySymbol,
                () => Interlocked.Increment(ref candidatesScreened),
                currentReserveCreated => _checkpointCoordinator.SaveAsync(
                    writeCtx,
                    context.CycleId,
                    checkpointFingerprint,
                    new StrategyGenerationCheckpointProgressSnapshot(
                        primaryResult.CompletedSymbolSet,
                        primaryResult.CandidatesCreated,
                        currentReserveCreated,
                        candidatesScreened,
                        primaryResult.SymbolsProcessed,
                        primaryResult.SymbolsSkipped,
                        primaryResult.PendingCandidates,
                        primaryResult.CandidatesPerCurrency,
                        primaryResult.RegimeCandidatesCreated,
                        context.CorrelationGroupCounts),
                    ct,
                    $"reserve:{currentReserveCreated}"),
                ct);
        }

        return new StrategyGenerationScreeningResult(
            primaryResult.PendingCandidates,
            reserveCreated,
            candidatesScreened,
            primaryResult.SymbolsProcessed,
            primaryResult.SymbolsSkipped);
    }

    private ScreeningConfig BuildScreeningConfig(GenerationConfig config)
    {
        var splitPcts = config.WalkForwardSplitPercentages;

        if (config.WalkForwardWindowCount != splitPcts.Count)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: WalkForwardWindowCount={WindowCount} but WalkForwardSplitPcts has {SplitCount} entries — the engine will use {SplitCount} windows. Update StrategyGeneration:WalkForwardWindowCount to match.",
                config.WalkForwardWindowCount,
                splitPcts.Count,
                splitPcts.Count);
        }

        if (config.WalkForwardMinWindowsPass > splitPcts.Count)
        {
            _logger.LogWarning(
                "StrategyGenerationWorker: WalkForwardMinWindowsPass={MinPass} exceeds available windows ({Count}) — walk-forward will be impossible to pass. Clamping to {Count}.",
                config.WalkForwardMinWindowsPass,
                splitPcts.Count,
                splitPcts.Count);
        }

        return new ScreeningConfig
        {
            ScreeningTimeoutSeconds = config.ScreeningTimeoutSeconds,
            ScreeningInitialBalance = config.ScreeningInitialBalance,
            MaxOosDegradationPct = config.MaxOosDegradationPct,
            MinEquityCurveR2 = config.MinEquityCurveR2,
            MaxTradeTimeConcentration = config.MaxTradeTimeConcentration,
            MonteCarloEnabled = config.MonteCarloEnabled,
            MonteCarloPermutations = config.MonteCarloPermutations,
            MonteCarloMinPValue = config.MonteCarloMinPValue,
            MonteCarloShuffleEnabled = config.MonteCarloShuffleEnabled,
            WalkForwardWindowCount = splitPcts.Count,
            WalkForwardMinWindowsPass = Math.Min(config.WalkForwardMinWindowsPass, splitPcts.Count),
            WalkForwardSplitPcts = splitPcts,
            MonteCarloShufflePermutations = config.MonteCarloShufflePermutations,
            MonteCarloShuffleMinPValue = config.MonteCarloShuffleMinPValue,
            ActiveStrategyCount = config.ActiveStrategyCount,
        };
    }
}
