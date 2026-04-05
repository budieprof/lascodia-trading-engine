namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Provides market sentiment data for a given currency pair symbol.
/// Implementations may source data from broker positioning APIs (e.g. OANDA Open Position Ratios),
/// social-media NLP indices, commercial providers (MarketPsych, Refinitiv), or the
/// <see cref="IDeepSeekSentimentService"/> NLP pipeline.
/// </summary>
public interface ISentimentFeed
{
    /// <summary>
    /// Fetches the current sentiment breakdown for the given symbol.
    /// </summary>
    /// <param name="symbol">Currency pair symbol (e.g. "EURUSD").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SentimentReading"/> containing the net sentiment score and
    /// bullish/bearish/neutral percentage breakdown, or <c>null</c> if data is
    /// unavailable for the requested symbol.
    /// </returns>
    Task<SentimentReading?> FetchAsync(string symbol, CancellationToken ct);

    /// <summary>
    /// Identifies this feed implementation for audit trail purposes.
    /// </summary>
    string SourceName { get; }
}

/// <summary>
/// A single sentiment data point for a currency pair.
/// </summary>
/// <param name="SentimentScore">Net sentiment in the range [-1.0, +1.0] (BullishPct - BearishPct).</param>
/// <param name="BullishPct">Fraction of participants holding long positions [0.0, 1.0].</param>
/// <param name="BearishPct">Fraction of participants holding short positions [0.0, 1.0].</param>
/// <param name="NeutralPct">Remainder not classified as directional [0.0, 1.0].</param>
public sealed record SentimentReading(
    decimal SentimentScore,
    decimal BullishPct,
    decimal BearishPct,
    decimal NeutralPct);
