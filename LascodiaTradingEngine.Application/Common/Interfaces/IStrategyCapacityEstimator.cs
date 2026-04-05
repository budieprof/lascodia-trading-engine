using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Estimates strategy capital capacity — the maximum lot size before expected alpha is
/// eroded by market impact, calibrated from execution quality data.
/// </summary>
public interface IStrategyCapacityEstimator
{
    /// <summary>
    /// Estimates the maximum lot size the strategy can trade before market impact erodes expected alpha.
    /// </summary>
    Task<StrategyCapacity> EstimateAsync(
        Strategy strategy,
        CancellationToken cancellationToken);
}
