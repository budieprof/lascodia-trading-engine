using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class PartialFillRedistributorTest
{
    private readonly PartialFillRedistributor _redistributor = new();

    private static IReadOnlyList<ChildOrderSlice> CreateSlices(params decimal[] quantities)
    {
        return quantities.Select((q, i) => new ChildOrderSlice(
            SliceIndex: i,
            Quantity: q,
            LimitPrice: 1.1000m,
            ScheduledAt: DateTime.UtcNow.AddSeconds(i * 30)
        )).ToList();
    }

    [Fact]
    public void Redistribute_FullFill_ReturnsOriginalSlices()
    {
        var slices = CreateSlices(0.5m, 0.5m, 0.5m);

        // Slice 0 fully filled (actualFilled == slice quantity)
        var result = _redistributor.Redistribute(slices, 0, 0.5m);

        // Full fill means no redistribution needed — returns original
        Assert.Equal(slices.Count, result.Count);
    }

    [Fact]
    public void Redistribute_PartialFirstSlice_RedistributesToRemaining()
    {
        var slices = CreateSlices(1.0m, 1.0m, 1.0m);

        // Slice 0 only filled 0.4 of 1.0 -> 0.6 unfilled to redistribute
        var result = _redistributor.Redistribute(slices, 0, 0.4m);

        // Should return 2 remaining slices (indices 1 and 2) with redistributed quantities
        Assert.Equal(2, result.Count);

        decimal totalRedistributed = result.Sum(s => s.Quantity);
        // Each remaining slice should have original 1.0 + proportional share of 0.6 unfilled
        Assert.Equal(1.0m + 1.0m + 0.6m, totalRedistributed);
    }

    [Fact]
    public void Redistribute_PartialLastSlice_CreatesResidualSlice()
    {
        var slices = CreateSlices(1.0m, 1.0m, 1.0m);

        // Last slice (index 2) partially filled: only 0.3 of 1.0
        var result = _redistributor.Redistribute(slices, 2, 0.3m);

        // No remaining slices after last — should create a single residual slice
        Assert.Single(result);
        Assert.Equal(0.7m, result[0].Quantity); // 1.0 - 0.3 = 0.7 unfilled
    }

    [Fact]
    public void Redistribute_InvalidIndex_ReturnsOriginal()
    {
        var slices = CreateSlices(1.0m, 1.0m, 1.0m);

        // Negative index
        var resultNeg = _redistributor.Redistribute(slices, -1, 0.5m);
        Assert.Equal(slices.Count, resultNeg.Count);

        // Index beyond range
        var resultOver = _redistributor.Redistribute(slices, 10, 0.5m);
        Assert.Equal(slices.Count, resultOver.Count);
    }
}
