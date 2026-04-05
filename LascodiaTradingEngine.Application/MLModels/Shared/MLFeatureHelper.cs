using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.MLModels.Shared;

/// <summary>
/// Shared static feature-engineering and indicator library used by both
/// <c>BaggedLogisticTrainer</c> (training) and <c>MLSignalScorer</c> (inference).
///
/// Keeping a single source of truth guarantees perfect feature/inference parity:
/// the same 33 numbers are produced at training time and at scoring time.
/// </summary>
public static class MLFeatureHelper
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>Number of historical candles required to compute the full feature vector.</summary>
    public const int LookbackWindow = 30;

    /// <summary>Total number of features in every feature vector.</summary>
    public const int FeatureCount = 33;

    /// <summary>Number of per-bar channels in the TCN sequence representation.</summary>
    public const int SequenceChannelCount = 9;

    /// <summary>
    /// Ordered channel names for the per-timestep sequence features used by TCN models.
    /// Each channel is a scalar computed independently at every bar in the lookback window.
    /// </summary>
    public static readonly string[] SequenceChannelNames =
    [
        "NormReturn",   // (Close_t − Close_{t−1}) / ATR_t  (0 at t=0)
        "NormRange",    // (High_t − Low_t) / ATR_t
        "BodyRatio",    // (Close_t − Open_t) / max(High_t − Low_t, ε)
        "UpperWick",    // (High_t − max(Open_t, Close_t)) / max(High_t − Low_t, ε)
        "LowerWick",    // (min(Open_t, Close_t) − Low_t) / max(High_t − Low_t, ε)
        "NormVolume",   // log(Volume_t / AvgVolume + 0.01), clamped [-3, 3]
        "HourSin",      // sin(2π × hour / 24)
        "HourCos",      // cos(2π × hour / 24)
        "DowSin",       // sin(2π × dayOfWeek / 7)
    ];

    /// <summary>
    /// Ordered feature names. Index must match the order in
    /// <see cref="BuildFeatureVector"/>.
    /// </summary>
    public static readonly string[] FeatureNames =
    [
        // ── Price momentum / trend ──────────────────────────────────────────
        "SmaRatio",       "EmaRatio",       "Rsi",            "PctVsSma",
        "MacdNorm",       "BollPctB",       "AtrNorm",        "BodyRatio",      "VolumeRatio",
        // ── Directional / oscillator ────────────────────────────────────────
        "Adx",            "StochK",         "Roc5",           "Roc10",          "Roc20",
        // ── Session / calendar (cyclical encoding) ───────────────────────────
        "HourSin",        "HourCos",        "DayOfWeekSin",   "DayOfWeekCos",
        // ── Candle structure ─────────────────────────────────────────────────
        "IsPinBar",       "IsEngulfing",    "IsDoji",         "SpreadProxy",
        // ── Lagged indicator trajectories ────────────────────────────────────
        "RsiLag1",        "RsiLag2",        "MacdLag1",       "BollPctBLag1",
        // ── COT sentiment ────────────────────────────────────────────────────
        "CotBaseNetNorm", "CotBaseMomentum",
        // ── COT availability sentinel (1 = real data, 0 = absent) ────────────
        "HasCotData",
        // ── Rec #253: CDA augmentation ───────────────────────────────────────
        "CdaMixRatio",    "CdaRangeNorm",
        // ── Rec #263: Feature grouping ───────────────────────────────────────
        "FeatureGroupCorr", "MomentumVolatilityRatio"
    ];

    /// <summary>Extended feature vector length including cross-pair, news, sentiment, and proxy features.</summary>
    public const int ExtendedFeatureCount = 57;

    /// <summary>Names of the 24 extended features (12 cross-pair + news + sentiment + tick flow + economic surprise + 6 proxy).</summary>
    public static readonly string[] ExtendedFeatureNames =
    [
        "XP1_Return", "XP1_RsiDelta", "XP1_AtrRatio", "XP1_Correlation",
        "XP2_Return", "XP2_RsiDelta", "XP2_AtrRatio", "XP2_Correlation",
        "XP3_Return", "XP3_RsiDelta", "XP3_AtrRatio", "XP3_Correlation",
        "NewsProximity", "SentimentAlignment",
        "TickDelta", "TickDeltaDivergence", "SpreadZScore", "EconomicSurprise",
        "AtrAcceleration", "BbwRateOfChange", "VolPercentile",
        "TickIntensity", "BidAskImbalance", "CalendarDensity",
    ];

    /// <summary>Pre-computed proxy feature data for the extended ML vector.</summary>
    public sealed record ProxyFeatureData(
        float AtrAcceleration,
        float BbwRateOfChange,
        float VolPercentile,
        float TickIntensity,
        float BidAskImbalance,
        float CalendarDensity);

    // ── Training sample builder ───────────────────────────────────────────────

    /// <summary>
    /// Converts a chronologically ordered list of closed candles into labelled
    /// feature vectors suitable for supervised training.
    /// </summary>
    /// <param name="candles">Must have at least <see cref="LookbackWindow"/> + 2 entries.</param>
    /// <param name="cotLookup">
    /// Optional function that returns a <see cref="CotFeatureEntry"/> for a given UTC timestamp.
    /// Pass <c>null</c> when COT data is unavailable; features default to zero.
    /// </param>
    public static List<TrainingSample> BuildTrainingSamples(
        List<Candle> candles,
        Func<DateTime, CotFeatureEntry>? cotLookup = null)
    {
        var samples = new List<TrainingSample>(candles.Count);

        for (int i = LookbackWindow; i < candles.Count - 1; i++)
        {
            var window  = candles.GetRange(i - LookbackWindow, LookbackWindow);
            var current = candles[i];
            var prev    = window[^1]; // candles[i-1]

            var cotEntry = cotLookup?.Invoke(current.Timestamp) ?? CotFeatureEntry.Zero;
            var features = BuildFeatureVector(window, current, prev, cotEntry);

            int   direction = candles[i + 1].Close > candles[i].Close ? 1 : 0;
            float atr       = (float)CalculateATR(window, 14);
            float magnitude = atr > 0
                ? Clamp((float)((double)(candles[i + 1].Close - candles[i].Close) / (double)atr), -5f, 5f)
                : 0f;

            samples.Add(new TrainingSample(features, direction, magnitude));
        }

        return samples;
    }

    // ── Triple-barrier labelled training sample builder ──────────────────────

    /// <summary>
    /// Builds training samples using the triple-barrier labelling method.
    /// A label of 1 (Buy) is assigned when the profit-target barrier is hit first;
    /// 0 (Sell/flat) when the stop-loss or time-horizon barrier fires first.
    /// This aligns labels directly with trading P&amp;L rather than next-bar direction.
    /// </summary>
    /// <param name="candles">Chronologically ordered closed candles.</param>
    /// <param name="cotLookup">Optional COT data lookup.</param>
    /// <param name="profitAtrMult">ATR multiplier for the profit-target barrier (e.g. 1.5).</param>
    /// <param name="stopAtrMult">ATR multiplier for the stop-loss barrier (e.g. 1.0).</param>
    /// <param name="horizonBars">Maximum bars to hold before the time-horizon barrier fires.</param>
    public static List<TrainingSample> BuildTrainingSamplesWithTripleBarrier(
        List<Candle>                    candles,
        Func<DateTime, CotFeatureEntry>? cotLookup  = null,
        float                           profitAtrMult = 1.5f,
        float                           stopAtrMult   = 1.0f,
        int                             horizonBars   = 24)
    {
        var samples = new List<TrainingSample>(candles.Count);

        for (int i = LookbackWindow; i < candles.Count - 1; i++)
        {
            var window  = candles.GetRange(i - LookbackWindow, LookbackWindow);
            var current = candles[i];
            var prev    = window[^1];

            var cotEntry = cotLookup?.Invoke(current.Timestamp) ?? CotFeatureEntry.Zero;
            var features = BuildFeatureVector(window, current, prev, cotEntry);

            float atr = (float)CalculateATR(window, 14);
            if (atr <= 0f)
            {
                // Degenerate candle window — fall back to next-bar direction label
                int fallbackDir = candles[i + 1].Close > candles[i].Close ? 1 : 0;
                samples.Add(new TrainingSample(features, fallbackDir, 0f));
                continue;
            }

            float profitTarget = atr * profitAtrMult;
            float stopLoss     = atr * stopAtrMult;
            float entry        = (float)current.Close;

            int   label     = 0;   // default: stop-loss / time-horizon → 0
            float magnitude = 0f;

            // Walk forward up to horizonBars bars to find which barrier fires first
            int maxLook = Math.Min(i + 1 + horizonBars, candles.Count);
            for (int j = i + 1; j < maxLook; j++)
            {
                float hi = (float)candles[j].High;
                float lo = (float)candles[j].Low;

                bool profitHit = hi - entry >= profitTarget;
                bool stopHit   = entry - lo  >= stopLoss;

                if (profitHit && stopHit)
                {
                    // Both hit on the same bar — assume profit target fires first (intrabar)
                    label     = 1;
                    magnitude = Clamp(profitTarget / atr, -5f, 5f);
                    break;
                }
                if (profitHit)
                {
                    label     = 1;
                    magnitude = Clamp((hi - entry) / atr, -5f, 5f);
                    break;
                }
                if (stopHit)
                {
                    label     = 0;
                    magnitude = Clamp((lo - entry) / atr, -5f, 5f);
                    break;
                }
            }

            // Time-horizon expiry: use close of the last bar as magnitude
            if (label == 0 && magnitude == 0f && maxLook > i + 1)
            {
                float exitClose = (float)candles[maxLook - 1].Close;
                magnitude = Clamp((exitClose - entry) / atr, -5f, 5f);
                label     = exitClose > entry ? 1 : 0;
            }

            samples.Add(new TrainingSample(features, label, magnitude));
        }

        return samples;
    }

    // ── Single-bar feature vector ─────────────────────────────────────────────

    /// <summary>
    /// Computes the 28-element feature vector for a single candle.
    /// Used by <c>MLSignalScorer</c> at inference time.
    /// </summary>
    /// <param name="window">
    /// The <see cref="LookbackWindow"/> candles immediately preceding
    /// <paramref name="current"/> (i.e. candles[i-30..i-1]).
    /// </param>
    /// <param name="current">The candle to score (candles[i]).</param>
    /// <param name="previous">candles[i-1] = window.Last().</param>
    /// <param name="cotEntry">COT sentiment; pass <see cref="CotFeatureEntry.Zero"/> if unavailable.</param>
    public static float[] BuildFeatureVector(
        List<Candle>   window,
        Candle         current,
        Candle         previous,
        CotFeatureEntry? cotEntry = null)
    {
        cotEntry ??= CotFeatureEntry.Zero;

        var closes  = window.Select(c => c.Close).ToList();
        var lag1Win = window.Take(window.Count - 1).ToList();          // 29 bars ending at i-2
        var lag2Win = window.Take(window.Count - 2).ToList();          // 28 bars ending at i-3

        // ── SMA5 / SMA20 ──────────────────────────────────────────────────────
        float sma5     = (float)window.TakeLast(5).Average(c => (double)c.Close);
        float sma20    = (float)window.Average(c => (double)c.Close);
        float smaRatio = sma20 > 0 ? sma5 / sma20 : 1f;

        // ── EMA9 / EMA21 ──────────────────────────────────────────────────────
        float ema9     = (float)CalculateEMA(closes, 9);
        float ema21    = (float)CalculateEMA(closes, 21);
        float emaRatio = ema21 > 0 ? ema9 / ema21 : 1f;

        // ── RSI(14) [0, 1] ────────────────────────────────────────────────────
        float rsi = (float)CalculateRSI(closes, 14) / 100f;

        // ── % distance from SMA20, clipped to [-3, 3] ────────────────────────
        float pctVsSma = sma20 > 0
            ? Clamp((float)((double)(current.Close - (decimal)sma20) / (double)sma20 * 100) / 5f, -3f, 3f)
            : 0f;

        // ── MACD histogram normalised by SMA20 ────────────────────────────────
        float macdLine = (float)(CalculateEMA(closes, 12) - CalculateEMA(closes, 26));
        float macdNorm = sma20 > 0 ? Clamp(macdLine / sma20 * 100f, -3f, 3f) : 0f;

        // ── Bollinger %B [0, 1] ───────────────────────────────────────────────
        float stdDev    = (float)Math.Sqrt(window.Average(c => Math.Pow((double)c.Close - sma20, 2)));
        float upperBand = sma20 + 2 * stdDev;
        float lowerBand = sma20 - 2 * stdDev;
        float bollPctB  = (upperBand - lowerBand) > 0
            ? Clamp((float)((double)current.Close - lowerBand) / (upperBand - lowerBand), 0f, 1f)
            : 0.5f;

        // ── ATR(14)-normalised 1-bar return ───────────────────────────────────
        float atr     = (float)CalculateATR(window, 14);
        float atrNorm = atr > 0
            ? Clamp((float)((double)(current.Close - previous.Close) / (double)atr), -3f, 3f)
            : 0f;

        // ── Candle body ratio [0, 1] ──────────────────────────────────────────
        float highLow   = (float)(current.High - current.Low);
        float bodyAbs   = (float)Math.Abs((double)(current.Close - current.Open));
        float bodyRatio = highLow > 0 ? Clamp(bodyAbs / highLow, 0f, 1f) : 0.5f;

        // ── Volume ratio (log-scaled, [-3, 3]) ────────────────────────────────
        float avgVol      = (float)window.Average(c => (double)c.Volume);
        float volumeRatio = avgVol > 0
            ? Clamp((float)Math.Log((double)current.Volume / avgVol + 0.01), -3f, 3f)
            : 0f;

        // ── ADX(14) [0, 1] ────────────────────────────────────────────────────
        float adx = Clamp((float)CalculateADX(window, 14) / 100f, 0f, 1f);

        // ── Stochastic %K(14) [0, 1] ──────────────────────────────────────────
        float stochK = (float)CalculateStochK(window, 14);

        // ── Rate of change at 5 / 10 / 20 bars ───────────────────────────────
        float roc5  = CalculateROC(closes, 5);
        float roc10 = CalculateROC(closes, 10);
        float roc20 = CalculateROC(closes, 20);

        // ── Session / calendar (cyclical encoding) ────────────────────────────
        float hour    = current.Timestamp.Hour;
        float dow     = (float)current.Timestamp.DayOfWeek;
        float hourSin = (float)Math.Sin(2 * Math.PI * hour / 24.0);
        float hourCos = (float)Math.Cos(2 * Math.PI * hour / 24.0);
        float dowSin  = (float)Math.Sin(2 * Math.PI * dow  /  7.0);
        float dowCos  = (float)Math.Cos(2 * Math.PI * dow  /  7.0);

        // ── Candle pattern flags ──────────────────────────────────────────────
        float longestWick = Math.Max(
            (float)(current.High - Math.Max(current.Open, current.Close)),
            (float)(Math.Min(current.Open, current.Close) - current.Low));
        float isPinBar = (highLow > 0 && bodyAbs < 0.3f * highLow && longestWick > 0.6f * highLow) ? 1f : 0f;

        float prevBodyDir = (float)(previous.Close - previous.Open);
        float currBodyDir = (float)(current.Close  - current.Open);
        float isEngulfing = 0f;
        if (Math.Abs(currBodyDir) > 0 && Math.Abs(prevBodyDir) > 0)
        {
            bool bullEngulf = currBodyDir > 0 && prevBodyDir < 0
                && current.Open  <= Math.Min(previous.Open, previous.Close)
                && current.Close >= Math.Max(previous.Open, previous.Close);
            bool bearEngulf = currBodyDir < 0 && prevBodyDir > 0
                && current.Open  >= Math.Max(previous.Open, previous.Close)
                && current.Close <= Math.Min(previous.Open, previous.Close);
            isEngulfing = bullEngulf ? 1f : bearEngulf ? -1f : 0f;
        }

        float isDoji       = (highLow > 0 && bodyAbs < 0.1f * highLow) ? 1f : 0f;
        float spreadProxy  = atr > 0 ? Clamp(highLow / atr, 0f, 3f) : 0f;

        // ── Lagged indicator values (t-1, t-2 windows) ───────────────────────
        var lag1Closes = lag1Win.Select(c => c.Close).ToList();
        var lag2Closes = lag2Win.Select(c => c.Close).ToList();

        float rsiLag1 = lag1Closes.Count >= 15 ? (float)CalculateRSI(lag1Closes, 14) / 100f : rsi;
        float rsiLag2 = lag2Closes.Count >= 15 ? (float)CalculateRSI(lag2Closes, 14) / 100f : rsiLag1;

        float sma20_lag1  = lag1Win.Count > 0 ? (float)lag1Win.Average(c => (double)c.Close) : sma20;
        float macdLine_l1 = (float)(CalculateEMA(lag1Closes, 12) - CalculateEMA(lag1Closes, 26));
        float macdLag1    = sma20_lag1 > 0 ? Clamp(macdLine_l1 / sma20_lag1 * 100f, -3f, 3f) : 0f;

        float stdDev_l1     = lag1Win.Count > 0 ? (float)Math.Sqrt(lag1Win.Average(c => Math.Pow((double)c.Close - sma20_lag1, 2))) : stdDev;
        float upper_l1      = sma20_lag1 + 2 * stdDev_l1;
        float lower_l1      = sma20_lag1 - 2 * stdDev_l1;
        float prevClose     = (float)previous.Close;
        float bollPctBLag1  = (upper_l1 - lower_l1) > 0
            ? Clamp((prevClose - lower_l1) / (upper_l1 - lower_l1), 0f, 1f)
            : 0.5f;

        // ── COT sentiment ─────────────────────────────────────────────────────
        float cotNetNorm  = cotEntry.NetNorm;
        float cotMomentum = cotEntry.Momentum;
        // 1.0 = real COT report was found; 0.0 = absent (prevents silent zero-padding)
        float hasCotData  = (cotEntry?.HasData ?? false) ? 1f : 0f;

        // ── Rec #253: CDA augmentation ──────────────────────────────────────
        // CdaMixRatio: ratio of close-to-open range vs high-to-low range across the window,
        // capturing how much of the bar range is directional vs noise.
        float totalHL = window.Sum(c => (float)(c.High - c.Low));
        float totalCO = window.Sum(c => Math.Abs((float)(c.Close - c.Open)));
        float cdaMixRatio = totalHL > 0 ? Clamp(totalCO / totalHL, 0f, 1f) : 0.5f;

        // CdaRangeNorm: current bar range normalised by the window average range.
        float avgRange = totalHL / window.Count;
        float cdaRangeNorm = avgRange > 0 ? Clamp(highLow / avgRange, 0f, 3f) : 1f;

        // ── Rec #263: Feature grouping ──────────────────────────────────────
        // FeatureGroupCorr: correlation proxy between momentum group (smaRatio, emaRatio, rsi)
        // and oscillator group (stochK, adx) — detects regime where indicators agree/disagree.
        float momAvg = (smaRatio + emaRatio + rsi) / 3f;
        float oscAvg = (stochK + adx) / 2f;
        float featureGroupCorr = Clamp(momAvg * oscAvg * 4f - 1f, -1f, 1f);

        // MomentumVolatilityRatio: momentum strength relative to volatility.
        float momStrength = Math.Abs(roc5) + Math.Abs(roc10);
        float momentumVolatilityRatio = atrNorm != 0
            ? Clamp(momStrength / (Math.Abs(atrNorm) + 0.01f), -3f, 3f)
            : 0f;

        return
        [
            smaRatio,   emaRatio,   rsi,        pctVsSma,
            macdNorm,   bollPctB,   atrNorm,    bodyRatio,    volumeRatio,
            adx,        stochK,     roc5,       roc10,        roc20,
            hourSin,    hourCos,    dowSin,     dowCos,
            isPinBar,   isEngulfing,isDoji,     spreadProxy,
            rsiLag1,    rsiLag2,    macdLag1,   bollPctBLag1,
            cotNetNorm, cotMomentum, hasCotData,
            cdaMixRatio, cdaRangeNorm,
            featureGroupCorr, momentumVolatilityRatio
        ];
    }

    // ── Sequence feature builder (for TCN temporal models) ─────────────────────

    /// <summary>
    /// Builds a per-timestep feature matrix <c>[T][C]</c> from a candle window,
    /// where T = <paramref name="window"/>.Count and C = <see cref="SequenceChannelCount"/>.
    /// Each channel is a bar-level feature that does not require indicator lookback
    /// beyond the window itself, allowing the TCN's causal dilated convolutions
    /// to learn temporal patterns directly from raw price action.
    /// </summary>
    /// <param name="window">
    /// Chronologically ordered candles of length <see cref="LookbackWindow"/>.
    /// </param>
    /// <returns>
    /// A jagged array <c>float[T][C]</c>. Outer index = time step (0 = oldest),
    /// inner index = channel matching <see cref="SequenceChannelNames"/>.
    /// </returns>
    public static float[][] BuildSequenceFeatures(List<Candle> window)
    {
        int T = window.Count;
        int C = SequenceChannelCount;
        var seq = new float[T][];

        // Running ATR(14) via exponential smoothing for normalisation
        double atrEma = 0;
        const int atrPeriod = 14;
        double atrK = 2.0 / (atrPeriod + 1);

        // Running average volume (simple rolling)
        double volumeSum = 0;

        for (int t = 0; t < T; t++)
        {
            var bar = window[t];
            float high   = (float)bar.High;
            float low    = (float)bar.Low;
            float open   = (float)bar.Open;
            float close  = (float)bar.Close;
            float hl     = high - low;
            float hlSafe = hl > 0 ? hl : 1e-6f;

            // Update running ATR
            double tr = t > 0
                ? Math.Max(hl, Math.Max(
                    Math.Abs(high - (float)window[t - 1].Close),
                    Math.Abs(low  - (float)window[t - 1].Close)))
                : hl;
            atrEma = t == 0 ? tr : atrEma * (1 - atrK) + tr * atrK;
            float atr = (float)Math.Max(atrEma, 1e-8);

            // Update running volume average
            volumeSum += (double)bar.Volume;
            double avgVol = volumeSum / (t + 1);

            // ── Channel values ────────────────────────────────────────────────
            var channels = new float[C];

            // 0: NormReturn
            channels[0] = t > 0
                ? Clamp((close - (float)window[t - 1].Close) / atr, -5f, 5f)
                : 0f;

            // 1: NormRange
            channels[1] = Clamp(hl / atr, 0f, 5f);

            // 2: BodyRatio
            channels[2] = (close - open) / hlSafe;

            // 3: UpperWick
            channels[3] = (high - Math.Max(open, close)) / hlSafe;

            // 4: LowerWick
            channels[4] = (Math.Min(open, close) - low) / hlSafe;

            // 5: NormVolume
            channels[5] = avgVol > 0
                ? Clamp((float)Math.Log((double)bar.Volume / avgVol + 0.01), -3f, 3f)
                : 0f;

            // 6: HourSin
            float hour = bar.Timestamp.Hour;
            channels[6] = (float)Math.Sin(2 * Math.PI * hour / 24.0);

            // 7: HourCos
            channels[7] = (float)Math.Cos(2 * Math.PI * hour / 24.0);

            // 8: DowSin
            float dow = (float)bar.Timestamp.DayOfWeek;
            channels[8] = (float)Math.Sin(2 * Math.PI * dow / 7.0);

            seq[t] = channels;
        }

        return seq;
    }

    /// <summary>
    /// Builds training samples with both flat features and sequence features (for TCN).
    /// </summary>
    public static List<TrainingSample> BuildTrainingSamplesWithSequence(
        List<Candle> candles,
        Func<DateTime, CotFeatureEntry>? cotLookup = null)
    {
        var samples = new List<TrainingSample>(candles.Count);

        for (int i = LookbackWindow; i < candles.Count - 1; i++)
        {
            var window  = candles.GetRange(i - LookbackWindow, LookbackWindow);
            var current = candles[i];
            var prev    = window[^1];

            var cotEntry = cotLookup?.Invoke(current.Timestamp) ?? CotFeatureEntry.Zero;
            var features = BuildFeatureVector(window, current, prev, cotEntry);
            var seqFeatures = BuildSequenceFeatures(window);

            int   direction = candles[i + 1].Close > candles[i].Close ? 1 : 0;
            float atr       = (float)CalculateATR(window, 14);
            float magnitude = atr > 0
                ? Clamp((float)((double)(candles[i + 1].Close - candles[i].Close) / (double)atr), -5f, 5f)
                : 0f;

            samples.Add(new TrainingSample(features, direction, magnitude, seqFeatures));
        }

        return samples;
    }

    /// <summary>
    /// Computes Z-score standardisation parameters for sequence features across all samples.
    /// Returns means and stds of shape [C] (one per channel).
    /// </summary>
    public static (float[] Means, float[] Stds) ComputeSequenceStandardization(
        List<float[][]> sequences)
    {
        int C = SequenceChannelCount;
        var means = new float[C];
        var stds  = new float[C];

        if (sequences.Count == 0) return (means, stds);

        // Two-pass: compute mean, then variance
        long totalSteps = 0;
        var channelSum = new double[C];
        foreach (var seq in sequences)
            foreach (var step in seq)
            {
                for (int c = 0; c < C; c++) channelSum[c] += step[c];
                totalSteps++;
            }

        if (totalSteps == 0) return (means, stds);
        for (int c = 0; c < C; c++) means[c] = (float)(channelSum[c] / totalSteps);

        var channelVarSum = new double[C];
        foreach (var seq in sequences)
            foreach (var step in seq)
                for (int c = 0; c < C; c++)
                {
                    double d = step[c] - means[c];
                    channelVarSum[c] += d * d;
                }

        for (int c = 0; c < C; c++)
            stds[c] = (float)Math.Sqrt(channelVarSum[c] / totalSteps);

        return (means, stds);
    }

    /// <summary>
    /// Z-score standardises a sequence feature matrix in-place using pre-computed means and stds.
    /// </summary>
    public static float[][] StandardizeSequence(float[][] seq, float[] means, float[] stds)
    {
        int C = means.Length;
        var result = new float[seq.Length][];
        for (int t = 0; t < seq.Length; t++)
        {
            result[t] = new float[C];
            for (int c = 0; c < C; c++)
            {
                float std = stds[c] > 1e-8f ? stds[c] : 1f;
                result[t][c] = (seq[t][c] - means[c]) / std;
            }
        }
        return result;
    }

    // ── Technical indicator helpers (public for unit-testing) ─────────────────

    public static double CalculateEMA(List<decimal> closes, int period)
    {
        if (closes.Count < period) return (double)closes.Last();
        double k   = 2.0 / (period + 1);
        double ema = (double)closes.Take(period).Average();
        for (int i = period; i < closes.Count; i++)
            ema = (double)closes[i] * k + ema * (1 - k);
        return ema;
    }

    public static double CalculateATR(List<Candle> candles, int period)
    {
        if (candles.Count < 2) return 0.0;
        var trs = new List<double>();
        for (int i = 1; i < candles.Count; i++)
        {
            double hl  = (double)(candles[i].High - candles[i].Low);
            double hpc = Math.Abs((double)(candles[i].High  - candles[i - 1].Close));
            double lpc = Math.Abs((double)(candles[i].Low   - candles[i - 1].Close));
            trs.Add(Math.Max(hl, Math.Max(hpc, lpc)));
        }
        return trs.TakeLast(period).Average();
    }

    public static double CalculateRSI(List<decimal> closes, int period)
    {
        if (closes.Count < period + 1) return 50.0;
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            double change = (double)(closes[i] - closes[i - 1]);
            if (change > 0) avgGain += change; else avgLoss += -change;
        }
        avgGain /= period;
        avgLoss /= period;
        if (avgLoss == 0) return 100;
        return 100 - 100 / (1 + avgGain / avgLoss);
    }

    /// <summary>
    /// ADX(period) via Wilder smoothing. Returns [0, 100];
    /// values above 25 indicate a trending market.
    /// </summary>
    public static double CalculateADX(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period * 2) return 25.0;

        double smoothTR = 0, smoothPlusDM = 0, smoothMinusDM = 0;
        for (int i = 1; i <= period; i++)
        {
            double tr          = TrueRange(candles[i], candles[i - 1]);
            var (pDM, nDM)     = DirectionalMove(candles[i], candles[i - 1]);
            smoothTR          += tr;
            smoothPlusDM      += pDM;
            smoothMinusDM     += nDM;
        }

        var dxValues = new List<double>();
        for (int i = period + 1; i < candles.Count; i++)
        {
            double tr          = TrueRange(candles[i], candles[i - 1]);
            var (pDM, nDM)     = DirectionalMove(candles[i], candles[i - 1]);
            smoothTR           = smoothTR       - smoothTR      / period + tr;
            smoothPlusDM       = smoothPlusDM   - smoothPlusDM  / period + pDM;
            smoothMinusDM      = smoothMinusDM  - smoothMinusDM / period + nDM;

            double plusDI  = smoothTR > 0 ? 100 * smoothPlusDM  / smoothTR : 0;
            double minusDI = smoothTR > 0 ? 100 * smoothMinusDM / smoothTR : 0;
            double diSum   = plusDI + minusDI;
            dxValues.Add(diSum > 0 ? 100 * Math.Abs(plusDI - minusDI) / diSum : 0);
        }
        return dxValues.Count > 0 ? dxValues.TakeLast(period).Average() : 25.0;
    }

    private static double TrueRange(Candle cur, Candle prev) =>
        Math.Max((double)(cur.High - cur.Low),
            Math.Max(Math.Abs((double)(cur.High - prev.Close)),
                     Math.Abs((double)(cur.Low  - prev.Close))));

    private static (double PlusDM, double MinusDM) DirectionalMove(Candle cur, Candle prev)
    {
        double upMove   = (double)(cur.High - prev.High);
        double downMove = (double)(prev.Low  - cur.Low);
        double pDM = upMove   > downMove && upMove   > 0 ? upMove   : 0;
        double nDM = downMove > upMove   && downMove > 0 ? downMove : 0;
        return (pDM, nDM);
    }

    /// <summary>Stochastic %K(period) = (Close − LowestLow) / (HighestHigh − LowestLow) ∈ [0, 1].</summary>
    public static double CalculateStochK(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period) return 0.5;
        var recent   = candles.TakeLast(period).ToList();
        double lo    = (double)recent.Min(c => c.Low);
        double hi    = (double)recent.Max(c => c.High);
        double close = (double)candles[^1].Close;
        return (hi - lo) > 1e-10 ? (close - lo) / (hi - lo) : 0.5;
    }

    /// <summary>Rate of change over <paramref name="period"/> bars, normalised to [−3, 3].</summary>
    public static float CalculateROC(List<decimal> closes, int period)
    {
        if (closes.Count <= period) return 0f;
        double past    = (double)closes[closes.Count - 1 - period];
        double current = (double)closes[^1];
        return past == 0 ? 0f : Clamp((float)((current - past) / past * 100.0 / 5.0), -3f, 3f);
    }

    // ── Standardisation helpers ───────────────────────────────────────────────

    public static (float[] Means, float[] Stds) ComputeStandardization(List<float[]> featureSets)
    {
        if (featureSets.Count == 0) return ([], []);
        int n     = featureSets[0].Length;
        var means = new float[n];
        var stds  = new float[n];
        for (int j = 0; j < n; j++)
        {
            float mean = (float)featureSets.Average(f => f[j]);
            float std  = (float)Math.Sqrt(featureSets.Average(f => Math.Pow(f[j] - mean, 2)));
            means[j]   = mean;
            stds[j]    = std < 1e-6f ? 1f : std;
        }
        return (means, stds);
    }

    public static float[] Standardize(float[] features, float[] means, float[] stds)
    {
        var result = new float[features.Length];
        for (int j = 0; j < features.Length; j++)
            result[j] = (features[j] - means[j]) / stds[j];
        return result;
    }

    /// <summary>
    /// Resolves the effective decision threshold using the same precedence as live scoring.
    /// </summary>
    public static double ResolveEffectiveDecisionThreshold(ModelSnapshot snap, string? regime = null)
    {
        if (regime is not null &&
            snap.RegimeThresholds is not null &&
            snap.RegimeThresholds.TryGetValue(regime, out var regimeThreshold) &&
            regimeThreshold > 0.0)
        {
            return regimeThreshold;
        }

        if (snap.AdaptiveThreshold > 0.0)
            return snap.AdaptiveThreshold;
        if (snap.OptimalThreshold > 0.0)
            return snap.OptimalThreshold;

        return 0.5;
    }

    /// <summary>
    /// Reconstructs the logged probability of the Buy class from the scorer's stored
    /// direction, confidence score, threshold, and optional ensemble disagreement.
    /// </summary>
    public static double ReconstructLoggedBuyProbability(
        TradeDirection predictedDirection,
        double confidenceScore,
        double decisionThreshold,
        decimal? ensembleDisagreement = null)
    {
        double confidence = Math.Clamp(confidenceScore, 0.0, 1.0);
        double rawConviction = confidence;

        if (ensembleDisagreement.HasValue)
        {
            double disagreement = Math.Clamp((double)ensembleDisagreement.Value, 0.0, 0.5);
            double disagreementFactor = Math.Clamp(1.0 - 2.0 * disagreement, 0.0, 1.0);
            rawConviction = disagreementFactor > 1e-8
                ? Math.Clamp(confidence / disagreementFactor, 0.0, 1.0)
                : 0.0;
        }

        double distanceFromThreshold = rawConviction / 2.0;
        return predictedDirection == TradeDirection.Buy
            ? Math.Clamp(decisionThreshold + distanceFromThreshold, decisionThreshold, 1.0)
            : Math.Clamp(decisionThreshold - distanceFromThreshold, 0.0, decisionThreshold);
    }

    /// <summary>
    /// Resolves the exact decision threshold used for a logged prediction when available,
    /// otherwise falls back to the caller-provided threshold.
    /// </summary>
    public static double ResolveLoggedDecisionThreshold(
        MLModelPredictionLog log,
        double               fallbackThreshold = 0.5)
    {
        if (log.DecisionThresholdUsed.HasValue)
        {
            double logged = (double)log.DecisionThresholdUsed.Value;
            if (double.IsFinite(logged) && logged > 0.0 && logged < 1.0)
                return logged;
        }

        return Math.Clamp(fallbackThreshold, 0.0, 1.0);
    }

    /// <summary>
    /// Resolves the logged raw Buy-class probability when available, otherwise falls back
    /// to reconstructing from the legacy threshold-relative confidence contract.
    /// </summary>
    public static double ResolveLoggedRawBuyProbability(
        MLModelPredictionLog log,
        double               fallbackThreshold = 0.5)
    {
        if (log.RawProbability.HasValue)
            return Math.Clamp((double)log.RawProbability.Value, 0.0, 1.0);

        double decisionThreshold = ResolveLoggedDecisionThreshold(log, fallbackThreshold);
        return ReconstructLoggedBuyProbability(
            log.PredictedDirection,
            (double)log.ConfidenceScore,
            decisionThreshold,
            log.EnsembleDisagreement);
    }

    /// <summary>
    /// Resolves the logged calibrated Buy-class probability when available, otherwise falls
    /// back to raw probability or legacy confidence reconstruction for historical rows.
    /// </summary>
    public static double ResolveLoggedCalibratedBuyProbability(
        MLModelPredictionLog log,
        double               fallbackThreshold = 0.5)
    {
        if (log.CalibratedProbability.HasValue)
            return Math.Clamp((double)log.CalibratedProbability.Value, 0.0, 1.0);
        if (log.RawProbability.HasValue)
            return Math.Clamp((double)log.RawProbability.Value, 0.0, 1.0);

        double decisionThreshold = ResolveLoggedDecisionThreshold(log, fallbackThreshold);
        return ReconstructLoggedBuyProbability(
            log.PredictedDirection,
            (double)log.ConfidenceScore,
            decisionThreshold,
            log.EnsembleDisagreement);
    }

    /// <summary>
    /// Resolves the effective served Buy-class probability that actually drove the live
    /// trade decision. Falls back to the base calibrated probability for older rows.
    /// </summary>
    public static double ResolveLoggedServedBuyProbability(
        MLModelPredictionLog log,
        double               fallbackThreshold = 0.5)
    {
        if (log.ServedCalibratedProbability.HasValue)
            return Math.Clamp((double)log.ServedCalibratedProbability.Value, 0.0, 1.0);

        return ResolveLoggedCalibratedBuyProbability(log, fallbackThreshold);
    }

    // ── Math primitives ───────────────────────────────────────────────────────

    public static double Sigmoid(double x) =>
        1.0 / (1.0 + Math.Exp(-Math.Max(-500, Math.Min(500, x))));

    public static float Clamp(float v, float min, float max) =>
        v < min ? min : v > max ? max : v;

    public static double DotProduct(double[] w, float[] f)
    {
        double sum = 0;
        for (int j = 0; j < w.Length; j++) sum += w[j] * f[j];
        return sum;
    }

    /// <summary>Logit (log-odds) of a probability p, clamped away from 0/1.</summary>
    public static double Logit(double p)
    {
        p = Math.Clamp(p, 1e-6, 1 - 1e-6);
        return Math.Log(p / (1 - p));
    }

    // ── Fractional differencing ───────────────────────────────────────────────

    /// <summary>
    /// Applies fractional differencing of order <paramref name="d"/> to a price series,
    /// achieving stationarity while preserving long-memory autocorrelation.
    ///
    /// The fractionally differenced series is:
    ///   x_t^d = Σ_{k=0}^{T-1} w_k * x_{t-k}
    /// where w_0 = 1, w_k = w_{k-1} * (d - k + 1) / k.
    ///
    /// Weights below <paramref name="threshold"/> are truncated (finite memory window).
    /// </summary>
    /// <param name="prices">Chronologically ordered price series.</param>
    /// <param name="d">Fractional differencing order (0 = identity, 1 = first difference).</param>
    /// <param name="threshold">Minimum absolute weight to retain (default 1e-5).</param>
    /// <returns>Differenced series; length = <c>prices.Count</c>, early entries that have
    /// insufficient history are filled with 0.</returns>
    public static double[] FractionalDiffSeries(IList<decimal> prices, double d, double threshold = 1e-5)
    {
        if (d <= 0 || prices.Count == 0) return prices.Select(p => (double)p).ToArray();

        // Compute weights until they drop below threshold
        var weights = new List<double> { 1.0 };
        for (int k = 1; k < prices.Count; k++)
        {
            double w = -weights[k - 1] * (d - k + 1) / k;
            if (Math.Abs(w) < threshold) break;
            weights.Add(w);
        }

        int winLen = weights.Count;
        var result = new double[prices.Count];

        for (int t = winLen - 1; t < prices.Count; t++)
        {
            double val = 0;
            for (int k = 0; k < winLen; k++)
                val += weights[k] * (double)prices[t - k];
            result[t] = val;
        }

        return result;
    }

    // ── ADF stationarity test ─────────────────────────────────────────────────

    /// <summary>
    /// Lightweight Augmented Dickey-Fuller test (constant-only model, no trend).
    /// Returns an approximate p-value for the null hypothesis of a unit root.
    ///
    /// Implementation:
    ///   1. Build regressors: lagged level y_{t-1} and up to <paramref name="maxLags"/> lagged differences.
    ///   2. Regress Δy_t on these regressors via OLS.
    ///   3. Compute t-statistic for the coefficient on y_{t-1}.
    ///   4. Map to approximate p-value using MacKinnon (1994) asymptotic response surface.
    ///
    /// p-value &lt; 0.05 → reject unit root → series is stationary at 5% level.
    /// </summary>
    /// <param name="series">Input time series (at least 20 observations).</param>
    /// <param name="maxLags">Maximum number of augmentation lags (default 4).</param>
    /// <returns>Approximate p-value; returns 1.0 when insufficient data.</returns>
    public static double AdfTest(double[] series, int maxLags = 4)
    {
        int n = series.Length;
        if (n < 20) return 1.0;

        int lags = Math.Min(maxLags, (n - 2) / 2);
        int rows = n - 1 - lags;
        if (rows < 5) return 1.0;

        // Build design matrix: [y_{t-1}, Δy_{t-1}, ..., Δy_{t-lags}, 1]
        int cols = 2 + lags; // y_lag + lags diffs + constant
        var X   = new double[rows, cols];
        var dy  = new double[rows];

        for (int i = 0; i < rows; i++)
        {
            int t = i + 1 + lags; // index into series
            dy[i]  = series[t] - series[t - 1];
            X[i, 0] = series[t - 1];           // lagged level
            for (int k = 1; k <= lags; k++)
                X[i, k] = series[t - k] - series[t - k - 1]; // lagged diff
            X[i, cols - 1] = 1.0;              // constant
        }

        // OLS: β = (X'X)^{-1} X'y  (via normal equations with Cholesky)
        var XtX = new double[cols, cols];
        var Xty = new double[cols];
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                Xty[j] += X[i, j] * dy[i];
                for (int k = j; k < cols; k++)
                    XtX[j, k] += X[i, j] * X[i, k];
            }
        }
        // Fill lower triangle
        for (int j = 0; j < cols; j++)
            for (int k = j + 1; k < cols; k++)
                XtX[k, j] = XtX[j, k];

        double[]? beta = SolveLinearSystem(XtX, Xty, cols);
        if (beta is null) return 1.0;

        // Residuals + SE of beta[0]
        double sse = 0;
        for (int i = 0; i < rows; i++)
        {
            double pred = 0;
            for (int j = 0; j < cols; j++) pred += beta[j] * X[i, j];
            double r = dy[i] - pred;
            sse += r * r;
        }
        double sigma2 = sse / (rows - cols);
        double[]? invDiag = InverseDiag(XtX, cols);
        if (invDiag is null || invDiag[0] <= 0 || sigma2 <= 0) return 1.0;

        double tStat = beta[0] / Math.Sqrt(sigma2 * invDiag[0]);

        // MacKinnon (1994) approximate p-value mapping for ADF (constant, no trend).
        // Critical values (constant-only model, asymptotic):
        //   1%: -3.43,  5%: -2.86,  10%: -2.57,  20%: -2.20,  40%: -1.80,  60%: -1.28
        // Linear interpolation between adjacent breakpoints gives smooth coverage
        // from near-zero up to 1.0, which is important for the 20–50% region where
        // the stationarity gate (>30% non-stationary features) is most sensitive.
        if (tStat < -3.43) return 0.01;
        if (tStat < -2.86) return 0.01 + (tStat + 3.43) / (-2.86 + 3.43) * 0.04;
        if (tStat < -2.57) return 0.05 + (tStat + 2.86) / (-2.57 + 2.86) * 0.05;
        if (tStat < -2.20) return 0.10 + (tStat + 2.57) / (-2.20 + 2.57) * 0.10;
        if (tStat < -1.80) return 0.20 + (tStat + 2.20) / (-1.80 + 2.20) * 0.20;
        if (tStat < -1.28) return 0.40 + (tStat + 1.80) / (-1.28 + 1.80) * 0.20;
        return 1.0;
    }

    /// <summary>Solve Ax = b via Gaussian elimination with partial pivoting. Returns null on failure.</summary>
    private static double[]? SolveLinearSystem(double[,] A, double[] b, int n)
    {
        var a = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) a[i, j] = A[i, j];
            a[i, n] = b[i];
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;

            for (int k = col; k <= n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);

            for (int row = col + 1; row < n; row++)
            {
                double f = a[row, col] / a[col, col];
                for (int k = col; k <= n; k++) a[row, k] -= f * a[col, k];
            }
        }

        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            x[i] = a[i, n];
            for (int j = i + 1; j < n; j++) x[i] -= a[i, j] * x[j];
            x[i] /= a[i, i];
        }
        return x;
    }

    /// <summary>
    /// Returns the diagonal of (A)^{-1} needed for OLS standard errors.
    /// Uses the formula diag((A^{-1})) from Gaussian elimination.
    /// Returns null on failure.
    /// </summary>
    private static double[]? InverseDiag(double[,] A, int n)
    {
        // Augment with identity and solve n systems
        var diag = new double[n];
        for (int col = 0; col < n; col++)
        {
            var e = new double[n];
            e[col] = 1.0;
            var x = SolveLinearSystem(A, e, n);
            if (x is null) return null;
            diag[col] = x[col];
        }
        return diag;
    }

    // ── PSI feature quantile baselines ────────────────────────────────────────

    /// <summary>
    /// Computes per-feature quantile bin edges from a set of standardised training features.
    /// Used as the "expected" distribution baseline for Population Stability Index monitoring.
    ///
    /// For each feature, the values are sorted and split into <paramref name="bins"/> equal-count
    /// buckets. The <c>bins − 1</c> internal edges are stored.
    /// </summary>
    /// <param name="standardisedFeatures">List of standardised feature vectors from training set.</param>
    /// <param name="bins">Number of quantile buckets (default 10).</param>
    /// <returns>
    /// Jagged array [featureIndex][binEdge]; each inner array has length <c>bins − 1</c>.
    /// </returns>
    public static double[][] ComputeFeatureQuantileBreakpoints(
        List<float[]> standardisedFeatures,
        int           bins = 10)
    {
        if (standardisedFeatures.Count == 0) return [];
        int featureCount = standardisedFeatures[0].Length;
        var result = new double[featureCount][];

        for (int j = 0; j < featureCount; j++)
        {
            var vals = standardisedFeatures
                .Select(f => (double)f[j])
                .OrderBy(v => v)
                .ToArray();

            var edges = new double[bins - 1];
            for (int b = 1; b < bins; b++)
            {
                double idx = b * vals.Length / (double)bins;
                int lo = (int)idx;
                int hi = Math.Min(lo + 1, vals.Length - 1);
                edges[b - 1] = vals[lo] + (idx - lo) * (vals[hi] - vals[lo]);
            }
            result[j] = edges;
        }

        return result;
    }

    /// <summary>
    /// Computes the Population Stability Index (PSI) for a single feature.
    /// PSI = Σ_i (A_i − E_i) × ln(A_i / E_i)
    /// where E_i is the expected (training) fraction and A_i is the actual (recent) fraction
    /// in each bin.
    ///
    /// Interpretation: &lt;0.1 = stable, 0.1–0.25 = moderate shift, &gt;0.25 = major shift.
    /// </summary>
    /// <param name="binEdges">Sorted bin edges for this feature (length = bins − 1).</param>
    /// <param name="trainingValues">Feature values from the training period (already standardised).</param>
    /// <param name="recentValues">Feature values from the recent observation window.</param>
    /// <returns>PSI scalar; 0.0 when insufficient data.</returns>
    public static double ComputeFeaturePsi(
        double[] binEdges,
        double[] trainingValues,
        double[] recentValues)
    {
        if (trainingValues.Length == 0 || recentValues.Length == 0 || binEdges.Length == 0)
            return 0.0;

        int bins = binEdges.Length + 1;
        var trainCounts   = new double[bins];
        var recentCounts  = new double[bins];

        static int GetBin(double[] edges, double v)
        {
            for (int i = 0; i < edges.Length; i++)
                if (v <= edges[i]) return i;
            return edges.Length;
        }

        foreach (var v in trainingValues) trainCounts[GetBin(binEdges, v)]++;
        foreach (var v in recentValues)   recentCounts[GetBin(binEdges, v)]++;

        double psi = 0;
        for (int i = 0; i < bins; i++)
        {
            double e = Math.Max(trainCounts[i]  / trainingValues.Length, 1e-6);
            double a = Math.Max(recentCounts[i] / recentValues.Length,   1e-6);
            psi += (a - e) * Math.Log(a / e);
        }

        return psi;
    }

    // ── Rec #2: Execution-aware labels ────────────────────────────────────────

    /// <summary>
    /// Converts candles into labelled training samples where triple-barrier outcomes are
    /// computed net of round-trip execution cost (spread + commission).
    /// This ensures the model learns to predict <em>profitable after costs</em> moves,
    /// not raw mid-price direction.
    /// </summary>
    /// <param name="candles">Chronologically ordered closed candles (minimum LookbackWindow + 2).</param>
    /// <param name="spreadCostPips">Round-trip cost in pips deducted from barrier targets.</param>
    /// <param name="takeProfitAtrMultiple">Take-profit barrier in ATR multiples (default 1.5).</param>
    /// <param name="stopLossAtrMultiple">Stop-loss barrier in ATR multiples (default 1.0).</param>
    /// <param name="maxHoldBars">Maximum bars before time-exit (label = 0/neutral).</param>
    /// <param name="cotLookup">Optional COT data provider.</param>
    public static List<TrainingSample> BuildExecutionAwareLabels(
        List<Candle>                     candles,
        double                           spreadCostPips,
        double                           takeProfitAtrMultiple = 1.5,
        double                           stopLossAtrMultiple   = 1.0,
        int                              maxHoldBars           = 20,
        Func<DateTime, CotFeatureEntry>? cotLookup             = null)
    {
        var samples = new List<TrainingSample>(candles.Count);

        // Infer pip size from typical spread or assume 0.0001 for majors
        const double defaultPipSize = 0.0001;

        for (int i = LookbackWindow; i < candles.Count - maxHoldBars; i++)
        {
            var window  = candles.GetRange(i - LookbackWindow, LookbackWindow);
            var current = candles[i];
            var prev    = window[^1];

            double atr = CalculateATR(window, 14);
            if (atr <= 0) continue;

            var cotEntry = cotLookup?.Invoke(current.Timestamp) ?? CotFeatureEntry.Zero;
            var features = BuildFeatureVector(window, current, prev, cotEntry);

            double tp  = atr * takeProfitAtrMultiple - spreadCostPips * defaultPipSize;
            double sl  = atr * stopLossAtrMultiple   + spreadCostPips * defaultPipSize;

            if (tp <= 0) continue; // spread exceeds barrier — skip degenerate sample

            double entry = (double)current.Close;
            int    label = 0;
            float  mag   = 0f;

            for (int j = i + 1; j < i + maxHoldBars && j < candles.Count; j++)
            {
                double high = (double)candles[j].High;
                double low  = (double)candles[j].Low;

                bool hitTp = high - entry >= tp;
                bool hitSl = entry - low  >= sl;

                if (hitTp && !hitSl) { label = 1; mag  =  (float)(tp / atr); break; }
                if (hitSl && !hitTp) { label = 0; mag  = -(float)(sl / atr); break; }
                if (hitTp && hitSl)  { label = 0; break; } // simultaneous — ambiguous
            }

            samples.Add(new TrainingSample(features, label, mag));
        }

        return samples;
    }

    // ── Rec #13: Cointegration spread features ────────────────────────────────

    /// <summary>
    /// Appends three cointegration-based features to existing feature vectors by
    /// computing the Engle-Granger spread between a primary symbol and a peer symbol.
    /// The spread is mean-reverting when the two series are cointegrated (e.g. EUR/USD + GBP/USD).
    /// </summary>
    /// <param name="primaryCandles">Candles for the symbol being trained.</param>
    /// <param name="peerCandles">Candles for the cointegration peer, aligned by timestamp.</param>
    /// <param name="baseFeatures">
    /// Existing feature vector of length <see cref="FeatureCount"/> to augment.
    /// The returned array has length <see cref="FeatureCount"/> + 3.
    /// </param>
    /// <param name="hedgeRatio">
    /// OLS hedge ratio β: spread = log(primary) − β × log(peer).
    /// Computed once per training run via <see cref="ComputeOlsHedgeRatio"/>.
    /// </param>
    /// <param name="spreadMean">Rolling mean of the spread series (for z-score normalisation).</param>
    /// <param name="spreadStd">Rolling std-dev of the spread series.</param>
    /// <returns>
    /// Feature vector extended with:
    /// [FeatureCount+0] SpreadZScore      — (spread − mean) / std
    /// [FeatureCount+1] SpreadMomentum    — SpreadZScore − SpreadZScore_lag1
    /// [FeatureCount+2] CointegResidLag1  — previous bar's spread z-score
    /// </returns>
    public static float[] AppendCointegrationFeatures(
        float[]  baseFeatures,
        double   currentSpreadZScore,
        double   prevSpreadZScore)
    {
        var extended = new float[baseFeatures.Length + 3];
        Array.Copy(baseFeatures, extended, baseFeatures.Length);
        int offset = baseFeatures.Length;
        extended[offset]     = Clamp((float)currentSpreadZScore, -5f, 5f);
        extended[offset + 1] = Clamp((float)(currentSpreadZScore - prevSpreadZScore), -5f, 5f);
        extended[offset + 2] = Clamp((float)prevSpreadZScore, -5f, 5f);
        return extended;
    }

    /// <summary>
    /// Computes the OLS hedge ratio β = Cov(X,Y) / Var(Y) from two log-price series.
    /// Used once at training time; stored in <c>HyperparamOverrides.CointegrationPeerSymbol</c>
    /// alongside the symbol. The same β must be used at inference time.
    /// </summary>
    public static double ComputeOlsHedgeRatio(IReadOnlyList<double> logPrimaryClose, IReadOnlyList<double> logPeerClose)
    {
        int n = Math.Min(logPrimaryClose.Count, logPeerClose.Count);
        if (n < 2) return 1.0;

        double meanX = 0, meanY = 0;
        for (int i = 0; i < n; i++) { meanX += logPrimaryClose[i]; meanY += logPeerClose[i]; }
        meanX /= n; meanY /= n;

        double cov = 0, varY = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = logPrimaryClose[i] - meanX;
            double dy = logPeerClose[i]    - meanY;
            cov  += dx * dy;
            varY += dy * dy;
        }
        return varY > 0 ? cov / varY : 1.0;
    }

    /// <summary>Computes the rolling mean and std-dev of a spread series for z-score normalisation.</summary>
    public static (double mean, double std) ComputeSpreadStats(IReadOnlyList<double> spreads)
    {
        if (spreads.Count == 0) return (0, 1);
        double mean = 0;
        foreach (var s in spreads) mean += s;
        mean /= spreads.Count;
        double variance = 0;
        foreach (var s in spreads) variance += (s - mean) * (s - mean);
        double std = Math.Sqrt(variance / spreads.Count);
        return (mean, std < 1e-8 ? 1.0 : std);
    }

    // ── Rec #30: Intraday Fourier seasonality features ────────────────────────

    /// <summary>
    /// Appends 4 intraday / day-of-week Fourier features to the given feature array,
    /// returning a new array with length <c>features.Length + 4</c>.
    /// </summary>
    /// <remarks>
    /// FX markets have strong intraday patterns (London open spike, NY/London overlap
    /// liquidity, Asian session quietness). A lossless cyclic encoding captures these
    /// patterns without ordinal bias:
    ///   HourSin  = sin(2π × hour / 24)
    ///   HourCos  = cos(2π × hour / 24)
    ///   DowSin   = sin(2π × dayOfWeek / 7)    (0 = Sunday)
    ///   DowCos   = cos(2π × dayOfWeek / 7)
    /// Note: the base 29-feature vector already contains HourSin/HourCos/DayOfWeekSin/
    /// DayOfWeekCos computed from the candle timestamp.  This helper appends additional
    /// higher-harmonic Fourier terms (2nd harmonic) for models that opt in via
    /// <c>HyperparamOverrides.AppendHigherHarmonicSeasonality</c>.
    /// </remarks>
    public static float[] AppendHigherHarmonicSeasonality(float[] features, DateTime candleTime)
    {
        double hour = candleTime.Hour + candleTime.Minute / 60.0;
        double dow  = (int)candleTime.DayOfWeek;
        var result = new float[features.Length + 4];
        Array.Copy(features, result, features.Length);
        result[features.Length]     = (float)Math.Sin(4 * Math.PI * hour / 24);  // 2nd harmonic hour
        result[features.Length + 1] = (float)Math.Cos(4 * Math.PI * hour / 24);
        result[features.Length + 2] = (float)Math.Sin(4 * Math.PI * dow  / 7);   // 2nd harmonic DoW
        result[features.Length + 3] = (float)Math.Cos(4 * Math.PI * dow  / 7);
        return result;
    }

    // ── Rec #34: Feature interaction products ─────────────────────────────────

    /// <summary>
    /// Appends product interaction features x_i × x_j for the specified index pairs,
    /// returning a new array with length <c>features.Length + pairs.Length</c>.
    /// </summary>
    /// <param name="features">The source feature vector.</param>
    /// <param name="pairs">
    /// Array of (indexA, indexB) pairs identifying the interactions to append.
    /// Discovered by <c>MLFeatureInteractionWorker</c> and stored in
    /// <c>MLFeatureInteractionAudit.IsIncludedAsFeature</c> records.
    /// </param>
    public static float[] AppendInteractionFeatures(float[] features, (int A, int B)[] pairs)
    {
        if (pairs.Length == 0) return features;
        var result = new float[features.Length + pairs.Length];
        Array.Copy(features, result, features.Length);
        for (int i = 0; i < pairs.Length; i++)
        {
            var (a, b) = pairs[i];
            result[features.Length + i] = features[a] * features[b];
        }
        return result;
    }

    // ── Rec #22: SMOTE oversampling ───────────────────────────────────────────

    /// <summary>
    /// Applies Synthetic Minority Oversampling (SMOTE) to balance an imbalanced list of
    /// <see cref="TrainingSample"/>s when the majority class fraction exceeds
    /// <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// For each minority-class sample, the method finds its K nearest neighbours in
    /// feature space (Euclidean distance) and generates a synthetic sample by linear
    /// interpolation: x_new = x + rand(0,1) × (x_neighbour − x).
    /// The set is balanced to a 50/50 split.
    /// </remarks>
    public static List<TrainingSample> ApplySmote(
        List<TrainingSample> samples,
        int k = 5,
        double threshold = 0.60,
        Random? rng = null)
    {
        rng ??= new Random(42);
        int total    = samples.Count;
        int majority = samples.Count(s => s.Direction == 1);
        int minority = total - majority;
        int majDir   = majority >= minority ? 1 : 0;
        int minDir   = 1 - majDir;
        double majorityFrac = (double)Math.Max(majority, minority) / total;
        if (majorityFrac < threshold) return samples;  // already balanced enough

        var minSamples = samples.Where(s => s.Direction == minDir).ToList();
        int needed     = Math.Max(majority, minority) - Math.Min(majority, minority);
        var synthetic  = new List<TrainingSample>(needed);

        for (int n = 0; n < needed; n++)
        {
            var x = minSamples[rng.Next(minSamples.Count)];
            // K-nearest neighbours in feature space
            var neighbours = minSamples
                .OrderBy(s => EuclideanDistanceSq(s.Features, x.Features))
                .Skip(1).Take(k).ToList();
            var neighbour = neighbours[rng.Next(neighbours.Count)];
            float gap = (float)rng.NextDouble();
            var synFeatures = new float[x.Features.Length];
            for (int i = 0; i < synFeatures.Length; i++)
                synFeatures[i] = x.Features[i] + gap * (neighbour.Features[i] - x.Features[i]);
            float synMag = x.Magnitude + gap * (neighbour.Magnitude - x.Magnitude);
            synthetic.Add(new TrainingSample(synFeatures, minDir, synMag));
        }

        var result = new List<TrainingSample>(samples.Count + synthetic.Count);
        result.AddRange(samples);
        result.AddRange(synthetic);
        return result;
    }

    private static double EuclideanDistanceSq(float[] a, float[] b)
    {
        double s = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++) { double d = a[i] - b[i]; s += d * d; }
        return s;
    }

    // ── Rec #39: Spectral / FFT features ──────────────────────────────────────

    /// <summary>
    /// Appends 6 FFT-based spectral features to an existing feature vector:
    /// power at dominant frequency, second dominant frequency, spectral centroid,
    /// spectral entropy, dominant period (in bars), and a stationarity proxy.
    /// </summary>
    public static float[] AppendSpectralFeatures(float[] existing, List<Candle> window)
    {
        if (window.Count < 8) return existing;

        int n = window.Count;
        // Use close prices
        double[] prices = window.Select(c => (double)c.Close).ToArray();

        // Remove mean
        double mean = prices.Average();
        double[] x  = prices.Select(p => p - mean).ToArray();

        // Radix-2 DFT (iterative, half-spectrum)
        int half  = n / 2;
        double[] power = new double[half];
        for (int k = 0; k < half; k++)
        {
            double re = 0, im = 0;
            for (int t = 0; t < n; t++)
            {
                double angle = -2 * Math.PI * k * t / n;
                re += x[t] * Math.Cos(angle);
                im += x[t] * Math.Sin(angle);
            }
            power[k] = re * re + im * im;
        }

        double totalPower = power.Sum() + 1e-12;
        double[] normPower = power.Select(p => p / totalPower).ToArray();

        // Dominant frequency power
        int dom1Idx = 1;
        for (int k = 2; k < half; k++)
            if (power[k] > power[dom1Idx]) dom1Idx = k;
        double dom1Power = normPower[dom1Idx];

        // Second dominant frequency
        int dom2Idx = dom1Idx == 1 ? 2 : 1;
        for (int k = 1; k < half; k++)
            if (k != dom1Idx && power[k] > power[dom2Idx]) dom2Idx = k;
        double dom2Power = normPower[dom2Idx];

        // Spectral centroid (frequency-weighted mean)
        double centroid = 0;
        for (int k = 1; k < half; k++) centroid += k * normPower[k];

        // Spectral entropy
        double entropy = 0;
        for (int k = 1; k < half; k++)
            if (normPower[k] > 1e-12) entropy -= normPower[k] * Math.Log(normPower[k]);
        entropy /= Math.Log(half); // normalise to [0, 1]

        // Dominant period in bars (normalised to window length)
        double domPeriod = dom1Idx > 0 ? (double)n / dom1Idx / n : 0.5;

        // Stationarity proxy: ratio of variance in second half vs first half (log scale)
        double[] first  = x.Take(n / 2).ToArray();
        double[] second = x.Skip(n / 2).ToArray();
        double varFirst  = first.Select(v => v * v).Average() + 1e-12;
        double varSecond = second.Select(v => v * v).Average() + 1e-12;
        double stationarity = Clamp((float)Math.Log(varSecond / varFirst), -3, 3) / 3.0;

        var result = new float[existing.Length + 6];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)(dom1Power * 10),  -5, 5);
        result[existing.Length + 1] = Clamp((float)(dom2Power * 10),  -5, 5);
        result[existing.Length + 2] = Clamp((float)centroid,           0, 1);
        result[existing.Length + 3] = Clamp((float)entropy,            0, 1);
        result[existing.Length + 4] = Clamp((float)domPeriod,          0, 1);
        result[existing.Length + 5] = Clamp((float)stationarity,      -1, 1);
        return result;
    }

    // ── Rec #40: Coreset selection ─────────────────────────────────────────────

    /// <summary>
    /// Greedy k-centre coreset selection: iteratively selects samples that are farthest
    /// from already-selected centres to build a representative subset of size
    /// <paramref name="targetCount"/>. Reduces training cost while preserving coverage.
    /// </summary>
    public static List<TrainingSample> SelectCoreset(
        List<TrainingSample> samples, int targetCount)
    {
        if (samples.Count <= targetCount) return samples;

        var rng     = new Random(42);
        var centres = new List<int> { rng.Next(samples.Count) };
        var minDist = new double[samples.Count];
        Array.Fill(minDist, double.MaxValue);

        for (int iter = 0; iter < targetCount - 1; iter++)
        {
            int last = centres[^1];
            float[] lv = samples[last].Features;
            double maxD = double.NegativeInfinity;
            int nextCentre = 0;

            for (int i = 0; i < samples.Count; i++)
            {
                float[] fv = samples[i].Features;
                double d = 0;
                for (int f = 0; f < Math.Min(lv.Length, fv.Length); f++)
                {
                    double diff = lv[f] - fv[f];
                    d += diff * diff;
                }
                if (d < minDist[i]) minDist[i] = d;
                if (minDist[i] > maxD) { maxD = minDist[i]; nextCentre = i; }
            }
            centres.Add(nextCentre);
        }

        return centres.Select(i => samples[i]).ToList();
    }

    // ── Rec #48: GARCH volatility features ────────────────────────────────────

    /// <summary>
    /// Appends 3 GARCH(1,1) derived features to an existing feature vector:
    /// current conditional variance (normalised), variance-in-mean ratio,
    /// and GARCH persistence (α + β).
    /// The caller provides the GARCH parameters (ω, α, β) fitted by <c>MLGarchFitWorker</c>.
    /// </summary>
    public static float[] AppendGarchFeatures(
        float[] existing, List<Candle> window,
        double omega, double alpha, double beta)
    {
        if (window.Count < 5) return existing;

        // Compute returns
        var returns = window.Skip(1)
            .Zip(window, (c, p) => (double)(c.Close - p.Close) / (double)p.Close)
            .ToArray();

        // Recursive GARCH conditional variance
        double variance = returns.Select(r => r * r).Average();
        foreach (var r in returns)
            variance = omega + alpha * r * r + beta * variance;

        double sigma = Math.Sqrt(Math.Max(0, variance));

        // Normalise to [−3, 3]
        double annualised   = sigma * Math.Sqrt(252);
        double varInMean    = returns.Length > 0 ? returns.Average() / (sigma + 1e-12) : 0;
        double persistence  = alpha + beta;

        var result = new float[existing.Length + 3];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)(annualised * 10),  -5, 5);
        result[existing.Length + 1] = Clamp((float)varInMean,          -3, 3);
        result[existing.Length + 2] = Clamp((float)persistence,         0, 1);
        return result;
    }

    // ── Rec #51: ETS / Holt-Winters features ──────────────────────────────────

    /// <summary>
    /// Appends 4 ETS(A,A,N) Holt-Winters features to an existing feature vector:
    /// smoothed level (relative to last close), smoothed trend (normalised by ATR),
    /// one-step-ahead forecast residual, and forecast-to-current ratio.
    /// </summary>
    public static float[] AppendEtsFeatures(
        float[] existing, List<Candle> window, double alpha, double beta)
    {
        if (window.Count < 3) return existing;

        double L = (double)window[0].Close;
        double T = 0;
        double lastResidual = 0;

        foreach (var candle in window)
        {
            double y  = (double)candle.Close;
            double Lp = L, Tp = T;
            double forecast = Lp + Tp;
            lastResidual = y - forecast;
            L = alpha * y + (1 - alpha) * (Lp + Tp);
            T = beta * (L - Lp) + (1 - beta) * Tp;
        }

        double lastClose = (double)window[^1].Close;
        double atr       = (double)CalculateATR(window, Math.Min(14, window.Count - 1));
        double atrNorm   = atr > 0 ? atr : 1.0;

        double levelRel   = lastClose > 0 ? (L - lastClose) / lastClose : 0;
        double trendNorm  = T / atrNorm;
        double residNorm  = lastResidual / atrNorm;
        double forecastRatio = lastClose > 0 ? (L + T) / lastClose - 1 : 0;

        var result = new float[existing.Length + 4];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)(levelRel   * 100), -5, 5);
        result[existing.Length + 1] = Clamp((float)trendNorm,          -5, 5);
        result[existing.Length + 2] = Clamp((float)residNorm,          -5, 5);
        result[existing.Length + 3] = Clamp((float)(forecastRatio * 100), -5, 5);
        return result;
    }

    // ── Rec #28: Confident learning (label noise detection) ───────────────────

    /// <summary>
    /// Uses Confident Learning to identify likely mislabelled samples and returns
    /// a filtered list with noise samples removed.
    /// </summary>
    /// <remarks>
    /// Algorithm:
    ///   1. Run 3-fold cross-validation to obtain out-of-fold predicted probabilities p̂(y|x).
    ///   2. For each class, compute the per-class threshold θ_y = mean of p̂(y | samples with label y).
    ///   3. Flag sample (x, ỹ) as mislabelled when p̂(ỹ|x) &lt; θ_ỹ AND argmax_y p̂(y|x) ≠ ỹ.
    ///   4. Return the samples that were NOT flagged.
    /// This is a lightweight approximation of Northcutt et al. (2021) Confident Learning.
    /// </remarks>
    public static List<TrainingSample> RemoveLabelNoise(
        List<TrainingSample> samples,
        out double noiseRate,
        int cvFolds = 3)
    {
        if (samples.Count < cvFolds * 20) { noiseRate = 0; return samples; }

        int N = samples.Count;
        var outOfFoldProbs = new double[N];

        int foldSize = N / cvFolds;
        for (int fold = 0; fold < cvFolds; fold++)
        {
            int valStart = fold * foldSize;
            int valEnd   = fold == cvFolds - 1 ? N : valStart + foldSize;

            var train = samples.Take(valStart).Concat(samples.Skip(valEnd)).ToList();
            var val   = samples.Skip(valStart).Take(valEnd - valStart).ToList();

            // Fit a single logistic regression on the train fold
            int F = train[0].Features.Length;
            var w = new double[F];
            double b = 0;
            double lr = 0.05;
            for (int epoch = 0; epoch < 30; epoch++)
            {
                foreach (var s in train)
                {
                    double dot = b;
                    for (int i = 0; i < F; i++) dot += w[i] * s.Features[i];
                    double p = Sigmoid(dot);
                    double err = p - s.Direction;
                    for (int i = 0; i < F; i++) w[i] -= lr * err * s.Features[i];
                    b -= lr * err;
                }
            }
            // Predict on val fold
            for (int j = 0; j < val.Count; j++)
            {
                double dot = b;
                var f = val[j].Features;
                for (int i = 0; i < F; i++) dot += w[i] * f[i];
                outOfFoldProbs[valStart + j] = Sigmoid(dot);
            }
        }

        // Per-class thresholds
        double threshold0 = 0, threshold1 = 0;
        int cnt0 = 0, cnt1 = 0;
        for (int i = 0; i < N; i++)
        {
            if (samples[i].Direction == 1) { threshold1 += outOfFoldProbs[i]; cnt1++; }
            else                           { threshold0 += 1 - outOfFoldProbs[i]; cnt0++; }
        }
        if (cnt1 > 0) threshold1 /= cnt1;
        if (cnt0 > 0) threshold0 /= cnt0;

        var clean = new List<TrainingSample>(N);
        int noisy = 0;
        for (int i = 0; i < N; i++)
        {
            double p     = outOfFoldProbs[i];
            int label    = samples[i].Direction;
            int predicted = p >= 0.5 ? 1 : 0;
            bool mislabelled = label == 1
                ? p < threshold1 && predicted != 1
                : (1 - p) < threshold0 && predicted != 0;
            if (mislabelled) noisy++;
            else clean.Add(samples[i]);
        }
        noiseRate = (double)noisy / N;
        return clean;
    }

    // ── Rec #29: Weight magnitude pruning ────────────────────────────────────

    /// <summary>
    /// Zero-out weights below <paramref name="sparsityTarget"/> fraction of the maximum
    /// absolute weight in each layer (magnitude pruning).
    /// </summary>
    /// <param name="weights">Ragged weight matrix (one row per ensemble learner).</param>
    /// <param name="sparsityTarget">
    /// Fraction of weights to prune (0–1).  E.g. 0.20 zeros the bottom 20 % by magnitude.
    /// </param>
    /// <returns>Pruned copy (original is not mutated).</returns>
    public static double[][] PruneWeights(double[][] weights, double sparsityTarget)
    {
        if (sparsityTarget <= 0) return weights;
        var pruned = weights.Select(row => (double[])row.Clone()).ToArray();
        foreach (var row in pruned)
        {
            var absVals  = row.Select(Math.Abs).OrderBy(v => v).ToArray();
            double threshold = absVals[(int)Math.Floor(sparsityTarget * absVals.Length)];
            for (int i = 0; i < row.Length; i++)
                if (Math.Abs(row[i]) < threshold) row[i] = 0.0;
        }
        return pruned;
    }

    // ── Rec #59: Path Signature Features (depth-2) ────────────────────────────

    /// <summary>
    /// Appends 6 depth-2 path signature features of the (close, log-volume) path
    /// over the lookback window. Signatures are universal nonlinear time-series
    /// features capturing all iterated integrals up to depth 2.
    /// Features: S¹, S², S¹¹, S²², S¹², S²¹.
    /// </summary>
    public static float[] AppendPathSignatureFeatures(float[] existing, List<Candle> window)
    {
        if (window.Count < 3) return existing;

        // Build increments for channel 1 (close) and channel 2 (log-volume)
        var closes = window.Select(c => (double)c.Close).ToArray();
        var logVols = window.Select(c => c.Volume > 0 ? Math.Log((double)c.Volume) : 0.0).ToArray();

        double s1 = 0, s2 = 0, s11 = 0, s22 = 0, s12 = 0, s21 = 0;
        double a1 = 0, a2 = 0; // running sums for iterated integrals

        for (int i = 1; i < closes.Length; i++)
        {
            double dc = closes[i] - closes[i - 1];
            double dv = logVols[i] - logVols[i - 1];
            s12 += a1 * dv;
            s21 += a2 * dc;
            a1 += dc;
            a2 += dv;
            s1 += dc;
            s2 += dv;
        }
        s11 = s1 * s1 / 2.0;
        s22 = s2 * s2 / 2.0;

        // Normalise by ATR
        double atr = (double)CalculateATR(window, Math.Min(14, window.Count - 1));
        double norm = atr > 0 ? atr : 1.0;

        var result = new float[existing.Length + 6];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)(s1  / norm),  -5, 5);
        result[existing.Length + 1] = Clamp((float)(s2  / 5.0),   -5, 5);
        result[existing.Length + 2] = Clamp((float)(s11 / (norm * norm)), -5, 5);
        result[existing.Length + 3] = Clamp((float)(s22 / 25.0),  -5, 5);
        result[existing.Length + 4] = Clamp((float)(s12 / (norm * 5.0)), -5, 5);
        result[existing.Length + 5] = Clamp((float)(s21 / (norm * 5.0)), -5, 5);
        return result;
    }

    // ── Rec #60: Kalman Filter State Estimation Features ──────────────────────

    /// <summary>
    /// Appends 4 Kalman filter features using a constant-velocity model on close prices:
    /// filtered level (relative to close), filtered velocity (ATR-normalised),
    /// last innovation (ATR-normalised), and innovation variance proxy.
    /// </summary>
    public static float[] AppendKalmanFeatures(float[] existing, List<Candle> window)
    {
        if (window.Count < 5) return existing;

        // State: [level, slope]; Transition F = [[1,1],[0,1]]
        // Process noise: Q_level=0.01, Q_slope=0.001; Measurement noise: R=1.0
        const double qLevel = 0.01, qSlope = 0.001, r = 1.0;

        double xLevel = (double)window[0].Close;
        double xSlope = 0;
        double pLL = 1, pLS = 0, pSL = 0, pSS = 1; // 2x2 covariance
        double lastInnovation = 0, innovVar = r;

        foreach (var candle in window)
        {
            double y = (double)candle.Close;

            // Predict
            double xLevelPred = xLevel + xSlope;
            double xSlopePred = xSlope;
            double pLLp = pLL + pLS + pSL + pSS + qLevel;
            double pLSp = pLS + pSS;
            double pSLp = pSL + pSS;
            double pSSp = pSS + qSlope;

            // Innovation
            double innov = y - xLevelPred;
            innovVar = pLLp + r;

            // Kalman gain
            double kL = pLLp / innovVar;
            double kS = pSLp / innovVar;

            // Update
            xLevel = xLevelPred + kL * innov;
            xSlope = xSlopePred + kS * innov;
            pLL = (1 - kL) * pLLp;
            pLS = (1 - kL) * pLSp;
            pSL = -kS * pLLp + pSLp;
            pSS = -kS * pLSp + pSSp;
            lastInnovation = innov;
        }

        double lastClose = (double)window[^1].Close;
        double atr = (double)CalculateATR(window, Math.Min(14, window.Count - 1));
        double norm = atr > 0 ? atr : 1.0;

        var result = new float[existing.Length + 4];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)((xLevel - lastClose) / lastClose), -0.05f, 0.05f);
        result[existing.Length + 1] = Clamp((float)(xSlope / norm),          -5, 5);
        result[existing.Length + 2] = Clamp((float)(lastInnovation / norm),  -5, 5);
        result[existing.Length + 3] = Clamp((float)(Math.Sqrt(Math.Max(0, innovVar)) / norm), 0, 5);
        return result;
    }

    // ── Rec #61: Rank-Based Feature Normalisation ─────────────────────────────

    /// <summary>
    /// Transforms each feature to its empirical percentile rank within the provided
    /// history window. Returns a new float[] of the same length as <paramref name="features"/>
    /// with values in [0, 1]. Robust to outliers and distributional shifts.
    /// </summary>
    public static float[] RankNormaliseFeatures(float[] features, IReadOnlyList<float[]> history)
    {
        if (history.Count == 0) return features;
        var result = new float[features.Length];
        for (int fi = 0; fi < features.Length; fi++)
        {
            float val = features[fi];
            int below = 0, total = 0;
            foreach (var h in history)
            {
                if (h.Length > fi) { below += h[fi] < val ? 1 : 0; total++; }
            }
            result[fi] = total > 0 ? (float)below / total : 0.5f;
        }
        return result;
    }

    // ── Rec #62: Hawkes Process Intensity Feature ─────────────────────────────

    /// <summary>
    /// Appends the current Hawkes process intensity λ(t) as a single feature.
    /// λ(t) = μ + α·Σ_i exp(-β·Δt_i) where Δt_i = time since i-th past signal in bars.
    /// </summary>
    public static float[] AppendHawkesIntensityFeature(
        float[] existing, double mu, double alpha, double beta,
        IEnumerable<double> signalAgesInBars)
    {
        double intensity = mu;
        foreach (double age in signalAgesInBars)
            if (age >= 0) intensity += alpha * Math.Exp(-beta * age);

        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length] = Clamp((float)intensity, 0, 10);
        return result;
    }

    // ── Rec #98: B-Spline Functional Data Analysis Features ───────────────────

    /// <summary>
    /// Fits a natural cubic spline with K=6 equally-spaced knots to the close price
    /// series and returns the 6 B-spline basis coefficients as features.
    /// Captures smooth shape: level, trend, curvature, and inflection points.
    /// </summary>
    public static float[] AppendBSplineFeatures(float[] existing, List<Candle> window)
    {
        if (window.Count < 6) return existing;

        int n    = window.Count;
        int K    = 6;
        var y    = window.Select(c => (double)c.Close).ToArray();
        double yMean = y.Average();
        double yStd  = Math.Sqrt(y.Select(v => (v - yMean) * (v - yMean)).Average()) + 1e-9;

        // Build design matrix for cubic B-spline basis (simplified: polynomial basis up to degree 5)
        // Use Chebyshev-like basis: T_k(t) = cos(k * arccos(2t-1)) for t in [0,1]
        double[] t = Enumerable.Range(0, n).Select(i => (double)i / (n - 1)).ToArray();
        var X = new double[n, K];
        for (int i = 0; i < n; i++)
        {
            double theta = Math.Acos(Math.Max(-1, Math.Min(1, 2 * t[i] - 1)));
            for (int k = 0; k < K; k++)
                X[i, k] = Math.Cos(k * theta);
        }

        // OLS: beta = (X^T X)^{-1} X^T y using normal equations (6x6 system)
        var XtX = new double[K, K];
        var Xty = new double[K];
        for (int j = 0; j < K; j++)
        {
            for (int l = 0; l < K; l++)
            {
                double s = 0; for (int i = 0; i < n; i++) s += X[i, j] * X[i, l];
                XtX[j, l] = s;
            }
            double s2 = 0;
            for (int i = 0; i < n; i++) s2 += X[i, j] * (y[i] - yMean) / yStd;
            Xty[j] = s2;
        }

        // Gaussian elimination on 6x6 system
        var beta = SolveLinearSystem(XtX, Xty, K);
        if (beta is null) return existing;

        var result = new float[existing.Length + K];
        Array.Copy(existing, result, existing.Length);
        for (int k = 0; k < K; k++)
            result[existing.Length + k] = Clamp((float)beta[k], -5, 5);
        return result;
    }

    // ── Rec #99: Topological Data Analysis — Persistence Features ─────────────

    /// <summary>
    /// Computes 3 features from the 0-homology persistence diagram of the close
    /// price series: max persistence (trend strength), mean persistence, and
    /// number of components at ε = ATR.
    /// </summary>
    public static float[] AppendPersistenceFeatures(float[] existing, List<Candle> window)
    {
        if (window.Count < 5) return existing;

        var prices = window.Select(c => (double)c.Close).ToArray();
        int n = prices.Length;
        double atr = (double)CalculateATR(window, Math.Min(14, n - 1));
        double norm = atr > 0 ? atr : 1.0;

        // Build 0-homology persistence: track local minima / maxima as birth/death of components
        // Simplified: sort prices and track connected-component merges using Union-Find
        var indexedPrices = prices.Select((v, i) => (v, i)).OrderBy(p => p.v).ToList();
        var parent = Enumerable.Range(0, n).ToArray();
        var birthLevel = new double[n];
        var persistences = new List<double>();

        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        foreach (var (level, idx) in indexedPrices)
        {
            birthLevel[idx] = level;
            // Check left and right neighbours
            foreach (int nb in new[] { idx - 1, idx + 1 })
            {
                if (nb < 0 || nb >= n) continue;
                if (prices[nb] > level) continue; // not yet active
                int ri = Find(idx), rn = Find(nb);
                if (ri == rn) continue;
                // Merge: younger component dies
                double persistence = level - Math.Min(birthLevel[ri], birthLevel[rn]);
                persistences.Add(persistence / norm);
                parent[ri] = rn; // merge
            }
        }

        double maxPersist  = persistences.Count > 0 ? persistences.Max() : 0;
        double meanPersist = persistences.Count > 0 ? persistences.Average() : 0;
        int    compAtAtr   = 1 + persistences.Count(p => p * norm > atr); // components surviving > ATR

        var result = new float[existing.Length + 3];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)maxPersist,  0, 10);
        result[existing.Length + 1] = Clamp((float)meanPersist, 0, 10);
        result[existing.Length + 2] = Clamp((float)compAtAtr,   0, 20);
        return result;
    }

    // ── Rec #102: Nadaraya-Watson Rolling Kernel Regression Features ───────────

    /// <summary>
    /// Computes a time-decaying Nadaraya-Watson kernel regression estimate of the
    /// close price and returns 2 features: the NW estimate relative to last close,
    /// and the residual (close - NW estimate) normalised by ATR.
    /// Uses Gaussian kernel with bandwidth h = window.Count / 4.
    /// </summary>
    public static float[] AppendKernelRegressionFeature(float[] existing, List<Candle> window)
    {
        if (window.Count < 5) return existing;

        int n = window.Count;
        double h = Math.Max(1.0, n / 4.0);
        double lastClose = (double)window[^1].Close;
        double atr = (double)CalculateATR(window, Math.Min(14, n - 1));
        double norm = atr > 0 ? atr : 1.0;

        // Nadaraya-Watson at the last time point (t = n-1)
        double sumW = 0, sumWY = 0;
        for (int i = 0; i < n; i++)
        {
            double dt = n - 1 - i;
            double w = Math.Exp(-0.5 * (dt / h) * (dt / h));
            sumW  += w;
            sumWY += w * (double)window[i].Close;
        }
        double nwEstimate = sumW > 0 ? sumWY / sumW : lastClose;
        double residual   = lastClose - nwEstimate;

        var result = new float[existing.Length + 2];
        Array.Copy(existing, result, existing.Length);
        result[existing.Length]     = Clamp((float)((nwEstimate - lastClose) / lastClose), -0.05f, 0.05f);
        result[existing.Length + 1] = Clamp((float)(residual / norm), -5, 5);
        return result;
    }

    // ── Rec #116: Continuous Wavelet Transform (CWT) scalogram features ───────
    /// <summary>
    /// Appends 5 energy features derived from a Morlet-like CWT applied to the last
    /// LookbackWindow close-price returns at scales [2,4,8,16,32].
    /// </summary>
    public static float[] AppendCwtFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = LookbackWindow;
        if (endIdx < W) return existing;

        double[] returns = new double[W];
        for (int i = 0; i < W; i++)
        {
            double c0 = (double)candles[endIdx - W + i].Close;
            double c1 = i > 0 ? (double)candles[endIdx - W + i - 1].Close : c0;
            returns[i] = c1 > 0 ? (c0 - c1) / c1 : 0;
        }

        int[] scales = { 2, 4, 8, 16, 32 };
        var features = new float[existing.Length + scales.Length];
        Array.Copy(existing, features, existing.Length);

        for (int si = 0; si < scales.Length; si++)
        {
            int s = scales[si];
            double energy = 0;
            for (int t = 0; t < W; t++)
            {
                double conv = 0;
                for (int tau = -s; tau <= s; tau++)
                {
                    int idx = t - tau;
                    if (idx < 0 || idx >= W) continue;
                    double u = (double)tau / s;
                    double psi = Math.Cos(5.0 * u) * Math.Exp(-0.5 * u * u); // Morlet
                    conv += returns[idx] * psi;
                }
                energy += conv * conv;
            }
            features[existing.Length + si] = Clamp((float)(energy / W * 100), -10f, 10f);
        }
        return features;
    }

    // ── Rec #117: Lempel-Ziv complexity feature ───────────────────────────────
    /// <summary>
    /// Computes LZ-76 complexity of the binarised return direction sequence over LookbackWindow.
    /// </summary>
    public static float[] AppendLzComplexityFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = LookbackWindow;
        if (endIdx < W) return existing;

        // Binarise: 1 if close > prev close, else 0
        var seq = new int[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            seq[i] = candles[ci].Close > candles[Math.Max(0, ci - 1)].Close ? 1 : 0;
        }

        // LZ-76 complexity: count new sub-patterns
        int complexity = 0;
        int w = 0, k = 1;
        while (w + k <= W)
        {
            // Check if seq[w..w+k-1] appeared in seq[0..w+k-2]
            bool found = false;
            for (int start = 0; start <= w - 1 && !found; start++)
            {
                bool match = true;
                for (int j = 0; j < k && match; j++)
                    if (seq[start + j] != seq[w + j]) match = false;
                if (match) found = true;
            }
            if (found) { k++; }
            else { complexity++; w += k; k = 1; }
        }
        double lzNorm = (double)complexity / W;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)(lzNorm * 10), -5f, 5f);
        return features;
    }

    // ── Rec #118: Lyapunov exponent feature ───────────────────────────────────
    /// <summary>
    /// Estimates the maximal Lyapunov exponent via divergence of nearest neighbours
    /// in a phase-space embedding (dim=3, lag=1) over the LookbackWindow.
    /// </summary>
    public static float[] AppendLyapunovFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W  = LookbackWindow;
        const int D  = 3;
        if (endIdx < W) return existing;

        double[] x = new double[W];
        for (int i = 0; i < W; i++)
            x[i] = (double)candles[endIdx - W + i].Close;

        int N = W - D + 1;
        if (N < 10)
        {
            var pad = new float[existing.Length + 1];
            Array.Copy(existing, pad, existing.Length);
            return pad;
        }

        // Build phase-space vectors
        double[][] pts = new double[N][];
        for (int i = 0; i < N; i++)
        {
            pts[i] = new double[D];
            for (int d = 0; d < D; d++) pts[i][d] = x[i + d];
        }

        // Estimate MLE via average log divergence of nearest neighbours
        double sumLog = 0; int cnt = 0;
        for (int i = 0; i < N - 1; i++)
        {
            double minDist = double.MaxValue;
            int nn = -1;
            for (int j = 0; j < N; j++)
            {
                if (Math.Abs(i - j) < 3) continue;
                double dist = 0;
                for (int d = 0; d < D; d++) dist += (pts[i][d] - pts[j][d]) * (pts[i][d] - pts[j][d]);
                if (dist < minDist) { minDist = dist; nn = j; }
            }
            if (nn < 0 || nn + 1 >= N || i + 1 >= N) continue;
            double d0 = Math.Sqrt(minDist) + 1e-10;
            double d1 = 0;
            for (int d = 0; d < D; d++) d1 += (pts[i+1][d] - pts[nn+1][d]) * (pts[i+1][d] - pts[nn+1][d]);
            d1 = Math.Sqrt(d1) + 1e-10;
            sumLog += Math.Log(d1 / d0);
            cnt++;
        }
        double mle = cnt > 0 ? sumLog / cnt : 0;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)(mle * 10), -5f, 5f);
        return features;
    }

    // ── Rec #119: Recurrence Quantification Analysis (RQA) features ───────────
    /// <summary>
    /// Computes Recurrence Rate (RR), Determinism (DET), and Laminarity (LAM)
    /// from a recurrence matrix over a 30-candle window.
    /// </summary>
    public static float[] AppendRqaFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 30;
        if (endIdx < W) return [.. existing, 0f, 0f, 0f];

        double[] x = new double[W];
        for (int i = 0; i < W; i++) x[i] = (double)candles[endIdx - W + i].Close;

        double mean = x.Average();
        double std  = Math.Sqrt(x.Select(v => (v - mean) * (v - mean)).Average()) + 1e-10;
        double eps  = 0.1 * std; // 10% of std as threshold

        // Build recurrence matrix
        bool[,] R = new bool[W, W];
        int totalRec = 0;
        for (int i = 0; i < W; i++)
            for (int j = 0; j < W; j++)
                if (Math.Abs(x[i] - x[j]) < eps) { R[i,j] = true; totalRec++; }

        double rr = (double)totalRec / (W * W);

        // DET: fraction of recurrence points forming diagonal lines of length >= 2
        int diagPts = 0;
        for (int d = -(W-2); d < W; d++)
        {
            int len = 0;
            for (int i = 0; i < W; i++)
            {
                int j = i - d;
                if (j < 0 || j >= W) continue;
                if (R[i,j]) { len++; if (len >= 2) diagPts++; }
                else len = 0;
            }
        }
        double det = totalRec > 0 ? (double)diagPts / totalRec : 0;

        // LAM: fraction forming vertical lines of length >= 2
        int vertPts = 0;
        for (int i = 0; i < W; i++)
        {
            int len = 0;
            for (int j = 0; j < W; j++)
            {
                if (R[i,j]) { len++; if (len >= 2) vertPts++; }
                else len = 0;
            }
        }
        double lam = totalRec > 0 ? (double)vertPts / totalRec : 0;

        var features = new float[existing.Length + 3];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length]     = Clamp((float)(rr  * 10), -5f, 5f);
        features[existing.Length + 1] = Clamp((float)(det * 5),  -5f, 5f);
        features[existing.Length + 2] = Clamp((float)(lam * 5),  -5f, 5f);
        return features;
    }

    // ── Rec #120: Higuchi fractal dimension feature ───────────────────────────
    /// <summary>
    /// Computes Higuchi fractal dimension of the 50-candle close-price series using k_max=8.
    /// </summary>
    public static float[] AppendHiguchiFractalFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W    = 50;
        const int Kmax = 8;
        if (endIdx < W) return [.. existing, 0f];

        double[] x = new double[W];
        for (int i = 0; i < W; i++) x[i] = (double)candles[endIdx - W + i].Close;

        // Compute L(k) for each k
        double[] logK = new double[Kmax];
        double[] logL = new double[Kmax];

        for (int k = 1; k <= Kmax; k++)
        {
            double Lk = 0;
            for (int m = 1; m <= k; m++)
            {
                double Lmk = 0;
                int cnt = (int)Math.Floor((double)(W - m) / k);
                for (int i = 1; i <= cnt; i++)
                    Lmk += Math.Abs(x[m - 1 + i * k] - x[m - 1 + (i - 1) * k]);
                if (cnt > 0)
                    Lmk = Lmk * (W - 1) / ((double)k * cnt * k);
                Lk += Lmk;
            }
            Lk /= k;
            logK[k-1] = Math.Log(k);
            logL[k-1] = Lk > 0 ? Math.Log(Lk) : 0;
        }

        // OLS slope of log(L) vs log(k)
        double meanK = logK.Average(), meanL = logL.Average();
        double num = 0, den = 0;
        for (int i = 0; i < Kmax; i++) { num += (logK[i] - meanK) * (logL[i] - meanL); den += (logK[i] - meanK) * (logK[i] - meanK); }
        double hfd = den > 0 ? -num / den : 1.5; // fractal dim in [1,2]

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)((hfd - 1.5) * 4), -5f, 5f); // centre around 1.5
        return features;
    }

    // ── Rec #121: Amihud illiquidity ratio feature ────────────────────────────
    /// <summary>
    /// Computes Amihud (2002) illiquidity = mean(|return|/volume) over 20 candles.
    /// </summary>
    public static float[] AppendAmihudFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 20;
        if (endIdx < W) return [.. existing, 0f];

        double sum = 0;
        for (int i = endIdx - W + 1; i <= endIdx; i++)
        {
            double prev  = i > 0 ? (double)candles[i-1].Close : (double)candles[i].Close;
            double ret   = prev > 0 ? Math.Abs(((double)candles[i].Close - prev) / prev) : 0;
            double vol   = (double)candles[i].Volume + 1.0;
            sum += ret / vol;
        }
        double amihud = sum / W * 1e6; // scale up for typical FX volumes

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)amihud, -5f, 5f);
        return features;
    }

    // ── Rec #122: Kaufman Efficiency Ratio feature ────────────────────────────
    /// <summary>
    /// Computes Kaufman's Efficiency Ratio = |directional_move| / sum(|daily_moves|) over 14 candles.
    /// ER → 1 = strong trend; ER → 0 = noise.
    /// </summary>
    public static float[] AppendKaufmanErFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 14;
        if (endIdx < W) return [.. existing, 0f];

        double first = (double)candles[endIdx - W].Close;
        double last  = (double)candles[endIdx].Close;
        double directional = Math.Abs(last - first);

        double totalPath = 0;
        for (int i = endIdx - W + 1; i <= endIdx; i++)
            totalPath += Math.Abs((double)candles[i].Close - (double)candles[i-1].Close);

        double er = totalPath > 0 ? directional / totalPath : 0;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)((er - 0.5) * 4), -5f, 5f); // centre at 0.5
        return features;
    }

    // ── Rec #142: Correlation surprise feature ────────────────────────────────
    /// <summary>
    /// Computes a correlation surprise z-score: deviation of current Pearson ρ between
    /// this symbol's returns and a market index proxy (mean of all candles at this endIdx)
    /// from a 60-candle rolling baseline. Requires at least 30 candles.
    /// Uses close-to-close return autocorrelation as a self-correlation surprise proxy
    /// when cross-pair data is unavailable.
    /// </summary>
    public static float[] AppendCorrelationSurpriseFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W        = 20; // current window
        const int Baseline = 60;
        if (endIdx < Baseline) return [.. existing, 0f];

        // Use 1-lag autocorrelation as proxy
        double AutoCorr(int start, int len)
        {
            if (len < 3) return 0;
            double[] r = new double[len];
            for (int i = 0; i < len; i++)
            {
                int ci = start + i;
                double prev = ci > 0 ? (double)candles[ci-1].Close : (double)candles[ci].Close;
                r[i] = prev > 0 ? ((double)candles[ci].Close - prev) / prev : 0;
            }
            double mu = r.Average();
            double num = 0, den = 0;
            for (int i = 1; i < len; i++) { num += (r[i] - mu) * (r[i-1] - mu); den += (r[i] - mu) * (r[i] - mu); }
            return den > 0 ? num / den : 0;
        }

        double currentRho  = AutoCorr(endIdx - W + 1, W);
        double baselineRho = AutoCorr(endIdx - Baseline + 1, Baseline);

        // Approximate std of baseline via sub-window variance
        double[] subRhos = new double[5];
        for (int s = 0; s < 5; s++)
            subRhos[s] = AutoCorr(endIdx - Baseline + s * 12, 12);
        double bMean = subRhos.Average();
        double bStd  = Math.Sqrt(subRhos.Select(v => (v - bMean) * (v - bMean)).Average()) + 1e-6;
        double zScore = (currentRho - baselineRho) / bStd;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)zScore, -5f, 5f);
        return features;
    }

    // ── Rec #143: Kyle's lambda feature ──────────────────────────────────────
    /// <summary>
    /// Estimates Kyle's lambda = cov(Δprice, signed_volume) / var(signed_volume) over 20 candles.
    /// Signed volume = +volume if close>open (buyer-initiated), else -volume.
    /// </summary>
    public static float[] AppendKyleLambdaFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 20;
        if (endIdx < W) return [.. existing, 0f];

        double[] dp  = new double[W];
        double[] sv  = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            double prev = ci > 0 ? (double)candles[ci-1].Close : (double)candles[ci].Open;
            dp[i] = (double)candles[ci].Close - prev;
            sv[i] = candles[ci].Close >= candles[ci].Open
                ? (double)candles[ci].Volume
                : -(double)candles[ci].Volume;
        }

        double dpMu = dp.Average(), svMu = sv.Average();
        double cov = 0, varSv = 0;
        for (int i = 0; i < W; i++) { cov += (dp[i] - dpMu) * (sv[i] - svMu); varSv += (sv[i] - svMu) * (sv[i] - svMu); }
        double lambda = varSv > 0 ? cov / varSv : 0;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)(lambda * 1000), -5f, 5f); // scale for FX
        return features;
    }

    // ── Rec #144: Roll bid-ask spread feature ────────────────────────────────
    /// <summary>
    /// Estimates effective bid-ask spread via Roll (1984): spread = 2*sqrt(-cov(Δp_t, Δp_{t-1})).
    /// </summary>
    public static float[] AppendRollSpreadFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 20;
        if (endIdx < W + 1) return [.. existing, 0f];

        double[] dp = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            double prev = (double)candles[Math.Max(0, ci-1)].Close;
            dp[i] = (double)candles[ci].Close - prev;
        }

        double mu = dp.Average();
        double serialCov = 0;
        for (int i = 1; i < W; i++)
            serialCov += (dp[i] - mu) * (dp[i-1] - mu);
        serialCov /= (W - 1);

        double spread = serialCov < 0 ? 2.0 * Math.Sqrt(-serialCov) : 0;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)(spread * 10000), -5f, 5f); // pips
        return features;
    }

    // ── Rec #145: Order flow imbalance features ───────────────────────────────
    /// <summary>
    /// Computes OFI = (buy_vol - sell_vol)/total_vol and OFI EWMA (α=0.2) over 20 candles.
    /// Buy-initiated if close >= open (Lee-Ready tick rule proxy).
    /// </summary>
    public static float[] AppendOrderFlowFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W     = 20;
        const double α  = 0.2;
        if (endIdx < W) return [.. existing, 0f, 0f];

        double buyVol = 0, sellVol = 0;
        double ewmaOfi = 0;
        bool first = true;
        for (int i = endIdx - W + 1; i <= endIdx; i++)
        {
            double vol = (double)candles[i].Volume;
            bool isBuy = candles[i].Close >= candles[i].Open;
            if (isBuy) buyVol += vol; else sellVol += vol;
            double totalV = buyVol + sellVol > 0 ? buyVol + sellVol : 1;
            double ofi = (buyVol - sellVol) / totalV;
            ewmaOfi = first ? ofi : α * ofi + (1 - α) * ewmaOfi;
            first = false;
        }
        double totalVol = buyVol + sellVol > 0 ? buyVol + sellVol : 1;
        double finalOfi = (buyVol - sellVol) / totalVol;

        var features = new float[existing.Length + 2];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length]     = Clamp((float)(finalOfi * 3), -5f, 5f);
        features[existing.Length + 1] = Clamp((float)(ewmaOfi * 3), -5f, 5f);
        return features;
    }

    // ── Rec #116: CWT energy features ────────────────────────────────────────

    /// <summary>
    /// Appends 5 Continuous Wavelet Transform (CWT) energy features computed from
    /// the close prices in <paramref name="candles"/>[0..<paramref name="endIdx"/>].
    /// Scales: 2, 4, 8, 16, 32 bars. Energy = sum of squared wavelet coefficients / N.
    /// </summary>
    public static float[] AppendCwtFeatures(float[] existing, List<Candle> candles, int endIdx)
    {
        const int NumScales = 5;
        int[] scales = { 2, 4, 8, 16, 32 };
        var result = new float[existing.Length + NumScales];
        Array.Copy(existing, result, existing.Length);

        int n = Math.Min(endIdx + 1, candles.Count);
        if (n < 4) return result;

        double[] closes = new double[n];
        for (int i = 0; i < n; i++) closes[i] = (double)candles[i].Close;

        for (int si = 0; si < NumScales; si++)
        {
            int s = scales[si];
            double energy = 0;
            int count = 0;
            for (int t = s; t < n; t++)
            {
                double coeff = closes[t] - closes[t - s];
                energy += coeff * coeff;
                count++;
            }
            result[existing.Length + si] = count > 0 ? Clamp((float)(energy / count), 0, 1e6f) : 0f;
        }
        return result;
    }

    // ── Rec #117: Lempel-Ziv complexity ──────────────────────────────────────

    /// <summary>
    /// Appends 1 normalised Lempel-Ziv complexity feature.
    /// Binarises returns (up=1 / down=0), computes LZ76 complexity, normalises by N/log2(N).
    /// </summary>
    public static float[] AppendLzComplexityFeature(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 4) return result;

        var bits = new bool[n - 1];
        for (int i = 1; i < n; i++)
            bits[i - 1] = candles[i].Close >= candles[i - 1].Close;

        int complexity = LzComplexity(bits);
        double norm = (n - 1) > 1 ? (n - 1) / Math.Log2(n - 1) : 1.0;
        result[existing.Length] = Clamp((float)(complexity / norm), 0, 10);
        return result;
    }

    private static int LzComplexity(bool[] s)
    {
        int n = s.Length;
        if (n == 0) return 0;
        int c = 1, l = 1, i = 0, k = 1, kMax = 1;
        while (true)
        {
            if (i + k - 1 >= n || l + k - 1 >= n) { c++; break; }
            if (s[i + k - 1] == s[l + k - 1])
            {
                k++;
                if (l + k > n) { c++; break; }
            }
            else
            {
                if (k > kMax) kMax = k;
                i++;
                if (i == l) { c++; l += kMax; if (l >= n) break; i = 0; k = 1; kMax = 1; }
                else k = 1;
            }
        }
        return c;
    }

    // ── Rec #118: Lyapunov exponent ───────────────────────────────────────────

    /// <summary>
    /// Appends 1 maximum Lyapunov exponent (MLE) feature.
    /// Uses a Rosenstein et al. nearest-neighbour divergence estimate on close-price returns.
    /// </summary>
    public static float[] AppendLyapunovFeature(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 10) return result;

        double[] returns = new double[n - 1];
        for (int i = 1; i < n; i++)
        {
            double prev = (double)candles[i - 1].Close;
            returns[i - 1] = prev != 0 ? ((double)candles[i].Close - prev) / prev : 0;
        }

        int m = returns.Length;
        int evolveSteps = Math.Max(1, m / 10);
        double sumLog = 0;
        int pairs = 0;

        for (int i = 0; i < m; i++)
        {
            double minDist = double.MaxValue;
            int nearestJ = -1;
            for (int j = 0; j < m; j++)
            {
                if (Math.Abs(i - j) < 5) continue;
                double d = Math.Abs(returns[i] - returns[j]);
                if (d < minDist) { minDist = d; nearestJ = j; }
            }
            if (nearestJ < 0 || minDist < 1e-12) continue;

            int evolveI = Math.Min(i + evolveSteps, m - 1);
            int evolveJ = Math.Min(nearestJ + evolveSteps, m - 1);
            double evolvedDist = Math.Abs(returns[evolveI] - returns[evolveJ]);
            if (evolvedDist > 1e-12)
            {
                sumLog += Math.Log(evolvedDist / minDist);
                pairs++;
            }
        }

        double mle = pairs > 0 ? sumLog / (pairs * evolveSteps) : 0;
        result[existing.Length] = Clamp((float)mle, -5, 5);
        return result;
    }

    // ── Rec #119: Recurrence Quantification Analysis ──────────────────────────

    /// <summary>
    /// Appends 3 RQA features: Recurrence Rate (RR), Determinism (DET), Laminarity (LAM).
    /// Uses a threshold of 0.1 × std(returns) for recurrence.
    /// </summary>
    public static float[] AppendRqaFeatures(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 3];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 6) return result;

        double[] returns = new double[n - 1];
        for (int i = 1; i < n; i++)
        {
            double prev = (double)candles[i - 1].Close;
            returns[i - 1] = prev != 0 ? ((double)candles[i].Close - prev) / prev : 0;
        }

        int m = returns.Length;
        double mean = returns.Average();
        double variance = returns.Select(r => (r - mean) * (r - mean)).Average();
        double std = Math.Sqrt(variance);
        double thresh = 0.1 * (std > 0 ? std : 1e-8);

        bool[,] R = new bool[m, m];
        int recCount = 0;
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                if (Math.Abs(returns[i] - returns[j]) <= thresh)
                {
                    R[i, j] = true;
                    recCount++;
                }

        double rr = (double)recCount / (m * m);

        // DET: fraction of recurrence points in diagonal lines of length >= 2
        int diagRec = 0;
        for (int d = -(m - 1); d <= m - 1; d++)
        {
            int lineLen = 0;
            for (int i = 0; i < m; i++)
            {
                int j = i + d;
                if (j < 0 || j >= m) { if (lineLen >= 2) diagRec += lineLen; lineLen = 0; continue; }
                if (R[i, j]) lineLen++;
                else { if (lineLen >= 2) diagRec += lineLen; lineLen = 0; }
            }
            if (lineLen >= 2) diagRec += lineLen;
        }
        double det = recCount > 0 ? (double)diagRec / recCount : 0;

        // LAM: fraction of recurrence points in vertical lines of length >= 2
        int vertRec = 0;
        for (int j = 0; j < m; j++)
        {
            int lineLen = 0;
            for (int i = 0; i < m; i++)
            {
                if (R[i, j]) lineLen++;
                else { if (lineLen >= 2) vertRec += lineLen; lineLen = 0; }
            }
            if (lineLen >= 2) vertRec += lineLen;
        }
        double lam = recCount > 0 ? (double)vertRec / recCount : 0;

        result[existing.Length]     = Clamp((float)rr,  0, 1);
        result[existing.Length + 1] = Clamp((float)det, 0, 1);
        result[existing.Length + 2] = Clamp((float)lam, 0, 1);
        return result;
    }

    // ── Rec #120: Higuchi Fractal Dimension ───────────────────────────────────

    /// <summary>
    /// Appends 1 Higuchi Fractal Dimension feature.
    /// kMax=8, computed via least-squares regression of log(Lm) vs log(1/k).
    /// </summary>
    public static float[] AppendHiguchiFractalFeature(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 10) return result;

        double[] x = new double[n];
        for (int i = 0; i < n; i++) x[i] = (double)candles[i].Close;

        int kMax = Math.Min(8, n / 2);
        var logK = new double[kMax];
        var logL = new double[kMax];

        for (int k = 1; k <= kMax; k++)
        {
            double Lk = 0;
            for (int m = 1; m <= k; m++)
            {
                double sum = 0;
                int count = (int)Math.Floor((double)(n - m) / k);
                for (int ii = 1; ii <= count; ii++)
                    sum += Math.Abs(x[m - 1 + ii * k] - x[m - 1 + (ii - 1) * k]);
                if (count > 0)
                    Lk += sum * (n - 1) / ((double)count * k);
            }
            Lk /= k;
            logK[k - 1] = -Math.Log(k);
            logL[k - 1] = Math.Log(Math.Max(Lk, 1e-12));
        }

        double meanK = logK.Average(), meanL = logL.Average();
        double num = 0, den = 0;
        for (int i = 0; i < kMax; i++) { num += (logK[i] - meanK) * (logL[i] - meanL); den += (logK[i] - meanK) * (logK[i] - meanK); }
        double hfd = den > 0 ? num / den : 1.5;

        result[existing.Length] = Clamp((float)hfd, 1.0f, 3.0f);
        return result;
    }

    // ── Rec #121: Amihud Illiquidity ──────────────────────────────────────────

    /// <summary>
    /// Appends 1 Amihud illiquidity feature: mean(|return_t| / dollar_volume_t) × 1e6.
    /// Dollar volume is approximated as Close × Volume.
    /// </summary>
    public static float[] AppendAmihudFeature(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 2) return result;

        double sum = 0;
        int count = 0;
        for (int i = 1; i < n; i++)
        {
            double prev = (double)candles[i - 1].Close;
            double ret = prev > 0 ? Math.Abs(((double)candles[i].Close - prev) / prev) : 0;
            double vol = (double)candles[i].Volume * (double)candles[i].Close;
            if (vol > 0) { sum += ret / vol; count++; }
        }
        double amihud = count > 0 ? sum / count * 1e6 : 0;
        result[existing.Length] = Clamp((float)amihud, 0, 100);
        return result;
    }

    // ── Rec #122: Kaufman Efficiency Ratio ────────────────────────────────────

    /// <summary>
    /// Appends 1 Kaufman Efficiency Ratio (ER) feature.
    /// ER = |net direction| / sum(|bar changes|) over the window.
    /// ER = 1 → perfect trend, ER = 0 → pure noise.
    /// </summary>
    public static float[] AppendKaufmanErFeature(float[] existing, List<Candle> candles)
    {
        var result = new float[existing.Length + 1];
        Array.Copy(existing, result, existing.Length);

        int n = candles.Count;
        if (n < 2) return result;

        double direction = Math.Abs((double)candles[^1].Close - (double)candles[0].Close);
        double volatility = 0;
        for (int i = 1; i < n; i++)
            volatility += Math.Abs((double)candles[i].Close - (double)candles[i - 1].Close);

        double er = volatility > 0 ? direction / volatility : 0;
        result[existing.Length] = Clamp((float)er, 0, 1);
        return result;
    }

    // ── Rec #150: Permutation entropy feature ─────────────────────────────────
    /// <summary>
    /// Computes permutation entropy of the close-price series over a 20-candle window,
    /// embedding dimension m=3 (6 possible ordinal patterns).
    /// </summary>
    public static float[] AppendPermutationEntropyFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 20;
        const int M = 3;
        if (endIdx < W) return [.. existing, 0f];

        int[] counts = new int[6]; // 3! = 6 patterns
        int total = 0;

        for (int i = endIdx - W + M - 1; i <= endIdx; i++)
        {
            double a = (double)candles[i - 2].Close;
            double b = (double)candles[i - 1].Close;
            double c = (double)candles[i].Close;
            // Map (a,b,c) to ordinal pattern index 0-5
            int idx;
            if      (a <= b && b <= c) idx = 0;
            else if (a <= c && c <  b) idx = 1;
            else if (b <  a && a <= c) idx = 2;
            else if (c <  a && a <= b) idx = 3; // fixed: was wrong
            else if (b <= c && c <  a) idx = 4;
            else                        idx = 5;
            counts[idx]++;
            total++;
        }

        double entropy = 0;
        for (int i = 0; i < 6; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / total;
            entropy -= p * Math.Log(p);
        }
        double maxEntropy = Math.Log(6);
        double normEntropy = maxEntropy > 0 ? entropy / maxEntropy : 0;

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)((normEntropy - 0.5) * 6), -5f, 5f);
        return features;
    }

    // ── Rec #151: Sample entropy feature ──────────────────────────────────────
    /// <summary>
    /// Computes SampEn(m=2, r=0.2σ) over a 50-candle close-price window.
    /// </summary>
    public static float[] AppendSampleEntropyFeature(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 50;
        const int M = 2;
        if (endIdx < W) return [.. existing, 0f];

        double[] x = new double[W];
        for (int i = 0; i < W; i++) x[i] = (double)candles[endIdx - W + i].Close;

        double mean = x.Average();
        double std  = Math.Sqrt(x.Select(v => (v - mean) * (v - mean)).Average()) + 1e-10;
        double r    = 0.2 * std;

        int A = 0, B = 0;
        for (int i = 0; i < W - M; i++)
        {
            for (int j = i + 1; j < W - M; j++)
            {
                // Check m-template match
                bool matchM = true, matchM1 = true;
                for (int k = 0; k < M && matchM; k++)
                    if (Math.Abs(x[i+k] - x[j+k]) > r) matchM = false;
                if (!matchM) continue;
                B++;
                // Check m+1-template match
                if (Math.Abs(x[i+M] - x[j+M]) > r) matchM1 = false;
                if (matchM1) A++;
            }
        }

        double sampEn = B > 0 ? -Math.Log((double)A / B + 1e-10) : 2.0;
        sampEn = Math.Min(sampEn, 3.0); // cap at 3

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)((sampEn - 1.5) * 2), -5f, 5f);
        return features;
    }

    // ── Rec #152: EMD features ────────────────────────────────────────────────
    /// <summary>
    /// Decomposes 60-candle close returns into 3 IMFs via envelope sifting.
    /// Returns energy and zero-crossing rate for each IMF (6 features total).
    /// </summary>
    public static float[] AppendEmdFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 60;
        if (endIdx < W) return [.. existing, 0f, 0f, 0f, 0f, 0f, 0f];

        double[] signal = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            double prev = ci > 0 ? (double)candles[ci-1].Close : (double)candles[ci].Close;
            signal[i] = prev > 0 ? ((double)candles[ci].Close - prev) / prev : 0;
        }

        var features = new float[existing.Length + 6];
        Array.Copy(existing, features, existing.Length);

        double[] residual = (double[])signal.Clone();

        for (int imf = 0; imf < 3; imf++)
        {
            // Simple sifting: subtract running mean of upper+lower envelope
            double[] upper = new double[W];
            double[] lower = new double[W];
            for (int t = 0; t < W; t++)
            {
                // Compute local max/min in ±3 window as envelope proxy
                int lo = Math.Max(0, t-3), hi = Math.Min(W-1, t+3);
                upper[t] = residual[lo..(hi+1)].Max();
                lower[t] = residual[lo..(hi+1)].Min();
            }
            double[] imfComp = new double[W];
            for (int t = 0; t < W; t++)
                imfComp[t] = residual[t] - (upper[t] + lower[t]) / 2.0;

            for (int t = 0; t < W; t++)
                residual[t] -= imfComp[t];

            // Energy and zero-crossing rate
            double energy = imfComp.Select(v => v * v).Average();
            int zc = 0;
            for (int t = 1; t < W; t++)
                if (imfComp[t-1] * imfComp[t] < 0) zc++;
            double zcr = (double)zc / W;

            features[existing.Length + imf * 2]     = Clamp((float)(energy * 1000), -5f, 5f);
            features[existing.Length + imf * 2 + 1] = Clamp((float)((zcr - 0.2) * 10), -5f, 5f);
        }
        return features;
    }

    // ── Rec #153: SAX bag-of-words features ───────────────────────────────────
    /// <summary>
    /// Normalises a 40-candle return window, PAA-reduces to 8 segments,
    /// maps to 4-letter alphabet, returns 4-bin frequency histogram.
    /// </summary>
    public static float[] AppendSaxFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W    = 40;
        const int W_paa = 8;
        if (endIdx < W) return [.. existing, 0f, 0f, 0f, 0f];

        double[] ret = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            double prev = ci > 0 ? (double)candles[ci-1].Close : (double)candles[ci].Close;
            ret[i] = prev > 0 ? ((double)candles[ci].Close - prev) / prev : 0;
        }

        // Z-normalise
        double mu  = ret.Average();
        double std = Math.Sqrt(ret.Select(v => (v - mu) * (v - mu)).Average()) + 1e-10;
        for (int i = 0; i < W; i++) ret[i] = (ret[i] - mu) / std;

        // PAA: average each segment of W/W_paa = 5 samples
        int segLen = W / W_paa;
        double[] paa = new double[W_paa];
        for (int s = 0; s < W_paa; s++)
        {
            double sum = 0;
            for (int j = 0; j < segLen; j++) sum += ret[s * segLen + j];
            paa[s] = sum / segLen;
        }

        // Gaussian breakpoints for 4 symbols: [-∞, -0.674, 0, 0.674, +∞]
        double[] breaks = { -0.674, 0.0, 0.674 };
        int[] hist = new int[4];
        foreach (double v in paa)
        {
            int sym = 0;
            foreach (double b in breaks) if (v > b) sym++;
            hist[sym]++;
        }

        var features = new float[existing.Length + 4];
        Array.Copy(existing, features, existing.Length);
        for (int i = 0; i < 4; i++)
            features[existing.Length + i] = Clamp((float)((double)hist[i] / W_paa * 4 - 0.5), -5f, 5f);
        return features;
    }

    // ── Rec #154: Matrix Profile motif features ───────────────────────────────
    /// <summary>
    /// Computes simplified matrix profile (z-normalised subsequence distances) over
    /// a 60-candle window with subsequence length 8.
    /// Returns motif distance (min MP) and discord distance (max MP).
    /// </summary>
    public static float[] AppendMatrixProfileFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W  = 60;
        const int SL = 8; // subsequence length
        if (endIdx < W) return [.. existing, 0f, 0f];

        double[] x = new double[W];
        for (int i = 0; i < W; i++) x[i] = (double)candles[endIdx - W + i].Close;

        int N = W - SL + 1;
        double[] mp = new double[N];
        for (int i = 0; i < N; i++) mp[i] = double.MaxValue;

        for (int i = 0; i < N; i++)
        {
            // Extract and z-normalise subsequence i
            double sumI = 0, sum2I = 0;
            for (int k = 0; k < SL; k++) { sumI += x[i+k]; sum2I += x[i+k]*x[i+k]; }
            double muI  = sumI / SL;
            double stdI = Math.Sqrt(Math.Max(0, sum2I/SL - muI*muI)) + 1e-10;

            for (int j = 0; j < N; j++)
            {
                if (Math.Abs(i - j) < SL / 2) continue; // exclusion zone

                double sumJ = 0, sum2J = 0, cross = 0;
                for (int k = 0; k < SL; k++) { sumJ += x[j+k]; sum2J += x[j+k]*x[j+k]; cross += x[i+k]*x[j+k]; }
                double muJ  = sumJ / SL;
                double stdJ = Math.Sqrt(Math.Max(0, sum2J/SL - muJ*muJ)) + 1e-10;

                double pearson = (cross/SL - muI*muJ) / (stdI * stdJ);
                pearson = Math.Clamp(pearson, -1, 1);
                double dist = Math.Sqrt(2 * SL * (1 - pearson));
                if (dist < mp[i]) mp[i] = dist;
            }
            if (mp[i] == double.MaxValue) mp[i] = 0;
        }

        double motifDist  = mp.Where(v => v < double.MaxValue).DefaultIfEmpty(0).Min();
        double discordDist = mp.Where(v => v < double.MaxValue).DefaultIfEmpty(0).Max();

        var features = new float[existing.Length + 2];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length]     = Clamp((float)((motifDist  - 2.0) * 1.5), -5f, 5f);
        features[existing.Length + 1] = Clamp((float)((discordDist - 4.0) * 1.0), -5f, 5f);
        return features;
    }

    // ── Rec #175: BPV realised volatility decomposition features ─────────────
    /// <summary>
    /// Computes Bipower Variation (BPV) and jump ratio over 20 candles.
    /// Returns [BPV ratio = BPV/RV, jump ratio = max(0, RV-BPV)/RV].
    /// </summary>
    public static float[] AppendBpvFeatures(float[] existing, IReadOnlyList<Candle> candles, int endIdx)
    {
        const int W = 20;
        if (endIdx < W) return [.. existing, 0f, 0f];

        double[] ret = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = endIdx - W + i;
            double prev = ci > 0 ? (double)candles[ci-1].Close : (double)candles[ci].Close;
            ret[i] = prev > 0 ? ((double)candles[ci].Close - prev) / prev : 0;
        }

        // Realised Variance
        double rv = ret.Select(r => r * r).Sum();

        // Bipower Variation = (π/2) * Σ|r_t||r_{t-1}|
        double bpv = 0;
        for (int t = 1; t < W; t++)
            bpv += Math.Abs(ret[t]) * Math.Abs(ret[t-1]);
        bpv *= Math.PI / 2.0;

        double bpvRatio  = rv > 0 ? Math.Min(bpv / rv, 2.0) : 1.0;
        double jumpRatio = rv > 0 ? Math.Max(0, rv - bpv) / rv : 0;

        var features = new float[existing.Length + 2];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length]     = Clamp((float)((bpvRatio  - 1.0) * 3), -5f, 5f);
        features[existing.Length + 1] = Clamp((float)(jumpRatio  * 5),         -5f, 5f);
        return features;
    }

    // ── Rec #183: Welch-like PSD dominant frequencies ────────────────────────
    /// <summary>
    /// Computes a Welch-like Power Spectral Density of the last LookbackWindow close returns.
    /// Splits returns into 4 half-overlapping segments, computes DFT magnitude per segment,
    /// averages across segments, and extracts the top-3 dominant frequencies (normalised 0–1)
    /// as features. Returns 3 features appended to <paramref name="existing"/>.
    /// </summary>
    public static float[] AppendPsdFeatures(float[] existing, IReadOnlyList<Candle> candles, int idx)
    {
        const int W = LookbackWindow; // 30
        if (idx < W) return [.. existing, 0f, 0f, 0f];

        // Compute log-returns over the window
        double[] returns = new double[W];
        for (int i = 0; i < W; i++)
        {
            int ci = idx - W + i;
            double prev = ci > 0 ? (double)candles[ci - 1].Close : (double)candles[ci].Close;
            returns[i] = prev > 0 ? ((double)candles[ci].Close - prev) / prev : 0;
        }

        // 4 half-overlapping segments
        const int numSegments = 4;
        int segLen  = W / 2;                        // 15 samples per segment
        int step    = (W - segLen) / (numSegments - 1); // step ≈ 5
        int freqBins = segLen / 2;                  // 7 positive-frequency bins

        double[] avgPsd = new double[freqBins];

        for (int s = 0; s < numSegments; s++)
        {
            int start = Math.Min(s * step, W - segLen);
            // DFT magnitude for each positive frequency bin
            for (int k = 0; k < freqBins; k++)
            {
                double re = 0, im = 0;
                for (int n = 0; n < segLen; n++)
                {
                    double angle = -2.0 * Math.PI * k * n / segLen;
                    re += returns[start + n] * Math.Cos(angle);
                    im += returns[start + n] * Math.Sin(angle);
                }
                avgPsd[k] += (re * re + im * im) / segLen;
            }
        }

        for (int k = 0; k < freqBins; k++)
            avgPsd[k] /= numSegments;

        // Find the indices of the top-3 dominant frequencies
        double totalPower = avgPsd.Sum() + 1e-12;
        int[] topIdx = Enumerable.Range(0, freqBins)
            .OrderByDescending(k => avgPsd[k])
            .Take(3)
            .OrderBy(k => k)
            .ToArray();

        // Normalise bin index to [0, 1]
        float f0 = freqBins > 1 ? (float)topIdx[0] / (freqBins - 1) : 0f;
        float f1 = freqBins > 1 && topIdx.Length > 1 ? (float)topIdx[1] / (freqBins - 1) : 0f;
        float f2 = freqBins > 1 && topIdx.Length > 2 ? (float)topIdx[2] / (freqBins - 1) : 0f;

        var features = new float[existing.Length + 3];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length]     = Clamp(f0, -5f, 5f);
        features[existing.Length + 1] = Clamp(f1, -5f, 5f);
        features[existing.Length + 2] = Clamp(f2, -5f, 5f);
        return features;
    }

    // ── Rec #184: Kolmogorov-Sinai entropy proxy ──────────────────────────────
    /// <summary>
    /// Computes a Kolmogorov-Sinai entropy proxy: -Σ(p_i × log(p_i)) where p_i is
    /// the empirical distribution of return sign sequences of length 3 (8 bins)
    /// over the last LookbackWindow candles. Result clamped to [-5, 5].
    /// Returns 1 feature appended to <paramref name="existing"/>.
    /// </summary>
    public static float[] AppendEntropyRateFeature(float[] existing, IReadOnlyList<Candle> candles, int idx)
    {
        const int W       = LookbackWindow; // 30
        const int SeqLen  = 3;              // length-3 sign sequences → 8 bins
        const int NumBins = 1 << SeqLen;    // 8

        if (idx < W) return [.. existing, 0f];

        // Compute sign sequence counts
        int[] hist = new int[NumBins];
        int count  = 0;

        for (int i = idx - W + SeqLen - 1; i <= idx; i++)
        {
            int sym = 0;
            for (int d = 0; d < SeqLen; d++)
            {
                int ci   = i - (SeqLen - 1 - d);
                int cPrev = ci > 0 ? ci - 1 : ci;
                int sign = (double)candles[ci].Close > (double)candles[cPrev].Close ? 1 : 0;
                sym = (sym << 1) | sign;
            }
            hist[sym]++;
            count++;
        }

        double entropy = 0.0;
        for (int b = 0; b < NumBins; b++)
        {
            if (hist[b] == 0) continue;
            double p = (double)hist[b] / count;
            entropy -= p * Math.Log(p);
        }

        var features = new float[existing.Length + 1];
        Array.Copy(existing, features, existing.Length);
        features[existing.Length] = Clamp((float)entropy, -5f, 5f);
        return features;
    }

    // ── Rec #206: Morphological features ─────────────────────────────────────
    /// <summary>
    /// Appends 4 mathematical-morphology features to <paramref name="features"/>:
    /// Erosion(5), Dilation(5), Morphological Gradient, and Morphological Opening Residual.
    /// Each feature is clamped to [−5, 5]. Appends 4 zeros when idx &lt; 4.
    /// </summary>
    private static void AppendMorphologicalFeatures(List<float> features, IReadOnlyList<Candle> candles, int idx)
    {
        if (idx < 4)
        {
            features.Add(0f);
            features.Add(0f);
            features.Add(0f);
            features.Add(0f);
            return;
        }

        // Erosion(5): min close in [idx-4 .. idx]
        double erosion = double.MaxValue;
        for (int i = idx - 4; i <= idx; i++)
        {
            double c = (double)candles[i].Close;
            if (c < erosion) erosion = c;
        }

        // Dilation(5): max close in [idx-4 .. idx]
        double dilation = double.MinValue;
        for (int i = idx - 4; i <= idx; i++)
        {
            double c = (double)candles[i].Close;
            if (c > dilation) dilation = c;
        }

        double closeIdx = (double)candles[idx].Close;

        // Morphological Gradient: (dilation - erosion) / (close + ε) — measure of local volatility
        double gradient = (dilation - erosion) / (closeIdx + 1e-8);

        // Morphological Opening Residual: how far close is above the erosion floor, normalised
        double openingResidual = (closeIdx - erosion) / (closeIdx + 1e-8);

        features.Add(Clamp((float)erosion,          -5f, 5f));
        features.Add(Clamp((float)dilation,         -5f, 5f));
        features.Add(Clamp((float)gradient,         -5f, 5f));
        features.Add(Clamp((float)openingResidual,  -5f, 5f));
    }

    /// <summary>Rec #253: CDA augmentation — computes linear interpolation mixing ratios between adjacent samples.</summary>
    public static void AppendCdaAugmentationFeatures(List<float> features, List<Candle> candles, int idx)
    {
        if (idx < 1 || idx >= candles.Count) { features.AddRange(new float[2]); return; }
        double prev = (double)candles[idx - 1].Close;
        double curr = (double)candles[idx].Close;
        double range = Math.Abs(curr - prev);
        double mixRatio = range > 0 ? (curr - prev) / range : 0;
        features.Add(Clamp((float)mixRatio, -1f, 1f));
        features.Add(Clamp((float)(range / (Math.Abs((double)candles[idx].Close) + 1e-8)), -5f, 5f));
    }

    /// <summary>Rec #263: Feature grouping — computes correlation between feature sub-groups (momentum vs volatility).</summary>
    public static void AppendFeatureGroupingFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int window = Math.Min(10, idx + 1);
        if (window < 3) { features.AddRange(new float[2]); return; }
        var slice = candles.Skip(idx + 1 - window).Take(window).ToList();
        double[] closes = slice.Select(c => (double)c.Close).ToArray();
        double[] highs  = slice.Select(c => (double)c.High).ToArray();
        double[] lows   = slice.Select(c => (double)c.Low).ToArray();
        // momentum group: returns
        double[] returns = closes.Skip(1).Zip(closes, (curr, prev) => prev > 0 ? (curr - prev) / prev : 0).ToArray();
        // volatility group: high-low ranges
        double[] ranges = highs.Zip(lows, (h, l) => h - l).ToArray();
        double meanR = returns.Average(); double meanV = ranges.Average();
        double corrNum = returns.Zip(ranges, (r, v) => (r - meanR) * (v - meanV)).Sum();
        double corrDenR = Math.Sqrt(returns.Sum(r => (r - meanR) * (r - meanR)) + 1e-10);
        double corrDenV = Math.Sqrt(ranges.Sum(v => (v - meanV) * (v - meanV)) + 1e-10);
        double corr = corrNum / (corrDenR * corrDenV);
        features.Add(Clamp((float)corr, -1f, 1f));
        features.Add(Clamp((float)(meanR / (meanV + 1e-8)), -5f, 5f));
    }

    /// <summary>Rec #276: Hilbert-Huang instantaneous frequency features (3 features).</summary>
    public static void AppendHilbertHuangFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int window = Math.Min(16, idx + 1);
        if (window < 8) { features.AddRange(new float[3]); return; }
        var slice = candles.Skip(idx + 1 - window).Take(window).ToList();
        double[] x = slice.Select(c => (double)c.Close).ToArray();
        // Compute Hilbert transform via 90-degree phase-shifted version (analytic signal approximation)
        double[] h = new double[window];
        for (int i = 1; i < window - 1; i++)
            h[i] = (x[i + 1] - x[i - 1]) / 2.0; // finite difference approximation of derivative
        double[] envelope = new double[window];
        for (int i = 0; i < window; i++)
            envelope[i] = Math.Sqrt(x[i] * x[i] + h[i] * h[i]);
        double[] instFreq = new double[window - 1];
        for (int i = 0; i < window - 1; i++)
            instFreq[i] = Math.Abs(envelope[i + 1] - envelope[i]) / (Math.Abs(envelope[i]) + 1e-8);
        double meanEnv  = envelope.Average();
        double meanFreq = instFreq.Average();
        double freqStd  = Math.Sqrt(instFreq.Select(f => (f - meanFreq) * (f - meanFreq)).Average());
        features.Add(Clamp((float)(meanEnv / ((double)candles[idx].Close + 1e-8)), -5f, 5f));
        features.Add(Clamp((float)(meanFreq * 100), -5f, 5f));
        features.Add(Clamp((float)(freqStd * 100), -5f, 5f));
    }

    /// <summary>Rec #277: STFT short-time Fourier features — mean spectral energy in 3 frequency bands (3 features).</summary>
    public static void AppendStftFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int hop = 2, win = 10;
        int numFrames = 3;
        if (idx < win) { features.AddRange(new float[3]); return; }
        double lowBand = 0, midBand = 0, highBand = 0;
        int frameCount = 0;
        for (int frame = 0; frame < numFrames && idx - frame * hop - win >= 0; frame++)
        {
            int start = idx - frame * hop - win + 1;
            double[] seg = candles.Skip(start).Take(win).Select(c => (double)c.Close).ToArray();
            double mean = seg.Average();
            double[] returns = seg.Select(v => v - mean).ToArray();
            // Compute 3-bin DFT manually
            double re1 = 0, im1 = 0, re2 = 0, im2 = 0, re3 = 0, im3 = 0;
            for (int t = 0; t < win; t++)
            {
                double angle1 = 2 * Math.PI * 1 * t / win;
                double angle2 = 2 * Math.PI * 2 * t / win;
                double angle3 = 2 * Math.PI * 3 * t / win;
                re1 += returns[t] * Math.Cos(angle1); im1 -= returns[t] * Math.Sin(angle1);
                re2 += returns[t] * Math.Cos(angle2); im2 -= returns[t] * Math.Sin(angle2);
                re3 += returns[t] * Math.Cos(angle3); im3 -= returns[t] * Math.Sin(angle3);
            }
            lowBand  += re1 * re1 + im1 * im1;
            midBand  += re2 * re2 + im2 * im2;
            highBand += re3 * re3 + im3 * im3;
            frameCount++;
        }
        double norm = frameCount > 0 ? frameCount : 1;
        features.Add(Clamp((float)(Math.Sqrt(lowBand / norm) / 100), -5f, 5f));
        features.Add(Clamp((float)(Math.Sqrt(midBand / norm) / 100), -5f, 5f));
        features.Add(Clamp((float)(Math.Sqrt(highBand / norm) / 100), -5f, 5f));
    }

    /// <summary>Rec #275: N-BEATS basis expansion features — trend slope + seasonal amplitude + residual variance (3 features).</summary>
    public static void AppendNBeatsFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int window = Math.Min(20, idx + 1);
        if (window < 5) { features.AddRange(new float[3]); return; }
        var slice = candles.Skip(idx + 1 - window).Take(window).ToList();
        double[] y = slice.Select(c => (double)c.Close).ToArray();
        double[] t = Enumerable.Range(0, window).Select(i => (double)i / (window - 1)).ToArray();
        // Fit polynomial basis (degree 2) for trend
        double sumT = t.Sum(), sumT2 = t.Sum(v => v * v), sumTY = t.Zip(y, (ti, yi) => ti * yi).Sum();
        double sumY = y.Sum(), n = window;
        double denom = n * sumT2 - sumT * sumT;
        double slope = denom > 1e-10 ? (n * sumTY - sumT * sumY) / denom : 0;
        double[] trend = t.Select(ti => (sumY - slope * sumT) / n + slope * ti).ToArray();
        // Seasonal: Fourier K=2 amplitude
        double re1 = 0, im1 = 0;
        for (int i = 0; i < window; i++)
        {
            double residual = y[i] - trend[i];
            re1 += residual * Math.Cos(2 * Math.PI * i / window);
            im1 -= residual * Math.Sin(2 * Math.PI * i / window);
        }
        double seasonalAmp = Math.Sqrt(re1 * re1 + im1 * im1) / window;
        // Residual after trend + seasonal
        double residualVar = y.Select((yi, i) => yi - trend[i]).Select(r => r * r).Average();
        features.Add(Clamp((float)(slope * 100), -5f, 5f));
        features.Add(Clamp((float)(seasonalAmp / ((double)candles[idx].Close + 1e-8)), -5f, 5f));
        features.Add(Clamp((float)(Math.Sqrt(residualVar) / ((double)candles[idx].Close + 1e-8)), -5f, 5f));
    }

    /// <summary>Rec #305-307: OHLC volatility estimators — Parkinson, Garman-Klass, Yang-Zhang (3 features).</summary>
    public static void AppendOhlcVolatilityFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int window = Math.Min(20, idx + 1);
        if (window < 2) { features.AddRange(new float[3]); return; }
        var slice = candles.Skip(idx + 1 - window).Take(window).ToList();
        // Parkinson: (1/4ln2) * mean[(ln H/L)^2]
        double parkSum = 0;
        // Garman-Klass: 0.5*(ln H/L)^2 - (2ln2-1)*(ln C/O)^2
        double gkSum = 0;
        // Yang-Zhang: uses overnight return + open-to-close
        double yzCC = 0, yzOC = 0;
        double meanRet = 0;
        for (int i = 0; i < window; i++)
        {
            double h = (double)slice[i].High, l = (double)slice[i].Low;
            double c = (double)slice[i].Close, o = (double)slice[i].Open;
            double lnHL = h > l ? Math.Log(h / l) : 0;
            double lnCO = o > 0 ? Math.Log(c / o) : 0;
            parkSum += lnHL * lnHL;
            gkSum   += 0.5 * lnHL * lnHL - (2 * Math.Log(2) - 1) * lnCO * lnCO;
            yzCC    += lnCO * lnCO;
            yzOC    += lnHL * lnHL;
            if (i > 0) meanRet += o > 0 ? Math.Log(c / (double)slice[i - 1].Close) : 0;
        }
        double n = window;
        double park  = Math.Sqrt(Math.Max(0, parkSum / (4 * Math.Log(2) * n)));
        double gk    = Math.Sqrt(Math.Max(0, gkSum / n));
        double yz    = Math.Sqrt(Math.Max(0, 0.34 * yzCC / n + 0.66 * yzOC / n));
        double close = (double)candles[idx].Close;
        features.Add(Clamp((float)(park / (close + 1e-8) * 100), 0f, 5f));
        features.Add(Clamp((float)(gk   / (close + 1e-8) * 100), 0f, 5f));
        features.Add(Clamp((float)(yz   / (close + 1e-8) * 100), 0f, 5f));
    }

    /// <summary>Rec #308: DMD spectral features — top-3 DMD eigenvalue magnitudes (3 features).</summary>
    public static void AppendDmdFeatures(List<float> features, List<Candle> candles, int idx)
    {
        int window = Math.Min(12, idx + 1);
        if (window < 6) { features.AddRange(new float[3]); return; }
        var slice = candles.Skip(idx + 1 - window).Take(window).ToList();
        double[] x = slice.Select(c => (double)c.Close).ToArray();
        // Build data matrices X (columns 0..n-2) and X' (columns 1..n-1)
        int n = window - 1;
        // SVD via power iteration for dominant mode
        double[] u = new double[n], v = new double[n];
        for (int i = 0; i < n; i++) u[i] = x[i + 1] - x[i]; // difference approximation
        double norm = Math.Sqrt(u.Sum(ui => ui * ui) + 1e-10);
        // Eigenvalue approx: ratio of consecutive differences
        double[] eigenMags = new double[3];
        for (int k = 0; k < Math.Min(3, n - 1); k++)
        {
            double dot = 0, denom = 0;
            for (int i = 0; i < n - 1; i++) { dot += x[i + k + 1] * x[i]; denom += x[i] * x[i]; }
            eigenMags[k] = denom > 1e-10 ? Math.Abs(dot / denom) : 0;
        }
        double maxEig = eigenMags.Max() + 1e-8;
        features.Add(Clamp((float)(eigenMags[0] / maxEig), 0f, 1f));
        features.Add(Clamp((float)(eigenMags[1] / maxEig), 0f, 1f));
        features.Add(Clamp((float)(eigenMags[2] / maxEig), 0f, 1f));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Cross-pair feature injection
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Well-known cross-pair correlation groups for major FX pairs.
    /// Maps a primary symbol to its correlated pairs (positive or inverse).
    /// Used by <see cref="AppendCrossPairFeatures"/> to inject macro-flow signals.
    /// </summary>
    public static readonly Dictionary<string, string[]> CrossPairMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EURUSD"] = ["GBPUSD", "USDCHF", "EURGBP"],
        ["GBPUSD"] = ["EURUSD", "EURGBP", "GBPJPY"],
        ["USDJPY"] = ["EURJPY", "GBPJPY", "USDCHF"],
        ["USDCHF"] = ["EURUSD", "USDJPY", "EURCHF"],
        ["AUDUSD"] = ["NZDUSD", "USDCAD", "AUDJPY"],
        ["NZDUSD"] = ["AUDUSD", "NZDJPY", "USDCAD"],
        ["USDCAD"] = ["AUDUSD", "NZDUSD", "CADJPY"],
        ["EURJPY"] = ["USDJPY", "EURUSD", "GBPJPY"],
        ["GBPJPY"] = ["USDJPY", "GBPUSD", "EURJPY"],
        ["EURGBP"] = ["EURUSD", "GBPUSD", "EURCHF"],
        ["EURCHF"] = ["USDCHF", "EURUSD", "EURGBP"],
        ["AUDJPY"] = ["USDJPY", "AUDUSD", "NZDJPY"],
    };

    /// <summary>
    /// Names of the cross-pair features appended by <see cref="AppendCrossPairFeatures"/>.
    /// 4 features per correlated pair × up to 3 pairs = 12 features max.
    /// When fewer than 3 correlated pairs have data, remaining slots are zero-filled.
    /// </summary>
    public static readonly string[] CrossPairFeatureNames =
    [
        "XP1_Return",  "XP1_RsiDelta",  "XP1_AtrRatio",  "XP1_Correlation",
        "XP2_Return",  "XP2_RsiDelta",  "XP2_AtrRatio",  "XP2_Correlation",
        "XP3_Return",  "XP3_RsiDelta",  "XP3_AtrRatio",  "XP3_Correlation",
    ];

    /// <summary>
    /// Number of cross-pair features appended per sample.
    /// </summary>
    public const int CrossPairFeatureCount = 12; // 4 per correlated pair × 3 pairs

    /// <summary>
    /// Appends cross-pair features from correlated instruments to an existing feature vector.
    /// <para>
    /// For each correlated pair (up to 3), computes:
    /// <list type="bullet">
    ///   <item><b>XP_Return:</b> normalised return of the correlated pair's latest candle
    ///         (Close − PrevClose) / ATR, clamped to [−3, 3].</item>
    ///   <item><b>XP_RsiDelta:</b> difference between the primary pair's RSI and the
    ///         correlated pair's RSI, normalised to [−1, 1] by dividing by 100.</item>
    ///   <item><b>XP_AtrRatio:</b> ratio of correlated pair ATR to primary pair ATR,
    ///         log-scaled and clamped to [−2, 2].</item>
    ///   <item><b>XP_Correlation:</b> rolling 20-bar Pearson correlation between the
    ///         primary and correlated pair's close-to-close returns.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="existing">The current feature vector to extend.</param>
    /// <param name="primarySymbol">The symbol being traded (e.g. "EURUSD").</param>
    /// <param name="primaryCandles">Recent candles for the primary symbol (at least 21).</param>
    /// <param name="crossPairCandles">
    /// Lookup of recent candles for correlated pairs. Key = symbol, Value = candle list.
    /// Pairs not present in this dictionary are zero-filled.
    /// </param>
    /// <returns>A new feature array with 12 additional cross-pair features appended.</returns>
    public static float[] AppendCrossPairFeatures(
        float[]                             existing,
        string                              primarySymbol,
        IReadOnlyList<Candle>               primaryCandles,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> crossPairCandles)
    {
        var result = new float[existing.Length + CrossPairFeatureCount];
        Array.Copy(existing, result, existing.Length);
        int offset = existing.Length;

        if (!CrossPairMap.TryGetValue(primarySymbol, out var correlatedSymbols))
        {
            // Unknown pair — return zero-filled cross-pair slots
            return result;
        }

        // Primary pair's latest return and RSI for delta computation
        double primaryReturn = 0, primaryRsi = 0, primaryAtr = 1e-8;
        if (primaryCandles.Count >= 2)
        {
            var curr = primaryCandles[^1];
            var prev = primaryCandles[^2];
            primaryAtr    = ComputeSimpleAtr(primaryCandles, Math.Min(14, primaryCandles.Count - 1));
            primaryReturn = primaryAtr > 1e-8 ? (double)(curr.Close - prev.Close) / primaryAtr : 0;
            primaryRsi    = ComputeSimpleRsi(primaryCandles, 14);
        }

        // Primary close returns for correlation computation
        double[]? primaryReturns = null;
        if (primaryCandles.Count >= 21)
        {
            primaryReturns = new double[20];
            for (int i = 0; i < 20; i++)
            {
                var c = primaryCandles[primaryCandles.Count - 20 + i];
                var p = primaryCandles[primaryCandles.Count - 21 + i];
                primaryReturns[i] = p.Close != 0 ? (double)(c.Close - p.Close) / (double)p.Close : 0;
            }
        }

        for (int pairIdx = 0; pairIdx < 3; pairIdx++)
        {
            int slotOffset = offset + pairIdx * 4;

            if (pairIdx >= correlatedSymbols.Length)
                continue; // zero-filled

            string crossSymbol = correlatedSymbols[pairIdx];
            if (!crossPairCandles.TryGetValue(crossSymbol, out var crossCandles) || crossCandles.Count < 2)
                continue; // zero-filled

            var crossCurr = crossCandles[^1];
            var crossPrev = crossCandles[^2];

            // XP_Return: normalised return
            double crossAtr    = ComputeSimpleAtr(crossCandles, Math.Min(14, crossCandles.Count - 1));
            double crossReturn = crossAtr > 1e-8 ? (double)(crossCurr.Close - crossPrev.Close) / crossAtr : 0;
            result[slotOffset + 0] = Clamp((float)crossReturn, -3f, 3f);

            // XP_RsiDelta: RSI difference / 100
            double crossRsi = ComputeSimpleRsi(crossCandles, 14);
            result[slotOffset + 1] = Clamp((float)((primaryRsi - crossRsi) / 100.0), -1f, 1f);

            // XP_AtrRatio: log(crossATR / primaryATR)
            double atrRatio = primaryAtr > 1e-8 ? crossAtr / primaryAtr : 1.0;
            result[slotOffset + 2] = Clamp((float)Math.Log(Math.Max(atrRatio, 1e-4)), -2f, 2f);

            // XP_Correlation: 20-bar Pearson correlation of returns
            if (primaryReturns is not null && crossCandles.Count >= 21)
            {
                var crossReturns = new double[20];
                for (int i = 0; i < 20; i++)
                {
                    var c = crossCandles[crossCandles.Count - 20 + i];
                    var p = crossCandles[crossCandles.Count - 21 + i];
                    crossReturns[i] = p.Close != 0 ? (double)(c.Close - p.Close) / (double)p.Close : 0;
                }
                result[slotOffset + 3] = Clamp((float)PearsonCorrelation(primaryReturns, crossReturns), -1f, 1f);
            }
        }

        return result;
    }

    /// <summary>
    /// Appends a news proximity feature: decays from 1.0 at event time to 0.0 at 24h away.
    /// Returns a new array with length <paramref name="existing"/>.Length + 1.
    /// </summary>
    public static float[] AppendNewsProximityFeature(float[] existing, double minutesUntilNextHighImpact)
    {
        var extended = new float[existing.Length + 1];
        existing.CopyTo(extended, 0);
        // Decay: 1.0 at event, 0.0 at 24h (1440 min), clamped [0, 1]
        extended[existing.Length] = (float)Math.Clamp(1.0 - minutesUntilNextHighImpact / 1440.0, 0.0, 1.0);
        return extended;
    }

    /// <summary>
    /// Appends a sentiment alignment feature: positive when sentiment agrees with trade direction.
    /// Returns a new array with length <paramref name="existing"/>.Length + 1.
    /// </summary>
    public static float[] AppendSentimentAlignmentFeature(float[] existing,
        decimal baseSentiment, decimal quoteSentiment)
    {
        var extended = new float[existing.Length + 1];
        existing.CopyTo(extended, 0);
        // Net sentiment: positive base + negative quote = bullish for the pair
        float alignment = (float)(baseSentiment - quoteSentiment);
        extended[existing.Length] = Math.Clamp(alignment, -1f, 1f);
        return extended;
    }

    /// <summary>
    /// Combines base features with all extended features into a single float[57] vector.
    /// Handles the case where cross-pair features may not be available (zeros them).
    /// </summary>
    public static float[] BuildExtendedFeatureVector(
        float[] baseFeatures,
        float[]? crossPairFeatures,
        double minutesUntilNextHighImpact,
        decimal baseSentiment, decimal quoteSentiment,
        TickFlowSnapshot? tickFlow = null,
        decimal priceReturn = 0m,
        float economicSurprise = 0f,
        ProxyFeatureData? proxyData = null)
    {
        var extended = new float[ExtendedFeatureCount];
        Array.Copy(baseFeatures, extended, Math.Min(baseFeatures.Length, FeatureCount));

        // Cross-pair features (12 elements at indices 33-44)
        if (crossPairFeatures != null && crossPairFeatures.Length > 0)
            Array.Copy(crossPairFeatures, 0, extended, FeatureCount, Math.Min(12, crossPairFeatures.Length));

        // News proximity (position 45)
        extended[FeatureCount + 12] = (float)Math.Clamp(1.0 - minutesUntilNextHighImpact / 1440.0, 0.0, 1.0);

        // Sentiment alignment (position 46)
        extended[FeatureCount + 13] = Math.Clamp((float)(baseSentiment - quoteSentiment), -1f, 1f);

        // Tick flow features (positions 47-49)
        if (tickFlow != null)
        {
            // TickDelta: net buying/selling pressure from tick sequence
            extended[FeatureCount + 14] = Math.Clamp((float)tickFlow.TickDelta, -1f, 1f);

            // TickDeltaDivergence: price vs delta direction mismatch
            float deltaSign = Math.Sign((float)tickFlow.TickDelta);
            float priceSign = Math.Sign((float)priceReturn);
            extended[FeatureCount + 15] = (priceSign != 0 && deltaSign != 0 && priceSign != deltaSign)
                ? -priceSign  // divergence: opposite of price direction
                : 0f;

            // SpreadZScore: current spread vs historical distribution
            if (tickFlow.SpreadStdDev > 0)
                extended[FeatureCount + 16] = Math.Clamp(
                    (float)((tickFlow.CurrentSpread - tickFlow.SpreadMean) / tickFlow.SpreadStdDev), -3f, 3f);
        }

        // Economic surprise (position 50)
        extended[FeatureCount + 17] = Math.Clamp(economicSurprise, -1f, 1f);

        // Proxy features (positions 51-56)
        if (proxyData != null)
        {
            extended[FeatureCount + 18] = Math.Clamp(proxyData.AtrAcceleration, -1f, 1f);
            extended[FeatureCount + 19] = Math.Clamp(proxyData.BbwRateOfChange, -1f, 1f);
            extended[FeatureCount + 20] = Math.Clamp(proxyData.VolPercentile, 0f, 1f);
            extended[FeatureCount + 21] = Math.Clamp(proxyData.TickIntensity, 0f, 3f);
            extended[FeatureCount + 22] = Math.Clamp(proxyData.BidAskImbalance, -1f, 1f);
            extended[FeatureCount + 23] = Math.Clamp(proxyData.CalendarDensity, 0f, 1f);
        }

        return extended;
    }

    /// <summary>
    /// Parses economic data strings like "200K", "3.5%", "1.2M" to decimal values.
    /// Returns null if unparseable.
    /// </summary>
    public static decimal? ParseEconomicValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        decimal multiplier = 1m;
        bool isPercent = false;

        if (raw.EndsWith('%')) { isPercent = true; raw = raw[..^1].Trim(); }
        else if (raw.EndsWith("T", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000_000_000m; raw = raw[..^1].Trim(); }
        else if (raw.EndsWith("B", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000_000m; raw = raw[..^1].Trim(); }
        else if (raw.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000m; raw = raw[..^1].Trim(); }
        else if (raw.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000m; raw = raw[..^1].Trim(); }

        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return isPercent ? value / 100m : value * multiplier;
        }
        return null;
    }

    /// <summary>
    /// Computes economic surprise: (Actual - Forecast) / |Previous|, clamped [-1, 1].
    /// Returns 0 if any value is unparseable.
    /// </summary>
    public static float ComputeEconomicSurprise(string? actual, string? forecast, string? previous)
    {
        var a = ParseEconomicValue(actual);
        var f = ParseEconomicValue(forecast);
        var p = ParseEconomicValue(previous);
        if (a == null || f == null || p == null || p == 0) return 0f;
        return Math.Clamp((float)((a.Value - f.Value) / Math.Abs(p.Value)), -1f, 1f);
    }

    /// <summary>
    /// Computes proxy features from candle window, tick data, and economic calendar.
    /// All features degrade gracefully to 0 when data is insufficient.
    /// </summary>
    public static ProxyFeatureData ComputeProxyFeatures(
        IReadOnlyList<Candle> candles, int lastIdx,
        TickFlowSnapshot? tickFlow,
        IReadOnlyList<(decimal Bid, decimal Ask, DateTime Timestamp)>? recentTicks,
        int upcomingEventCount)
    {
        // 1. ATR Acceleration: ATR(5) / ATR(20) - 1.0
        float atrAccel = 0f;
        if (lastIdx >= 20)
        {
            decimal atr5 = IndicatorCalculator.WilderAtr(candles, lastIdx, 5);
            decimal atr20 = IndicatorCalculator.WilderAtr(candles, lastIdx, 20);
            if (atr20 > 0) atrAccel = (float)((atr5 / atr20) - 1.0m);
        }

        // 2. BBW Rate of Change: (BBW_now - BBW_5ago) / BBW_5ago
        float bbwRoc = 0f;
        if (lastIdx >= 25)
        {
            decimal sma20 = IndicatorCalculator.Sma(candles, lastIdx, 20);
            decimal std20 = IndicatorCalculator.StdDev(candles, lastIdx, 20, sma20);
            decimal bbwNow = sma20 > 0 ? 2m * std20 / sma20 : 0m;

            decimal sma20_5 = IndicatorCalculator.Sma(candles, lastIdx - 5, 20);
            decimal std20_5 = IndicatorCalculator.StdDev(candles, lastIdx - 5, 20, sma20_5);
            decimal bbw5Ago = sma20_5 > 0 ? 2m * std20_5 / sma20_5 : 0m;

            if (bbw5Ago > 0) bbwRoc = (float)((bbwNow - bbw5Ago) / bbw5Ago);
        }

        // 3. Volatility Percentile: where is current 20-bar vol in the 90-bar distribution?
        float volPctl = 0.5f;
        if (lastIdx >= 90)
        {
            // Current 20-bar realized vol (RMS of returns)
            var returns20 = new List<double>(20);
            for (int i = lastIdx - 19; i <= lastIdx; i++)
                if (candles[i - 1].Close > 0)
                    returns20.Add((double)(candles[i].Close - candles[i - 1].Close) / (double)candles[i - 1].Close);
            double currentVol = returns20.Count > 1
                ? Math.Sqrt(returns20.Select(r => r * r).Average())
                : 0;

            // Count how many 20-bar windows in the last 90 bars had lower vol
            int lowerCount = 0, totalWindows = 0;
            for (int start = lastIdx - 89; start <= lastIdx - 19; start++)
            {
                var windowReturns = new List<double>(20);
                for (int j = start; j < start + 20 && j <= lastIdx; j++)
                    if (candles[j - 1].Close > 0)
                        windowReturns.Add((double)(candles[j].Close - candles[j - 1].Close) / (double)candles[j - 1].Close);
                if (windowReturns.Count > 1)
                {
                    double windowVol = Math.Sqrt(windowReturns.Select(r => r * r).Average());
                    if (windowVol < currentVol) lowerCount++;
                    totalWindows++;
                }
            }
            if (totalWindows > 0) volPctl = (float)lowerCount / totalWindows;
        }

        // 4. Tick Intensity: recent tick rate vs older tick rate (split-window comparison)
        float tickIntensity = 1f;
        if (recentTicks is { Count: >= 10 })
        {
            // Split ticks into recent half and older half to create independent baseline
            int half = recentTicks.Count / 2;
            var recentSpan = (recentTicks[0].Timestamp - recentTicks[half].Timestamp).TotalMinutes;
            var olderSpan = (recentTicks[half].Timestamp - recentTicks[^1].Timestamp).TotalMinutes;

            if (recentSpan > 0 && olderSpan > 0)
            {
                double recentRate = half / recentSpan;
                double olderRate = (recentTicks.Count - half) / olderSpan;
                if (olderRate > 0)
                    tickIntensity = (float)(recentRate / olderRate);
            }
        }

        // 5. Bid-Ask Imbalance: are asks moving faster than bids?
        float bidAskImbalance = 0f;
        if (recentTicks is { Count: >= 5 })
        {
            decimal totalAskMove = 0m, totalBidMove = 0m;
            for (int i = 1; i < recentTicks.Count; i++)
            {
                totalAskMove += Math.Abs(recentTicks[i - 1].Ask - recentTicks[i].Ask);
                totalBidMove += Math.Abs(recentTicks[i - 1].Bid - recentTicks[i].Bid);
            }
            decimal totalMove = totalAskMove + totalBidMove;
            if (totalMove > 0) bidAskImbalance = (float)((totalAskMove - totalBidMove) / totalMove);
        }

        // 6. Calendar Density: upcoming high/medium events in 24h, divided by 5
        float calendarDensity = Math.Min(upcomingEventCount / 5f, 1f);

        return new ProxyFeatureData(atrAccel, bbwRoc, volPctl, tickIntensity, bidAskImbalance, calendarDensity);
    }

    /// <summary>Simple ATR over the last N candles.</summary>
    private static double ComputeSimpleAtr(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < 2 || period < 1) return 1e-8;
        double sum = 0;
        int start = Math.Max(1, candles.Count - period);
        int count = 0;
        for (int i = start; i < candles.Count; i++)
        {
            double tr = Math.Max(
                (double)(candles[i].High - candles[i].Low),
                Math.Max(
                    Math.Abs((double)(candles[i].High - candles[i - 1].Close)),
                    Math.Abs((double)(candles[i].Low  - candles[i - 1].Close))));
            sum += tr;
            count++;
        }
        return count > 0 ? sum / count : 1e-8;
    }

    /// <summary>Simple RSI over the last 14 candles.</summary>
    private static double ComputeSimpleRsi(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 50.0;
        double gainSum = 0, lossSum = 0;
        int start = candles.Count - period;
        for (int i = start; i < candles.Count; i++)
        {
            double delta = (double)(candles[i].Close - candles[i - 1].Close);
            if (delta > 0) gainSum += delta;
            else           lossSum -= delta;
        }
        double avgGain = gainSum / period;
        double avgLoss = lossSum / period;
        if (avgLoss < 1e-10) return 100.0;
        double rs = avgGain / avgLoss;
        return 100.0 - 100.0 / (1.0 + rs);
    }

    /// <summary>Pearson correlation between two equal-length arrays.</summary>
    private static double PearsonCorrelation(double[] x, double[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        if (n < 3) return 0;
        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            cov += dx * dy;
            vx  += dx * dx;
            vy  += dy * dy;
        }
        double denom = Math.Sqrt(vx * vy);
        return denom > 1e-12 ? cov / denom : 0;
    }

    // ── Fractional differencing on TrainingSample sequences ───────────────────

    /// <summary>
    /// Applies fractional differencing of order <paramref name="d"/> to every feature column
    /// in <paramref name="samples"/>, achieving stationarity while preserving long-memory
    /// autocorrelation (Lopez de Prado, 2018).
    ///
    /// The convolution weights are: w_0 = 1, w_k = −w_{k−1} × (d − k + 1) / k.
    /// Weights below <paramref name="threshold"/> are truncated (finite-memory window).
    /// Samples with insufficient history (index &lt; window length) have their features zero-filled.
    /// </summary>
    /// <param name="samples">Chronologically ordered training samples.</param>
    /// <param name="F">Feature count per sample.</param>
    /// <param name="d">Differencing order. 0 = identity, 1 = full first difference. Typical range 0.2–0.6.</param>
    /// <param name="threshold">Minimum absolute weight to retain (default 1e-5).</param>
    /// <returns>New list with each feature column replaced by its fractionally differenced values.</returns>
    public static List<TrainingSample> ApplyFractionalDifferencing(
        List<TrainingSample> samples, int F, double d, double threshold = 1e-5)
    {
        if (d <= 0.0 || samples.Count == 0) return samples;

        // Build convolution weights until they decay below threshold
        var weights = new List<double> { 1.0 };
        for (int k = 1; k < samples.Count; k++)
        {
            double w = -weights[k - 1] * (d - k + 1) / k;
            if (Math.Abs(w) < threshold) break;
            weights.Add(w);
        }

        int winLen = weights.Count;
        var result = new List<TrainingSample>(samples.Count);

        for (int t = 0; t < samples.Count; t++)
        {
            if (t < winLen - 1)
            {
                // Insufficient history for full convolution window — zero-fill
                result.Add(samples[t] with { Features = new float[F] });
            }
            else
            {
                var newFeatures = new float[F];
                for (int j = 0; j < F; j++)
                {
                    double val = 0;
                    for (int k = 0; k < winLen; k++)
                        val += weights[k] * (j < samples[t - k].Features.Length ? samples[t - k].Features[j] : 0f);
                    newFeatures[j] = float.IsFinite((float)val) ? (float)val : 0f;
                }
                result.Add(samples[t] with { Features = newFeatures });
            }
        }

        return result;
    }
}
