using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

public class MLFeatureHelperTest
{
    /// <summary>
    /// Generates a list of closed candles with deterministic price movement.
    /// </summary>
    private static List<Candle> GenerateCandles(int count, decimal startPrice = 1.1000m)
    {
        var candles = new List<Candle>();
        var price = startPrice;
        for (int i = 0; i < count; i++)
        {
            var change = (i % 3 == 0 ? 0.001m : -0.0005m);
            price += change;
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                Open      = price,
                High      = price + 0.002m,
                Low       = price - 0.001m,
                Close     = price + change,
                Volume    = 1000 + i * 10,
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    // -- Test 1: BuildFeatureVector returns correct length (33 features)

    [Fact]
    public void BuildFeatureVector_ReturnsCorrectLength()
    {
        // Arrange — generate a lookback window of candles plus a current and previous candle
        var candles = GenerateCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = candles.GetRange(0, MLFeatureHelper.LookbackWindow);
        var current = candles[MLFeatureHelper.LookbackWindow];
        var prev    = candles[MLFeatureHelper.LookbackWindow - 1];

        // Act
        float[] features = MLFeatureHelper.BuildFeatureVector(window, current, prev);

        // Assert
        Assert.Equal(MLFeatureHelper.FeatureCount, features.Length);
    }

    // -- Test 2: BuildFeatureVector clamps extreme values to [-3, 3]

    [Fact]
    public void BuildFeatureVector_ClampsExtremeValues()
    {
        // Arrange — generate candles; the clamping behavior applies to all computed features
        var candles = GenerateCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = candles.GetRange(0, MLFeatureHelper.LookbackWindow);
        var current = candles[MLFeatureHelper.LookbackWindow];
        var prev    = candles[MLFeatureHelper.LookbackWindow - 1];

        // Act
        float[] features = MLFeatureHelper.BuildFeatureVector(window, current, prev);

        // Assert — all feature values should be within the clamped range [-3, 3]
        // (Some features like RSI are 0-1, some cyclical are [-1,1], all are bounded)
        foreach (var val in features)
        {
            Assert.True(val >= -3.0f && val <= 3.0f,
                $"Feature value {val} is outside the expected clamped range [-3, 3].");
        }
    }

    // -- Test 3: BuildTrainingSamples returns correct labels (next-bar direction)

    [Fact]
    public void BuildTrainingSamples_ReturnsCorrectLabels()
    {
        // Arrange — generate enough candles for at least a few training samples
        // Need LookbackWindow + 2 candles minimum (LookbackWindow for features, +1 for
        // the current candle, +1 for the label/next candle)
        var candles = GenerateCandles(MLFeatureHelper.LookbackWindow + 10);

        // Act
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        // Assert — should produce samples; each label should be 0 or 1
        Assert.NotEmpty(samples);

        foreach (var sample in samples)
        {
            Assert.True(sample.Direction == 0 || sample.Direction == 1,
                $"Direction label should be 0 or 1, got {sample.Direction}.");
            Assert.Equal(MLFeatureHelper.FeatureCount, sample.Features.Length);
        }

        // Verify the expected number of samples:
        // candles.Count - LookbackWindow - 1 = 10 + LookbackWindow - LookbackWindow - 1 = 9
        Assert.Equal(9, samples.Count);
    }

    // -- Test 4: BuildTrainingSamples with insufficient candles returns empty

    [Fact]
    public void BuildTrainingSamples_InsufficientCandles_ReturnsEmpty()
    {
        // Arrange — fewer candles than LookbackWindow + 2 (need at least LookbackWindow + 2)
        var candles = GenerateCandles(MLFeatureHelper.LookbackWindow);

        // Act
        var samples = MLFeatureHelper.BuildTrainingSamples(candles);

        // Assert — cannot produce any samples because we need LookbackWindow + at least 2 candles
        Assert.Empty(samples);
    }

    // -- Test 5: Triple barrier labels correctly — profit target hit before stop

    [Fact]
    public void TripleBarrier_LabelsCorrectly()
    {
        // Arrange — create a sequence where after the lookback window, the price
        // clearly trends upward. This means the profit target should be hit before
        // the stop loss for at least some samples.
        var candles = new List<Candle>();
        decimal price = 1.1000m;

        // Build the lookback window with moderate price action
        for (int i = 0; i < MLFeatureHelper.LookbackWindow + 5; i++)
        {
            decimal change = 0.0005m; // consistent upward movement
            price += change;
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.AddHours(-(MLFeatureHelper.LookbackWindow + 20) + i),
                Open      = price,
                High      = price + 0.003m,  // wide highs to establish ATR
                Low       = price - 0.002m,  // wide lows to establish ATR
                Close     = price + change,
                Volume    = 1000,
                IsClosed  = true,
                IsDeleted = false
            });
        }

        // Add a strong upward trend after the lookback window
        for (int i = 0; i < 15; i++)
        {
            price += 0.005m; // strong upward move
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.AddHours(-15 + i),
                Open      = price - 0.005m,
                High      = price + 0.001m,
                Low       = price - 0.006m,
                Close     = price,
                Volume    = 1500,
                IsClosed  = true,
                IsDeleted = false
            });
        }

        // Act — use triple-barrier labeling with default ATR multipliers
        var samples = MLFeatureHelper.BuildTrainingSamplesWithTripleBarrier(candles);

        // Assert — should produce samples; at least some should have label=1
        // (profit target hit) given the strong upward trend
        Assert.NotEmpty(samples);

        bool anyProfitHit = samples.Any(s => s.Direction == 1);
        Assert.True(anyProfitHit,
            "Expected at least one sample where the profit target was hit before the stop loss.");

        // All directions should be 0 or 1
        foreach (var sample in samples)
        {
            Assert.True(sample.Direction == 0 || sample.Direction == 1,
                $"Direction label should be 0 or 1, got {sample.Direction}.");
        }
    }
}
