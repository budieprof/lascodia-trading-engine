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
    //  Meta-label & abstention (kept inline — they use ensemble inference)
    // ═══════════════════════════════════════════════════════════════════════════

    private (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double decisionThreshold,
        int[]? topFeatureIndices = null,
        Func<float[], double>? calibratedProb = null,
        double[]? stackingWeights = null, double stackingBias = 0.0,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        int embargo = 0, CancellationToken ct = default)
    {
        if (calSet.Count < 10) return ([], 0.0);

        int metaDim = 2 + Math.Min(5, topFeatureIndices?.Length ?? featureCount);
        double metaBaseLr = configLr > 0.0 ? configLr : 0.01;
        int maxPatience = configPatience > 0 ? configPatience : 25;

        var metaXs = new double[calSet.Count][];
        var targets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s = calSet[i];
            double calibP = ClampProbabilityOrNeutral(calibratedProb is not null
                ? calibratedProb(s.Features)
                : EnsembleCalibProb(
                    s.Features, weights, biases, inputWeights, inputBiases,
                    1.0, 0.0, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));

            double ensStd = ClampNonNegativeFinite(ComputeEnsembleStd(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, featureSubsets, learnerHiddenSizes, learnerActivations,
                stackingWeights: stackingWeights, stackingBias: stackingBias));

            metaXs[i] = BuildMetaLabelFeatureVector(calibP, ensStd, s.Features, featureCount, topFeatureIndices);

            targets[i] = (calibP >= decisionThreshold ? 1 : 0) == ToBinaryLabel(s.Direction) ? 1.0 : 0.0;
        }

        int metaMaxEpochs = configMaxEpochs > 0 ? configMaxEpochs : 200;
        int metaEmbargo = Math.Max(0, embargo);
        int metaTrainCount = Math.Max(1, (int)(calSet.Count * 0.8) - metaEmbargo);
        int metaValStart = Math.Min(metaTrainCount + metaEmbargo, calSet.Count);

        return FitLinearModelAdam(
            metaXs, targets, metaTrainCount, metaValStart, calSet.Count,
            computeGrad: static (z, target) => MLFeatureHelper.Sigmoid(z) - target,
            computeValLoss: static (z, target) =>
            {
                double p = MLFeatureHelper.Sigmoid(z);
                return -(target * Math.Log(Math.Max(p, 1e-10))
                       + (1 - target) * Math.Log(Math.Max(1 - p, 1e-10)));
            },
            baseLr: metaBaseLr,
            maxEpochs: metaMaxEpochs,
            maxPatience: maxPatience,
            addL2ToValLoss: true,
            earlyStopThreshold: 1e-7,
            rngSeed: calSet.Count + 7,
            ct: ct);
    }

    private (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        double[] metaLabelWeights, double metaLabelBias,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double decisionThreshold,
        int[]? topFeatureIndices = null,
        Func<float[], double>? calibratedProb = null,
        double[]? stackingWeights = null, double stackingBias = 0.0,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        int embargo = 0, CancellationToken ct = default)
    {
        if (calSet.Count < 10) return ([], 0.0, 0.5);

        var absXs = new double[calSet.Count][];
        var absTargets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s = calSet[i];
            double calibP = ClampProbabilityOrNeutral(calibratedProb is not null
                ? calibratedProb(s.Features)
                : EnsembleCalibProb(
                    s.Features, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));

            double ensStd = ClampNonNegativeFinite(ComputeEnsembleStd(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, featureSubsets, learnerHiddenSizes, learnerActivations,
                stackingWeights: stackingWeights, stackingBias: stackingBias));

            double mlScore = ComputeMetaLabelScoreWithTopFeatures(
                calibP, ensStd, s.Features, featureCount, metaLabelWeights, metaLabelBias, topFeatureIndices);

            absXs[i] = [calibP, ensStd, mlScore];
            absTargets[i] = (calibP >= decisionThreshold ? 1 : 0) == ToBinaryLabel(s.Direction) ? 1.0 : 0.0;
        }

        int absMaxEpochs = configMaxEpochs > 0 ? configMaxEpochs : 200;
        int absEmbargo = Math.Max(0, embargo);
        int absTrainCount = Math.Max(1, (int)(calSet.Count * 0.8) - absEmbargo);
        int absValStart = Math.Min(absTrainCount + absEmbargo, calSet.Count);
        int maxPatience = configPatience > 0 ? configPatience : 25;
        double absBaseLr = configLr > 0.0 ? configLr : 0.01;

        var (aw, ab) = FitLinearModelAdam(
            absXs, absTargets, absTrainCount, absValStart, calSet.Count,
            computeGrad: static (z, target) => MLFeatureHelper.Sigmoid(z) - target,
            computeValLoss: static (z, target) =>
            {
                double p = MLFeatureHelper.Sigmoid(z);
                return -(target * Math.Log(Math.Max(p, 1e-10))
                       + (1 - target) * Math.Log(Math.Max(1 - p, 1e-10)));
            },
            baseLr: absBaseLr,
            maxEpochs: absMaxEpochs,
            maxPatience: maxPatience,
            addL2ToValLoss: true,
            earlyStopThreshold: 1e-7,
            rngSeed: calSet.Count + 13,
            ct: ct);

        double bestThr = 0.5;
        double bestAcc = 0;
        for (int halfPct = 60; halfPct <= 140; halfPct++)
        {
            double threshold = halfPct / 200.0;
            int c = 0, t = 0;
            foreach (var s in calSet)
            {
                double calibP = ClampProbabilityOrNeutral(calibratedProb is not null
                    ? calibratedProb(s.Features)
                    : EnsembleCalibProb(
                        s.Features, weights, biases, inputWeights, inputBiases,
                        plattA, plattB, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));
                double ensStd = ClampNonNegativeFinite(ComputeEnsembleStd(
                    s.Features, weights, biases, inputWeights, inputBiases,
                    featureCount, featureSubsets, learnerHiddenSizes, learnerActivations,
                    stackingWeights: stackingWeights, stackingBias: stackingBias));

                double mlScore = ComputeMetaLabelScoreWithTopFeatures(
                    calibP, ensStd, s.Features, featureCount, metaLabelWeights, metaLabelBias, topFeatureIndices);

                double absZ = ab + aw[0] * calibP + aw[1] * ensStd + aw[2] * mlScore;
                if (MLFeatureHelper.Sigmoid(absZ) < threshold) continue;
                t++;
                if ((calibP >= decisionThreshold ? 1 : 0) == ToBinaryLabel(s.Direction)) c++;
            }
            if (t < 5) continue;
            double acc = (double)c / t;
            if (acc > bestAcc && t >= calSet.Count / 4)
            {
                bestAcc = acc;
                bestThr = threshold;
            }
        }

        return (aw, ab, bestThr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stacking meta-learner
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Trains a logistic meta-learner that maps per-base-learner probabilities [p_0,..,p_{K-1}]
    /// to a final probability via σ(Σ w_k·p_k + b). Fitted on the calibration set which base
    /// learners never saw. When the meta-learner is active, it replaces simple/weighted averaging,
    /// learning optimal per-learner combination weights.
    /// </summary>
    private (double[] Weights, double Bias) FitStackingMetaLearner(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        CancellationToken ct = default)
    {
        int K = Math.Min(
            weights.Length,
            Math.Min(biases.Length, Math.Min(inputWeights.Length, inputBiases.Length)));
        if (calSet.Count < 20 || K < 2) return ([], 0.0);

        int n = calSet.Count;
        var calLp = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLp[i] = new double[K];
            for (int k = 0; k < K; k++)
            {
                if (weights[k] is not { Length: > 0 } ||
                    inputWeights[k] is not { Length: > 0 } ||
                    inputBiases[k] is null)
                {
                    continue;
                }

                calLp[i][k] = ClampProbabilityOrNeutral(ElmLearnerProb(
                    calSet[i].Features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount,
                    ResolveLearnerHiddenSize(learnerHiddenSizes, k, hiddenSize, inputBiases[k]),
                    ResolveLearnerSubset(featureSubsets, k),
                    ResolveLearnerActivation(learnerActivations, k)));
            }
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        // 80/20 train/val split for early stopping (consistent with FitMetaLabelModel)
        int trainCount = Math.Max(1, (int)(n * 0.8));
        int valStart   = trainCount;

        // Uniform 1/K init — matches the original SGD starting point and gives
        // a better initial loss than zeros for the BCE logistic objective.
        var uniformInit = new double[K];
        Array.Fill(uniformInit, 1.0 / K);

        var (bestMw, bestMb) = FitLinearModelAdam(
            calLp, calLabels, trainCount, valStart, n,
            computeGrad: static (z, target) => MLFeatureHelper.Sigmoid(z) - target,
            computeValLoss: static (z, target) =>
            {
                double p = MLFeatureHelper.Sigmoid(z);
                return -(target * Math.Log(Math.Max(p, 1e-10))
                       + (1 - target) * Math.Log(Math.Max(1 - p, 1e-10)));
            },
            baseLr: 0.01,
            maxEpochs: 300,
            maxPatience: 30,
            addL2ToValLoss: false,
            earlyStopThreshold: 1e-7,
            rngSeed: n + 17,
            initialWeights: uniformInit,
            ct: ct);

        _logger.LogDebug(
            "ELM stacking meta-learner: bias={B:F4} weights=[{W}]",
            bestMb, string.Join(",", bestMw.Select(w => w.ToString("F3"))));

        return (bestMw, bestMb);
    }
}
