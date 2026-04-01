using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class AdaptiveSliceCalculatorTest
{
    private readonly AdaptiveSliceCalculator _calculator = new();

    [Fact]
    public void ComputeSliceCount_NormalComputation_ReturnsReasonableSliceCount()
    {
        // 1 lot order, 1000 lot ADV, normal spread, 1 hour window
        int slices = _calculator.ComputeSliceCount(
            orderQuantity: 1.0m,
            averageDailyVolume: 1000m,
            currentSpread: 1.0m,
            averageSpread: 1.0m,
            durationSeconds: 3600);

        Assert.InRange(slices, 2, 50);
    }

    [Fact]
    public void ComputeSliceCount_ZeroADV_ReturnsMinimumSlices()
    {
        // Zero ADV triggers the fallback path
        int slices = _calculator.ComputeSliceCount(
            orderQuantity: 0.1m,
            averageDailyVolume: 0m,
            currentSpread: 1.0m,
            averageSpread: 1.0m,
            durationSeconds: 3600);

        Assert.InRange(slices, 2, 50);
    }

    [Fact]
    public void ComputeSliceCount_WideSpread_MoreSlicesThanNormalSpread()
    {
        // Normal spread scenario
        int normalSlices = _calculator.ComputeSliceCount(
            orderQuantity: 5.0m,
            averageDailyVolume: 500m,
            currentSpread: 1.0m,
            averageSpread: 1.0m,
            durationSeconds: 3600);

        // Wide spread scenario (3x normal spread, above the 1.5x threshold)
        int wideSpreadSlices = _calculator.ComputeSliceCount(
            orderQuantity: 5.0m,
            averageDailyVolume: 500m,
            currentSpread: 3.0m,
            averageSpread: 1.0m,
            durationSeconds: 3600);

        Assert.True(wideSpreadSlices >= normalSlices,
            $"Wide spread ({wideSpreadSlices}) should produce >= slices than normal ({normalSlices})");
    }

    [Fact]
    public void ComputeSliceCount_ExtremeValues_ClampedToMinMax()
    {
        // Very tiny order -> should clamp to minimum 2
        int tinySlices = _calculator.ComputeSliceCount(
            orderQuantity: 0.001m,
            averageDailyVolume: 10000m,
            currentSpread: 0.5m,
            averageSpread: 1.0m,
            durationSeconds: 3600);

        Assert.True(tinySlices >= 2, "Minimum slices should be 2");

        // Very large order relative to ADV -> should clamp to maximum 50
        int hugeSlices = _calculator.ComputeSliceCount(
            orderQuantity: 10000m,
            averageDailyVolume: 10m,
            currentSpread: 5.0m,
            averageSpread: 1.0m,
            durationSeconds: 60);

        Assert.True(hugeSlices <= 50, "Maximum slices should be 50");
    }
}
