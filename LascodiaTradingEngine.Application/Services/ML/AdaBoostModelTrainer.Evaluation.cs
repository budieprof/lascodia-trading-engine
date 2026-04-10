using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── ECE computation (10 equal-width bins) ─────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        int                  bins       = 10,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (testSet.Count < bins) return 1.0;

        var binAcc  = new double[bins];
        var binConf = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p    = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            int    binI = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[binI] += p;
            if (s.Direction > 0) binAcc[binI]++; // positive-class frequency, not classification accuracy
            binCnt[binI]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            double acc  = binAcc[b]  / binCnt[b];
            double conf = binConf[b] / binCnt[b];
            ece += (double)binCnt[b] / n * Math.Abs(acc - conf);
        }
        return ece;
    }

    // ── EV-optimal decision threshold sweep ───────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        int                  searchMin  = 30,
        int                  searchMax  = 75,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (calSet.Count < 30) return 0.5;

        int minThreshold = Math.Clamp(Math.Min(searchMin, searchMax), 1, 99);
        int maxThreshold = Math.Clamp(Math.Max(searchMin, searchMax), minThreshold, 99);
        var probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = PredictProb(
                calSet[i].Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);

        double bestEv        = double.MinValue;
        double bestThreshold = 0.5;

        for (int ti = minThreshold; ti <= maxThreshold; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (calSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * (double)calSet[i].Magnitude;
            }
            ev /= calSet.Count;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }
        return bestThreshold;
    }

    // ── Permutation feature importance (Fisher-Yates shuffle, fixed seed) ─────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> evalSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        int                  F,
        double               decisionThreshold,
        int                  baseSeed,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        int n = evalSet.Count;
        if (n == 0)
            return new float[F];

        // Baseline accuracy with original features
        int baseCorrect = 0;
        foreach (var s in evalSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, decisionThreshold,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            if ((p >= decisionThreshold ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / n;

        var importance = new float[F];
        var shuffled   = new float[n];     // column buffer
        var featBuf    = new float[F];     // per-sample mutable feature vector

        for (int fi = 0; fi < F; fi++)
        {
            // Copy and Fisher-Yates shuffle the fi-th feature column
            var localRng = CreateSeededRandom(baseSeed, 42 + fi * 17);
            for (int i = 0; i < n; i++) shuffled[i] = evalSet[i].Features[fi];
            for (int i = n - 1; i > 0; i--)
            {
                int j = localRng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int correct = 0;
            for (int i = 0; i < n; i++)
            {
                var orig = evalSet[i].Features;
                int fLen = Math.Min(orig.Length, F);
                for (int j = 0; j < fLen; j++) featBuf[j] = orig[j];
                featBuf[fi] = shuffled[i];
                double p = PredictProb(
                    featBuf, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, decisionThreshold,
                    plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
                if ((p >= decisionThreshold ? 1 : 0) == (evalSet[i].Direction > 0 ? 1 : 0)) correct++;
            }

            double shuffledAcc = (double)correct / n;
            importance[fi] = (float)Math.Max(0.0, baseAcc - shuffledAcc);
        }

        // Normalise to sum to 1
        double total = 0;
        foreach (var v in importance) total += v;
        if (total > 0)
            for (int i = 0; i < importance.Length; i++) importance[i] = (float)(importance[i] / total);

        return importance;
    }

    // ── Full evaluation on held-out test set ──────────────────────────────────

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               decisionThreshold = 0.5,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, double.MaxValue, 0, 1, 0, 0, 0, 0, 0, 0);

        int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, retSumSq = 0;

        foreach (var s in testSet)
        {
            double p    = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, decisionThreshold,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            int    yHat = p >= decisionThreshold ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);
            double ret = (yHat == y ? 1 : -1) * (double)s.Magnitude;
            evSum    += ret;
            retSumSq += ret * ret;

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

        // Proper equity-curve Sharpe: mean(r) / std(r) over magnitude-weighted returns
        // Uses sample variance (Bessel's correction, n-1 denominator) consistent with
        // ComputeEquityCurveStats and standard Sharpe ratio estimation.
        double retMean = ev;
        double retVar  = n > 1 ? (retSumSq / n - retMean * retMean) * ((double)n / (n - 1)) : 0.0;
        double retStd  = retVar > 1e-15 ? Math.Sqrt(retVar) : 0.0;
        double sharpe  = retStd < 1e-10 ? 0.0 : retMean / retStd;

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

    // ── Brier Skill Score vs. naive base-rate predictor ───────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (testSet.Count == 0) return 0;

        int posCount = 0;
        foreach (var s in testSet) if (s.Direction > 0) posCount++;
        double pBase = (double)posCount / testSet.Count;

        double brierModel = 0, brierNaive = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            int    y = s.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
            brierNaive += (pBase - y) * (pBase - y);
        }
        brierModel /= testSet.Count;
        brierNaive /= testSet.Count;
        return brierNaive > 1e-15 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ── Equity-curve statistics (max drawdown + Sharpe on fold predictions) ───

    /// <summary>
    /// Computes the maximum drawdown and Sharpe ratio of the simulated equity curve
    /// from a realised return series.
    /// Each element is the magnitude-weighted return for one prediction.
    /// Returns (MaxDrawdown, Sharpe); both 0.0 for empty input.
    /// </summary>
    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(double[] returns)
    {
        if (returns.Length == 0) return (0.0, 0.0);

        double equity  = 1.0;
        double peak    = 1.0;
        double maxDD   = 0.0;

        for (int i = 0; i < returns.Length; i++)
        {
            equity += returns[i];
            if (equity > peak) peak = equity;
            double dd = peak > 1e-10 ? (peak - equity) / peak : 0.0;
            if (dd > maxDD) maxDD = dd;
        }

        double mean = 0.0;
        foreach (double r in returns) mean += r;
        mean /= returns.Length;

        double variance = 0.0;
        foreach (double r in returns) { double d = r - mean; variance += d * d; }
        double std    = returns.Length > 1 ? Math.Sqrt(variance / (returns.Length - 1)) : 0.0;
        double sharpe = std < 1e-10 ? 0.0 : mean / std;

        return (maxDD, sharpe);
    }

    // ── Cal-set permutation importance ────────────────────────────────────────

    /// <summary>
    /// Computes permutation feature importance on the calibration set using the raw AdaBoost
    /// score (no Platt) for speed. Each feature is Fisher-Yates shuffled with a per-feature
    /// fixed seed; drop in accuracy relative to baseline = importance.
    /// Returns importance normalised to sum to 1; length F.
    /// </summary>
    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        int                  F,
        int                  baseSeed = 1)
    {
        if (calSet.Count < 10 || F == 0) return new double[F];

        int m = calSet.Count;

        // Baseline accuracy (no Platt — consistency with fast CV evaluation)
        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double score = PredictScore(s.Features, stumps, alphas);
            if ((score >= 0 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / m;

        var importance = new double[F];
        var scratch    = new float[calSet[0].Features.Length];

        for (int j = 0; j < F; j++)
        {
            // Clone column j and Fisher-Yates shuffle (per-feature seed for reproducibility)
            var vals = new float[m];
            for (int i = 0; i < m; i++) vals[i] = calSet[i].Features[j];
            var localRng = CreateSeededRandom(baseSeed, j * 17 + 99);
            for (int i = vals.Length - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            int shuffledCorrect = 0;
            for (int idx = 0; idx < m; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double score = PredictScore(scratch, stumps, alphas);
                if ((score >= 0 ? 1 : 0) == (calSet[idx].Direction > 0 ? 1 : 0))
                    shuffledCorrect++;
            }
            importance[j] = Math.Max(0.0, baseAcc - (double)shuffledCorrect / m);
        }

        // Normalise to sum to 1
        double total = 0;
        foreach (double v in importance) total += v;
        if (total > 1e-10)
            for (int j = 0; j < F; j++) importance[j] /= total;

        return importance;
    }

    // ── Decision boundary distance stats ──────────────────────────────────────

    /// <summary>
    /// Computes mean and standard deviation of the normalised score magnitude
    /// |score|/sumAlpha over the calibration set. Higher values indicate the sample
    /// is far from the decision boundary (AdaBoost analog of ‖∇_x P‖).
    /// Returns (Mean, Std); both 0.0 when the cal set is empty.
    /// </summary>
    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               sumAlpha)
    {
        if (calSet.Count == 0) return (0.0, 0.0);

        double alphaNorm = Math.Max(sumAlpha, 1e-6);
        var    norms     = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double rawP  = MLFeatureHelper.Sigmoid(2 * score);
            // Combine normalised margin with P(1−P) to match logistic convention
            norms[i] = rawP * (1.0 - rawP) * (Math.Abs(score) / alphaNorm);
        }

        double mean = 0.0;
        foreach (double v in norms) mean += v;
        mean /= norms.Length;

        double variance = 0.0;
        foreach (double v in norms) { double d = v - mean; variance += d * d; }
        double std = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0.0;
        return (mean, std);
    }

    // ── Mutual-information redundancy check ───────────────────────────────────

    /// <summary>
    /// Checks pairwise mutual information (MI) between the first 10 features using
    /// a 10×10 bin joint histogram (features assumed to be z-scored, centred near 0).
    /// Feature pairs with MI ≥ threshold × log(2) are flagged as redundant.
    /// Returns an array of "Name_i:Name_j" strings; empty when threshold is 0.
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  F,
        double               threshold,
        int                  topN = 10)
    {
        if (threshold <= 0.0 || trainSet.Count < 20) return [];

        const int NumBin = 10;

        int        checkCount = Math.Min(Math.Max(2, topN), F);
        var        result     = new List<string>();
        double     maxMi      = threshold * Math.Log(2);

        for (int i = 0; i < checkCount; i++)
        {
            for (int j = i + 1; j < checkCount; j++)
            {
                var    joint = new double[NumBin, NumBin];
                var    margI = new double[NumBin];
                var    margJ = new double[NumBin];
                int    n     = 0;

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
                {
                    string nameI = i < MLFeatureHelper.FeatureNames.Length
                        ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
                    string nameJ = j < MLFeatureHelper.FeatureNames.Length
                        ? MLFeatureHelper.FeatureNames[j] : $"F{j}";
                    result.Add($"{nameI}:{nameJ}");
                }
            }
        }

        return [.. result];
    }

    // ── Jackknife+ residuals (half-ensemble LOO proxy) ────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals for AdaBoost.
    /// Since AdaBoost has no bootstrap, we use a half-ensemble leave-K/2-rounds-out proxy:
    /// the "base" prediction uses only the first ⌈K/2⌉ stumps; the residual
    /// r_i = |trueLabel − firstHalfP_i| captures how much residual rounds had to
    /// correct the base prediction for each training sample.
    /// Returns residuals sorted in ascending order; empty when too few stumps.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        List<GbmTree>        stumps,
        List<double>         alphas)
    {
        int K = Math.Min(stumps.Count, alphas.Count);
        if (K < 4 || trainSet.Count < 20) return [];

        int halfK = (K + 1) / 2;
        var halfStumps = stumps[..halfK];
        var halfAlphas = alphas[..halfK];

        var residuals = new List<double>(trainSet.Count);
        foreach (var s in trainSet)
        {
            double halfScore = PredictScore(s.Features, halfStumps, halfAlphas);
            double halfP     = MLFeatureHelper.Sigmoid(2 * halfScore);
            double trueLabel = s.Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - halfP));
        }

        residuals.Sort();
        return [.. residuals];
    }

    // ── Reliability diagram (10 equal-width bins) ─────────────────────────────

    private static (double[] BinConf, double[] BinAcc, int[] BinCounts) ComputeReliabilityDiagram(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold,
        int                  bins = 10)
    {
        var binConf  = new double[bins];
        var binAcc   = new double[bins];
        var binCounts = new int[bins];

        if (testSet.Count < bins)
            return (binConf, binAcc, binCounts);

        foreach (var s in testSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[b] += p;
            if (s.Direction > 0) binAcc[b]++;
            binCounts[b]++;
        }

        for (int b = 0; b < bins; b++)
        {
            if (binCounts[b] > 0)
            {
                binConf[b] /= binCounts[b];
                binAcc[b]  /= binCounts[b];
            }
        }
        return (binConf, binAcc, binCounts);
    }

    // ── Murphy decomposition (Brier = calibration + refinement) ───────────────

    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold,
        int                  bins = 10)
    {
        if (testSet.Count < bins) return (0.0, 0.0);

        var binSumP = new double[bins];
        var binSumY = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            int b = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[b] += p;
            binSumY[b] += s.Direction > 0 ? 1.0 : 0.0;
            binCnt[b]++;
        }

        int    n   = testSet.Count;
        double cal = 0.0, ref_ = 0.0;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            double meanP = binSumP[b] / binCnt[b];
            double meanY = binSumY[b] / binCnt[b];
            double w     = (double)binCnt[b] / n;
            cal  += w * (meanY - meanP) * (meanY - meanP);
            ref_ += w * meanY * (1.0 - meanY);
        }
        return (cal, ref_);
    }

    // ── Calibration residual stats ────────────────────────────────────────────

    private static (double Mean, double Std, double Threshold) ComputeCalibrationResidualStats(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (calSet.Count < 10) return (0.0, 0.0, 1.0);

        var residuals = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = PredictProb(
                calSet[i].Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i] = Math.Abs(p - y);
        }

        double mean = 0.0;
        foreach (double r in residuals) mean += r;
        mean /= residuals.Length;

        double variance = 0.0;
        foreach (double r in residuals) { double d = r - mean; variance += d * d; }
        double std = residuals.Length > 1 ? Math.Sqrt(variance / (residuals.Length - 1)) : 0.0;

        return (mean, std, mean + 2.0 * std);
    }

    // ── Prediction stability score ────────────────────────────────────────────

    private static double ComputePredictionStabilityScore(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (testSet.Count == 0) return 0.0;

        double sum = 0.0;
        foreach (var s in testSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            sum += Math.Abs(p - 0.5);
        }
        return sum / testSet.Count;
    }

    // ── Feature variances ─────────────────────────────────────────────────────

    private static double[] ComputeFeatureVariances(List<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 2) return new double[F];

        var variances = new double[F];
        int n = trainSet.Count;

        for (int j = 0; j < F; j++)
        {
            double sum = 0.0, sumSq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                sum   += v;
                sumSq += v * v;
            }
            double mean = sum / n;
            variances[j] = sumSq / n - mean * mean;
        }
        return variances;
    }

    // ── Split-conformal qHat ──────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               alpha      = 0.10,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double p = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, 0.5,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }
        scores.Sort();

        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    private static double ComputeConformalQHatForLabel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               alpha,
        int                  label,
        double               fallbackQHat,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold)
    {
        var filtered = calSet
            .Where(sample => (sample.Direction > 0 ? 1 : 0) == label)
            .ToList();
        if (filtered.Count < 10)
            return fallbackQHat;

        return ComputeConformalQHat(
            filtered, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, alpha,
            plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
    }
}
