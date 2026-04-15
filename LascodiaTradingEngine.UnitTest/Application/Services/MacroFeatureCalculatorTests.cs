using LascodiaTradingEngine.Application.Services.ML;
using Xunit;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MacroFeatureCalculatorTests
{
    // SliceBasketAsOf is the only V2-specific helper that does non-trivial indexing
    // (binary search over a timestamp array). The pure math helpers are exercised
    // transitively by MacroFeatureProvider's existing code paths; this test class
    // focuses on the slice boundaries that an integration test would not catch.

    private static (DateTime[] Times, double[] Closes) MakeSeries(int count, DateTime start, TimeSpan step)
    {
        var times = new DateTime[count];
        var closes = new double[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = start + step * i;
            closes[i] = 1.0 + i * 0.001;
        }
        return (times, closes);
    }

    [Fact]
    public void SliceBasketAsOf_EmptyBasket_ReturnsEmptyDictionary()
    {
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>();
        var result = MacroFeatureCalculator.SliceBasketAsOf(full, DateTime.UtcNow);
        Assert.Empty(result);
    }

    [Fact]
    public void SliceBasketAsOf_AsOfBeforeFirstBar_SkipsSymbol()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = MakeSeries(10, start, TimeSpan.FromHours(1)),
        };

        var result = MacroFeatureCalculator.SliceBasketAsOf(full, start.AddHours(-1));

        // No bars at or before asOf → symbol omitted entirely.
        Assert.False(result.ContainsKey("EURUSD"));
    }

    [Fact]
    public void SliceBasketAsOf_AsOfAfterLastBar_ReturnsAllBarsUpToMaxBars()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = MakeSeries(50, start, TimeSpan.FromHours(1)),
        };

        var result = MacroFeatureCalculator.SliceBasketAsOf(full, start.AddYears(1), maxBars: 120);

        Assert.True(result.ContainsKey("EURUSD"));
        Assert.Equal(50, result["EURUSD"].Length);
        // Last value should match the last close of the original series.
        Assert.Equal(full["EURUSD"].Closes[^1], result["EURUSD"][^1]);
    }

    [Fact]
    public void SliceBasketAsOf_AsOfAfterLastBar_RespectsMaxBarsCap()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = MakeSeries(500, start, TimeSpan.FromHours(1)),
        };

        var result = MacroFeatureCalculator.SliceBasketAsOf(full, start.AddYears(1), maxBars: 120);

        Assert.Equal(120, result["EURUSD"].Length);
        // Tail should be preserved (sliced from the end, not the beginning).
        Assert.Equal(full["EURUSD"].Closes[^1], result["EURUSD"][^1]);
        Assert.Equal(full["EURUSD"].Closes[^120], result["EURUSD"][0]);
    }

    [Fact]
    public void SliceBasketAsOf_ExactTimestampMatch_IncludesMatchingBar()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = MakeSeries(10, start, TimeSpan.FromHours(1)),
        };

        // asOf lands exactly on the 5th bar (index 4).
        var asOf = start.AddHours(4);
        var result = MacroFeatureCalculator.SliceBasketAsOf(full, asOf, maxBars: 120);

        Assert.Equal(5, result["EURUSD"].Length);
        Assert.Equal(full["EURUSD"].Closes[4], result["EURUSD"][^1]);
    }

    [Fact]
    public void SliceBasketAsOf_AsOfBetweenBars_ExcludesFutureBar()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = MakeSeries(10, start, TimeSpan.FromHours(1)),
        };

        // asOf falls 30 minutes after the 5th bar, before the 6th.
        var asOf = start.AddHours(4).AddMinutes(30);
        var result = MacroFeatureCalculator.SliceBasketAsOf(full, asOf, maxBars: 120);

        // Should include bars 0..4 (five total). Future bar 5 must NOT appear.
        Assert.Equal(5, result["EURUSD"].Length);
        Assert.Equal(full["EURUSD"].Closes[4], result["EURUSD"][^1]);
    }

    [Fact]
    public void SliceBasketAsOf_MultipleSymbols_SlicedIndependently()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            // EURUSD has 10 bars, one per hour.
            ["EURUSD"] = MakeSeries(10, start, TimeSpan.FromHours(1)),
            // GBPUSD starts 5 hours later and has its own schedule.
            ["GBPUSD"] = MakeSeries(10, start.AddHours(5), TimeSpan.FromHours(1)),
        };

        // asOf lands 7 hours after start: EURUSD has 8 bars available (0..7),
        // GBPUSD has 3 available (starting from hour 5).
        var asOf = start.AddHours(7);
        var result = MacroFeatureCalculator.SliceBasketAsOf(full, asOf, maxBars: 120);

        Assert.Equal(8, result["EURUSD"].Length);
        Assert.Equal(3, result["GBPUSD"].Length);
    }

    [Fact]
    public void SliceBasketAsOf_SingleBarSeries_SlicesCorrectly()
    {
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var full = new Dictionary<string, (DateTime[] Times, double[] Closes)>
        {
            ["EURUSD"] = (new[] { start }, new[] { 1.23 }),
        };

        var result = MacroFeatureCalculator.SliceBasketAsOf(full, start.AddMinutes(1), maxBars: 120);

        Assert.Equal(1, result["EURUSD"].Length);
        Assert.Equal(1.23, result["EURUSD"][0]);
    }
}
