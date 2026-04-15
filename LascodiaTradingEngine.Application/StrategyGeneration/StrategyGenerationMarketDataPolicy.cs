using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationMarketDataPolicy))]
/// <summary>
/// Thin policy wrapper around strategy-generation market-data helper logic.
/// </summary>
internal sealed class StrategyGenerationMarketDataPolicy : IStrategyGenerationMarketDataPolicy
{
    public double ComputeEffectiveCandleAgeHours(DateTime lastCandleTimestampUtc, string? tradingHoursJson, DateTime utcNow)
        => StrategyGenerationHelpers.ComputeEffectiveCandleAgeHours(lastCandleTimestampUtc, tradingHoursJson, utcNow);

    public BacktestOptions BuildScreeningOptions(
        string symbol,
        CurrencyPair? pairInfo,
        StrategyGenerationHelpers.AssetClass assetClass,
        double screeningSpreadPoints,
        double screeningCommissionPerLot,
        double screeningSlippagePips,
        ILivePriceCache livePriceCache,
        DateTime utcNow)
        => StrategyGenerationHelpers.BuildScreeningOptions(
            symbol,
            pairInfo,
            assetClass,
            screeningSpreadPoints,
            screeningCommissionPerLot,
            screeningSlippagePips,
            livePriceCache,
            utcNow);

    public double ComputeRegimeDurationFactor(DateTime regimeDetectedAt, DateTime utcNow)
        => StrategyGenerationHelpers.ComputeRegimeDurationFactor(regimeDetectedAt, utcNow);

    public double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies,
        double halfLifeDays,
        DateTime utcNow)
        => StrategyGenerationHelpers.ComputeRecencyWeightedSurvivalRate(strategies, halfLifeDays, utcNow);
}
