using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class OptimizationRunArchitectureTest
{
    [Fact]
    public void SerializeConfigSnapshot_RoundTripsRunScopedConfig()
    {
        var config = CreateConfig();

        string json = OptimizationRunContracts.SerializeConfigSnapshot(config);
        var run = new OptimizationRun
        {
            Id = 401,
            StrategyId = 55,
            ConfigSnapshotJson = json,
            IsDeleted = false
        };

        bool restored = OptimizationRunContracts.TryDeserializeConfigSnapshot(run, out var snapshotConfig);

        Assert.True(restored);
        Assert.Equal(config.PresetName, snapshotConfig.PresetName);
        Assert.Equal(config.AutoApprovalMinHealthScore, snapshotConfig.AutoApprovalMinHealthScore);
        Assert.Equal(config.CandleLookbackMonths, snapshotConfig.CandleLookbackMonths);
        Assert.Equal(config.CircuitBreakerThreshold, snapshotConfig.CircuitBreakerThreshold);
    }

    [Fact]
    public void ExtractOptimizationRegime_PrefersFrozenOptimizationRegime()
    {
        string metadata = """
            {
              "CurrentRegime": "Ranging",
              "OptimizationRegime": "Trending"
            }
            """;

        string? regime = OptimizationRunContracts.ExtractOptimizationRegime(metadata);

        Assert.Equal("Trending", regime);
    }

    [Fact]
    public void FailForRetry_ClearsApprovalAndPublicationState()
    {
        var nowUtc = new DateTime(2026, 04, 06, 12, 0, 0, DateTimeKind.Utc);
        var run = new OptimizationRun
        {
            Id = 402,
            StrategyId = 88,
            Status = OptimizationRunStatus.Completed,
            ApprovedAt = nowUtc.AddMinutes(-20),
            DeferredUntilUtc = nowUtc.AddMinutes(30),
            BestParametersJson = """{"Fast":12}""",
            BestHealthScore = 0.66m,
            BestSharpeRatio = 1.2m,
            BestMaxDrawdownPct = 8m,
            BestWinRate = 0.58m,
            ApprovalReportJson = """{"passed":true}""",
            ValidationFollowUpsCreatedAt = nowUtc.AddMinutes(-15),
            ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
            FollowUpLastCheckedAt = nowUtc.AddMinutes(-10),
            NextFollowUpCheckAt = nowUtc.AddMinutes(10),
            FollowUpRepairAttempts = 2,
            FollowUpLastStatusCode = "Pending",
            FollowUpLastStatusMessage = "awaiting completion",
            FollowUpStatusUpdatedAt = nowUtc.AddMinutes(-5),
            CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Failed,
            CompletionPublicationPayloadJson = """{"event":"completed"}""",
            CompletionPublicationAttempts = 3,
            CompletionPublicationLastAttemptAt = nowUtc.AddMinutes(-4),
            CompletionPublicationCompletedAt = nowUtc.AddMinutes(-3),
            CompletionPublicationErrorMessage = "transport timeout",
            ExecutionStage = OptimizationExecutionStage.CompletionPublication,
            ExecutionStageMessage = "publishing completion side effects",
            ExecutionStageUpdatedAt = nowUtc.AddMinutes(-2),
            LastOperationalIssueCode = "CompletionReplayFailed",
            LastOperationalIssueMessage = "transport timeout",
            LastOperationalIssueAt = nowUtc.AddMinutes(-1),
            IsDeleted = false
        };

        OptimizationRunLifecycle.FailForRetry(
            run,
            "retry me",
            OptimizationFailureCategory.Transient,
            nowUtc);

        Assert.Equal(OptimizationRunStatus.Failed, run.Status);
        Assert.Equal(OptimizationFailureCategory.Transient, run.FailureCategory);
        Assert.Equal("retry me", run.ErrorMessage);
        Assert.Null(run.ApprovedAt);
        Assert.Null(run.DeferredUntilUtc);
        Assert.Null(run.BestParametersJson);
        Assert.Null(run.ValidationFollowUpsCreatedAt);
        Assert.Null(run.ValidationFollowUpStatus);
        Assert.Null(run.NextFollowUpCheckAt);
        Assert.Null(run.CompletionPublicationStatus);
        Assert.Null(run.CompletionPublicationPayloadJson);
        Assert.Equal(0, run.CompletionPublicationAttempts);
        Assert.Equal(OptimizationExecutionStage.Failed, run.ExecutionStage);
        Assert.Null(run.LastOperationalIssueCode);
        Assert.Null(run.LastOperationalIssueMessage);
        Assert.Null(run.LastOperationalIssueAt);
    }

    [Fact]
    public void SetStageAndIssue_PersistStructuredProgressAndDegradedState()
    {
        var nowUtc = new DateTime(2026, 04, 06, 12, 15, 0, DateTimeKind.Utc);
        var run = new OptimizationRun
        {
            Id = 403,
            StrategyId = 89,
            IsDeleted = false
        };

        OptimizationRunProgressTracker.SetStage(
            run,
            OptimizationExecutionStage.Validation,
            "Applying Pareto validation gates.",
            nowUtc);
        OptimizationRunProgressTracker.RecordOperationalIssue(
            run,
            "LeaseHeartbeatFailed",
            "Transient lease heartbeat write failed.",
            nowUtc.AddMinutes(1));

        Assert.Equal(OptimizationExecutionStage.Validation, run.ExecutionStage);
        Assert.Equal("Applying Pareto validation gates.", run.ExecutionStageMessage);
        Assert.Equal(nowUtc, run.ExecutionStageUpdatedAt);
        Assert.Equal("LeaseHeartbeatFailed", run.LastOperationalIssueCode);
        Assert.Equal("Transient lease heartbeat write failed.", run.LastOperationalIssueMessage);
        Assert.Equal(nowUtc.AddMinutes(1), run.LastOperationalIssueAt);
    }

    [Theory]
    [InlineData(Timeframe.D1, 24)]
    [InlineData(Timeframe.H4, 12)]
    [InlineData(Timeframe.H1, 6)]
    [InlineData(Timeframe.M15, 3)]
    [InlineData(Timeframe.M5, 2)]
    public void ComputeEffectiveLookback_UsesTimeframeDefaults(Timeframe timeframe, int expectedMonths)
    {
        int months = OptimizationDataLoader.ComputeEffectiveLookback(timeframe, configuredMonths: 6);

        Assert.Equal(expectedMonths, months);
    }

    [Fact]
    public void ComputeAdaptiveBudget_ShrinksButDoesNotExceedConfiguredBudget()
    {
        int budget = OptimizationSearchEngine.ComputeAdaptiveBudget(50, [10, 12, 15, 16]);

        Assert.InRange(budget, 30, 50);
        Assert.True(budget < 50);
    }

    [Fact]
    public void Classify_UsesTypedFailureCategory()
    {
        var ex = new OptimizationConfigSnapshotException(777);

        var category = OptimizationFailureClassifier.Classify(ex);

        Assert.Equal(OptimizationFailureCategory.ConfigError, category);
    }

    private static OptimizationConfig CreateConfig() => new()
    {
        SchedulePollSeconds = 60,
        CooldownDays = 14,
        RolloutObservationDays = 14,
        MaxQueuedPerCycle = 3,
        FollowUpMonitorBatchSize = 10,
        AutoScheduleEnabled = true,
        MaxRunsPerWeek = 5,
        MinWinRate = 0.55,
        MinProfitFactor = 1.10,
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
        HyperbandEnabled = false,
        HyperbandEta = 3,
        UseEhviAcquisition = false,
        UseParegoScalarization = false,
        ScreeningTimeoutSeconds = 30,
        ScreeningSpreadPoints = 20,
        ScreeningCommissionPerLot = 7,
        ScreeningSlippagePips = 1,
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
        MinEquityCurveR2 = 0.25,
        MaxTradeTimeConcentration = 0.60,
        CpcvNFolds = 6,
        CpcvTestFoldCount = 2,
        CpcvMaxCombinations = 15,
        DataScarcityThreshold = 200,
        CandleLookbackMonths = 6,
        CandleLookbackAutoScale = true,
        UseSymbolSpecificSpread = true,
        RegimeBlendRatio = 0.20,
        MaxCrossRegimeEvals = 2,
        RegimeStabilityHours = 6,
        SuppressDuringDrawdownRecovery = true,
        SeasonalBlackoutEnabled = true,
        BlackoutPeriods = "12/20-01/05",
        RequireEADataAvailability = true,
        MaxRetryAttempts = 2,
        MaxConsecutiveFailuresBeforeEscalation = 3,
        CheckpointEveryN = 10,
        MaxConcurrentRuns = 3,
    };
}
