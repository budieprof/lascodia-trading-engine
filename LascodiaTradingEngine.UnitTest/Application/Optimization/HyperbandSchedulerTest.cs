using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class HyperbandSchedulerTest
{
    // ── Bracket computation ────────────────────────────────────────────────

    [Fact]
    public void ComputeBrackets_StandardEta3_ProducesMultipleBrackets()
    {
        // 3000 H1 candles, min 30 → minFidelity ≈ 0.01
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 81);

        Assert.True(brackets.Count >= 3, $"Expected ≥3 brackets, got {brackets.Count}");

        // Most aggressive bracket should start at lowest fidelity
        var mostAggressive = brackets[0];
        Assert.True(mostAggressive.FidelityRungs[0] < 0.15,
            $"Most aggressive bracket should start at low fidelity, got {mostAggressive.FidelityRungs[0]:P0}");

        // Most conservative bracket should start at high fidelity
        var mostConservative = brackets[^1];
        Assert.True(mostConservative.FidelityRungs[0] >= 0.90,
            $"Most conservative bracket should start near full fidelity, got {mostConservative.FidelityRungs[0]:P0}");
    }

    [Fact]
    public void ComputeBrackets_ScarceData_ProducesFewBrackets()
    {
        // 200 D1 candles, min 30 → minFidelity = 0.15
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.15, budgetPerBracket: 27);

        // With minFidelity = 0.15: sMax = floor(log3(1/0.15)) = floor(log3(6.67)) = 1
        Assert.True(brackets.Count <= 3,
            $"Scarce data should produce ≤3 brackets, got {brackets.Count}");
        Assert.True(brackets.Count >= 1,
            $"Should produce at least 1 bracket, got {brackets.Count}");
    }

    [Fact]
    public void ComputeBrackets_AllBracketsEndAtFullFidelity()
    {
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 81);

        foreach (var bracket in brackets)
        {
            double lastFidelity = bracket.FidelityRungs[^1];
            Assert.True(lastFidelity >= 0.99,
                $"Bracket {bracket.Index} ends at fidelity {lastFidelity:P0}, expected ≥1.0");
        }
    }

    [Fact]
    public void ComputeBrackets_FidelityIncreases_CandidatesDecrease()
    {
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 81);

        foreach (var bracket in brackets.Where(b => b.FidelityRungs.Length > 1))
        {
            for (int i = 1; i < bracket.FidelityRungs.Length; i++)
            {
                Assert.True(bracket.FidelityRungs[i] >= bracket.FidelityRungs[i - 1],
                    $"Bracket {bracket.Index}: fidelity should increase at each rung");
                Assert.True(bracket.CandidatesPerRung[i] <= bracket.CandidatesPerRung[i - 1],
                    $"Bracket {bracket.Index}: candidates should decrease at each rung");
            }
        }
    }

    [Fact]
    public void ComputeBrackets_Eta2_ProducesMoreBrackets()
    {
        var eta2 = HyperbandScheduler.ComputeBrackets(
            eta: 2, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 64);
        var eta3 = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 81);

        // eta=2 has more rungs per bracket (slower reduction), which means sMax is higher
        Assert.True(eta2.Count >= eta3.Count,
            $"eta=2 should produce ≥ brackets than eta=3: {eta2.Count} vs {eta3.Count}");
    }

    [Fact]
    public void ComputeBrackets_CapsAt5Brackets()
    {
        // Very high ratio → would produce many brackets without cap
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 2, maxFidelity: 1.0, minFidelity: 0.001, budgetPerBracket: 128);

        Assert.True(brackets.Count <= 6, // sMax capped at 5 → max 6 brackets (0..5)
            $"Should cap at ≤6 brackets, got {brackets.Count}");
    }

    [Fact]
    public void ComputeBrackets_InvalidMinFidelity_ReturnsSingleBracket()
    {
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 1.5, budgetPerBracket: 10);

        Assert.Single(brackets);
        Assert.Equal(1.0, brackets[0].FidelityRungs[0]);
    }

    // ── Downsampling ───────────────────────────────────────────────────────

    [Fact]
    public void DownsampleCandles_FullFidelity_ReturnsOriginalList()
    {
        var candles = CreateCandles(100);
        var result = HyperbandScheduler.DownsampleCandles(candles, 1.0);
        Assert.Same(candles, result);
    }

    [Fact]
    public void DownsampleCandles_HalfFidelity_ReturnsEveryOther()
    {
        var candles = CreateCandles(100);
        var result = HyperbandScheduler.DownsampleCandles(candles, 0.5);
        Assert.Equal(50, result.Count);
    }

    [Fact]
    public void DownsampleCandles_QuarterFidelity_ReturnsEveryFourth()
    {
        var candles = CreateCandles(100);
        var result = HyperbandScheduler.DownsampleCandles(candles, 0.25);
        Assert.Equal(25, result.Count);
    }

    // ── Min fidelity computation ───────────────────────────────────────────

    [Fact]
    public void ComputeMinFidelity_LargeDataset_ReturnsSmallFraction()
    {
        double minF = HyperbandScheduler.ComputeMinFidelity(3000, minUsableCandles: 30);
        Assert.Equal(0.01, minF);
    }

    [Fact]
    public void ComputeMinFidelity_SmallDataset_Returns1()
    {
        double minF = HyperbandScheduler.ComputeMinFidelity(25, minUsableCandles: 30);
        Assert.Equal(1.0, minF);
    }

    // ── Survivor pooling ───────────────────────────────────────────────────

    [Fact]
    public void PoolSurvivors_DeduplicatesByParams_KeepsHighestFidelity()
    {
        var survivors = new List<HyperbandScheduler.ScoredCandidateWithFidelity>
        {
            new("""{"Fast":10}""", 0.60m, new(), 0.25, 0),  // Low fidelity
            new("""{"Fast":10}""", 0.65m, new(), 1.00, 1),  // Same params, higher fidelity
            new("""{"Fast":20}""", 0.70m, new(), 1.00, 2),  // Different params
        };

        var pooled = HyperbandScheduler.PoolSurvivors(survivors, maxCount: 10);

        Assert.Equal(2, pooled.Count);
        // The Fast:10 entry should be the one evaluated at fidelity 1.0
        var fast10 = pooled.First(p => p.ParamsJson.Contains("\"Fast\":10"));
        Assert.Equal(1.00, fast10.EvaluatedAtFidelity);
    }

    [Fact]
    public void PoolSurvivors_PreservesCvFromHighestFidelityEntry()
    {
        var survivors = new List<HyperbandScheduler.ScoredCandidateWithFidelity>
        {
            new("""{"Fast":10}""", 0.60m, new(), 0.25, 0, 0.40),
            new("""{"Fast":10}""", 0.65m, new(), 1.00, 1, 0.12),
        };

        var pooled = HyperbandScheduler.PoolSurvivors(survivors, maxCount: 10);

        var fast10 = Assert.Single(pooled);
        Assert.Equal(0.12, fast10.CvCoefficientOfVariation, 6);
    }

    [Fact]
    public void PoolSurvivors_RespectsMaxCount()
    {
        var survivors = Enumerable.Range(1, 20)
            .Select(i => new HyperbandScheduler.ScoredCandidateWithFidelity(
                $"{{\"Fast\":{i}}}", i * 0.05m, new(), 1.0, 0))
            .ToList();

        var pooled = HyperbandScheduler.PoolSurvivors(survivors, maxCount: 5);
        Assert.Equal(5, pooled.Count);
        // Should keep the top 5 by health score
        Assert.True(pooled[0].HealthScore >= pooled[^1].HealthScore);
    }

    [Fact]
    public void PoolSurvivors_EmptyInput_ReturnsEmpty()
    {
        var pooled = HyperbandScheduler.PoolSurvivors([], maxCount: 5);
        Assert.Empty(pooled);
    }

    // ── Budget estimation ──────────────────────────────────────────────────

    [Fact]
    public void EstimateTotalEvaluations_SumsAllRungs()
    {
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 27);

        int estimated = HyperbandScheduler.EstimateTotalEvaluations(brackets);
        Assert.True(estimated > 0);

        // Total should be roughly budget * brackets (each bracket uses ~budgetPerBracket)
        // but with overhead from rung structure. Verify it's reasonable.
        Assert.True(estimated < 27 * brackets.Count * 3,
            $"Estimated {estimated} seems too high for {brackets.Count} brackets");
    }

    // ── Edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public void ComputeBrackets_Eta1_FallsBackToEta3()
    {
        // eta < 2 is invalid — should be corrected to 3
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 1, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 27);

        // Should behave like eta=3 (the fallback)
        Assert.True(brackets.Count >= 2);
    }

    [Fact]
    public void ComputeBrackets_CandidatesPerRung_NeverZero()
    {
        var brackets = HyperbandScheduler.ComputeBrackets(
            eta: 3, maxFidelity: 1.0, minFidelity: 0.01, budgetPerBracket: 81);

        foreach (var bracket in brackets)
        {
            foreach (int n in bracket.CandidatesPerRung)
                Assert.True(n >= 1, $"Bracket {bracket.Index} has a rung with 0 candidates");
        }
    }

    [Fact]
    public async Task ExecuteAllBracketsAsync_PropagatesExternalCancellationDuringBracketEvaluation()
    {
        var metricsServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var scheduler = new HyperbandScheduler(
            Mock.Of<ILogger>(),
            new TradingMetrics(metricsServices.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));

        using var cts = new CancellationTokenSource();
        var validator = new OptimizationValidator(
            new SelfCancellingBacktestEngine(cts),
            TimeProvider.System);
        validator.SetInitialBalance(10_000m);

        var brackets = new List<HyperbandScheduler.Bracket>
        {
            new HyperbandScheduler.Bracket(
                Index: 0,
                InitialCandidates: 3,
                FidelityRungs: [1.0],
                CandidatesPerRung: [3])
        };

        var strategy = new Strategy
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            StrategyType = StrategyType.BreakoutScalper,
            ParametersJson = """{"Fast":10}"""
        };

        List<string> CandidateSource(int requestedCount, int _) =>
            Enumerable.Range(1, requestedCount)
                .Select(i => $"{{\"Fast\":{i}}}")
                .ToList();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.ExecuteAllBracketsAsync(
                brackets,
                CandidateSource,
                CreateCandles(120),
                strategy,
                new BacktestOptions(),
                validator,
                baselineScore: 0.5m,
                maxParallel: 1,
                screeningTimeoutSeconds: 30,
                circuitBreakerThreshold: 10,
                globalBudgetRemaining: 3,
                kFolds: 3,
                embargoPerFold: 1,
                minTrades: 1,
                cts.Token));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<Candle> CreateCandles(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Candle
            {
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                Open = 1.1000m + i * 0.0001m,
                High = 1.1010m + i * 0.0001m,
                Low = 1.0990m + i * 0.0001m,
                Close = 1.1005m + i * 0.0001m,
                Volume = 100,
                IsClosed = true,
            })
            .ToList();

    private sealed class SelfCancellingBacktestEngine : IBacktestEngine
    {
        private readonly CancellationTokenSource _ownerCts;
        private int _calls;

        public SelfCancellingBacktestEngine(CancellationTokenSource ownerCts) => _ownerCts = ownerCts;

        public async Task<BacktestResult> RunAsync(
            Strategy strategy,
            IReadOnlyList<Candle> candles,
            decimal initialBalance,
            CancellationToken ct,
            BacktestOptions? options = null)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                _ownerCts.Cancel();

            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            return new BacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = initialBalance + 100m,
                TotalTrades = 10,
                WinRate = 0.60m,
                ProfitFactor = 1.40m,
                MaxDrawdownPct = 5m,
                SharpeRatio = 1.1m,
                Trades = []
            };
        }
    }
}
