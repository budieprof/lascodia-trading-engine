using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Transforms a time-ordered list of <see cref="Candle"/> rows into sliding-window sequences
/// of raw OHLCV feature rows suitable for CPC pre-training. Each per-step row is six floats:
/// <c>[logRet(Open), logRet(High), logRet(Low), logRet(Close), volZ, bodyOverRange]</c>.
///
/// <para>
/// The per-step schema is intentionally independent of the engineered feature versions
/// (V1..V6) so the CPC encoder can be retrained without bumping every downstream model's
/// input contract.
/// </para>
/// </summary>
public static class MLCpcSequenceBuilder
{
    /// <summary>Number of features emitted per step.</summary>
    public const int FeaturesPerStep = 6;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Build overlapping sequences of length <paramref name="seqLen"/> with the given stride.
    /// Returns an empty list when too few candles are available or the inputs fail validation.
    /// </summary>
    /// <param name="candles">Closed candles ordered ascending by <see cref="Candle.Timestamp"/>.</param>
    /// <param name="seqLen">Length of each emitted sequence (>= 2).</param>
    /// <param name="stride">Step between window starts (>= 1).</param>
    /// <param name="maxSequences">Upper bound on emitted sequences.</param>
    public static IReadOnlyList<float[][]> Build(
        IReadOnlyList<Candle> candles,
        int seqLen,
        int stride,
        int maxSequences)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (seqLen < 2 || stride < 1 || maxSequences < 1)
            return Array.Empty<float[][]>();

        // Keep only closed, finite OHLCV rows. Leave ordering to the caller.
        var cleaned = new List<Candle>(candles.Count);
        foreach (var c in candles)
        {
            if (!c.IsClosed) continue;
            double o = (double)c.Open, h = (double)c.High, l = (double)c.Low, cl = (double)c.Close, v = (double)c.Volume;
            if (!double.IsFinite(o) || !double.IsFinite(h) || !double.IsFinite(l) || !double.IsFinite(cl) || !double.IsFinite(v))
                continue;
            if (o <= 0.0 || h <= 0.0 || l <= 0.0 || cl <= 0.0)
                continue;
            cleaned.Add(c);
        }

        // Need one extra candle as the prior-close reference for the first step's log-returns.
        if (cleaned.Count < seqLen + 1)
            return Array.Empty<float[][]>();

        var sequences = new List<float[][]>();
        // Window start index into `cleaned` (inclusive). A window spans [start .. start + seqLen - 1];
        // we consume `cleaned[start - 1]` as the prior-close reference, so start must be >= 1.
        int lastStart = cleaned.Count - seqLen;
        for (int start = 1; start <= lastStart && sequences.Count < maxSequences; start += stride)
        {
            var window = BuildWindow(cleaned, start, seqLen);
            if (window is not null)
                sequences.Add(window);
        }

        return sequences;
    }

    private static float[][]? BuildWindow(IReadOnlyList<Candle> cleaned, int start, int seqLen)
    {
        // First pass: compute volume mean & std over the window for z-score.
        double volSum = 0.0, volSumSq = 0.0;
        for (int i = 0; i < seqLen; i++)
        {
            double v = (double)cleaned[start + i].Volume;
            volSum   += v;
            volSumSq += v * v;
        }
        double volMean = volSum / seqLen;
        double variance = Math.Max(0.0, (volSumSq / seqLen) - (volMean * volMean));
        double volStd   = Math.Sqrt(variance);

        var window = new float[seqLen][];
        for (int i = 0; i < seqLen; i++)
        {
            var curr = cleaned[start + i];
            var prevClose = (double)cleaned[start + i - 1].Close;
            if (prevClose <= 0.0) return null;

            double o = (double)curr.Open;
            double h = (double)curr.High;
            double l = (double)curr.Low;
            double c = (double)curr.Close;
            double vol = (double)curr.Volume;

            double logRetO = Math.Log(o / prevClose);
            double logRetH = Math.Log(h / prevClose);
            double logRetL = Math.Log(l / prevClose);
            double logRetC = Math.Log(c / prevClose);
            double volZ    = volStd > Epsilon ? (vol - volMean) / volStd : 0.0;
            double range   = h - l;
            double bodyRatio = range > Epsilon ? (c - o) / range : 0.0;

            if (!double.IsFinite(logRetO) || !double.IsFinite(logRetH) ||
                !double.IsFinite(logRetL) || !double.IsFinite(logRetC) ||
                !double.IsFinite(volZ) || !double.IsFinite(bodyRatio))
            {
                return null;
            }

            window[i] = new float[FeaturesPerStep]
            {
                (float)logRetO,
                (float)logRetH,
                (float)logRetL,
                (float)logRetC,
                (float)volZ,
                (float)bodyRatio,
            };
        }

        return window;
    }
}
