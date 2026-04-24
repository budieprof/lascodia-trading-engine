using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Merges events from multiple <see cref="IEconomicCalendarFeed"/> sources into a single
/// deduplicated stream. Each source is queried in parallel; failures in one source do not
/// block the others.
///
/// Deduplication: events with the same Title + Currency + ScheduledAt (minute precision)
/// are collapsed and merged so richer fields from later sources are preserved.
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

        // Merge and deduplicate across sources while preserving richer fields from
        // later sources (for example when one feed has the Actual or ExternalKey
        // and the earlier feed does not).
        var mergedByKey = new Dictionary<string, EconomicCalendarEvent>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in results)
        {
            foreach (var ev in batch)
            {
                var key = $"{ev.Title.Trim().ToUpperInvariant()}|{ev.Currency.ToUpperInvariant()}|{ev.ScheduledAt:yyyyMMddHHmm}";
                if (!mergedByKey.TryGetValue(key, out var existing))
                {
                    mergedByKey[key] = ev;
                    continue;
                }

                mergedByKey[key] = Merge(existing, ev);
            }
        }

        var merged = mergedByKey.Values.ToList();

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

    private static EconomicCalendarEvent Merge(EconomicCalendarEvent existing, EconomicCalendarEvent candidate)
    {
        bool candidateProvidesActual = !string.IsNullOrWhiteSpace(candidate.Actual);
        bool existingProvidesActual = !string.IsNullOrWhiteSpace(existing.Actual);
        bool candidateProvidesExternalKey = !string.IsNullOrWhiteSpace(candidate.ExternalKey);
        bool existingProvidesExternalKey = !string.IsNullOrWhiteSpace(existing.ExternalKey);

        var preferred = existing;
        if (candidateProvidesActual && !existingProvidesActual)
            preferred = candidate;
        else if (candidateProvidesActual == existingProvidesActual &&
                 candidateProvidesExternalKey && !existingProvidesExternalKey)
            preferred = candidate;
        else if (candidateProvidesActual == existingProvidesActual &&
                 candidateProvidesExternalKey == existingProvidesExternalKey &&
                 GetImpactPriority(candidate.Impact) > GetImpactPriority(existing.Impact))
            preferred = candidate;

        var other = ReferenceEquals(preferred, existing) ? candidate : existing;

        return preferred with
        {
            Forecast = FirstNonBlank(preferred.Forecast, other.Forecast),
            Previous = FirstNonBlank(preferred.Previous, other.Previous),
            Actual = FirstNonBlank(preferred.Actual, other.Actual),
            ExternalKey = FirstNonBlank(preferred.ExternalKey, other.ExternalKey) ?? string.Empty,
            Impact = GetImpactPriority(preferred.Impact) >= GetImpactPriority(other.Impact) ? preferred.Impact : other.Impact,
            IsAllDay = preferred.IsAllDay || other.IsAllDay,
            IsTentative = preferred.IsTentative || other.IsTentative
        };
    }

    private static string? FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : second;

    private static int GetImpactPriority(Domain.Enums.EconomicImpact impact)
        => impact switch
        {
            Domain.Enums.EconomicImpact.High => 3,
            Domain.Enums.EconomicImpact.Medium => 2,
            Domain.Enums.EconomicImpact.Low => 1,
            _ => 0
        };
}
