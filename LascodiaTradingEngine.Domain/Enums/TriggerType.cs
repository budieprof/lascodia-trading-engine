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
    AutoDegrading = 2
}
