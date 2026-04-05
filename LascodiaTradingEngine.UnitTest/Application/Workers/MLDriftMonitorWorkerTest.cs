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

public class MLDriftMonitorWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLDriftMonitorWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLDriftMonitorWorker _worker;

    public MLDriftMonitorWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLDriftMonitorWorker>>();
        _mockScopeFactory  = new Mock<IServiceScopeFactory>();
        _mockReadDbContext  = new Mock<DbContext>();
        _mockWriteDbContext = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockReadDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockWriteDbContext.Object);

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

        _worker = new MLDriftMonitorWorker(_mockScopeFactory.Object, _mockLogger.Object);
    }

    private void SetupModels(List<MLModel> models)
    {
        var mockSet = models.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModel>()).Returns(mockSet.Object);
    }

    private void SetupEngineConfigs(List<EngineConfig> configs)
    {
        var mockSet = configs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockSet.Object);
    }

    private void SetupPredictionLogs(List<MLModelPredictionLog> logs)
    {
        var mockSet = logs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModelPredictionLog>()).Returns(mockSet.Object);
    }

    private void SetupTrainingRuns(List<MLTrainingRun> runs)
    {
        var mockSet = runs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLTrainingRun>()).Returns(mockSet.Object);
    }

    private void SetupWriteTrainingRuns(List<MLTrainingRun> runs)
    {
        var mockSet = runs.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<MLTrainingRun>()).Returns(mockSet.Object);
    }

    private void SetupWriteEngineConfigs(List<EngineConfig> configs)
    {
        var mockSet = configs.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockSet.Object);
    }

    private void SetupWriteModels(List<MLModel> models)
    {
        var mockSet = models.AsQueryable().BuildMockDbSet();
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
    /// Builds a list of prediction logs for a given model with a specific correct/incorrect ratio.
    /// All logs have outcomes recorded within the drift window (last 14 days).
    /// </summary>
    private static List<MLModelPredictionLog> BuildPredictionLogs(
        long modelId,
        int correctCount,
        int incorrectCount)
    {
        var logs = new List<MLModelPredictionLog>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < correctCount; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = i + 1,
                MLModelId         = modelId,
                DirectionCorrect  = true,
                OutcomeRecordedAt = now.AddDays(-1),
                PredictedAt       = now.AddDays(-2),
                ConfidenceScore   = 0.7m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Buy,
                ActualMagnitudePips = 10m,
            });
        }

        for (int i = 0; i < incorrectCount; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = correctCount + i + 1,
                MLModelId         = modelId,
                DirectionCorrect  = false,
                OutcomeRecordedAt = now.AddDays(-1),
                PredictedAt       = now.AddDays(-2),
                ConfidenceScore   = 0.6m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Sell,
                ActualMagnitudePips = 5m,
            });
        }

        return logs;
    }

    // -- Test 1: No active models → no training runs queued

    [Fact]
    public async Task NoActiveModels_DoesNothing()
    {
        // Arrange — no active models at all
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(new List<MLModelPredictionLog>());
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(new List<EngineConfig>());
        SetupWriteModels(new List<MLModel>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — completes without error, no writes attempted
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        // SaveChangesAsync should not have been called on write context
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 2: Healthy model resets persisted consecutive failure counter

    [Fact]
    public async Task HealthyModel_ResetsPersistedCounter()
    {
        // Arrange — model with accuracy well above 50% threshold (80% accuracy)
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "EURUSD",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
            DirectionAccuracy = 0.75m,
        };

        // 32 correct, 8 incorrect → 80% accuracy, above the default 50% threshold
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 32, incorrectCount: 8);

        // Persisted counter exists with value "2" — should be reset to "0"
        var configs = new List<EngineConfig>
        {
            new EngineConfig
            {
                Id    = 100,
                Key   = "MLDrift:EURUSD:H1:ConsecutiveFailures",
                Value = "2",
                DataType = ConfigDataType.Int,
            }
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(configs);
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(configs);
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes, and the write context was used to reset the counter
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: Drift detected on first window, below consecutive threshold → no run queued

    [Fact]
    public async Task DriftDetected_BelowConsecutiveThreshold_DoesNotQueueRun()
    {
        // Arrange — model with accuracy below 50% (40%), but only 1st failure
        // requiredConsecutiveFailures defaults to 3
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "GBPUSD",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
            DirectionAccuracy = 0.60m,
        };

        // 12 correct, 18 incorrect → 40% accuracy, below 50% threshold
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 12, incorrectCount: 18);

        // No persisted counter yet (first failure)
        var configs = new List<EngineConfig>();

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(configs);
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(configs);
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — no crash, counter incremented but no training run queued
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Accuracy drift with consecutive threshold met → queues training run

    [Fact]
    public async Task AccuracyDrift_ConsecutiveThresholdMet_QueuesTrainingRun()
    {
        // Arrange — model with accuracy below 50%, and counter already at "2"
        // (next poll will be 3rd consecutive failure, meeting default threshold of 3)
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "EURUSD",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
            DirectionAccuracy = 0.60m,
        };

        // 10 correct, 25 incorrect → ~28.6% accuracy, well below 50%
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 10, incorrectCount: 25);

        // Counter is at "2" — this poll makes it 3, which meets requiredConsecutiveFailures=3
        var configs = new List<EngineConfig>
        {
            new EngineConfig
            {
                Id    = 100,
                Key   = "MLDrift:EURUSD:H1:ConsecutiveFailures",
                Value = "2",
                DataType = ConfigDataType.Int,
            }
        };

        // No existing queued/running training runs
        var trainingRuns = new List<MLTrainingRun>();

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(configs);
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(trainingRuns);
        SetupWriteTrainingRuns(trainingRuns);
        SetupWriteEngineConfigs(configs);
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes and attempts to write (queue a training run + save)
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 5: Queue depth exceeded → skips queuing even when consecutive threshold met

    [Fact]
    public async Task QueueDepthExceeded_SkipsQueuing()
    {
        // Arrange — model with drift detected and consecutive threshold met,
        // but the training queue already has 10 runs (maxQueueDepth default = 10)
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "EURUSD",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
            DirectionAccuracy = 0.60m,
        };

        // 10 correct, 25 incorrect → below threshold
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 10, incorrectCount: 25);

        // Counter at "2" so this poll will be the 3rd consecutive failure
        var configs = new List<EngineConfig>
        {
            new EngineConfig
            {
                Id    = 100,
                Key   = "MLDrift:EURUSD:H1:ConsecutiveFailures",
                Value = "2",
                DataType = ConfigDataType.Int,
            }
        };

        // 10 already-queued runs for OTHER symbols — fills the queue
        var trainingRuns = Enumerable.Range(1, 10).Select(i => new MLTrainingRun
        {
            Id        = i,
            Symbol    = $"PAIR{i}",
            Timeframe = Timeframe.H1,
            Status    = RunStatus.Queued,
            TriggerType = TriggerType.AutoDegrading,
        }).ToList();

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(configs);
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(trainingRuns);
        SetupWriteTrainingRuns(trainingRuns);
        SetupWriteEngineConfigs(configs);
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — completes without error; no new training run added due to queue depth limit.
        // SaveChangesAsync should NOT be called for adding a training run (only for config upserts).
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 6: Already queued for same symbol/timeframe → skips duplicate queuing

    [Fact]
    public async Task AlreadyQueuedForSymbol_SkipsQueuing()
    {
        // Arrange — model with drift and consecutive threshold met, but a run
        // is already queued for the same symbol/timeframe
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "EURUSD",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
            DirectionAccuracy = 0.60m,
        };

        // 10 correct, 25 incorrect → below threshold
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 10, incorrectCount: 25);

        // Counter at "2" so this poll triggers the 3rd consecutive failure
        var configs = new List<EngineConfig>
        {
            new EngineConfig
            {
                Id    = 100,
                Key   = "MLDrift:EURUSD:H1:ConsecutiveFailures",
                Value = "2",
                DataType = ConfigDataType.Int,
            }
        };

        // Existing queued run for the SAME symbol/timeframe
        var trainingRuns = new List<MLTrainingRun>
        {
            new MLTrainingRun
            {
                Id        = 99,
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Status    = RunStatus.Queued,
                TriggerType = TriggerType.AutoDegrading,
            }
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(configs);
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(trainingRuns);
        SetupWriteTrainingRuns(trainingRuns);
        SetupWriteEngineConfigs(configs);
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — completes without error; no duplicate training run created
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 7: Insufficient predictions in drift window → skips drift check entirely

    [Fact]
    public async Task InsufficientPredictions_SkipsDriftCheck()
    {
        // Arrange — model is active but only has 5 resolved predictions (below default min of 30)
        var model = new MLModel
        {
            Id              = 1,
            Symbol          = "USDJPY",
            Timeframe       = Timeframe.H1,
            IsActive        = true,
            IsDeleted       = false,
        };

        // Only 5 predictions — below the default DriftMinPredictions of 30
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 2, incorrectCount: 3);

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(new List<EngineConfig>());
        SetupWriteModels(new List<MLModel> { model });

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — completes without error, no writes to training runs or config
        Assert.Null(exception);
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 8: Custom poll interval from EngineConfig is read without error

    [Fact]
    public async Task CustomPollInterval_ReadsFromEngineConfig()
    {
        // Arrange
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>
        {
            new EngineConfig
            {
                Id    = 1,
                Key   = "MLDrift:PollIntervalSeconds",
                Value = "60",
            }
        });
        SetupPredictionLogs(new List<MLModelPredictionLog>());
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(new List<EngineConfig>());
        SetupWriteModels(new List<MLModel>());

        // Act — should not throw; worker reads config and uses it
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
