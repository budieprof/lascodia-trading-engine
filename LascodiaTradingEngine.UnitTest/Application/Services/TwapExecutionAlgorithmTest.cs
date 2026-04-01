using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class TwapExecutionAlgorithmTest
{
    private readonly TwapExecutionAlgorithm _algo = new();

    [Fact]
    public void GenerateSlices_5Slices_PreservesTotalQuantity()
    {
        var order = EntityFactory.CreateOrder(quantity: 1.0m);

        var slices = _algo.GenerateSlices(order, sliceCount: 5, durationSeconds: 300, currentPrice: 1.1m);

        Assert.Equal(5, slices.Count);
        Assert.Equal(1.0m, slices.Sum(s => s.Quantity));
    }

    [Fact]
    public void GenerateSlices_RemainderGoesToLastSlice()
    {
        var order = EntityFactory.CreateOrder(quantity: 1.03m);

        var slices = _algo.GenerateSlices(order, sliceCount: 3, durationSeconds: 180, currentPrice: 1.1m);

        // 1.03 / 3 = 0.34 per slice, remainder 0.01 goes to last
        var lastSlice = slices[^1];
        Assert.True(lastSlice.Quantity >= slices[0].Quantity);
        Assert.Equal(1.03m, slices.Sum(s => s.Quantity));
    }

    [Fact]
    public void GenerateSlices_ZeroSliceCount_Throws()
    {
        var order = EntityFactory.CreateOrder();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => _algo.GenerateSlices(order, 0, 300, 1.1m));
    }

    [Fact]
    public void GenerateSlices_SlicesAreEvenlySpaced()
    {
        var order = EntityFactory.CreateOrder(quantity: 1.0m);
        var slices = _algo.GenerateSlices(order, 4, 400, 1.1m);

        for (int i = 1; i < slices.Count; i++)
        {
            var gap = (slices[i].ScheduledAt - slices[i - 1].ScheduledAt).TotalMilliseconds;
            Assert.InRange(gap, 99000, 101000); // ~100 seconds per slice
        }
    }
}
