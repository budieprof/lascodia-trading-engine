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
    //  Magnitude regressors
    // ═══════════════════════════════════════════════════════════════════════════

    private static double PredictMagnitudeAug(
        float[] features, double[] augWeights, double augBias,
        int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        int K = Math.Min(elmInputWeights.Length, elmInputBiases.Length);
        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();
        double pred = augBias;

        for (int j = 0; j < Math.Min(Math.Min(featureCount, features.Length), augWeights.Length); j++)
            pred += augWeights[j] * features[j];

        for (int h = 0; h < hiddenSize; h++)
        {
            if (featureCount + h >= augWeights.Length) break;
            double hSum = 0;
            int hCount = 0;
            for (int ki = 0; ki < K; ki++)
            {
                if (elmInputWeights[ki] is not { Length: > 0 } ||
                    elmInputBiases[ki] is not { Length: > 0 })
                {
                    continue;
                }

                var bIn = elmInputBiases[ki];
                if (h >= bIn.Length) continue; // learner has fewer hidden units

                var wIn = elmInputWeights[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length && featureSubsets[ki] is { Length: > 0 }
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                double z = bIn[h];
                int rowOff = h * subLen;
                z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, sub, subLen);
                ElmActivation learnerAct = learnerActivations.Length > 0
                    ? ResolveLearnerActivation(learnerActivations, ki)
                    : ElmActivation.Sigmoid;
                hSum += ElmMathHelper.Activate(z, learnerAct);
                hCount++;
            }
            pred += augWeights[featureCount + h] * (hCount > 0 ? hSum / hCount : 0.0);
        }

        return pred;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Shared Adam mini-batch trainer
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fits a linear model (w·x + b) using Adam with cosine-annealed LR, mini-batching,
    /// and early stopping. Used by the magnitude regressor, quantile regressor, meta-label
    /// model, and abstention model — eliminating ~400 lines of duplicated optimiser code.
    /// <para>
    /// Callers provide two delegates to customise the loss:
    /// <list type="bullet">
    ///   <item><paramref name="computeGrad"/>: (linearOutput, target) → scalar gradient multiplier for a single sample.</item>
    ///   <item><paramref name="computeValLoss"/>: (linearOutput, target) → per-sample validation loss contribution.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static (double[] Weights, double Bias) FitLinearModelAdam(
        double[][] features,
        double[] targets,
        int trainCount,
        int valStart,
        int totalCount,
        Func<double, double, double> computeGrad,
        Func<double, double, double> computeValLoss,
        double baseLr,
        int maxEpochs,
        int maxPatience,
        double l2Lambda = 1e-4,
        bool addL2ToValLoss = false,
        double earlyStopThreshold = 1e-6,
        int batchSize = 256,
        int rngSeed = 0,
        double[]? initialWeights = null,
        CancellationToken ct = default)
    {
        int dim = features.Length > 0 ? features[0].Length : 0;
        if (dim == 0 || trainCount <= 0)
            return (new double[dim], 0.0);

        double[] w = new double[dim];
        if (initialWeights is { Length: > 0 })
            Array.Copy(initialWeights, w, Math.Min(initialWeights.Length, dim));
        double b = 0.0;
        double[] bestW = new double[dim];
        double   bestB = 0.0;
        double   bestLoss = double.MaxValue;
        int      patience = 0;

        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double[] adamMW = new double[dim], adamVW = new double[dim];
        double   adamMB = 0, adamVB = 0;
        int      globalStep = 0;

        bool useBatch     = trainCount > batchSize * 2;
        var  batchRng     = useBatch ? new Random(rngSeed) : null;
        int[] batchOrder  = useBatch ? Enumerable.Range(0, trainCount).ToArray() : [];
        double[] gradW    = new double[dim];

        int valCount = totalCount - valStart;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = ElmMathHelper.CosineAnnealLr(baseLr, epoch, maxEpochs);
            if (useBatch) ElmMathHelper.ShuffleArray(batchOrder, batchRng!);

            int batchCount = useBatch ? (trainCount + batchSize - 1) / batchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = useBatch ? bi * batchSize : 0;
                int bEnd   = useBatch ? Math.Min(bStart + batchSize, trainCount) : trainCount;
                int bLen   = bEnd - bStart;

                double gradB = 0;
                Array.Clear(gradW, 0, dim);

                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int idx = useBatch ? batchOrder[bIdx] : bIdx;
                    double z = b;
                    for (int j = 0; j < dim; j++) z += w[j] * features[idx][j];
                    double grad = computeGrad(z, targets[idx]);
                    gradB += grad;
                    for (int j = 0; j < dim; j++) gradW[j] += grad * features[idx][j];
                }

                double gB = gradB / bLen + 2.0 * l2Lambda * b;
                globalStep++;
                adamMB = beta1 * adamMB + (1 - beta1) * gB;
                adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
                b -= lr * (adamMB / (1 - Math.Pow(beta1, globalStep)))
                   / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, globalStep))) + eps);

                for (int j = 0; j < dim; j++)
                {
                    double gW = gradW[j] / bLen + 2.0 * l2Lambda * w[j];
                    adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                    adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                    w[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, globalStep)))
                          / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, globalStep))) + eps);
                }
            }

            // Validation loss
            int evalCount = valCount > 0 ? valCount : totalCount;
            int evalStart = valCount > 0 ? valStart : 0;
            double loss = 0;
            for (int i = evalStart; i < evalStart + evalCount; i++)
            {
                double z = b;
                for (int j = 0; j < dim; j++) z += w[j] * features[i][j];
                loss += computeValLoss(z, targets[i]);
            }
            loss /= Math.Max(1, evalCount);

            if (addL2ToValLoss)
            {
                double l2Penalty = b * b;
                for (int j = 0; j < dim; j++) l2Penalty += w[j] * w[j];
                loss += l2Lambda * l2Penalty;
            }

            if (loss < bestLoss - earlyStopThreshold)
            {
                bestLoss = loss;
                Array.Copy(w, bestW, dim);
                bestB = b;
                patience = 0;
            }
            else if (++patience >= maxPatience) break;
        }

        return (bestW, bestB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Magnitude regressors
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] EquivWeights, double EquivBias,
                    double[] AugWeights, double AugBias) FitElmMagnitudeRegressor(
        List<TrainingSample> train, int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        int embargo = 0, CancellationToken ct = default)
    {
        if (train.Count < 10) return (new double[featureCount], 0.0,
                                      new double[featureCount + hiddenSize], 0.0);

        int K = elmInputWeights.Length;
        int augDim = featureCount + hiddenSize;
        int valSize = Math.Max(10, train.Count / 10);
        if (train.Count <= valSize)
            valSize = Math.Max(1, train.Count / 5);
        // Insert embargo gap between training and validation subsets to prevent
        // information leakage through autocorrelated time-series features.
        int magEmbargo = Math.Max(0, embargo);
        int trainSubEnd = train.Count - valSize - magEmbargo;
        if (trainSubEnd < 1)
            return (new double[featureCount], 0.0,
                new double[featureCount + hiddenSize], 0.0);
        int valStart = trainSubEnd + magEmbargo;

        var augFeatures = BuildAugmentedFeatures(train, featureCount, hiddenSize, K, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);

        var targets = new double[train.Count];
        for (int i = 0; i < train.Count; i++) targets[i] = train[i].Magnitude;

        var (bestW, bestB) = FitLinearModelAdam(
            augFeatures, targets, trainSubEnd, valStart, train.Count,
            computeGrad: static (pred, target) =>
            {
                double err = pred - target;
                return Math.Abs(err) > 1.35 ? 1.35 * Math.Sign(err) : err;
            },
            computeValLoss: static (pred, target) => { double e = pred - target; return e * e; },
            baseLr: configLr > 0.0 ? configLr : 0.001,
            maxEpochs: configMaxEpochs > 0 ? configMaxEpochs : 200,
            maxPatience: configPatience > 0 ? configPatience : 15,
            rngSeed: trainSubEnd + 19,
            ct: ct);

        double[] equivW = ProjectAugWeightsToFeatureSpace(bestW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, bestB, bestW, bestB);
    }

    /// <summary>
    /// Walk-forward CV for the magnitude regressor: trains on expanding windows with embargo.
    /// Stores per-fold weights for prediction averaging at inference time (avoids the lossy
    /// weight-averaging approach which can cancel useful asymmetric patterns).
    /// Returns the mean-averaged weights for backward-compatible single-model inference,
    /// plus per-fold weight arrays for prediction-averaged inference.
    /// </summary>
    private static (double[] EquivWeights, double EquivBias,
                    double[] AugWeights, double AugBias,
                    double[][]? FoldAugWeights, double[]? FoldAugBiases) FitElmMagnitudeRegressorCV(
        List<TrainingSample> train, int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations,
        double configLr, int configMaxEpochs, int configPatience,
        int cvFolds, int embargo,
        CancellationToken ct = default)
    {
        int foldSize = train.Count / (cvFolds + 1);
        if (foldSize < 20)
        {
            var single = FitElmMagnitudeRegressor(train, featureCount, hiddenSize,
                elmInputWeights, elmInputBiases, featureSubsets, learnerActivations,
                configLr, configMaxEpochs, configPatience, embargo, ct);
            return (single.EquivWeights, single.EquivBias, single.AugWeights, single.AugBias, null, null);
        }

        int augDim = featureCount + hiddenSize;
        int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
        var allFoldWeights = new List<double[]>();
        var allFoldBiases = new List<double>();

        for (int fold = 0; fold < cvFolds; fold++)
        {
            ct.ThrowIfCancellationRequested();
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int trainEnd  = Math.Max(0, testStart - embargo - purgeExtra);
            if (trainEnd < 20) continue;

            var foldTrain = train[..trainEnd];
            var (_, _, foldAugW, foldAugB) = FitElmMagnitudeRegressor(
                foldTrain, featureCount, hiddenSize, elmInputWeights, elmInputBiases, featureSubsets,
                learnerActivations, configLr, configMaxEpochs, configPatience, embargo, ct);

            allFoldWeights.Add(foldAugW);
            allFoldBiases.Add(foldAugB);
        }

        if (allFoldWeights.Count == 0)
        {
            var single = FitElmMagnitudeRegressor(train, featureCount, hiddenSize,
                elmInputWeights, elmInputBiases, featureSubsets, learnerActivations,
                configLr, configMaxEpochs, configPatience, embargo, ct);
            return (single.EquivWeights, single.EquivBias, single.AugWeights, single.AugBias, null, null);
        }

        // Compute mean-averaged weights as fallback / backward-compatible single model
        int validFolds = allFoldWeights.Count;
        double[] avgAugW = new double[augDim];
        double avgAugB = 0.0;
        for (int fi = 0; fi < validFolds; fi++)
        {
            for (int j = 0; j < augDim && j < allFoldWeights[fi].Length; j++)
                avgAugW[j] += allFoldWeights[fi][j];
            avgAugB += allFoldBiases[fi];
        }
        for (int j = 0; j < augDim; j++) avgAugW[j] /= validFolds;
        avgAugB /= validFolds;

        int K = elmInputWeights.Length;
        double[] equivW = ProjectAugWeightsToFeatureSpace(avgAugW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, avgAugB, avgAugW, avgAugB, allFoldWeights.ToArray(), allFoldBiases.ToArray());
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int featureCount, double tau,
        int hiddenSize, double[][] elmInputWeights, double[][] elmInputBiases,
        int[][]? featureSubsets, ElmActivation[] learnerActivations,
        int embargo = 0, CancellationToken ct = default)
    {
        if (train.Count < 10) return (new double[featureCount], 0.0);

        int K = elmInputWeights.Length;
        int augDim = featureCount + hiddenSize;
        int valSize = Math.Max(10, train.Count / 10);
        if (train.Count <= valSize)
            valSize = Math.Max(1, train.Count / 5);
        int qEmbargo = Math.Max(0, embargo);
        int trainSubEnd = train.Count - valSize - qEmbargo;
        if (trainSubEnd < 1)
            return (new double[featureCount], 0.0);
        int qValStart = trainSubEnd + qEmbargo;

        var augFeatures = BuildAugmentedFeatures(train, featureCount, hiddenSize, K, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);

        var targets = new double[train.Count];
        for (int i = 0; i < train.Count; i++) targets[i] = train[i].Magnitude;

        double capturedTau = tau;
        var (bestW, bestB) = FitLinearModelAdam(
            augFeatures, targets, trainSubEnd, qValStart, train.Count,
            computeGrad: (pred, target) =>
            {
                double err = target - pred;
                return err >= 0 ? -capturedTau : (1.0 - capturedTau);
            },
            computeValLoss: (pred, target) =>
            {
                double e = target - pred;
                return e >= 0 ? capturedTau * e : (capturedTau - 1.0) * e;
            },
            baseLr: 0.001,
            maxEpochs: 200,
            maxPatience: 15,
            rngSeed: trainSubEnd + 31,
            ct: ct);

        double[] equivW = ProjectAugWeightsToFeatureSpace(bestW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, bestB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Shared magnitude helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[][] BuildAugmentedFeatures(
        List<TrainingSample> samples, int featureCount, int hiddenSize, int K,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        K = Math.Min(K, Math.Min(elmInputWeights.Length, elmInputBiases.Length));
        int augDim = featureCount + hiddenSize;

        // Pre-compute default subset once — avoids per-sample per-learner allocation
        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();

        // Pre-compute per-learner effective hidden size (clamped to augDim slots)
        int[] learnerH = new int[K];
        for (int ki = 0; ki < K; ki++)
            learnerH[ki] = elmInputBiases[ki] is { Length: > 0 } && elmInputWeights[ki] is { Length: > 0 }
                ? Math.Min(hiddenSize, elmInputBiases[ki].Length)
                : 0;

        var augFeatures = new double[samples.Count][];
        var hSum   = new double[hiddenSize];
        var hCount = new int[hiddenSize];
        for (int i = 0; i < samples.Count; i++)
        {
            augFeatures[i] = new double[augDim];
            var f = samples[i].Features;
            for (int j = 0; j < Math.Min(featureCount, f.Length); j++)
                augFeatures[i][j] = f[j];

            Array.Clear(hSum, 0, hiddenSize);
            Array.Clear(hCount, 0, hiddenSize);
            for (int ki = 0; ki < K; ki++)
            {
                if (elmInputWeights[ki] is not { Length: > 0 } ||
                    elmInputBiases[ki] is not { Length: > 0 })
                {
                    continue;
                }

                var wIn = elmInputWeights[ki];
                var bIn = elmInputBiases[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length && featureSubsets[ki] is { Length: > 0 }
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                int effH = learnerH[ki];

                for (int h = 0; h < effH; h++)
                {
                    double z = bIn[h];
                    int rowOff = h * subLen;
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, f, sub, subLen);
                    ElmActivation learnerAct = learnerActivations.Length > 0
                        ? ResolveLearnerActivation(learnerActivations, ki)
                        : ElmActivation.Sigmoid;
                    hSum[h] += ElmMathHelper.Activate(z, learnerAct);
                    hCount[h]++;
                }
            }
            for (int h = 0; h < hiddenSize; h++)
                augFeatures[i][featureCount + h] = hCount[h] > 0 ? hSum[h] / hCount[h] : 0.0;
        }
        return augFeatures;
    }

    private static double[] ProjectAugWeightsToFeatureSpace(
        double[] augWeights, int featureCount, int hiddenSize, int K,
        List<TrainingSample> train,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        K = Math.Min(K, Math.Min(elmInputWeights.Length, elmInputBiases.Length));
        double[] equivW = new double[featureCount];
        if (augWeights.Length > 0)
            Array.Copy(augWeights, equivW, Math.Min(featureCount, augWeights.Length));

        if (train.Count == 0 || hiddenSize <= 0)
            return equivW;

        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();

        double[] meanActivationDeriv = new double[hiddenSize];
        int[] derivContributors = new int[hiddenSize];
        for (int ki = 0; ki < K; ki++)
        {
            if (elmInputWeights[ki] is not { Length: > 0 } ||
                elmInputBiases[ki] is not { Length: > 0 })
            {
                continue;
            }

            var wIn = elmInputWeights[ki];
            var bIn = elmInputBiases[ki];
            int effH = Math.Min(hiddenSize, bIn.Length);
            int[] sub = featureSubsets is not null && ki < featureSubsets.Length && featureSubsets[ki] is { Length: > 0 }
                ? featureSubsets[ki]
                : defaultSubset;
            int subLen = sub.Length;

            for (int h = 0; h < effH; h++)
            {
                double derivSum = 0;
                int rowOff = h * subLen;
                for (int i = 0; i < train.Count; i++)
                {
                    double z = bIn[h];
                    var f = train[i].Features;
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, f, sub, subLen);
                    ElmActivation learnerAct = learnerActivations.Length > 0
                        ? ResolveLearnerActivation(learnerActivations, ki)
                        : ElmActivation.Sigmoid;
                    derivSum += ActivationDerivative(z, learnerAct);
                }
                meanActivationDeriv[h] += derivSum / train.Count;
                derivContributors[h]++;
            }
        }
        for (int h = 0; h < hiddenSize; h++)
            if (derivContributors[h] > 0) meanActivationDeriv[h] /= derivContributors[h];

        for (int h = 0; h < hiddenSize; h++)
        {
            if (featureCount + h >= augWeights.Length)
                break;

            double hiddenW = augWeights[featureCount + h];
            if (Math.Abs(hiddenW) < 1e-10) continue;

            double deriv = meanActivationDeriv[h];
            int contributors = derivContributors[h];
            if (contributors == 0) continue;

            for (int ki = 0; ki < K; ki++)
            {
                if (elmInputWeights[ki] is not { Length: > 0 } ||
                    elmInputBiases[ki] is not { Length: > 0 })
                {
                    continue;
                }

                var bIn = elmInputBiases[ki];
                if (h >= bIn.Length) continue; // learner has fewer hidden units

                var wIn = elmInputWeights[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length && featureSubsets[ki] is { Length: > 0 }
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                int rowOff = h * subLen;

                for (int si = 0; si < subLen; si++)
                {
                    int fi = sub[si];
                    if (fi >= 0 && fi < featureCount && rowOff + si < wIn.Length)
                        equivW[fi] += hiddenW * deriv * wIn[rowOff + si] / contributors;
                }
            }
        }

        return equivW;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Activation derivative (for Taylor projection in magnitude regressor)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ActivationDerivative(double z, ElmActivation activation)
    {
        switch (activation)
        {
            case ElmActivation.Tanh:
                var t = Math.Tanh(z);
                return 1.0 - t * t;
            case ElmActivation.Relu:
                return z > 0.0 ? 1.0 : 0.0;
            default: // Sigmoid
                var s = MLFeatureHelper.Sigmoid(z);
                return s * (1.0 - s);
        }
    }
}
