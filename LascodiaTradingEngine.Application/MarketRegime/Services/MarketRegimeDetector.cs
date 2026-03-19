using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.MarketRegime.Services;

/// <summary>
/// Detects the current market regime for a given symbol/timeframe using
/// ADX, ATR, and Bollinger Band Width calculated from the provided candle history.
/// </summary>
public class MarketRegimeDetector : IMarketRegimeDetector
{
    private const int AdxPeriod = 14;
    private const int AtrPeriod = 14;
    private const int BbPeriod  = 20;

    public Task<MarketRegimeSnapshot> DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        CancellationToken ct)
    {
        if (candles.Count < Math.Max(AdxPeriod + 1, BbPeriod))
            throw new InvalidOperationException(
                $"Insufficient candle data for regime detection. Need at least {Math.Max(AdxPeriod + 1, BbPeriod)} candles, got {candles.Count}.");

        double adx = CalculateAdx(candles);
        double atr = CalculateAtr(candles, AtrPeriod);
        double bbw = CalculateBollingerBandWidth(candles, BbPeriod);

        // ATR moving average over last 20 periods for high/low volatility classification
        double atrAvg = CalculateAtrAverage(candles, AtrPeriod, 20);

        MarketRegimeEnum regime;
        double confidence;

        if (atr > atrAvg * 1.5)
        {
            regime     = MarketRegimeEnum.HighVolatility;
            confidence = Math.Min(1.0, atr / (atrAvg * 2.0));
        }
        else if (atr < atrAvg * 0.5)
        {
            regime     = MarketRegimeEnum.LowVolatility;
            confidence = Math.Min(1.0, (atrAvg * 0.5 - atr) / (atrAvg * 0.5));
        }
        else if (adx > 25.0)
        {
            regime     = MarketRegimeEnum.Trending;
            confidence = Math.Min(1.0, adx / 50.0);
        }
        else
        {
            regime     = MarketRegimeEnum.Ranging;
            confidence = 1.0 - Math.Min(1.0, adx / 50.0);
        }

        var snapshot = new MarketRegimeSnapshot
        {
            Symbol             = symbol.ToUpperInvariant(),
            Timeframe          = timeframe,
            Regime             = regime,
            Confidence         = (decimal)Math.Round(confidence, 4),
            ADX                = (decimal)Math.Round(adx, 4),
            ATR                = (decimal)Math.Round(atr, 6),
            BollingerBandWidth = (decimal)Math.Round(bbw, 6),
            DetectedAt         = DateTime.UtcNow
        };

        return Task.FromResult(snapshot);
    }

    // ── ADX (Average Directional Index) ───────────────────────────────────────

    private static double CalculateAdx(IReadOnlyList<Candle> candles)
    {
        int n = candles.Count;
        if (n < AdxPeriod + 1) return 0.0;

        // True Range
        var trList  = new double[n];
        var dmPlus  = new double[n];
        var dmMinus = new double[n];

        for (int i = 1; i < n; i++)
        {
            double high  = (double)candles[i].High;
            double low   = (double)candles[i].Low;
            double close = (double)candles[i - 1].Close;

            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - close), Math.Abs(low - close)));
            trList[i] = tr;

            double upMove   = high - (double)candles[i - 1].High;
            double downMove = (double)candles[i - 1].Low - low;

            dmPlus[i]  = (upMove > downMove   && upMove > 0)   ? upMove   : 0.0;
            dmMinus[i] = (downMove > upMove   && downMove > 0) ? downMove : 0.0;
        }

        // Wilder smoothing for first ATR period
        double smoothedTr     = trList.Skip(1).Take(AdxPeriod).Sum();
        double smoothedDmPlus  = dmPlus.Skip(1).Take(AdxPeriod).Sum();
        double smoothedDmMinus = dmMinus.Skip(1).Take(AdxPeriod).Sum();

        var dxList = new List<double>();

        for (int i = AdxPeriod + 1; i < n; i++)
        {
            smoothedTr     = smoothedTr     - smoothedTr     / AdxPeriod + trList[i];
            smoothedDmPlus  = smoothedDmPlus  - smoothedDmPlus  / AdxPeriod + dmPlus[i];
            smoothedDmMinus = smoothedDmMinus - smoothedDmMinus / AdxPeriod + dmMinus[i];

            double diPlus  = smoothedTr > 0 ? 100.0 * smoothedDmPlus  / smoothedTr : 0.0;
            double diMinus = smoothedTr > 0 ? 100.0 * smoothedDmMinus / smoothedTr : 0.0;
            double diSum   = diPlus + diMinus;
            double dx      = diSum > 0 ? 100.0 * Math.Abs(diPlus - diMinus) / diSum : 0.0;

            dxList.Add(dx);
        }

        if (dxList.Count == 0) return 0.0;

        // ADX = simple average of last AdxPeriod DX values (Wilder would smooth, this is an approximation)
        return dxList.TakeLast(AdxPeriod).Average();
    }

    // ── ATR (Average True Range) ───────────────────────────────────────────────

    private static double CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        if (n < period + 1) return 0.0;

        double atr = 0.0;
        for (int i = n - period; i < n; i++)
        {
            double high  = (double)candles[i].High;
            double low   = (double)candles[i].Low;
            double close = (double)candles[i - 1].Close;

            double tr = Math.Max(high - low, Math.Max(Math.Abs(high - close), Math.Abs(low - close)));
            atr += tr;
        }

        return atr / period;
    }

    // ── ATR rolling average over lookback bars (each bar's ATR averaged) ───────

    private static double CalculateAtrAverage(IReadOnlyList<Candle> candles, int atrPeriod, int lookback)
    {
        int n = candles.Count;
        int start = n - lookback - atrPeriod;
        if (start < 1) start = 1;

        var atrValues = new List<double>();
        for (int end = start + atrPeriod; end <= n; end++)
        {
            var slice = candles.Skip(end - atrPeriod - 1).Take(atrPeriod + 1).ToList();
            atrValues.Add(CalculateAtr(slice, atrPeriod));
        }

        return atrValues.Count > 0 ? atrValues.Average() : CalculateAtr(candles, atrPeriod);
    }

    // ── Bollinger Band Width ───────────────────────────────────────────────────

    private static double CalculateBollingerBandWidth(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        if (n < period) return 0.0;

        var closes = candles.TakeLast(period).Select(c => (double)c.Close).ToList();

        double sma    = closes.Average();
        double sumSq  = closes.Sum(c => Math.Pow(c - sma, 2));
        double stdDev = Math.Sqrt(sumSq / period);

        double upper = sma + 2.0 * stdDev;
        double lower = sma - 2.0 * stdDev;

        return sma > 0 ? (upper - lower) / sma : 0.0;
    }
}
