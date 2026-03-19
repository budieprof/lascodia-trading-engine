using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Abstraction over an external economic calendar data provider
/// (e.g. ForexFactory, Investing.com, or a commercial data vendor).
/// </summary>
public interface IEconomicCalendarFeed
{
    /// <summary>
    /// Fetches upcoming economic events for the given currencies within the specified UTC window.
    /// </summary>
    Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
        IEnumerable<string> currencies,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct);

    /// <summary>
    /// Fetches the actual released figure for a past event identified by its external key.
    /// Returns null if the actual is not yet available or the event is not found.
    /// </summary>
    Task<string?> GetActualAsync(string externalKey, CancellationToken ct);
}

/// <summary>
/// Lightweight DTO returned by <see cref="IEconomicCalendarFeed"/>.
/// </summary>
public record EconomicCalendarEvent(
    string Title,
    string Currency,
    EconomicImpact Impact,
    DateTime ScheduledAt,
    string? Forecast,
    string? Previous,
    /// <summary>Stable identifier from the data provider used to fetch the post-release actual.</summary>
    string ExternalKey,
    EconomicEventSource Source);
