namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the operational state of a trading strategy. Valid transitions
/// are enforced by <see cref="StrategyStatusTransitions"/>.
/// </summary>
public enum StrategyStatus
{
    /// <summary>Strategy is actively evaluating signals and generating trades.</summary>
    Active = 0,

    /// <summary>Strategy is temporarily paused; no new signals are generated.</summary>
    Paused = 1,

    /// <summary>Strategy is running in backtest mode against historical data.</summary>
    Backtesting = 2,

    /// <summary>Strategy has been permanently stopped.</summary>
    Stopped = 3
}

/// <summary>
/// Enforces valid state transitions for <see cref="StrategyStatus"/>.
/// </summary>
public static class StrategyStatusTransitions
{
    private static readonly Dictionary<StrategyStatus, StrategyStatus[]> Allowed = new()
    {
        [StrategyStatus.Active]      = [StrategyStatus.Paused, StrategyStatus.Stopped],
        [StrategyStatus.Paused]      = [StrategyStatus.Active, StrategyStatus.Stopped, StrategyStatus.Backtesting],
        [StrategyStatus.Backtesting] = [StrategyStatus.Paused, StrategyStatus.Stopped],
        [StrategyStatus.Stopped]     = [StrategyStatus.Paused],
    };

    /// <summary>
    /// Returns <c>true</c> if transitioning from <paramref name="current"/> to
    /// <paramref name="target"/> is a valid strategy status change.
    /// </summary>
    public static bool CanTransitionTo(this StrategyStatus current, StrategyStatus target)
        => Allowed.TryGetValue(current, out var targets) && targets.Contains(target);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if transitioning from
    /// <paramref name="current"/> to <paramref name="target"/> is not allowed.
    /// </summary>
    public static void EnsureTransition(this StrategyStatus current, StrategyStatus target)
    {
        if (!current.CanTransitionTo(target))
            throw new InvalidOperationException(
                $"Invalid strategy status transition: {current} → {target}");
    }
}
