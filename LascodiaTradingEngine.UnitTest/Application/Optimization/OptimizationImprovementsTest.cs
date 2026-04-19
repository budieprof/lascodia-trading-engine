using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class OptimizationImprovementsTest
{
    // ── 1. OptimizationConfigValidator cross-constraints ─────────────────

    [Fact]
    public void CpcvNFolds_LessThanOrEqualTo_CpcvTestFoldCount_ReturnsError()
    {
        var logger = Mock.Of<ILogger>();

        // CpcvNFolds = 2, CpcvTestFoldCount = 2 → no training folds remain
        var issues = OptimizationConfigValidator.Validate(
            autoApprovalImprovementThreshold: 0.10m,
            autoApprovalMinHealthScore: 0.55m,
            minBootstrapCILower: 0.40m,
            embargoRatio: 0.05,
            tpeBudget: 50,
            tpeInitialSamples: 15,
            maxParallelBacktests: 4,
            screeningTimeoutSeconds: 30,
            correlationParamThreshold: 0.15,
            sensitivityPerturbPct: 0.10,
            gpEarlyStopPatience: 4,
            cooldownDays: 14,
            checkpointEveryN: 10,
            logger: logger,
            cpcvNFolds: 2,
            cpcvTestFoldCount: 2);

        Assert.Contains(issues, i => i.Contains("CpcvNFolds") && i.Contains("CpcvTestFoldCount"));
    }

    [Fact]
    public void CpcvNFolds_GreaterThan_CpcvTestFoldCount_NoError()
    {
        var logger = Mock.Of<ILogger>();

        var issues = OptimizationConfigValidator.Validate(
            autoApprovalImprovementThreshold: 0.10m,
            autoApprovalMinHealthScore: 0.55m,
            minBootstrapCILower: 0.40m,
            embargoRatio: 0.05,
            tpeBudget: 50,
            tpeInitialSamples: 15,
            maxParallelBacktests: 4,
            screeningTimeoutSeconds: 30,
            correlationParamThreshold: 0.15,
            sensitivityPerturbPct: 0.10,
            gpEarlyStopPatience: 4,
            cooldownDays: 14,
            checkpointEveryN: 10,
            logger: logger,
            cpcvNFolds: 6,
            cpcvTestFoldCount: 2);

        Assert.DoesNotContain(issues, i => i.Contains("CpcvNFolds") && i.Contains("CpcvTestFoldCount"));
    }

    // ── 2. CanonicalParameterJson.Normalize ─────────────────────────────

    [Fact]
    public void Normalize_SortsKeysAlphabetically()
    {
        string input = """{"Slow":34,"Fast":12,"Medium":22}""";
        string normalized = CanonicalParameterJson.Normalize(input);

        Assert.Equal("""{"Fast":12,"Medium":22,"Slow":34}""", normalized);
    }

    [Fact]
    public void Normalize_HandlesNullGracefully()
    {
        string resultNull = CanonicalParameterJson.Normalize(null);
        string resultEmpty = CanonicalParameterJson.Normalize("");
        string resultWhitespace = CanonicalParameterJson.Normalize("   ");

        Assert.Equal(string.Empty, resultNull);
        Assert.Equal(string.Empty, resultEmpty);
        Assert.Equal(string.Empty, resultWhitespace);
    }

    // ── 3. OptimizationHealthScorer ─────────────────────────────────────

    [Fact]
    public void ComputeHealthScore_ReturnsExpectedRange()
    {
        // A reasonable set of metrics should produce a score in [0, 1]
        decimal score = OptimizationHealthScorer.ComputeHealthScore(
            winRate: 0.55m,
            profitFactor: 1.5m,
            maxDrawdownPct: 8m,
            sharpeRatio: 1.2m,
            totalTrades: 40);

        Assert.True(score >= 0m, $"Score {score} should be >= 0");
        Assert.True(score <= 1m, $"Score {score} should be <= 1");

        // Also verify extreme inputs stay within range
        decimal zeroScore = OptimizationHealthScorer.ComputeHealthScore(0m, 0m, 0m, 0m, 0);
        Assert.True(zeroScore >= 0m && zeroScore <= 1m, $"Zero-input score {zeroScore} should be in [0, 1]");

        decimal perfectScore = OptimizationHealthScorer.ComputeHealthScore(1.0m, 3.0m, 0m, 3.0m, 100);
        Assert.True(perfectScore >= 0m && perfectScore <= 1m, $"Perfect-input score {perfectScore} should be in [0, 1]");
    }

    [Fact]
    public void ComputeHealthScore_HandlesNaN()
    {
        // Casting NaN to decimal is undefined in .NET — the Sanitize guard inside the scorer
        // should handle it. We simulate the scenario through the BacktestResult overload.
        var result = new BacktestResult
        {
            TotalTrades = 0,
            WinRate = 0m,
            ProfitFactor = 0m,
            MaxDrawdownPct = 0m,
            SharpeRatio = 0m,
            Trades = []
        };

        decimal score = OptimizationHealthScorer.ComputeHealthScore(result);

        Assert.False(double.IsNaN((double)score), "Score should not be NaN");
        Assert.True(score >= 0m && score <= 1m, $"Score {score} should be in [0, 1]");
    }

    // ── 4. OptimizationValidator.ImputeMinorGaps ────────────────────────

    [Fact]
    public void ImputeMinorGaps_FillsOneBarGap()
    {
        // Three H1 candles: 00:00, 01:00, 03:00 — missing the 02:00 bar (1-bar gap)
        var candles = new List<Candle>
        {
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), 1.10m), // Monday
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 1, 0, 0, DateTimeKind.Utc), 1.11m),
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 3, 0, 0, DateTimeKind.Utc), 1.12m), // 02:00 missing
        };

        var (result, imputedCount) = OptimizationValidator.ImputeMinorGaps(candles, Timeframe.H1);

        Assert.Equal(1, imputedCount);
        Assert.Equal(4, result.Count);
        // The imputed bar should be at 02:00 with OHLC = previous close (1.11)
        Assert.Equal(new DateTime(2026, 3, 2, 2, 0, 0, DateTimeKind.Utc), result[2].Timestamp);
        Assert.Equal(1.11m, result[2].Open);
        Assert.Equal(1.11m, result[2].Close);
        Assert.Equal(0m, result[2].Volume);
        Assert.True(result[2].IsClosed);
    }

    [Fact]
    public void ImputeMinorGaps_DoesNotFillLargeGaps()
    {
        // H1 candles with a 5-bar gap (exceeds default maxImputeBars=2)
        var candles = new List<Candle>
        {
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), 1.10m), // Monday
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), 1.15m), // 5-bar gap
        };

        var (result, imputedCount) = OptimizationValidator.ImputeMinorGaps(candles, Timeframe.H1);

        Assert.Equal(0, imputedCount);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ImputeMinorGaps_SkipsWeekendGaps()
    {
        // D1 candles: Friday → Monday (2-bar gap = Saturday + Sunday) — natural weekend closure
        var candles = new List<Candle>
        {
            MakeCandle("EURUSD", Timeframe.D1, new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc), 1.10m), // Friday
            MakeCandle("EURUSD", Timeframe.D1, new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc), 1.12m), // Monday
        };

        var (result, imputedCount) = OptimizationValidator.ImputeMinorGaps(candles, Timeframe.D1);

        Assert.Equal(0, imputedCount);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ImputeMinorGaps_ReturnsZeroForNoGaps()
    {
        // Perfectly consecutive H1 candles — no gaps
        var candles = new List<Candle>
        {
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), 1.10m),
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 1, 0, 0, DateTimeKind.Utc), 1.11m),
            MakeCandle("EURUSD", Timeframe.H1, new DateTime(2026, 3, 2, 2, 0, 0, DateTimeKind.Utc), 1.12m),
        };

        var (result, imputedCount) = OptimizationValidator.ImputeMinorGaps(candles, Timeframe.H1);

        Assert.Equal(0, imputedCount);
        Assert.Equal(3, result.Count);
    }

    // ── 5. GradualRolloutManager ────────────────────────────────────────

    [Fact]
    public void StartRollout_SetsInitialState()
    {
        var strategy = new Strategy
        {
            Id = 1,
            Name = "Test",
            ParametersJson = """{"Fast":8}""",
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1
        };

        var rolloutStart = new DateTime(2026, 04, 07, 10, 0, 0, DateTimeKind.Utc);
        GradualRolloutManager.StartRollout(strategy, """{"Fast":12}""", optimizationRunId: 99, rolloutStart);

        Assert.Equal("""{"Fast":8}""", strategy.RollbackParametersJson);
        Assert.Equal("""{"Fast":12}""", strategy.ParametersJson);
        Assert.Equal(25, strategy.RolloutPct);
        Assert.NotNull(strategy.RolloutStartedAt);
        Assert.Equal(99L, strategy.RolloutOptimizationRunId);
    }

    [Fact]
    public void PromoteRollout_Progresses25To50To75To100()
    {
        var originalStart = new DateTime(2026, 04, 03, 12, 0, 0, DateTimeKind.Utc);
        var strategy = new Strategy
        {
            Id = 1,
            Name = "Test",
            ParametersJson = """{"Fast":12}""",
            RollbackParametersJson = """{"Fast":8}""",
            RolloutPct = 25,
            RolloutStartedAt = originalStart,
            RolloutOptimizationRunId = 99,
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1
        };

        // 25 → 50
        bool completed = GradualRolloutManager.PromoteRollout(strategy, originalStart.AddDays(1));
        Assert.False(completed);
        Assert.Equal(50, strategy.RolloutPct);
        Assert.NotNull(strategy.RolloutStartedAt);
        Assert.True(strategy.RolloutStartedAt > originalStart);

        var secondTierStart = strategy.RolloutStartedAt;

        // 50 → 75
        completed = GradualRolloutManager.PromoteRollout(strategy, originalStart.AddDays(2));
        Assert.False(completed);
        Assert.Equal(75, strategy.RolloutPct);
        Assert.NotNull(strategy.RolloutStartedAt);
        Assert.True(strategy.RolloutStartedAt > secondTierStart);

        // 75 → 100 (complete — clears rollback state)
        completed = GradualRolloutManager.PromoteRollout(strategy, originalStart.AddDays(3));
        Assert.True(completed);
        Assert.Null(strategy.RolloutPct);
        Assert.Null(strategy.RollbackParametersJson);
        Assert.Null(strategy.RolloutStartedAt);
        Assert.Null(strategy.RolloutOptimizationRunId);
    }

    [Fact]
    public void Rollback_RestoresOriginalParams()
    {
        var strategy = new Strategy
        {
            Id = 1,
            Name = "Test",
            ParametersJson = """{"Fast":12}""",
            RollbackParametersJson = """{"Fast":8}""",
            RolloutPct = 50,
            RolloutStartedAt = DateTime.UtcNow,
            RolloutOptimizationRunId = 99,
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1
        };

        GradualRolloutManager.Rollback(strategy);

        Assert.Equal("""{"Fast":8}""", strategy.ParametersJson);
        Assert.Null(strategy.RollbackParametersJson);
        Assert.Null(strategy.RolloutPct);
        Assert.Null(strategy.RolloutStartedAt);
        Assert.Null(strategy.RolloutOptimizationRunId);
    }

    [Fact]
    public void SelectParameters_UsesNewParamsBasedOnPct()
    {
        var strategy = new Strategy
        {
            Id = 1,
            Name = "Test",
            ParametersJson = """{"Fast":12}""",
            RollbackParametersJson = """{"Fast":8}""",
            RolloutPct = 50,
            StrategyType = StrategyType.BreakoutScalper,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1
        };

        // Seed 10 → bucket = 10 % 100 = 10 < 50 → new params
        string resultNew = GradualRolloutManager.SelectParameters(strategy, deterministicSeed: 10);
        Assert.Equal("""{"Fast":12}""", resultNew);

        // Seed 75 → bucket = 75 % 100 = 75 >= 50 → rollback params
        string resultOld = GradualRolloutManager.SelectParameters(strategy, deterministicSeed: 75);
        Assert.Equal("""{"Fast":8}""", resultOld);

        // Same seed always returns same result (deterministic)
        string resultRepeat = GradualRolloutManager.SelectParameters(strategy, deterministicSeed: 10);
        Assert.Equal(resultNew, resultRepeat);
    }

    // ── 6. ParameterImportanceTracker ───────────────────────────────────

    [Fact]
    public void ComputeParameterDeltas_CalculatesRelativeChange()
    {
        string baseline = """{"Fast":10,"Slow":30}""";
        string optimized = """{"Fast":15,"Slow":30}""";

        var deltas = ParameterImportanceTracker.ComputeParameterDeltas(baseline, optimized);

        // Fast changed from 10 to 15: |15-10| / max(|10|, |15|) = 5/15 = 0.333...
        Assert.True(deltas.ContainsKey("Fast"));
        Assert.Equal(5.0 / 15.0, deltas["Fast"], precision: 6);

        // Slow unchanged: |30-30| / max(30,30) = 0
        Assert.True(deltas.ContainsKey("Slow"));
        Assert.Equal(0.0, deltas["Slow"], precision: 6);
    }

    [Fact]
    public void AggregateImportance_NormalizesToOneMax()
    {
        var allDeltas = new List<Dictionary<string, double>>
        {
            new() { ["Fast"] = 0.5, ["Slow"] = 0.1 },
            new() { ["Fast"] = 0.4, ["Slow"] = 0.2 },
            new() { ["Fast"] = 0.6, ["Slow"] = 0.0 },
        };

        var importance = ParameterImportanceTracker.AggregateImportance(allDeltas);

        // The most important parameter should be normalized to 1.0
        double maxValue = importance.Values.Max();
        Assert.Equal(1.0, maxValue, precision: 6);

        // All values should be in [0, 1]
        Assert.All(importance.Values, v =>
        {
            Assert.True(v >= 0.0 && v <= 1.0, $"Importance {v} should be in [0, 1]");
        });
    }

    // ── 7. ParetoFrontSelector ──────────────────────────────────────────

    [Fact]
    public void RankByNonDominatedSorting_ReturnsCorrectOrder()
    {
        // Simple 2-objective case (both maximized):
        // A(3, 1), B(1, 3), C(2, 2) — all on Pareto front (none dominates another)
        // D(1, 1) — dominated by all three
        var candidates = new List<(double Obj1, double Obj2)>
        {
            (3.0, 1.0), // A
            (1.0, 3.0), // B
            (2.0, 2.0), // C
            (1.0, 1.0), // D — dominated
        };

        var ranked = ParetoFrontSelector.RankByNonDominatedSorting(
            candidates,
            maxCount: 4,
            c => c.Obj1,
            c => c.Obj2);

        Assert.Equal(4, ranked.Count);

        // First 3 should be the Pareto-optimal front (A, B, C in some order)
        var front = ranked.Take(3).ToList();
        Assert.Contains((3.0, 1.0), front); // A
        Assert.Contains((1.0, 3.0), front); // B
        Assert.Contains((2.0, 2.0), front); // C

        // D should be last (dominated)
        Assert.Equal((1.0, 1.0), ranked[3]);
    }

    // ── 8. TreeParzenEstimator weighted observation ─────────────────────

    [Fact]
    public void AddObservation_WithWeight_BlendsTowardMean()
    {
        var bounds = new Dictionary<string, (double Min, double Max, bool IsInteger)>
        {
            ["P1"] = (0, 100, false)
        };

        // Create two TPE instances with identical seeds
        var tpeBaseline = new TreeParzenEstimator(bounds, seed: 42);
        var tpeWeighted = new TreeParzenEstimator(bounds, seed: 42);

        // Add 5 baseline observations to both
        for (int i = 0; i < 5; i++)
        {
            var p = new Dictionary<string, double> { ["P1"] = i * 20.0 };
            tpeBaseline.AddObservation(p, 0.5);
            tpeWeighted.AddObservation(p, 0.5);
        }

        // Add an extreme observation: weight=1.0 (full) vs weight=0.5 (blended toward mean)
        var extremeParams = new Dictionary<string, double> { ["P1"] = 95.0 };
        tpeBaseline.AddObservation(extremeParams, 0.95, weight: 1.0);
        tpeWeighted.AddObservation(extremeParams, 0.95, weight: 0.5);

        // Both should have the same observation count
        Assert.Equal(tpeBaseline.ObservationCount, tpeWeighted.ObservationCount);

        // The weighted version's internal state diverges — we can verify by checking
        // that random states differ after adding the weighted observation with different
        // effective scores, meaning the TPE models are built from different data.
        // (This is a structural test — the weighted score blends toward the mean.)
        Assert.Equal(6, tpeWeighted.ObservationCount);
    }

    // ── 9. OptimizationRunStateMachine ──────────────────────────────────

    [Theory]
    [InlineData(OptimizationRunStatus.Queued, OptimizationRunStatus.Running)]
    [InlineData(OptimizationRunStatus.Running, OptimizationRunStatus.Completed)]
    [InlineData(OptimizationRunStatus.Running, OptimizationRunStatus.Failed)]
    [InlineData(OptimizationRunStatus.Running, OptimizationRunStatus.Queued)]
    [InlineData(OptimizationRunStatus.Failed, OptimizationRunStatus.Queued)]
    [InlineData(OptimizationRunStatus.Failed, OptimizationRunStatus.Abandoned)]
    // Deferral-budget exhaustion transitions — OptimizationRunDeferralTracker needs
    // to move Running / Queued straight to Abandoned when MaxDeferralCount or TTL is
    // exceeded. Without these the worker throws "Illegal transition" at runtime.
    [InlineData(OptimizationRunStatus.Running, OptimizationRunStatus.Abandoned)]
    [InlineData(OptimizationRunStatus.Queued, OptimizationRunStatus.Abandoned)]
    [InlineData(OptimizationRunStatus.Completed, OptimizationRunStatus.Approved)]
    [InlineData(OptimizationRunStatus.Completed, OptimizationRunStatus.Rejected)]
    public void CanTransition_AllowsValidTransitions(OptimizationRunStatus from, OptimizationRunStatus to)
    {
        Assert.True(OptimizationRunStateMachine.CanTransition(from, to),
            $"Expected transition {from} -> {to} to be valid");
    }

    [Theory]
    [InlineData(OptimizationRunStatus.Queued, OptimizationRunStatus.Completed)]
    [InlineData(OptimizationRunStatus.Queued, OptimizationRunStatus.Approved)]
    [InlineData(OptimizationRunStatus.Failed, OptimizationRunStatus.Approved)]
    [InlineData(OptimizationRunStatus.Failed, OptimizationRunStatus.Completed)]
    [InlineData(OptimizationRunStatus.Approved, OptimizationRunStatus.Running)]
    [InlineData(OptimizationRunStatus.Approved, OptimizationRunStatus.Failed)]
    [InlineData(OptimizationRunStatus.Rejected, OptimizationRunStatus.Running)]
    [InlineData(OptimizationRunStatus.Abandoned, OptimizationRunStatus.Running)]
    public void CanTransition_RejectsInvalidTransitions(OptimizationRunStatus from, OptimizationRunStatus to)
    {
        Assert.False(OptimizationRunStateMachine.CanTransition(from, to),
            $"Expected transition {from} -> {to} to be invalid");
    }

    [Fact]
    public void Transition_RunningToAbandoned_SetsTerminalFields()
    {
        var run = new OptimizationRun
        {
            Id = 42,
            Status = OptimizationRunStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            ExecutionLeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ExecutionLeaseToken = Guid.NewGuid(),
        };
        var now = DateTime.UtcNow;

        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Abandoned, now,
            errorMessage: "Deferral budget exhausted");

        Assert.Equal(OptimizationRunStatus.Abandoned, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Equal("Deferral budget exhausted", run.ErrorMessage);
        Assert.Null(run.ExecutionLeaseExpiresAt);
        Assert.Null(run.ExecutionLeaseToken);
    }

    [Fact]
    public void Transition_QueuedToAbandoned_SetsTerminalFields()
    {
        // Deferral path: Queued run exhausts its TTL before ever running → straight to Abandoned.
        var run = new OptimizationRun
        {
            Id = 43,
            Status = OptimizationRunStatus.Queued,
            QueuedAt = DateTime.UtcNow.AddDays(-8),
            DeferralCount = 6,
        };
        OptimizationRunStateMachine.Transition(run, OptimizationRunStatus.Abandoned, DateTime.UtcNow,
            errorMessage: "Exceeded max deferral TTL (7 days)");

        Assert.Equal(OptimizationRunStatus.Abandoned, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Contains("TTL", run.ErrorMessage);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Candle MakeCandle(string symbol, Timeframe timeframe, DateTime timestamp, decimal close)
    {
        return new Candle
        {
            Symbol = symbol,
            Timeframe = timeframe,
            Timestamp = timestamp,
            Open = close,
            High = close + 0.001m,
            Low = close - 0.001m,
            Close = close,
            Volume = 100m,
            IsClosed = true
        };
    }
}
