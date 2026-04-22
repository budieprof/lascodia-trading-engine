using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcSequencePreparationServiceTest
{
    [Fact]
    public void BuildSequences_Splits_At_Time_Gaps()
    {
        var candles = new List<Candle>();
        candles.AddRange(BuildRun(DateTime.UtcNow.AddDays(-3), idStart: 1, count: 30, startPrice: 1.10m));
        candles.AddRange(BuildRun(DateTime.UtcNow.AddDays(-3).AddHours(40), idStart: 100, count: 30, startPrice: 2.20m));
        var service = new CpcSequencePreparationService();

        var sequences = service.BuildSequences(candles, sequenceLength: 10, sequenceStride: 1, maxSequences: 100);

        Assert.NotEmpty(sequences);
        Assert.DoesNotContain(sequences.SelectMany(s => s), row =>
            row.Length > 3 && Math.Abs(row[3]) > 0.20f);
    }

    [Fact]
    public void BuildSequences_Honours_MaxSequences_Across_Runs()
    {
        var candles = new List<Candle>();
        candles.AddRange(BuildRun(DateTime.UtcNow.AddDays(-3), idStart: 1, count: 30, startPrice: 1.10m));
        candles.AddRange(BuildRun(DateTime.UtcNow.AddDays(-1), idStart: 100, count: 30, startPrice: 1.20m));
        var service = new CpcSequencePreparationService();

        var sequences = service.BuildSequences(candles, sequenceLength: 10, sequenceStride: 1, maxSequences: 5);

        Assert.Equal(5, sequences.Count);
    }

    private static IEnumerable<Candle> BuildRun(DateTime start, int idStart, int count, decimal startPrice)
    {
        decimal price = startPrice;
        for (int i = 0; i < count; i++)
        {
            decimal close = price + 0.0001m;
            yield return new Candle
            {
                Id = idStart + i,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = start.AddHours(i),
                Open = price,
                High = close + 0.0001m,
                Low = price - 0.0001m,
                Close = close,
                Volume = 1000m + i,
                IsClosed = true
            };
            price = close;
        }
    }
}
