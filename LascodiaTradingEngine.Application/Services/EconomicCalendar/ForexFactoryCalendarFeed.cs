using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Fetches economic calendar data by scraping ForexFactory's public calendar page.
/// </summary>
/// <remarks>
/// ForexFactory does not expose a public API. This implementation scrapes the calendar
/// HTML table at <c>https://www.forexfactory.com/calendar</c>, extracting events from
/// the structured table rows. The page is requested week-by-week using ForexFactory's
/// <c>?week=mmmDD.YYYY</c> query parameter.
///
/// <b>Time handling:</b> ForexFactory renders times in Eastern Time (ET). This feed
/// converts all times to UTC, accounting for US Eastern daylight saving transitions.
///
/// <b>Date inheritance:</b> ForexFactory only renders the date on the first event of
/// each day — subsequent rows on the same day have empty date cells. The parser carries
/// the current date forward across rows.
///
/// <b>External key format:</b> <c>ff|{eventId}|{yyyyMMdd}</c> where eventId is the
/// <c>data-eventid</c> attribute from the HTML row. This allows <see cref="GetActualAsync"/>
/// to fetch the correct week and locate the event for post-release actual patching.
///
/// <b>Rate limiting:</b> Requests use realistic browser headers and a named HttpClient
/// (<c>"ForexFactoryCalendar"</c>) so timeout and handler policies can be configured
/// externally via <c>IHttpClientFactory</c>.
/// </remarks>
public class ForexFactoryCalendarFeed : IEconomicCalendarFeed
{
    private const string CalendarBaseUrl = "https://www.forexfactory.com/calendar";
    private const string HttpClientName = "ForexFactoryCalendar";

    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
    ];

    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows()
            ? "Eastern Standard Time"
            : "America/New_York");

    // ── Compiled regex patterns for HTML extraction ───────────────────────────
    // All patterns use a 5-second timeout to guard against catastrophic backtracking
    // on pathological or unexpectedly structured HTML responses.

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex RowPattern = new(
        @"<tr[^>]*\bcalendar__row\b[^>]*\bdata-eventid=""(\d+)""[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex DateCellPattern = new(
        @"<td[^>]*\bcalendar__date\b[^>]*>.*?<span[^>]*>\s*(\w{3}\s*\w{3}\s*\d{1,2})\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex TimeCellPattern = new(
        @"<td[^>]*\bcalendar__time\b[^>]*>\s*<span[^>]*>\s*([\d:apm]+|Tentative|All Day)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex CurrencyCellPattern = new(
        @"<td[^>]*\bcalendar__currency\b[^>]*>\s*([A-Z]{3})\s*</td>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex ImpactPattern = new(
        @"icon--ff-impact-(red|ora|yel|gra)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex TitlePattern = new(
        @"calendar__event-title[^>]*>\s*([^<]+?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex ActualCellPattern = new(
        @"<td[^>]*\bcalendar__actual\b[^>]*>\s*<span[^>]*>\s*([^<]*?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex ForecastCellPattern = new(
        @"<td[^>]*\bcalendar__forecast\b[^>]*>\s*<span[^>]*>\s*([^<]*?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex PreviousCellPattern = new(
        @"<td[^>]*\bcalendar__previous\b[^>]*>\s*<span[^>]*>\s*([^<]*?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    // ── Concurrency & caching ────────────────────────────────────────────────

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Number of consecutive weeks that returned substantial HTML but parsed zero events.
    /// Escalates logging from Warning to Error after the threshold to trigger alerts.
    /// Reset to zero when any week parses successfully.
    /// </summary>
    private int _consecutiveParseFailures;
    private const int ParseFailureEscalationThreshold = 3;

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ForexFactoryFetchThrottle _throttle;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<ForexFactoryCalendarFeed> _logger;

    public ForexFactoryCalendarFeed(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ForexFactoryFetchThrottle throttle,
        TradingMetrics metrics,
        ILogger<ForexFactoryCalendarFeed> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache             = cache;
        _throttle          = throttle;
        _metrics           = metrics;
        _logger            = logger;
    }

    // ── IEconomicCalendarFeed ────────────────────────────────────────────────

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

        var weeks = ComputeWeekMondays(fromUtc, toUtc);

        // Fetch each week with throttled concurrency to avoid triggering rate limits
        var fetchTasks = weeks.Select(monday => FetchAndParseWeekThrottledAsync(monday, ct)).ToList();
        var results = await Task.WhenAll(fetchTasks);

        var events = results
            .SelectMany(batch => batch)
            .Where(e => e.ScheduledAt >= fromUtc && e.ScheduledAt <= toUtc)
            .Where(e => currencySet.Contains(e.Currency))
            .DistinctBy(e => e.ExternalKey)
            .ToList();

        _logger.LogInformation(
            "ForexFactoryCalendarFeed: fetched {Count} events from {Weeks} week(s) for {Currencies} currencies",
            events.Count, weeks.Count, currencySet.Count);

        return events.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        // External key format: "ff|{eventId}|{yyyyMMdd}"
        var parts = externalKey.Split('|');
        if (parts.Length < 3
            || !string.Equals(parts[0], "ff", StringComparison.OrdinalIgnoreCase)
            || !DateTime.TryParseExact(parts[2], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var eventDate))
        {
            _logger.LogWarning("ForexFactoryCalendarFeed: invalid external key format '{Key}'", externalKey);
            return null;
        }

        var eventId = parts[1];
        var monday = GetMondayOfWeek(eventDate);

        try
        {
            var rowIndex = await GetOrBuildRowIndexAsync(monday, ct);

            if (rowIndex is null || !rowIndex.TryGetValue(eventId, out var rowHtml))
            {
                _logger.LogDebug(
                    "ForexFactoryCalendarFeed: event {EventId} not found in week of {Monday:yyyy-MM-dd}",
                    eventId, monday);
                return null;
            }

            return ExtractCellValue(ActualCellPattern, rowHtml);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ForexFactoryCalendarFeed: failed to fetch actual for external key '{Key}'", externalKey);
            return null;
        }
    }

    // ── Fetching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches and parses a week's events, respecting the global throttle.
    /// </summary>
    private async Task<List<EconomicCalendarEvent>> FetchAndParseWeekThrottledAsync(
        DateTime monday, CancellationToken ct)
    {
        var html = await FetchWeekHtmlThrottledAsync(monday, ct);

        if (string.IsNullOrWhiteSpace(html))
            return [];

        var eventsCacheKey = $"ff_events_{monday:yyyyMMdd}";
        if (_cache.TryGetValue(eventsCacheKey, out List<EconomicCalendarEvent>? cached) && cached is not null)
            return cached;

        var events = ParseCalendarHtml(html, monday.Year, monday.Month);

        if (events.Count == 0 && html.Length > 1000)
        {
            var failures = Interlocked.Increment(ref _consecutiveParseFailures);
            _metrics.EconFeedParseFailures.Add(1);

            if (failures >= ParseFailureEscalationThreshold)
            {
                _logger.LogError(
                    "ForexFactoryCalendarFeed: {Failures} consecutive parse failures — fetched {HtmlLength} bytes for week of {Monday:yyyy-MM-dd} but parsed 0 events. Likely a structural change in ForexFactory HTML",
                    failures, html.Length, monday);
            }
            else
            {
                _logger.LogWarning(
                    "ForexFactoryCalendarFeed: fetched {HtmlLength} bytes for week of {Monday:yyyy-MM-dd} but parsed 0 events — possible page structure change ({Failures}/{Threshold})",
                    html.Length, monday, failures, ParseFailureEscalationThreshold);
            }

            // Don't cache zero-event results from substantial HTML — likely a structural
            // change or blocked response. Allow the next call to retry a fresh parse.
            return events;
        }

        Interlocked.Exchange(ref _consecutiveParseFailures, 0);

        _cache.Set(eventsCacheKey, events, CacheDuration);
        return events;
    }

    /// <summary>
    /// Fetches raw HTML for a week, respecting the global throttle and HTML cache.
    /// Shared by both <see cref="GetUpcomingEventsAsync"/> and <see cref="GetActualAsync"/>
    /// so that multiple actual-patch requests for the same week reuse a single fetch.
    /// </summary>
    private async Task<string> FetchWeekHtmlThrottledAsync(DateTime monday, CancellationToken ct)
    {
        var htmlCacheKey = $"ff_html_{monday:yyyyMMdd}";

        if (_cache.TryGetValue(htmlCacheKey, out string? cachedHtml) && cachedHtml is not null)
        {
            _metrics.EconFeedCacheHits.Add(1);
            return cachedHtml;
        }

        await _throttle.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the semaphore (another thread may have populated)
            if (_cache.TryGetValue(htmlCacheKey, out cachedHtml) && cachedHtml is not null)
            {
                _metrics.EconFeedCacheHits.Add(1);
                return cachedHtml;
            }

            var url = BuildWeekUrl(monday);
            var sw = Stopwatch.StartNew();
            var html = await FetchPageAsync(url, ct);
            sw.Stop();

            _metrics.EconFeedFetches.Add(1);
            _metrics.EconFeedFetchLatencyMs.Record(sw.Elapsed.TotalMilliseconds);

            if (!string.IsNullOrWhiteSpace(html))
                _cache.Set(htmlCacheKey, html, CacheDuration);

            return html;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.EconFeedErrors.Add(1);
            _logger.LogWarning(ex,
                "ForexFactoryCalendarFeed: failed to fetch week of {Monday:yyyy-MM-dd}", monday);
            return string.Empty;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>
    /// Returns a cached dictionary mapping eventId → row HTML for a given week.
    /// Built once per cache window from the fetched HTML, giving <see cref="GetActualAsync"/>
    /// O(1) lookups instead of re-scanning all regex matches on every call.
    /// </summary>
    private async Task<Dictionary<string, string>?> GetOrBuildRowIndexAsync(
        DateTime monday, CancellationToken ct)
    {
        var indexCacheKey = $"ff_rowindex_{monday:yyyyMMdd}";

        if (_cache.TryGetValue(indexCacheKey, out Dictionary<string, string>? cached) && cached is not null)
            return cached;

        var html = await FetchWeekHtmlThrottledAsync(monday, ct);

        if (string.IsNullOrWhiteSpace(html))
            return null;

        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match rowMatch in RowPattern.Matches(html))
            index[rowMatch.Groups[1].Value] = rowMatch.Groups[2].Value;

        _cache.Set(indexCacheKey, index, CacheDuration);
        return index;
    }

    private async Task<string> FetchPageAsync(string url, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgents[Random.Shared.Next(UserAgents.Length)]);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Referer", CalendarBaseUrl);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // Polly retry policy on the named HttpClient already handles 429 and transient 5xx
        // with exponential backoff (3 retries). If we still see a failure status here, all
        // retries were exhausted — log a descriptive message and throw.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(
                "ForexFactoryCalendarFeed: received 429 from {Url} after all Polly retries exhausted", url);
            throw new HttpRequestException("Rate limited (429) by ForexFactory — all retries exhausted");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            _logger.LogWarning(
                "ForexFactoryCalendarFeed: received 503 from {Url} after all Polly retries exhausted", url);
            throw new HttpRequestException("ForexFactory returned 503 — all retries exhausted");
        }

        response.EnsureSuccessStatusCode();

        // Detect silent redirects to login, consent, or CAPTCHA pages. The final URI
        // after redirects may differ from the requested URL even with a 200 status.
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null
            && !finalUri.AbsolutePath.StartsWith("/calendar", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ForexFactoryCalendarFeed: request to {Url} was redirected to {FinalUri} — possible login/consent redirect",
                url, finalUri);
            return string.Empty;
        }

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Detect blocked, CAPTCHA, or consent-redirect responses that return 200
        // but serve a page without the expected calendar table structure.
        if (html.Length > 500
            && !html.Contains("calendar__row", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "ForexFactoryCalendarFeed: response from {Url} does not contain expected 'calendar__row' landmark ({Length} bytes) — possible CAPTCHA, consent page, or structural change",
                url, html.Length);
            return string.Empty;
        }

        return html;
    }

    // ── HTML Parsing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses ForexFactory's calendar HTML table into structured events.
    /// Handles date inheritance (ForexFactory only shows the date on the first event of each day).
    /// </summary>
    private List<EconomicCalendarEvent> ParseCalendarHtml(string html, int fallbackYear, int mondayMonth)
    {
        var events = new List<EconomicCalendarEvent>();
        var currentDate = DateTime.MinValue;

        foreach (Match rowMatch in RowPattern.Matches(html))
        {
            var eventId  = rowMatch.Groups[1].Value;
            var rowHtml  = rowMatch.Groups[2].Value;

            try
            {
                var ev = ParseEventRow(eventId, rowHtml, ref currentDate, fallbackYear, mondayMonth);
                if (ev is not null)
                    events.Add(ev);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "ForexFactoryCalendarFeed: failed to parse row with eventId={EventId}", eventId);
            }
        }

        return events;
    }

    private EconomicCalendarEvent? ParseEventRow(
        string eventId, string rowHtml, ref DateTime currentDate, int fallbackYear, int mondayMonth)
    {
        // ── Date (inherited if empty) ────────────────────────────────────────
        var dateMatch = DateCellPattern.Match(rowHtml);
        if (dateMatch.Success)
        {
            var rawDate = dateMatch.Groups[1].Value.Trim();
            if (TryParseForexFactoryDate(rawDate, fallbackYear, mondayMonth, out var parsedDate))
                currentDate = parsedDate;
        }

        if (currentDate == DateTime.MinValue)
            return null; // No date context yet

        // ── Currency ─────────────────────────────────────────────────────────
        var currencyMatch = CurrencyCellPattern.Match(rowHtml);
        if (!currencyMatch.Success)
            return null;
        var currency = currencyMatch.Groups[1].Value.Trim().ToUpperInvariant();

        // ── Title ────────────────────────────────────────────────────────────
        var titleMatch = TitlePattern.Match(rowHtml);
        if (!titleMatch.Success)
            return null;
        var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());

        if (string.IsNullOrWhiteSpace(title))
            return null;

        // ── Time ─────────────────────────────────────────────────────────────
        var scheduledAt = currentDate; // Default: midnight if no time
        var isAllDay = false;
        var isTentative = false;
        var timeMatch = TimeCellPattern.Match(rowHtml);
        if (timeMatch.Success)
        {
            var rawTime = timeMatch.Groups[1].Value.Trim();
            if (string.Equals(rawTime, "All Day", StringComparison.OrdinalIgnoreCase))
            {
                isAllDay = true;
            }
            else if (string.Equals(rawTime, "Tentative", StringComparison.OrdinalIgnoreCase))
            {
                isTentative = true;
            }
            else
            {
                if (TryParseTime(rawTime, out var time))
                    scheduledAt = currentDate.Date.Add(time);
            }
        }

        // Capture the Eastern date before UTC conversion for the external key,
        // so GetActualAsync can target the correct ForexFactory week page.
        var easternDate = scheduledAt.Date;

        // Convert from Eastern Time to UTC
        scheduledAt = ConvertEasternToUtc(scheduledAt);

        // ── Impact ───────────────────────────────────────────────────────────
        var impactMatch = ImpactPattern.Match(rowHtml);
        var impact = impactMatch.Success ? MapImpact(impactMatch.Groups[1].Value) : EconomicImpact.Low;

        // ── Forecast / Previous / Actual ─────────────────────────────────────
        var forecast = ExtractCellValue(ForecastCellPattern, rowHtml);
        var previous = ExtractCellValue(PreviousCellPattern, rowHtml);
        var actual   = ExtractCellValue(ActualCellPattern, rowHtml);

        // ── External key ─────────────────────────────────────────────────────
        // Uses the Eastern date (not UTC) so the key maps to the correct FF week.
        var externalKey = $"ff|{eventId}|{easternDate:yyyyMMdd}";

        return new EconomicCalendarEvent(
            Title:       title,
            Currency:    currency,
            Impact:      impact,
            ScheduledAt: scheduledAt,
            Forecast:    forecast,
            Previous:    previous,
            Actual:      actual,
            ExternalKey: externalKey,
            Source:      EconomicEventSource.ForexFactory,
            IsAllDay:    isAllDay,
            IsTentative: isTentative);
    }

    // ── Date / Time Parsing ──────────────────────────────────────────────────

    /// <summary>
    /// Parses ForexFactory's date format: "Mon Mar 24" or "Wed Mar 26".
    /// The year is not included — uses <paramref name="fallbackYear"/> and adjusts
    /// for December→January year boundary when the current month is January and
    /// the parsed month is December (or vice versa).
    /// </summary>
    private static bool TryParseForexFactoryDate(string raw, int fallbackYear, int mondayMonth, out DateTime result)
    {
        result = DateTime.MinValue;

        // Strip day-of-week prefix if present (e.g., "Mon " or "Wed ")
        var cleaned = raw.Trim();
        if (cleaned.Length > 4 && cleaned[3] == ' ')
            cleaned = cleaned[4..].Trim();

        // Expected: "Mar 24", "Jan 5", etc.
        string[] formats = ["MMM d", "MMM dd", "MMMM d", "MMMM dd"];

        if (!DateTime.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return false;

        // Apply year — handle Dec/Jan boundary using the Monday anchor month.
        // A week starting on Dec 29 can contain Jan 1-3 events (next year).
        // A week starting on Jan 5 cannot contain Dec events, but Jan 1 weeks
        // starting on Dec 30 can contain both Dec and Jan dates.
        var year = fallbackYear;
        if (parsed.Month == 1 && mondayMonth == 12)
            year = fallbackYear + 1;
        else if (parsed.Month == 12 && mondayMonth == 1)
            year = fallbackYear - 1;

        result = new DateTime(year, parsed.Month, parsed.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>
    /// Parses ForexFactory's time format: "8:30am", "2:00pm", "12:30pm", etc.
    /// </summary>
    private static bool TryParseTime(string raw, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        string[] formats = ["h:mmtt", "hh:mmtt", "h:mm tt", "hh:mm tt"];

        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault, out var parsed))
        {
            result = parsed.TimeOfDay;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Converts an Eastern Time datetime to UTC, respecting daylight saving transitions.
    /// </summary>
    private static DateTime ConvertEasternToUtc(DateTime easternDateTime)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(easternDateTime, DateTimeKind.Unspecified),
                EasternTimeZone);
        }
        catch (ArgumentException)
        {
            // Invalid time during spring-forward DST gap — assume standard time (EST, UTC-5).
            // Ambiguous times during fall-back are handled automatically by
            // ConvertTimeToUtc (picks standard time), so this only fires for the gap.
            return DateTime.SpecifyKind(easternDateTime.AddHours(5), DateTimeKind.Utc);
        }
    }

    // ── Week Computation ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes the Monday dates for all weeks that overlap the [fromUtc, toUtc] range.
    /// </summary>
    private static List<DateTime> ComputeWeekMondays(DateTime fromUtc, DateTime toUtc)
    {
        var mondays = new List<DateTime>();
        var current = GetMondayOfWeek(fromUtc);

        while (current <= toUtc)
        {
            mondays.Add(current);
            current = current.AddDays(7);
        }

        return mondays;
    }

    /// <summary>
    /// Returns the Monday of the week containing the given date.
    /// </summary>
    private static DateTime GetMondayOfWeek(DateTime date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.Date.AddDays(-daysSinceMonday);
    }

    /// <summary>
    /// Builds the ForexFactory calendar URL for a specific week.
    /// Format: <c>?week=mar24.2026</c> (lowercase month abbreviation + day + year).
    /// </summary>
    private static string BuildWeekUrl(DateTime monday)
    {
        var monthAbbr = monday.ToString("MMM", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{CalendarBaseUrl}?week={monthAbbr}{monday.Day}.{monday.Year}";
    }

    // ── Mapping Helpers ──────────────────────────────────────────────────────

    private static EconomicImpact MapImpact(string colorCode) =>
        colorCode.ToLowerInvariant() switch
        {
            "red" => EconomicImpact.High,
            "ora" => EconomicImpact.Medium,
            "yel" => EconomicImpact.Low,
            "gra" => EconomicImpact.Holiday,
            _     => EconomicImpact.Low
        };

    private static string? ExtractCellValue(Regex pattern, string rowHtml)
    {
        var match = pattern.Match(rowHtml);
        if (!match.Success)
            return null;

        var raw = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(raw) || raw == "&nbsp;" || raw == "&#160;")
            return null;

        var value = System.Net.WebUtility.HtmlDecode(raw);

        // HtmlDecode converts &nbsp;/&#160; to \u00A0 (non-breaking space),
        // which string.IsNullOrWhiteSpace does not treat as whitespace.
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Replace('\u00A0', ' ').Trim();
        return value.Length == 0 ? null : value;
    }
}
