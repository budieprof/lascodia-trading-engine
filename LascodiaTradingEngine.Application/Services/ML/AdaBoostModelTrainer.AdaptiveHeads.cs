using LascodiaTradingEngine.Application.MLModels.Shared;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Meta-label model (TorchSharp logistic correctness predictor) ─────────

    /// <summary>
    /// Trains a 7-feature TorchSharp logistic meta-classifier on the calibration set.
    /// Meta-features: [calibP, ensembleStd, top feature values].
    /// Label = 1 if the AdaBoost prediction was correct for that sample.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns (weights[2 + topFeatures], bias).
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double               decisionThreshold,
        int[]                metaLabelTopFeatureIndices,
        int                  F,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN,
        double               routingThreshold = DefaultConditionalRoutingThreshold,
        CancellationToken    ct = default)
    {
        int rawTop = metaLabelTopFeatureIndices.Length > 0
            ? Math.Min(5, metaLabelTopFeatureIndices.Length)
            : Math.Min(5, F);
        int metaDim = 2 + rawTop;
        if (calSet.Count < 10) return (new double[metaDim], 0.0);

        int n      = calSet.Count;

        var xArr = new float[n * metaDim];
        var yArr = new float[n];

        for (int i = 0; i < n; i++)
        {
            var    s         = calSet[i];
            double calibP    = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, decisionThreshold,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            double ensStd    = ComputeEnsembleStd(s.Features, stumps, alphas);

            xArr[i * metaDim + 0] = (float)calibP;
            xArr[i * metaDim + 1] = (float)ensStd;
            for (int j = 0; j < rawTop; j++)
            {
                int featureIndex = metaLabelTopFeatureIndices.Length > 0 && j < metaLabelTopFeatureIndices.Length
                    ? metaLabelTopFeatureIndices[j]
                    : j;
                if (featureIndex < 0 || featureIndex >= s.Features.Length)
                    continue;
                xArr[i * metaDim + 2 + j] = s.Features[featureIndex];
            }

            int predicted = calibP >= decisionThreshold ? 1 : -1;
            int actual    = s.Direction > 0 ? 1 : -1;
            yArr[i] = predicted == actual ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(metaDim, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.001);

        using var xTConst = torch.tensor(xArr, device: CPU).reshape(n, metaDim);
        using var yTConst = torch.tensor(yArr, device: CPU).reshape(n, 1);

        for (int epoch = 0; epoch < 40; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            opt.zero_grad();
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yTConst);
            loss.backward();
            opt.step();
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var mw = new double[metaDim];
        for (int i = 0; i < metaDim; i++) mw[i] = finalW[i];
        return (mw, finalB);
    }

    // ── Abstention gate (TorchSharp logistic selective predictor) ────────────

    /// <summary>
    /// Trains a 3-feature TorchSharp logistic gate on [calibP, ensembleStd, metaLabelScore].
    /// Label = 1 if the AdaBoost prediction was correct for that calibration sample.
    /// ensembleStd = std-dev of per-stump sigmoid probabilities — measures ensemble disagreement.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns (weights[3], bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        double               decisionThreshold,
        int[]                metaLabelTopFeatureIndices,
        int                  F)
        =>
            FitAbstentionModel(
                calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                metaLabelWeights, metaLabelBias, decisionThreshold, metaLabelTopFeatureIndices, F,
                double.NaN, double.NaN, double.NaN, double.NaN, DefaultConditionalRoutingThreshold);

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               temperatureScale,
        double[]             isotonicBp,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        double               decisionThreshold,
        int[]                metaLabelTopFeatureIndices,
        int                  F,
        double               plattABuy,
        double               plattBBuy,
        double               plattASell,
        double               plattBSell,
        double               routingThreshold,
        CancellationToken    ct = default)
    {
        const int Dim     = 3;   // [calibP, ensembleStd, metaLabelScore]
        if (calSet.Count < 10) return (new double[Dim], 0.0, 0.5);

        int n      = calSet.Count;
        int rawTop = metaLabelTopFeatureIndices.Length > 0
            ? Math.Min(5, metaLabelTopFeatureIndices.Length)
            : Math.Min(5, F);
        int metaDim = 2 + rawTop;

        var xArr = new float[n * Dim];
        var yArr = new float[n];
        var mf   = new double[metaDim];

        for (int i = 0; i < n; i++)
        {
            var    s         = calSet[i];
            double calibP    = PredictProb(
                s.Features, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, decisionThreshold,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
            double ensStd    = ComputeEnsembleStd(s.Features, stumps, alphas);

            // Meta-label score
            mf[0] = calibP;
            mf[1] = ensStd;
            for (int j = 0; j < rawTop; j++)
            {
                int featureIndex = metaLabelTopFeatureIndices.Length > 0 && j < metaLabelTopFeatureIndices.Length
                    ? metaLabelTopFeatureIndices[j]
                    : j;
                mf[2 + j] = featureIndex >= 0 && featureIndex < s.Features.Length
                    ? s.Features[featureIndex]
                    : 0.0;
            }
            double mz = metaLabelBias;
            for (int j = 0; j < metaDim && j < metaLabelWeights.Length; j++)
                mz += metaLabelWeights[j] * mf[j];
            double metaScore = MLFeatureHelper.Sigmoid(mz);

            xArr[i * Dim + 0] = (float)calibP;
            xArr[i * Dim + 1] = (float)ensStd;
            xArr[i * Dim + 2] = (float)metaScore;
            yArr[i] = (calibP >= decisionThreshold) == (s.Direction > 0) ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(Dim, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.001);

        using var xTConst = torch.tensor(xArr, device: CPU).reshape(n, Dim);
        using var yTConst = torch.tensor(yArr, device: CPU).reshape(n, 1);

        for (int epoch = 0; epoch < 60; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            opt.zero_grad();
            using var logit = torch.mm(xTConst, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yTConst);
            loss.backward();
            opt.step();
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var aw = new double[Dim];
        for (int i = 0; i < Dim; i++) aw[i] = finalW[i];
        return (aw, finalB, 0.5);
    }

    // ── Magnitude linear regressor (TorchSharp: Adam + Huber + weight_decay + cosine LR) ──

    /// <summary>
    /// Fits a linear magnitude regressor using TorchSharp's vectorised Adam optimizer.
    /// Huber loss (δ=1) is computed as a tensor expression for full batch-level
    /// parallelism.  L2 regularisation is applied via Adam's <c>weight_decay</c>
    /// argument rather than manually adding it to the gradient, which is the
    /// numerically stable formulation used by PyTorch and TorchSharp.
    /// Cosine-annealed LR is approximated by scaling the loss (avoids resetting
    /// moment buffers that would occur if the optimizer were re-created each epoch).
    /// </summary>
    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  F,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        if (train.Count == 0) return (new double[F], 0.0);

        int    n          = train.Count;
        bool   canEs      = n >= 30;
        int    valSize    = canEs ? Math.Max(5, n / 10) : 0;
        int    trainN     = n - valSize;
        if (trainN == 0) return (new double[F], 0.0);

        double baseLr     = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2         = hp.L2Lambda;
        int    epochs     = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);
        int    batchSz    = Math.Min(256, trainN);

        // Build flat arrays once; mini-batches slice into them
        var xArr = new float[trainN * F];
        var yArr = new float[trainN];
        for (int i = 0; i < trainN; i++)
        {
            Array.Copy(train[i].Features, 0, xArr, i * F, F);
            yArr[i] = train[i].Magnitude;
        }

        float[]? vxArr = null; float[]? vyArr = null;
        if (canEs)
        {
            vxArr = new float[valSize * F];
            vyArr = new float[valSize];
            for (int i = 0; i < valSize; i++)
            {
                Array.Copy(train[trainN + i].Features, 0, vxArr, i * F, F);
                vyArr[i] = train[trainN + i].Magnitude;
            }
        }

        // TorchSharp parameters: weight [F,1] and bias [1]
        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: baseLr, weight_decay: l2);

        float[]? bestW       = null;
        float    bestB       = 0f;
        double   bestValLoss = double.MaxValue;
        int      noImprove   = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            // Cosine LR scaling: approximate schedule by scaling loss (preserves moments)
            double cosScale = 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            for (int start = 0; start < trainN; start += batchSz)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(start + batchSz, trainN);
                int bsz = end - start;

                var xB = new float[bsz * F]; var yB = new float[bsz];
                Array.Copy(xArr, start * F, xB, 0, bsz * F);
                Array.Copy(yArr, start,     yB, 0, bsz);

                opt.zero_grad();
                using var xT    = torch.tensor(xB, device: CPU).reshape(bsz, F);
                using var yT    = torch.tensor(yB, device: CPU).reshape(bsz, 1);
                using var pred  = torch.mm(xT, wP) + bP;
                using var err   = pred - yT;
                using var abse  = err.abs();
                using var huber = torch.where(abse <= 1.0f,
                                              err.pow(2f) * 0.5f,
                                              abse - 0.5f).mean();
                // Apply cosine scaling to approximate LR annealing
                (cosScale < 1.0 ? huber * (float)cosScale : huber).backward();
                opt.step();
            }

            if (!canEs) continue;

            using (no_grad())
            {
                using var vxT   = torch.tensor(vxArr!, device: CPU).reshape(valSize, F);
                using var vyT   = torch.tensor(vyArr!, device: CPU).reshape(valSize, 1);
                using var vpred = torch.mm(vxT, wP) + bP;
                using var verr  = vpred - vyT;
                using var vabse = verr.abs();
                using var vloss = torch.where(vabse <= 1.0f,
                                              verr.pow(2f) * 0.5f,
                                              vabse - 0.5f).mean();
                double vl = vloss.item<float>();
                if (vl < bestValLoss)
                {
                    bestValLoss = vl;
                    bestW       = wP.cpu().data<float>().ToArray();
                    bestB       = bP.cpu().data<float>()[0];
                    noImprove   = 0;
                }
                else if (++noImprove >= esPatience) break;
            }
        }

        float[] finalW = bestW ?? wP.cpu().data<float>().ToArray();
        float   finalB = bestW is null ? bP.cpu().data<float>()[0] : bestB;

        var wOut = new double[F];
        for (int j = 0; j < F; j++) wOut[j] = finalW[j];
        return (wOut, finalB);
    }

    // ── Quantile magnitude regressor (TorchSharp: Adam + pinball loss + weight_decay) ──

    /// <summary>
    /// Fits a linear quantile regressor using TorchSharp's vectorised Adam optimizer.
    /// The pinball (check) loss: L = τ·max(r,0) − (1−τ)·min(r,0), where r = y − ŷ,
    /// is computed as a tensor expression for batch-level parallelism.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns weights and bias for the τ-th conditional quantile.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train,
        int                  F,
        double               tau,
        CancellationToken    ct = default)
    {
        if (train.Count == 0) return (new double[F], 0.0);

        int n = train.Count;
        var xArr = new float[n * F];
        var yArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(train[i].Features, 0, xArr, i * F, F);
            yArr[i] = train[i].Magnitude;
        }

        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.005, weight_decay: 1e-4);

        float tauF    = (float)tau;
        float tauMF   = (float)(1.0 - tau);  // 1 - τ

        // Hoist constant tensors out of the training loop to avoid per-pass allocation
        using var xTConst = torch.tensor(xArr, device: CPU).reshape(n, F);
        using var yTConst = torch.tensor(yArr, device: CPU).reshape(n, 1);

        double prevLoss = double.MaxValue;
        int    noImpro  = 0;

        for (int pass = 0; pass < 60; pass++)
        {
            ct.ThrowIfCancellationRequested();
            opt.zero_grad();
            using var pred  = torch.mm(xTConst, wP) + bP;
            using var r     = yTConst - pred;                              // residual r = y - ŷ
            // Pinball: τ·max(r,0) − (1-τ)·min(r,0)  = τ·relu(r) + (1-τ)·relu(-r)
            using var loss  = (functional.relu(r) * tauF + functional.relu(-r) * tauMF).mean();
            loss.backward();
            opt.step();

            double curLoss = loss.item<float>();
            if (prevLoss - curLoss < 1e-7) { if (++noImpro >= 5) break; }
            else                           noImpro = 0;
            prevLoss = curLoss;
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var wOut = new double[F];
        for (int j = 0; j < F; j++) wOut[j] = finalW[j];
        return (wOut, finalB);
    }

    // ── Cross-fit adaptive heads ──────────────────────────────────────────────

    /// <summary>
    /// Cross-fits meta-label and abstention models using K-fold cross-validation on the
    /// diagnostics set, preventing overfitting of the adaptive heads to the same data they evaluate on.
    /// Each fold: fit on K-1 folds, predict on held-out fold. Averages weights across folds.
    /// </summary>
    private static (double[] MetaLabelWeights, double MetaLabelBias, double MetaLabelThreshold,
                     double[] AbstentionWeights, double AbstentionBias, double AbstentionThreshold,
                     bool Used, int FoldCount)
        CrossFitAdaptiveHeads(
            List<TrainingSample> diagnosticsSet,
            List<GbmTree>        stumps,
            List<double>         alphas,
            double               plattA,
            double               plattB,
            double               temperatureScale,
            double[]             isotonicBp,
            double               optimalThreshold,
            int[]                metaLabelTopFeatureIndices,
            int                  F,
            double               plattABuy,
            double               plattBBuy,
            double               plattASell,
            double               plattBSell,
            double               routingThreshold,
            int                  minSamples = 20,
            CancellationToken    ct = default)
    {
        const int K = 3;
        int n = diagnosticsSet.Count;

        if (n < minSamples * K)
            return ([], 0.0, 0.5, [], 0.0, 0.5, false, 0);

        int foldSize = n / K;
        int rawTop = metaLabelTopFeatureIndices.Length > 0
            ? Math.Min(5, metaLabelTopFeatureIndices.Length)
            : Math.Min(5, F);
        int metaDim = 2 + rawTop;
        const int abstDim = 3;

        var mlWeightsAccum = new double[metaDim];
        double mlBiasAccum = 0.0;
        var absWeightsAccum = new double[abstDim];
        double absBiasAccum = 0.0;
        int validFolds = 0;

        for (int fold = 0; fold < K && !ct.IsCancellationRequested; fold++)
        {
            int testStart = fold * foldSize;
            int testEnd   = fold == K - 1 ? n : (fold + 1) * foldSize;

            var foldTrain = new List<TrainingSample>(n - (testEnd - testStart));
            for (int i = 0; i < testStart; i++) foldTrain.Add(diagnosticsSet[i]);
            for (int i = testEnd; i < n; i++) foldTrain.Add(diagnosticsSet[i]);

            if (foldTrain.Count < minSamples) continue;

            var (mw, mb) = FitMetaLabelModel(
                foldTrain, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                optimalThreshold, metaLabelTopFeatureIndices, F,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold, ct);

            var (aw, ab, _) = FitAbstentionModel(
                foldTrain, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                mw, mb, optimalThreshold, metaLabelTopFeatureIndices, F,
                plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold, ct);

            for (int j = 0; j < metaDim && j < mw.Length; j++) mlWeightsAccum[j] += mw[j];
            mlBiasAccum += mb;
            for (int j = 0; j < abstDim && j < aw.Length; j++) absWeightsAccum[j] += aw[j];
            absBiasAccum += ab;
            validFolds++;
        }

        if (validFolds == 0)
            return ([], 0.0, 0.5, [], 0.0, 0.5, false, 0);

        for (int j = 0; j < metaDim; j++) mlWeightsAccum[j] /= validFolds;
        mlBiasAccum /= validFolds;
        for (int j = 0; j < abstDim; j++) absWeightsAccum[j] /= validFolds;
        absBiasAccum /= validFolds;

        return (mlWeightsAccum, mlBiasAccum, 0.5,
                absWeightsAccum, absBiasAccum, 0.5,
                true, validFolds);
    }
}
