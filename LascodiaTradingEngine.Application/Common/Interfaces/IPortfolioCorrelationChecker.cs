namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IPortfolioCorrelationChecker
{
    /// Returns true if adding this signal would breach correlation limits.
    Task<bool> IsCorrelationBreachedAsync(string symbol, string direction, int maxCorrelatedPositions, CancellationToken ct);
}
