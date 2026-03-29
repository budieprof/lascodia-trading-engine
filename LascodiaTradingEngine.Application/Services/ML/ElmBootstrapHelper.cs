using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Extracted bootstrap, SMOTE, and feature-subset utilities for the ELM trainer.
/// All methods are stateless and thread-safe.
/// </summary>
internal static class ElmBootstrapHelper
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Temporal weighting
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count <= 0)
            return [];

        double safeLambda = double.IsFinite(lambda) && lambda > 0.0 ? lambda : 0.0;
        var w = new double[count];
        double maxExponent = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            double exponent = safeLambda * i / Math.Max(1, count);
            w[i] = exponent;
            maxExponent = Math.Max(maxExponent, exponent);
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            w[i] = Math.Exp(w[i] - maxExponent);
            sum += w[i];
        }

        if (sum > 1e-15)
        {
            for (int i = 0; i < count; i++)
                w[i] /= sum;
            return w;
        }

        double uniform = 1.0 / count;
        for (int i = 0; i < count; i++)
            w[i] = uniform;
        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stratified biased bootstrap
    // ═══════════════════════════════════════════════════════════════════════════

    internal static List<TrainingSample> StratifiedBiasedBootstrap(
        List<TrainingSample> train, double[] temporalWeights, int count, int seed)
    {
        var buyIdx  = new List<int>();
        var sellIdx = new List<int>();
        for (int i = 0; i < train.Count; i++)
        {
            if (train[i].Direction > 0) buyIdx.Add(i);
            else sellIdx.Add(i);
        }

        if (buyIdx.Count == 0 || sellIdx.Count == 0)
            return BiasedBootstrap(train, temporalWeights, count, seed);

        int halfCount = count / 2;
        var rng = new Random(seed);
        var result = new List<TrainingSample>(count);

        var buyCdf  = BuildClassCdf(buyIdx, temporalWeights);
        var sellCdf = BuildClassCdf(sellIdx, temporalWeights);

        for (int i = 0; i < halfCount; i++)
        {
            result.Add(train[buyIdx[SampleFromCdf(buyCdf, rng)]]);
            result.Add(train[sellIdx[SampleFromCdf(sellCdf, rng)]]);
        }

        // Handle odd count — add one more sample from the larger class
        if (count % 2 != 0)
        {
            bool sampleBuy = buyIdx.Count >= sellIdx.Count;
            result.Add(sampleBuy
                ? train[buyIdx[SampleFromCdf(buyCdf, rng)]]
                : train[sellIdx[SampleFromCdf(sellCdf, rng)]]);
        }

        return result;
    }

    internal static HashSet<int> ReplayBootstrapIndices(
        List<TrainingSample> train, double[] temporalWeights, int count, int seed)
    {
        var buyIdx  = new List<int>();
        var sellIdx = new List<int>();
        for (int i = 0; i < train.Count; i++)
        {
            if (train[i].Direction > 0) buyIdx.Add(i);
            else sellIdx.Add(i);
        }

        var drawn = new HashSet<int>();

        if (buyIdx.Count == 0 || sellIdx.Count == 0)
        {
            var cdf = BuildNormalisedCdf(temporalWeights);
            var rng = new Random(seed);
            for (int i = 0; i < count; i++)
                drawn.Add(SampleFromCdf(cdf, rng));
            return drawn;
        }

        int halfCount = count / 2;
        var rng2 = new Random(seed);
        var buyCdf  = BuildClassCdf(buyIdx, temporalWeights);
        var sellCdf = BuildClassCdf(sellIdx, temporalWeights);

        for (int i = 0; i < halfCount; i++)
        {
            drawn.Add(buyIdx[SampleFromCdf(buyCdf, rng2)]);
            drawn.Add(sellIdx[SampleFromCdf(sellCdf, rng2)]);
        }

        // Handle odd count
        if (count % 2 != 0)
        {
            bool sampleBuy = buyIdx.Count >= sellIdx.Count;
            drawn.Add(sampleBuy
                ? buyIdx[SampleFromCdf(buyCdf, rng2)]
                : sellIdx[SampleFromCdf(sellCdf, rng2)]);
        }

        return drawn;
    }

    internal static List<TrainingSample> BiasedBootstrap(
        List<TrainingSample> train, double[] temporalWeights, int count, int seed)
    {
        var cdf = BuildNormalisedCdf(temporalWeights);
        var rng = new Random(seed);
        var result = new List<TrainingSample>(count);
        for (int i = 0; i < count; i++)
            result.Add(train[SampleFromCdf(cdf, rng)]);
        return result;
    }

    internal static double[] BuildClassCdf(List<int> indices, double[] weights)
    {
        if (indices.Count == 0)
            return [];

        var cdf = new double[indices.Count];
        double sum = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            double w = indices[i] < weights.Length ? weights[indices[i]] : 1.0;
            w = double.IsFinite(w) && w > 0.0 ? w : 0.0;
            sum += w;
            cdf[i] = sum;
        }

        if (sum > 1e-15)
        {
            for (int i = 0; i < cdf.Length; i++)
                cdf[i] /= sum;
            cdf[^1] = 1.0;
            return cdf;
        }

        return BuildUniformCdf(indices.Count);
    }

    internal static double[] BuildNormalisedCdf(double[] weights)
    {
        if (weights.Length == 0)
            return [];

        var cdf = new double[weights.Length];
        double sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double w = double.IsFinite(weights[i]) && weights[i] > 0.0 ? weights[i] : 0.0;
            sum += w;
            cdf[i] = sum;
        }

        if (sum > 1e-15)
        {
            for (int i = 0; i < cdf.Length; i++)
                cdf[i] /= sum;
            cdf[^1] = 1.0;
            return cdf;
        }

        return BuildUniformCdf(weights.Length);
    }

    internal static int SampleFromCdf(double[] cdf, Random rng)
    {
        if (cdf.Length == 0)
            return 0;

        double u = rng.NextDouble();
        int idx = Array.BinarySearch(cdf, u);
        return idx >= 0 ? idx : Math.Min(~idx, cdf.Length - 1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Feature subset generation
    // ═══════════════════════════════════════════════════════════════════════════

    internal static int[] GenerateFeatureSubset(int featureCount, double ratio, int seed)
    {
        if (featureCount <= 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subsetSize = Math.Clamp(Math.Max(1, (int)Math.Ceiling(featureCount * safeRatio)), 1, featureCount);
        var indices = Enumerable.Range(0, featureCount).ToArray();
        var rng = new Random(seed);

        for (int i = 0; i < subsetSize; i++)
        {
            int j = rng.Next(i, featureCount);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var subset = new int[subsetSize];
        Array.Copy(indices, subset, subsetSize);
        Array.Sort(subset);
        return subset;
    }

    internal static int[] GenerateBiasedFeatureSubset(
        int featureCount, double ratio, double[] importanceScores, int seed)
    {
        if (featureCount <= 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subCount = Math.Clamp(Math.Max(1, (int)Math.Ceiling(safeRatio * featureCount)), 1, featureCount);
        var rng      = new Random(seed);
        double epsilon = 1.0 / featureCount;

        var rawWeights = new double[featureCount];
        double sum = 0.0;
        for (int j = 0; j < featureCount; j++)
        {
            double importance = j < importanceScores.Length ? importanceScores[j] : 0.0;
            double w = Math.Max(0.0, importance) + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        if (sum <= 1e-15)
            return GenerateFeatureSubset(featureCount, ratio, seed);

        var cdf = new double[featureCount];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < featureCount; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        var selected = new HashSet<int>(subCount);
        int attempts = 0;
        while (selected.Count < subCount && attempts < featureCount * 10)
        {
            attempts++;
            double u   = rng.NextDouble();
            int    idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, featureCount - 1);
            selected.Add(idx);
        }

        for (int j = 0; j < featureCount && selected.Count < subCount; j++)
            selected.Add(j);

        return [.. selected.OrderBy(x => x)];
    }

    internal static int[] GenerateFeatureSubsetFromPool(int[] pool, double ratio, int seed)
    {
        if (pool.Length == 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subsetSize = Math.Clamp(Math.Max(1, (int)Math.Ceiling(pool.Length * safeRatio)), 1, pool.Length);
        var indices = (int[])pool.Clone();
        var rng = new Random(seed);

        for (int i = 0; i < subsetSize; i++)
        {
            int j = rng.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var subset = new int[subsetSize];
        Array.Copy(indices, subset, subsetSize);
        Array.Sort(subset);
        return subset;
    }

    internal static int[] GenerateBiasedFeatureSubsetFromPool(
        int[] pool, double ratio, double[] importanceScores, int seed)
    {
        if (pool.Length == 0)
            return [];

        double safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 1.0;
        int subCount = Math.Clamp(Math.Max(1, (int)Math.Ceiling(pool.Length * safeRatio)), 1, pool.Length);
        var rng = new Random(seed);
        double epsilon = 1.0 / pool.Length;

        var rawWeights = new double[pool.Length];
        double sum = 0.0;
        for (int j = 0; j < pool.Length; j++)
        {
            int fi = pool[j];
            double importance = fi < importanceScores.Length ? importanceScores[fi] : 0.0;
            double w = Math.Max(0.0, importance) + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        if (sum <= 1e-15)
            return GenerateFeatureSubsetFromPool(pool, ratio, seed);

        var cdf = new double[pool.Length];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < pool.Length; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        var selected = new HashSet<int>(subCount);
        int attempts = 0;
        while (selected.Count < subCount && attempts < pool.Length * 10)
        {
            attempts++;
            double u = rng.NextDouble();
            int idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, pool.Length - 1);
            selected.Add(pool[idx]);
        }

        for (int j = 0; j < pool.Length && selected.Count < subCount; j++)
            selected.Add(pool[j]);

        return [.. selected.OrderBy(x => x)];
    }

    internal static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        if (threshold <= 0) return Enumerable.Repeat(true, featureCount).ToArray();

        var normalised = ElmEvaluationHelper.NormalisePositiveImportance(importance, featureCount);
        if (normalised.Length == 0 || normalised.Sum(v => v) <= 1e-12f)
            return Enumerable.Repeat(true, featureCount).ToArray();

        double equalShare = 1.0 / featureCount;
        double cutoff = equalShare * threshold;
        var mask = new bool[featureCount];
        for (int i = 0; i < featureCount; i++)
            mask[i] = normalised[i] >= cutoff;
        return mask;
    }

    internal static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        int activeCount = mask.Count(m => m);
        return samples.Select(s =>
        {
            var compact = new float[activeCount];
            int ci = 0;
            for (int i = 0; i < s.Features.Length && i < mask.Length; i++)
                if (mask[i]) compact[ci++] = s.Features[i];
            return s with { Features = compact };
        }).ToList();
    }

    internal static List<TrainingSample> ApplyZeroMask(List<TrainingSample> samples, bool[] mask)
    {
        return samples.Select(s =>
        {
            var masked = (float[])s.Features.Clone();
            for (int i = 0; i < masked.Length && i < mask.Length; i++)
            {
                if (!mask[i]) masked[i] = 0f;
            }
            return s with { Features = masked };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  SMOTE (Synthetic Minority Oversampling)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates synthetic samples for the minority class using SMOTE with approximate
    /// nearest neighbors. Returns tuples of (sample, isSynthetic) so callers can apply
    /// differential weighting to synthetic vs real samples.
    /// </summary>
    internal static List<(TrainingSample Sample, bool IsSynthetic)> GenerateSmoteSamples(
        List<TrainingSample> minoritySamples,
        int syntheticCount,
        int kNeighbors,
        int seed)
    {
        if (minoritySamples.Count < 2 || syntheticCount <= 0)
            return [];

        int n = minoritySamples.Count;
        int k = Math.Min(kNeighbors, n - 1);
        var rng = new Random(seed);
        var result = new List<(TrainingSample, bool)>(syntheticCount);
        int featureLen = minoritySamples[0].Features.Length;

        int candidatePoolSize = Math.Min(n - 1, Math.Max(3 * k, (int)(Math.Sqrt(n) * k)));
        bool useExact = n <= 500;

        var neighborIdx = new int[n][];
        if (useExact)
        {
            for (int i = 0; i < n; i++)
            {
                var dists = new (double Dist, int Idx)[n];
                for (int j = 0; j < n; j++)
                {
                    double d = 0;
                    for (int f = 0; f < featureLen; f++)
                    {
                        double diff = minoritySamples[i].Features[f] - minoritySamples[j].Features[f];
                        d += diff * diff;
                    }
                    dists[j] = (d, j);
                }
                Array.Sort(dists, (a, b) => a.Dist.CompareTo(b.Dist));
                neighborIdx[i] = new int[k];
                for (int ni = 0; ni < k; ni++)
                    neighborIdx[i][ni] = dists[ni + 1].Idx;
            }
        }
        else
        {
            var threadLocalPool = new ThreadLocal<int[]>(() => new int[n - 1]);
            var threadLocalCandidates = new ThreadLocal<(double Dist, int Idx)[]>(
                () => new (double Dist, int Idx)[candidatePoolSize]);

            Parallel.For(0, n, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            }, i =>
            {
                var localRng = new Random(ElmMathHelper.HashSeed(seed, i));
                var candidates = threadLocalCandidates.Value!;
                var pool = threadLocalPool.Value!;

                int pi = 0;
                for (int idx = 0; idx < n; idx++)
                    if (idx != i) pool[pi++] = idx;
                for (int si = 0; si < candidatePoolSize; si++)
                {
                    int swapIdx = localRng.Next(si, pool.Length);
                    (pool[si], pool[swapIdx]) = (pool[swapIdx], pool[si]);
                }

                for (int ci = 0; ci < candidatePoolSize; ci++)
                {
                    int j = pool[ci];
                    double d = 0;
                    for (int f = 0; f < featureLen; f++)
                    {
                        double diff = minoritySamples[i].Features[f] - minoritySamples[j].Features[f];
                        d += diff * diff;
                    }
                    candidates[ci] = (d, j);
                }

                Array.Sort(candidates, (a, b) => a.Dist.CompareTo(b.Dist));
                neighborIdx[i] = new int[k];
                for (int ni = 0; ni < k; ni++)
                    neighborIdx[i][ni] = candidates[ni].Idx;
            });

            threadLocalPool.Dispose();
            threadLocalCandidates.Dispose();
        }

        for (int s = 0; s < syntheticCount; s++)
        {
            int baseIdx = rng.Next(n);
            int nnIdx = neighborIdx[baseIdx][rng.Next(k)];
            double lambda = rng.NextDouble();

            var baseFeatures = minoritySamples[baseIdx].Features;
            var nnFeatures = minoritySamples[nnIdx].Features;
            var synFeatures = new float[featureLen];
            for (int f = 0; f < featureLen; f++)
                synFeatures[f] = (float)(baseFeatures[f] + lambda * (nnFeatures[f] - baseFeatures[f]));

            double synMag = minoritySamples[baseIdx].Magnitude +
                            lambda * (minoritySamples[nnIdx].Magnitude - minoritySamples[baseIdx].Magnitude);

            result.Add((new TrainingSample(
                synFeatures,
                minoritySamples[baseIdx].Direction,
                (float)synMag), true));
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Density-ratio importance weights
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double[] ComputeDensityRatioWeights(
        List<TrainingSample> train, int featureCount, int recentWindowDays, int barsPerDay = 24)
    {
        int effectiveBarsPerDay = barsPerDay > 0 ? barsPerDay : 24;
        int recentCount = Math.Min(train.Count / 4, recentWindowDays * effectiveBarsPerDay);
        if (recentCount < 10)
        {
            var uniform = new double[train.Count];
            double uniformW = train.Count > 0 ? 1.0 / train.Count : 0.0;
            Array.Fill(uniform, uniformW);
            return uniform;
        }

        int splitPoint = train.Count - recentCount;
        double[] weights = new double[train.Count];
        double[] w = new double[featureCount];
        double   b = 0;
        const double densityBaseLr = 0.01;
        const double l2Lambda = 1e-3;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        const int densityMaxEpochs = 100;

        double[] adamMW = new double[featureCount], adamVW = new double[featureCount];
        double   adamMB = 0, adamVB = 0;
        int      globalStep = 0;

        int historicalCount = splitPoint;
        int recentTotalCount = train.Count - splitPoint;
        int valHistorical = Math.Max(2, historicalCount / 5);
        int valRecent     = Math.Max(2, recentTotalCount / 5);

        var trainIndices = new List<int>(train.Count);
        var valIndices   = new List<int>(valHistorical + valRecent);

        for (int i = 0; i < historicalCount - valHistorical; i++) trainIndices.Add(i);
        for (int i = historicalCount - valHistorical; i < historicalCount; i++) valIndices.Add(i);
        for (int i = splitPoint; i < train.Count - valRecent; i++) trainIndices.Add(i);
        for (int i = train.Count - valRecent; i < train.Count; i++) valIndices.Add(i);

        int trainSize = trainIndices.Count;
        double bestValLoss = double.MaxValue;
        double[] bestW = new double[featureCount];
        double   bestB = 0;
        int patience = 0;
        const int maxPatience = 10;

        for (int epoch = 0; epoch < densityMaxEpochs; epoch++)
        {
            double lr = ElmMathHelper.CosineAnnealLr(densityBaseLr, epoch, densityMaxEpochs);
            double gradB = 0;
            double[] gradW = new double[featureCount];

            for (int ti = 0; ti < trainIndices.Count; ti++)
            {
                int i = trainIndices[ti];
                var f = train[i].Features;
                double z = b;
                for (int j = 0; j < Math.Min(w.Length, f.Length); j++) z += w[j] * f[j];
                double p = MLFeatureHelper.Sigmoid(z);
                double y = i >= splitPoint ? 1.0 : 0.0;
                double err = p - y;
                gradB += err;
                for (int j = 0; j < Math.Min(w.Length, f.Length); j++) gradW[j] += err * f[j];
            }

            double gB = gradB / trainSize + 2.0 * l2Lambda * b;
            globalStep++;
            adamMB = beta1 * adamMB + (1 - beta1) * gB;
            adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
            b -= lr * (adamMB / (1 - Math.Pow(beta1, globalStep))) / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, globalStep))) + eps);

            for (int j = 0; j < featureCount; j++)
            {
                double gW = gradW[j] / trainSize + 2.0 * l2Lambda * w[j];
                adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                w[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, globalStep))) / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, globalStep))) + eps);
            }

            double valLoss = 0;
            for (int vi = 0; vi < valIndices.Count; vi++)
            {
                int i = valIndices[vi];
                double z = b;
                var f = train[i].Features;
                for (int j = 0; j < Math.Min(w.Length, f.Length); j++) z += w[j] * f[j];
                double p = MLFeatureHelper.Sigmoid(z);
                double y = i >= splitPoint ? 1.0 : 0.0;
                valLoss -= y * Math.Log(Math.Max(p, 1e-10))
                         + (1 - y) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            valLoss /= valIndices.Count;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, featureCount);
                bestB = b;
                patience = 0;
            }
            else if (++patience >= maxPatience)
            {
                break;
            }
        }

        w = bestW;
        b = bestB;

        double sum = 0;
        for (int i = 0; i < train.Count; i++)
        {
            double z = b;
            var f = train[i].Features;
            for (int j = 0; j < Math.Min(w.Length, f.Length); j++) z += w[j] * f[j];
            double p = MLFeatureHelper.Sigmoid(z);
            weights[i] = Math.Clamp(p / Math.Max(1 - p, 1e-6), 0.1, 10.0);
            sum += weights[i];
        }
        if (sum > 1e-15) for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    }

    internal static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> train, double[][] parentBp, int featureCount)
    {
        if (train.Count == 0)
            return [];

        int effectiveFeatureCount = Math.Min(
            Math.Min(featureCount, parentBp.Length),
            train.Min(s => s.Features.Length));

        double[] weights = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            int outsideCount = 0;
            int checkedCount = 0;
            for (int j = 0; j < effectiveFeatureCount; j++)
            {
                var bp = parentBp[j];
                if (bp is null || bp.Length < 2) continue;
                double q10 = bp[0];
                double q90 = bp[^1];
                if (!double.IsFinite(q10) || !double.IsFinite(q90))
                    continue;
                checkedCount++;
                double v = train[i].Features[j];
                if (!double.IsFinite(v))
                    continue;
                if (v < q10 || v > q90) outsideCount++;
            }
            weights[i] = 1.0 + (double)outsideCount / Math.Max(1, checkedCount);
        }

        double sum = 0;
        for (int i = 0; i < weights.Length; i++) sum += weights[i];
        if (double.IsFinite(sum) && sum > 1e-15)
        {
            for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        }
        else
        {
            double uniform = 1.0 / weights.Length;
            for (int i = 0; i < weights.Length; i++) weights[i] = uniform;
        }
        return weights;
    }

    private static double[] BuildUniformCdf(int count)
    {
        if (count <= 0)
            return [];

        var cdf = new double[count];
        for (int i = 0; i < count; i++)
            cdf[i] = (i + 1.0) / count;
        cdf[^1] = 1.0;
        return cdf;
    }
}
