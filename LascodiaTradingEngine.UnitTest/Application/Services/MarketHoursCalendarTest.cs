using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MarketHoursCalendarTest
{
    private readonly MarketHoursCalendar _calendar = new();

    // ── IsMarketClosed ──────────────────────────────────────────────────

    [Fact]
    public void IsMarketClosed_Wednesday1200Utc_ReturnsFalse()
    {
        // Weekday mid-day — markets are open.
        var t = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc); // Wed
        Assert.False(_calendar.IsMarketClosed("EURUSD", t));
    }

    [Fact]
    public void IsMarketClosed_FridayBefore22Utc_ReturnsFalse()
    {
        var t = new DateTime(2026, 4, 24, 21, 59, 59, DateTimeKind.Utc);
        Assert.False(_calendar.IsMarketClosed("EURUSD", t));
    }

    [Fact]
    public void IsMarketClosed_FridayAt22Utc_ReturnsTrue()
    {
        var t = new DateTime(2026, 4, 24, 22, 0, 0, DateTimeKind.Utc);
        Assert.True(_calendar.IsMarketClosed("EURUSD", t));
    }

    [Fact]
    public void IsMarketClosed_SaturdayAnyTime_ReturnsTrue()
    {
        var morning = new DateTime(2026, 4, 25, 6, 0, 0, DateTimeKind.Utc);
        var evening = new DateTime(2026, 4, 25, 23, 0, 0, DateTimeKind.Utc);
        Assert.True(_calendar.IsMarketClosed("EURUSD", morning));
        Assert.True(_calendar.IsMarketClosed("EURUSD", evening));
    }

    [Fact]
    public void IsMarketClosed_SundayBefore22Utc_ReturnsTrue()
    {
        var t = new DateTime(2026, 4, 26, 21, 59, 0, DateTimeKind.Utc);
        Assert.True(_calendar.IsMarketClosed("EURUSD", t));
    }

    [Fact]
    public void IsMarketClosed_SundayAt22Utc_ReturnsFalse_MarketReopens()
    {
        var t = new DateTime(2026, 4, 26, 22, 0, 0, DateTimeKind.Utc);
        Assert.False(_calendar.IsMarketClosed("EURUSD", t));
    }

    [Fact]
    public void IsMarketClosed_TreatsUnspecifiedKindAsUtc()
    {
        var t = new DateTime(2026, 4, 25, 12, 0, 0, DateTimeKind.Unspecified); // Saturday
        Assert.True(_calendar.IsMarketClosed("EURUSD", t));
    }

    // ── NextMarketOpen ──────────────────────────────────────────────────

    [Fact]
    public void NextMarketOpen_WhenOpen_ReturnsInputUnchanged()
    {
        var t = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc); // Wednesday
        Assert.Equal(t, _calendar.NextMarketOpen("EURUSD", t));
    }

    [Fact]
    public void NextMarketOpen_FridayEvening_ReturnsSunday22Utc()
    {
        var friday = new DateTime(2026, 4, 24, 23, 0, 0, DateTimeKind.Utc); // After close
        var expectedSunday = new DateTime(2026, 4, 26, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedSunday, _calendar.NextMarketOpen("EURUSD", friday));
    }

    [Fact]
    public void NextMarketOpen_Saturday_ReturnsSameWeekSunday22Utc()
    {
        var saturday = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var expectedSunday = new DateTime(2026, 4, 26, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedSunday, _calendar.NextMarketOpen("EURUSD", saturday));
    }

    [Fact]
    public void NextMarketOpen_SundayBefore22Utc_ReturnsSameSunday22Utc()
    {
        var sunday = new DateTime(2026, 4, 26, 12, 0, 0, DateTimeKind.Utc);
        var expected = new DateTime(2026, 4, 26, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _calendar.NextMarketOpen("EURUSD", sunday));
    }
}
