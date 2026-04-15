namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies what initiated a strategy optimisation or retraining run.
/// </summary>
public enum TriggerType
{
    /// <summary>Run was triggered on a recurring schedule.</summary>
    Scheduled = 0,

    /// <summary>Run was manually initiated by a user.</summary>
    Manual = 1,

    /// <summary>Run was automatically triggered due to degrading strategy performance.</summary>
    AutoDegrading = 2,

    /// <summary>
    /// Run was queued by <c>StrategyGenerationWorker</c> because a CompositeML candidate
    /// was proposed for a (Symbol, Timeframe) combo that has no active MLModel. The
    /// strategy is parked in <c>LifecycleStage = PendingModel</c> until the training
    /// run completes and <c>MLModelActivatedIntegrationEvent</c> fires, at which point
    /// a handler re-screens the deferred strategy.
    /// </summary>
    AutoDeferred = 3
}
