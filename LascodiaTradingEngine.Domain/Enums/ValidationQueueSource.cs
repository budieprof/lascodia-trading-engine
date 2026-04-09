namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Identifies the producer or purpose that queued a validation run.
/// </summary>
public enum ValidationQueueSource
{
    Legacy = 0,
    Manual = 1,
    AutoRefresh = 2,
    ActivationBaseline = 3,
    StrategyGenerationInitial = 4,
    OptimizationFollowUp = 5,
    BacktestFollowUp = 6
}
