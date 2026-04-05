namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Provides the latest sentiment scores for a symbol's base and quote currencies.
/// Used as an ML feature for sentiment-direction alignment.
/// </summary>
public interface ISentimentProvider
{
    /// <summary>
    /// Returns the latest sentiment scores for the base and quote currencies.
    /// Scores range [-1, +1] where negative = bearish, positive = bullish.
    /// Returns (0, 0) if no sentiment data available.
    /// </summary>
    Task<(decimal BaseSentiment, decimal QuoteSentiment)> GetSentimentAsync(string symbol, CancellationToken ct);
}
