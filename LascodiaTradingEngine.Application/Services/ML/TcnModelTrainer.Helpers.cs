using System.Buffers;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ── Loss spike detection (item 29) ──────────────────────────────────────

    /// <summary>
    /// Detects if epoch loss spiked above 3× the running EMA average.
    /// Returns the decay factor to apply to LR (0.5 on spike, 1.0 otherwise).
    /// </summary>
    internal static (double LrDecay, bool Spiked) DetectLossSpike(
        double epochLoss, ref double lossEma, double emaAlpha = 0.1)
    {
        if (lossEma <= 0) { lossEma = epochLoss; return (1.0, false); }
        bool spiked = epochLoss > 3.0 * lossEma;
        lossEma = emaAlpha * epochLoss + (1.0 - emaAlpha) * lossEma;
        return (spiked ? 0.5 : 1.0, spiked);
    }

    // ── Kahan-compensated gradient accumulation (item 30) ───────────────────

    /// <summary>Accumulates gradient with Kahan compensation to prevent floating-point drift.</summary>
    internal static void KahanAccumulate(double[] accumulator, double[] compensation, double[] gradient, int length)
    {
        for (int i = 0; i < length; i++)
        {
            double y = gradient[i] - compensation[i];
            double t = accumulator[i] + y;
            compensation[i] = (t - accumulator[i]) - y;
            accumulator[i] = t;
        }
    }

    /// <summary>Scalar Kahan accumulation for single values (biases).</summary>
    internal static double KahanAdd(double sum, double value, ref double compensation)
    {
        double y = value - compensation;
        double t = sum + y;
        compensation = (t - sum) - y;
        return t;
    }

    // ── Mixed-precision forward pass (item 31) ──────────────────────────────

    /// <summary>
    /// Runs the causal dilated conv inner loop in single precision for speed,
    /// accumulating in double. Returns the sum as double.
    /// Weight layout: <c>(outputChannel * inC + c) * kernelSize + k</c>.
    /// </summary>
    internal static double MixedPrecisionConvDot(
        double[] convW, int outputChannel, int inC, int kernelSize,
        double[][] blockInput, int t, int dilation)
    {
        float sum = 0f;
        for (int k = 0; k < kernelSize; k++)
        {
            int srcT = t - k * dilation;
            if (srcT < 0) continue;
            for (int c = 0; c < inC; c++)
                sum += (float)convW[(outputChannel * inC + c) * kernelSize + k] * (float)blockInput[srcT][c];
        }
        return sum;
    }

    // ── Span-based conv helpers (item 34) ───────────────────────────────────

    /// <summary>
    /// Computes conv output for a single (output, timestep) using Span to enable bounds-check elimination.
    /// </summary>
    internal static double SpanConvDot(
        ReadOnlySpan<double> convW, ReadOnlySpan<double> input, int inC, int kernelSize)
    {
        double sum = 0;
        for (int i = 0; i < convW.Length && i < input.Length; i++)
            sum += convW[i] * input[i];
        return sum;
    }

    // ── ArrayPool SWA state helpers (item 35) ───────────────────────────────

    /// <summary>Rents SWA accumulator arrays from ArrayPool. Returns an array of rented arrays that must be returned.</summary>
    internal static double[][] RentSwaState(ArrayPool<double> pool, int count, int size)
    {
        var result = new double[count][];
        for (int i = 0; i < count; i++)
        {
            result[i] = pool.Rent(size);
            Array.Clear(result[i], 0, size);
        }
        return result;
    }

    /// <summary>Returns all rented SWA arrays back to the pool.</summary>
    internal static void ReturnSwaState(ArrayPool<double> pool, double[][] arrays)
    {
        for (int i = 0; i < arrays.Length; i++)
            if (arrays[i] != null) pool.Return(arrays[i]);
    }

    // ── In-place feature masking (item 36) ──────────────────────────────────

    /// <summary>
    /// Applies feature mask in-place on a working copy, avoiding per-element Clone().
    /// Returns the modified samples list (samples are reused from a shared buffer).
    /// </summary>
    internal static List<TrainingSample> ApplySequenceMaskInPlace(
        List<TrainingSample> samples, bool[] mask,
        float[][] flatBuffer, float[][][] seqBuffer)
    {
        var result = new List<TrainingSample>(samples.Count);
        for (int si = 0; si < samples.Count; si++)
        {
            var s = samples[si];

            // Reuse flat buffer
            var f = flatBuffer[si];
            Array.Copy(s.Features, f, s.Features.Length);
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;

            float[][]? seq = null;
            if (s.SequenceFeatures is not null)
            {
                seq = seqBuffer[si];
                for (int t = 0; t < s.SequenceFeatures.Length; t++)
                {
                    Array.Copy(s.SequenceFeatures[t], seq[t], s.SequenceFeatures[t].Length);
                    for (int c = 0; c < seq[t].Length && c < mask.Length; c++)
                        if (!mask[c]) seq[t][c] = 0f;
                }
            }

            result.Add(s with { Features = f, SequenceFeatures = seq });
        }
        return result;
    }

    /// <summary>Pre-allocates reusable buffers for <see cref="ApplySequenceMaskInPlace"/>.</summary>
    internal static (float[][] FlatBuf, float[][][] SeqBuf) AllocMaskBuffers(
        List<TrainingSample> samples)
    {
        var flatBuf = new float[samples.Count][];
        var seqBuf = new float[samples.Count][][];
        for (int i = 0; i < samples.Count; i++)
        {
            flatBuf[i] = new float[samples[i].Features.Length];
            if (samples[i].SequenceFeatures is not null)
            {
                seqBuf[i] = new float[samples[i].SequenceFeatures!.Length][];
                for (int t = 0; t < samples[i].SequenceFeatures!.Length; t++)
                    seqBuf[i][t] = new float[samples[i].SequenceFeatures![t].Length];
            }
        }
        return (flatBuf, seqBuf);
    }
}
