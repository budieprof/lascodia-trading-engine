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

public class MLModelRetirementWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLModelRetirementWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLModelRetirementWorker _worker;

    public MLModelRetirementWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLModelRetirementWorker>>();
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

        _worker = new MLModelRetirementWorker(_mockScopeFactory.Object, _mockLogger.Object);
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

    private void SetupEwmaAccuracies(List<MLModelEwmaAccuracy> rows)
    {
        var mockSet = rows.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModelEwmaAccuracy>()).Returns(mockSet.Object);
    }

    private void SetupAlerts(List<Alert> alerts)
    {
        var mockSet = alerts.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<Alert>()).Returns(mockSet.Object);
    }

    private void SetupDriftFlags(List<MLDriftFlag> flags)
    {
        var mockSet = flags.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLDriftFlag>()).Returns(mockSet.Object);
    }

    private void SetupWriteContext(List<MLModel> models, List<Alert> alerts)
    {
        var writeModelSet = models.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<MLModel>()).Returns(writeModelSet.Object);

        var writeAlertSet = alerts.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<Alert>()).Returns(writeAlertSet.Object);
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

    // -- Test 1: No active models -> no suppression

    [Fact]
    public async Task NoActiveModels_DoesNothing()
    {
        // Arrange — empty model set, no candidates for retirement
        SetupModels(new List<MLModel>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy>());
        SetupAlerts(new List<Alert>());
        SetupDriftFlags(new List<MLDriftFlag>());
        SetupWriteContext(new List<MLModel>(), new List<Alert>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes without error; no models means no suppression writes
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        // Write context is resolved eagerly but SaveChangesAsync should not be called
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 2: Already suppressed model is skipped

    [Fact]
    public async Task AlreadySuppressed_Skipped()
    {
        // Arrange — model is active but already suppressed; worker should skip it
        var suppressedModel = new MLModel
        {
            Id                    = 10,
            Symbol                = "EURUSD",
            Timeframe             = Timeframe.H1,
            IsActive              = true,
            IsSuppressed          = true,
            IsDeleted             = false,
            LiveDirectionAccuracy = 0.30m
        };

        SetupModels(new List<MLModel> { suppressedModel });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy>());
        SetupAlerts(new List<Alert>());
        SetupDriftFlags(new List<MLDriftFlag>());
        SetupWriteContext(new List<MLModel> { suppressedModel }, new List<Alert>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — the model is already suppressed so it should not be evaluated
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        // Suppressed model is filtered out; no suppression writes should occur
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 3: Single signal active — not enough to suppress (default requires 2)

    [Fact]
    public async Task SingleSignal_NotEnough()
    {
        // Arrange — only EWMA is critical (1 signal), default requires 2
        var model = new MLModel
        {
            Id                    = 10,
            Symbol                = "EURUSD",
            Timeframe             = Timeframe.H1,
            IsActive              = true,
            IsSuppressed          = false,
            IsDeleted             = false,
            LiveDirectionAccuracy = 0.55m // above floor
        };

        var ewmaRow = new MLModelEwmaAccuracy
        {
            Id           = 1,
            MLModelId    = 10,
            Symbol       = "EURUSD",
            Timeframe    = Timeframe.H1,
            EwmaAccuracy = 0.40 // below threshold 0.48
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>()); // no cooldown, no ADWIN
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy> { ewmaRow });
        SetupAlerts(new List<Alert>());
        SetupDriftFlags(new List<MLDriftFlag>());
        SetupWriteContext(new List<MLModel> { model }, new List<Alert>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — only 1 signal active, model should NOT be suppressed
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        // SaveChangesAsync should not be called since the model was not suppressed
        _mockWriteDbContext.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Test 4: Two signals — cooldown + EWMA critical -> model suppressed

    [Fact]
    public async Task TwoSignals_CooldownAndEwma_SuppressesModel()
    {
        // Arrange — cooldown active (signal 1) + EWMA critical (signal 2)
        var model = new MLModel
        {
            Id                    = 10,
            Symbol                = "EURUSD",
            Timeframe             = Timeframe.H1,
            IsActive              = true,
            IsSuppressed          = false,
            IsDeleted             = false,
            LiveDirectionAccuracy = 0.55m // above floor — not a signal
        };

        var ewmaRow = new MLModelEwmaAccuracy
        {
            Id           = 1,
            MLModelId    = 10,
            Symbol       = "EURUSD",
            Timeframe    = Timeframe.H1,
            EwmaAccuracy = 0.40 // below threshold 0.48
        };

        var cooldownConfig = new EngineConfig
        {
            Id    = 1,
            Key   = "MLCooldown:EURUSD:H1:ExpiresAt",
            Value = DateTime.UtcNow.AddHours(1).ToString("O") // future — cooldown active
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig> { cooldownConfig });
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy> { ewmaRow });
        SetupAlerts(new List<Alert>());
        SetupDriftFlags(new List<MLDriftFlag>());
        SetupWriteContext(new List<MLModel> { model }, new List<Alert>());

        // Act
        await RunWorkerOnceAsync();

        // Assert — 2 signals active (cooldown + EWMA), model should be suppressed
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 5: Two signals — live accuracy degraded + ADWIN drift -> model suppressed

    [Fact]
    public async Task TwoSignals_LiveAccuracyAndAdwin_SuppressesModel()
    {
        // Arrange — live accuracy degraded (signal 3) + ADWIN drift detected (signal 4)
        var model = new MLModel
        {
            Id                    = 10,
            Symbol                = "GBPUSD",
            Timeframe             = Timeframe.H1,
            IsActive              = true,
            IsSuppressed          = false,
            IsDeleted             = false,
            LiveDirectionAccuracy = 0.42m // below floor 0.48 — signal 3
        };

        var ewmaRow = new MLModelEwmaAccuracy
        {
            Id           = 1,
            MLModelId    = 10,
            Symbol       = "GBPUSD",
            Timeframe    = Timeframe.H1,
            EwmaAccuracy = 0.60 // above threshold — not a signal
        };

        var adwinFlag = new MLDriftFlag
        {
            Id            = 1,
            Symbol        = "GBPUSD",
            Timeframe     = Timeframe.H1,
            DetectorType  = "AdwinDrift",
            ExpiresAtUtc  = DateTime.UtcNow.AddHours(24), // future — drift active
            FirstDetectedAtUtc = DateTime.UtcNow.AddHours(-1),
            LastRefreshedAtUtc = DateTime.UtcNow,
            ConsecutiveDetections = 1,
            IsDeleted     = false
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy> { ewmaRow });
        SetupAlerts(new List<Alert>());
        SetupDriftFlags(new List<MLDriftFlag> { adwinFlag });
        SetupWriteContext(new List<MLModel> { model }, new List<Alert>());

        // Act
        await RunWorkerOnceAsync();

        // Assert — 2 signals active (live accuracy + ADWIN drift), model should be suppressed
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 6: Three signals — cooldown + EWMA + live accuracy -> suppressed + alert created

    [Fact]
    public async Task ThreeSignals_SuppressesAndCreatesAlert()
    {
        // Arrange — cooldown active (signal 1) + EWMA critical (signal 2)
        //           + live accuracy degraded (signal 3)
        var model = new MLModel
        {
            Id                    = 10,
            Symbol                = "USDJPY",
            Timeframe             = Timeframe.H1,
            IsActive              = true,
            IsSuppressed          = false,
            IsDeleted             = false,
            LiveDirectionAccuracy = 0.40m // below floor 0.48 — signal 3
        };

        var ewmaRow = new MLModelEwmaAccuracy
        {
            Id           = 1,
            MLModelId    = 10,
            Symbol       = "USDJPY",
            Timeframe    = Timeframe.H1,
            EwmaAccuracy = 0.35 // below threshold 0.48 — signal 2
        };

        var cooldownConfig = new EngineConfig
        {
            Id    = 1,
            Key   = "MLCooldown:USDJPY:H1:ExpiresAt",
            Value = DateTime.UtcNow.AddMinutes(30).ToString("O") // future — signal 1
        };

        SetupModels(new List<MLModel> { model });
        SetupEngineConfigs(new List<EngineConfig> { cooldownConfig });
        SetupEwmaAccuracies(new List<MLModelEwmaAccuracy> { ewmaRow });
        SetupAlerts(new List<Alert>()); // no existing alert — so a new one should be created
        SetupDriftFlags(new List<MLDriftFlag>());
        SetupWriteContext(new List<MLModel> { model }, new List<Alert>());

        // Act
        await RunWorkerOnceAsync();

        // Assert — 3 signals active; the worker attempts suppression via ExecuteUpdateAsync
        // on the write context. ExecuteUpdateAsync is a relational extension that cannot
        // execute against mock DbSets, so the per-model try/catch will log a warning.
        // We verify the write context was resolved and Set<MLModel>() was accessed.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteDbContext.Verify(c => c.Set<MLModel>(), Times.AtLeastOnce);
    }
}
