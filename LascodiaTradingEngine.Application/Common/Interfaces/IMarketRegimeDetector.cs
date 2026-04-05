using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Classifies the current market regime (Trending, Ranging, HighVolatility, Crisis, Breakout)
/// from recent candle data using ADX, ATR, and Bollinger Band Width indicators.
/// </summary>
public interface IMarketRegimeDetector
{
    /// <summary>
    /// Analyses the provided candles and returns a <see cref="MarketRegimeSnapshot"/>
    /// containing the detected regime, confidence score, and supporting indicator values.
    /// </summary>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="timeframe">Chart timeframe to classify.</param>
    /// <param name="candles">Recent candle history; must contain enough bars for ADX/ATR calculation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MarketRegimeSnapshot> DetectAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> candles,
        CancellationToken ct);
}
