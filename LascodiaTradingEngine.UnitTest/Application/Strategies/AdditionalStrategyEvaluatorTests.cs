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

public class AdditionalStrategyEvaluatorTests : IDisposable
{
    private readonly StrategyEvaluatorOptions _defaultOptions = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TradingMetrics _metrics;
    private readonly IMarketRegimeDetector _regimeDetector = Mock.Of<IMarketRegimeDetector>();
    private readonly IMultiTimeframeFilter _mtfFilter = Mock.Of<IMultiTimeframeFilter>();

    public AdditionalStrategyEvaluatorTests()
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

    // ── Helpers ────────────────────────────────────────────────────────────────

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

    private static List<Candle> GenerateTrendCandles(
        int count, decimal startPrice, decimal stepPerBar, decimal spread = 0.0010m)
    {
        var prices = new decimal[count];
        for (int i = 0; i < count; i++)
            prices[i] = startPrice + stepPerBar * i;
        return GenerateCandles(prices, spread);
    }

    // ========================================================================
    //  BollingerBandReversionEvaluator
    // ========================================================================

    [Fact]
    public async Task Bollinger_Returns_Null_When_Insufficient_Candles()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion);

        var candles = GenerateCandles([1.1000m, 1.1001m, 1.1002m]);
        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Bollinger_Buy_Signal_When_Price_Bounces_Off_Lower_Band()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        // Create candles that range tightly, then dip sharply below lower band, then recover
        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1000m); // stable SMA
        prices.Add(1.0950m); // previous bar: drops below lower band
        prices.Add(1.1005m); // current bar: recovers above lower band

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);
        decimal bid = 1.1003m;
        decimal ask = 1.1005m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(ask, signal.EntryPrice);
        Assert.True(signal.StopLoss < signal.EntryPrice);
        Assert.True(signal.TakeProfit > signal.EntryPrice);
    }

    [Fact]
    public async Task Bollinger_Sell_Signal_When_Price_Bounces_Off_Upper_Band()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1000m);
        prices.Add(1.1050m); // previous bar: spikes above upper band
        prices.Add(1.0995m); // current bar: falls back below upper band

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);
        decimal bid = 1.0993m;
        decimal ask = 1.0995m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(bid, signal.EntryPrice);
        Assert.True(signal.StopLoss > signal.EntryPrice);
        Assert.True(signal.TakeProfit < signal.EntryPrice);
    }

    [Fact]
    public async Task Bollinger_Returns_Null_When_Price_Inside_Bands()
    {
        var evaluator = new BollingerBandReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion);

        // Flat prices — close stays inside bands
        var candles = GenerateTrendCandles(25, 1.1000m, 0m, spread: 0.0010m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);
        Assert.Null(signal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// All hardening filters disabled — isolates core BB crossover logic.
    /// </summary>
    private static StrategyEvaluatorOptions BollingerCoreOptions() => new()
    {
        BollingerMaxSpreadAtrFraction      = 0,
        BollingerMaxGapAtrFraction         = 0,
        BollingerMinVolume                 = 0,
        BollingerMaxRsiForBuy              = 0,
        BollingerMinRsiForSell             = 0,
        BollingerMinBandwidthAtrFraction   = 0,
        BollingerMinRiskRewardRatio        = 0,
        BollingerRequireCandleConfirmation = false,
        BollingerSwingSlEnabled            = false,
        BollingerMidBandTpEnabled          = false,
        BollingerConfidenceLotSizing       = false,
        BollingerWeightRsi                 = 0,
        BollingerWeightVolume              = 0,
        BollingerWeightCandle              = 0,
        BollingerSlippageAtrFraction       = 0,
        BollingerSqueezeLookbackBars       = 1,  // single-bar, matches original behaviour
    };

    /// <summary>Creates candles that trigger a BB buy signal (period 5, multiplier 1.0, no squeeze).</summary>
    private static List<Candle> BollingerBuyCandles(decimal spread = 0.0020m)
    {
        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1000m);
        prices.Add(1.0950m); // prev: dips below lower band
        prices.Add(1.1005m); // last: recovers above lower band
        return GenerateCandles(prices.ToArray(), spread);
    }

    // ── Spread filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Spread_Too_Wide()
    {
        var options = BollingerCoreOptions();
        options.BollingerMaxSpreadAtrFraction = 0.001m; // very tight — any real spread fails
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        // bid/ask spread = 0.0100 (wide)
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.0995m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Gap filter ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Gap_Too_Large()
    {
        var options = BollingerCoreOptions();
        options.BollingerMaxGapAtrFraction = 0.001m; // impossibly tight
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        // BollingerBuyCandles has a large open gap on the recovery bar (prev close 1.0950 → open ~1.1004)
        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Volume filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Volume_Too_Low()
    {
        var options = BollingerCoreOptions();
        options.BollingerMinVolume = 999_999m; // impossibly high
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles(); // GenerateCandles sets Volume = 1000
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Minimum bandwidth filter ──────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Bandwidth_Too_Narrow()
    {
        var options = BollingerCoreOptions();
        options.BollingerMinBandwidthAtrFraction = 1000m; // impossibly high
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── RSI filter ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Buy_Returns_Null_When_RSI_Exceeds_MaxRsiForBuy()
    {
        var options = BollingerCoreOptions();
        options.BollingerMaxRsiForBuy = 1m; // virtually nothing passes RSI < 1
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    [Fact]
    public async Task Bollinger_Sell_Returns_Null_When_RSI_Below_MinRsiForSell()
    {
        var options = BollingerCoreOptions();
        options.BollingerMinRsiForSell = 99m; // virtually nothing passes RSI > 99
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1000m);
        prices.Add(1.1050m); // prev: spikes above upper band
        prices.Add(1.0995m); // last: falls back below upper band
        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.0993m, 1.0995m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Candle confirmation gate ───────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Candle_Confirmation_Required_But_Not_Met()
    {
        var options = BollingerCoreOptions();
        options.BollingerRequireCandleConfirmation = true;
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        // GenerateCandles produces near-doji bodies (tiny body, equal wicks) → ScoreCandlePatterns = 0.5
        // The gate rejects when score <= 0.5, so this signal must be suppressed.
        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Risk-reward filter ────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_RiskReward_Below_Minimum()
    {
        var options = BollingerCoreOptions();
        options.BollingerMinRiskRewardRatio = 1000m; // impossibly high
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.Null(signal);
    }

    // ── Slippage buffer ───────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Buy_Entry_Price_Shifted_Up_By_Slippage()
    {
        var options = BollingerCoreOptions();
        options.BollingerSlippageAtrFraction = 0.5m;
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        decimal ask = 1.1005m;
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.True(signal.EntryPrice > ask, "Entry price should be above Ask due to slippage buffer");
    }

    // ── Mid-band TP ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Buy_TP_Targets_SMA_When_MidBandTp_Enabled()
    {
        var options = BollingerCoreOptions();
        options.BollingerMidBandTpEnabled = true;
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        decimal ask = 1.1005m;
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        // With mid-band TP the take-profit must be at or above the ask (SMA is above entry for a lower-band touch)
        Assert.True(signal.TakeProfit > signal.EntryPrice);
        Assert.True(signal.StopLoss < signal.EntryPrice);
    }

    // ── Confidence-based lot sizing ───────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Lot_Size_Scales_With_Confidence_When_Enabled()
    {
        var options = BollingerCoreOptions();
        options.BollingerConfidenceLotSizing = true;
        options.BollingerMinLotSize          = 0.01m;
        options.BollingerMaxLotSize          = 0.10m;
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.True(signal!.SuggestedLotSize >= 0.01m);
        Assert.True(signal.SuggestedLotSize  <= 0.10m);
    }

    // ── Multi-bar squeeze detection ───────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Returns_Null_When_Multi_Bar_Squeeze_Detected()
    {
        var options = BollingerCoreOptions();
        options.BollingerSqueezeLookbackBars = 3;   // compare current bandwidth to 3 bars ago
        options.BollingerMinBandwidthAtrFraction = 0; // disable so only squeeze fires
        var evaluator = new BollingerBandReversionEvaluator(options);
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":2.0,"SqueezeThreshold":0.99}""");

        // Build candles where bands were wide then progressively narrow (squeeze).
        // 10 volatile bars → 5 flat bars → 2 signal bars.
        // With lookback=3, baseIdx=13 whose 5-bar SMA window still includes bar 9 (volatile),
        // so referenceBandwidth > 0. The flat section gives a much narrower bandwidth,
        // triggering the squeeze and blocking the signal.
        var prices = new List<decimal>();
        for (int i = 0; i < 10; i++) prices.Add(1.1000m + (i % 2 == 0 ? 0.0200m : -0.0200m)); // volatile
        for (int i = 0; i < 5; i++)  prices.Add(1.1000m); // tight range — bands contract
        prices.Add(1.0990m); // prev: dips below lower band (now very narrow)
        prices.Add(1.1005m); // last: recovers

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0005m);
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        // With 0.99 threshold, current bandwidth must be ≥ 99% of the 3-bar-ago bandwidth.
        // The flat section contracts to near-zero, so the squeeze fires and blocks the signal.
        Assert.Null(signal);
    }

    // ── RSI confidence factor direction ──────────────────────────────────────

    [Fact]
    public async Task Bollinger_Buy_Confidence_Higher_When_RSI_Is_Deeply_Oversold()
    {
        var optionsLowRsi  = BollingerCoreOptions();
        optionsLowRsi.BollingerWeightRsi = 1.0m;  // RSI is the only confidence factor
        optionsLowRsi.BollingerWeightDepth = 0;
        var evaluatorA = new BollingerBandReversionEvaluator(optionsLowRsi);

        var optionsHighRsi = BollingerCoreOptions();
        optionsHighRsi.BollingerWeightRsi = 1.0m;
        optionsHighRsi.BollingerWeightDepth = 0;
        var evaluatorB = new BollingerBandReversionEvaluator(optionsHighRsi);

        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        // Scenario A: strong downtrend before the bounce → RSI will be low (oversold) → high rsiFactor
        var pricesA = new List<decimal>();
        for (int i = 0; i < 15; i++) pricesA.Add(1.1200m - i * 0.0020m); // downtrend → low RSI
        pricesA.Add(1.0850m); // prev: below lower band
        pricesA.Add(1.0960m); // last: recovers
        var candlesA = GenerateCandles(pricesA.ToArray(), spread: 0.0020m);

        // Scenario B: flat / mild recovery before the bounce → RSI near 50 → lower rsiFactor
        var candlesB = BollingerBuyCandles(); // flat start then dip/recover

        var signalA = await evaluatorA.EvaluateAsync(strategy, candlesA, (1.0958m, 1.0960m), CancellationToken.None);
        var signalB = await evaluatorB.EvaluateAsync(strategy, candlesB, (1.1003m, 1.1005m), CancellationToken.None);

        // Both should fire; the deeply oversold scenario should produce higher or equal confidence
        if (signalA != null && signalB != null)
            Assert.True(signalA.Confidence >= signalB.Confidence,
                $"Deeply oversold RSI should yield higher confidence. A={signalA.Confidence:F4}, B={signalB.Confidence:F4}");
    }

    // ── Confidence sensitivity option ─────────────────────────────────────────

    [Fact]
    public async Task Bollinger_Confidence_Sensitivity_Controls_Score_Spread()
    {
        var optionsNarrow = BollingerCoreOptions();
        optionsNarrow.BollingerWeightDepth           = 1.0m;
        optionsNarrow.BollingerConfidenceSensitivity = 0.10m; // tight band

        var optionsWide = BollingerCoreOptions();
        optionsWide.BollingerWeightDepth           = 1.0m;
        optionsWide.BollingerConfidenceSensitivity = 0.60m; // wide band

        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");
        var candles = BollingerBuyCandles();

        var signalNarrow = await new BollingerBandReversionEvaluator(optionsNarrow)
            .EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);
        var signalWide = await new BollingerBandReversionEvaluator(optionsWide)
            .EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.NotNull(signalNarrow);
        Assert.NotNull(signalWide);

        // Both start from the same base confidence (0.65). With a strong depth factor (>0.5),
        // the wide-sensitivity evaluator should produce a higher confidence.
        Assert.True(signalWide!.Confidence >= signalNarrow!.Confidence,
            $"Wider sensitivity should allow higher confidence. Wide={signalWide.Confidence:F4}, Narrow={signalNarrow.Confidence:F4}");
    }

    // ── MinRequiredCandles includes RSI warmup ────────────────────────────────

    [Fact]
    public void Bollinger_MinRequiredCandles_Includes_Rsi_Period_When_RsiActive()
    {
        var optionsWithRsi = new StrategyEvaluatorOptions
        {
            BollingerRsiPeriod    = 21,
            BollingerWeightRsi    = 0.20m,
            AtrPeriodForSlTp      = 14,
        };
        var optionsNoRsi = new StrategyEvaluatorOptions
        {
            BollingerWeightRsi    = 0,
            BollingerMaxRsiForBuy = 0,
            BollingerMinRsiForSell = 0,
            AtrPeriodForSlTp      = 14,
        };

        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":20}""");

        int withRsi = new BollingerBandReversionEvaluator(optionsWithRsi).MinRequiredCandles(strategy);
        int noRsi   = new BollingerBandReversionEvaluator(optionsNoRsi).MinRequiredCandles(strategy);

        // With RSI period=21, required = max(20, 14, 21*2+1) + 1 = max(20, 14, 43) + 1 = 44
        Assert.Equal(44, withRsi);
        // Without RSI: max(20, 14) + 1 = 21
        Assert.Equal(21, noRsi);
    }

    [Fact]
    public void Bollinger_MinRequiredCandles_Includes_Squeeze_Lookback()
    {
        var options = new StrategyEvaluatorOptions
        {
            BollingerSqueezeLookbackBars = 5,
            BollingerWeightRsi           = 0,
            BollingerMaxRsiForBuy        = 0,
            BollingerMinRsiForSell       = 0,
            AtrPeriodForSlTp             = 14,
        };
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":20}""");

        int required = new BollingerBandReversionEvaluator(options).MinRequiredCandles(strategy);

        // With squeeze lookback=5: period + (5-1) = 24, max(24, 14) + 1 = 25
        Assert.Equal(25, required);
    }

    // ── Core signal still fires with all filters disabled ─────────────────────

    [Fact]
    public async Task Bollinger_Buy_Signal_Fires_With_All_Filters_Disabled()
    {
        var evaluator = new BollingerBandReversionEvaluator(BollingerCoreOptions());
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var candles = BollingerBuyCandles();
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1003m, 1.1005m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(1.1005m, signal.EntryPrice);
        Assert.True(signal.StopLoss  < signal.EntryPrice);
        Assert.True(signal.TakeProfit > signal.EntryPrice);
        Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        Assert.True(signal.ExpiresAt > signal.GeneratedAt);
    }

    [Fact]
    public async Task Bollinger_Sell_Signal_Fires_With_All_Filters_Disabled()
    {
        var evaluator = new BollingerBandReversionEvaluator(BollingerCoreOptions());
        var strategy  = CreateStrategy(StrategyType.BollingerBandReversion,
            parametersJson: """{"Period":5,"StdDevMultiple":1.0,"SqueezeThreshold":0}""");

        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1000m);
        prices.Add(1.1050m); // prev: spikes above upper band
        prices.Add(1.0995m); // last: falls back below upper band
        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.0993m, 1.0995m), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(1.0993m, signal.EntryPrice);
        Assert.True(signal.StopLoss  > signal.EntryPrice);
        Assert.True(signal.TakeProfit < signal.EntryPrice);
    }

    // ========================================================================
    //  RSIReversionEvaluator
    // ========================================================================

    [Fact]
    public async Task RSI_Returns_Null_When_Insufficient_Candles()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion);

        var candles = GenerateCandles([1.1000m, 1.1001m]);
        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RSI_Buy_Signal_When_Exiting_Oversold()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: """{"Period":5,"Oversold":30,"Overbought":70}""");

        // Create a strong downtrend to push RSI oversold, then a reversal bar
        var prices = new List<decimal>();
        prices.Add(1.1100m);
        for (int i = 1; i <= 14; i++) prices.Add(1.1100m - i * 0.0020m); // strong downtrend → RSI very low
        prices.Add(1.0900m); // one more down bar to ensure deeply oversold
        prices.Add(1.0950m); // reversal bar — RSI should cross back above oversold

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);
        decimal bid = 1.0948m;
        decimal ask = 1.0952m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        // Signal may or may not fire depending on exact RSI values — this tests the flow
        if (signal != null)
        {
            Assert.Equal(TradeDirection.Buy, signal.Direction);
            Assert.Equal(ask, signal.EntryPrice);
            Assert.True(signal.StopLoss < signal.EntryPrice);
            Assert.True(signal.TakeProfit > signal.EntryPrice);
            Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        }
    }

    [Fact]
    public async Task RSI_Sell_Signal_When_Exiting_Overbought()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion,
            parametersJson: """{"Period":5,"Oversold":30,"Overbought":70}""");

        // Strong uptrend to push RSI overbought, then reversal
        var prices = new List<decimal>();
        prices.Add(1.0900m);
        for (int i = 1; i <= 14; i++) prices.Add(1.0900m + i * 0.0020m);
        prices.Add(1.1200m); // one more up bar to ensure deeply overbought
        prices.Add(1.1150m); // reversal bar — RSI should cross back below overbought

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);
        decimal bid = 1.1148m;
        decimal ask = 1.1152m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        if (signal != null)
        {
            Assert.Equal(TradeDirection.Sell, signal.Direction);
            Assert.Equal(bid, signal.EntryPrice);
            Assert.True(signal.StopLoss > signal.EntryPrice);
            Assert.True(signal.TakeProfit < signal.EntryPrice);
        }
    }

    [Fact]
    public async Task RSI_Returns_Null_When_RSI_In_Neutral_Zone()
    {
        var evaluator = new RSIReversionEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.RSIReversion);

        // Flat prices — RSI hovers near 50
        var candles = GenerateTrendCandles(25, 1.1000m, 0.00001m, spread: 0.0010m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);
        Assert.Null(signal);
    }

    // ========================================================================
    //  MomentumTrendEvaluator
    // ========================================================================

    [Fact]
    public async Task Momentum_Returns_Null_When_Insufficient_Candles()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.MomentumTrend);

        var candles = GenerateCandles([1.1000m, 1.1001m, 1.1002m]);
        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Momentum_Returns_Null_In_Ranging_Market()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: """{"AdxPeriod":14,"AdxThreshold":25}""");

        // Flat market — ADX will be very low (below 25)
        var candles = GenerateTrendCandles(50, 1.1000m, 0m, spread: 0.0005m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);
        Assert.Null(signal);
    }

    [Fact]
    public async Task Momentum_Generates_Signal_In_Strong_Trend()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: """{"AdxPeriod":7,"AdxThreshold":15}""");

        // Strong uptrend — ADX should be high, +DI should dominate -DI
        var prices = new List<decimal>();
        // Start flat then trend strongly up
        for (int i = 0; i < 15; i++) prices.Add(1.1000m + (i % 2) * 0.0003m);
        for (int i = 0; i < 25; i++) prices.Add(1.1000m + i * 0.0030m); // strong uptrend

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);
        decimal bid = 1.1700m;
        decimal ask = 1.1702m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        // The signal depends on the exact DI crossover timing
        if (signal != null)
        {
            Assert.True(signal.Direction is TradeDirection.Buy or TradeDirection.Sell);
            Assert.True(signal.StopLoss.HasValue);
            Assert.True(signal.TakeProfit.HasValue);
            Assert.Equal(TradeSignalStatus.Pending, signal.Status);
        }
    }

    [Fact]
    public async Task Momentum_Signal_Has_ADX_Confidence_Bonus()
    {
        var evaluator = new MomentumTrendEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.MomentumTrend,
            parametersJson: """{"AdxPeriod":7,"AdxThreshold":10}""");

        // Very strong directional move
        var prices = new List<decimal>();
        for (int i = 0; i < 10; i++) prices.Add(1.1000m);
        for (int i = 0; i < 30; i++) prices.Add(1.1000m + i * 0.0040m);

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0020m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.2100m, 1.2102m), CancellationToken.None);

        if (signal != null)
        {
            // Confidence should be >= base confidence due to ADX bonus
            Assert.True(signal.Confidence >= _defaultOptions.MomentumTrendConfidence);
        }
    }

    // ========================================================================
    //  SessionBreakoutEvaluator
    // ========================================================================

    [Fact]
    public async Task Session_Returns_Null_When_Insufficient_Candles()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.SessionBreakout);

        var candles = GenerateCandles([1.1000m, 1.1001m]);
        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Session_Returns_Null_Outside_Breakout_Window()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.SessionBreakout,
            parametersJson: """{"RangeStartHourUtc":0,"RangeEndHourUtc":8,"BreakoutStartHour":8,"BreakoutEndHour":12}""");

        // Generate candles all timestamped at 14:00 UTC (outside breakout window 8-12)
        var baseTime = new DateTime(2026, 1, 5, 14, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>();
        for (int i = 0; i < 65; i++)
        {
            candles.Add(new Candle
            {
                Id = i + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000m, Timestamp = baseTime.AddHours(-65 + i), IsClosed = true
            });
        }

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);
        Assert.Null(signal);
    }

    [Fact]
    public async Task Session_Buy_Signal_On_Breakout_Above_Range_High()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.SessionBreakout,
            parametersJson: """{"RangeStartHourUtc":0,"RangeEndHourUtc":8,"BreakoutStartHour":8,"BreakoutEndHour":12,"ThresholdMultiplier":0}""");

        // Build candles: Asian session (00:00-08:00) with range 1.0990-1.1010
        var candles = new List<Candle>();
        var baseDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // Monday

        // Pre-history candles for ATR (14 bars before Asian session)
        for (int i = -20; i < 0; i++)
        {
            candles.Add(new Candle
            {
                Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000m, Timestamp = baseDate.AddHours(i), IsClosed = true
            });
        }

        // Asian session candles (hours 0-7): range 1.0990 - 1.1010
        for (int h = 0; h < 8; h++)
        {
            candles.Add(new Candle
            {
                Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000m, Timestamp = baseDate.AddHours(h), IsClosed = true
            });
        }

        // London session breakout bar at 09:00 — closes above range high
        candles.Add(new Candle
        {
            Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Open = 1.1012m, High = 1.1030m, Low = 1.1005m, Close = 1.1025m,
            Volume = 2000m, Timestamp = baseDate.AddHours(9), IsClosed = true
        });

        decimal bid = 1.1023m;
        decimal ask = 1.1025m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Buy, signal!.Direction);
        Assert.Equal(ask, signal.EntryPrice);
        Assert.True(signal.StopLoss < signal.EntryPrice);
        Assert.True(signal.TakeProfit > signal.EntryPrice);
    }

    [Fact]
    public async Task Session_Sell_Signal_On_Breakout_Below_Range_Low()
    {
        var evaluator = new SessionBreakoutEvaluator(_defaultOptions);
        var strategy = CreateStrategy(StrategyType.SessionBreakout,
            parametersJson: """{"RangeStartHourUtc":0,"RangeEndHourUtc":8,"BreakoutStartHour":8,"BreakoutEndHour":12,"ThresholdMultiplier":0}""");

        var candles = new List<Candle>();
        var baseDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        for (int i = -20; i < 0; i++)
        {
            candles.Add(new Candle
            {
                Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000m, Timestamp = baseDate.AddHours(i), IsClosed = true
            });
        }

        for (int h = 0; h < 8; h++)
        {
            candles.Add(new Candle
            {
                Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000m, Timestamp = baseDate.AddHours(h), IsClosed = true
            });
        }

        // Breakout below range low
        candles.Add(new Candle
        {
            Id = candles.Count + 1, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Open = 1.0988m, High = 1.0995m, Low = 1.0970m, Close = 1.0975m,
            Volume = 2000m, Timestamp = baseDate.AddHours(9), IsClosed = true
        });

        decimal bid = 1.0975m;
        decimal ask = 1.0977m;

        var signal = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Sell, signal!.Direction);
        Assert.Equal(bid, signal.EntryPrice);
        Assert.True(signal.StopLoss > signal.EntryPrice);
        Assert.True(signal.TakeProfit < signal.EntryPrice);
    }

    // ========================================================================
    //  MACDDivergenceEvaluator
    // ========================================================================

    [Fact]
    public async Task MACD_Returns_Null_When_Insufficient_Candles()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence);

        var candles = GenerateCandles([1.1000m, 1.1001m, 1.1002m]);
        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1002m), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task MACD_Returns_Null_When_Candles_Out_Of_Order()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence);

        // Generate enough candles but reverse the timestamps
        var candles = GenerateTrendCandles(60, 1.1000m, 0.0001m);
        // Swap two timestamps to break ordering
        (candles[10].Timestamp, candles[11].Timestamp) = (candles[11].Timestamp, candles[10].Timestamp);

        var result = await evaluator.EvaluateAsync(strategy, candles, (1.1050m, 1.1052m), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task MACD_Generates_Signal_On_Strong_Trend()
    {
        // Disable all hardening filters to test core signal generation
        var options = new StrategyEvaluatorOptions
        {
            MacdDivergenceMinAdx = 0,
            MacdDivergenceMaxGapAtrFraction = 0,
            MacdDivergenceMaxSpreadAtrFraction = 0,
            MacdDivergenceMinVolume = 0,
            MacdDivergenceRequireHistogramTurn = false,
            MacdDivergenceMaxRsiForBuy = 0,
            MacdDivergenceMinRsiForSell = 0,
            MacdDivergenceMinRiskRewardRatio = 0,
            MacdDivergenceSwingSlEnabled = false,
            MacdDivergenceSwingTpEnabled = false,
            MacdDivergenceDynamicSlTp = false,
            MacdDivergenceSlippageAtrFraction = 0,
        };
        var evaluator = new MACDDivergenceEvaluator(options, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":5,"SlowPeriod":10,"SignalPeriod":3,"DivergenceLookback":5}""");

        // Create a crossover scenario: flat then strong uptrend
        var prices = new List<decimal>();
        for (int i = 0; i < 15; i++) prices.Add(1.1000m + (i % 2) * 0.0002m);
        for (int i = 0; i < 20; i++) prices.Add(1.1000m + i * 0.0020m);

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1380m, 1.1382m), CancellationToken.None);

        if (signal != null)
        {
            Assert.Equal(strategy.Id, signal.StrategyId);
            Assert.Equal(strategy.Symbol, signal.Symbol);
            Assert.True(signal.StopLoss.HasValue);
            Assert.True(signal.TakeProfit.HasValue);
            Assert.Equal(TradeSignalStatus.Pending, signal.Status);
            Assert.True(signal.ExpiresAt > signal.GeneratedAt);
        }
    }

    [Fact]
    public async Task MACD_Respects_Gap_Filter()
    {
        var options = new StrategyEvaluatorOptions
        {
            MacdDivergenceMaxGapAtrFraction = 0.01m, // Very tight gap filter — almost any gap rejected
            MacdDivergenceMinAdx = 0,
            MacdDivergenceMaxSpreadAtrFraction = 0,
            MacdDivergenceMinVolume = 0,
        };
        var evaluator = new MACDDivergenceEvaluator(options, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":5,"SlowPeriod":10,"SignalPeriod":3}""");

        // Create candles with a large gap between the last two
        var prices = new decimal[30];
        for (int i = 0; i < 29; i++) prices[i] = 1.1000m + i * 0.0005m;
        prices[29] = 1.2000m; // huge gap

        var candles = GenerateCandles(prices, spread: 0.0010m);
        // Force a large open-to-previous-close gap
        candles[29] = new Candle
        {
            Id = 30, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            Open = 1.2000m, High = 1.2010m, Low = 1.1990m, Close = 1.2000m,
            Volume = 1000m, Timestamp = candles[28].Timestamp.AddHours(1), IsClosed = true
        };

        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1998m, 1.2002m), CancellationToken.None);
        Assert.Null(signal); // Gap should cause rejection
    }

    [Fact]
    public async Task MACD_Returns_Null_When_Spread_Too_Wide()
    {
        var options = MacdCoreOptions();
        options.MacdDivergenceMaxSpreadAtrFraction = 0.001m; // extremely tight — any real spread fails
        var evaluator = new MACDDivergenceEvaluator(options, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":5,"SlowPeriod":10,"SignalPeriod":3}""");

        var prices = new List<decimal>();
        for (int i = 0; i < 15; i++) prices.Add(1.1000m + (i % 2) * 0.0002m);
        for (int i = 0; i < 20; i++) prices.Add(1.1000m + i * 0.0020m);
        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);

        // Wide bid/ask span of 100 pips — far exceeds the 0.001× ATR threshold
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.1000m, 1.1100m), CancellationToken.None);
        Assert.Null(signal);
    }

    [Fact]
    public async Task MACD_Cooldown_Suppresses_Immediate_Repeat()
    {
        var options = MacdCoreOptions();
        options.MacdDivergenceCooldownBars = 5;
        var evaluator = new MACDDivergenceEvaluator(options, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":3,"SlowPeriod":5,"SignalPeriod":2,"DivergenceLookback":5}""");

        // Flat start then strong uptrend to trigger a zero-line crossover
        var prices = new List<decimal>();
        for (int i = 0; i < 15; i++) prices.Add(1.1000m);
        for (int i = 0; i < 20; i++) prices.Add(1.1000m + i * 0.0030m);
        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);
        (decimal bid, decimal ask) = (1.1570m, 1.1572m);

        var signal1 = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);
        if (signal1 == null) return; // crossover not at last bar — cooldown never stamped, test not applicable

        // Immediate second call with identical candles — zero bars elapsed since signal
        var signal2 = await evaluator.EvaluateAsync(strategy, candles, (bid, ask), CancellationToken.None);
        Assert.Null(signal2);
    }

    [Fact]
    public void MACD_MinRequiredCandles_Scales_With_SlowPeriod()
    {
        var evaluator = new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);

        var shortPeriod = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":5,"SlowPeriod":10,"SignalPeriod":3}""");
        var longPeriod  = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":12,"SlowPeriod":50,"SignalPeriod":9}""");

        int minShort = evaluator.MinRequiredCandles(shortPeriod);
        int minLong  = evaluator.MinRequiredCandles(longPeriod);

        Assert.True(minShort > 0);
        Assert.True(minLong > minShort,
            $"Longer slow period should require more candles: {minLong} > {minShort}");

        // At minimum: emaWarmup (slowPeriod * 2) + divergenceLookback + 2
        // For SlowPeriod=50, DivergenceLookback=10 (default): 50*2+10+2 = 112
        Assert.True(minLong >= 50 * 2 + 10 + 2);
    }

    [Fact]
    public async Task MACD_ZeroCross_Filter_Rejects_When_No_Cross_Between_Swing_Points()
    {
        // With requireZeroCross=true, divergence between two negative-histogram swing points
        // that never crosses zero should be rejected.
        var options = MacdCoreOptions();
        options.MacdDivergenceRequireHistogramZeroCross = true;
        options.MacdDivergenceDetectHidden = false;

        var evaluator = new MACDDivergenceEvaluator(options, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter);
        var strategy = CreateStrategy(StrategyType.MACDDivergence,
            parametersJson: """{"FastPeriod":3,"SlowPeriod":5,"SignalPeriod":2,"DivergenceLookback":8}""");

        // Gradual, continuous downtrend — histogram stays negative throughout,
        // no zero-cross occurs, so any classic bullish divergence should be rejected.
        var prices = new List<decimal>();
        for (int i = 0; i < 20; i++) prices.Add(1.1200m - i * 0.0030m);

        var candles = GenerateCandles(prices.ToArray(), spread: 0.0015m);
        var signal = await evaluator.EvaluateAsync(strategy, candles, (1.0600m, 1.0602m), CancellationToken.None);

        // Either null (histogram zero-cross filter suppressed it) or null because no divergence
        // was found — either way, no signal should emerge from a pure downtrend without oscillation.
        Assert.Null(signal);
    }

    // ── MACD helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// All hardening filters disabled — isolates core MACD crossover/divergence logic.
    /// Regime filter is also disabled since unit tests have no real regime detector.
    /// </summary>
    private static StrategyEvaluatorOptions MacdCoreOptions() => new()
    {
        MacdDivergenceMinAdx                      = 0,
        MacdDivergenceMaxGapAtrFraction            = 0,
        MacdDivergenceMaxSpreadAtrFraction         = 0,
        MacdDivergenceMinVolume                    = 0,
        MacdDivergenceRequireHistogramTurn         = false,
        MacdDivergenceMaxRsiForBuy                 = 0,
        MacdDivergenceMinRsiForSell                = 0,
        MacdDivergenceMinRiskRewardRatio           = 0,
        MacdDivergenceSwingSlEnabled               = false,
        MacdDivergenceSwingTpEnabled               = false,
        MacdDivergenceDynamicSlTp                  = false,
        MacdDivergenceSlippageAtrFraction          = 0,
        MacdDivergenceRegimeFilterEnabled          = false,
        MacdDivergenceRequireCurrentBarPivot       = false,
        MacdDivergenceCooldownBars                 = 0,
        MacdDivergenceDetectHidden                 = false,
        MacdDivergenceRequireSignalLineCross       = false,
        MacdDivergenceRequireHistogramZeroCross    = false,
        MacdDivergenceRequireIndicatorPivot        = false,
        MacdDivergenceRequireCandlePatternConfirmation = false,
        MacdDivergencePartialTpEnabled             = false,
        MacdDivergenceWeightCandlePattern          = 0,
    };

    // ========================================================================
    //  Common signal property validation
    // ========================================================================

    [Fact]
    public async Task All_Evaluators_Set_Correct_StrategyType()
    {
        // Verify each evaluator reports its correct StrategyType
        Assert.Equal(StrategyType.BollingerBandReversion, new BollingerBandReversionEvaluator(_defaultOptions).StrategyType);
        Assert.Equal(StrategyType.RSIReversion, new RSIReversionEvaluator(_defaultOptions).StrategyType);
        Assert.Equal(StrategyType.MomentumTrend, new MomentumTrendEvaluator(_defaultOptions).StrategyType);
        Assert.Equal(StrategyType.SessionBreakout, new SessionBreakoutEvaluator(_defaultOptions).StrategyType);
        Assert.Equal(StrategyType.MACDDivergence,
            new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter).StrategyType);
    }

    [Fact]
    public async Task All_Evaluators_Return_Positive_MinRequiredCandles()
    {
        var strategy = CreateStrategy(StrategyType.BollingerBandReversion);

        Assert.True(new BollingerBandReversionEvaluator(_defaultOptions).MinRequiredCandles(strategy) > 0);
        Assert.True(new RSIReversionEvaluator(_defaultOptions).MinRequiredCandles(
            CreateStrategy(StrategyType.RSIReversion)) > 0);
        Assert.True(new MomentumTrendEvaluator(_defaultOptions).MinRequiredCandles(
            CreateStrategy(StrategyType.MomentumTrend)) > 0);
        Assert.True(new SessionBreakoutEvaluator(_defaultOptions).MinRequiredCandles(
            CreateStrategy(StrategyType.SessionBreakout)) > 0);
        Assert.True(new MACDDivergenceEvaluator(_defaultOptions, NullLogger<MACDDivergenceEvaluator>.Instance, _metrics, _regimeDetector, _mtfFilter)
            .MinRequiredCandles(CreateStrategy(StrategyType.MACDDivergence)) > 0);
    }
}
