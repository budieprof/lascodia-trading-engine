using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Prepares CPC input windows from candles without allowing windows to cross timestamp gaps.
/// Regime-filtered data can contain separated episodes; each episode is windowed independently.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ICpcSequencePreparationService))]
public sealed class CpcSequencePreparationService : ICpcSequencePreparationService
{
    public IReadOnlyList<float[][]> BuildSequences(
        IReadOnlyList<Candle> candles,
        int sequenceLength,
        int sequenceStride,
        int maxSequences)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0 || maxSequences < 1)
            return Array.Empty<float[][]>();

        var expectedStep = TimeframeDuration(candles[0].Timeframe);
        var sequences = new List<float[][]>(Math.Min(maxSequences, candles.Count));

        foreach (var run in SplitContiguousCandleRuns(candles, expectedStep))
        {
            if (sequences.Count >= maxSequences)
                break;

            var remaining = maxSequences - sequences.Count;
            sequences.AddRange(MLCpcSequenceBuilder.Build(
                run,
                sequenceLength,
                sequenceStride,
                remaining));
        }

        return sequences;
    }

    private static IEnumerable<IReadOnlyList<Candle>> SplitContiguousCandleRuns(
        IReadOnlyList<Candle> candles,
        TimeSpan expectedStep)
    {
        if (candles.Count == 0)
            yield break;

        var current = new List<Candle> { candles[0] };
        var tolerance = TimeSpan.FromSeconds(1);
        for (int i = 1; i < candles.Count; i++)
        {
            var gap = candles[i].Timestamp - candles[i - 1].Timestamp;
            if (gap > TimeSpan.Zero && gap <= expectedStep + tolerance)
            {
                current.Add(candles[i]);
                continue;
            }

            yield return current;
            current = [candles[i]];
        }

        yield return current;
    }

    private static TimeSpan TimeframeDuration(Timeframe timeframe)
        => timeframe switch
        {
            Timeframe.M1  => TimeSpan.FromMinutes(1),
            Timeframe.M5  => TimeSpan.FromMinutes(5),
            Timeframe.M15 => TimeSpan.FromMinutes(15),
            Timeframe.H1  => TimeSpan.FromHours(1),
            Timeframe.H4  => TimeSpan.FromHours(4),
            Timeframe.D1  => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1),
        };
}
