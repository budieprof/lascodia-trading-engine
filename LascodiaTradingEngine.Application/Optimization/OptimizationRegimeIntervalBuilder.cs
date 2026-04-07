using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationRegimeIntervalBuilder
{
    internal static List<(DateTime StartUtc, DateTime EndUtc)> BuildRegimeIntervals(
        IReadOnlyList<MarketRegimeSnapshot> snapshots,
        MarketRegimeEnum regime,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        var intervals = new List<(DateTime StartUtc, DateTime EndUtc)>();
        if (snapshots.Count == 0 || rangeStartUtc >= rangeEndUtc)
            return intervals;

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Regime != regime)
                continue;

            var startUtc = snapshot.DetectedAt > rangeStartUtc ? snapshot.DetectedAt : rangeStartUtc;
            var endUtc = i + 1 < snapshots.Count
                ? snapshots[i + 1].DetectedAt
                : rangeEndUtc;
            if (endUtc > rangeEndUtc)
                endUtc = rangeEndUtc;

            if (startUtc < endUtc)
                intervals.Add((startUtc, endUtc));
        }

        return intervals;
    }

    internal static List<Candle> FilterCandlesByIntervals(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> intervals)
    {
        if (candles.Count == 0 || intervals.Count == 0)
            return [];

        var filtered = new List<Candle>();
        int intervalIndex = 0;
        foreach (var candle in candles)
        {
            while (intervalIndex < intervals.Count && candle.Timestamp >= intervals[intervalIndex].EndUtc)
                intervalIndex++;

            if (intervalIndex >= intervals.Count)
                break;

            var (startUtc, endUtc) = intervals[intervalIndex];
            if (candle.Timestamp >= startUtc && candle.Timestamp < endUtc)
                filtered.Add(candle);
        }

        return filtered;
    }
}
