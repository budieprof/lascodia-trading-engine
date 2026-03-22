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

    internal static decimal CalculateAdxProxy(IReadOnlyList<Candle> candles, int period)
    {
        // Simplified ADX proxy: use 14-period True Range average relative to price range
        decimal atr      = CalculateAtr(candles, period);
        decimal avgClose = candles.TakeLast(period).Average(c => c.Close);

        if (avgClose <= 0m) return 0m;

        // Scale to approximate ADX range (0-100)
        decimal adxProxy = atr / avgClose * 10000m * 2m;
        return Math.Min(100m, adxProxy);
    }
}
