using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class QuantileRfModelTrainer
{

    // ── Permutation feature importance on test set (Fisher-Yates, seed 42) ────

    /// <summary>
    /// #28: Multi-shuffle permutation importance. Averages across <paramref name="repeats"/>
    /// independent shuffles to reduce variance in importance estimates.
    /// </summary>
    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  F,
        int                  seed = 42,
        int                  repeats = DefaultPermutationRepeats)
    {
        int n = testSet.Count;
        repeats = Math.Max(1, repeats);

        int baseCorrect = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / n;

        var importance = new float[F];
        var shuffled   = new float[n];
        var featBuf    = new float[F];

        for (int fi = 0; fi < F; fi++)
        {
            double dropSum = 0;
            for (int rep = 0; rep < repeats; rep++)
            {
                var rng = seed != 0 ? new Random(seed + fi * repeats + rep + 1) : new Random();
                for (int i = 0; i < n; i++) shuffled[i] = testSet[i].Features[fi];
                for (int i = n - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }

                int correct = 0;
                for (int i = 0; i < n; i++)
                {
                    var orig = testSet[i].Features;
                    int fLen = Math.Min(orig.Length, F);
                    for (int j = 0; j < fLen; j++) featBuf[j] = orig[j];
                    featBuf[fi] = shuffled[i];
                    double p = PredictProb(featBuf, allTrees, trainSet, plattA, plattB, isotonicBp);
                    if ((p >= 0.5 ? 1 : 0) == (testSet[i].Direction > 0 ? 1 : 0)) correct++;
                }
                dropSum += Math.Max(0.0, baseAcc - (double)correct / n);
            }
            importance[fi] = (float)(dropSum / repeats);
        }

        double total = 0;
        foreach (var v in importance) total += v;
        if (total > 0)
            for (int i = 0; i < importance.Length; i++) importance[i] = (float)(importance[i] / total);

        return importance;
    }

    // ── Calibration-set permutation importance (parallel, for warm-start) ─────

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  F,
        CancellationToken    ct)
    {
        int n = calSet.Count;

        int baseCorrect = 0;
        foreach (var s in calSet)
            if ((PredictRawProb(s.Features, allTrees, trainSet) >= 0.5) == (s.Direction > 0))
                baseCorrect++;
        double baseAcc = (double)baseCorrect / n;

        var importance = new double[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng  = new Random(j * 17 + 7);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (vals[k], vals[i]) = (vals[i], vals[k]);
            }

            var scratch = new float[calSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((PredictRawProb(scratch, allTrees, trainSet) >= 0.5) == (calSet[idx].Direction > 0))
                    correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    // ── Split-conformal q̂ ─────────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        double               alpha = 0.10)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }
        scores.Sort();

        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ── Full evaluation on held-out test set ──────────────────────────────────

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        double[]             isotonicBp)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, double.MaxValue, 0, 1, 0, 0, 0, 0, 0, 0);

        int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, retSumSq = 0;

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    yHat = p >= 0.5 ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);
            double r  = (yHat == y ? 1 : -1) * (double)s.Magnitude;
            evSum    += r;
            retSumSq += r * r;

            double magPred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                magPred += magWeights[j] * s.Features[j];
            double magErr = magPred - s.Magnitude;
            magSse += magErr * magErr;
        }

        int    n         = testSet.Count;
        double accuracy  = (double)correct / n;
        double brier     = brierSum / n;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = evSum / n;
        double magRmse   = Math.Sqrt(magSse / n);

        // Magnitude-weighted Sharpe: mean(r) / std(r) × √252, where r = ±Magnitude per trade.
        // Replaces the (accuracy−0.5)/(brier+0.01) proxy which does not reflect risk-adjusted returns.
        double retMean = ev;
        double retVar  = retSumSq / n - retMean * retMean;
        double retStd  = retVar > Eps ? Math.Sqrt(retVar) : 0.0;
        double sharpe  = retStd > Eps ? retMean / retStd * Math.Sqrt(252) : 0.0;

        return new EvalMetrics(
            Accuracy:         accuracy,
            Precision:        precision,
            Recall:           recall,
            F1:               f1,
            MagnitudeRmse:    magRmse,
            ExpectedValue:    ev,
            BrierScore:       brier,
            WeightedAccuracy: accuracy,
            SharpeRatio:      sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ── Brier Skill Score vs. naïve base-rate predictor ───────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp)
    {
        if (testSet.Count == 0) return 0;

        int posCount = 0;
        foreach (var s in testSet) if (s.Direction > 0) posCount++;
        double pBase = (double)posCount / testSet.Count;

        double brierModel = 0, brierNaive = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    y = s.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
            brierNaive += (pBase - y) * (pBase - y);
        }
        brierModel /= testSet.Count;
        brierNaive /= testSet.Count;
        return brierNaive > 1e-15 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ── OOB accuracy (RF-native, uses OOB leaf fractions per new tree) ─────────

    private static double ComputeOobAccuracy(
        List<TrainingSample>  trainSet,
        List<List<TreeNode>>  newTrees,
        List<HashSet<int>>    oobMasks)
    {
        if (trainSet.Count < 10 || newTrees.Count < 2 || oobMasks.Count != newTrees.Count) return 0;

        int correct = 0, evaluated = 0;
        for (int i = 0; i < trainSet.Count; i++)
        {
            double probSum  = 0.0;
            int    oobCount = 0;
            for (int t = 0; t < newTrees.Count; t++)
            {
                if (!oobMasks[t].Contains(i)) continue;
                probSum += GetLeafProb(newTrees[t], 0, trainSet[i].Features);
                oobCount++;
            }

            if (oobCount == 0) continue;

            double oobProb = probSum / oobCount;
            if ((oobProb >= 0.5) == (trainSet[i].Direction > 0)) correct++;
            evaluated++;
        }

        return evaluated > 0 ? (double)correct / evaluated : 0;
    }

    // ── Equity curve stats (max drawdown + Sharpe on simulated ±1% returns) ───

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);

        var    returns = new double[predictions.Length];
        double equity  = 1.0, peak = 1.0, maxDD = 0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted == predictions[i].Actual ? 0.01 : -0.01;
            returns[i] = r;
            equity    += r;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }

        double mean = returns.Average();
        double varSum = 0;
        foreach (double r in returns) varSum += (r - mean) * (r - mean);
        double std    = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        double sharpe = std > 1e-10 ? mean / std * Math.Sqrt(252) : 0;

        return (maxDD, sharpe);
    }

    // ── Sharpe trend (OLS slope across CV folds) ──────────────────────────────

    private static double ComputeSharpeTrend(List<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0;

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
        return Math.Abs(denom) > 1e-15 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    // ── Stationarity gate (lag-1 Pearson correlation as ADF proxy) ────────────

    /// <summary>
    /// #12: Improved stationarity test using a sample-size-aware critical value.
    /// For N &gt; 100, the threshold is relaxed slightly (0.97 − 0.5/√N) to account for
    /// the fact that lag-1 correlation estimates are biased upward in small samples.
    /// </summary>
    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int n = samples.Count;
        if (n < 3) return 0;

        // Sample-size-aware critical value for lag-1 correlation
        double criticalRho = StationarityRhoThreshold - (n > 100 ? 0.5 / Math.Sqrt(n) : 0.0);

        int nonStat = 0;
        for (int fi = 0; fi < F; fi++)
        {
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            int    nc   = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = samples[i].Features[fi];
                double y = samples[i + 1].Features[fi];
                sumX  += x; sumY  += y;
                sumXY += x * y;
                sumX2 += x * x; sumY2 += y * y;
            }
            double varX  = sumX2 - sumX * sumX / nc;
            double varY  = sumY2 - sumY * sumY / nc;
            double denom = Math.Sqrt(Math.Max(0, varX * varY));
            double rho   = denom > 1e-12 ? (sumXY - sumX * sumY / nc) / denom : 0;
            if (Math.Abs(rho) > criticalRho) nonStat++;
        }
        return nonStat;
    }

    // ── Durbin-Watson test on magnitude residuals ─────────────────────────────

    private static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int F)
    {
        if (train.Count < 10 || magWeights.Length == 0) return 2.0;

        var residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, train[i].Features.Length); j++)
                pred += magWeights[j] * train[i].Features[j];
            residuals[i] = train[i].Magnitude - pred;
        }

        double numSum = 0, denSum = 0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double d = residuals[i] - residuals[i - 1];
            numSum += d * d;
        }
        for (int i = 0; i < residuals.Length; i++)
            denSum += residuals[i] * residuals[i];

        return denSum > 1e-15 ? numSum / denSum : 2.0;
    }

    // ── Mutual information feature redundancy (histogram MI, Sturges' rule) ───

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30) return [];

        // #24: Random subsample (seeded) instead of taking the first N samples
        int maxN = Math.Min(trainSet.Count, MaxMiDefaultSamples);
        List<TrainingSample> miSamples;
        if (trainSet.Count > maxN)
        {
            var miRng = new Random(42);
            var indices = Enumerable.Range(0, trainSet.Count).OrderBy(_ => miRng.Next()).Take(maxN).ToList();
            miSamples = [.. indices.Select(i => trainSet[i])];
        }
        else
        {
            miSamples = trainSet;
        }
        int n = miSamples.Count;

        // #25: Freedman-Diaconis bin count — adapts to actual data distribution.
        // Compute IQR of the first feature as representative, fall back to Sturges.
        int numBins;
        {
            var f0 = new double[n];
            for (int i = 0; i < n; i++) f0[i] = miSamples[i].Features.Length > 0 ? miSamples[i].Features[0] : 0;
            Array.Sort(f0);
            double q1 = f0[n / 4], q3 = f0[3 * n / 4];
            double iqr = q3 - q1;
            double range = f0[^1] - f0[0];
            if (iqr > 1e-12 && range > 1e-12)
            {
                double binWidth = 2.0 * iqr / Math.Cbrt(n);
                numBins = Math.Clamp((int)Math.Ceiling(range / binWidth), 5, 50);
            }
            else
            {
                numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
            }
        }

        var featureMin    = new double[F];
        var featureMax    = new double[F];
        var featureBinIdx = new int[F * n];

        Array.Fill(featureMin, double.MaxValue);
        Array.Fill(featureMax, double.MinValue);

        for (int j = 0; j < F; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = miSamples[i].Features[j];
                if (v < featureMin[j]) featureMin[j] = v;
                if (v > featureMax[j]) featureMax[j] = v;
            }
            double range    = featureMax[j] - featureMin[j];
            double binWidth = range > 1e-15 ? range / numBins : 1.0;
            for (int i = 0; i < n; i++)
            {
                int bin = (int)((miSamples[i].Features[j] - featureMin[j]) / binWidth);
                featureBinIdx[j * n + i] = Math.Clamp(bin, 0, numBins - 1);
            }
        }

        var pairs  = new List<string>();
        double invN = 1.0 / n;

        for (int a = 0; a < F; a++)
        {
            for (int bj = a + 1; bj < F; bj++)
            {
                var joint = new int[numBins * numBins];
                var margA = new int[numBins];
                var margB = new int[numBins];

                for (int i = 0; i < n; i++)
                {
                    int ba = featureBinIdx[a  * n + i];
                    int bb = featureBinIdx[bj * n + i];
                    joint[ba * numBins + bb]++;
                    margA[ba]++;
                    margB[bb]++;
                }

                double mi = 0;
                for (int ia = 0; ia < numBins; ia++)
                {
                    if (margA[ia] == 0) continue;
                    double pA = margA[ia] * invN;
                    for (int ib = 0; ib < numBins; ib++)
                    {
                        int jCount = joint[ia * numBins + ib];
                        if (jCount == 0 || margB[ib] == 0) continue;
                        double pJ = jCount * invN;
                        double pB = margB[ib] * invN;
                        mi += pJ * Math.Log(pJ / (pA * pB));
                    }
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a  < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a]  : $"F{a}";
                    string nameB = bj < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[bj] : $"F{bj}";
                    pairs.Add($"{nameA}:{nameB}");
                }
            }
        }
        return [.. pairs];
    }

    // ── Tree ensemble diversity (#5) ──────────────────────────────────────────

    /// <summary>
    /// Samples up to <paramref name="maxSamples"/> training points, computes per-tree
    /// leaf-fraction prediction vectors, and returns the average pairwise Pearson
    /// correlation (lower = more diverse).
    /// </summary>
    private static double ComputeTreeDiversity(
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  maxSamples = MaxDiversitySamples)
    {
        int T = allTrees.Count;
        if (T < 2 || trainSet.Count == 0) return 0.0;

        int sampleCount = Math.Min(maxSamples, trainSet.Count);
        // Deterministic even-spaced sample to avoid LINQ allocation
        var sampleIndices = new int[sampleCount];
        for (int s = 0; s < sampleCount; s++)
            sampleIndices[s] = (int)((long)s * trainSet.Count / sampleCount);

        var predictions = new double[T][];

        for (int t = 0; t < T; t++)
        {
            predictions[t] = new double[sampleCount];
            for (int s = 0; s < sampleCount; s++)
            {
                int    si = sampleIndices[s];
                double p  = GetLeafProb(allTrees[t], 0, trainSet[si].Features);
                predictions[t][s] = double.IsFinite(p) ? p : 0.5;
            }
        }

        // #31: For large T, sample a random subset of pairs instead of O(T²) full scan.
        int totalPairs = T * (T - 1) / 2;
        int maxPairs   = 500;
        double sumCorr = 0.0;
        int    pairs   = 0;

        if (totalPairs <= maxPairs)
        {
            for (int i = 0; i < T; i++)
                for (int j = i + 1; j < T; j++)
                {
                    sumCorr += PearsonCorrelation(predictions[i], predictions[j], sampleCount);
                    pairs++;
                }
        }
        else
        {
            var pairRng = new Random(42);
            for (int p = 0; p < maxPairs; p++)
            {
                int i = pairRng.Next(T);
                int j = pairRng.Next(T - 1);
                if (j >= i) j++;
                sumCorr += PearsonCorrelation(predictions[i], predictions[j], sampleCount);
                pairs++;
            }
        }

        return pairs > 0 ? sumCorr / pairs : 0.0;
    }

    /// <summary>
    /// Pearson correlation between the first <paramref name="len"/> elements of
    /// <paramref name="a"/> and <paramref name="b"/>. Returns 0.0 when either array
    /// has near-zero variance.
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

    // ── Jackknife+ nonconformity residuals (#9) ───────────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals for each training sample:
    /// r_i = |trueLabel − oobProb_i| where oobProb_i is the average leaf-fraction
    /// probability over trees for which sample i was out-of-bag.
    /// Returns residuals sorted in ascending order; empty when the training set is
    /// too small or no OOB membership exists.
    /// Only <paramref name="newTrees"/> have associated <paramref name="oobMasks"/>.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        List<List<TreeNode>> newTrees,
        List<HashSet<int>>   oobMasks)
    {
        if (trainSet.Count < 20 || newTrees.Count == 0 || oobMasks.Count != newTrees.Count)
            return [];

        var residuals = new List<double>(trainSet.Count);

        for (int i = 0; i < trainSet.Count; i++)
        {
            double probSum  = 0.0;
            int    oobCount = 0;
            for (int t = 0; t < newTrees.Count; t++)
            {
                if (!oobMasks[t].Contains(i)) continue;
                probSum += GetLeafProb(newTrees[t], 0, trainSet[i].Features);
                oobCount++;
            }
            if (oobCount == 0) continue;

            double oobProb   = probSum / oobCount;
            double trueLabel = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - oobProb));
        }

        residuals.Sort();
        return [.. residuals];
    }

    // ── Population Stability Index (PSI) across features ─────────────────────

    /// <summary>
    /// Computes the average PSI between the current training distribution and the parent
    /// model's distribution (represented by its quantile breakpoints).
    /// Current samples are binned into the parent's quantile intervals; the fraction in
    /// each bin is compared to the expected uniform fraction (1 / numBins).
    /// PSI = Σ_bins (actual% − expected%) × ln(actual% / expected%).
    /// Averaged over all features for which parent breakpoints are available.
    /// </summary>
    private static double ComputeAvgPsi(
        List<TrainingSample> trainSet,
        double[][]           parentBp,
        int                  F)
    {
        if (parentBp.Length == 0 || F == 0 || trainSet.Count == 0) return 0.0;

        int    n        = trainSet.Count;
        double totalPsi = 0.0;
        int    computed = 0;

        for (int fi = 0; fi < Math.Min(F, parentBp.Length); fi++)
        {
            double[] bp = parentBp[fi];
            if (bp is not { Length: >= 2 }) continue;

            int    numBins      = bp.Length + 1;          // n breakpoints → n+1 bins
            double expectedFrac = 1.0 / numBins;          // parent decile bins: ~1/numBins each
            var    binCounts    = new int[numBins];

            foreach (var s in trainSet)
            {
                double v   = fi < s.Features.Length ? s.Features[fi] : 0.0;
                int    bin = Array.BinarySearch(bp, v);
                if (bin < 0) bin = ~bin;
                bin = Math.Clamp(bin, 0, numBins - 1);
                binCounts[bin]++;
            }

            double psi = 0.0;
            for (int b = 0; b < numBins; b++)
            {
                double actual = Math.Max((double)binCounts[b] / n, 1e-4);
                double expect = Math.Max(expectedFrac, 1e-4);
                psi += (actual - expect) * Math.Log(actual / expect);
            }
            totalPsi += psi;
            computed++;
        }

        return computed > 0 ? totalPsi / computed : 0.0;
    }

    // ── 2nd-order feature interactions (#27) ──────────────────────────────────

    /// <summary>
    /// #27: Tracks which feature pairs co-occur on root→child paths across all trees.
    /// Returns a list of "FeatureA:FeatureB" pairs that co-occur in more than
    /// <paramref name="minCoOccurrenceFraction"/> of all trees.
    /// </summary>
    private static string[] Compute2ndOrderFeatureInteractions(
        List<List<TreeNode>> allTrees,
        double               minCoOccurrenceFraction = 0.5)
    {
        if (allTrees.Count < 2) return [];

        var pairCounts = new Dictionary<(int, int), int>();
        foreach (var tree in allTrees)
        {
            var featsInTree = new HashSet<int>();
            foreach (var node in tree)
                if (node.SplitFeat >= 0) featsInTree.Add(node.SplitFeat);

            var featList = featsInTree.OrderBy(f => f).ToList();
            for (int i = 0; i < featList.Count; i++)
                for (int j = i + 1; j < featList.Count; j++)
                {
                    var key = (featList[i], featList[j]);
                    pairCounts.TryGetValue(key, out int cnt);
                    pairCounts[key] = cnt + 1;
                }
        }

        int threshold = (int)(allTrees.Count * minCoOccurrenceFraction);
        return [.. pairCounts
            .Where(kv => kv.Value >= threshold)
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv =>
            {
                string a = kv.Key.Item1 < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[kv.Key.Item1] : $"F{kv.Key.Item1}";
                string b = kv.Key.Item2 < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[kv.Key.Item2] : $"F{kv.Key.Item2}";
                return $"{a}×{b}";
            })];
    }
}
