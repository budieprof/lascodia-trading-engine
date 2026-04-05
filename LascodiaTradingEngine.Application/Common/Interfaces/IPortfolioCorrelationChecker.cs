namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Evaluates whether adding a new position would breach the portfolio's correlated-position limits.
/// Uses predefined correlation groups to identify related currency pairs.
/// </summary>
public interface IPortfolioCorrelationChecker
{
    /// <summary>
    /// Returns <c>true</c> if adding a position for the given symbol and direction would breach
    /// the <paramref name="maxCorrelatedPositions"/> limit across correlated pairs.
    /// </summary>
    /// <param name="symbol">Currency pair of the proposed trade.</param>
    /// <param name="direction">Trade direction ("Buy" or "Sell").</param>
    /// <param name="maxCorrelatedPositions">Maximum allowed positions in the same correlation group.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsCorrelationBreachedAsync(string symbol, string direction, int maxCorrelatedPositions, CancellationToken ct);
}
