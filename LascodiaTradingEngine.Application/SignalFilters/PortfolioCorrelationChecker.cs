using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Checks whether opening a new position for the given symbol+direction would
/// breach the maximum number of correlated open positions.
/// </summary>
public class PortfolioCorrelationChecker : IPortfolioCorrelationChecker
{
    // USD-base pairs: positively correlated
    // USD-quote pairs: negatively correlated with above (same group for simplicity)
    // JPY crosses: positively correlated among themselves
    private static readonly string[][] CorrelationGroups =
    [
        ["EURUSD", "GBPUSD", "AUDUSD", "NZDUSD"],
        ["USDCHF", "USDJPY", "USDCAD"],
        ["EURJPY", "GBPJPY", "AUDJPY"],
    ];

    private readonly IReadApplicationDbContext _context;

    public PortfolioCorrelationChecker(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> IsCorrelationBreachedAsync(
        string symbol,
        string direction,
        int maxCorrelatedPositions,
        CancellationToken ct)
    {
        var group = FindCorrelationGroup(symbol.ToUpperInvariant());
        if (group == null)
            return false;  // Symbol not in any known correlation group

        // Count open positions in the same correlation group
        var openCount = await _context.GetDbContext()
            .Set<Domain.Entities.Position>()
            .CountAsync(x => !x.IsDeleted
                          && x.Status == PositionStatus.Open
                          && group.Contains(x.Symbol),
                        ct);

        return openCount >= maxCorrelatedPositions;
    }

    private static string[]? FindCorrelationGroup(string symbol)
    {
        foreach (var group in CorrelationGroups)
        {
            if (group.Contains(symbol))
                return group;
        }
        return null;
    }
}
