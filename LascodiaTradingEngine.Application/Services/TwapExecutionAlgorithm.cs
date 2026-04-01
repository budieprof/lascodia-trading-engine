using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Time-Weighted Average Price execution: splits a parent order into equal-sized child
/// orders spaced evenly over a specified time window to minimize market impact.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class TwapExecutionAlgorithm : IExecutionAlgorithm
{
    public ExecutionAlgorithmType AlgorithmType => ExecutionAlgorithmType.TWAP;

    public IReadOnlyList<ChildOrderSlice> GenerateSlices(
        Order parentOrder,
        int sliceCount,
        int durationSeconds,
        decimal currentPrice)
    {
        if (sliceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCount), "Slice count must be positive");
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive");

        var totalQuantity = parentOrder.Quantity;
        var baseSliceQty  = Math.Floor(totalQuantity / sliceCount * 100m) / 100m; // Round down to 0.01
        var remainder     = totalQuantity - baseSliceQty * sliceCount;
        var intervalMs    = (long)durationSeconds * 1000 / sliceCount;
        var startTime     = DateTime.UtcNow;

        var slices = new List<ChildOrderSlice>(sliceCount);

        for (int i = 0; i < sliceCount; i++)
        {
            // Add any remainder to the last slice
            var qty = i == sliceCount - 1
                ? baseSliceQty + remainder
                : baseSliceQty;

            // Skip zero-quantity slices
            if (qty <= 0) continue;

            slices.Add(new ChildOrderSlice(
                SliceIndex: i,
                Quantity: qty,
                LimitPrice: null, // TWAP uses market orders
                ScheduledAt: startTime.AddMilliseconds(intervalMs * i)));
        }

        return slices;
    }
}
