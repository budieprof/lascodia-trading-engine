using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
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

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationReserveScreeningPlanner))]
/// <summary>
/// Screens reserve candidates that broaden coverage after the main pass has finished.
/// </summary>
internal sealed class StrategyGenerationReserveScreeningPlanner : IStrategyGenerationReserveScreeningPlanner
{
    private sealed record CandleLoadResult(List<Candle>? Candles, string? SkipReason);

    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly IStrategyParameterTemplateProvider _templateProvider;
    private readonly ILivePriceCache _livePriceCache;
    private readonly TradingMetrics _metrics;
    private readonly IStrategyCandidateSelectionPolicy _candidateSelectionPolicy;
    private readonly IStrategyGenerationMarketDataPolicy _marketDataPolicy;
    private readonly IStrategyGenerationSpreadPolicy _spreadPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly string[][] _correlationGroups;

    public StrategyGenerationReserveScreeningPlanner(
        ILogger<StrategyGenerationWorker> logger,
        IStrategyParameterTemplateProvider templateProvider,
        ILivePriceCache livePriceCache,
        TradingMetrics metrics,
        IStrategyCandidateSelectionPolicy candidateSelectionPolicy,
        IStrategyGenerationMarketDataPolicy marketDataPolicy,
        IStrategyGenerationSpreadPolicy spreadPolicy,
        TimeProvider timeProvider,
        CorrelationGroupOptions correlationGroups)
    {
        _logger = logger;
        _templateProvider = templateProvider;
        _livePriceCache = livePriceCache;
        _metrics = metrics;
        _candidateSelectionPolicy = candidateSelectionPolicy;
        _marketDataPolicy = marketDataPolicy;
        _spreadPolicy = spreadPolicy;
        _timeProvider = timeProvider;
        _correlationGroups = correlationGroups.Groups;
    }

    public async Task<int> ScreenReserveCandidatesAsync(
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
        CancellationToken ct)
    {
        // Reserve screening intentionally starts from symbols already known to have regime data
        // but prioritizes under-covered combinations and missing strategy-type coverage.
        var config = context.Config;
        int reserveCreated = 0;
        int totalCreated = candidatesCreated;

        var existingTypeCountsBySymbol = context.Existing
            .Where(e => e.Status == StrategyStatus.Active)
            .GroupBy(e => e.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.StrategyType).ToDictionary(tg => tg.Key, tg => tg.Count()),
                StringComparer.OrdinalIgnoreCase);

        var totalCountBySymbol = context.Existing
            .GroupBy(e => e.Symbol)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var prioritisedSymbols = context.RegimeBySymbol.Keys
            .OrderBy(sym => totalCountBySymbol.TryGetValue(sym, out var count) ? count : 0)
            .ThenBy(sym => sym, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var reserveScreeningConfig = BuildScreeningConfig(config);
        using var reserveThrottle = new SemaphoreSlim(config.MaxParallelBacktests, config.MaxParallelBacktests);

        foreach (var symbol in prioritisedSymbols)
        {
            if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates)
                break;
            if (!context.RegimeBySymbol.TryGetValue(symbol, out var regime))
                continue;
            int activeCount = context.ActiveCountBySymbol.GetValueOrDefault(symbol);
            if (activeCount + generatedCountBySymbol.GetValueOrDefault(symbol) >= config.MaxActivePerSymbol)
                continue;

            var currentTypes = existingTypeCountsBySymbol.GetValueOrDefault(symbol)?.Keys.ToHashSet() ?? [];
            if (generatedTypeCountsBySymbol.TryGetValue(symbol, out var generatedTypes))
                currentTypes = currentTypes.Concat(generatedTypes.Keys).ToHashSet();
            var counterTypes = GetCounterRegimeTypes(regime).Where(t => !currentTypes.Contains(t)).ToList();
            if (counterTypes.Count == 0)
                continue;

            var pairInfo = context.PairDataBySymbol.GetValueOrDefault(symbol);
            string baseCurrency = pairInfo?.BaseCurrency ?? (symbol.Length >= 6 ? symbol[..3] : symbol);
            string quoteCurrency = pairInfo?.QuoteCurrency ?? (symbol.Length >= 6 ? symbol[3..6] : "");
            candidatesPerCurrency.TryGetValue(baseCurrency, out var baseCount);
            candidatesPerCurrency.TryGetValue(quoteCurrency, out var quoteCount);
            if (baseCount >= config.MaxPerCurrencyGroup || (quoteCurrency.Length > 0 && quoteCount >= config.MaxPerCurrencyGroup))
                continue;
            if (IsCorrelationGroupSaturated(symbol, context.CorrelationGroupCounts, config.MaxCorrelatedCandidates))
                continue;

            var assetClass = ClassifyAsset(symbol, pairInfo);
            var reserveOptions = _marketDataPolicy.BuildScreeningOptions(
                symbol,
                pairInfo,
                assetClass,
                config.ScreeningSpreadPoints,
                config.ScreeningCommissionPerLot,
                config.ScreeningSlippagePips,
                _livePriceCache,
                _timeProvider.GetUtcNow().UtcDateTime);
            context.SymbolOverridesBySymbol.TryGetValue(symbol, out var symbolOverrides);
            var (acWR, acPF, acSh, acDD) = GetAssetClassThresholdMultipliers(assetClass);
            double baseWR = (symbolOverrides?.MinWinRate ?? config.MinWinRate) * acWR;
            double basePF = (symbolOverrides?.MinProfitFactor ?? config.MinProfitFactor) * acPF;
            double baseSh = (symbolOverrides?.MinSharpe ?? config.MinSharpe) * acSh;
            double baseDD = (symbolOverrides?.MaxDrawdownPct ?? config.MaxDrawdownPct) * acDD;

            if (context.SpreadProfileProvider != null)
            {
                try
                {
                    var profiles = await context.SpreadProfileProvider.GetProfilesAsync(symbol, ct);
                    var spreadFunc = context.SpreadProfileProvider.BuildSpreadFunction(symbol, profiles);
                    if (spreadFunc != null)
                        reserveOptions.SpreadFunction = spreadFunc;
                }
                catch
                {
                    // Non-critical.
                }
            }

            foreach (var reserveTf in config.CandidateTimeframes)
            {
                if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates)
                    break;

                var candleLoad = await LoadCandlesForScreeningAsync(db, candleCache, config, pairInfo, symbol, reserveTf, ct);
                if (candleLoad.Candles == null)
                    continue;

                var candles = candleLoad.Candles;
                decimal atr = ComputeAtr(candles);
                if (!_spreadPolicy.PassesSpreadFilter(atr, reserveOptions, candles, assetClass, config.MaxSpreadToRangeRatio))
                {
                    _metrics.StrategyGenReserveSpreadSkipped.Add(1);
                    continue;
                }

                var reserveTasks = new List<Func<Task<ScreeningOutcome?>>>();

                double trainRatio = GetTrainSplitRatio(candles.Count);
                int splitIdx = (int)(candles.Count * trainRatio);
                var trainCandles = candles.Take(splitIdx).ToList();
                var testCandles = candles.Skip(splitIdx).ToList();

                foreach (var counterType in counterTypes)
                {
                    if (reserveCreated + reserveTasks.Count >= config.StrategicReserveQuota
                        || totalCreated + reserveTasks.Count >= config.MaxCandidates)
                        break;

                    if (context.FaultTracker.IsTypeDisabled(counterType))
                        continue;

                    int typeCount = existingTypeCountsBySymbol.GetValueOrDefault(symbol)?.GetValueOrDefault(counterType) ?? 0;
                    typeCount += generatedTypeCountsBySymbol.GetValueOrDefault(symbol)?.GetValueOrDefault(counterType) ?? 0;
                    if (typeCount >= config.MaxActivePerTypePerSymbol)
                        continue;

                    var combo = new CandidateCombo(counterType, symbol, reserveTf);
                    if (context.ExistingSet.Contains(combo) || context.FullyPrunedCombos.Contains(combo))
                        continue;

                    var templates = _templateProvider.GetTemplates(counterType);
                    if (templates.Count == 0)
                        continue;

                    var reserveTargetRegime = GetReserveTargetRegime(regime, counterType);
                    var reserveAdaptiveAdjustments = ResolveAdaptiveAdjustments(
                        context.AdaptiveAdjustmentsByContext,
                        reserveTargetRegime,
                        reserveTf);
                    var primaryThresholds = BuildThresholdsForTimeframe(
                        config,
                        reserveAdaptiveAdjustments,
                        reserveTargetRegime,
                        reserveTf,
                        baseWR,
                        basePF,
                        baseSh,
                        baseDD,
                        context.Haircuts);
                    // Reserve-pass threshold relaxation — configurable via
                    // StrategyGeneration:ArchetypeReserveThresholdMultiplier (default 0.75).
                    // Applied to MinWinRate/MinProfitFactor/MinSharpe directly; inverse
                    // applied to MaxDrawdownPct so lower multiplier ↔ more permissive gate.
                    double mul = Math.Clamp(config.ArchetypeReserveThresholdMultiplier, 0.50, 1.00);
                    double ddRelax = 1.0 + (1.0 - mul);
                    var thresholds = new ScreeningThresholds(
                        primaryThresholds.MinWinRate * mul,
                        primaryThresholds.MinProfitFactor * mul,
                        primaryThresholds.MinSharpe * mul,
                        primaryThresholds.MaxDrawdownPct * ddRelax,
                        primaryThresholds.MinTotalTrades);
                    var orderedTemplates = OrderTemplatesForRegime(
                        templates,
                        reserveTargetRegime,
                        context.TemplateSurvivalRates,
                        counterType,
                        reserveTf);
                    context.PrunedTemplates.TryGetValue(combo, out var failedParamsForCombo);

                    string? selectedParams = null;
                    foreach (var templateJson in orderedTemplates)
                    {
                        var enriched = InjectAtrContext(templateJson, atr);
                        var normalizedParams = NormalizeTemplateParameters(enriched);
                        if (failedParamsForCombo != null && failedParamsForCombo.Contains(normalizedParams))
                            continue;
                        selectedParams = enriched;
                        break;
                    }

                    if (selectedParams == null)
                        continue;

                    var capturedType = counterType;
                    var capturedParams = selectedParams;
                    var capturedTargetRegime = reserveTargetRegime;
                    var capturedThresholds = thresholds;
                    reserveTasks.Add(async () =>
                    {
                        await reserveThrottle.WaitAsync(ct);
                        try
                        {
                            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            taskCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));

                            MarketRegimeEnum? reserveOosRegime = context.RegimeTransitions.TryGetValue(symbol, out var previousRegime)
                                ? previousRegime
                                : null;
                            onCandidateScreened();
                            _metrics.StrategyCandidatesScreened.Add(1,
                                new KeyValuePair<string, object?>("strategy_type", capturedType.ToString()));
                            var result = await context.ScreeningEngine.ScreenCandidateAsync(
                                capturedType,
                                symbol,
                                reserveTf,
                                capturedParams,
                                0,
                                candles,
                                trainCandles,
                                testCandles,
                                reserveOptions,
                                capturedThresholds,
                                reserveScreeningConfig,
                                capturedTargetRegime,
                                regime,
                                "Reserve",
                                taskCts.Token,
                                reserveOosRegime,
                                context.PortfolioEquityCurve,
                                context.Haircuts);
                            if (result != null && !result.Passed)
                            {
                                await context.AuditLogger.LogFailureAsync(result, ct);
                                return null;
                            }

                            return result;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            _logger.LogWarning(
                                "StrategyGenerationWorker: reserve screening timed out for {Type} on {Symbol}/{Tf}",
                                capturedType,
                                symbol,
                                reserveTf);
                            _metrics.StrategyGenScreeningRejections.Add(1,
                                new KeyValuePair<string, object?>("gate", "reserve_timeout"));
                            await context.AuditLogger.LogExecutionFailureAsync(
                                capturedType,
                                symbol,
                                reserveTf,
                                capturedTargetRegime,
                                regime,
                                "Reserve",
                                "Timeout",
                                "Reserve",
                                capturedTargetRegime,
                                ct);
                            return null;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(
                                ex,
                                "StrategyGenerationWorker: reserve screening faulted for {Type} on {Symbol}/{Tf}",
                                capturedType,
                                symbol,
                                reserveTf);
                            _metrics.StrategyGenScreeningRejections.Add(1,
                                new KeyValuePair<string, object?>("gate", "reserve_task_fault"));
                            await context.AuditLogger.LogExecutionFailureAsync(
                                capturedType,
                                symbol,
                                reserveTf,
                                capturedTargetRegime,
                                regime,
                                "Reserve",
                                "TaskFault",
                                "Reserve",
                                capturedTargetRegime,
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
                            reserveThrottle.Release();
                        }
                    });
                }

                if (reserveTasks.Count == 0)
                    continue;

                var results = await Task.WhenAll(reserveTasks.Select(f => f()));
                foreach (var selection in _candidateSelectionPolicy.SelectBestCandidates(
                             results.Where(r => r != null && r.Passed)!.Cast<ScreeningOutcome>()))
                {
                    if (reserveCreated >= config.StrategicReserveQuota || totalCreated >= config.MaxCandidates)
                        break;

                    var result = ApplyCandidateSelectionMetadata(selection.Candidate, selection, context.CycleId);
                    var combo = selection.Identity.Combo;
                    if (context.ExistingSet.Contains(combo))
                        continue;

                    candidatesPerCurrency.TryGetValue(baseCurrency, out baseCount);
                    candidatesPerCurrency.TryGetValue(quoteCurrency, out quoteCount);
                    if (baseCount >= config.MaxPerCurrencyGroup || (quoteCurrency.Length > 0 && quoteCount >= config.MaxPerCurrencyGroup))
                        continue;
                    if (IsCorrelationGroupSaturated(symbol, context.CorrelationGroupCounts, config.MaxCorrelatedCandidates))
                        continue;
                    var correlationCandidates = string.Equals(result.GenerationSource, "Reserve", StringComparison.OrdinalIgnoreCase)
                        ? pendingCandidates
                            .Where(existing => !string.Equals(
                                existing.Strategy.Symbol,
                                result.Strategy.Symbol,
                                StringComparison.OrdinalIgnoreCase))
                            .ToList()
                        : pendingCandidates;
                    if (correlationCandidates.Count > 0
                        && StrategyScreeningEngine.IsCorrelatedWithAccepted(result, correlationCandidates, config.ScreeningInitialBalance))
                    {
                        _metrics.StrategyGenScreeningRejections.Add(1,
                            new KeyValuePair<string, object?>("gate", "correlation_precheck"));
                        continue;
                    }

                    pendingCandidates.Add(result);
                    context.ExistingSet.Add(combo);
                    totalCreated++;
                    reserveCreated++;
                    IncrementGeneratedCounts(symbol, result.Strategy.StrategyType, generatedCountBySymbol, generatedTypeCountsBySymbol);
                    candidatesPerCurrency[baseCurrency] = candidatesPerCurrency.GetValueOrDefault(baseCurrency) + 1;
                    if (quoteCurrency.Length > 0)
                        candidatesPerCurrency[quoteCurrency] = candidatesPerCurrency.GetValueOrDefault(quoteCurrency) + 1;
                    IncrementCorrelationGroupCount(symbol, context.CorrelationGroupCounts);

                    _logger.LogInformation("StrategyGenerationWorker: reserve — {Name} (counter-{Regime})", result.Strategy.Name, regime);
                }
            }

            await saveCheckpointAsync(reserveCreated);
        }

        if (reserveCreated > 0)
            _logger.LogInformation("StrategyGenerationWorker: {Count} strategic reserve candidates", reserveCreated);

        return reserveCreated;
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

        if (candles.Count < 100)
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
            OosPfRelaxation = config.OosPfRelaxation,
            OosDdRelaxation = config.OosDdRelaxation,
            OosSharpeRelaxation = config.OosSharpeRelaxation,
            RegimeDegradationRelaxation = config.RegimeDegradationRelaxation,
            KellyFactor = config.KellyFactor,
            KellyMinLot = config.KellyMinLot,
            KellyMaxLot = config.KellyMaxLot,
            MinDeflatedSharpe = config.MinDeflatedSharpe,
            DeflatedSharpeTrials = Math.Max(10, config.ActiveStrategyCount),
        };
    }

    private bool IsCorrelationGroupSaturated(string symbol, Dictionary<int, int> groupCounts, int maxPerGroup)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        return groupIdx.HasValue && groupCounts.GetValueOrDefault(groupIdx.Value) >= maxPerGroup;
    }

    private void IncrementCorrelationGroupCount(string symbol, Dictionary<int, int> groupCounts)
    {
        int? groupIdx = FindCorrelationGroupIndex(symbol);
        if (groupIdx.HasValue)
            groupCounts[groupIdx.Value] = groupCounts.GetValueOrDefault(groupIdx.Value) + 1;
    }

    private int? FindCorrelationGroupIndex(string symbol)
    {
        var upper = symbol.ToUpperInvariant();
        for (int i = 0; i < _correlationGroups.Length; i++)
        {
            if (_correlationGroups[i].Any(s => s.Equals(upper, StringComparison.OrdinalIgnoreCase)))
                return i;
        }

        return null;
    }

    private static AdaptiveThresholdAdjustments ResolveAdaptiveAdjustments(
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adjustmentsByContext,
        MarketRegimeEnum regime,
        Timeframe timeframe)
        => adjustmentsByContext.TryGetValue((regime, timeframe), out var exact)
            ? exact
            : AdaptiveThresholdAdjustments.Neutral;

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
        return new ScreeningThresholds(scaledWR, scaledPF, scaledSh, scaledDD, adjustedMinTrades);
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

    private static ScreeningMetrics BuildBaseMetrics(ScreeningOutcome candidate)
        => ScreeningMetrics.FromJson(candidate.Strategy.ScreeningMetricsJson)
           ?? new ScreeningMetrics
           {
               Regime = candidate.Regime.ToString(),
               ObservedRegime = candidate.ObservedRegime.ToString(),
               GenerationSource = candidate.GenerationSource,
               ScreenedAtUtc = candidate.Strategy.CreatedAt,
           };

    private static ScreeningOutcome ApplyCandidateSelectionMetadata(
        ScreeningOutcome candidate,
        CandidateSelectionResult selection,
        string cycleId)
    {
        candidate.Strategy.GenerationCycleId = cycleId;
        candidate.Strategy.GenerationCandidateId = selection.Identity.CandidateId;

        var metrics = (candidate.Metrics ?? BuildBaseMetrics(candidate)) with
        {
            CycleId = cycleId,
            CandidateId = selection.Identity.CandidateId,
            SelectionScore = selection.Score.TotalScore,
            SelectionScoreBreakdown = selection.Score,
        };

        candidate.Strategy.ScreeningMetricsJson = metrics.ToJson();
        return candidate with { Metrics = metrics };
    }
}
