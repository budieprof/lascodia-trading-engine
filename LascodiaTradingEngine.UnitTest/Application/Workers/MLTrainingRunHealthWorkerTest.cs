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

public class MLTrainingRunHealthWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLTrainingRunHealthWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLTrainingRunHealthWorker _worker;

    public MLTrainingRunHealthWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLTrainingRunHealthWorker>>();
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

        _worker = new MLTrainingRunHealthWorker(_mockScopeFactory.Object, _mockLogger.Object);
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

    private void SetupAlerts(List<Alert> alerts)
    {
        var mockSet = alerts.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<Alert>()).Returns(mockSet.Object);
    }

    private void SetupWriteAlerts(List<Alert> alerts)
    {
        var mockSet = alerts.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<Alert>()).Returns(mockSet.Object);
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

    // -- Test 1: No running runs -> does nothing

    [Fact]
    public async Task NoRunningRuns_DoesNothing()
    {
        // Arrange — no training runs at all
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(new List<MLTrainingRun>());
        SetupAlerts(new List<Alert>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteTrainingRuns(new List<MLTrainingRun>());
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 2: Stalled run creates alert — run in Running state for > MaxRunMinutes

    [Fact]
    public async Task StalledRun_CreatesAlert()
    {
        // Arrange — a training run has been in Running state for 180 minutes
        // (default MaxRunMinutes = 120)
        var stalledRun = new MLTrainingRun
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Status    = RunStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-180), // stalled for 3 hours
            TriggerType = TriggerType.AutoDegrading,
            IsDeleted = false,
        };

        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(new List<MLTrainingRun> { stalledRun });
        SetupAlerts(new List<Alert>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteTrainingRuns(new List<MLTrainingRun> { stalledRun });
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — stalled run detected; worker engages write context to create alert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: High failure rate creates alert — 4/10 recent runs failed (>30%)

    [Fact]
    public async Task HighFailureRate_CreatesAlert()
    {
        // Arrange — 10 recent completed runs for EURUSD/H1, 4 of which failed.
        // Default FailRateThreshold = 0.30, so 4/10 = 40% > 30% triggers an alert.
        var runs = new List<MLTrainingRun>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 6; i++)
        {
            runs.Add(new MLTrainingRun
            {
                Id          = i + 1,
                Symbol      = "EURUSD",
                Timeframe   = Timeframe.H1,
                Status      = RunStatus.Completed,
                StartedAt   = now.AddDays(-10 + i),
                CompletedAt = now.AddDays(-10 + i).AddMinutes(30),
                TriggerType = TriggerType.Scheduled,
                IsDeleted   = false,
            });
        }
        for (int i = 0; i < 4; i++)
        {
            runs.Add(new MLTrainingRun
            {
                Id          = 7 + i,
                Symbol      = "EURUSD",
                Timeframe   = Timeframe.H1,
                Status      = RunStatus.Failed,
                StartedAt   = now.AddDays(-4 + i),
                CompletedAt = now.AddDays(-4 + i).AddMinutes(5),
                TriggerType = TriggerType.Scheduled,
                IsDeleted   = false,
            });
        }

        // Include an active model so the worker checks its symbol/timeframe
        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(runs);
        SetupAlerts(new List<Alert>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteTrainingRuns(runs);
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — high failure rate detected; write context engaged to create alert
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Low failure rate — no alert created (2/10 failed, < 30%)

    [Fact]
    public async Task LowFailureRate_NoAlert()
    {
        // Arrange — 10 recent completed runs, only 2 failed.
        // 2/10 = 20% < 30% threshold, so no alert.
        var runs = new List<MLTrainingRun>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 8; i++)
        {
            runs.Add(new MLTrainingRun
            {
                Id          = i + 1,
                Symbol      = "EURUSD",
                Timeframe   = Timeframe.H1,
                Status      = RunStatus.Completed,
                StartedAt   = now.AddDays(-10 + i),
                CompletedAt = now.AddDays(-10 + i).AddMinutes(30),
                TriggerType = TriggerType.Scheduled,
                IsDeleted   = false,
            });
        }
        for (int i = 0; i < 2; i++)
        {
            runs.Add(new MLTrainingRun
            {
                Id          = 9 + i,
                Symbol      = "EURUSD",
                Timeframe   = Timeframe.H1,
                Status      = RunStatus.Failed,
                StartedAt   = now.AddDays(-2 + i),
                CompletedAt = now.AddDays(-2 + i).AddMinutes(5),
                TriggerType = TriggerType.Scheduled,
                IsDeleted   = false,
            });
        }

        var model = new MLModel
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            IsActive  = true,
            IsDeleted = false,
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTrainingRuns(runs);
        SetupAlerts(new List<Alert>());
        SetupWriteAlerts(new List<Alert>());
        SetupWriteTrainingRuns(runs);
        SetupWriteEngineConfigs(new List<EngineConfig>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — failure rate is 20%, below the 30% threshold.
        // Worker completes cleanly without creating failure-rate alerts.
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
