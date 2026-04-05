namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Specifies the execution method for a trade order.
/// </summary>
public enum ExecutionType
{
    /// <summary>Immediate execution at the current market price.</summary>
    Market    = 0,

    /// <summary>Execute only at the specified price or better.</summary>
    Limit     = 1,

    /// <summary>Trigger a market order when price reaches the stop level.</summary>
    Stop      = 2,

    /// <summary>Trigger a limit order when price reaches the stop level.</summary>
    StopLimit = 3
}
