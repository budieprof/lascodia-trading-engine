using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SmoteModelTrainer
{

    // ── Final evaluation ──────────────────────────────────────────────────────

    private static EvalMetrics EvaluateEnsemble(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double               oobAccuracy,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, oobAccuracy);

        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSqSum = 0;
        double sumR = 0, sumR2 = 0;

        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    yHat   = calibP >= 0.5 ? 1 : 0;
            int    yVal   = s.Direction > 0 ? 1 : 0;

            if (yHat == yVal) correct++;
            if (yHat == 1 && yVal == 1) tp++;
            if (yHat == 1 && yVal == 0) fp++;
            if (yHat == 0 && yVal == 1) fn++;
            if (yHat == 0 && yVal == 0) tn++;

            brierSum += (calibP - yVal) * (calibP - yVal);

            // Per-sample return: +1 correct, -1 incorrect
            double ret = yHat == yVal ? 1.0 : -1.0;
            sumR  += ret;
            sumR2 += ret * ret;

            double magPred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, F); j++)
                magPred += magWeights[j] * s.Features[j];
            magSqSum += (magPred - s.Magnitude) * (magPred - s.Magnitude);
        }

        int    evalN     = evalSet.Count;
        double accuracy  = (double)correct / evalN;
        double brier     = brierSum / evalN;
        double magRmse   = Math.Sqrt(magSqSum / evalN);
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        // Sharpe = mean(returns) / std(returns) where return is +1/-1 per prediction
        double meanR = sumR / evalN;
        double varR  = evalN > 1 ? (sumR2 / evalN - meanR * meanR) : 0;
        double sharpe = varR > 0 ? meanR / Math.Sqrt(varR) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: accuracy, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn, OobAccuracy: oobAccuracy);
    }

    // ── OOB accuracy ──────────────────────────────────────────────────────────

    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        bool[][]             oobMasks,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K = weights.Length;
        int correct = 0, total = 0;

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Average predictions only from learners where sample i is out-of-bag
            double sumP = 0;
            int oobCount = 0;
            for (int k = 0; k < K; k++)
            {
                if (i < oobMasks[k].Length && oobMasks[k][i])
                {
                    sumP += SingleLearnerProb(trainSet[i].Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
                    oobCount++;
                }
            }
            if (oobCount == 0) continue;
            double avgP = sumP / oobCount;
            if ((avgP >= 0.5 ? 1 : 0) == (trainSet[i].Direction > 0 ? 1 : 0)) correct++;
            total++;
        }

        return total > 0 ? (double)correct / total : 0.0;
    }

    // ── H6: ECE ───────────────────────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0) return 0.0;

        var binAcc  = new double[EceBinCount];
        var binConf = new double[EceBinCount];
        var binCnt  = new int[EceBinCount];

        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    b      = Math.Min((int)(calibP * EceBinCount), EceBinCount - 1);
            binCnt[b]++;
            binConf[b] += calibP;
            binAcc[b]  += (s.Direction > 0 ? 1 : 0);
        }

        double ece = 0.0;
        for (int b = 0; b < EceBinCount; b++)
        {
            if (binCnt[b] == 0) continue;
            double conf = binConf[b] / binCnt[b];
            double acc  = binAcc[b]  / binCnt[b];
            ece += Math.Abs(acc - conf) * binCnt[b];
        }
        return ece / evalSet.Count;
    }

    // ── H7: Brier Skill Score ─────────────────────────────────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0) return 0.0;

        double brierSum = 0;
        double posCount = 0;
        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    yVal   = s.Direction > 0 ? 1 : 0;
            brierSum += (calibP - yVal) * (calibP - yVal);
            posCount += yVal;
        }

        double brier      = brierSum / evalSet.Count;
        double pBase      = posCount / evalSet.Count;
        double brierNaive = pBase * (1 - pBase);
        return brierNaive > 0 ? 1.0 - brier / brierNaive : 0.0;
    }

    // ── M10: Ensemble diversity ───────────────────────────────────────────────
    //
    // For MLP learners, weight-space correlation is misleading (two learners can
    // have different output weights but identical predictions due to hidden-layer
    // reparameterisation). Use prediction-based Pearson correlation instead:
    // compute each learner's predictions on the cal set and correlate those vectors.
    // For linear learners (hidDim == 0) without a cal set, fall back to weight correlation.

    private static double ComputeEnsembleDiversity(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K = weights.Length;
        if (K < 2) return 0.0;

        // Use prediction-based correlation when we have cal samples
        if (calSet.Count > 0)
        {
            int N = calSet.Count;
            var preds = new double[K][];
            for (int k = 0; k < K; k++)
            {
                preds[k] = new double[N];
                for (int i = 0; i < N; i++)
                    preds[k][i] = SingleLearnerProb(calSet[i].Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
            }

            double sumRho = 0;
            int    pairs  = 0;
            for (int i = 0; i < K - 1; i++)
            for (int j = i + 1; j < K; j++)
            {
                sumRho += Math.Abs(PearsonCorrelation(preds[i], preds[j], N));
                pairs++;
            }
            return pairs > 0 ? sumRho / pairs : 0.0;
        }

        // Fallback: weight-space correlation (meaningful only for linear learners)
        double sumRhoW = 0;
        int    pairsW  = 0;
        for (int i = 0; i < K - 1; i++)
        for (int j = i + 1; j < K; j++)
        {
            sumRhoW += Math.Abs(PearsonCorrelation(weights[i], weights[j], F));
            pairsW++;
        }
        return pairsW > 0 ? sumRhoW / pairsW : 0.0;
    }

    // ── M18: Decision boundary stats ─────────────────────────────────────────

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        // Numerical gradient norm via finite differences — correct for both linear and MLP
        if (calSet.Count == 0 || weights.Length == 0) return (0.0, 0.0);

        const double eps = 1e-4;
        var gradNorms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var x = calSet[i].Features;
            // Use a defensive copy to avoid mutating the shared feature array
            var xPerturbed = (float[])x.Clone();
            double p0 = EnsembleProb(x, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
            double gradSq = 0;
            for (int j = 0; j < Math.Min(F, x.Length); j++)
            {
                xPerturbed[j] = (float)(x[j] + eps);
                double pPlus = EnsembleProb(xPerturbed, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
                xPerturbed[j] = x[j]; // restore for next feature
                double dP = (pPlus - p0) / eps;
                gradSq += dP * dP;
            }
            gradNorms[i] = Math.Sqrt(gradSq);
        }

        double mean = gradNorms.Average();
        double std  = StdDev(gradNorms, mean);
        return (mean, std);
    }

    // ── M12: Durbin-Watson ────────────────────────────────────────────────────

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  F)
    {
        if (trainSet.Count < 3) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, F); j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumDiff = 0, sumSq = 0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double d = residuals[i] - residuals[i - 1];
            sumDiff += d * d;
        }
        for (int i = 0; i < residuals.Length; i++) sumSq += residuals[i] * residuals[i];

        return sumSq > 0 ? sumDiff / sumSq : 2.0;
    }

    // ── M16: MI redundancy check ──────────────────────────────────────────────

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  F,
        double               threshold)
    {
        if (trainSet.Count < MinCalSamples || F < 2) return [];

        // Approximate MI via binned joint entropy: MI(i,j) = H(i) + H(j) - H(i,j)
        // Precompute per-feature min/max/marginal entropy to avoid redundant scans
        var featureNames = MLFeatureHelper.FeatureNames;
        double maxMI = Math.Log(2);
        double n = trainSet.Count;

        // Precompute per-feature min, max, and binned marginal counts
        var fMin = new double[F];
        var fMax = new double[F];
        Array.Fill(fMin, double.MaxValue);
        Array.Fill(fMax, double.MinValue);
        foreach (var s in trainSet)
            for (int fi = 0; fi < Math.Min(F, s.Features.Length); fi++)
            {
                if (s.Features[fi] < fMin[fi]) fMin[fi] = s.Features[fi];
                if (s.Features[fi] > fMax[fi]) fMax[fi] = s.Features[fi];
            }

        // Build total pair count for parallel array sizing
        int totalPairs = F * (F - 1) / 2;
        var pairResults = new string?[totalPairs];

        Parallel.For(0, F - 1, i =>
        {
            double iRange = Math.Max(fMax[i] - fMin[i], 1e-9);

            for (int j = i + 1; j < F; j++)
            {
                double jRange = Math.Max(fMax[j] - fMin[j], 1e-9);

                var jointCounts = new double[MIBinCount, MIBinCount];
                double[] piCounts = new double[MIBinCount];
                double[] pjCounts = new double[MIBinCount];

                foreach (var s in trainSet)
                {
                    int bi = Math.Min((int)((s.Features[i] - fMin[i]) / iRange * MIBinCount), MIBinCount - 1);
                    int bj = Math.Min((int)((s.Features[j] - fMin[j]) / jRange * MIBinCount), MIBinCount - 1);
                    jointCounts[bi, bj]++;
                    piCounts[bi]++;
                    pjCounts[bj]++;
                }

                double Hi = 0, Hj = 0, Hij = 0;
                for (int bi = 0; bi < MIBinCount; bi++)
                {
                    if (piCounts[bi] > 0) { double p = piCounts[bi] / n; Hi -= p * Math.Log(p); }
                    if (pjCounts[bi] > 0) { double p = pjCounts[bi] / n; Hj -= p * Math.Log(p); }
                    for (int bj = 0; bj < MIBinCount; bj++)
                        if (jointCounts[bi, bj] > 0) { double p = jointCounts[bi, bj] / n; Hij -= p * Math.Log(p); }
                }

                double mi = Hi + Hj - Hij;
                if (mi > threshold * maxMI)
                {
                    string iName = i < featureNames.Length ? featureNames[i] : $"f{i}";
                    string jName = j < featureNames.Length ? featureNames[j] : $"f{j}";
                    // Compute flat pair index for lock-free storage
                    int pairIdx = i * (2 * F - i - 1) / 2 + (j - i - 1);
                    pairResults[pairIdx] = $"{iName}:{jName}";
                }
            }
        });

        return [.. pairResults.Where(r => r is not null).Cast<string>()];
    }

    // ── Permutation feature importance (test set) ─────────────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW,
        double[][]?          mlpHB,
        int                  hidDim,
        Random               _,
        CancellationToken    ct)
    {
        double baseline = EvalAccuracy(testSet, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var imp = new float[F];

        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(77 * (j + 1));
            var origVals = testSet.Select(s => s.Features[j]).ToArray();
            for (int i = origVals.Length - 1; i > 0; i--)
            {
                int swap = rng.Next(i + 1);
                (origVals[i], origVals[swap]) = (origVals[swap], origVals[i]);
            }
            var permuted = testSet.Select((s, idx) =>
            {
                var f2 = (float[])s.Features.Clone(); f2[j] = origVals[idx];
                return s with { Features = f2 };
            }).ToList();
            double permAcc = EvalAccuracy(permuted, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            imp[j] = (float)Math.Max(0, baseline - permAcc);
        });

        float sum = imp.Sum();
        if (sum > 0) for (int j = 0; j < F; j++) imp[j] /= sum;
        return imp;
    }

    // ── M7: Cal-set permutation importance ────────────────────────────────────

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW,
        double[][]?          mlpHB,
        int                  hidDim,
        CancellationToken    ct)
    {
        double baseline = EvalAccuracy(calSet, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var imp = new double[F];

        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(13 * (j + 1));
            var origVals = calSet.Select(s => s.Features[j]).ToArray();
            for (int i = origVals.Length - 1; i > 0; i--)
            {
                int swap = rng.Next(i + 1);
                (origVals[i], origVals[swap]) = (origVals[swap], origVals[i]);
            }
            var permuted = calSet.Select((s, idx) =>
            {
                var f2 = (float[])s.Features.Clone(); f2[j] = origVals[idx];
                return s with { Features = f2 };
            }).ToList();
            double permAcc = EvalAccuracy(permuted, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            imp[j] = Math.Max(0, baseline - permAcc);
        });

        return imp;
    }

    // ── H3: Equity-curve gate ─────────────────────────────────────────────────

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Pred, int Actual)[] predictions)
    {
        double equity    = 0;
        double peak      = 0;
        double maxDD     = 0;
        double sumPnl    = 0, sumSqPnl = 0;
        int    n         = predictions.Length;

        for (int i = 0; i < n; i++)
        {
            double pnl = predictions[i].Pred == predictions[i].Actual ? 1.0 : -1.0;
            equity   += pnl;
            peak      = Math.Max(peak, equity);
            maxDD     = Math.Max(maxDD, peak - equity);
            sumPnl   += pnl;
            sumSqPnl += pnl * pnl;
        }

        double meanPnl = n > 0 ? sumPnl / n : 0;
        double varPnl  = n > 1 ? (sumSqPnl / n - meanPnl * meanPnl) : 0;
        double sharpe  = varPnl > 0 ? meanPnl / Math.Sqrt(varPnl) : 0;
        double ddFrac  = peak > 0 ? maxDD / peak : 0;

        return (ddFrac, sharpe);
    }

    private static double EvalAccuracy(
        List<TrainingSample> samples,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (samples.Count == 0) return 0.0;
        int correct = 0;
        foreach (var s in samples)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            if ((calibP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
        }
        return (double)correct / samples.Count;
    }

    private static double ComputeSharpeTrend(List<double> sharpeList)
    {
        int n = sharpeList.Count;
        if (n < 2) return 0.0;
        double xMean = (n - 1) / 2.0, yMean = sharpeList.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = i - xMean;
            num += dx * (sharpeList[i] - yMean);
            den += dx * dx;
        }
        return den > 0 ? num / den : 0.0;
    }
}
