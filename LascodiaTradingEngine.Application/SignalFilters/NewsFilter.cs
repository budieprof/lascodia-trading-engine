using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Checks whether any high-impact economic event for the symbol's currencies
/// falls within the blackout window around the intended trade time.
/// Returns false (not safe) if a blocking event is found.
/// </summary>
[RegisterService]
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
        if (string.IsNullOrWhiteSpace(symbol))
            return [];

        var upper = symbol.ToUpperInvariant();

        // Non-standard short symbols (e.g. indices like "US30", "SPX") — cannot
        // reliably extract forex currency codes, skip news filtering.
        if (upper.Length < 6)
            return [];

        // Standard 6-character forex symbol: first 3 = base, last 3 = quote (e.g. EURUSD)
        // Also handles 6+ char symbols like XAUUSD, XAGUSD
        if (upper.Length >= 6 && upper.All(char.IsLetterOrDigit))
            return [upper[..3], upper[3..6]];

        // 3-character symbol (e.g. index CFD like "SPX") — treat as single currency
        if (upper.Length == 3)
            return [upper];

        // Non-standard symbol (crypto like "BTCUSD" is 6 chars so handled above;
        // but "BTC/USD" with separator or short names like "US30" are not forex).
        // Extract known 3-letter ISO codes by checking if any suffix/prefix matches.
        var currencies = new List<string>();
        if (upper.Length >= 3)
        {
            currencies.Add(upper[..3]);
            if (upper.Length >= 6)
                currencies.Add(upper[^3..]);
        }
        return currencies.Count > 0 ? currencies : [upper];
    }
}
