using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  META-LABEL MODEL (Item 20: MLP with configurable hidden dim)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double[] HiddenWeights, double[] HiddenBiases, int HiddenDim) FitMetaLabelNetwork(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, int[]? topFeatureIndices = null, int hiddenDim = 0, int baseSeed = 0)
    {
        if (calSet.Count < 20) return ([], 0.0, [], [], 0);

        int metaDim = 2 + Math.Min(3, topFeatureIndices?.Length ?? 3);

        // Item 20: MLP with hidden layer if configured
        if (hiddenDim > 0)
            return FitMetaLabelMLP(
                calSet, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates,
                decisionThreshold, topFeatureIndices, metaDim, hiddenDim, baseSeed);

        var w = new double[metaDim]; double b = 0;
        const double sgdLr = 0.01;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double[] metaF = BuildMetaLabelFeatureVector(s.Features, featureCount, calibP, ensembleStd, topFeatureIndices);

                double z = b; for (int j = 0; j < metaDim; j++) z += w[j] * metaF[j];
                double p = Sigmoid(z);
                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = p - (isCorrect ? 1.0 : 0.0);
                b -= sgdLr * err;
                for (int j = 0; j < metaDim; j++) w[j] -= sgdLr * err * metaF[j];
            }
        }
        return (w, b, [], [], 0);
    }

    /// <summary>Item 20: 2-layer MLP meta-label model.</summary>
    private static (double[] Weights, double Bias, double[] HiddenWeights, double[] HiddenBiases, int HiddenDim) FitMetaLabelMLP(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, int[]? topFeatureIndices, int inputDim, int hiddenDim, int baseSeed = 0)
    {
        // Hidden layer: inputDim → hiddenDim (ReLU), Output: hiddenDim → 1 (sigmoid)
        var wH = new double[inputDim * hiddenDim]; var bH = new double[hiddenDim];
        var wO = new double[hiddenDim]; double bO = 0;
        var rng = CreateSeededRandom(baseSeed, 42);
        for (int i = 0; i < wH.Length; i++) wH[i] = (rng.NextDouble() - 0.5) * 0.1;
        for (int i = 0; i < wO.Length; i++) wO[i] = (rng.NextDouble() - 0.5) * 0.1;

        const double sgdLr = 0.005;
        var hidden = new double[hiddenDim];

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double[] input = BuildMetaLabelFeatureVector(s.Features, featureCount, calibP, ensembleStd, topFeatureIndices);

                // Forward: hidden = ReLU(W_H · input + b_H)
                for (int h = 0; h < hiddenDim; h++)
                {
                    double z = bH[h];
                    int rowOffset = h * inputDim;
                    for (int j = 0; j < inputDim; j++) z += wH[rowOffset + j] * input[j];
                    hidden[h] = Math.Max(0, z); // ReLU
                }
                double output = bO;
                for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
                double pred = Sigmoid(output);

                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = pred - (isCorrect ? 1.0 : 0.0);

                // Backward: output layer
                bO -= sgdLr * err;
                var outputWeightsBefore = (double[])wO.Clone();
                for (int h = 0; h < hiddenDim; h++) wO[h] -= sgdLr * err * hidden[h];

                // Backward: hidden layer
                for (int h = 0; h < hiddenDim; h++)
                {
                    if (hidden[h] <= 0) continue; // ReLU gradient
                    double dh = err * outputWeightsBefore[h];
                    bH[h] -= sgdLr * dh;
                    int rowOffset = h * inputDim;
                    for (int j = 0; j < inputDim; j++) wH[rowOffset + j] -= sgdLr * dh * input[j];
                }
            }
        }

        return (wO, bO, wH, bH, hiddenDim);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ABSTENTION GATE (Items 21,22,24)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold, double ThresholdBuy, double ThresholdSell, double[] CoverageAccCurve)
        FitAbstentionModel(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double[] metaLabelWeights, double metaLabelBias,
        double[] metaLabelHiddenWeights, double[] metaLabelHiddenBiases, int metaLabelHiddenDim,
        double decisionThreshold, int[]? topFeatureIndices = null, bool separateThresholds = false)
    {
        if (calSet.Count < 20) return ([], 0.0, 0.5, 0.5, 0.5, []);

        int dim = 3;
        var w = new double[dim]; double b = 0;
        const double sgdLr = 0.01;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double ms = ComputeMetaLabelScore(
                    calibP, ensembleStd, s.Features, featureCount,
                    metaLabelWeights, metaLabelBias, topFeatureIndices,
                    metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);

                var af = new[] { calibP, ensembleStd, ms };
                double z = b; for (int j = 0; j < dim; j++) z += w[j] * af[j];
                double p = Sigmoid(z);
                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = p - (isCorrect ? 1.0 : 0.0);
                b -= sgdLr * err;
                for (int j = 0; j < dim; j++) w[j] -= sgdLr * err * af[j];
            }
        }

        // Item 21: Finer sweep (0.5% steps) + Item 22: coverage-accuracy curve
        var curveEntries = new List<double>();
        double bestThreshold = 0.5, bestFilteredAcc = 0;
        double bestThresholdBuy = 0.5, bestThresholdSell = 0.5;

        // Precompute per-sample scores
        var sampleScores = new (double AbstScore, double CalibP, bool IsBuy, bool IsCorrect)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double calibP = GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double ensembleStd = ComputeEnsembleStd(calSet[i].Features, trees, baseLogOdds, lr, perTreeLearningRates);
            double ms = ComputeMetaLabelScore(
                calibP, ensembleStd, calSet[i].Features, featureCount,
                metaLabelWeights, metaLabelBias, topFeatureIndices,
                metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);
            var af = new[] { calibP, ensembleStd, ms };
            double z = b; for (int j = 0; j < dim; j++) z += w[j] * af[j];
            bool predictedBuy = calibP >= decisionThreshold;
            sampleScores[i] = (Sigmoid(z), calibP, predictedBuy, predictedBuy == (calSet[i].Direction > 0));
        }

        for (int thresholdBps = 2000; thresholdBps <= 8000; thresholdBps += 50)
        {
            double t = thresholdBps / 10_000.0;
            int correct = 0, total = 0;
            foreach (var ss in sampleScores)
            {
                if (ss.AbstScore < t) continue;
                total++; if (ss.IsCorrect) correct++;
            }
            double acc = total > 0 ? (double)correct / total : 0;
            double coverage = (double)total / calSet.Count;
            curveEntries.AddRange([t, coverage, acc]); // Item 22

            if (acc > bestFilteredAcc && total >= calSet.Count / 4)
            { bestFilteredAcc = acc; bestThreshold = t; }
        }

        // Item 24: separate buy/sell thresholds
        if (separateThresholds)
        {
            double bestBuyAcc = 0, bestSellAcc = 0;
            for (int thresholdBps = 2000; thresholdBps <= 8000; thresholdBps += 50)
            {
                double t = thresholdBps / 10_000.0;
                int cBuy = 0, tBuy = 0, cSell = 0, tSell = 0;
                foreach (var ss in sampleScores)
                {
                    if (ss.AbstScore < t) continue;
                    if (ss.IsBuy) { tBuy++; if (ss.IsCorrect) cBuy++; }
                    else { tSell++; if (ss.IsCorrect) cSell++; }
                }
                double buyAcc = tBuy > 0 ? (double)cBuy / tBuy : 0;
                double sellAcc = tSell > 0 ? (double)cSell / tSell : 0;
                if (buyAcc > bestBuyAcc && tBuy >= calSet.Count / 8) { bestBuyAcc = buyAcc; bestThresholdBuy = t; }
                if (sellAcc > bestSellAcc && tSell >= calSet.Count / 8) { bestSellAcc = sellAcc; bestThresholdSell = t; }
            }
        }

        return (w, b, bestThreshold, bestThresholdBuy, bestThresholdSell, curveEntries.ToArray());
    }

    private static double[] BuildMetaLabelFeatureVector(
        float[] features, int featureCount, double calibP, double ensembleStd, int[]? topFeatureIndices)
    {
        int[] effectiveTopFeatures = topFeatureIndices is { Length: > 0 }
            ? topFeatureIndices.Take(3).ToArray()
            : [0, 1, 2];

        var metaFeatures = new double[2 + effectiveTopFeatures.Length];
        metaFeatures[0] = calibP;
        metaFeatures[1] = ensembleStd;
        for (int i = 0; i < effectiveTopFeatures.Length; i++)
        {
            int featureIndex = effectiveTopFeatures[i];
            if (featureIndex < 0 || featureIndex >= featureCount || featureIndex >= features.Length)
                continue;

            metaFeatures[2 + i] = features[featureIndex];
        }

        return metaFeatures;
    }

    private static double ComputeMetaLabelScore(
        double calibP, double ensembleStd, float[] features, int featureCount,
        double[] metaLabelWeights, double metaLabelBias, int[]? topFeatureIndices = null,
        double[]? metaLabelHiddenWeights = null, double[]? metaLabelHiddenBiases = null, int metaLabelHiddenDim = 0)
    {
        if (metaLabelWeights.Length == 0)
            return 0.5;

        decimal? score = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            calibP, ensembleStd, features, featureCount,
            metaLabelWeights, metaLabelBias, topFeatureIndices,
            metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);
        return score.HasValue ? (double)score.Value : 0.5;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSORS (Item 44: quantile with Adam)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train, int featureCount, TrainingHyperparams hp)
    {
        var w = new double[featureCount]; double b = 0.0;
        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;
        if (trainSet.Count == 0) return (w, b);

        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0, beta1t = 1.0, beta2t = 1.0;
        int t = 0;
        double bestValLoss = double.MaxValue;
        var bestW = new double[featureCount]; double bestB = 0.0; int patience = 0;
        int epochs = hp.MaxEpochs;
        double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1;
        double l2 = hp.L2Lambda;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t;
                double alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5; valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }
        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    /// <summary>Item 44: Quantile regressor with Adam optimizer + early stopping.</summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int featureCount, double tau, TrainingHyperparams hp)
    {
        var w = new double[featureCount]; double b = 0;
        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0, vB = 0, beta1t = 1.0, beta2t = 1.0;
        int t = 0;

        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;

        double bestValLoss = double.MaxValue;
        var bestW = new double[featureCount]; double bestB = 0; int patience = 0;
        double baseLr = 0.001;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / 100.0));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                double grad = err >= 0 ? -tau : (1 - tau);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t;
                double alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * grad;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * grad * grad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = grad * s.Features[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                valLoss += err >= 0 ? tau * err : (1 - tau) * (-err); valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= Math.Max(3, hp.EarlyStoppingPatience / 2)) break;
        }
        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }
}
