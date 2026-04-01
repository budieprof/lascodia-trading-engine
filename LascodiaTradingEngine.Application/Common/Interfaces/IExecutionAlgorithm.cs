using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Execution algorithm for order slicing (TWAP/VWAP). Produces a sequence of child orders
/// from a parent order based on the configured algorithm parameters.
/// </summary>
public record ChildOrderSlice(
    int SliceIndex,
    decimal Quantity,
    decimal? LimitPrice,
    DateTime ScheduledAt);

public interface IExecutionAlgorithm
{
    ExecutionAlgorithmType AlgorithmType { get; }

    IReadOnlyList<ChildOrderSlice> GenerateSlices(
        Order parentOrder,
        int sliceCount,
        int durationSeconds,
        decimal currentPrice);
}
