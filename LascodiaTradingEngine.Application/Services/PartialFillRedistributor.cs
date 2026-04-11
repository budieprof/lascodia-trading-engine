using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Recalculates remaining child order slices when a partial fill is detected.
/// Redistributes unfilled quantity proportionally across remaining slices.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class PartialFillRedistributor
{
    private readonly ILogger<PartialFillRedistributor>? _logger;

    public PartialFillRedistributor(ILogger<PartialFillRedistributor>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Given the original slice plan, the index of the partially filled slice,
    /// and the actually filled quantity, returns an updated plan for remaining slices.
    /// </summary>
    public IReadOnlyList<ChildOrderSlice> Redistribute(
        IReadOnlyList<ChildOrderSlice> originalSlices,
        int partiallyFilledIndex,
        decimal actualFilledQuantity)
    {
        if (partiallyFilledIndex < 0 || partiallyFilledIndex >= originalSlices.Count)
        {
            _logger?.LogWarning(
                "Invalid partial fill index {Index} for {Count} slices",
                partiallyFilledIndex, originalSlices.Count);
            return originalSlices; // return unmodified
        }

        var partialSlice = originalSlices[partiallyFilledIndex];
        decimal unfilled = partialSlice.Quantity - actualFilledQuantity;

        if (unfilled <= 0)
            return originalSlices; // Fully filled, no redistribution needed

        // Get remaining slices after the partial fill
        var remaining = originalSlices
            .Where((s, i) => i > partiallyFilledIndex)
            .ToList();

        if (remaining.Count == 0)
        {
            // Last slice was partial — create a single residual slice
            return new[]
            {
                new ChildOrderSlice(
                    partiallyFilledIndex + 1,
                    unfilled,
                    partialSlice.LimitPrice,
                    DateTime.UtcNow.AddSeconds(5)) // Small delay before retry
            };
        }

        // Redistribute unfilled quantity proportionally based on each slice's original weight
        decimal totalRemaining = remaining.Sum(s => s.Quantity);
        decimal distributed = 0;

        var result = remaining.Select((s, i) =>
        {
            decimal additionalQty;
            if (i == remaining.Count - 1)
            {
                // Last slice absorbs rounding remainder to ensure exact total
                additionalQty = unfilled - distributed;
            }
            else
            {
                // Proportional share based on this slice's weight relative to remaining total
                decimal proportion = totalRemaining > 0
                    ? s.Quantity / totalRemaining
                    : 1.0m / remaining.Count;

                additionalQty = Math.Floor(unfilled * proportion * 100m) / 100m;
            }

            distributed += additionalQty;
            return new ChildOrderSlice(s.SliceIndex, s.Quantity + additionalQty, s.LimitPrice, s.ScheduledAt);
        }).ToList();

        // Reindex slices sequentially so downstream consumers see contiguous indices
        for (int i = 0; i < result.Count; i++)
            result[i] = result[i] with { SliceIndex = i };

        return result;
    }
}
