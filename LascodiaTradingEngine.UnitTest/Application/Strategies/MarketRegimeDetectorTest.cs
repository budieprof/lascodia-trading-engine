using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.UnitTest.Application.Strategies;

public class MarketRegimeDetectorTest
{
    private readonly MarketRegimeOptions _defaultOptions;
    private readonly MarketRegimeDetector _detector;

    public MarketRegimeDetectorTest()
    {
        _defaultOptions = new MarketRegimeOptions();
        _detector = new MarketRegimeDetector(_defaultOptions);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a list of candles with a specific base price, spread (High-Low), and
    /// optional upward/downward trend factor per candle.
    /// </summary>
    private static List<Candle> GenerateCandles(
        int count,
        decimal basePrice,
        decimal spread,
        decimal trendPerCandle = 0m)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            decimal price = basePrice + trendPerCandle * i;
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = price,
                High      = price + spread / 2m,
                Low       = price - spread / 2m,
                Close     = price + trendPerCandle * 0.5m,
                Volume    = 100m,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    /// <summary>
    /// Generates candles with very large High-Low spread to produce high volatility scores.
    /// </summary>
    private static List<Candle> GenerateHighVolatilityCandles(int count, decimal basePrice, decimal spread)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            decimal price = basePrice;
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = price,
                High      = price + spread,
                Low       = price - spread,
                Close     = price,
                Volume    = 200m,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    /// <summary>
    /// Generates flat candles with minimal spread, producing low ADX and low volatility.
    /// </summary>
    private static List<Candle> GenerateFlatCandles(int count, decimal basePrice)
    {
        var candles = new List<Candle>(count);
        for (int i = 0; i < count; i++)
        {
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = basePrice,
                High      = basePrice + 0.00005m,
                Low       = basePrice - 0.00005m,
                Close     = basePrice,
                Volume    = 50m,
                Timestamp = DateTime.UtcNow.AddHours(-count + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }
        return candles;
    }

    // ── Insufficient data ──────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_InsufficientCandles_ThrowsInvalidOperationException()
    {
        // The default period is 14, so we need at least 15 candles.
        var candles = GenerateCandles(10, 1.10000m, 0.00100m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None));

        Assert.Contains("Insufficient candle data", ex.Message);
    }

    [Fact]
    public async Task DetectAsync_ExactlyPeriodCandles_ThrowsInvalidOperationException()
    {
        // period + 1 = 15 required; exactly 14 should throw
        var candles = GenerateCandles(14, 1.10000m, 0.00100m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None));
    }

    // ── Trending regime ────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_HighAdxLowVolatility_ReturnsTrending()
    {
        // Create candles with a meaningful spread relative to price to push ADX proxy high,
        // but keep volatility score below TrendingMaxVolatility (default 20).
        // ADX proxy = (ATR / avgClose) * 10000 * 2
        // volatility = (ATR / avgClose) * 10000
        // We need ADX > 25 → volatility > 12.5
        // And volatility < 20 (TrendingMaxVolatility)
        // So spread that gives volatility around 15 works: ATR/avgClose*10000 ~ 15
        // ATR ~ avgClose * 15 / 10000 = 1.1 * 0.0015 = 0.00165
        // Spread (High-Low) ~ 0.00165
        var candles = GenerateCandles(20, 1.10000m, 0.00170m, trendPerCandle: 0.00010m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.Trending, result.Regime);
        Assert.Equal("EURUSD", result.Symbol);
    }

    // ── Ranging regime ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_LowAdxLowVolatility_ReturnsRanging()
    {
        // Low ADX (< 20) and low volatility (< 10 RangingMaxVolatility)
        // ADX proxy = volatility * 2 < 20 → volatility < 10
        // Use very small spread: ATR/avgClose*10000 < 10
        // ATR ~ avgClose * 10/10000 * 0.5 = 1.1 * 0.0005 = 0.00055
        var candles = GenerateCandles(20, 1.10000m, 0.00050m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.Ranging, result.Regime);
    }

    // ── HighVolatility regime ──────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_HighVolatilityScore_ReturnsHighVolatility()
    {
        // volatility score > 30 (HighVolatilityThreshold)
        // ATR/avgClose*10000 > 30 → ATR > avgClose * 0.003
        // For avgClose=1.1 → ATR > 0.0033, use spread = 0.0070
        var candles = GenerateHighVolatilityCandles(20, 1.10000m, 0.00400m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.HighVolatility, result.Regime);
    }

    // ── LowVolatility regime (default fallback) ────────────────────────────────

    [Fact]
    public async Task DetectAsync_ModerateMixedConditions_ReturnsLowVolatility()
    {
        // Conditions: ADX > RangingAdxThreshold (20) so not ranging,
        // volatility >= TrendingMaxVolatility (20) so not trending,
        // volatility <= HighVolatilityThreshold (30) so not high volatility.
        // → Falls through to LowVolatility.
        // ADX proxy = volatility * 2 → need volatility between 20 and 30, so ADX between 40 and 60
        // volatility ~ 25: ATR/avgClose*10000 = 25 → ATR = 1.1*0.0025 = 0.00275
        // spread ~ 0.00275
        var candles = GenerateCandles(20, 1.10000m, 0.00280m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.LowVolatility, result.Regime);
    }

    // ── Crisis regime ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_ExtremeAtrSpikeWithSellOff_ReturnsCrisis()
    {
        // 40 calm candles establish a low ATR rolling average, then 4 bearish candles
        // with extreme spread spike ATR well above 2.5× the rolling average.
        var candles = new List<Candle>();
        decimal basePrice = 1.10000m;

        // 40 calm candles — very small spread (ATR ~ 0.00020)
        for (int i = 0; i < 40; i++)
        {
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = basePrice,
                High      = basePrice + 0.00010m,
                Low       = basePrice - 0.00010m,
                Close     = basePrice,
                Volume    = 100m,
                Timestamp = DateTime.UtcNow.AddHours(-44 + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }

        // 4 bearish candles with extreme spread (crisis sell-off)
        // Each candle: Open > Close (bearish), spread = 0.01400
        for (int i = 0; i < 4; i++)
        {
            decimal dropPrice = basePrice - 0.00500m * (i + 1);
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = dropPrice + 0.01000m,
                High      = dropPrice + 0.01200m,
                Low       = dropPrice - 0.00200m,
                Close     = dropPrice,
                Volume    = 500m,
                Timestamp = DateTime.UtcNow.AddHours(-4 + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.Crisis, result.Regime);
    }

    // ── Breakout regime ──────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_CompressionThenExpansion_ReturnsBreakout()
    {
        // Phase 1: 20 candles with moderate spread establish BBW rolling average.
        // Phase 2: 19 ultra-tight candles compress BBW well below 40% of the rolling avg.
        // Phase 3: 1 expansion candle whose TR >> ATR × 1.8.
        var candles = new List<Candle>();
        decimal basePrice = 1.10000m;

        // Phase 1: 30 moderate-spread candles to establish a non-zero BBW rolling average
        // (need >= bbPeriod + lookback = 40 pre-expansion candles total)
        for (int i = 0; i < 30; i++)
        {
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = basePrice,
                High      = basePrice + 0.00100m,
                Low       = basePrice - 0.00100m,
                Close     = basePrice + (i % 2 == 0 ? 0.00050m : -0.00050m),
                Volume    = 100m,
                Timestamp = DateTime.UtcNow.AddHours(-50 + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }

        // Phase 2: 19 very tight candles — almost no movement (compression)
        for (int i = 0; i < 19; i++)
        {
            candles.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Open      = basePrice,
                High      = basePrice + 0.00002m,
                Low       = basePrice - 0.00002m,
                Close     = basePrice,
                Volume    = 50m,
                Timestamp = DateTime.UtcNow.AddHours(-20 + i),
                IsClosed  = true,
                IsDeleted = false
            });
        }

        // Phase 3: 1 expansion candle — huge range relative to the compressed candles
        candles.Add(new Candle
        {
            Symbol    = "EURUSD",
            Timeframe = Timeframe.H1,
            Open      = basePrice,
            High      = basePrice + 0.00500m,
            Low       = basePrice - 0.00100m,
            Close     = basePrice + 0.00450m,
            Volume    = 400m,
            Timestamp = DateTime.UtcNow.AddHours(-1),
            IsClosed  = true,
            IsDeleted = false
        });

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(MarketRegimeEnum.Breakout, result.Regime);
    }

    // ── Confidence calculation ──────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_ConfidenceIsCappedAt1()
    {
        // Very high spread → high ADX proxy → confidence = min(1, adx/50)
        // With huge spread, ADX will be well above 50 → capped at 1.0
        var candles = GenerateHighVolatilityCandles(20, 1.10000m, 0.01000m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal(1m, result.Confidence);
    }

    [Fact]
    public async Task DetectAsync_ConfidenceIsPositiveAndBelowOrEqualOne()
    {
        var candles = GenerateCandles(20, 1.10000m, 0.00100m);

        var result = await _detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        Assert.InRange(result.Confidence, 0m, 1m);
    }

    // ── Snapshot fields ────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_SnapshotFieldsArePopulated()
    {
        var candles = GenerateCandles(20, 1.10000m, 0.00100m);

        var result = await _detector.DetectAsync("GBPUSD", Timeframe.D1, candles, CancellationToken.None);

        Assert.Equal("GBPUSD", result.Symbol);
        Assert.Equal(Timeframe.D1, result.Timeframe);
        Assert.True(result.ADX >= 0m);
        Assert.True(result.ATR >= 0m);
        Assert.True(result.BollingerBandWidth >= 0m);
        Assert.True(result.DetectedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task DetectAsync_SymbolIsUpperCased()
    {
        var candles = GenerateCandles(20, 1.10000m, 0.00100m);

        var result = await _detector.DetectAsync("eurusd", Timeframe.H1, candles, CancellationToken.None);

        Assert.Equal("EURUSD", result.Symbol);
    }

    // ── Custom options ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DetectAsync_CustomPeriod_UsesConfiguredPeriod()
    {
        var options = new MarketRegimeOptions { Period = 5 };
        var detector = new MarketRegimeDetector(options);

        // 6 candles is enough for period=5 (need period+1)
        var candles = GenerateCandles(6, 1.10000m, 0.00100m);

        var result = await detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None);

        // Should not throw — just verify it produces a valid regime
        Assert.True(Enum.IsDefined(typeof(MarketRegimeEnum), result.Regime));
    }

    [Fact]
    public async Task DetectAsync_CustomPeriod_InsufficientForThatPeriod_Throws()
    {
        var options = new MarketRegimeOptions { Period = 30 };
        var detector = new MarketRegimeDetector(options);

        // 20 candles < 31 required
        var candles = GenerateCandles(20, 1.10000m, 0.00100m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => detector.DetectAsync("EURUSD", Timeframe.H1, candles, CancellationToken.None));
    }
}
