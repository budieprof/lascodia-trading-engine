using Microsoft.EntityFrameworkCore;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.SignalFilters;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.SignalFilters;

public class NewsFilterTest
{
    private readonly Mock<IReadApplicationDbContext> _mockContext;
    private readonly Mock<DbContext> _mockDbContext;
    private readonly NewsFilter _filter;

    public NewsFilterTest()
    {
        _mockContext = new Mock<IReadApplicationDbContext>();
        _mockDbContext = new Mock<DbContext>();
        _mockContext.Setup(c => c.GetDbContext()).Returns(_mockDbContext.Object);
        _filter = new NewsFilter(_mockContext.Object);
    }

    private void SetupEvents(List<EconomicEvent> events)
    {
        var mockSet = events.AsQueryable().BuildMockDbSet();
        _mockDbContext.Setup(c => c.Set<EconomicEvent>()).Returns(mockSet.Object);
    }

    [Fact]
    public async Task IsSafeToTrade_NoEvents_ReturnsTrue()
    {
        SetupEvents(new List<EconomicEvent>());

        var result = await _filter.IsSafeToTradeAsync("EURUSD", DateTime.UtcNow, 30, 30, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsSafeToTrade_HighImpactEventInWindow_ReturnsFalse()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "USD",
                Impact = EconomicImpact.High,
                ScheduledAt = tradeTime.AddMinutes(10), // 10 min after trade time, within 30-min window
                IsDeleted = false
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsSafeToTrade_HighImpactEventOutsideWindow_ReturnsTrue()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "USD",
                Impact = EconomicImpact.High,
                ScheduledAt = tradeTime.AddMinutes(60), // 60 min after, outside 30-min window
                IsDeleted = false
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsSafeToTrade_LowImpactEventInWindow_ReturnsTrue()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "EUR",
                Impact = EconomicImpact.Low,
                ScheduledAt = tradeTime.AddMinutes(5),
                IsDeleted = false
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsSafeToTrade_EventForUnrelatedCurrency_ReturnsTrue()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "JPY",
                Impact = EconomicImpact.High,
                ScheduledAt = tradeTime.AddMinutes(5),
                IsDeleted = false
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsSafeToTrade_EventBeforeWindow_ReturnsTrue()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "EUR",
                Impact = EconomicImpact.High,
                ScheduledAt = tradeTime.AddMinutes(-60), // 60 min before, outside 30-min window
                IsDeleted = false
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsSafeToTrade_DeletedEvent_ReturnsTrue()
    {
        var tradeTime = new DateTime(2026, 3, 25, 14, 0, 0, DateTimeKind.Utc);
        var events = new List<EconomicEvent>
        {
            new()
            {
                Id = 1,
                Currency = "USD",
                Impact = EconomicImpact.High,
                ScheduledAt = tradeTime.AddMinutes(5),
                IsDeleted = true
            }
        };
        SetupEvents(events);

        var result = await _filter.IsSafeToTradeAsync("EURUSD", tradeTime, 30, 30, CancellationToken.None);

        Assert.True(result);
    }
}
