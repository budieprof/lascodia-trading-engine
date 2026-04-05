namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks the lifecycle status of a position scale-in or scale-out order.
/// </summary>
public enum ScaleOrderStatus
{
    /// <summary>Order is queued and awaiting its trigger condition.</summary>
    Pending = 0,

    /// <summary>Trigger condition met; order has been sent to the broker.</summary>
    Triggered = 1,

    /// <summary>Order has been fully filled at the broker.</summary>
    Filled = 2,

    /// <summary>Order was cancelled before execution.</summary>
    Cancelled = 3
}
