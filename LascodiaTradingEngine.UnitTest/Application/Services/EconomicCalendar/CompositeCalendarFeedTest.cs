using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.EconomicCalendar;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Services.EconomicCalendar;

public sealed class CompositeCalendarFeedTest
{
    [Fact]
    public async Task GetUpcomingEventsAsync_DuplicateEventsAreMergedWithRicherFields()
    {
        var scheduledAt = new DateTime(2026, 04, 24, 14, 0, 0, DateTimeKind.Utc);
        var primaryFeed = new StubEconomicCalendarFeed(
        [
            new EconomicCalendarEvent(
                "US CPI YoY",
                "USD",
                EconomicImpact.Low,
                scheduledAt,
                null,
                "3.0%",
                null,
                string.Empty,
                EconomicEventSource.ForexFactory)
        ]);

        var secondaryFeed = new StubEconomicCalendarFeed(
        [
            new EconomicCalendarEvent(
                "US CPI YoY",
                "USD",
                EconomicImpact.High,
                scheduledAt,
                "3.2%",
                "3.0%",
                "3.1%",
                "us-cpi-1",
                EconomicEventSource.Investing)
        ]);

        var composite = new CompositeCalendarFeed(
            [primaryFeed, secondaryFeed],
            NullLogger<CompositeCalendarFeed>.Instance);

        var merged = await composite.GetUpcomingEventsAsync(["USD"], scheduledAt.AddHours(-1), scheduledAt.AddHours(1), CancellationToken.None);
        var single = Assert.Single(merged);

        Assert.Equal(EconomicImpact.High, single.Impact);
        Assert.Equal("3.2%", single.Forecast);
        Assert.Equal("3.0%", single.Previous);
        Assert.Equal("3.1%", single.Actual);
        Assert.Equal("us-cpi-1", single.ExternalKey);
    }

    private sealed class StubEconomicCalendarFeed(IReadOnlyList<EconomicCalendarEvent> events) : IEconomicCalendarFeed
    {
        public Task<IReadOnlyList<EconomicCalendarEvent>> GetUpcomingEventsAsync(
            IEnumerable<string> currencies,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken ct)
            => Task.FromResult(events);

        public Task<string?> GetActualAsync(string externalKey, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}
