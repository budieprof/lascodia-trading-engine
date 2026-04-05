namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the current state of a trading position.
/// </summary>
public enum PositionStatus
{
    /// <summary>Position is active with exposure in the market.</summary>
    Open = 0,

    /// <summary>Position has been fully closed and realised.</summary>
    Closed = 1,

    /// <summary>A close request has been issued but not yet confirmed by the broker.</summary>
    Closing = 2
}
