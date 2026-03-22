namespace LascodiaTradingEngine.Domain.Enums;
public enum StrategyStatus { Active = 0, Paused = 1, Backtesting = 2, Stopped = 3 }

public static class StrategyStatusTransitions
{
    private static readonly Dictionary<StrategyStatus, StrategyStatus[]> Allowed = new()
    {
        [StrategyStatus.Active]      = [StrategyStatus.Paused, StrategyStatus.Stopped],
        [StrategyStatus.Paused]      = [StrategyStatus.Active, StrategyStatus.Stopped, StrategyStatus.Backtesting],
        [StrategyStatus.Backtesting] = [StrategyStatus.Paused, StrategyStatus.Stopped],
        [StrategyStatus.Stopped]     = [StrategyStatus.Paused],
    };

    public static bool CanTransitionTo(this StrategyStatus current, StrategyStatus target)
        => Allowed.TryGetValue(current, out var targets) && targets.Contains(target);

    public static void EnsureTransition(this StrategyStatus current, StrategyStatus target)
    {
        if (!current.CanTransitionTo(target))
            throw new InvalidOperationException(
                $"Invalid strategy status transition: {current} → {target}");
    }
}
