using System.Diagnostics.Metrics;
using System.Reflection;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.AuditTrail.Commands.LogDecision;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Application.StrategyEnsemble.Commands.RebalanceEnsemble;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

public class StrategyHealthWorkerTest : IDisposable
{
    private readonly Mock<ILogger<StrategyHealthWorker>> _mockLogger;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IReadApplicationDbContext> _mockReadContext;
    private readonly Mock<IWriteApplicationDbContext> _mockWriteContext;
    private readonly StrategyHealthWorker _worker;

    // Track entities added to write context for assertion
    private readonly List<StrategyPerformanceSnapshot> _addedSnapshots = new();
    private readonly List<OptimizationRun> _addedOptimizationRuns = new();

    public StrategyHealthWorkerTest()
    {
        _mockLogger       = new Mock<ILogger<StrategyHealthWorker>>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockMediator     = new Mock<IMediator>();
        _mockReadContext  = new Mock<IReadApplicationDbContext>();
        _mockWriteContext = new Mock<IWriteApplicationDbContext>();

        // Wire up IServiceScopeFactory -> IServiceScope -> IServiceProvider
        var mockScope    = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();

        mockProvider.Setup(p => p.GetService(typeof(IMediator))).Returns(_mockMediator.Object);
        mockProvider.Setup(p => p.GetService(typeof(IReadApplicationDbContext))).Returns(_mockReadContext.Object);
        mockProvider.Setup(p => p.GetService(typeof(IWriteApplicationDbContext))).Returns(_mockWriteContext.Object);

        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        // Default: mediator returns success for LogDecisionCommand
        _mockMediator
            .Setup(m => m.Send(It.IsAny<LogDecisionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseData<long>.Init(1L, true, "Successful", "00"));

        // Default: write context SaveChangesAsync returns 1
        _mockWriteContext
            .Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _worker = new StrategyHealthWorker(
            _mockLogger.Object,
            _mockScopeFactory.Object);
    }

    public void Dispose()
    {
        // No meter factory to dispose in this worker, but keep IDisposable for pattern consistency
    }

    // -- Helpers ---------------------------------------------------------------

    private void SetupReadContext(
        List<Strategy> strategies,
        List<TradeSignal> signals,
        List<Order> orders,
        List<CurrencyPair> currencyPairs)
    {
        var readDbContext = new Mock<DbContext>();

        var strategyDbSet     = strategies.AsQueryable().BuildMockDbSet();
        var signalDbSet       = signals.AsQueryable().BuildMockDbSet();
        var orderDbSet        = orders.AsQueryable().BuildMockDbSet();
        var currencyPairDbSet = currencyPairs.AsQueryable().BuildMockDbSet();
        var engineConfigDbSet = new List<EngineConfig>().AsQueryable().BuildMockDbSet();

        readDbContext.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);
        readDbContext.Setup(c => c.Set<TradeSignal>()).Returns(signalDbSet.Object);
        readDbContext.Setup(c => c.Set<Order>()).Returns(orderDbSet.Object);
        readDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(currencyPairDbSet.Object);
        readDbContext.Setup(c => c.Set<EngineConfig>()).Returns(engineConfigDbSet.Object);

        _mockReadContext.Setup(c => c.GetDbContext()).Returns(readDbContext.Object);
    }

    private void SetupWriteContext(List<Strategy> strategies)
    {
        var writeDbContext = new Mock<DbContext>();

        // Strategy set for the write context (used to load and pause critical strategies)
        var strategyDbSet = strategies.AsQueryable().BuildMockDbSet();
        writeDbContext.Setup(c => c.Set<Strategy>()).Returns(strategyDbSet.Object);

        // StrategyPerformanceSnapshot set — capture added snapshots
        var snapshotList  = new List<StrategyPerformanceSnapshot>();
        var snapshotDbSet = snapshotList.AsQueryable().BuildMockDbSet();
        snapshotDbSet.Setup(d => d.AddAsync(It.IsAny<StrategyPerformanceSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<StrategyPerformanceSnapshot, CancellationToken>((s, _) => _addedSnapshots.Add(s))
            .ReturnsAsync((StrategyPerformanceSnapshot s, CancellationToken _) => null!);
        writeDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>()).Returns(snapshotDbSet.Object);

        // OptimizationRun set — capture added runs
        var optRunList  = new List<OptimizationRun>();
        var optRunDbSet = optRunList.AsQueryable().BuildMockDbSet();
        optRunDbSet.Setup(d => d.AddAsync(It.IsAny<OptimizationRun>(), It.IsAny<CancellationToken>()))
            .Callback<OptimizationRun, CancellationToken>((r, _) => _addedOptimizationRuns.Add(r))
            .ReturnsAsync((OptimizationRun r, CancellationToken _) => null!);
        writeDbContext.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);

        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(writeDbContext.Object);
    }

    /// <summary>
    /// Invokes the private EvaluateAllActiveStrategiesAsync method directly to avoid
    /// the Task.Delay in ExecuteAsync.
    /// </summary>
    private async Task InvokeEvaluateAllActiveStrategiesAsync(CancellationToken ct = default)
    {
        var method = typeof(StrategyHealthWorker)
            .GetMethod("EvaluateAllActiveStrategiesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(_worker, new object[] { ct })!;
    }

    private static Strategy MakeStrategy(long id, string symbol = "EURUSD") => new()
    {
        Id           = id,
        Status       = StrategyStatus.Active,
        StrategyType = StrategyType.BreakoutScalper,
        Symbol       = symbol,
        IsDeleted    = false
    };

    private static TradeSignal MakeSignal(
        long id, long strategyId, long? orderId,
        TradeDirection direction, decimal entryPrice,
        decimal lotSize = 1.0m, string symbol = "EURUSD") => new()
    {
        Id               = id,
        StrategyId       = strategyId,
        OrderId          = orderId,
        Direction        = direction,
        EntryPrice       = entryPrice,
        SuggestedLotSize = lotSize,
        Status           = TradeSignalStatus.Executed,
        GeneratedAt      = DateTime.UtcNow,
        Symbol           = symbol,
        IsDeleted        = false
    };

    private static Order MakeFilledOrder(long id, decimal filledPrice) => new()
    {
        Id          = id,
        Status      = OrderStatus.Filled,
        FilledPrice = filledPrice,
        IsDeleted   = false
    };

    private static CurrencyPair MakePair(string symbol = "EURUSD", decimal contractSize = 100_000m) => new()
    {
        Symbol       = symbol,
        ContractSize = contractSize,
        IsDeleted    = false
    };

    // -- Tests -----------------------------------------------------------------

    [Fact]
    public async Task EvaluateAll_NoActiveStrategies_DoesNothing()
    {
        // Arrange
        SetupReadContext(
            strategies: new List<Strategy>(),
            signals: new List<TradeSignal>(),
            orders: new List<Order>(),
            currencyPairs: new List<CurrencyPair>());
        SetupWriteContext(new List<Strategy>());

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert -- no snapshots persisted, no mediator calls
        Assert.Empty(_addedSnapshots);
        _mockWriteContext.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAll_StrategyWithNoExecutedSignals_Skips()
    {
        // Arrange -- strategy exists but has zero executed signals
        var strategy = MakeStrategy(1);

        SetupReadContext(
            strategies: new List<Strategy> { strategy },
            signals: new List<TradeSignal>(),
            orders: new List<Order>(),
            currencyPairs: new List<CurrencyPair> { MakePair() });
        SetupWriteContext(new List<Strategy> { strategy });

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert -- no snapshot written
        Assert.Empty(_addedSnapshots);
        _mockWriteContext.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAll_AllWinningTrades_HealthyStatus()
    {
        // Arrange -- 5 Buy signals, each bought at 1.1000 and filled at 1.1050 (all winners)
        var strategy = MakeStrategy(1);

        var signals = new List<TradeSignal>();
        var orders  = new List<Order>();
        for (int i = 1; i <= 5; i++)
        {
            signals.Add(MakeSignal(i, strategyId: 1, orderId: i,
                TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m));
            orders.Add(MakeFilledOrder(i, filledPrice: 1.1050m));
        }

        SetupReadContext(
            strategies: new List<Strategy> { strategy },
            signals: signals,
            orders: orders,
            currencyPairs: new List<CurrencyPair> { MakePair() });
        SetupWriteContext(new List<Strategy> { strategy });

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert
        Assert.Single(_addedSnapshots);
        var snap = _addedSnapshots[0];

        Assert.Equal(1, snap.StrategyId);
        Assert.Equal(5, snap.WindowTrades);
        Assert.Equal(5, snap.WinningTrades);
        Assert.Equal(0, snap.LosingTrades);
        Assert.Equal(1.0m, snap.WinRate);
        Assert.True(snap.HealthScore >= 0.6m, $"Expected HealthScore >= 0.6 but was {snap.HealthScore}");
        Assert.Equal(StrategyHealthStatus.Healthy, snap.HealthStatus);

        // No optimization run should be queued for Healthy strategies
        Assert.Empty(_addedOptimizationRuns);

        _mockWriteContext.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateAll_AllLosingTrades_CriticalStatus_AutoPausesAndQueuesOptimization()
    {
        // Arrange -- 1 small winner followed by 9 large losers.
        // The initial winner creates a positive peak so the drawdown formula activates,
        // and the subsequent losses drive winRate, profitFactor, and drawdown all into
        // the Critical band (healthScore < 0.3).
        var strategy = MakeStrategy(1);

        var signals = new List<TradeSignal>();
        var orders  = new List<Order>();

        // 1 tiny winner first (most recent GeneratedAt so it appears first in desc order),
        // followed by 19 large losers. The winner creates a positive peak (+10) so the
        // drawdown formula activates; losses then drive cumulative PnL deep negative,
        // producing drawdown >> 20% which zeroes the drawdown term.
        //
        // Expected metrics:
        //   winRate = 1/20 = 0.05 => term = 0.4 * 0.05 = 0.02
        //   profitFactor = 10 / 95000 ~ 0 => term ~ 0
        //   maxDrawdown >> 20% => term = 0
        //   healthScore ~ 0.02 => Critical
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Winner: most recent timestamp so desc ordering puts it first in iteration
        var winSignal = MakeSignal(1, strategyId: 1, orderId: 1,
            TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m);
        winSignal.GeneratedAt = baseTime.AddMinutes(20);
        signals.Add(winSignal);
        orders.Add(MakeFilledOrder(1, filledPrice: 1.1001m));

        // 19 losers: older timestamps
        for (int i = 2; i <= 20; i++)
        {
            var loseSignal = MakeSignal(i, strategyId: 1, orderId: i,
                TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m);
            loseSignal.GeneratedAt = baseTime.AddMinutes(20 - i);
            signals.Add(loseSignal);
            orders.Add(MakeFilledOrder(i, filledPrice: 1.0500m));
        }

        SetupReadContext(
            strategies: new List<Strategy> { strategy },
            signals: signals,
            orders: orders,
            currencyPairs: new List<CurrencyPair> { MakePair() });
        SetupWriteContext(new List<Strategy> { strategy });

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert -- snapshot persisted with Critical status
        Assert.Single(_addedSnapshots);
        var snap = _addedSnapshots[0];

        Assert.True(snap.WinRate <= 0.15m, $"Expected WinRate <= 0.15 but was {snap.WinRate}");
        Assert.True(snap.HealthScore < 0.3m, $"Expected HealthScore < 0.3 but was {snap.HealthScore}");
        Assert.Equal(StrategyHealthStatus.Critical, snap.HealthStatus);

        // Assert -- optimization run queued
        Assert.Single(_addedOptimizationRuns);
        var optRun = _addedOptimizationRuns[0];
        Assert.Equal(1, optRun.StrategyId);
        Assert.Equal(TriggerType.AutoDegrading, optRun.TriggerType);
        Assert.Equal(OptimizationRunStatus.Queued, optRun.Status);

        // Assert -- SaveChangesAsync called (persists snapshot + pause + optimization run)
        _mockWriteContext.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert -- audit trail logged for HealthEvaluation and AutoPause
        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c =>
                    c.EntityId == 1 &&
                    c.DecisionType == "HealthEvaluation" &&
                    c.Outcome == "Critical"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _mockMediator.Verify(
            m => m.Send(
                It.Is<LogDecisionCommand>(c =>
                    c.EntityId == 1 &&
                    c.DecisionType == "AutoPause" &&
                    c.Outcome == "Paused"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateAll_MixedTrades_DegradingStatus()
    {
        // Arrange -- 10 trades: 3 winners, 7 losers => winRate = 0.3
        // Winners: Buy at 1.1000, fill at 1.1020 => PnL = +200 each (3 x 200 = 600)
        // Losers:  Buy at 1.1000, fill at 1.0990 => PnL = -100 each (7 x 100 = 700)
        // ProfitFactor = 600 / 700 = ~0.857
        // MaxDrawdown will be moderate since losses dominate
        // HealthScore = 0.4*0.3 + 0.3*min(1, 0.857/2) + 0.3*max(0, 1 - dd/20)
        //             = 0.12 + 0.3*0.4286 + 0.3*(drawdown term)
        // With these values we expect Degrading (0.3 <= score < 0.6)
        var strategy = MakeStrategy(1);

        var signals = new List<TradeSignal>();
        var orders  = new List<Order>();

        // 3 winning trades
        for (int i = 1; i <= 3; i++)
        {
            signals.Add(MakeSignal(i, strategyId: 1, orderId: i,
                TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m));
            orders.Add(MakeFilledOrder(i, filledPrice: 1.1020m));
        }

        // 7 losing trades
        for (int i = 4; i <= 10; i++)
        {
            signals.Add(MakeSignal(i, strategyId: 1, orderId: i,
                TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m));
            orders.Add(MakeFilledOrder(i, filledPrice: 1.0990m));
        }

        SetupReadContext(
            strategies: new List<Strategy> { strategy },
            signals: signals,
            orders: orders,
            currencyPairs: new List<CurrencyPair> { MakePair() });
        SetupWriteContext(new List<Strategy> { strategy });

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert
        Assert.Single(_addedSnapshots);
        var snap = _addedSnapshots[0];

        Assert.Equal(10, snap.WindowTrades);
        Assert.Equal(3, snap.WinningTrades);
        Assert.Equal(7, snap.LosingTrades);

        // Verify Degrading band: 0.3 <= HealthScore < 0.6
        Assert.True(snap.HealthScore >= 0.3m, $"Expected HealthScore >= 0.3 but was {snap.HealthScore}");
        Assert.True(snap.HealthScore < 0.6m, $"Expected HealthScore < 0.6 but was {snap.HealthScore}");
        Assert.Equal(StrategyHealthStatus.Degrading, snap.HealthStatus);

        // No optimization run for Degrading (only Critical triggers auto-pause)
        Assert.Empty(_addedOptimizationRuns);
    }

    [Fact]
    public async Task EvaluateAll_OneStrategyThrows_OtherStrategyStillEvaluated()
    {
        // Arrange -- two active strategies; strategy 1 will have data that causes
        // an exception (no signals but we simulate by having the read context return
        // signals only for strategy 2). Strategy 1 gets signals with no matching orders
        // (the read context returns orders only for strategy 2's order IDs).
        //
        // We use a different approach: set up strategy 1 with a valid signal referencing
        // a non-existent order so it skips cleanly, and strategy 2 with valid data.
        // To actually test error isolation we need strategy 1 to throw.
        // We'll set up strategy 1 with signals whose OrderId maps to an order with
        // null FilledPrice (which means pnlList will be empty => skip).
        //
        // Instead, let's set up the read context so that the first strategy's
        // signal processing throws by having a currency pair query fail.
        // Simplest approach: two strategies, one has good data, one has signals
        // that map to orders but we force a throw via a mock callback.

        var strategy1 = MakeStrategy(1, "EURUSD");
        var strategy2 = MakeStrategy(2, "GBPUSD");

        // Strategy 2 has valid winning trades
        var signals = new List<TradeSignal>
        {
            MakeSignal(10, strategyId: 2, orderId: 10,
                TradeDirection.Buy, entryPrice: 1.2500m, lotSize: 1.0m, symbol: "GBPUSD"),
            MakeSignal(11, strategyId: 2, orderId: 11,
                TradeDirection.Buy, entryPrice: 1.2500m, lotSize: 1.0m, symbol: "GBPUSD"),
        };

        // Strategy 1 has signals that reference valid orders
        signals.Add(MakeSignal(1, strategyId: 1, orderId: 1,
            TradeDirection.Buy, entryPrice: 1.1000m, lotSize: 1.0m, symbol: "EURUSD"));

        var orders = new List<Order>
        {
            MakeFilledOrder(1, filledPrice: 1.1050m),
            MakeFilledOrder(10, filledPrice: 1.2550m),
            MakeFilledOrder(11, filledPrice: 1.2550m),
        };

        var currencyPairs = new List<CurrencyPair>
        {
            MakePair("GBPUSD"),
            MakePair("EURUSD"),
        };

        // Use a custom read context setup that throws on the first strategy evaluation.
        // We achieve this by making the write context throw for strategy 1's snapshot add,
        // then succeed for strategy 2.
        var readDbContext = new Mock<DbContext>();
        readDbContext.Setup(c => c.Set<Strategy>()).Returns(
            new List<Strategy> { strategy1, strategy2 }.AsQueryable().BuildMockDbSet().Object);
        readDbContext.Setup(c => c.Set<TradeSignal>()).Returns(
            signals.AsQueryable().BuildMockDbSet().Object);
        readDbContext.Setup(c => c.Set<Order>()).Returns(
            orders.AsQueryable().BuildMockDbSet().Object);
        readDbContext.Setup(c => c.Set<CurrencyPair>()).Returns(
            currencyPairs.AsQueryable().BuildMockDbSet().Object);
        readDbContext.Setup(c => c.Set<EngineConfig>()).Returns(
            new List<EngineConfig>().AsQueryable().BuildMockDbSet().Object);
        _mockReadContext.Setup(c => c.GetDbContext()).Returns(readDbContext.Object);

        // Write context: first call to AddAsync for StrategyPerformanceSnapshot throws,
        // second call succeeds (simulating per-strategy error isolation).
        var writeDbContext = new Mock<DbContext>();

        // Strategy set for write context
        writeDbContext.Setup(c => c.Set<Strategy>()).Returns(
            new List<Strategy> { strategy1, strategy2 }.AsQueryable().BuildMockDbSet().Object);

        int snapshotAddCount = 0;
        var snapshotDbSet = new List<StrategyPerformanceSnapshot>().AsQueryable().BuildMockDbSet();
        snapshotDbSet.Setup(d => d.AddAsync(It.IsAny<StrategyPerformanceSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<StrategyPerformanceSnapshot, CancellationToken>((s, _) =>
            {
                snapshotAddCount++;
                if (snapshotAddCount == 1)
                    throw new InvalidOperationException("Simulated DB failure for strategy 1");
                _addedSnapshots.Add(s);
            })
            .ReturnsAsync((StrategyPerformanceSnapshot s, CancellationToken _) => null!);
        writeDbContext.Setup(c => c.Set<StrategyPerformanceSnapshot>()).Returns(snapshotDbSet.Object);

        var optRunDbSet = new List<OptimizationRun>().AsQueryable().BuildMockDbSet();
        optRunDbSet.Setup(d => d.AddAsync(It.IsAny<OptimizationRun>(), It.IsAny<CancellationToken>()))
            .Callback<OptimizationRun, CancellationToken>((r, _) => _addedOptimizationRuns.Add(r))
            .ReturnsAsync((OptimizationRun r, CancellationToken _) => null!);
        writeDbContext.Setup(c => c.Set<OptimizationRun>()).Returns(optRunDbSet.Object);

        _mockWriteContext.Setup(c => c.GetDbContext()).Returns(writeDbContext.Object);

        // Act
        await InvokeEvaluateAllActiveStrategiesAsync();

        // Assert -- strategy 2 was still evaluated despite strategy 1 throwing
        Assert.Single(_addedSnapshots);
        Assert.Equal(2, _addedSnapshots[0].StrategyId);

        // Assert -- SaveChangesAsync called at least once (for strategy 2)
        _mockWriteContext.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
