using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationSpreadPolicy))]
internal sealed class StrategyGenerationSpreadPolicy : IStrategyGenerationSpreadPolicy
{
    public bool PassesSpreadFilter(
        decimal atr,
        BacktestOptions options,
        IReadOnlyList<Candle> candles,
        StrategyGenerationHelpers.AssetClass assetClass,
        double maxRatio)
    {
        decimal representativeSpread = ResolveRepresentativeSpread(options, candles);
        if (atr <= 0 || representativeSpread <= 0)
            return true;

        double spreadToRange = (double)(representativeSpread / atr);
        return spreadToRange <= StrategyGenerationHelpers.GetSpreadToRangeLimit(assetClass, maxRatio);
    }

    public decimal ResolveRepresentativeSpread(BacktestOptions options, IReadOnlyList<Candle> candles)
    {
        if (options.SpreadFunction == null || candles.Count == 0)
            return options.SpreadPriceUnits;

        var sample = candles
            .Skip(Math.Max(0, candles.Count - 24))
            .Select(candle =>
            {
                try
                {
                    return Math.Max(0m, options.SpreadFunction(candle.Timestamp));
                }
                catch
                {
                    return options.SpreadPriceUnits;
                }
            })
            .Where(spread => spread > 0)
            .OrderBy(spread => spread)
            .ToList();

        if (sample.Count == 0)
            return options.SpreadPriceUnits;

        int mid = sample.Count / 2;
        return sample.Count % 2 == 0
            ? (sample[mid - 1] + sample[mid]) / 2m
            : sample[mid];
    }
}
