using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ── Item 5: Mixup augmentation ─────────────────────────────────────────

    /// <summary>
    /// Applies Mixup augmentation: creates a mixed sample from two random samples.
    /// x_mix = lambda * x_i + (1 - lambda) * x_j, y_mix = lambda * y_i + (1 - lambda) * y_j where lambda ~ Beta(alpha, alpha).
    /// Returns (mixedSequence, mixedDirection as continuous label, mixedMagnitude).
    /// </summary>
    internal static (float[][] MixedSeq, double MixedLabel, float MixedMag) ApplyMixup(
        TrainingSample s1, TrainingSample s2, double alpha, Random rng)
    {
        double lambda = SampleBeta(rng, alpha, alpha);
        int T = s1.SequenceFeatures!.Length;
        int C = s1.SequenceFeatures![0].Length;
        var mixed = new float[T][];
        for (int t = 0; t < T; t++)
        {
            mixed[t] = new float[C];
            for (int c = 0; c < C; c++)
                mixed[t][c] = (float)(lambda * s1.SequenceFeatures![t][c] + (1 - lambda) * s2.SequenceFeatures![t][c]);
        }
        double y1 = s1.Direction > 0 ? 1.0 : 0.0;
        double y2 = s2.Direction > 0 ? 1.0 : 0.0;
        double mixedLabel = lambda * y1 + (1 - lambda) * y2;
        float mixedMag = (float)(lambda * s1.Magnitude + (1 - lambda) * s2.Magnitude);
        return (mixed, mixedLabel, mixedMag);
    }

    /// <summary>Samples from Beta(a, b) distribution using the Gamma ratio method.</summary>
    internal static double SampleBeta(Random rng, double a, double b)
    {
        double x = SampleGamma(rng, a);
        double y = SampleGamma(rng, b);
        double sum = x + y;
        return sum > 1e-15 ? x / sum : 0.5; // guard against 0/0
    }

    private static double SampleGamma(Random rng, double shape)
    {
        if (shape >= 1.0)
        {
            // Marsaglia and Tsang's method
            double d = shape - 1.0 / 3.0;
            double c = 1.0 / Math.Sqrt(9.0 * d);
            while (true)
            {
                double x, v;
                do { x = SampleStdNormal(rng); v = 1.0 + c * x; } while (v <= 0);
                v = v * v * v;
                double u = rng.NextDouble();
                if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
                if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
            }
        }
        double u2 = rng.NextDouble();
        // Guard against u=0 which would produce 0^(1/shape) = 0, causing 0/0 in SampleBeta
        if (u2 < 1e-15) u2 = 1e-15;
        return SampleGamma(rng, shape + 1.0) * Math.Pow(u2, 1.0 / shape);
    }

    private static double SampleStdNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ── Item 6: CutMix for sequences ──────────────────────────────────────

    /// <summary>
    /// CutMix for temporal sequences: splice a contiguous timestep range from s2 into s1.
    /// Cut length ~ Beta(alpha, alpha) x T. Preserves local temporal structure.
    /// </summary>
    internal static (float[][] CutSeq, double CutLabel, float CutMag) ApplyCutMix(
        TrainingSample s1, TrainingSample s2, double alpha, Random rng)
    {
        int T = s1.SequenceFeatures!.Length;
        int C = s1.SequenceFeatures![0].Length;
        double lambda = SampleBeta(rng, alpha, alpha);
        int cutLen = Math.Max(1, (int)(lambda * T));
        int cutStart = rng.Next(0, T - cutLen + 1);

        var cut = new float[T][];
        for (int t = 0; t < T; t++)
        {
            cut[t] = new float[C];
            bool fromS2 = t >= cutStart && t < cutStart + cutLen;
            var src = fromS2 ? s2.SequenceFeatures! : s1.SequenceFeatures!;
            Array.Copy(src[t], cut[t], C);
        }
        double mixRatio = (double)cutLen / T;
        double y1 = s1.Direction > 0 ? 1.0 : 0.0;
        double y2 = s2.Direction > 0 ? 1.0 : 0.0;
        return (cut, (1 - mixRatio) * y1 + mixRatio * y2,
                (float)((1 - mixRatio) * s1.Magnitude + mixRatio * s2.Magnitude));
    }

    // ── Item 7: Gradient noise injection ──────────────────────────────────

    /// <summary>
    /// Adds decaying Gaussian noise to gradient: g += N(0, sigma / (1 + t)^0.55).
    /// </summary>
    internal static void InjectGradientNoise(double[] gradient, int length, double sigma, int adamT, Random rng)
    {
        if (sigma <= 0) return;
        double scale = sigma / Math.Pow(1.0 + adamT, 0.55);
        for (int i = 0; i < length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            gradient[i] += scale * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    // ── Item 8: Per-parameter-group learning rates ────────────────────────

    /// <summary>
    /// Computes per-group learning rate by applying group-specific scale to the base LR.
    /// </summary>
    internal static double GroupLr(double baseLr, double groupScale) => baseLr * groupScale;

    // ── Item 9: Lookahead optimizer wrapping Adam ─────────────────────────

    /// <summary>
    /// Lookahead state: maintains slow weights updated every K fast steps.
    /// theta_slow += alpha x (theta_fast - theta_slow)
    /// </summary>
    internal sealed class LookaheadState
    {
        private readonly double[][] _slowConvW;
        private readonly double[][] _slowConvB;
        private readonly double[] _slowHeadW;
        private readonly double[] _slowHeadB;
        private readonly double[] _slowMagW;
        private double _slowMagB;
        private int _step;

        public LookaheadState(double[][] convW, double[][] convB, double[] headW, double[] headB,
            double[] magW, double magB)
        {
            _slowConvW = DeepCopy2D(convW);
            _slowConvB = DeepCopy2D(convB);
            _slowHeadW = (double[])headW.Clone();
            _slowHeadB = (double[])headB.Clone();
            _slowMagW = (double[])magW.Clone();
            _slowMagB = magB;
        }

        /// <summary>
        /// Called after each Adam update. Every K steps, interpolates fast weights toward slow weights.
        /// </summary>
        public void Step(double[][] convW, double[][] convB, double[] headW, double[] headB,
            double[] magW, ref double magB, int lookaheadK, double alpha)
        {
            _step++;
            if (_step % lookaheadK != 0) return;

            // theta_slow += alpha * (theta_fast - theta_slow), then theta_fast = theta_slow
            InterpolateAndSync(_slowConvW, convW, alpha);
            InterpolateAndSync(_slowConvB, convB, alpha);
            InterpolateAndSync1D(_slowHeadW, headW, alpha);
            InterpolateAndSync1D(_slowHeadB, headB, alpha);
            InterpolateAndSync1D(_slowMagW, magW, alpha);
            _slowMagB += alpha * (magB - _slowMagB);
            magB = _slowMagB;
        }

        private static void InterpolateAndSync(double[][] slow, double[][] fast, double alpha)
        {
            for (int b = 0; b < slow.Length; b++)
                InterpolateAndSync1D(slow[b], fast[b], alpha);
        }

        private static void InterpolateAndSync1D(double[] slow, double[] fast, double alpha)
        {
            for (int i = 0; i < slow.Length; i++)
            {
                slow[i] += alpha * (fast[i] - slow[i]);
                fast[i] = slow[i];
            }
        }

        private static double[][] DeepCopy2D(double[][] src)
        {
            var d = new double[src.Length][];
            for (int i = 0; i < src.Length; i++) d[i] = (double[])src[i].Clone();
            return d;
        }
    }

    // ── Item 10: R-Drop regularisation ────────────────────────────────────

    /// <summary>
    /// Computes symmetric KL divergence between two probability outputs from two forward passes.
    /// KL(p1 || p2) = p1 * log(p1/p2) + (1-p1) * log((1-p1)/(1-p2)).
    /// Returns the symmetric KL: 0.5 * (KL(p1||p2) + KL(p2||p1)).
    /// </summary>
    internal static double SymmetricKL(double p1, double p2)
    {
        p1 = Math.Clamp(p1, 1e-7, 1 - 1e-7);
        p2 = Math.Clamp(p2, 1e-7, 1 - 1e-7);
        double kl12 = p1 * Math.Log(p1 / p2) + (1 - p1) * Math.Log((1 - p1) / (1 - p2));
        double kl21 = p2 * Math.Log(p2 / p1) + (1 - p2) * Math.Log((1 - p2) / (1 - p1));
        return 0.5 * (kl12 + kl21);
    }

    // ── Item 11: Curriculum learning ──────────────────────────────────────

    /// <summary>
    /// Computes the curriculum subset size for a given epoch.
    /// Starts with easyFraction of samples, grows to 100% by the final epoch.
    /// Difficulty percentile = (epoch / totalEpochs)^pacingExponent.
    /// </summary>
    internal static int CurriculumSubsetSize(int totalSamples, int epoch, int totalEpochs,
        double easyFraction, double pacingExponent)
    {
        if (epoch >= totalEpochs - 1) return totalSamples;
        double progress = Math.Pow((double)(epoch + 1) / totalEpochs, pacingExponent);
        double fraction = easyFraction + (1.0 - easyFraction) * progress;
        return Math.Max(1, (int)(totalSamples * fraction));
    }

    /// <summary>
    /// Sorts samples by "easiness" (magnitude of signed label x magnitude = how clear the signal is).
    /// High magnitude = easy (clear directional move), low = hard (ambiguous).
    /// Returns sorted indices with easiest samples first.
    /// </summary>
    internal static int[] SortByEasiness(List<TrainingSample> samples)
    {
        var indices = new int[samples.Count];
        var keys = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            indices[i] = i;
            keys[i] = -Math.Abs(samples[i].Magnitude); // negate for ascending sort (easiest = highest magnitude first)
        }
        Array.Sort(keys, indices);
        return indices;
    }

    // ── Item 12: Gradient accumulation (formalise micro-batches) ──────────

    /// <summary>
    /// Computes effective batch parameters for micro-batch gradient accumulation.
    /// When the desired batch size exceeds available memory, split into micro-batches
    /// and accumulate gradients across them.
    /// </summary>
    internal static (int MicroBatchSize, int AccumulationSteps) ComputeMicroBatchParams(
        int desiredBatchSize, int maxMicroBatchSize)
    {
        if (desiredBatchSize <= maxMicroBatchSize)
            return (desiredBatchSize, 1);
        int steps = (desiredBatchSize + maxMicroBatchSize - 1) / maxMicroBatchSize;
        int micro = (desiredBatchSize + steps - 1) / steps;
        return (micro, steps);
    }
}
