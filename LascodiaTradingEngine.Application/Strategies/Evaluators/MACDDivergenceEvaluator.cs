using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Detects MACD histogram divergence from price to identify trend exhaustion
/// and reversal entries. Works well in trending and high-volatility regimes.
///
/// Bullish divergence: price makes a lower low but MACD histogram makes a higher low
///   → trend exhaustion, likely reversal up → Buy signal.
/// Bearish divergence: price makes a higher high but MACD histogram makes a lower high
///   → trend exhaustion, likely reversal down → Sell signal.
///
/// Also generates trend-continuation signals on MACD zero-line crossovers
/// when no divergence is present, confirmed by histogram direction.
/// </summary>
public class MACDDivergenceEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public MACDDivergenceEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.MACDDivergence;

    public int MinRequiredCandles(Strategy strategy)
    {
        int slowPeriod = 26, divergenceLookback = 10;
        ParseParameters(strategy.ParametersJson, out _, out slowPeriod, out _, out divergenceLookback);
        // EMA needs ~2× slow period to stabilise, plus divergence lookback, plus ATR
        int emaWarmup = slowPeriod * 2;
        return Math.Max(emaWarmup + divergenceLookback, _options.AtrPeriodForSlTp) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int fastPeriod = 12, slowPeriod = 26, signalPeriod = 9, divergenceLookback = 10;
        ParseParameters(strategy.ParametersJson, out fastPeriod, out slowPeriod, out signalPeriod, out divergenceLookback);

        int emaWarmup = slowPeriod * 2;
        int required  = Math.Max(emaWarmup + divergenceLookback, _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < required)
            return Task.FromResult<TradeSignal?>(null);

        // Compute MACD line, signal line, and histogram for all bars
        var closes    = ExtractCloses(candles);
        var fastEma   = ComputeEma(closes, fastPeriod);
        var slowEma   = ComputeEma(closes, slowPeriod);
        var macdLine  = new decimal[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            macdLine[i] = fastEma[i] - slowEma[i];

        var signalLine = ComputeEma(macdLine, signalPeriod);
        var histogram  = new decimal[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            histogram[i] = macdLine[i] - signalLine[i];

        int last = candles.Count - 1;

        TradeDirection? direction = null;
        decimal entryPrice;
        decimal confidence;

        // Check for divergence over the lookback window
        var divResult = DetectDivergence(candles, histogram, last, divergenceLookback);
        if (divResult.HasValue)
        {
            direction  = divResult.Value.Direction;
            entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;
            confidence = Math.Clamp(_options.MacdDivergenceConfidence + 0.05m, 0m, 1m); // Divergence = higher confidence
        }
        else
        {
            // Fallback: MACD zero-line crossover (trend continuation)
            bool bullishCross = macdLine[last - 1] <= 0 && macdLine[last] > 0 && histogram[last] > histogram[last - 1];
            bool bearishCross = macdLine[last - 1] >= 0 && macdLine[last] < 0 && histogram[last] < histogram[last - 1];

            if (bullishCross)
            {
                direction  = TradeDirection.Buy;
                entryPrice = currentPrice.Ask;
                confidence = _options.MacdDivergenceConfidence;
            }
            else if (bearishCross)
            {
                direction  = TradeDirection.Sell;
                entryPrice = currentPrice.Bid;
                confidence = _options.MacdDivergenceConfidence;
            }
            else
            {
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ATR-based SL/TP
        decimal atr           = CalculateAtr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

        decimal? stopLoss, takeProfit;
        if (direction == TradeDirection.Buy)
        {
            stopLoss   = entryPrice - stopDistance;
            takeProfit = entryPrice + profitDistance;
        }
        else
        {
            stopLoss   = entryPrice + stopDistance;
            takeProfit = entryPrice - profitDistance;
        }

        var now = DateTime.UtcNow;
        return Task.FromResult<TradeSignal?>(new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = _options.DefaultLotSize,
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.MacdDivergenceExpiryMinutes)
        });
    }

    // ── Divergence detection ────────────────────────────────────────────────

    private static (TradeDirection Direction, int SwingIndex)? DetectDivergence(
        IReadOnlyList<Candle> candles, decimal[] histogram, int currentIndex, int lookback)
    {
        int start = currentIndex - lookback;
        if (start < 1) return null;

        decimal currentLow  = candles[currentIndex].Low;
        decimal currentHigh = candles[currentIndex].High;
        decimal currentHist = histogram[currentIndex];

        // Bullish divergence: price lower low, histogram higher low
        for (int i = start; i < currentIndex - 1; i++)
        {
            if (IsSwingLow(candles, i) && candles[i].Low > currentLow && histogram[i] < currentHist)
            {
                // Price made lower low, but histogram made higher low → bullish
                if (currentHist < 0) // histogram should be negative for a low
                    return (TradeDirection.Buy, i);
            }
        }

        // Bearish divergence: price higher high, histogram lower high
        for (int i = start; i < currentIndex - 1; i++)
        {
            if (IsSwingHigh(candles, i) && candles[i].High < currentHigh && histogram[i] > currentHist)
            {
                // Price made higher high, but histogram made lower high → bearish
                if (currentHist > 0) // histogram should be positive for a high
                    return (TradeDirection.Sell, i);
            }
        }

        return null;
    }

    private static bool IsSwingLow(IReadOnlyList<Candle> candles, int i)
        => i > 0 && i < candles.Count - 1
           && candles[i].Low < candles[i - 1].Low
           && candles[i].Low < candles[i + 1].Low;

    private static bool IsSwingHigh(IReadOnlyList<Candle> candles, int i)
        => i > 0 && i < candles.Count - 1
           && candles[i].High > candles[i - 1].High
           && candles[i].High > candles[i + 1].High;

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void ParseParameters(string? json, out int fast, out int slow, out int signal, out int divLookback)
    {
        fast = 12; slow = 26; signal = 9; divLookback = 10;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("FastPeriod",          out var fp) && fp.TryGetInt32(out var fv)) fast        = fv;
            if (root.TryGetProperty("SlowPeriod",          out var sp) && sp.TryGetInt32(out var sv)) slow        = sv;
            if (root.TryGetProperty("SignalPeriod",        out var sg) && sg.TryGetInt32(out var gv)) signal      = gv;
            if (root.TryGetProperty("DivergenceLookback",  out var dl) && dl.TryGetInt32(out var dv)) divLookback = dv;
        }
        catch { /* defaults */ }

        fast        = Math.Clamp(fast, 2, 200);
        slow        = Math.Clamp(slow, 2, 500);
        signal      = Math.Clamp(signal, 2, 100);
        divLookback = Math.Clamp(divLookback, 3, 50);
        if (fast >= slow) fast = Math.Max(1, slow - 1);
    }

    private static decimal[] ExtractCloses(IReadOnlyList<Candle> candles)
    {
        var c = new decimal[candles.Count];
        for (int i = 0; i < candles.Count; i++) c[i] = candles[i].Close;
        return c;
    }

    /// <summary>Standard EMA: multiplier = 2/(period+1).</summary>
    private static decimal[] ComputeEma(decimal[] values, int period)
    {
        var ema = new decimal[values.Length];
        if (values.Length == 0) return ema;

        // Seed with SMA of first `period` values
        decimal seed = 0;
        int seedEnd = Math.Min(period, values.Length);
        for (int i = 0; i < seedEnd; i++) seed += values[i];
        ema[seedEnd - 1] = seed / seedEnd;

        decimal multiplier = 2m / (period + 1);
        for (int i = seedEnd; i < values.Length; i++)
            ema[i] = (values[i] - ema[i - 1]) * multiplier + ema[i - 1];

        // Fill earlier values with SMA seed
        for (int i = 0; i < seedEnd - 1; i++)
            ema[i] = ema[seedEnd - 1];

        return ema;
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sumTr = 0m;
        for (int i = endIndex - period + 1; i <= endIndex; i++)
        {
            decimal prevClose = candles[i - 1].Close;
            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - prevClose),
                                  Math.Abs(candles[i].Low  - prevClose)));
            sumTr += tr;
        }
        return sumTr / period;
    }
}
