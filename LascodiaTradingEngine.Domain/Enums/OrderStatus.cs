namespace LascodiaTradingEngine.Domain.Enums;

public enum OrderStatus
{
    Pending     = 0,
    Submitted   = 1,
    PartialFill = 2,
    Filled      = 3,
    Cancelled   = 4,
    Rejected    = 5,
    Expired     = 6
}

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

    public static bool CanTransitionTo(this OrderStatus current, OrderStatus target)
        => Allowed.TryGetValue(current, out var targets) && targets.Contains(target);

    public static void EnsureTransition(this OrderStatus current, OrderStatus target)
    {
        if (!current.CanTransitionTo(target))
            throw new InvalidOperationException(
                $"Invalid order status transition: {current} → {target}");
    }
}
