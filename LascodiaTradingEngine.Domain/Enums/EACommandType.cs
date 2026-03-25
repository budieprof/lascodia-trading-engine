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
    RequestBackfill,

    /// <summary>
    /// Request the EA to report the current status of an order by broker ticket or engine order ID.
    /// Queued by <c>StaleOrderRecoveryWorker</c> when an order is stuck in Submitted status
    /// without an execution report from the EA.
    /// </summary>
    RequestExecutionStatus,

    /// <summary>
    /// Hot-reload EA safety configuration parameters without requiring an EA restart.
    /// Parameters JSON contains the safety limits to update (zero values are ignored/keep current).
    /// Queued via POST /ea/commands/update-config endpoint.
    /// </summary>
    UpdateConfig
}
