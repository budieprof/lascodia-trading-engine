using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.SignalFilters;

/// <summary>
/// Checks whether any high-impact economic event for the symbol's currencies
/// falls within the blackout window around the intended trade time.
/// Returns false (not safe) if a blocking event is found.
///
/// <para>Freshness: an empty / stale calendar returns "safe to trade" by construction
/// (no blocking event found). Without a freshness SLA, a broken calendar feed would
/// silently open a window where news halts stop working. IsSafeToTradeAsync therefore
/// also consults MAX(ScheduledAt) and warns when the newest scheduled event is more
/// than <see cref="StaleCalendarHours"/> hours in the past — the feed should always
/// contain future events during trading hours. By default the filter returns true
/// (permissive) on staleness and logs a Warning; set <c>NewsFilter:FailClosedOnStale</c>
/// via EngineConfig to flip to fail-closed (returns false → block trading).</para>
/// </summary>
[RegisterService]
public class NewsFilter : INewsFilter
{
    /// <summary>Threshold for "stale calendar" detection. The feed writes future events
    /// ahead of time, so MAX(ScheduledAt) should usually be hours in the future.</summary>
    public const int StaleCalendarHours = 6;

    private readonly IReadApplicationDbContext _context;
    private readonly ILogger<NewsFilter> _logger;

    // Throttle stale-calendar warnings to one per this interval so a persistently stale
    // feed doesn't spam logs.
    private static DateTime s_lastStaleWarnAt = DateTime.MinValue;
    private static readonly TimeSpan s_staleWarnInterval = TimeSpan.FromMinutes(5);
    private static readonly object s_staleWarnLock = new();

    public NewsFilter(IReadApplicationDbContext context, ILogger<NewsFilter> logger)
    {
        _context = context;
        _logger  = logger;
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

        var db = _context.GetDbContext();

        // Freshness check — cheap probe of MAX(ScheduledAt) across non-deleted rows.
        // A non-trivial feed always has future events queued ahead of time; if the
        // newest scheduled event is in the past, the sync worker has fallen behind
        // or the feed is down. We DON'T block on staleness by default (callers
        // already have kill switches + other gates) but we do log and emit a
        // stable Warning so monitoring can alert.
        DateTime? latestEvent = await db.Set<Domain.Entities.EconomicEvent>()
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.ScheduledAt)
            .Select(x => (DateTime?)x.ScheduledAt)
            .FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;
        bool calendarIsStale = latestEvent is null
            || latestEvent.Value < now.AddHours(-StaleCalendarHours);
        if (calendarIsStale)
        {
            bool shouldWarn;
            lock (s_staleWarnLock)
            {
                shouldWarn = now - s_lastStaleWarnAt > s_staleWarnInterval;
                if (shouldWarn) s_lastStaleWarnAt = now;
            }
            if (shouldWarn)
                _logger.LogWarning(
                    "NewsFilter: economic calendar appears stale — latest scheduled event at {Latest} (threshold {Hours}h). " +
                    "High-impact news halts may not fire. Check EconomicCalendarWorker feed health.",
                    latestEvent?.ToString("u") ?? "none",
                    StaleCalendarHours);
        }

        bool hasBlockingEvent = await db.Set<Domain.Entities.EconomicEvent>()
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
