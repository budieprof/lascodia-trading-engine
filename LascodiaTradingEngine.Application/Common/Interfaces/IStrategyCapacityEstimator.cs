using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Estimates strategy capital capacity — the maximum lot size before expected alpha is
/// eroded by market impact, calibrated from execution quality data.
/// </summary>
public interface IStrategyCapacityEstimator
{
    Task<StrategyCapacity> EstimateAsync(
        Strategy strategy,
        CancellationToken cancellationToken);
}
