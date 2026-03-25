using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Evaluates whether to execute immediately or delay for a better fill within the
/// current bar. Returns a delay duration (zero = execute now).
/// </summary>
public static class EntryTimingEvaluator
{
    private const double MaxDelayMs = 3000;
    private const double SpreadThresholdPips = 2.0;
    private const double MagnitudeThresholdPips = 5.0;

    public static TimeSpan Evaluate(
        TradeSignal signal,
        ILivePriceCache? livePriceCache,
        ILogger logger)
    {
        if (livePriceCache is null)
            return TimeSpan.Zero;

        decimal? bid = null, ask = null;
        try
        {
            var livePrice = livePriceCache.Get(signal.Symbol);
            if (livePrice is not null)
            {
                bid = livePrice.Value.Bid;
                ask = livePrice.Value.Ask;
            }
        }
        catch
        {
            return TimeSpan.Zero;
        }

        if (bid is null || ask is null || bid == 0)
            return TimeSpan.Zero;

        double spread = (double)(ask.Value - bid.Value);
        bool is5Digit = signal.Symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase);
        double pipMultiplier = is5Digit ? 100.0 : 10000.0;
        double spreadPips = spread * pipMultiplier;

        double magnitudePips = (double)(signal.MLPredictedMagnitude ?? 0);
        double confidence = (double)signal.Confidence;

        double delayMs = 0;

        if (spreadPips > SpreadThresholdPips)
        {
            delayMs += Math.Min(2000, spreadPips / SpreadThresholdPips * 500);
            logger.LogDebug(
                "Entry timing: spread={Spread:F1} pips > threshold — adding {Delay}ms delay",
                spreadPips, delayMs);
        }

        if (magnitudePips > 0 && magnitudePips < MagnitudeThresholdPips && confidence < 0.8)
        {
            double magDelay = (1.0 - magnitudePips / MagnitudeThresholdPips) * 1000;
            delayMs += magDelay;
            logger.LogDebug(
                "Entry timing: magnitude={Mag:F1} pips < threshold — adding {Delay}ms delay",
                magnitudePips, magDelay);
        }

        delayMs = Math.Min(delayMs, MaxDelayMs);

        return delayMs > 50 ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero;
    }
}
