namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Represents the lifecycle status of a backtest or walk-forward run.
/// </summary>
public enum RunStatus
{
    /// <summary>Run is queued and waiting to be picked up by a worker.</summary>
    Queued = 0,

    /// <summary>Run is currently executing.</summary>
    Running = 1,

    /// <summary>Run finished successfully.</summary>
    Completed = 2,

    /// <summary>Run terminated due to an error.</summary>
    Failed = 3
}
