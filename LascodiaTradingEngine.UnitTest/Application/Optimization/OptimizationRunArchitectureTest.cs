using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
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
            DeferralReason = OptimizationDeferralReason.DataQuality,
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
        Assert.Null(run.DeferralReason);
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

    [Fact]
    public void ComputeDeterministicSeed_IsStableAcrossInvocations()
    {
        var queueAnchorUtc = new DateTime(2026, 04, 09, 11, 30, 0, DateTimeKind.Utc);

        int first = OptimizationDeterministicSeed.Compute(41, 7, queueAnchorUtc);
        int second = OptimizationDeterministicSeed.Compute(41, 7, queueAnchorUtc);
        int different = OptimizationDeterministicSeed.Compute(41, 7, queueAnchorUtc.AddMinutes(1));

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
        Assert.True(first > 0);
    }

    [Fact]
    public void DeferralTracker_AppliesAndClearsCurrentDeferralLifecycle()
    {
        var deferredAtUtc = new DateTime(2026, 04, 09, 12, 0, 0, DateTimeKind.Utc);
        var resumedAtUtc = deferredAtUtc.AddMinutes(30);
        var run = new OptimizationRun
        {
            Id = 404,
            StrategyId = 90,
            Status = OptimizationRunStatus.Running,
            DeferralCount = 1,
            IsDeleted = false
        };

        OptimizationRunDeferralTracker.ApplyDeferral(
            run,
            OptimizationDeferralReason.DataQuality,
            deferredAtUtc.AddHours(1),
            deferredAtUtc);

        Assert.Equal(OptimizationRunStatus.Queued, run.Status);
        Assert.Equal(OptimizationDeferralReason.DataQuality, run.DeferralReason);
        Assert.Equal(deferredAtUtc, run.DeferredAtUtc);
        Assert.Equal(deferredAtUtc.AddHours(1), run.DeferredUntilUtc);
        Assert.Equal(2, run.DeferralCount);
        Assert.Null(run.LastResumedAtUtc);

        OptimizationRunDeferralTracker.MarkResumed(run, resumedAtUtc);

        Assert.Null(run.DeferralReason);
        Assert.Null(run.DeferredAtUtc);
        Assert.Null(run.DeferredUntilUtc);
        Assert.Equal(resumedAtUtc, run.LastResumedAtUtc);
        Assert.Equal(2, run.DeferralCount);
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
    public async Task LoadAsync_ExecutesExtractedDataPathAndPopulatesBaselineContext()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 06, 12, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 600)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = nowUtc.UtcDateTime.AddHours(-(600 - i)),
                Open = 1.1000m + i * 0.0001m,
                High = 1.1005m + i * 0.0001m,
                Low = 1.0995m + i * 0.0001m,
                Close = 1.1000m + i * 0.0001m,
                IsClosed = true
            })
            .ToList();

        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<Candle>()).Returns(candles.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<EconomicEvent>()).Returns(new List<EconomicEvent>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<StrategyRegimeParams>()).Returns(new List<StrategyRegimeParams>().AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<CurrencyPair>()).Returns(new List<CurrencyPair>
        {
            new() { Symbol = "EURUSD", DecimalPlaces = 5, ContractSize = 100_000m, SpreadPoints = 12, IsDeleted = false }
        }.AsQueryable().BuildMockDbSet().Object);

        var services = new ServiceCollection().BuildServiceProvider();
        var loader = new OptimizationDataLoader(
            NullLogger<OptimizationDataLoader>.Instance,
            Mock.Of<ISpreadProfileProvider>(),
            new OptimizationValidator(new BaselineSplitBacktestEngine(), new FakeTimeProvider(nowUtc)),
            new FakeTimeProvider(nowUtc));

        var strategy = new Strategy
        {
            Id = 601,
            Name = "ExtractedPath",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12,"Slow":34}"""
        };
        var run = new OptimizationRun
        {
            Id = 602,
            StrategyId = strategy.Id,
            StartedAt = nowUtc.UtcDateTime
        };
        var config = CreateConfig() with { UseSymbolSpecificSpread = false };

        var result = await loader.LoadAsync(db.Object, run, strategy, config, CancellationToken.None);

        Assert.NotNull(run.BaselineHealthScore);
        Assert.NotEmpty(result.TrainCandles);
        Assert.NotEmpty(result.TestCandles);
        Assert.Equal(run.BaselineParametersJson, result.BaselineParametersJson);
        Assert.True(result.BaselineComparisonScore > run.BaselineHealthScore!.Value);
    }

    [Fact]
    public void AutoRegisterAttributedServices_ResolvesOptimizationSearchGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AutoRegisterAttributedServices(typeof(OptimizationRunProcessor).Assembly);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 04, 06, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<TradingMetrics>(sp => new TradingMetrics(sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        services.AddSingleton<IBacktestEngine>(new BaselineSplitBacktestEngine());

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var bootstrapper = scope.ServiceProvider.GetRequiredService<OptimizationSearchBootstrapper>();
        var searchCoordinator = scope.ServiceProvider.GetRequiredService<OptimizationSearchCoordinator>();

        Assert.NotNull(bootstrapper);
        Assert.NotNull(searchCoordinator);
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class BaselineSplitBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            bool isTrainLike = candles.Count >= 300;
            return Task.FromResult(new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + (isTrainLike ? 200m : 800m),
                TotalTrades = isTrainLike ? 40 : 25,
                WinRate = isTrainLike ? 0.45m : 0.62m,
                ProfitFactor = isTrainLike ? 1.05m : 1.70m,
                MaxDrawdownPct = isTrainLike ? 12m : 4m,
                SharpeRatio = isTrainLike ? 0.4m : 1.6m,
                Trades = []
            });
        }
    }
}
