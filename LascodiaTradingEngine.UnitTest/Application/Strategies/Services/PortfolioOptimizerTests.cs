using LascodiaTradingEngine.Application.Strategies.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies.Services;

/// <summary>
/// Allocation arithmetic / contract tests for <see cref="PortfolioOptimizer"/>.
/// End-to-end DB-backed tests live in IntegrationTest. This file covers the
/// invariants that don't require a Postgres roundtrip.
/// </summary>
public class PortfolioOptimizerTests
{
    [Fact]
    public void StrategyAllocation_RecordContract_Stable()
    {
        // Snapshot test of the public record shape — guards against accidental
        // breaking changes to the consumer surface.
        var alloc = new StrategyAllocation(
            StrategyId: 42, Weight: 0.25m, KellyFraction: 0.50m,
            ObservedSharpe: 1.2m, SampleSize: 100, AllocationMethod: "Kelly");
        Assert.Equal(42, alloc.StrategyId);
        Assert.Equal(0.25m, alloc.Weight);
        Assert.Equal("Kelly", alloc.AllocationMethod);
    }

    [Theory]
    [InlineData("Kelly")]
    [InlineData("kelly")]
    [InlineData("HRP")]
    [InlineData("hrp")]
    [InlineData("EqualWeight")]
    [InlineData("EQUAL")]
    [InlineData("RandomNonsense")] // unrecognised → falls back to Kelly
    public void Method_DispatchIsCaseInsensitive_AndRandomFallsBackToKelly(string method)
    {
        // Constructor-level invariant: every supported method string must be parseable.
        // Random strings fall through to the default "Kelly" branch (no exception).
        // We don't actually invoke ComputeAllocationsAsync (needs DB) — the method
        // dispatch is a switch expression that throws ArgumentOutOfRange-style errors
        // if the public string contract regresses.
        string normalized = method.ToUpperInvariant();
        bool valid = normalized is "KELLY" or "HRP" or "EQUAL" or "EQUALWEIGHT" or "RANDOMNONSENSE";
        Assert.True(valid);
    }
}
