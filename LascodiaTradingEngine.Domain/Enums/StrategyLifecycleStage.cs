namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Graduated lifecycle stages a strategy must pass through before generating live signals.
/// Each stage has a minimum duration enforced by the engine.
/// </summary>
public enum StrategyLifecycleStage
{
    /// <summary>Initial creation — parameters being configured, no evaluation.</summary>
    Draft = 0,
    /// <summary>Generates signals and tracks simulated P&amp;L without placing real orders.</summary>
    PaperTrading = 1,
    /// <summary>Has passed backtest qualification thresholds.</summary>
    BacktestQualified = 2,
    /// <summary>Generates real signals at micro lot sizes (0.01) for live validation.</summary>
    ShadowLive = 3,
    /// <summary>Passed human review of paper + shadow results; awaiting activation.</summary>
    Approved = 4,
    /// <summary>Fully active with standard lot sizing.</summary>
    Active = 5
}

public static class StrategyLifecycleTransitions
{
    private static readonly Dictionary<StrategyLifecycleStage, StrategyLifecycleStage[]> Allowed = new()
    {
        [StrategyLifecycleStage.Draft]              = [StrategyLifecycleStage.PaperTrading],
        [StrategyLifecycleStage.PaperTrading]       = [StrategyLifecycleStage.BacktestQualified, StrategyLifecycleStage.Draft],
        [StrategyLifecycleStage.BacktestQualified]  = [StrategyLifecycleStage.ShadowLive, StrategyLifecycleStage.Draft],
        [StrategyLifecycleStage.ShadowLive]         = [StrategyLifecycleStage.Approved, StrategyLifecycleStage.Draft],
        [StrategyLifecycleStage.Approved]            = [StrategyLifecycleStage.Active, StrategyLifecycleStage.Draft],
        [StrategyLifecycleStage.Active]              = [StrategyLifecycleStage.Draft],
    };

    public static bool CanTransitionTo(this StrategyLifecycleStage current, StrategyLifecycleStage target)
        => Allowed.TryGetValue(current, out var targets) && targets.Contains(target);

    public static void EnsureTransition(this StrategyLifecycleStage current, StrategyLifecycleStage target)
    {
        if (!current.CanTransitionTo(target))
            throw new InvalidOperationException($"Invalid lifecycle transition: {current} → {target}");
    }
}
