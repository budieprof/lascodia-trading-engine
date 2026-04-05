namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies major forex trading sessions used for session-based filtering and strategies.
/// </summary>
public enum TradingSession
{
    /// <summary>London session (08:00-16:00 GMT).</summary>
    London          = 0,

    /// <summary>New York session (13:00-22:00 GMT).</summary>
    NewYork         = 1,

    /// <summary>Asian session (Tokyo/Sydney, 00:00-08:00 GMT).</summary>
    Asian           = 2,

    /// <summary>London-New York overlap period with peak liquidity (13:00-16:00 GMT).</summary>
    LondonNYOverlap = 3
}
