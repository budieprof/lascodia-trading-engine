using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  Model evaluation
    // ═══════════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateModel(
        List<double[]>       rocketFeatures,
        List<TrainingSample> samples,
        double[]             w, double bias,
        double[]             magWeights, double magBias,
        double               plattA, double plattB,
        int                  dim, int featureCount,
        double[]?            rocketMagW = null, double rocketMagB = 0.0)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double sumBrier = 0, sumMagSqErr = 0, sumEV = 0;
        var retBuf = new double[samples.Count];
        int retCount = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            double calibP = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            bool predictedUp = calibP >= 0.5;
            bool actualUp    = samples[i].Direction == 1;
            bool isCorrect   = predictedUp == actualUp;

            double y = actualUp ? 1.0 : 0.0;
            sumBrier += (calibP - y) * (calibP - y);

            double magPred = featureCount <= magWeights.Length
                ? MLFeatureHelper.DotProduct(magWeights, samples[i].Features) + magBias
                : 0;

            // #14: Ensemble with ROCKET-space magnitude prediction
            if (rocketMagW is { Length: > 0 })
            {
                double rocketMagPred = rocketMagB;
                int rLen = Math.Min(rocketMagW.Length, rocketFeatures[i].Length);
                for (int j = 0; j < rLen; j++)
                    rocketMagPred += rocketMagW[j] * rocketFeatures[i][j];
                magPred = (magPred + rocketMagPred) * 0.5;
            }

            double magErr = magPred - samples[i].Magnitude;
            sumMagSqErr += magErr * magErr;

            double edge = calibP - 0.5;
            sumEV += (isCorrect ? 1 : -1) * Math.Abs(edge) * Math.Abs(samples[i].Magnitude);

            int predDir = predictedUp ? 1 : -1;
            int actDir  = actualUp    ? 1 : -1;
            retBuf[retCount++] = predDir * actDir * Math.Abs(samples[i].Magnitude);

            if (isCorrect) correct++;
            if (predictedUp && actualUp)   tp++;
            if (predictedUp && !actualUp)  fp++;
            if (!predictedUp && actualUp)  fn++;
            if (!predictedUp && !actualUp) tn++;
        }

        int    evalN     = samples.Count;
        double accuracy  = evalN > 0 ? (double)correct / evalN : 0;
        double brier     = evalN > 0 ? sumBrier / evalN : 1;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = evalN > 0 ? sumEV / evalN : 0;
        double magRmse   = evalN > 0 ? Math.Sqrt(sumMagSqErr / evalN) : 0;

        // Sharpe ratio from directional returns
        double retMean = 0;
        for (int i = 0; i < retCount; i++) retMean += retBuf[i];
        retMean /= retCount > 0 ? retCount : 1;
        double retVar = 0;
        for (int i = 0; i < retCount; i++)
        {
            double d = retBuf[i] - retMean;
            retVar += d * d;
        }
        double retStd = retCount > 1 ? Math.Sqrt(retVar / (retCount - 1)) : 1.0;
        double sharpe = retStd > 1e-10 ? retMean / retStd * Math.Sqrt(252) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: accuracy, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ECE (Expected Calibration Error)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Ece, double[]? BinConf, double[]? BinAcc, int[]? BinCounts) ComputeEce(
        List<double[]> rocketFeatures, List<TrainingSample> samples,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binCorrect = new int[NumBins];
        var binCount   = new int[NumBins];

        for (int i = 0; i < samples.Count; i++)
        {
            double p   = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            int    bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);
            binConfSum[bin] += p;
            if (samples[i].Direction == 1) binCorrect[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = samples.Count;
        var binConf = new double[NumBins];
        var binAcc  = new double[NumBins];
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double acc     = binCorrect[b] / (double)binCount[b];
            binConf[b] = avgConf;
            binAcc[b]  = acc;
            ece += Math.Abs(acc - avgConf) * binCount[b] / n;
        }

        return (ece, binConf, binAcc, binCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Permutation feature importance (on original features)
    // ═══════════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<double[]>       testRocket,
        double[]             w, double bias, double plattA, double plattB, int dim,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int featureCount,
        double[] rocketMeans, double[] rocketStds,
        CancellationToken ct, bool deterministic = false)
    {
        // Baseline accuracy
        int baseCorrect = 0;
        for (int i = 0; i < testSet.Count; i++)
        {
            double p = CalibratedProb(testRocket[i], w, bias, plattA, plattB, dim);
            if ((p >= 0.5) == (testSet[i].Direction == 1)) baseCorrect++;
        }
        double baseline = (double)baseCorrect / testSet.Count;

        var importance = new float[featureCount];
        int tn = testSet.Count;
        const int numRuns = 3;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = deterministic ? 1 : -1 }, j =>
        {
            if (ct.IsCancellationRequested) return;

            double totalDrop = 0;
            for (int run = 0; run < numRuns; run++)
            {
                if (ct.IsCancellationRequested) return;

                var shuffleRng = new Random(j * 71 + 13 + run * 997);
                var indices = Enumerable.Range(0, tn).ToArray();
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int swap = shuffleRng.Next(i + 1);
                    (indices[i], indices[swap]) = (indices[swap], indices[i]);
                }

                int correct = 0;
                for (int i = 0; i < tn; i++)
                {
                    // Create shuffled feature vector
                    var scratch = new float[testSet[i].Features.Length];
                    Array.Copy(testSet[i].Features, scratch, scratch.Length);
                    scratch[j] = testSet[indices[i]].Features[j];

                    // Re-extract ROCKET features for this shuffled sample
                    var shuffledSample = new List<TrainingSample>(1) { new(scratch, testSet[i].Direction, testSet[i].Magnitude) };
                    var shuffledRocket = ExtractRocketFeatures(shuffledSample, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
                    // Standardize
                    for (int d = 0; d < dim; d++)
                        shuffledRocket[0][d] = (shuffledRocket[0][d] - rocketMeans[d]) / rocketStds[d];

                    double p = CalibratedProb(shuffledRocket[0], w, bias, plattA, plattB, dim);
                    if ((p >= 0.5) == (testSet[i].Direction == 1)) correct++;
                }
                double shuffledAcc = (double)correct / tn;
                totalDrop += Math.Max(0, baseline - shuffledAcc);
            }
            importance[j] = (float)(totalDrop / numRuns);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Conformal prediction threshold
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB,
        double[] isotonicBp, int dim, double alpha)
    {
        var scores = new List<double>(calRocket.Count);
        for (int i = 0; i < calRocket.Count; i++)
        {
            double p = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            if (isotonicBp.Length >= 4) p = ApplyIsotonicCalibration(p, isotonicBp);
            scores.Add(calSet[i].Direction > 0 ? 1.0 - p : p);
        }

        scores.Sort();
        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Decision boundary distance statistics
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<double[]> calRocket, double[] w, double bias, int dim)
    {
        double wNorm = 0;
        for (int j = 0; j < dim; j++) wNorm += w[j] * w[j];
        wNorm = Math.Sqrt(wNorm);
        if (wNorm < 1e-10) return (0, 0);

        var norms = new double[calRocket.Count];
        for (int i = 0; i < calRocket.Count; i++)
        {
            double p = RocketProb(calRocket[i], w, bias, dim);
            norms[i] = p * (1.0 - p) * wNorm;
        }

        double mean = norms.Average();
        double variance = 0;
        for (int i = 0; i < norms.Length; i++)
        {
            double d = norms[i] - mean;
            variance += d * d;
        }
        double std = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0;
        return (mean, std);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Durbin-Watson autocorrelation test
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet, double[] magW, double magB, int featureCount)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magB;
            for (int j = 0; j < featureCount && j < trainSet[i].Features.Length && j < magW.Length; j++)
                pred += magW[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumSqDiff = 0, sumSqRes = 0;
        for (int i = 0; i < residuals.Length; i++)
        {
            sumSqRes += residuals[i] * residuals[i];
            if (i > 0)
            {
                double diff = residuals[i] - residuals[i - 1];
                sumSqDiff += diff * diff;
            }
        }

        return sumSqRes > 1e-10 ? sumSqDiff / sumSqRes : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Brier Skill Score
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeBrierSkillScore(
        List<double[]> rocketFeatures, List<TrainingSample> testSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        double sumBrier = 0;
        int buyCount = 0;

        for (int i = 0; i < testSet.Count; i++)
        {
            double calibP = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            double y      = testSet[i].Direction > 0 ? 1.0 : 0.0;
            double diff   = calibP - y;
            sumBrier += diff * diff;
            if (testSet[i].Direction > 0) buyCount++;
        }

        int    n          = testSet.Count;
        double brierModel = sumBrier / n;
        double pBase      = (double)buyCount / n;
        double brierNaive = pBase * (1.0 - pBase);

        return brierNaive < 1e-10 ? 0.0 : 1.0 - brierModel / brierNaive;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Mutual-information feature redundancy
    // ═══════════════════════════════════════════════════════════════════════════

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int featureCount, double threshold)
    {
        if (trainSet.Count < 20 || featureCount < 2) return [];

        const int NumBins = 10;
        int topN = Math.Min(10, featureCount);
        var pairs = new List<string>();

        for (int a = 0; a < topN; a++)
        {
            float minA = float.MaxValue, maxA = float.MinValue;
            foreach (var s in trainSet)
            {
                if (s.Features[a] < minA) minA = s.Features[a];
                if (s.Features[a] > maxA) maxA = s.Features[a];
            }
            float rangeA = maxA - minA;
            if (rangeA < 1e-6f) continue;

            for (int b = a + 1; b < topN; b++)
            {
                float minB = float.MaxValue, maxB = float.MinValue;
                foreach (var s in trainSet)
                {
                    if (s.Features[b] < minB) minB = s.Features[b];
                    if (s.Features[b] > maxB) maxB = s.Features[b];
                }
                float rangeB = maxB - minB;
                if (rangeB < 1e-6f) continue;

                var joint = new int[NumBins, NumBins];
                var margA = new int[NumBins];
                var margB = new int[NumBins];
                int n = trainSet.Count;

                foreach (var s in trainSet)
                {
                    int binA = Math.Clamp((int)((s.Features[a] - minA) / rangeA * NumBins), 0, NumBins - 1);
                    int binB = Math.Clamp((int)((s.Features[b] - minB) / rangeB * NumBins), 0, NumBins - 1);
                    joint[binA, binB]++;
                    margA[binA]++;
                    margB[binB]++;
                }

                double mi = 0;
                for (int ia = 0; ia < NumBins; ia++)
                for (int ib = 0; ib < NumBins; ib++)
                {
                    if (joint[ia, ib] == 0) continue;
                    double pJoint = (double)joint[ia, ib] / n;
                    double pA = (double)margA[ia] / n;
                    double pB = (double)margB[ib] / n;
                    mi += pJoint * Math.Log(pJoint / (pA * pB + 1e-15));
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    pairs.Add($"{nameA}:{nameB}");
                }
            }
        }

        return [.. pairs];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stationarity gate helper
    // ═══════════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        int nonStationary = 0;
        int n = samples.Count;
        if (n < 30) return 0;

        for (int j = 0; j < featureCount; j++)
        {
            // Simple Dickey-Fuller proxy: compute AR(1) coefficient
            double sumXY = 0, sumXX = 0;
            for (int i = 1; i < n; i++)
            {
                double x = samples[i - 1].Features[j];
                double y = samples[i].Features[j];
                sumXY += x * y;
                sumXX += x * x;
            }
            double rho = sumXX > 1e-10 ? sumXY / sumXX : 0;
            if (Math.Abs(rho) > 0.95) nonStationary++;
        }

        return nonStationary;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Equity-curve statistics
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);

        double equity = 0, peak = 0, maxDD = 0;
        var returns = new double[predictions.Length];

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == predictions[i].Actual ? 1.0 : -1.0;
            returns[i] = ret;
            equity += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 0
                ? (peak - equity) / peak
                : (equity < 0 ? -equity / predictions.Length : 0);
            if (dd > maxDD) maxDD = dd;
        }

        double mean = 0;
        for (int i = 0; i < returns.Length; i++) mean += returns[i];
        mean /= returns.Length;
        double var_ = 0;
        for (int i = 0; i < returns.Length; i++)
        {
            double d = returns[i] - mean;
            var_ += d * d;
        }
        double std = returns.Length > 1 ? Math.Sqrt(var_ / (returns.Length - 1)) : 1;
        double sharpe = std > 1e-10 ? mean / std : 0;

        return (maxDD, sharpe);
    }

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0.0;

        int n = sharpePerFold.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += i;
            sumY  += sharpePerFold[i];
            sumXY += i * sharpePerFold[i];
            sumXX += i * i;
        }

        double denom = n * sumXX - sumX * sumX;
        return denom > 1e-10 ? (n * sumXY - sumX * sumY) / denom : 0.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Calibration-set permutation importance (for warm-start transfer)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<double[]>       calRocket,
        double[]             w, double bias, int dim,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int featureCount,
        double[] rocketMeans, double[] rocketStds,
        CancellationToken ct, bool deterministic = false)
    {
        // Baseline accuracy on cal set
        int baseCorrect = 0;
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = RocketProb(calRocket[i], w, bias, dim);
            if ((p >= 0.5) == (calSet[i].Direction == 1)) baseCorrect++;
        }
        double baseline = (double)baseCorrect / calSet.Count;

        var importance = new double[featureCount];
        int m = calSet.Count;
        const int numRuns = 3;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = deterministic ? 1 : -1 }, j =>
        {
            double totalDrop = 0;
            for (int run = 0; run < numRuns; run++)
            {
                var shuffleRng = new Random(j * 71 + 17 + run * 997);
                var indices = Enumerable.Range(0, m).ToArray();
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int swap = shuffleRng.Next(i + 1);
                    (indices[i], indices[swap]) = (indices[swap], indices[i]);
                }

                int correct = 0;
                for (int i = 0; i < m; i++)
                {
                    var scratch = new float[calSet[i].Features.Length];
                    Array.Copy(calSet[i].Features, scratch, scratch.Length);
                    scratch[j] = calSet[indices[i]].Features[j];

                    var shuffledSample = new List<TrainingSample>(1) { new(scratch, calSet[i].Direction, calSet[i].Magnitude) };
                    var shuffledRocket = ExtractRocketFeatures(shuffledSample, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
                    for (int d = 0; d < dim; d++)
                        shuffledRocket[0][d] = (shuffledRocket[0][d] - rocketMeans[d]) / rocketStds[d];

                    double p = RocketProb(shuffledRocket[0], w, bias, dim);
                    if ((p >= 0.5) == (calSet[i].Direction == 1)) correct++;
                }
                double shuffledAcc = (double)correct / m;
                totalDrop += Math.Max(0.0, baseline - shuffledAcc);
            }
            importance[j] = totalDrop / numRuns;
        });

        double total = importance.Sum();
        if (total > 1e-10)
            for (int j = 0; j < featureCount; j++) importance[j] /= total;

        return importance;
    }
}
