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
}
