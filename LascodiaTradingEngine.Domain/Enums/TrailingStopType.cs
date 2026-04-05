namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Determines the method used to calculate the trailing stop distance.
/// </summary>
public enum TrailingStopType
{
    /// <summary>Trail by a fixed number of pips from the best price.</summary>
    FixedPips  = 0,

    /// <summary>Trail by a multiple of the Average True Range (ATR).</summary>
    ATR        = 1,

    /// <summary>Trail by a percentage of the current price.</summary>
    Percentage = 2
}
