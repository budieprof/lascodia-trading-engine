using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Implementation of <see cref="IEconomicCalendarFeed"/> backed by OANDA's Labs economic calendar API.
/// </summary>
/// <remarks>
/// Uses the endpoint <c>https://api-fxpractice.oanda.com/labs/v1/calendar</c> to fetch upcoming
/// economic events and their released actuals. The API key is loaded from the active Oanda
/// <see cref="Broker"/> entity in the database.
/// </remarks>
public class OandaCalendarFeed : IEconomicCalendarFeed
{
    private const string HttpClientName = "OandaCalendar";
    private const string CalendarBaseUrl = "https://api-fxpractice.oanda.com/labs/v1/calendar";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OandaCalendarFeed> _logger;

    public OandaCalendarFeed(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<OandaCalendarFeed> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory      = scopeFactory;
        _logger            = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
        IEnumerable<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        try
        {
            var apiKey = await GetOandaApiKeyAsync(ct);
            if (apiKey is null)
                return Array.Empty<EconomicCalendarEvent>();

            var periodSeconds = (long)Math.Ceiling((toUtc - fromUtc).TotalSeconds);
            if (periodSeconds <= 0)
                return Array.Empty<EconomicCalendarEvent>();

            var url = $"{CalendarBaseUrl}?period={periodSeconds}";

            var rawEvents = await FetchEventsAsync(url, apiKey, ct);
            if (rawEvents is null || rawEvents.Count == 0)
                return Array.Empty<EconomicCalendarEvent>();

            var currencySet = new HashSet<string>(
                currencies,
                StringComparer.OrdinalIgnoreCase);

            var results = new List<EconomicCalendarEvent>();

            foreach (var dto in rawEvents)
            {
                if (string.IsNullOrWhiteSpace(dto.Currency))
                    continue;

                if (currencySet.Count > 0 && !currencySet.Contains(dto.Currency))
                    continue;

                var scheduled = DateTimeOffset
                    .FromUnixTimeSeconds(dto.Timestamp)
                    .UtcDateTime;

                // Only include events within the requested window
                if (scheduled < fromUtc || scheduled > toUtc)
                    continue;

                results.Add(new EconomicCalendarEvent(
                    Title:       dto.Title ?? string.Empty,
                    Currency:    dto.Currency,
                    Impact:      MapImpact(dto.Impact),
                    ScheduledAt: scheduled,
                    Forecast:    NullIfEmpty(dto.Forecast),
                    Previous:    NullIfEmpty(dto.Previous),
                    ExternalKey: $"oanda|{dto.Id}|{dto.Timestamp}",
                    Source:      EconomicEventSource.Oanda));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch upcoming events from OANDA economic calendar");
            return Array.Empty<EconomicCalendarEvent>();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        try
        {
            // External key format: "oanda|{id}|{timestamp}"
            var parts = externalKey.Split('|');
            if (parts.Length < 3
                || !string.Equals(parts[0], "oanda", StringComparison.OrdinalIgnoreCase)
                || !long.TryParse(parts[2], out var timestamp))
            {
                _logger.LogWarning(
                    "Invalid OANDA external key format: {ExternalKey}",
                    externalKey);
                return null;
            }

            var eventId = parts[1];

            var apiKey = await GetOandaApiKeyAsync(ct);
            if (apiKey is null)
                return null;

            // Fetch a 24-hour window around the event timestamp to find the matching event
            const long daySeconds = 86_400;
            var url = $"{CalendarBaseUrl}?period={daySeconds}";

            var rawEvents = await FetchEventsAsync(url, apiKey, ct);
            if (rawEvents is null || rawEvents.Count == 0)
                return null;

            var match = rawEvents.FirstOrDefault(e =>
                string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase)
                || e.Timestamp == timestamp);

            return NullIfEmpty(match?.Actual);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to fetch actual value from OANDA for external key {ExternalKey}",
                externalKey);
            return null;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<List<OandaCalendarDto>?> FetchEventsAsync(
        string url,
        string apiKey,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout   = RequestTimeout;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);

        return JsonSerializer.Deserialize<List<OandaCalendarDto>>(json, JsonOptions);
    }

    private async Task<string?> GetOandaApiKeyAsync(CancellationToken ct)
    {
        using var scope    = _scopeFactory.CreateScope();
        var readContext     = scope.ServiceProvider
            .GetRequiredService<IReadApplicationDbContext>();
        var db             = readContext.GetDbContext();

        var broker = await db.Set<Broker>()
            .FirstOrDefaultAsync(
                x => x.BrokerType == BrokerType.Oanda && !x.IsDeleted,
                ct);

        if (broker is null)
        {
            _logger.LogWarning(
                "No OANDA broker configured in the database; " +
                "OANDA economic calendar feed will return empty results");
            return null;
        }

        if (string.IsNullOrWhiteSpace(broker.ApiKey))
        {
            _logger.LogWarning(
                "OANDA broker (Id={BrokerId}) has no API key configured; " +
                "economic calendar feed will return empty results",
                broker.Id);
            return null;
        }

        return broker.ApiKey;
    }

    private static EconomicImpact MapImpact(int oandaImpact) =>
        oandaImpact switch
        {
            1 => EconomicImpact.Low,
            2 => EconomicImpact.Medium,
            3 => EconomicImpact.High,
            _ => EconomicImpact.Low,
        };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // ── JSON serialization ───────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// DTO mirroring the JSON shape returned by the OANDA Labs calendar endpoint.
    /// </summary>
    private sealed class OandaCalendarDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;

        [JsonPropertyName("impact")]
        public int Impact { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("forecast")]
        public string? Forecast { get; set; }

        [JsonPropertyName("previous")]
        public string? Previous { get; set; }

        [JsonPropertyName("actual")]
        public string? Actual { get; set; }
    }
}
