using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class MLCpcSequenceBuilderTest
{
    [Fact]
    public void Build_Returns_Empty_When_Too_Few_Candles()
    {
        var candles = CreateCandles(count: 10);
        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 1, maxSequences: 100);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_Returns_Empty_When_SeqLen_Or_Stride_Or_Max_Are_Invalid()
    {
        var candles = CreateCandles(count: 120);
        Assert.Empty(MLCpcSequenceBuilder.Build(candles, seqLen: 1, stride: 1, maxSequences: 10));
        Assert.Empty(MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 0, maxSequences: 10));
        Assert.Empty(MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 1, maxSequences: 0));
    }

    [Fact]
    public void Build_Produces_Sequences_With_Correct_Shape()
    {
        var candles = CreateCandles(count: 200);
        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 16, maxSequences: 100);
        Assert.NotEmpty(result);
        foreach (var seq in result)
        {
            Assert.Equal(60, seq.Length);
            foreach (var row in seq)
            {
                Assert.Equal(MLCpcSequenceBuilder.FeaturesPerStep, row.Length);
                foreach (var v in row)
                    Assert.True(float.IsFinite(v));
            }
        }
    }

    [Fact]
    public void Build_Skips_Unclosed_Candles()
    {
        var candles = CreateCandles(count: 200);
        // Mark every 10th candle as not closed — builder should drop them before sequencing.
        for (int i = 0; i < candles.Count; i += 10) candles[i].IsClosed = false;

        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 16, maxSequences: 100);
        // It should still produce at least one sequence despite the drops (180 usable candles).
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Build_Respects_MaxSequences()
    {
        var candles = CreateCandles(count: 1000);
        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 60, stride: 1, maxSequences: 5);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Build_Filters_NonFinite_OHLCV_Rows()
    {
        var candles = CreateCandles(count: 120);
        candles[50].Close = 0m;   // drops candle 50 (non-positive close)
        candles[60].Open  = 0m;   // drops candle 60

        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 30, stride: 5, maxSequences: 100);
        // Windows either produce finite rows or are dropped — never emit Inf/NaN.
        foreach (var seq in result)
            foreach (var row in seq)
                foreach (var v in row)
                    Assert.True(float.IsFinite(v));
    }

    [Fact]
    public void Build_First_Row_Log_Returns_Reference_Prior_Candle_Close()
    {
        var candles = CreateCandles(count: 100);
        // Force deterministic progression so we can check log-return math.
        for (int i = 0; i < candles.Count; i++)
        {
            candles[i].Open  = 100m + i * 0.1m;
            candles[i].High  = 100m + i * 0.1m + 0.05m;
            candles[i].Low   = 100m + i * 0.1m - 0.05m;
            candles[i].Close = 100m + i * 0.1m + 0.02m;
            candles[i].Volume = 1000m;
        }

        var result = MLCpcSequenceBuilder.Build(candles, seqLen: 10, stride: 1, maxSequences: 1);
        Assert.Single(result);
        var firstRow = result[0][0];
        // prevClose = candles[0].Close, current = candles[1].
        double expectedLogRetOpen = Math.Log((double)candles[1].Open / (double)candles[0].Close);
        Assert.Equal((float)expectedLogRetOpen, firstRow[0], precision: 5);
    }

    private static List<Candle> CreateCandles(int count)
    {
        var list = new List<Candle>(count);
        decimal price = 1.1000m;
        var rng = new Random(17);
        for (int i = 0; i < count; i++)
        {
            decimal delta = (decimal)((rng.NextDouble() - 0.5) * 0.002);
            decimal open = price;
            decimal close = price + delta;
            decimal high = Math.Max(open, close) + 0.0001m;
            decimal low  = Math.Min(open, close) - 0.0001m;
            list.Add(new Candle
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = 1000m + i,
                IsClosed = true
            });
            price = close;
        }
        return list;
    }
}
