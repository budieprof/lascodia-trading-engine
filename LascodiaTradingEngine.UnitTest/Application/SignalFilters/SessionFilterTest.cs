using LascodiaTradingEngine.Application.SignalFilters;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.SignalFilters;

public class SessionFilterTest
{
    private readonly SessionFilter _filter = new();

    // ── GetCurrentSession ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 30, TradingSession.Asian)]       // 00:30 UTC
    [InlineData(3, 0, TradingSession.Asian)]         // 03:00 UTC
    [InlineData(7, 59, TradingSession.Asian)]        // 07:59 UTC — before London starts at 08:00
    public void GetCurrentSession_AsianHours_ReturnsAsian(int hour, int minute, TradingSession expected)
    {
        var utc = new DateTime(2026, 3, 25, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _filter.GetCurrentSession(utc));
    }

    [Theory]
    [InlineData(9, 0, TradingSession.London)]        // 09:00 UTC — London starts at 08:00 but 09:00 is not LNY overlap
    [InlineData(10, 0, TradingSession.London)]       // 10:00 UTC
    [InlineData(12, 59, TradingSession.London)]      // 12:59 UTC — just before LNY overlap
    public void GetCurrentSession_LondonOnlyHours_ReturnsLondon(int hour, int minute, TradingSession expected)
    {
        var utc = new DateTime(2026, 3, 25, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _filter.GetCurrentSession(utc));
    }

    [Theory]
    [InlineData(13, 0, TradingSession.LondonNYOverlap)]   // 13:00 UTC — overlap starts
    [InlineData(15, 30, TradingSession.LondonNYOverlap)]  // 15:30 UTC
    [InlineData(16, 59, TradingSession.LondonNYOverlap)]  // 16:59 UTC — overlap ends at 17:00
    public void GetCurrentSession_OverlapHours_ReturnsLondonNYOverlap(int hour, int minute, TradingSession expected)
    {
        var utc = new DateTime(2026, 3, 25, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _filter.GetCurrentSession(utc));
    }

    [Theory]
    [InlineData(17, 0, TradingSession.NewYork)]      // 17:00 UTC — NY only after overlap
    [InlineData(20, 0, TradingSession.NewYork)]      // 20:00 UTC
    [InlineData(21, 59, TradingSession.NewYork)]     // 21:59 UTC
    public void GetCurrentSession_NewYorkOnlyHours_ReturnsNewYork(int hour, int minute, TradingSession expected)
    {
        var utc = new DateTime(2026, 3, 25, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(expected, _filter.GetCurrentSession(utc));
    }

    [Theory]
    [InlineData(22, 0)]   // 22:00 UTC — late evening
    [InlineData(23, 30)]  // 23:30 UTC
    public void GetCurrentSession_LateEvening_ReturnsAsianPreOpen(int hour, int minute)
    {
        var utc = new DateTime(2026, 3, 25, hour, minute, 0, DateTimeKind.Utc);
        Assert.Equal(TradingSession.Asian, _filter.GetCurrentSession(utc));
    }

    // ── IsSessionAllowed ───────────────────────────────────────────────────

    [Fact]
    public void IsSessionAllowed_SessionInList_ReturnsTrue()
    {
        var allowed = new List<TradingSession> { TradingSession.London, TradingSession.NewYork };
        Assert.True(_filter.IsSessionAllowed(TradingSession.London, allowed));
    }

    [Fact]
    public void IsSessionAllowed_SessionNotInList_ReturnsFalse()
    {
        var allowed = new List<TradingSession> { TradingSession.London };
        Assert.False(_filter.IsSessionAllowed(TradingSession.Asian, allowed));
    }

    [Fact]
    public void IsSessionAllowed_EmptyList_ReturnsFalse()
    {
        Assert.False(_filter.IsSessionAllowed(TradingSession.London, new List<TradingSession>()));
    }
}
