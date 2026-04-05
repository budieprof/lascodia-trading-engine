namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies the provider from which an economic calendar event was sourced.
/// </summary>
public enum EconomicEventSource
{
    /// <summary>Forex Factory economic calendar.</summary>
    ForexFactory = 0,

    /// <summary>Investing.com economic calendar.</summary>
    Investing = 1,

    /// <summary>Manually entered by a user or administrator.</summary>
    Manual = 2,

    /// <summary>OANDA economic calendar feed.</summary>
    Oanda = 3
}
