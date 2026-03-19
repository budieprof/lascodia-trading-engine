using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MarketRegime.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Infrastructure implementation of IMarketRegimeDetector that uses ADX, ATR and
/// volatility score to classify the current market regime.
/// </summary>
public class MarketRegimeDetector : IMarketRegimeDetector
{
    private const int Period = 14;

    public Task<MarketRegimeSnapshot> DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        CancellationToken ct)
    {
        if (candles.Count < Period + 1)
            throw new InvalidOperationException(
                $"Insufficient candle data. Need at least {Period + 1} candles, got {candles.Count}.");

        decimal atr             = CalculateAtr(candles, Period);
        decimal adx             = CalculateAdxProxy(candles, Period);
        decimal avgClose        = candles.Average(c => c.Close);
        decimal volatilityScore = avgClose > 0 ? atr / avgClose * 10000m : 0m;

        MarketRegimeEnum regime = adx > 25m && volatilityScore < 20m ? MarketRegimeEnum.Trending
            : adx < 20m && volatilityScore < 10m ? MarketRegimeEnum.Ranging
            : volatilityScore > 30m ? MarketRegimeEnum.HighVolatility
            : MarketRegimeEnum.LowVolatility;

        var snapshot = new MarketRegimeSnapshot
        {
            Symbol             = symbol.ToUpperInvariant(),
            Timeframe          = timeframe,
            Regime             = regime,
            Confidence         = Math.Min(1m, adx / 50m),
            ADX                = Math.Round(adx, 4),
            ATR                = Math.Round(atr, 6),
            BollingerBandWidth = volatilityScore,
            DetectedAt         = DateTime.UtcNow
        };

        return Task.FromResult(snapshot);
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int period)
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

    private static decimal CalculateAdxProxy(IReadOnlyList<Candle> candles, int period)
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
