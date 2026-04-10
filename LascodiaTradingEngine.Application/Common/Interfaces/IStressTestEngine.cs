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

    /// <summary>
    /// Runs a correlation-aware reverse stress test. Instead of uniform shocks,
    /// applies correlated shocks derived from the historical correlation matrix,
    /// using the first principal component (maximum variance direction) as the
    /// shock vector. Finds the minimum shock magnitude that causes the target loss.
    /// Falls back to uncorrelated reverse stress if correlation matrix is unavailable
    /// or non-positive-definite.
    /// </summary>
    /// <param name="positions">Open positions to stress test.</param>
    /// <param name="account">The trading account (provides equity for loss target calculation).</param>
    /// <param name="targetLossPct">Target loss as percentage of equity (e.g. 25.0 = 25%).</param>
    /// <param name="recentVolatilities">Per-position annualized volatilities, aligned with positions list.</param>
    /// <param name="correlationMatrix">n x n correlation matrix, aligned with positions list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stress test result including the minimum correlated shock scale factor.</returns>
    /// <remarks>
    /// This method assumes multivariate normal returns, which underestimates tail
    /// dependence during crisis scenarios. For fat-tailed stress testing, combine
    /// with historical replay scenarios from actual crisis periods.
    /// </remarks>
    Task<StressTestResult> RunCorrelatedReverseStressAsync(
        List<Position> positions,
        TradingAccount account,
        double targetLossPct,
        double[] recentVolatilities,
        double[,] correlationMatrix,
        CancellationToken ct = default);
}
