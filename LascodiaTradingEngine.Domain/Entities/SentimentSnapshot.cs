using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a derived market sentiment score for a single currency at a point in time,
/// aggregated from one or more data sources (COT positioning, news, retail sentiment feeds).
/// </summary>
/// <remarks>
/// Sentiment snapshots provide a macro-level directional bias for a currency.
/// Strategies may use these as a confirming filter — e.g. only taking Long EURUSD trades
/// when EUR sentiment is positive and USD sentiment is negative.
///
/// The <c>SentimentWorker</c> produces snapshots periodically by processing the latest
/// <see cref="COTReport"/> data, news polarity scores, and broker retail positioning feeds,
/// normalising each into the −1.0 to +1.0 scale.
/// </remarks>
public class SentimentSnapshot : Entity<long>
{
    /// <summary>
    /// Three-letter currency code this snapshot covers (e.g. "USD", "EUR", "GBP").
    /// Strategies look up the sentiment for both currencies in a pair
    /// (e.g. EUR and USD for EURUSD) before evaluating signal direction.
    /// </summary>
    public string  Currency        { get; set; } = string.Empty;

    /// <summary>
    /// The data source used to derive this sentiment score.
    /// e.g. <c>COT</c> (CFTC futures positioning), <c>News</c> (NLP news polarity),
    /// <c>RetailContrarian</c> (inverse retail broker sentiment).
    /// </summary>
    public SentimentSource  Source          { get; set; } = SentimentSource.COT;

    /// <summary>
    /// Normalised sentiment score in the range −1.0 (extremely bearish) to +1.0 (extremely bullish).
    /// A score of 0.0 indicates neutral or mixed sentiment.
    /// Values outside [−1, 1] are invalid and indicate a calculation error.
    /// </summary>
    public decimal SentimentScore  { get; set; }

    /// <summary>
    /// Confidence level in the derived score, in the range 0.0–1.0.
    /// Low confidence may occur when the underlying data sample is too small
    /// or when conflicting signals from multiple indicators cancel out.
    /// </summary>
    public decimal Confidence      { get; set; }

    /// <summary>
    /// Optional raw JSON payload from the source system (COT figures, news article scores, etc.)
    /// stored for audit and reprocessing purposes. Not used at runtime.
    /// </summary>
    public string? RawDataJson     { get; set; }

    /// <summary>UTC timestamp when this sentiment snapshot was computed and persisted.</summary>
    public DateTime CapturedAt     { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted       { get; set; }
}
