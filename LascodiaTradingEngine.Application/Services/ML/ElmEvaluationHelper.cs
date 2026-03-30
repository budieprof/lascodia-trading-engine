using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Extracted evaluation routines for the ELM trainer: metrics computation, ECE, BSS,
/// permutation importance, Durbin-Watson, ensemble diversity, MI redundancy, and stationarity checks.
/// All methods are stateless and thread-safe.
/// </summary>
internal static class ElmEvaluationHelper
{
    private const double DefaultSharpeAnnualisationFactor = 252.0;

    private static int ToBinaryLabel(int direction) => direction > 0 ? 1 : 0;

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ensemble evaluation
    // ═══════════════════════════════════════════════════════════════════════════

    internal static EvalMetrics EvaluateEnsemble(
        List<TrainingSample> testSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double[] magWeights, double magBias,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double[]? magAugWeights, double magAugBias,
        double sharpeAnnualisationFactor,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb,
        Func<float[], double[], double, int, int, double[][], double[][], int[][]?, double> predictMagnitudeAug)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        bool useAug = magAugWeights is not null && magAugWeights.Length == featureCount + hiddenSize;

        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;
        double evWinSum = 0, evLossSum = 0;
        double[] returns = new double[testSet.Count];

        for (int i = 0; i < testSet.Count; i++)
        {
            var s = testSet[i];
            double calibP = ClampProbability(ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));

            int pred = calibP >= 0.5 ? 1 : 0;
            int actual = ToBinaryLabel(s.Direction);
            double y = actual;

            if (pred == actual) correct++;
            if (pred == 1 && actual == 1) tp++;
            if (pred == 1 && actual == 0) fp++;
            if (pred == 0 && actual == 1) fn++;
            if (pred == 0 && actual == 0) tn++;
            brierSum += (calibP - y) * (calibP - y);

            double targetMagnitude = double.IsFinite(s.Magnitude) ? s.Magnitude : 0.0;
            double absMag = Math.Max(0.001, Math.Abs(targetMagnitude));
            if (pred == actual)
                evWinSum += absMag;
            else
                evLossSum += absMag;

            double magPred;
            if (useAug)
            {
                magPred = predictMagnitudeAug(
                    s.Features, magAugWeights!, magAugBias,
                    featureCount, hiddenSize, inputWeights, inputBiases, featureSubsets);
            }
            else
            {
                magPred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    magPred += magWeights[j] * s.Features[j];
            }
            if (!double.IsFinite(magPred))
                magPred = 0.0;

            double magErr = magPred - targetMagnitude;
            magSse += magErr * magErr;

            returns[i] = (pred == 1 ? 1 : -1) * (s.Direction > 0 ? 1 : -1) * absMag;
        }

        int ne          = testSet.Count;
        double accuracy = (double)correct / ne;
        double brier    = brierSum / ne;
        double prec     = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double rec      = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1       = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
        double ev       = (evWinSum - evLossSum) / ne;
        double magRmse  = Math.Sqrt(magSse / ne);
        double sharpe   = ElmMathHelper.ComputeSharpe(returns, sharpeAnnualisationFactor);
        double wAcc     = accuracy;

        return new EvalMetrics(accuracy, prec, rec, f1, magRmse, ev, brier, wAcc, sharpe, tp, fp, fn, tn);
    }

    internal static float[] NormalisePositiveImportance(float[] importance, int featureCount)
    {
        if (featureCount <= 0)
            return [];

        var normalised = new float[featureCount];
        double sum = 0.0;
        int copyLen = Math.Min(featureCount, importance.Length);
        for (int i = 0; i < copyLen; i++)
        {
            float value = importance[i];
            if (!float.IsFinite(value) || value <= 0f)
                continue;

            normalised[i] = value;
            sum += value;
        }

        if (sum <= 1e-12)
            return normalised;

        float invSum = (float)(1.0 / sum);
        for (int i = 0; i < normalised.Length; i++)
            normalised[i] *= invSum;

        return normalised;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ECE (Expected Calibration Error)
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeEce(
        List<TrainingSample> samples,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb,
        int bins = 10)
    {
        if (samples.Count == 0 || bins <= 0) return 0;

        int[] binCounts = new int[bins];
        double[] binAcc = new double[bins];
        double[] binConf = new double[bins];

        foreach (var s in samples)
        {
            double p = ClampProbability(ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binCounts[bin]++;
            binAcc[bin]  += ToBinaryLabel(s.Direction);
            binConf[bin] += p;
        }

        double ece = 0;
        for (int b = 0; b < bins; b++)
        {
            if (binCounts[b] == 0) continue;
            double avgAcc  = binAcc[b] / binCounts[b];
            double avgConf = binConf[b] / binCounts[b];
            ece += Math.Abs(avgAcc - avgConf) * binCounts[b];
        }
        return ece / samples.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Brier Skill Score
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb)
    {
        if (testSet.Count == 0) return 0;

        double brierModel = 0, brierNaive = 0;
        int posSamples = testSet.Count(s => s.Direction > 0);
        double naiveP = (double)posSamples / testSet.Count;

        foreach (var s in testSet)
        {
            double p = ClampProbability(ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));
            double y = s.Direction > 0 ? 1.0 : 0.0;
            brierModel += (p - y) * (p - y);
            brierNaive += (naiveP - y) * (naiveP - y);
        }

        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Permutation feature importance
    // ═══════════════════════════════════════════════════════════════════════════

    internal static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], double, double, int, int, int[][]?, double[]?, double> ensembleCalibProb,
        CancellationToken ct)
    {
        if (testSet.Count == 0) return new float[featureCount];

        int effectiveFeatureCount = Math.Min(featureCount, testSet.Max(s => s.Features.Length));
        if (effectiveFeatureCount <= 0)
            return new float[featureCount];

        int baselineCorrect = 0;
        foreach (var s in testSet)
        {
            double p = ClampProbability(ensembleCalibProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets, null));
            if ((p >= 0.5 ? 1 : 0) == ToBinaryLabel(s.Direction)) baselineCorrect++;
        }
        double baselineAcc = (double)baselineCorrect / testSet.Count;

        var importance = new float[featureCount];
        Parallel.For(0, effectiveFeatureCount, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        }, f =>
        {
            var rng = new Random(f * 71);
            var shuffled = new float[testSet.Count];
            for (int i = 0; i < testSet.Count; i++)
                shuffled[i] = f < testSet[i].Features.Length ? testSet[i].Features[f] : 0f;
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int bufferLen = testSet.Max(s => s.Features.Length);
            var buffer = new float[bufferLen];

            int correct = 0;
            for (int i = 0; i < testSet.Count; i++)
            {
                var orig = testSet[i].Features;
                Array.Clear(buffer, 0, buffer.Length);
                Array.Copy(orig, buffer, orig.Length);
                buffer[f] = shuffled[i];

                double p = ClampProbability(ensembleCalibProb(
                    buffer, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets, null));
                if ((p >= 0.5 ? 1 : 0) == ToBinaryLabel(testSet[i].Direction)) correct++;
            }
            double permAcc = (double)correct / testSet.Count;
            importance[f] = (float)(baselineAcc - permAcc);
        });

        return importance;
    }

    internal static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double> ensembleRawProb,
        CancellationToken ct)
    {
        if (calSet.Count == 0) return new double[featureCount];

        int effectiveFeatureCount = Math.Min(featureCount, calSet.Max(s => s.Features.Length));
        if (effectiveFeatureCount <= 0)
            return new double[featureCount];

        int baselineCorrect = 0;
        foreach (var s in calSet)
        {
            double p = ClampProbability(ensembleRawProb(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, null));
            if ((p >= 0.5 ? 1 : 0) == ToBinaryLabel(s.Direction)) baselineCorrect++;
        }
        double baselineAcc = (double)baselineCorrect / calSet.Count;

        var importance = new double[featureCount];
        Parallel.For(0, effectiveFeatureCount, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        }, f =>
        {
            var rng = new Random(f * 71);
            var shuffled = new float[calSet.Count];
            for (int i = 0; i < calSet.Count; i++)
                shuffled[i] = f < calSet[i].Features.Length ? calSet[i].Features[f] : 0f;
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int bufferLen = calSet.Max(s => s.Features.Length);
            var buffer = new float[bufferLen];

            int correct = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                var orig = calSet[i].Features;
                Array.Clear(buffer, 0, buffer.Length);
                Array.Copy(orig, buffer, orig.Length);
                buffer[f] = shuffled[i];
                double p = ClampProbability(ensembleRawProb(
                    buffer, weights, biases, inputWeights, inputBiases,
                    featureCount, hiddenSize, featureSubsets, null));
                if ((p >= 0.5 ? 1 : 0) == ToBinaryLabel(calSet[i].Direction)) correct++;
            }
            importance[f] = baselineAcc - (double)correct / calSet.Count;
        });

        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Durbin-Watson
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int featureCount,
        double[]? magAugWeights, double magAugBias,
        int hiddenSize, double[][]? elmInputWeights,
        double[][]? elmInputBiases, int[][]? featureSubsets,
        Func<float[], double[], double, int, int, double[][], double[][], int[][]?, double> predictMagnitudeAug)
    {
        if (train.Count < 3) return 2.0;

        bool useAug = magAugWeights is not null && elmInputWeights is not null
                      && elmInputBiases is not null
                      && magAugWeights.Length == featureCount + hiddenSize;

        double[] residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred;
            if (useAug)
            {
                pred = predictMagnitudeAug(
                    train[i].Features, magAugWeights!, magAugBias,
                    featureCount, hiddenSize, elmInputWeights!, elmInputBiases!, featureSubsets);
            }
            else
            {
                pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, train[i].Features.Length); j++)
                    pred += magWeights[j] * train[i].Features[j];
            }
            if (!double.IsFinite(pred))
                pred = 0.0;

            double targetMagnitude = double.IsFinite(train[i].Magnitude) ? train[i].Magnitude : 0.0;
            double residual = targetMagnitude - pred;
            residuals[i] = double.IsFinite(residual) ? residual : 0.0;
        }

        double sumDiffSq = 0, sumSq = 0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double d = residuals[i] - residuals[i - 1];
            sumDiffSq += d * d;
        }
        for (int i = 0; i < residuals.Length; i++)
            sumSq += residuals[i] * residuals[i];

        return sumSq > 1e-15 ? sumDiffSq / sumSq : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ensemble diversity
    // ═══════════════════════════════════════════════════════════════════════════

    internal static double ComputeEnsembleDiversity(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        Func<float[], double[], double, double[], double[], int, int, int[]?, int, double> elmLearnerProb)
    {
        int K = Math.Min(
            weights.Length,
            Math.Min(biases.Length, Math.Min(inputWeights.Length, inputBiases.Length)));
        if (K < 2 || calSet.Count == 0) return 0;

        long totalDisagreePairs = 0;
        long totalPossiblePairs = 0;

        for (int i = 0; i < calSet.Count; i++)
        {
            int positiveCount = 0;
            int activeLearners = 0;
            for (int k = 0; k < K; k++)
            {
                if (weights[k] is not { Length: > 0 } ||
                    inputWeights[k] is not { Length: > 0 } ||
                    inputBiases[k] is not { Length: > 0 })
                {
                    continue;
                }

                double p = ClampProbability(elmLearnerProb(
                    calSet[i].Features, weights[k], biases[k],
                    inputWeights[k], inputBiases[k],
                    featureCount, hiddenSize,
                    featureSubsets is not null && k < featureSubsets.Length && featureSubsets[k] is { Length: > 0 }
                        ? featureSubsets[k]
                        : null,
                    k));
                activeLearners++;
                if (p >= 0.5) positiveCount++;
            }

            if (activeLearners < 2)
                continue;

            totalDisagreePairs += (long)positiveCount * (activeLearners - positiveCount);
            totalPossiblePairs += (long)activeLearners * (activeLearners - 1) / 2;
        }

        double totalPossible = totalPossiblePairs;
        return totalPossible > 0 ? totalDisagreePairs / totalPossible : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MI redundancy
    // ═══════════════════════════════════════════════════════════════════════════

    internal static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> train, int featureCount, double threshold)
    {
        if (train.Count < 20 || featureCount < 2) return [];

        int effectiveFeatureCount = Math.Min(featureCount, train.Max(s => s.Features.Length));
        if (effectiveFeatureCount < 2)
            return [];

        int topN = Math.Min(10, effectiveFeatureCount);
        double[] variances = new double[effectiveFeatureCount];
        for (int j = 0; j < effectiveFeatureCount; j++)
        {
            double sum = 0, sumSq = 0;
            foreach (var s in train)
            {
                double v = j < s.Features.Length ? s.Features[j] : 0.0;
                if (!double.IsFinite(v))
                    v = 0.0;
                sum += v; sumSq += v * v;
            }
            double mean = sum / train.Count;
            variances[j] = sumSq / train.Count - mean * mean;
        }

        var topIdx = variances
            .Select((v, i) => (v, i))
            .OrderByDescending(x => x.v)
            .Take(topN)
            .Select(x => x.i)
            .ToArray();

        var pairs = new List<string>();
        const int miBins = 10;

        for (int a = 0; a < topIdx.Length; a++)
        {
            for (int b = a + 1; b < topIdx.Length; b++)
            {
                int fi = topIdx[a], fj = topIdx[b];
                double mi = ComputeBinnedMutualInformation(train, fi, fj, miBins);
                if (mi > threshold * Math.Log(2))
                {
                    string nameI = fi < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[fi] : $"F{fi}";
                    string nameJ = fj < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[fj] : $"F{fj}";
                    pairs.Add($"{nameI}↔{nameJ}({mi:F3})");
                }
            }
        }

        return pairs.ToArray();
    }

    /// <summary>
    /// Returns redundant feature pairs as (indexI, indexJ) tuples for programmatic use
    /// (e.g. pruning the less-important feature from each collinear pair).
    /// </summary>
    internal static (int IndexI, int IndexJ)[] ComputeRedundantFeaturePairIndices(
        List<TrainingSample> train, int featureCount, double threshold)
    {
        if (train.Count < 20 || featureCount < 2) return [];

        int effectiveFeatureCount = Math.Min(featureCount, train.Max(s => s.Features.Length));
        if (effectiveFeatureCount < 2)
            return [];

        int topN = Math.Min(10, effectiveFeatureCount);
        double[] variances = new double[effectiveFeatureCount];
        for (int j = 0; j < effectiveFeatureCount; j++)
        {
            double sum = 0, sumSq = 0;
            foreach (var s in train)
            {
                double v = j < s.Features.Length ? s.Features[j] : 0.0;
                if (!double.IsFinite(v))
                    v = 0.0;
                sum += v; sumSq += v * v;
            }
            double mean = sum / train.Count;
            variances[j] = sumSq / train.Count - mean * mean;
        }

        var topIdx = variances
            .Select((v, i) => (v, i))
            .OrderByDescending(x => x.v)
            .Take(topN)
            .Select(x => x.i)
            .ToArray();

        var result = new List<(int, int)>();
        const int miBins = 10;

        for (int a = 0; a < topIdx.Length; a++)
        {
            for (int b = a + 1; b < topIdx.Length; b++)
            {
                int fi = topIdx[a], fj = topIdx[b];
                double mi = ComputeBinnedMutualInformation(train, fi, fj, miBins);
                if (mi > threshold * Math.Log(2))
                    result.Add((fi, fj));
            }
        }

        return result.ToArray();
    }

    private static double ComputeBinnedMutualInformation(
        List<TrainingSample> samples, int featureI, int featureJ, int bins)
    {
        int n = samples.Count;
        if (n < bins * 2) return 0;

        int[] rankI = ComputeRankBins(samples, featureI, bins);
        int[] rankJ = ComputeRankBins(samples, featureJ, bins);

        int[,] joint = new int[bins, bins];
        int[] margI = new int[bins], margJ = new int[bins];
        for (int s = 0; s < n; s++)
        {
            int bi = rankI[s], bj = rankJ[s];
            joint[bi, bj]++;
            margI[bi]++;
            margJ[bj]++;
        }

        double mi = 0;
        for (int i = 0; i < bins; i++)
        {
            if (margI[i] == 0) continue;
            for (int j = 0; j < bins; j++)
            {
                if (joint[i, j] == 0 || margJ[j] == 0) continue;
                double pij = (double)joint[i, j] / n;
                double pi  = (double)margI[i] / n;
                double pj  = (double)margJ[j] / n;
                mi += pij * Math.Log(pij / (pi * pj));
            }
        }

        return Math.Max(0, mi);
    }

    private static int[] ComputeRankBins(List<TrainingSample> samples, int featureIdx, int bins)
    {
        int n = samples.Count;
        var indexed = new (float Value, int Idx)[n];
        for (int i = 0; i < n; i++)
        {
            float value = featureIdx < samples[i].Features.Length ? samples[i].Features[featureIdx] : 0f;
            indexed[i] = (float.IsFinite(value) ? value : 0f, i);
        }
        Array.Sort(indexed, (a, b) => a.Value.CompareTo(b.Value));

        int[] result = new int[n];
        for (int rank = 0; rank < n; rank++)
        {
            int bin = Math.Min((int)((long)rank * bins / n), bins - 1);
            result[indexed[rank].Idx] = bin;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stationarity check
    // ═══════════════════════════════════════════════════════════════════════════

    internal static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        if (samples.Count < 20 || featureCount <= 0)
            return 0;

        int effectiveFeatureCount = Math.Min(featureCount, samples.Max(s => s.Features.Length));
        if (effectiveFeatureCount <= 0)
            return 0;

        int nonStat = 0;
        for (int j = 0; j < effectiveFeatureCount; j++)
        {
            var series = new double[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                double value = j < samples[i].Features.Length ? samples[i].Features[j] : 0.0;
                series[i] = double.IsFinite(value) ? value : 0.0;
            }
            double pValue = MLFeatureHelper.AdfTest(series, maxLags: 4);
            if (pValue > 0.05) nonStat++;
        }
        return nonStat;
    }

    private static double ClampProbability(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 0.0, 1.0);
    }
}
