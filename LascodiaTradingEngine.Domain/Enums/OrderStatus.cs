namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the lifecycle status of a trade order. Valid transitions are
/// enforced by <see cref="OrderStatusTransitions"/>.
/// </summary>
public enum OrderStatus
{
    /// <summary>Order created but not yet submitted to the broker.</summary>
    Pending     = 0,

    /// <summary>Order has been sent to the broker and is awaiting execution.</summary>
    Submitted   = 1,

    /// <summary>Order has been partially filled; remaining quantity is still working.</summary>
    PartialFill = 2,

    /// <summary>Order has been completely filled.</summary>
    Filled      = 3,

    /// <summary>Order was cancelled before full execution.</summary>
    Cancelled   = 4,

    /// <summary>Order was rejected by the broker or risk checks.</summary>
    Rejected    = 5,

    /// <summary>Order expired without being filled (e.g. GTD timeout).</summary>
    Expired     = 6
}

/// <summary>
/// Enforces valid state transitions for <see cref="OrderStatus"/>.
/// </summary>
public static class OrderStatusTransitions
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Pending]     = [OrderStatus.Submitted, OrderStatus.Cancelled, OrderStatus.Rejected, OrderStatus.Expired],
        [OrderStatus.Submitted]   = [OrderStatus.PartialFill, OrderStatus.Filled, OrderStatus.Cancelled, OrderStatus.Rejected, OrderStatus.Expired],
        [OrderStatus.PartialFill] = [OrderStatus.Filled, OrderStatus.Cancelled],
        [OrderStatus.Filled]      = [],
        [OrderStatus.Cancelled]   = [],
        [OrderStatus.Rejected]    = [],
        [OrderStatus.Expired]     = [],
    };

    /// <summary>
    /// Returns <c>true</c> if transitioning from <paramref name="current"/> to
    /// <paramref name="target"/> is a valid order status change.
    /// </summary>
    public static bool CanTransitionTo(this OrderStatus current, OrderStatus target)
        => Allowed.TryGetValue(current, out var targets) && targets.Contains(target);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if transitioning from
    /// <paramref name="current"/> to <paramref name="target"/> is not allowed.
    /// </summary>
    public static void EnsureTransition(this OrderStatus current, OrderStatus target)
    {
        if (!current.CanTransitionTo(target))
            throw new InvalidOperationException(
                $"Invalid order status transition: {current} → {target}");
    }
}
