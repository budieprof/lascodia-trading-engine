using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MockQueryable.Moq;
using Moq;
using LascodiaTradingEngine.Application.Backtesting;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class OptimizationHelperServicesTest
{
    [Theory]
    [InlineData("")]
    [InlineData("13/01-01/05")]
    [InlineData("abc")]
    [InlineData("01/32-02/01")]
    public void IsInBlackoutPeriod_MalformedInput_ReturnsFalse(string periods)
    {
        Assert.False(OptimizationPolicyHelpers.IsInBlackoutPeriod(
            periods,
            new DateTime(2026, 01, 15, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void IsInBlackoutPeriod_UsesProvidedUtcNow()
    {
        var inWindow = OptimizationPolicyHelpers.IsInBlackoutPeriod(
            "12/20-01/05",
            new DateTime(2026, 12, 24, 12, 0, 0, DateTimeKind.Utc));
        var outOfWindow = OptimizationPolicyHelpers.IsInBlackoutPeriod(
            "12/20-01/05",
            new DateTime(2026, 02, 01, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(inWindow);
        Assert.False(outOfWindow);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_IdenticalScores_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.70m, 0.70m, 0.70m },
            out var predictedDecline);

        Assert.False(result);
        Assert.Equal(0m, predictedDecline);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_AscendingScores_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.70m, 0.60m },
            out _);

        Assert.False(result);
    }

    [Fact]
    public void IsMeaningfullyDeteriorating_FewerThan3Snapshots_ReturnsFalse()
    {
        var result = OptimizationPolicyHelpers.IsMeaningfullyDeteriorating(
            new List<decimal> { 0.80m, 0.60m },
            out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 45)]
    [InlineData(5, 255)]
    public void GetRetryEligibilityWindow_ReturnsCorrectTimeSpan(int maxRetries, int expectedMinutes)
    {
        var result = OptimizationPolicyHelpers.GetRetryEligibilityWindow(maxRetries);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), result);
    }

    [Fact]
    public void AreParametersSimilarToAny_ReturnsTrue_ForMatchingCategoricalOnlyParameters()
    {
        const string candidateJson = """{"Mode":"Breakout","Session":"London"}""";
        var parsed = new List<Dictionary<string, JsonElement>>
        {
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidateJson)!
        };

        bool isSimilar = OptimizationPolicyHelpers.AreParametersSimilarToAny(candidateJson, parsed, 0.15);

        Assert.True(isSimilar);
    }

    [Fact]
    public void ParseFidelityRungs_MalformedValues_FallsBackToDefault()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "abc,def",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.25, 0.50], result);
    }

    [Fact]
    public void ParseFidelityRungs_PartiallyValid_KeepsValidValues()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "abc,0.30,0.60",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.30, 0.60], result);
    }

    [Fact]
    public void ParseFidelityRungs_OutOfRangeValues_Excluded()
    {
        var result = OptimizationPolicyHelpers.ParseFidelityRungs(
            "0,0.50,1.0,1.5",
            NullLogger.Instance,
            "OptimizationHelperServicesTest");

        Assert.Equal([0.50], result);
    }

    [Fact]
    public void DiffConfigSnapshots_NumericPrecision_NoFalsePositive()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"EmbargoRatio":0.05}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Empty(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsChangedKey()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":100}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Single(result);
    }

    [Fact]
    public void DiffConfigSnapshots_DetectsAddedKey()
    {
        string prior = """{"Version":1,"Config":{"TpeBudget":50}}""";
        string current = """{"Version":1,"Config":{"TpeBudget":50,"NewKey":true}}""";

        var result = OptimizationRunScopedConfigService.DiffConfigSnapshots(prior, current);

        Assert.Single(result);
    }

    [Fact]
    public void HasLeaseOwnershipChanged_ReturnsTrue_WhenTokenOrStatusDiffers()
    {
        var expectedToken = Guid.NewGuid();

        Assert.False(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, expectedToken));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Running, Guid.NewGuid()));
        Assert.True(OptimizationRunLeaseManager.HasLeaseOwnershipChanged(expectedToken, OptimizationRunStatus.Completed, expectedToken));
    }

    [Fact]
    public void ExecutionLeaseHeartbeatInterval_IsShorterThanLeaseDuration()
    {
        var interval = OptimizationRunLeaseManager.GetHeartbeatInterval();

        Assert.True(interval >= TimeSpan.FromMinutes(1));
        Assert.True(interval < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task ClaimNextRunAsync_RejectsNonPostgresProviders()
    {
        var options = new DbContextOptionsBuilder<TestOptimizationDbContext>()
            .Options;

        await using var db = new TestOptimizationDbContext(options);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            OptimizationRunClaimer.ClaimNextRunAsync(
                db,
                maxConcurrentRuns: 1,
                leaseDuration: TimeSpan.FromMinutes(10),
                nowUtc: DateTime.UtcNow,
                CancellationToken.None));

        Assert.Contains("PostgreSQL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRegimeIntervals_And_FilterCandlesByIntervals_ReturnExpectedSlice()
    {
        var snapshots = new List<MarketRegimeSnapshot>
        {
            new() { DetectedAt = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
            new() { DetectedAt = new DateTime(2026, 03, 03, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Ranging },
            new() { DetectedAt = new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc), Regime = MarketRegime.Trending },
        };
        var candles = Enumerable.Range(0, 120)
            .Select(i => new Candle
            {
                Timestamp = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.1m,
                IsClosed = true
            })
            .ToList();

        var intervals = OptimizationRegimeIntervalBuilder.BuildRegimeIntervals(
            snapshots,
            MarketRegime.Trending,
            snapshots[0].DetectedAt,
            new DateTime(2026, 03, 05, 0, 0, 0, DateTimeKind.Utc));
        var filtered = OptimizationRegimeIntervalBuilder.FilterCandlesByIntervals(candles, intervals);

        Assert.Equal(48, filtered.Count);
        Assert.Equal(new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc), filtered.First().Timestamp);
    }

    [Fact]
    public void IsDuplicateFollowUpConstraintViolation_MatchesProviderSpecificOrFallbackErrors()
    {
        var ex = new DbUpdateException(
            "save failed",
            new Exception("duplicate key value violates unique constraint \"IX_BacktestRun_SourceOptimizationRunId\""));

        Assert.True(OptimizationDbExceptionClassifier.IsDuplicateFollowUpConstraintViolation(ex));
    }

    [Fact]
    public void IsActiveQueueConstraintViolation_MatchesFallbackDuplicateKeyMessage()
    {
        var ex = new DbUpdateException(
            "save failed",
            new Exception("duplicate key value violates unique constraint on OptimizationRun StrategyId"));

        Assert.True(OptimizationDbExceptionClassifier.IsActiveQueueConstraintViolation(ex));
    }

    [Theory]
    [InlineData("""{"hasSufficientOutOfSampleData":true}""", true)]
    [InlineData("""{"hasSufficientOutOfSampleData":false}""", false)]
    [InlineData("""{"passed":false}""", false)]
    [InlineData(null, false)]
    public void HasApprovalGradeOosValidation_RequiresExplicitTrueFlag(string? approvalReportJson, bool expected)
    {
        bool result = OptimizationApprovalReportParser.HasApprovalGradeOosValidation(approvalReportJson);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task PersistAsync_PassingValidation_CompletesRunBeforeApproval()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 07, 10, 0, 0, TimeSpan.Zero);
        var run = new OptimizationRun
        {
            Id = 710,
            StrategyId = 55,
            Status = OptimizationRunStatus.Running,
            BaselineHealthScore = 0.51m,
            DeterministicSeed = 42
        };
        var strategy = new Strategy
        {
            Id = run.StrategyId,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = nowUtc.UtcDateTime.AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.15m,
                IsClosed = true
            })
            .ToList();
        var winnerResult = new BacktestResult
        {
            SharpeRatio = 1.4m,
            MaxDrawdownPct = 6m,
            WinRate = 0.61m,
            Trades = []
        };
        var validationResult = CandidateValidationResult.Create(
            passed: true,
            winner: new ScoredCandidate("""{"Fast":12}""", 0.88m, winnerResult),
            oosHealthScore: 0.88m,
            oosResult: winnerResult,
            hasOosValidation: true,
            ciLower: 0.80m,
            ciMedian: 0.88m,
            ciUpper: 0.93m,
            permPValue: 0.01,
            permCorrectedAlpha: 0.05,
            permSignificant: true,
            sensitivityOk: true,
            sensitivityReport: "ok",
            costSensitiveOk: true,
            pessimisticScore: 0.80m,
            degradationFailed: false,
            wfAvgScore: 0.85m,
            wfStable: true,
            mtfCompatible: true,
            correlationSafe: true,
            temporalCorrelationSafe: true,
            temporalMaxOverlap: 0,
            portfolioCorrelationSafe: true,
            portfolioMaxCorrelation: 0,
            cvConsistent: true,
            cvValue: 0.02,
            approvalReportJson: """{"passed":true,"hasSufficientOutOfSampleData":true}""",
            failureReason: string.Empty);
        var searchResult = new SearchResult(
            [],
            TotalIterations: 27,
            SurrogateKind: "TPE",
            WarmStartedObservations: 0,
            ResumedFromCheckpoint: false,
            Diagnostics: new SearchExecutionSummary(null, false, 27, 0, 0, 0, 0, 0, 0, 0));

        var writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var service = new OptimizationRunPersistenceService(
            NullLogger<OptimizationRunPersistenceService>.Instance,
            new OptimizationRunMetadataService(NullLogger<OptimizationRunMetadataService>.Instance),
            new OptimizationRunOwnedMutationGuard(
                new OptimizationRunLeaseManager(
                    scopeFactory,
                    NullLogger<OptimizationRunLeaseManager>.Instance,
                    timeProvider),
                NullLogger<OptimizationRunOwnedMutationGuard>.Instance),
            timeProvider);

        await service.PersistAsync(
            new OptimizationRunPersistenceContext(
                Run: run,
                ExpectedLeaseToken: Guid.NewGuid(),
                Strategy: strategy,
                Candles: candles,
                TrainCandles: candles.Take(6).ToList(),
                TestCandles: candles.Skip(6).ToList(),
                EmbargoSize: 1,
                OptimizationRegime: MarketRegime.Trending,
                PersistenceRegime: MarketRegime.Trending,
                BaselineRegimeParamsUsed: false,
                BaselineComparisonScore: 0.52m,
                SearchResult: searchResult,
                ValidationResult: validationResult,
                TotalIterations: 27),
            writeCtx.Object,
            CancellationToken.None);

        Assert.Equal(OptimizationRunStatus.Completed, run.Status);
        Assert.Equal(nowUtc.UtcDateTime, run.CompletedAt);
        Assert.Equal(nowUtc.UtcDateTime, run.ResultsPersistedAt);
        Assert.Equal(0.88m, run.BestHealthScore);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OptimizationValidator_IsolatesRunStateAcrossConcurrentFlows()
    {
        var backtestEngine = new InitialBalanceEchoBacktestEngine();
        var sharedTimeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 04, 07, 12, 0, 0, TimeSpan.Zero));
        var firstValidator = new OptimizationValidator(backtestEngine, sharedTimeProvider);
        var secondValidator = new OptimizationValidator(backtestEngine, sharedTimeProvider);
        var strategy = new Strategy
        {
            Id = 911,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.BreakoutScalper,
            ParametersJson = "{}"
        };
        var candles = Enumerable.Range(0, 8)
            .Select(i => new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = new DateTime(2026, 04, 01, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = 1.1m,
                High = 1.2m,
                Low = 1.0m,
                Close = 1.15m,
                IsClosed = true
            })
            .ToList();

        var firstMayRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstConfigured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<decimal> FirstFlowAsync()
        {
            firstValidator.SetInitialBalance(1_000m);
            firstValidator.EnableCache();
            firstConfigured.SetResult();
            await firstMayRun.Task;
            try
            {
                var result = await firstValidator.RunWithTimeoutAsync(
                    strategy,
                    """{"flow":"first"}""",
                    candles,
                    new BacktestOptions(),
                    timeoutSecs: 5,
                    CancellationToken.None);
                return result.InitialBalance;
            }
            finally
            {
                firstValidator.ClearCache();
            }
        }

        async Task<decimal> SecondFlowAsync()
        {
            await firstConfigured.Task;
            secondValidator.SetInitialBalance(2_500m);
            secondValidator.EnableCache();
            firstMayRun.SetResult();
            try
            {
                var result = await secondValidator.RunWithTimeoutAsync(
                    strategy,
                    """{"flow":"second"}""",
                    candles,
                    new BacktestOptions(),
                    timeoutSecs: 5,
                    CancellationToken.None);
                return result.InitialBalance;
            }
            finally
            {
                secondValidator.ClearCache();
            }
        }

        var results = await Task.WhenAll(
            Task.Run(FirstFlowAsync),
            Task.Run(SecondFlowAsync));

        Assert.Contains(1_000m, results);
        Assert.Contains(2_500m, results);
    }

    [Fact]
    public void CandidateValidationResultCreate_RejectsInconsistentOosFlags()
    {
        var winner = new ScoredCandidate("{}",
            0.55m,
            new BacktestResult
            {
                Trades = []
            });

        var ex = Assert.Throws<ArgumentException>(() => CandidateValidationResult.Create(
            passed: false,
            winner: winner,
            oosHealthScore: 0m,
            oosResult: new BacktestResult { Trades = [] },
            hasOosValidation: true,
            ciLower: 0.4m,
            ciMedian: 0.5m,
            ciUpper: 0.6m,
            permPValue: 1.0,
            permCorrectedAlpha: 0.05,
            permSignificant: false,
            sensitivityOk: false,
            sensitivityReport: "failed",
            costSensitiveOk: false,
            pessimisticScore: 0.4m,
            degradationFailed: true,
            wfAvgScore: 0.4m,
            wfStable: false,
            mtfCompatible: true,
            correlationSafe: true,
            temporalCorrelationSafe: true,
            temporalMaxOverlap: 0,
            portfolioCorrelationSafe: true,
            portfolioMaxCorrelation: 0,
            cvConsistent: true,
            cvValue: 0.1,
            approvalReportJson: """{"hasSufficientOutOfSampleData":false}""",
            failureReason: "manual review"));

        Assert.Contains("inconsistent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateValidationResultCreate_RejectsInconsistentPassedFlag()
    {
        var winner = new ScoredCandidate(
            "{}",
            0.72m,
            new BacktestResult { Trades = [] });

        var ex = Assert.Throws<ArgumentException>(() => CandidateValidationResult.Create(
            passed: true,
            winner: winner,
            oosHealthScore: 0.72m,
            oosResult: new BacktestResult { Trades = [] },
            hasOosValidation: true,
            ciLower: 0.5m,
            ciMedian: 0.6m,
            ciUpper: 0.7m,
            permPValue: 0.01,
            permCorrectedAlpha: 0.05,
            permSignificant: true,
            sensitivityOk: true,
            sensitivityReport: "ok",
            costSensitiveOk: true,
            pessimisticScore: 0.5m,
            degradationFailed: false,
            wfAvgScore: 0.6m,
            wfStable: true,
            mtfCompatible: true,
            correlationSafe: true,
            temporalCorrelationSafe: true,
            temporalMaxOverlap: 0,
            portfolioCorrelationSafe: true,
            portfolioMaxCorrelation: 0,
            cvConsistent: true,
            cvValue: 0.1,
            approvalReportJson: """{"passed":false,"hasSufficientOutOfSampleData":true,"hasOosValidation":true,"failureReason":"manual review"}""",
            failureReason: string.Empty));

        Assert.Contains("Passed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateValidationResultCreate_RejectsFailureReasonDrift()
    {
        var winner = new ScoredCandidate(
            "{}",
            0.48m,
            new BacktestResult { Trades = [] });

        var ex = Assert.Throws<ArgumentException>(() => CandidateValidationResult.Create(
            passed: false,
            winner: winner,
            oosHealthScore: 0.48m,
            oosResult: new BacktestResult { Trades = [] },
            hasOosValidation: true,
            ciLower: 0.4m,
            ciMedian: 0.5m,
            ciUpper: 0.6m,
            permPValue: 0.5,
            permCorrectedAlpha: 0.05,
            permSignificant: false,
            sensitivityOk: false,
            sensitivityReport: "failed",
            costSensitiveOk: false,
            pessimisticScore: 0.3m,
            degradationFailed: true,
            wfAvgScore: 0.4m,
            wfStable: false,
            mtfCompatible: true,
            correlationSafe: true,
            temporalCorrelationSafe: true,
            temporalMaxOverlap: 0,
            portfolioCorrelationSafe: true,
            portfolioMaxCorrelation: 0,
            cvConsistent: true,
            cvValue: 0.1,
            approvalReportJson: """{"passed":false,"hasSufficientOutOfSampleData":true,"hasOosValidation":true,"failureReason":"different reason"}""",
            failureReason: "manual review"));

        Assert.Contains("failure reason", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileLifecycleStateAsync_RebuildsMalformedCompletionPayload()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 07, 14, 0, 0, TimeSpan.Zero);
        var run = new OptimizationRun
        {
            Id = 991,
            StrategyId = 77,
            Status = OptimizationRunStatus.Completed,
            CompletedAt = nowUtc.UtcDateTime.AddMinutes(-20),
            ResultsPersistedAt = nowUtc.UtcDateTime.AddMinutes(-20),
            BaselineHealthScore = 0.41m,
            BestHealthScore = 0.73m,
            Iterations = 12,
            CompletionPublicationPayloadJson = "{ this is not valid json",
            CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Pending,
            IsDeleted = false
        };
        var strategy = new Strategy
        {
            Id = run.StrategyId,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":12}""",
            IsDeleted = false
        };

        var coordinator = CreateRecoveryCoordinator([run], [strategy], nowUtc, out var writeCtx);

        var summary = await coordinator.ReconcileLifecycleStateAsync(CreateRecoveryConfig(), CancellationToken.None);

        Assert.Equal(1, summary.RepairedRuns);
        Assert.Equal(1, summary.MalformedCompletionPayloadRepairs);
        Assert.NotNull(run.CompletionPublicationPreparedAt);
        Assert.NotNull(run.LifecycleReconciledAt);

        var rebuiltEvent = JsonSerializer.Deserialize<OptimizationCompletedIntegrationEvent>(run.CompletionPublicationPayloadJson!);
        Assert.NotNull(rebuiltEvent);
        Assert.Equal(run.Id, rebuiltEvent!.OptimizationRunId);
        Assert.Equal(run.StrategyId, rebuiltEvent.StrategyId);
        Assert.Equal(strategy.Symbol, rebuiltEvent.Symbol);
        writeCtx.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileLifecycleStateAsync_RepairsApprovedBestParametersAndMalformedConfigSnapshot()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 07, 15, 0, 0, TimeSpan.Zero);
        var validPayload = JsonSerializer.Serialize(new OptimizationCompletedIntegrationEvent
        {
            OptimizationRunId = 992,
            StrategyId = 88,
            Symbol = "GBPUSD",
            Timeframe = Timeframe.H1,
            Iterations = 4,
            BaselineScore = 0.30m,
            BestOosScore = 0.66m,
            CompletedAt = nowUtc.UtcDateTime.AddMinutes(-30)
        });
        var run = new OptimizationRun
        {
            Id = 992,
            StrategyId = 88,
            Status = OptimizationRunStatus.Approved,
            CompletedAt = nowUtc.UtcDateTime.AddMinutes(-30),
            ApprovedAt = nowUtc.UtcDateTime.AddMinutes(-25),
            ResultsPersistedAt = nowUtc.UtcDateTime.AddMinutes(-30),
            ApprovalEvaluatedAt = nowUtc.UtcDateTime.AddMinutes(-25),
            BestParametersJson = null,
            ConfigSnapshotJson = "{ malformed snapshot",
            CompletionPublicationPayloadJson = validPayload,
            CompletionPublicationPreparedAt = nowUtc.UtcDateTime.AddMinutes(-24),
            CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Pending,
            ValidationFollowUpsCreatedAt = nowUtc.UtcDateTime.AddMinutes(-23),
            ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
            IsDeleted = false
        };
        var strategy = new Strategy
        {
            Id = run.StrategyId,
            Symbol = "GBPUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":21,"Slow":55}""",
            IsDeleted = false
        };

        var coordinator = CreateRecoveryCoordinator([run], [strategy], nowUtc, out _);

        var summary = await coordinator.ReconcileLifecycleStateAsync(CreateRecoveryConfig(), CancellationToken.None);

        Assert.Equal(1, summary.RepairedRuns);
        Assert.Equal(1, summary.BestParameterRepairs);
        Assert.Equal(1, summary.ConfigSnapshotRepairs);
        Assert.Equal("""{"Fast":21,"Slow":55}""", run.BestParametersJson);
        Assert.True(OptimizationRunContracts.TryDeserializeConfigSnapshot(run, out _));
    }

    [Fact]
    public async Task ReconcileLifecycleStateAsync_ClearsRejectedFollowUpStateAndRepairsPublishedHistory()
    {
        var nowUtc = new DateTimeOffset(2026, 04, 07, 16, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new OptimizationCompletedIntegrationEvent
        {
            OptimizationRunId = 993,
            StrategyId = 99,
            Symbol = "USDJPY",
            Timeframe = Timeframe.H1,
            Iterations = 9,
            BaselineScore = 0.22m,
            BestOosScore = 0.44m,
            CompletedAt = nowUtc.UtcDateTime.AddMinutes(-40)
        });
        var run = new OptimizationRun
        {
            Id = 993,
            StrategyId = 99,
            Status = OptimizationRunStatus.Rejected,
            CompletedAt = nowUtc.UtcDateTime.AddMinutes(-40),
            ResultsPersistedAt = nowUtc.UtcDateTime.AddMinutes(-40),
            ApprovalEvaluatedAt = nowUtc.UtcDateTime.AddMinutes(-35),
            ValidationFollowUpsCreatedAt = nowUtc.UtcDateTime.AddMinutes(-34),
            ValidationFollowUpStatus = ValidationFollowUpStatus.Pending,
            NextFollowUpCheckAt = nowUtc.UtcDateTime.AddMinutes(30),
            FollowUpLastCheckedAt = nowUtc.UtcDateTime.AddMinutes(-10),
            FollowUpRepairAttempts = 2,
            FollowUpLastStatusCode = "Pending",
            FollowUpLastStatusMessage = "awaiting follow-up completion",
            FollowUpStatusUpdatedAt = nowUtc.UtcDateTime.AddMinutes(-9),
            CompletionPublicationPayloadJson = payload,
            CompletionPublicationPreparedAt = nowUtc.UtcDateTime.AddMinutes(-33),
            CompletionPublicationStatus = OptimizationCompletionPublicationStatus.Published,
            CompletionPublicationCompletedAt = null,
            CompletionPublicationErrorMessage = "stale error",
            IsDeleted = false
        };
        var strategy = new Strategy
        {
            Id = run.StrategyId,
            Symbol = "USDJPY",
            Timeframe = Timeframe.H1,
            ParametersJson = """{"Fast":8}""",
            IsDeleted = false
        };

        var coordinator = CreateRecoveryCoordinator([run], [strategy], nowUtc, out _);

        var summary = await coordinator.ReconcileLifecycleStateAsync(CreateRecoveryConfig(), CancellationToken.None);

        Assert.Equal(1, summary.RepairedRuns);
        Assert.Equal(1, summary.FollowUpRepairs);
        Assert.Null(run.ValidationFollowUpsCreatedAt);
        Assert.Null(run.ValidationFollowUpStatus);
        Assert.Null(run.NextFollowUpCheckAt);
        Assert.Equal(OptimizationCompletionPublicationStatus.Published, run.CompletionPublicationStatus);
        Assert.NotNull(run.CompletionPublicationCompletedAt);
        Assert.Null(run.CompletionPublicationErrorMessage);
    }

    private static OptimizationRunRecoveryCoordinator CreateRecoveryCoordinator(
        List<OptimizationRun> runs,
        List<Strategy> strategies,
        DateTimeOffset nowUtc,
        out Mock<IWriteApplicationDbContext> writeCtx)
    {
        var db = new Mock<DbContext>();
        db.Setup(c => c.Set<OptimizationRun>()).Returns(runs.AsQueryable().BuildMockDbSet().Object);
        db.Setup(c => c.Set<Strategy>()).Returns(strategies.AsQueryable().BuildMockDbSet().Object);

        writeCtx = new Mock<IWriteApplicationDbContext>();
        writeCtx.Setup(x => x.GetDbContext()).Returns(db.Object);
        writeCtx.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var services = new ServiceCollection();
        services.AddSingleton<IWriteApplicationDbContext>(writeCtx.Object);
        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new TradingMetrics(metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var timeProvider = new FixedTimeProvider(nowUtc);
        var configProvider = new OptimizationConfigProvider(
            NullLogger<OptimizationConfigProvider>.Instance,
            timeProvider);
        var runScopedConfigService = new OptimizationRunScopedConfigService(
            configProvider,
            NullLogger<OptimizationRunScopedConfigService>.Instance);
        var settingsProvider = new ValidationSettingsProvider();
        var optionsBuilder = new BacktestOptionsSnapshotBuilder(
            settingsProvider,
            NullLogger<BacktestOptionsSnapshotBuilder>.Instance);
        var snapshotBuilder = new StrategyExecutionSnapshotBuilder();
        var validationRunFactory = new ValidationRunFactory(optionsBuilder, snapshotBuilder, timeProvider);
        var followUpCoordinator = new OptimizationFollowUpCoordinator(
            scopeFactory,
            Mock.Of<IAlertDispatcher>(),
            runScopedConfigService,
            validationRunFactory,
            optionsBuilder,
            snapshotBuilder,
            NullLogger<OptimizationFollowUpCoordinator>.Instance,
            metrics,
            timeProvider);

        return new OptimizationRunRecoveryCoordinator(
            scopeFactory,
            metrics,
            NullLogger<OptimizationRunRecoveryCoordinator>.Instance,
            followUpCoordinator,
            runScopedConfigService,
            timeProvider);
    }

    private static OptimizationConfig CreateRecoveryConfig() => new()
    {
        SchedulePollSeconds = 60,
        CooldownDays = 14,
        RolloutObservationDays = 14,
        MaxQueuedPerCycle = 3,
        AutoApprovalMinHealthScore = 0.55m,
        AutoScheduleEnabled = true,
        MaxRunsPerWeek = 5,
        MinWinRate = 0.55,
        MinProfitFactor = 1.10,
        MinTotalTrades = 10,
        AutoApprovalImprovementThreshold = 0.10m,
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
        FollowUpMonitorBatchSize = 10,
    };

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _nowUtc;

        public FixedTimeProvider(DateTimeOffset nowUtc) => _nowUtc = nowUtc;

        public override DateTimeOffset GetUtcNow() => _nowUtc;
    }

    private sealed class TestOptimizationDbContext(DbContextOptions<TestOptimizationDbContext> options)
        : DbContext(options)
    {
    }

    private sealed class InitialBalanceEchoBacktestEngine : IBacktestEngine
    {
        public async Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            await Task.Delay(15, ct);
            return new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance,
                Trades = []
            };
        }
    }
}
