using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

public static partial class StrategyGenerationHelpers
{
    /// <summary>
    /// Converts an observed historical median into a bounded threshold multiplier.
    /// </summary>
    public static double ComputeAdaptiveMultiplier(double observedMedian, double baseThreshold)
    {
        if (baseThreshold <= 0)
            return 1.0;

        double ratio = observedMedian / baseThreshold;
        return Math.Clamp(ratio, 0.85, 1.25);
    }

    /// <summary>
    /// Applies a precomputed adaptive multiplier to a base screening threshold.
    /// </summary>
    public static double ApplyAdaptiveAdjustment(double threshold, double multiplier)
        => threshold * multiplier;

    /// <summary>
    /// Computes the median of a value set, returning <c>0</c> for an empty collection.
    /// </summary>
    public static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    /// <summary>
    /// Computes a recency-weighted survival rate using exponential decay.
    /// </summary>
    public static double ComputeRecencyWeightedSurvivalRate(
        IEnumerable<(bool Survived, DateTime CreatedAt)> strategies,
        double halfLifeDays,
        DateTime utcNow)
    {
        double weightedSurvived = 0;
        double totalWeight = 0;
        double denominator = halfLifeDays / Math.Log(2);

        foreach (var (survived, createdAt) in strategies)
        {
            double daysAgo = (utcNow - createdAt).TotalDays;
            double weight = Math.Exp(-daysAgo / denominator);
            totalWeight += weight;
            if (survived)
                weightedSurvived += weight;
        }

        return totalWeight > 0 ? weightedSurvived / totalWeight : 0;
    }

    /// <summary>
    /// Returns the strategy types preferred when a symbol is in a recent regime transition.
    /// </summary>
    public static IReadOnlyList<StrategyType> GetTransitionTypes()
        => [StrategyType.BreakoutScalper, StrategyType.MomentumTrend, StrategyType.SessionBreakout];

    /// <summary>
    /// Rewards regime agreement across adjacent timeframes and penalizes disagreement.
    /// </summary>
    public static double ComputeMultiTimeframeConfidenceBoost(
        MarketRegimeEnum primaryRegime,
        string symbol,
        Timeframe primaryTf,
        IReadOnlyDictionary<(string, Timeframe), MarketRegimeEnum> regimeBySymbolTf)
    {
        var higherTf = GetHigherTimeframe(primaryTf);
        if (higherTf is null)
            return 1.0;

        if (regimeBySymbolTf.TryGetValue((symbol, higherTf.Value), out var higherRegime))
            return higherRegime == primaryRegime ? 1.15 : 0.90;

        return 1.0;
    }

    /// <summary>
    /// Converts regime age into a modest confidence multiplier for candidate generation.
    /// </summary>
    public static double ComputeRegimeDurationFactor(DateTime regimeDetectedAt, DateTime utcNow)
    {
        var elapsed = utcNow - regimeDetectedAt;
        if (elapsed.TotalDays < 2)
            return 0.8;
        if (elapsed.TotalDays <= 14)
            return 1.0;
        return 1.1;
    }
}
