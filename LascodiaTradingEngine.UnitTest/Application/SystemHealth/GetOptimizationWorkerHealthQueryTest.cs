using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
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
                WorkerName = "OptimizationWorker",
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
                WorkerName = "OptimizationCompletionReplayWorker",
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
            ConfigRefreshIntervalSeconds = 30,
            ConfigRefreshDueAtUtc = DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(),
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
        Assert.Equal(30, response.data.ConfigRefreshIntervalSeconds);
        Assert.Equal(DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(), response.data.ConfigRefreshDueAtUtc);
        Assert.Equal(DateTime.Parse("2026-04-06T10:14:30Z").ToUniversalTime(), response.data.LastLifecycleReconciledAtUtc);
        Assert.Equal(99, response.data.OldestRunningRunId);
        Assert.Equal(OptimizationExecutionStage.Validation, response.data.OldestRunningStage);
        Assert.Equal("Applying validation gates.", response.data.OldestRunningStageMessage);
        Assert.Equal(DateTime.Parse("2026-04-06T10:10:00Z").ToUniversalTime(), response.data.OldestRunningStageUpdatedAt);
        Assert.Equal(44, response.data.OldestStrandedLifecycleRunId);
        Assert.Equal(OptimizationRunStatus.Approved, response.data.OldestStrandedLifecycleStatus);
        Assert.Equal(DateTime.Parse("2026-04-06T09:55:00Z").ToUniversalTime(), response.data.OldestStrandedLifecycleAnchorAtUtc);
        Assert.NotNull(response.data.OptimizationWorker);
        Assert.Equal("OptimizationWorker", response.data.OptimizationWorker!.WorkerName);
        Assert.Equal(250, response.data.OptimizationWorker.LastCycleDurationMs);
        Assert.NotNull(response.data.CompletionReplayWorker);
        Assert.Equal("OptimizationCompletionReplayWorker", response.data.CompletionReplayWorker!.WorkerName);
        Assert.Equal(90, response.data.CompletionReplayWorker.LastCycleDurationMs);
    }
}
