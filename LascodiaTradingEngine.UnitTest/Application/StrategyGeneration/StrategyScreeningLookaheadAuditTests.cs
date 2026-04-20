using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Covers the post-walk-forward lookahead continuity audit: a full-range replay
/// whose trade count / PnL diverges materially from (IS + OOS) indicates the
/// evaluator or backtest engine is seeing future candles at the boundary.
/// </summary>
public class StrategyScreeningLookaheadAuditTests
{
    [Fact]
    public async Task LookaheadAudit_FullRangeDivergesOnTradeCount_RejectsCandidate()
    {
        // Arrange: IS + OOS both produce ~28 trades; the full-range replay
        // produces 500 — that is the telltale signature of lookahead where the
        // boundary-straddling window exposes future bars.
        var allCandles = BuildCandles(220);
        var trainCandles = BuildCandles(160);
        var testCandles = BuildCandles(60);

        var engine = new FuncBacktestEngine((_, candles, _) =>
        {
            if (candles.Count == 160) return Passing(trades: 20, finalBalance: 11_500m);
            if (candles.Count == 60)  return Passing(trades: 8,  finalBalance: 10_500m);
            // Walk-forward windows — small to medium sizes. Keep passing so
            // we actually reach the lookahead audit gate.
            if (candles.Count < 160) return Passing(trades: 5,  finalBalance: 10_200m);
            // The full-range audit call: same range as allCandles.
            if (candles.Count == 220) return Passing(trades: 500, finalBalance: 25_000m);
            return Passing(trades: 0, finalBalance: 10_000m);
        });

        var outcome = await new StrategyScreeningEngine(engine, NullLogger.Instance)
            .ScreenCandidateAsync(
                StrategyType.MovingAverageCrossover,
                "EURUSD",
                Timeframe.H1,
                "{\"Template\":\"Primary\"}",
                0,
                allCandles, trainCandles, testCandles,
                new BacktestOptions(),
                new ScreeningThresholds(0.55, 1.05, 0.2, 0.30, 5),
                DisableDownstreamGates(),
                MarketRegime.Trending,
                "Primary",
                CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Passed);
        Assert.Equal(ScreeningFailureReason.LookaheadAudit, outcome.Failure);
        Assert.Equal("LookaheadAuditRejected", outcome.FailureOutcome);
        Assert.Contains("lookahead audit failed", outcome.FailureReason ?? string.Empty);
    }

    [Fact]
    public async Task LookaheadAudit_FullRangeDivergesOnPnl_RejectsCandidate()
    {
        // Same trade count but very different PnL → the other half of the audit.
        var allCandles = BuildCandles(220);
        var trainCandles = BuildCandles(160);
        var testCandles = BuildCandles(60);

        var engine = new FuncBacktestEngine((_, candles, _) =>
        {
            if (candles.Count == 160) return Passing(trades: 20, finalBalance: 11_000m);
            if (candles.Count == 60)  return Passing(trades: 8,  finalBalance: 10_500m);
            if (candles.Count < 160)  return Passing(trades: 5,  finalBalance: 10_200m);
            // Same trade-count neighbourhood but PnL is 4× the split combined (~1,500).
            if (candles.Count == 220) return Passing(trades: 28, finalBalance: 20_000m);
            return Passing(trades: 0, finalBalance: 10_000m);
        });

        var outcome = await new StrategyScreeningEngine(engine, NullLogger.Instance)
            .ScreenCandidateAsync(
                StrategyType.MovingAverageCrossover,
                "EURUSD",
                Timeframe.H1,
                "{\"Template\":\"Primary\"}",
                0,
                allCandles, trainCandles, testCandles,
                new BacktestOptions(),
                new ScreeningThresholds(0.55, 1.05, 0.2, 0.30, 5),
                DisableDownstreamGates(),
                MarketRegime.Trending,
                "Primary",
                CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Passed);
        Assert.Equal(ScreeningFailureReason.LookaheadAudit, outcome.Failure);
    }

    [Fact]
    public async Task LookaheadAudit_FullRangeMatchesSplit_AllowsCandidateToContinue()
    {
        // Negative control: full-range trade count & PnL within tolerances of
        // (IS + OOS) — the audit must NOT reject. Downstream gates are also
        // disabled here, so the outcome should be "passed".
        var allCandles = BuildCandles(220);
        var trainCandles = BuildCandles(160);
        var testCandles = BuildCandles(60);

        var engine = new FuncBacktestEngine((_, candles, _) =>
        {
            if (candles.Count == 160) return Passing(trades: 20, finalBalance: 11_200m);
            if (candles.Count == 60)  return Passing(trades: 8,  finalBalance: 10_500m);
            if (candles.Count < 160)  return Passing(trades: 5,  finalBalance: 10_200m);
            // Full-range result close to concatenated split: 28 vs 28 trades,
            // combined PnL 1_700 vs 1_650 — well within the 50% tolerance.
            if (candles.Count == 220) return Passing(trades: 28, finalBalance: 11_650m);
            return Passing(trades: 0, finalBalance: 10_000m);
        });

        var outcome = await new StrategyScreeningEngine(engine, NullLogger.Instance)
            .ScreenCandidateAsync(
                StrategyType.MovingAverageCrossover,
                "EURUSD",
                Timeframe.H1,
                "{\"Template\":\"Primary\"}",
                0,
                allCandles, trainCandles, testCandles,
                new BacktestOptions(),
                new ScreeningThresholds(0.55, 1.05, 0.2, 0.30, 5),
                DisableDownstreamGates(),
                MarketRegime.Trending,
                "Primary",
                CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.NotEqual(ScreeningFailureReason.LookaheadAudit, outcome!.Failure);
    }

    [Fact]
    public async Task LookaheadAudit_DisabledByConfig_SkipsGate_EvenOnDivergentFullRange()
    {
        var allCandles = BuildCandles(220);
        var trainCandles = BuildCandles(160);
        var testCandles = BuildCandles(60);

        var engine = new FuncBacktestEngine((_, candles, _) =>
        {
            if (candles.Count == 160) return Passing(trades: 20, finalBalance: 11_200m);
            if (candles.Count == 60)  return Passing(trades: 8,  finalBalance: 10_500m);
            if (candles.Count < 160)  return Passing(trades: 5,  finalBalance: 10_200m);
            if (candles.Count == 220) return Passing(trades: 500, finalBalance: 25_000m);
            return Passing(trades: 0, finalBalance: 10_000m);
        });

        var cfg = DisableDownstreamGates() with { LookaheadAuditEnabled = false };

        var outcome = await new StrategyScreeningEngine(engine, NullLogger.Instance)
            .ScreenCandidateAsync(
                StrategyType.MovingAverageCrossover,
                "EURUSD",
                Timeframe.H1,
                "{\"Template\":\"Primary\"}",
                0,
                allCandles, trainCandles, testCandles,
                new BacktestOptions(),
                new ScreeningThresholds(0.55, 1.05, 0.2, 0.30, 5),
                cfg,
                MarketRegime.Trending,
                "Primary",
                CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.NotEqual(ScreeningFailureReason.LookaheadAudit, outcome!.Failure);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ScreeningConfig DisableDownstreamGates() => new()
    {
        ScreeningTimeoutSeconds     = 5,
        ScreeningInitialBalance     = 10_000m,
        MaxOosDegradationPct        = 0.95, // lenient
        MinEquityCurveR2            = 0.0,  // skip
        MaxTradeTimeConcentration   = 1.0,  // skip
        MonteCarloEnabled           = false,
        MonteCarloShuffleEnabled    = false,
        MinDeflatedSharpe           = 0.0,  // skip
        // Leave LookaheadAuditEnabled = true by default.
    };

    private static List<Candle> BuildCandles(int count)
        => Enumerable.Range(0, count).Select(i => new Candle
        {
            Symbol     = "TEST",
            Timeframe  = Timeframe.H1,
            Timestamp  = DateTime.UtcNow.AddHours(-count + i),
            Open       = 1.1000m,
            High       = 1.1010m,
            Low        = 1.0990m,
            Close      = 1.1005m,
            Volume     = 1000 + i,
            IsClosed   = true,
        }).ToList();

    private static BacktestResult Passing(int trades, decimal finalBalance)
    {
        // Produce the minimum synthetic BacktestResult the screening pipeline
        // needs: enough trades to cross the IS threshold and a distinct
        // FinalBalance so the lookahead audit can compute a meaningful PnL
        // delta against the concatenated split.
        var tradeList = Enumerable.Range(0, trades).Select(i => new BacktestTrade
        {
            PnL       = i % 3 == 0 ? -25m : 80m,
            EntryTime = DateTime.UtcNow.AddHours(-40 + i * 2),
            ExitTime  = DateTime.UtcNow.AddHours(-39 + i * 2),
        }).ToList();

        return new BacktestResult
        {
            TotalTrades      = trades,
            WinningTrades    = (int)(trades * 0.65),
            LosingTrades     = trades - (int)(trades * 0.65),
            WinRate          = 0.65m,
            ProfitFactor     = 1.80m,
            SharpeRatio      = 1.20m,
            MaxDrawdownPct   = 0.12m,
            AverageWin       = 80m,
            // AverageLoss = 0 so the Kelly position-sizing gate takes the
            // fallback b = 1 path and skips when kellyFull <= 0 — we want
            // the audit to be the ONLY gate that can reject in this test.
            AverageLoss      = 0m,
            Trades           = tradeList,
            InitialBalance   = 10_000m,
            FinalBalance     = finalBalance,
        };
    }

    private sealed class FuncBacktestEngine : IBacktestEngine
    {
        private readonly Func<Strategy, IReadOnlyList<Candle>, int, BacktestResult> _fn;
        private int _calls;
        public FuncBacktestEngine(Func<Strategy, IReadOnlyList<Candle>, int, BacktestResult> fn) => _fn = fn;

        public Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            _calls++;
            return Task.FromResult(_fn(strategy, candles, _calls));
        }
    }
}
