using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Utilities;

/// <summary>
/// Shared technical indicator calculations used by strategy evaluators.
/// All methods are static and allocation-free where possible.
/// </summary>
public static class IndicatorCalculator
{
    // ═════════════════════════════════════════════════════════════════════════
    // True Range
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the True Range for the candle at <paramref name="index"/>:
    /// max(high - low, |high - prevClose|, |low - prevClose|).
    /// </summary>
    public static decimal TrueRange(IReadOnlyList<Candle> candles, int index)
    {
        decimal high      = candles[index].High;
        decimal low       = candles[index].Low;
        decimal prevClose = candles[index - 1].Close;
        return Math.Max(high - low,
               Math.Max(Math.Abs(high - prevClose),
                        Math.Abs(low  - prevClose)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ATR — Simple (SMA of True Range)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple ATR: arithmetic mean of True Range over <paramref name="period"/> bars.
    /// Used by most evaluators for basic volatility measurement.
    /// </summary>
    public static decimal Atr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        if (endIndex < 1) return 0m; // TrueRange requires index >= 1
        decimal sumTr = 0m;
        int start = Math.Max(1, endIndex - period + 1); // guard: TrueRange accesses candles[index-1]
        int count = endIndex - start + 1;
        for (int i = start; i <= endIndex; i++)
            sumTr += TrueRange(candles, i);
        return count > 0 ? sumTr / count : 0m;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ATR — Wilder smoothing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ATR with Wilder's smoothing. More responsive to recent volatility changes
    /// and consistent with ADX/RSI smoothing. Seeds from a simple average, then
    /// applies Wilder's recursive formula.
    /// </summary>
    public static decimal WilderAtr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        if (endIndex < 1) return 0m; // TrueRange requires index >= 1
        int seedStart = endIndex - period * 2 + 1;
        if (seedStart < 1) seedStart = 1;

        int seedEnd = seedStart + period - 1;
        if (seedEnd > endIndex) seedEnd = endIndex;

        int seedCount = seedEnd - seedStart + 1;
        if (seedCount <= 0) return 0m; // degenerate: no valid bars

        decimal atr = 0m;
        for (int i = seedStart; i <= seedEnd; i++)
            atr += TrueRange(candles, i);
        atr /= seedCount;

        for (int i = seedEnd + 1; i <= endIndex; i++)
        {
            decimal tr = TrueRange(candles, i);
            atr = (atr * (period - 1) + tr) / period;
        }

        return atr;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SMA (Simple Moving Average)
    // ═════════════════════════════════════════════════════════════════════════

    public static decimal Sma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sum = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // EMA (Exponential Moving Average)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Single-point EMA at <paramref name="endIndex"/>. Seeds from an SMA over
    /// the first <paramref name="period"/> bars, then applies EMA smoothing.
    /// Falls back to SMA if insufficient history for a proper seed.
    /// </summary>
    public static decimal Ema(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        int seedEnd = endIndex - period;
        if (seedEnd < period - 1)
            return Sma(candles, endIndex, Math.Min(period, endIndex + 1));

        int seedStart = seedEnd - period + 1;
        decimal ema = 0;
        for (int i = seedStart; i <= seedEnd; i++)
            ema += candles[i].Close;
        ema /= period;

        decimal multiplier = 2.0m / (period + 1);
        for (int i = seedEnd + 1; i <= endIndex; i++)
            ema = (candles[i].Close - ema) * multiplier + ema;

        return ema;
    }

    /// <summary>
    /// Returns (previous, current) EMA values in a single pass — avoids
    /// computing the full EMA series twice for crossover detection.
    /// </summary>
    public static (decimal Previous, decimal Current) EmaPair(
        IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        int seedEnd = endIndex - period;
        if (seedEnd < period - 1)
        {
            return (Sma(candles, endIndex - 1, Math.Min(period, endIndex)),
                    Sma(candles, endIndex, Math.Min(period, endIndex + 1)));
        }

        int seedStart = seedEnd - period + 1;
        decimal ema = 0;
        for (int i = seedStart; i <= seedEnd; i++)
            ema += candles[i].Close;
        ema /= period;

        decimal multiplier = 2.0m / (period + 1);
        decimal prev = ema;
        for (int i = seedEnd + 1; i <= endIndex; i++)
        {
            prev = ema;
            ema = (candles[i].Close - ema) * multiplier + ema;
        }

        return (prev, ema);
    }

    /// <summary>
    /// Full EMA series over a decimal array. Used by MACD where the full
    /// series is needed for histogram/divergence analysis.
    /// </summary>
    public static decimal[] EmaSeries(decimal[] values, int period)
    {
        var ema = new decimal[values.Length];
        if (values.Length == 0) return ema;

        int seedEnd = Math.Min(period, values.Length);
        decimal seed = 0;
        for (int i = 0; i < seedEnd; i++) seed += values[i];
        ema[seedEnd - 1] = seed / seedEnd;

        decimal multiplier = 2m / (period + 1);
        for (int i = seedEnd; i < values.Length; i++)
            ema[i] = (values[i] - ema[i - 1]) * multiplier + ema[i - 1];

        for (int i = 0; i < seedEnd - 1; i++)
            ema[i] = ema[seedEnd - 1];

        return ema;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // VWMA (Volume-Weighted Moving Average)
    // ═════════════════════════════════════════════════════════════════════════

    public static decimal Vwma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sumPriceVol = 0;
        decimal sumVol = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
        {
            decimal vol = candles[i].Volume;
            sumPriceVol += candles[i].Close * vol;
            sumVol += vol;
        }
        return sumVol > 0 ? sumPriceVol / sumVol : Sma(candles, endIndex, period);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Standard Deviation
    // ═════════════════════════════════════════════════════════════════════════

    public static decimal StdDev(IReadOnlyList<Candle> candles, int endIndex, int period, decimal mean)
    {
        decimal sumSqDiff = 0;
        for (int i = endIndex - period + 1; i <= endIndex; i++)
        {
            decimal diff = candles[i].Close - mean;
            sumSqDiff += diff * diff;
        }
        return (decimal)Math.Sqrt((double)(sumSqDiff / period));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // RSI — Wilder smoothing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RSI with Wilder's smoothing. Seeds from a simple average, then applies
    /// the recursive gain/loss formula.
    /// </summary>
    public static decimal Rsi(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        if (endIndex < period) return 50m;

        int seedStart = endIndex - period * 2 + 1;
        if (seedStart < 1) seedStart = 1;
        int seedEnd = seedStart + period - 1;
        if (seedEnd > endIndex) seedEnd = endIndex;

        decimal avgGain = 0m, avgLoss = 0m;
        for (int i = seedStart; i <= seedEnd; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            if (change > 0) avgGain += change;
            else avgLoss -= change;
        }

        int seedCount = seedEnd - seedStart + 1;
        avgGain /= seedCount;
        avgLoss /= seedCount;

        for (int i = seedEnd + 1; i <= endIndex; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            decimal gain = change > 0 ? change : 0m;
            decimal loss = change < 0 ? -change : 0m;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
        }

        if (avgLoss == 0m) return 100m;
        decimal rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    /// <summary>
    /// Simple RSI without Wilder smoothing — single-window average gain/loss.
    /// Lighter weight for evaluators that don't need the full recursive formula.
    /// </summary>
    public static decimal SimpleRsi(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        if (endIndex < period || period < 1) return 50m; // insufficient data
        int startIndex = endIndex - period;

        decimal avgGain = 0m, avgLoss = 0m;
        for (int i = startIndex + 1; i <= startIndex + period; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            if (change > 0) avgGain += change;
            else avgLoss -= change;
        }
        avgGain /= period;
        avgLoss /= period;

        if (avgLoss == 0m) return 100m;
        decimal rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ADX (Average Directional Index) — Wilder smoothing
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full ADX with proper Wilder-smoothed DX series. Returns only the ADX value.
    /// </summary>
    public static decimal Adx(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        int startIdx = endIndex - (period * 2) + 1;
        if (startIdx < 1) startIdx = 1;

        decimal smoothedPlusDm  = 0m;
        decimal smoothedMinusDm = 0m;
        decimal smoothedTr      = 0m;

        int seedEnd = startIdx + period - 1;
        if (seedEnd > endIndex) seedEnd = endIndex;
        for (int i = startIdx; i <= seedEnd; i++)
        {
            var (plusDm, minusDm, tr) = DirectionalComponents(candles, i);
            smoothedPlusDm  += plusDm;
            smoothedMinusDm += minusDm;
            smoothedTr      += tr;
        }

        decimal firstDx = 0m;
        if (smoothedTr > 0)
        {
            decimal plusDi  = 100m * smoothedPlusDm / smoothedTr;
            decimal minusDi = 100m * smoothedMinusDm / smoothedTr;
            decimal diSum   = plusDi + minusDi;
            firstDx = diSum > 0 ? 100m * Math.Abs(plusDi - minusDi) / diSum : 0m;
        }

        var dxValues = new List<decimal>(period) { firstDx };

        for (int i = seedEnd + 1; i <= endIndex; i++)
        {
            var (plusDm, minusDm, tr) = DirectionalComponents(candles, i);

            smoothedPlusDm  = smoothedPlusDm  - (smoothedPlusDm  / period) + plusDm;
            smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / period) + minusDm;
            smoothedTr      = smoothedTr      - (smoothedTr      / period) + tr;

            decimal dx = 0m;
            if (smoothedTr > 0)
            {
                decimal plusDi  = 100m * smoothedPlusDm / smoothedTr;
                decimal minusDi = 100m * smoothedMinusDm / smoothedTr;
                decimal diSum   = plusDi + minusDi;
                if (diSum > 0)
                    dx = 100m * Math.Abs(plusDi - minusDi) / diSum;
            }

            dxValues.Add(dx);
        }

        if (dxValues.Count == 0) return 0m;

        int adxSeedCount = Math.Min(period, dxValues.Count);
        decimal adx = 0m;
        for (int i = 0; i < adxSeedCount; i++)
            adx += dxValues[i];
        adx /= adxSeedCount;

        for (int i = adxSeedCount; i < dxValues.Count; i++)
            adx = (adx * (period - 1) + dxValues[i]) / period;

        return adx;
    }

    /// <summary>
    /// ADX with +DI and -DI for evaluators that need directional indicators.
    /// </summary>
    public static (decimal Adx, decimal PlusDI, decimal MinusDI) AdxWithDI(
        IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        int start = endIndex - period * 2;
        if (start < 1) start = 1;

        decimal smoothedPlusDM  = 0;
        decimal smoothedMinusDM = 0;
        decimal smoothedTR      = 0;

        int initEnd = Math.Min(start + period, endIndex + 1);
        for (int i = start; i < initEnd; i++)
        {
            var (plusDm, minusDm, tr) = DirectionalComponents(candles, i);
            smoothedTR      += tr;
            smoothedPlusDM  += plusDm;
            smoothedMinusDM += minusDm;
        }

        for (int i = initEnd; i <= endIndex; i++)
        {
            var (plusDm, minusDm, tr) = DirectionalComponents(candles, i);
            smoothedTR      = smoothedTR      - smoothedTR      / period + tr;
            smoothedPlusDM  = smoothedPlusDM  - smoothedPlusDM  / period + plusDm;
            smoothedMinusDM = smoothedMinusDM - smoothedMinusDM / period + minusDm;
        }

        decimal plusDI  = smoothedTR > 0 ? (smoothedPlusDM  / smoothedTR) * 100 : 0;
        decimal minusDI = smoothedTR > 0 ? (smoothedMinusDM / smoothedTR) * 100 : 0;

        decimal diSum = plusDI + minusDI;
        decimal dx    = diSum > 0 ? Math.Abs(plusDI - minusDI) / diSum * 100 : 0;

        return (dx, plusDI, minusDI);
    }

    public static (decimal PlusDm, decimal MinusDm, decimal Tr) DirectionalComponents(
        IReadOnlyList<Candle> candles, int index)
    {
        decimal high      = candles[index].High;
        decimal low       = candles[index].Low;
        decimal prevHigh  = candles[index - 1].High;
        decimal prevLow   = candles[index - 1].Low;
        decimal prevClose = candles[index - 1].Close;

        decimal upMove   = high - prevHigh;
        decimal downMove = prevLow - low;

        decimal plusDm  = (upMove > downMove && upMove > 0) ? upMove : 0;
        decimal minusDm = (downMove > upMove && downMove > 0) ? downMove : 0;

        decimal tr = Math.Max(high - low,
                     Math.Max(Math.Abs(high - prevClose),
                              Math.Abs(low  - prevClose)));

        return (plusDm, minusDm, tr);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Swing detection — true pivot points
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds the nearest true swing low (pivot low) within the lookback window.
    /// A pivot low has its Low lower than <paramref name="pivotRadius"/> bars on each side.
    /// Falls back to the absolute minimum if no true pivot exists.
    /// </summary>
    public static decimal FindSwingLow(IReadOnlyList<Candle> candles, int endIndex, int lookbackBars, int pivotRadius = 3)
    {
        int start = Math.Max(pivotRadius, endIndex - lookbackBars);
        int end = endIndex - pivotRadius;

        for (int i = end; i >= start; i--)
        {
            decimal low = candles[i].Low;
            bool isPivot = true;
            for (int j = 1; j <= pivotRadius; j++)
            {
                if (candles[i - j].Low <= low || candles[i + j].Low <= low)
                {
                    isPivot = false;
                    break;
                }
            }
            if (isPivot)
                return low;
        }

        decimal lowest = decimal.MaxValue;
        for (int i = Math.Max(0, endIndex - lookbackBars); i <= endIndex; i++)
        {
            if (candles[i].Low < lowest)
                lowest = candles[i].Low;
        }
        return lowest;
    }

    /// <summary>
    /// Finds the nearest true swing high (pivot high) within the lookback window.
    /// A pivot high has its High higher than <paramref name="pivotRadius"/> bars on each side.
    /// Falls back to the absolute maximum if no true pivot exists.
    /// </summary>
    public static decimal FindSwingHigh(IReadOnlyList<Candle> candles, int endIndex, int lookbackBars, int pivotRadius = 3)
    {
        int start = Math.Max(pivotRadius, endIndex - lookbackBars);
        int end = endIndex - pivotRadius;

        for (int i = end; i >= start; i--)
        {
            decimal high = candles[i].High;
            bool isPivot = true;
            for (int j = 1; j <= pivotRadius; j++)
            {
                if (candles[i - j].High >= high || candles[i + j].High >= high)
                {
                    isPivot = false;
                    break;
                }
            }
            if (isPivot)
                return high;
        }

        decimal highest = decimal.MinValue;
        for (int i = Math.Max(0, endIndex - lookbackBars); i <= endIndex; i++)
        {
            if (candles[i].High > highest)
                highest = candles[i].High;
        }
        return highest;
    }

    /// <summary>Simple 1-bar swing low check (used by MACD divergence detection).</summary>
    public static bool IsSwingLow(IReadOnlyList<Candle> candles, int i)
        => i > 0 && i < candles.Count - 1
           && candles[i].Low < candles[i - 1].Low
           && candles[i].Low < candles[i + 1].Low;

    /// <summary>Simple 1-bar swing high check (used by MACD divergence detection).</summary>
    public static bool IsSwingHigh(IReadOnlyList<Candle> candles, int i)
        => i > 0 && i < candles.Count - 1
           && candles[i].High > candles[i - 1].High
           && candles[i].High > candles[i + 1].High;

    // ═════════════════════════════════════════════════════════════════════════
    // Candle pattern scoring
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scores engulfing and pin bar patterns on the signal bar.
    /// Returns 0..1 where 0.5 is neutral.
    /// </summary>
    public static decimal ScoreCandlePatterns(IReadOnlyList<Candle> candles, int lastIdx, bool isBullish)
    {
        if (lastIdx < 1) return 0.5m;

        var curr = candles[lastIdx];
        var prev = candles[lastIdx - 1];
        decimal score = 0.5m;

        decimal currBody = curr.Close - curr.Open;
        decimal prevBody = prev.Close - prev.Open;

        bool bullishEngulfing = currBody > 0 && prevBody < 0
            && curr.Open <= prev.Close && curr.Close >= prev.Open;
        bool bearishEngulfing = currBody < 0 && prevBody > 0
            && curr.Open >= prev.Close && curr.Close <= prev.Open;

        if ((isBullish && bullishEngulfing) || (!isBullish && bearishEngulfing))
            score += 0.3m;

        decimal bodySize  = Math.Abs(currBody);
        decimal upperWick = curr.High - Math.Max(curr.Open, curr.Close);
        decimal lowerWick = Math.Min(curr.Open, curr.Close) - curr.Low;

        if (bodySize > 0)
        {
            if (isBullish && lowerWick > bodySize * 2m && upperWick < bodySize)
                score += 0.2m;
            else if (!isBullish && upperWick > bodySize * 2m && lowerWick < bodySize)
                score += 0.2m;
        }

        return Math.Min(1.0m, score);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Close price extraction
    // ═════════════════════════════════════════════════════════════════════════

    public static decimal[] ExtractCloses(IReadOnlyList<Candle> candles)
    {
        var c = new decimal[candles.Count];
        for (int i = 0; i < candles.Count; i++) c[i] = candles[i].Close;
        return c;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // VWAP (Volume-Weighted Average Price)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the Volume-Weighted Average Price (VWAP) from sessionStartIndex to endIndex.
    /// VWAP = Σ(TypicalPrice × Volume) / Σ(Volume) where TypicalPrice = (H+L+C)/3.
    /// Returns 0 if total volume is zero.
    /// </summary>
    public static decimal Vwap(IReadOnlyList<Candle> candles, int endIndex, int sessionStartIndex)
    {
        if (sessionStartIndex < 0 || endIndex < sessionStartIndex || endIndex >= candles.Count)
            return 0m;

        decimal sumPV = 0m, sumV = 0m;
        for (int i = sessionStartIndex; i <= endIndex; i++)
        {
            var c = candles[i];
            decimal typicalPrice = (c.High + c.Low + c.Close) / 3m;
            sumPV += typicalPrice * c.Volume;
            sumV += c.Volume;
        }
        return sumV > 0 ? sumPV / sumV : 0m;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // OLS Hedge Ratio (linear regression)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the OLS (Ordinary Least Squares) linear regression hedge ratio.
    /// Returns (alpha, beta) where y ≈ alpha + beta * x.
    /// Returns (0, 0) if the regression is degenerate (insufficient data or zero variance).
    /// </summary>
    public static (decimal Alpha, decimal Beta) OlsHedgeRatio(decimal[] y, decimal[] x)
    {
        int n = Math.Min(y.Length, x.Length);
        if (n < 3) return (0m, 0m);

        // Use centered formulation to avoid catastrophic cancellation
        // (n*sumX2 - sumX*sumX can lose precision with large price values like JPY pairs)
        decimal meanX = 0m, meanY = 0m;
        for (int i = 0; i < n; i++) { meanX += x[i]; meanY += y[i]; }
        meanX /= n; meanY /= n;

        decimal ssXX = 0m, ssXY = 0m;
        for (int i = 0; i < n; i++)
        {
            decimal dx = x[i] - meanX;
            decimal dy = y[i] - meanY;
            ssXX += dx * dx;
            ssXY += dx * dy;
        }

        if (Math.Abs(ssXX) < 0.000000001m) return (0m, 0m);

        decimal beta = ssXY / ssXX;
        decimal alpha = meanY - beta * meanX;
        return (alpha, beta);
    }
}
