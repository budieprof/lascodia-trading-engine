using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Maps market regimes to strategy types. The static baseline reflects empirical effectiveness:
/// <list type="bullet">
///   <item><b>Trending</b> → trend-following (MA Crossover, MACD, Momentum, Breakout)</item>
///   <item><b>Ranging</b> → mean-reversion (RSI, Bollinger Band)</item>
///   <item><b>HighVolatility / Breakout</b> → momentum + breakout (Breakout Scalper, Momentum Trend)</item>
///   <item><b>LowVolatility</b> → range + session (RSI, Bollinger, Session Breakout)</item>
///   <item><b>Crisis</b> → empty (no generation — too dangerous)</item>
/// </list>
///
/// <see cref="RefreshFromFeedback"/> augments the static mapping with data-driven promotions:
/// strategy types that demonstrate high survival rates in regimes they aren't statically mapped
/// to are added to those regimes' candidate pools. Static types are never removed — feedback
/// can only expand the mapping, not contract it.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class RegimeStrategyMapper : IRegimeStrategyMapper
{
    private static readonly IReadOnlyDictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>> StaticMap =
        new Dictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>>
        {
            [MarketRegimeEnum.Trending] = new[]
            {
                StrategyType.MovingAverageCrossover,
                StrategyType.MACDDivergence,
                StrategyType.MomentumTrend,
                StrategyType.BreakoutScalper,
                StrategyType.CompositeML,
            },
            [MarketRegimeEnum.Ranging] = new[]
            {
                StrategyType.RSIReversion,
                StrategyType.BollingerBandReversion,
                StrategyType.StatisticalArbitrage,
                StrategyType.VwapReversion,
                StrategyType.CompositeML,
            },
            [MarketRegimeEnum.HighVolatility] = new[]
            {
                StrategyType.BreakoutScalper,
                StrategyType.MomentumTrend,
                StrategyType.CompositeML,
            },
            [MarketRegimeEnum.LowVolatility] = new[]
            {
                StrategyType.RSIReversion,
                StrategyType.BollingerBandReversion,
                StrategyType.StatisticalArbitrage,
                StrategyType.VwapReversion,
                StrategyType.SessionBreakout,
                StrategyType.CompositeML,
            },
            [MarketRegimeEnum.Breakout] = new[]
            {
                StrategyType.BreakoutScalper,
                StrategyType.MomentumTrend,
                StrategyType.CompositeML,
            },
            [MarketRegimeEnum.Crisis] = Array.Empty<StrategyType>(),
        };

    /// <summary>Merged mapping: static baseline + feedback-promoted types. Thread-safe via volatile swap.</summary>
    private volatile IReadOnlyDictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>> _effectiveMap = StaticMap;

    public IReadOnlyList<StrategyType> GetStrategyTypes(MarketRegimeEnum regime)
        => _effectiveMap.TryGetValue(regime, out var types) ? types : Array.Empty<StrategyType>();

    public void RefreshFromFeedback(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum), double> feedbackRates,
        double promotionThreshold = 0.65)
    {
        if (feedbackRates.Count == 0)
        {
            _effectiveMap = StaticMap;
            return;
        }

        // Build merged map: start with static types, then append feedback-promoted types
        var merged = new Dictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>>();
        foreach (var (regime, staticTypes) in StaticMap)
        {
            var staticSet = new HashSet<StrategyType>(staticTypes);
            var promoted = new List<StrategyType>();

            foreach (var ((strategyType, feedbackRegime), survivalRate) in feedbackRates)
            {
                if (feedbackRegime != regime) continue;
                if (staticSet.Contains(strategyType)) continue;
                if (survivalRate < promotionThreshold) continue;
                // Never promote into Crisis
                if (regime == MarketRegimeEnum.Crisis) continue;

                promoted.Add(strategyType);
            }

            if (promoted.Count == 0)
            {
                merged[regime] = staticTypes;
            }
            else
            {
                var combined = new List<StrategyType>(staticTypes);
                combined.AddRange(promoted);
                merged[regime] = combined;
            }
        }

        _effectiveMap = merged;
    }
}
