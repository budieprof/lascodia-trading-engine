using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

internal static class ValidationRunQueueSources
{
    internal const ValidationQueueSource Legacy = ValidationQueueSource.Legacy;
    internal const ValidationQueueSource Manual = ValidationQueueSource.Manual;
    internal const ValidationQueueSource AutoRefresh = ValidationQueueSource.AutoRefresh;
    internal const ValidationQueueSource ActivationBaseline = ValidationQueueSource.ActivationBaseline;
    internal const ValidationQueueSource StrategyGenerationInitial = ValidationQueueSource.StrategyGenerationInitial;
    internal const ValidationQueueSource OptimizationFollowUp = ValidationQueueSource.OptimizationFollowUp;
    internal const ValidationQueueSource BacktestFollowUp = ValidationQueueSource.BacktestFollowUp;
}
