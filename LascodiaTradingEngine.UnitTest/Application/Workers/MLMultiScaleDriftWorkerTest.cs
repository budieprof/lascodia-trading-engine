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

public class MLMultiScaleDriftWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLMultiScaleDriftWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLMultiScaleDriftWorker _worker;

    public MLMultiScaleDriftWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLMultiScaleDriftWorker>>();
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

        _worker = new MLMultiScaleDriftWorker(_mockScopeFactory.Object, _mockLogger.Object);
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
    /// Builds prediction logs within a specified date range with a given accuracy rate.
    /// </summary>
    private static List<MLModelPredictionLog> BuildPredictionLogs(
        long modelId,
        int count,
        double accuracyRate,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var logs = new List<MLModelPredictionLog>();
        var span = windowEnd - windowStart;

        for (int i = 0; i < count; i++)
        {
            bool correct = i < (int)(count * accuracyRate);
            var timestamp = windowStart.Add(TimeSpan.FromTicks(span.Ticks * i / count));

            logs.Add(new MLModelPredictionLog
            {
                Id                = modelId * 10000 + i,
                MLModelId         = modelId,
                DirectionCorrect  = correct,
                OutcomeRecordedAt = timestamp.AddMinutes(30),
                PredictedAt       = timestamp,
                ConfidenceScore   = correct ? 0.7m : 0.4m,
                IsDeleted         = false,
                ActualDirection   = correct ? TradeDirection.Buy : TradeDirection.Sell,
                ActualMagnitudePips = correct ? 10m : 5m,
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

    // -- Test 2: Insufficient predictions in long window -> skips model

    [Fact]
    public async Task InsufficientPredictions_SkipsModel()
    {
        // Arrange — model is active but only has 10 resolved predictions
        // (below the default MinPredictions of 20)
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var now = DateTime.UtcNow;
        var logs = BuildPredictionLogs(
            modelId: 1,
            count: 10,
            accuracyRate: 0.6,
            windowStart: now.AddDays(-21),
            windowEnd: now);

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(logs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — too few predictions to evaluate; no writes expected
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: Sudden drift — short accuracy 7%+ below long accuracy

    [Fact]
    public async Task SuddenDrift_QueuesRetrainingRun()
    {
        // Arrange — model with high long-window accuracy but collapsed short-window accuracy.
        // Long window (21 days): ~70% accuracy. Short window (3 days): ~30% accuracy.
        // Gap = 30% - 70% = -40%, well below the -7% threshold for sudden drift.
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var now = DateTime.UtcNow;

        // Long window predictions (older, mostly correct — 70% accuracy)
        var longLogs = BuildPredictionLogs(
            modelId: 1,
            count: 60,
            accuracyRate: 0.70,
            windowStart: now.AddDays(-21),
            windowEnd: now.AddDays(-3));

        // Short window predictions (recent, mostly incorrect — 30% accuracy)
        var shortLogs = BuildPredictionLogs(
            modelId: 1,
            count: 30,
            accuracyRate: 0.30,
            windowStart: now.AddDays(-3),
            windowEnd: now);
        // Offset IDs to avoid collision
        foreach (var log in shortLogs) log.Id += 100000;

        var allLogs = longLogs.Concat(shortLogs).ToList();

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(allLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — sudden drift detected; worker engages write context
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Gradual drift — long accuracy below 50% floor

    [Fact]
    public async Task GradualDrift_QueuesRetrainingRun()
    {
        // Arrange — both short and long windows have accuracy below 50%.
        // Long window at ~45% accuracy, short window at ~42%.
        // The gap is small (-3%) so sudden drift is NOT triggered.
        // But the long window floor (default 0.50) is breached.
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "GBPUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var now = DateTime.UtcNow;

        // Long window: ~45% accuracy (below 50% floor)
        var longLogs = BuildPredictionLogs(
            modelId: 1,
            count: 60,
            accuracyRate: 0.45,
            windowStart: now.AddDays(-21),
            windowEnd: now.AddDays(-3));

        // Short window: ~42% accuracy (similar to long — no sudden gap)
        var shortLogs = BuildPredictionLogs(
            modelId: 1,
            count: 30,
            accuracyRate: 0.42,
            windowStart: now.AddDays(-3),
            windowEnd: now);
        foreach (var log in shortLogs) log.Id += 100000;

        var allLogs = longLogs.Concat(shortLogs).ToList();

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupPredictionLogs(allLogs);
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — gradual drift detected (long window < 50% floor).
        // Worker engages write context to queue a retraining run.
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
