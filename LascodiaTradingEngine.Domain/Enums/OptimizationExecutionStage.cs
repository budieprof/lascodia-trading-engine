namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Current persisted phase of an optimization run.
/// Complements <see cref="OptimizationRunStatus"/> with finer-grained pipeline progress.
/// </summary>
public enum OptimizationExecutionStage
{
    Queued = 0,
    Preflight = 1,
    DataLoad = 2,
    Search = 3,
    Validation = 4,
    Persist = 5,
    Approval = 6,
    CompletionPublication = 7,
    FollowUp = 8,
    Completed = 9,
    Approved = 10,
    Rejected = 11,
    Failed = 12,
    Abandoned = 13,
}
