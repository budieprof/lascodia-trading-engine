using System.Globalization;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Fetches economic calendar data from Investing.com's public calendar export endpoint.
/// </summary>
/// <remarks>
/// The endpoint returns tab-separated data with columns for date/time, currency, impact,
/// event title, actual, forecast, and previous values. Importance levels (1/2/3) are
/// mapped to <see cref="EconomicImpact"/> values. Results are filtered by the requested
/// currencies before being returned.
/// </remarks>
public class InvestingComCalendarFeed : IEconomicCalendarFeed
{
    private const string BaseUrl = "https://sslecal2.investing.com/export.php";
    private const string HttpClientName = "InvestingCalendar";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InvestingComCalendarFeed> _logger;

    public InvestingComCalendarFeed(
        IHttpClientFactory httpClientFactory,
        ILogger<InvestingComCalendarFeed> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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

        try
        {
            var url = BuildUrl(fromUtc, toUtc);
            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = RequestTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var allEvents = ParseCsv(body);

            return allEvents
                .Where(e => currencySet.Contains(e.Currency))
                .ToList()
                .AsReadOnly();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate genuine cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch economic calendar data from Investing.com");
            return Array.Empty<EconomicCalendarEvent>();
        }
    }

    /// <inheritdoc />
    public Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        // Investing.com does not support individual event lookup by key.
        // Actuals are retrieved in bulk during the next GetUpcomingEventsAsync call.
        return Task.FromResult<string?>(null);
    }

    private static string BuildUrl(DateTime fromUtc, DateTime toUtc)
    {
        var dateFrom = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dateTo = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return $"{BaseUrl}?dateFrom={dateFrom}&dateTo={dateTo}" +
               "&timeZone=8&lang=1&importance=1,2,3&calType=day&timeFilter=timeRemain";
    }

    /// <summary>
    /// Parses the tab-separated response from Investing.com's calendar export.
    /// Expected columns (tab-delimited):
    ///   DateTime | Currency | Importance | Event | Actual | Forecast | Previous
    /// </summary>
    private List<EconomicCalendarEvent> ParseCsv(string body)
    {
        var events = new List<EconomicCalendarEvent>();
        if (string.IsNullOrWhiteSpace(body))
            return events;

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Skip the header row if present.
        var startIndex = 0;
        if (lines.Length > 0 && lines[0].Contains("DateTime", StringComparison.OrdinalIgnoreCase))
            startIndex = 1;

        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            try
            {
                var columns = line.Split('\t');
                if (columns.Length < 7)
                    continue;

                var dateTimeRaw = columns[0].Trim();
                var currency = columns[1].Trim().ToUpperInvariant();
                var importanceRaw = columns[2].Trim();
                var title = columns[3].Trim();
                var actual = NormalizeValue(columns[4]);
                var forecast = NormalizeValue(columns[5]);
                var previous = NormalizeValue(columns[6]);

                if (!TryParseScheduledAt(dateTimeRaw, out var scheduledAt))
                    continue;

                var impact = MapImportance(importanceRaw);

                // Build a stable external key from the source data.
                var externalKey = $"investing|{currency}|{scheduledAt:yyyyMMddHHmm}|{title}";

                events.Add(new EconomicCalendarEvent(
                    Title: title,
                    Currency: currency,
                    Impact: impact,
                    ScheduledAt: scheduledAt,
                    Forecast: forecast,
                    Previous: previous,
                    ExternalKey: externalKey,
                    Source: EconomicEventSource.Investing));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Investing.com calendar row: {Line}", line);
            }
        }

        return events;
    }

    private static bool TryParseScheduledAt(string raw, out DateTime result)
    {
        // Investing.com export uses formats like "2026-03-20 14:30:00" in UTC.
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy HH:mm"
        ];

        return DateTime.TryParseExact(
            raw,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static EconomicImpact MapImportance(string raw)
    {
        return raw switch
        {
            "1" => EconomicImpact.Low,
            "2" => EconomicImpact.Medium,
            "3" => EconomicImpact.High,
            _ => EconomicImpact.Low
        };
    }

    private static string? NormalizeValue(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "&nbsp;" ? null : trimmed;
    }
}
