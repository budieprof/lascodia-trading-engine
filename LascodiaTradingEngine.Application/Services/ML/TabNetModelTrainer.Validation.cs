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
        bool useGlu, double lr, double sparsityCoeff, int epochs, double bnMomentum,
        TabNetRunContext runContext,
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
        var oofResiduals = new List<double>(samples.Count / 3);
        int badFolds   = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd    = (fold + 2) * foldSize;
            int testStart  = testEnd - foldSize;
            // Effective gap between train and test = embargo + lookback window + PurgeHorizonBars.
            // The embargo prevents label leakage, the lookback window accounts for indicator dependencies,
            // and PurgeHorizonBars provides an additional configurable purge zone.
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

            // #22: Per-fold class balance check — skip folds with single class
            int foldTrainPos = foldTrain.Count(s => s.Direction > 0);
            int foldTestPos = foldTest.Count(s => s.Direction > 0);
            if (foldTrainPos == 0 || foldTrainPos == foldTrain.Count || foldTestPos == 0 || foldTestPos == foldTest.Count)
            {
                _logger.LogWarning("TabNet CV fold {Fold}: single-class data (trainPos={TrainPos}/{Train}, testPos={TestPos}/{Test}), skipping",
                    fold, foldTrainPos, foldTrain.Count, foldTestPos, foldTest.Count);
                continue;
            }

            int foldSelectionCount = Math.Min(
                Math.Max(runContext.MinCalibrationSamples, foldTrain.Count / 10),
                Math.Max(0, foldTrain.Count - hp.MinSamples));
            if (foldSelectionCount < runContext.MinCalibrationSamples)
                continue;

            var foldSelection = foldTrain[^foldSelectionCount..];
            var foldFitTrain = foldTrain[..^foldSelectionCount];
            if (foldFitTrain.Count < hp.MinSamples)
                continue;

            int cvEpochs = Math.Max(10, epochs / 3);
            var cvW = FitTabNet(
                foldFitTrain, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, sparsityCoeff, cvEpochs,
                hp.LabelSmoothing, null, null, null, hp.TemporalDecayLambda, hp.L2Lambda,
                hp.EarlyStoppingPatience, 0.0, hp.MaxGradNorm, 0, bnMomentum, 128, 0, runContext, ct);

            var foldCalibrationFit = FitTabNetCalibrationStack(
                foldSelection,
                null,
                cvW,
                hp.FitTemperatureScale,
                runContext.MinCalibrationSamples,
                Math.Max(6, runContext.CalibrationEpochs / 2),
                runContext.CalibrationLr);
            var foldCalibration = foldCalibrationFit.FinalSnapshot;
            double foldThreshold = ComputeOptimalThreshold(
                foldSelection, cvW, foldCalibration, hp.ThresholdSearchMin, hp.ThresholdSearchMax);
            var m = EvaluateTabNet(foldTest, cvW, foldCalibration, [], 0, F, foldThreshold);

            double[] foldImp = ComputeAttentionStats(foldTest, cvW).MeanAttn;

            // Equity-curve gate
            var preds = new (int Predicted, int Actual)[foldTest.Count];
            for (int i = 0; i < foldTest.Count; i++)
            {
                double p = TabNetCalibProb(foldTest[i].Features, cvW, foldCalibration);
                preds[i] = (p >= foldThreshold ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
                double y = foldTest[i].Direction > 0 ? 1.0 : 0.0;
                oofResiduals.Add(Math.Abs(p - y));
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

        // #24: Minimum fold count enforcement
        if (accList.Count == 0)
        {
            _logger.LogWarning(
                "TabNet walk-forward CV: all {Folds} folds were skipped (insufficient data per fold). CV metrics are unavailable.",
                folds);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }
        if (accList.Count < 2)
        {
            _logger.LogWarning(
                "TabNet walk-forward CV: only {Completed}/{Total} folds completed — variance estimates unreliable.",
                accList.Count, folds);
        }

        // #25: Bad-fold threshold — use Ceiling for faithful enforcement
        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0 ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds >= (int)Math.Ceiling(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("TabNet equity-curve gate: {Bad}/{Total} folds failed", badFolds, accList.Count);

        double avgAcc    = accList.Average();
        double stdAcc    = StdDev(accList, avgAcc);
        double avgF1     = f1List.Average();
        double stdF1     = StdDev(f1List, avgF1);
        double avgEV     = evList.Average();
        double stdEV     = StdDev(evList, avgEV);
        double avgSharpe = sharpeList.Average();
        double stdSharpe = StdDev(sharpeList, avgSharpe);
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
            AvgF1:                  avgF1,
            AvgEV:                  avgEV,
            AvgSharpe:              avgSharpe,
            FoldCount:              accList.Count,
            StdF1:                  stdF1,
            StdEV:                  stdEV,
            StdSharpe:              stdSharpe,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores,
            FoldMetrics:            foldMetrics.ToArray(),
            OofResiduals:           oofResiduals.Count > 0 ? oofResiduals.ToArray() : null), equityCurveGateFailed);
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
                    var hNew = new double[w.HiddenDim];
                    if (w.UseGlu)
                    {
                        var gate = FcSigmoid(h, inputDim, w.HiddenDim, w.SharedGW[l], w.SharedGB[l]);
                        for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    }
                    else
                    {
                        Array.Copy(bnH, hNew, w.HiddenDim);
                    }
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
                    var hNew = new double[w.HiddenDim];
                    if (w.UseGlu)
                    {
                        var gate = FcSigmoid(h, w.HiddenDim, w.HiddenDim, w.StepGW[s][l], w.StepGB[s][l]);
                        for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    }
                    else
                    {
                        Array.Copy(bnH, hNew, w.HiddenDim);
                    }
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

    private static void ApplyBnRunningStatsEma(
        TabNetWeights w, double[][] batchMeans, double[][] batchVars, double momentum)
    {
        int layerCount = Math.Min(w.TotalBnLayers, Math.Min(batchMeans.Length, batchVars.Length));
        for (int b = 0; b < layerCount; b++)
        {
            int dim = Math.Min(w.BnMean[b].Length, Math.Min(batchMeans[b].Length, batchVars[b].Length));
            for (int j = 0; j < dim; j++)
            {
                double batchVar = Math.Max(0.0, batchVars[b][j]);
                w.BnMean[b][j] = momentum * w.BnMean[b][j] + (1 - momentum) * batchMeans[b][j];
                w.BnVar[b][j]  = momentum * w.BnVar[b][j]  + (1 - momentum) * batchVar;
            }
        }
    }

    private static (double[][] Means, double[][] Vars) ComputeEpochBatchStats(
        TabNetWeights w, List<TrainingSample> fitSet, int ghostBatchSize, int minCalibrationSamples, Random epochRng)
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

        if (batchN < minCalibrationSamples) return (means, vars);

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

    /// <summary>
    /// Compute ghost-batch BN stats from a specific set of mini-batch sample indices.
    /// Reuses TraverseBnLayers to avoid duplicating the forward-pass traversal logic.
    /// </summary>
    private static (double[][] Means, double[][] Vars) ComputeMiniBatchBnStats(
        TabNetWeights w, List<TrainingSample> fitSet, int count, int minCalibrationSamples, int[] sampleIndices)
    {
        var means = new double[w.TotalBnLayers][];
        var vars  = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            means[b] = new double[dim];
            vars[b]  = new double[dim];
        }

        if (count < minCalibrationSamples) return (means, vars);

        var (layerSums, layerSqSum) = TraverseBnLayers(w, fitSet, count, sampleIndices);

        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = layerSums[b].Length;
            for (int j = 0; j < dim; j++)
            {
                means[b][j] = layerSums[b][j] / count;
                vars[b][j]  = Math.Max(0.0, layerSqSum[b][j] / count - means[b][j] * means[b][j]);
            }
        }

        return (means, vars);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY / DENSITY / COVARIATE / TEMPORAL / EQUITY / SHARPE
    // ═══════════════════════════════════════════════════════════════════════

    private static TabNetDriftArtifact ComputeDriftDiagnostics(
        IReadOnlyList<TrainingSample> trainSet,
        int featureCount,
        IReadOnlyList<string> featureNames,
        double fracDiffD)
    {
        var artifact = new TabNetDriftArtifact
        {
            SampleCount = trainSet.Count,
            FeatureCount = featureCount,
            GateAction = "PASS",
        };

        if (trainSet.Count < 30 || featureCount <= 0)
            return artifact;

        int n = trainSet.Count;
        int half = Math.Max(1, n / 2);
        int segment = Math.Max(10, n / 3);
        int tailStart = Math.Max(0, n - segment);
        var flaggedFeatures = new List<(string Name, double Score)>();
        double sumLag = 0.0, maxLag = 0.0;
        double sumVar = 0.0, maxVar = 0.0;
        double sumPsi = 0.0, maxPsi = 0.0;
        double sumCp = 0.0, maxCp = 0.0;
        double sumAdf = 0.0, maxAdf = double.NegativeInfinity;
        double sumKpss = 0.0, maxKpss = 0.0;
        double sumMeanShift = 0.0, maxMeanShift = 0.0;
        int nonStationaryFeatureCount = 0;
        int severeFeatureCount = 0;

        for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
        {
            var values = new double[n];
            for (int sampleIndex = 0; sampleIndex < n; sampleIndex++)
                values[sampleIndex] = trainSet[sampleIndex].Features[featureIndex];

            double lag1 = Math.Abs(ComputeLag1Autocorrelation(values));
            double varFirst = Variance(values, 0, segment);
            double varLast = Variance(values, tailStart, n - tailStart);
            double varianceRatioDistance = Math.Abs(Math.Log((varLast + Eps) / (varFirst + Eps)));
            double psi = ComputePopulationStabilityIndex(values[..half], values[half..]);
            double changePointScore = ComputeChangePointScore(values);
            double adfLike = ComputeAdfLikeStatistic(values);
            double kpssLike = ComputeKpssLikeStatistic(values);
            double meanShift = ComputeNormalisedMeanShift(values, segment, tailStart);

            sumLag += lag1;
            sumVar += varianceRatioDistance;
            sumPsi += psi;
            sumCp += changePointScore;
            sumAdf += adfLike;
            sumKpss += kpssLike;
            sumMeanShift += meanShift;
            maxLag = Math.Max(maxLag, lag1);
            maxVar = Math.Max(maxVar, varianceRatioDistance);
            maxPsi = Math.Max(maxPsi, psi);
            maxCp = Math.Max(maxCp, changePointScore);
            maxAdf = Math.Max(maxAdf, adfLike);
            maxKpss = Math.Max(maxKpss, kpssLike);
            maxMeanShift = Math.Max(maxMeanShift, meanShift);

            bool lagSignal = lag1 > 0.985;
            bool varianceSignal = varianceRatioDistance > Math.Log(2.5);
            bool psiSignal = psi > 0.20;
            bool changePointSignal = changePointScore > 1.50;
            bool adfSignal = adfLike > -2.0;
            bool kpssSignal = kpssLike > 0.50;
            bool meanShiftSignal = meanShift > 1.0;
            int triggeredSignals =
                (lagSignal ? 1 : 0) +
                (varianceSignal ? 1 : 0) +
                (psiSignal ? 1 : 0) +
                (changePointSignal ? 1 : 0) +
                (adfSignal ? 1 : 0) +
                (kpssSignal ? 1 : 0) +
                (meanShiftSignal ? 1 : 0);
            bool severeFeature =
                (adfSignal && kpssSignal && psi > 0.25) ||
                meanShift > 1.75 ||
                changePointScore > 2.25 ||
                (varianceRatioDistance > Math.Log(3.0) && psi > 0.30);

            if (triggeredSignals >= 3 ||
                (adfSignal && (kpssSignal || psiSignal || meanShiftSignal)) ||
                severeFeature)
            {
                nonStationaryFeatureCount++;
                string name = featureIndex < featureNames.Count ? featureNames[featureIndex] : $"F{featureIndex}";
                double score =
                    0.16 * lag1 +
                    0.12 * varianceRatioDistance +
                    0.16 * psi +
                    0.14 * changePointScore +
                    0.14 * Math.Max(0.0, adfLike + 3.0) +
                    0.14 * kpssLike +
                    0.14 * meanShift;
                flaggedFeatures.Add((name, score));
                if (severeFeature)
                    severeFeatureCount++;
            }
        }

        artifact.NonStationaryFeatureCount = nonStationaryFeatureCount;
        artifact.NonStationaryFeatureFraction = featureCount > 0 ? (double)nonStationaryFeatureCount / featureCount : 0.0;
        artifact.MeanLag1Autocorrelation = featureCount > 0 ? sumLag / featureCount : 0.0;
        artifact.MaxLag1Autocorrelation = maxLag;
        artifact.MeanVarianceRatioDistance = featureCount > 0 ? sumVar / featureCount : 0.0;
        artifact.MaxVarianceRatioDistance = maxVar;
        artifact.MeanPopulationStabilityIndex = featureCount > 0 ? sumPsi / featureCount : 0.0;
        artifact.MaxPopulationStabilityIndex = maxPsi;
        artifact.MeanChangePointScore = featureCount > 0 ? sumCp / featureCount : 0.0;
        artifact.MaxChangePointScore = maxCp;
        artifact.MeanAdfLikeStatistic = featureCount > 0 ? sumAdf / featureCount : 0.0;
        artifact.MaxAdfLikeStatistic = double.IsFinite(maxAdf) ? maxAdf : 0.0;
        artifact.MeanKpssLikeStatistic = featureCount > 0 ? sumKpss / featureCount : 0.0;
        artifact.MaxKpssLikeStatistic = maxKpss;
        artifact.MeanRecentMeanShiftScore = featureCount > 0 ? sumMeanShift / featureCount : 0.0;
        artifact.MaxRecentMeanShiftScore = maxMeanShift;
        string gateAction = "PASS";
        if (artifact.NonStationaryFeatureFraction > 0.60 || severeFeatureCount > featureCount * 0.45)
            gateAction = "REJECT";
        else if (artifact.NonStationaryFeatureFraction > 0.35 || severeFeatureCount > 0)
            gateAction = "DEGRADED";
        else if (artifact.NonStationaryFeatureFraction > 0.15)
            gateAction = "WARN";

        if (fracDiffD > 0.0 && gateAction == "REJECT")
            gateAction = "DEGRADED";
        else if (fracDiffD > 0.0 && gateAction == "DEGRADED")
            gateAction = "WARN";

        artifact.GateAction = gateAction;
        artifact.GateTriggered = !string.Equals(gateAction, "PASS", StringComparison.OrdinalIgnoreCase);
        artifact.FlaggedFeatures = flaggedFeatures
            .OrderByDescending(entry => entry.Score)
            .Take(8)
            .Select(entry => entry.Name)
            .ToArray();
        return artifact;
    }

    private static double ComputeLag1Autocorrelation(double[] values)
    {
        if (values.Length < 3)
            return 0.0;

        double mean = values.Average();
        double numerator = 0.0;
        double denominator = 0.0;
        for (int i = 1; i < values.Length; i++)
            numerator += (values[i] - mean) * (values[i - 1] - mean);
        for (int i = 0; i < values.Length; i++)
        {
            double centered = values[i] - mean;
            denominator += centered * centered;
        }

        return denominator > Eps ? numerator / denominator : 0.0;
    }

    private static double ComputePopulationStabilityIndex(double[] reference, double[] observed)
    {
        if (reference.Length < 5 || observed.Length < 5)
            return 0.0;

        double[] edges =
        [
            Quantile(reference, 0.20),
            Quantile(reference, 0.40),
            Quantile(reference, 0.60),
            Quantile(reference, 0.80),
        ];
        var refCounts = new double[edges.Length + 1];
        var obsCounts = new double[edges.Length + 1];

        foreach (double value in reference)
            refCounts[FindPsiBin(value, edges)]++;
        foreach (double value in observed)
            obsCounts[FindPsiBin(value, edges)]++;

        double psi = 0.0;
        for (int i = 0; i < refCounts.Length; i++)
        {
            double expected = Math.Max(Eps, refCounts[i] / reference.Length);
            double actual = Math.Max(Eps, obsCounts[i] / observed.Length);
            psi += (actual - expected) * Math.Log(actual / expected);
        }

        return psi;
    }

    private static double ComputeAdfLikeStatistic(double[] values)
    {
        if (values.Length < 12)
            return 0.0;

        int n = values.Length - 1;
        double[] lagged = new double[n];
        double[] delta = new double[n];
        for (int i = 0; i < n; i++)
        {
            lagged[i] = values[i];
            delta[i] = values[i + 1] - values[i];
        }

        double meanX = lagged.Average();
        double meanY = delta.Average();
        double sxx = 0.0;
        double sxy = 0.0;
        for (int i = 0; i < n; i++)
        {
            double cx = lagged[i] - meanX;
            double cy = delta[i] - meanY;
            sxx += cx * cx;
            sxy += cx * cy;
        }

        if (sxx <= Eps)
            return 0.0;

        double slope = sxy / sxx;
        double intercept = meanY - slope * meanX;
        double rss = 0.0;
        for (int i = 0; i < n; i++)
        {
            double err = delta[i] - (intercept + slope * lagged[i]);
            rss += err * err;
        }

        double sigma2 = rss / Math.Max(1, n - 2);
        double stdErr = Math.Sqrt(Math.Max(Eps, sigma2 / sxx));
        return stdErr > Eps ? slope / stdErr : 0.0;
    }

    private static double ComputeKpssLikeStatistic(double[] values)
    {
        if (values.Length < 12)
            return 0.0;

        double mean = values.Average();
        double variance = Variance(values, 0, values.Length);
        if (variance <= Eps)
            return 0.0;

        double cumulative = 0.0;
        double sumSquares = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            cumulative += values[i] - mean;
            sumSquares += cumulative * cumulative;
        }

        return sumSquares / (values.Length * values.Length * variance + Eps);
    }

    private static double ComputeNormalisedMeanShift(double[] values, int segment, int tailStart)
    {
        if (values.Length < segment + 5 || segment <= 0 || tailStart >= values.Length)
            return 0.0;

        double firstMean = values.Take(segment).Average();
        double lastMean = values.Skip(tailStart).Average();
        double std = Math.Sqrt(Math.Max(Eps, Variance(values, 0, values.Length)));
        return Math.Abs(lastMean - firstMean) / std;
    }

    private static int FindPsiBin(double value, double[] edges)
    {
        for (int i = 0; i < edges.Length; i++)
            if (value <= edges[i])
                return i;
        return edges.Length;
    }

    private static double ComputeChangePointScore(double[] values)
    {
        if (values.Length < 10)
            return 0.0;

        double mean = values.Average();
        double std = Math.Sqrt(Math.Max(Eps, Variance(values, 0, values.Length)));
        double cumulative = 0.0;
        double maxDeviation = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            cumulative += values[i] - mean;
            maxDeviation = Math.Max(maxDeviation, Math.Abs(cumulative));
        }

        return maxDeviation / (std * Math.Sqrt(values.Length));
    }

    private static double[] ComputeDensityRatioWeights(IReadOnlyList<TrainingSample> trainSet, int F, int windowDays, double learningRate)
    {
        int n = trainSet.Count;
        int recentCount = Math.Min(n / 3, windowDays * 24);
        if (recentCount < 20)
            return Enumerable.Repeat(1.0, n).ToArray();

        int cutoff = n - recentCount;
        int oldCount = cutoff;
        if (oldCount < 20)
            return Enumerable.Repeat(1.0, n).ToArray();

        int oldDiagStart = Math.Max(1, (int)Math.Floor(oldCount * 0.80));
        int recentDiagStart = cutoff + Math.Max(1, (int)Math.Floor(recentCount * 0.80));
        if (oldDiagStart >= cutoff || recentDiagStart >= n)
            return Enumerable.Repeat(1.0, n).ToArray();

        var trainIndices = new List<int>(oldDiagStart + recentDiagStart - cutoff);
        for (int i = 0; i < oldDiagStart; i++) trainIndices.Add(i);
        for (int i = cutoff; i < recentDiagStart; i++) trainIndices.Add(i);

        var diagnosticsIndices = new List<int>((cutoff - oldDiagStart) + (n - recentDiagStart));
        for (int i = oldDiagStart; i < cutoff; i++) diagnosticsIndices.Add(i);
        for (int i = recentDiagStart; i < n; i++) diagnosticsIndices.Add(i);

        if (trainIndices.Count < 20 || diagnosticsIndices.Count < 10)
            return Enumerable.Repeat(1.0, n).ToArray();

        const double L2 = 1e-3;
        const double ClipMin = 0.25;
        const double ClipMax = 4.0;
        var dw = new double[F];
        double bias = 0.0;
        double bestLoss = double.PositiveInfinity;
        double bestAuc = 0.0;
        var bestDw = new double[F];
        double bestBias = 0.0;
        int staleEpochs = 0;

        for (int ep = 0; ep < 60; ep++)
        {
            double[] grad = new double[F];
            double gradBias = 0.0;
            for (int idx = 0; idx < trainIndices.Count; idx++)
            {
                int i = trainIndices[idx];
                double label = i >= cutoff ? 1.0 : 0.0;
                double z = bias;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    z += dw[j] * trainSet[i].Features[j];
                double err = Sigmoid(z) - label;
                gradBias += err;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    grad[j] += err * trainSet[i].Features[j];
            }

            double scale = 1.0 / trainIndices.Count;
            gradBias *= scale;
            bias -= learningRate * gradBias;
            bias = Math.Clamp(bias, -MaxWeightVal, MaxWeightVal);
            for (int j = 0; j < F; j++)
            {
                double g = grad[j] * scale + L2 * dw[j];
                dw[j] -= learningRate * g;
                dw[j] = Math.Clamp(dw[j], -MaxWeightVal, MaxWeightVal);
            }

            double diagLoss = 0.0;
            var diagScores = new double[diagnosticsIndices.Count];
            var diagLabels = new int[diagnosticsIndices.Count];
            for (int di = 0; di < diagnosticsIndices.Count; di++)
            {
                int i = diagnosticsIndices[di];
                double z = bias;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    z += dw[j] * trainSet[i].Features[j];
                double p = Math.Clamp(Sigmoid(z), 0.01, 0.99);
                int label = i >= cutoff ? 1 : 0;
                diagScores[di] = p;
                diagLabels[di] = label;
                diagLoss -= label * Math.Log(p) + (1.0 - label) * Math.Log(1.0 - p);
            }

            diagLoss /= diagnosticsIndices.Count;
            double diagAuc = ComputeRankingAuc(diagScores, diagLabels);
            bool improved = diagLoss + 1e-5 < bestLoss || (Math.Abs(diagLoss - bestLoss) <= 1e-5 && diagAuc > bestAuc);
            if (improved)
            {
                bestLoss = diagLoss;
                bestAuc = diagAuc;
                bestBias = bias;
                Array.Copy(dw, bestDw, F);
                staleEpochs = 0;
            }
            else if (++staleEpochs >= 6)
            {
                break;
            }
        }

        if (!(bestLoss < 0.68 && bestAuc >= 0.55))
            return Enumerable.Repeat(1.0, n).ToArray();

        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double z = bestBias;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                z += bestDw[j] * trainSet[i].Features[j];
            double p = Math.Clamp(Sigmoid(z), 0.01, 0.99);
            weights[i] = Math.Clamp(p / Math.Max(0.01, 1.0 - p), ClipMin, ClipMax);
        }

        double mean = weights.Average();
        if (mean > Eps)
        {
            for (int i = 0; i < n; i++)
                weights[i] /= mean;
        }

        return weights;
    }

    private static double ComputeRankingAuc(double[] scores, int[] labels)
    {
        if (scores.Length == 0 || scores.Length != labels.Length)
            return 0.5;

        int positives = labels.Count(label => label == 1);
        int negatives = labels.Length - positives;
        if (positives == 0 || negatives == 0)
            return 0.5;

        var ranked = scores
            .Select((score, index) => (Score: score, Label: labels[index]))
            .OrderBy(tuple => tuple.Score)
            .ToArray();

        double rankSum = 0.0;
        for (int i = 0; i < ranked.Length; i++)
        {
            if (ranked[i].Label == 1)
                rankSum += i + 1;
        }

        return (rankSum - positives * (positives + 1) / 2.0) / (positives * negatives);
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
        // Per-bar Sharpe without annualization — timeframe is unknown, so sqrt(252) would be misleading
        return (maxDD, stdRet > 1e-10 ? avgRet / stdRet : 0);
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
