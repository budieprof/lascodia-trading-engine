using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Pruned-model result ───────────────────────────────────────────────────

    private readonly record struct PrunedModelResult(
        List<GbmTree>        Stumps,
        List<double>         Alphas,
        double               PlattA,
        double               PlattB,
        double               PlattABuy,
        double               PlattBBuy,
        double               PlattASell,
        double               PlattBSell,
        double[]             IsotonicBp,
        List<TrainingSample> MaskedTrain,
        List<TrainingSample> MaskedCal,
        List<TrainingSample> MaskedTest,
        double[]             MagWeights,
        double               MagBias,
        double[]             MagQ90Weights,
        double               MagQ90Bias,
        double               DurbinWatson,
        double               AvgKellyFraction,
        double               Ece,
        double               OptimalThreshold,
        double               TemperatureScale,
        EvalMetrics          Metrics,
        double               BaseAccuracy);

    // ── Pruned-model training helper ──────────────────────────────────────────

    /// <summary>
    /// Trains a pruned AdaBoost ensemble on masked features, calibrates it, and compares
    /// against the unpruned baseline. Returns <c>null</c> if the pruned model degrades
    /// accuracy by more than 1 %, otherwise returns a <see cref="PrunedModelResult"/>
    /// containing all updated artefacts.
    /// </summary>
    private PrunedModelResult? TrainPrunedModel(
        List<TrainingSample> trainSet,
        List<TrainingSample> calSet,
        List<TrainingSample> testSet,
        bool[]               activeMask,
        List<GbmTree>        stumps,
        List<double>         alphas,
        List<GbmTree>        warmStumps,
        List<double>         warmAlphas,
        bool                 replayWarmStartWeights,
        double[]?            densityWeights,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        double               plattABuy,
        double               plattBBuy,
        double               plattASell,
        double               plattBSell,
        double               temperatureScale,
        double[]             isotonicBp,
        TrainingHyperparams  hp,
        int                  F,
        int                  effectiveK,
        double               shrinkage,
        bool                 sammeR,
        int                  treeDepth,
        double               optimalThreshold,
        int                  prunedCount,
        CancellationToken    ct)
    {
        _logger.LogInformation(
            "Feature pruning: masking {Pruned}/{Total} low-importance features; re-training.",
            prunedCount, F);

        var maskedTrain = ApplyMask(trainSet, activeMask);
        var maskedCal   = ApplyMask(calSet,   activeMask);
        var maskedTest  = ApplyMask(testSet,  activeMask);

        // ── Warm-start pruned retrain from filtered existing stumps ──────────
        var filteredPStumps = new List<GbmTree>(stumps.Count);
        var filteredPAlphas = new List<double>(stumps.Count);
        for (int i = 0; i < Math.Min(stumps.Count, alphas.Count); i++)
        {
            if (TreeUsesOnlyActiveFeatures(stumps[i], activeMask))
            {
                filteredPStumps.Add(stumps[i]);
                filteredPAlphas.Add(alphas[i]);
            }
        }
        int pResidualRounds = filteredPStumps.Count > 0
            ? Math.Max(5, effectiveK / 3)
            : effectiveK;

        int pM      = maskedTrain.Count;
        var pLabels = new int[pM];
        for (int i = 0; i < pM; i++) pLabels[i] = maskedTrain[i].Direction > 0 ? 1 : -1;

        var pSoftLabels = new double[pM];
        if (hp.UseAdaptiveLabelSmoothing)
        {
            double pMaxMag = 0.0;
            foreach (var s in maskedTrain) { double mag = Math.Abs((double)s.Magnitude); if (mag > pMaxMag) pMaxMag = mag; }
            for (int i = 0; i < pM; i++)
            {
                double eps_i = pMaxMag > 1e-9
                    ? Math.Clamp(1.0 - Math.Abs((double)maskedTrain[i].Magnitude) / pMaxMag, 0.0, 0.20)
                    : 0.0;
                pSoftLabels[i] = pLabels[i] * (1.0 - eps_i);
            }
        }
        else
        {
            for (int i = 0; i < pM; i++) pSoftLabels[i] = pLabels[i];
        }

        double[]? pDensityWeights = densityWeights is null || densityWeights.Length >= maskedTrain.Count
            ? densityWeights
            : null;
        if (densityWeights is not null && densityWeights.Length < maskedTrain.Count)
            _logger.LogWarning(
                "AdaBoost pruned retrain: densityWeights length ({DW}) < maskedTrain count ({MT}); " +
                "density blending skipped to avoid partial index misalignment.",
                densityWeights.Length, maskedTrain.Count);

        double[] pWeights = InitialiseBoostWeights(maskedTrain, hp.TemporalDecayLambda, pDensityWeights);

        if (replayWarmStartWeights)
        {
            var filteredStumps = new List<GbmTree>(warmStumps.Count);
            var filteredAlphas = new List<double>(warmStumps.Count);
            foreach (var (ws, wa) in warmStumps.Zip(warmAlphas))
            {
                if (TreeUsesOnlyActiveFeatures(ws, activeMask))
                { filteredStumps.Add(ws); filteredAlphas.Add(wa); }
            }
            if (filteredStumps.Count > 0)
                AdjustWarmStartWeights(pWeights, pLabels, maskedTrain, filteredStumps, filteredAlphas);
        }

        bool pJointD2 = treeDepth == 2 && hp.UseJointDepth2Search;
        var pStumps  = new List<GbmTree>(filteredPStumps);
        var pAlphas  = new List<double>(filteredPAlphas);
        var pSortK   = new double[pM];
        var pSortIdx = new int[pM];

        for (int round = 0; round < pResidualRounds && !ct.IsCancellationRequested; round++)
        {
            var (bFi, bThresh, bParity, bErr) =
                FindBestStump(maskedTrain, pLabels, pWeights, F, pSortK, pSortIdx, activeMask);

            if (!double.IsFinite(bErr) || bErr >= 0.5 - Eps) break;

            GbmTree pTree;
            double  pAlpha;

            if (sammeR)
            {
                pTree  = treeDepth == 2
                    ? (pJointD2
                        ? BuildJointDepth2Tree(maskedTrain, pLabels, pWeights, F, pSortK, pSortIdx, true, activeMask)
                        : BuildDepth2Tree(bFi, bThresh, maskedTrain, pLabels,
                                          pWeights, F, pSortK, pSortIdx, true, activeMask))
                    : BuildSammeRStump(bFi, bThresh, maskedTrain, pLabels, pWeights, pM);
                pAlpha = 1.0;
                pAlphas.Add(pAlpha);
                pStumps.Add(pTree);
                double wSum = 0;
                for (int i = 0; i < pM; i++)
                {
                    double hR = PredictStump(pTree, maskedTrain[i].Features);
                    pWeights[i] *= Math.Exp(-pSoftLabels[i] * hR);
                    wSum += pWeights[i];
                }
                if (wSum > 0) for (int i = 0; i < pM; i++) pWeights[i] /= wSum;
            }
            else
            {
                double cErr = Math.Max(Eps, Math.Min(1 - Eps, bErr));
                pAlpha = shrinkage * 0.5 * Math.Log((1 - cErr) / cErr);
                pTree  = treeDepth == 2
                    ? (pJointD2
                        ? BuildJointDepth2Tree(maskedTrain, pLabels, pWeights, F, pSortK, pSortIdx, false, activeMask)
                        : BuildDepth2Tree(bFi, bThresh, maskedTrain, pLabels,
                                          pWeights, F, pSortK, pSortIdx, false, activeMask))
                    : BuildStump(bFi, bThresh, bParity);
                pAlphas.Add(pAlpha);
                pStumps.Add(pTree);
                double wSum = 0;
                for (int i = 0; i < pM; i++)
                {
                    double pred = PredictStump(pTree, maskedTrain[i].Features);
                    pWeights[i] *= Math.Exp(-pAlpha * pSoftLabels[i] * pred);
                    wSum += pWeights[i];
                }
                if (wSum > 0) for (int i = 0; i < pM; i++) pWeights[i] /= wSum;
            }
        }

        // Sanitise pruned-retrain alphas
        for (int k = 0; k < pStumps.Count; k++)
            if (!double.IsFinite(pAlphas[k]) || pStumps[k].Nodes is not { Count: > 0 })
                pAlphas[k] = 0.0;

        // Calibrate the pruned model
        var (pPlattA, pPlattB)  = FitPlattScaling(maskedCal, pStumps, pAlphas);
        double pTempScale = 0.0;
        if (hp.FitTemperatureScale && maskedCal.Count >= 10)
        {
            double candidateTemperature = FitTemperatureScaling(maskedCal, pStumps, pAlphas, ct);
            double plattNll = ComputeCalibrationNll(maskedCal, pStumps, pAlphas, pPlattA, pPlattB);
            double tempNll  = ComputeCalibrationNll(maskedCal, pStumps, pAlphas, pPlattA, pPlattB,
                                                    candidateTemperature);
            if (tempNll + 1e-6 < plattNll)
                pTempScale = candidateTemperature;
        }

        var (pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell) =
            FitClassConditionalPlatt(maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale,
                DefaultConditionalRoutingThreshold);
        double[] pIsotonicBp = FitIsotonicCalibration(
            maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale,
            pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell, DefaultConditionalRoutingThreshold);
        double pOptThreshold = ComputeOptimalThreshold(
            maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell, DefaultConditionalRoutingThreshold);

        // Re-fit magnitude regressors on masked features
        var (pMagWeights, pMagBias) = FitLinearRegressor(maskedTrain, F, hp, ct);
        double pDurbinWatson = ComputeDurbinWatson(maskedTrain, pMagWeights, pMagBias, F);
        double[] pMagQ90Weights = [];
        double   pMagQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && maskedTrain.Count >= 10)
            (pMagQ90Weights, pMagQ90Bias) = FitQuantileRegressor(maskedTrain, F, hp.MagnitudeQuantileTau, ct);

        double pAvgKelly = ComputeAvgKellyFraction(
            maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell, DefaultConditionalRoutingThreshold);
        var baseSelectionMetrics = EvaluateModel(
            calSet, stumps, alphas, magWeights, magBias, plattA, plattB, temperatureScale, isotonicBp,
            optimalThreshold, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        var pSelectionMetrics = EvaluateModel(
            maskedCal, pStumps, pAlphas, pMagWeights, pMagBias, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            pOptThreshold, pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell, DefaultConditionalRoutingThreshold);
        double baseSelectionEce = ComputeEce(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy: plattABuy, plattBBuy: plattBBuy,
            plattASell: plattASell, plattBSell: plattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);
        double pSelectionEce = ComputeEce(
            maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            plattABuy: pPlattABuy, plattBBuy: pPlattBBuy,
            plattASell: pPlattASell, plattBSell: pPlattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);
        double baseSelectionBrierSkill = ComputeBrierSkillScore(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        double pSelectionBrierSkill = ComputeBrierSkillScore(
            maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell, DefaultConditionalRoutingThreshold);

        if (!IsPrunedModelAcceptable(
                baseSelectionMetrics,
                pSelectionMetrics,
                baseSelectionEce,
                pSelectionEce,
                baseSelectionBrierSkill,
                pSelectionBrierSkill))
            return null;

        double pEce = ComputeEce(maskedTest, pStumps, pAlphas, pPlattA, pPlattB, pTempScale, pIsotonicBp,
            plattABuy: pPlattABuy, plattBBuy: pPlattBBuy,
            plattASell: pPlattASell, plattBSell: pPlattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);

        return new PrunedModelResult(
            Stumps:           pStumps,
            Alphas:           pAlphas,
            PlattA:           pPlattA,
            PlattB:           pPlattB,
            PlattABuy:        pPlattABuy,
            PlattBBuy:        pPlattBBuy,
            PlattASell:       pPlattASell,
            PlattBSell:       pPlattBSell,
            IsotonicBp:       pIsotonicBp,
            MaskedTrain:      maskedTrain,
            MaskedCal:        maskedCal,
            MaskedTest:       maskedTest,
            MagWeights:       pMagWeights,
            MagBias:          pMagBias,
            MagQ90Weights:    pMagQ90Weights,
            MagQ90Bias:       pMagQ90Bias,
            DurbinWatson:     pDurbinWatson,
            AvgKellyFraction: pAvgKelly,
            Ece:              pEce,
            OptimalThreshold: pOptThreshold,
            TemperatureScale: pTempScale,
            Metrics:          pSelectionMetrics,
            BaseAccuracy:     baseSelectionMetrics.Accuracy);
    }
}
