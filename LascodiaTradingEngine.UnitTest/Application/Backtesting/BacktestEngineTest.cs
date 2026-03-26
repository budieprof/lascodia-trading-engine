using Moq;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Backtesting;

public class BacktestEngineTest
{
    private readonly CancellationToken _ct = CancellationToken.None;

    // ── Helper factories ─────────────────────────────────────────────────────

    private static Strategy CreateStrategy(StrategyType type = StrategyType.MovingAverageCrossover) => new()
    {
        Id           = 1,
        StrategyType = type,
        Symbol       = "EURUSD",
        Timeframe    = Timeframe.H1,
        Status       = StrategyStatus.Active
    };

    private static Candle MakeCandle(
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        DateTime? timestamp = null) => new()
    {
        Symbol    = "EURUSD",
        Timeframe = Timeframe.H1,
        Open      = open,
        High      = high,
        Low       = low,
        Close     = close,
        IsClosed  = true,
        IsDeleted = false,
        Timestamp = timestamp ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(index)
    };

    /// <summary>
    /// Creates a mock evaluator that returns a signal when the candle window reaches
    /// <paramref name="signalAtWindowSize"/> candles, and null otherwise.
    /// </summary>
    private static Mock<IStrategyEvaluator> CreateMockEvaluator(
        StrategyType type,
        int signalAtWindowSize,
        TradeDirection direction,
        decimal entryPrice,
        decimal? stopLoss,
        decimal? takeProfit,
        decimal lotSize = 0.1m)
    {
        var mock = new Mock<IStrategyEvaluator>();
        mock.Setup(e => e.StrategyType).Returns(type);
        mock.Setup(e => e.MinRequiredCandles(It.IsAny<Strategy>())).Returns(1);

        mock.Setup(e => e.EvaluateAsync(
                It.IsAny<Strategy>(),
                It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal Bid, decimal Ask)>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Strategy _, IReadOnlyList<Candle> candles, (decimal, decimal) _, CancellationToken _) =>
            {
                if (candles.Count == signalAtWindowSize)
                {
                    return new TradeSignal
                    {
                        Direction        = direction,
                        EntryPrice       = entryPrice,
                        StopLoss         = stopLoss,
                        TakeProfit       = takeProfit,
                        SuggestedLotSize = lotSize,
                        Confidence       = 0.8m,
                        StrategyId       = 1,
                        Symbol           = "EURUSD",
                        ExpiresAt        = DateTime.UtcNow.AddHours(1)
                    };
                }

                return null;
            });

        return mock;
    }

    /// <summary>Creates a mock evaluator that never signals.</summary>
    private static Mock<IStrategyEvaluator> CreateSilentEvaluator(StrategyType type)
    {
        var mock = new Mock<IStrategyEvaluator>();
        mock.Setup(e => e.StrategyType).Returns(type);
        mock.Setup(e => e.MinRequiredCandles(It.IsAny<Strategy>())).Returns(1);
        mock.Setup(e => e.EvaluateAsync(
                It.IsAny<Strategy>(),
                It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal Bid, decimal Ask)>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((TradeSignal?)null);
        return mock;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoEvaluatorForStrategyType_ThrowsInvalidOperation()
    {
        // Arrange — register an evaluator for a different type
        var evaluator = CreateSilentEvaluator(StrategyType.RSIReversion);
        var engine    = new BacktestEngine(new[] { evaluator.Object });
        var strategy  = CreateStrategy(StrategyType.MovingAverageCrossover);
        var candles   = new List<Candle> { MakeCandle(0, 1.00m, 1.01m, 0.99m, 1.005m) };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync(strategy, candles, 10_000m, _ct));
    }

    [Fact]
    public async Task RunAsync_EvaluatorNeverSignals_ReturnsZeroTrades()
    {
        // Arrange
        var evaluator = CreateSilentEvaluator(StrategyType.MovingAverageCrossover);
        var engine    = new BacktestEngine(new[] { evaluator.Object });
        var strategy  = CreateStrategy();
        var candles   = Enumerable.Range(0, 20)
            .Select(i => MakeCandle(i, 1.00m, 1.01m, 0.99m, 1.005m))
            .ToList();

        // Act
        var result = await engine.RunAsync(strategy, candles, 10_000m, _ct);

        // Assert
        Assert.Equal(0, result.TotalTrades);
        Assert.Equal(10_000m, result.FinalBalance);
        Assert.Equal(0m, result.TotalReturn);
        Assert.Empty(result.Trades);
    }

    [Fact]
    public async Task RunAsync_BuySignalHitsTP_RecordsProfitableTrade()
    {
        // Arrange: signal on bar 2 (window size 3), entry on bar 3 open at 1.0000
        // TP = 1.0200, SL = 0.9900
        // Bar 4 has High >= 1.0200 so TP is hit.
        var evaluator = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 3,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9900m,
            takeProfit: 1.0200m,
            lotSize: 0.1m);

        var engine   = new BacktestEngine(new[] { evaluator.Object });
        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0000m, 1.0050m, 0.9950m, 1.0010m), // bar 0
            MakeCandle(1, 1.0010m, 1.0060m, 0.9970m, 1.0020m), // bar 1
            MakeCandle(2, 1.0020m, 1.0070m, 0.9980m, 1.0030m), // bar 2 — signal fires here (window=3)
            MakeCandle(3, 1.0000m, 1.0100m, 0.9950m, 1.0050m), // bar 3 — entry on open 1.0000
            MakeCandle(4, 1.0050m, 1.0250m, 1.0000m, 1.0200m), // bar 4 — High >= 1.0200 → TP hit
        };

        // Act
        var result = await engine.RunAsync(strategy, candles, 10_000m, _ct);

        // Assert
        Assert.Equal(1, result.TotalTrades);
        Assert.Equal(1, result.WinningTrades);
        Assert.Equal(0, result.LosingTrades);

        var trade = result.Trades[0];
        Assert.Equal(TradeDirection.Buy, trade.Direction);
        Assert.Equal(1.0000m, trade.EntryPrice);
        Assert.Equal(1.0200m, trade.ExitPrice);  // TP level, no slippage
        Assert.Equal(TradeExitReason.TakeProfit, trade.ExitReason);

        // PnL = (1.02 - 1.00) * 0.1 * 100,000 = 200
        Assert.Equal(200m, trade.PnL);
        Assert.Equal(10_200m, result.FinalBalance);
    }

    [Fact]
    public async Task RunAsync_SellSignalHitsSL_RecordsLosingTrade()
    {
        // Arrange: signal on bar 1 (window size 2), entry on bar 2 open at 1.0500
        // SL = 1.0600 (above entry for sell), TP = 1.0300
        // Bar 3 has High >= 1.0600 → SL hit for sell direction.
        var evaluator = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Sell,
            entryPrice: 1.0500m,
            stopLoss: 1.0600m,
            takeProfit: 1.0300m,
            lotSize: 0.1m);

        var engine   = new BacktestEngine(new[] { evaluator.Object });
        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0500m, 1.0550m, 1.0450m, 1.0510m), // bar 0
            MakeCandle(1, 1.0510m, 1.0560m, 1.0460m, 1.0520m), // bar 1 — signal fires (window=2)
            MakeCandle(2, 1.0500m, 1.0550m, 1.0450m, 1.0480m), // bar 2 — entry on open 1.0500
            MakeCandle(3, 1.0480m, 1.0650m, 1.0470m, 1.0620m), // bar 3 — High >= 1.0600 → SL hit
            MakeCandle(4, 1.0620m, 1.0650m, 1.0580m, 1.0600m), // bar 4 — not reached
        };

        // Act
        var result = await engine.RunAsync(strategy, candles, 10_000m, _ct);

        // Assert
        Assert.Equal(1, result.TotalTrades);
        Assert.Equal(0, result.WinningTrades);
        Assert.Equal(1, result.LosingTrades);

        var trade = result.Trades[0];
        Assert.Equal(TradeDirection.Sell, trade.Direction);
        Assert.Equal(1.0500m, trade.EntryPrice);
        Assert.Equal(1.0600m, trade.ExitPrice);  // SL level, no slippage
        Assert.Equal(TradeExitReason.StopLoss, trade.ExitReason);

        // PnL = (1.0500 - 1.0600) * 0.1 * 100,000 = -100
        Assert.Equal(-100m, trade.PnL);
        Assert.Equal(9_900m, result.FinalBalance);
    }

    [Fact]
    public async Task RunAsync_WithSlippage_ReducesPnL()
    {
        // Arrange: Buy signal hitting TP. Run once without slippage, once with.
        var evaluatorNoSlip = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9900m,
            takeProfit: 1.0200m,
            lotSize: 0.1m);

        var evaluatorWithSlip = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9900m,
            takeProfit: 1.0200m,
            lotSize: 0.1m);

        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0000m, 1.0050m, 0.9950m, 1.0010m), // bar 0
            MakeCandle(1, 1.0010m, 1.0060m, 0.9970m, 1.0020m), // bar 1 — signal (window=2)
            MakeCandle(2, 1.0000m, 1.0100m, 0.9960m, 1.0050m), // bar 2 — entry on open 1.0000
            MakeCandle(3, 1.0050m, 1.0250m, 1.0000m, 1.0200m), // bar 3 — TP hit
        };

        var engineNoSlip   = new BacktestEngine(new[] { evaluatorNoSlip.Object });
        var engineWithSlip = new BacktestEngine(new[] { evaluatorWithSlip.Object });

        // Act
        var resultNoSlip = await engineNoSlip.RunAsync(strategy, candles, 10_000m, _ct);
        var resultWithSlip = await engineWithSlip.RunAsync(
            strategy, candles, 10_000m, _ct,
            new BacktestOptions { SlippagePriceUnits = 0.0005m });

        // Assert — slippage reduces PnL
        Assert.True(resultWithSlip.Trades[0].PnL < resultNoSlip.Trades[0].PnL,
            "Slippage should reduce PnL.");

        // Entry slips up by 0.0005, exit slips down by 0.0005 → total slip = 0.001
        // PnL reduction = 0.001 * 0.1 * 100,000 = 100
        decimal expectedDifference = 0.001m * 0.1m * 100_000m; // 100
        decimal actualDifference = resultNoSlip.Trades[0].PnL - resultWithSlip.Trades[0].PnL;
        Assert.Equal(expectedDifference, actualDifference);
    }

    [Fact]
    public async Task RunAsync_WithCommission_ReducesPnL()
    {
        // Arrange: same profitable Buy trade, with and without commission.
        var evaluatorNoComm = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9900m,
            takeProfit: 1.0200m,
            lotSize: 0.1m);

        var evaluatorWithComm = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9900m,
            takeProfit: 1.0200m,
            lotSize: 0.1m);

        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0000m, 1.0050m, 0.9950m, 1.0010m),
            MakeCandle(1, 1.0010m, 1.0060m, 0.9970m, 1.0020m), // signal (window=2)
            MakeCandle(2, 1.0000m, 1.0100m, 0.9960m, 1.0050m), // entry
            MakeCandle(3, 1.0050m, 1.0250m, 1.0000m, 1.0200m), // TP hit
        };

        var engineNoComm   = new BacktestEngine(new[] { evaluatorNoComm.Object });
        var engineWithComm = new BacktestEngine(new[] { evaluatorWithComm.Object });

        // Act
        var resultNoComm = await engineNoComm.RunAsync(strategy, candles, 10_000m, _ct);
        var resultWithComm = await engineWithComm.RunAsync(
            strategy, candles, 10_000m, _ct,
            new BacktestOptions { CommissionPerLot = 70m }); // $70 per lot round-trip

        // Assert
        Assert.True(resultWithComm.Trades[0].PnL < resultNoComm.Trades[0].PnL,
            "Commission should reduce PnL.");

        // Commission = lotSize * commissionPerLot = 0.1 * 70 = 7
        decimal expectedDifference = 0.1m * 70m; // 7
        decimal actualDifference = resultNoComm.Trades[0].PnL - resultWithComm.Trades[0].PnL;
        Assert.Equal(expectedDifference, actualDifference);
    }

    [Fact]
    public async Task RunAsync_OpenTradeAtEndOfData_ClosedAtLastBarClose()
    {
        // Arrange: signal fires on last viable bar, trade never hits SL/TP, closed at end.
        // Signal on bar 1 (window=2), entry on bar 2 at open 1.0000.
        // Bar 2 is the last bar — no SL/TP hit, so trade closes at bar 2 close.
        var evaluator = CreateMockEvaluator(
            StrategyType.MovingAverageCrossover,
            signalAtWindowSize: 2,
            direction: TradeDirection.Buy,
            entryPrice: 1.0000m,
            stopLoss: 0.9800m,   // far away
            takeProfit: 1.0500m, // far away
            lotSize: 0.1m);

        var engine   = new BacktestEngine(new[] { evaluator.Object });
        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0000m, 1.0050m, 0.9950m, 1.0010m), // bar 0
            MakeCandle(1, 1.0010m, 1.0060m, 0.9970m, 1.0020m), // bar 1 — signal (window=2)
            MakeCandle(2, 1.0000m, 1.0080m, 0.9950m, 1.0050m), // bar 2 — entry on open, last bar
        };

        // Act
        var result = await engine.RunAsync(strategy, candles, 10_000m, _ct);

        // Assert
        Assert.Equal(1, result.TotalTrades);
        var trade = result.Trades[0];
        Assert.Equal(TradeExitReason.EndOfData, trade.ExitReason);
        Assert.Equal(1.0050m, trade.ExitPrice); // last bar's Close (no slippage)
        Assert.Equal(candles[2].Timestamp, trade.ExitTime);

        // PnL = (1.0050 - 1.0000) * 0.1 * 100,000 = 50
        Assert.Equal(50m, trade.PnL);
    }

    [Fact]
    public async Task RunAsync_MultipleTradesCalculatesStatisticsCorrectly()
    {
        // Arrange: two trades — one winner (Buy hits TP), one loser (Buy hits SL).
        // Trade 1: signal at window=2, entry bar 2 open=1.0000, TP=1.0100, SL=0.9950
        //   Bar 3: High >= 1.0100 → TP hit. PnL = (1.01-1.00)*0.1*100000 = 1000
        // Trade 2: signal at window=5, entry bar 5 open=1.0100, TP=1.0200, SL=1.0050
        //   Bar 6: Low <= 1.0050 → SL hit. PnL = (1.005-1.01)*0.1*100000 = -500
        var mock = new Mock<IStrategyEvaluator>();
        mock.Setup(e => e.StrategyType).Returns(StrategyType.MovingAverageCrossover);
        mock.Setup(e => e.MinRequiredCandles(It.IsAny<Strategy>())).Returns(1);
        mock.Setup(e => e.EvaluateAsync(
                It.IsAny<Strategy>(),
                It.IsAny<IReadOnlyList<Candle>>(),
                It.IsAny<(decimal Bid, decimal Ask)>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Strategy _, IReadOnlyList<Candle> candles, (decimal, decimal) _, CancellationToken _) =>
            {
                // First signal at window size 2
                if (candles.Count == 2)
                {
                    return new TradeSignal
                    {
                        Direction        = TradeDirection.Buy,
                        EntryPrice       = 1.0000m,
                        StopLoss         = 0.9950m,
                        TakeProfit       = 1.0100m,
                        SuggestedLotSize = 0.1m,
                        Confidence       = 0.8m,
                        StrategyId       = 1,
                        Symbol           = "EURUSD",
                        ExpiresAt        = DateTime.UtcNow.AddHours(1)
                    };
                }

                // Second signal at window size 5
                if (candles.Count == 5)
                {
                    return new TradeSignal
                    {
                        Direction        = TradeDirection.Buy,
                        EntryPrice       = 1.0100m,
                        StopLoss         = 1.0050m,
                        TakeProfit       = 1.0200m,
                        SuggestedLotSize = 0.1m,
                        Confidence       = 0.7m,
                        StrategyId       = 1,
                        Symbol           = "EURUSD",
                        ExpiresAt        = DateTime.UtcNow.AddHours(1)
                    };
                }

                return null;
            });

        var engine   = new BacktestEngine(new[] { mock.Object });
        var strategy = CreateStrategy();

        var candles = new List<Candle>
        {
            MakeCandle(0, 1.0000m, 1.0050m, 0.9960m, 1.0010m), // bar 0
            MakeCandle(1, 1.0010m, 1.0060m, 0.9970m, 1.0020m), // bar 1 — signal 1 (window=2)
            MakeCandle(2, 1.0000m, 1.0040m, 0.9960m, 1.0020m), // bar 2 — entry 1 at open 1.0000
            MakeCandle(3, 1.0020m, 1.0150m, 0.9980m, 1.0100m), // bar 3 — TP1 hit (High >= 1.01)
            MakeCandle(4, 1.0100m, 1.0130m, 1.0060m, 1.0110m), // bar 4 — signal 2 (window=5)
            MakeCandle(5, 1.0100m, 1.0130m, 1.0060m, 1.0090m), // bar 5 — entry 2 at open 1.0100
            MakeCandle(6, 1.0090m, 1.0095m, 1.0020m, 1.0040m), // bar 6 — SL2 hit (Low <= 1.005)
            MakeCandle(7, 1.0040m, 1.0060m, 1.0020m, 1.0050m), // bar 7 — filler
        };

        // Act
        var result = await engine.RunAsync(strategy, candles, 10_000m, _ct);

        // Assert — trade counts
        Assert.Equal(2, result.TotalTrades);
        Assert.Equal(1, result.WinningTrades);
        Assert.Equal(1, result.LosingTrades);

        // Win rate = 1/2 = 0.5
        Assert.Equal(0.5m, result.WinRate);

        // Trade 1: PnL = (1.01-1.00)*0.1*100000 = +100, Trade 2: PnL = (1.005-1.01)*0.1*100000 = -50
        Assert.Equal(100m, result.Trades[0].PnL);
        Assert.Equal(-50m, result.Trades[1].PnL);

        // Profit factor = grossProfit / grossLoss = 100 / 50 = 2.0
        Assert.Equal(2.0m, result.ProfitFactor);

        // Final balance = 10,000 + 100 - 50 = 10,050
        Assert.Equal(10_050m, result.FinalBalance);

        // Total return = (10050 - 10000) / 10000 * 100 = 0.5%
        Assert.Equal(0.5m, result.TotalReturn);

        // Max drawdown: equity curve = [10000, 10100, 10050]
        // Peak = 10100, trough = 10050 → drawdown = (10100-10050)/10100*100 ≈ 0.4950...%
        Assert.True(result.MaxDrawdownPct > 0m, "Should have non-zero drawdown.");
        decimal expectedDrawdown = Math.Round((10_100m - 10_050m) / 10_100m * 100m, 4);
        Assert.Equal(expectedDrawdown, result.MaxDrawdownPct);

        // Sharpe ratio should be non-zero with mixed PnLs
        Assert.NotEqual(0m, result.SharpeRatio);
    }
}
