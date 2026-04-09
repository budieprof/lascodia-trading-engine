using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Backtesting;

internal static class ValidationRunFailureCodes
{
    internal const ValidationFailureCode StrategyNotFound = ValidationFailureCode.StrategyNotFound;
    internal const ValidationFailureCode StrategyDeleted = ValidationFailureCode.StrategyDeleted;
    internal const ValidationFailureCode NoClosedCandles = ValidationFailureCode.NoClosedCandles;
    internal const ValidationFailureCode InvalidCandleSeries = ValidationFailureCode.InvalidCandleSeries;
    internal const ValidationFailureCode InvalidWindow = ValidationFailureCode.InvalidWindow;
    internal const ValidationFailureCode InvalidOptionsSnapshot = ValidationFailureCode.InvalidOptionsSnapshot;
    internal const ValidationFailureCode ExecutionFailed = ValidationFailureCode.ExecutionFailed;
    internal const ValidationFailureCode TransientInfrastructure = ValidationFailureCode.TransientInfrastructure;
}
