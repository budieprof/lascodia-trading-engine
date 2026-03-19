using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Filters;

/// <summary>
/// Checks whether any high-impact economic event for the symbol's currencies
/// falls within the blackout window around the intended trade time.
/// Returns false (not safe) if a blocking event is found.
/// </summary>
public class NewsFilter : INewsFilter
{
    private readonly IReadApplicationDbContext _context;

    public NewsFilter(IReadApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> IsSafeToTradeAsync(
        string symbol,
        DateTime tradeTime,
        int blackoutMinutesBefore,
        int blackoutMinutesAfter,
        CancellationToken ct)
    {
        string baseCurrency  = symbol.Length >= 3 ? symbol[..3].ToUpperInvariant()  : symbol.ToUpperInvariant();
        string quoteCurrency = symbol.Length >= 6 ? symbol[3..6].ToUpperInvariant() : string.Empty;

        var windowStart = tradeTime.AddMinutes(-blackoutMinutesBefore);
        var windowEnd   = tradeTime.AddMinutes(blackoutMinutesAfter);

        bool hasBlockingEvent = await _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .AnyAsync(x => !x.IsDeleted
                        && x.Impact == EconomicImpact.High
                        && (x.Currency == baseCurrency || (!string.IsNullOrEmpty(quoteCurrency) && x.Currency == quoteCurrency))
                        && x.ScheduledAt >= windowStart
                        && x.ScheduledAt <= windowEnd,
                      ct);

        return !hasBlockingEvent;
    }
}
