using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Optimization;

namespace LascodiaTradingEngine.UnitTest.Application.Optimization;

public class EhviAcquisitionTest
{
    // ── Hypervolume3D Tests ─────────────────────────────────────────────────

    [Fact]
    public void Hypervolume3D_EmptyFront_ReturnsZero()
    {
        double hv = EhviAcquisition.Hypervolume3D([]);
        Assert.Equal(0.0, hv, 3);
    }

    [Fact]
    public void Hypervolume3D_SinglePoint_ReturnsProduct()
    {
        // (0.5, 0.4, 0.3) → 0.5 × 0.4 × 0.3 = 0.06
        var front = new List<double[]> { new[] { 0.5, 0.4, 0.3 } };
        double hv = EhviAcquisition.Hypervolume3D(front);
        Assert.Equal(0.06, hv, 3);
    }

    [Fact]
    public void Hypervolume3D_TwoNonDominatedPoints_ReturnsCorrectVolume()
    {
        // A=(1.0, 0.3, 0.3), B=(0.3, 1.0, 1.0) → verified by hand = 0.363
        var front = new List<double[]>
        {
            new[] { 1.0, 0.3, 0.3 },
            new[] { 0.3, 1.0, 1.0 }
        };
        double hv = EhviAcquisition.Hypervolume3D(front);
        Assert.Equal(0.363, hv, 3);
    }

    [Fact]
    public void Hypervolume3D_DominatedPointIgnored()
    {
        // A=(0.8, 0.8, 0.8) dominates B=(0.4, 0.4, 0.4)
        // HV should equal just A = 0.8 × 0.8 × 0.8 = 0.512
        var frontBoth = new List<double[]>
        {
            new[] { 0.8, 0.8, 0.8 },
            new[] { 0.4, 0.4, 0.4 }
        };
        var frontJustA = new List<double[]>
        {
            new[] { 0.8, 0.8, 0.8 }
        };
        double hvBoth = EhviAcquisition.Hypervolume3D(frontBoth);
        double hvJustA = EhviAcquisition.Hypervolume3D(frontJustA);
        Assert.Equal(0.512, hvJustA, 3);
        Assert.Equal(0.512, hvBoth, 3);
    }

    [Fact]
    public void Hypervolume3D_AllIdenticalPoints()
    {
        // Three copies of (0.5, 0.5, 0.5) → HV = 0.125
        var front = new List<double[]>
        {
            new[] { 0.5, 0.5, 0.5 },
            new[] { 0.5, 0.5, 0.5 },
            new[] { 0.5, 0.5, 0.5 }
        };
        double hv = EhviAcquisition.Hypervolume3D(front);
        Assert.Equal(0.125, hv, 3);
    }

    [Fact]
    public void Hypervolume3D_BoundaryPoints()
    {
        // Point at (1.0, 1.0, 1.0) → HV = 1.0
        var frontMax = new List<double[]> { new[] { 1.0, 1.0, 1.0 } };
        double hvMax = EhviAcquisition.Hypervolume3D(frontMax);
        Assert.Equal(1.0, hvMax, 3);

        // Point at (0.0, 0.0, 0.0) → HV = 0.0
        var frontMin = new List<double[]> { new[] { 0.0, 0.0, 0.0 } };
        double hvMin = EhviAcquisition.Hypervolume3D(frontMin);
        Assert.Equal(0.0, hvMin, 3);
    }

    // ── EhviAcquisition Integration Tests ───────────────────────────────────

    private static EhviAcquisition CreateDefaultEhvi(int seed = 42)
    {
        var paramNames = new[] { "p1", "p2" };
        var lower = new[] { 0.0, 0.0 };
        var upper = new[] { 10.0, 10.0 };
        var isInt = new[] { false, false };
        return new EhviAcquisition(paramNames, lower, upper, isInt, seed: seed);
    }

    private static BacktestResult MakeResult(decimal sharpe, decimal maxDD, decimal winRate)
    {
        return new BacktestResult
        {
            SharpeRatio = sharpe,
            MaxDrawdownPct = maxDD,
            WinRate = winRate,
            ProfitFactor = 1.5m,
            TotalTrades = 50,
            Trades = []
        };
    }

    [Fact]
    public void AddObservation_UpdatesParetoFront()
    {
        var ehvi = CreateDefaultEhvi();

        Assert.Equal(0, ehvi.ObservationCount);
        Assert.Equal(0, ehvi.ParetoFrontSize);

        // Add first observation
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 1.0, ["p2"] = 2.0 },
            MakeResult(sharpe: 1.5m, maxDD: 10m, winRate: 0.55m));

        Assert.Equal(1, ehvi.ObservationCount);
        Assert.True(ehvi.ParetoFrontSize >= 1);

        // Add a second non-dominated observation (different trade-off)
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 5.0, ["p2"] = 8.0 },
            MakeResult(sharpe: 0.8m, maxDD: 5m, winRate: 0.70m));

        Assert.Equal(2, ehvi.ObservationCount);
        Assert.True(ehvi.ParetoFrontSize >= 1);

        // Add a third observation
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 3.0, ["p2"] = 4.0 },
            MakeResult(sharpe: 2.0m, maxDD: 15m, winRate: 0.50m));

        Assert.Equal(3, ehvi.ObservationCount);
        Assert.True(ehvi.ParetoFrontSize >= 1);
    }

    [Fact]
    public void SuggestCandidates_FallsBackToLHS_WhenFewObservations()
    {
        var ehvi = CreateDefaultEhvi();

        // Add fewer than 10 observations
        for (int i = 0; i < 5; i++)
        {
            ehvi.AddObservation(
                new Dictionary<string, double> { ["p1"] = i * 2.0, ["p2"] = i * 1.5 },
                MakeResult(sharpe: 0.5m + i * 0.2m, maxDD: 5m + i * 2m, winRate: 0.45m + i * 0.02m));
        }

        Assert.Equal(5, ehvi.ObservationCount);

        // Should return exactly 'count' candidates without error (LHS fallback)
        var candidates = ehvi.SuggestCandidates(count: 4);
        Assert.Equal(4, candidates.Count);
    }

    [Fact]
    public void AddWarmStartObservation_BelowWeightThreshold_ExcludedFromFront()
    {
        var ehvi = CreateDefaultEhvi();

        // Add a full-weight observation first to establish a Pareto front
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 5.0, ["p2"] = 5.0 },
            MakeResult(sharpe: 1.0m, maxDD: 10m, winRate: 0.50m));

        int frontSizeAfterFirst = ehvi.ParetoFrontSize;
        Assert.Equal(1, ehvi.ObservationCount);
        Assert.Equal(1, frontSizeAfterFirst);

        // Add a warm-start observation with weight=0.1 (below threshold of 0.30)
        // This should NOT increase the Pareto front size but SHOULD increase observation count
        ehvi.AddWarmStartObservation(
            new Dictionary<string, double> { ["p1"] = 8.0, ["p2"] = 8.0 },
            sharpeRatio: 3.0m, maxDrawdownPct: 2m, winRate: 0.80m, weight: 0.1);

        Assert.Equal(2, ehvi.ObservationCount);
        Assert.Equal(frontSizeAfterFirst, ehvi.ParetoFrontSize);
    }

    [Fact]
    public void SerializeCheckpoint_RoundTrips()
    {
        var ehvi = CreateDefaultEhvi();

        // Add several observations to build a Pareto front
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 1.0, ["p2"] = 2.0 },
            MakeResult(sharpe: 1.5m, maxDD: 10m, winRate: 0.55m));
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 5.0, ["p2"] = 8.0 },
            MakeResult(sharpe: 0.8m, maxDD: 5m, winRate: 0.70m));
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 3.0, ["p2"] = 4.0 },
            MakeResult(sharpe: 2.0m, maxDD: 15m, winRate: 0.50m));

        int originalFrontSize = ehvi.ParetoFrontSize;
        Assert.True(originalFrontSize > 0);

        // Serialize
        string checkpoint = ehvi.SerializeCheckpoint();
        Assert.False(string.IsNullOrWhiteSpace(checkpoint));

        // Restore into a fresh instance
        var restored = CreateDefaultEhvi(seed: 99);
        restored.RestoreCheckpoint(checkpoint);

        Assert.Equal(originalFrontSize, restored.ParetoFrontSize);
    }

    // ── MC-EHVI Convergence Tests ───────────────────────────────────────────

    [Fact]
    public void SuggestCandidates_WithEnoughObservations_ReturnsNonEmptyList()
    {
        var ehvi = CreateDefaultEhvi();

        // Add 15 observations with varied objectives
        var rng = new Random(42);
        for (int i = 0; i < 15; i++)
        {
            ehvi.AddObservation(
                new Dictionary<string, double>
                {
                    ["p1"] = rng.NextDouble() * 10.0,
                    ["p2"] = rng.NextDouble() * 10.0
                },
                MakeResult(
                    sharpe: (decimal)(rng.NextDouble() * 3.0 - 1.0),
                    maxDD: (decimal)(rng.NextDouble() * 30.0),
                    winRate: (decimal)(0.30 + rng.NextDouble() * 0.40)));
        }

        Assert.Equal(15, ehvi.ObservationCount);

        var candidates = ehvi.SuggestCandidates(count: 4);
        Assert.Equal(4, candidates.Count);

        // Each candidate should have the expected parameter keys
        foreach (var c in candidates)
        {
            Assert.True(c.ContainsKey("p1"));
            Assert.True(c.ContainsKey("p2"));
        }
    }

    [Fact]
    public void CurrentHypervolume_IncreasesAsObservationsAdded()
    {
        var ehvi = CreateDefaultEhvi();

        // Start with a mediocre observation
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 5.0, ["p2"] = 5.0 },
            MakeResult(sharpe: 0.0m, maxDD: 30m, winRate: 0.40m));

        double hv1 = ehvi.CurrentHypervolume;

        // Add a strictly better observation
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 3.0, ["p2"] = 7.0 },
            MakeResult(sharpe: 1.5m, maxDD: 10m, winRate: 0.60m));

        double hv2 = ehvi.CurrentHypervolume;
        Assert.True(hv2 >= hv1, $"HV should be non-decreasing: hv1={hv1}, hv2={hv2}");

        // Add another excellent observation on a different trade-off axis
        ehvi.AddObservation(
            new Dictionary<string, double> { ["p1"] = 8.0, ["p2"] = 2.0 },
            MakeResult(sharpe: 3.0m, maxDD: 20m, winRate: 0.45m));

        double hv3 = ehvi.CurrentHypervolume;
        Assert.True(hv3 >= hv2, $"HV should be non-decreasing: hv2={hv2}, hv3={hv3}");
    }
}
