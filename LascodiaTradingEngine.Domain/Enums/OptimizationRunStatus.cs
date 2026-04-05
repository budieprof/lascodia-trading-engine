namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Tracks the lifecycle of a strategy parameter optimisation run, including approval workflow.
/// </summary>
public enum OptimizationRunStatus
{
    /// <summary>Run is queued and waiting for worker pickup.</summary>
    Queued = 0,

    /// <summary>Optimisation is currently executing.</summary>
    Running = 1,

    /// <summary>Optimisation finished successfully and awaits review.</summary>
    Completed = 2,

    /// <summary>Optimisation terminated due to an error.</summary>
    Failed = 3,

    /// <summary>Optimised parameters were approved for deployment.</summary>
    Approved = 4,

    /// <summary>Optimised parameters were rejected during review.</summary>
    Rejected = 5,

    /// <summary>
    /// Run exhausted its retry budget and was moved to the dead-letter queue.
    /// Requires manual investigation — the run will not be retried automatically.
    /// </summary>
    Abandoned = 6
}
