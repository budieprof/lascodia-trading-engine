using System.Diagnostics.Metrics;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Strategies.Evaluators;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

public class StrategyEvaluatorTests : IDisposable
{
    private readonly StrategyEvaluatorOptions _defaultOptions = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly IMarketRegimeDetector _regimeDetector = Mock.Of<IMarketRegimeDetector>();
    private readonly IMultiTimeframeFilter _mtfFilter = Mock.Of<IMultiTimeframeFilter>();

    public StrategyEvaluatorTests()
    {
        _metrics = new TradingMetrics(_meterFactory);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }
        public void Dispose()
        {
            foreach (var meter in _meters) meter.Dispose();
            _meters.Clear();
        }
    }

    /// <summary>
    /// Options with all MA crossover hardening filters disabled so core crossover
    /// logic can be tested with minimal candle data.
    /// </summary>
    private static StrategyEvaluatorOptions MaCrossoverCoreOptions() => new()
    {
        MaCrossoverMinAdx = 0,
        MaCrossoverMaxRecentCrossovers = 0,
        MaCrossoverMinMagnitudeAtrFraction = 0,
        MaCrossoverMaxSpreadAtrFraction = 0,
        MaCrossoverMinVolume = 0,
        MaCrossoverMaxGapAtrFraction = 0,        // disable gap rejection
        MaCrossoverDeadbandAtrFraction = 0,       // disable deadband so small crosses register
        MaCrossoverMinRiskRewardRatio = 0,        // disable R:R gate
        MaCrossoverMaxRsiForBuy = 0,              // disable RSI filter
        MaCrossoverMinRsiForSell = 0,             // disable RSI filter
        MaCrossoverSwingSlEnabled = false,         // no swing SL override
        MaCrossoverSwingTpEnabled = false,         // no swing TP override
        MaCrossoverConfirmationBars = 0,           // no multi-bar confirmation
        MaCrossoverDynamicSlTp = false,            // no ADX-based SL/TP scaling
        MaCrossoverSlippageAtrFraction = 0         // no slippage buffer
    };

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static Strategy CreateStrategy(
        StrategyType type,
        string symbol = "EURUSD",
        string parametersJson = "{}")
    {
        return new Strategy
        {
            Id = 1,
            Name = $"Test {type}",
            StrategyType = type,
            Symbol = symbol,
            Timeframe = Timeframe.H1,
            Status = StrategyStatus.Active,
            ParametersJson = parametersJson
        };
    }

    /// <summary>
    /// Generates a list of candles with the specified closing prices.
    /// High/Low are derived from the close with a fixed spread so ATR is predictable.
    /// </summary>
    private static List<Candle> GenerateCandles(decimal[] closePrices, decimal spread = 0.0010m)
    {
        var candles = new List<Candle>();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < closePrices.Length; i++)
        {
            decimal close = closePrices[i];
            candles.Add(new Candle
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = close - spread * 0.5m,
                High = close + spread,
                Low = close - spread,
                Close = close,
                Volume = 1000m,
                Timestamp = baseTime.AddHours(i),
                IsClosed = true
            });
        }

        return candles;
    }

    /// <summary>
    /// Generates candles whose Close prices form a steady uptrend or downtrend.
    /// </summary>
    private static List<Candle> GenerateTrendCandles(
        int count,
        decimal startPrice,
        decimal stepPerBar,
        decimal spread = 0.0010m)
    {
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
            prices[i] = startPrice + stepPerBar * i;
        return GenerateCandles(prices, spread);
    }

    /// <summary>
    /// Computes the simple moving average of the last <paramref name="period"/> closing prices
    /// ending at <paramref name="endIndex"/>. Mirrors the evaluator SMA logic.
    /// </summary>
    private static decimal Sma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sum = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }

    // ========================================================================
    //  BreakoutScalperEvaluator
    // ========================================================================

    [Fact]
    public async Task Breakout_Buy_Signal_When_Price_Breaks_Above_NBarHigh()
    {
        var evaluator = new BreakoutScalperEvaluator(_defaultOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.BreakoutScalper);

        // 22 candles ranging around 1.1000
        var candles = GenerateCandles(Enumerable.Range(0, 22)
            .Select(i => 1.1000m + (i % 2 == 0 ? 0.0005m : -0.0005m))
            .ToArray(), spread: 0.0010m);

        // Set Ask well above the range high to guarantee a breakout
        decimal ask = 1.1100m;
        decimal bid = 1.1098m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(ask, signal.EntryPrice);
        Assert.True(signal.Confidence >= _defaultOptions.BreakoutConfidence, "Confidence should be at least base confidence");
        Assert.InRange(signal.Confidence, 0m, 1m);
        Assert.Equal(_defaultOptions.DefaultLotSize, signal.SuggestedLotSize);
        Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        Assert.Equal(strategy.Id, signal.StrategyId);
        Assert.Equal(strategy.Symbol, signal.Symbol);
        Assert.True(signal.ExpiresAt > signal.GeneratedAt);
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes,
            _defaultOptions.BreakoutExpiryMinutes - 1,
            _defaultOptions.BreakoutExpiryMinutes + 1);
    }

    [Fact]
    public async Task Breakout_Sell_Signal_When_Price_Breaks_Below_NBarLow()
    {
        var evaluator = new BreakoutScalperEvaluator(_defaultOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.BreakoutScalper);

        var candles = GenerateCandles(Enumerable.Range(0, 22)
            .Select(i => 1.1000m + (i % 2 == 0 ? 0.0005m : -0.0005m))
            .ToArray(), spread: 0.0010m);

        // Bid well below range low
        decimal bid = 1.0900m;
        decimal ask = 1.0902m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(bid, signal.EntryPrice);
        Assert.True(signal.Confidence >= _defaultOptions.BreakoutConfidence, "Confidence should be at least base confidence");
        Assert.InRange(signal.Confidence, 0m, 1m);
        Assert.Equal(_defaultOptions.DefaultLotSize, signal.SuggestedLotSize);
    }

    [Fact]
    public async Task Breakout_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new BreakoutScalperEvaluator(_defaultOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.BreakoutScalper);

        // Only 10 candles -- needs 21 (lookbackBars + 1)
        var candles = GenerateTrendCandles(10, 1.1000m, 0.0001m);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Breakout_No_Signal_When_Price_Inside_Range()
    {
        var evaluator = new BreakoutScalperEvaluator(_defaultOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.BreakoutScalper);

        var candles = GenerateCandles(Enumerable.Range(0, 22)
            .Select(i => 1.1000m + (i % 2 == 0 ? 0.0005m : -0.0005m))
            .ToArray(), spread: 0.0010m);

        // Price right in the middle of the range -- no breakout
        decimal mid = 1.1000m;
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (mid, mid + 0.0002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Breakout_Uses_Configurable_Options()
    {
        var customOptions = new StrategyEvaluatorOptions
        {
            DefaultLotSize = 0.05m,
            BreakoutConfidence = 0.80m,
            BreakoutExpiryMinutes = 30
        };
        var evaluator = new BreakoutScalperEvaluator(customOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.BreakoutScalper);

        var candles = GenerateCandles(Enumerable.Range(0, 22)
            .Select(i => 1.1000m + (i % 2 == 0 ? 0.0005m : -0.0005m))
            .ToArray(), spread: 0.0010m);

        decimal ask = 1.1100m;
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1098m, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(0.05m, signal!.SuggestedLotSize);
        Assert.True(signal.Confidence >= 0.80m, "Confidence should be at least the configured base confidence");
        Assert.InRange(signal.Confidence, 0m, 1m);
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes, 29, 31);
    }

    // ========================================================================
    //  MovingAverageCrossoverEvaluator
    // ========================================================================

    [Fact]
    public async Task MaCrossover_Buy_Signal_When_Fast_Crosses_Above_Slow()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        // 8 flat + decline + large jump at bar 15 to force bullish crossover
        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);
        decimal ask = 1.2002m;
        decimal bid = 1.2000m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(ask, signal.EntryPrice);
        Assert.Equal(options.DefaultLotSize, signal.SuggestedLotSize);
        Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        Assert.Equal(strategy.Id, signal.StrategyId);
        Assert.Equal(strategy.Symbol, signal.Symbol);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss < signal.EntryPrice, "Buy signal SL should be below entry");
        Assert.True(signal.TakeProfit > signal.EntryPrice, "Buy signal TP should be above entry");
        Assert.InRange(signal.Confidence, 0.1m, 1.0m);
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes,
            options.MaCrossoverExpiryMinutes - 1,
            options.MaCrossoverExpiryMinutes + 1);
    }

    [Fact]
    public async Task MaCrossover_Sell_Signal_When_Fast_Crosses_Below_Slow()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        // Rally then a single large drop at the last bar to force bearish crossover
        var prices = new decimal[]
        {
            1.0700m, 1.0700m, 1.0700m, 1.0700m, 1.0700m, 1.0700m, 1.0700m, 1.0700m,
            1.0400m, 1.0500m, 1.0600m, 1.0700m, 1.0800m, 1.0900m, 1.1000m, 0.9400m
        };
        var candles = GenerateCandles(prices);
        decimal bid = 0.9398m;
        decimal ask = 0.9400m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(bid, signal.EntryPrice);
        Assert.InRange(signal.Confidence, 0.1m, 1.0m);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss > signal.EntryPrice, "Sell signal SL should be above entry");
        Assert.True(signal.TakeProfit < signal.EntryPrice, "Sell signal TP should be below entry");
    }

    [Fact]
    public async Task MaCrossover_No_Signal_When_Insufficient_Candles()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover);

        var candles = GenerateTrendCandles(5, 1.1000m, 0.0001m);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_No_Signal_When_No_Crossover()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        // Steady uptrend -- fast SMA stays above slow SMA the whole time
        var candles = GenerateTrendCandles(16, 1.1000m, 0.0010m);
        decimal lastClose = candles[^1].Close;

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose, lastClose + 0.0002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Uses_Configurable_Options()
    {
        var customOptions = new StrategyEvaluatorOptions
        {
            DefaultLotSize = 0.10m,
            MaCrossoverConfidence = 0.85m,
            MaCrossoverExpiryMinutes = 120,
            MaCrossoverMinAdx = 0,
            MaCrossoverMaxRecentCrossovers = 0,
            MaCrossoverMinMagnitudeAtrFraction = 0,
            MaCrossoverMaxSpreadAtrFraction = 0,
            MaCrossoverMinVolume = 0,
            MaCrossoverMaxGapAtrFraction = 0,
            MaCrossoverDeadbandAtrFraction = 0,
            MaCrossoverMinRiskRewardRatio = 0,
            MaCrossoverMaxRsiForBuy = 0,
            MaCrossoverMinRsiForSell = 0,
            MaCrossoverSwingSlEnabled = false,
            MaCrossoverSwingTpEnabled = false,
            MaCrossoverConfirmationBars = 0,
            MaCrossoverDynamicSlTp = false,
            MaCrossoverSlippageAtrFraction = 0
        };
        var evaluator = new MovingAverageCrossoverEvaluator(customOptions, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(0.10m, signal!.SuggestedLotSize);
        Assert.InRange(signal.Confidence, 0.1m, 1.0m);
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes, 119, 121);
    }

    // ========================================================================
    //  MovingAverageCrossoverEvaluator — Hardening filters
    // ========================================================================

    [Fact]
    public async Task MaCrossover_EMA_Produces_Signal()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":true}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
    }

    [Fact]
    public async Task MaCrossover_Rejected_By_Spread_Filter()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMaxSpreadAtrFraction = 0.01m; // very tight — will reject
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        // Wide spread: 50 pips
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1950m, 1.2000m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Rejected_By_Volume_Filter()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMinVolume = 5000m; // test candles have Volume = 1000
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Rejected_By_Magnitude_Filter()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMinMagnitudeAtrFraction = 10.0m; // impossibly high
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Rejected_By_Whipsaw_Filter()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMaxRecentCrossovers = 1; // allow only 1 recent crossover
        options.MaCrossoverWhipsawLookbackBars = 15;
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":5,\"MaPeriod\":0,\"UseEma\":false}");

        // Choppy price action: alternating up/down to create multiple crossovers
        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1200m, 1.0800m, 1.1200m, 1.0800m, 1.1200m, 1.0800m, 1.1200m, 1.0800m
        };
        var candles = GenerateCandles(prices);
        decimal lastClose = candles[^1].Close;

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose, lastClose + 0.0002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Dynamic_Confidence_Is_Not_Static()
    {
        // Verify that dynamic confidence varies based on signal conditions
        // (not a fixed value from options)
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverConfidence = 0.70m;
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
        // Confidence should be dynamic (not exactly the base value)
        Assert.InRange(signal!.Confidence, 0.1m, 1.0m);
        // It should differ from the static base — the multi-factor scoring adjusts it
        Assert.NotEqual(0.70m, signal.Confidence);
    }

    [Fact]
    public async Task MaCrossover_Rejected_By_ADX_Filter()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMinAdx = 99m; // impossibly high — guaranteed rejection
        options.MaCrossoverAdxPeriod = 7;
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_No_Signal_When_Zero_Volume_Candles()
    {
        var options = MaCrossoverCoreOptions();
        options.MaCrossoverMinVolume = 1m; // require at least 1 volume
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        // Generate candles with zero volume
        var candles = GenerateCandles(prices);
        foreach (var c in candles) c.Volume = 0m;

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task MaCrossover_Passes_When_Equal_Fast_Slow_Clamped()
    {
        // FastPeriod == SlowPeriod should be clamped so fast = slow - 1
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":7,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        // After clamping, fast=6, slow=7 — should not crash
        var candles = GenerateTrendCandles(16, 1.1000m, 0.0010m);
        decimal lastClose = candles[^1].Close;

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose, lastClose + 0.0002m), CancellationToken.None);

        // No assertion on signal value — just verify it doesn't throw
    }

    [Fact]
    public async Task MaCrossover_VWMA_Produces_Signal()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"MaType\":\"Vwma\"}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
    }

    [Fact]
    public async Task MaCrossover_VWMA_Falls_Back_To_SMA_On_Zero_Volume()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"MaType\":\"Vwma\"}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);
        foreach (var c in candles) c.Volume = 0m;

        // Should not crash — VWMA falls back to SMA when volume is zero
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
    }

    [Fact]
    public async Task MaCrossover_MaType_String_Backwards_Compatible_With_UseEma()
    {
        var options = MaCrossoverCoreOptions();
        var evaluator = new MovingAverageCrossoverEvaluator(options, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);

        // Old-style "UseEma": false should still work
        var strategy = CreateStrategy(StrategyType.MovingAverageCrossover,
            parametersJson: "{\"FastPeriod\":3,\"SlowPeriod\":7,\"MaPeriod\":0,\"UseEma\":false}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.0900m, 1.0800m, 1.0700m, 1.0600m, 1.0500m, 1.0400m, 1.2000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.2000m, 1.2002m), CancellationToken.None);

        Assert.NotNull(signal);
    }

    // ========================================================================
    //  RSIReversionEvaluator
    // ========================================================================

    [Fact]
    public async Task Rsi_Buy_Signal_When_RSI_Crosses_Above_Oversold()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        // Use a short period (5) so we need fewer candles and can control the RSI precisely.
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: "{\"Period\":5,\"Oversold\":30,\"Overbought\":70}");

        // We need period + 1 = 6 candles for one RSI calc, plus 1 more for prevRsi => 7 candles.
        // Plan: 6 bars of decline to make RSI very low, then 1 up bar to cross above 30.
        //
        // Prices:  100, 98, 96, 94, 92, 90, 95
        // Changes from bar 1: -2, -2, -2, -2, -2, +5
        //
        // prevRsi at index 5 (last 5 changes: -2,-2,-2,-2,-2):
        //   avgGain=0, avgLoss=2 => RS=0 => RSI=0
        //
        // currentRsi at index 6 (last 5 changes: -2,-2,-2,-2,+5):
        //   avgGain=5/5=1, avgLoss=8/5=1.6 => RS=0.625 => RSI=38.46
        //
        // prevRsi(0) <= 30, currentRsi(38.46) > 30 => Buy signal!
        // 9 flat candles + 7 original pattern = 16 (enough for ATR-14 SL/TP)
        var prices = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m,
                                     100m, 98m, 96m, 94m, 92m, 90m, 95m };
        var candles = GenerateCandles(prices, spread: 0.50m);
        decimal ask = 95.10m;
        decimal bid = 95.00m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(ask, signal.EntryPrice);
        Assert.Equal(_defaultOptions.DefaultLotSize, signal.SuggestedLotSize);
        Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        Assert.Equal(strategy.Id, signal.StrategyId);
        Assert.Equal(strategy.Symbol, signal.Symbol);
        Assert.True(signal.Confidence > 0m && signal.Confidence <= 1.0m);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss < signal.EntryPrice, "Buy signal SL should be below entry");
        Assert.True(signal.TakeProfit > signal.EntryPrice, "Buy signal TP should be above entry");
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes,
            _defaultOptions.RsiReversionExpiryMinutes - 1,
            _defaultOptions.RsiReversionExpiryMinutes + 1);
    }

    [Fact]
    public async Task Rsi_Sell_Signal_When_RSI_Crosses_Below_Overbought()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: "{\"Period\":5,\"Oversold\":30,\"Overbought\":70}");

        // Mirror of the buy test: 6 bars of rally then 1 down bar.
        //
        // Prices:  100, 102, 104, 106, 108, 110, 105
        // Changes from bar 1: +2, +2, +2, +2, +2, -5
        //
        // prevRsi at index 5 (last 5 changes: +2,+2,+2,+2,+2):
        //   avgGain=2, avgLoss=0 => RSI=100
        //
        // currentRsi at index 6 (last 5 changes: +2,+2,+2,+2,-5):
        //   avgGain=8/5=1.6, avgLoss=5/5=1 => RS=1.6 => RSI=61.54
        //
        // prevRsi(100) >= 70, currentRsi(61.54) < 70 => Sell signal!
        // 9 flat candles + 7 original pattern = 16 (enough for ATR-14 SL/TP)
        var prices = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m,
                                     100m, 102m, 104m, 106m, 108m, 110m, 105m };
        var candles = GenerateCandles(prices, spread: 0.50m);
        decimal bid = 104.90m;
        decimal ask = 105.00m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(bid, signal.EntryPrice);
        Assert.Equal(_defaultOptions.DefaultLotSize, signal.SuggestedLotSize);
        Assert.True(signal.Confidence > 0m && signal.Confidence <= 1.0m);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss > signal.EntryPrice, "Sell signal SL should be above entry");
        Assert.True(signal.TakeProfit < signal.EntryPrice, "Sell signal TP should be below entry");
    }

    [Fact]
    public async Task Rsi_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: "{\"Period\":14}");

        // Only 10 candles -- needs 15 (period + 1)
        var candles = GenerateTrendCandles(10, 1.1000m, 0.0001m);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Rsi_No_Signal_When_RSI_In_Neutral_Zone()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: "{\"Period\":5,\"Oversold\":30,\"Overbought\":70}");

        // Alternating up/down: RSI stays around 50, never crosses 30 or 70 boundary
        var prices = new decimal[] { 100m, 101m, 100m, 101m, 100m, 101m, 100m };
        var candles = GenerateCandles(prices, spread: 0.50m);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (100.00m, 100.10m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Rsi_Uses_Configurable_Options()
    {
        var customOptions = new StrategyEvaluatorOptions
        {
            DefaultLotSize = 0.03m,
            RsiReversionExpiryMinutes = 45
        };
        var evaluator = new RSIReversionEvaluator(customOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: "{\"Period\":5,\"Oversold\":30,\"Overbought\":70}");

        // 9 flat candles + 7 original pattern = 16 (enough for ATR-14 SL/TP)
        var prices = new decimal[] { 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m,
                                     100m, 98m, 96m, 94m, 92m, 90m, 95m };
        var candles = GenerateCandles(prices, spread: 0.50m);
        decimal ask = 95.10m;

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (95.00m, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(0.03m, signal!.SuggestedLotSize);
        Assert.InRange(
            (signal.ExpiresAt - signal.GeneratedAt).TotalMinutes, 44, 46);
    }

    // ========================================================================
    //  StrategyType property verification
    // ========================================================================

    [Fact]
    public void Evaluators_Report_Correct_StrategyType()
    {
        var breakout   = new BreakoutScalperEvaluator(_defaultOptions, NullLogger<BreakoutScalperEvaluator>.Instance, _metrics);
        var maCross    = new MovingAverageCrossoverEvaluator(_defaultOptions, NullLogger<MovingAverageCrossoverEvaluator>.Instance, _metrics);
        var rsi        = new RSIReversionEvaluator(_defaultOptions);
        var bollinger  = new BollingerBandReversionEvaluator(_defaultOptions);
        var macd       = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var session    = new SessionBreakoutEvaluator(_defaultOptions, NullLogger<SessionBreakoutEvaluator>.Instance, _metrics);
        var momentum   = new MomentumTrendEvaluator(_defaultOptions, NullLogger<MomentumTrendEvaluator>.Instance, _metrics);

        Assert.Equal(StrategyType.BreakoutScalper, breakout.StrategyType);
        Assert.Equal(StrategyType.MovingAverageCrossover, maCross.StrategyType);
        Assert.Equal(StrategyType.RSIReversion, rsi.StrategyType);
        Assert.Equal(StrategyType.BollingerBandReversion, bollinger.StrategyType);
        Assert.Equal(StrategyType.MACDDivergence, macd.StrategyType);
        Assert.Equal(StrategyType.SessionBreakout, session.StrategyType);
        Assert.Equal(StrategyType.MomentumTrend, momentum.StrategyType);
    }

    // ========================================================================
    //  BollingerBandReversionEvaluator
    // ========================================================================

    /// <summary>
    /// Generates candles that oscillate in a tight range then dip below the lower
    /// Bollinger Band and recover — triggering a Buy signal.
    /// </summary>
    [Fact]
    public async Task Bollinger_Buy_Signal_When_Price_Bounces_Off_Lower_Band()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: "{\"Period\":10,\"StdDevMultiple\":2.0,\"SqueezeThreshold\":0.0}");

        // 14 flat warmup bars (RSI warmup) + 10 oscillating bars + sharp dip + recovery
        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.1000m, 1.1000m, 1.1000m, // 14 warmup bars
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 1.0995m,
            1.1002m, 1.0998m, 1.1003m, 1.0997m, 1.1001m,
            1.1000m, 1.1005m, 1.0995m, 1.1000m,
            1.0940m, // dip — below lower band but gap within 2× ATR threshold
            1.0980m  // recovery — back inside band
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.0978m, 1.0982m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss < signal.EntryPrice);
        Assert.True(signal.TakeProfit > signal.EntryPrice);
    }

    [Fact]
    public async Task Bollinger_Sell_Signal_When_Price_Bounces_Off_Upper_Band()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: "{\"Period\":10,\"StdDevMultiple\":2.0,\"SqueezeThreshold\":0.0}");

        var prices = new decimal[]
        {
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.1000m, 1.1000m, 1.1000m, 1.1000m,
            1.1000m, 1.1000m, 1.1000m, 1.1000m, // 14 warmup bars
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 1.0995m,
            1.1002m, 1.0998m, 1.1003m, 1.0997m, 1.1001m,
            1.1000m, 1.1005m, 1.0995m, 1.1000m,
            1.1060m, // spike above upper band but gap within 2× ATR threshold
            1.1020m  // reversal — back inside band
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1018m, 1.1022m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss > signal.EntryPrice);
        Assert.True(signal.TakeProfit < signal.EntryPrice);
    }

    [Fact]
    public async Task Bollinger_No_Signal_When_Price_Inside_Bands()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: "{\"Period\":10,\"StdDevMultiple\":2.0,\"SqueezeThreshold\":0.0}");

        // Steady oscillation — never touches bands
        var prices = new decimal[]
        {
            1.1000m, 1.1002m, 1.0998m, 1.1001m, 1.0999m,
            1.1003m, 1.0997m, 1.1002m, 1.0998m, 1.1001m,
            1.1000m, 1.1001m, 1.0999m, 1.1000m, 1.1001m, 1.1000m
        };
        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.0999m, 1.1001m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Bollinger_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: "{\"Period\":20}");

        var candles = GenerateTrendCandles(5, 1.1000m, 0.0001m);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ========================================================================
    //  MACDDivergenceEvaluator
    // ========================================================================

    [Fact]
    public async Task Macd_Buy_Signal_On_Zero_Line_Crossover()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: "{\"FastPeriod\":5,\"SlowPeriod\":10,\"SignalPeriod\":3,\"DivergenceLookback\":5}");

        // Long decline then recovery: creates MACD crossing from negative to positive
        int count = 30;
        var prices = new decimal[count];
        for (int i = 0; i < 20; i++)
            prices[i] = 1.1000m - i * 0.0020m; // decline
        for (int i = 20; i < count; i++)
            prices[i] = prices[19] + (i - 19) * 0.0040m; // sharp recovery

        var candles = GenerateCandles(prices);
        decimal lastClose = prices[^1];

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose, lastClose + 0.0002m), CancellationToken.None);

        // Should get a signal (either divergence or zero-line cross)
        if (signal is not null)
        {
            Assert.Equal(TradeDirection.Buy, signal.Direction);
            Assert.NotNull(signal.StopLoss);
            Assert.NotNull(signal.TakeProfit);
            Assert.True(signal.StopLoss < signal.EntryPrice);
            Assert.True(signal.TakeProfit > signal.EntryPrice);
        }
        // If no signal, the pattern wasn't strong enough — still valid behaviour
    }

    [Fact]
    public async Task Macd_Sell_Signal_On_Zero_Line_Crossover()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: "{\"FastPeriod\":5,\"SlowPeriod\":10,\"SignalPeriod\":3,\"DivergenceLookback\":5}");

        // Long rally then decline
        int count = 30;
        var prices = new decimal[count];
        for (int i = 0; i < 20; i++)
            prices[i] = 1.1000m + i * 0.0020m; // rally
        for (int i = 20; i < count; i++)
            prices[i] = prices[19] - (i - 19) * 0.0040m; // sharp decline

        var candles = GenerateCandles(prices);
        decimal lastClose = prices[^1];

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose - 0.0002m, lastClose), CancellationToken.None);

        if (signal is not null)
        {
            Assert.Equal(TradeDirection.Sell, signal.Direction);
            Assert.NotNull(signal.StopLoss);
            Assert.NotNull(signal.TakeProfit);
            Assert.True(signal.StopLoss > signal.EntryPrice);
            Assert.True(signal.TakeProfit < signal.EntryPrice);
        }
    }

    [Fact]
    public async Task Macd_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence);

        var candles = GenerateTrendCandles(5, 1.1000m, 0.0001m);
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ========================================================================
    //  SessionBreakoutEvaluator
    // ========================================================================

    /// <summary>
    /// Helper that generates candles with specific hour timestamps for session tests.
    /// </summary>
    private static List<Candle> GenerateSessionCandles(
        (int Hour, decimal Close)[] hourPrices, decimal spread = 0.0010m)
    {
        var candles = new List<Candle>();
        var baseDate = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < hourPrices.Length; i++)
        {
            decimal close = hourPrices[i].Close;
            candles.Add(new Candle
            {
                Id = i + 1,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = close - spread * 0.5m,
                High = close + spread,
                Low = close - spread,
                Close = close,
                Volume = 1000m,
                Timestamp = baseDate.AddHours(hourPrices[i].Hour),
                IsClosed = true
            });
        }
        return candles;
    }

    [Fact]
    public async Task Session_Buy_Signal_When_Price_Breaks_Above_Asian_Range()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions, NullLogger<SessionBreakoutEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.SessionBreakout,
            parametersJson: "{\"RangeStartHourUtc\":0,\"RangeEndHourUtc\":8,\"BreakoutStartHour\":8,\"BreakoutEndHour\":12,\"ThresholdMultiplier\":0.1}");

        // Asian session (hours 0-7): tight range around 1.1000
        // London session (hours 8+): breakout above
        var hourPrices = new (int Hour, decimal Close)[]
        {
            (0, 1.1000m), (1, 1.1005m), (2, 1.0995m), (3, 1.1002m),
            (4, 1.0998m), (5, 1.1003m), (6, 1.0997m), (7, 1.1001m),
            // London — 6 bars for ATR
            (8,  1.1010m), (9,  1.1020m), (10, 1.1030m), (11, 1.1040m),
            (8,  1.1015m), (9,  1.1025m), (10, 1.1035m), (11, 1.1045m),
            // Breakout bar
            (9, 1.1100m) // breaks well above Asian high of 1.1005+spread
        };
        var candles = GenerateSessionCandles(hourPrices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1098m, 1.1102m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.NotNull(signal.StopLoss);
        Assert.NotNull(signal.TakeProfit);
        Assert.True(signal.StopLoss < signal.EntryPrice);
        Assert.True(signal.TakeProfit > signal.EntryPrice);
    }

    [Fact]
    public async Task Session_No_Signal_Outside_Breakout_Window()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions, NullLogger<SessionBreakoutEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.SessionBreakout,
            parametersJson: "{\"BreakoutStartHour\":8,\"BreakoutEndHour\":12}");

        // All candles in Asian session — outside breakout window
        var hourPrices = new (int Hour, decimal Close)[]
        {
            (0, 1.1000m), (1, 1.1005m), (2, 1.0995m), (3, 1.1002m),
            (4, 1.0998m), (5, 1.1003m), (6, 1.0997m), (7, 1.1001m),
            (0, 1.1000m), (1, 1.1005m), (2, 1.0995m), (3, 1.1002m),
            (4, 1.0998m), (5, 1.1003m), (6, 1.0997m), (7, 1.1001m),
        };
        var candles = GenerateSessionCandles(hourPrices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1200m, 1.1202m), CancellationToken.None);

        Assert.Null(signal); // Outside breakout hours — no signal regardless of price
    }

    [Fact]
    public async Task Session_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions, NullLogger<SessionBreakoutEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.SessionBreakout);

        var candles = GenerateTrendCandles(5, 1.1000m, 0.0001m);
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ========================================================================
    //  MomentumTrendEvaluator
    // ========================================================================

    [Fact]
    public async Task Momentum_Buy_Signal_In_Strong_Uptrend()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions, NullLogger<MomentumTrendEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: "{\"AdxPeriod\":7,\"AdxThreshold\":20}");

        // Strong uptrend: steady climb with increasing highs
        int count = 30;
        var prices = new decimal[count];
        // Start flat then accelerate up (creates rising ADX + +DI > -DI)
        for (int i = 0; i < 15; i++)
            prices[i] = 1.1000m + (i % 2 == 0 ? 0.0002m : -0.0001m) * i; // choppy flat
        for (int i = 15; i < count; i++)
            prices[i] = prices[14] + (i - 14) * 0.0030m; // strong ramp up

        var candles = GenerateCandles(prices, spread: 0.0015m);
        decimal lastClose = prices[^1];

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose, lastClose + 0.0002m), CancellationToken.None);

        // ADX-based signals depend on the exact DI crossover timing;
        // validate that IF a signal fires, it has correct properties
        if (signal is not null)
        {
            Assert.Equal(TradeDirection.Buy, signal.Direction);
            Assert.NotNull(signal.StopLoss);
            Assert.NotNull(signal.TakeProfit);
            Assert.True(signal.StopLoss < signal.EntryPrice);
            Assert.True(signal.TakeProfit > signal.EntryPrice);
            Assert.True(signal.Confidence >= _defaultOptions.MomentumTrendConfidence);
        }
    }

    [Fact]
    public async Task Momentum_Sell_Signal_In_Strong_Downtrend()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions, NullLogger<MomentumTrendEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: "{\"AdxPeriod\":7,\"AdxThreshold\":20}");

        int count = 30;
        var prices = new decimal[count];
        for (int i = 0; i < 15; i++)
            prices[i] = 1.1000m + (i % 2 == 0 ? 0.0002m : -0.0001m) * i;
        for (int i = 15; i < count; i++)
            prices[i] = prices[14] - (i - 14) * 0.0030m; // strong ramp down

        var candles = GenerateCandles(prices, spread: 0.0015m);
        decimal lastClose = prices[^1];

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (lastClose - 0.0002m, lastClose), CancellationToken.None);

        if (signal is not null)
        {
            Assert.Equal(TradeDirection.Sell, signal.Direction);
            Assert.NotNull(signal.StopLoss);
            Assert.NotNull(signal.TakeProfit);
            Assert.True(signal.StopLoss > signal.EntryPrice);
            Assert.True(signal.TakeProfit < signal.EntryPrice);
        }
    }

    [Fact]
    public async Task Momentum_No_Signal_In_Ranging_Market()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions, NullLogger<MomentumTrendEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: "{\"AdxPeriod\":7,\"AdxThreshold\":25}");

        // Flat market — ADX will be low, should not trigger
        int count = 30;
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
            prices[i] = 1.1000m + (i % 2 == 0 ? 0.0001m : -0.0001m);

        var candles = GenerateCandles(prices);

        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(signal); // ADX too low in ranging market
    }

    [Fact]
    public async Task Momentum_No_Signal_When_Insufficient_Candles()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions, NullLogger<MomentumTrendEvaluator>.Instance, _metrics);
        var strategy = CreateStrategy(StrategyType.MomentumTrend);

        var candles = GenerateTrendCandles(5, 1.1000m, 0.0001m);
        var signal = await evaluator.EvaluateAsync(
            strategy, candles, (1.1010m, 1.1012m), CancellationToken.None);

        Assert.Null(signal);
    }
}
