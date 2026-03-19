using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a point-in-time classification of the current market regime for a specific
/// symbol and timeframe, based on quantitative indicator readings.
/// </summary>
/// <remarks>
/// The market regime classifier runs periodically and detects whether the market is
/// trending (strong directional movement), ranging (oscillating within a band), or
/// in a volatile/breakout state. Strategies can filter their signals based on the
/// current regime to avoid counter-trend trades in strong trends, or trend-following
/// trades in ranging markets.
///
/// Key indicators used:
/// <list type="bullet">
///   <item><description><b>ADX</b> — Average Directional Index; values &gt; 25 typically indicate a trend.</description></item>
///   <item><description><b>ATR</b> — Average True Range; measures absolute volatility.</description></item>
///   <item><description><b>Bollinger Band Width</b> — measures relative volatility; expanding bands suggest breakouts.</description></item>
/// </list>
/// </remarks>
public class MarketRegimeSnapshot : Entity<long>
{
    /// <summary>The currency pair this regime snapshot covers (e.g. "EURUSD").</summary>
    public string  Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe on which the regime classification was computed.</summary>
    public Timeframe  Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// The detected regime classification: <c>Trending</c>, <c>Ranging</c>, or <c>Volatile</c>.
    /// Strategies use this to select appropriate trade-entry logic.
    /// </summary>
    public MarketRegime  Regime             { get; set; } = MarketRegime.Ranging;

    /// <summary>
    /// Classifier confidence in the regime determination, in the range 0.0–1.0.
    /// Low confidence indicates an ambiguous or transitioning market condition.
    /// </summary>
    public decimal Confidence         { get; set; }

    /// <summary>
    /// Average Directional Index value at the time of classification.
    /// Values above 25 indicate a strong trend; below 20 suggest a ranging market.
    /// </summary>
    public decimal ADX                { get; set; }

    /// <summary>
    /// Average True Range in price units at the time of classification.
    /// Used to contextualise the absolute volatility level for stop-loss sizing.
    /// </summary>
    public decimal ATR                { get; set; }

    /// <summary>
    /// Bollinger Band width (upper band − lower band) at the time of classification.
    /// Expanding width signals increasing volatility / potential breakout;
    /// contracting width signals low volatility / consolidation.
    /// </summary>
    public decimal BollingerBandWidth { get; set; }

    /// <summary>UTC timestamp when this regime snapshot was recorded.</summary>
    public DateTime DetectedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted          { get; set; }
}
