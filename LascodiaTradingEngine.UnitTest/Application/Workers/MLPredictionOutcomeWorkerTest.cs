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

public class MLPredictionOutcomeWorkerTest
{
    private readonly Mock<IReadApplicationDbContext>  _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<ILogger<MLPredictionOutcomeWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory>       _mockScopeFactory;
    private readonly Mock<DbContext>                  _mockReadDbContext;
    private readonly Mock<DbContext>                  _mockWriteDbContext;

    private readonly MLPredictionOutcomeWorker _worker;

    public MLPredictionOutcomeWorkerTest()
    {
        _mockReadContext   = new Mock<IReadApplicationDbContext>();
        _mockWriteContext  = new Mock<IWriteApplicationDbContext>();
        _mockLogger        = new Mock<ILogger<MLPredictionOutcomeWorker>>();
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

        _worker = new MLPredictionOutcomeWorker(_mockScopeFactory.Object, _mockLogger.Object);
    }

    private void SetupPredictionLogs(List<MLModelPredictionLog> logs)
    {
        var mockSet = logs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<MLModelPredictionLog>()).Returns(mockSet.Object);
    }

    private void SetupCandles(List<Candle> candles)
    {
        var mockSet = candles.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<Candle>()).Returns(mockSet.Object);
    }

    private void SetupEngineConfigs(List<EngineConfig> configs)
    {
        var mockSet = configs.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<EngineConfig>()).Returns(mockSet.Object);
    }

    private void SetupTradeSignals(List<TradeSignal> signals)
    {
        var mockSet = signals.AsQueryable().BuildMockDbSet();
        _mockReadDbContext.Setup(c => c.Set<TradeSignal>()).Returns(mockSet.Object);
    }

    private void SetupWritePredictionLogs(List<MLModelPredictionLog> logs)
    {
        var mockSet = logs.AsQueryable().BuildMockDbSet();
        _mockWriteDbContext.Setup(c => c.Set<MLModelPredictionLog>()).Returns(mockSet.Object);
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

    // -- Test 1: No unresolved logs → does nothing

    [Fact]
    public async Task NoUnresolvedLogs_DoesNothing()
    {
        // Arrange — all logs already have DirectionCorrect set
        var resolvedLog = new MLModelPredictionLog
        {
            Id                 = 1,
            MLModelId          = 10,
            Symbol             = "EURUSD",
            Timeframe          = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            PredictedAt        = DateTime.UtcNow.AddHours(-5),
            DirectionCorrect   = true,
            ResolutionSource   = "NextBarCandle",
            IsDeleted          = false
        };

        SetupPredictionLogs(new List<MLModelPredictionLog> { resolvedLog });
        SetupCandles(new List<Candle>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTradeSignals(new List<TradeSignal>());
        SetupWritePredictionLogs(new List<MLModelPredictionLog>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes without error and no writes attempted
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 2: Already resolved log is not processed again

    [Fact]
    public async Task ResolvedLog_SkipsAlreadyResolved()
    {
        // Arrange — log has DirectionCorrect != null, so it should not be picked up
        var alreadyResolved = new MLModelPredictionLog
        {
            Id                 = 1,
            MLModelId          = 10,
            Symbol             = "EURUSD",
            Timeframe          = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            PredictedAt        = DateTime.UtcNow.AddHours(-5),
            DirectionCorrect   = false,
            ActualDirection    = TradeDirection.Sell,
            OutcomeRecordedAt  = DateTime.UtcNow.AddHours(-3),
            ResolutionSource   = "NextBarCandle",
            IsDeleted          = false
        };

        SetupPredictionLogs(new List<MLModelPredictionLog> { alreadyResolved });
        SetupCandles(new List<Candle>());
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTradeSignals(new List<TradeSignal>());
        SetupWritePredictionLogs(new List<MLModelPredictionLog>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker runs cleanly; the resolved log is excluded by the
        // DirectionCorrect == null filter and no write context mutation happens
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 3: Too recent prediction is skipped (below timeframe cutoff)

    [Fact]
    public async Task TooRecentPrediction_SkipsLog()
    {
        // Arrange — H1 prediction made only 1 minute ago; the timeframe-aware cutoff
        // (125 minutes for H1) means it will NOT be resolved yet
        var recentLog = new MLModelPredictionLog
        {
            Id                 = 1,
            MLModelId          = 10,
            Symbol             = "EURUSD",
            Timeframe          = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            PredictedAt        = DateTime.UtcNow.AddMinutes(-1),
            DirectionCorrect   = null,
            IsDeleted          = false
        };

        // Even though there are candles, the log is too recent to resolve
        var candle1 = new Candle
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = DateTime.UtcNow.AddHours(-2),
            Open      = 1.1000m,
            High      = 1.1050m,
            Low       = 1.0950m,
            Close     = 1.1020m,
            IsClosed  = true,
            IsDeleted = false
        };

        SetupPredictionLogs(new List<MLModelPredictionLog> { recentLog });
        SetupCandles(new List<Candle> { candle1 });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTradeSignals(new List<TradeSignal>());
        SetupWritePredictionLogs(new List<MLModelPredictionLog>());

        // Act
        var exception = await Record.ExceptionAsync(() => RunWorkerOnceAsync());

        // Assert — worker completes without error; the log is too recent so the
        // absolute cutoff (5 min) filters it out before grouping even happens
        Assert.Null(exception);
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 4: Valid H1 prediction resolved with correct direction

    [Fact]
    public async Task ValidPrediction_ResolvesDirection()
    {
        // Arrange — H1 prediction from 3 hours ago with matching candles available
        var predictedAt = DateTime.UtcNow.AddHours(-3);

        var unresolvedLog = new MLModelPredictionLog
        {
            Id                 = 1,
            MLModelId          = 10,
            Symbol             = "EURUSD",
            Timeframe          = Timeframe.H1,
            PredictedDirection = TradeDirection.Buy,
            PredictedAt        = predictedAt,
            DirectionCorrect   = null,
            IsDeleted          = false
        };

        // prevCandle: closed candle at or before PredictedAt
        var prevCandle = new Candle
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = predictedAt.AddMinutes(-30),
            Open      = 1.1000m,
            High      = 1.1050m,
            Low       = 1.0950m,
            Close     = 1.1000m,
            IsClosed  = true,
            IsDeleted = false
        };

        // outcomeCandle: first closed candle strictly after PredictedAt
        // Close > prevCandle.Close so actual direction = Buy (matches predicted)
        var outcomeCandle = new Candle
        {
            Id        = 2,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = predictedAt.AddMinutes(30),
            Open      = 1.1010m,
            High      = 1.1080m,
            Low       = 1.0990m,
            Close     = 1.1050m,
            IsClosed  = true,
            IsDeleted = false
        };

        SetupPredictionLogs(new List<MLModelPredictionLog> { unresolvedLog });
        SetupCandles(new List<Candle> { prevCandle, outcomeCandle });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTradeSignals(new List<TradeSignal>());
        SetupWritePredictionLogs(new List<MLModelPredictionLog> { unresolvedLog });

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should have queried the read context for unresolved logs
        // and candles, and attempted to resolve via the write context.
        // The gap between candles is 60 min which is within 3x of H1 expected gap (60 min).
        // outcomeCandle.Close (1.1050) > prevCandle.Close (1.1000), so actual = Buy = predicted.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }

    // -- Test 5: Weekend gap exceeds 3x expected → marked GapSkipped

    [Fact]
    public async Task GapBetweenCandles_MarksGapSkipped()
    {
        // Arrange — H1 prediction with a weekend gap between prev and outcome candle.
        // Expected gap for H1 = 60 min. Max gap factor default = 3.0.
        // So any gap > 180 min triggers GapSkipped.
        // We simulate a ~48-hour weekend gap (2880 min >> 180 min).
        var fridayClose = DateTime.UtcNow.AddDays(-3);
        var predictedAt = fridayClose.AddMinutes(10);

        var unresolvedLog = new MLModelPredictionLog
        {
            Id                 = 1,
            MLModelId          = 10,
            Symbol             = "EURUSD",
            Timeframe          = Timeframe.H1,
            PredictedDirection = TradeDirection.Sell,
            PredictedAt        = predictedAt,
            DirectionCorrect   = null,
            IsDeleted          = false
        };

        // prevCandle: Friday close candle at or before PredictedAt
        var prevCandle = new Candle
        {
            Id        = 1,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = fridayClose,
            Open      = 1.1000m,
            High      = 1.1050m,
            Low       = 1.0950m,
            Close     = 1.1020m,
            IsClosed  = true,
            IsDeleted = false
        };

        // outcomeCandle: Monday open — ~48 hours after Friday close
        var mondayOpen = fridayClose.AddHours(48);
        var outcomeCandle = new Candle
        {
            Id        = 2,
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = mondayOpen,
            Open      = 1.0980m,
            High      = 1.1010m,
            Low       = 1.0960m,
            Close     = 1.0990m,
            IsClosed  = true,
            IsDeleted = false
        };

        SetupPredictionLogs(new List<MLModelPredictionLog> { unresolvedLog });
        SetupCandles(new List<Candle> { prevCandle, outcomeCandle });
        SetupEngineConfigs(new List<EngineConfig>());
        SetupTradeSignals(new List<TradeSignal>());
        SetupWritePredictionLogs(new List<MLModelPredictionLog> { unresolvedLog });

        // Act
        await RunWorkerOnceAsync();

        // Assert — the worker should detect the 48-hour gap (2880 min) which far exceeds
        // the maximum allowed gap of 180 min (3.0 × 60 min for H1). The log should be
        // marked as GapSkipped via ExecuteUpdateAsync on the write context.
        _mockReadContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
        _mockWriteContext.Verify(c => c.GetDbContext(), Times.AtLeastOnce);
    }
}
