using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Calibration pipeline (shared by pruning branch and final calibration)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds all calibration parameters produced by <see cref="FitCalibrationPipeline"/>.
    /// Eliminates ~20 loose variables that previously threaded through the pruning branch
    /// and the main pipeline's step 12+.
    /// </summary>
    private sealed record CalibrationResult(
        double[] LearnerCalAccuracies,
        double[]? LearnerAccWeights,
        double[] StackingWeights,
        double StackingBias,
        double PlattA,
        double PlattB,
        double TemperatureScale,
        double PlattABuy,
        double PlattBBuy,
        double PlattASell,
        double PlattBSell,
        double[] IsotonicBreakpoints,
        double OptimalThreshold,
        DateTime TrainedAtUtc);

    /// <summary>
    /// Runs the full calibration pipeline: learner stats → stacking → Platt CV → temperature →
    /// class-conditional Platt → isotonic → temperature refit → optimal threshold.
    /// <para>
    /// When <paramref name="prior"/> is non-null, upstream calibration (learner stats, stacking,
    /// Platt, initial temperature, class-conditional) is reused and only the isotonic tail
    /// (isotonic → temperature refit → threshold) runs. This is used by the main pipeline's
    /// step 12 where Platt/stacking were already fitted before the pruning decision.
    /// </para>
    /// </summary>
    private CalibrationResult FitCalibrationPipeline(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        TrainingHyperparams hp,
        CancellationToken ct,
        CalibrationResult? prior = null)
    {
        // ── Upstream calibration (computed fresh or reused from prior) ────────
        double[] learnerCalAccuracies;
        double[]? learnerAccWeights;
        double[] stackingWeights;
        double stackingBias;
        double plattA, plattB;
        double temperatureScale;
        double plattABuy, plattBBuy, plattASell, plattBSell;

        if (prior is not null)
        {
            learnerCalAccuracies = prior.LearnerCalAccuracies;
            learnerAccWeights    = prior.LearnerAccWeights;
            stackingWeights      = prior.StackingWeights;
            stackingBias         = prior.StackingBias;
            plattA               = prior.PlattA;
            plattB               = prior.PlattB;
            temperatureScale     = prior.TemperatureScale;
            plattABuy            = prior.PlattABuy;
            plattBBuy            = prior.PlattBBuy;
            plattASell           = prior.PlattASell;
            plattBSell           = prior.PlattBSell;
        }
        else
        {
            (learnerCalAccuracies, learnerAccWeights) = ComputeLearnerCalibrationStats(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, featureSubsets, learnerHiddenSizes, learnerActivations);

            (stackingWeights, stackingBias) = FitStackingMetaLearner(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations, ct);

            // Raw-prob delegate used by all ElmCalibrationHelper methods.
            // Captures learnerAccWeights/stackingWeights so the delegate signature matches the helper.
            Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double>
                rawProbFresh = (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);

            double plattAResult = 0, plattBResult = 0;
            double tempResult = 0.0;

            var plattTask = Task.Run(() =>
            {
                (plattAResult, plattBResult) = ElmCalibrationHelper.FitPlattScalingCV(
                    calSet, weights, biases, inputWeights, inputBiases,
                    featureCount, hiddenSize, featureSubsets, rawProbFresh);
            }, ct);

            var tempTask = (hp.FitTemperatureScale && calSet.Count >= 10)
                ? Task.Run(() =>
                {
                    tempResult = ElmCalibrationHelper.FitTemperatureScaling(
                        calSet, weights, biases, inputWeights, inputBiases,
                        featureCount, hiddenSize, featureSubsets, rawProbFresh);
                }, ct)
                : Task.CompletedTask;

            Task.WaitAll([plattTask, tempTask], ct);
            plattA = plattAResult;
            plattB = plattBResult;
            temperatureScale = tempResult;

            (plattABuy, plattBBuy, plattASell, plattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets,
                plattA, plattB, temperatureScale, rawProbFresh);
        }

        var trainedAtUtc = DateTime.UtcNow;

        // ── Raw-prob delegate (always needed for isotonic + refit) ────────────
        Func<float[], double[][], double[], double[][], double[][], int, int, int[][]?, double[]?, double>
            rawProb = (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);

        // ── Pre-isotonic calibrated prob ──────────────────────────────────────
        double PreIsoCalibProb(float[] features) => ApplyProductionCalibration(
            EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias),
            plattA, plattB, temperatureScale, plattABuy, plattBBuy, plattASell, plattBSell);

        // ── Isotonic calibration (PAVA) ──────────────────────────────────────
        double[] isotonicBp = ElmCalibrationHelper.FitIsotonicCalibration(
            calSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PreIsoCalibProb(f));

        // ── Temperature refit with isotonic context ──────────────────────────
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            double refitTemp = ElmCalibrationHelper.FitTemperatureScaling(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, rawProb,
                plattA, plattB, plattABuy, plattBBuy, plattASell, plattBSell,
                isotonicBp, hp.AgeDecayLambda, trainedAtUtc);

            if (Math.Abs(refitTemp - temperatureScale) > 1e-6)
            {
                temperatureScale = refitTemp;
                (plattABuy, plattBBuy, plattASell, plattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                    calSet, weights, biases, inputWeights, inputBiases,
                    featureCount, hiddenSize, featureSubsets,
                    plattA, plattB, temperatureScale, rawProb);

                isotonicBp = ElmCalibrationHelper.FitIsotonicCalibration(
                    calSet, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets,
                    (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PreIsoCalibProb(f));
            }
        }

        // ── Optimal threshold ────────────────────────────────────────────────
        double FinalCalibProb(float[] features)
        {
            double p = PreIsoCalibProb(features);
            p = isotonicBp.Length >= 4 ? ElmCalibrationHelper.ApplyIsotonicCalibration(p, isotonicBp) : p;
            double safeDecay = ClampNonNegativeFinite(hp.AgeDecayLambda);
            if (safeDecay > 0.0)
            {
                double days = (DateTime.UtcNow - trainedAtUtc).TotalDays;
                p = 0.5 + (p - 0.5) * Math.Exp(-safeDecay * Math.Max(0.0, days));
            }
            return p;
        }

        double optimalThreshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            calSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalCalibProb(f));

        return new CalibrationResult(
            learnerCalAccuracies, learnerAccWeights,
            stackingWeights, stackingBias,
            plattA, plattB, temperatureScale,
            plattABuy, plattBBuy, plattASell, plattBSell,
            isotonicBp, optimalThreshold, trainedAtUtc);
    }

    /// <summary>
    /// Builds the fully-calibrated probability function from a <see cref="CalibrationResult"/>.
    /// Applies: raw ensemble → production calibration → isotonic → age decay.
    /// </summary>
    private Func<float[], double> BuildCalibratedProbFunc(
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        CalibrationResult calib, double ageDecayLambda)
    {
        return (float[] features) =>
        {
            double raw = EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, calib.LearnerAccWeights,
                learnerHiddenSizes, learnerActivations, calib.StackingWeights, calib.StackingBias);
            double p = ApplyProductionCalibration(raw,
                calib.PlattA, calib.PlattB, calib.TemperatureScale,
                calib.PlattABuy, calib.PlattBBuy, calib.PlattASell, calib.PlattBSell);
            if (calib.IsotonicBreakpoints.Length >= 4)
                p = ElmCalibrationHelper.ApplyIsotonicCalibration(p, calib.IsotonicBreakpoints);
            double safeDecay = ClampNonNegativeFinite(ageDecayLambda);
            if (safeDecay > 0.0)
            {
                double days = (DateTime.UtcNow - calib.TrainedAtUtc).TotalDays;
                p = 0.5 + (p - 0.5) * Math.Exp(-safeDecay * Math.Max(0.0, days));
            }
            return p;
        };
    }

    private (double[] Accuracies, double[]? AccuracyWeights) ComputeLearnerCalibrationStats(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations)
    {
        int K = Math.Min(
            weights.Length,
            Math.Min(biases.Length, Math.Min(inputWeights.Length, inputBiases.Length)));
        var accuracies = new double[K];
        if (K <= 0 || calSet.Count == 0) return (accuracies, null);

        for (int k = 0; k < K; k++)
        {
            if (weights[k] is not { Length: > 0 } ||
                inputWeights[k] is not { Length: > 0 } ||
                inputBiases[k] is null)
            {
                continue;
            }

            int correct = 0;
            foreach (var s in calSet)
            {
                double prob = ClampProbabilityOrNeutral(ElmLearnerProb(
                    s.Features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount,
                    ResolveLearnerHiddenSize(learnerHiddenSizes, k, inputBiases[k].Length, inputBiases[k]),
                    ResolveLearnerSubset(featureSubsets, k),
                    ResolveLearnerActivation(learnerActivations, k)));
                if ((prob >= 0.5 ? 1 : 0) == ToBinaryLabel(s.Direction)) correct++;
            }
            accuracies[k] = (double)correct / calSet.Count;
        }

        _logger.LogDebug("ELM per-learner cal accuracies: [{Accs}]",
            string.Join(", ", accuracies.Select(a => $"{a:P0}")));

        var accuracyWeights = new double[K];
        const double tempScale = 5.0;
        double maxShifted = accuracies.Max() - 0.5;
        double expSum = 0.0;
        for (int k = 0; k < K; k++)
        {
            double shifted = accuracies[k] - 0.5;
            accuracyWeights[k] = Math.Exp(tempScale * (shifted - maxShifted));
            expSum += accuracyWeights[k];
        }

        if (expSum <= 1e-15) return (accuracies, null);
        for (int k = 0; k < K; k++) accuracyWeights[k] /= expSum;

        // Active ensemble pruning: zero out sub-random learners (acc < 0.5)
        int prunedLearners = 0;
        for (int k = 0; k < K; k++)
        {
            if (accuracies[k] < 0.5)
            {
                accuracyWeights[k] = 0.0;
                prunedLearners++;
            }
        }
        if (prunedLearners > 0)
        {
            // Re-normalize remaining weights
            double remainingSum = 0.0;
            for (int k = 0; k < K; k++) remainingSum += accuracyWeights[k];
            if (remainingSum > 1e-15)
                for (int k = 0; k < K; k++) accuracyWeights[k] /= remainingSum;
            _logger.LogWarning(
                "ELM active ensemble pruning: zeroed {Pruned}/{K} sub-random learners (acc < 0.5)",
                prunedLearners, K);
        }

        return (accuracies, accuracyWeights);
    }

    private static double ComputeEnsembleStd(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? learnerWeights = null,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        int maxK = Math.Min(
            weights.Length,
            Math.Min(biases.Length, Math.Min(inputWeights.Length, inputBiases.Length)));
        if (maxK <= 1) return 0.0;

        var probs = new double[maxK];
        var validIndices = new int[maxK];
        int validCount = 0;
        for (int k = 0; k < maxK; k++)
        {
            if (weights[k] is not { Length: > 0 } ||
                inputWeights[k] is not { Length: > 0 } ||
                inputBiases[k] is null)
            {
                continue;
            }

            probs[validCount++] = ClampProbabilityOrNeutral(ElmLearnerProb(
                features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                featureCount,
                ResolveLearnerHiddenSize(learnerHiddenSizes, k, inputBiases[k].Length, inputBiases[k]),
                ResolveLearnerSubset(featureSubsets, k),
                ResolveLearnerActivation(learnerActivations, k)));
            validIndices[validCount - 1] = k;
        }

        if (validCount <= 1)
            return 0.0;

        double avg;
        if (stackingWeights is { Length: > 0 } sw)
        {
            double z = stackingBias;
            for (int k = 0; k < validCount; k++)
            {
                int originalIndex = validIndices[k];
                double stackingWeight = originalIndex < sw.Length && double.IsFinite(sw[originalIndex])
                    ? sw[originalIndex]
                    : 0.0;
                z += stackingWeight * probs[k];
            }
            avg = ClampProbabilityOrNeutral(MLFeatureHelper.Sigmoid(z));
        }
        else if (learnerWeights is { Length: > 0 } lw)
        {
            double sumP = 0.0;
            double sumW = 0.0;
            for (int k = 0; k < validCount; k++)
            {
                int originalIndex = validIndices[k];
                double learnerWeight = originalIndex < lw.Length && double.IsFinite(lw[originalIndex]) && lw[originalIndex] > 0.0
                    ? lw[originalIndex]
                    : 0.0;
                sumP += learnerWeight * probs[k];
                sumW += learnerWeight;
            }
            avg = ClampProbabilityOrNeutral(sumW > 1e-15 ? sumP / sumW : probs[..validCount].Average());
        }
        else
        {
            avg = probs[..validCount].Average();
        }

        double variance = 0.0;
        for (int k = 0; k < validCount; k++)
        {
            double d = probs[k] - avg;
            variance += d * d;
        }

        return double.IsFinite(variance) ? Math.Sqrt(variance / (validCount - 1)) : 0.0;
    }
}
