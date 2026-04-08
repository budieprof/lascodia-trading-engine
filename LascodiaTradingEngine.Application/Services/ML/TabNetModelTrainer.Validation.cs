using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION (with per-fold metrics)
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples, TrainingHyperparams hp,
        int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax,
        double lr, double sparsityCoeff, int epochs, double bnMomentum,
        CancellationToken ct)
    {
        int folds    = hp.WalkForwardFolds;
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < 50)
        {
            _logger.LogWarning("TabNet walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImps   = new List<double[]>(folds);
        var foldMetrics = new List<WalkForwardFoldMetric>(folds);
        int badFolds   = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd    = (fold + 2) * foldSize;
            int testStart  = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) continue;

            var foldTrain = samples[..trainEnd].ToList();
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count) foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) continue;

            int cvEpochs = Math.Max(10, epochs / 3);
            var cvW = FitTabNet(
                foldTrain, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, lr, sparsityCoeff, cvEpochs,
                hp.LabelSmoothing, null, null, null, hp.TemporalDecayLambda, hp.L2Lambda,
                hp.EarlyStoppingPatience, 0.0, hp.MaxGradNorm, 0, bnMomentum, 128, 0, ct);

            var m = EvaluateTabNet(foldTest, cvW, 1.0, 0.0, [], 0, F);

            double[] foldImp = ComputeAttentionStats(foldTest, cvW).MeanAttn;

            // Equity-curve gate
            var preds = new (int Predicted, int Actual)[foldTest.Count];
            var priorBuf = new double[F];
            var attnBuf  = new double[F];
            for (int i = 0; i < foldTest.Count; i++)
            {
                var fwd = ForwardPass(foldTest[i].Features, cvW, priorBuf, attnBuf, false, 0, null);
                preds[i] = (fwd.Prob >= 0.5 ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(preds);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            accList.Add(m.Accuracy);
            f1List.Add(m.F1);
            evList.Add(m.ExpectedValue);
            sharpeList.Add(m.SharpeRatio);
            foldImps.Add(foldImp);
            foldMetrics.Add(new WalkForwardFoldMetric(m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, maxDD));
            if (isBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0 ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("TabNet equity-curve gate: {Bad}/{Total} folds failed", badFolds, accList.Count);

        double avgAcc    = accList.Average();
        double stdAcc    = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("TabNet Sharpe trend gate: slope={Slope:F3} < threshold", sharpeTrend);
            equityCurveGateFailed = true;
        }

        double[]? featureStabilityScores = null;
        if (foldImps.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = foldImps.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImps[fi].Length > j ? foldImps[fi][j] : 0;
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = (foldImps[fi].Length > j ? foldImps[fi][j] : 0) - meanImp;
                    varImp += d * d;
                }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0.0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0.0;
            }
        }

        return (new WalkForwardResult(
            AvgAccuracy:            avgAcc,
            StdAccuracy:            stdAcc,
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores,
            FoldMetrics:            foldMetrics.ToArray()), equityCurveGateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BN TRAVERSAL HELPER (shared between ComputeEpochBatchStats and
    //  UpdateBnRunningStats to eliminate ~150 lines of duplication)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[][] LayerSums, double[][] LayerSqSums) TraverseBnLayers(
        TabNetWeights w, List<TrainingSample> samples, int batchN, int[]? sampleIndices = null)
    {
        var layerSums  = new double[w.TotalBnLayers][];
        var layerSqSum = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            layerSums[b]  = new double[dim];
            layerSqSum[b] = new double[dim];
        }

        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];

        for (int si = 0; si < batchN; si++)
        {
            int sampleIdx = sampleIndices is not null ? sampleIndices[si] : si;
            var sample = samples[sampleIdx];
            Array.Fill(priorBuf, 1.0, 0, w.F);
            double[] hPrev = new double[w.HiddenDim];

            for (int s = 0; s < w.NSteps; s++)
            {
                // Attention BN input
                var attnInput = new double[w.F];
                if (s == 0)
                {
                    if (w.InitialBnFcW.Length > 0 && w.InitialBnFcW[0].Length == w.F)
                    {
                        for (int j = 0; j < w.F; j++)
                        {
                            double val = w.InitialBnFcB[j];
                            for (int k = 0; k < w.F; k++) val += w.InitialBnFcW[j][k] * sample.Features[k];
                            attnInput[j] = val;
                        }
                    }
                    else
                    {
                        for (int j = 0; j < w.F; j++) attnInput[j] = sample.Features[j];
                    }
                }
                else
                {
                    for (int j = 0; j < w.F; j++)
                    {
                        double val = 0;
                        for (int k = 0; k < w.HiddenDim && k < w.AttnFcW[s][j].Length; k++)
                            val += w.AttnFcW[s][j][k] * hPrev[k];
                        attnInput[j] = val + w.AttnFcB[s][j];
                    }
                }

                int bnIdx = s;
                for (int j = 0; j < w.F; j++)
                {
                    layerSums[bnIdx][j]  += attnInput[j];
                    layerSqSum[bnIdx][j] += attnInput[j] * attnInput[j];
                }

                var bnAttn = ApplyBatchNorm(attnInput, w.F, w.BnGamma[bnIdx], w.BnBeta[bnIdx],
                    w.BnMean[bnIdx], w.BnVar[bnIdx]);
                for (int j = 0; j < w.F; j++) attnBuf[j] = priorBuf[j] * bnAttn[j];
                double[] attn = w.UseSparsemax ? Sparsemax(attnBuf, w.F) : SoftmaxArr(attnBuf, w.F);
                for (int j = 0; j < w.F; j++)
                    priorBuf[j] = Math.Max(1e-6, priorBuf[j] * (w.Gamma - attn[j]));

                double[] masked = new double[w.F];
                for (int j = 0; j < w.F; j++) masked[j] = sample.Features[j] * attn[j];

                // Shared layers
                double[] h = masked;
                int inputDim = w.F;
                for (int l = 0; l < w.SharedLayers; l++)
                {
                    int bnSIdx = w.NSteps + l;
                    var linear = FcLinear(h, inputDim, w.HiddenDim, w.SharedW[l], w.SharedB[l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnSIdx][j]  += linear[j];
                        layerSqSum[bnSIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.HiddenDim, w.BnGamma[bnSIdx], w.BnBeta[bnSIdx],
                        w.BnMean[bnSIdx], w.BnVar[bnSIdx]);
                    var gate = FcSigmoid(h, inputDim, w.HiddenDim, w.SharedGW[l], w.SharedGB[l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0 && h.Length == w.HiddenDim)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * SqrtHalfResidualScale;
                    h = hNew;
                    inputDim = w.HiddenDim;
                }

                // Step layers
                for (int l = 0; l < w.StepLayers; l++)
                {
                    int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                    var linear = FcLinear(h, w.HiddenDim, w.HiddenDim, w.StepW[s][l], w.StepB[s][l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnStIdx][j]  += linear[j];
                        layerSqSum[bnStIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.HiddenDim, w.BnGamma[bnStIdx], w.BnBeta[bnStIdx],
                        w.BnMean[bnStIdx], w.BnVar[bnStIdx]);
                    var gate = FcSigmoid(h, w.HiddenDim, w.HiddenDim, w.StepGW[s][l], w.StepGB[s][l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * SqrtHalfResidualScale;
                    h = hNew;
                }

                hPrev = h;
            }
        }

        return (layerSums, layerSqSum);
    }

    private void UpdateBnRunningStats(TabNetWeights w, List<TrainingSample> fitSet, double momentum, int ghostBatchSize)
    {
        int batchN = Math.Min(fitSet.Count, ghostBatchSize);
        if (batchN < MinCalibrationSamples) return;

        var (layerSums, layerSqSum) = TraverseBnLayers(w, fitSet, batchN);

        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = layerSums[b].Length;
            for (int j = 0; j < dim; j++)
            {
                double batchMean = layerSums[b][j] / batchN;
                double batchVar  = layerSqSum[b][j] / batchN - batchMean * batchMean;
                batchVar = Math.Max(batchVar, 0.0);

                w.BnMean[b][j] = momentum * w.BnMean[b][j] + (1 - momentum) * batchMean;
                w.BnVar[b][j]  = momentum * w.BnVar[b][j]  + (1 - momentum) * batchVar;
            }
        }
    }

    private static (double[][] Means, double[][] Vars) ComputeEpochBatchStats(
        TabNetWeights w, List<TrainingSample> fitSet, int ghostBatchSize, Random epochRng)
    {
        int batchN = Math.Min(fitSet.Count, ghostBatchSize);
        var means = new double[w.TotalBnLayers][];
        var vars  = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            means[b] = new double[dim];
            vars[b]  = new double[dim];
        }

        if (batchN < MinCalibrationSamples) return (means, vars);

        // Shuffle sample indices for true ghost-batch randomization
        var ghostIndices = new int[fitSet.Count];
        for (int i = 0; i < ghostIndices.Length; i++) ghostIndices[i] = i;
        for (int i = ghostIndices.Length - 1; i > 0; i--)
        {
            int k = epochRng.Next(i + 1);
            (ghostIndices[k], ghostIndices[i]) = (ghostIndices[i], ghostIndices[k]);
        }

        var (layerSums, layerSqSum) = TraverseBnLayers(w, fitSet, batchN, ghostIndices);

        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = layerSums[b].Length;
            for (int j = 0; j < dim; j++)
            {
                means[b][j] = layerSums[b][j] / batchN;
                vars[b][j]  = Math.Max(0.0, layerSqSum[b][j] / batchN - means[b][j] * means[b][j]);
            }
        }

        return (means, vars);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY / DENSITY / COVARIATE / TEMPORAL / EQUITY / SHARPE
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(IReadOnlyList<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 30) return 0;
        int nonStat = 0;
        for (int j = 0; j < F; j++)
        {
            var vals = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) vals[i] = trainSet[i].Features[j];
            int n = vals.Length, half = n / 2;
            double varFirst = Variance(vals, 0, half), varSecond = Variance(vals, half, n - half);
            double ratio = varSecond > Eps ? varFirst / varSecond : 1.0;
            if (ratio > 3.0 || ratio < 0.333) nonStat++;
        }
        return nonStat;
    }

    private static double[] ComputeDensityRatioWeights(IReadOnlyList<TrainingSample> trainSet, int F, int windowDays)
    {
        int n = trainSet.Count, recentCount = Math.Min(n / 3, windowDays * 24);
        if (recentCount < 20) return Enumerable.Repeat(1.0, n).ToArray();
        int cutoff = n - recentCount;
        var dw = new double[F]; double bias = 0;
        for (int ep = 0; ep < 30; ep++)
            for (int i = 0; i < n; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0, z = bias;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) z += dw[j] * trainSet[i].Features[j];
                double err = Sigmoid(z) - label;
                bias -= CalibrationLr * err;
                bias = Math.Clamp(bias, -MaxWeightVal, MaxWeightVal);
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                {
                    dw[j] -= CalibrationLr * err * trainSet[i].Features[j];
                    dw[j] = Math.Clamp(dw[j], -MaxWeightVal, MaxWeightVal);
                }
            }
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double z = bias;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) z += dw[j] * trainSet[i].Features[j];
            double p = Math.Clamp(Sigmoid(z), 0.01, 0.99); weights[i] = p / (1 - p);
        }
        double sum = weights.Sum();
        if (sum > Eps) for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    private static double[] ComputeCovariateShiftWeights(IReadOnlyList<TrainingSample> trainSet, double[][] parentBp, int F)
    {
        int n = trainSet.Count; var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            int outside = 0, checked_ = 0;
            for (int j = 0; j < F && j < parentBp.Length; j++)
            {
                if (parentBp[j].Length < 2) continue; checked_++;
                double v = trainSet[i].Features[j];
                if (v < parentBp[j][0] || v > parentBp[j][^1]) outside++;
            }
            weights[i] = 1.0 + (checked_ > 0 ? (double)outside / checked_ : 0);
        }
        double mean = weights.Average();
        if (mean > Eps) for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    private static double[] ComputeTemporalWeights(int n, double lambdaDecay)
    {
        var tw = new double[n];
        if (lambdaDecay <= 0) { Array.Fill(tw, 1.0 / Math.Max(1, n)); return tw; }
        double sum = 0;
        for (int i = 0; i < n; i++) { tw[i] = Math.Exp(-lambdaDecay * (n - 1 - i)); sum += tw[i]; }
        if (sum > Eps) for (int i = 0; i < n; i++) tw[i] /= sum;
        return tw;
    }

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats((int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);
        double equity = 0, peak = 0, maxDD = 0;
        var returns = new List<double>(predictions.Length);
        foreach (var (pred, actual) in predictions)
        {
            double ret = pred == actual ? 1.0 : -1.0; returns.Add(ret); equity += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 1e-10 ? (peak - equity) / peak : (peak - equity > 0 ? 1.0 : 0.0);
            if (dd > maxDD) maxDD = dd;
        }
        double avgRet = returns.Average(), stdRet = StdDev(returns, avgRet);
        return (maxDD, stdRet > 1e-10 ? avgRet / stdRet * Math.Sqrt(252) : 0);
    }

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpeList)
    {
        int n = sharpeList.Count; if (n < 3) return 0;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = 0; i < n; i++) { sumX += i; sumY += sharpeList[i]; sumXX += i * i; sumXY += i * sharpeList[i]; }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) > 1e-10 ? (n * sumXY - sumX * sumY) / denom : 0;
    }
}
