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
            new PortfolioCorrelationSizer(_mockScopeFactory.Object, memoryCache, new Mock<ILogger<PortfolioCorrelationSizer>>().Object));
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

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
