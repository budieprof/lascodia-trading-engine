using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.EconomicCalendar;

/// <summary>
/// Stub implementation of <see cref="IEconomicCalendarFeed"/> used during development
/// and testing when no live calendar API is configured.
/// </summary>
/// <remarks>
/// Returns a small set of deterministic placeholder events so the rest of the system
/// (signal filters, <c>INewsFilter</c>) can exercise the full path without a real feed.
/// Replace this registration in DI with a real implementation (ForexFactory or
/// Investing.com HTTP client) before going live.
/// </remarks>
public class StubEconomicCalendarFeed : IEconomicCalendarFeed
{
    private static readonly (string Title, EconomicImpact Impact)[] EventTemplates =
    [
        ("Non-Farm Payrolls",           EconomicImpact.High),
        ("CPI YoY",                     EconomicImpact.High),
        ("Interest Rate Decision",      EconomicImpact.High),
        ("GDP QoQ",                     EconomicImpact.High),
        ("Retail Sales MoM",            EconomicImpact.Medium),
        ("Manufacturing PMI",           EconomicImpact.Medium),
        ("Unemployment Rate",           EconomicImpact.Medium),
        ("Trade Balance",               EconomicImpact.Low),
    ];

    public Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
        IEnumerable<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var currencyList = currencies.ToList();
        var events       = new List<EconomicCalendarEvent>();
        var span         = toUtc - fromUtc;
        var rng          = new Random(fromUtc.DayOfYear);

        // Generate one or two events per currency across the lookahead window
        foreach (var currency in currencyList)
        {
            var template = EventTemplates[Math.Abs(currency.GetHashCode()) % EventTemplates.Length];

            // Distribute event times deterministically within the window
            double offsetHours = rng.NextDouble() * span.TotalHours;
            var scheduled      = fromUtc.AddHours(offsetHours);
            scheduled          = new DateTime(scheduled.Year, scheduled.Month, scheduled.Day,
                                              scheduled.Hour, 0, 0, DateTimeKind.Utc);

            events.Add(new EconomicCalendarEvent(
                Title:       $"{currency} {template.Title}",
                Currency:    currency,
                Impact:      template.Impact,
                ScheduledAt: scheduled,
                Forecast:    $"{rng.Next(100, 300)}K",
                Previous:    $"{rng.Next(100, 300)}K",
                ExternalKey: $"stub|{currency}|{scheduled:yyyyMMddHH}",
                Source:      EconomicEventSource.Manual));
        }

        return Task.FromResult<IReadOnlyList<EconomicCalendarEvent>>(events);
    }

    public Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
    {
        // Stub: return a plausible placeholder actual so the patch pass can exercise the path.
        var rng = new Random(externalKey.GetHashCode());
        return Task.FromResult<string?>($"{rng.Next(100, 300)}K");
    }
}
