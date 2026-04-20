using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Strategies.Evaluators;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

/// <summary>
/// Pins the deterministic-replay invariant: given identical inputs, an evaluator
/// must produce identical <see cref="TradeSignal"/> outputs across repeated calls
/// and across parallel calls. Silent non-determinism — hidden RNG state, dictionary
/// iteration leaks, racy statics — causes live/backtest divergence that is very
/// expensive to catch later. A single representative evaluator is exercised here
/// because the concern is infrastructural, not per-strategy-type.
/// </summary>
public class EvaluatorDeterminismTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;

    public EvaluatorDeterminismTests()
    {
        _metrics = new TradingMetrics(_meterFactory);
    }

    public void Dispose() => _meterFactory.Dispose();

    [Fact]
    public async Task MaCrossover_SameInputs_ProducesIdenticalSignal_AcrossRepeatedCalls()
    {
        var evaluator = new MovingAverageCrossoverEvaluator(
            MaCrossoverCoreOptions(),
            NullLogger<MovingAverageCrossoverEvaluator>.Instance,
            _metrics);

        var strategy = CreateStrategy();
        var candles = GenerateCandles(BullishCrossoverPrices());
        var price = (bid: 1.2000m, ask: 1.2002m);

        var first  = await evaluator.EvaluateAsync(strategy, candles, price, CancellationToken.None);
        var second = await evaluator.EvaluateAsync(strategy, candles, price, CancellationToken.None);
        var third  = await evaluator.EvaluateAsync(strategy, candles, price, CancellationToken.None);

        Assert.NotNull(first);
        AssertSignalsEqual(first, second);
        AssertSignalsEqual(first, third);
    }

    [Fact]
    public async Task MaCrossover_SameInputs_ProducesIdenticalSignal_UnderParallelInvocation()
    {
        // The parallel evaluator loop in StrategyWorker runs ForEachAsync across
        // strategies. If the evaluator has hidden shared state — a static RNG, a
        // field mutation, a non-thread-safe indicator buffer — two concurrent
        // evaluations on the same candle series could produce different signals.
        // This test exercises that path explicitly.
        var evaluator = new MovingAverageCrossoverEvaluator(
            MaCrossoverCoreOptions(),
            NullLogger<MovingAverageCrossoverEvaluator>.Instance,
            _metrics);

        var strategy = CreateStrategy();
        var candles = GenerateCandles(BullishCrossoverPrices());
        var price = (bid: 1.2000m, ask: 1.2002m);

        const int parallelCount = 16;
        var results = await Task.WhenAll(Enumerable.Range(0, parallelCount)
            .Select(_ => evaluator.EvaluateAsync(strategy, candles, price, CancellationToken.None)));

        var reference = results[0];
        Assert.NotNull(reference);
        foreach (var r in results)
            AssertSignalsEqual(reference, r);
    }

    [Fact]
    public async Task MaCrossover_DisjointStrategyInstances_ProduceIdenticalSignal_ForSameParameters()
    {
        // Replaying a strategy config from snapshot should produce the same signal
        // as the original instance. Guards against state leaking via the entity
        // identity rather than just the ParametersJson — e.g. an evaluator that
        // internally keys a cache off strategy.Id would silently bifurcate
        // behaviour when the same params were re-persisted under a new Id.
        var evaluator = new MovingAverageCrossoverEvaluator(
            MaCrossoverCoreOptions(),
            NullLogger<MovingAverageCrossoverEvaluator>.Instance,
            _metrics);

        var strategyA = CreateStrategy(id: 1);
        var strategyB = CreateStrategy(id: 9999);

        var candles = GenerateCandles(BullishCrossoverPrices());
        var price = (bid: 1.2000m, ask: 1.2002m);

        var a = await evaluator.EvaluateAsync(strategyA, candles, price, CancellationToken.None);
        var b = await evaluator.EvaluateAsync(strategyB, candles, price, CancellationToken.None);

        Assert.NotNull(a);
        Assert.NotNull(b);
        // StrategyId on the signal intentionally differs by design — everything else must match.
        Assert.Equal(a!.Direction,       b!.Direction);
        Assert.Equal(a.EntryPrice,       b.EntryPrice);
        Assert.Equal(a.StopLoss,         b.StopLoss);
        Assert.Equal(a.TakeProfit,       b.TakeProfit);
        Assert.Equal(a.SuggestedLotSize, b.SuggestedLotSize);
        Assert.Equal(a.Confidence,       b.Confidence);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertSignalsEqual(TradeSignal? expected, TradeSignal? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Direction,        actual!.Direction);
        Assert.Equal(expected.EntryPrice,        actual.EntryPrice);
        Assert.Equal(expected.StopLoss,          actual.StopLoss);
        Assert.Equal(expected.TakeProfit,        actual.TakeProfit);
        Assert.Equal(expected.SuggestedLotSize,  actual.SuggestedLotSize);
        Assert.Equal(expected.Confidence,        actual.Confidence);
        Assert.Equal(expected.StrategyId,        actual.StrategyId);
        Assert.Equal(expected.Symbol,            actual.Symbol);
    }

    private static StrategyEvaluatorOptions MaCrossoverCoreOptions() => new()
    {
        // Mirror the disable-all-filters config used by the core evaluator tests
        // so the evaluator actually produces a signal for our simple price series.
        // The determinism check only cares that output is stable given the same
        // inputs, not that every filter is exercised.
        MaCrossoverMinAdx                      = 0,
        MaCrossoverMaxRecentCrossovers         = 0,
        MaCrossoverMinMagnitudeAtrFraction     = 0,
        MaCrossoverMaxSpreadAtrFraction        = 0,
        MaCrossoverMinVolume                   = 0,
        MaCrossoverMaxGapAtrFraction           = 0,
        MaCrossoverDeadbandAtrFraction         = 0,
        MaCrossoverMinRiskRewardRatio          = 0,
        MaCrossoverMaxRsiForBuy                = 0,
        MaCrossoverMinRsiForSell               = 0,
        MaCrossoverSwingSlEnabled              = false,
        MaCrossoverSwingTpEnabled              = false,
        MaCrossoverConfirmationBars            = 0,
        MaCrossoverDynamicSlTp                 = false,
        MaCrossoverSlippageAtrFraction         = 0,
    };

    private static Strategy CreateStrategy(long id = 1) => new()
    {
        Id             = id,
        Name           = "MA Test",
        StrategyType   = StrategyType.MovingAverageCrossover,
        Symbol         = "EURUSD",
        Timeframe      = Timeframe.H1,
        Status         = StrategyStatus.Active,
        ParametersJson = "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}",
    };

    private static decimal[] BullishCrossoverPrices() => new decimal[]
    {
        1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
        1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
    };

    private static List<Candle> GenerateCandles(decimal[] closes)
    {
        var now = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
        return closes.Select((close, i) => new Candle
        {
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Timestamp = now.AddHours(-closes.Length + i),
            Open      = close,
            High      = close + 0.0005m,
            Low       = close - 0.0005m,
            Close     = close,
            Volume    = 1000,
            IsClosed  = true,
        }).ToList();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = new();
        public Meter Create(MeterOptions options) { var m = new Meter(options); _meters.Add(m); return m; }
        public void Dispose() { foreach (var m in _meters) m.Dispose(); }
    }
}
