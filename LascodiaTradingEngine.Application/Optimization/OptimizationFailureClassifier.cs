using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationFailureClassifier
{
    internal static OptimizationFailureCategory Classify(Exception ex) => ex switch
    {
        OptimizationFailureException failure => failure.FailureCategory,
        DataQualityException => OptimizationFailureCategory.DataQuality,
        _ => OptimizationFailureCategory.Transient,
    };
}
