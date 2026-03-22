using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Checks whether opening a new position for the given symbol+direction would
/// breach the maximum number of correlated open positions.
/// </summary>
[RegisterService]
public class PortfolioCorrelationChecker : IPortfolioCorrelationChecker
{
    private readonly string[][] _correlationGroups;
    private readonly IReadApplicationDbContext _context;

    public PortfolioCorrelationChecker(
        IReadApplicationDbContext context,
        CorrelationGroupOptions options)
    {
        _context = context;
        _correlationGroups = options.Groups;
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

    private string[]? FindCorrelationGroup(string symbol)
    {
        foreach (var group in _correlationGroups)
        {
            if (group.Contains(symbol))
                return group;
        }
        return null;
    }
}
