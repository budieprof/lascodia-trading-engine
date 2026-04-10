using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationFailureClassifier
{
    internal static OptimizationFailureCategory Classify(Exception ex) => ex switch
    {
        OptimizationFailureException failure => failure.FailureCategory,
        DataQualityException => OptimizationFailureCategory.DataQuality,
        TimeoutException => OptimizationFailureCategory.Timeout,
        OperationCanceledException => OptimizationFailureCategory.Timeout,
        OutOfMemoryException => OptimizationFailureCategory.Transient,
        InvalidOperationException ioe when ContainsDataKeyword(ioe.Message) => OptimizationFailureCategory.DataQuality,
        _ => OptimizationFailureCategory.Transient,
    };

    private static bool ContainsDataKeyword(string? message)
        => message is not null
        && (message.Contains("data", StringComparison.OrdinalIgnoreCase)
         || message.Contains("candle", StringComparison.OrdinalIgnoreCase));
}
