namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Indicates the overall health of a trading strategy based on performance metrics.
/// </summary>
public enum StrategyHealthStatus
{
    /// <summary>Strategy is performing within expected parameters.</summary>
    Healthy = 0,

    /// <summary>Strategy shows declining performance; may trigger automatic retraining.</summary>
    Degrading = 1,

    /// <summary>Strategy performance has breached critical thresholds; intervention required.</summary>
    Critical = 2
}
