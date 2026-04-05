using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Returns parameter templates per strategy type: static defaults (ordered by conservatism)
/// merged with dynamic templates learned from promoted strategies. Dynamic templates are
/// refreshed once per generation cycle via <see cref="RefreshDynamicTemplates"/> with
/// optimized parameters from strategies that reached <c>BacktestQualified</c> or higher.
///
/// The merge deduplicates by exact JSON match and caps dynamic templates at
/// <see cref="MaxDynamicTemplatesPerType"/> to prevent bloat.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class StrategyParameterTemplateProvider : IStrategyParameterTemplateProvider
{
    private const int MaxDynamicTemplatesPerType = 3;

    private static readonly Dictionary<StrategyType, IReadOnlyList<string>> StaticTemplates = new()
    {
        [StrategyType.MovingAverageCrossover] = new[]
        {
            """{"FastPeriod":9,"SlowPeriod":21}""",
            """{"FastPeriod":20,"SlowPeriod":50}""",
            """{"FastPeriod":50,"SlowPeriod":200}""",
        },
        [StrategyType.RSIReversion] = new[]
        {
            """{"Period":14,"Oversold":30,"Overbought":70}""",
            """{"Period":7,"Oversold":25,"Overbought":75}""",
            """{"Period":21,"Oversold":35,"Overbought":65}""",
        },
        [StrategyType.BreakoutScalper] = new[]
        {
            """{"LookbackBars":20,"ConfirmationBars":1}""",
            """{"LookbackBars":50,"ConfirmationBars":2}""",
        },
        [StrategyType.BollingerBandReversion] = new[]
        {
            """{"Period":20,"StdDevMultiplier":2.0}""",
            """{"Period":14,"StdDevMultiplier":2.5}""",
        },
        [StrategyType.MACDDivergence] = new[]
        {
            """{"FastPeriod":12,"SlowPeriod":26,"SignalPeriod":9}""",
            """{"FastPeriod":8,"SlowPeriod":17,"SignalPeriod":9}""",
        },
        [StrategyType.SessionBreakout] = new[]
        {
            """{"SessionStartHour":8,"SessionEndHour":12,"BreakoutBufferPips":5}""",
            """{"SessionStartHour":13,"SessionEndHour":17,"BreakoutBufferPips":3}""",
        },
        [StrategyType.MomentumTrend] = new[]
        {
            """{"MomentumPeriod":14,"TrendMaPeriod":50}""",
            """{"MomentumPeriod":10,"TrendMaPeriod":30}""",
        },
        [StrategyType.StatisticalArbitrage] = new[]
        {
            """{"CorrelatedSymbol":"GBPUSD","LookbackPeriod":60,"ZScoreEntry":2.0,"ZScoreExit":0.5,"StopLossAtrMultiplier":2.0,"TakeProfitAtrMultiplier":3.0,"AtrPeriod":14}""",
            """{"CorrelatedSymbol":"GBPUSD","LookbackPeriod":90,"ZScoreEntry":2.5,"ZScoreExit":0.3,"StopLossAtrMultiplier":2.5,"TakeProfitAtrMultiplier":3.5,"AtrPeriod":14}""",
        },
        [StrategyType.VwapReversion] = new[]
        {
            """{"SessionStartHour":8,"SessionEndHour":16,"EntryAtrThreshold":1.5,"StopLossAtrMultiplier":2.0,"TakeProfitAtrMultiplier":1.0,"AtrPeriod":14,"MaxAdx":40,"MinVolumeRatio":1.2}""",
            """{"SessionStartHour":13,"SessionEndHour":21,"EntryAtrThreshold":2.0,"StopLossAtrMultiplier":2.5,"TakeProfitAtrMultiplier":1.5,"AtrPeriod":14,"MaxAdx":35,"MinVolumeRatio":1.0}""",
        },
        [StrategyType.CompositeML] = new[]
        {
            """{"ConfidenceThreshold":0.60,"ModelPreference":"Ensemble","StopLossAtrMultiplier":2.0,"TakeProfitAtrMultiplier":3.0,"AtrPeriod":14}""",
            """{"ConfidenceThreshold":0.70,"ModelPreference":"Ensemble","StopLossAtrMultiplier":1.5,"TakeProfitAtrMultiplier":2.5,"AtrPeriod":14}""",
            """{"ConfidenceThreshold":0.80,"ModelPreference":"Ensemble","StopLossAtrMultiplier":2.5,"TakeProfitAtrMultiplier":4.0,"AtrPeriod":14}""",
        },
    };

    /// <summary>Dynamic templates from promoted strategies, keyed by strategy type. Thread-safe via volatile swap.</summary>
    private volatile IReadOnlyDictionary<StrategyType, IReadOnlyList<string>> _dynamicTemplates =
        new Dictionary<StrategyType, IReadOnlyList<string>>();

    public IReadOnlyList<string> GetTemplates(StrategyType strategyType)
    {
        var statics = StaticTemplates.TryGetValue(strategyType, out var s) ? s : Array.Empty<string>();

        if (!_dynamicTemplates.TryGetValue(strategyType, out var dynamics) || dynamics.Count == 0)
            return statics;

        // Merge: statics first (ordered by conservatism), then dynamics (deduped)
        var staticSet = new HashSet<string>(statics, StringComparer.Ordinal);
        var merged = new List<string>(statics);
        foreach (var d in dynamics)
        {
            if (staticSet.Add(d))
                merged.Add(d);
        }
        return merged;
    }

    public void RefreshDynamicTemplates(IReadOnlyDictionary<StrategyType, IReadOnlyList<string>> promotedParams)
    {
        var capped = new Dictionary<StrategyType, IReadOnlyList<string>>();
        foreach (var (type, paramsList) in promotedParams)
        {
            if (paramsList.Count == 0) continue;
            // Take the most recent promoted params, capped to prevent template bloat
            capped[type] = paramsList.Count <= MaxDynamicTemplatesPerType
                ? paramsList
                : paramsList.Take(MaxDynamicTemplatesPerType).ToList();
        }
        // Atomic swap — readers see either old or new, never a partial state
        _dynamicTemplates = capped;
    }
}
