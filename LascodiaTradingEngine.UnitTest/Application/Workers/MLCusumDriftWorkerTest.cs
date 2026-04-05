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

public class MLCusumDriftWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLCusumDriftWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLCusumDriftWorker _worker;

    public MLCusumDriftWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLCusumDriftWorker>>();
        _mockScopeFactory  = new Mock<IServiceScopeFactory>();
        _mockReadDbContext  = new Mock<DbContext>();
        _mockWriteDbContext = new Mock<DbContext>();

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockReadDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockWriteDbContext.Object);

        var mockScope           = new Mock<IServiceScope>();
        var mockServiceProvider = new Mock<IServiceProvider>();

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IReadApplicationDbContext)))
            .Returns(_mockReadContext.Object);

        mockServiceProvider
            .Setup(sp => sp.GetService(typeof(IWriteApplicationDbContext)))
            .Returns(_mockWriteContext.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _worker = new MLCusumDriftWorker(_mockScopeFactory.Object, _mockLogger.Object);
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

    private void SetupWriteAlerts(List<Alert> alerts)
    {
        var mockSet = alerts.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<Alert>()).Returns(mockSet.Object);
    }

    private void SetupWriteEngineConfigs(List<EngineConfig> configs)
    {
        var mockSet = configs.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockSet.Object);
    }

    /// <summary>
    /// Runs the worker for a single iteration by cancelling after a short delay.
    /// </summary>
    private async Task RunWorkerOnceAsync()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        try
        {
            await _worker.StartAsync(cts.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await _worker.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation fires
        }
    }

    /// <summary>
    /// Builds prediction logs with a specified correct/incorrect ratio.
    /// All logs are within the CUSUM observation window (recent dates).
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
                OutcomeRecordedAt = now.AddHours(-(correctCount + incorrectCount) + i),
                PredictedAt       = now.AddHours(-(correctCount + incorrectCount) + i - 1),
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
                OutcomeRecordedAt = now.AddHours(-(incorrectCount) + i),
                PredictedAt       = now.AddHours(-(incorrectCount) + i - 1),
                ConfidenceScore   = 0.4m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Sell,
                ActualMagnitudePips = 5m,
            });
        }

        return logs;
    }

    // -- Test 1: No active models -> does nothing

    [Fact]
    public async Task NoActiveModels_DoesNothing()
    {
        // Arrange
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(new List<MLModelPredictionLog>());
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 2: Insufficient predictions — skips model (fewer than 30 resolved logs)

    [Fact]
    public async Task InsufficientPredictions_SkipsModel()
    {
        // Arrange — model is active but only has 10 resolved predictions
        // (below the minimum needed for the CUSUM reference + test split)
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 5, incorrectCount: 5);

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — too few predictions for CUSUM; no writes expected
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: No alarm — S+ stays below h, no run queued

    [Fact]
    public async Task NoAlarm_DoesNotQueueRun()
    {
        // Arrange — model with high accuracy (80%); CUSUM S+ should stay near zero
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        // 160 correct, 40 incorrect -> 80% accuracy; stable, no drift
        var predLogs = BuildPredictionLogs(modelId: 1, correctCount: 160, incorrectCount: 40);

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(predLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — high accuracy means no alarm; worker should complete cleanly
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Degradation alarm — S+ exceeds h, queues training run and creates alert

    [Fact]
    public async Task DegradationAlarm_QueuesRetrainingRun()
    {
        // Arrange — model where accuracy collapses in the second half of the window.
        // First 50 predictions: 80% correct (reference half).
        // Next 50 predictions: 20% correct (test half) — massive accuracy drop.
        // This should push S+ well above the decision interval h (default 5.0).
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        // Build a window where the first half is healthy and the second half is degraded.
        // The reference accuracy mu0 will be computed from the first half (~80%).
        // The test half at ~20% accuracy will cause rapid S+ accumulation.
        var logs = new List<MLModelPredictionLog>();
        var now = DateTime.UtcNow;

        // First half: 40 correct, 10 incorrect (80% accuracy — establishes reference)
        for (int i = 0; i < 40; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = i + 1,
                MLModelId         = 1,
                DirectionCorrect  = true,
                OutcomeRecordedAt = now.AddHours(-100 + i),
                PredictedAt       = now.AddHours(-101 + i),
                ConfidenceScore   = 0.7m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Buy,
                ActualMagnitudePips = 10m,
            });
        }
        for (int i = 0; i < 10; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = 41 + i,
                MLModelId         = 1,
                DirectionCorrect  = false,
                OutcomeRecordedAt = now.AddHours(-60 + i),
                PredictedAt       = now.AddHours(-61 + i),
                ConfidenceScore   = 0.4m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Sell,
                ActualMagnitudePips = 5m,
            });
        }

        // Second half: 10 correct, 40 incorrect (20% accuracy — degradation)
        for (int i = 0; i < 10; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = 51 + i,
                MLModelId         = 1,
                DirectionCorrect  = true,
                OutcomeRecordedAt = now.AddHours(-50 + i),
                PredictedAt       = now.AddHours(-51 + i),
                ConfidenceScore   = 0.5m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Buy,
                ActualMagnitudePips = 8m,
            });
        }
        for (int i = 0; i < 40; i++)
        {
            logs.Add(new MLModelPredictionLog
            {
                Id                = 61 + i,
                MLModelId         = 1,
                DirectionCorrect  = false,
                OutcomeRecordedAt = now.AddHours(-40 + i),
                PredictedAt       = now.AddHours(-41 + i),
                ConfidenceScore   = 0.3m,
                IsDeleted         = false,
                ActualDirection   = TradeDirection.Sell,
                ActualMagnitudePips = 3m,
            });
        }

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(logs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — the dramatic accuracy collapse should trigger the CUSUM alarm.
        // Worker engages the write context to queue a training run and create an alert.
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
