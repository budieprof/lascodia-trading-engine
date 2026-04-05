using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.MarketRegime.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Infrastructure implementation of IMarketRegimeDetector that uses ADX, ATR and
/// volatility score to classify the current market regime.
/// </summary>
[RegisterService]
public class MarketRegimeDetector : IMarketRegimeDetector
{
    private readonly MarketRegimeOptions _options;

    public MarketRegimeDetector(MarketRegimeOptions options)
    {
        _options = options;
    }

    public Task<MarketRegimeSnapshot> DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        CancellationToken ct)
    {
        int period = _options.Period;

        if (candles.Count < period + 1)
            throw new InvalidOperationException(
                $"Insufficient candle data. Need at least {period + 1} candles, got {candles.Count}.");

        decimal atr             = CalculateAtr(candles, period);
        decimal adx             = CalculateAdxProxy(candles, period);
        decimal avgClose        = candles.Average(c => c.Close);
        decimal volatilityScore = avgClose > 0 ? atr / avgClose * 10000m : 0m;

        // ── Crisis detection: extreme ATR spike + directional sell-off ──────
        bool isCrisis = false;
        if (avgClose > 0)
        {
            decimal atrRolling = CalculateAtrRollingAverage(candles, period, 20);
            if (atrRolling > 0 && atr > atrRolling * _options.CrisisAtrMultiplier)
            {
                int bearish = CountTrailingBearishCandles(candles);
                if (bearish >= _options.CrisisMinBearishCandles)
                    isCrisis = true;
            }
        }

        // ── Breakout detection: BB compression (pre-expansion) + ATR expansion ──
        bool isBreakout = false;
        if (!isCrisis && candles.Count > 20)
        {
            // Measure compression on the window BEFORE the latest candle — the expansion
            // candle itself would inflate the BBW and mask the preceding squeeze.
            var preExpansion = candles.Take(candles.Count - 1).ToList();
            decimal bbw = CalculateBollingerBandWidth(preExpansion, 20);
            decimal bbwAvg = CalculateBbwRollingAverage(preExpansion, 20, 20);
            if (bbwAvg > 0 && bbw < bbwAvg * _options.BreakoutCompressionRatio)
            {
                decimal latestTr = candles[^1].High - candles[^1].Low;
                if (atr > 0 && latestTr > atr * _options.BreakoutExpansionMultiplier)
                    isBreakout = true;
            }
        }

        MarketRegimeEnum regime = isCrisis ? MarketRegimeEnum.Crisis
            : isBreakout ? MarketRegimeEnum.Breakout
            : adx > _options.TrendingAdxThreshold && volatilityScore < _options.TrendingMaxVolatility ? MarketRegimeEnum.Trending
            : adx < _options.RangingAdxThreshold  && volatilityScore < _options.RangingMaxVolatility  ? MarketRegimeEnum.Ranging
            : volatilityScore > _options.HighVolatilityThreshold ? MarketRegimeEnum.HighVolatility
            : MarketRegimeEnum.LowVolatility;

        var snapshot = new MarketRegimeSnapshot
        {
            Symbol             = symbol.ToUpperInvariant(),
            Timeframe          = timeframe,
            Regime             = regime,
            Confidence         = Math.Min(1m, adx / _options.ConfidenceDivisor),
            ADX                = Math.Round(adx, 4),
            ATR                = Math.Round(atr, 6),
            BollingerBandWidth = volatilityScore,
            DetectedAt         = DateTime.UtcNow
        };

        return Task.FromResult(snapshot);
    }

    internal static decimal CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        decimal sum = 0m;
        int start = Math.Max(1, n - period);

        for (int i = start; i < n; i++)
        {
            decimal trueRange = candles[i].High - candles[i].Low;
            sum += trueRange;
        }

        int count = n - start;
        return count > 0 ? sum / count : 0m;
    }

    internal static decimal CalculateAtrRollingAverage(IReadOnlyList<Candle> candles, int atrPeriod, int lookback)
    {
        int n = candles.Count;
        if (n < atrPeriod + lookback) return CalculateAtr(candles, atrPeriod);

        decimal sum = 0m;
        int count = 0;
        for (int end = n - lookback; end < n; end++)
        {
            int start = Math.Max(1, end - atrPeriod);
            decimal s = 0m;
            for (int i = start; i <= end && i < n; i++)
                s += candles[i].High - candles[i].Low;
            int c = end - start + 1;
            if (c > 0) { sum += s / c; count++; }
        }

        return count > 0 ? sum / count : CalculateAtr(candles, atrPeriod);
    }

    internal static int CountTrailingBearishCandles(IReadOnlyList<Candle> candles)
    {
        int count = 0;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].Close < candles[i].Open)
                count++;
            else
                break;
        }
        return count;
    }

    internal static decimal CalculateBollingerBandWidth(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        if (n < period) return 0m;

        decimal sum = 0m;
        for (int i = n - period; i < n; i++)
            sum += candles[i].Close;
        decimal sma = sum / period;
        if (sma <= 0) return 0m;

        decimal sumSq = 0m;
        for (int i = n - period; i < n; i++)
        {
            decimal diff = candles[i].Close - sma;
            sumSq += diff * diff;
        }
        decimal stdDev = (decimal)Math.Sqrt((double)(sumSq / period));
        decimal upper = sma + 2m * stdDev;
        decimal lower = sma - 2m * stdDev;

        return (upper - lower) / sma;
    }

    internal static decimal CalculateBbwRollingAverage(IReadOnlyList<Candle> candles, int bbPeriod, int lookback)
    {
        int n = candles.Count;
        if (n < bbPeriod + lookback) return CalculateBollingerBandWidth(candles, bbPeriod);

        decimal sum = 0m;
        int count = 0;
        for (int end = n - lookback; end <= n; end++)
        {
            if (end < bbPeriod) continue;
            var slice = candles.Skip(end - bbPeriod).Take(bbPeriod).ToList();
            sum += CalculateBollingerBandWidth(slice, bbPeriod);
            count++;
        }

        return count > 0 ? sum / count : CalculateBollingerBandWidth(candles, bbPeriod);
    }

    /// <summary>
    /// Calculates ADX using Wilder's smoothed directional movement (+DI/-DI → DX → ADX).
    /// Falls back to an ATR-based proxy if there are insufficient candles for the full calculation.
    /// </summary>
    internal static decimal CalculateAdxProxy(IReadOnlyList<Candle> candles, int period)
    {
        // Need at least 2*period candles for Wilder's smoothing
        if (candles.Count < period * 2 + 1)
        {
            // Fallback: ATR-based proxy for insufficient data
            decimal atr      = CalculateAtr(candles, period);
            decimal avgClose = candles.TakeLast(period).Average(c => c.Close);
            if (avgClose <= 0m) return 0m;
            return Math.Min(100m, atr / avgClose * 10000m * 2m);
        }

        // Wilder's +DM/-DM and TR for each bar
        int start = candles.Count - period * 2;
        decimal smoothedPlusDm  = 0m, smoothedMinusDm = 0m, smoothedTr = 0m;

        // Seed the first period with simple sums
        for (int i = start + 1; i <= start + period; i++)
        {
            decimal high = candles[i].High, low = candles[i].Low;
            decimal prevHigh = candles[i - 1].High, prevLow = candles[i - 1].Low, prevClose = candles[i - 1].Close;

            decimal upMove   = high - prevHigh;
            decimal downMove = prevLow - low;
            smoothedPlusDm  += upMove > downMove && upMove > 0 ? upMove : 0m;
            smoothedMinusDm += downMove > upMove && downMove > 0 ? downMove : 0m;
            smoothedTr      += Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
        }

        // Wilder's smoothing for the remaining bars and collect DX values
        var dxValues = new List<decimal>();
        for (int i = start + period + 1; i < candles.Count; i++)
        {
            decimal high = candles[i].High, low = candles[i].Low;
            decimal prevHigh = candles[i - 1].High, prevLow = candles[i - 1].Low, prevClose = candles[i - 1].Close;

            decimal upMove   = high - prevHigh;
            decimal downMove = prevLow - low;
            decimal plusDm   = upMove > downMove && upMove > 0 ? upMove : 0m;
            decimal minusDm  = downMove > upMove && downMove > 0 ? downMove : 0m;
            decimal tr       = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));

            // Wilder's exponential smoothing: prev - prev/period + current
            smoothedPlusDm  = smoothedPlusDm  - smoothedPlusDm  / period + plusDm;
            smoothedMinusDm = smoothedMinusDm - smoothedMinusDm / period + minusDm;
            smoothedTr      = smoothedTr      - smoothedTr      / period + tr;

            if (smoothedTr <= 0m) continue;
            decimal plusDi  = smoothedPlusDm  / smoothedTr * 100m;
            decimal minusDi = smoothedMinusDm / smoothedTr * 100m;
            decimal diSum   = plusDi + minusDi;
            if (diSum <= 0m) continue;
            dxValues.Add(Math.Abs(plusDi - minusDi) / diSum * 100m);
        }

        if (dxValues.Count == 0) return 0m;

        // ADX = Wilder's smoothed average of DX values
        decimal adx = dxValues.Take(period).Average();
        for (int i = period; i < dxValues.Count; i++)
            adx = (adx * (period - 1) + dxValues[i]) / period;

        return Math.Min(100m, adx);
    }
}
