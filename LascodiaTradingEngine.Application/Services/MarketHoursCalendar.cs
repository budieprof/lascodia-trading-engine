using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Conservative forex/CFD market-hours calendar: treats Friday 22:00 UTC through
/// Sunday 22:00 UTC as the standard weekly closure. No broker- or symbol-specific
/// overrides — this is intentionally a floor, not an exact schedule. Holiday
/// closures are not modelled here; the tick path already degrades gracefully on
/// EA-heartbeat loss, so the worst case for an unmodelled holiday is that a
/// signal's TTL is not extended and it expires normally.
///
/// <para>
/// Registered as a singleton so every worker sees the same calendar without
/// per-request allocation.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IMarketHoursCalendar))]
public sealed class MarketHoursCalendar : IMarketHoursCalendar
{
    // Friday 22:00 UTC close → Sunday 22:00 UTC open is the standard 24/5 FX window.
    // We deliberately use fixed UTC anchors rather than broker-local times so the
    // calendar is deterministic regardless of DST transitions in the caller's zone.
    private static readonly TimeSpan WeeklyCloseTimeUtc = new(22, 0, 0);
    private static readonly TimeSpan WeeklyOpenTimeUtc  = new(22, 0, 0);

    public bool IsMarketClosed(string symbol, DateTime utcTime)
    {
        var t = EnsureUtc(utcTime);

        return t.DayOfWeek switch
        {
            DayOfWeek.Saturday => true,
            DayOfWeek.Friday   => t.TimeOfDay >= WeeklyCloseTimeUtc,
            DayOfWeek.Sunday   => t.TimeOfDay < WeeklyOpenTimeUtc,
            _                  => false,
        };
    }

    public DateTime NextMarketOpen(string symbol, DateTime utcTime)
    {
        var t = EnsureUtc(utcTime);

        if (!IsMarketClosed(symbol, t))
            return t;

        // Walk forward to Sunday 22:00 UTC. The worst case is Saturday → at most 2 days.
        var sunday = t.Date;
        while (sunday.DayOfWeek != DayOfWeek.Sunday)
            sunday = sunday.AddDays(1);

        var open = DateTime.SpecifyKind(sunday + WeeklyOpenTimeUtc, DateTimeKind.Utc);
        return open <= t ? open.AddDays(7) : open;
    }

    private static DateTime EnsureUtc(DateTime utcTime) => utcTime.Kind switch
    {
        DateTimeKind.Utc         => utcTime,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(utcTime, DateTimeKind.Utc),
        _                        => utcTime.ToUniversalTime(),
    };
}
