using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.MLModels;

/// <summary>
/// Look-ahead audit (roadmap #7). A feature that consumes information from
/// timestamps strictly greater than the decision time produces fake backtest
/// edge — model sees future, model predicts future, Sharpe is inflated. This
/// test catches that class of bug by the redaction principle:
///
/// <para>
/// <b>If we mutate the "future" (the <c>current</c> candle's close, high, low)
/// and the feature vector at decision time changes, the feature is contaminated.</b>
/// </para>
///
/// <para>
/// Rationale: <c>BuildFeatureVector(window, current, prev)</c> is the
/// decision-time call. The <c>window</c> is closed candles ending at T−1.
/// The <c>current</c> candle represents the bar whose open we would execute on
/// — its high/low/close are strictly future information at decision time.
/// A legitimate feature may read <c>current.Open</c> (the decision-time price)
/// but must not consume <c>current.Close/High/Low/Volume</c>.
/// </para>
///
/// <para>
/// These tests run in CI on every MLFeatureHelper change. If they fail, the
/// feature vector is look-ahead-contaminated — the contaminated feature must
/// be removed OR shifted by +1 bar before the change can land.
/// </para>
/// </summary>
public class LookAheadAuditTest
{
    /// <summary>
    /// Deterministic candles so feature outputs are reproducible between
    /// redacted and unredacted runs.
    /// </summary>
    private static List<Candle> BuildDeterministicCandles(int count)
    {
        var list = new List<Candle>();
        decimal price = 1.1000m;
        for (int i = 0; i < count; i++)
        {
            decimal step = (i % 5 == 0 ? 0.0012m : -0.0008m);
            price += step;
            list.Add(new Candle
            {
                Symbol    = "EURUSD",
                Timeframe = Timeframe.H1,
                Timestamp = DateTime.UtcNow.Date.AddHours(-count + i),
                Open      = price,
                High      = price + 0.0015m,
                Low       = price - 0.0011m,
                Close     = price + step * 0.5m,
                Volume    = 1000 + i * 7,
                IsClosed  = true,
                IsDeleted = false,
            });
        }
        return list;
    }

    [Fact]
    public void V1_FeatureVector_IsIndependentOfCurrentCandleFuture()
    {
        // Arrange: build a clean lookback window + current + prev.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        float[] baseline = MLFeatureHelper.BuildFeatureVector(window, current, prev);

        // Act: redact the "future" fields of the current candle — these are values
        // a decision-time system cannot legitimately see. High/Low/Close are
        // known only after the bar closes; Volume likewise. Open is the
        // decision-time price, so leave it intact.
        var redactedCurrent = new Candle
        {
            Symbol    = current.Symbol,
            Timeframe = current.Timeframe,
            Timestamp = current.Timestamp,
            Open      = current.Open,
            High      = current.Open,  // redacted: pretend bar hasn't moved
            Low       = current.Open,  // redacted
            Close     = current.Open,  // redacted
            Volume    = 0,             // redacted
            IsClosed  = false,         // redacted: bar not closed at decision time
            IsDeleted = false,
        };

        float[] redacted = MLFeatureHelper.BuildFeatureVector(window, redactedCurrent, prev);

        // Assert: every feature must be identical. If any differ, that feature
        // index consumed future information from the current candle and is
        // look-ahead contaminated.
        Assert.Equal(baseline.Length, redacted.Length);
        var contaminatedIndices = new List<int>();
        for (int i = 0; i < baseline.Length; i++)
        {
            if (Math.Abs(baseline[i] - redacted[i]) > 1e-6f)
                contaminatedIndices.Add(i);
        }

        Assert.True(
            contaminatedIndices.Count == 0,
            $"Look-ahead contamination detected in feature indices: " +
            $"[{string.Join(", ", contaminatedIndices)}]. " +
            "These features consumed information from current.Close/High/Low/Volume which " +
            "is strictly future at decision time. Remove the feature OR shift its inputs " +
            "by +1 bar (use prev instead of current). See docs/adr/0013 for audit rationale.");
    }

    [Fact]
    public void V1_FeatureVector_IsDeterministicAcrossRuns()
    {
        // Sanity: identical inputs → identical outputs. If this fails the
        // redaction test above is meaningless because the feature function is
        // non-deterministic (uses RNG, time-of-day, etc.) and all subsequent
        // audits need re-design.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        float[] a = MLFeatureHelper.BuildFeatureVector(window, current, prev);
        float[] b = MLFeatureHelper.BuildFeatureVector(window, current, prev);

        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void V4_FeatureVector_IsIndependentOfCurrentCandleFuture()
    {
        // Same redaction principle applied to the full 48-feature V4 vector.
        // Tick-flow snapshot and event lookup are passed as deterministic stubs so
        // a shift in the V3→V4 extension slots (43..47) can't mask regressions
        // in the V3 base (0..42) either.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(
            DxyReturn5d: 0.01f, Us10YYieldChange5d: 0.002f, VixLevelNormalized: 0.3f);
        var eventFeat = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(
            HoursToNextHighNormalized: 0.5f,
            HoursSinceLastHighNormalized: 0.2f,
            HighMedPending6hNormalized: 0.1f);
        (float, float) minuteEvents = (0.4f, 0.3f);
        var tickFlow = new global::LascodiaTradingEngine.Application.Services.TickFlowSnapshot(
            TickDelta: 0.2m, CurrentSpread: 0.00012m, SpreadMean: 0.00010m, SpreadStdDev: 0.00002m,
            SpreadPercentileRank: 0.6m, SpreadRelVolatility: 0.2m, TickVolumeImbalance: 0.1m);

        float[] baseline = MLFeatureHelper.BuildFeatureVectorV4(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, minuteEvents, tickFlow);

        var redactedCurrent = new Candle
        {
            Symbol    = current.Symbol,
            Timeframe = current.Timeframe,
            Timestamp = current.Timestamp,
            Open      = current.Open,
            High      = current.Open,
            Low       = current.Open,
            Close     = current.Open,
            Volume    = 0,
            IsClosed  = false,
            IsDeleted = false,
        };

        float[] redacted = MLFeatureHelper.BuildFeatureVectorV4(
            window, redactedCurrent, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, minuteEvents, tickFlow);

        Assert.Equal(MLFeatureHelper.FeatureCountV4, baseline.Length);
        Assert.Equal(MLFeatureHelper.FeatureCountV4, redacted.Length);

        var contaminatedIndices = new List<int>();
        for (int i = 0; i < baseline.Length; i++)
        {
            if (Math.Abs(baseline[i] - redacted[i]) > 1e-6f)
                contaminatedIndices.Add(i);
        }

        Assert.True(
            contaminatedIndices.Count == 0,
            $"V4 look-ahead contamination in feature indices: [{string.Join(", ", contaminatedIndices)}]. " +
            "These consumed current.Close/High/Low/Volume at decision time. Shift the input by +1 bar.");
    }

    [Fact]
    public void V5_FeatureVector_IsIndependentOfCurrentCandleFuture()
    {
        // V5 = V4 + 4 synthetic-microstructure proxies derived from tick data.
        // The new slots must also be redaction-safe under the same audit principle.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(
            DxyReturn5d: 0.01f, Us10YYieldChange5d: 0.002f, VixLevelNormalized: 0.3f);
        var eventFeat = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(
            HoursToNextHighNormalized: 0.5f,
            HoursSinceLastHighNormalized: 0.2f,
            HighMedPending6hNormalized: 0.1f);
        (float, float) minuteEvents = (0.4f, 0.3f);
        var tickFlow = new global::LascodiaTradingEngine.Application.Services.TickFlowSnapshot(
            TickDelta: 0.2m, CurrentSpread: 0.00012m, SpreadMean: 0.00010m, SpreadStdDev: 0.00002m,
            SpreadPercentileRank: 0.6m, SpreadRelVolatility: 0.2m, TickVolumeImbalance: 0.1m,
            EffectiveSpread: 0.00015m, AmihudIlliquidity: 0.4m,
            RollSpreadEstimate: 0.00009m, VarianceRatio: 1.05m);

        float[] baseline = MLFeatureHelper.BuildFeatureVectorV5(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, minuteEvents, tickFlow);

        var redactedCurrent = new Candle
        {
            Symbol = current.Symbol, Timeframe = current.Timeframe, Timestamp = current.Timestamp,
            Open = current.Open, High = current.Open, Low = current.Open,
            Close = current.Open, Volume = 0, IsClosed = false, IsDeleted = false,
        };

        float[] redacted = MLFeatureHelper.BuildFeatureVectorV5(
            window, redactedCurrent, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, minuteEvents, tickFlow);

        Assert.Equal(MLFeatureHelper.FeatureCountV5, baseline.Length);
        var contaminated = new List<int>();
        for (int i = 0; i < baseline.Length; i++)
            if (Math.Abs(baseline[i] - redacted[i]) > 1e-6f) contaminated.Add(i);
        Assert.True(contaminated.Count == 0,
            $"V5 look-ahead contamination at indices [{string.Join(", ", contaminated)}].");
    }

    [Fact]
    public void V5_FeatureVector_ZeroFillsMicrostructureWhenTickFlowNull()
    {
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];
        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(
            0.01f, 0.002f, 0.3f);
        var eventFeat = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(
            0.5f, 0.2f, 0.1f);

        float[] v5 = MLFeatureHelper.BuildFeatureVectorV5(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, (1.0f, 1.0f), tickFlow: null);

        Assert.Equal(MLFeatureHelper.FeatureCountV5, v5.Length);
        // V4 microstructure (45,46,47) AND V5 microstructure (48,49,50,51) all zero.
        for (int i = MLFeatureHelper.FeatureCountV3 + 2; i < v5.Length; i++)
            Assert.Equal(0f, v5[i]);
    }

    [Fact]
    public void V6_FeatureVector_IsIndependentOfCurrentCandleFuture()
    {
        // V6 = V5 + 5 real-DOM features. Order-book data is keyed by symbol+timestamp,
        // not by candle OHLCV — so the redaction test must show V6 also passes.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(0.01f, 0.002f, 0.3f);
        var eventFeat  = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(0.5f, 0.2f, 0.1f);
        var tickFlow   = new global::LascodiaTradingEngine.Application.Services.TickFlowSnapshot(
            TickDelta: 0.2m, CurrentSpread: 0.00012m, SpreadMean: 0.00010m, SpreadStdDev: 0.00002m,
            SpreadPercentileRank: 0.6m, SpreadRelVolatility: 0.2m, TickVolumeImbalance: 0.1m,
            EffectiveSpread: 0.00015m, AmihudIlliquidity: 0.4m,
            RollSpreadEstimate: 0.00009m, VarianceRatio: 1.05m);
        var orderBook = new global::LascodiaTradingEngine.Application.Services.OrderBookFeatureSnapshot(
            BookImbalanceTop1: 0.55m, BookImbalanceTop5: 0.52m,
            TotalLiquidityNorm: 0.7m, BookSlopeBid: 0.1m, BookSlopeAsk: -0.05m);

        float[] baseline = MLFeatureHelper.BuildFeatureVectorV6(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, (0.4f, 0.3f), tickFlow, orderBook);

        var redactedCurrent = new Candle
        {
            Symbol = current.Symbol, Timeframe = current.Timeframe, Timestamp = current.Timestamp,
            Open = current.Open, High = current.Open, Low = current.Open,
            Close = current.Open, Volume = 0, IsClosed = false, IsDeleted = false,
        };
        float[] redacted = MLFeatureHelper.BuildFeatureVectorV6(
            window, redactedCurrent, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, (0.4f, 0.3f), tickFlow, orderBook);

        Assert.Equal(MLFeatureHelper.FeatureCountV6, baseline.Length);
        var contaminated = new List<int>();
        for (int i = 0; i < baseline.Length; i++)
            if (Math.Abs(baseline[i] - redacted[i]) > 1e-6f) contaminated.Add(i);
        Assert.True(contaminated.Count == 0,
            $"V6 look-ahead contamination at indices [{string.Join(", ", contaminated)}].");
    }

    [Fact]
    public void V6_FeatureVector_ZeroFillsDomWhenOrderBookNull()
    {
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];
        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(0.01f, 0.002f, 0.3f);
        var eventFeat  = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(0.5f, 0.2f, 0.1f);

        float[] v6 = MLFeatureHelper.BuildFeatureVectorV6(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, (1.0f, 1.0f), tickFlow: null, orderBook: null);

        Assert.Equal(MLFeatureHelper.FeatureCountV6, v6.Length);
        // Last 5 slots (DOM) all zero when order book absent.
        for (int i = MLFeatureHelper.FeatureCountV5; i < v6.Length; i++)
            Assert.Equal(0f, v6[i]);
    }

    [Fact]
    public void V4_FeatureVector_ZeroFillsMicrostructureWhenTickFlowNull()
    {
        // Contract: V4 inference runs on symbols with no persisted ticks yet
        // (TickFlowProvider returns null). The last three slots must zero-fill so a
        // V4-trained model can still score on cold-tick symbols without NaN explosion.
        var all     = BuildDeterministicCandles(MLFeatureHelper.LookbackWindow + 2);
        var window  = all.GetRange(0, MLFeatureHelper.LookbackWindow);
        var prev    = all[MLFeatureHelper.LookbackWindow - 1];
        var current = all[MLFeatureHelper.LookbackWindow];

        var crossAsset = new global::LascodiaTradingEngine.Application.Services.ML.CrossAssetSnapshot(
            DxyReturn5d: 0.01f, Us10YYieldChange5d: 0.002f, VixLevelNormalized: 0.3f);
        var eventFeat = new global::LascodiaTradingEngine.Application.Services.ML.EventFeatureSnapshot(
            HoursToNextHighNormalized: 0.5f,
            HoursSinceLastHighNormalized: 0.2f,
            HighMedPending6hNormalized: 0.1f);

        float[] v4 = MLFeatureHelper.BuildFeatureVectorV4(
            window, current, prev, new Dictionary<string, double[]>(), "EURUSD",
            crossAsset, eventFeat, (1.0f, 1.0f), tickFlow: null);

        Assert.Equal(MLFeatureHelper.FeatureCountV4, v4.Length);
        Assert.Equal(0f, v4[MLFeatureHelper.FeatureCountV3 + 2]);
        Assert.Equal(0f, v4[MLFeatureHelper.FeatureCountV3 + 3]);
        Assert.Equal(0f, v4[MLFeatureHelper.FeatureCountV3 + 4]);
    }
}
