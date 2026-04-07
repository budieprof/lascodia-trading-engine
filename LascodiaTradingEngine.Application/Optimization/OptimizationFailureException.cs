using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal abstract class OptimizationFailureException : InvalidOperationException
{
    protected OptimizationFailureException(
        string message,
        OptimizationFailureCategory failureCategory,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCategory = failureCategory;
    }

    internal OptimizationFailureCategory FailureCategory { get; }
}

internal sealed class OptimizationStrategyRemovedException : OptimizationFailureException
{
    internal OptimizationStrategyRemovedException(long strategyId)
        : base(
            $"Strategy {strategyId} not found.",
            OptimizationFailureCategory.StrategyRemoved)
    {
    }
}

internal sealed class OptimizationConfigSnapshotException : OptimizationFailureException
{
    internal OptimizationConfigSnapshotException(long runId)
        : base(
            $"Stored optimization config snapshot is malformed or unsupported for run {runId}.",
            OptimizationFailureCategory.ConfigError)
    {
    }
}

internal sealed class OptimizationSearchExhaustedException : OptimizationFailureException
{
    internal OptimizationSearchExhaustedException()
        : base(
            "All parameter candidates failed during TPE search.",
            OptimizationFailureCategory.SearchExhausted)
    {
    }
}
