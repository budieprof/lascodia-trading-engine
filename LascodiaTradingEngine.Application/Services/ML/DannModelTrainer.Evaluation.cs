using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class DannModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        DannModel            model,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  F)
    {
        if (testSet.Count == 0) return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, wAccSum = 0, wSum = 0;
        var returns = new List<double>();

        for (int i = 0; i < testSet.Count; i++)
        {
            var   s     = testSet[i];
            double rawP = ForwardCls(model, s.Features);
            double p    = ApplyPlatt(rawP, plattA, plattB);
            int    pred = p >= 0.5 ? 1 : 0;

            if (pred == 1 && s.Direction == 1) tp++;
            else if (pred == 1 && s.Direction == 0) fp++;
            else if (pred == 0 && s.Direction == 1) fn++;
            else tn++;

            brierSum += (p - s.Direction) * (p - s.Direction);
            double ev = (2.0 * p - 1.0) * Math.Sign(s.Magnitude);
            evSum += ev;
            double magPred = magBias;
            if (magWeights.Length == F)
                for (int fi = 0; fi < F; fi++) magPred += magWeights[fi] * s.Features[fi];
            magSse += (magPred - s.Magnitude) * (magPred - s.Magnitude);

            double confidence = Math.Abs(p - 0.5) * 2.0;
            bool correct = pred == s.Direction;
            wAccSum += confidence * (correct ? 1.0 : 0.0);
            wSum    += confidence;

            double ret = (s.Direction == 1 ? 1.0 : -1.0) * (pred == s.Direction ? Math.Abs((double)s.Magnitude) : -Math.Abs((double)s.Magnitude));
            returns.Add(ret);
        }

        int n = testSet.Count;
        double acc       = (double)(tp + tn) / n;
        double prec      = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
        double rec       = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
        double f1        = prec + rec > 0 ? 2.0 * prec * rec / (prec + rec) : 0.0;
        double brier     = brierSum / n;
        double ev2       = evSum / n;
        double magRmse   = Math.Sqrt(magSse / n);
        double wAcc      = wSum > 0 ? wAccSum / wSum : acc;

        double retMean   = returns.Count > 0 ? returns.Average() : 0.0;
        double retStd    = returns.Count > 1 ? Math.Sqrt(returns.Sum(r => (r - retMean) * (r - retMean)) / (returns.Count - 1)) : 1.0;
        double sharpe    = retStd > 1e-9 ? retMean / retStd * Math.Sqrt(252.0) : 0.0;

        return new EvalMetrics(acc, prec, rec, f1, magRmse, ev2, brier, wAcc, sharpe, tp, fp, fn, tn);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ECE, THRESHOLD, KELLY, BRIER SKILL SCORE
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        List<TrainingSample> testSet, DannModel model, double plattA, double plattB, int F,
        int bins = 10)
    {
        if (testSet.Count == 0) return 0.0;

        // Precompute probabilities once — avoids 3× redundant forward passes per bin
        var probs = testSet.Select(s =>
            ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB)).ToArray();

        double binWidth = 1.0 / bins;
        double ece = 0.0;
        for (int b = 0; b < bins; b++)
        {
            double lo = b * binWidth, hi = lo + binWidth;
            double confSum = 0.0, accSum = 0.0;
            int count = 0;
            for (int i = 0; i < testSet.Count; i++)
            {
                double p2 = probs[i];
                if (p2 < lo || p2 >= hi) continue;
                confSum += p2;
                accSum  += testSet[i].Direction; // empirical positive rate (ECE calibration, not accuracy)
                count++;
            }
            if (count == 0) continue;
            ece += Math.Abs(accSum / count - confSum / count) * count / testSet.Count;
        }
        return ece;
    }

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, DannModel model,
        double plattA, double plattB, int F)
    {
        if (testSet.Count == 0) return 0.0;
        double baseFrac  = testSet.Average(s => s.Direction);
        double naiveBrier = baseFrac * (1.0 - baseFrac);
        double modelBrier = testSet.Average(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return (p2 - s.Direction) * (p2 - s.Direction);
        });
        return naiveBrier > 1e-9 ? 1.0 - modelBrier / naiveBrier : 0.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONFORMAL + JACKKNIFE+
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F,
        double[] isotonicBp, double alpha)
    {
        if (calSet.Count == 0) return 0.5;
        var scores = calSet.Select(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return s.Direction == 1 ? 1.0 - p2 : p2;
        }).OrderBy(x => x).ToArray();
        int idx = (int)Math.Ceiling((1.0 - alpha) * (scores.Length + 1)) - 1;
        idx = Math.Clamp(idx, 0, scores.Length - 1);
        return scores[idx];
    }

    /// <summary>
    /// Computes leverage-corrected LOO residuals for jackknife+ coverage intervals.
    /// Uses the OLS hat-matrix formula: LOO_i = e_i / (1 − h_ii), where h_ii = x_i^T (X^T X)^{-1} x_i.
    /// This is an approximation for the Huber-trained magnitude regressor but gives proper
    /// coverage guarantees much closer to true jackknife+ than sorted raw residuals.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (magWeights.Length != F || trainSet.Count == 0) return [];

        // Build X^T X (F×F) with small ridge for numerical stability
        var XtX = new double[F, F];
        foreach (var s in trainSet)
            for (int a = 0; a < F; a++)
                for (int b = 0; b < F; b++)
                    XtX[a, b] += s.Features[a] * s.Features[b];
        for (int a = 0; a < F; a++) XtX[a, a] += 1e-6;

        var XtX_inv = InvertMatrix(XtX, F);

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            var s = trainSet[i];
            double pred = magBias;
            for (int fi = 0; fi < F; fi++) pred += magWeights[fi] * s.Features[fi];
            double e_i = s.Magnitude - pred;

            // h_ii = x_i^T (X^T X)^{-1} x_i
            double h_ii = 0.0;
            for (int a = 0; a < F; a++)
            {
                double v = 0.0;
                for (int b = 0; b < F; b++) v += XtX_inv[a, b] * s.Features[b];
                h_ii += s.Features[a] * v;
            }
            h_ii = Math.Clamp(h_ii, 0.0, 0.999);
            residuals[i] = Math.Abs(e_i / (1.0 - h_ii));
        }
        Array.Sort(residuals);
        return residuals;
    }

    /// <summary>Inverts an n×n matrix using Gauss-Jordan elimination with partial pivoting.</summary>
    private static double[,] InvertMatrix(double[,] A, int n)
    {
        // Build augmented matrix [A | I]
        var aug = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = A[i, j];
            aug[i, n + i] = 1.0;
        }

        for (int col = 0; col < n; col++)
        {
            // Partial pivot
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(aug[row, col]) > Math.Abs(aug[pivot, col])) pivot = row;
            if (pivot != col)
                for (int j = 0; j < 2 * n; j++)
                    (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);

            double diag = aug[col, col];
            if (Math.Abs(diag) < 1e-14) continue; // numerically singular column — skip
            for (int j = 0; j < 2 * n; j++) aug[col, j] /= diag;

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = aug[row, col];
                for (int j = 0; j < 2 * n; j++) aug[row, j] -= factor * aug[col, j];
            }
        }

        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                inv[i, j] = aug[i, n + j];
        return inv;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, DannModel model,
        double plattA, double plattB, int F,
        CancellationToken ct)
    {
        if (testSet.Count == 0) return new float[F];

        double baseAcc = testSet.Average(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        });

        var importance = new float[F];
        var rng = new Random(42);

        for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
        {
            double shuffleAcc = 0.0;
            const int Rounds = 3;
            for (int r = 0; r < Rounds; r++)
            {
                var shuffled = testSet.Select(s =>
                {
                    var f2 = (float[])s.Features.Clone();
                    return (Features: f2, s.Direction);
                }).ToList();

                // Shuffle feature fi
                for (int i = shuffled.Count - 1; i > 0; i--)
                {
                    int j2 = rng.Next(i + 1);
                    (shuffled[i].Features[fi], shuffled[j2].Features[fi]) =
                        (shuffled[j2].Features[fi], shuffled[i].Features[fi]);
                }

                shuffleAcc += shuffled.Average(s =>
                {
                    double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                    return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
                });
            }
            importance[fi] = (float)Math.Max(0.0, baseAcc - shuffleAcc / Rounds);
        }

        // Normalise to sum to 1
        float impSum = importance.Sum();
        if (impSum > 0) for (int fi = 0; fi < F; fi++) importance[fi] /= impSum;

        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet, DannModel model, int F, CancellationToken ct)
    {
        if (calSet.Count == 0) return new double[F];

        double baseAcc = calSet.Average(s =>
        {
            double p2 = ForwardCls(model, s.Features);
            return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        });

        var importance = new double[F];
        var rng = new Random(99);

        for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
        {
            var shuffled = calSet.Select(s => ((float[])s.Features.Clone(), s.Direction)).ToList();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j2 = rng.Next(i + 1);
                (shuffled[i].Item1[fi], shuffled[j2].Item1[fi]) =
                    (shuffled[j2].Item1[fi], shuffled[i].Item1[fi]);
            }
            double shuffleAcc = shuffled.Average(s =>
            {
                double p2 = ForwardCls(model, s.Item1);
                return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
            });
            importance[fi] = Math.Max(0.0, baseAcc - shuffleAcc);
        }
        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FEATURE PRUNING + MASKING
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double minImportance, int F)
    {
        var mask = new bool[F];
        if (minImportance <= 0.0) { Array.Fill(mask, true); return mask; }
        double equalShare = 1.0 / F;
        for (int fi = 0; fi < F; fi++)
            mask[fi] = importance[fi] >= minImportance * equalShare;
        // Never prune everything
        if (mask.All(m2 => !m2)) Array.Fill(mask, true);
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        int activeF = mask.Count(m2 => m2);
        return samples.Select(s =>
        {
            var f2 = new float[activeF];
            int idx = 0;
            for (int fi = 0; fi < mask.Length; fi++) if (mask[fi]) f2[idx++] = s.Features[fi];
            return s with { Features = f2 };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DECISION BOUNDARY + DURBIN-WATSON + MI REDUNDANCY
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        // Approximate boundary distance as |p - 0.5| normalised by variance
        var dists = calSet.Select(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return Math.Abs(p2 - 0.5) * 2.0;
        }).ToList();
        double mean = dists.Average();
        double std2 = dists.Count > 1
            ? Math.Sqrt(dists.Sum(d => (d - mean) * (d - mean)) / (dists.Count - 1))
            : 0.0;
        return (mean, std2);
    }

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (trainSet.Count < 3 || magWeights.Length != F) return 2.0;
        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int fi = 0; fi < F; fi++) pred += magWeights[fi] * trainSet[i].Features[fi];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double num = 0.0, den = 0.0;
        for (int i = 1; i < residuals.Length; i++) num += (residuals[i] - residuals[i - 1]) * (residuals[i] - residuals[i - 1]);
        for (int i = 0; i < residuals.Length; i++) den += residuals[i] * residuals[i];
        return den > 1e-9 ? num / den : 2.0;
    }

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 20 || F < 2) return [];
        int bins = (int)Math.Ceiling(1.0 + Math.Log2(trainSet.Count)); // Sturges' rule
        var pairs = new List<string>();

        for (int a = 0; a < F - 1; a++)
        for (int b2 = a + 1; b2 < F; b2++)
        {
            double mi = ComputeMutualInfo(trainSet, a, b2, bins);
            if (mi > threshold)
                pairs.Add($"{MLFeatureHelper.FeatureNames[a]}:{MLFeatureHelper.FeatureNames[b2]}");
        }
        return pairs.ToArray();
    }

    private static double ComputeMutualInfo(List<TrainingSample> samples, int a, int b2, int bins)
    {
        double minA = double.MaxValue, maxA = double.MinValue;
        double minB = double.MaxValue, maxB = double.MinValue;
        foreach (var s in samples)
        {
            if (s.Features[a] < minA) minA = s.Features[a];
            if (s.Features[a] > maxA) maxA = s.Features[a];
            if (s.Features[b2] < minB) minB = s.Features[b2];
            if (s.Features[b2] > maxB) maxB = s.Features[b2];
        }
        if (maxA <= minA || maxB <= minB) return 0.0;
        double wA = (maxA - minA) / bins, wB = (maxB - minB) / bins;
        int n = samples.Count;
        var joint = new int[bins, bins]; var mA = new int[bins]; var mB = new int[bins];
        foreach (var s in samples)
        {
            int bA = Math.Min(bins - 1, (int)((s.Features[a]  - minA) / wA));
            int bB = Math.Min(bins - 1, (int)((s.Features[b2] - minB) / wB));
            joint[bA, bB]++; mA[bA]++; mB[bB]++;
        }
        double mi = 0.0;
        for (int i = 0; i < bins; i++)
        for (int j = 0; j < bins; j++)
        {
            if (joint[i, j] == 0) continue;
            double pij = (double)joint[i, j] / n;
            double pi  = (double)mA[i] / n;
            double pj  = (double)mB[j] / n;
            mi += pij * Math.Log(pij / (pi * pj));
        }
        return mi;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STATIONARITY GATE (variance-ratio proxy ADF)
    // ═══════════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int count = 0;
        for (int fi = 0; fi < F; fi++)
        {
            double prevMean = 0.0, currMean = 0.0;
            int half = samples.Count / 2;
            for (int i = 0;    i < half;           i++) prevMean += samples[i].Features[fi];
            for (int i = half; i < samples.Count;  i++) currMean += samples[i].Features[fi];
            prevMean /= Math.Max(1, half);
            currMean /= Math.Max(1, samples.Count - half);
            double prevVar = 0.0, currVar = 0.0;
            for (int i = 0;    i < half;          i++) { double d = samples[i].Features[fi] - prevMean; prevVar += d * d; }
            for (int i = half; i < samples.Count; i++) { double d = samples[i].Features[fi] - currMean; currVar += d * d; }
            prevVar /= Math.Max(1, half - 1);
            currVar /= Math.Max(1, samples.Count - half - 1);
            // Non-stationary proxy: variance ratio > 3 or mean shift > 2 std devs
            double baseStd = Math.Sqrt(Math.Max(prevVar, 1e-12));
            if (prevVar > 0 && currVar / prevVar > 3.0)     count++;
            else if (Math.Abs(currMean - prevMean) > 2.0 * baseStd) count++;
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO + COVARIATE-SHIFT WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int F, int recentWindowDays, int barsPerDay = 24)
    {
        int recentCount = Math.Min(trainSet.Count / 2,
            Math.Max(10, recentWindowDays * barsPerDay));
        int n = trainSet.Count;

        // Train a logistic discriminator: label=1 for "recent", 0 for "historical"
        var w = new double[F]; double b = 0.0;
        for (int iter = 0; iter < 50; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            for (int i = 0; i < n; i++)
            {
                double y  = i >= n - recentCount ? 1.0 : 0.0;
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * trainSet[i].Features[fi];
                double p  = Sigmoid(pred);
                double err = p - y;
                db += err;
                for (int fi = 0; fi < F; fi++) dw[fi] += err * trainSet[i].Features[fi];
            }
            double lr2 = 0.01 / n;
            b -= lr2 * db;
            for (int fi = 0; fi < F; fi++) w[fi] -= lr2 * dw[fi];
        }

        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = b;
            for (int fi = 0; fi < F; fi++) pred += w[fi] * trainSet[i].Features[fi];
            double p = Math.Clamp(Sigmoid(pred), 0.05, 0.95);
            weights[i] = p / (1.0 - p);
        }
        // Clip to [0.1, 10] to prevent extreme upweighting
        for (int i = 0; i < n; i++) weights[i] = Math.Clamp(weights[i], 0.1, 10.0);
        return weights;
    }

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBreakpoints, int F)
    {
        int n = trainSet.Count;
        var weights = new double[n];
        Array.Fill(weights, 1.0);

        int usedF = Math.Min(F, parentBreakpoints.Length);
        for (int i = 0; i < n; i++)
        {
            double novelty = 0.0;
            for (int fi = 0; fi < usedF; fi++)
            {
                var bp = parentBreakpoints[fi];
                if (bp is not { Length: > 0 }) continue;
                double val = trainSet[i].Features[fi];
                if (val < bp[0] || val > bp[^1]) novelty += 1.0;
            }
            weights[i] = 1.0 + novelty / Math.Max(1, usedF);
        }
        // Normalise
        double wMax = weights.Max();
        if (wMax > 0) for (int i = 0; i < n; i++) weights[i] /= wMax;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TEMPORAL DECAY WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int n, double lambda)
    {
        var weights = new double[n];
        if (lambda <= 0)
        {
            Array.Fill(weights, 1.0);
            return weights;
        }
        for (int i = 0; i < n; i++)
            weights[i] = Math.Exp(-lambda * (n - 1 - i));
        return weights;
    }

    private static double Std(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double mean = values.Average();
        double sumSq = values.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    private static double ComputeLinearSlope(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double n    = values.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < values.Count; i++)
        {
            sumX  += i; sumY  += values[i];
            sumXY += i * values[i]; sumX2 += i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        return Math.Abs(denom) > 1e-9 ? (n * sumXY - sumX * sumY) / denom : 0.0;
    }
}
