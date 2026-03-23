namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Types of commands that the engine can queue for execution by an EA instance.
/// </summary>
public enum EACommandType
{
    /// <summary>Modify the stop-loss and/or take-profit of an open position.</summary>
    ModifySLTP,

    /// <summary>Close an open position by its broker ticket.</summary>
    ClosePosition,

    /// <summary>Cancel a pending order by its broker ticket.</summary>
    CancelOrder,

    /// <summary>Update or reconfigure trailing stop parameters for a position.</summary>
    UpdateTrailing,

    /// <summary>Request the EA to backfill historical candle data for a symbol/timeframe.</summary>
    RequestBackfill
}
