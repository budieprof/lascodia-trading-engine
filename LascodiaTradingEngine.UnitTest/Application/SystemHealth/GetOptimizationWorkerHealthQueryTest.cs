using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.SystemHealth.Queries.GetOptimizationWorkerHealth;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.SystemHealth;

public class GetOptimizationWorkerHealthQueryTest
{
    [Fact]
    public async Task Handle_MapsOptimizationWorkerMetadataIntoDedicatedDto()
    {
        var snapshots = new List<WorkerHealthSnapshot>
        {
            new()
            {
                WorkerName = OptimizationWorkerHealthNames.CoordinatorWorker,
                IsRunning = true,
                LastCycleDurationMs = 250,
                CycleDurationP50Ms = 200,
                CycleDurationP95Ms = 400,
                CycleDurationP99Ms = 500,
                ConsecutiveFailures = 1,
                ErrorsLastHour = 2,
                SuccessesLastHour = 7,
                BacklogDepth = 3,
                ConfiguredIntervalSeconds = 30,
            },
            new()
            {
                WorkerName = OptimizationWorkerHealthNames.ExecutionWorker,
                IsRunning = true,
                LastSuccessAt = DateTime.Parse("2026-04-06T10:14:30Z").ToUniversalTime(),
                LastCycleDurationMs = 0,
                BacklogDepth = 0,
                ConfiguredIntervalSeconds = 30,
            },
            new()
            {
                WorkerName = OptimizationWorkerHealthNames.CompletionReplayWorker,
                IsRunning = true,
                LastCycleDurationMs = 90,
                BacklogDepth = 2,
                ConfiguredIntervalSeconds = 30
            }
        };

        var healthMonitor = new Mock<IWorkerHealthMonitor>();
        healthMonitor.Setup(x => x.GetCurrentSnapshots()).Returns(snapshots);
        var optimizationHealthStore = new Mock<IOptimizationWorkerHealthStore>();
        optimizationHealthStore.Setup(x => x.GetMainWorkerState()).Returns(new OptimizationWorkerHealthStateSnapshot
        {
            ActiveProcessingSlots = 2,
            ConfiguredMaxConcurrentRuns = 3,
            ProcessingSlotFailuresLastHour = 1,
            LastProcessingSlotFailureAtUtc = DateTime.Parse("2026-04-06T10:12:00Z").ToUniversalTime(),
            LastProcessingSlotFailureMessage = "processing slot failed",
            QueueWaitP50Ms = 1_500,
            QueueWaitP95Ms = 9_000,
            QueueWaitP99Ms = 12_000,
            OldestQueuedRunId = 77,
            OldestQueuedAtUtc = DateTime.Parse("2026-04-06T10:00:00Z").ToUniversalTime(),
            OldestQueuedAgeSeconds = 840,
            QueuedRuns = 4,
            RunningRuns = 2,
            RetryableFailedRuns = 1,
            AbandonedRuns = 1,
            PendingFollowUps = 5,
            PendingCompletionPublications = 2,
            ApprovedRunsMissingFollowUps = 1,
            PendingCompletionPreparation = 3,
            StrandedLifecycleRuns = 2,
            LifecycleRepairsLastCycle = 4,
            LifecycleBatchesLastCycle = 2,
            ConfigCacheAgeSeconds = 12,
            ConfigRefreshIntervalSeconds = 60,
            ConfigRefreshDueAtUtc = DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(),
            LastSuccessfulConfigRefreshAtUtc = DateTime.Parse("2026-04-06T10:14:00Z").ToUniversalTime(),
            IsConfigLoadDegraded = true,
            ConsecutiveConfigLoadFailures = 2,
            LastConfigLoadFailureAtUtc = DateTime.Parse("2026-04-06T10:14:45Z").ToUniversalTime(),
            LastConfigLoadFailureMessage = "configuration load failed",
            LastLifecycleReconciledAtUtc = DateTime.Parse("2026-04-06T10:14:30Z").ToUniversalTime(),
            OldestRunningRunId = 99,
            OldestRunningStage = OptimizationExecutionStage.Validation,
            OldestRunningStageMessage = "Applying validation gates.",
            OldestRunningStageUpdatedAt = DateTime.Parse("2026-04-06T10:10:00Z").ToUniversalTime(),
            OldestStrandedLifecycleRunId = 44,
            OldestStrandedLifecycleStatus = OptimizationRunStatus.Approved,
            OldestStrandedLifecycleAnchorAtUtc = DateTime.Parse("2026-04-06T09:55:00Z").ToUniversalTime()
        });

        var handler = new GetOptimizationWorkerHealthQueryHandler(healthMonitor.Object, optimizationHealthStore.Object);

        var response = await handler.Handle(new GetOptimizationWorkerHealthQuery(), CancellationToken.None);

        Assert.True(response.status);
        Assert.NotNull(response.data);
        Assert.NotNull(response.data!.CoordinatorWorker);
        Assert.Equal(OptimizationWorkerHealthNames.CoordinatorWorker, response.data.CoordinatorWorker!.WorkerName);
        Assert.Equal(2, response.data.ActiveProcessingSlots);
        Assert.Equal(3, response.data.ConfiguredMaxConcurrentRuns);
        Assert.Equal(1, response.data.ProcessingSlotFailuresLastHour);
        Assert.Equal(DateTime.Parse("2026-04-06T10:12:00Z").ToUniversalTime(), response.data.LastProcessingSlotFailureAtUtc);
        Assert.Equal("processing slot failed", response.data.LastProcessingSlotFailureMessage);
        Assert.Equal(1_500, response.data.QueueWaitP50Ms);
        Assert.Equal(9_000, response.data.QueueWaitP95Ms);
        Assert.Equal(12_000, response.data.QueueWaitP99Ms);
        Assert.Equal(77, response.data.OldestQueuedRunId);
        Assert.Equal(DateTime.Parse("2026-04-06T10:00:00Z").ToUniversalTime(), response.data.OldestQueuedAtUtc);
        Assert.Equal(840, response.data.OldestQueuedAgeSeconds);
        Assert.Equal(4, response.data!.QueuedRuns);
        Assert.Equal(2, response.data.RunningRuns);
        Assert.Equal(1, response.data.RetryableFailedRuns);
        Assert.Equal(1, response.data.AbandonedRuns);
        Assert.Equal(5, response.data.PendingFollowUps);
        Assert.Equal(2, response.data.PendingCompletionPublications);
        Assert.Equal(1, response.data.ApprovedRunsMissingFollowUps);
        Assert.Equal(3, response.data.PendingCompletionPreparation);
        Assert.Equal(2, response.data.StrandedLifecycleRuns);
        Assert.Equal(4, response.data.LifecycleRepairsLastCycle);
        Assert.Equal(2, response.data.LifecycleBatchesLastCycle);
        Assert.Equal(12, response.data.ConfigCacheAgeSeconds);
        Assert.Equal(60, response.data.ConfigRefreshIntervalSeconds);
        Assert.Equal(DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(), response.data.ConfigRefreshDueAtUtc);
        Assert.Equal(DateTime.Parse("2026-04-06T10:14:00Z").ToUniversalTime(), response.data.LastSuccessfulConfigRefreshAtUtc);
        Assert.True(response.data.IsConfigLoadDegraded);
        Assert.Equal(2, response.data.ConsecutiveConfigLoadFailures);
        Assert.Equal(DateTime.Parse("2026-04-06T10:14:45Z").ToUniversalTime(), response.data.LastConfigLoadFailureAtUtc);
        Assert.Equal("configuration load failed", response.data.LastConfigLoadFailureMessage);
        Assert.Equal(DateTime.Parse("2026-04-06T10:14:30Z").ToUniversalTime(), response.data.LastLifecycleReconciledAtUtc);
        Assert.Equal(99, response.data.OldestRunningRunId);
        Assert.Equal(OptimizationExecutionStage.Validation, response.data.OldestRunningStage);
        Assert.Equal("Applying validation gates.", response.data.OldestRunningStageMessage);
        Assert.Equal(DateTime.Parse("2026-04-06T10:10:00Z").ToUniversalTime(), response.data.OldestRunningStageUpdatedAt);
        Assert.Equal(44, response.data.OldestStrandedLifecycleRunId);
        Assert.Equal(OptimizationRunStatus.Approved, response.data.OldestStrandedLifecycleStatus);
        Assert.Equal(DateTime.Parse("2026-04-06T09:55:00Z").ToUniversalTime(), response.data.OldestStrandedLifecycleAnchorAtUtc);
        Assert.NotNull(response.data.OptimizationWorker);
        Assert.Equal(OptimizationWorkerHealthNames.ExecutionWorker, response.data.OptimizationWorker!.WorkerName);
        Assert.Equal(0, response.data.OptimizationWorker.LastCycleDurationMs);
        Assert.NotNull(response.data.CompletionReplayWorker);
        Assert.Equal(OptimizationWorkerHealthNames.CompletionReplayWorker, response.data.CompletionReplayWorker!.WorkerName);
        Assert.Equal(90, response.data.CompletionReplayWorker.LastCycleDurationMs);
    }
}
