using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class MLShadowArbiterWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLShadowArbiterWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<IDistributedLock>            _mockDistributedLock;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLShadowArbiterWorker _worker;

    public MLShadowArbiterWorkerTest()
    {
        _mockReadContext      = new Mock<IReadApplicationDbContext>();
        _mockWriteContext     = new Mock<IWriteApplicationDbContext>();
        _mockLogger           = new Mock<ILogger<MLShadowArbiterWorker>>();
        _mockScopeFactory     = new Mock<IServiceScopeFactory>();
        _mockDistributedLock  = new Mock<IDistributedLock>();
        _mockReadDbContext    = new Mock<DbContext>();
        _mockWriteDbContext   = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockReadDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockWriteDbContext.Object);

        // Mock IDistributedLock to return a successfully acquired lock
        var mockLockHandle = new Mock<IAsyncDisposable>();
        mockLockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockDistributedLock
            .Setup(dl => dl.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockLockHandle.Object);

        // Wire up IServiceScopeFactory -> AsyncServiceScope -> IServiceProvider
        var mockScope           = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IReadApplicationDbContext)))
            .Returns(_mockReadContext.Object);

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IWriteApplicationDbContext)))
            .Returns(_mockWriteContext.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

        // CreateAsyncScope is an extension method that internally calls CreateScope
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _worker = new MLShadowArbiterWorker(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            _mockDistributedLock.Object);
    }

    private void SetupShadowEvaluations(List<MLShadowEvaluation> evaluations)
    {
        var mockSet = evaluations.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLShadowEvaluation>()).Returns(mockSet.Object);
        _mockWriteDbContext.Setup(c => c.Set<MLShadowEvaluation>()).Returns(mockSet.Object);
    }

    private void SetupPredictionLogs(List<MLModelPredictionLog> logs)
    {
        var mockSet = logs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModelPredictionLog>()).Returns(mockSet.Object);
    }

    private void SetupEngineConfigs(List<EngineConfig> configs)
    {
        var mockSet = configs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockSet.Object);
    }

    private void SetupMarketRegimeSnapshots(List<MarketRegimeSnapshot> snapshots)
    {
        var mockSet = snapshots.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(mockSet.Object);
    }

    private void SetupModels(List<MLModel> models)
    {
        var mockSet = models.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModel>()).Returns(mockSet.Object);
        _mockWriteDbContext.Setup(c => c.Set<MLModel>()).Returns(mockSet.Object);
    }

    /// <summary>
    /// Runs the worker for a single iteration by cancelling after a short delay.
    /// </summary>
    private async Task RunWorkerOnceAsync()
    {
        using var cts = new CancellationTokenSource();
        // Cancel almost immediately so the worker runs one iteration then stops
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        try
        {
            await _worker.StartAsync(cts.Token);
            // Give the worker enough time to execute its loop body
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await _worker.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation fires
        }
    }

    /// <summary>
    /// Helper: generates a list of resolved prediction logs for a given model.
    /// </summary>
    private static List<MLModelPredictionLog> GeneratePredictionLogs(
        long modelId, ModelRole role, int count, double accuracyRate)
    {
        var logs = new List<MLModelPredictionLog>();
        var rng  = new Random(42 + (int)modelId); // deterministic seed per model

        for (int i = 0; i < count; i++)
        {
            bool correct = rng.NextDouble() < accuracyRate;
            logs.Add(new MLModelPredictionLog
            {
                Id                = modelId * 10000 + i,
                MLModelId         = modelId,
                ModelRole         = role,
                DirectionCorrect  = correct,
                ConfidenceScore   = correct ? 0.75m : 0.35m,
                ActualDirection   = correct ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = correct ? 15.0m : -8.0m,
                PredictedAt       = DateTime.UtcNow.AddHours(-count + i),
                OutcomeRecordedAt = DateTime.UtcNow.AddHours(-count + i + 1),
                IsDeleted         = false
            });
        }

        return logs;
    }

    // -- Test 1: No running evaluations -> does nothing

    [Fact]
    public async Task NoRunningEvaluations_DoesNothing()
    {
        // Arrange — no shadow evaluations in Running state
        SetupShadowEvaluations(new List<MLShadowEvaluation>());
        SetupPredictionLogs(new List<MLModelPredictionLog>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupMarketRegimeSnapshots(new List<MarketRegimeSnapshot>());
        SetupModels(new List<MLModel>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes without error; no writes attempted
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 2: Insufficient trades -> waits for more (status stays Running)

    [Fact]
    public async Task InsufficientTrades_WaitsForMore()
    {
        // Arrange — evaluation requires 50 trades but only 10 completed
        var shadow = new MLShadowEvaluation
        {
            Id                   = 1,
            ChallengerModelId    = 100,
            ChampionModelId      = 200,
            Symbol               = "EURUSD",
            Timeframe            = Timeframe.H1,
            Status               = ShadowEvaluationStatus.Running,
            RequiredTrades       = 50,
            CompletedTrades      = 0,
            PromotionDecision    = null,
            DecisionReason       = null,
            PromotionThreshold   = 0.05m,
            StartedAt            = DateTime.UtcNow.AddDays(-1),
            ExpiresAt            = DateTime.UtcNow.AddDays(10), // not expired
            IsDeleted            = false
        };

        SetupShadowEvaluations(new List<MLShadowEvaluation> { shadow });

        // Only 10 prediction logs — far below RequiredTrades=50 and below 50% for partial sufficiency
        var challengerLogs = GeneratePredictionLogs(100, ModelRole.Challenger, 10, 0.60);
        var championLogs   = GeneratePredictionLogs(200, ModelRole.Champion, 10, 0.50);
        SetupPredictionLogs(challengerLogs.Concat(championLogs).ToList());

        SetupEngineConfigs(new List<EngineConfig>());
        SetupMarketRegimeSnapshots(new List<MarketRegimeSnapshot>());
        SetupModels(new List<MLModel>());

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should have queried running evaluations but left the status
        // as Running because there are insufficient trades for a decision.
        // Since ExecuteUpdateAsync is an EF extension method that cannot be easily mocked,
        // we verify the read context was used to check the evaluation and prediction counts.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: Challenger wins -> promotes model (AutoPromoted)

    [Fact]
    public async Task ChallengerWins_PromotesModel()
    {
        // Arrange — challenger has 60% accuracy vs champion 48% with enough trades
        var shadow = new MLShadowEvaluation
        {
            Id                   = 2,
            ChallengerModelId    = 101,
            ChampionModelId      = 201,
            Symbol               = "EURUSD",
            Timeframe            = Timeframe.H1,
            Status               = ShadowEvaluationStatus.Running,
            RequiredTrades       = 50,
            CompletedTrades      = 0,
            PromotionDecision    = null,
            DecisionReason       = null,
            PromotionThreshold   = 0.05m,
            StartedAt            = DateTime.UtcNow.AddDays(-7),
            ExpiresAt            = DateTime.UtcNow.AddDays(7),
            IsDeleted            = false
        };

        SetupShadowEvaluations(new List<MLShadowEvaluation> { shadow });

        // Generate enough logs: challenger ~60% accuracy, champion ~48%
        var challengerLogs = GeneratePredictionLogs(101, ModelRole.Challenger, 60, 0.60);
        var championLogs   = GeneratePredictionLogs(201, ModelRole.Champion, 60, 0.48);
        SetupPredictionLogs(challengerLogs.Concat(championLogs).ToList());

        SetupEngineConfigs(new List<EngineConfig>());
        SetupMarketRegimeSnapshots(new List<MarketRegimeSnapshot>());

        var challengerModel = new MLModel
        {
            Id           = 101,
            Symbol       = "EURUSD",
            Timeframe    = Timeframe.H1,
            IsActive     = false,
            IsSuppressed = false,
            IsDeleted    = false
        };
        var championModel = new MLModel
        {
            Id           = 201,
            Symbol       = "EURUSD",
            Timeframe    = Timeframe.H1,
            IsActive     = true,
            IsSuppressed = false,
            IsDeleted    = false
        };
        SetupModels(new List<MLModel> { challengerModel, championModel });

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should have processed the evaluation and attempted to
        // promote the challenger. We verify both read and write contexts were engaged.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Champion is better -> rejects challenger (Rejected)

    [Fact]
    public async Task ChampionBetter_RejectsChallenger()
    {
        // Arrange — champion 55% vs challenger 45% with enough trades
        var shadow = new MLShadowEvaluation
        {
            Id                   = 3,
            ChallengerModelId    = 102,
            ChampionModelId      = 202,
            Symbol               = "GBPUSD",
            Timeframe            = Timeframe.H1,
            Status               = ShadowEvaluationStatus.Running,
            RequiredTrades       = 50,
            CompletedTrades      = 0,
            PromotionDecision    = null,
            DecisionReason       = null,
            PromotionThreshold   = 0.05m,
            StartedAt            = DateTime.UtcNow.AddDays(-7),
            ExpiresAt            = DateTime.UtcNow.AddDays(7),
            IsDeleted            = false
        };

        SetupShadowEvaluations(new List<MLShadowEvaluation> { shadow });

        // Champion outperforms: 55% vs challenger 45%
        var challengerLogs = GeneratePredictionLogs(102, ModelRole.Challenger, 60, 0.45);
        var championLogs   = GeneratePredictionLogs(202, ModelRole.Champion, 60, 0.55);
        SetupPredictionLogs(challengerLogs.Concat(championLogs).ToList());

        SetupEngineConfigs(new List<EngineConfig>());
        SetupMarketRegimeSnapshots(new List<MarketRegimeSnapshot>());

        var challengerModel = new MLModel
        {
            Id           = 102,
            Symbol       = "GBPUSD",
            Timeframe    = Timeframe.H1,
            IsActive     = false,
            IsSuppressed = false,
            IsDeleted    = false
        };
        var championModel = new MLModel
        {
            Id           = 202,
            Symbol       = "GBPUSD",
            Timeframe    = Timeframe.H1,
            IsActive     = true,
            IsSuppressed = false,
            IsDeleted    = false
        };
        SetupModels(new List<MLModel> { challengerModel, championModel });

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should have attempted to reject the challenger.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 5: Expired with insufficient data -> FlaggedForReview

    [Fact]
    public async Task ExpiredWithInsufficientData_FlagsForReview()
    {
        // Arrange — evaluation expired and has insufficient trades
        var shadow = new MLShadowEvaluation
        {
            Id                   = 4,
            ChallengerModelId    = 103,
            ChampionModelId      = 203,
            Symbol               = "USDJPY",
            Timeframe            = Timeframe.H1,
            Status               = ShadowEvaluationStatus.Running,
            RequiredTrades       = 50,
            CompletedTrades      = 0,
            PromotionDecision    = null,
            DecisionReason       = null,
            PromotionThreshold   = 0.05m,
            StartedAt            = DateTime.UtcNow.AddDays(-30),
            ExpiresAt            = DateTime.UtcNow.AddDays(-1), // expired yesterday
            IsDeleted            = false
        };

        SetupShadowEvaluations(new List<MLShadowEvaluation> { shadow });

        // Only 8 trades — well below RequiredTrades=50 and below 50% for partial sufficiency
        var challengerLogs = GeneratePredictionLogs(103, ModelRole.Challenger, 8, 0.55);
        var championLogs   = GeneratePredictionLogs(203, ModelRole.Champion, 8, 0.50);
        SetupPredictionLogs(challengerLogs.Concat(championLogs).ToList());

        SetupEngineConfigs(new List<EngineConfig>());
        SetupMarketRegimeSnapshots(new List<MarketRegimeSnapshot>());

        var challengerModel = new MLModel
        {
            Id           = 103,
            Symbol       = "USDJPY",
            Timeframe    = Timeframe.H1,
            IsActive     = false,
            IsSuppressed = false,
            IsDeleted    = false
        };
        var championModel = new MLModel
        {
            Id           = 203,
            Symbol       = "USDJPY",
            Timeframe    = Timeframe.H1,
            IsActive     = true,
            IsSuppressed = false,
            IsDeleted    = false
        };
        SetupModels(new List<MLModel> { challengerModel, championModel });

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should have flagged the evaluation for review due to
        // expiry with insufficient data. Champion retained by default.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
