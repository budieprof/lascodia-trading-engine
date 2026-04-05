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
    //  Walk-forward cross-validation
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  hiddenSize,
        CancellationToken    ct,
        double               sharpeAnnualisationFactor = DefaultSharpeAnnualisationFactor)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int K       = Math.Max(1, hp.K);

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("ELM walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        int cvInnerParallelism = Math.Max(1, Environment.ProcessorCount / Math.Max(1, folds));
        Parallel.For(0, folds, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(folds, Environment.ProcessorCount))
        }, fold =>
        {
            ct.ThrowIfCancellationRequested();

            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("ELM CV fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                return;
            }

            var fullFoldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < fullFoldTrain.Count)
                    fullFoldTrain = fullFoldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            // Carve a mini-cal set from the tail of the fold-train window BEFORE fitting
            // the ensemble, so base learners never see the cal samples via bootstrap.
            int cvCalSize = fullFoldTrain.Count / 7; // ~14 % of fold-train as mini cal
            List<TrainingSample> foldTrain;
            List<TrainingSample>? cvCalSet = null;
            if (cvCalSize >= 20)
            {
                int calStart = fullFoldTrain.Count - cvCalSize;
                int cvGap = Math.Max(embargo, hp.PurgeHorizonBars) + purgeExtra;
                int foldTrainEnd = Math.Max(0, calStart - cvGap);
                if (foldTrainEnd >= hp.MinSamples)
                {
                    cvCalSet  = fullFoldTrain[calStart..];
                    foldTrain = fullFoldTrain[..foldTrainEnd];
                }
                else
                {
                    foldTrain = fullFoldTrain;
                }
            }
            else
            {
                foldTrain = fullFoldTrain;
            }

            if (foldTrain.Count < hp.MinSamples) return;

            var cvLabelSmoothing = hp.LabelSmoothing;
            var (w, b, iw, ib, subs, lhs, cvla, _, _) = FitBaggedElm(
                foldTrain, hp, featureCount, hiddenSize, Math.Max(1, K / 2),
                cvLabelSmoothing, null, null, ct,
                maxInnerParallelism: cvInnerParallelism);
            var (mw, mb, maw, mab) = FitElmMagnitudeRegressor(
                foldTrain, featureCount, hiddenSize, iw, ib, subs, cvla,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience, embargo);

            // Fit a lightweight Platt calibration on the held-out mini-cal set so
            // that CV fold evaluation uses calibrated probabilities, matching the full
            // training pipeline. Using raw probabilities (plattA=1, plattB=0) overstates
            // Brier/EV metrics on folds where the ensemble is poorly calibrated.
            double cvPlattA = 1.0, cvPlattB = 0.0;
            double cvTemp = 0.0;
            double cvPlattABuy = 0.0, cvPlattBBuy = 0.0, cvPlattASell = 0.0, cvPlattBSell = 0.0;
            double[]? cvLearnerAccWeights = null;
            if (cvCalSet is not null)
            {
                (_, cvLearnerAccWeights) = ComputeLearnerCalibrationStats(
                    cvCalSet, w, b, iw, ib, featureCount, subs, lhs, cvla);
                (cvPlattA, cvPlattB) = ElmCalibrationHelper.FitPlattScalingCV(
                    cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                    (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                        f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
                if (hp.FitTemperatureScale && cvCalSet.Count >= 10)
                {
                    cvTemp = ElmCalibrationHelper.FitTemperatureScaling(
                        cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                        (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                            f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
                }
                (cvPlattABuy, cvPlattBBuy, cvPlattASell, cvPlattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                    cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                    cvPlattA, cvPlattB, cvTemp,
                    (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                        f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
            }

            double CvPrimaryCalibProb(float[] features) => ApplyProductionCalibration(
                EnsembleRawProb(features, w, b, iw, ib, featureCount, hiddenSize, subs, cvLearnerAccWeights, lhs, cvla),
                cvPlattA, cvPlattB, cvTemp, cvPlattABuy, cvPlattBBuy, cvPlattASell, cvPlattBSell);

            var m = ElmEvaluationHelper.EvaluateEnsemble(foldTest, w, b, iw, ib, mw, mb, cvPlattA, cvPlattB, featureCount, hiddenSize, subs,
                maw, mab, sharpeAnnualisationFactor,
                (f, ww, bb, iww, ibb, pA, pB, fc, hs, fs, lw) => CvPrimaryCalibProb(f),
                (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, cvla));

            var foldImp = new double[featureCount];
            for (int ki = 0; ki < w.Length; ki++)
            {
                int[] sub = subs is not null && ki < subs.Length ? subs[ki]
                    : Enumerable.Range(0, featureCount).ToArray();
                int subLen = sub.Length;
                for (int h = 0; h < Math.Min(w[ki].Length, lhs[ki]); h++)
                {
                    double outMag = Math.Abs(w[ki][h]);
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen; si++)
                    {
                        int fi = sub[si];
                        if (fi < featureCount && rowOff + si < iw[ki].Length)
                            foldImp[fi] += outMag * Math.Abs(iw[ki][rowOff + si]);
                    }
                }
            }
            double kCount = w.Length;
            for (int j = 0; j < featureCount; j++) foldImp[j] /= kCount;

            var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double calibP = CvPrimaryCalibProb(foldTest[pi].Features);
                foldPredictions[pi] = (calibP >= 0.5 ? 1 : -1,
                                       foldTest[pi].Direction > 0 ? 1 : -1);
            }

            var (foldMaxDD, foldCurveSharpe) = ElmMathHelper.ComputeEquityCurveStats(foldPredictions, sharpeAnnualisationFactor);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImps   = new List<double[]>(folds);
        int badFolds   = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            foldImps.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        for (int fi = 0; fi < foldResults.Length; fi++)
        {
            var r = foldResults[fi];
            if (r is null) continue;
            _logger.LogDebug(
                "ELM CV fold {Fold}/{Total}: acc={Acc:P1} f1={F1:F3} ev={EV:F4} sharpe={Sharpe:F2}{Bad}",
                fi + 1, folds, r.Value.Acc, r.Value.F1, r.Value.EV, r.Value.Sharpe,
                r.Value.IsBad ? " [FAILED]" : "");
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "ELM equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = ElmMathHelper.StdDev(accList, avgAcc);
        double sharpeTrend = ElmMathHelper.ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "ELM Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        double[]? featureStabilityScores = null;
        if (foldImps.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImps.Count;
            for (int j = 0; j < featureCount; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImps[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = foldImps[fi][j] - meanImp;
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
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ridge lambda auto-selection
    // ═══════════════════════════════════════════════════════════════════════════

    private static double SelectRidgeLambda(
        List<TrainingSample> trainSet,
        int featureCount, int hiddenSize, double labelSmoothing,
        TrainingHyperparams hp, CancellationToken ct)
    {
        double[] candidates = [1e-6, 3e-6, 1e-5, 3e-5, 1e-4, 3e-4, 1e-3, 3e-3, 1e-2, 3e-2, 1e-1, 3e-1];
        double bestLambda = 1e-3;
        double bestAvgAcc = -1;

        int embargo = hp.EmbargoBarCount;
        int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
        const int cvFolds = 5;
        int foldSize = trainSet.Count / (cvFolds + 1);
        if (foldSize < 10) return bestLambda;

        // Use multiple random projections so the selected lambda generalises
        // across diverse hidden layers, not just a single lucky projection.
        // When mixed activations are enabled, rotate activations across probes
        // to match FitBaggedElm's per-learner activation diversity.
        int nProbes   = Math.Max(1, Math.Min(Math.Max(1, hp.K), 3));
        int subsetLen = featureCount;
        double scale  = Math.Sqrt(2.0 / (subsetLen + hiddenSize));

        var availableActivations = new[] { ElmActivation.Sigmoid, ElmActivation.Tanh, ElmActivation.Relu };
        var probeWIn = new double[nProbes][];
        var probeBIn = new double[nProbes][];
        var probeActivations = new ElmActivation[nProbes];
        for (int pi = 0; pi < nProbes; pi++)
        {
            var probeRng = new Random(ElmMathHelper.HashSeed(hp.ElmOuterSeed, pi, 777));
            probeWIn[pi] = new double[hiddenSize * subsetLen];
            probeBIn[pi] = new double[hiddenSize];
            for (int i = 0; i < probeWIn[pi].Length; i++) probeWIn[pi][i] = ElmMathHelper.SampleGaussian(probeRng) * scale;
            for (int h = 0; h < hiddenSize; h++) probeBIn[pi][h] = ElmMathHelper.SampleGaussian(probeRng) * scale;
            probeActivations[pi] = hp.ElmMixActivations
                ? availableActivations[pi % availableActivations.Length]
                : hp.ElmActivation;
        }

        int solveSize = hiddenSize + 1;
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;

        foreach (double lambda in candidates)
        {
            ct.ThrowIfCancellationRequested();
            double totalAccSum = 0;
            int    totalValid  = 0;

            for (int pi = 0; pi < nProbes; pi++)
            {
                double[] wIn = probeWIn[pi];
                double[] bIn = probeBIn[pi];
                ElmActivation probeAct = probeActivations[pi];

                for (int fold = 0; fold < cvFolds; fold++)
                {
                    int valEnd = (fold + 2) * foldSize;
                    int valStart = valEnd - foldSize;
                    int trainEndIdx = Math.Max(0, valStart - embargo - purgeExtra);
                    if (trainEndIdx < 20) continue;
                    int actualValEnd = Math.Min(valEnd, trainSet.Count);
                    int actualValStart = Math.Min(valStart, actualValEnd);
                    if (actualValEnd - actualValStart < 5) continue;

                    double[,] HtH = new double[solveSize, solveSize];
                    double[] HtY = new double[solveSize];
                    double[] hRow = new double[solveSize];

                    for (int t = 0; t < trainEndIdx; t++)
                    {
                        var features = trainSet[t].Features;
                        for (int h = 0; h < hiddenSize; h++)
                        {
                            double z = bIn[h];
                            int rowOff = h * subsetLen;
                            for (int si = 0; si < subsetLen; si++)
                                if (si < features.Length) z += wIn[rowOff + si] * features[si];
                            hRow[h] = ElmMathHelper.Activate(z, probeAct);
                        }
                        hRow[hiddenSize] = 1.0;
                        double yt = trainSet[t].Direction > 0 ? posLabel : negLabel;
                        for (int ri = 0; ri < solveSize; ri++)
                        {
                            HtY[ri] += hRow[ri] * yt;
                            for (int j = ri; j < solveSize; j++)
                                HtH[ri, j] += hRow[ri] * hRow[j];
                        }
                    }

                    for (int ri = 0; ri < solveSize; ri++)
                    {
                        if (ri < hiddenSize) HtH[ri, ri] += lambda;
                        for (int j = ri + 1; j < solveSize; j++)
                            HtH[j, ri] = HtH[ri, j];
                    }

                    double[] wSolve = new double[solveSize];
                    if (!ElmMathHelper.CholeskySolve(HtH, HtY, wSolve, solveSize))
                        continue;

                    int correct = 0, valCount = 0;
                    for (int vi = actualValStart; vi < actualValEnd; vi++)
                    {
                        var s = trainSet[vi];
                        double score = wSolve[hiddenSize];
                        for (int h = 0; h < hiddenSize; h++)
                        {
                            double z = bIn[h];
                            int rowOff = h * subsetLen;
                            for (int si = 0; si < subsetLen; si++)
                                if (si < s.Features.Length) z += wIn[rowOff + si] * s.Features[si];
                            score += wSolve[h] * ElmMathHelper.Activate(z, probeAct);
                        }
                        int pred = MLFeatureHelper.Sigmoid(score) >= 0.5 ? 1 : 0;
                        if (pred == ToBinaryLabel(s.Direction)) correct++;
                        valCount++;
                    }

                    if (valCount > 0)
                    {
                        totalAccSum += (double)correct / valCount;
                        totalValid++;
                    }
                }
            }

            if (totalValid == 0) continue;
            double avgAcc = totalAccSum / totalValid;
            if (avgAcc > bestAvgAcc) { bestAvgAcc = avgAcc; bestLambda = lambda; }
        }
        return bestLambda;
    }
}
