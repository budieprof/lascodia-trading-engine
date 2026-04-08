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
    QueueWaitPercentileSnapshot GetQueueWaitPercentiles();
    OptimizationWorkerHealthStateSnapshot GetMainWorkerState();
}

public readonly record struct QueueWaitPercentileSnapshot(
    long P50Ms,
    long P95Ms,
    long P99Ms);

public sealed record OptimizationWorkerHealthStateSnapshot
{
    public int QueuedRuns { get; init; }
    public int RunningRuns { get; init; }
    public int RetryableFailedRuns { get; init; }
    public int AbandonedRuns { get; init; }
    public int PendingFollowUps { get; init; }
    public int PendingCompletionPublications { get; init; }
    public int ApprovedRunsMissingFollowUps { get; init; }
    public int PendingCompletionPreparation { get; init; }
    public int StrandedLifecycleRuns { get; init; }
    public int LifecycleRepairsLastCycle { get; init; }
    public int LifecycleBatchesLastCycle { get; init; }
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
