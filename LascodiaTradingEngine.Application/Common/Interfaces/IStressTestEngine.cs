using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Applies stress test scenarios to the current portfolio, computing P&amp;L impact,
/// margin call risk, and per-position attribution.
/// </summary>
public interface IStressTestEngine
{
    Task<StressTestResult> RunScenarioAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);
}
