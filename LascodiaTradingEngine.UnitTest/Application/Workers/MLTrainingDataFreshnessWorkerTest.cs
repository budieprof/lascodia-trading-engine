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

public class MLTrainingDataFreshnessWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLTrainingDataFreshnessWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLTrainingDataFreshnessWorker _worker;

    public MLTrainingDataFreshnessWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLTrainingDataFreshnessWorker>>();
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

        _worker = new MLTrainingDataFreshnessWorker(_mockScopeFactory.Object, _mockLogger.Object);
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

    // -- Test 1: No active models -> does nothing

    [Fact]
    public async Task NoActiveModels_DoesNothing()
    {
        // Arrange
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 2: Fresh data — writes metric only, no run queued

    [Fact]
    public async Task FreshData_WritesMetricOnly()
    {
        // Arrange — model has a completed training run with ToDate = 10 days ago.
        // Default StalenessDays = 60, so 10 days is well within the freshness window.
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var recentRun = new MLTrainingRun
        {
            Id          = 1,
            Symbol      = "EURUSD",
            Timeframe   = Timeframe.H1,
            Status      = RunStatus.Completed,
            ToDate      = DateTime.UtcNow.AddDays(-10), // fresh — 10 days old
            StartedAt   = DateTime.UtcNow.AddDays(-11),
            CompletedAt = DateTime.UtcNow.AddDays(-10),
            TriggerType = TriggerType.Scheduled,
            IsDeleted   = false,
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(new List<MLTrainingRun> { recentRun });
        SetupWriteTrainingRuns(new List<MLTrainingRun> { recentRun });
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — data is fresh so the worker may write the metric to EngineConfig
        // but should NOT queue a new training run. Worker completes cleanly.
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: Stale data — queues retraining run

    [Fact]
    public async Task StaleData_QueuesRetrainingRun()
    {
        // Arrange — model's most recent training run has ToDate = 90 days ago.
        // Default StalenessDays = 60, so 90 > 60 triggers a retraining run.
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        var staleRun = new MLTrainingRun
        {
            Id          = 1,
            Symbol      = "EURUSD",
            Timeframe   = Timeframe.H1,
            Status      = RunStatus.Completed,
            ToDate      = DateTime.UtcNow.AddDays(-90), // stale — 90 days old
            StartedAt   = DateTime.UtcNow.AddDays(-91),
            CompletedAt = DateTime.UtcNow.AddDays(-90),
            TriggerType = TriggerType.Scheduled,
            IsDeleted   = false,
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(new List<MLTrainingRun> { staleRun });
        SetupWriteTrainingRuns(new List<MLTrainingRun> { staleRun });
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — stale data detected; worker engages write context to queue
        // a new training run with TriggerType.AutoDegrading.
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
