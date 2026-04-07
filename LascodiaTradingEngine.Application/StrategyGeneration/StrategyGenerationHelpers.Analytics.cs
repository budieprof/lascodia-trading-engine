using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

public static partial class StrategyGenerationHelpers
{
    public static double ComputeAdaptiveMultiplier(double observedMedian, double baseThreshold)
    {
        if (baseThreshold <= 0)
            return 1.0;

        double ratio = observedMedian / baseThreshold;
        return Math.Clamp(ratio, 0.85, 1.25);
    }

    public static double ApplyAdaptiveAdjustment(double threshold, double multiplier)
        => threshold * multiplier;

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

    public static IReadOnlyList<StrategyType> GetTransitionTypes()
        => [StrategyType.BreakoutScalper, StrategyType.MomentumTrend, StrategyType.SessionBreakout];

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
