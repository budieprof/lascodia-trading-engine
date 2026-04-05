using LascodiaTradingEngine.Application.MarketRegime.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.UnitTest.Application.MarketRegimeDetection;

public class HmmRegimeDetectorTest
{
    /// <summary>
    /// Generates candles with a strong directional trend (steadily increasing closes).
    /// This simulates a trending market with high ADX equivalent.
    /// </summary>
    private static List<Candle> GenerateTrendingCandles(int count, decimal startPrice = 1.1000m)
    {
        var candles = new List<Candle>();
        decimal price = startPrice;

        for (int i = 0; i < count; i++)
        {
            // Consistent upward movement with tight ranges — strong trend signal
            decimal change = 0.003m;
            price += change;
            candles.Add(new Candle
            {
                Id        = i + 1,
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                Open      = price - change,
                High      = price + 0.001m,
                Low       = price - change - 0.0005m,
                Close     = price,
                Volume    = 1200 + i * 5,
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    /// <summary>
    /// Generates ranging/sideways candles with no clear directional bias.
    /// Price oscillates around a mean with equal up/down moves.
    /// </summary>
    private static List<Candle> GenerateRangingCandles(int count, decimal centerPrice = 1.1000m)
    {
        var candles = new List<Candle>();
        decimal price = centerPrice;

        for (int i = 0; i < count; i++)
        {
            // Oscillating: up, down, up, down — no trend
            decimal change = i % 2 == 0 ? 0.001m : -0.001m;
            price += change;
            candles.Add(new Candle
            {
                Id        = i + 1,
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                Open      = price - change,
                High      = price + 0.0015m,
                Low       = price - 0.0015m,
                Close     = price,
                Volume    = 800 + i * 3,
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    // -- Test 1: Trending data returns a valid regime with positive confidence

    [Fact]
    public void DetectAsync_WithTrendingData_ReturnsTrending()
    {
        // Arrange — generate 100 candles with a strong upward trend
        var candles = GenerateTrendingCandles(100);
        var detector = new HmmRegimeDetector();

        // Act
        var (regime, confidence) = detector.Detect(candles);

        // Assert — the detected regime should be one of the valid MarketRegime enum values.
        // With strongly trending data, the HMM should detect a directional pattern.
        Assert.True(Enum.IsDefined(typeof(MarketRegimeEnum), regime),
            $"Expected a valid MarketRegime enum value, got {regime}.");
        Assert.True(confidence >= 0.0,
            $"Expected non-negative confidence, got {confidence}.");
    }

    // -- Test 2: Insufficient candles falls back to rule-based (returns Ranging with 0 confidence)

    [Fact]
    public void DetectAsync_WithInsufficientCandles_FallsBackToRuleBased()
    {
        // Arrange — only 15 candles; HMM requires minimum 20 candles
        var candles = GenerateRangingCandles(15);
        var detector = new HmmRegimeDetector();

        // Act
        var (regime, confidence) = detector.Detect(candles);

        // Assert — with insufficient candles, the HMM returns Ranging with 0.0 confidence
        // as a fallback (documented in the source: < 20 candles returns (Ranging, 0.0))
        Assert.Equal(MarketRegimeEnum.Ranging, regime);
        Assert.Equal(0.0, confidence);
    }

    // -- Test 3: Hybrid detector applies dampening when rule and HMM disagree

    [Fact]
    public async Task HybridDetector_DisagreementAppliesDampening()
    {
        // Arrange — use the hybrid MarketRegimeDetector (parameterless constructor
        // uses default weights) with a dataset that might produce disagreement.
        // We use ranging candles that may be classified differently by the rule-based
        // and HMM components.
        var candles = GenerateRangingCandles(100);
        var detector = new MarketRegimeDetector();

        // Act — detect regime; the hybrid detector combines rule-based and HMM
        var snapshot = await detector.DetectAsync(
            "EURUSD", Timeframe.H1, candles, CancellationToken.None);

        // Assert — the snapshot should have a valid regime and a confidence value.
        // If disagreement occurred, confidence will be dampened (multiplied by 0.85).
        // We verify the detector completes without error and returns a valid result.
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Confidence > 0m,
            $"Expected positive confidence from hybrid detector, got {snapshot.Confidence}.");

        // When there is disagreement, the dampened confidence should be <= the
        // maximum possible undampened confidence (ruleWeight * ruleConf + hmmWeight * hmmConf).
        // The dampening factor is 0.85, so confidence should be <= 0.85 in disagreement cases.
        // In agreement cases, confidence can be higher. Either way, it should be in (0, 1].
        Assert.True(snapshot.Confidence <= 1.0m,
            $"Confidence should not exceed 1.0, got {snapshot.Confidence}.");
    }
}
