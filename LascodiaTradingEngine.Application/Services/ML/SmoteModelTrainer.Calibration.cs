using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SmoteModelTrainer
{

    // ── Magnitude regressors ──────────────────────────────────────────────────

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet,
        int                  F,
        TrainingHyperparams  hp)
    {
        double lr  = hp.LearningRate > 0 ? hp.LearningRate * 0.1 : 0.001;
        double l2  = hp.L2Lambda     > 0 ? hp.L2Lambda            : 0.001;
        int epochs = Math.Max(5, hp.MaxEpochs / 4);

        var    w = new double[F];
        double b = 0;
        var    bestW = new double[F];
        double bestB = 0;

        var indices = Enumerable.Range(0, trainSet.Count).ToArray();
        var shuffleRng = new Random(42);

        for (int ep = 0; ep < epochs; ep++)
        {
            for (int si = indices.Length - 1; si > 0; si--)
            {
                int sj = shuffleRng.Next(si + 1);
                (indices[si], indices[sj]) = (indices[sj], indices[si]);
            }

            for (int ii = 0; ii < indices.Length; ii++)
            {
                var s = trainSet[indices[ii]];
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = Math.Clamp(pred - s.Magnitude, -10.0, 10.0);
                b -= lr * err;
                for (int j = 0; j < F; j++)
                {
                    w[j] -= lr * err * s.Features[j];
                    w[j] *= (1.0 - lr * l2); // decoupled weight decay
                }
            }

            // NaN/Inf guard — rollback to last good state
            bool bad = !double.IsFinite(b);
            if (!bad) for (int j = 0; j < F; j++) if (!double.IsFinite(w[j])) { bad = true; break; }
            if (bad) { Array.Copy(bestW, w, F); b = bestB; break; }
            Array.Copy(w, bestW, F); bestB = b;
        }

        return (w, b);
    }

    // L11: Quantile magnitude regressor (pinball loss)
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet,
        int                  F,
        double               tau)
    {
        double lr  = 0.001;
        double l2  = 0.001;
        int epochs = 20;

        var    w = new double[F];
        double b = 0;
        var    bestW = new double[F];
        double bestB = 0;

        var indices = Enumerable.Range(0, trainSet.Count).ToArray();
        var shuffleRng = new Random(42);

        for (int ep = 0; ep < epochs; ep++)
        {
            for (int si = indices.Length - 1; si > 0; si--)
            {
                int sj = shuffleRng.Next(si + 1);
                (indices[si], indices[sj]) = (indices[sj], indices[si]);
            }

            for (int ii = 0; ii < indices.Length; ii++)
            {
                var s = trainSet[indices[ii]];
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double residual = s.Magnitude - pred;
                double dLdpred = residual >= 0 ? -tau : (1.0 - tau);
                b -= lr * dLdpred;
                for (int j = 0; j < F; j++)
                {
                    w[j] -= lr * dLdpred * s.Features[j];
                    w[j] *= (1.0 - lr * l2); // decoupled weight decay
                }
            }

            bool bad = !double.IsFinite(b);
            if (!bad) for (int j = 0; j < F; j++) if (!double.IsFinite(w[j])) { bad = true; break; }
            if (bad) { Array.Copy(bestW, w, F); b = bestB; break; }
            Array.Copy(w, bestW, F); bestB = b;
        }

        return (w, b);
    }

    // ── H5: Stacking meta-learner ─────────────────────────────────────────────

    private static MetaLearner FitMetaLearner(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW   = null,
        double[][]?          mlpHB   = null,
        int                  hidDim  = 0)
    {
        int K = weights.Length;
        if (calSet.Count < MinCalSamples || K < 2) return MetaLearner.None;

        var mw = new double[K];
        double mb = 0;
        const double lr = 0.01;
        const int    ep = DefaultCalibrationEpochs;

        for (int e = 0; e < ep; e++)
        {
            double dmb = 0;
            var dw = new double[K];
            foreach (var s in calSet)
            {
                double metaLogit = mb;
                var    perK      = new double[K];
                for (int k = 0; k < K; k++)
                {
                    perK[k] = SingleLearnerProb(s.Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
                    metaLogit += mw[k] * perK[k];
                }
                double p   = Sigmoid(metaLogit);
                double err = p - (s.Direction > 0 ? 1.0 : 0.0);
                dmb += err;
                for (int k = 0; k < K; k++) dw[k] += err * perK[k];
            }
            double inv = 1.0 / calSet.Count;
            mb -= lr * dmb * inv;
            for (int k = 0; k < K; k++) mw[k] -= lr * dw[k] * inv;
        }

        return new MetaLearner(mw, mb);
    }

    // ── Platt calibration ─────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW   = null,
        double[][]?          mlpHB   = null,
        int                  hidDim  = 0)
    {
        double A = 1.0, B = 0.0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double dA = 0, dB = 0;
            foreach (var s in calSet)
            {
                double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double calibP = Sigmoid(A * logit + B);
                double err    = calibP - (s.Direction > 0 ? 1.0 : 0.0);
                dA += err * logit;
                dB += err;
            }
            double inv = 1.0 / calSet.Count;
            A -= lr * dA * inv;
            B -= lr * dB * inv;
        }

        return (A, B);
    }

    // ── M3: Class-conditional Platt ───────────────────────────────────────────

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW = null,
        double[][]?          mlpHB = null,
        int                  hidDim = 0)
    {
        // Split by predicted direction (not true label) so each subset contains
        // both correct and incorrect predictions — making calibration well-posed
        var buyPredSet  = new List<TrainingSample>();
        var sellPredSet = new List<TrainingSample>();
        foreach (var s in calSet)
        {
            double rawP = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            if (rawP >= 0.5)
                buyPredSet.Add(s);
            else
                sellPredSet.Add(s);
        }

        if (buyPredSet.Count < 5 || sellPredSet.Count < 5)
            return (1.0, 0.0, 1.0, 0.0); // identity on logit scale

        static (double A, double B) FitOnSubset(
            List<TrainingSample> sub, double[][] w, double[] b, int f,
            int[][]? subs, MetaLearner m, double[][]? mHW, double[][]? mHB, int hd)
        {
            double A = 1.0, B = 0.0;
            const double lr = 0.01;
            for (int ep = 0; ep < ClassCondPlattEpochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var s in sub)
                {
                    double rawP   = EnsembleProb(s.Features, w, b, f, subs, m, mHW, mHB, hd);
                    double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                    double calibP = Sigmoid(A * logit + B);
                    double err    = calibP - (s.Direction > 0 ? 1.0 : 0.0);
                    dA += err * logit; dB += err;
                }
                double inv = 1.0 / sub.Count;
                A -= lr * dA * inv;
                B -= lr * dB * inv;
            }
            return (A, B);
        }

        var (AB, BB) = FitOnSubset(buyPredSet,  weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var (AS, BS) = FitOnSubset(sellPredSet, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        return (AB, BB, AS, BS);
    }

    // ── H8: Isotonic calibration (PAVA) ──────────────────────────────────────

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
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
        if (calSet.Count < MinEvalSamples) return [];

        // Collect (platt-calibrated probability, label) pairs, sorted by probability
        var pairs = new List<(double P, double Y)>(calSet.Count);
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            pairs.Add((calibP, s.Direction > 0 ? 1.0 : 0.0));
        }
        pairs.Sort((a, b) => a.P.CompareTo(b.P));

        // Pool Adjacent Violators Algorithm (PAVA) — O(n) stack-based.
        // Each block stores (sum of Y values, count). Invariant: mean of each block
        // is non-decreasing from bottom to top of the stack.
        int n = pairs.Count;
        var stack = new List<(double SumY, int Count)>(n);
        foreach (var (_, y) in pairs)
        {
            stack.Add((y, 1));
            // Merge backward while the block below has a larger mean (violates isotonicity)
            while (stack.Count >= 2)
            {
                var (loSumY, loCount) = stack[^2];
                var (hiSumY, hiCount) = stack[^1];
                if (loSumY / loCount <= hiSumY / hiCount) break;
                stack.RemoveAt(stack.Count - 1);
                stack[^1] = (loSumY + hiSumY, loCount + hiCount);
            }
        }

        // Expand blocks back to per-sample isotonic values
        var isotonic = new double[n];
        int pos = 0;
        foreach (var (sumY, count) in stack)
        {
            double mean = sumY / count;
            for (int bi = 0; bi < count; bi++) isotonic[pos++] = mean;
        }

        // Encode as interleaved [x0, y0, x1, y1, ...] breakpoints
        // Stride-sample to cap size (preserves quantile distribution like jackknife residuals)
        const int MaxIsotonicBreakpoints = 1_000;
        int stride = n > MaxIsotonicBreakpoints ? n / MaxIsotonicBreakpoints : 1;
        int capacity = (n / stride + 1) * 2;
        var bps = new List<double>(capacity);
        for (int i = 0; i < n; i += stride) { bps.Add(pairs[i].P); bps.Add(isotonic[i]); }
        // Always include the last point for boundary coverage
        if (stride > 1 && n > 0) { bps.Add(pairs[n - 1].P); bps.Add(isotonic[n - 1]); }
        return [.. bps];
    }

    // ── M11: Temperature scaling ──────────────────────────────────────────────

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        double T = 1.0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double dT = 0;
            foreach (var s in calSet)
            {
                double rawP  = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double sP    = Sigmoid(logit / T);
                double err   = sP - (s.Direction > 0 ? 1.0 : 0.0);
                // dL/dT = err * sP * (1-sP) * (-logit/T²)
                dT += err * sP * (1 - sP) * (-logit / (T * T));
            }
            T -= lr * dT / calSet.Count;
            T  = Math.Max(0.1, Math.Min(10.0, T)); // sanity bounds
        }

        return T;
    }

    // ── H11: Meta-label secondary classifier ─────────────────────────────────

    private static (double[] Weights, double Bias, int[] TopFeatureIndices) FitMetaLabelModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW            = null,
        double[][]?          mlpHB            = null,
        int                  hidDim           = 0,
        double[]?            importanceScores = null)
    {
        int[] defaultTop3 = [0, Math.Min(1, Math.Max(0, F - 1)), Math.Min(2, Math.Max(0, F - 1))];
        if (calSet.Count < MinCalSamples) return ([], 0.0, defaultTop3);

        // Determine top-3 feature indices by cal-set importance; fall back to [0,1,2]
        int[] top3 = importanceScores is { Length: > 0 }
            ? [.. Enumerable.Range(0, Math.Min(F, importanceScores.Length))
                            .OrderByDescending(j => importanceScores[j])
                            .Take(3)]
            : [0, Math.Min(1, F - 1), Math.Min(2, F - 1)];

        const int MetaInputDim = 7;
        var mw = new double[MetaInputDim];
        double mb = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double[] dw = new double[MetaInputDim];
            double   db = 0;

            foreach (var s in calSet)
            {
                var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
                double logit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
                double distFromBoundary = Math.Abs(avgP - 0.5);
                double maxP = double.MinValue, minP = double.MaxValue;
                for (int k2 = 0; k2 < weights.Length; k2++)
                {
                    double pk = SingleLearnerProb(s.Features, weights[k2], biases[k2],
                        featureSubsets?[k2], F, mlpHW?[k2], mlpHB?[k2], hidDim);
                    if (pk > maxP) maxP = pk;
                    if (pk < minP) minP = pk;
                }
                double maxDisagreement = maxP - minP;
                double[] x = [logit, stdP,
                               top3[0] < s.Features.Length ? s.Features[top3[0]] : 0.0f,
                               top3[1] < s.Features.Length ? s.Features[top3[1]] : 0.0f,
                               top3[2] < s.Features.Length ? s.Features[top3[2]] : 0.0f,
                               distFromBoundary, maxDisagreement];

                double metaLogit = mb;
                for (int j = 0; j < MetaInputDim; j++) metaLogit += mw[j] * x[j];
                double p   = Sigmoid(metaLogit);
                int yHat = avgP >= 0.5 ? 1 : 0;
                int yTrue = s.Direction > 0 ? 1 : 0;
                double y = yHat == yTrue ? 1.0 : 0.0;
                double err = p - y;
                db += err;
                for (int j = 0; j < MetaInputDim; j++) dw[j] += err * x[j];
            }
            double inv = 1.0 / calSet.Count;
            mb -= lr * db * inv;
            for (int j = 0; j < MetaInputDim; j++) mw[j] -= lr * (dw[j] * inv + 0.001 * mw[j]);
        }

        return (mw, mb, top3);
    }

    private static double ComputeMetaLabelScore(
        float[] features, double avgP, double stdP,
        double[][] weights, double[] biases, int F, int[][]? featureSubsets,
        double[][]? mlpHW, double[][]? mlpHB, int hidDim,
        double[] metaLabelWeights, double metaLabelBias, int[] topFeatures)
    {
        if (metaLabelWeights.Length == 0) return 0.5;
        double rawLogit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
        double distFromBoundary = Math.Abs(avgP - 0.5);
        double maxP = double.MinValue, minP = double.MaxValue;
        for (int k2 = 0; k2 < weights.Length; k2++)
        {
            double pk = SingleLearnerProb(features, weights[k2], biases[k2],
                featureSubsets?[k2], F, mlpHW?[k2], mlpHB?[k2], hidDim);
            if (pk > maxP) maxP = pk;
            if (pk < minP) minP = pk;
        }
        double maxDisagreement = maxP - minP;
        double metaLogit = metaLabelBias;
        double[] mx = [rawLogit, stdP,
            topFeatures.Length > 0 && topFeatures[0] < features.Length ? features[topFeatures[0]] : 0,
            topFeatures.Length > 1 && topFeatures[1] < features.Length ? features[topFeatures[1]] : 0,
            topFeatures.Length > 2 && topFeatures[2] < features.Length ? features[topFeatures[2]] : 0,
            distFromBoundary, maxDisagreement];
        for (int j = 0; j < Math.Min(metaLabelWeights.Length, mx.Length); j++)
            metaLogit += metaLabelWeights[j] * mx[j];
        return Sigmoid(metaLogit);
    }

    // ── H12: Abstention gate ──────────────────────────────────────────────────

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        int[]                metaLabelTopFeatures,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (calSet.Count < MinCalSamples || metaLabelWeights.Length == 0) return ([], 0.0, 0.5);

        // Input features for abstention gate: [calibP, ensStd, metaLabelScore]
        var aw = new double[3];
        double ab = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double[] dw = new double[3];
            double   db = 0;

            foreach (var s in calSet)
            {
                var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
                double rawLogit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
                double calibP   = Sigmoid(plattA * rawLogit + plattB);

                double metaScore = ComputeMetaLabelScore(s.Features, avgP, stdP,
                    weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim,
                    metaLabelWeights, metaLabelBias, metaLabelTopFeatures);

                double[] x = [calibP, stdP, metaScore];
                double absLogit = ab;
                for (int j = 0; j < 3; j++) absLogit += aw[j] * x[j];
                double p = Sigmoid(absLogit);
                double y = (calibP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0) ? 1.0 : 0.0;
                double err = p - y;
                db += err;
                for (int j = 0; j < 3; j++) dw[j] += err * x[j];
            }
            double inv = 1.0 / calSet.Count;
            ab -= lr * db * inv;
            for (int j = 0; j < 3; j++) aw[j] -= lr * (dw[j] * inv + 0.001 * aw[j]);
        }

        // Cost-sensitive threshold: maximize net profit on cal set
        var sampleScores = new (double Score, bool Correct, double Magnitude)[calSet.Count];
        for (int si = 0; si < calSet.Count; si++)
        {
            var s = calSet[si];
            var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
            double rawLogit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
            double calibP = Sigmoid(plattA * rawLogit + plattB);
            double metaScore = ComputeMetaLabelScore(s.Features, avgP, stdP,
                weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim,
                metaLabelWeights, metaLabelBias, metaLabelTopFeatures);
            double[] x = [calibP, stdP, metaScore];
            double absLogit = ab;
            for (int j = 0; j < 3; j++) absLogit += aw[j] * x[j];
            double score = Sigmoid(absLogit);
            bool correct = (calibP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0);
            sampleScores[si] = (score, correct, Math.Abs(s.Magnitude));
        }

        double bestProfit = double.MinValue;
        double threshold = 0.5;
        for (double t = 0.1; t <= 0.9; t += 0.02)
        {
            double profit = 0;
            foreach (var (score, correct, mag) in sampleScores)
            {
                if (score >= t)
                    profit += correct ? mag : -mag;
            }
            if (profit > bestProfit) { bestProfit = profit; threshold = t; }
        }

        return (aw, ab, threshold);
    }

    // ── H9: Conformal qHat ────────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        double               alpha  = 0.10)
    {
        if (calSet.Count < MinEvalSamples) return 0.5;

        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s       = calSet[i];
            double rawP = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            // Apply isotonic if available
            if (isotonicBp.Length >= 4) calibP = ApplyIsotonicCalibration(isotonicBp, calibP);

            int yTrue = s.Direction > 0 ? 1 : 0;
            // Non-conformity score = 1 - P(true class)
            scores[i] = yTrue == 1 ? 1.0 - calibP : calibP;
        }

        Array.Sort(scores);
        int idx = (int)Math.Ceiling((1.0 - alpha) * (scores.Length + 1)) - 1;
        idx = Math.Clamp(idx, 0, scores.Length - 1);
        return scores[idx];
    }

    // ── H10: Jackknife+ residuals (OOB-approximated) ───────────────────────────
    //
    // True Jackknife+ requires N leave-one-out retrains. We approximate by using
    // OOB predictions: for each sample, average only learners where the sample was
    // out-of-bag. This gives an honest (not seen during training) prediction per
    // sample, closely approximating leave-one-out without the N× retraining cost.

    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        bool[][]             oobMasks,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K   = weights.Length;
        var res = new List<double>(trainSet.Count);

        for (int i = 0; i < trainSet.Count; i++)
        {
            var s = trainSet[i];
            double yTrue = s.Direction > 0 ? 1.0 : 0.0;

            // Average predictions only from learners where this sample is OOB
            double sumP = 0;
            int oobCount = 0;
            for (int k = 0; k < K; k++)
            {
                if (i < oobMasks[k].Length && oobMasks[k][i])
                {
                    sumP += SingleLearnerProb(s.Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
                    oobCount++;
                }
            }
            if (oobCount == 0) continue; // skip samples seen by all learners
            double rawP = sumP / oobCount;
            res.Add(Math.Abs(yTrue - rawP));
        }

        res.Sort();

        // Cap serialised residuals to avoid outsized snapshot payloads.
        // Stride-sample the sorted list so the empirical quantile distribution
        // is preserved — quantile lookups in MLSignalScorer remain accurate.
        // MaxJackknifeResiduals is defined as a class-level constant
        if (res.Count > MaxJackknifeResiduals)
        {
            int stride  = res.Count / MaxJackknifeResiduals;
            var sampled = new List<double>(MaxJackknifeResiduals);
            for (int i = 0; i < res.Count; i += stride)
                sampled.Add(res[i]);
            return [.. sampled];
        }

        return [.. res];
    }

    // ── EV-optimal threshold ──────────────────────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        double               lo     = 0.30,
        double               hi     = 0.70)
    {
        double best   = 0.50;
        double bestEV = double.MinValue;

        for (double t = lo; t <= hi + 1e-9; t += ThresholdSearchStep)
        {
            // Only count samples where the model would trade (calibP >= t for buy, calibP <= 1-t for sell)
            int wins = 0, trades = 0;
            foreach (var s in calSet)
            {
                double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double calibP = Sigmoid(plattA * logit + plattB);
                bool wouldTrade = calibP >= t || calibP <= (1.0 - t);
                if (!wouldTrade) continue;
                trades++;
                int yHat = calibP >= 0.5 ? 1 : 0;
                if (yHat == (s.Direction > 0 ? 1 : 0)) wins++;
            }
            // EV = win_rate - (1 - win_rate) = 2 * win_rate - 1, scaled by trade frequency
            double ev = trades > 0 ? ((double)wins / trades * 2.0 - 1.0) * trades / calSet.Count : 0.0;
            if (ev > bestEV) { bestEV = ev; best = t; }
        }

        return best;
    }

    // ── M9: Average Kelly fraction ────────────────────────────────────────────

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
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
        if (calSet.Count == 0) return 0.0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            sum += Math.Max(0, 2 * calibP - 1) * 0.5; // half-Kelly
        }
        return sum / calSet.Count;
    }

    // ── Isotonic calibration application ─────────────────────────────────────

    private static double ApplyIsotonicCalibration(double[] bps, double prob)
    {
        if (bps.Length < 4) return prob;
        int n = bps.Length / 2;

        if (prob <= bps[0]) return bps[1];
        if (prob >= bps[(n - 1) * 2]) return bps[(n - 1) * 2 + 1];

        for (int i = 0; i < n - 1; i++)
        {
            double x0 = bps[i * 2], y0 = bps[i * 2 + 1];
            double x1 = bps[(i + 1) * 2], y1 = bps[(i + 1) * 2 + 1];
            if (prob >= x0 && prob <= x1 && x1 > x0)
                return y0 + (y1 - y0) * (prob - x0) / (x1 - x0);
        }
        return prob;
    }
}
