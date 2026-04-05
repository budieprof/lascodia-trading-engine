using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Applies stress test scenarios to the current portfolio, computing P&amp;L impact,
/// margin call risk, and per-position attribution.
/// </summary>
public interface IStressTestEngine
{
    /// <summary>
    /// Applies the given stress scenario to the account's open positions and computes
    /// estimated P&amp;L impact, margin call risk, and per-position attribution.
    /// </summary>
    Task<StressTestResult> RunScenarioAsync(
        StressTestScenario scenario,
        TradingAccount account,
        IReadOnlyList<Position> openPositions,
        CancellationToken cancellationToken);
}
