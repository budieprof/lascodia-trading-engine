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

    // ── Platt scaling ─────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        // Pre-compute logits and labels once — ensemble weights are frozen before Platt fitting.
        // Avoids calling EnsembleProb (O(K×F) per sample) on every epoch of the 200-epoch SGD loop.
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            logits[i]  = MLFeatureHelper.Logit(raw);
            labels[i]  = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr = 0.01;
        const int epochs = 200;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= lr * dA / n;
            plattB -= lr * dB / n;
        }

        return (plattA, plattB);
    }

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample>  calSet,
        Func<float[], double> rawProbProvider)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(rawProbProvider(calSet[i].Features), 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr = 0.01;
        const int epochs = 200;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0.0, dB = 0.0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }

            plattA -= lr * dA / n;
            plattB -= lr * dB / n;
        }

        return (plattA, plattB);
    }

    // ── Stacking meta-learner ─────────────────────────────────────────────────

    /// <summary>
    /// Trains a logistic meta-learner that maps per-base-learner probabilities to a final
    /// probability. Fitted on the calibration set (which base learners never saw).
    /// When the cal set is too small, falls back to returning <see cref="MetaLearner.None"/>.
    /// </summary>
    private static MetaLearner FitMetaLearner(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        int K = weights.Length;
        if (calSet.Count < 20 || K < 2) return MetaLearner.None;

        // Pre-compute per-learner probabilities and labels once — ensemble weights are frozen.
        // Avoids calling GetLearnerProbs (O(K×F) per sample) on every epoch of the 300-epoch loop.
        int n = calSet.Count;
        var calLp     = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLp[i]     = GetLearnerProbs(calSet[i].Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        var mw = new double[K];
        for (int k = 0; k < K; k++) mw[k] = 1.0 / K;   // uniform init
        double mb = 0.0;

        const double lr     = 0.01;
        const int    epochs = 300;

        var dW = new double[K]; // pre-allocated — zeroed each epoch instead of re-allocated
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            Array.Clear(dW, 0, K);
            double dB = 0;

            for (int i = 0; i < n; i++)
            {
                var    lp  = calLp[i];
                double z   = mb;
                for (int k = 0; k < K; k++) z += mw[k] * lp[k];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - calLabels[i];
                for (int k = 0; k < K; k++) dW[k] += err * lp[k];
                dB += err;
            }

            for (int k = 0; k < K; k++) mw[k] -= lr * dW[k] / n;
            mb -= lr * dB / n;
        }

        return new MetaLearner(mw, mb);
    }

    private static double ApplyProductionCalibration(
        double   rawP,
        double   plattA,
        double   plattB,
        double   temperatureScale,
        double   plattABuy,
        double   plattBBuy,
        double   plattASell,
        double   plattBSell,
        double[] isotonicBreakpoints,
        double   ageDecayLambda,
        DateTime trainedAtUtc)
    {
        double clampedRaw = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
        double rawLogit = MLFeatureHelper.Logit(clampedRaw);
        double globalCalibP = temperatureScale > 0.0 && temperatureScale < 10.0
            ? MLFeatureHelper.Sigmoid(rawLogit / temperatureScale)
            : MLFeatureHelper.Sigmoid(plattA * rawLogit + plattB);

        double calibP;
        if (globalCalibP >= 0.5 && plattABuy != 0.0)
            calibP = MLFeatureHelper.Sigmoid(plattABuy * rawLogit + plattBBuy);
        else if (globalCalibP < 0.5 && plattASell != 0.0)
            calibP = MLFeatureHelper.Sigmoid(plattASell * rawLogit + plattBSell);
        else
            calibP = globalCalibP;

        if (isotonicBreakpoints.Length >= 4)
            calibP = ApplyIsotonicCalibration(calibP, isotonicBreakpoints);

        if (ageDecayLambda > 0.0 && trainedAtUtc != default)
        {
            double daysSinceTrain = (DateTime.UtcNow - trainedAtUtc).TotalDays;
            double decayFactor = Math.Exp(-ageDecayLambda * Math.Max(0.0, daysSinceTrain));
            calibP = 0.5 + (calibP - 0.5) * decayFactor;
        }

        return Math.Clamp(calibP, 0.0, 1.0);
    }

    // ── EV-optimal decision threshold ─────────────────────────────────────────

    /// <summary>
    /// Sweeps decision thresholds in steps of 0.01 and returns the threshold that
    /// maximises mean expected value (signed magnitude-weighted accuracy).
    /// The search range is configurable via <paramref name="searchMin"/>/<paramref name="searchMax"/>.
    /// </summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta      = default,
        int                  searchMin = 30,
        int                  searchMax = 75,
        int                  stepBps   = 50,
        MlpState             mlp       = default)
    {
        if (dataSet.Count < 30) return 0.5;

        // Pre-compute calibrated probabilities — plain loop avoids LINQ Select+ToArray.
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
        {
            double raw = EnsembleProb(dataSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            probs[i]   = ApplyGlobalPlattCalibration(raw, plattA, plattB);
        }

        double bestEv        = double.MinValue;
        double bestThreshold = 0.5;

        int minBps = Math.Max(0, searchMin * 100);
        int maxBps = Math.Max(minBps, searchMax * 100);
        int effectiveStepBps = stepBps > 0 ? stepBps : 50;

        for (int ti = minBps; ti <= maxBps; ti += effectiveStepBps)
        {
            double t  = ti / 10000.0;
            double ev = 0;

            for (int i = 0; i < dataSet.Count; i++)
            {
                bool predictedUp = probs[i] >= t;
                bool actualUp    = dataSet[i].Direction == 1;
                bool correct     = predictedUp == actualUp;
                ev += (correct ? 1 : -1) * Math.Abs(probs[i] - t) * Math.Abs(dataSet[i].Magnitude);
            }
            ev /= dataSet.Count;

            if (ev > bestEv)
            {
                bestEv        = ev;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }

    private static double ComputeOptimalThreshold(
        List<TrainingSample>  dataSet,
        Func<float[], double> calibratedProb,
        int                   searchMin = 30,
        int                   searchMax = 75,
        int                   stepBps   = 50)
    {
        if (dataSet.Count < 30) return 0.5;

        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = Math.Clamp(calibratedProb(dataSet[i].Features), 0.0, 1.0);

        double bestEv = double.MinValue;
        double bestThreshold = 0.5;

        int minBps = Math.Max(0, searchMin * 100);
        int maxBps = Math.Max(minBps, searchMax * 100);
        int effectiveStepBps = stepBps > 0 ? stepBps : 50;

        for (int ti = minBps; ti <= maxBps; ti += effectiveStepBps)
        {
            double t = ti / 10000.0;
            double ev = 0.0;

            for (int i = 0; i < dataSet.Count; i++)
            {
                bool predictedUp = probs[i] >= t;
                bool actualUp = dataSet[i].Direction == 1;
                bool correct = predictedUp == actualUp;
                ev += (correct ? 1 : -1) * Math.Abs(probs[i] - t) * Math.Abs(dataSet[i].Magnitude);
            }

            ev /= dataSet.Count;
            if (ev > bestEv)
            {
                bestEv = ev;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }

    private static double TuneMetaLabelThreshold(
        List<TrainingSample>                  selectionSet,
        Func<float[], (double Probability, double EnsembleStd)> probabilityAndStdProvider,
        double                                decisionThreshold,
        double[]                              metaLabelWeights,
        double                                metaLabelBias,
        int[]?                                metaLabelTopFeatureIndices)
    {
        if (metaLabelWeights.Length == 0 || selectionSet.Count < 10)
            return 0.5;

        double bestThreshold = 0.5;
        double bestEv = double.MinValue;
        for (int thresholdBps = 3500; thresholdBps <= 7000; thresholdBps += 500)
        {
            double candidate = thresholdBps / 10000.0;
            var evaluation = EvaluateSelectivePolicy(
                selectionSet,
                [],
                0.0,
                probabilityAndStdProvider,
                decisionThreshold,
                metaLabelWeights,
                metaLabelBias,
                candidate,
                metaLabelTopFeatureIndices);
            double score = evaluation.Metrics.ExpectedValue;
            if (score > bestEv + 1e-9 ||
                (Math.Abs(score - bestEv) <= 1e-9 && candidate < bestThreshold))
            {
                bestEv = score;
                bestThreshold = candidate;
            }
        }

        return bestThreshold;
    }

    private static double TuneSelectiveDecisionThreshold(
        List<TrainingSample>                  selectionSet,
        double[]                              magWeights,
        double                                magBias,
        Func<float[], (double Probability, double EnsembleStd)> probabilityAndStdProvider,
        double[]                              metaLabelWeights,
        double                                metaLabelBias,
        double                                metaLabelThreshold,
        int[]?                                metaLabelTopFeatureIndices,
        double[]                              abstentionWeights,
        double                                abstentionBias,
        double                                abstentionThreshold,
        double                                abstentionThresholdBuy,
        double                                abstentionThresholdSell,
        int                                   searchMin,
        int                                   searchMax,
        int                                   stepBps)
    {
        if (selectionSet.Count < 30)
            return 0.5;

        double bestThreshold = 0.5;
        double bestEv = double.MinValue;
        int minBps = Math.Max(0, searchMin * 100);
        int maxBps = Math.Max(minBps, searchMax * 100);
        int effectiveStepBps = stepBps > 0 ? stepBps : 50;

        for (int thresholdBps = minBps; thresholdBps <= maxBps; thresholdBps += effectiveStepBps)
        {
            double candidate = thresholdBps / 10000.0;
            var evaluation = EvaluateSelectivePolicy(
                selectionSet,
                magWeights,
                magBias,
                probabilityAndStdProvider,
                candidate,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                abstentionThreshold,
                abstentionThresholdBuy,
                abstentionThresholdSell);
            double score = evaluation.Metrics.ExpectedValue;
            if (score > bestEv + 1e-9 ||
                (Math.Abs(score - bestEv) <= 1e-9 && candidate < bestThreshold))
            {
                bestEv = score;
                bestThreshold = candidate;
            }
        }

        return bestThreshold;
    }

    private static (double GlobalThreshold, double BuyThreshold, double SellThreshold) TuneAbstentionThresholds(
        List<TrainingSample>                  selectionSet,
        Func<float[], (double Probability, double EnsembleStd)> probabilityAndStdProvider,
        double                                decisionThreshold,
        double[]                              metaLabelWeights,
        double                                metaLabelBias,
        double                                metaLabelThreshold,
        int[]?                                metaLabelTopFeatureIndices,
        double[]                              abstentionWeights,
        double                                abstentionBias,
        double                                defaultThreshold)
    {
        if (abstentionWeights.Length == 0 || selectionSet.Count < 10)
            return (defaultThreshold, defaultThreshold, defaultThreshold);

        static double EvaluateThresholdCandidate(
            List<TrainingSample> samples,
            Func<float[], (double Probability, double EnsembleStd)> provider,
            double decisionThreshold,
            double[] metaLabelWeights,
            double metaLabelBias,
            double metaLabelThreshold,
            int[]? metaLabelTopFeatureIndices,
            double[] abstentionWeights,
            double abstentionBias,
            double abstentionThreshold,
            double abstentionThresholdBuy,
            double abstentionThresholdSell)
        {
            return EvaluateSelectivePolicy(
                samples,
                [],
                0.0,
                provider,
                decisionThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                abstentionThreshold,
                abstentionThresholdBuy,
                abstentionThresholdSell).Metrics.ExpectedValue;
        }

        double bestGlobal = defaultThreshold;
        double bestEv = double.MinValue;
        for (int thresholdBps = 3500; thresholdBps <= 8000; thresholdBps += 500)
        {
            double candidate = thresholdBps / 10000.0;
            double ev = EvaluateThresholdCandidate(
                selectionSet,
                probabilityAndStdProvider,
                decisionThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                candidate,
                candidate,
                candidate);
            if (ev > bestEv + 1e-9 || (Math.Abs(ev - bestEv) <= 1e-9 && candidate < bestGlobal))
            {
                bestEv = ev;
                bestGlobal = candidate;
            }
        }

        double bestBuy = bestGlobal;
        bestEv = double.MinValue;
        for (int thresholdBps = 3000; thresholdBps <= 8000; thresholdBps += 500)
        {
            double candidate = thresholdBps / 10000.0;
            double ev = EvaluateThresholdCandidate(
                selectionSet,
                probabilityAndStdProvider,
                decisionThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                bestGlobal,
                candidate,
                bestGlobal);
            if (ev > bestEv + 1e-9 || (Math.Abs(ev - bestEv) <= 1e-9 && candidate < bestBuy))
            {
                bestEv = ev;
                bestBuy = candidate;
            }
        }

        double bestSell = bestGlobal;
        bestEv = double.MinValue;
        for (int thresholdBps = 3000; thresholdBps <= 8000; thresholdBps += 500)
        {
            double candidate = thresholdBps / 10000.0;
            double ev = EvaluateThresholdCandidate(
                selectionSet,
                probabilityAndStdProvider,
                decisionThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                bestGlobal,
                bestBuy,
                candidate);
            if (ev > bestEv + 1e-9 || (Math.Abs(ev - bestEv) <= 1e-9 && candidate < bestSell))
            {
                bestEv = ev;
                bestSell = candidate;
            }
        }

        return (bestGlobal, bestBuy, bestSell);
    }

    internal static double ApplyGlobalPlattCalibration(double rawProbability, double plattA, double plattB)
    {
        double clampedRaw = Math.Clamp(rawProbability, 1e-7, 1.0 - 1e-7);
        return MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(clampedRaw) + plattB);
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    /// <summary>
    /// Fits a monotone isotonic regression using the Pool Adjacent Violators Algorithm (PAVA)
    /// over Platt-calibrated probabilities vs. binary outcomes on the calibration set.
    /// Returns interleaved breakpoints [x₀,y₀,x₁,y₁,…] in ascending probability order.
    /// </summary>
    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count < 10) return [];

        // Build and sort pairs without LINQ Select+OrderBy overhead.
        int cn = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
        {
            double raw = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double p   = ApplyGlobalPlattCalibration(raw, plattA, plattB);
            pairs[i]   = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Stack-based PAVA: each entry is (sumY, sumP, count)
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break;
            }
        }

        // Interleaved [x₀,y₀,x₁,y₁,...] — one breakpoint per PAVA block
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    private static double[] FitIsotonicCalibration(
        List<TrainingSample>  calSet,
        Func<float[], double> calibratedProb)
    {
        if (calSet.Count < 10) return [];

        int cn = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
        {
            double p = Math.Clamp(calibratedProb(calSet[i].Features), 0.0, 1.0);
            pairs[i] = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
        foreach (var (p, y) in pairs)
        {
            stack.Add((y, p, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY, prev.SumP + last.SumP, prev.Count + last.Count);
                }
                else break;
            }
        }

        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2] = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    /// <summary>
    /// Guarded isotonic calibration that prevents overfitting on small calibration sets.
    /// <list type="bullet">
    ///   <item>If <paramref name="calSet"/> has fewer than <paramref name="minSamples"/> samples,
    ///         isotonic calibration is skipped entirely (returns empty breakpoints).</item>
    ///   <item>If the calibration set has between <paramref name="minSamples"/> and 2× that
    ///         threshold, leave-one-out cross-validation is used: if LOO error is worse than
    ///         pre-isotonic error, isotonic calibration is skipped.</item>
    ///   <item>Otherwise, proceeds to standard PAVA fitting.</item>
    /// </list>
    /// </summary>
    private double[] FitIsotonicCalibrationGuarded(
        List<TrainingSample>  calSet,
        Func<float[], double> calibratedProb,
        int                   minSamples)
    {
        if (calSet.Count < minSamples)
        {
            _logger.LogDebug(
                "Skipping isotonic calibration: only {N} calibration samples (min {Min} required)",
                calSet.Count, minSamples);
            return [];
        }

        // For small calibration sets (minSamples .. 2×minSamples), apply LOO overfitting guard
        if (calSet.Count <= minSamples * 2)
        {
            // Compute pre-isotonic log-loss (baseline without isotonic)
            double preIsotonicError = 0;
            foreach (var s in calSet)
            {
                double p = Math.Clamp(calibratedProb(s.Features), 1e-7, 1.0 - 1e-7);
                double y = s.Direction > 0 ? 1.0 : 0.0;
                preIsotonicError += -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p));
            }
            preIsotonicError /= calSet.Count;

            // LOO cross-validation: for each sample, fit isotonic on all others, evaluate on held-out
            double looError = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                var looSet = new List<TrainingSample>(calSet.Count - 1);
                for (int j = 0; j < calSet.Count; j++)
                    if (j != i) looSet.Add(calSet[j]);

                double[] looBp = FitIsotonicCalibration(looSet, calibratedProb);

                double p = Math.Clamp(calibratedProb(calSet[i].Features), 1e-7, 1.0 - 1e-7);
                if (looBp.Length >= 4)
                    p = Math.Clamp(ApplyIsotonicCalibration(p, looBp), 1e-7, 1.0 - 1e-7);

                double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                looError += -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p));
            }
            looError /= calSet.Count;

            if (looError >= preIsotonicError)
            {
                _logger.LogWarning(
                    "Skipping isotonic calibration: LOO error {Loo:F4} >= pre-isotonic error {Pre:F4} " +
                    "({N} samples — likely overfitting)",
                    looError, preIsotonicError, calSet.Count);
                return [];
            }

            _logger.LogDebug(
                "Isotonic LOO guard passed: LOO error {Loo:F4} < pre-isotonic {Pre:F4} ({N} samples)",
                looError, preIsotonicError, calSet.Count);
        }

        return FitIsotonicCalibration(calSet, calibratedProb);
    }

    /// <summary>
    /// Applies isotonic calibration via linear interpolation over the PAVA breakpoints.
    /// Returns <paramref name="p"/> unchanged when fewer than 4 breakpoint values exist.
    /// </summary>
    internal static double ApplyIsotonicCalibration(double p, double[] breakpoints)
    {
        if (breakpoints.Length < 4) return p;

        int nPoints = breakpoints.Length / 2;
        if (p <= breakpoints[0])                  return breakpoints[1];
        if (p >= breakpoints[(nPoints - 1) * 2])  return breakpoints[(nPoints - 1) * 2 + 1];

        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (breakpoints[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }

        double x0 = breakpoints[lo * 2],       y0 = breakpoints[lo * 2 + 1];
        double x1 = breakpoints[(lo + 1) * 2], y1 = breakpoints[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15
            ? (y0 + y1) / 2.0
            : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    // ── Class-conditional Platt scaling (Round 6) ─────────────────────────────

    /// <summary>
    /// Fits separate Platt scalers for Buy (raw prob ≥ 0.5) and Sell (raw prob &lt; 0.5) subsets
    /// of the calibration set to correct directional calibration bias.
    /// Returns (ABuy, BBuy, ASell, BSell); returns (0,0,0,0) when a class subset has &lt; 5 samples.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample> calSet,
            double[][]           weights,
            double[]             biases,
            int                  featureCount,
            int[][]?             subsets,
            MetaLearner          meta = default,
            MlpState             mlp  = default,
            double               plattA = 1.0,
            double               plattB = 0.0,
            double               temperatureScale = 0.0)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double rawP  = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP         = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double logit = MLFeatureHelper.Logit(rawP);
            double globalCalibP = temperatureScale > 0.0 && temperatureScale < 10.0
                ? MLFeatureHelper.Sigmoid(logit / temperatureScale)
                : MLFeatureHelper.Sigmoid(plattA * logit + plattB);
            double y     = s.Direction > 0 ? 1.0 : 0.0;
            if (globalCalibP >= 0.5) buySamples.Add((logit, y));
            else             sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (0.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample>  calSet,
            Func<float[], double> rawProbProvider,
            double                plattA = 1.0,
            double                plattB = 0.0,
            double                temperatureScale = 0.0)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double rawP = Math.Clamp(rawProbProvider(s.Features), 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(rawP);
            double globalCalibP = temperatureScale > 0.0 && temperatureScale < 10.0
                ? MLFeatureHelper.Sigmoid(logit / temperatureScale)
                : MLFeatureHelper.Sigmoid(plattA * logit + plattB);
            double y = s.Direction > 0 ? 1.0 : 0.0;
            if (globalCalibP >= 0.5) buySamples.Add((logit, y));
            else sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (0.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0.0, dB = 0.0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err = calibP - y;
                    dA += err * logit;
                    dB += err;
                }

                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }

            return (a, b);
        }

        var (aBuy, bBuy) = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ── Average Kelly fraction (Round 6) ──────────────────────────────────────

    /// <summary>
    /// Computes the half-Kelly fraction averaged over the calibration set:
    ///   mean( max(0, 2·calibP − 1) ) × 0.5
    /// where calibP uses the already-fitted global Platt (A, B).
    /// Returns 0.0 if the calibration set is empty.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double calibP = ApplyGlobalPlattCalibration(rawP, plattA, plattB);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return sum / calSet.Count * 0.5;
    }

    private static double ComputeAvgKellyFraction(
        List<TrainingSample>  calSet,
        Func<float[], double> calibratedProb)
    {
        if (calSet.Count == 0) return 0.0;

        double sum = 0.0;
        foreach (var s in calSet)
            sum += Math.Max(0.0, 2.0 * Math.Clamp(calibratedProb(s.Features), 0.0, 1.0) - 1.0);

        return sum / calSet.Count * 0.5;
    }

    // ── Temperature scaling (Round 7) ─────────────────────────────────────────

    /// <summary>
    /// Fits a single temperature scalar T on the calibration set via grid search over
    /// [0.1, 3.0] in 30 steps, selecting the T that minimises binary cross-entropy.
    /// calibP = σ(logit(rawP) / T).
    /// Returns 1.0 (no-op) when the cal set is too small.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
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
        if (calSet.Count < 10) return 1.0;

        // Pre-cache raw probabilities and labels once — EnsembleProb is O(K×F) per sample.
        // Avoids recomputing the same inference 31 times (once per T candidate).
        int n = calSet.Count;
        var rawProbs = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double rawP = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawProbs[i] = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double bestT    = 1.0;
        double bestLoss = double.MaxValue;

        for (int step = 0; step <= 30; step++)
        {
            double T    = 0.1 + step * (3.0 - 0.1) / 30.0;
            double loss = 0.0;
            const double eps = 1e-10;

            for (int i = 0; i < n; i++)
            {
                double calibP = ApplyProductionCalibration(
                    rawProbs[i],
                    plattA,
                    plattB,
                    T,
                    plattABuy,
                    plattBBuy,
                    plattASell,
                    plattBSell,
                    isotonicBreakpoints,
                    ageDecayLambda,
                    trainedAtUtc);
                double y = labels[i];
                loss += -(y * Math.Log(calibP + eps) + (1 - y) * Math.Log(1 - calibP + eps));
            }

            if (loss / n < bestLoss)
            {
                bestLoss = loss / n;
                bestT    = T;
            }
        }

        return bestT;
    }

    private static double FitTemperatureScaling(
        List<TrainingSample>  calSet,
        Func<float[], double> rawProbProvider,
        double                plattA,
        double                plattB,
        double                plattABuy,
        double                plattBBuy,
        double                plattASell,
        double                plattBSell,
        double[]              isotonicBreakpoints,
        double                ageDecayLambda,
        DateTime              trainedAtUtc)
    {
        if (calSet.Count < 10) return 1.0;

        int n = calSet.Count;
        var rawProbs = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            rawProbs[i] = Math.Clamp(rawProbProvider(calSet[i].Features), 1e-7, 1.0 - 1e-7);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double bestT = 1.0;
        double bestLoss = double.MaxValue;

        for (int step = 0; step <= 30; step++)
        {
            double t = 0.1 + step * (3.0 - 0.1) / 30.0;
            double loss = 0.0;
            const double eps = 1e-10;

            for (int i = 0; i < n; i++)
            {
                double calibP = ApplyProductionCalibration(
                    rawProbs[i],
                    plattA,
                    plattB,
                    t,
                    plattABuy,
                    plattBBuy,
                    plattASell,
                    plattBSell,
                    isotonicBreakpoints,
                    ageDecayLambda,
                    trainedAtUtc);
                double y = labels[i];
                loss += -(y * Math.Log(calibP + eps) + (1 - y) * Math.Log(1 - calibP + eps));
            }

            if (loss / n < bestLoss)
            {
                bestLoss = loss / n;
                bestT = t;
            }
        }

        return bestT;
    }
}
