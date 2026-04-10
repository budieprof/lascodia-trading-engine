using MediatR;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SystemHealth.Queries.GetOptimizationWorkerHealth;

public sealed class OptimizationWorkerHealthSnapshotDto
{
    public string WorkerName { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public DateTime? LastErrorAt { get; init; }
    public string? LastErrorMessage { get; init; }
    public long LastCycleDurationMs { get; init; }
    public long CycleDurationP50Ms { get; init; }
    public long CycleDurationP95Ms { get; init; }
    public long CycleDurationP99Ms { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int ErrorsLastHour { get; init; }
    public int SuccessesLastHour { get; init; }
    public int BacklogDepth { get; init; }
    public long LastQueueLatencyMs { get; init; }
    public long QueueLatencyP50Ms { get; init; }
    public long QueueLatencyP95Ms { get; init; }
    public long LastExecutionDurationMs { get; init; }
    public long ExecutionDurationP50Ms { get; init; }
    public long ExecutionDurationP95Ms { get; init; }
    public int RetriesLastHour { get; init; }
    public int RecoveriesLastHour { get; init; }
    public int ConfiguredIntervalSeconds { get; init; }
}

public sealed class OptimizationDeferralReasonCountDto
{
    public OptimizationDeferralReason Reason { get; init; }
    public int Count { get; init; }
}

public sealed class OptimizationWorkerPhaseHealthDto
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
    public bool IsDegraded { get; init; }
    public DateTime? BackoffUntilUtc { get; init; }
    public DateTime? LastSkippedAtUtc { get; init; }
    public string? LastSkipReason { get; init; }
    public int SkippedExecutionsLastHour { get; init; }
}

public sealed class OptimizationWorkerHealthDto
{
    public OptimizationWorkerHealthSnapshotDto? CoordinatorWorker { get; init; }
    public OptimizationWorkerHealthSnapshotDto? OptimizationWorker { get; init; }
    public OptimizationWorkerHealthSnapshotDto? CompletionReplayWorker { get; init; }
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
    public int QueuedRuns { get; init; }
    public int DeferredQueuedRuns { get; init; }
    public long? OldestDeferredQueuedRunId { get; init; }
    public DateTime? OldestDeferredQueuedAtUtc { get; init; }
    public DateTime? OldestDeferredUntilUtc { get; init; }
    public int OldestDeferredQueuedAgeSeconds { get; init; }
    public long? MostDeferredQueuedRunId { get; init; }
    public int MostDeferredQueuedDeferralCount { get; init; }
    public long? MostRecentDeferredResumeRunId { get; init; }
    public DateTime? MostRecentDeferredResumeAtUtc { get; init; }
    public int DeferredRunsStartedLastHour { get; init; }
    public int DeferredRunsResumedLastHour { get; init; }
    public int RepeatedlyDeferredQueuedRuns { get; init; }
    public long? OldestActiveDeferralRunId { get; init; }
    public DateTime? OldestActiveDeferralAtUtc { get; init; }
    public int OldestActiveDeferralAgeSeconds { get; init; }
    public IReadOnlyList<OptimizationDeferralReasonCountDto> DeferredQueuedRunsByReason { get; init; } = [];
    public int StarvedQueuedRuns { get; init; }
    public long? OldestStarvedQueuedRunId { get; init; }
    public DateTime? OldestStarvedQueuedAtUtc { get; init; }
    public int OldestStarvedQueuedAgeSeconds { get; init; }
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
    public IReadOnlyList<OptimizationWorkerPhaseHealthDto> PhaseStates { get; init; } = [];
}

public class GetOptimizationWorkerHealthQuery : IRequest<ResponseData<OptimizationWorkerHealthDto>>
{
}

public class GetOptimizationWorkerHealthQueryHandler
    : IRequestHandler<GetOptimizationWorkerHealthQuery, ResponseData<OptimizationWorkerHealthDto>>
{
    private readonly IWorkerHealthMonitor _healthMonitor;
    private readonly IOptimizationWorkerHealthStore _optimizationHealthStore;

    public GetOptimizationWorkerHealthQueryHandler(
        IWorkerHealthMonitor healthMonitor,
        IOptimizationWorkerHealthStore optimizationHealthStore)
    {
        _healthMonitor = healthMonitor;
        _optimizationHealthStore = optimizationHealthStore;
    }

    public Task<ResponseData<OptimizationWorkerHealthDto>> Handle(
        GetOptimizationWorkerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();
        var coordinatorWorker = snapshots.FirstOrDefault(s => s.WorkerName == OptimizationWorkerHealthNames.CoordinatorWorker);
        var optimizationWorker = snapshots.FirstOrDefault(s => s.WorkerName == OptimizationWorkerHealthNames.ExecutionWorker);
        var replayWorker = snapshots.FirstOrDefault(s => s.WorkerName == OptimizationWorkerHealthNames.CompletionReplayWorker);
        var typedState = _optimizationHealthStore.GetMainWorkerState();
        var phaseStates = _optimizationHealthStore.GetPhaseStates() ?? [];

        var dto = new OptimizationWorkerHealthDto
        {
            CoordinatorWorker = MapSnapshot(coordinatorWorker),
            OptimizationWorker = MapSnapshot(optimizationWorker),
            CompletionReplayWorker = MapSnapshot(replayWorker),
            ActiveProcessingSlots = typedState.ActiveProcessingSlots,
            ConfiguredMaxConcurrentRuns = typedState.ConfiguredMaxConcurrentRuns,
            ProcessingSlotFailuresLastHour = typedState.ProcessingSlotFailuresLastHour,
            LastProcessingSlotFailureAtUtc = typedState.LastProcessingSlotFailureAtUtc,
            LastProcessingSlotFailureMessage = typedState.LastProcessingSlotFailureMessage,
            QueueWaitP50Ms = typedState.QueueWaitP50Ms,
            QueueWaitP95Ms = typedState.QueueWaitP95Ms,
            QueueWaitP99Ms = typedState.QueueWaitP99Ms,
            OldestQueuedRunId = typedState.OldestQueuedRunId,
            OldestQueuedAtUtc = typedState.OldestQueuedAtUtc,
            OldestQueuedAgeSeconds = typedState.OldestQueuedAgeSeconds,
            QueuedRuns = typedState.QueuedRuns,
            DeferredQueuedRuns = typedState.DeferredQueuedRuns,
            OldestDeferredQueuedRunId = typedState.OldestDeferredQueuedRunId,
            OldestDeferredQueuedAtUtc = typedState.OldestDeferredQueuedAtUtc,
            OldestDeferredUntilUtc = typedState.OldestDeferredUntilUtc,
            OldestDeferredQueuedAgeSeconds = typedState.OldestDeferredQueuedAgeSeconds,
            MostDeferredQueuedRunId = typedState.MostDeferredQueuedRunId,
            MostDeferredQueuedDeferralCount = typedState.MostDeferredQueuedDeferralCount,
            MostRecentDeferredResumeRunId = typedState.MostRecentDeferredResumeRunId,
            MostRecentDeferredResumeAtUtc = typedState.MostRecentDeferredResumeAtUtc,
            DeferredRunsStartedLastHour = typedState.DeferredRunsStartedLastHour,
            DeferredRunsResumedLastHour = typedState.DeferredRunsResumedLastHour,
            RepeatedlyDeferredQueuedRuns = typedState.RepeatedlyDeferredQueuedRuns,
            OldestActiveDeferralRunId = typedState.OldestActiveDeferralRunId,
            OldestActiveDeferralAtUtc = typedState.OldestActiveDeferralAtUtc,
            OldestActiveDeferralAgeSeconds = typedState.OldestActiveDeferralAgeSeconds,
            DeferredQueuedRunsByReason = typedState.DeferredQueuedRunsByReason
                .Select(x => new OptimizationDeferralReasonCountDto
                {
                    Reason = x.Reason,
                    Count = x.Count
                })
                .ToArray(),
            StarvedQueuedRuns = typedState.StarvedQueuedRuns,
            OldestStarvedQueuedRunId = typedState.OldestStarvedQueuedRunId,
            OldestStarvedQueuedAtUtc = typedState.OldestStarvedQueuedAtUtc,
            OldestStarvedQueuedAgeSeconds = typedState.OldestStarvedQueuedAgeSeconds,
            RunningRuns = typedState.RunningRuns,
            ActiveLeasedRunningRuns = typedState.ActiveLeasedRunningRuns,
            StaleRunningRuns = typedState.StaleRunningRuns,
            LeaseMissingRunningRuns = typedState.LeaseMissingRunningRuns,
            RetryableFailedRuns = typedState.RetryableFailedRuns,
            AbandonedRuns = typedState.AbandonedRuns,
            PendingFollowUps = typedState.PendingFollowUps,
            PendingCompletionPublications = typedState.PendingCompletionPublications,
            ApprovedRunsMissingFollowUps = typedState.ApprovedRunsMissingFollowUps,
            PendingCompletionPreparation = typedState.PendingCompletionPreparation,
            StrandedLifecycleRuns = typedState.StrandedLifecycleRuns,
            LifecycleRepairsLastCycle = typedState.LifecycleRepairsLastCycle,
            LifecycleBatchesLastCycle = typedState.LifecycleBatchesLastCycle,
            LifecycleMissingCompletionPayloadRepairsLastCycle = typedState.LifecycleMissingCompletionPayloadRepairsLastCycle,
            LifecycleMalformedCompletionPayloadRepairsLastCycle = typedState.LifecycleMalformedCompletionPayloadRepairsLastCycle,
            LifecycleFollowUpRepairsLastCycle = typedState.LifecycleFollowUpRepairsLastCycle,
            LifecycleConfigSnapshotRepairsLastCycle = typedState.LifecycleConfigSnapshotRepairsLastCycle,
            LifecycleBestParameterRepairsLastCycle = typedState.LifecycleBestParameterRepairsLastCycle,
            LeaseReclaimsLastCycle = typedState.LeaseReclaimsLastCycle,
            OrphanedStaleRunningRunsLastCycle = typedState.OrphanedStaleRunningRunsLastCycle,
            ConfigCacheAgeSeconds = typedState.ConfigCacheAgeSeconds,
            ConfigRefreshDueAtUtc = typedState.ConfigRefreshDueAtUtc,
            ConfigRefreshIntervalSeconds = typedState.ConfigRefreshIntervalSeconds,
            LastSuccessfulConfigRefreshAtUtc = typedState.LastSuccessfulConfigRefreshAtUtc,
            IsConfigLoadDegraded = typedState.IsConfigLoadDegraded,
            ConsecutiveConfigLoadFailures = typedState.ConsecutiveConfigLoadFailures,
            LastConfigLoadFailureAtUtc = typedState.LastConfigLoadFailureAtUtc,
            LastConfigLoadFailureMessage = typedState.LastConfigLoadFailureMessage,
            LastLifecycleReconciledAtUtc = typedState.LastLifecycleReconciledAtUtc,
            OldestRunningRunId = typedState.OldestRunningRunId,
            OldestRunningStage = typedState.OldestRunningStage,
            OldestRunningStageMessage = typedState.OldestRunningStageMessage,
            OldestRunningStageUpdatedAt = typedState.OldestRunningStageUpdatedAt,
            OldestStrandedLifecycleRunId = typedState.OldestStrandedLifecycleRunId,
            OldestStrandedLifecycleStatus = typedState.OldestStrandedLifecycleStatus,
            OldestStrandedLifecycleAnchorAtUtc = typedState.OldestStrandedLifecycleAnchorAtUtc,
            PhaseStates = phaseStates
                .OrderBy(x => x.PhaseName, StringComparer.Ordinal)
                .Select(MapPhaseState)
                .ToArray(),
        };

        return Task.FromResult(ResponseData<OptimizationWorkerHealthDto>.Init(dto, true, "Successful", "00"));
    }

    private static OptimizationWorkerHealthSnapshotDto? MapSnapshot(WorkerHealthSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        return new OptimizationWorkerHealthSnapshotDto
        {
            WorkerName = snapshot.WorkerName,
            IsRunning = snapshot.IsRunning,
            LastSuccessAt = snapshot.LastSuccessAt,
            LastErrorAt = snapshot.LastErrorAt,
            LastErrorMessage = snapshot.LastErrorMessage,
            LastCycleDurationMs = snapshot.LastCycleDurationMs,
            CycleDurationP50Ms = snapshot.CycleDurationP50Ms,
            CycleDurationP95Ms = snapshot.CycleDurationP95Ms,
            CycleDurationP99Ms = snapshot.CycleDurationP99Ms,
            ConsecutiveFailures = snapshot.ConsecutiveFailures,
            ErrorsLastHour = snapshot.ErrorsLastHour,
            SuccessesLastHour = snapshot.SuccessesLastHour,
            BacklogDepth = snapshot.BacklogDepth,
            LastQueueLatencyMs = snapshot.LastQueueLatencyMs,
            QueueLatencyP50Ms = snapshot.QueueLatencyP50Ms,
            QueueLatencyP95Ms = snapshot.QueueLatencyP95Ms,
            LastExecutionDurationMs = snapshot.LastExecutionDurationMs,
            ExecutionDurationP50Ms = snapshot.ExecutionDurationP50Ms,
            ExecutionDurationP95Ms = snapshot.ExecutionDurationP95Ms,
            RetriesLastHour = snapshot.RetriesLastHour,
            RecoveriesLastHour = snapshot.RecoveriesLastHour,
            ConfiguredIntervalSeconds = snapshot.ConfiguredIntervalSeconds,
        };
    }

    private static OptimizationWorkerPhaseHealthDto MapPhaseState(OptimizationWorkerPhaseStateSnapshot snapshot)
    {
        return new OptimizationWorkerPhaseHealthDto
        {
            PhaseName = snapshot.PhaseName,
            LastStartedAtUtc = snapshot.LastStartedAtUtc,
            LastCompletedAtUtc = snapshot.LastCompletedAtUtc,
            LastSuccessAtUtc = snapshot.LastSuccessAtUtc,
            LastFailureAtUtc = snapshot.LastFailureAtUtc,
            LastFailureType = snapshot.LastFailureType,
            LastFailureMessage = snapshot.LastFailureMessage,
            ConsecutiveFailures = snapshot.ConsecutiveFailures,
            LastDurationMs = snapshot.LastDurationMs,
            LastSuccessDurationMs = snapshot.LastSuccessDurationMs,
            SuccessesLastHour = snapshot.SuccessesLastHour,
            FailuresLastHour = snapshot.FailuresLastHour,
            IsDegraded = snapshot.IsDegraded,
            BackoffUntilUtc = snapshot.BackoffUntilUtc,
            LastSkippedAtUtc = snapshot.LastSkippedAtUtc,
            LastSkipReason = snapshot.LastSkipReason,
            SkippedExecutionsLastHour = snapshot.SkippedExecutionsLastHour,
        };
    }
}
