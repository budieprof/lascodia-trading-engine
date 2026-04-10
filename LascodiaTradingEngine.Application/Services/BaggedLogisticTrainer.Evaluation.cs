using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services;

public sealed partial class BaggedLogisticTrainer
{

    private static EvalMetrics EvaluateEnsemble(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double sumMagSqErr = 0, sumBrier = 0, sumEV = 0;

        // Use ArrayPool to avoid a heap allocation proportional to testSet.Count.
        int    n           = testSet.Count;
        double[] retBuf    = ArrayPool<double>.Shared.Rent(n);
        int      retCount  = 0;
        try
        {
            foreach (var s in testSet)
            {
                double rawProb   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                double calibP    = ApplyGlobalPlattCalibration(rawProb, plattA, plattB);
                bool predictedUp = calibP >= 0.5;
                bool actualUp    = s.Direction == 1;
                bool correct     = predictedUp == actualUp;

                double y = actualUp ? 1.0 : 0.0;
                sumBrier += (calibP - y) * (calibP - y);

                double magPred = PredictMagnitude(s.Features, magWeights, magBias);
                double magErr  = magPred - s.Magnitude;
                sumMagSqErr += magErr * magErr;

                double edge = calibP - 0.5;
                sumEV += (correct ? 1 : -1) * Math.Abs(edge) * Math.Abs(s.Magnitude);

                int predDir = predictedUp ? 1 : -1;
                int actDir  = actualUp    ? 1 : -1;
                retBuf[retCount++] = predDir * actDir * Math.Abs(s.Magnitude);

                if (correct && predictedUp)        tp++;
                else if (!correct && predictedUp)  fp++;
                else if (!correct && !predictedUp) fn++;
                else                               tn++;
            }

            double accuracy  = (tp + tn) / (double)n;
            double precision = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0;
            double recall    = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0;
            double f1        = (precision + recall) > 0
                               ? 2 * precision * recall / (precision + recall) : 0;
            double wAcc      = WeightedAccuracy(testSet, weights, biases, plattA, plattB, featureCount, subsets, meta, mlp);

            return new EvalMetrics(
                Accuracy:         accuracy,
                Precision:        precision,
                Recall:           recall,
                F1:               f1,
                MagnitudeRmse:    Math.Sqrt(sumMagSqErr / n),
                ExpectedValue:    sumEV / n,
                BrierScore:       sumBrier / n,
                WeightedAccuracy: wAcc,
                SharpeRatio:      ComputeSharpe(retBuf, retCount),
                TP: tp, FP: fp, FN: fn, TN: tn);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(retBuf);
        }
    }

    private static EvalMetrics EvaluateEnsemble(
        List<TrainingSample>  testSet,
        double[]              magWeights,
        double                magBias,
        Func<float[], double> calibratedProb,
        double                decisionThreshold = 0.5)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double sumMagSqErr = 0, sumBrier = 0, sumEV = 0;

        int n = testSet.Count;
        double[] retBuf = ArrayPool<double>.Shared.Rent(n);
        int retCount = 0;
        try
        {
            foreach (var s in testSet)
            {
                double calibP = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
                bool predictedUp = calibP >= decisionThreshold;
                bool actualUp = s.Direction == 1;
                bool correct = predictedUp == actualUp;

                double y = actualUp ? 1.0 : 0.0;
                sumBrier += (calibP - y) * (calibP - y);

                double magPred = PredictMagnitude(s.Features, magWeights, magBias);
                double magErr = magPred - s.Magnitude;
                sumMagSqErr += magErr * magErr;

                double edge = calibP - decisionThreshold;
                sumEV += (correct ? 1 : -1) * Math.Abs(edge) * Math.Abs(s.Magnitude);

                int predDir = predictedUp ? 1 : -1;
                int actDir = actualUp ? 1 : -1;
                retBuf[retCount++] = predDir * actDir * Math.Abs(s.Magnitude);

                if (correct && predictedUp) tp++;
                else if (!correct && predictedUp) fp++;
                else if (!correct && !predictedUp) fn++;
                else tn++;
            }

            double accuracy = (tp + tn) / (double)n;
            double precision = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0;
            double recall = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0;
            double f1 = (precision + recall) > 0
                ? 2 * precision * recall / (precision + recall)
                : 0;
            double weightedAccuracy = ComputeWeightedAccuracy(testSet, calibratedProb, decisionThreshold);

            return new EvalMetrics(
                Accuracy:         accuracy,
                Precision:        precision,
                Recall:           recall,
                F1:               f1,
                MagnitudeRmse:    Math.Sqrt(sumMagSqErr / n),
                ExpectedValue:    sumEV / n,
                BrierScore:       sumBrier / n,
                WeightedAccuracy: weightedAccuracy,
                SharpeRatio:      ComputeSharpe(retBuf, retCount),
                TP: tp, FP: fp, FN: fn, TN: tn);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(retBuf);
        }
    }

    private static PolicyEvaluation EvaluateSelectivePolicy(
        List<TrainingSample>                  testSet,
        double[]                              magWeights,
        double                                magBias,
        Func<float[], (double Probability, double EnsembleStd)> probabilityAndStdProvider,
        double                                decisionThreshold = 0.5,
        double[]?                             metaLabelWeights = null,
        double                                metaLabelBias = 0.0,
        double                                metaLabelThreshold = 0.5,
        int[]?                                metaLabelTopFeatureIndices = null,
        double[]?                             abstentionWeights = null,
        double                                abstentionBias = 0.0,
        double                                abstentionThreshold = 0.5,
        double                                abstentionThresholdBuy = 0.0,
        double                                abstentionThresholdSell = 0.0)
    {
        if (testSet.Count == 0)
            return new PolicyEvaluation(
                new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                []);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        int correctPredictions = 0;
        double sumMagSqErr = 0.0, sumBrier = 0.0, sumEV = 0.0;
        double weightedCorrect = 0.0, weightedTotal = 0.0;

        int n = testSet.Count;
        double[] retBuf = ArrayPool<double>.Shared.Rent(n);
        var predictions = new (int Predicted, int Actual)[n];
        int retCount = 0;
        try
        {
            for (int i = 0; i < n; i++)
            {
                var s = testSet[i];
                var (calibP, ensembleStd) = probabilityAndStdProvider(s.Features);
                calibP = Math.Clamp(calibP, 0.0, 1.0);

                bool actualUp = s.Direction == 1;
                int actualDir = actualUp ? 1 : -1;
                double y = actualUp ? 1.0 : 0.0;
                sumBrier += (calibP - y) * (calibP - y);

                double magPred = PredictMagnitude(s.Features, magWeights, magBias);
                double magErr = magPred - s.Magnitude;
                sumMagSqErr += magErr * magErr;

                double wt = 1.0 + (double)i / n;
                weightedTotal += wt;

                bool predictedUp = calibP >= decisionThreshold;
                decimal? metaLabelScore = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
                    calibP,
                    ensembleStd,
                    s.Features,
                    s.Features.Length,
                    metaLabelWeights ?? [],
                    metaLabelBias,
                    metaLabelTopFeatureIndices);
                decimal? abstentionScore = ScoringEnrichmentCalculator.ComputeAbstentionScore(
                    calibP,
                    ensembleStd,
                    metaLabelScore,
                    null,
                    ScoringEnrichmentCalculator.ComputeEntropy(calibP),
                    decisionThreshold,
                    abstentionWeights ?? [],
                    abstentionBias);
                var (_, suppressed) = ScoringEnrichmentCalculator.ComputeSelectiveSuppression(
                    predictedUp,
                    metaLabelScore,
                    metaLabelWeights?.Length ?? 0,
                    metaLabelThreshold,
                    abstentionScore,
                    abstentionWeights?.Length ?? 0,
                    abstentionThreshold,
                    abstentionThresholdBuy,
                    abstentionThresholdSell);

                if (suppressed)
                {
                    predictions[i] = (0, actualDir);
                    if (actualUp) fn++;
                    retBuf[retCount++] = 0.0;
                    continue;
                }

                bool correct = predictedUp == actualUp;
                if (correct)
                {
                    correctPredictions++;
                    weightedCorrect += wt;
                }

                double edge = calibP - decisionThreshold;
                sumEV += (correct ? 1 : -1) * Math.Abs(edge) * Math.Abs(s.Magnitude);

                int predDir = predictedUp ? 1 : -1;
                predictions[i] = (predDir, actualDir);
                retBuf[retCount++] = predDir * actualDir * Math.Abs(s.Magnitude);

                if (predictedUp)
                {
                    if (correct) tp++;
                    else fp++;
                }
                else
                {
                    if (correct) tn++;
                    else fn++;
                }
            }

            double accuracy = correctPredictions / (double)n;
            double precision = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0.0;
            double recall = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0.0;
            double f1 = (precision + recall) > 0
                ? 2 * precision * recall / (precision + recall)
                : 0.0;

            return new PolicyEvaluation(
                new EvalMetrics(
                    Accuracy: accuracy,
                    Precision: precision,
                    Recall: recall,
                    F1: f1,
                    MagnitudeRmse: Math.Sqrt(sumMagSqErr / n),
                    ExpectedValue: sumEV / n,
                    BrierScore: sumBrier / n,
                    WeightedAccuracy: weightedTotal > 0.0 ? weightedCorrect / weightedTotal : 0.0,
                    SharpeRatio: ComputeSharpe(retBuf, retCount),
                    TP: tp, FP: fp, FN: fn, TN: tn),
                predictions);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(retBuf);
        }
    }

    // ── ECE (Expected Calibration Error) ──────────────────────────────────────

    /// <summary>
    /// Measures how well the calibrated probability outputs match actual positive-class
    /// frequencies. Uses 10 equal-width bins over [0, 1].
    /// ECE = Σ_b |freq_positive(b) − avg_conf(b)| × n_b / n.
    /// </summary>
    internal static double ComputeEce(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count < 20) return 0.5;

        const int NumBins = 10;
        var binConfSum   = new double[NumBins];
        var binPositive  = new int[NumBins];
        var binCount     = new int[NumBins];

        foreach (var s in testSet)
        {
            double raw  = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double p    = ApplyGlobalPlattCalibration(raw, plattA, plattB);
            int    bin  = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);

            binConfSum[bin] += p;
            if (s.Direction == 1) binPositive[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf    = binConfSum[b] / binCount[b];
            double posFreq    = binPositive[b] / (double)binCount[b];
            ece += Math.Abs(posFreq - avgConf) * binCount[b] / n;
        }

        return ece;
    }

    private static double ComputeProductionEce(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double               plattABuy,
        double               plattBBuy,
        double               plattASell,
        double               plattBSell,
        double[]             isotonicBreakpoints,
        double               ageDecayLambda,
        DateTime             trainedAtUtc,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count < 20) return 0.5;

        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binPositive = new int[NumBins];
        var binCount = new int[NumBins];

        foreach (var s in testSet)
        {
            double raw = EnsembleProb(
                s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double p = ApplyProductionCalibration(
                raw,
                plattA,
                plattB,
                temperatureScale,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBreakpoints,
                ageDecayLambda,
                trainedAtUtc);
            int bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);

            binConfSum[bin] += p;
            if (s.Direction == 1) binPositive[bin]++;
            binCount[bin]++;
        }

        double ece = 0.0;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double posFreq = binPositive[b] / (double)binCount[b];
            ece += Math.Abs(posFreq - avgConf) * binCount[b] / testSet.Count;
        }

        return ece;
    }

    private static double ComputeProductionEce(
        List<TrainingSample>  testSet,
        Func<float[], double> calibratedProb)
    {
        if (testSet.Count < 20) return 0.5;

        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binPositive = new int[NumBins];
        var binCount = new int[NumBins];

        foreach (var s in testSet)
        {
            double p = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
            int bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);

            binConfSum[bin] += p;
            if (s.Direction == 1) binPositive[bin]++;
            binCount[bin]++;
        }

        double ece = 0.0;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double posFreq = binPositive[b] / (double)binCount[b];
            ece += Math.Abs(posFreq - avgConf) * binCount[b] / testSet.Count;
        }

        return ece;
    }

    /// <summary>Log-loss for a single learner using only the specified feature subset.
    /// Pass <paramref name="baseFeatureCount"/> >= 0 to enable poly feature augmentation.
    /// When <paramref name="mlpHiddenW"/> is non-null, uses MLP forward pass.</summary>
    internal static double ComputeLogLossSubset(
        List<TrainingSample> set,
        double[]             w,
        double               b,
        int[]                subset,
        double               labelSmoothing   = 0.0,
        int                  baseFeatureCount = -1,
        int                  polyTopN         = 5,
        double[]?            mlpHiddenW       = null,
        double[]?            mlpHiddenB       = null,
        int                  mlpHiddenDim     = 0)
    {
        if (set.Count == 0) return double.MaxValue;
        bool augment = baseFeatureCount >= 0;
        bool useMlp  = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        int  subsetLen = subset.Length;
        double loss = 0;
        foreach (var s in set)
        {
            float[] features = augment
                ? AugmentWithPolyFeatures(s.Features, baseFeatureCount, polyTopN)
                : s.Features;
            double z;
            if (useMlp)
            {
                z = b;
                int hiddenUnits = GetUsableHiddenUnitCount(mlpHiddenDim, w, mlpHiddenB!);
                for (int h = 0; h < hiddenUnits; h++)
                {
                    double act = mlpHiddenB![h];
                    int rowOff = h * subsetLen;
                    for (int si = 0; si < subsetLen && rowOff + si < mlpHiddenW!.Length; si++)
                    {
                        if (TryGetFeatureValue(features, subset[si], out double featureValue))
                            act += mlpHiddenW[rowOff + si] * featureValue;
                    }
                    double hidden = Math.Max(0.0, act); // ReLU
                    z += w[h] * hidden;
                }
            }
            else
            {
                z = b;
                foreach (int j in subset)
                    if ((uint)j < (uint)w.Length && (uint)j < (uint)features.Length)
                        z += w[j] * features[j];
            }
            double p = MLFeatureHelper.Sigmoid(z);
            double y = s.Direction > 0 ? 1.0 - labelSmoothing : labelSmoothing;
            loss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
        }
        return loss / set.Count;
    }

    private static double WeightedAccuracy(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        int n = testSet.Count;
        if (n == 0) return 0;
        double weightSum = 0, correctSum = 0;
        for (int i = 0; i < n; i++)
        {
            double wt     = 1.0 + (double)i / n;
            var    s      = testSet[i];
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double calibP = ApplyGlobalPlattCalibration(rawP, plattA, plattB);
            weightSum  += wt;
            correctSum += (calibP >= 0.5) == (s.Direction == 1) ? wt : 0;
        }
        return correctSum / weightSum;
    }

    private static double ComputeWeightedAccuracy(
        List<TrainingSample>  testSet,
        Func<float[], double> calibratedProb,
        double                decisionThreshold = 0.5)
    {
        int n = testSet.Count;
        if (n == 0) return 0.0;

        double weightSum = 0.0, correctSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double wt = 1.0 + (double)i / n;
            double calibP = Math.Clamp(calibratedProb(testSet[i].Features), 0.0, 1.0);
            weightSum += wt;
            correctSum += IsPredictionCorrect(calibP, testSet[i].Direction, decisionThreshold) ? wt : 0.0;
        }

        return correctSum / weightSum;
    }

    private static double StdDev(IEnumerable<double> values, double mean)
    {
        var list = values as IList<double> ?? [..values];
        if (list.Count < 2) return 0;
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }

    // ── Permutation importance ────────────────────────────────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default,
        double               decisionThreshold = 0.5,
        CancellationToken    ct   = default)
    {
        double baseline = ComputeAccuracy(
            testSet, weights, biases, plattA, plattB, featureCount, subsets, meta, mlp, decisionThreshold);
        var    importance = new float[featureCount];

        // Each feature's shuffle-and-evaluate is independent — run in parallel.
        // Each feature gets its own seeded Random so results are deterministic.
        int tn = testSet.Count;
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 13 + 42);
            // Plain loop to extract column — avoids LINQ Select+ToArray.
            var vals = new float[tn];
            for (int i = 0; i < tn; i++) vals[i] = testSet[i].Features[j];
            for (int i = tn - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            // Score using thread-local scratch buffer — avoids cloning full feature array per sample.
            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < tn; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j]   = vals[idx];
                double rawP   = EnsembleProb(scratch, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                double calibP = ApplyGlobalPlattCalibration(rawP, plattA, plattB);
                if (IsPredictionCorrect(calibP, testSet[idx].Direction, decisionThreshold)) correct++;
            }
            double shuffledAcc = (double)correct / tn;
            importance[j] = (float)Math.Max(0, baseline - shuffledAcc);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    private static float[] ComputePermutationImportance(
        List<TrainingSample>  testSet,
        Func<float[], double> calibratedProb,
        int                   featureCount,
        double                decisionThreshold = 0.5,
        CancellationToken     ct = default)
    {
        if (testSet.Count == 0 || featureCount == 0) return new float[featureCount];

        double baseline = ComputeAccuracy(testSet, calibratedProb, decisionThreshold);
        var importance = new float[featureCount];
        int sampleCount = testSet.Count;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 13 + 42);
            var vals = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++) vals[i] = testSet[i].Features[j];
            for (int i = sampleCount - 1; i > 0; i--)
            {
                int swapIndex = localRng.Next(i + 1);
                (vals[swapIndex], vals[i]) = (vals[i], vals[swapIndex]);
            }

            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < sampleCount; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if (IsPredictionCorrect(Math.Clamp(calibratedProb(scratch), 0.0, 1.0), testSet[idx].Direction, decisionThreshold))
                    correct++;
            }

            double shuffledAcc = (double)correct / sampleCount;
            importance[j] = (float)Math.Max(0.0, baseline - shuffledAcc);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    private static double ComputeAccuracy(
        List<TrainingSample> set,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default,
        double               decisionThreshold = 0.5)
    {
        if (set.Count == 0) return 0;
        int correct = 0;
        foreach (var s in set)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double calibP = ApplyGlobalPlattCalibration(rawP, plattA, plattB);
            if (IsPredictionCorrect(calibP, s.Direction, decisionThreshold)) correct++;
        }
        return (double)correct / set.Count;
    }

    private static double ComputeAccuracy(
        List<TrainingSample>  set,
        Func<float[], double> calibratedProb,
        double                decisionThreshold = 0.5)
    {
        if (set.Count == 0) return 0.0;

        int correct = 0;
        foreach (var s in set)
        {
            double prob = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
            if (IsPredictionCorrect(prob, s.Direction, decisionThreshold))
                correct++;
        }

        return (double)correct / set.Count;
    }

    // ── Split conformal prediction ─────────────────────────────────────────────

    /// <summary>
    /// Computes the split-conformal quantile <c>qHat</c> at coverage level 1−α (default 90%).
    /// Nonconformity score: <c>1−p</c> for Buy labels, <c>p</c> for Sell labels,
    /// where <c>p</c> is the Platt+isotonic calibrated probability.
    /// qHat = empirical ⌈(n+1)(1−α)⌉/n quantile.
    /// </summary>
    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta  = default,
        MlpState             mlp   = default,
        double               alpha = 0.10)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double raw = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double p   = ApplyGlobalPlattCalibration(raw, plattA, plattB);
            if (isotonicBp.Length >= 4)
                p = ApplyIsotonicCalibration(p, isotonicBp);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }

        scores.Sort();
        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    private static double ComputeConformalQHat(
        List<TrainingSample>  calSet,
        Func<float[], double> calibratedProb,
        double                alpha = 0.10)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double p = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }

        scores.Sort();
        int n = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ── Stationarity gate helper ───────────────────────────────────────────────

    /// <summary>
    /// Counts how many features (columns) fail the ADF stationarity test at p > 0.05.
    /// </summary>
    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        int nonStationary = 0;
        int ns = samples.Count;
        var values = new double[ns]; // reuse buffer across features
        for (int j = 0; j < featureCount; j++)
        {
            for (int i = 0; i < ns; i++) values[i] = samples[i].Features[j];
            double pValue = MLFeatureHelper.AdfTest(values, maxLags: 4);
            if (pValue > 0.05) nonStationary++;
        }
        return nonStationary;
    }

    // ── Equity-curve gate helper ───────────────────────────────────────────────

    /// <summary>
    /// Computes max peak-to-trough drawdown and Sharpe ratio from a sequence of unit trade P&amp;L.
    /// Each prediction contributes +1 (correct) or -1 (incorrect) to the running P&amp;L.
    /// </summary>
    internal static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0.0, 0.0);

        var returns = new double[predictions.Length];
        // Anchor the curve at 1.0 so an immediate losing streak still registers
        // as drawdown instead of being masked by the old peak==0 shortcut.
        double equity = 1.0;
        double peak   = 1.0;
        double maxDD  = 0.0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == 0
                ? 0.0
                : predictions[i].Predicted == predictions[i].Actual ? +1.0 : -1.0;
            returns[i] = ret;
            equity    += ret;
            if (equity > peak) peak = equity;
            double dd = (peak - equity) / Math.Max(peak, 1e-12);
            if (dd > maxDD) maxDD = dd;
        }

        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean));
        double std = returns.Length > 1 ? Math.Sqrt(variance / (returns.Length - 1)) : 0.0;
        double sharpe = std < 1e-10 ? 0.0 : mean / std;

        return (maxDD, sharpe);
    }

    // ── Jackknife+ residuals ───────────────────────────────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals: r_i = |trueLabel - oobP| for each training sample.
    /// Reuses the same bootstrap membership logic as OOB accuracy.
    /// Returns residuals sorted in ascending order.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             featureSubsets,
        int                  K,
        MetaLearner          meta = default,
        double[]?            gesWeights = null,
        double[]?            learnerAccuracyWeights = null,
        double[]?            learnerCalAccuracies = null,
        Func<double, double>? probabilityTransform = null,
        bool[]?              activeLearners = null,
        MlpState             mlp = default)
    {
        if (trainSet.Count < 20) return [];

        var inSets = new HashSet<int>[K];
        for (int k = 0; k < K; k++)
            inSets[k] = GenerateBootstrapInSet(
                trainSet, temporalWeights, trainSet.Count, seed: k * 31 + 7);

        var residuals = new List<double>(trainSet.Count);
        var availableLearners = new List<int>(K);

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Use GetLearnerProbs to handle both linear and MLP forward passes
            var lp = GetLearnerProbs(trainSet[i].Features, weights, biases, featureCount, featureSubsets,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

            availableLearners.Clear();

            for (int k = 0; k < K; k++)
            {
                if (activeLearners is not null && (k >= activeLearners.Length || !activeLearners[k]))
                    continue;
                if (inSets[k].Contains(i)) continue;
                availableLearners.Add(k);
            }

            if (availableLearners.Count == 0) continue;

            double oobP = AggregateSelectedLearnerProbs(
                lp, availableLearners, meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies);
            if (probabilityTransform is not null)
                oobP = probabilityTransform(oobP);
            double trueLabel = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - oobP));
        }

        residuals.Sort();
        return [..residuals];
    }

    // ── Label noise correction ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a soft downweight factor in [0, 1] for a training sample's gradient.
    /// When <paramref name="ensP"/> indicates the ensemble is very confident that the label
    /// is wrong (P(correct) &lt; threshold), the gradient is scaled down proportionally.
    /// Returns 1.0 (no downweight) when threshold is 0 or P(correct) >= threshold.
    /// </summary>
    private static double ComputeNoiseCorrectionWeight(double ensP, int label, double threshold)
    {
        if (threshold <= 0.0) return 1.0;

        // P(correct label): for label=1 (Buy), P(correct) = ensP; for label=0 (Sell), P(correct) = 1-ensP
        double pCorrect = label == 1 ? ensP : 1.0 - ensP;
        if (pCorrect >= threshold) return 1.0;

        // Soft downweight: gradient × (P(correct) / threshold)
        return pCorrect / threshold;
    }

    // ── Pearson correlation between learner weight vectors ────────────────────

    /// <summary>
    /// Computes the Pearson correlation coefficient between two weight arrays
    /// using only the first <paramref name="len"/> elements.
    /// Returns 0.0 when either array has zero variance.
    /// </summary>
    private static double PearsonCorrelation(double[] a, double[] b, int len)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), len);
        if (n < 2) return 0.0;

        double sumA = 0, sumB = 0;
        for (int i = 0; i < n; i++) { sumA += a[i]; sumB += b[i]; }
        double meanA = sumA / n, meanB = sumB / n;

        double cov = 0, varA = 0, varB = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            cov  += da * db;
            varA += da * da;
            varB += db * db;
        }

        double denom = Math.Sqrt(varA * varB);
        return denom < 1e-15 ? 0.0 : cov / denom;
    }

    // ── Calibration-set permutation importance (double[] for warm-start transfer) ──

    /// <summary>
    /// Computes permutation importance on the calibration set using raw ensemble accuracy
    /// (no Platt scaling — intentional, so this is pure weight-space importance independent
    /// of the calibration transform).
    /// Returns importances normalised to sum to 1.0. Empty when cal set is too small.
    /// </summary>
    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default,
        CancellationToken    ct  = default)
    {
        if (calSet.Count < 10 || featureCount == 0) return new double[featureCount];

        // Baseline accuracy: raw ensemble (no Platt)
        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double p = EnsembleProb(s.Features, weights, biases, featureCount, subsets, default, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            if ((p >= 0.5) == (s.Direction == 1)) baseCorrect++;
        }
        double baselineAcc = (double)baseCorrect / calSet.Count;

        // Pre-extract feature columns once so parallel workers don't iterate calSet per column.
        int m = calSet.Count;
        var featureCols = new float[featureCount][];
        for (int j = 0; j < featureCount; j++)
        {
            var col = new float[m];
            for (int i = 0; i < m; i++) col[i] = calSet[i].Features[j];
            featureCols[j] = col;
        }

        var importance = new double[featureCount];

        // Each feature's shuffle is independent — run in parallel with per-feature seeded RNG.
        // Scoring avoids float[] clone by patching a thread-local copy once per feature.
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 17 + 99);
            var vals     = (float[])featureCols[j].Clone(); // one clone per feature, not per sample
            for (int i = vals.Length - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            // Score without cloning the full feature array: use a thread-local scratch buffer.
            var scratch = new float[calSet[0].Features.Length];
            int shuffledCorrect = 0;
            for (int idx = 0; idx < m; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p = EnsembleProb(scratch, weights, biases, featureCount, subsets, default, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                if ((p >= 0.5) == (calSet[idx].Direction == 1)) shuffledCorrect++;
            }
            double shuffledAcc = (double)shuffledCorrect / m;
            importance[j] = Math.Max(0.0, baselineAcc - shuffledAcc);
        });

        // Normalise to sum to 1
        double total = importance.Sum();
        if (total > 1e-10)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample>  calSet,
        Func<float[], double> rawProbProvider,
        int                   featureCount,
        CancellationToken     ct = default)
    {
        if (calSet.Count < 10 || featureCount == 0) return new double[featureCount];

        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double p = Math.Clamp(rawProbProvider(s.Features), 0.0, 1.0);
            if ((p >= 0.5) == (s.Direction == 1)) baseCorrect++;
        }
        double baselineAcc = (double)baseCorrect / calSet.Count;

        int sampleCount = calSet.Count;
        var featureCols = new float[featureCount][];
        for (int j = 0; j < featureCount; j++)
        {
            var col = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++) col[i] = calSet[i].Features[j];
            featureCols[j] = col;
        }

        var importance = new double[featureCount];
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 17 + 99);
            var vals = (float[])featureCols[j].Clone();
            for (int i = vals.Length - 1; i > 0; i--)
            {
                int swapIndex = localRng.Next(i + 1);
                (vals[swapIndex], vals[i]) = (vals[i], vals[swapIndex]);
            }

            var scratch = new float[calSet[0].Features.Length];
            int shuffledCorrect = 0;
            for (int idx = 0; idx < sampleCount; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p = Math.Clamp(rawProbProvider(scratch), 0.0, 1.0);
                if ((p >= 0.5) == (calSet[idx].Direction == 1)) shuffledCorrect++;
            }

            double shuffledAcc = (double)shuffledCorrect / sampleCount;
            importance[j] = Math.Max(0.0, baselineAcc - shuffledAcc);
        });

        double total = importance.Sum();
        if (total > 1e-10)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    // ── Decision boundary distance (numeric gradient norms) ───────────────────

    /// <summary>
    /// Computes the mean and standard deviation of the approximate input-space gradient norm
    /// ‖∇_x P(Buy|x)‖ over the supplied calibration set using finite differences.
    /// This keeps the statistic valid for linear, subsampled, polynomial, and MLP learners.
    /// </summary>
    internal static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample>  calSet,
        Func<float[], double> probabilityProvider,
        bool[]?               activeFeatureMask = null)
    {
        if (calSet.Count == 0) return (0.0, 0.0);

        int featureCount = calSet[0].Features.Length;
        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            const float Epsilon = 1e-3f;
            var plus = (float[])calSet[i].Features.Clone();
            var minus = (float[])calSet[i].Features.Clone();
            double gradSq = 0.0;

            for (int j = 0; j < featureCount && j < plus.Length; j++)
            {
                if (activeFeatureMask is not null &&
                    (j >= activeFeatureMask.Length || !activeFeatureMask[j]))
                    continue;

                plus[j] += Epsilon;
                minus[j] -= Epsilon;

                double pPlus = probabilityProvider(plus);
                double pMinus = probabilityProvider(minus);
                if (!double.IsFinite(pPlus) || !double.IsFinite(pMinus))
                    continue;
                double grad = (pPlus - pMinus) / (2.0 * Epsilon);
                if (!double.IsFinite(grad))
                    continue;
                gradSq += grad * grad;

                plus[j] = calSet[i].Features[j];
                minus[j] = calSet[i].Features[j];
            }

            norms[i] = Math.Sqrt(gradSq);
        }

        double mean = norms.Average();
        double variance = norms.Sum(n => (n - mean) * (n - mean));
        double std  = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0.0;
        return (mean, std);
    }

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        double ProbProvider(float[] features) =>
            EnsembleProb(features, weights, biases, featureCount, subsets, default,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

        return ComputeDecisionBoundaryStats(calSet, ProbProvider);
    }

    // ── Durbin-Watson autocorrelation test ────────────────────────────────────

    /// <summary>
    /// Computes the Durbin-Watson statistic on magnitude regressor residuals over the
    /// training set. DW = Σ(e_t − e_{t-1})² / Σe_t².
    /// DW ≈ 2 → no autocorrelation; DW &lt; 1.5 → positive autocorrelation;
    /// DW > 2.5 → negative autocorrelation.
    /// Returns 2.0 when the training set is too small to compute reliably.
    /// </summary>
    internal static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  featureCount)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = PredictMagnitude(trainSet[i].Features, magWeights, magBias);
            residuals[i] = trainSet[i].Magnitude - pred;
            if (!double.IsFinite(residuals[i]))
                return 2.0;
        }

        double sumSqDiff = 0.0;
        double sumSqRes  = 0.0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double diff   = residuals[i] - residuals[i - 1];
            if (!double.IsFinite(diff))
                return 2.0;
            sumSqDiff    += diff * diff;
        }
        for (int i = 0; i < residuals.Length; i++)
            sumSqRes += residuals[i] * residuals[i];

        return sumSqRes < 1e-15 ? 2.0 : sumSqDiff / sumSqRes;
    }

    // ── Mutual-information feature redundancy (Round 6) ───────────────────────

    /// <summary>
    /// Computes pairwise mutual information between the top-N features on the training set
    /// (discretised into 10 equal-width bins). Returns pairs whose MI exceeds
    /// <paramref name="threshold"/> × log(2) as "FeatureA:FeatureB" strings.
    /// Empty when disabled (threshold == 0) or fewer than 20 training samples.
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  featureCount,
        double               threshold)
    {
        if (threshold <= 0.0 || trainSet.Count < 20) return [];

        const int TopN   = 10;   // only check first TopN features to bound O(N²) cost
        const int NumBin = 10;

        int checkCount = Math.Min(TopN, featureCount);
        var result     = new List<string>();
        double maxMi   = threshold * Math.Log(2);

        for (int i = 0; i < checkCount; i++)
        {
            for (int j = i + 1; j < checkCount; j++)
            {
                // Build joint 2-D histogram
                var joint  = new double[NumBin, NumBin];
                var margI  = new double[NumBin];
                var margJ  = new double[NumBin];
                int n      = 0;

                foreach (var s in trainSet)
                {
                    double vi = s.Features[i];
                    double vj = s.Features[j];
                    int bi = Math.Clamp((int)((vi + 3.0) / 6.0 * NumBin), 0, NumBin - 1);
                    int bj = Math.Clamp((int)((vj + 3.0) / 6.0 * NumBin), 0, NumBin - 1);
                    joint[bi, bj]++;
                    margI[bi]++;
                    margJ[bj]++;
                    n++;
                }

                if (n == 0) continue;
                double mi = 0.0;
                for (int bi = 0; bi < NumBin; bi++)
                    for (int bj = 0; bj < NumBin; bj++)
                    {
                        double pij = joint[bi, bj] / n;
                        double pi  = margI[bi]      / n;
                        double pj  = margJ[bj]      / n;
                        if (pij > 0 && pi > 0 && pj > 0)
                            mi += pij * Math.Log(pij / (pi * pj));
                    }

                if (mi >= maxMi)
                    result.Add($"{GetFeatureDisplayName(i)}:{GetFeatureDisplayName(j)}");
            }
        }

        return [.. result];
    }

    // ── Ensemble diversity (Round 7) ──────────────────────────────────────────

    /// <summary>
    /// Computes the average pairwise Pearson correlation between all K learners after
    /// projecting each learner back into raw feature space.
    /// Returns 0.0 when K &lt; 2 or all weights are zero.
    /// </summary>
    internal static double ComputeEnsembleDiversity(
        double[][]  weights,
        int         featureCount,
        int[][]?    subsets,
        bool[]?     activeLearners = null,
        MlpState    mlp = default)
    {
        int K = weights.Length;
        if (K < 2) return 0.0;

        double sumCorr = 0.0;
        int    pairs   = 0;

        for (int i = 0; i < K; i++)
            for (int j = i + 1; j < K; j++)
            {
                if (activeLearners is not null &&
                    ((i >= activeLearners.Length || !activeLearners[i]) ||
                     (j >= activeLearners.Length || !activeLearners[j])))
                    continue;

                var learnerProjectionI = ProjectLearnerToFeatureSpace(
                    i, weights, featureCount, subsets, mlp.HiddenW, mlp.HiddenDim);
                var learnerProjectionJ = ProjectLearnerToFeatureSpace(
                    j, weights, featureCount, subsets, mlp.HiddenW, mlp.HiddenDim);
                double rho = PearsonCorrelation(learnerProjectionI, learnerProjectionJ, featureCount);
                sumCorr += rho;
                pairs++;
            }

        return pairs > 0 ? sumCorr / pairs : 0.0;
    }

    // ── Brier Skill Score (Round 7) ───────────────────────────────────────────

    /// <summary>
    /// Computes BSS = 1 − Brier_model / Brier_naive on the test set.
    /// Brier_naive = p_base × (1 − p_base) where p_base = fraction of Buy labels.
    /// Returns 0.0 when the test set is empty.
    /// </summary>
    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count == 0) return 0.0;

        double sumBrier = 0.0;
        int    buyCount = 0;

        foreach (var s in testSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double calibP = ApplyGlobalPlattCalibration(rawP, plattA, plattB);
            double y      = s.Direction > 0 ? 1.0 : 0.0;
            double diff   = calibP - y;
            sumBrier += diff * diff;
            if (s.Direction > 0) buyCount++;
        }

        int    n           = testSet.Count;
        double brierModel  = sumBrier / n;
        double pBase       = (double)buyCount / n;
        double brierNaive  = pBase * (1.0 - pBase);

        return brierNaive < 1e-10 ? 0.0 : 1.0 - brierModel / brierNaive;
    }

    private static double ComputeBrierSkillScore(
        List<TrainingSample>  testSet,
        Func<float[], double> calibratedProb)
    {
        if (testSet.Count == 0) return 0.0;

        double sumBrier = 0.0;
        int buyCount = 0;

        foreach (var s in testSet)
        {
            double p = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
            double y = s.Direction > 0 ? 1.0 : 0.0;
            double diff = p - y;
            sumBrier += diff * diff;
            if (s.Direction > 0) buyCount++;
        }

        int n = testSet.Count;
        double brierModel = sumBrier / n;
        double pBase = (double)buyCount / n;
        double brierNaive = pBase * (1.0 - pBase);

        return brierNaive < 1e-10 ? 0.0 : 1.0 - brierModel / brierNaive;
    }

    // ── Walk-forward Sharpe trend (Round 6) ───────────────────────────────────

    /// <summary>
    /// Fits a least-squares linear regression slope through the per-fold Sharpe series.
    /// Returns 0.0 when fewer than 3 folds are available.
    /// A negative slope indicates degrading out-of-sample performance over time.
    /// </summary>
    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        int n = sharpePerFold.Count;
        if (n < 3) return 0.0;

        // Simple OLS: slope = ( n·Σxy − Σx·Σy ) / ( n·Σx² − (Σx)² )
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = sharpePerFold[i];
            sumX  += x;
            sumY  += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) < 1e-12 ? 0.0 : (n * sumXY - sumX * sumY) / denom;
    }
}
