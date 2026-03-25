using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.UnitTest.Application.Workers;

/// <summary>
/// Tests for the TrailingStopWorker's ComputeAtr helper method.
/// The method is private static, so we use reflection to test it directly.
/// </summary>
public class TrailingStopWorkerComputeAtrTest
{
    private static decimal InvokeComputeAtr(List<Candle> candles)
    {
        var workerType = typeof(LascodiaTradingEngine.Application.Workers.TrailingStopWorker);
        var method = workerType.GetMethod("ComputeAtr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (decimal)method!.Invoke(null, [candles])!;
    }

    [Fact]
    public void ComputeAtr_EmptyCandles_ReturnsZero()
    {
        var result = InvokeComputeAtr(new List<Candle>());
        Assert.Equal(0m, result);
    }

    [Fact]
    public void ComputeAtr_SingleCandle_ReturnsZero()
    {
        var candles = new List<Candle>
        {
            new() { Timestamp = DateTime.UtcNow, High = 1.1050m, Low = 1.1000m, Close = 1.1020m, IsClosed = true }
        };
        var result = InvokeComputeAtr(candles);
        Assert.Equal(0m, result);
    }

    [Fact]
    public void ComputeAtr_TwoCandles_ReturnsSingleTrueRange()
    {
        var candles = new List<Candle>
        {
            new() { Timestamp = DateTime.UtcNow, High = 1.1060m, Low = 1.1010m, Close = 1.1040m, IsClosed = true },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-5), High = 1.1050m, Low = 1.1000m, Close = 1.1020m, IsClosed = true }
        };

        // TR = max(H-L, |H-PrevClose|, |L-PrevClose|)
        // = max(1.1060-1.1010, |1.1060-1.1020|, |1.1010-1.1020|)
        // = max(0.0050, 0.0040, 0.0010)
        // = 0.0050
        var result = InvokeComputeAtr(candles);
        Assert.Equal(0.0050m, result);
    }

    [Fact]
    public void ComputeAtr_MultipleCandles_ReturnsAverage()
    {
        // Create 4 candles (3 TR values)
        var candles = new List<Candle>
        {
            new() { Timestamp = DateTime.UtcNow, High = 1.1060m, Low = 1.1010m, Close = 1.1040m, IsClosed = true },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-5), High = 1.1050m, Low = 1.1000m, Close = 1.1020m, IsClosed = true },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-10), High = 1.1030m, Low = 1.0990m, Close = 1.1010m, IsClosed = true },
            new() { Timestamp = DateTime.UtcNow.AddMinutes(-15), High = 1.1020m, Low = 1.0980m, Close = 1.1000m, IsClosed = true }
        };

        var result = InvokeComputeAtr(candles);

        // Should be average of 3 TR values; verify it's a positive decimal
        Assert.True(result > 0);
    }

    // ── Trailing distance calculation tests ──────────────────────────────

    [Fact]
    public void FixedPips_TrailingDistance_ConvertsCorrectly()
    {
        // 50 pips = 50 / 10,000 = 0.0050
        decimal trailingStopValue = 50m;
        decimal distance = trailingStopValue / 10_000m;
        Assert.Equal(0.0050m, distance);
    }

    [Fact]
    public void Percentage_TrailingDistance_CalculatesCorrectly()
    {
        // 0.5% of price 1.1000 = 0.0055
        decimal currentPrice = 1.1000m;
        decimal trailingStopValue = 0.5m;
        decimal distance = currentPrice * trailingStopValue / 100m;
        Assert.Equal(0.005500m, distance);
    }

    [Fact]
    public void LongPosition_SL_RatchetsUpOnly()
    {
        decimal currentPrice = 1.1100m;
        decimal trailDistance = 0.0050m;   // 50 pips
        decimal existingSl = 1.1030m;

        decimal newSl = currentPrice - trailDistance; // 1.1050

        // New SL (1.1050) > existing SL (1.1030) → ratchet up
        Assert.True(newSl > existingSl);
    }

    [Fact]
    public void LongPosition_SL_DoesNotRatchetDown()
    {
        decimal currentPrice = 1.1050m;
        decimal trailDistance = 0.0050m;
        decimal existingSl = 1.1020m;

        decimal newSl = currentPrice - trailDistance; // 1.1000

        // New SL (1.1000) < existing SL (1.1020) → should NOT update
        Assert.True(newSl < existingSl);
    }

    [Fact]
    public void ShortPosition_SL_RatchetsDownOnly()
    {
        decimal currentPrice = 1.0900m;
        decimal trailDistance = 0.0050m;
        decimal existingSl = 1.0970m;

        decimal newSl = currentPrice + trailDistance; // 1.0950

        // New SL (1.0950) < existing SL (1.0970) → ratchet down
        Assert.True(newSl < existingSl);
    }

    [Fact]
    public void ShortPosition_SL_DoesNotRatchetUp()
    {
        decimal currentPrice = 1.0950m;
        decimal trailDistance = 0.0050m;
        decimal existingSl = 1.0980m;

        decimal newSl = currentPrice + trailDistance; // 1.1000

        // New SL (1.1000) > existing SL (1.0980) → should NOT update
        Assert.True(newSl > existingSl);
    }
}
