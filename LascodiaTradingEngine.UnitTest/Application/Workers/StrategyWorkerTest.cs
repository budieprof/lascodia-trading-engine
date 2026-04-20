using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.EventBus.Abstractions;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.TradeSignals.Commands.CreateTradeSignal;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class StrategyWorkerTest : IDisposable
{
    private readonly Mock<ILogger<StrategyWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IEventBus> _mockEventBus;
    private readonly Mock<IStrategyEvaluator> _mockEvaluator;
    private readonly Mock<IDistributedLock> _mockDistributedLock;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<ISessionFilter> _mockSessionFilter;
    private readonly Mock<INewsFilter> _mockNewsFilter;
    private readonly Mock<IMLSignalScorer> _mockMlScorer;
    private readonly Mock<ILivePriceCache> _mockPriceCache;
    private readonly Mock<IMultiTimeframeFilter> _mockMtfFilter;
    private readonly Mock<IPortfolioCorrelationChecker> _mockCorrelation;
    private readonly Mock<IHawkesSignalFilter> _mockHawkes;
    private readonly Mock<IIntegrationEventService> _mockEventService;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly StrategyEvaluatorOptions _options;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;
    private readonly StrategyWorker _worker;
    private readonly StrategyMetricsCache _strategyMetricsCache;
    private readonly Mock<IMarketHoursCalendar> _mockMarketHoursCalendar;
    private readonly StrategyRegimeParamsCache _regimeParamsCache;
    private readonly Mock<ISignalRejectionAuditor> _mockRejectionAuditor;
    private readonly MarketRegimeCache _marketRegimeCache;
    private readonly EngineConfigCache _engineConfigCache;
    private readonly Mock<IKillSwitchService> _mockKillSwitch;
    private readonly Mock<IDegradationModeManager> _mockDegradationManager;
    private readonly Mock<IExternalServiceCircuitBreaker> _mockCircuitBreaker;
    private readonly DbOperationBulkhead _bulkhead;

    public StrategyWorkerTest()
    {
        _mockLogger = new Mock<ILogger<StrategyWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockEventBus = new Mock<IEventBus>();
        _mockEvaluator = new Mock<IStrategyEvaluator>();
        _mockDistributedLock = new Mock<IDistributedLock>();
        _mockReadContext = new Mock<IReadApplicationDbContext>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();
        _mockMediator = new Mock<IMediator>();
        _mockSessionFilter = new Mock<ISessionFilter>();
        _mockNewsFilter = new Mock<INewsFilter>();
        _mockMlScorer = new Mock<IMLSignalScorer>();
        _mockPriceCache = new Mock<ILivePriceCache>();
        _mockMtfFilter = new Mock<IMultiTimeframeFilter>();
        _mockCorrelation = new Mock<IPortfolioCorrelationChecker>();
        _mockHawkes = new Mock<IHawkesSignalFilter>();
        _mockEventService = new Mock<IIntegrationEventService>();
        _mockDbContext = new Mock<DbContext>();
        _meterFactory = new TestMeterFactory();
        _metrics = new TradingMetrics(_meterFactory);

        _options = new StrategyEvaluatorOptions
        {
            MaxTickAgeSeconds = 10,
            MaxEAHeartbeatAgeSeconds = 60,
            SignalCooldownSeconds = 0,
            MaxConsecutiveFailures = 3,
            CircuitBreakerRecoverySeconds = 300,
            AllowedSessions = new List<TradingSession>(),
            NewsBlackoutMinutesBefore = 0,
            NewsBlackoutMinutesAfter = 0,
            BlockedRegimes = new List<MarketRegimeEnum>(),
            RequireMultiTimeframeConfirmation = false,
            MaxCorrelatedPositions = 0,
            HawkesRecentSignalCount = 0,
            MinAbstentionScore = 0,
            MaxParallelStrategies = 1,
            ExpirySweepBatchSize = 100,
        };

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);

        // Default evaluator matches StrategyType.MovingAverageCrossover
        _mockEvaluator.Setup(e => e.StrategyType).Returns(StrategyType.MovingAverageCrossover);
        _mockEvaluator.Setup(e => e.MinRequiredCandles(It.IsAny<Strategy>())).Returns(5);

        // Default lock always acquired
        var mockHandle = new Mock<IAsyncDisposable>();
        mockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHandle.Object);
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHandle.Object);
        _mockDistributedLock
            .Setup(l => l.TryAcquireAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockHandle.Object);

        // Default ML scorer returns neutral result
        _mockMlScorer.Setup(s => s.ScoreAsync(
                It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MLScoreResult(null, null, null, null));

        // Default mediator returns success
        _mockMediator
            .Setup(m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));
        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        // Default price cache
        _mockPriceCache.Setup(p => p.Get(It.IsAny<string>()))
            .Returns((1.1000m, 1.1002m, DateTime.UtcNow));

        // Wire up IServiceScopeFactory
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(ISessionFilter))).Returns(_mockSessionFilter.Object);
        mockProvider.Setup(p => p.GetService(typeof(INewsFilter))).Returns(_mockNewsFilter.Object);
        mockProvider.Setup(p => p.GetService(typeof(IMLSignalScorer))).Returns(_mockMlScorer.Object);
        mockProvider.Setup(p => p.GetService(typeof(ILivePriceCache))).Returns(_mockPriceCache.Object);
        mockProvider.Setup(p => p.GetService(typeof(IMultiTimeframeFilter))).Returns(_mockMtfFilter.Object);
        mockProvider.Setup(p => p.GetService(typeof(IPortfolioCorrelationChecker))).Returns(_mockCorrelation.Object);
        mockProvider.Setup(p => p.GetService(typeof(IHawkesSignalFilter))).Returns(_mockHawkes.Object);
        mockProvider.Setup(p => p.GetService(typeof(IIntegrationEventService))).Returns(_mockEventService.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        _strategyMetricsCache = new StrategyMetricsCache(_metrics, TimeProvider.System);
        _mockMarketHoursCalendar = new Mock<IMarketHoursCalendar>();
        _mockMarketHoursCalendar
            .Setup(c => c.IsMarketClosed(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);
        _mockMarketHoursCalendar
            .Setup(c => c.NextMarketOpen(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns<string, DateTime>((_, t) => t);
        _regimeParamsCache = new StrategyRegimeParamsCache(_metrics, TimeProvider.System);
        _mockRejectionAuditor = new Mock<ISignalRejectionAuditor>();
        _mockRejectionAuditor
            .Setup(a => a.RecordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _marketRegimeCache = new MarketRegimeCache(_metrics, TimeProvider.System);
        _engineConfigCache = new EngineConfigCache(_metrics, TimeProvider.System);
        _mockKillSwitch = new Mock<IKillSwitchService>();
        _mockKillSwitch.Setup(k => k.IsGlobalKilledAsync(It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(false));
        _mockKillSwitch.Setup(k => k.IsStrategyKilledAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(false));
        _mockDegradationManager = new Mock<IDegradationModeManager>();
        _mockDegradationManager.Setup(d => d.CurrentMode).Returns(DegradationMode.Normal);
        _mockCircuitBreaker = new Mock<IExternalServiceCircuitBreaker>();
        _mockCircuitBreaker.Setup(c => c.IsOpen(It.IsAny<string>())).Returns(false);
        // Use the real bulkhead — mocking a ValueTask-returning method through
        // Moq is fiddly and the real bulkhead's per-group semaphores are
        // plentiful (60+ slots) so tests never block on acquisition.
        _bulkhead = new DbOperationBulkhead(_metrics,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DbOperationBulkhead>.Instance);

        _worker = new StrategyWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object,
            _mockEventBus.Object,
            new[] { _mockEvaluator.Object },
            _mockDistributedLock.Object,
            _options,
            _metrics,
            new SignalConflictResolver(new Mock<ILogger<SignalConflictResolver>>().Object),
            new RegimeCoherenceChecker(_mockScopeFactory.Object, memoryCache, new Mock<ILogger<RegimeCoherenceChecker>>().Object),
            new DrawdownRecoveryModeProvider(_mockScopeFactory.Object, memoryCache),
            new PortfolioCorrelationSizer(_mockScopeFactory.Object, memoryCache, new Mock<ILogger<PortfolioCorrelationSizer>>().Object),
            _strategyMetricsCache,
            _mockMarketHoursCalendar.Object,
            _regimeParamsCache,
            _mockRejectionAuditor.Object,
            _marketRegimeCache,
            _engineConfigCache,
            _mockKillSwitch.Object,
            _mockDegradationManager.Object,
            _mockCircuitBreaker.Object,
            _bulkhead);
    }

    public void Dispose() => _meterFactory.Dispose();

    private void SetupDbSets(
        List<EAInstance>? eaInstances = null,
        List<Strategy>? strategies = null,
        List<Candle>? candles = null,
        List<StrategyPerformanceSnapshot>? snapshots = null)
    {
        eaInstances ??= new List<EAInstance>
        {
            new() { InstanceId = "ea1", Symbols = "EURUSD", Status = EAInstanceStatus.Active, LastHeartbeat = DateTime.UtcNow }
        };
        strategies ??= new List<Strategy>();
        candles ??= new List<Candle>();
        snapshots ??= new List<StrategyPerformanceSnapshot>();

        _mockDbContext.Setup(c => c.Set<EAInstance>()).Returns(eaInstances.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<Strategy>()).Returns(strategies.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<Candle>()).Returns(candles.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>()).Returns(snapshots.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<TradeSignal>()).Returns(new List<TradeSignal>().AsQueryable().BuildMockDbSet().Object);
        // Disable the backtest gate so all strategies qualify by default in unit tests
        var configs = new List<EngineConfig>
        {
            new() { Key = "Backtest:Gate:Enabled", Value = "false", DataType = ConfigDataType.Bool }
        };
        _mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(configs.AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<MarketRegimeSnapshot>()).Returns(new List<MarketRegimeSnapshot>().AsQueryable().BuildMockDbSet().Object);
        _mockDbContext.Setup(c => c.Set<BacktestRun>()).Returns(new List<BacktestRun>().AsQueryable().BuildMockDbSet().Object);
    }

    private PriceUpdatedIntegrationEvent CreatePriceEvent(string symbol = "EURUSD") => new()
    {
        Symbol = symbol,
        Bid = 1.1000m,
        Ask = 1.1002m,
        Timestamp = DateTime.UtcNow
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_StaleTickBeyondMaxAge_DropsEvent()
    {
        SetupDbSets();

        var staleEvent = new PriceUpdatedIntegrationEvent
        {
            Symbol = "EURUSD",
            Bid = 1.1000m,
            Ask = 1.1002m,
            Timestamp = DateTime.UtcNow.AddSeconds(-20) // 20s old, max is 10s
        };

        await _worker.ProcessPriceUpdateAsync(staleEvent);

        // No strategy evaluation should occur
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NoActiveEAForSymbol_SkipsTick()
    {
        SetupDbSets(eaInstances: new List<EAInstance>()); // No EA instances

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_StaleEAHeartbeat_SkipsTick()
    {
        // EA with old heartbeat (beyond MaxEAHeartbeatAgeSeconds)
        var staleEA = new EAInstance
        {
            InstanceId = "ea1",
            Symbols = "EURUSD",
            Status = EAInstanceStatus.Active,
            LastHeartbeat = DateTime.UtcNow.AddSeconds(-120) // 120s old, max is 60s
        };
        SetupDbSets(eaInstances: new List<EAInstance> { staleEA });

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NoStrategiesForSymbol_NoEvaluation()
    {
        SetupDbSets(strategies: new List<Strategy>()); // No strategies

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EvaluatorReturnsNull_NoSignalCreated()
    {
        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        // Evaluator returns no signal
        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TradeSignal?)null);

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // No signal creation command
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_CriticalHealthStrategy_SkipsEvaluation()
    {
        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var snapshot = new StrategyPerformanceSnapshot
        {
            StrategyId = 1, HealthStatus = StrategyHealthStatus.Critical,
            EvaluatedAt = DateTime.UtcNow
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        SetupDbSets(
            strategies: new List<Strategy> { strategy },
            candles: candles,
            snapshots: new List<StrategyPerformanceSnapshot> { snapshot });

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Should skip evaluation for Critical strategy
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EvaluatorThrows_IncrementsFailureCounter_DoesNotCrash()
    {
        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("test error"));

        // Should not throw
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Verify it was attempted
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Fail-closed paths introduced by the 10 post-eval fixes (1, 2, 3, 8)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Handle_MlScorerThrows_SuppressesSignal_NoStrategyCircuitIncrement()
    {
        // Fix #1: ML infra exceptions should drop the signal (fail-closed) without opening
        // the STRATEGY's circuit breaker — the strategy itself didn't fail.
        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 1, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            });

        _mockMlScorer
            .Setup(s => s.ScoreAsync(It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated ONNX runtime crash"));

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Fail-closed: no CreateTradeSignalCommand must be dispatched.
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // And the strategy's consecutive-failure counter must stay at 0 — ML stack crashes
        // are infra failures, not strategy failures, so they must not open the circuit breaker.
        var failuresField = typeof(StrategyWorker)
            .GetField("_consecutiveFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var failures = failuresField.GetValue(_worker) as System.Collections.Concurrent.ConcurrentDictionary<long, int>;
        Assert.NotNull(failures);
        Assert.False(failures!.ContainsKey(1L) && failures[1L] > 0,
            "Strategy circuit breaker was incremented on ML-stack failure — it should remain untouched.");
    }

    [Fact]
    public async Task Handle_RegimeCoherenceBelowThreshold_SuppressesAllSignals()
    {
        // Fix #2: Regime coherence is a global per-tick gate. When cross-timeframe regimes
        // disagree (e.g. H1 Trending / H4 Ranging / D1 Breakout), the fraction falls below
        // MinRegimeCoherence and ALL signals are suppressed for the symbol.
        _options.MinRegimeCoherence = 0.50m;

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        // Three timeframes, three different regimes → majority=1/3, no category bonus,
        // coherence = 0.33 → below the 0.50 threshold → suppression fires.
        var regimeSnapshots = new List<MarketRegimeSnapshot>
        {
            new() { Symbol = "EURUSD", Timeframe = Timeframe.H1, Regime = MarketRegimeEnum.Trending,   DetectedAt = DateTime.UtcNow.AddMinutes(-5) },
            new() { Symbol = "EURUSD", Timeframe = Timeframe.H4, Regime = MarketRegimeEnum.Ranging,    DetectedAt = DateTime.UtcNow.AddMinutes(-5) },
            new() { Symbol = "EURUSD", Timeframe = Timeframe.D1, Regime = MarketRegimeEnum.Breakout,   DetectedAt = DateTime.UtcNow.AddMinutes(-5) },
        };

        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);
        _mockDbContext
            .Setup(c => c.Set<MarketRegimeSnapshot>())
            .Returns(regimeSnapshots.AsQueryable().BuildMockDbSet().Object);

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // No strategy evaluation should have occurred — the gate is before the strategy loop.
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_NewsFilterTimeout_DropsTickWithinBoundedTime()
    {
        // Fix #3: The news filter is a synchronous upstream I/O call on the hot tick path.
        // A hang must not block the worker — the timeout drops the tick fail-closed and the
        // call returns well before the fake 10 s I/O would have completed.
        _options.NewsBlackoutMinutesBefore = 30;
        _options.NewsBlackoutMinutesAfter = 15;
        _options.NewsFilterTimeoutSeconds = 1;

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        SetupDbSets(strategies: new List<Strategy> { strategy });

        _mockNewsFilter
            .Setup(f => f.IsSafeToTradeAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, DateTime __, int ___, int ____, CancellationToken ct) =>
            {
                // Respect the linked cancellation so the timeout actually short-circuits.
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return true;
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());
        sw.Stop();

        // Must return well within the fake 10s hang — allow up to 4s for CI slack around 1s timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"News filter timeout did not short-circuit — took {sw.Elapsed.TotalSeconds:F1}s.");

        // Fail-closed: no strategy evaluation after a news-filter timeout.
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CooldownHydrationFailsRepeatedly_RefusesToSubscribe()
    {
        // Fix #8: When cooldown hydration fails for every retry attempt, the worker must
        // refuse to subscribe to the event bus — emitting signals with a stale/empty cooldown
        // cache is worse than emitting no signals at all.
        SetupDbSets();

        // Swap the Strategy DbSet so every attempt throws. The retry loop will exhaust and
        // the worker should drop out of ExecuteAsync without calling Subscribe.
        var throwingSet = new Mock<DbSet<Strategy>>();
        throwingSet
            .As<IQueryable<Strategy>>()
            .Setup(q => q.Provider)
            .Throws(new InvalidOperationException("simulated DB outage during hydration"));
        _mockDbContext
            .Setup(c => c.Set<Strategy>())
            .Returns(throwingSet.Object);

        using var cts = new CancellationTokenSource();
        // Stop the worker after a generous window (longer than first two backoffs of 1s + 2s
        // but short enough the test doesn't drag on). The remaining retries will no-op once
        // cancellation fires and the worker returns fail-closed.
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        await _worker.StartAsync(cts.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
        }
        catch (OperationCanceledException) { /* expected */ }
        await _worker.StopAsync(CancellationToken.None);

        // The event bus must never have been subscribed — this is the fail-closed guarantee.
        _mockEventBus.Verify(
            b => b.Subscribe<PriceUpdatedIntegrationEvent, StrategyWorker>(),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SignalCooldownActive_SkipsEvaluation()
    {
        _options.SignalCooldownSeconds = 300; // 5 minutes

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m
        }).ToList();

        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        // First call: evaluator returns a signal, signal is created
        var signal = new TradeSignal
        {
            StrategyId = 1, Symbol = "EURUSD", Direction = TradeDirection.Buy,
            EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
            SuggestedLotSize = 0.01m, Confidence = 0.70m,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signal);

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Second call: cooldown should prevent evaluation
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Signal created only once (first call), second call skipped due to cooldown
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Pipeline evaluation fixes (Fix #1, #2, #3, #5)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Handle_MissingEvaluator_EmitsEvaluatorMissingMetric()
    {
        // Fix #2: a StrategyType with no registered evaluator must surface on
        // dashboards, not only in the decision log, so DI misconfiguration is
        // caught in minutes rather than after an audit trail trawl.
        var strategy = new Strategy
        {
            Id = 42, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.CarryTrade, // Registered evaluator is MovingAverageCrossover only.
            Timeframe = Timeframe.H1,
        };
        SetupDbSets(strategies: new List<Strategy> { strategy });

        using var counter = new CounterProbe(_meterFactory, "trading.signals.evaluator_missing");
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        Assert.True(counter.Total >= 1,
            "EvaluatorMissing counter was not incremented when the dispatcher had no match.");
        Assert.Contains("CarryTrade", counter.TagValues("strategy_type"));
    }

    [Fact]
    public async Task Handle_MarketClosed_ExtendsSignalExpiryAndEmitsMetric()
    {
        // Fix #5: a signal generated during the weekend closure must have its
        // TTL extended to cover at least the next open + grace period, otherwise
        // it expires before Monday fill.
        _options.SignalCooldownSeconds = 0;
        _options.AdaptiveSignalTtlEnabled = true;
        _options.MarketClosedGracePeriodMinutes = 30;
        _options.NewsBlackoutMinutesBefore = 0;
        _options.NewsBlackoutMinutesAfter = 0;
        _options.MinRegimeCoherence = 0;

        var reopenUtc = DateTime.UtcNow.AddDays(2);
        _mockMarketHoursCalendar
            .Setup(c => c.IsMarketClosed(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(true);
        _mockMarketHoursCalendar
            .Setup(c => c.NextMarketOpen(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(reopenUtc);

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1,
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();
        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        // Evaluator emits a signal with a short 30-min TTL that would otherwise
        // expire well before the (mocked) reopen two days out.
        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 1, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            });

        CreateTradeSignalCommand? captured = null;
        _mockMediator
            .Setup(m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((cmd, _) =>
            {
                captured = cmd as CreateTradeSignalCommand;
            })
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        using var counter = new CounterProbe(_meterFactory, "trading.signals.ttl_extended_market_closed");

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        Assert.NotNull(captured);
        Assert.True(captured!.ExpiresAt >= reopenUtc.AddMinutes(29),
            $"Expected ExpiresAt to be at least reopen+30min ({reopenUtc.AddMinutes(30):u}) but was {captured.ExpiresAt:u}.");
        Assert.True(counter.Total >= 1, "SignalTtlExtendedMarketClosed counter did not fire.");
    }

    [Fact]
    public async Task Handle_MarketOpen_DoesNotExtendSignalExpiry()
    {
        // Sanity check for Fix #5: when the calendar reports "open", the adaptive
        // TTL path must not touch the signal's ExpiresAt.
        _options.SignalCooldownSeconds = 0;
        _options.AdaptiveSignalTtlEnabled = true;
        _options.NewsBlackoutMinutesBefore = 0;
        _options.NewsBlackoutMinutesAfter = 0;
        _options.MinRegimeCoherence = 0;

        _mockMarketHoursCalendar
            .Setup(c => c.IsMarketClosed(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(false);

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1,
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();
        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        var signalExpiry = DateTime.UtcNow.AddMinutes(30);
        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 1, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = signalExpiry,
            });

        CreateTradeSignalCommand? captured = null;
        _mockMediator
            .Setup(m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((cmd, _) => { captured = cmd as CreateTradeSignalCommand; })
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        using var counter = new CounterProbe(_meterFactory, "trading.signals.ttl_extended_market_closed");
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        Assert.NotNull(captured);
        // The vol-conditional TTL path may adjust by small amounts when ATR is degenerate —
        // assert we stay within 5 minutes of the original, not that the market-closed
        // extension fired.
        Assert.True(Math.Abs((captured!.ExpiresAt - signalExpiry).TotalMinutes) < 5,
            $"Expires drifted more than 5 min without market-closed extension (got {captured.ExpiresAt:u}, expected ~{signalExpiry:u}).");
        Assert.Equal(0L, counter.Total);
    }

    [Fact]
    public async Task BacktestCompletedIntegrationEvent_Handler_InvalidatesMetricsCache()
    {
        // Fix #3: the BacktestCompleted handler must drop the strategy's cached
        // metrics so the very next tick re-queries the DB and picks up the
        // updated Sharpe / health status.
        var snap = new StrategyPerformanceSnapshot
        {
            StrategyId = 7, SharpeRatio = 0.8m, HealthStatus = StrategyHealthStatus.Healthy,
            EvaluatedAt = DateTime.UtcNow,
        };
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(new List<StrategyPerformanceSnapshot> { snap }.AsQueryable().BuildMockDbSet().Object);

        // Prime the cache.
        _ = await _strategyMetricsCache.GetManyAsync(_mockDbContext.Object, new long[] { 7 }, 60, CancellationToken.None);
        _mockDbContext.Invocations.Clear();

        await _worker.Handle(new BacktestCompletedIntegrationEvent
        {
            BacktestRunId = 11, StrategyId = 7, Symbol = "EURUSD", Timeframe = Timeframe.H1,
        });

        // Next lookup should hit DB again.
        _ = await _strategyMetricsCache.GetManyAsync(_mockDbContext.Object, new long[] { 7 }, 60, CancellationToken.None);
        _mockDbContext.Verify(c => c.Set<StrategyPerformanceSnapshot>(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StrategyActivatedIntegrationEvent_Handler_InvalidatesMetricsCache()
    {
        // Parallel check for the second cache-invalidation event: when a strategy
        // flips to Active its cached 0-Sharpe sentinel must be purged so the
        // next tick fetches the real snapshot.
        var snap = new StrategyPerformanceSnapshot
        {
            StrategyId = 9, SharpeRatio = 1.6m, HealthStatus = StrategyHealthStatus.Healthy,
            EvaluatedAt = DateTime.UtcNow,
        };
        _mockDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>())
            .Returns(new List<StrategyPerformanceSnapshot> { snap }.AsQueryable().BuildMockDbSet().Object);

        _ = await _strategyMetricsCache.GetManyAsync(_mockDbContext.Object, new long[] { 9 }, 60, CancellationToken.None);
        _mockDbContext.Invocations.Clear();

        await _worker.Handle(new StrategyActivatedIntegrationEvent
        {
            StrategyId = 9, Name = "sma-9", Symbol = "EURUSD", Timeframe = Timeframe.H1,
            ActivatedAt = DateTime.UtcNow,
        });

        _ = await _strategyMetricsCache.GetManyAsync(_mockDbContext.Object, new long[] { 9 }, 60, CancellationToken.None);
        _mockDbContext.Verify(c => c.Set<StrategyPerformanceSnapshot>(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_BacktestQualificationQueryFails_FailsClosed_NoSignalsEmitted()
    {
        // Fix #1: a failing BacktestRun query must fail-closed (no qualified
        // strategies this tick, PrefetchQueryTimeouts incremented) rather than
        // bubbling up into the tick loop. We verify the fail-closed path with a
        // throwing EngineConfig query — the gate's wrapper catches it, logs,
        // emits the metric, and treats the qualified set as empty.
        _options.PrefetchQueryTimeoutSeconds = 1;
        _options.MinRegimeCoherence = 0;
        _options.NewsBlackoutMinutesBefore = 0;
        _options.NewsBlackoutMinutesAfter = 0;

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1,
        };
        SetupDbSets(strategies: new List<Strategy> { strategy });

        // Force the EngineConfig query to throw the same way a stalled query
        // finally surfaces. Our timeout wrapper treats failed + timed-out
        // identically: increment the metric and fail closed. This exercises the
        // catch-all branch without depending on EF's async cancellation plumbing.
        var throwingConfig = new Mock<Microsoft.EntityFrameworkCore.DbSet<EngineConfig>>();
        throwingConfig.As<IQueryable<EngineConfig>>()
            .Setup(q => q.Provider)
            .Throws(new InvalidOperationException("simulated DB failure during backtest gate"));
        _mockDbContext.Setup(c => c.Set<EngineConfig>()).Returns(throwingConfig.Object);

        using var counter = new CounterProbe(_meterFactory, "trading.signals.prefetch_query_timeouts");
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        Assert.True(counter.Total >= 1, "PrefetchQueryTimeouts counter was not incremented on failure.");
        _mockEvaluator.Verify(
            e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_MLScoringTimeout_FailsClosed_NoSignalCreated_NoCircuitIncrement()
    {
        // Fix 7: ML scoring on the hot evaluator loop is bounded by
        // MLScoringTimeoutSeconds. On timeout we drop the signal but leave the
        // strategy's circuit-breaker counter alone.
        _options.MLScoringTimeoutSeconds = 1;
        _options.SignalCooldownSeconds = 0;
        _options.MinRegimeCoherence = 0;
        _options.NewsBlackoutMinutesBefore = 0;
        _options.NewsBlackoutMinutesAfter = 0;

        var strategy = new Strategy
        {
            Id = 1, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover,
            Timeframe = Timeframe.H1,
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();
        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 1, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            });

        // ML scorer hangs longer than the 1s timeout, but honours its CT.
        _mockMlScorer
            .Setup(s => s.ScoreAsync(It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()))
            .Returns(async (TradeSignal _, IReadOnlyList<Candle> _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new MLScoreResult(null, null, null, null);
            });

        using var counter = new CounterProbe(_meterFactory, "trading.ml.scoring_timeouts");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"ML scoring timeout did not short-circuit — took {sw.Elapsed.TotalSeconds:F1}s.");
        Assert.True(counter.Total >= 1, "MLScoringTimeouts counter did not fire.");
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Strategy's consecutive-failure counter stays at 0 — infra failure must not
        // open the strategy circuit breaker.
        var failuresField = typeof(StrategyWorker)
            .GetField("_consecutiveFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var failures = failuresField.GetValue(_worker) as System.Collections.Concurrent.ConcurrentDictionary<long, int>;
        Assert.NotNull(failures);
        Assert.False(failures!.ContainsKey(1L) && failures[1L] > 0,
            "Strategy circuit breaker incremented on ML timeout — it must not.");

        _mockRejectionAuditor.Verify(
            a => a.RecordAsync("MLScoring", "ml_scoring_timeout",
                "EURUSD", "StrategyWorker", 1L, null,
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Pre-score early-exit gate (Fix 22)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Handle_LowSharpeStrategy_InSameGroup_AsHighSharpeLeader_IsEarlyExited()
    {
        // Two strategies in the SAME (Symbol, Timeframe, Direction) group. One
        // has a strong cached Sharpe (0.9) → high pre-score; the other has a
        // weak Sharpe (0.1) → below the 70% ratio of the leader. The weak
        // candidate should early-exit before ML scoring runs and the ML
        // scorer should be invoked at most once — for the winner only.
        _options.PreScoreEarlyExitEnabled = true;
        _options.PreScoreHopelessRatio = 0.70m;
        _options.PreScoreSortBySharpeDescending = true;
        _options.SignalCooldownSeconds = 0;
        _options.MaxParallelStrategies = 1; // serialise so the leader runs first

        var leader = new Strategy
        {
            Id = 101, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1,
        };
        var laggard = new Strategy
        {
            Id = 102, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1,
        };

        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();

        SetupDbSets(
            strategies: new List<Strategy> { leader, laggard },
            candles: candles,
            snapshots: new List<StrategyPerformanceSnapshot>
            {
                new() { StrategyId = 101, SharpeRatio = 2.7m, HealthStatus = StrategyHealthStatus.Healthy, EvaluatedAt = DateTime.UtcNow },
                new() { StrategyId = 102, SharpeRatio = 0.3m, HealthStatus = StrategyHealthStatus.Healthy, EvaluatedAt = DateTime.UtcNow },
            });

        int evaluatorCalls = 0;
        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .Returns<Strategy, IReadOnlyList<Candle>, (decimal, decimal), CancellationToken>((s, _, _, _) =>
            {
                Interlocked.Increment(ref evaluatorCalls);
                return Task.FromResult<TradeSignal?>(new TradeSignal
                {
                    StrategyId = s.Id, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                    EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                    SuggestedLotSize = 0.01m, Confidence = 0.70m,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                });
            });

        using var earlyExitCounter = new CounterProbe(_meterFactory, "trading.signals.conflict_resolution_early_exits");

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        // Both strategies were evaluated (early-exit fires AFTER evaluate so
        // we can still compare signal.Confidence). But the laggard must have
        // been dropped before ML scoring ran, so the ML scorer was called at
        // most once (for the leader).
        Assert.Equal(2, evaluatorCalls);
        _mockMlScorer.Verify(
            s => s.ScoreAsync(It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()),
            Times.AtMostOnce());
        Assert.True(earlyExitCounter.Total >= 1,
            "Pre-score early-exit counter did not fire even though the laggard's score is clearly below the 70% ratio.");

        _mockRejectionAuditor.Verify(
            a => a.RecordAsync("PreScore", "pre_score_hopeless",
                "EURUSD", "StrategyWorker", 102L, null,
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_UseBatchedMLScoring_RoutesThroughScoreBatchAsync_NotScoreAsync()
    {
        // Fix #16: when UseBatchedMLScoring is on, the parallel loop deposits
        // pre-ML candidates and Phase 2 calls ScoreBatchAsync ONCE for the
        // whole tick. ScoreAsync (the per-signal call) must NOT fire — that
        // was the whole point of batching. The final signal still lands in
        // the CreateTradeSignal pipeline with ML fields attached.
        _options.UseBatchedMLScoring = true;
        _options.SignalCooldownSeconds = 0;
        _options.PreScoreEarlyExitEnabled = false;  // focus the test on #16
        _options.MinAbstentionScore = 0;

        var strategy = new Strategy
        {
            Id = 301, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1,
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();
        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 301, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            });

        int scoreBatchCalls = 0;
        _mockMlScorer
            .Setup(s => s.ScoreBatchAsync(
                It.IsAny<IReadOnlyList<(TradeSignal, IReadOnlyList<Candle>)>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(TradeSignal, IReadOnlyList<Candle>)>, CancellationToken>((b, _) =>
            {
                Interlocked.Increment(ref scoreBatchCalls);
                var results = b.Select(x => new MLScoreResult(
                    PredictedDirection: x.Item1.Direction,
                    PredictedMagnitudePips: 12m,
                    ConfidenceScore: 0.9m,
                    MLModelId: 7)).ToList();
                return Task.FromResult<IReadOnlyList<MLScoreResult>>(results);
            });

        CreateTradeSignalCommand? captured = null;
        _mockMediator
            .Setup(m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResponseData<long>>, CancellationToken>((cmd, _) => { captured = cmd as CreateTradeSignalCommand; })
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        Assert.Equal(1, scoreBatchCalls);
        _mockMlScorer.Verify(
            s => s.ScoreAsync(It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.NotNull(captured);
        Assert.Equal(7L, captured!.MLModelId);
    }

    [Fact]
    public async Task Handle_UseBatchedMLScoring_CircuitOpen_SuppressesAllCandidates()
    {
        // When the ML circuit is open, the batched path short-circuits the
        // entire batch without attempting ScoreBatchAsync at all and
        // produces one suppression audit per candidate.
        _options.UseBatchedMLScoring = true;
        _options.PreScoreEarlyExitEnabled = false;
        _options.SignalCooldownSeconds = 0;
        _mockCircuitBreaker
            .Setup(c => c.IsOpen("MLSignalScorer"))
            .Returns(true);

        var strategy = new Strategy
        {
            Id = 311, Symbol = "EURUSD", Status = StrategyStatus.Active,
            StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1,
        };
        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();
        SetupDbSets(strategies: new List<Strategy> { strategy }, candles: candles);

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TradeSignal
            {
                StrategyId = 311, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                SuggestedLotSize = 0.01m, Confidence = 0.70m,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            });

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        _mockMlScorer.Verify(
            s => s.ScoreBatchAsync(It.IsAny<IReadOnlyList<(TradeSignal, IReadOnlyList<Candle>)>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockMediator.Verify(
            m => m.Send(It.IsAny<CreateTradeSignalCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockRejectionAuditor.Verify(
            a => a.RecordAsync("MLScoring", "ml_circuit_open",
                "EURUSD", "StrategyWorker", 311L, null,
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_PreScoreGate_Disabled_LetsBothCandidatesReachMLScoring()
    {
        // Same setup as above but with the gate disabled — both strategies
        // should reach ML scoring (neither is dropped).
        _options.PreScoreEarlyExitEnabled = false;
        _options.SignalCooldownSeconds = 0;
        _options.MaxParallelStrategies = 1;

        var leader  = new Strategy { Id = 201, Symbol = "EURUSD", Status = StrategyStatus.Active, StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1 };
        var laggard = new Strategy { Id = 202, Symbol = "EURUSD", Status = StrategyStatus.Active, StrategyType = StrategyType.MovingAverageCrossover, Timeframe = Timeframe.H1 };

        var candles = Enumerable.Range(0, 10).Select(i => new Candle
        {
            Symbol = "EURUSD", Timeframe = Timeframe.H1, IsClosed = true,
            Timestamp = DateTime.UtcNow.AddHours(-i),
            Open = 1.1m, High = 1.11m, Low = 1.09m, Close = 1.1m,
        }).ToList();

        SetupDbSets(
            strategies: new List<Strategy> { leader, laggard },
            candles: candles,
            snapshots: new List<StrategyPerformanceSnapshot>
            {
                new() { StrategyId = 201, SharpeRatio = 2.7m, HealthStatus = StrategyHealthStatus.Healthy, EvaluatedAt = DateTime.UtcNow },
                new() { StrategyId = 202, SharpeRatio = 0.3m, HealthStatus = StrategyHealthStatus.Healthy, EvaluatedAt = DateTime.UtcNow },
            });

        _mockEvaluator
            .Setup(e => e.EvaluateAsync(It.IsAny<Strategy>(), It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal, decimal)>(), It.IsAny<CancellationToken>()))
            .Returns<Strategy, IReadOnlyList<Candle>, (decimal, decimal), CancellationToken>((s, _, _, _) =>
                Task.FromResult<TradeSignal?>(new TradeSignal
                {
                    StrategyId = s.Id, Symbol = "EURUSD", Direction = TradeDirection.Buy,
                    EntryPrice = 1.1001m, StopLoss = 1.0950m, TakeProfit = 1.1100m,
                    SuggestedLotSize = 0.01m, Confidence = 0.70m,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30),
                }));

        await _worker.ProcessPriceUpdateAsync(CreatePriceEvent());

        _mockMlScorer.Verify(
            s => s.ScoreAsync(It.IsAny<TradeSignal>(), It.IsAny<IReadOnlyList<Candle>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }

    /// <summary>
    /// Minimal in-process probe for a single counter. Installs a MeterListener
    /// scoped to this probe's lifetime, captures <c>long</c> increments against
    /// the named instrument, and tallies their tag values so assertions can read
    /// both totals and per-tag slices without touching exporter infrastructure.
    /// </summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;
        private readonly List<KeyValuePair<string, object?>[]> _tagSnapshots = new();
        private readonly object _lock = new();

        public CounterProbe(IMeterFactory factory, string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == instrumentName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                lock (_lock)
                {
                    _total += value;
                    _tagSnapshots.Add(tags.ToArray());
                }
            });
            _listener.Start();
        }

        public long Total
        {
            get { lock (_lock) return _total; }
        }

        public IEnumerable<string> TagValues(string tagKey)
        {
            lock (_lock)
            {
                return _tagSnapshots
                    .SelectMany(s => s)
                    .Where(kv => kv.Key == tagKey)
                    .Select(kv => kv.Value?.ToString() ?? string.Empty)
                    .ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
