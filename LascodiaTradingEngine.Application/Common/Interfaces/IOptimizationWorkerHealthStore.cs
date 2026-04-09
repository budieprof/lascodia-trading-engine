using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Typed operational snapshot for the optimization worker family.
/// Avoids stringly-typed JSON contracts between the worker and health queries.
/// </summary>
public interface IOptimizationWorkerHealthStore
{
    void UpdateMainWorkerState(OptimizationWorkerHealthStateSnapshot snapshot);
    void UpdateMainWorkerState(Func<OptimizationWorkerHealthStateSnapshot, OptimizationWorkerHealthStateSnapshot> updater);
    void RecordQueueWaitSample(long queueWaitMs);
    void RecordPhaseStarted(string phaseName, DateTime utcNow);
    void RecordPhaseSuccess(string phaseName, long durationMs, DateTime utcNow);
    void RecordPhaseFailure(string phaseName, string errorType, string errorMessage, long durationMs, DateTime utcNow);
    QueueWaitPercentileSnapshot GetQueueWaitPercentiles();
    OptimizationWorkerHealthStateSnapshot GetMainWorkerState();
    IReadOnlyList<OptimizationWorkerPhaseStateSnapshot> GetPhaseStates();
}

public readonly record struct QueueWaitPercentileSnapshot(
    long P50Ms,
    long P95Ms,
    long P99Ms);

public sealed record OptimizationDeferralReasonCountSnapshot(
    OptimizationDeferralReason Reason,
    int Count);

public sealed record OptimizationWorkerPhaseStateSnapshot
{
    public string PhaseName { get; init; } = string.Empty;
    public DateTime? LastStartedAtUtc { get; init; }
    public DateTime? LastCompletedAtUtc { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public DateTime? LastFailureAtUtc { get; init; }
    public string? LastFailureType { get; init; }
    public string? LastFailureMessage { get; init; }
    public int ConsecutiveFailures { get; init; }
    public long LastDurationMs { get; init; }
    public long LastSuccessDurationMs { get; init; }
    public int SuccessesLastHour { get; init; }
    public int FailuresLastHour { get; init; }
}

public sealed record OptimizationWorkerHealthStateSnapshot
{
    public int QueuedRuns { get; init; }
    public int DeferredQueuedRuns { get; init; }
    public int RunningRuns { get; init; }
    public int ActiveLeasedRunningRuns { get; init; }
    public int StaleRunningRuns { get; init; }
    public int LeaseMissingRunningRuns { get; init; }
    public int RetryableFailedRuns { get; init; }
    public int AbandonedRuns { get; init; }
    public int PendingFollowUps { get; init; }
    public int PendingCompletionPublications { get; init; }
    public int ApprovedRunsMissingFollowUps { get; init; }
    public int PendingCompletionPreparation { get; init; }
    public int StrandedLifecycleRuns { get; init; }
    public int LifecycleRepairsLastCycle { get; init; }
    public int LifecycleBatchesLastCycle { get; init; }
    public int LifecycleMissingCompletionPayloadRepairsLastCycle { get; init; }
    public int LifecycleMalformedCompletionPayloadRepairsLastCycle { get; init; }
    public int LifecycleFollowUpRepairsLastCycle { get; init; }
    public int LifecycleConfigSnapshotRepairsLastCycle { get; init; }
    public int LifecycleBestParameterRepairsLastCycle { get; init; }
    public int LeaseReclaimsLastCycle { get; init; }
    public int OrphanedStaleRunningRunsLastCycle { get; init; }
    public int ConfigCacheAgeSeconds { get; init; }
    public DateTime? ConfigRefreshDueAtUtc { get; init; }
    public int ConfigRefreshIntervalSeconds { get; init; }
    public int ActiveProcessingSlots { get; init; }
    public int ConfiguredMaxConcurrentRuns { get; init; }
    public int ProcessingSlotFailuresLastHour { get; init; }
    public DateTime? LastProcessingSlotFailureAtUtc { get; init; }
    public string? LastProcessingSlotFailureMessage { get; init; }
    public long QueueWaitP50Ms { get; init; }
    public long QueueWaitP95Ms { get; init; }
    public long QueueWaitP99Ms { get; init; }
    public long? OldestQueuedRunId { get; init; }
    public DateTime? OldestQueuedAtUtc { get; init; }
    public int OldestQueuedAgeSeconds { get; init; }
    public long? OldestDeferredQueuedRunId { get; init; }
    public DateTime? OldestDeferredQueuedAtUtc { get; init; }
    public DateTime? OldestDeferredUntilUtc { get; init; }
    public int OldestDeferredQueuedAgeSeconds { get; init; }
    public long? MostDeferredQueuedRunId { get; init; }
    public int MostDeferredQueuedDeferralCount { get; init; }
    public long? MostRecentDeferredResumeRunId { get; init; }
    public DateTime? MostRecentDeferredResumeAtUtc { get; init; }
    public IReadOnlyList<OptimizationDeferralReasonCountSnapshot> DeferredQueuedRunsByReason { get; init; } = [];
    public int StarvedQueuedRuns { get; init; }
    public long? OldestStarvedQueuedRunId { get; init; }
    public DateTime? OldestStarvedQueuedAtUtc { get; init; }
    public int OldestStarvedQueuedAgeSeconds { get; init; }
    public DateTime? LastSuccessfulConfigRefreshAtUtc { get; init; }
    public bool IsConfigLoadDegraded { get; init; }
    public int ConsecutiveConfigLoadFailures { get; init; }
    public DateTime? LastConfigLoadFailureAtUtc { get; init; }
    public string? LastConfigLoadFailureMessage { get; init; }
    public DateTime? LastLifecycleReconciledAtUtc { get; init; }
    public long? OldestRunningRunId { get; init; }
    public OptimizationExecutionStage? OldestRunningStage { get; init; }
    public string? OldestRunningStageMessage { get; init; }
    public DateTime? OldestRunningStageUpdatedAt { get; init; }
    public long? OldestStrandedLifecycleRunId { get; init; }
    public OptimizationRunStatus? OldestStrandedLifecycleStatus { get; init; }
    public DateTime? OldestStrandedLifecycleAnchorAtUtc { get; init; }
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}
