using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Computes optimal child order slice count based on order size relative to
/// average daily volume, current spread conditions, and time of day.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class AdaptiveSliceCalculator
{
    /// <summary>Target participation rate (fraction of ADV per slice interval).</summary>
    private const decimal TargetParticipationRate = 0.02m; // 2% of ADV per slice

    /// <summary>Trading hours per day (used to normalize ADV to per-slice rate).</summary>
    private const decimal TradingHoursPerDay = 20m; // FX ~20h/day

    /// <summary>
    /// Computes optimal number of slices for a TWAP/VWAP order.
    /// More slices for: larger orders, thinner markets, wider spreads.
    /// Fewer slices for: smaller orders, liquid markets, tight spreads.
    /// </summary>
    /// <param name="orderQuantity">Total order quantity in lots.</param>
    /// <param name="averageDailyVolume">Average daily volume for the symbol in lots.</param>
    /// <param name="currentSpread">Current bid-ask spread in pips.</param>
    /// <param name="averageSpread">Average spread for the symbol in pips.</param>
    /// <param name="durationSeconds">Total execution window in seconds.</param>
    /// <returns>Recommended number of slices (minimum 2, maximum 50).</returns>
    public int ComputeSliceCount(
        decimal orderQuantity,
        decimal averageDailyVolume,
        decimal currentSpread,
        decimal averageSpread,
        int durationSeconds)
    {
        if (orderQuantity <= 0 || durationSeconds <= 0)
            return 2; // Minimum slices

        // Base: how many slices to stay below target participation rate
        decimal durationHours = durationSeconds / 3600m;
        decimal advPerHour = averageDailyVolume > 0
            ? averageDailyVolume / TradingHoursPerDay
            : orderQuantity; // Fallback: assume order = 1 hour's volume

        decimal targetQtyPerSlice = advPerHour * durationHours * TargetParticipationRate;
        int baseSlices = targetQtyPerSlice > 0
            ? (int)Math.Ceiling(orderQuantity / targetQtyPerSlice)
            : 5;

        // Spread adjustment: wider spread -> more slices (reduce per-slice impact)
        decimal spreadRatio = averageSpread > 0 ? currentSpread / averageSpread : 1.0m;
        if (spreadRatio > 1.5m)
            baseSlices = (int)(baseSlices * spreadRatio);

        return Math.Clamp(baseSlices, 2, 50);
    }
}
