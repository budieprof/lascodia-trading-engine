using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Execution algorithm for order slicing (TWAP/VWAP). Produces a sequence of child orders
/// from a parent order based on the configured algorithm parameters.
/// </summary>
/// <summary>
/// A single child order slice produced by an execution algorithm, scheduled for a specific time.
/// </summary>
public record ChildOrderSlice(
    int SliceIndex,
    decimal Quantity,
    decimal? LimitPrice,
    DateTime ScheduledAt);

public interface IExecutionAlgorithm
{
    /// <summary>The algorithm type this implementation handles (e.g. TWAP, VWAP).</summary>
    ExecutionAlgorithmType AlgorithmType { get; }

    /// <summary>
    /// Splits a parent order into a sequence of child slices scheduled over the given duration.
    /// </summary>
    IReadOnlyList<ChildOrderSlice> GenerateSlices(
        Order parentOrder,
        int sliceCount,
        int durationSeconds,
        decimal currentPrice);
}
