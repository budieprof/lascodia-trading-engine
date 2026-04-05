using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Fetches economic calendar data from the FairEconomy public JSON API, which mirrors
/// ForexFactory's calendar data in a structured JSON format — no scraping or JavaScript
/// challenge solving required.
/// </summary>
/// <remarks>
/// Endpoint: <c>https://nfs.faireconomy.media/ff_calendar_thisweek.json</c>
/// Returns the current week's events (Sunday–Saturday) with title, country (ISO currency),
/// date (Eastern Time with offset), impact level, forecast, and previous values.
///
/// Since only the current week is available, the lookahead is naturally capped at ~7 days.
/// Results are cached for <see cref="CacheDuration"/> to avoid redundant fetches within the
/// same polling cycle.
/// </remarks>
public class FairEconomyCalendarFeed : IEconomicCalendarFeed
{
    private const string ThisWeekUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
    private const string HttpClientName = "FairEconomyCalendar";
    private const string CacheKey = "faireconomy_thisweek";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<FairEconomyCalendarFeed> _logger;

    public FairEconomyCalendarFeed(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        TradingMetrics metrics,
        ILogger<FairEconomyCalendarFeed> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _metrics           = metrics;
        _logger            = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
        IEnumerable<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var currencySet = new HashSet<string>(
            currencies.Select(c => c.Trim().ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (currencySet.Count == 0)
            return Array.Empty<EconomicCalendarEvent>();

        var allEvents = await FetchThisWeekAsync(ct);

        var filtered = allEvents
            .Where(e => e.ScheduledAt >= fromUtc && e.ScheduledAt <= toUtc)
            .Where(e => currencySet.Contains(e.Currency))
            .ToList();

        _logger.LogInformation(
            "FairEconomyCalendarFeed: returning {Count} events for {Currencies} currencies (from {Total} total)",
            filtered.Count, currencySet.Count, allEvents.Count);

        return filtered.AsReadOnly();
    }

    /// <inheritdoc />
    public Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        // The FairEconomy API does not support individual event lookup.
        // Actuals are available inline in the next GetUpcomingEventsAsync call
        // once they are released (the API mirrors ForexFactory's live data).
        return Task.FromResult<string?>(null);
    }

    private async Task<List<EconomicCalendarEvent>> FetchThisWeekAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out List<EconomicCalendarEvent>? cached) && cached is not null)
        {
            _metrics.EconFeedCacheHits.Add(1);
            return cached;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, ThisWeekUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var rawEvents = JsonSerializer.Deserialize<List<FairEconomyEvent>>(json, JsonOptions);

        if (rawEvents is null || rawEvents.Count == 0)
        {
            _logger.LogWarning("FairEconomyCalendarFeed: API returned empty or null response");
            return [];
        }

        _metrics.EconFeedFetches.Add(1);

        var events = new List<EconomicCalendarEvent>(rawEvents.Count);
        foreach (var raw in rawEvents)
        {
            try
            {
                var ev = MapToCalendarEvent(raw);
                if (ev is not null)
                    events.Add(ev);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "FairEconomyCalendarFeed: failed to parse event '{Title}'", raw.Title);
            }
        }

        _logger.LogInformation(
            "FairEconomyCalendarFeed: fetched and parsed {Count} events from API ({Raw} raw)",
            events.Count, rawEvents.Count);

        _cache.Set(CacheKey, events, CacheDuration);
        return events;
    }

    private static EconomicCalendarEvent? MapToCalendarEvent(FairEconomyEvent raw)
    {
        if (string.IsNullOrWhiteSpace(raw.Title) || string.IsNullOrWhiteSpace(raw.Country))
            return null;

        // Parse the ISO 8601 date with offset (e.g., "2026-03-31T08:30:00-04:00")
        if (!DateTimeOffset.TryParse(raw.Date, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
            return null;

        var scheduledAtUtc = dto.UtcDateTime;
        var currency = raw.Country.Trim().ToUpperInvariant();
        var impact = MapImpact(raw.Impact);
        var forecast = NormalizeValue(raw.Forecast);
        var previous = NormalizeValue(raw.Previous);
        var actual = NormalizeValue(raw.Actual);

        // Build a stable external key compatible with the existing ForexFactory format
        // so deduplication works if the user later switches back or runs both feeds.
        var externalKey = $"fe|{currency}|{scheduledAtUtc:yyyyMMddHHmm}|{raw.Title.Trim()}";

        var isHoliday = impact == EconomicImpact.Holiday;

        return new EconomicCalendarEvent(
            Title:       raw.Title.Trim(),
            Currency:    currency,
            Impact:      impact,
            ScheduledAt: scheduledAtUtc,
            Forecast:    forecast,
            Previous:    previous,
            Actual:      actual,
            ExternalKey: externalKey,
            Source:      EconomicEventSource.ForexFactory,
            IsAllDay:    isHoliday);
    }

    private static EconomicImpact MapImpact(string? impact) =>
        impact?.Trim() switch
        {
            "High"    => EconomicImpact.High,
            "Medium"  => EconomicImpact.Medium,
            "Low"     => EconomicImpact.Low,
            "Holiday" => EconomicImpact.Holiday,
            _         => EconomicImpact.Low
        };

    private static string? NormalizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Mirrors the JSON shape returned by the FairEconomy API.
    /// </summary>
    private sealed class FairEconomyEvent
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("impact")]
        public string? Impact { get; set; }

        [JsonPropertyName("forecast")]
        public string? Forecast { get; set; }

        [JsonPropertyName("previous")]
        public string? Previous { get; set; }

        [JsonPropertyName("actual")]
        public string? Actual { get; set; }
    }
}
