using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

public static partial class StrategyGenerationHelpers
{
    // Backward-compatible overloads preserved for older callers and tests that still rely on
    // DateTime.UtcNow-based helper signatures.

    public static double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies)
        => ComputeRecencyWeightedSurvivalRate(strategies, 62.0, DateTime.UtcNow);

    public static double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies,
        double halfLifeDays)
        => ComputeRecencyWeightedSurvivalRate(strategies, halfLifeDays, DateTime.UtcNow);

    public static bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols)
        => IsWeekendForAssetMix(symbols, DateTime.UtcNow);

    public static bool IsInBlackoutPeriod(string blackoutPeriods)
        => IsInBlackoutPeriod(blackoutPeriods, "UTC", DateTime.UtcNow);

    public static bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone)
        => IsInBlackoutPeriod(blackoutPeriods, blackoutTimezone, DateTime.UtcNow);

    public static LascodiaTradingEngine.Application.Backtesting.Services.BacktestOptions BuildScreeningOptions(
        string symbol,
        CurrencyPair? pairInfo,
        AssetClass assetClass,
        double screeningSpreadPoints,
        double screeningCommissionPerLot,
        double screeningSlippagePips,
        ILivePriceCache livePriceCache)
        => BuildScreeningOptions(
            symbol,
            pairInfo,
            assetClass,
            screeningSpreadPoints,
            screeningCommissionPerLot,
            screeningSlippagePips,
            livePriceCache,
            DateTime.UtcNow);

    public static double ComputeRegimeDurationFactor(DateTime regimeDetectedAt)
        => ComputeRegimeDurationFactor(regimeDetectedAt, DateTime.UtcNow);

    public static string NormalizeTemplateParameters(string parametersJson)
        => string.IsNullOrWhiteSpace(parametersJson)
            ? string.Empty
            : global::LascodiaTradingEngine.Application.Optimization.CanonicalParameterJson.Normalize(parametersJson);

    /// <summary>
    /// Builds a stable key used to cache feedback for a normalized parameter template.
    /// </summary>
    public static string BuildTemplateFeedbackKey(
        StrategyType strategyType,
        Timeframe timeframe,
        string normalizedParametersJson)
        => $"{strategyType}|{timeframe}|{normalizedParametersJson}";

    /// <summary>
    /// Orders candidate templates using feedback data when available, then falls back to the
    /// regime-aware default template ordering.
    /// </summary>
    public static IReadOnlyList<string> OrderTemplatesForRegime(
        IReadOnlyList<string> templates,
        MarketRegimeEnum regime,
        IReadOnlyDictionary<string, double>? templateSurvivalRates,
        StrategyType strategyType,
        Timeframe timeframe)
    {
        if (templates.Count <= 1 || templateSurvivalRates is not { Count: > 0 })
            return OrderTemplatesForRegime(templates, regime, templateSurvivalRates);

        var withData = new List<(string Template, double SurvivalRate)>();
        var withoutData = new List<string>();
        foreach (var template in templates)
        {
            string key = BuildTemplateFeedbackKey(strategyType, timeframe, NormalizeTemplateParameters(template));
            if (templateSurvivalRates.TryGetValue(key, out var rate))
                withData.Add((template, rate));
            else
                withoutData.Add(template);
        }

        var ordered = withData
            .OrderByDescending(x => x.SurvivalRate)
            .Select(x => x.Template)
            .ToList();
        ordered.AddRange(OrderTemplatesForRegime(withoutData, regime, templateSurvivalRates: null));
        return ordered;
    }

    /// <summary>
    /// Maps a currently observed regime to the reserve regime that should diversify coverage
    /// for a given strategy type.
    /// </summary>
    public static MarketRegimeEnum GetReserveTargetRegime(MarketRegimeEnum currentRegime, StrategyType strategyType)
        => strategyType switch
        {
            StrategyType.MovingAverageCrossover or StrategyType.MACDDivergence or StrategyType.MomentumTrend
                => MarketRegimeEnum.Trending,
            StrategyType.BreakoutScalper or StrategyType.SessionBreakout
                => currentRegime is MarketRegimeEnum.HighVolatility ? MarketRegimeEnum.HighVolatility : MarketRegimeEnum.Breakout,
            StrategyType.RSIReversion or StrategyType.BollingerBandReversion or StrategyType.StatisticalArbitrage or StrategyType.VwapReversion or StrategyType.CalendarEffect
                => currentRegime is MarketRegimeEnum.HighVolatility ? MarketRegimeEnum.LowVolatility : MarketRegimeEnum.Ranging,
            StrategyType.NewsFade
                => MarketRegimeEnum.HighVolatility,
            StrategyType.CarryTrade
                => currentRegime is MarketRegimeEnum.HighVolatility ? MarketRegimeEnum.LowVolatility : MarketRegimeEnum.Trending,
            _ => currentRegime,
        };
}
