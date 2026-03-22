using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Merges events from multiple <see cref="IEconomicCalendarFeed"/> sources into a single
/// deduplicated stream. Each source is queried in parallel; failures in one source do not
/// block the others.
///
/// Deduplication: events with the same Title + Currency + ScheduledAt (minute precision)
/// are collapsed — the first source to return an event wins.
/// </summary>
public sealed class CompositeCalendarFeed : IEconomicCalendarFeed
{
    private readonly IEnumerable<IEconomicCalendarFeed> _feeds;
    private readonly ILogger<CompositeCalendarFeed> _logger;

    public CompositeCalendarFeed(
        IEnumerable<IEconomicCalendarFeed> feeds,
        ILogger<CompositeCalendarFeed> logger)
    {
        _feeds  = feeds;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
        IEnumerable<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var currencyList = currencies.ToList();
        var tasks = _feeds
            .Where(f => f is not CompositeCalendarFeed) // avoid recursion if accidentally registered
            .Select(feed => FetchSafe(feed, currencyList, fromUtc, toUtc, ct))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Merge and deduplicate across sources
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged  = new List<EconomicCalendarEvent>();

        foreach (var batch in results)
        {
            foreach (var ev in batch)
            {
                var key = $"{ev.Title.Trim().ToUpperInvariant()}|{ev.Currency.ToUpperInvariant()}|{ev.ScheduledAt:yyyyMMddHHmm}";
                if (seen.Add(key))
                    merged.Add(ev);
            }
        }

        _logger.LogInformation(
            "CompositeCalendarFeed: merged {Total} events from {Sources} source(s) ({Deduped} after dedup)",
            results.Sum(r => r.Count), results.Length, merged.Count);

        return merged;
    }

    public async Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        // Try each feed until one returns a non-null actual
        foreach (var feed in _feeds)
        {
            if (feed is CompositeCalendarFeed) continue;

            try
            {
                var actual = await feed.GetActualAsync(externalKey, ct);
                if (actual is not null)
                    return actual;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CompositeCalendarFeed: {Feed} failed to resolve actual for key '{Key}'",
                    feed.GetType().Name, externalKey);
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<EconomicCalendarEvent>> FetchSafe(
        IEconomicCalendarFeed feed,
        List<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        try
        {
            return await feed.GetUpcomingEventsAsync(currencies, fromUtc, toUtc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CompositeCalendarFeed: {Feed} failed — skipping this source",
                feed.GetType().Name);
            return [];
        }
    }
}
