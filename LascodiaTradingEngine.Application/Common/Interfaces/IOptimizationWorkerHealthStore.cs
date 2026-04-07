using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Typed operational snapshot for the optimization worker family.
/// Avoids stringly-typed JSON contracts between the worker and health queries.
/// </summary>
public interface IOptimizationWorkerHealthStore
{
    void UpdateMainWorkerState(OptimizationWorkerHealthStateSnapshot snapshot);
    OptimizationWorkerHealthStateSnapshot GetMainWorkerState();
}

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
