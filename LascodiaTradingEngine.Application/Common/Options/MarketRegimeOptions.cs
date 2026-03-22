using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Thresholds for market regime classification.
/// Bound from the <c>MarketRegimeOptions</c> section in appsettings.json.
/// </summary>
public class MarketRegimeOptions : ConfigurationOption<MarketRegimeOptions>
{
    /// <summary>Number of candles used for ADX and ATR calculation. Defaults to 14.</summary>
    public int Period { get; set; } = 14;

    /// <summary>ADX above this value indicates a trending market. Defaults to 25.</summary>
    public decimal TrendingAdxThreshold { get; set; } = 25m;

    /// <summary>Volatility score below this combined with high ADX indicates trending. Defaults to 20.</summary>
    public decimal TrendingMaxVolatility { get; set; } = 20m;

    /// <summary>ADX below this value indicates a ranging market. Defaults to 20.</summary>
    public decimal RangingAdxThreshold { get; set; } = 20m;

    /// <summary>Volatility score below this combined with low ADX indicates ranging. Defaults to 10.</summary>
    public decimal RangingMaxVolatility { get; set; } = 10m;

    /// <summary>Volatility score above this indicates high volatility regime. Defaults to 30.</summary>
    public decimal HighVolatilityThreshold { get; set; } = 30m;

    /// <summary>
    /// ATR spike multiplier above the rolling average that signals a crisis regime.
    /// When ATR exceeds atrAvg × this value AND price is dropping, Crisis is detected.
    /// Defaults to 2.5 (checked before HighVolatility at 1.5×).
    /// </summary>
    public decimal CrisisAtrMultiplier { get; set; } = 2.5m;

    /// <summary>
    /// Minimum number of consecutive bearish candles (close &lt; open) at the tail of the
    /// candle window required to confirm directional sell-off for Crisis classification.
    /// Defaults to 3.
    /// </summary>
    public int CrisisMinBearishCandles { get; set; } = 3;

    /// <summary>
    /// Bollinger Band Width compression ratio relative to its rolling average.
    /// When current BBW &lt; atrAvg × this value, the market is in squeeze/compression.
    /// Defaults to 0.4 (i.e., BBW is less than 40% of its recent average).
    /// </summary>
    public decimal BreakoutCompressionRatio { get; set; } = 0.4m;

    /// <summary>
    /// ATR expansion multiplier that confirms a breakout after compression.
    /// When the latest candle's true range exceeds ATR × this value, the expansion is confirmed.
    /// Defaults to 1.8.
    /// </summary>
    public decimal BreakoutExpansionMultiplier { get; set; } = 1.8m;

    /// <summary>Divisor for scaling ADX into a 0-1 confidence range. Defaults to 50.</summary>
    public decimal ConfidenceDivisor { get; set; } = 50m;
}
