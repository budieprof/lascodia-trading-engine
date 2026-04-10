namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Structured failure codes for validation runs. Stored alongside runs for retry and
/// recovery policy decisions.
/// </summary>
public enum ValidationFailureCode
{
    StrategyNotFound = 0,
    StrategyDeleted = 1,
    NoClosedCandles = 2,
    InvalidCandleSeries = 3,
    InvalidWindow = 4,
    InvalidOptionsSnapshot = 5,
    InvalidStrategySnapshot = 6,
    ExecutionFailed = 7,
    TransientInfrastructure = 8
}
