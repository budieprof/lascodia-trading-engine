using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
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
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationPrimaryScreeningPlanner))]
/// <summary>
/// Drives the main symbol-by-symbol candidate screening pass for a generation cycle.
/// </summary>
/// <remarks>
/// This planner applies the majority of capacity limits, regime gating, template ordering,
/// spread checks, and checkpoint-aware symbol traversal before reserve screening begins.
/// </remarks>
internal sealed class StrategyGenerationPrimaryScreeningPlanner : IStrategyGenerationPrimaryScreeningPlanner
{
    private sealed record ScreeningTaskArgs(
        List<Candle> Candles,
        decimal Atr,
        string Symbol,
        Timeframe Timeframe,
        MarketRegimeEnum Regime,
        IReadOnlyList<StrategyType> SuitableTypes,
        Dictionary<StrategyType, int> ActiveTypeCountsForSymbol,
        BacktestOptions ScreeningOptions,
        ScreeningThresholds Thresholds,
        ScreeningConfig ScreeningConfig,
        int MaxTemplates,
        int CandidatesCreated);

    private sealed record CandleLoadResult(List<Candle>? Candles, string? SkipReason);

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IBacktestEngine _backtestEngine;
    private readonly IRegimeStrategyMapper _regimeMapper;
    private readonly IStrategyParameterTemplateProvider _templateProvider;
    private readonly IScreeningSurrogateService _surrogateService;
    private readonly ILivePriceCache _livePriceCache;
    private readonly TradingMetrics _metrics;
    private readonly IStrategyCandidateSelectionPolicy _candidateSelectionPolicy;
    private readonly IStrategyGenerationCheckpointCoordinator _checkpointCoordinator;
    private readonly IStrategyGenerationFeedbackCoordinator _feedbackCoordinator;
    private readonly IStrategyGenerationMarketDataPolicy _marketDataPolicy;
    private readonly IStrategyGenerationCorrelationCoordinator _correlationCoordinator;
    private readonly IStrategyGenerationSpreadPolicy _spreadPolicy;
    private readonly IDeferredCompositeMLRegistrar _deferredCompositeMLRegistrar;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationPrimaryScreeningPlanner(
        ILogger<StrategyGenerationWorker> logger,
        IBacktestEngine backtestEngine,
        IRegimeStrategyMapper regimeMapper,
        IStrategyParameterTemplateProvider templateProvider,
        IScreeningSurrogateService surrogateService,
        ILivePriceCache livePriceCache,
        TradingMetrics metrics,
        IStrategyCandidateSelectionPolicy candidateSelectionPolicy,
        IStrategyGenerationCheckpointCoordinator checkpointCoordinator,
        IStrategyGenerationFeedbackCoordinator feedbackCoordinator,
        IStrategyGenerationMarketDataPolicy marketDataPolicy,
        IStrategyGenerationCorrelationCoordinator correlationCoordinator,
        IStrategyGenerationSpreadPolicy spreadPolicy,
        IDeferredCompositeMLRegistrar deferredCompositeMLRegistrar,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _backtestEngine = backtestEngine;
        _regimeMapper = regimeMapper;
        _templateProvider = templateProvider;
        _surrogateService = surrogateService;
        _livePriceCache = livePriceCache;
        _metrics = metrics;
        _candidateSelectionPolicy = candidateSelectionPolicy;
        _checkpointCoordinator = checkpointCoordinator;
        _feedbackCoordinator = feedbackCoordinator;
        _marketDataPolicy = marketDataPolicy;
        _correlationCoordinator = correlationCoordinator;
        _spreadPolicy = spreadPolicy;
        _deferredCompositeMLRegistrar = deferredCompositeMLRegistrar;
        _timeProvider = timeProvider;
    }

    public async Task<StrategyGenerationPrimaryScreeningResult> ScreenAsync(
        DbContext db,
        IWriteApplicationDbContext writeCtx,
        StrategyGenerationScreeningContext context,
        IReadOnlyList<string> prioritisedSymbols,
        IReadOnlyCollection<string> skippedNoRegimeSymbols,
        IReadOnlySet<string> lowConfidenceSymbolSet,
        ScreeningConfig screeningConfig,
        CandleLruCache candleCache,
        StrategyGenerationCheckpointResumeState resumeState,
        CancellationToken ct)
    {
        // Restore mutable counters from checkpoint state so the planner can resume partway
        // through a cycle without regenerating already-accepted candidates.
        var config = context.Config;
        int candidatesCreated = resumeState.CandidatesCreated;
        int candidatesScreened = resumeState.CandidatesScreened;
        int symbolsSkipped = resumeState.SymbolsSkipped;
        int processedSymbolsCount = resumeState.SymbolsProcessed;
        var pendingCandidates = resumeState.PendingCandidates;
        var candidatesPerCurrency = resumeState.CandidatesPerCurrency;
        var regimeCandidatesCreated = resumeState.RegimeCandidatesCreated;
        var generatedCountBySymbol = resumeState.GeneratedCountBySymbol;
        var generatedTypeCountsBySymbol = resumeState.GeneratedTypeCountsBySymbol;
        var completedSymbolSet = resumeState.CompletedSymbolSet;
        string checkpointFingerprint = _checkpointCoordinator.ComputeFingerprint(context);

        var symbolSkipReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var activeTypeCountsBySymbol = context.Existing
            .Where(e => e.Status == StrategyStatus.Active)
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.StrategyType).ToDictionary(tg => tg.Key, tg => tg.Count()),
                StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "StrategyGenerationWorker: symbol priority — {Queued} queued (by ascending strategy count), {SkippedNoRegime} skipped (no fresh regime data), {SkippedLowConfidence} skipped (low-confidence regime)",
            prioritisedSymbols.Count,
            skippedNoRegimeSymbols.Count,
            lowConfidenceSymbolSet.Count);
        if (skippedNoRegimeSymbols.Count > 0)
            _logger.LogDebug("StrategyGenerationWorker: symbols without regime data: {Symbols}", string.Join(", ", skippedNoRegimeSymbols));
        if (lowConfidenceSymbolSet.Count > 0)
            _logger.LogDebug("StrategyGenerationWorker: symbols skipped for low-confidence regime: {Symbols}", string.Join(", ", lowConfidenceSymbolSet));

        using var screeningThrottle = new SemaphoreSlim(config.MaxParallelBacktests, config.MaxParallelBacktests);

        foreach (var symbol in prioritisedSymbols)
        {
            // All symbol-level skip gates are checked before candle loading to avoid expensive
            // backtest setup for symbols that are already over budget or otherwise ineligible.
            if (candidatesCreated >= config.MaxCandidates || ct.IsCancellationRequested)
                break;

            if (completedSymbolSet.Contains(symbol))
                continue;

            int generatedForSymbol = generatedCountBySymbol.GetValueOrDefault(symbol);
            int activeCount = context.ActiveCountBySymbol.GetValueOrDefault(symbol);
            if (activeCount + generatedForSymbol >= config.MaxActivePerSymbol)
            {
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "saturated"));
                symbolSkipReasons[symbol] = "saturated";
                symbolsSkipped++;
                continue;
            }

            var pairInfo = context.PairDataBySymbol.GetValueOrDefault(symbol);
            string baseCurrency = pairInfo?.BaseCurrency ?? (symbol.Length >= 6 ? symbol[..3] : symbol);
            string quoteCurrency = pairInfo?.QuoteCurrency ?? (symbol.Length >= 6 ? symbol[3..6] : "");

            candidatesPerCurrency.TryGetValue(baseCurrency, out var baseCount);
            candidatesPerCurrency.TryGetValue(quoteCurrency, out var quoteCount);
            if (baseCount >= config.MaxPerCurrencyGroup || (quoteCurrency.Length > 0 && quoteCount >= config.MaxPerCurrencyGroup))
            {
                symbolSkipReasons[symbol] = "currency_group_cap";
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "currency_group_cap"));
                symbolsSkipped++;
                continue;
            }

            if (_correlationCoordinator.IsSaturated(symbol, context.CorrelationGroupCounts, config.MaxCorrelatedCandidates))
            {
                _metrics.StrategyGenCorrelationSkipped.Add(1);
                symbolSkipReasons[symbol] = "correlation_group_saturated";
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "correlation_group_saturated"));
                symbolsSkipped++;
                continue;
            }

            var regime = context.RegimeBySymbol[symbol];
            var suitableTypes = context.TransitionSymbols.Contains(symbol)
                ? GetTransitionTypes()
                : _regimeMapper.GetStrategyTypes(regime);
            if (suitableTypes.Count == 0)
            {
                symbolSkipReasons[symbol] = "no_suitable_types";
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "no_suitable_types"));
                symbolsSkipped++;
                continue;
            }

            if (context.RegimeBudget.TryGetValue(regime, out var budget)
                && regimeCandidatesCreated.GetValueOrDefault(regime) >= budget)
            {
                symbolSkipReasons[symbol] = $"regime_budget_exhausted({regime})";
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "regime_budget_exhausted"));
                symbolsSkipped++;
                continue;
            }

            double confidence = context.RegimeConfidenceBySymbol.GetValueOrDefault(symbol, config.MinRegimeConfidence);
            double confidenceRange = 1.0 - config.MinRegimeConfidence;
            double confidenceFraction = confidenceRange > 0
                ? Math.Clamp((confidence - config.MinRegimeConfidence) / confidenceRange, 0, 1)
                : 1.0;
            double durationFactor = context.RegimeDetectedAtBySymbol.TryGetValue(symbol, out var detectedAt)
                ? _marketDataPolicy.ComputeRegimeDurationFactor(detectedAt, _timeProvider.GetUtcNow().UtcDateTime)
                : 1.0;
            int confidenceScaledMaxTemplates = Math.Max(1,
                (int)Math.Ceiling(config.MaxTemplatesPerCombo * confidenceFraction * durationFactor));

            var assetClass = ClassifyAsset(symbol, pairInfo);
            var screeningOptions = _marketDataPolicy.BuildScreeningOptions(
                symbol,
                pairInfo,
                assetClass,
                config.ScreeningSpreadPoints,
                config.ScreeningCommissionPerLot,
                config.ScreeningSlippagePips,
                _livePriceCache,
                _timeProvider.GetUtcNow().UtcDateTime);

            if (context.SpreadProfileProvider != null)
            {
                try
                {
                    // Spread profiles are optional enrichment; screening should continue with
                    // static spread assumptions if profile lookup fails.
                    var profiles = await context.SpreadProfileProvider.GetProfilesAsync(symbol, ct);
                    var spreadFunc = context.SpreadProfileProvider.BuildSpreadFunction(symbol, profiles);
                    if (spreadFunc != null)
                        screeningOptions.SpreadFunction = spreadFunc;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "StrategyGenerationWorker: spread profile load failed for {Symbol}", symbol);
                }
            }

            context.SymbolOverridesBySymbol.TryGetValue(symbol, out var symbolOverrides);
            var (acWR, acPF, acSh, acDD) = GetAssetClassThresholdMultipliers(assetClass);
            double baseWR = (symbolOverrides?.MinWinRate ?? config.MinWinRate) * acWR;
            double basePF = (symbolOverrides?.MinProfitFactor ?? config.MinProfitFactor) * acPF;
            double baseSh = (symbolOverrides?.MinSharpe ?? config.MinSharpe) * acSh;
            double baseDD = (symbolOverrides?.MaxDrawdownPct ?? config.MaxDrawdownPct) * acDD;
            bool hadUsableCandles = false;
            bool hadScreeningTasks = false;
            bool spreadRejected = false;
            string? lastSkipReason = null;
            bool countedProcessedSymbol = false;

            foreach (var timeframe in config.CandidateTimeframes)
            {
                if (candidatesCreated >= config.MaxCandidates || ct.IsCancellationRequested)
                    break;
                if (activeCount + generatedCountBySymbol.GetValueOrDefault(symbol) >= config.MaxActivePerSymbol)
                    break;

                var activeTypeCountsForSymbol = MergeTypeCounts(
                    activeTypeCountsBySymbol.GetValueOrDefault(symbol),
                    generatedTypeCountsBySymbol.GetValueOrDefault(symbol));

                var candleLoad = await LoadCandlesForScreeningAsync(db, candleCache, config, pairInfo, symbol, timeframe, ct);
                if (candleLoad.Candles == null)
                {
                    lastSkipReason ??= candleLoad.SkipReason;
                    continue;
                }

                hadUsableCandles = true;
                if (!countedProcessedSymbol)
                {
                    processedSymbolsCount++;
                    countedProcessedSymbol = true;
                }
                var candles = candleLoad.Candles;

                decimal atr = ComputeAtr(candles);
                if (!_spreadPolicy.PassesSpreadFilter(atr, screeningOptions, candles, assetClass, config.MaxSpreadToRangeRatio))
                {
                    spreadRejected = true;
                    lastSkipReason ??= "spread_filter";
                    _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "spread_filter"));
                    continue;
                }

                var suitableTypesForTimeframe = _feedbackCoordinator.ApplyPerformanceFeedback(
                    suitableTypes,
                    regime,
                    timeframe,
                    context.FeedbackRates);

                // Archetype rotation: when the diversity floor is enabled, sort suitable
                // types by current cycle-wide count ascending so starved archetypes get
                // first crack at the remaining MaxCandidates budget. Preserves relative
                // mapper priority as tiebreaker. Without this, the mapper's static order
                // starves later archetypes whenever the budget saturates early.
                if (config.EnforceArchetypeDiversity)
                {
                    var cycleCounts = AggregateCycleArchetypeCounts(generatedTypeCountsBySymbol);
                    var baseOrder = suitableTypesForTimeframe
                        .Select((t, i) => (Type: t, Priority: i))
                        .ToDictionary(x => x.Type, x => x.Priority);
                    suitableTypesForTimeframe = suitableTypesForTimeframe
                        .OrderBy(t => cycleCounts.GetValueOrDefault(t, 0))
                        .ThenBy(t => baseOrder[t])
                        .ToList();
                }
                var adaptiveAdjustments = ResolveAdaptiveAdjustments(context.AdaptiveAdjustmentsByContext, regime, timeframe);
                var thresholds = BuildThresholdsForTimeframe(
                    config,
                    adaptiveAdjustments,
                    regime,
                    timeframe,
                    baseWR,
                    basePF,
                    baseSh,
                    baseDD,
                    context.Haircuts);

                double mtfBoost = ComputeMultiTimeframeConfidenceBoost(regime, symbol, timeframe, context.RegimeBySymbolTf);
                int mtfScaledMaxTemplates = Math.Max(1,
                    (int)Math.Ceiling(confidenceScaledMaxTemplates * mtfBoost));

                var taskArgs = new ScreeningTaskArgs(
                    candles,
                    atr,
                    symbol,
                    timeframe,
                    regime,
                    suitableTypesForTimeframe,
                    activeTypeCountsForSymbol,
                    screeningOptions,
                    thresholds,
                    screeningConfig,
                    mtfScaledMaxTemplates,
                    candidatesCreated);
                var screeningTasks = BuildScreeningTasks(
                    context,
                    taskArgs,
                    screeningThrottle,
                    () => Interlocked.Increment(ref candidatesScreened),
                    ct);

                if (screeningTasks.Count > 0)
                {
                    hadScreeningTasks = true;
                    var results = await Task.WhenAll(screeningTasks.Select(f => f()));
                    foreach (var selection in _candidateSelectionPolicy.SelectBestCandidates(
                                 results.Where(r => r != null && r.Passed)!.Cast<ScreeningOutcome>()))
                    {
                        if (candidatesCreated >= config.MaxCandidates)
                            break;
                        if (context.RegimeBudget.TryGetValue(regime, out var regimeBudgetCount)
                            && regimeCandidatesCreated.GetValueOrDefault(regime) >= regimeBudgetCount)
                            break;

                        var result = ApplyCandidateSelectionMetadata(selection.Candidate, selection, context.CycleId);
                        var combo = selection.Identity.Combo;
                        if (context.ExistingSet.Contains(combo))
                            continue;

                        if (pendingCandidates.Count > 0
                            && StrategyScreeningEngine.IsCorrelatedWithAccepted(result, pendingCandidates, config.ScreeningInitialBalance))
                        {
                            _metrics.StrategyGenScreeningRejections.Add(1,
                                new KeyValuePair<string, object?>("gate", "correlation_precheck"));
                            continue;
                        }

                        pendingCandidates.Add(result);
                        context.ExistingSet.Add(combo);
                        candidatesCreated++;
                        IncrementGeneratedCounts(symbol, result.Strategy.StrategyType, generatedCountBySymbol, generatedTypeCountsBySymbol);
                        regimeCandidatesCreated[regime] = regimeCandidatesCreated.GetValueOrDefault(regime) + 1;
                        candidatesPerCurrency[baseCurrency] = candidatesPerCurrency.GetValueOrDefault(baseCurrency) + 1;
                        if (quoteCurrency.Length > 0)
                            candidatesPerCurrency[quoteCurrency] = candidatesPerCurrency.GetValueOrDefault(quoteCurrency) + 1;
                        _correlationCoordinator.IncrementCount(symbol, context.CorrelationGroupCounts);
                    }
                }
            }

            if (!hadUsableCandles)
            {
                symbolSkipReasons[symbol] = lastSkipReason ?? "insufficient_candles";
            }
            else if (!hadScreeningTasks)
            {
                symbolSkipReasons[symbol] = spreadRejected ? "spread_filter" : (lastSkipReason ?? "no_screening_tasks");
            }
            else
            {
                symbolSkipReasons.Remove(symbol);
            }

            completedSymbolSet.Add(symbol);
            await _checkpointCoordinator.SaveAsync(
                writeCtx,
                context.CycleId,
                checkpointFingerprint,
                new StrategyGenerationCheckpointProgressSnapshot(
                    completedSymbolSet,
                    candidatesCreated,
                    resumeState.ReserveCreated,
                    candidatesScreened,
                    processedSymbolsCount,
                    symbolsSkipped,
                    pendingCandidates,
                    candidatesPerCurrency,
                    regimeCandidatesCreated,
                    context.CorrelationGroupCounts),
                ct,
                symbol);
        }

        if (symbolSkipReasons.Count > 0)
        {
            var skipSummary = symbolSkipReasons
                .GroupBy(kv => kv.Value)
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            _logger.LogInformation(
                "StrategyGenerationWorker: symbol skip summary — {SkipSummary}",
                string.Join(", ", skipSummary));
            _logger.LogDebug(
                "StrategyGenerationWorker: per-symbol skips — {Details}",
                string.Join("; ", symbolSkipReasons.Select(kv => $"{kv.Key}:{kv.Value}")));
        }

        var faultCounts = context.FaultTracker.GetFaultCounts();
        if (faultCounts.Count > 0)
        {
            _logger.LogInformation(
                "StrategyGenerationWorker: per-type faults — {Faults}",
                string.Join(", ", faultCounts.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // Archetype diversity floor diagnostic: surface any StrategyType that
        // is regime-compatible with at least one active symbol but fell below
        // MinCandidatesPerArchetype for the cycle. We log + metric; reserve
        // planner is responsible for attempting to close the gap with relaxed
        // thresholds. Keeping this as observability-only here preserves
        // MaxCandidates as a hard cap.
        if (context.Config.EnforceArchetypeDiversity)
        {
            var cycleCounts = AggregateCycleArchetypeCounts(generatedTypeCountsBySymbol);
            var compatibleTypes = context.RegimeBySymbol.Values
                .SelectMany(r => _regimeMapper.GetStrategyTypes(r))
                .ToHashSet();
            var shortfalls = compatibleTypes
                .Select(t => (Type: t, Count: cycleCounts.GetValueOrDefault(t, 0)))
                .Where(x => x.Count < context.Config.MinCandidatesPerArchetype)
                .ToList();
            if (shortfalls.Count > 0)
            {
                _logger.LogWarning(
                    "StrategyGenerationWorker: archetype-diversity floor unmet — {Shortfalls} (floor={Floor})",
                    string.Join(", ", shortfalls.Select(s => $"{s.Type}:{s.Count}")),
                    context.Config.MinCandidatesPerArchetype);
                foreach (var shortfall in shortfalls)
                    _metrics.StrategyGenSymbolsSkipped.Add(1,
                        new KeyValuePair<string, object?>("reason", "archetype_floor_unmet"),
                        new KeyValuePair<string, object?>("archetype", shortfall.Type.ToString()));
            }
        }

        return new StrategyGenerationPrimaryScreeningResult(
            pendingCandidates,
            candidatesCreated,
            candidatesScreened,
            processedSymbolsCount,
            symbolsSkipped + skippedNoRegimeSymbols.Count + lowConfidenceSymbolSet.Count,
            candidatesPerCurrency,
            regimeCandidatesCreated,
            generatedCountBySymbol,
            generatedTypeCountsBySymbol,
            completedSymbolSet);
    }

    private async Task<CandleLoadResult> LoadCandlesForScreeningAsync(
        DbContext db,
        CandleLruCache candleCache,
        GenerationConfig config,
        CurrencyPair? pairInfo,
        string symbol,
        Timeframe timeframe,
        CancellationToken ct)
    {
        int scaledMonths = ScaleScreeningWindowForTimeframe(config.ScreeningMonths, timeframe);
        var cacheKey = (symbol, timeframe);

        if (!candleCache.TryGet(cacheKey, out var candles))
        {
            var screeningFrom = _timeProvider.GetUtcNow().UtcDateTime.AddMonths(-scaledMonths);
            candles = await db.Set<Candle>()
                .Where(c => c.Symbol == symbol
                         && c.Timeframe == timeframe
                         && c.Timestamp >= screeningFrom
                         && c.IsClosed
                         && !c.IsDeleted)
                .OrderBy(c => c.Timestamp)
                .Select(c => new Candle
                {
                    Id = c.Id,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume,
                    Timestamp = c.Timestamp,
                })
                .ToListAsync(ct);
            int evictions = candleCache.Put(cacheKey, candles);
            for (int i = 0; i < evictions; i++)
                _metrics.StrategyGenCandleCacheEvictions.Add(1);
        }

        if (candles.Count < config.DataHealthMinCandles)
        {
            _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "insufficient_candles"));
            return new CandleLoadResult(null, "insufficient_candles");
        }

        if (config.MaxCandleAgeHours > 0)
        {
            double effectiveCandleAgeHours = _marketDataPolicy.ComputeEffectiveCandleAgeHours(
                candles[^1].Timestamp,
                pairInfo?.TradingHoursJson,
                _timeProvider.GetUtcNow().UtcDateTime);
            if (effectiveCandleAgeHours > config.MaxCandleAgeHours)
            {
                _metrics.StrategyGenSymbolsSkipped.Add(1, new KeyValuePair<string, object?>("reason", "stale_candles"));
                return new CandleLoadResult(null, "stale_candles");
            }
        }

        return new CandleLoadResult(candles, null);
    }

    private static ScreeningThresholds BuildThresholdsForTimeframe(
        GenerationConfig config,
        AdaptiveThresholdAdjustments adjustments,
        MarketRegimeEnum regime,
        Timeframe timeframe,
        double baseWR,
        double basePF,
        double baseSh,
        double baseDD,
        HaircutRatios? haircuts = null)
    {
        var (scaledWR, scaledPF, scaledSh, scaledDD) = ScaleThresholdsForRegime(baseWR, basePF, baseSh, baseDD, regime);
        scaledWR = ApplyAdaptiveAdjustment(scaledWR, adjustments.WinRateMultiplier);
        scaledPF = ApplyAdaptiveAdjustment(scaledPF, adjustments.ProfitFactorMultiplier);
        scaledSh = ApplyAdaptiveAdjustment(scaledSh, adjustments.SharpeMultiplier);
        scaledDD = ApplyAdaptiveAdjustment(scaledDD, adjustments.DrawdownMultiplier);

        if (haircuts is { SampleCount: >= 5 or < 0 })
        {
            scaledWR /= Math.Max(0.5, haircuts.WinRateHaircut);
            scaledPF /= Math.Max(0.5, haircuts.ProfitFactorHaircut);
            scaledSh /= Math.Max(0.5, haircuts.SharpeHaircut);
            scaledDD *= Math.Max(0.5, haircuts.DrawdownInflation);
        }

        int adjustedMinTrades = AdjustMinTradesForTimeframe(config.MinTotalTrades, timeframe);
        return new ScreeningThresholds(
            scaledWR,
            scaledPF,
            scaledSh,
            scaledDD,
            adjustedMinTrades,
            config.MaxCostToWinRatio);
    }

    private List<Func<Task<ScreeningOutcome?>>> BuildScreeningTasks(
        StrategyGenerationScreeningContext context,
        ScreeningTaskArgs args,
        SemaphoreSlim screeningThrottle,
        Action onCandidateScreened,
        CancellationToken ct)
    {
        var config = context.Config;
        double trainRatio = GetTrainSplitRatio(args.Candles.Count);
        int splitIndex = (int)(args.Candles.Count * trainRatio);
        var trainCandles = args.Candles.Take(splitIndex).ToList();
        var testCandles = args.Candles.Skip(splitIndex).ToList();

        var tasks = new List<Func<Task<ScreeningOutcome?>>>();

        foreach (var strategyType in args.SuitableTypes)
        {
            if (args.CandidatesCreated + tasks.Count >= config.MaxCandidates)
                break;

            if (context.FaultTracker.IsTypeDisabled(strategyType))
                continue;

            if (args.ActiveTypeCountsForSymbol.TryGetValue(strategyType, out var typeCount)
                && typeCount >= config.MaxActivePerTypePerSymbol)
                continue;

            var combo = new CandidateCombo(strategyType, args.Symbol, args.Timeframe);
            if (context.ExistingSet.Contains(combo) || context.FullyPrunedCombos.Contains(combo))
                continue;

            var higherTf = GetHigherTimeframe(args.Timeframe);
            if (higherTf.HasValue
                && context.RegimeBySymbolTf.TryGetValue((args.Symbol.ToUpperInvariant(), higherTf.Value), out var higherRegime)
                && !IsRegimeCompatibleWithStrategy(strategyType, higherRegime))
            {
                continue;
            }

            var templates = _templateProvider.GetTemplates(strategyType);

            // Blend TPE-surrogate proposals learned from past screening failures.
            // Proposals are prepended so they get the best priority slots in the
            // MaxTemplatesPerCombo-capped queue. The planner's dedup check below
            // (failedParamsForCombo + normalizedParams) still applies so duplicates
            // with prior rejections won't re-run within cooldown.
            var surrogateProposals = _surrogateService.GetProposals(
                strategyType,
                args.Symbol,
                args.Timeframe,
                args.Regime,
                count: args.MaxTemplates);
            if (surrogateProposals.Count > 0)
            {
                var merged = new List<string>(surrogateProposals.Count + templates.Count);
                var seenTemplates = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in surrogateProposals)
                    if (seenTemplates.Add(p)) merged.Add(p);
                foreach (var t in templates)
                    if (seenTemplates.Add(t)) merged.Add(t);
                templates = merged;
            }

            if (templates.Count == 0)
                continue;

            // UCB1-aware template ordering when the config flag is on and sample counts are
            // available; falls back to the pure survival-rate ordering otherwise so unit
            // tests and older deployments behave exactly as before.
            IReadOnlyList<string> orderedTemplates;
            if (config.UseUcb1TemplateSelection
                && context.TemplateSampleCounts is { Count: > 0 })
            {
                orderedTemplates = StrategyGenerationHelpers.OrderTemplatesForRegimeUcb1(
                    templates,
                    args.Regime,
                    context.TemplateSurvivalRates,
                    context.TemplateSampleCounts,
                    config.Ucb1ExplorationConstant);
            }
            else
            {
                orderedTemplates = OrderTemplatesForRegime(
                    templates,
                    args.Regime,
                    context.TemplateSurvivalRates,
                    strategyType,
                    args.Timeframe);
            }
            context.PrunedTemplates.TryGetValue(combo, out var failedParamsForCombo);
            int templatesQueued = 0;

            foreach (var parametersJson in orderedTemplates)
            {
                if (templatesQueued >= args.MaxTemplates)
                    break;

                var enrichedParams = InjectAtrContext(parametersJson, args.Atr);
                var normalizedParams = NormalizeTemplateParameters(enrichedParams);
                if (failedParamsForCombo != null && failedParamsForCombo.Contains(normalizedParams))
                    continue;

                var capturedType = strategyType;
                var capturedIdx = templatesQueued;

                tasks.Add(async () =>
                {
                    await screeningThrottle.WaitAsync(ct);
                    try
                    {
                        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        taskCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));

                        MarketRegimeEnum? oosRegime = context.RegimeTransitions.TryGetValue(args.Symbol, out var previousRegime)
                            ? previousRegime
                            : null;
                        onCandidateScreened();
                        _metrics.StrategyCandidatesScreened.Add(1,
                            new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        var screeningSw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await context.ScreeningEngine.ScreenCandidateAsync(
                            capturedType,
                            args.Symbol,
                            args.Timeframe,
                            enrichedParams,
                            capturedIdx,
                            args.Candles,
                            trainCandles,
                            testCandles,
                            args.ScreeningOptions,
                            args.Thresholds,
                            args.ScreeningConfig,
                            args.Regime,
                            args.Regime,
                            "Primary",
                            taskCts.Token,
                            oosRegime,
                            context.PortfolioEquityCurve,
                            context.Haircuts);
                        screeningSw.Stop();
                        _metrics.StrategyGenScreeningDurationMs.Record(
                            screeningSw.Elapsed.TotalMilliseconds,
                            new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        if (result != null && !result.Passed)
                        {
                            // ── Chicken-and-egg deferral for CompositeML ──
                            // A CompositeML candidate rejected for zero IS trades when no
                            // active MLModel exists is actually a "waiting for model"
                            // state, not a genuine failure. Park it as a PendingModel
                            // strategy and queue a training run; the event handler on
                            // MLModelActivatedIntegrationEvent will re-screen it once
                            // the model is ready. Only zero-trade rejections are eligible
                            // for deferral — every other gate failure is a real rejection.
                            if (result.Failure == ScreeningFailureReason.ZeroTradesIS
                                && capturedType == StrategyType.CompositeML)
                            {
                                bool deferred = await _deferredCompositeMLRegistrar.TryDeferAsync(
                                    args.Symbol,
                                    args.Timeframe,
                                    result.Strategy?.ParametersJson ?? "{}",
                                    context.CycleId,
                                    candidateHash: null,
                                    taskCts.Token);
                                if (deferred)
                                {
                                    _metrics.StrategyGenScreeningRejections.Add(1,
                                        new KeyValuePair<string, object?>("gate", "deferred_pending_model"));
                                    return null;
                                }
                            }

                            await context.AuditLogger.LogFailureAsync(result, ct);
                            return null;
                        }

                        return result;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "StrategyGenerationWorker: screening task timed out for {Type} on {Symbol}/{Tf}",
                            capturedType,
                            args.Symbol,
                            args.Timeframe);
                        _metrics.StrategyGenScreeningRejections.Add(1,
                            new KeyValuePair<string, object?>("gate", "timeout"));
                        await context.AuditLogger.LogExecutionFailureAsync(
                            capturedType,
                            args.Symbol,
                            args.Timeframe,
                            args.Regime,
                            args.Regime,
                            "Primary",
                            "Timeout",
                            "Primary",
                            null,
                            ct);
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            ex,
                            "StrategyGenerationWorker: screening task faulted for {Type} on {Symbol}/{Tf}",
                            capturedType,
                            args.Symbol,
                            args.Timeframe);
                        _metrics.StrategyGenScreeningRejections.Add(1,
                            new KeyValuePair<string, object?>("gate", "task_fault"));
                        await context.AuditLogger.LogExecutionFailureAsync(
                            capturedType,
                            args.Symbol,
                            args.Timeframe,
                            args.Regime,
                            args.Regime,
                            "Primary",
                            "TaskFault",
                            "Primary",
                            null,
                            ct);
                        context.FaultTracker.RecordFault(capturedType);
                        if (context.FaultTracker.IsTypeDisabled(capturedType))
                        {
                            _logger.LogWarning(
                                "StrategyGenerationWorker: {Type} disabled for remainder of cycle — too many screening faults",
                                capturedType);
                            _metrics.StrategyGenTypeFaultDisabled.Add(1,
                                new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                        }

                        return null;
                    }
                    finally
                    {
                        screeningThrottle.Release();
                    }
                });
                templatesQueued++;
            }
        }

        return tasks;
    }

    private ScreeningOutcome ApplyCandidateSelectionMetadata(
        ScreeningOutcome candidate,
        CandidateSelectionResult selection,
        string cycleId)
    {
        candidate.Strategy.GenerationCycleId = cycleId;
        candidate.Strategy.GenerationCandidateId = selection.Identity.CandidateId;

        var baseMetrics = candidate.Metrics ?? BuildBaseMetrics(candidate);
        double qualityScore = baseMetrics.QualityScore > 0
            ? baseMetrics.QualityScore
            : ScreeningQualityScorer.ComputeScore(
                candidate.TrainResult,
                candidate.OosResult,
                baseMetrics.EquityCurveR2,
                baseMetrics.WalkForwardWindowsPassed,
                null,
                baseMetrics.MonteCarloPValue,
                baseMetrics.ShufflePValue,
                baseMetrics.MaxTradeTimeConcentration,
                baseMetrics.MarginalSharpeContribution,
                baseMetrics.KellySharpeRatio,
                baseMetrics.FixedLotSharpeRatio);

        var metrics = baseMetrics with
        {
            CycleId = cycleId,
            CandidateId = selection.Identity.CandidateId,
            SelectionScore = selection.Score.TotalScore,
            SelectionScoreBreakdown = selection.Score,
            QualityScore = qualityScore,
            QualityBand = ScreeningQualityScorer.ComputeBand(qualityScore),
        };

        candidate.Strategy.ScreeningMetricsJson = metrics.ToJson();
        return candidate with { Metrics = metrics };
    }

    private ScreeningMetrics BuildBaseMetrics(ScreeningOutcome candidate)
        => ScreeningMetrics.FromJson(candidate.Strategy.ScreeningMetricsJson)
           ?? new ScreeningMetrics
           {
               Regime = candidate.Regime.ToString(),
               ObservedRegime = candidate.ObservedRegime.ToString(),
               GenerationSource = candidate.GenerationSource,
               ScreenedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
           };

    private static AdaptiveThresholdAdjustments ResolveAdaptiveAdjustments(
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adjustmentsByContext,
        MarketRegimeEnum regime,
        Timeframe timeframe)
        => adjustmentsByContext.TryGetValue((regime, timeframe), out var exact)
            ? exact
            : AdaptiveThresholdAdjustments.Neutral;

    private static Dictionary<StrategyType, int> AggregateCycleArchetypeCounts(
        Dictionary<string, Dictionary<StrategyType, int>> generatedTypeCountsBySymbol)
    {
        var result = new Dictionary<StrategyType, int>();
        foreach (var perSymbol in generatedTypeCountsBySymbol.Values)
        {
            foreach (var (type, count) in perSymbol)
                result[type] = result.GetValueOrDefault(type) + count;
        }
        return result;
    }

    private static Dictionary<StrategyType, int> MergeTypeCounts(
        Dictionary<StrategyType, int>? existingCounts,
        Dictionary<StrategyType, int>? generatedCounts)
    {
        var merged = existingCounts != null
            ? new Dictionary<StrategyType, int>(existingCounts)
            : new Dictionary<StrategyType, int>();

        if (generatedCounts == null)
            return merged;

        foreach (var (strategyType, count) in generatedCounts)
            merged[strategyType] = merged.GetValueOrDefault(strategyType) + count;

        return merged;
    }

    private static void IncrementGeneratedCounts(
        string symbol,
        StrategyType strategyType,
        Dictionary<string, int> generatedCountBySymbol,
        Dictionary<string, Dictionary<StrategyType, int>> generatedTypeCountsBySymbol)
    {
        generatedCountBySymbol[symbol] = generatedCountBySymbol.GetValueOrDefault(symbol) + 1;

        if (!generatedTypeCountsBySymbol.TryGetValue(symbol, out var typeCounts))
        {
            typeCounts = new Dictionary<StrategyType, int>();
            generatedTypeCountsBySymbol[symbol] = typeCounts;
        }

        typeCounts[strategyType] = typeCounts.GetValueOrDefault(strategyType) + 1;
    }
}
