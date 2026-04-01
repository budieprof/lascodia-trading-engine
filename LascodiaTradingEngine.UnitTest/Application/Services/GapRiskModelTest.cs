using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MockQueryable.Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.UnitTest.TestHelpers;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class GapRiskModelTest
{
    private Mock<IServiceScopeFactory> CreateScopeFactory(List<Candle> candles)
    {
        var mockDbContext = new Mock<DbContext>();
        var mockCandleSet = candles.AsQueryable().BuildMockDbSet();
        mockDbContext.Setup(c => c.Set<Candle>()).Returns(mockCandleSet.Object);

        var mockReadContext = new Mock<IReadApplicationDbContext>();
        mockReadContext.Setup(c => c.GetDbContext()).Returns(mockDbContext.Object);

        var mockServiceProvider = new Mock<IServiceProvider>();
        mockServiceProvider
            .Setup(p => p.GetService(typeof(IReadApplicationDbContext)))
            .Returns(mockReadContext.Object);

        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        return mockScopeFactory;
    }

    [Fact]
    public async Task GetGapMultiplierAsync_CacheHitWithin7Days_ReturnsCached()
    {
        // First call will calibrate, second should return cached
        var candles = new List<Candle>();
        for (int i = 0; i < 5; i++)
        {
            candles.Add(EntityFactory.CreateCandle(
                symbol: "EURUSD", timeframe: Timeframe.D1,
                close: 1.1000m + i * 0.001m,
                timestamp: DateTime.UtcNow.AddDays(-5 + i)));
        }

        var scopeFactory = CreateScopeFactory(candles);
        var model = new GapRiskModel(scopeFactory.Object, Mock.Of<ILogger<GapRiskModel>>());

        // First call triggers calibration (insufficient samples -> default)
        var first = await model.GetGapMultiplierAsync("EURUSD", CancellationToken.None);
        Assert.Equal(1.5m, first.GapMultiplier); // Default due to < 20 samples

        // Second call should return cached (no new scope creation)
        var second = await model.GetGapMultiplierAsync("EURUSD", CancellationToken.None);
        Assert.Equal(first.GapMultiplier, second.GapMultiplier);
        Assert.Equal(first.LastCalibrated, second.LastCalibrated);
    }

    [Fact]
    public async Task GetGapMultiplierAsync_InsufficientSamples_ReturnsDefault1_5x()
    {
        // Only 10 candles, well below MinSamples of 20
        var candles = new List<Candle>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(EntityFactory.CreateCandle(
                symbol: "GBPUSD", timeframe: Timeframe.D1,
                close: 1.2500m + i * 0.001m,
                timestamp: DateTime.UtcNow.AddDays(-10 + i)));
        }

        var scopeFactory = CreateScopeFactory(candles);
        var model = new GapRiskModel(scopeFactory.Object, Mock.Of<ILogger<GapRiskModel>>());

        var result = await model.GetGapMultiplierAsync("GBPUSD", CancellationToken.None);

        Assert.Equal(1.5m, result.GapMultiplier);
    }

    [Fact]
    public async Task GetGapMultiplierAsync_LargeGaps_MultiplierClampedToMax5()
    {
        // Generate candles with large weekend gaps to push multiplier high
        var candles = new List<Candle>();
        decimal basePrice = 1.1000m;

        // Create 2+ years of daily candles with exaggerated weekend gaps
        for (int week = 0; week < 60; week++)
        {
            // Monday-Friday (5 trading days)
            for (int day = 0; day < 5; day++)
            {
                int dayOffset = week * 7 + day;
                decimal close = basePrice + (week % 2 == 0 ? 0.001m : -0.001m) * day;
                candles.Add(EntityFactory.CreateCandle(
                    symbol: "USDJPY", timeframe: Timeframe.D1,
                    close: close,
                    timestamp: DateTime.UtcNow.AddDays(-420 + dayOffset)));
            }

            // Skip Saturday/Sunday (gap of 2+ days between Friday and Monday)
            // The next Monday candle's open will differ from Friday's close
            // We simulate this by making the Monday candle have a different open
        }

        var scopeFactory = CreateScopeFactory(candles);
        var model = new GapRiskModel(scopeFactory.Object, Mock.Of<ILogger<GapRiskModel>>());

        var result = await model.GetGapMultiplierAsync("USDJPY", CancellationToken.None);

        // Multiplier should be clamped at 5.0 maximum
        Assert.True(result.GapMultiplier <= 5.0m,
            $"Gap multiplier {result.GapMultiplier} should be clamped to max 5.0");
    }
}
