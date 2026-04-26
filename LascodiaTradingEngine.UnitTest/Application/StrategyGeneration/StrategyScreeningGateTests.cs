using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.UnitTest.Application.StrategyGeneration;

/// <summary>
/// Golden tests for the 12-gate screening pipeline. Each test arranges a backtest
/// stream that passes every gate except the one under test, then asserts the
/// expected <see cref="ScreeningFailureReason"/>. Pinning these prevents future
/// threshold tweaks or refactors from silently changing rejection semantics —
/// the lookahead audit is intentionally covered separately in
/// <see cref="StrategyScreeningLookaheadAuditTests"/>.
/// </summary>
public class StrategyScreeningGateTests
{
    // ── Gate 1: ZeroTradesIS ────────────────────────────────────────────────

    [Fact]
    public async Task Gate_ZeroTradesIS_IsBacktestProducesNoTrades_RejectsCandidate()
    {
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(trades: 0));

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.ZeroTradesIS);
    }

    // ── Gate 2: IsThreshold ─────────────────────────────────────────────────

    [Fact]
    public async Task Gate_IsThreshold_LowWinRate_RejectsCandidate()
    {
        // IS produces trades but win rate is below the configured floor.
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(
            trades: 10,
            winRate: 0.30m));

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.IsThreshold);
        Assert.Contains("WR=", outcome!.FailureReason ?? string.Empty);
    }

    [Fact]
    public async Task Gate_IsThreshold_CostToWinRatioTooHigh_RejectsCandidate()
    {
        // PF/WR/Sharpe pass, but transaction costs eat too much of each winning
        // trade — strategy is dangerously close to the friction floor.
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(
            trades: 10,
            averageWin: 50m,
            totalCommission: 250m, // 25/trade vs 50 avg win = 0.50 ratio (floor 0.35)
            totalSlippage: 0m));

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.IsThreshold);
        Assert.Contains("Cost/AvgWin=", outcome!.FailureReason ?? string.Empty);
    }

    // ── Gate 3: ZeroTradesOOS ───────────────────────────────────────────────

    [Fact]
    public async Task Gate_ZeroTradesOOS_OosBacktestProducesNoTrades_RejectsCandidate()
    {
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 12),       // IS
            2 => Healthy(trades: 0),        // OOS — empty
            _ => Healthy(trades: 5),
        });

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.ZeroTradesOOS);
    }

    // ── Gate 4: OosThreshold ────────────────────────────────────────────────

    [Fact]
    public async Task Gate_OosThreshold_OosProfitFactorBelowRelaxed_RejectsCandidate()
    {
        // IS healthy, OOS PF below MinProfitFactor × OosPfRelaxation (1.05 × 0.9 = 0.945).
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 12),
            2 => Healthy(trades: 8, profitFactor: 0.80m), // < 0.945 even after relaxation
            _ => Healthy(trades: 5),
        });

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.OosThreshold);
    }

    // ── Gate 5: Degradation ─────────────────────────────────────────────────

    [Fact]
    public async Task Gate_Degradation_OosSharpeCollapsesVsIs_RejectsCandidate()
    {
        // IS Sharpe 1.5; OOS Sharpe 0.4 (ratio 0.27) but OOS still passes its own
        // relaxed thresholds (0.30 × 0.8 = 0.24). The degradation gate must catch this.
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 12, sharpeRatio: 1.5m),
            2 => Healthy(trades: 8,  sharpeRatio: 0.4m),
            _ => Healthy(trades: 5),
        });

        var outcome = await Screen(engine);

        AssertFailure(outcome, ScreeningFailureReason.Degradation);
    }

    [Fact]
    public async Task Gate_Degradation_OosRegimeDiffers_RelaxedToleranceLetsCandidatePass()
    {
        // Same setup as the rejection test, but the OOS regime differs from the
        // target — RegimeDegradationRelaxation (1.5×) widens the tolerance and
        // the candidate is no longer rejected for degradation.
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 12, sharpeRatio: 1.5m),
            2 => Healthy(trades: 8,  sharpeRatio: 0.4m),
            _ => Healthy(trades: 5),
        });

        var outcome = await Screen(
            engine,
            oosRegime: MarketRegimeEnum.Ranging); // differs from default Trending

        Assert.NotNull(outcome);
        Assert.NotEqual(ScreeningFailureReason.Degradation, outcome!.Failure);
    }

    // ── Gate 6: EquityCurveR² ───────────────────────────────────────────────

    [Fact]
    public async Task Gate_EquityCurveR2_NonLinearEquityCurve_RejectsCandidate()
    {
        // Trades produce a "hockey stick" cumulative — long flat run followed by
        // a single PnL spike. R² of the equity-vs-time line will be far below 0.99.
        var spikeTrades = BuildTrades(new decimal[]
        {
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 1500, 5, 5, 5, 5, 5, 5, 5,
        });

        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: spikeTrades.Count, customTrades: spikeTrades),
            2 => Healthy(trades: 8),
            _ => Healthy(trades: 5),
        });

        var cfg = PermissiveConfig() with { MinEquityCurveR2 = 0.99 };

        var outcome = await Screen(engine, config: cfg);

        AssertFailure(outcome, ScreeningFailureReason.EquityCurveR2);
    }

    // ── Gate 7: TimeConcentration ───────────────────────────────────────────

    [Fact]
    public async Task Gate_TimeConcentration_AllTradesInSingleHour_RejectsCandidate()
    {
        // Combined IS+OOS trades all entered at the same wall-clock hour.
        var clusteredTrades = BuildTrades(
            pnls: Enumerable.Repeat(80m, 11).ToArray(),
            entryHour: 14);

        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: clusteredTrades.Count, customTrades: clusteredTrades),
            2 => Healthy(trades: 8, customTrades: BuildTrades(
                pnls: Enumerable.Repeat(60m, 8).ToArray(),
                entryHour: 14)),
            _ => Healthy(trades: 5),
        });

        var cfg = PermissiveConfig() with { MaxTradeTimeConcentration = 0.30 };

        var outcome = await Screen(engine, config: cfg);

        AssertFailure(outcome, ScreeningFailureReason.TimeConcentration);
    }

    // ── Gate 8: WalkForward ─────────────────────────────────────────────────

    [Fact]
    public async Task Gate_WalkForward_AllWindowsProduceZeroTrades_RejectsCandidate()
    {
        // allCandles >= 200 triggers WF; subsequent calls (windows) return 0
        // trades so no window passes — fewer than the required 2-of-3.
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 12),       // IS
            2 => Healthy(trades: 8),        // OOS
            _ => Healthy(trades: 0),        // walk-forward windows + lookahead
        });

        var allCandles = BuildCandles(220);

        // Disable lookahead so the WF gate is the one that rejects.
        var cfg = PermissiveConfig() with { LookaheadAuditEnabled = false };

        var outcome = await Screen(engine, config: cfg, allCandlesOverride: allCandles);

        AssertFailure(outcome, ScreeningFailureReason.WalkForward);
    }

    // ── Gate 9: MonteCarloSignFlip ──────────────────────────────────────────

    [Fact]
    public async Task Gate_MonteCarloSignFlip_NoiseDominatesSignal_RejectsCandidate()
    {
        // BacktestResult headline metrics fake-pass the IS gate, but the underlying
        // PnL series (alternating wins/losses with comparable magnitudes) has near-
        // zero true Sharpe — sign-flipping easily exceeds it, p-value blows past 0.05.
        var noisyTrades = BuildTrades(new decimal[]
        {
            +100, -95, +100, -95, +100, -95, +100, -95, +100, -95, +100,
        });

        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: noisyTrades.Count, customTrades: noisyTrades),
            2 => Healthy(trades: 8, customTrades: BuildTrades(new decimal[]
            {
                +50, -45, +50, -45, +50, -45, +50, -45,
            })),
            _ => Healthy(trades: 5),
        });

        var cfg = PermissiveConfig() with
        {
            MonteCarloEnabled       = true,
            MonteCarloPermutations  = 200,
            MonteCarloMinPValue     = 0.05,
        };

        var outcome = await Screen(engine, config: cfg);

        AssertFailure(outcome, ScreeningFailureReason.MonteCarloSignFlip);
    }

    // ── Gate 10: DeflatedSharpe ─────────────────────────────────────────────

    [Fact]
    public async Task Gate_DeflatedSharpe_HighTrialCount_DeflatesRawSharpeBelowFloor_RejectsCandidate()
    {
        // Raw OOS Sharpe is 0.5 — fine on its own, but with 500 strategy-parameter
        // trials in the cycle, the Bailey/López de Prado deflated Sharpe drops below
        // the 1.0 "meaningful" floor.
        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: 15, sharpeRatio: 0.6m),
            2 => Healthy(trades: 10, sharpeRatio: 0.5m),
            _ => Healthy(trades: 5),
        });

        var cfg = PermissiveConfig() with
        {
            MinDeflatedSharpe    = 1.0,
            DeflatedSharpeTrials = 500,
        };

        var outcome = await Screen(engine, config: cfg);

        AssertFailure(outcome, ScreeningFailureReason.DeflatedSharpe);
    }

    // ── Gate 11: MonteCarloShuffle (path-dependent metric) ──────────────────

    [Fact]
    public async Task Gate_MonteCarloShuffle_PathFriendlyTrades_PassesGate()
    {
        // Regression test: prior to the path-dependent fix this gate compared
        // permutation-invariant Sharpe and rejected every candidate. After the
        // fix, a path-friendly trade list (small alternating drawdowns) produces
        // a low actual max DD that random shuffles rarely match — p-value stays
        // below the 0.05 floor and the gate lets the candidate through.
        var pathFriendly = BuildTrades(new decimal[]
        {
            -25, +80, +80, -25, +80, +80, -25, +80, +80, -25, +80, +80,
        });

        var engine = new FuncBacktestEngine((_, _, callNumber) => callNumber switch
        {
            1 => Healthy(trades: pathFriendly.Count, customTrades: pathFriendly),
            2 => Healthy(trades: 8, customTrades: BuildTrades(new decimal[]
            {
                -25, +80, +80, -25, +80, +80, -25, +80,
            })),
            _ => Healthy(trades: 5),
        });

        var cfg = PermissiveConfig() with
        {
            MonteCarloShuffleEnabled       = true,
            MonteCarloShufflePermutations  = 200,
            MonteCarloShuffleMinPValue     = 0.05,
            // sign-flip stays disabled so any rejection points at shuffle.
            MonteCarloEnabled              = false,
        };

        var outcome = await Screen(engine, config: cfg);

        Assert.NotNull(outcome);
        Assert.NotEqual(ScreeningFailureReason.MonteCarloShuffle, outcome!.Failure);
    }

    // ── Bypass tracking: short-history skips must be visible in GateTrace ──

    [Fact]
    public async Task Bypass_ShortHistory_MarksWalkForwardAndLookaheadAsBypassed()
    {
        // allCandles < 200 short-circuits both the WalkForward and LookaheadAudit
        // gates. Before the fix those traces showed up as Passed=true with no
        // signal that they had been skipped. After the fix the Bypassed flag
        // surfaces the silent bypass so audit tooling can flag short-history
        // approvals as "untested" rather than "approved."
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(trades: 12));

        var outcome = await Screen(engine);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Passed, $"Expected pass, got {outcome.Failure}: {outcome.FailureReason}");

        var gateTrace = outcome.Metrics.GateTrace;
        Assert.NotNull(gateTrace);

        var walkForward = gateTrace!.LastOrDefault(g => g.Gate == "WalkForward");
        Assert.NotNull(walkForward);
        Assert.True(walkForward!.Bypassed,
            "WalkForward must be flagged Bypassed when allCandles < 200");

        var lookahead = gateTrace.LastOrDefault(g => g.Gate == "LookaheadAudit");
        Assert.NotNull(lookahead);
        Assert.True(lookahead!.Bypassed,
            "LookaheadAudit must be flagged Bypassed when allCandles < 200");
    }

    [Fact]
    public async Task Bypass_LongHistory_DoesNotMarkWalkForwardAsBypassed()
    {
        // Negative control: with sufficient history, WalkForward actually runs
        // and the Bypassed flag must stay false so monitoring can distinguish
        // a real pass from a silent skip.
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(trades: 12));
        var allCandles = BuildCandles(220);

        // Disable lookahead so its passing/bypass state isn't mixed into the
        // assertion — this test is about WalkForward.
        var cfg = PermissiveConfig() with { LookaheadAuditEnabled = false };

        var outcome = await Screen(engine, config: cfg, allCandlesOverride: allCandles);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Passed, $"Expected pass, got {outcome.Failure}: {outcome.FailureReason}");

        var walkForward = outcome.Metrics.GateTrace!.LastOrDefault(g => g.Gate == "WalkForward");
        Assert.NotNull(walkForward);
        Assert.False(walkForward!.Bypassed,
            "WalkForward must not be flagged Bypassed when allCandles >= 200");
    }

    // ── Happy path: every gate passes ───────────────────────────────────────

    [Fact]
    public async Task HappyPath_AllGatesPass_ReturnsPassingOutcome()
    {
        // All engine calls return the same healthy result. This pins the negative
        // control: nothing in the default-permissive config should reject a
        // healthy strategy with linear equity, modest costs, and clean PnL.
        var engine = new FuncBacktestEngine((_, _, _) => Healthy(trades: 12));

        var outcome = await Screen(engine);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Passed, $"Expected pass, got {outcome.Failure}: {outcome.FailureReason}");
        Assert.Equal(ScreeningFailureReason.None, outcome.Failure);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Default screening invocation. Uses small-allCandles (skips walk-forward
    /// and lookahead unless explicitly overridden), trending regime, and the
    /// permissive config from <see cref="PermissiveConfig"/>.
    /// </summary>
    private static async Task<ScreeningOutcome?> Screen(
        IBacktestEngine engine,
        ScreeningConfig? config = null,
        ScreeningThresholds? thresholds = null,
        List<Candle>? allCandlesOverride = null,
        MarketRegimeEnum? oosRegime = null)
    {
        var allCandles = allCandlesOverride ?? BuildCandles(150);
        var trainCandles = BuildCandles(120);
        var testCandles = BuildCandles(80);

        return await new StrategyScreeningEngine(engine, NullLogger.Instance)
            .ScreenCandidateAsync(
                StrategyType.MovingAverageCrossover,
                "EURUSD",
                Timeframe.H1,
                "{\"Template\":\"Primary\"}",
                0,
                allCandles, trainCandles, testCandles,
                new BacktestOptions(),
                thresholds ?? DefaultThresholds(),
                config ?? PermissiveConfig(),
                MarketRegimeEnum.Trending,
                "Primary",
                CancellationToken.None,
                oosRegime: oosRegime);
    }

    private static void AssertFailure(ScreeningOutcome? outcome, ScreeningFailureReason expected)
    {
        Assert.NotNull(outcome);
        Assert.False(outcome!.Passed, $"Expected failure {expected} but candidate passed");
        Assert.Equal(expected, outcome.Failure);
    }

    private static ScreeningThresholds DefaultThresholds()
        => new(MinWinRate: 0.55,
               MinProfitFactor: 1.05,
               MinSharpe: 0.30,
               MaxDrawdownPct: 0.30,
               MinTotalTrades: 5);

    /// <summary>
    /// Permissive baseline: every late-stage gate is either disabled or made
    /// lenient enough that only the gate explicitly being exercised should fire.
    /// Lookahead audit stays enabled by default; tests using small allCandles
    /// (&lt; 200) bypass it via the size guard inside the engine.
    /// </summary>
    private static ScreeningConfig PermissiveConfig() => new()
    {
        ScreeningTimeoutSeconds   = 5,
        ScreeningInitialBalance   = 10_000m,
        MaxOosDegradationPct      = 0.50,
        MinEquityCurveR2          = 0.0,  // disable
        MaxTradeTimeConcentration = 1.0,  // disable
        MonteCarloEnabled         = false,
        MonteCarloShuffleEnabled  = false,
        MinDeflatedSharpe         = 0.0,  // disable
    };

    private static List<Candle> BuildCandles(int count)
        => Enumerable.Range(0, count).Select(i => new Candle
        {
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = DateTime.UtcNow.AddHours(-count + i),
            Open      = 1.1000m,
            High      = 1.1010m,
            Low       = 1.0990m,
            Close     = 1.1005m,
            Volume    = 1000 + i,
            IsClosed  = true,
        }).ToList();

    /// <summary>
    /// Builds a healthy <see cref="BacktestResult"/> for any phase. Default field
    /// values pass the IS threshold gate, the OOS threshold gate, and skip the
    /// Kelly position-sizing gate (AverageLoss = 0). Override individual fields
    /// via named arguments to target a specific failure mode.
    /// </summary>
    private static BacktestResult Healthy(
        int trades,
        decimal? winRate = null,
        decimal profitFactor = 1.80m,
        decimal sharpeRatio = 1.20m,
        decimal maxDrawdownPct = 0.12m,
        decimal averageWin = 80m,
        decimal totalCommission = 0m,
        decimal totalSlippage = 0m,
        decimal totalSwap = 0m,
        decimal finalBalance = 11_500m,
        IReadOnlyList<BacktestTrade>? customTrades = null)
    {
        var defaultedWinRate = winRate ?? 0.65m;
        var tradeList = customTrades is not null
            ? customTrades.ToList()
            : Enumerable.Range(0, trades).Select(i => new BacktestTrade
            {
                PnL       = i % 3 == 0 ? -25m : 80m,
                EntryTime = DateTime.UtcNow.AddHours(-40 + i * 2),
                ExitTime  = DateTime.UtcNow.AddHours(-39 + i * 2),
            }).ToList();

        return new BacktestResult
        {
            TotalTrades      = trades,
            WinningTrades    = (int)(trades * (double)defaultedWinRate),
            LosingTrades     = trades - (int)(trades * (double)defaultedWinRate),
            WinRate          = defaultedWinRate,
            ProfitFactor     = profitFactor,
            SharpeRatio      = sharpeRatio,
            MaxDrawdownPct   = maxDrawdownPct,
            AverageWin       = averageWin,
            // AverageLoss = 0 keeps the Kelly position-sizing gate inert so it
            // never becomes the cause of an unrelated test failure.
            AverageLoss      = 0m,
            TotalCommission  = totalCommission,
            TotalSlippage    = totalSlippage,
            TotalSwap        = totalSwap,
            Trades           = tradeList,
            InitialBalance   = 10_000m,
            FinalBalance     = finalBalance,
        };
    }

    /// <summary>
    /// Builds a synthetic trade list with explicit PnLs, optionally pinning every
    /// entry to a specific wall-clock hour for the time-concentration gate.
    /// </summary>
    private static List<BacktestTrade> BuildTrades(decimal[] pnls, int? entryHour = null)
    {
        var basis = DateTime.UtcNow.Date.AddHours(-pnls.Length * 2);
        return pnls.Select((p, i) =>
        {
            var entry = entryHour is null
                ? basis.AddHours(i * 2)
                : basis.Date.AddDays(i).AddHours(entryHour.Value);
            return new BacktestTrade
            {
                PnL       = p,
                EntryTime = entry,
                ExitTime  = entry.AddMinutes(30),
            };
        }).ToList();
    }

    private sealed class FuncBacktestEngine : IBacktestEngine
    {
        private readonly Func<Strategy, IReadOnlyList<Candle>, int, BacktestResult> _fn;
        private int _calls;

        public FuncBacktestEngine(Func<Strategy, IReadOnlyList<Candle>, int, BacktestResult> fn)
            => _fn = fn;

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
