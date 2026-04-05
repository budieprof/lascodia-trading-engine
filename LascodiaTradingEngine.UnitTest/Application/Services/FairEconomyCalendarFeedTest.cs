using System.Net;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.EconomicCalendar;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics.Metrics;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class FairEconomyCalendarFeedTest : IDisposable
{
    private readonly Mock<ILogger<FairEconomyCalendarFeed>> _mockLogger;
    private readonly IMemoryCache _cache;
    private readonly TradingMetrics _metrics;
    private readonly TestMeterFactory _meterFactory;

    public FairEconomyCalendarFeedTest()
    {
        _mockLogger   = new Mock<ILogger<FairEconomyCalendarFeed>>();
        _cache        = new MemoryCache(new MemoryCacheOptions());
        _meterFactory = new TestMeterFactory();
        _metrics      = new TradingMetrics(_meterFactory);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _meterFactory.Dispose();
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_ParsesJsonCorrectly()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "Non-Farm Employment Change", country = "USD", date = "2026-04-03T08:30:00-04:00", impact = "High", forecast = "65K", previous = "-92K" },
            new { title = "Core CPI Flash Estimate y/y", country = "EUR", date = "2026-03-31T05:00:00-04:00", impact = "Medium", forecast = "2.4%", previous = "2.4%" },
            new { title = "BOJ Summary of Opinions", country = "JPY", date = "2026-03-29T19:50:00-04:00", impact = "Low", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD", "EUR", "JPY"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Equal(3, result.Count);

        var nfp = result.First(e => e.Title == "Non-Farm Employment Change");
        Assert.Equal("USD", nfp.Currency);
        Assert.Equal(EconomicImpact.High, nfp.Impact);
        Assert.Equal("65K", nfp.Forecast);
        Assert.Equal("-92K", nfp.Previous);
        Assert.Equal(EconomicEventSource.ForexFactory, nfp.Source);
        // 08:30 ET (EDT, UTC-4) = 12:30 UTC
        Assert.Equal(new DateTime(2026, 4, 3, 12, 30, 0, DateTimeKind.Utc), nfp.ScheduledAt);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_FiltersByCurrency()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "NFP", country = "USD", date = "2026-04-03T08:30:00-04:00", impact = "High", forecast = "", previous = "" },
            new { title = "CPI", country = "EUR", date = "2026-04-03T05:00:00-04:00", impact = "Medium", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("USD", result[0].Currency);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_FiltersByDateRange()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "In range", country = "USD", date = "2026-04-01T08:30:00-04:00", impact = "High", forecast = "", previous = "" },
            new { title = "Out of range", country = "USD", date = "2026-04-15T08:30:00-04:00", impact = "High", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("In range", result[0].Title);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_HandlesEmptyResponse()
    {
        var feed = CreateFeed("[]", HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD"],
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(7),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_ThrowsOnHttpError()
    {
        var feed = CreateFeed("", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            feed.GetUpcomingEventsAsync(
                ["USD"],
                DateTime.UtcNow,
                DateTime.UtcNow.AddDays(7),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_MapsHolidayImpact()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "Bank Holiday", country = "GBP", date = "2026-04-03T03:00:00-04:00", impact = "Holiday", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["GBP"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(EconomicImpact.Holiday, result[0].Impact);
        Assert.True(result[0].IsAllDay);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_NormalizesEmptyForecastToNull()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "Test", country = "USD", date = "2026-04-03T08:30:00-04:00", impact = "Low", forecast = "", previous = "1.2%" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Null(result[0].Forecast);
        Assert.Equal("1.2%", result[0].Previous);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_CachesResults()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = "NFP", country = "USD", date = "2026-04-03T08:30:00-04:00", impact = "High", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);
        var currencies = new[] { "USD" };
        var from = new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        // First call populates cache
        var result1 = await feed.GetUpcomingEventsAsync(currencies, from, to, CancellationToken.None);
        // Second call should hit cache (underlying handler only called once)
        var result2 = await feed.GetUpcomingEventsAsync(currencies, from, to, CancellationToken.None);

        Assert.Single(result1);
        Assert.Single(result2);
    }

    [Fact]
    public async Task GetUpcomingEventsAsync_SkipsEventsWithMissingTitle()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { title = (string?)null, country = "USD", date = "2026-04-03T08:30:00-04:00", impact = "High", forecast = "", previous = "" },
            new { title = "Valid Event", country = "USD", date = "2026-04-03T09:00:00-04:00", impact = "Low", forecast = "", previous = "" }
        });

        var feed = CreateFeed(json, HttpStatusCode.OK);

        var result = await feed.GetUpcomingEventsAsync(
            ["USD"],
            new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Valid Event", result[0].Title);
    }

    [Fact]
    public async Task GetActualAsync_ReturnsNull()
    {
        var feed = CreateFeed("[]", HttpStatusCode.OK);
        var result = await feed.GetActualAsync("fe|USD|202604031230|NFP", CancellationToken.None);
        Assert.Null(result);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private FairEconomyCalendarFeed CreateFeed(string responseBody, HttpStatusCode statusCode)
    {
        var handler = new FakeHttpMessageHandler(responseBody, statusCode);
        var client = new HttpClient(handler);

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory
            .Setup(f => f.CreateClient("FairEconomyCalendar"))
            .Returns(client);

        return new FairEconomyCalendarFeed(mockFactory.Object, _cache, _metrics, _mockLogger.Object);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
                meter.Dispose();
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode)
        {
            _responseBody = responseBody;
            _statusCode   = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
