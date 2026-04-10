using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  Model probability computation
    // ═══════════════════════════════════════════════════════════════════════════

    private static double RocketProb(double[] rocketFeatures, double[] w, double bias, int dim)
    {
        double logit = bias;
        for (int j = 0; j < dim; j++) logit += w[j] * rocketFeatures[j];
        return MLFeatureHelper.Sigmoid(logit);
    }

    private static double CalibratedProb(
        double[] rocketFeatures, double[] w, double bias, double plattA, double plattB, int dim)
    {
        double raw = RocketProb(rocketFeatures, w, bias, dim);
        raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
        return MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
    }

    /// <summary>#28: Overload accepting <see cref="RocketModelParams"/>.</summary>
    private static double CalibratedProb(double[] rocketFeatures, in RocketModelParams p)
        => CalibratedProb(rocketFeatures, p.W, p.Bias, p.PlattA, p.PlattB, p.Dim);

    /// <summary>#28: Overload accepting <see cref="RocketModelParams"/>.</summary>
    private static double RocketProb(double[] rocketFeatures, in RocketModelParams p)
        => RocketProb(rocketFeatures, p.W, p.Bias, p.Dim);

    // ═══════════════════════════════════════════════════════════════════════════
    //  Density-ratio importance weights
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int featureCount, int windowDays)
    {
        int n = trainSet.Count;
        int recentN = Math.Min(n / 4, windowDays * 24);
        if (recentN < 10) return Enumerable.Repeat(1.0 / n, n).ToArray();

        int splitIdx = n - recentN;

        // Train a simple logistic discriminator: recent (1) vs historical (0)
        var w = new double[featureCount];
        double b = 0;

        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < n; i++)
            {
                double label = i >= splitIdx ? 1.0 : 0.0;
                double logit = b;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    logit += w[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(logit);
                double err = p - label;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    w[j] -= 0.01 * err * trainSet[i].Features[j] / n;
                b -= 0.01 * err / n;
            }
        }

        var weights = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double logit = b;
            for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                logit += w[j] * trainSet[i].Features[j];
            double p = MLFeatureHelper.Sigmoid(logit);
            p = Math.Clamp(p, 0.01, 0.99);
            weights[i] = p / (1.0 - p);
            sum += weights[i];
        }

        if (sum > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Covariate shift weights from parent model
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBreakpoints, int featureCount)
    {
        int n = trainSet.Count;
        var weights = new double[n];
        Array.Fill(weights, 1.0);

        int usableFeatures = Math.Min(featureCount, parentBreakpoints.Length);
        if (usableFeatures == 0) return weights;

        for (int i = 0; i < n; i++)
        {
            double novelty = 0;
            for (int j = 0; j < usableFeatures; j++)
            {
                double val = trainSet[i].Features[j];
                var bp = parentBreakpoints[j];
                if (bp.Length == 0) continue;

                // Count which bin the value falls into; extreme bins get higher novelty
                int bin = 0;
                while (bin < bp.Length && val > bp[bin]) bin++;
                double binFrac = (double)bin / (bp.Length + 1);
                novelty += Math.Abs(binFrac - 0.5);
            }
            weights[i] = 1.0 + novelty / usableFeatures;
        }

        double sum = 0;
        for (int i = 0; i < n; i++) sum += weights[i];
        if (sum > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Temporal weights
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        var weights = new double[count];
        if (lambda <= 0 || count == 0)
        {
            double uniform = count > 0 ? 1.0 / count : 1.0;
            Array.Fill(weights, uniform);
            return weights;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            weights[i] = Math.Exp(lambda * i / count);
            sum += weights[i];
        }
        for (int i = 0; i < count; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Statistical helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double d = values[i] - mean;
            sum += d * d;
        }
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double SampleGaussian(Random rng)
    {
        double u1 = Math.Max(1e-10, rng.NextDouble());
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Feature pruning helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++)
            mask[j] = j < importance.Length && importance[j] >= threshold;

        // Ensure at least 10 features remain active
        int active = mask.Count(m => m);
        if (active < 10)
        {
            Array.Fill(mask, true);
        }

        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var f = new float[s.Features.Length];
            for (int j = 0; j < f.Length; j++)
                f[j] = j < mask.Length && mask[j] ? s.Features[j] : 0f;
            result.Add(s with { Features = f });
        }
        return result;
    }

    /// <summary>Samples from Gamma(shape, 1) using the Marsaglia-Tsang method.</summary>
    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            double u = rng.NextDouble();
            return SampleGamma(rng, shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = SampleGaussian(rng);
                v = 1.0 + c * x;
            } while (v <= 0);

            v = v * v * v;
            double u2 = rng.NextDouble();
            if (u2 < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v;
            if (Math.Log(u2) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }

    /// <summary>Samples from Beta(alpha, alpha) for Mixup augmentation.</summary>
    private static double SampleBeta(Random rng, double alpha)
    {
        double x = SampleGamma(rng, alpha);
        double y = SampleGamma(rng, alpha);
        double sum = x + y;
        return sum > 1e-15 ? x / sum : 0.5;
    }
}
