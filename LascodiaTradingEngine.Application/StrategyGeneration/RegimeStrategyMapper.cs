using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    // CompositeML is listed first in every regime so it dominates the per-combo slot
    // budget (MaxTemplatesPerCombo) after the regime-aware template ordering step.
    // Rule-based archetypes are kept as fallbacks because classical TA still captures
    // some micro-regime edges and serves as a baseline the ML model can be evaluated
    // against. When the TPE surrogate and dynamic-template-refresh eventually produce
    // enough observations to support an ML-first bandit, the rule-based types can be
    // pruned entirely. Crisis stays empty to prevent any trading during flagged
    // market stress.
    private static readonly IReadOnlyDictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>> StaticMap =
        new Dictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>>
        {
            [MarketRegimeEnum.Trending] = new[]
            {
                StrategyType.CompositeML,
                StrategyType.MovingAverageCrossover,
                StrategyType.MACDDivergence,
                StrategyType.MomentumTrend,
                StrategyType.BreakoutScalper,
                StrategyType.CarryTrade,
            },
            [MarketRegimeEnum.Ranging] = new[]
            {
                StrategyType.CompositeML,
                StrategyType.RSIReversion,
                StrategyType.BollingerBandReversion,
                StrategyType.StatisticalArbitrage,
                StrategyType.VwapReversion,
                StrategyType.CalendarEffect,
            },
            [MarketRegimeEnum.HighVolatility] = new[]
            {
                StrategyType.CompositeML,
                StrategyType.BreakoutScalper,
                StrategyType.MomentumTrend,
                StrategyType.NewsFade,
            },
            [MarketRegimeEnum.LowVolatility] = new[]
            {
                StrategyType.CompositeML,
                StrategyType.RSIReversion,
                StrategyType.BollingerBandReversion,
                StrategyType.StatisticalArbitrage,
                StrategyType.VwapReversion,
                StrategyType.SessionBreakout,
                StrategyType.CalendarEffect,
                StrategyType.CarryTrade,
            },
            [MarketRegimeEnum.Breakout] = new[]
            {
                StrategyType.CompositeML,
                StrategyType.BreakoutScalper,
                StrategyType.MomentumTrend,
                StrategyType.NewsFade,
            },
            [MarketRegimeEnum.Crisis] = Array.Empty<StrategyType>(),
        };

    /// <summary>Merged mapping: static baseline + feedback-promoted types. Thread-safe via volatile swap.</summary>
    private volatile IReadOnlyDictionary<MarketRegimeEnum, IReadOnlyList<StrategyType>> _effectiveMap = StaticMap;

    /// <summary>Optional logger for diagnostics.</summary>
    private readonly ILogger<RegimeStrategyMapper>? _logger;

    public RegimeStrategyMapper() { }

    public RegimeStrategyMapper(ILogger<RegimeStrategyMapper> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<StrategyType> GetStrategyTypes(MarketRegimeEnum regime)
    {
        var types = _effectiveMap.TryGetValue(regime, out var mapped) ? mapped : Array.Empty<StrategyType>();

        if (types.Count == 0)
            _logger?.LogWarning("No strategy types mapped for regime {Regime} — generation will skip", regime);

        return types;
    }

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
