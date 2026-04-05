namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the candlestick chart timeframe used for market data aggregation.
/// </summary>
public enum Timeframe
{
    /// <summary>1-minute bars.</summary>
    M1 = 0,

    /// <summary>5-minute bars.</summary>
    M5 = 1,

    /// <summary>15-minute bars.</summary>
    M15 = 2,

    /// <summary>1-hour bars.</summary>
    H1 = 3,

    /// <summary>4-hour bars.</summary>
    H4 = 4,

    /// <summary>Daily bars.</summary>
    D1 = 5
}
