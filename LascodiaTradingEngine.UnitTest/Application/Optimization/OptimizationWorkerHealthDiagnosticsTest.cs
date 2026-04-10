using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class OptimizationWorkerHealthDiagnosticsTest
{
    [Fact]
    public void GetQueueWaitPercentiles_UsesNearestRankForSmallSamples()
    {
        var store = new OptimizationWorkerHealthStore();

        store.RecordQueueWaitSample(100);
        store.RecordQueueWaitSample(200);

        var percentiles = store.GetQueueWaitPercentiles();

        Assert.Equal(100, percentiles.P50Ms);
        Assert.Equal(200, percentiles.P95Ms);
        Assert.Equal(200, percentiles.P99Ms);
    }

    [Fact]
    public void PhaseFailureBackoff_TracksSkipAndRecovery()
    {
        var store = new OptimizationWorkerHealthStore();
        var nowUtc = new DateTime(2026, 4, 9, 12, 0, 0, DateTimeKind.Utc);

        store.RecordPhaseFailure(OptimizationWorkerHealthNames.Phases.AutoScheduling, "InvalidOperationException", "boom-1", 15, nowUtc);
        store.RecordPhaseFailure(OptimizationWorkerHealthNames.Phases.AutoScheduling, "InvalidOperationException", "boom-2", 18, nowUtc.AddSeconds(30));
        store.RecordPhaseFailure(OptimizationWorkerHealthNames.Phases.AutoScheduling, "InvalidOperationException", "boom-3", 20, nowUtc.AddSeconds(60));

        var decision = store.GetPhaseExecutionDecision(
            OptimizationWorkerHealthNames.Phases.AutoScheduling,
            nowUtc.AddSeconds(75));

        Assert.False(decision.ShouldExecute);
        Assert.NotNull(decision.BackoffUntilUtc);

        store.RecordPhaseSkipped(
            OptimizationWorkerHealthNames.Phases.AutoScheduling,
            decision.Reason ?? "phase degraded",
            decision.BackoffUntilUtc,
            nowUtc.AddSeconds(80));

        var degradedPhase = Assert.Single(store.GetPhaseStates());
        Assert.True(degradedPhase.IsDegraded);
        Assert.Equal(3, degradedPhase.ConsecutiveFailures);
        Assert.Equal(nowUtc.AddSeconds(80), degradedPhase.LastSkippedAtUtc);
        Assert.Equal(1, degradedPhase.SkippedExecutionsLastHour);
        Assert.Equal(decision.BackoffUntilUtc, degradedPhase.BackoffUntilUtc);

        store.RecordPhaseSuccess(
            OptimizationWorkerHealthNames.Phases.AutoScheduling,
            22,
            decision.BackoffUntilUtc!.Value.AddSeconds(1));

        var recoveredPhase = Assert.Single(store.GetPhaseStates());
        Assert.False(recoveredPhase.IsDegraded);
        Assert.Equal(0, recoveredPhase.ConsecutiveFailures);
        Assert.Null(recoveredPhase.BackoffUntilUtc);
        Assert.Null(recoveredPhase.LastSkipReason);
    }

    [Fact]
    public async Task RecordAsync_ExcludesDeferredQueuedRunsFromBacklogAndOldestQueued()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var deferredQueuedAt = nowUtc.UtcDateTime.AddDays(-3);
        var eligibleQueuedAt = nowUtc.UtcDateTime.AddHours(-2);
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 7001,
                StrategyId = 81,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = deferredQueuedAt,
                StartedAt = deferredQueuedAt,
                DeferralReason = OptimizationDeferralReason.SeasonalBlackout,
                DeferredAtUtc = nowUtc.UtcDateTime.AddHours(-12),
                DeferredUntilUtc = nowUtc.UtcDateTime.AddHours(6),
                DeferralCount = 3,
                IsDeleted = false
            },
            new()
            {
                Id = 7002,
                StrategyId = 82,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = eligibleQueuedAt,
                StartedAt = nowUtc.UtcDateTime.AddHours(-3),
                IsDeleted = false
            }
        };

        var recorder = CreateRecorder(runs, nowUtc, out var store);

        await recorder.RecordAsync(
            CreateOptimizationConfig(maxRetryAttempts: 2),
            nowUtc.UtcDateTime.AddSeconds(-30),
            nowUtc.UtcDateTime.AddSeconds(30),
            staleRunningSummary: null,
            reconciliationSummary: null,
            CancellationToken.None);

        var snapshot = store.GetMainWorkerState();
        Assert.Equal(1, snapshot.QueuedRuns);
        Assert.Equal(1, snapshot.DeferredQueuedRuns);
        Assert.Equal(7002, snapshot.OldestQueuedRunId);
        Assert.Equal(eligibleQueuedAt, snapshot.OldestQueuedAtUtc);
        Assert.Equal((int)(nowUtc.UtcDateTime - eligibleQueuedAt).TotalSeconds, snapshot.OldestQueuedAgeSeconds);
        Assert.Equal(7001, snapshot.OldestDeferredQueuedRunId);
        Assert.Equal(deferredQueuedAt, snapshot.OldestDeferredQueuedAtUtc);
        Assert.Equal(nowUtc.UtcDateTime.AddHours(6), snapshot.OldestDeferredUntilUtc);
        Assert.Equal((int)(nowUtc.UtcDateTime - deferredQueuedAt).TotalSeconds, snapshot.OldestDeferredQueuedAgeSeconds);
        Assert.Equal(7001, snapshot.MostDeferredQueuedRunId);
        Assert.Equal(3, snapshot.MostDeferredQueuedDeferralCount);
        var reasonCount = Assert.Single(snapshot.DeferredQueuedRunsByReason);
        Assert.Equal(OptimizationDeferralReason.SeasonalBlackout, reasonCount.Reason);
        Assert.Equal(1, reasonCount.Count);
    }

    [Fact]
    public async Task RecordAsync_TracksDeferredChurnMetrics()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var oldestDeferredAtUtc = nowUtc.UtcDateTime.AddHours(-2);
        var recentDeferredAtUtc = nowUtc.UtcDateTime.AddMinutes(-20);
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 7051,
                StrategyId = 85,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = nowUtc.UtcDateTime.AddHours(-3),
                StartedAt = nowUtc.UtcDateTime.AddHours(-3),
                DeferralReason = OptimizationDeferralReason.SeasonalBlackout,
                DeferredAtUtc = oldestDeferredAtUtc,
                DeferredUntilUtc = nowUtc.UtcDateTime.AddHours(2),
                DeferralCount = 1,
                IsDeleted = false
            },
            new()
            {
                Id = 7052,
                StrategyId = 86,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = nowUtc.UtcDateTime.AddHours(-1),
                StartedAt = nowUtc.UtcDateTime.AddHours(-1),
                DeferralReason = OptimizationDeferralReason.DataQuality,
                DeferredAtUtc = recentDeferredAtUtc,
                DeferredUntilUtc = nowUtc.UtcDateTime.AddHours(1),
                DeferralCount = 3,
                IsDeleted = false
            },
            new()
            {
                Id = 7053,
                StrategyId = 87,
                Status = OptimizationRunStatus.Completed,
                LastResumedAtUtc = nowUtc.UtcDateTime.AddMinutes(-10),
                IsDeleted = false
            }
        };

        var recorder = CreateRecorder(runs, nowUtc, out var store);

        await recorder.RecordAsync(
            CreateOptimizationConfig(maxRetryAttempts: 2),
            nowUtc.UtcDateTime.AddSeconds(-30),
            nowUtc.UtcDateTime.AddSeconds(30),
            staleRunningSummary: null,
            reconciliationSummary: null,
            CancellationToken.None);

        var snapshot = store.GetMainWorkerState();
        Assert.Equal(1, snapshot.DeferredRunsStartedLastHour);
        Assert.Equal(1, snapshot.DeferredRunsResumedLastHour);
        Assert.Equal(1, snapshot.RepeatedlyDeferredQueuedRuns);
        Assert.Equal(7051, snapshot.OldestActiveDeferralRunId);
        Assert.Equal(oldestDeferredAtUtc, snapshot.OldestActiveDeferralAtUtc);
        Assert.Equal((int)(nowUtc.UtcDateTime - oldestDeferredAtUtc).TotalSeconds, snapshot.OldestActiveDeferralAgeSeconds);
    }

    [Fact]
    public async Task RecordAsync_CountsOnlyRunsReadyForRetry()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 7101,
                StrategyId = 91,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.Transient,
                CompletedAt = nowUtc.UtcDateTime.AddMinutes(-20),
                IsDeleted = false
            },
            new()
            {
                Id = 7102,
                StrategyId = 92,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.ConfigError,
                CompletedAt = nowUtc.UtcDateTime.AddMinutes(-20),
                IsDeleted = false
            },
            new()
            {
                Id = 7103,
                StrategyId = 93,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 0,
                FailureCategory = OptimizationFailureCategory.Transient,
                CompletedAt = nowUtc.UtcDateTime.AddMinutes(-5),
                IsDeleted = false
            },
            new()
            {
                Id = 7104,
                StrategyId = 94,
                Status = OptimizationRunStatus.Failed,
                RetryCount = 1,
                FailureCategory = OptimizationFailureCategory.Transient,
                CompletedAt = nowUtc.UtcDateTime.AddMinutes(-40),
                IsDeleted = false
            },
            new()
            {
                Id = 7105,
                StrategyId = 94,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = nowUtc.UtcDateTime.AddMinutes(-1),
                StartedAt = nowUtc.UtcDateTime.AddMinutes(-1),
                IsDeleted = false
            }
        };

        var recorder = CreateRecorder(runs, nowUtc, out var store);

        await recorder.RecordAsync(
            CreateOptimizationConfig(maxRetryAttempts: 2),
            nowUtc.UtcDateTime.AddSeconds(-30),
            nowUtc.UtcDateTime.AddSeconds(30),
            staleRunningSummary: null,
            reconciliationSummary: null,
            CancellationToken.None);

        var snapshot = store.GetMainWorkerState();
        Assert.Equal(1, snapshot.RetryableFailedRuns);
    }

    [Fact]
    public async Task RecordAsync_TracksStarvedEligibleQueuedRunsSeparatelyFromDeferredRuns()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var starvedQueuedAt = nowUtc.UtcDateTime.AddHours(-30);
        var deferredQueuedAt = nowUtc.UtcDateTime.AddHours(-40);
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 7201,
                StrategyId = 101,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = starvedQueuedAt,
                StartedAt = starvedQueuedAt,
                IsDeleted = false
            },
            new()
            {
                Id = 7202,
                StrategyId = 102,
                Status = OptimizationRunStatus.Queued,
                QueuedAt = deferredQueuedAt,
                StartedAt = deferredQueuedAt,
                DeferralReason = OptimizationDeferralReason.DataQuality,
                DeferredUntilUtc = nowUtc.UtcDateTime.AddHours(2),
                IsDeleted = false
            }
        };

        var recorder = CreateRecorder(runs, nowUtc, out var store);

        await recorder.RecordAsync(
            CreateOptimizationConfig(maxRetryAttempts: 2),
            nowUtc.UtcDateTime.AddSeconds(-30),
            nowUtc.UtcDateTime.AddSeconds(30),
            staleRunningSummary: null,
            reconciliationSummary: null,
            CancellationToken.None);

        var snapshot = store.GetMainWorkerState();
        Assert.Equal(1, snapshot.QueuedRuns);
        Assert.Equal(1, snapshot.DeferredQueuedRuns);
        Assert.Equal(1, snapshot.StarvedQueuedRuns);
        Assert.Equal(7201, snapshot.OldestStarvedQueuedRunId);
        Assert.Equal(starvedQueuedAt, snapshot.OldestStarvedQueuedAtUtc);
        Assert.Equal((int)(nowUtc.UtcDateTime - starvedQueuedAt).TotalSeconds, snapshot.OldestStarvedQueuedAgeSeconds);
    }

    [Fact]
    public async Task RecordAsync_SplitsRunningLeaseHealthAndTracksDeferredResumeMarkers()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 09, 12, 0, 0, TimeSpan.Zero);
        var runs = new List<OptimizationRun>
        {
            new()
            {
                Id = 7301,
                StrategyId = 111,
                Status = OptimizationRunStatus.Running,
                ExecutionLeaseToken = Guid.NewGuid(),
                ExecutionLeaseExpiresAt = nowUtc.UtcDateTime.AddMinutes(5),
                IsDeleted = false
            },
            new()
            {
                Id = 7302,
                StrategyId = 112,
                Status = OptimizationRunStatus.Running,
                ExecutionLeaseToken = Guid.NewGuid(),
                ExecutionLeaseExpiresAt = nowUtc.UtcDateTime.AddMinutes(-1),
                IsDeleted = false
            },
            new()
            {
                Id = 7303,
                StrategyId = 113,
                Status = OptimizationRunStatus.Running,
                ExecutionLeaseToken = null,
                ExecutionLeaseExpiresAt = null,
                IsDeleted = false
            },
            new()
            {
                Id = 7304,
                StrategyId = 114,
                Status = OptimizationRunStatus.Completed,
                LastResumedAtUtc = nowUtc.UtcDateTime.AddMinutes(-10),
                IsDeleted = false
            }
        };

        var recorder = CreateRecorder(runs, nowUtc, out var store);

        await recorder.RecordAsync(
            CreateOptimizationConfig(maxRetryAttempts: 2),
            nowUtc.UtcDateTime.AddSeconds(-30),
            nowUtc.UtcDateTime.AddSeconds(30),
            new OptimizationRunRecoveryCoordinator.StaleRunningRecoverySummary(
                RequeuedRuns: 2,
                OrphanedRuns: 1,
                ExecutedAtUtc: nowUtc.UtcDateTime.AddSeconds(-5)),
            new OptimizationRunRecoveryCoordinator.LifecycleReconciliationSummary(
                RepairedRuns: 4,
                BatchesProcessed: 1,
                MissingCompletionPayloadRepairs: 1,
                MalformedCompletionPayloadRepairs: 1,
                FollowUpRepairs: 1,
                ConfigSnapshotRepairs: 1,
                BestParameterRepairs: 0,
                StartedAtUtc: nowUtc.UtcDateTime.AddSeconds(-20),
                LastActivityAtUtc: nowUtc.UtcDateTime.AddSeconds(-3)),
            CancellationToken.None);

        var snapshot = store.GetMainWorkerState();
        Assert.Equal(3, snapshot.RunningRuns);
        Assert.Equal(1, snapshot.ActiveLeasedRunningRuns);
        Assert.Equal(1, snapshot.StaleRunningRuns);
        Assert.Equal(1, snapshot.LeaseMissingRunningRuns);
        Assert.Equal(2, snapshot.LeaseReclaimsLastCycle);
        Assert.Equal(1, snapshot.OrphanedStaleRunningRunsLastCycle);
        Assert.Equal(1, snapshot.LifecycleMissingCompletionPayloadRepairsLastCycle);
        Assert.Equal(1, snapshot.LifecycleMalformedCompletionPayloadRepairsLastCycle);
        Assert.Equal(1, snapshot.LifecycleFollowUpRepairsLastCycle);
        Assert.Equal(1, snapshot.LifecycleConfigSnapshotRepairsLastCycle);
        Assert.Equal(0, snapshot.LifecycleBestParameterRepairsLastCycle);
        Assert.Equal(7304, snapshot.MostRecentDeferredResumeRunId);
        Assert.Equal(nowUtc.UtcDateTime.AddMinutes(-10), snapshot.MostRecentDeferredResumeAtUtc);
    }

    private static OptimizationWorkerHealthRecorder CreateRecorder(
        List<OptimizationRun> runs,
        DateTimeOffset nowUtc,
        out OptimizationWorkerHealthStore store)
    {
        var optRunDbSet = runs.AsQueryable().BuildMockDbSet();
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);

        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(writeCtx.Object);
        var provider = services.BuildServiceProvider();

        store = new OptimizationWorkerHealthStore();
        return new OptimizationWorkerHealthRecorder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            store,
            Mock.Of<IWorkerHealthMonitor>(),
            new FixedTimeProvider(nowUtc));
    }

    private static OptimizationConfig CreateOptimizationConfig(int maxRetryAttempts)
    {
        return new OptimizationConfig
        {
            SchedulePollSeconds = 7200,
            CooldownDays = 14,
            RolloutObservationDays = 14,
            MaxQueuedPerCycle = 3,
            FollowUpMonitorBatchSize = 10,
            AutoScheduleEnabled = true,
            MaxRunsPerWeek = 20,
            MinWinRate = 0.60,
            MinProfitFactor = 1.0,
            MinTotalTrades = 10,
            AutoApprovalImprovementThreshold = 0.10m,
            AutoApprovalMinHealthScore = 0.55m,
            TopNCandidates = 5,
            CoarsePhaseThreshold = 10,
            TpeBudget = 50,
            TpeInitialSamples = 15,
            PurgedKFolds = 5,
            AdaptiveBoundsEnabled = true,
            GpEarlyStopPatience = 4,
            PresetName = "balanced",
            HyperbandEnabled = true,
            HyperbandEta = 3,
            UseEhviAcquisition = false,
            UseParegoScalarization = false,
            ScreeningTimeoutSeconds = 30,
            ScreeningSpreadPoints = 20.0,
            ScreeningCommissionPerLot = 7.0,
            ScreeningSlippagePips = 1.0,
            ScreeningInitialBalance = 10_000m,
            MaxParallelBacktests = 4,
            MinCandidateTrades = 10,
            MaxRunTimeoutMinutes = 30,
            CircuitBreakerThreshold = 10,
            SuccessiveHalvingRungs = "0.25,0.50",
            MaxOosDegradationPct = 0.60,
            EmbargoRatio = 0.05,
            CorrelationParamThreshold = 0.15,
            SensitivityPerturbPct = 0.10,
            SensitivityDegradationTolerance = 0.20,
            BootstrapIterations = 1000,
            MinBootstrapCILower = 0.40m,
            CostSensitivityEnabled = true,
            CostStressMultiplier = 2.0,
            TemporalOverlapThreshold = 0.70,
            PortfolioCorrelationThreshold = 0.80,
            WalkForwardMinMaxRatio = 0.50,
            MinOosCandlesForValidation = 50,
            MaxCvCoefficientOfVariation = 0.50,
            PermutationIterations = 1000,
            MinEquityCurveR2 = 0.60,
            MaxTradeTimeConcentration = 0.60,
            CpcvNFolds = 6,
            CpcvTestFoldCount = 2,
            CpcvMaxCombinations = 15,
            DataScarcityThreshold = 200,
            CandleLookbackMonths = 12,
            CandleLookbackAutoScale = true,
            UseSymbolSpecificSpread = true,
            RegimeBlendRatio = 0.50,
            MaxCrossRegimeEvals = 4,
            RegimeStabilityHours = 6,
            SuppressDuringDrawdownRecovery = true,
            SeasonalBlackoutEnabled = true,
            BlackoutPeriods = "12/20-01/05",
            RequireEADataAvailability = false,
            MaxRetryAttempts = maxRetryAttempts,
            MaxConsecutiveFailuresBeforeEscalation = 3,
            CheckpointEveryN = 10,
            MaxConcurrentRuns = 2,
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
