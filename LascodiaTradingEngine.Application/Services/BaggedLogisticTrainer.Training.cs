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

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        DateTime             trainingRunUtc,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        if (folds <= 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        int embargo = hp.EmbargoBarCount;

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("Walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // Folds are independent — train them in parallel.
        // Each slot is null if the fold was skipped (insufficient data / too small test).
        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            // Purged CV: also remove samples whose feature-lookback window overlaps the test period.
            // Sample i uses candles [i .. i + LookbackWindow - 1]; overlap starts when
            // i + LookbackWindow - 1 >= testStart, i.e. i >= testStart - LookbackWindow + 1.
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("Fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                return;
            }

            var foldTrainRaw = samples[..trainEnd].ToList();

            // ── Time-series purging: remove trailing training samples whose label horizon
            //    overlaps the test fold start (in addition to the lookback-window embargo above).
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeLimit = foldTrainRaw.Count;
                // Remove samples at index i where i + PurgeHorizonBars >= testStart
                // i.e. i >= testStart - PurgeHorizonBars
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < purgeLimit)
                {
                    int purgeCount = purgeLimit - purgeFrom;
                    foldTrainRaw = foldTrainRaw[..purgeFrom];
                    if (purgeCount > 0)
                        _logger.LogDebug(
                            "Purging: removed {N} train samples overlapping test fold start.",
                            purgeCount);
                }
            }

            var foldTestRaw = samples[testStart..Math.Min(testEnd, samples.Count)];

            if (foldTrainRaw.Count < hp.MinSamples)
            {
                _logger.LogDebug("Fold {Fold} skipped after purging — insufficient training data ({N})",
                    fold, foldTrainRaw.Count);
                return;
            }
            if (foldTestRaw.Count < 20) return;

            int foldFitEnd;
            int foldHoldoutStart;
            try
            {
                (foldFitEnd, foldHoldoutStart) = ComputeWalkForwardInnerValidationBoundaries(
                    foldTrainRaw.Count,
                    embargo,
                    hp.PurgeHorizonBars,
                    MLFeatureHelper.LookbackWindow);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            var foldFitRaw = foldTrainRaw[..foldFitEnd];
            var foldHoldoutRaw = foldTrainRaw[foldHoldoutStart..];
            if (foldFitRaw.Count < hp.MinSamples || foldHoldoutRaw.Count < 20)
                return;

            var foldHoldoutPlan = ComputeHoldoutWindowPlan(foldHoldoutRaw.Count);
            var foldSelectionRaw = foldHoldoutRaw[
                foldHoldoutPlan.SelectionStart..(foldHoldoutPlan.SelectionStart + foldHoldoutPlan.SelectionCount)];
            var foldCalRaw = foldHoldoutRaw[
                foldHoldoutPlan.CalibrationStart..(foldHoldoutPlan.CalibrationStart + foldHoldoutPlan.CalibrationCount)];
            if (foldSelectionRaw.Count < 10 || foldCalRaw.Count < 10)
                return;

            var (foldMeans, foldStds) = ComputeStandardizationStats(foldFitRaw);
            var foldTrain = ApplyStandardization(foldFitRaw, foldMeans, foldStds);
            var foldSelection = ApplyStandardization(foldSelectionRaw, foldMeans, foldStds);
            var foldCal  = ApplyStandardization(foldCalRaw, foldMeans, foldStds);
            var foldTest = ApplyStandardization(foldTestRaw, foldMeans, foldStds);

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(50, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            var (w, b, subs, _, foldMtMagWeights, foldMtMagBias, foldMlpHW, foldMlpHB, foldOobTrainSet, foldOobSamplingWeights) =
                FitEnsemble(foldTrain, cvHp, featureCount, null, null, ct, forceSequential: true);
            var foldMlp = new MlpState(foldMlpHW, foldMlpHB, cvHp.MlpHiddenDim);
            int foldSanitizedCount = SanitizeLearners(w, b, foldMlp.HiddenW, foldMlp.HiddenB);
            if (foldSanitizedCount > 0)
                _logger.LogDebug(
                    "Walk-forward fold {Fold}: sanitized {N}/{K} learners before evaluation.",
                    fold, foldSanitizedCount, w.Length);
            var (mw, mb) = foldMtMagWeights is { Length: > 0 }
                ? (foldMtMagWeights, foldMtMagBias)
                : FitLinearRegressor(foldTrain, featureCount, cvHp, ct);

            var foldActiveLearnerMask = ComputeActiveLearnerMask(w, b);
            var foldMeta = FitMetaLearner(foldSelection, w, b, featureCount, subs, foldMlp);
            var foldLearnerCalAccuracies = ComputeLearnerCalAccuracies(foldSelection, w, b, featureCount, subs, foldMlp);
            for (int k = 0; k < foldLearnerCalAccuracies.Length && k < foldActiveLearnerMask.Length; k++)
                if (!foldActiveLearnerMask[k]) foldLearnerCalAccuracies[k] = 0.0;
            var foldLearnerAccuracyWeights = BuildLearnerAccuracyWeights(
                foldLearnerCalAccuracies, foldActiveLearnerMask);
            double[] foldGesWeights = MaybeRunGreedyEnsembleSelection(
                cvHp, foldSelection, w, b, featureCount, subs, foldMeta, foldActiveLearnerMask, foldMlp);

            double FoldRawProb(float[] features)
            {
                var (rawProb, _) = ComputeEnsembleProbabilityAndStd(
                    features, w, b, featureCount, subs,
                    foldMeta, foldGesWeights, foldLearnerAccuracyWeights, foldLearnerCalAccuracies,
                    foldActiveLearnerMask, foldMlp);
                return Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
            }

            var foldTrainedAtUtc = trainingRunUtc;
            double foldPlattA = 1.0;
            double foldPlattB = 0.0;
            if (foldCal.Count >= 10)
                (foldPlattA, foldPlattB) = FitPlattScaling(foldCal, FoldRawProb);

            double foldTemperatureScale = 0.0;
            if (cvHp.FitTemperatureScale && foldCal.Count >= 10)
            {
                foldTemperatureScale = FitTemperatureScaling(
                    foldCal,
                    FoldRawProb,
                    foldPlattA,
                    foldPlattB,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    [],
                    0.0,
                    foldTrainedAtUtc);
            }

            var (foldPlattABuy, foldPlattBBuy, foldPlattASell, foldPlattBSell) = FitClassConditionalPlatt(
                foldCal, FoldRawProb, foldPlattA, foldPlattB, foldTemperatureScale);

            double FoldPreIsotonicProb(float[] features)
            {
                return ApplyProductionCalibration(
                    FoldRawProb(features),
                    foldPlattA,
                    foldPlattB,
                    foldTemperatureScale,
                    foldPlattABuy,
                    foldPlattBBuy,
                    foldPlattASell,
                    foldPlattBSell,
                    [],
                    0.0,
                    foldTrainedAtUtc);
            }

            double[] foldIsotonicBp = FitIsotonicCalibrationGuarded(
                foldCal, FoldPreIsotonicProb, cvHp.MinIsotonicCalibrationSamples);

            if (cvHp.FitTemperatureScale && foldCal.Count >= 10)
            {
                double refitFoldTemperatureScale = FitTemperatureScaling(
                    foldCal,
                    FoldRawProb,
                    foldPlattA,
                    foldPlattB,
                    foldPlattABuy,
                    foldPlattBBuy,
                    foldPlattASell,
                    foldPlattBSell,
                    foldIsotonicBp,
                    cvHp.AgeDecayLambda,
                    foldTrainedAtUtc);

                if (Math.Abs(refitFoldTemperatureScale - foldTemperatureScale) > 1e-6)
                {
                    foldTemperatureScale = refitFoldTemperatureScale;
                    (foldPlattABuy, foldPlattBBuy, foldPlattASell, foldPlattBSell) = FitClassConditionalPlatt(
                        foldCal, FoldRawProb, foldPlattA, foldPlattB, foldTemperatureScale);
                    foldIsotonicBp = FitIsotonicCalibrationGuarded(
                        foldCal, FoldPreIsotonicProb, cvHp.MinIsotonicCalibrationSamples);
                }
            }

            double FoldProductionProb(float[] features)
            {
                return ApplyProductionCalibration(
                    FoldRawProb(features),
                    foldPlattA,
                    foldPlattB,
                    foldTemperatureScale,
                    foldPlattABuy,
                    foldPlattBBuy,
                    foldPlattASell,
                    foldPlattBSell,
                    foldIsotonicBp,
                    cvHp.AgeDecayLambda,
                    foldTrainedAtUtc);
            }

            double FoldProductionProbFromRaw(double rawProb)
            {
                return ApplyProductionCalibration(
                    Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7),
                    foldPlattA,
                    foldPlattB,
                    foldTemperatureScale,
                    foldPlattABuy,
                    foldPlattBBuy,
                    foldPlattASell,
                    foldPlattBSell,
                    foldIsotonicBp,
                    cvHp.AgeDecayLambda,
                    foldTrainedAtUtc);
            }

            double foldDecisionThreshold = ComputeOptimalThreshold(
                foldSelection,
                FoldProductionProb,
                cvHp.ThresholdSearchMin,
                cvHp.ThresholdSearchMax,
                cvHp.ThresholdSearchStepBps);

            if (cvHp.OobPruningEnabled && cvHp.K >= 2)
            {
                int foldOobPrunedCount = PruneByOobContribution(
                    foldOobTrainSet, w, b, foldOobSamplingWeights, featureCount, subs, cvHp.K,
                    foldMeta, foldGesWeights, foldLearnerAccuracyWeights, foldLearnerCalAccuracies,
                    foldMlp, foldActiveLearnerMask, FoldProductionProbFromRaw, foldDecisionThreshold);
                if (foldOobPrunedCount > 0)
                {
                    foldActiveLearnerMask = ComputeActiveLearnerMask(w, b);
                    foldMeta = FitMetaLearner(foldSelection, w, b, featureCount, subs, foldMlp);
                    foldLearnerCalAccuracies = ComputeLearnerCalAccuracies(foldSelection, w, b, featureCount, subs, foldMlp);
                    for (int k = 0; k < foldLearnerCalAccuracies.Length && k < foldActiveLearnerMask.Length; k++)
                        if (!foldActiveLearnerMask[k]) foldLearnerCalAccuracies[k] = 0.0;
                    foldLearnerAccuracyWeights = BuildLearnerAccuracyWeights(
                        foldLearnerCalAccuracies, foldActiveLearnerMask);
                    foldGesWeights = MaybeRunGreedyEnsembleSelection(
                        cvHp, foldSelection, w, b, featureCount, subs, foldMeta, foldActiveLearnerMask, foldMlp);

                    if (foldCal.Count >= 10)
                        (foldPlattA, foldPlattB) = FitPlattScaling(foldCal, FoldRawProb);
                    else
                        (foldPlattA, foldPlattB) = (1.0, 0.0);

                    foldTemperatureScale = 0.0;
                    if (cvHp.FitTemperatureScale && foldCal.Count >= 10)
                    {
                        foldTemperatureScale = FitTemperatureScaling(
                            foldCal,
                            FoldRawProb,
                            foldPlattA,
                            foldPlattB,
                            0.0,
                            0.0,
                            0.0,
                            0.0,
                            [],
                            0.0,
                            foldTrainedAtUtc);
                    }

                    (foldPlattABuy, foldPlattBBuy, foldPlattASell, foldPlattBSell) = FitClassConditionalPlatt(
                        foldCal, FoldRawProb, foldPlattA, foldPlattB, foldTemperatureScale);
                    foldIsotonicBp = FitIsotonicCalibrationGuarded(
                        foldCal, FoldPreIsotonicProb, cvHp.MinIsotonicCalibrationSamples);

                    if (cvHp.FitTemperatureScale && foldCal.Count >= 10)
                    {
                        double refitFoldTemperatureScale = FitTemperatureScaling(
                            foldCal,
                            FoldRawProb,
                            foldPlattA,
                            foldPlattB,
                            foldPlattABuy,
                            foldPlattBBuy,
                            foldPlattASell,
                            foldPlattBSell,
                            foldIsotonicBp,
                            cvHp.AgeDecayLambda,
                            foldTrainedAtUtc);

                        if (Math.Abs(refitFoldTemperatureScale - foldTemperatureScale) > 1e-6)
                        {
                            foldTemperatureScale = refitFoldTemperatureScale;
                            (foldPlattABuy, foldPlattBBuy, foldPlattASell, foldPlattBSell) = FitClassConditionalPlatt(
                                foldCal, FoldRawProb, foldPlattA, foldPlattB, foldTemperatureScale);
                            foldIsotonicBp = FitIsotonicCalibrationGuarded(
                                foldCal, FoldPreIsotonicProb, cvHp.MinIsotonicCalibrationSamples);
                        }
                    }

                    foldDecisionThreshold = ComputeOptimalThreshold(
                        foldSelection,
                        FoldProductionProb,
                        cvHp.ThresholdSearchMin,
                        cvHp.ThresholdSearchMax,
                        cvHp.ThresholdSearchStepBps);
                }
            }

            double[] foldSelectionImportanceScores = foldSelection.Count >= 10
                ? ComputeCalPermutationImportance(foldSelection, FoldRawProb, featureCount, ct)
                : new double[featureCount];
            int[] foldMetaLabelTopFeatureIndices = ComputeTopFeatureIndices(
                foldSelectionImportanceScores,
                5,
                featureCount);

            var (foldMetaLabelWeights, foldMetaLabelBias) = FitMetaLabelModel(
                foldCal, FoldProductionProb, w, b, featureCount, subs,
                foldMeta, foldGesWeights, foldLearnerAccuracyWeights, foldLearnerCalAccuracies,
                foldDecisionThreshold, foldActiveLearnerMask, foldMlp, foldMetaLabelTopFeatureIndices);
            double foldMetaLabelThreshold = TuneMetaLabelThreshold(
                foldSelection,
                FoldProductionProbAndStd,
                foldDecisionThreshold,
                foldMetaLabelWeights,
                foldMetaLabelBias,
                foldMetaLabelTopFeatureIndices);
            var (foldAbstentionWeights, foldAbstentionBias, foldAbstentionThreshold) = FitAbstentionModel(
                foldCal, FoldProductionProb, w, b, foldMetaLabelWeights, foldMetaLabelBias,
                featureCount, subs, foldMeta, foldGesWeights, foldLearnerAccuracyWeights,
                foldLearnerCalAccuracies, foldDecisionThreshold, foldActiveLearnerMask, foldMlp,
                foldMetaLabelTopFeatureIndices);
            var (foldAbstentionThresholdGlobal, foldAbstentionThresholdBuy, foldAbstentionThresholdSell) =
                TuneAbstentionThresholds(
                    foldSelection,
                    FoldProductionProbAndStd,
                    foldDecisionThreshold,
                    foldMetaLabelWeights,
                    foldMetaLabelBias,
                    foldMetaLabelThreshold,
                    foldMetaLabelTopFeatureIndices,
                    foldAbstentionWeights,
                    foldAbstentionBias,
                    foldAbstentionThreshold);
            foldAbstentionThreshold = foldAbstentionThresholdGlobal;

            double foldSelectiveDecisionThreshold = TuneSelectiveDecisionThreshold(
                foldSelection,
                mw,
                mb,
                FoldProductionProbAndStd,
                foldMetaLabelWeights,
                foldMetaLabelBias,
                foldMetaLabelThreshold,
                foldMetaLabelTopFeatureIndices,
                foldAbstentionWeights,
                foldAbstentionBias,
                foldAbstentionThreshold,
                foldAbstentionThresholdBuy,
                foldAbstentionThresholdSell,
                cvHp.ThresholdSearchMin,
                cvHp.ThresholdSearchMax,
                cvHp.ThresholdSearchStepBps);
            if (Math.Abs(foldSelectiveDecisionThreshold - foldDecisionThreshold) > 1e-6)
            {
                foldDecisionThreshold = foldSelectiveDecisionThreshold;
                (foldMetaLabelWeights, foldMetaLabelBias) = FitMetaLabelModel(
                    foldCal, FoldProductionProb, w, b, featureCount, subs,
                    foldMeta, foldGesWeights, foldLearnerAccuracyWeights, foldLearnerCalAccuracies,
                    foldDecisionThreshold, foldActiveLearnerMask, foldMlp, foldMetaLabelTopFeatureIndices);
                foldMetaLabelThreshold = TuneMetaLabelThreshold(
                    foldSelection,
                    FoldProductionProbAndStd,
                    foldDecisionThreshold,
                    foldMetaLabelWeights,
                    foldMetaLabelBias,
                    foldMetaLabelTopFeatureIndices);
                (foldAbstentionWeights, foldAbstentionBias, foldAbstentionThreshold) = FitAbstentionModel(
                    foldCal, FoldProductionProb, w, b, foldMetaLabelWeights, foldMetaLabelBias,
                    featureCount, subs, foldMeta, foldGesWeights, foldLearnerAccuracyWeights,
                    foldLearnerCalAccuracies, foldDecisionThreshold, foldActiveLearnerMask, foldMlp,
                    foldMetaLabelTopFeatureIndices);
                (foldAbstentionThresholdGlobal, foldAbstentionThresholdBuy, foldAbstentionThresholdSell) =
                    TuneAbstentionThresholds(
                        foldSelection,
                        FoldProductionProbAndStd,
                        foldDecisionThreshold,
                        foldMetaLabelWeights,
                        foldMetaLabelBias,
                        foldMetaLabelThreshold,
                        foldMetaLabelTopFeatureIndices,
                        foldAbstentionWeights,
                        foldAbstentionBias,
                        foldAbstentionThreshold);
                foldAbstentionThreshold = foldAbstentionThresholdGlobal;
            }

            (double Probability, double EnsembleStd) FoldProductionProbAndStd(float[] features)
            {
                var (rawProb, ensembleStd) = ComputeEnsembleProbabilityAndStd(
                    features, w, b, featureCount, subs,
                    foldMeta, foldGesWeights, foldLearnerAccuracyWeights, foldLearnerCalAccuracies,
                    foldActiveLearnerMask, foldMlp);
                return (FoldProductionProbFromRaw(rawProb), ensembleStd);
            }

            var foldEvaluation = BaggedLogisticTrainer.EvaluateSelectivePolicy(
                foldTest,
                mw,
                mb,
                FoldProductionProbAndStd,
                foldDecisionThreshold,
                foldMetaLabelWeights,
                foldMetaLabelBias,
                foldMetaLabelThreshold,
                foldMetaLabelTopFeatureIndices,
                foldAbstentionWeights,
                foldAbstentionBias,
                foldAbstentionThreshold,
                foldAbstentionThresholdBuy,
                foldAbstentionThresholdSell);
            var m = foldEvaluation.Metrics;

            // Compute per-feature mean projected |weight| for walk-forward stability scoring.
            // This keeps MLP/subsampled/poly learners comparable in raw feature space and
            // excludes sanitized inactive learners from diluting fold importances.
            var foldImp = ComputeMeanProjectedFeatureImportance(
                w, b, featureCount, subs, foldMlp.HiddenW, foldMlp.HiddenDim);

            // ── Equity-curve gate ──────────────────────────────────────────────
            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldEvaluation.Predictions);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;

            // Write to slot indexed by fold — each fold owns a unique index, no lock needed.
            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        // ── Aggregate parallel fold results (preserve fold order for Sharpe trend) ──
        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            foldImportances.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // Check equity-curve gate: bad-fold fraction exceeds MaxBadFoldFraction (default 0.5)
        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
        {
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{TotalFolds} folds failed (maxDD or Sharpe). Model rejected.",
                badFolds, accList.Count);
        }

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // Sharpe trend gate: if slope is significantly negative, treat as bad-fold majority
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Walk-forward Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // Feature stability: CV = σ/μ of mean |weight| across folds per feature
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < featureCount; j++)
            {
                // Compute mean and std with plain loops — avoids LINQ Average+Select+ToList.
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = foldImportances[fi][j] - meanImp;
                    varImp += d * d;
                }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0.0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0.0;
            }
        }

        return (new WalkForwardResult(
            AvgAccuracy:           avgAcc,
            StdAccuracy:           stdAcc,
            AvgF1:                 f1List.Average(),
            AvgEV:                 evList.Average(),
            AvgSharpe:             sharpeList.Average(),
            FoldCount:             accList.Count,
            SharpeTrend:           sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ── Ensemble fitting ──────────────────────────────────────────────────────

    /// <summary>
    /// Fits K base logistic regression learners.
    /// <list type="bullet">
    ///   <item><b>Feature subsampling</b> — each learner trains on a random subset of √F features.</item>
    ///   <item><b>Adam</b> optimizer (β₁=0.9, β₂=0.999) with cosine-annealed base LR.</item>
    ///   <item><b>Stratified biased bootstrap</b> — equal buy/sell class ratio per bag.</item>
    ///   <item><b>Label smoothing</b> — y_smooth = y(1−ε) + 0.5ε.</item>
    ///   <item><b>Warm-start</b> — initialise weights from previous snapshot when supplied.</item>
    /// </list>
    /// Returns weights, biases, and per-learner feature-subset indices (null when no subsampling).
    /// </summary>
    private (double[][] Weights, double[] Biases, int[][]? Subsets, int PolyStart,
             double[]? MtMagWeights, double MtMagBias,
             double[][]? MlpHiddenW, double[][]? MlpHiddenB,
             List<TrainingSample> OobTrainSet, double[] OobSamplingWeights) FitEnsemble(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        int                  featureCount,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool                 forceSequential = false)
    {
        var weights        = new double[hp.K][];
        var biases         = new double[hp.K];
        bool useSubsampling = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0;
        var featureSubsets  = useSubsampling ? new int[hp.K][] : null;

        // MLP hidden layer arrays (null when MlpHiddenDim == 0 → linear logistic)
        int hiddenDim       = Math.Max(0, hp.MlpHiddenDim);
        bool useMlp         = hiddenDim > 0;
        var mlpHiddenW      = useMlp ? new double[hp.K][] : null;  // [k][h * inputDim] row-major
        var mlpHiddenB      = useMlp ? new double[hp.K][] : null;  // [k][hiddenDim]

        // Polynomial learner support: last PolyLearnerFraction * K learners use augmented features
        int polyStart = hp.PolyLearnerFraction > 0
            ? (int)(hp.K * (1.0 - hp.PolyLearnerFraction))
            : hp.K;
        // Number of pairwise products for top-5 features: 5C2 = 10
        const int PolyTopN = 5;
        int polyPairCount = PolyTopN * (PolyTopN - 1) / 2; // = 10
        int polyFeatureCount = featureCount + polyPairCount;

        var (useValidationHoldout, valSize) = ComputeEnsembleValidationPlan(train.Count);
        var valSet   = useValidationHoldout ? train[^valSize..] : train;
        var trainSet = useValidationHoldout ? train[..^valSize] : train;
        int baseSeed = ResolveTrainingRandomSeed(hp.TrainingRandomSeed);

        var temporalWeights = ComputeTemporalWeights(trainSet.Count, hp.TemporalDecayLambda);
        bool useNoise       = hp.NoiseSigma > 0.0;

        // Class weights: inverse-frequency balancing to prevent majority-class collapse.
        // classWeightBuy/Sell are multiplied into the per-sample gradient so the minority
        // class receives proportionally higher loss penalty.
        double classWeightBuy  = 1.0;
        double classWeightSell = 1.0;
        if (hp.UseClassWeights)
        {
            int buyCount  = trainSet.Count(s => s.Direction > 0);
            int sellCount = trainSet.Count - buyCount;
            if (buyCount > 0 && sellCount > 0)
            {
                classWeightBuy  = (double)trainSet.Count / (2.0 * buyCount);
                classWeightSell = (double)trainSet.Count / (2.0 * sellCount);
            }
        }

        // Blend density-ratio importance weights with temporal decay weights.
        // densityWeights are computed on the full train split passed to FitEnsemble,
        // while temporalWeights are computed on the inner trainSet (after val split).
        // The inner trainSet is a prefix of the full split, so truncate densityWeights.
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= temporalWeights.Length)
        {
            var blended = new double[temporalWeights.Length];
            double sum  = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < blended.Length; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // Multi-task magnitude heads (one per learner, active when MagLossWeight > 0)
        bool useMagTask      = hp.MagLossWeight > 0.0;
        var  magWeightsK     = useMagTask ? new double[hp.K][] : null;
        var  magBiasesK      = useMagTask ? new double[hp.K]  : null;

        // Warm-start: check if prior importance scores are available for biased feature sampling
        bool useBiasedFeatureSampling =
            warmStart is not null &&
            warmStart.FeatureImportanceScores.Length == featureCount &&
            hp.FeatureSampleRatio > 0.0;

        if (useBiasedFeatureSampling)
            _logger.LogDebug("Warm-start: using prior feature importance scores for biased feature sampling.");

        // ── Determine parallelism gate ─────────────────────────────────────────
        // Learners are fully independent when sequential-coupling regularisers are
        // disabled (NCL, diversity, noise-correction all reference prior learners k'<k).
        bool learnersAreIndependent =
            hp.NclLambda              <= 0.0 &&
            hp.DiversityLambda        <= 0.0 &&
            hp.NoiseCorrectionThreshold <= 0.0;

        // Local function: train a single learner k and write into the shared arrays.
        // All captured arrays (weights, biases, featureSubsets, magWeightsK, magBiasesK)
        // are indexed by k, so no cross-slot races when running in parallel.
        void TrainLearner(int k)
        {
            if (!learnersAreIndependent) ct.ThrowIfCancellationRequested();

            // ── Feature subset for this learner ───────────────────────────────
            bool isPolyLearner = hp.PolyLearnerFraction > 0 && k >= polyStart;
            int effectiveDim   = isPolyLearner ? polyFeatureCount : featureCount;

            int[] subset;
            if (useSubsampling)
            {
                if (useBiasedFeatureSampling && !isPolyLearner)
                    subset = GenerateBiasedFeatureSubset(
                        effectiveDim, hp.FeatureSampleRatio, warmStart!.FeatureImportanceScores, seed: k * 97 + 13);
                else
                    subset = GenerateFeatureSubset(effectiveDim, hp.FeatureSampleRatio, seed: k * 97 + 13);
            }
            else
            {
                subset = Enumerable.Range(0, effectiveDim).ToArray();
            }

            if (featureSubsets is not null)
                featureSubsets[k] = subset;

            // ── Determine output-layer dimension ───────────────────────────────
            // MLP: output weights map from hiddenDim → scalar logit
            // Linear: output weights map from effectiveDim → scalar logit
            int outputDim = useMlp ? hiddenDim : effectiveDim;
            int subsetLen = subset.Length;

            // ── MLP hidden layer initialisation (Xavier) ──────────────────────
            double[]? hW = null;   // [hiddenDim × subsetLen] row-major
            double[]? hB = null;   // [hiddenDim]
            if (useMlp)
            {
                hW = new double[hiddenDim * subsetLen];
                hB = new double[hiddenDim];
                // Xavier initialisation: std = sqrt(2 / (fan_in + fan_out))
                double xavierStd = Math.Sqrt(2.0 / (subsetLen + hiddenDim));
                var initRng = new Random(baseSeed + k * 71 + 3);
                for (int i = 0; i < hW.Length; i++)
                    hW[i] = SampleGaussian(initRng, xavierStd);
            }

            // ── Warm-start: copy weights, zero non-subset features ────────────
            // Accept warm-start when the saved weight length matches the current output dimension
            // (handles both plain-feature and poly-learner dimension).
            if (warmStart is not null &&
                k < warmStart.Weights.Length &&
                warmStart.Weights[k].Length == outputDim)
            {
                weights[k] = [..warmStart.Weights[k]];
                biases[k]  = k < warmStart.Biases.Length ? warmStart.Biases[k] : 0.0;

                // Zero non-subset features so they don't pollute this learner (linear mode only)
                if (useSubsampling && !useMlp)
                {
                    var subsetSet = new HashSet<int>(subset);
                    int wLen = weights[k].Length;
                    for (int j = 0; j < wLen; j++)
                        if (!subsetSet.Contains(j)) weights[k][j] = 0.0;
                }

                // Warm-start hidden layer if available
                if (useMlp && warmStart.MlpHiddenWeights is not null &&
                    k < warmStart.MlpHiddenWeights.Length &&
                    warmStart.MlpHiddenWeights[k]?.Length == hW!.Length)
                {
                    int[]? oldSubset = warmStart.FeatureSubsetIndices is { Length: > 0 } warmStartSubsets &&
                                       k < warmStartSubsets.Length
                        ? warmStartSubsets[k]
                        : null;
                    if (!TryCopyWarmStartMlpHiddenWeights(
                            warmStart.MlpHiddenWeights[k], hW, hiddenDim, oldSubset, subset))
                    {
                        _logger.LogDebug(
                            "Skipped MLP hidden warm-start for learner {K}: subset mapping was not compatible.",
                            k);
                    }
                    if (warmStart.MlpHiddenBiases is not null &&
                        k < warmStart.MlpHiddenBiases.Length &&
                        !TryCopyWarmStartMlpHiddenBiases(warmStart.MlpHiddenBiases[k], hB!))
                    {
                        _logger.LogDebug(
                            "Skipped MLP hidden-bias warm-start for learner {K}: saved bias length {Saved} != expected {Expected}.",
                            k,
                            warmStart.MlpHiddenBiases[k].Length,
                            hB!.Length);
                    }
                }
            }
            else
            {
                weights[k] = new double[outputDim];
                biases[k]  = 0.0;
            }

            // Multi-task magnitude head for this learner
            if (useMagTask)
            {
                magWeightsK![k] = new double[effectiveDim];
                magBiasesK![k]  = 0.0;
            }

            // Stratified biased bootstrap: equal class balance per bag
            var bootstrap = StratifiedBiasedBootstrap(
                trainSet, temporalWeights, trainSet.Count, seed: baseSeed + k * 31 + 7);

            // Adam first and second moment vectors (rented from ArrayPool to reduce GC pressure
            // when K learners are trained in parallel — all K sets are alive simultaneously).
            var pool = ArrayPool<double>.Shared;
            var mW  = pool.Rent(outputDim);  Array.Clear(mW, 0, outputDim);
            var vW  = pool.Rent(outputDim);  Array.Clear(vW, 0, outputDim);
            // Hidden layer Adam moments (MLP only)
            int hWLen = hiddenDim * subsetLen;
            var mHW = useMlp ? pool.Rent(hWLen) : null;  if (mHW is not null) Array.Clear(mHW, 0, hWLen);
            var vHW = useMlp ? pool.Rent(hWLen) : null;  if (vHW is not null) Array.Clear(vHW, 0, hWLen);
            var mHB = useMlp ? pool.Rent(hiddenDim) : null;  if (mHB is not null) Array.Clear(mHB, 0, hiddenDim);
            var vHB = useMlp ? pool.Rent(hiddenDim) : null;  if (vHB is not null) Array.Clear(vHB, 0, hiddenDim);
            double mB = 0, vB = 0;
            int t = 0;
            double beta1t = 1.0; // running product: AdamBeta1^t — avoids Math.Pow per gradient step
            double beta2t = 1.0; // running product: AdamBeta2^t
            var noiseRng = useNoise ? new Random(baseSeed + k * 137 + 41) : null;

            // Adam moments for multi-task magnitude head
            var    mWmag = useMagTask ? pool.Rent(effectiveDim) : null;  if (mWmag is not null) Array.Clear(mWmag, 0, effectiveDim);
            var    vWmag = useMagTask ? pool.Rent(effectiveDim) : null;  if (vWmag is not null) Array.Clear(vWmag, 0, effectiveDim);
            double mBmag = 0, vBmag = 0;

            double bias         = biases[k];
            double bestValLoss  = double.MaxValue;
            double peakValAcc   = 0.0;
            double lrScale      = 1.0;   // adaptive LR multiplier (rec 2)
            bool   lrDecayed    = false;
            int    patience     = 0;
            double[] bestW      = [..weights[k]];
            double   bestB      = bias;
            double[]? bestHW    = useMlp ? (double[])hW!.Clone() : null;
            double[]? bestHB    = useMlp ? (double[])hB!.Clone() : null;

            // ── Soft labels + optional Mixup ──────────────────────────────────
            List<TrainingSample> trainingBootstrap;
            double[] bootstrapSoftLabels;
            if (hp.MixupAlpha > 0.0)
            {
                var mixRng        = new Random(baseSeed + k * 53 + 19 + bootstrap.Count);
                var mixedList     = new List<TrainingSample>(bootstrap.Count);
                bootstrapSoftLabels = new double[bootstrap.Count];
                for (int si = 0; si < bootstrap.Count; si++)
                {
                    int    sj  = mixRng.Next(bootstrap.Count);
                    double lam = SampleBeta(mixRng, hp.MixupAlpha);
                    var    sx  = bootstrap[si];
                    var    sy  = bootstrap[sj];
                    var    fm  = new float[sx.Features.Length];
                    for (int fi = 0; fi < sx.Features.Length; fi++)
                        fm[fi] = (float)(lam * sx.Features[fi] + (1.0 - lam) * sy.Features[fi]);
                    double lx = hp.AtrLabelSensitivity > 0.0
                        ? MLFeatureHelper.Sigmoid(sx.Magnitude * (sx.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity)
                        : (sx.Direction > 0 ? 1.0 - hp.LabelSmoothing : (double)hp.LabelSmoothing);
                    double ly = hp.AtrLabelSensitivity > 0.0
                        ? MLFeatureHelper.Sigmoid(sy.Magnitude * (sy.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity)
                        : (sy.Direction > 0 ? 1.0 - hp.LabelSmoothing : (double)hp.LabelSmoothing);
                    bootstrapSoftLabels[si] = lam * lx + (1.0 - lam) * ly;
                    mixedList.Add(new TrainingSample(fm, sx.Direction, sx.Magnitude));
                }
                trainingBootstrap = mixedList;
            }
            else if (hp.AtrLabelSensitivity > 0.0)
            {
                trainingBootstrap   = bootstrap;
                bootstrapSoftLabels = new double[bootstrap.Count];
                for (int si = 0; si < bootstrap.Count; si++)
                {
                    var s = bootstrap[si];
                    bootstrapSoftLabels[si] = MLFeatureHelper.Sigmoid(
                        s.Magnitude * (s.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity);
                }
            }
            else
            {
                trainingBootstrap   = bootstrap;
                bootstrapSoftLabels = new double[bootstrap.Count];
                double posLabel = 1.0 - hp.LabelSmoothing;
                double negLabel = hp.LabelSmoothing;
                for (int si = 0; si < bootstrap.Count; si++)
                    bootstrapSoftLabels[si] = bootstrap[si].Direction > 0 ? posLabel : negLabel;
            }

            // ── SWA state ─────────────────────────────────────────────────────
            bool     useSwa   = hp.SwaStartEpoch > 0 && hp.SwaFrequency > 0;
            double[] swaW     = useSwa ? new double[outputDim] : [];
            double   swaB     = 0.0;
            int      swaCount = 0;

            // Pre-allocated gradient buffer (for gradient norm clipping)
            var rawGrads = new double[outputDim];
            // MLP hidden activation buffer (reused per sample)
            var hiddenAct = useMlp ? new double[hiddenDim] : null;
            int batchSize = Math.Max(1, hp.MiniBatchSize);
            bool useMiniBatch = batchSize > 1;
            // Mini-batch gradient accumulators (allocated once, zeroed per batch)
            // For MLP: batchGradW accumulates output-layer gradients (indexed 0..hiddenDim-1)
            // For linear: batchGradW accumulates weight gradients (indexed by feature subset)
            var batchGradW = useMiniBatch ? new double[useMlp ? hiddenDim : effectiveDim] : null;
            double batchGradB = 0.0;
            // MLP hidden-layer batch gradient accumulators
            var batchGradHW = useMiniBatch && useMlp ? new double[hiddenDim * subsetLen] : null;
            var batchGradHB = useMiniBatch && useMlp ? new double[hiddenDim] : null;
            var batchGradMagW = useMiniBatch && useMagTask ? new double[effectiveDim] : null;
            double batchGradMagB = 0.0;

            // Per-epoch shuffle index array for mini-batch training
            // Reduces gradient correlation within batches, improving convergence.
            int[] shuffleIdx = useMiniBatch
                ? Enumerable.Range(0, trainingBootstrap.Count).ToArray()
                : [];
            var shuffleRng = useMiniBatch ? new Random(baseSeed + k * 59 + 17) : null;

            for (int epoch = 0; epoch < hp.MaxEpochs; epoch++)
            {
                ct.ThrowIfCancellationRequested();

                // Cosine-annealing base LR (scaled by adaptive decay if triggered)
                double alpha = hp.LearningRate * lrScale * 0.5 *
                    (1.0 + Math.Cos(Math.PI * epoch / hp.MaxEpochs));

                if (useMiniBatch)
                {
                    if (useMlp)
                    {
                        Array.Clear(batchGradW!, 0, hiddenDim);
                        Array.Clear(batchGradHW!, 0, hWLen);
                        Array.Clear(batchGradHB!, 0, hiddenDim);
                    }
                    else
                    {
                        Array.Clear(batchGradW!, 0, effectiveDim);
                    }
                    batchGradB = 0.0;
                    if (batchGradMagW is not null) Array.Clear(batchGradMagW, 0, effectiveDim);
                    batchGradMagB = 0.0;

                    // Fisher-Yates shuffle for this epoch
                    for (int i = shuffleIdx.Length - 1; i > 0; i--)
                    {
                        int j2 = shuffleRng!.Next(i + 1);
                        (shuffleIdx[i], shuffleIdx[j2]) = (shuffleIdx[j2], shuffleIdx[i]);
                    }
                }

                for (int si = 0; si < trainingBootstrap.Count; si++)
                {
                    // Dense cancellation check every 2000 samples to allow prompt timeout
                    if (si % 2000 == 0 && si > 0) ct.ThrowIfCancellationRequested();

                    int sampleIdx = useMiniBatch ? shuffleIdx[si] : si;
                    var sample = trainingBootstrap[sampleIdx];
                    // Only advance Adam timestep per batch boundary (or every sample if batch=1)
                    if (!useMiniBatch || si % batchSize == 0)
                    {
                        t++;
                        beta1t *= AdamBeta1;
                        beta2t *= AdamBeta2;
                    }

                    // Soft label (Mixup / AtrLabelSensitivity / hard + label smoothing)
                    double y = bootstrapSoftLabels[sampleIdx];

                    // Build augmented features for poly learners
                    float[] sampleFeatures = isPolyLearner
                        ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                        : sample.Features;

                    // Build noisy feature view (shared by linear and MLP paths)
                    // For MLP, we gather subset features into a contiguous buffer
                    double z;
                    if (useMlp)
                    {
                        // Forward: hidden = ReLU(Wh × x_subset + bh), z = Wo · hidden + bias
                        for (int h = 0; h < hiddenDim; h++)
                        {
                            double act = hB![h];
                            int rowOff = h * subsetLen;
                            for (int si2 = 0; si2 < subsetLen; si2++)
                            {
                                double fv = sampleFeatures[subset[si2]];
                                if (useNoise) fv += SampleGaussian(noiseRng!, hp.NoiseSigma);
                                act += hW![rowOff + si2] * fv;
                            }
                            hiddenAct![h] = Math.Max(0.0, act); // ReLU
                        }
                        z = bias;
                        for (int h = 0; h < hiddenDim; h++)
                            z += weights[k][h] * hiddenAct![h];
                    }
                    else
                    {
                        // Linear logistic forward pass
                        z = bias;
                        foreach (int j in subset)
                        {
                            double fv = sampleFeatures[j];
                            if (useNoise) fv += SampleGaussian(noiseRng!, hp.NoiseSigma);
                            z += weights[k][j] * fv;
                        }
                    }

                    double p = MLFeatureHelper.Sigmoid(z);
                    if (!double.IsFinite(p)) continue; // NaN/Inf guard

                    // Asymmetric loss: FpCostWeight > 0.5 → more FP penalty → higher precision
                    double errWeight = ComputeAsymmetricErrorWeight(sample.Direction, hp.FpCostWeight);
                    // Class weight: minority class gets higher gradient contribution
                    double cw = sample.Direction > 0 ? classWeightBuy : classWeightSell;
                    double err = (p - y) * errWeight * cw;

                    // Label noise correction via confident learning (from epoch 1 onward).
                    // Uses current ensemble average probability to estimate P(correct label).
                    // Samples with low P(correct) get their gradient soft-downweighted.
                    if (epoch > 0 && hp.NoiseCorrectionThreshold > 0.0 && k > 0)
                    {
                        // Compute ensemble average probability across already-fitted learners
                        double ensP = 0.0;
                        for (int kp = 0; kp < k; kp++)
                        {
                            ensP += ComputeLearnerProbability(
                                sample.Features,
                                kp,
                                weights,
                                biases,
                                featureCount,
                                featureSubsets,
                                polyStart,
                                mlpHiddenW,
                                mlpHiddenB,
                                hiddenDim);
                        }
                        ensP /= k; // average over k prior learners

                        int label = sample.Direction > 0 ? 1 : 0;
                        double noiseWeight = ComputeNoiseCorrectionWeight(ensP, label, hp.NoiseCorrectionThreshold);
                        err *= noiseWeight;
                    }

                    // ── Shared pAvg computation for NCL and diversity regularisation ──
                    double pAvg = 0.0;
                    if ((hp.NclLambda > 0.0 || hp.DiversityLambda > 0.0) && k > 0)
                    {
                        double pSum = p;
                        for (int kp = 0; kp < k; kp++)
                        {
                            pSum += ComputeLearnerProbability(
                                sample.Features,
                                kp,
                                weights,
                                biases,
                                featureCount,
                                featureSubsets,
                                polyStart,
                                mlpHiddenW,
                                mlpHiddenB,
                                hiddenDim);
                        }
                        pAvg = pSum / (k + 1);
                    }

                    // NCL: sequential negative-correlation regularisation
                    double nclGrad = hp.NclLambda > 0.0 && k > 0
                        ? hp.NclLambda * (p - pAvg) * p * (1.0 - p)
                        : 0.0;

                    // Symmetric Cross-Entropy (Wang et al. 2019): adds reverse-KL gradient
                    // d(L_RCE)/dz = |log(A)| × p(1−p) × (y==0 ? +1 : −1), A = 1e-4
                    // Saturates for confident-wrong predictions → robust to noisy timeout labels.
                    double sceGrad = hp.UseSymmetricCE && hp.SymmetricCeAlpha > 0.0
                        ? hp.SymmetricCeAlpha * 9.2103 * p * (1.0 - p) * (y < 0.5 ? 1.0 : -1.0)
                        : 0.0;

                    // Diversity regularisation: maximises (p_k − p̄)² across learners
                    // d(−λ(p−p̄)²)/dz_k = −2λ(p−p̄)·p(1−p)
                    double divGrad = hp.DiversityLambda > 0.0 && k > 0
                        ? -hp.DiversityLambda * 2.0 * (p - pAvg) * p * (1.0 - p)
                        : 0.0;

                    double totalErr = err + nclGrad + sceGrad + divGrad;

                    // ── Gradient computation + optional norm clipping ─────────
                    double bGrad = totalErr;
                    // rawGrads holds gradients for the output layer weights
                    if (useMlp)
                    {
                        // Output layer: dL/dWo[h] = totalErr * hidden[h] + L2 * Wo[h]
                        for (int h = 0; h < hiddenDim; h++)
                            rawGrads[h] = totalErr * hiddenAct![h] + hp.L2Lambda * weights[k][h];
                    }
                    else
                    {
                        foreach (int j in subset)
                            rawGrads[j] = totalErr * sampleFeatures[j] + hp.L2Lambda * weights[k][j];
                    }

                    // Magnitude head gradient (computed alongside direction gradient)
                    double magHuberGradSample = 0.0;
                    if (useMagTask)
                    {
                        double magPred = magBiasesK![k];
                        foreach (int j in subset) magPred += magWeightsK![k][j] * sampleFeatures[j];
                        double magErr = magPred - sample.Magnitude;
                        magHuberGradSample = Math.Abs(magErr) <= 1.0 ? magErr : Math.Sign(magErr);
                    }

                    if (hp.MaxGradNorm > 0.0)
                    {
                        double gnormSq = bGrad * bGrad;
                        if (useMlp)
                            for (int h = 0; h < hiddenDim; h++) gnormSq += rawGrads[h] * rawGrads[h];
                        else
                            foreach (int j in subset) gnormSq += rawGrads[j] * rawGrads[j];
                        double gnorm = Math.Sqrt(gnormSq);
                        if (gnorm > hp.MaxGradNorm)
                        {
                            double sc = hp.MaxGradNorm / gnorm;
                            bGrad *= sc;
                            if (useMlp)
                                for (int h = 0; h < hiddenDim; h++) rawGrads[h] *= sc;
                            else
                                foreach (int j in subset) rawGrads[j] *= sc;
                        }
                    }

                    // ── Mini-batch: accumulate gradients ──────────────────────
                    if (useMiniBatch)
                    {
                        if (useMlp)
                        {
                            // Output-layer gradients: rawGrads indexed 0..hiddenDim-1
                            for (int h = 0; h < hiddenDim; h++) batchGradW![h] += rawGrads[h];
                            // Hidden-layer backprop: accumulate dL/dWh and dL/dbh
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                if (hiddenAct![h] <= 0.0) continue; // ReLU gate
                                double dHidden = totalErr * weights[k][h];
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen; si2++)
                                    batchGradHW![rowOff + si2] += dHidden * sampleFeatures[subset[si2]] + hp.L2Lambda * hW![rowOff + si2];
                                batchGradHB![h] += dHidden;
                            }
                        }
                        else
                        {
                            foreach (int j in subset) batchGradW![j] += rawGrads[j];
                        }
                        batchGradB += bGrad;
                        if (useMagTask)
                        {
                            double sMag = hp.MagLossWeight * magHuberGradSample;
                            foreach (int j in subset) batchGradMagW![j] += sMag * sampleFeatures[j];
                            batchGradMagB += sMag;
                        }

                        // Apply accumulated gradients at batch boundary or end of epoch
                        bool isBatchEnd = (si + 1) % batchSize == 0 || si == trainingBootstrap.Count - 1;
                        if (!isBatchEnd) continue;

                        int actualBatch = (si % batchSize) + 1;
                        double invBatch = 1.0 / actualBatch;

                        double bc1    = 1.0 - beta1t;
                        double bc2    = 1.0 - beta2t;
                        double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                        // Direction head Adam update (averaged over batch)
                        if (useMlp)
                        {
                            // Output layer: weights indexed 0..hiddenDim-1
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double grad = batchGradW![h] * invBatch;
                                mW[h] = AdamBeta1 * mW[h] + (1 - AdamBeta1) * grad;
                                vW[h] = AdamBeta2 * vW[h] + (1 - AdamBeta2) * grad * grad;
                                weights[k][h] -= alphAt * mW[h] / (Math.Sqrt(vW[h]) + AdamEpsilon);
                            }
                            // Hidden layer backprop Adam update
                            for (int hi = 0; hi < hWLen; hi++)
                            {
                                double gH = batchGradHW![hi] * invBatch;
                                mHW![hi] = AdamBeta1 * mHW[hi] + (1 - AdamBeta1) * gH;
                                vHW![hi] = AdamBeta2 * vHW[hi] + (1 - AdamBeta2) * gH * gH;
                                hW![hi] -= alphAt * mHW[hi] / (Math.Sqrt(vHW[hi]) + AdamEpsilon);
                            }
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double gHB = batchGradHB![h] * invBatch;
                                mHB![h] = AdamBeta1 * mHB[h] + (1 - AdamBeta1) * gHB;
                                vHB![h] = AdamBeta2 * vHB[h] + (1 - AdamBeta2) * gHB * gHB;
                                hB![h] -= alphAt * mHB[h] / (Math.Sqrt(vHB[h]) + AdamEpsilon);
                            }
                        }
                        else
                        {
                            foreach (int j in subset)
                            {
                                double grad = batchGradW![j] * invBatch;
                                mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                                vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                                weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                            }
                        }
                        double bGradAvg = batchGradB * invBatch;
                        mB  = AdamBeta1 * mB + (1 - AdamBeta1) * bGradAvg;
                        vB  = AdamBeta2 * vB + (1 - AdamBeta2) * bGradAvg * bGradAvg;
                        bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                        // Magnitude head Adam update (averaged over batch)
                        if (useMagTask)
                        {
                            double magBAvg = batchGradMagB * invBatch;
                            mBmag = AdamBeta1 * mBmag + (1 - AdamBeta1) * magBAvg;
                            vBmag = AdamBeta2 * vBmag + (1 - AdamBeta2) * magBAvg * magBAvg;
                            magBiasesK![k] -= alphAt * mBmag / (Math.Sqrt(vBmag) + AdamEpsilon);
                            foreach (int j in subset)
                            {
                                double gm = batchGradMagW![j] * invBatch;
                                mWmag![j] = AdamBeta1 * mWmag[j] + (1 - AdamBeta1) * gm;
                                vWmag![j] = AdamBeta2 * vWmag[j] + (1 - AdamBeta2) * gm * gm;
                                magWeightsK![k][j] -= alphAt * mWmag[j] / (Math.Sqrt(vWmag[j]) + AdamEpsilon);
                            }
                        }

                        // Reset batch accumulators
                        if (useMlp)
                        {
                            Array.Clear(batchGradW!, 0, hiddenDim);
                            Array.Clear(batchGradHW!, 0, hWLen);
                            Array.Clear(batchGradHB!, 0, hiddenDim);
                        }
                        else
                        {
                            Array.Clear(batchGradW!, 0, effectiveDim);
                        }
                        batchGradB = 0.0;
                        if (batchGradMagW is not null) Array.Clear(batchGradMagW, 0, effectiveDim);
                        batchGradMagB = 0.0;
                    }
                    else
                    {
                        // ── Sample-by-sample Adam update (legacy path) ────────
                        double bc1    = 1.0 - beta1t;
                        double bc2    = 1.0 - beta2t;
                        double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                        if (useMlp)
                        {
                            var hiddenSignals = ComputeMlpHiddenBackpropSignals(
                                totalErr,
                                weights[k],
                                hiddenAct!,
                                hiddenDim);

                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double grad = rawGrads[h];
                                mW[h] = AdamBeta1 * mW[h] + (1 - AdamBeta1) * grad;
                                vW[h] = AdamBeta2 * vW[h] + (1 - AdamBeta2) * grad * grad;
                                weights[k][h] -= alphAt * mW[h] / (Math.Sqrt(vW[h]) + AdamEpsilon);
                            }
                            // Hidden layer backprop: dL/dhidden[h] = totalErr * Wo[h] * ReLU'(preact)
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double dHidden = hiddenSignals[h];
                                if (dHidden == 0.0) continue;
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen; si2++)
                                {
                                    double gH = dHidden * sampleFeatures[subset[si2]] + hp.L2Lambda * hW![rowOff + si2];
                                    mHW![rowOff + si2] = AdamBeta1 * mHW[rowOff + si2] + (1 - AdamBeta1) * gH;
                                    vHW![rowOff + si2] = AdamBeta2 * vHW[rowOff + si2] + (1 - AdamBeta2) * gH * gH;
                                    hW![rowOff + si2] -= alphAt * mHW[rowOff + si2] / (Math.Sqrt(vHW[rowOff + si2]) + AdamEpsilon);
                                }
                                // Hidden bias
                                mHB![h] = AdamBeta1 * mHB[h] + (1 - AdamBeta1) * dHidden;
                                vHB![h] = AdamBeta2 * vHB[h] + (1 - AdamBeta2) * dHidden * dHidden;
                                hB![h] -= alphAt * mHB[h] / (Math.Sqrt(vHB[h]) + AdamEpsilon);
                            }
                        }
                        else
                        {
                            foreach (int j in subset)
                            {
                                double grad = rawGrads[j];
                                mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                                vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                                weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                            }
                        }

                        mB  = AdamBeta1 * mB + (1 - AdamBeta1) * bGrad;
                        vB  = AdamBeta2 * vB + (1 - AdamBeta2) * bGrad * bGrad;
                        bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                        // Magnitude head
                        if (useMagTask)
                        {
                            double scaledMag = hp.MagLossWeight * magHuberGradSample;
                            mBmag = AdamBeta1 * mBmag + (1 - AdamBeta1) * scaledMag;
                            vBmag = AdamBeta2 * vBmag + (1 - AdamBeta2) * scaledMag * scaledMag;
                            magBiasesK![k] -= alphAt * mBmag / (Math.Sqrt(vBmag) + AdamEpsilon);
                            foreach (int j in subset)
                            {
                                double gm = scaledMag * sampleFeatures[j];
                                mWmag![j] = AdamBeta1 * mWmag[j] + (1 - AdamBeta1) * gm;
                                vWmag![j] = AdamBeta2 * vWmag[j] + (1 - AdamBeta2) * gm * gm;
                                magWeightsK![k][j] -= alphAt * mWmag[j] / (Math.Sqrt(vWmag[j]) + AdamEpsilon);
                            }
                        }
                    }

                    // ── L1 elastic-net proximal operator (soft thresholding) ───
                    if (hp.L1Lambda > 0.0)
                    {
                        double l1AlphAt = hp.L1Lambda * alpha;
                        for (int j2 = 0; j2 < outputDim; j2++)
                        {
                            double w = weights[k][j2];
                            weights[k][j2] = Math.Abs(w) <= l1AlphAt
                                ? 0.0
                                : w - Math.Sign(w) * l1AlphAt;
                        }
                    }

                    // ── Weight magnitude clipping ──────────────────────────────
                    if (hp.MaxWeightMagnitude > 0.0)
                    {
                        double wMax = hp.MaxWeightMagnitude;
                        for (int j2 = 0; j2 < outputDim; j2++)
                            weights[k][j2] = Math.Clamp(weights[k][j2], -wMax, wMax);
                        bias = Math.Clamp(bias, -wMax, wMax);
                        // Clip hidden layer weights too
                        if (useMlp)
                        {
                            for (int j2 = 0; j2 < hW!.Length; j2++)
                                hW[j2] = Math.Clamp(hW[j2], -wMax, wMax);
                            for (int h = 0; h < hiddenDim; h++)
                                hB![h] = Math.Clamp(hB![h], -wMax, wMax);
                        }
                    }

                    // ── NaN/Inf weight guard ───────────────────────────────────
                    // If any weight went non-finite (numerical instability), reset
                    // this learner to the best checkpoint and break the epoch loop.
                    bool hasNaN = !double.IsFinite(bias);
                    if (!hasNaN)
                    {
                        for (int j2 = 0; j2 < outputDim && !hasNaN; j2++)
                            if (!double.IsFinite(weights[k][j2])) hasNaN = true;
                    }
                    if (!hasNaN && useMlp)
                    {
                        for (int j2 = 0; j2 < hW!.Length && !hasNaN; j2++)
                            if (!double.IsFinite(hW[j2])) hasNaN = true;
                    }
                    if (hasNaN)
                    {
                        Array.Copy(bestW, weights[k], outputDim);
                        bias = bestB;
                        if (useMlp && bestHW is not null && bestHB is not null)
                        {
                            Array.Copy(bestHW, hW!, hW!.Length);
                            Array.Copy(bestHB, hB!, hB!.Length);
                        }
                        goto EndEpochLoop;
                    }
                }

                // Early stopping on validation loss (computed over subset features)
                double valLoss = ComputeLogLossSubset(valSet, weights[k], bias, subset, hp.LabelSmoothing,
                    isPolyLearner ? featureCount : -1, PolyTopN,
                    useMlp ? hW : null, useMlp ? hB : null, hiddenDim);
                // Use relative improvement threshold (0.1% of current loss or 0.001, whichever is smaller)
                // to avoid getting stuck when loss plateaus with sub-1e-6 fluctuations
                double lossThreshold = Math.Min(Math.Abs(bestValLoss) * 0.001, 0.001);
                if (valLoss < bestValLoss - lossThreshold)
                {
                    bestValLoss = valLoss;
                    bestW       = [..weights[k]];
                    bestB       = bias;
                    if (useMlp) { bestHW = (double[])hW!.Clone(); bestHB = (double[])hB!.Clone(); }
                    patience    = 0;
                }
                else if (++patience >= hp.EarlyStoppingPatience)
                {
                    // Always break on patience exhaustion — SWA will average the
                    // accumulated weights regardless. The previous logic skipped
                    // the break during the SWA phase, causing an infinite loop
                    // when validation loss plateaued.
                    break;
                }

                // ── Adaptive LR decay (rec 2) ──────────────────────────────────
                // Monitor val accuracy every 5 epochs; if it drops >5 % below peak,
                // decay the LR once by AdaptiveLrDecayFactor.
                if (!lrDecayed && hp.AdaptiveLrDecayFactor > 0.0 && epoch % 5 == 0)
                {
                    int correct = 0;
                    foreach (var sv in valSet)
                    {
                        double zv;
                        float[] valFeatures = isPolyLearner
                            ? AugmentWithPolyFeatures(sv.Features, featureCount, PolyTopN)
                            : sv.Features;
                        if (useMlp)
                        {
                            // MLP forward pass: hidden = ReLU(Wh × x_subset + bh), z = Wo · hidden + bias
                            zv = bias;
                            int hiddenUnits = GetUsableHiddenUnitCount(hiddenDim, weights[k], hB!);
                            for (int h = 0; h < hiddenUnits; h++)
                            {
                                double act = hB![h];
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen && rowOff + si2 < hW!.Length; si2++)
                                {
                                    if (TryGetFeatureValue(valFeatures, subset[si2], out double featureValue))
                                        act += hW[rowOff + si2] * featureValue;
                                }
                                double hidden = Math.Max(0.0, act); // ReLU
                                zv += weights[k][h] * hidden;
                            }
                        }
                        else
                        {
                            zv = bias;
                            foreach (int j in subset)
                                if ((uint)j < (uint)weights[k].Length && (uint)j < (uint)valFeatures.Length)
                                    zv += weights[k][j] * valFeatures[j];
                        }
                        if ((MLFeatureHelper.Sigmoid(zv) >= 0.5) == (sv.Direction == 1)) correct++;
                    }
                    double curAcc = valSet.Count > 0 ? (double)correct / valSet.Count : 0.0;
                    if (curAcc > peakValAcc) peakValAcc = curAcc;
                    else if (peakValAcc > 0.0 && curAcc < peakValAcc - 0.05)
                    {
                        lrScale  *= hp.AdaptiveLrDecayFactor;
                        lrDecayed = true;
                    }
                }

                // SWA accumulation (independent of early stopping)
                if (useSwa && epoch >= hp.SwaStartEpoch &&
                    (epoch - hp.SwaStartEpoch) % Math.Max(1, hp.SwaFrequency) == 0)
                {
                    for (int j = 0; j < outputDim; j++) swaW[j] += weights[k][j];
                    swaB += bias;
                    swaCount++;
                }

                EndEpochLoop:;
            }

            // ── SWA final: use average if it improves over early-stopped best ──
            if (useSwa && swaCount > 0 && !useMlp) // SWA disabled for MLP (hidden layer not averaged)
            {
                var swaAvgW = new double[outputDim];
                for (int j = 0; j < outputDim; j++) swaAvgW[j] = swaW[j] / swaCount;
                double swaAvgB = swaB / swaCount;
                double swaLoss = ComputeLogLossSubset(valSet, swaAvgW, swaAvgB, subset,
                    hp.LabelSmoothing, isPolyLearner ? featureCount : -1, PolyTopN,
                    useMlp ? hW : null, useMlp ? hB : null, hiddenDim);
                if (swaLoss <= bestValLoss)
                {
                    bestW = swaAvgW;
                    bestB = swaAvgB;
                }
            }

            weights[k] = bestW;
            biases[k]  = bestB;
            // Store MLP hidden layer weights (restored from best checkpoint)
            if (useMlp)
            {
                mlpHiddenW![k] = bestHW ?? hW!;
                mlpHiddenB![k] = bestHB ?? hB!;
            }

            // Return rented ArrayPool buffers
            pool.Return(mW);  pool.Return(vW);
            if (mHW is not null) pool.Return(mHW);
            if (vHW is not null) pool.Return(vHW);
            if (mHB is not null) pool.Return(mHB);
            if (vHB is not null) pool.Return(vHB);
            if (mWmag is not null) pool.Return(mWmag);
            if (vWmag is not null) pool.Return(vWmag);
        } // end TrainLearner

        // ── Dispatch: parallel when learners are independent and not inside nested Parallel.For ──
        if (learnersAreIndependent && !forceSequential)
        {
            Parallel.For(0, hp.K,
                new ParallelOptions { CancellationToken = ct },
                TrainLearner);
        }
        else
        {
            for (int k = 0; k < hp.K; k++)
            {
                ct.ThrowIfCancellationRequested();
                TrainLearner(k);
            }
        }

        // ── Ensemble weight diversity enforcement ─────────────────────────────
        // After all K learners are trained, check for redundant pairs (ρ > MaxLearnerCorrelation)
        // and re-initialise the redundant learner with a different seed, fine-tuning for 10 epochs.
        if (hp.MaxLearnerCorrelation < 1.0 && hp.K >= 2)
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool foundViolation = false;
                for (int k1 = 0; k1 < hp.K && !foundViolation; k1++)
                {
                    for (int k2 = k1 + 1; k2 < hp.K && !foundViolation; k2++)
                    {
                        var learnerProjection1 = ProjectLearnerToFeatureSpace(
                            k1, weights, featureCount, featureSubsets, mlpHiddenW, hiddenDim);
                        var learnerProjection2 = ProjectLearnerToFeatureSpace(
                            k2, weights, featureCount, featureSubsets, mlpHiddenW, hiddenDim);
                        double rho = PearsonCorrelation(learnerProjection1, learnerProjection2, featureCount);
                        if (rho > hp.MaxLearnerCorrelation)
                        {
                            foundViolation = true;
                            // Pick the higher-indexed learner as the redundant one to re-init
                            int redundant = k2;
                            int other     = k1;
                            _logger.LogDebug(
                                "Diversity enforcement: learner {K} re-init (ρ={Rho:F3} with learner {Other}).",
                                redundant, rho, other);
                            int reinitSeed = baseSeed + redundant * 37 + 13;
                            ReinitLearner(
                                redundant,
                                weights,
                                biases,
                                trainSet,
                                hp,
                                featureCount,
                                featureSubsets,
                                mlpHiddenW,
                                mlpHiddenB,
                                hiddenDim,
                                new Random(reinitSeed),
                                ct);
                        }
                    }
                }
                if (!foundViolation) break;
            }
        }

        // ── Average multi-task magnitude heads ────────────────────────────────
        double[]? avgMtMagWeights = null;
        double    avgMtMagBias    = 0.0;
        if (useMagTask && magWeightsK is { Length: > 0 })
        {
            int maxMagDim = 0;
            for (int k = 0; k < hp.K; k++)
                maxMagDim = Math.Max(maxMagDim, magWeightsK[k].Length);
            avgMtMagWeights = new double[maxMagDim];
            for (int k = 0; k < hp.K; k++)
            {
                for (int j = 0; j < maxMagDim && j < magWeightsK[k].Length; j++)
                    avgMtMagWeights[j] += magWeightsK[k][j];
                avgMtMagBias += magBiasesK![k];
            }
            for (int j = 0; j < maxMagDim; j++)
                avgMtMagWeights[j] /= hp.K;
            avgMtMagBias /= hp.K;
        }

        return (weights, biases, featureSubsets, polyStart, avgMtMagWeights, avgMtMagBias,
            mlpHiddenW, mlpHiddenB, trainSet, temporalWeights);
    }

    // ── Magnitude regressor ───────────────────────────────────────────────────

    /// <summary>
    /// Fits a Huber-loss linear regressor for ATR-normalised magnitude prediction.
    /// Uses the same Adam + cosine-annealing + early-stopping treatment as the direction
    /// learners so the magnitude head quality matches the direction head quality.
    /// Falls back to a lightweight two-pass SGD when the training set is too small to
    /// hold out a validation split (fewer than 30 samples).
    /// </summary>
    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  featureCount,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var w    = new double[featureCount];
        double b = 0.0;

        // ── Validation split for early stopping ──────────────────────────────
        // Mirror the direction learners: hold out 10 % of samples (min 5) as a
        // val set. Skip early stopping when the set is too small to split safely.
        bool   canEarlyStop = train.Count >= 30;
        int    valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var    valSet       = canEarlyStop ? train[^valSize..] : train;
        var    trainSet     = canEarlyStop ? train[..^valSize] : train;

        if (trainSet.Count == 0)   // degenerate edge case
            return (w, b);

        // ── Adam state ────────────────────────────────────────────────────────
        var    mW     = new double[featureCount];
        var    vW     = new double[featureCount];
        double mB     = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int    t      = 0;

        double bestValLoss = double.MaxValue;
        var    bestW       = new double[featureCount];
        double bestB       = 0.0;
        int    patience    = 0;

        int    epochs = hp.MaxEpochs;
        double baseLr = hp.LearningRate;
        double l2     = hp.L2Lambda;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            // Cosine-annealing LR — matches the direction learner schedule exactly
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            int regSi = 0;
            foreach (var s in trainSet)
            {
                // Dense cancellation check every 2000 samples
                if (++regSi % 2000 == 0) ct.ThrowIfCancellationRequested();

                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                // Forward pass
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;

                // Huber loss gradient: L2 region when |err| ≤ 1, L1 region otherwise.
                // More robust to outlier magnitude values than plain MSE, and matches
                // the Huber gradient already used by the multi-task magnitude head.
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);

                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                // Bias Adam step
                mB  = AdamBeta1 * mB  + (1.0 - AdamBeta1) * huberGrad;
                vB  = AdamBeta2 * vB  + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b  -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                // Weight Adam step + L2
                for (int j = 0; j < featureCount; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j]  = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j]  = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j]  -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;

            // ── Validation Huber loss ─────────────────────────────────────────
            double valLoss = 0.0;
            int    valN    = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
                valN++;
            }
            if (valN > 0) valLoss /= valN;
            else          valLoss  = double.MaxValue;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, featureCount);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= hp.EarlyStoppingPatience)
            {
                break;
            }
        }

        if (canEarlyStop)
        {
            w = bestW;
            b = bestB;
        }

        return (w, b);
    }

    // ── Stratified biased bootstrap ───────────────────────────────────────────

    private static List<TrainingSample> StratifiedBiasedBootstrap(
        List<TrainingSample> source,
        double[]             temporalWeights,
        int                  n,
        int                  seed)
    {
        var rng = new Random(seed);

        // Build pos/neg index lists with plain loops — avoids LINQ Select+Where allocation chain.
        var posIdx = new List<(TrainingSample s, int i)>();
        var negIdx = new List<(TrainingSample s, int i)>();
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Direction > 0) posIdx.Add((source[i], i));
            else                         negIdx.Add((source[i], i));
        }

        if (posIdx.Count < 5 || negIdx.Count < 5)
            return BiasedBootstrap(source, temporalWeights, n, seed);

        // Build CDF weight arrays with plain loops instead of LINQ Select+ToArray.
        var posW = new double[posIdx.Count];
        for (int i = 0; i < posIdx.Count; i++) posW[i] = temporalWeights[posIdx[i].i];
        var negW = new double[negIdx.Count];
        for (int i = 0; i < negIdx.Count; i++) negW[i] = temporalWeights[negIdx[i].i];

        double[] posCdf = BuildNormalisedCdf(posW);
        double[] negCdf = BuildNormalisedCdf(negW);

        int halfN  = n / 2;
        var result = new List<TrainingSample>(n);

        for (int i = 0; i < halfN; i++)
            result.Add(posIdx[SampleFromCdf(posCdf, rng)].s);
        for (int i = 0; i < n - halfN; i++)
            result.Add(negIdx[SampleFromCdf(negCdf, rng)].s);

        // Fisher-Yates shuffle
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (result[j], result[i]) = (result[i], result[j]);
        }

        return result;
    }

    private static List<TrainingSample> BiasedBootstrap(
        List<TrainingSample> source,
        double[]             weights,
        int                  n,
        int                  seed)
    {
        var rng    = new Random(seed);
        var result = new List<TrainingSample>(n);
        var cdf    = BuildNormalisedCdf(weights);

        for (int i = 0; i < n; i++)
            result.Add(source[SampleFromCdf(cdf, rng)]);

        return result;
    }

    private double[] MaybeRunGreedyEnsembleSelection(
        TrainingHyperparams  hp,
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        bool[]?              activeLearners = null,
        MlpState             mlp = default)
    {
        if (!hp.EnableGreedyEnsembleSelection || calSet.Count < 20)
            return [];
        if (meta.IsActive)
        {
            _logger.LogDebug("Skipping GES because stacking meta-learner is active.");
            return [];
        }

        var gesWeights = RunGreedyEnsembleSelection(
            calSet, weights, biases, featureCount, featureSubsets, activeLearners: activeLearners, mlp: mlp);
        if (gesWeights.Length > 0)
            _logger.LogDebug("GES weights: [{W}]",
                string.Join(",", gesWeights.Select(w => w.ToString("F3"))));
        return gesWeights;
    }

    // ── Box-Muller Gaussian sampler ───────────────────────────────────────────

    private static double SampleGaussian(Random rng, double sigma)
    {
        // Box-Muller transform: two uniform samples → standard normal
        double u1 = Math.Max(1e-10, rng.NextDouble());
        double u2 = rng.NextDouble();
        double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return sigma * z;
    }

    // ── Bootstrap membership set ──────────────────────────────────────────────

    /// <summary>
    /// Returns the set of unique source indices sampled by
    /// <see cref="StratifiedBiasedBootstrap"/> for learner <paramref name="seed"/>.
    /// Mirrors the bootstrap sampling logic exactly so OOB sets are consistent.
    /// </summary>
    private static HashSet<int> GenerateBootstrapInSet(
        List<TrainingSample> source,
        double[]             temporalWeights,
        int                  n,
        int                  seed)
    {
        var rng   = new Random(seed);
        // Build pos/neg index lists with plain loops — avoids LINQ Select+Where allocation chain.
        var posIdx = new List<(TrainingSample s, int i)>();
        var negIdx = new List<(TrainingSample s, int i)>();
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Direction > 0) posIdx.Add((source[i], i));
            else                         negIdx.Add((source[i], i));
        }
        var inSet = new HashSet<int>();

        if (posIdx.Count < 5 || negIdx.Count < 5)
        {
            var cdf = BuildNormalisedCdf(temporalWeights);
            for (int i = 0; i < n; i++) inSet.Add(SampleFromCdf(cdf, rng));
            return inSet;
        }

        // Build CDF weight arrays with plain loops.
        var posW = new double[posIdx.Count];
        for (int i = 0; i < posIdx.Count; i++) posW[i] = temporalWeights[posIdx[i].i];
        var negW = new double[negIdx.Count];
        for (int i = 0; i < negIdx.Count; i++) negW[i] = temporalWeights[negIdx[i].i];

        double[] posCdf = BuildNormalisedCdf(posW);
        double[] negCdf = BuildNormalisedCdf(negW);

        int halfN = n / 2;
        for (int i = 0; i < halfN;     i++) inSet.Add(posIdx[SampleFromCdf(posCdf, rng)].i);
        for (int i = 0; i < n - halfN; i++) inSet.Add(negIdx[SampleFromCdf(negCdf, rng)].i);
        return inSet;
    }

    // ── OOB accuracy estimation ───────────────────────────────────────────────

    /// <summary>
    /// Estimates out-of-bag accuracy by averaging predictions from learners that
    /// did not include each training sample in their bootstrap (~37 % of samples per learner).
    /// </summary>
    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             featureSubsets,
        int                  K,
        MetaLearner          meta = default,
        double[]?            gesWeights = null,
        double[]?            learnerAccuracyWeights = null,
        double[]?            learnerCalAccuracies = null,
        Func<double, double>? probabilityTransform = null,
        double               decisionThreshold = 0.5,
        bool[]?              activeLearners = null,
        MlpState             mlp = default)
    {
        if (trainSet.Count < 20) return 0.0;

        var inSets = new HashSet<int>[K];
        for (int k = 0; k < K; k++)
            inSets[k] = GenerateBootstrapInSet(
                trainSet, temporalWeights, trainSet.Count, seed: k * 31 + 7);

        int oobCorrect = 0, oobTotal = 0;
        var availableLearners = new List<int>(K);

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Use GetLearnerProbs to handle both linear and MLP forward passes
            var lp = GetLearnerProbs(trainSet[i].Features, weights, biases, featureCount, featureSubsets,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

            availableLearners.Clear();

            for (int k = 0; k < K; k++)
            {
                if (activeLearners is not null && (k >= activeLearners.Length || !activeLearners[k]))
                    continue;
                if (inSets[k].Contains(i)) continue;
                availableLearners.Add(k);
            }

            if (availableLearners.Count == 0) continue;

            double oobProb = AggregateSelectedLearnerProbs(
                lp, availableLearners, meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies);
            if (probabilityTransform is not null)
                oobProb = probabilityTransform(oobProb);
            if (IsPredictionCorrect(oobProb, trainSet[i].Direction, decisionThreshold)) oobCorrect++;
            oobTotal++;
        }

        return oobTotal > 0 ? (double)oobCorrect / oobTotal : 0.0;
    }

    // ── Polynomial feature augmentation ───────────────────────────────────────

    /// <summary>
    /// Augments the feature array with pairwise products of the top <paramref name="topN"/> features.
    /// Returns a new array of length <paramref name="baseFeatureCount"/> + topN*(topN-1)/2.
    /// </summary>
    private static float[] AugmentWithPolyFeatures(float[] features, int baseFeatureCount, int topN)
    {
        int actualTop = Math.Min(topN, baseFeatureCount);
        int pairCount = actualTop * (actualTop - 1) / 2;
        var result    = new float[baseFeatureCount + pairCount];

        // Copy base features
        Array.Copy(features, result, baseFeatureCount);

        // Append pairwise products
        int idx = baseFeatureCount;
        for (int i = 0; i < actualTop; i++)
            for (int j = i + 1; j < actualTop; j++)
                result[idx++] = features[i] * features[j];

        return result;
    }

    // ── Meta-label secondary classifier ───────────────────────────────────────

    /// <summary>
    /// Trains a simple logistic regression on meta-features: [ensP, ensStd, raw features 0..4].
    /// Labels: 1 if the ensemble prediction was correct for that calibration sample, else 0.
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        Func<float[], double> calibratedProb,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        double[]?            gesWeights = null,
        double[]?            learnerAccuracyWeights = null,
        double[]?            learnerCalAccuracies = null,
        double               decisionThreshold = 0.5,
        bool[]?              activeLearners = null,
        MlpState             mlp = default,
        int[]?               topFeatureIndices = null)
    {
        const int MetaFeatureDim = 7; // ensP + ensStd + 5 raw features
        const int Epochs         = 30;
        const double Lr          = 0.01;
        const double L2          = 0.001;

        if (calSet.Count < 10)
            return ([], 0.0);

        int K     = weights.Length;
        var mw    = new double[MetaFeatureDim];
        double mb = 0.0;

        // Hoist allocations: dW reused each epoch, metaF reused each sample.
        var dW    = new double[MetaFeatureDim];
        var metaF = new double[MetaFeatureDim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, MetaFeatureDim);

            foreach (var s in calSet)
            {
                double calibP = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
                var (_, ensStd) = ComputeEnsembleProbabilityAndStd(
                    s.Features, weights, biases, featureCount, subsets,
                    meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies, activeLearners, mlp);

                // Build meta-features: [calibP, ensStd, feat[0..4]] — reuse pre-allocated array.
                metaF[0] = calibP;
                metaF[1] = ensStd;
                CopySelectedFeatureWindow(metaF, s.Features, destinationOffset: 2, topFeatureIndices, maxRawFeatures: 5);

                // Label: 1 if ensemble prediction was correct
                int predicted = calibP >= decisionThreshold ? 1 : -1;
                int actual    = s.Direction > 0 ? 1 : -1;
                double label  = predicted == actual ? 1.0 : 0.0;

                // Forward pass
                double z = mb;
                for (int i = 0; i < MetaFeatureDim; i++) z += mw[i] * metaF[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - label;

                for (int i = 0; i < MetaFeatureDim; i++) dW[i] += err * metaF[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < MetaFeatureDim; i++)
                mw[i] -= Lr * (dW[i] / n + L2 * mw[i]);
            mb -= Lr * dB / n;
        }

        return (mw, mb);
    }

    // ── Diversity enforcement: re-initialise a redundant learner ─────────────

    /// <summary>
    /// Re-initialises learner <paramref name="k"/> weights with a fresh random seed and
    /// fine-tunes for 10 epochs with Adam on the full (non-bootstrap) training set.
    /// The training set is expected to already be standardised (same transform as in <see cref="FitEnsemble"/>).
    /// </summary>
    private static void ReinitLearner(
        int                  k,
        double[][]           weights,
        double[]             biases,
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  featureCount,
        int[][]?             featureSubsets,
        double[][]?          mlpHiddenW,
        double[][]?          mlpHiddenB,
        int                  hiddenDim,
        Random               rng,
        CancellationToken    ct)
    {
        const int FineTuneEpochs = 10;
        const int PolyTopN = 5;

        bool isPolyLearner = hp.PolyLearnerFraction > 0 && k >= (int)(hp.K * (1.0 - hp.PolyLearnerFraction));
        int polyPairCount   = PolyTopN * (PolyTopN - 1) / 2;
        int effectiveDim    = isPolyLearner ? featureCount + polyPairCount : featureCount;
        int[] subset = featureSubsets is not null &&
                       k < featureSubsets.Length &&
                       featureSubsets[k] is { Length: > 0 } storedSubset
            ? storedSubset
            : [..Enumerable.Range(0, effectiveDim)];
        int subsetLen = subset.Length;
        bool useMlp = hiddenDim > 0 &&
                      mlpHiddenW is not null &&
                      mlpHiddenB is not null &&
                      k < mlpHiddenW.Length &&
                      k < mlpHiddenB.Length;

        // Re-initialise the learner while preserving its inference representation.
        weights[k] = new double[useMlp ? hiddenDim : effectiveDim];
        biases[k]  = 0.0;
        if (useMlp)
        {
            mlpHiddenW![k] = new double[hiddenDim * subsetLen];
            mlpHiddenB![k] = new double[hiddenDim];
            double xavierStd = Math.Sqrt(2.0 / (subsetLen + hiddenDim));
            for (int i = 0; i < mlpHiddenW[k].Length; i++)
                mlpHiddenW[k][i] = SampleGaussian(rng, xavierStd);
        }

        // Adam moments
        var mW  = new double[weights[k].Length];
        var vW  = new double[weights[k].Length];
        double mB = 0, vB = 0;
        int t = 0;
        double beta1t = 1.0; // running product: AdamBeta1^t — avoids Math.Pow per step
        double beta2t = 1.0; // running product: AdamBeta2^t

        double bias = 0.0;
        double[]? hiddenAct = useMlp ? new double[hiddenDim] : null;
        var mHW = useMlp ? new double[hiddenDim * subsetLen] : null;
        var vHW = useMlp ? new double[hiddenDim * subsetLen] : null;
        var mHB = useMlp ? new double[hiddenDim] : null;
        var vHB = useMlp ? new double[hiddenDim] : null;

        for (int epoch = 0; epoch < FineTuneEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double alpha = hp.LearningRate * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / FineTuneEpochs));

            foreach (var sample in trainSet)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;
                double y = sample.Direction > 0 ? 1.0 - hp.LabelSmoothing : hp.LabelSmoothing;

                float[] sampleFeatures = isPolyLearner
                    ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                    : sample.Features;

                double z;
                if (useMlp)
                {
                    z = bias;
                    var hiddenWeights = mlpHiddenW![k];
                    var hiddenBiases = mlpHiddenB![k];
                    for (int h = 0; h < hiddenDim; h++)
                    {
                        double act = hiddenBiases[h];
                        int rowOffset = h * subsetLen;
                        for (int si = 0; si < subsetLen && rowOffset + si < hiddenWeights.Length; si++)
                        {
                            if (TryGetFeatureValue(sampleFeatures, subset[si], out double featureValue))
                                act += hiddenWeights[rowOffset + si] * featureValue;
                        }
                        hiddenAct![h] = Math.Max(0.0, act);
                        z += weights[k][h] * hiddenAct[h];
                    }
                }
                else
                {
                    z = bias;
                    foreach (int j in subset)
                        if ((uint)j < (uint)weights[k].Length && (uint)j < (uint)sampleFeatures.Length)
                            z += weights[k][j] * sampleFeatures[j];
                }

                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - y;

                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                if (useMlp)
                {
                    var hiddenSignals = ComputeMlpHiddenBackpropSignals(
                        err,
                        weights[k],
                        hiddenAct!,
                        hiddenDim);

                    for (int h = 0; h < hiddenDim; h++)
                    {
                        double grad = err * hiddenAct![h] + hp.L2Lambda * weights[k][h];
                        mW[h] = AdamBeta1 * mW[h] + (1 - AdamBeta1) * grad;
                        vW[h] = AdamBeta2 * vW[h] + (1 - AdamBeta2) * grad * grad;
                        weights[k][h] -= alphAt * mW[h] / (Math.Sqrt(vW[h]) + AdamEpsilon);
                    }

                    for (int h = 0; h < hiddenDim; h++)
                    {
                        double dHidden = hiddenSignals[h];
                        if (dHidden == 0.0) continue;
                        int rowOffset = h * subsetLen;
                        for (int si = 0; si < subsetLen; si++)
                        {
                            int hiddenIndex = rowOffset + si;
                            if (hiddenIndex >= mlpHiddenW![k].Length)
                                break;
                            if (!TryGetFeatureValue(sampleFeatures, subset[si], out double featureValue))
                                continue;
                            double grad = dHidden * featureValue + hp.L2Lambda * mlpHiddenW[k][hiddenIndex];
                            mHW![hiddenIndex] = AdamBeta1 * mHW[hiddenIndex] + (1 - AdamBeta1) * grad;
                            vHW![hiddenIndex] = AdamBeta2 * vHW[hiddenIndex] + (1 - AdamBeta2) * grad * grad;
                            mlpHiddenW[k][hiddenIndex] -= alphAt * mHW[hiddenIndex] / (Math.Sqrt(vHW[hiddenIndex]) + AdamEpsilon);
                        }

                        mHB![h] = AdamBeta1 * mHB[h] + (1 - AdamBeta1) * dHidden;
                        vHB![h] = AdamBeta2 * vHB[h] + (1 - AdamBeta2) * dHidden * dHidden;
                        mlpHiddenB![k][h] -= alphAt * mHB[h] / (Math.Sqrt(vHB[h]) + AdamEpsilon);
                    }
                }
                else
                {
                    foreach (int j in subset)
                    {
                        if ((uint)j >= (uint)weights[k].Length || (uint)j >= (uint)sampleFeatures.Length)
                            continue;
                        double grad = err * sampleFeatures[j] + hp.L2Lambda * weights[k][j];
                        mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                        vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                        weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                    }
                }

                mB  = AdamBeta1 * mB + (1 - AdamBeta1) * err;
                vB  = AdamBeta2 * vB + (1 - AdamBeta2) * err * err;
                bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);
            }
        }

        biases[k] = bias;
    }

    // ── Greedy Ensemble Selection (Caruana et al. 2004) ───────────────────────

    /// <summary>
    /// Greedily selects a subset/weighting of learners by minimising log-loss on
    /// the calibration set. Returns normalised usage frequencies (sum = 1) for
    /// all K learners, or an empty array when the cal set is too small.
    /// </summary>
    internal static double[] RunGreedyEnsembleSelection(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        int                  rounds = 100,
        bool[]?              activeLearners = null,
        MlpState             mlp    = default)
    {
        int K = weights.Length;
        if (calSet.Count < 10 || K < 2) return [];

        // Pre-compute per-learner probabilities on cal set: [sample][learner] — plain loop.
        int gesN  = calSet.Count;
        var allLP = new double[gesN][];
        for (int i = 0; i < gesN; i++)
            allLP[i] = GetLearnerProbs(calSet[i].Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

        var counts   = new int[K];
        var ensProbs = new double[calSet.Count]; // running ensemble average
        int ensSize  = 0;

        for (int round = 0; round < rounds; round++)
        {
            int    bestK    = -1;
            double bestLoss = double.MaxValue;

            for (int k = 0; k < K; k++)
            {
                if (activeLearners is not null && (k >= activeLearners.Length || !activeLearners[k]))
                    continue;

                double loss = 0.0;
                int    n1   = ensSize + 1;
                for (int i = 0; i < gesN; i++)
                {
                    double avg = (ensProbs[i] * ensSize + allLP[i][k]) / n1;
                    double y   = calSet[i].Direction > 0 ? 1.0 : 0.0;
                    loss -= y * Math.Log(avg + 1e-15) + (1 - y) * Math.Log(1 - avg + 1e-15);
                }
                if (loss < bestLoss) { bestLoss = loss; bestK = k; }
            }

            if (bestK < 0) break;
            for (int i = 0; i < gesN; i++)
                ensProbs[i] = (ensProbs[i] * ensSize + allLP[i][bestK]) / (ensSize + 1);
            counts[bestK]++;
            ensSize++;
        }

        double totalCount = counts.Sum();
        if (totalCount <= 0) return [];
        var result = new double[K];
        for (int k = 0; k < K; k++) result[k] = counts[k] / totalCount;
        return result;
    }

    // ── Density-ratio covariate reweighting ───────────────────────────────────

    /// <summary>
    /// Trains a logistic discriminator to distinguish "recent" samples (label=1) from
    /// "historical" samples (label=0), using sample index as a temporal proxy.
    /// The last <c>min(recentDays, 20%)</c> samples are treated as "recent".
    /// Returns importance weights w_i = p_i / (1 − p_i) normalised to sum to 1,
    /// which are blended with temporal decay weights to focus bootstrap sampling on
    /// samples from the current distribution.
    /// </summary>
    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  featureCount,
        int                  recentWindowDays,
        int                  barsPerDay)
    {
        int n = trainSet.Count;
        if (n < 50) { var uniform = new double[n]; Array.Fill(uniform, 1.0 / n); return uniform; }

        // Proxy: treat last 20 % (capped at recentWindowDays candle equivalents) as "recent"
        int recentCount = ComputeDensityRatioRecentCount(n, recentWindowDays, barsPerDay);
        int histCount   = n - recentCount;

        // Simple logistic discriminator: fit 30 epochs of SGD
        var dw  = new double[featureCount];
        double db = 0.0;
        const double lr = 0.01;
        const double l2 = 0.01;

        var rng = new Random(77);
        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < n; i++)
            {
                double y = i >= histCount ? 1.0 : 0.0;
                double z = db;
                for (int j = 0; j < featureCount; j++) z += dw[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - y;
                for (int j = 0; j < featureCount; j++)
                    dw[j] -= lr * (err * trainSet[i].Features[j] + l2 * dw[j]);
                db -= lr * err;
            }
        }

        // Compute importance weights p/(1-p), clip, normalise
        var weights = new double[n];
        double sum  = 0.0;
        for (int i = 0; i < n; i++)
        {
            double z = db;
            for (int j = 0; j < featureCount; j++) z += dw[j] * trainSet[i].Features[j];
            double p = MLFeatureHelper.Sigmoid(z);
            // Clip ratio to [0.01, 10] for numerical stability
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i] = ratio;
            sum += ratio;
        }
        for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ── Quantile magnitude regressor (pinball loss) ───────────────────────────

    /// <summary>
    /// Fits a linear quantile regressor using the pinball (check) loss:
    ///   L(r) = τ × r   if r ≥ 0
    ///         (τ − 1) × r  if r &lt; 0
    /// where r = y − ŷ.
    /// Returns regression weights and bias for the τ-th conditional quantile of magnitude.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train,
        int                  featureCount,
        double               tau)
    {
        var w    = new double[featureCount];
        double b = 0.0;
        const double lr = 0.005;
        const double l2 = 1e-4;
        const int    passes = 5;

        for (int pass = 0; pass < passes; pass++)
        {
            foreach (var s in train)
            {
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double r    = s.Magnitude - pred;
                // Subgradient of pinball loss
                double grad = r >= 0 ? -tau : -(tau - 1.0);
                for (int j = 0; j < featureCount; j++)
                    w[j] -= lr * (grad * s.Features[j] + l2 * w[j]);
                b -= lr * grad;
            }
        }

        return (w, b);
    }

    // ── Abstention gate (selective prediction) ────────────────────────────────

    /// <summary>
    /// Trains a 3-feature logistic gate on [calibP, ensStd, metaLabelScore].
    /// Label: 1 if the ensemble prediction was correct for that calibration sample.
    /// Returns (weights, bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        Func<float[], double> calibratedProb,
        double[][]           weights,
        double[]             biases,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        double[]?            gesWeights = null,
        double[]?            learnerAccuracyWeights = null,
        double[]?            learnerCalAccuracies = null,
        double               decisionThreshold = 0.5,
        bool[]?              activeLearners = null,
        MlpState             mlp = default,
        int[]?               metaLabelTopFeatureIndices = null)
    {
        const int    Dim    = 3;   // [calibP, ensStd, metaLabelScore]
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return ([], 0.0, 0.5);

        int    K  = weights.Length;
        var    aw = new double[Dim];
        double ab = 0.0;

        // Hoist allocations out of loops to avoid per-epoch/per-sample heap pressure.
        var dW = new double[Dim];
        var af = new double[Dim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, Dim);

            foreach (var s in calSet)
            {
                double calibP = Math.Clamp(calibratedProb(s.Features), 0.0, 1.0);
                var (_, ensStd) = ComputeEnsembleProbabilityAndStd(
                    s.Features, weights, biases, featureCount, subsets,
                    meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies, activeLearners, mlp);

                decimal? metaScoreDecimal = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
                    calibP,
                    ensStd,
                    s.Features,
                    s.Features.Length,
                    metaLabelWeights,
                    metaLabelBias,
                    metaLabelTopFeatureIndices);
                double metaScore = metaScoreDecimal.HasValue
                    ? (double)metaScoreDecimal.Value
                    : 0.5;

                // Reuse pre-allocated af array instead of creating new double[Dim] each sample.
                af[0] = calibP; af[1] = ensStd; af[2] = metaScore;
                double lbl = IsPredictionCorrect(calibP, s.Direction, decisionThreshold) ? 1.0 : 0.0;

                double z   = ab;
                for (int i = 0; i < Dim; i++) z += aw[i] * af[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - lbl;

                for (int i = 0; i < Dim; i++) dW[i] += err * af[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < Dim; i++)
                aw[i] -= Lr * (dW[i] / n + L2 * aw[i]);
            ab -= Lr * dB / n;
        }

        return (aw, ab, 0.5);
    }

    // ── OOB-contribution ensemble pruning (Round 6) ───────────────────────────

    /// <summary>
    /// For each learner k, measures the marginal OOB accuracy contribution:
    /// ensemble accuracy with k  vs ensemble accuracy without k.
    /// Sets weights[k] to zero and increments <paramref name="prunedCount"/> for every
    /// learner whose removal improves accuracy.
    /// </summary>
    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             subsets,
        int                  K,
        MetaLearner          meta = default,
        double[]?            gesWeights = null,
        double[]?            learnerAccuracyWeights = null,
        double[]?            learnerCalAccuracies = null,
        MlpState             mlp = default,
        bool[]?              initialActiveLearners = null,
        Func<double, double>? probabilityTransform = null,
        double               decisionThreshold = 0.5)
    {
        if (trainSet.Count < 20 || K < 2) return 0;

        int prunedCount = 0;
        var activeLearners = new bool[K];
        if (initialActiveLearners is { Length: > 0 })
        {
            for (int k = 0; k < K; k++)
                activeLearners[k] = k < initialActiveLearners.Length && initialActiveLearners[k];
        }
        else
        {
            Array.Fill(activeLearners, true);
        }
        double baseAcc = ComputeOobAccuracy(
            trainSet, weights, biases, temporalWeights, featureCount, subsets, K,
            meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies,
            probabilityTransform, decisionThreshold, activeLearners, mlp);

        for (int k = 0; k < K; k++)
        {
            if (!activeLearners[k])
                continue;

            activeLearners[k] = false;
            var  savedW = weights[k];
            var  savedB = biases[k];
            weights[k]  = new double[savedW.Length];
            biases[k]   = 0.0;

            double accWithout = ComputeOobAccuracy(
                trainSet, weights, biases, temporalWeights, featureCount, subsets, K,
                meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies,
                probabilityTransform, decisionThreshold, activeLearners, mlp);

            if (accWithout > baseAcc)
            {
                // Removing learner k improved accuracy — keep it pruned
                prunedCount++;
                baseAcc = accWithout;
                // Leave weights[k] as zeros
            }
            else
            {
                // Restore
                activeLearners[k] = true;
                weights[k] = savedW;
                biases[k]  = savedB;
            }
        }

        return prunedCount;
    }

    // ── Covariate shift weight integration (Round 8) ──────────────────────────

    /// <summary>
    /// Computes per-sample novelty scores using the parent model's feature quantile
    /// breakpoints. Each sample's weight = 1 + fraction_of_features_outside_[q10,q90].
    /// Normalised to mean = 1.0 so the effective gradient scale is unchanged.
    /// </summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> samples,
        double[][]           parentQuantileBreakpoints,
        int                  featureCount,
        bool[]?              parentActiveFeatureMask)
    {
        int n = samples.Count;
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            float[] feat = samples[i].Features;
            int outsideCount = 0;
            int checkedCount = 0;
            for (int j = 0; j < featureCount; j++)
            {
                if (j >= parentQuantileBreakpoints.Length) continue;
                if (parentActiveFeatureMask is { Length: > 0 } mask &&
                    j < mask.Length &&
                    !mask[j])
                {
                    continue;
                }
                var bp = parentQuantileBreakpoints[j];
                if (bp.Length < 2) continue;
                double q10 = bp[0];
                double q90 = bp[bp.Length - 1];
                if ((double)feat[j] < q10 || (double)feat[j] > q90) outsideCount++;
                checkedCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0.0;
            weights[i] = 1.0 + noveltyFraction; // range [1, 2]
        }

        // Normalise to mean = 1.0
        double mean = weights.Average();
        if (mean > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }
}
