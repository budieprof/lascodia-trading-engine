using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

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
        // Extract the two currencies from the symbol (e.g. EURUSD → EUR, USD)
        var currencies = ExtractCurrencies(symbol);

        var windowStart = tradeTime.AddMinutes(-blackoutMinutesBefore);
        var windowEnd   = tradeTime.AddMinutes(blackoutMinutesAfter);

        bool hasBlockingEvent = await _context.GetDbContext()
            .Set<Domain.Entities.EconomicEvent>()
            .AnyAsync(x => !x.IsDeleted
                        && x.Impact == EconomicImpact.High
                        && currencies.Contains(x.Currency)
                        && x.ScheduledAt >= windowStart
                        && x.ScheduledAt <= windowEnd,
                      ct);

        return !hasBlockingEvent;
    }

    private static List<string> ExtractCurrencies(string symbol)
    {
        // Standard 6-character forex symbol: first 3 = base, last 3 = quote
        if (symbol.Length >= 6)
            return [symbol[..3].ToUpperInvariant(), symbol[3..6].ToUpperInvariant()];

        return [symbol.ToUpperInvariant()];
    }
}
