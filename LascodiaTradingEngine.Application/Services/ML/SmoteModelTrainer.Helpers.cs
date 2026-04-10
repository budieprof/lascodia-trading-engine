using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SmoteModelTrainer
{

    // ── M4: Density-ratio importance weights ──────────────────────────────────

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  F,
        int                  windowDays,
        int                  barsPerDay)
    {
        int recentCount = Math.Min(trainSet.Count, windowDays * barsPerDay);
        int n           = trainSet.Count;
        var weights     = new double[n]; Array.Fill(weights, 1.0);

        if (recentCount < 20 || n - recentCount < 20) return weights;

        // Train logistic discriminator: recent (y=1) vs historical (y=0)
        var dw = new double[F];
        double db = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DensityRatioEpochs; ep++)
        {
            double[] gdw = new double[F]; double gdb = 0;
            for (int i = 0; i < n; i++)
            {
                double y    = i >= (n - recentCount) ? 1.0 : 0.0;
                double logit = db;
                for (int j = 0; j < F; j++) logit += dw[j] * trainSet[i].Features[j];
                double p   = Sigmoid(logit);
                double err = p - y;
                gdb += err;
                for (int j = 0; j < F; j++) gdw[j] += err * trainSet[i].Features[j];
            }
            double inv = 1.0 / n;
            db -= lr * gdb * inv;
            for (int j = 0; j < F; j++) dw[j] -= lr * (gdw[j] * inv + 0.001 * dw[j]);
        }

        for (int i = 0; i < n; i++)
        {
            double logit = db;
            for (int j = 0; j < F; j++) logit += dw[j] * trainSet[i].Features[j];
            double p = Sigmoid(logit);
            weights[i] = Math.Max(0.1, Math.Min(10.0, p / Math.Max(1 - p, 1e-9)));
        }

        return weights;
    }

    // ── M5: Covariate shift weights ───────────────────────────────────────────

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet,
        double[][]           parentBp,
        int                  F)
    {
        var weights = new double[trainSet.Count];
        int n = trainSet.Count;

        for (int i = 0; i < n; i++)
        {
            var s = trainSet[i];
            int outOfRange   = 0;
            int checkedCount = 0;

            for (int j = 0; j < Math.Min(F, parentBp.Length); j++)
            {
                var bp = parentBp[j];
                if (bp.Length < 2) continue;

                double val = j < s.Features.Length ? s.Features[j] : 0;
                double q10 = bp[0];
                double q90 = bp[^1];
                if (val < q10 || val > q90) outOfRange++;
                checkedCount++;
            }

            // Higher weight for novel samples outside parent's inter-decile range
            double noveltyFrac = checkedCount > 0 ? (double)outOfRange / checkedCount : 0.0;
            weights[i] = 1.0 + noveltyFrac; // [1.0, 2.0]
        }

        return weights;
    }

    // ── Helpers: ensemble probability computation ─────────────────────────────

    private static double EnsembleProb(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         F,
        int[][]?    featureSubsets,
        MetaLearner meta   = default,
        double[][]? mlpHW  = null,
        double[][]? mlpHB  = null,
        int         hidDim = 0)
    {
        int K = weights.Length;

        if (meta.IsActive)
        {
            double metaLogit = meta.Bias;
            for (int k = 0; k < Math.Min(K, meta.Weights.Length); k++)
            {
                double p = SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                    mlpHW?[k], mlpHB?[k], hidDim);
                metaLogit += meta.Weights[k] * p;
            }
            return Sigmoid(metaLogit);
        }

        double sumP = 0;
        for (int k = 0; k < K; k++)
            sumP += SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                mlpHW?[k], mlpHB?[k], hidDim);
        return sumP / K;
    }

    private static (double AvgP, double StdP) EnsembleProbAndStd(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         F,
        int[][]?    featureSubsets,
        double[][]? mlpHW  = null,
        double[][]? mlpHB  = null,
        int         hidDim = 0)
    {
        int K = weights.Length;
        if (K == 0) return (0.5, 0.0);

        var probs = new double[K];
        for (int k = 0; k < K; k++)
            probs[k] = SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                mlpHW?[k], mlpHB?[k], hidDim);

        double mean = probs.Average();
        double var2 = probs.Sum(p => (p - mean) * (p - mean)) / K;
        return (mean, Math.Sqrt(var2));
    }

    private static double SingleLearnerProb(
        float[]  x,
        double[] w,
        double   b,
        int[]?   subset,
        int      F,
        double[]? hW    = null,
        double[]? hB    = null,
        int       hidDim = 0)
    {
        if (hidDim > 0 && hW is not null && hB is not null && w.Length == hidDim)
        {
            int fk = subset?.Length ?? F;
            // Detect packed 2-layer model: L1 weights (hidDim×fk) + L2 weights (hidDim×hidDim)
            bool isDeep = hW.Length > hidDim * fk;

            // Layer 1: input → hidDim
            var h1 = new double[hidDim];
            for (int hj = 0; hj < hidDim; hj++)
            {
                double act = hB[hj];
                for (int ji = 0; ji < fk; ji++)
                {
                    int fi = subset is not null ? subset[ji] : ji;
                    if (hj * fk + ji < hW.Length && fi < x.Length)
                        act += hW[hj * fk + ji] * x[fi];
                }
                h1[hj] = Math.Max(0, act); // ReLU
            }

            double[] hFinal = h1;
            if (isDeep)
            {
                // Layer 2: hidDim → hidDim (weights packed after layer 1)
                int l2WOff = hidDim * fk;
                int l2BOff = hidDim;
                var h2 = new double[hidDim];
                for (int hj = 0; hj < hidDim; hj++)
                {
                    double act = l2BOff + hj < hB.Length ? hB[l2BOff + hj] : 0.0;
                    for (int ji = 0; ji < hidDim; ji++)
                    {
                        int wIdx = l2WOff + hj * hidDim + ji;
                        if (wIdx < hW.Length) act += hW[wIdx] * h1[ji];
                    }
                    h2[hj] = Math.Max(0, act); // ReLU
                }
                hFinal = h2;
            }

            double logit = b;
            for (int hj = 0; hj < Math.Min(hidDim, w.Length); hj++) logit += w[hj] * hFinal[hj];
            return Sigmoid(logit);
        }
        else
        {
            double z = b;
            if (subset is { Length: > 0 })
            {
                foreach (int j in subset)
                    if (j < F && j < w.Length && j < x.Length) z += w[j] * x[j];
            }
            else
                for (int j = 0; j < Math.Min(w.Length, F); j++) z += w[j] * x[j];
            return Sigmoid(z);
        }
    }

    // ── L8: Polynomial feature augmentation ──────────────────────────────────

    private static float[] BuildPolyAugmentedFeatures(float[] x, int[] top5, int F)
    {
        int    lenIn  = Math.Min(x.Length, F);
        var    aug    = new float[F + PolyPairCount];
        Array.Copy(x, aug, lenIn);
        int pairIdx = F;
        for (int i = 0; i < top5.Length - 1; i++)
        for (int j = i + 1; j < top5.Length; j++)
        {
            if (pairIdx < aug.Length)
            {
                float vi = top5[i] < x.Length ? x[top5[i]] : 0f;
                float vj = top5[j] < x.Length ? x[top5[j]] : 0f;
                aug[pairIdx++] = vi * vj;
            }
        }
        return aug;
    }

    private static int[] GetTop5FeatureIndices(ModelSnapshot? warmStart, int F)
    {
        if (warmStart?.FeatureImportance is { Length: > 0 } imp && imp.Length == F)
        {
            return [.. Enumerable.Range(0, F)
                .OrderByDescending(i => imp[i])
                .Take(PolyTopN)
                .OrderBy(i => i)];
        }
        return [0, 1, 2, 3, 4];
    }

    // ── Feature mask helpers ──────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double minImportance, int F)
    {
        var mask = new bool[F]; Array.Fill(mask, true);
        if (minImportance <= 0.0) return mask;

        double equalShare = 1.0 / Math.Max(1, F);
        for (int j = 0; j < F; j++)
            if (j < importance.Length && importance[j] < minImportance * equalShare)
                mask[j] = false;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        // Reduce dimensionality: only keep features where mask[j] is true
        int[] kept = Enumerable.Range(0, mask.Length).Where(j => mask[j]).ToArray();
        int newF = kept.Length;
        return samples.Select(s =>
        {
            var mf = new float[newF];
            for (int i = 0; i < newF; i++)
                mf[i] = kept[i] < s.Features.Length ? s.Features[kept[i]] : 0f;
            return s with { Features = mf };
        }).ToList();
    }

    // ── M8: Biased and random feature subset generation ───────────────────────

    private static int[] GenerateFeatureSubset(int F, double ratio, int seed)
    {
        int subF = Math.Max(3, (int)(F * ratio));
        var rng  = new Random(seed);
        return [.. Enumerable.Range(0, F).OrderBy(_ => rng.NextDouble()).Take(subF).OrderBy(x => x)];
    }

    private static int[] GenerateBiasedFeatureSubset(
        int      F,
        double   ratio,
        double[] importanceScores,
        int      seed)
    {
        int subF = Math.Max(3, (int)(F * ratio));
        var rng  = new Random(seed);

        // Softmax-weighted sampling from feature importance distribution
        double[] weights = new double[F];
        double   sum     = 0;
        for (int j = 0; j < F; j++)
        {
            double imp = j < importanceScores.Length ? Math.Max(0, importanceScores[j]) : 0;
            weights[j] = Math.Exp(imp * BiasedSamplingTemp);
            sum += weights[j];
        }
        for (int j = 0; j < F; j++) weights[j] /= sum;

        var selected = new HashSet<int>();
        while (selected.Count < subF)
        {
            double target = rng.NextDouble(), cum = 0;
            for (int j = 0; j < F; j++)
            {
                cum += weights[j];
                if (cum >= target) { selected.Add(j); break; }
            }
            if (selected.Count == 0) selected.Add(rng.Next(F));
        }
        return [.. selected.OrderBy(x => x)];
    }

    // ── Pearson correlation between learner weight vectors ────────────────────

    private static double PearsonCorrelation(double[] a, double[] b, int F)
    {
        int len = Math.Min(Math.Min(a.Length, b.Length), F);
        if (len == 0) return 0.0;

        double ma = 0, mb2 = 0;
        for (int j = 0; j < len; j++) { ma += a[j]; mb2 += b[j]; }
        ma /= len; mb2 /= len;

        double num = 0, da2 = 0, db2 = 0;
        for (int j = 0; j < len; j++)
        {
            double dA = a[j] - ma, dB = b[j] - mb2;
            num += dA * dB; da2 += dA * dA; db2 += dB * dB;
        }
        double denom = Math.Sqrt(da2 * db2);
        return denom > 1e-12 ? num / denom : 0.0;
    }

    // ── Temporal decay weights ────────────────────────────────────────────────

    private static double[] ComputeTemporalWeights(int n, double lambda)
    {
        var w = new double[n];
        if (lambda <= 0) { Array.Fill(w, 1.0); return w; }
        for (int i = 0; i < n; i++) w[i] = Math.Exp(-lambda * (n - 1 - i));
        return w;
    }

    // ── Math helpers ──────────────────────────────────────────────────────────

    private static double Sigmoid(double x) =>
        1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50.0, 50.0)));

    private static double EuclideanDistSq(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; sum += d * d; }
        return sum;
    }

    // Box-Muller transform for Gaussian noise
    private static double SampleNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // L7: Beta(α, α) sample via Gamma distribution (Marsaglia-Tsang method)
    private static double SampleBeta(double alpha, Random rng)
    {
        if (alpha <= 0) return 0.5;
        double x = SampleGamma(alpha, rng);
        double y = SampleGamma(alpha, rng);
        double sum = x + y;
        return sum > 0 ? x / sum : 0.5;
    }

    private static double SampleGamma(double shape, Random rng)
    {
        // For shape < 1, use Ahrens-Dieter correction: Gamma(a) = Gamma(a+1) * U^(1/a)
        if (shape < 1.0)
        {
            double u = rng.NextDouble();
            return SampleGamma(shape + 1.0, rng) * Math.Pow(Math.Max(u, 1e-15), 1.0 / shape);
        }
        // Marsaglia-Tsang method for shape >= 1
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = SampleNormal(rng);
                v = 1.0 + c * x;
            } while (v <= 0);
            v = v * v * v;
            double u2 = rng.NextDouble();
            if (u2 < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v;
            if (Math.Log(u2) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0.0;
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double StdDev(double[] values, double mean)
    {
        if (values.Length < 2) return 0.0;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) { double d = values[i] - mean; sum += d * d; }
        return Math.Sqrt(sum / (values.Length - 1));
    }
}
