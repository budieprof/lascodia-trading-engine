namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies the origin of a market sentiment data point.
/// </summary>
public enum SentimentSource
{
    /// <summary>Commitment of Traders report from the CFTC.</summary>
    COT = 0,

    /// <summary>Sentiment derived from news article analysis.</summary>
    NewsSentiment = 1,

    /// <summary>Automated sentiment feed from a third-party provider.</summary>
    AutoFeed = 2
}
