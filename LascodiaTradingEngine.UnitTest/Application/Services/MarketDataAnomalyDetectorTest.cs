using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MarketDataAnomalyDetectorTest
{
    private readonly MarketDataAnomalyDetector _detector;

    public MarketDataAnomalyDetectorTest()
    {
        var options = new AnomalyDetectionOptions
        {
            PriceSpikeAtrMultiple = 5.0m,
            StaleQuoteMaxSeconds = 30,
            VolumeAnomalyMultiple = 10.0m,
            QuarantineAnomalies = true
        };
        _detector = new MarketDataAnomalyDetector(options, Mock.Of<ILogger<MarketDataAnomalyDetector>>());
    }

    [Fact]
    public async Task ValidateTickAsync_NormalTick_NotAnomalous()
    {
        var result = await _detector.ValidateTickAsync("EURUSD", 1.1000m, 1.1002m, DateTime.UtcNow, "EA1", CancellationToken.None);
        Assert.False(result.IsAnomalous);
    }

    [Fact]
    public async Task ValidateTickAsync_InvertedSpread_Detected()
    {
        var result = await _detector.ValidateTickAsync("GBPUSD", 1.3002m, 1.3000m, DateTime.UtcNow, "EA1", CancellationToken.None);
        Assert.True(result.IsAnomalous);
        Assert.Equal(MarketDataAnomalyType.InvertedSpread, result.AnomalyType);
    }

    [Fact]
    public async Task ValidateTickAsync_TimestampRegression_Detected()
    {
        var now = DateTime.UtcNow;
        // First tick sets state
        await _detector.ValidateTickAsync("USDJPY", 110.00m, 110.02m, now, "EA1", CancellationToken.None);
        // Second tick with earlier timestamp
        var result = await _detector.ValidateTickAsync("USDJPY", 110.01m, 110.03m, now.AddSeconds(-5), "EA1", CancellationToken.None);

        Assert.True(result.IsAnomalous);
        Assert.Equal(MarketDataAnomalyType.TimestampRegression, result.AnomalyType);
    }

    [Fact]
    public void ValidateCandle_ValidOhlc_ReturnsValid()
    {
        var result = _detector.ValidateCandle(1.1000m, 1.1050m, 1.0950m, 1.1020m, 5000, DateTime.UtcNow, null);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateCandle_HighBelowOpen_ReturnsInvalid()
    {
        var result = _detector.ValidateCandle(1.1000m, 1.0990m, 1.0950m, 1.0980m, 5000, DateTime.UtcNow, null);
        Assert.False(result.IsValid);
        Assert.Equal(MarketDataAnomalyType.InvalidOhlc, result.AnomalyType);
    }

    [Fact]
    public void ValidateCandle_NegativeVolume_ReturnsInvalid()
    {
        var result = _detector.ValidateCandle(1.1000m, 1.1050m, 1.0950m, 1.1020m, -1, DateTime.UtcNow, null);
        Assert.False(result.IsValid);
        Assert.Equal(MarketDataAnomalyType.VolumeAnomaly, result.AnomalyType);
    }

    [Fact]
    public void ValidateCandle_ZeroPrice_ReturnsInvalid()
    {
        var result = _detector.ValidateCandle(0m, 1.1050m, 1.0950m, 1.1020m, 5000, DateTime.UtcNow, null);
        Assert.False(result.IsValid);
    }
}
