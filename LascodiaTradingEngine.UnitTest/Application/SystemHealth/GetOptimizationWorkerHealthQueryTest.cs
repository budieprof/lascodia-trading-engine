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
                LastQueueLatencyMs = 2500,
                QueueLatencyP50Ms = 2000,
                QueueLatencyP95Ms = 9000,
                LastExecutionDurationMs = 1400,
                ExecutionDurationP50Ms = 1200,
                ExecutionDurationP95Ms = 2500,
                RetriesLastHour = 2,
                RecoveriesLastHour = 1,
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
            DeferredQueuedRuns = 2,
            OldestDeferredQueuedRunId = 78,
            OldestDeferredQueuedAtUtc = DateTime.Parse("2026-04-06T09:58:00Z").ToUniversalTime(),
            OldestDeferredUntilUtc = DateTime.Parse("2026-04-06T10:30:00Z").ToUniversalTime(),
            OldestDeferredQueuedAgeSeconds = 960,
            MostDeferredQueuedRunId = 78,
            MostDeferredQueuedDeferralCount = 4,
            MostRecentDeferredResumeRunId = 80,
            MostRecentDeferredResumeAtUtc = DateTime.Parse("2026-04-06T10:13:30Z").ToUniversalTime(),
            DeferredRunsStartedLastHour = 3,
            DeferredRunsResumedLastHour = 2,
            RepeatedlyDeferredQueuedRuns = 1,
            OldestActiveDeferralRunId = 81,
            OldestActiveDeferralAtUtc = DateTime.Parse("2026-04-06T09:40:00Z").ToUniversalTime(),
            OldestActiveDeferralAgeSeconds = 2_100,
            DeferredQueuedRunsByReason =
            [
                new OptimizationDeferralReasonCountSnapshot(OptimizationDeferralReason.SeasonalBlackout, 1),
                new OptimizationDeferralReasonCountSnapshot(OptimizationDeferralReason.DataQuality, 1)
            ],
            StarvedQueuedRuns = 1,
            OldestStarvedQueuedRunId = 79,
            OldestStarvedQueuedAtUtc = DateTime.Parse("2026-04-05T08:00:00Z").ToUniversalTime(),
            OldestStarvedQueuedAgeSeconds = 97_200,
            RunningRuns = 2,
            ActiveLeasedRunningRuns = 1,
            StaleRunningRuns = 1,
            LeaseMissingRunningRuns = 0,
            RetryableFailedRuns = 1,
            AbandonedRuns = 1,
            PendingFollowUps = 5,
            PendingCompletionPublications = 2,
            ApprovedRunsMissingFollowUps = 1,
            PendingCompletionPreparation = 3,
            StrandedLifecycleRuns = 2,
            LifecycleRepairsLastCycle = 4,
            LifecycleBatchesLastCycle = 2,
            LifecycleMissingCompletionPayloadRepairsLastCycle = 1,
            LifecycleMalformedCompletionPayloadRepairsLastCycle = 1,
            LifecycleFollowUpRepairsLastCycle = 2,
            LifecycleConfigSnapshotRepairsLastCycle = 1,
            LifecycleBestParameterRepairsLastCycle = 0,
            LeaseReclaimsLastCycle = 2,
            OrphanedStaleRunningRunsLastCycle = 1,
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
        optimizationHealthStore.Setup(x => x.GetPhaseStates()).Returns(
        [
            new OptimizationWorkerPhaseStateSnapshot
            {
                PhaseName = OptimizationWorkerHealthNames.Phases.StaleRunningRecovery,
                LastStartedAtUtc = DateTime.Parse("2026-04-06T10:14:05Z").ToUniversalTime(),
                LastCompletedAtUtc = DateTime.Parse("2026-04-06T10:14:10Z").ToUniversalTime(),
                LastSuccessAtUtc = DateTime.Parse("2026-04-06T10:14:10Z").ToUniversalTime(),
                LastDurationMs = 18,
                LastSuccessDurationMs = 18,
                SuccessesLastHour = 5,
                FailuresLastHour = 0
            },
            new OptimizationWorkerPhaseStateSnapshot
            {
                PhaseName = OptimizationWorkerHealthNames.Phases.AutoScheduling,
                LastStartedAtUtc = DateTime.Parse("2026-04-06T10:14:18Z").ToUniversalTime(),
                LastCompletedAtUtc = DateTime.Parse("2026-04-06T10:14:20Z").ToUniversalTime(),
                LastFailureAtUtc = DateTime.Parse("2026-04-06T10:14:20Z").ToUniversalTime(),
                LastFailureType = "InvalidOperationException",
                LastFailureMessage = "schedule failed",
                ConsecutiveFailures = 2,
                LastDurationMs = 45,
                SuccessesLastHour = 1,
                FailuresLastHour = 2,
                IsDegraded = true,
                BackoffUntilUtc = DateTime.Parse("2026-04-06T10:20:00Z").ToUniversalTime(),
                LastSkippedAtUtc = DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(),
                LastSkipReason = "phase degraded due to repeated scheduling failures",
                SkippedExecutionsLastHour = 3
            }
        ]);

        var handler = new GetOptimizationWorkerHealthQueryHandler(healthMonitor.Object, optimizationHealthStore.Object);

        var response = await handler.Handle(new GetOptimizationWorkerHealthQuery(), CancellationToken.None);

        Assert.True(response.status);
        Assert.NotNull(response.data);
        Assert.NotNull(response.data!.CoordinatorWorker);
        Assert.Equal(OptimizationWorkerHealthNames.CoordinatorWorker, response.data.CoordinatorWorker!.WorkerName);
        Assert.Equal(2500, response.data.CoordinatorWorker.LastQueueLatencyMs);
        Assert.Equal(2000, response.data.CoordinatorWorker.QueueLatencyP50Ms);
        Assert.Equal(9000, response.data.CoordinatorWorker.QueueLatencyP95Ms);
        Assert.Equal(1400, response.data.CoordinatorWorker.LastExecutionDurationMs);
        Assert.Equal(1200, response.data.CoordinatorWorker.ExecutionDurationP50Ms);
        Assert.Equal(2500, response.data.CoordinatorWorker.ExecutionDurationP95Ms);
        Assert.Equal(2, response.data.CoordinatorWorker.RetriesLastHour);
        Assert.Equal(1, response.data.CoordinatorWorker.RecoveriesLastHour);
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
        Assert.Equal(2, response.data.DeferredQueuedRuns);
        Assert.Equal(78, response.data.OldestDeferredQueuedRunId);
        Assert.Equal(DateTime.Parse("2026-04-06T09:58:00Z").ToUniversalTime(), response.data.OldestDeferredQueuedAtUtc);
        Assert.Equal(DateTime.Parse("2026-04-06T10:30:00Z").ToUniversalTime(), response.data.OldestDeferredUntilUtc);
        Assert.Equal(960, response.data.OldestDeferredQueuedAgeSeconds);
        Assert.Equal(78, response.data.MostDeferredQueuedRunId);
        Assert.Equal(4, response.data.MostDeferredQueuedDeferralCount);
        Assert.Equal(80, response.data.MostRecentDeferredResumeRunId);
        Assert.Equal(DateTime.Parse("2026-04-06T10:13:30Z").ToUniversalTime(), response.data.MostRecentDeferredResumeAtUtc);
        Assert.Equal(3, response.data.DeferredRunsStartedLastHour);
        Assert.Equal(2, response.data.DeferredRunsResumedLastHour);
        Assert.Equal(1, response.data.RepeatedlyDeferredQueuedRuns);
        Assert.Equal(81, response.data.OldestActiveDeferralRunId);
        Assert.Equal(DateTime.Parse("2026-04-06T09:40:00Z").ToUniversalTime(), response.data.OldestActiveDeferralAtUtc);
        Assert.Equal(2_100, response.data.OldestActiveDeferralAgeSeconds);
        Assert.Collection(
            response.data.DeferredQueuedRunsByReason,
            item =>
            {
                Assert.Equal(OptimizationDeferralReason.SeasonalBlackout, item.Reason);
                Assert.Equal(1, item.Count);
            },
            item =>
            {
                Assert.Equal(OptimizationDeferralReason.DataQuality, item.Reason);
                Assert.Equal(1, item.Count);
            });
        Assert.Equal(1, response.data.StarvedQueuedRuns);
        Assert.Equal(79, response.data.OldestStarvedQueuedRunId);
        Assert.Equal(DateTime.Parse("2026-04-05T08:00:00Z").ToUniversalTime(), response.data.OldestStarvedQueuedAtUtc);
        Assert.Equal(97_200, response.data.OldestStarvedQueuedAgeSeconds);
        Assert.Equal(2, response.data.RunningRuns);
        Assert.Equal(1, response.data.ActiveLeasedRunningRuns);
        Assert.Equal(1, response.data.StaleRunningRuns);
        Assert.Equal(0, response.data.LeaseMissingRunningRuns);
        Assert.Equal(1, response.data.RetryableFailedRuns);
        Assert.Equal(1, response.data.AbandonedRuns);
        Assert.Equal(5, response.data.PendingFollowUps);
        Assert.Equal(2, response.data.PendingCompletionPublications);
        Assert.Equal(1, response.data.ApprovedRunsMissingFollowUps);
        Assert.Equal(3, response.data.PendingCompletionPreparation);
        Assert.Equal(2, response.data.StrandedLifecycleRuns);
        Assert.Equal(4, response.data.LifecycleRepairsLastCycle);
        Assert.Equal(2, response.data.LifecycleBatchesLastCycle);
        Assert.Equal(1, response.data.LifecycleMissingCompletionPayloadRepairsLastCycle);
        Assert.Equal(1, response.data.LifecycleMalformedCompletionPayloadRepairsLastCycle);
        Assert.Equal(2, response.data.LifecycleFollowUpRepairsLastCycle);
        Assert.Equal(1, response.data.LifecycleConfigSnapshotRepairsLastCycle);
        Assert.Equal(0, response.data.LifecycleBestParameterRepairsLastCycle);
        Assert.Equal(2, response.data.LeaseReclaimsLastCycle);
        Assert.Equal(1, response.data.OrphanedStaleRunningRunsLastCycle);
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
        Assert.Collection(
            response.data.PhaseStates,
            phase =>
            {
                Assert.Equal(OptimizationWorkerHealthNames.Phases.AutoScheduling, phase.PhaseName);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:18Z").ToUniversalTime(), phase.LastStartedAtUtc);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:20Z").ToUniversalTime(), phase.LastCompletedAtUtc);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:20Z").ToUniversalTime(), phase.LastFailureAtUtc);
                Assert.Equal("InvalidOperationException", phase.LastFailureType);
                Assert.Equal("schedule failed", phase.LastFailureMessage);
                Assert.Equal(2, phase.ConsecutiveFailures);
                Assert.Equal(1, phase.SuccessesLastHour);
                Assert.Equal(2, phase.FailuresLastHour);
                Assert.True(phase.IsDegraded);
                Assert.Equal(DateTime.Parse("2026-04-06T10:20:00Z").ToUniversalTime(), phase.BackoffUntilUtc);
                Assert.Equal(DateTime.Parse("2026-04-06T10:15:00Z").ToUniversalTime(), phase.LastSkippedAtUtc);
                Assert.Equal("phase degraded due to repeated scheduling failures", phase.LastSkipReason);
                Assert.Equal(3, phase.SkippedExecutionsLastHour);
            },
            phase =>
            {
                Assert.Equal(OptimizationWorkerHealthNames.Phases.StaleRunningRecovery, phase.PhaseName);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:05Z").ToUniversalTime(), phase.LastStartedAtUtc);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:10Z").ToUniversalTime(), phase.LastCompletedAtUtc);
                Assert.Equal(DateTime.Parse("2026-04-06T10:14:10Z").ToUniversalTime(), phase.LastSuccessAtUtc);
                Assert.Equal(18, phase.LastDurationMs);
                Assert.Equal(18, phase.LastSuccessDurationMs);
                Assert.Equal(5, phase.SuccessesLastHour);
                Assert.Equal(0, phase.FailuresLastHour);
                Assert.False(phase.IsDegraded);
                Assert.Null(phase.BackoffUntilUtc);
                Assert.Null(phase.LastSkippedAtUtc);
                Assert.Null(phase.LastSkipReason);
                Assert.Equal(0, phase.SkippedExecutionsLastHour);
            });
    }

    [Fact]
    public async Task Handle_DoesNotBackfillExecutionWorkerFromCoordinatorSnapshot()
    {
        var healthMonitor = new Mock<IWorkerHealthMonitor>();
        healthMonitor.Setup(x => x.GetCurrentSnapshots()).Returns(
        [
            new WorkerHealthSnapshot
            {
                WorkerName = OptimizationWorkerHealthNames.CoordinatorWorker,
                IsRunning = true,
                LastCycleDurationMs = 250
            }
        ]);

        var optimizationHealthStore = new Mock<IOptimizationWorkerHealthStore>();
        optimizationHealthStore.Setup(x => x.GetMainWorkerState()).Returns(new OptimizationWorkerHealthStateSnapshot());
        optimizationHealthStore.Setup(x => x.GetPhaseStates()).Returns([]);

        var handler = new GetOptimizationWorkerHealthQueryHandler(healthMonitor.Object, optimizationHealthStore.Object);

        var response = await handler.Handle(new GetOptimizationWorkerHealthQuery(), CancellationToken.None);

        Assert.True(response.status);
        Assert.NotNull(response.data);
        Assert.NotNull(response.data!.CoordinatorWorker);
        Assert.Null(response.data.OptimizationWorker);
    }
}
