using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Time-Weighted Average Price execution: splits a parent order into equal-sized child
/// orders spaced evenly over a specified time window to minimize market impact.
/// Adds +/-10% jitter to interval timing to avoid predictable execution patterns.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class TwapExecutionAlgorithm : IExecutionAlgorithm
{
    public ExecutionAlgorithmType AlgorithmType => ExecutionAlgorithmType.TWAP;

    public IReadOnlyList<ChildOrderSlice> GenerateSlices(
        Order parentOrder,
        int sliceCount,
        int durationSeconds,
        decimal currentPrice,
        decimal lotStep = 0.01m)
    {
        if (sliceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCount), "Slice count must be positive");
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive");
        if (lotStep <= 0)
            lotStep = 0.01m; // Fallback to 0.01 if invalid (default for most forex brokers)

        var totalQuantity = parentOrder.Quantity;
        // Round down to broker lot step (from CurrencyPair.VolumeStep, default 0.01)
        var baseSliceQty  = Math.Floor(totalQuantity / sliceCount / lotStep) * lotStep;
        decimal allocated = baseSliceQty * sliceCount;
        var remainder     = totalQuantity - allocated;
        var intervalSeconds = (double)durationSeconds / sliceCount;
        var startTime     = DateTime.UtcNow;

        var slices = new List<ChildOrderSlice>(sliceCount);
        double cumulativeMs = 0;

        for (int i = 0; i < sliceCount; i++)
        {
            // Add any remainder to the last slice to guarantee exact total
            var qty = i == sliceCount - 1
                ? baseSliceQty + remainder
                : baseSliceQty;

            // Skip zero-quantity slices
            if (qty <= 0) continue;

            // Add +/-10% randomized jitter to interval timing to avoid predictable execution patterns
            double jitterFactor = 0.9 + Random.Shared.NextDouble() * 0.2; // [0.9, 1.1]
            double jitteredIntervalMs = intervalSeconds * 1000.0 * jitterFactor;

            slices.Add(new ChildOrderSlice(
                SliceIndex: i,
                Quantity: qty,
                LimitPrice: null, // TWAP uses market orders
                ScheduledAt: startTime.AddMilliseconds(i == 0 ? 0 : cumulativeMs)));

            cumulativeMs += jitteredIntervalMs;
        }

        // Post-check: verify total quantity equals parent quantity
        Debug.Assert(
            slices.Sum(s => s.Quantity) == totalQuantity,
            $"TWAP slice total {slices.Sum(s => s.Quantity)} != parent quantity {totalQuantity}");

        return slices;
    }
}
