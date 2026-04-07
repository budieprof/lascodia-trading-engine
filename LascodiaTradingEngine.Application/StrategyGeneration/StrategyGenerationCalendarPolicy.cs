using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCalendarPolicy))]
internal sealed class StrategyGenerationCalendarPolicy : IStrategyGenerationCalendarPolicy
{
    public bool IsWeekendForAssetMix(IEnumerable<(string Symbol, CurrencyPair? Pair)> symbols, DateTime utcNow)
        => StrategyGenerationHelpers.IsWeekendForAssetMix(symbols, utcNow);

    public bool IsInBlackoutPeriod(string blackoutPeriods, string blackoutTimezone, DateTime utcNow)
        => StrategyGenerationHelpers.IsInBlackoutPeriod(blackoutPeriods, blackoutTimezone, utcNow);
}
