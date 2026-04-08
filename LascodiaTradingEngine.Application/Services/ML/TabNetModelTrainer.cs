using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// TabNet trainer (Rec #389, v3). with sequential attentive feature selection across N decision steps.
/// <para>
/// Architecture per decision step:
/// <list type="number">
///   <item>Attentive Transformer: FC → BN → Sparsemax (true sparse attention with exact zeros).</item>
///   <item>Prior-scale update with configurable relaxation γ ∈ [1, 2].</item>
///   <item>Feature Transformer: shared FC→BN→GLU blocks + step-specific FC→BN→GLU blocks with √0.5 residuals.</item>
///   <item>ReLU-gated step aggregation into [hiddenDim] vector.</item>
///   <item>Final FC output head → sigmoid for binary classification.</item>
/// </list>
/// </para>
/// <para>
/// Training pipeline:
/// <list type="number">
///   <item>Optional unsupervised encoder-decoder pre-training (masked feature reconstruction).</item>
///   <item>Z-score standardisation.</item>
///   <item>Polynomial feature augmentation (top-5 pairs).</item>
///   <item>Walk-forward CV (expanding window, embargo, purging, equity-curve gate, Sharpe trend).</item>
///   <item>Final splits: 70% train | 10% cal | ~20% test with embargo.</item>
///   <item>Stationarity gate, density-ratio weights, covariate-shift weights, adaptive label smoothing.</item>
///   <item>Adam-optimised true TabNet with Ghost BN, sparsemax, GLU, cosine LR, gradient clipping, early stopping.</item>
///   <item>Weight sanitization.</item>
///   <item>Platt + class-conditional Platt + isotonic + temperature calibration.</item>
///   <item>ECE, EV-optimal threshold, Kelly fraction.</item>
///   <item>Magnitude regressor (joint or standalone) with Huber loss.</item>
///   <item>Permutation feature importance with optional pruning re-train.</item>
///   <item>Conformal prediction (split-conformal qHat) + Jackknife+ residuals.</item>
///   <item>Meta-label model, abstention gate, quantile magnitude regressor.</item>
///   <item>Decision boundary stats, Durbin-Watson, MI redundancy, BSS, PSI baselines.</item>
///   <item>Per-step attention breakdown, attention entropy, sparsity statistics.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.TabNet)]
public sealed partial class TabNetModelTrainer : IMLModelTrainer
{
    // ── Named Constants ──────────────────────────────────────────────────
    private const string ModelType    = "TABNET";
    private const string ModelVersion = "3.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;
    private const double BnEpsilon   = 1e-5;
    private const double HuberDelta  = 1.0;
    private const double MaxWeightVal = 10.0;
    private const double MaxInvStd    = 1e4;     // prevent gradient explosion from near-zero BN variance
    private const double SqrtHalfResidualScale = 0.7071067811865476; // 1/√2
    private const double Eps          = 1e-15;    // zero guard for log/division
    private const double EarlyStopMinDelta = 1e-6;
    private const double ProbClampMin = 1e-7;
    private const int    DefaultBatchSize = 32;
    private const int    MeanAttentionSampleCap = 500;
    private const int    CalibrationEpochs = 200;
    private const double CalibrationLr = 0.01;
    private const int    MinCalibrationSamples = 10;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────
    private readonly ILogger<TabNetModelTrainer> _logger;

    public TabNetModelTrainer(ILogger<TabNetModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ──────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);
    }

    // ── Core training logic (synchronous, runs on thread-pool) ──────────

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNetModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        int F       = samples[0].Features.Length;
        int nSteps  = hp.TabNetSteps > 0 ? hp.TabNetSteps : (hp.K > 0 ? hp.K : 3);
        double lr   = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        int epochs  = hp.MaxEpochs > 0 ? hp.MaxEpochs : 50;
        int hiddenDim    = hp.TabNetHiddenDim > 0 ? hp.TabNetHiddenDim : Math.Max(8, 8 * nSteps);
        int sharedLayers = hp.TabNetSharedLayers > 0 ? hp.TabNetSharedLayers : 2;
        int stepLayers   = hp.TabNetStepLayers > 0 ? hp.TabNetStepLayers : 2;
        int attentionDim = hp.TabNetAttentionDim > 0 ? hp.TabNetAttentionDim : hiddenDim;
        double gamma     = Math.Clamp(hp.TabNetRelaxationGamma > 0 ? hp.TabNetRelaxationGamma : 1.5, 1.0, 2.0);
        double sparsityCoeff = hp.TabNetSparsity > 0 ? hp.TabNetSparsity : 0.0001;
        bool useSparsemax = hp.TabNetUseSparsemax;
        double dropoutRate = hp.TabNetDropoutRate;
        double bnMomentum  = hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98;
        int ghostBatchSize = hp.TabNetGhostBatchSize > 0 ? hp.TabNetGhostBatchSize : 128;

        // ── 0. Incremental update fast-path ────────────────────────────────
        if (warmStart is not null && warmStart.Type == ModelType && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * 24);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "TabNet incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    MaxEpochs              = Math.Max(10, epochs / 3),
                    EarlyStoppingPatience  = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate           = lr / 3.0,
                    DensityRatioWindowDays = 0,
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        _logger.LogInformation(
            "TabNetModelTrainer v3: n={N} F={F} steps={S} hidden={H} shared={SL} step={StL} epochs={E} lr={LR} \u03b3={G:F2}",
            samples.Count, F, nSteps, hiddenDim, sharedLayers, stepLayers, epochs, lr, gamma);

        // ── 1. Z-score standardisation ─────────────────────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 1b. Polynomial feature augmentation ────────────────────────────
        int[] polyTopIdx = [];
        int origF = F;
        if (hp.PolyLearnerFraction > 0.0)
        {
            const int PolyTopN = 5;
            polyTopIdx = SelectPolyTopFeatureIndices(allStd, F, warmStart, PolyTopN);
            int polyPairCount = polyTopIdx.Length * (polyTopIdx.Length - 1) / 2;
            allStd = AugmentSamplesWithPoly(allStd, F, polyTopIdx);
            F += polyPairCount;
            _logger.LogInformation(
                "TabNet poly augmentation: top-{N} features \u2192 {PC} pair products, F {Old}\u2192{New}",
                polyTopIdx.Length, polyPairCount, origF, F);
        }

        // ── 1c. Optional unsupervised pre-training ─────────────────────────
        TabNetWeights? pretrainedWeights = null;
        int pretrainEpochs = hp.TabNetPretrainEpochs;
        if (pretrainEpochs > 0 && allStd.Count >= hp.MinSamples * 2)
        {
            double maskFrac = hp.TabNetPretrainMaskFraction > 0 ? hp.TabNetPretrainMaskFraction : 0.3;
            pretrainedWeights = RunUnsupervisedPretraining(
                allStd, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, lr, pretrainEpochs, maskFrac, bnMomentum, ct);
            _logger.LogInformation("TabNet pre-training complete ({Epochs} epochs, mask={Mask:P0})",
                pretrainEpochs, maskFrac);
        }

        // ── 2. Walk-forward cross-validation ───────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(
            allStd, hp, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, lr, sparsityCoeff, epochs, bnMomentum, ct);
        _logger.LogInformation(
            "TabNet Walk-forward CV \u2014 folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70% train | 10% cal | ~20% test ─────────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 3b. Stationarity gate ──────────────────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, F);
            double nonStatFraction = F > 0 ? (double)nonStatCount / F : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "TabNet stationarity gate: {NonStat}/{Total} features may have unit root. Consider enabling FracDiffD.",
                    nonStatCount, F);
        }

        // ── 3c. Density-ratio importance weights ───────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays);
            _logger.LogDebug("TabNet density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 3d. Covariate shift weights ────────────────────────────────────
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, F);
            if (densityWeights is not null)
            {
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("TabNet covariate shift weights applied from parent model (gen={Gen}).",
                warmStart!.GenerationNumber);
        }

        // ── 3e. Adaptive label smoothing ───────────────────────────────────
        double effectiveLabelSmoothing = hp.LabelSmoothing;
        if (hp.UseAdaptiveLabelSmoothing && trainSet.Count > 0)
        {
            var sortedMags = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) sortedMags[i] = Math.Abs(trainSet[i].Magnitude);
            Array.Sort(sortedMags);
            double p20Threshold = sortedMags[(int)(sortedMags.Length * 0.20)];
            int ambiguousCount = 0;
            foreach (var s in trainSet) if (Math.Abs(s.Magnitude) <= p20Threshold) ambiguousCount++;
            double ambiguousFraction = (double)ambiguousCount / trainSet.Count;
            effectiveLabelSmoothing = Math.Clamp(ambiguousFraction * 0.5, 0.01, 0.20);
            _logger.LogInformation(
                "TabNet adaptive label smoothing: \u03b5={Eps:F3} (ambiguous fraction={Frac:P1})",
                effectiveLabelSmoothing, ambiguousFraction);
        }

        // ── 4. Fit TabNet ──────────────────────────────────────────────────
        var weights = FitTabNet(
            trainSet, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, lr, sparsityCoeff, epochs, effectiveLabelSmoothing,
            warmStart, pretrainedWeights, densityWeights, hp.TemporalDecayLambda, hp.L2Lambda,
            hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
            dropoutRate, bnMomentum, ghostBatchSize, hp.TabNetWarmupEpochs, ct);

        _logger.LogInformation("TabNet fitted: steps={S} hidden={H}", nSteps, hiddenDim);

        // ── 4b. Weight sanitization ────────────────────────────────────────
        int sanitizedCount = SanitizeWeights(weights);
        if (sanitizedCount > 0)
            _logger.LogWarning("TabNet sanitized {N} non-finite weight values.", sanitizedCount);

        // ── 5. Platt calibration ───────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, weights);
        if (!double.IsFinite(plattA)) plattA = 1.0;
        if (!double.IsFinite(plattB)) plattB = 0.0;

        // ── 5b. Class-conditional Platt ────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, weights);
        if (!double.IsFinite(plattABuy)) plattABuy = 1.0;
        if (!double.IsFinite(plattBBuy)) plattBBuy = 0.0;
        if (!double.IsFinite(plattASell)) plattASell = 1.0;
        if (!double.IsFinite(plattBSell)) plattBSell = 0.0;

        // ── 5c. Kelly fraction ─────────────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, weights, plattA, plattB);

        // ── 6. Magnitude regressor ─────────────────────────────────────────
        double[] magWeights;
        double magBias;
        if (hp.MagLossWeight > 0.0 && weights.MagW.Length > 0)
        {
            magWeights = weights.MagW;
            magBias    = weights.MagB;
        }
        else
        {
            (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp);
        }

        // ── 7. Evaluation on held-out test set ─────────────────────────────
        var finalMetrics = EvaluateTabNet(testSet, weights, plattA, plattB, magWeights, magBias, F);
        _logger.LogInformation(
            "TabNet eval \u2014 acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE ─────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, weights, plattA, plattB);

        // ── 9. EV-optimal threshold ────────────────────────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, weights, plattA, plattB,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);

        // ── 10. Permutation feature importance ─────────────────────────────
        var featureImportance = testSet.Count >= MinCalibrationSamples
            ? ComputePermutationImportance(testSet, weights, plattA, plattB, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp,
                Name: idx < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[idx] : $"Poly{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "TabNet top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Cal-set importance (for warm-start transfer) ──────────────
        double[] calImportanceScores = calSet.Count >= MinCalibrationSamples
            ? ComputeCalPermutationImportance(calSet, weights, ct)
            : new double[F];

        // ── 11. Feature pruning re-train pass ──────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= MinCalibrationSamples)
        {
            _logger.LogInformation("TabNet feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, F);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);
            int maskedF     = activeMask.Count(m => m);

            int prunedEpochs = Math.Max(10, epochs / 2);
            var prunedW = FitTabNet(
                maskedTrain, maskedF, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, lr, sparsityCoeff, prunedEpochs,
                effectiveLabelSmoothing, null, null, densityWeights, hp.TemporalDecayLambda,
                hp.L2Lambda, hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
                dropoutRate, bnMomentum, ghostBatchSize, 0, ct);
            var (pA, pB) = FitPlattScaling(maskedCal, prunedW);

            double[] pmw;
            double pmb;
            if (hp.MagLossWeight > 0.0 && prunedW.MagW.Length > 0)
            { pmw = prunedW.MagW; pmb = prunedW.MagB; }
            else
            { (pmw, pmb) = FitLinearRegressor(maskedTrain, maskedF, hp); }

            var prunedMetrics = EvaluateTabNet(maskedTest, prunedW, pA, pB, pmw, pmb, maskedF);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation("TabNet pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                weights      = prunedW;
                magWeights   = pmw; magBias = pmb;
                plattA       = pA;  plattB  = pB;
                finalMetrics = prunedMetrics;
                F            = maskedF;
                trainSet     = maskedTrain;
                testSet      = maskedTest;
                calSet       = maskedCal;
                ece              = ComputeEce(maskedTest, weights, plattA, plattB);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, weights, plattA, plattB,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax);
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(maskedCal, weights);
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, weights, plattA, plattB);
            }
            else
            {
                _logger.LogInformation("TabNet pruned model rejected \u2014 keeping full model");
                prunedCount = 0;
                activeMask  = new bool[F]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
        }

        // ── 11b. Isotonic calibration, conformal threshold ─────────────────
        double[] isotonicBp = FitIsotonicCalibration(calSet, weights, plattA, plattB);
        _logger.LogInformation("TabNet isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(calSet, weights, plattA, plattB, isotonicBp, conformalAlpha);

        // ── 11c. Jackknife+ residuals ──────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(trainSet, weights);

        // ── 11d. Meta-label model ──────────────────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(calSet, weights);

        // ── 11e. Abstention gate ───────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            calSet, weights, plattA, plattB);

        // ── 11f. Quantile magnitude regressor ─────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau);
        }

        // ── 11g. Decision boundary stats ──────────────────────────────────
        var (dbMean, dbStd) = calSet.Count >= MinCalibrationSamples
            ? ComputeDecisionBoundaryStats(calSet, weights)
            : (0.0, 0.0);

        // ── 11h. Durbin-Watson on magnitude residuals ──────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning("TabNet magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2})",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 11i. MI redundancy ─────────────────────────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning("TabNet MI redundancy: {N} pairs exceed threshold", redundantPairs.Length);
        }

        // ── 11j. Temperature scaling ───────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= MinCalibrationSamples)
        {
            temperatureScale = FitTemperatureScaling(calSet, weights);
        }

        // ── 11k. Brier Skill Score ─────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, weights, plattA, plattB);
        _logger.LogInformation("TabNet BSS={BSS:F4}", brierSkillScore);

        // ── 11l. PSI baseline ──────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 12. Mean attention + per-step attention + attention entropy ─────
        var (meanAttn, perStepAttn, attnEntropy) = ComputeAttentionStats(testSet, weights);
        SanitizeArr(meanAttn);
        foreach (var row in perStepAttn) SanitizeArr(row);
        SanitizeArr(attnEntropy);

        // Log sparsity statistics
        if (meanAttn.Length > 0)
        {
            int nonZero = meanAttn.Count(a => a > 1e-6);
            _logger.LogInformation("TabNet attention sparsity: {NonZero}/{Total} features selected on average",
                nonZero, F);
        }

        // ── 12b. Sanitize all scalar doubles ───────────────────────────────
        static double Safe(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;
        ece              = Safe(ece, 1.0);
        optimalThreshold = Safe(optimalThreshold, 0.5);
        avgKellyFraction = Safe(avgKellyFraction);
        dbMean           = Safe(dbMean);
        dbStd            = Safe(dbStd);
        durbinWatson     = Safe(durbinWatson, 2.0);
        temperatureScale = Safe(temperatureScale);
        brierSkillScore  = Safe(brierSkillScore);
        effectiveLabelSmoothing = Safe(effectiveLabelSmoothing);

        // ── 13. Serialise model snapshot ───────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = nSteps,
            Weights                    = weights.AttnFcW.Length > 0
                                             ? weights.AttnFcW.Select(a => a.Length > 0 ? a[0] : []).ToArray()
                                             : [[]],
            Biases                     = [weights.OutputB],
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = DateTime.UtcNow,
            FeatureImportance          = featureImportance,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            IsotonicBreakpoints        = isotonicBp,
            ConformalQHat              = conformalQHat,
            FracDiffD                  = hp.FracDiffD,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            JackknifeResiduals         = jackknifeResiduals,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            FeatureImportanceScores    = calImportanceScores,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = dbMean,
            DecisionBoundaryStd        = dbStd,
            DurbinWatsonStatistic      = durbinWatson,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            AvgKellyFraction           = avgKellyFraction,
            RedundantFeaturePairs      = redundantPairs,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            BrierSkillScore            = brierSkillScore,
            TrainedAtUtc               = DateTime.UtcNow,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount      = sanitizedCount,
            AdaptiveLabelSmoothing     = effectiveLabelSmoothing,
            ConformalCoverage          = hp.ConformalCoverage,
            TabNetAttentionJson        = JsonSerializer.Serialize(meanAttn, JsonOpts),
            TabNetStepAttentionWeights = weights.AttnFcW.Length > 0
                ? weights.AttnFcW.Select(w => w.Length > 0 ? w[0] : []).ToArray() : null,
            FeatureSubsetIndices       = polyTopIdx.Length > 0 ? [polyTopIdx] : null,

            // ── v3 architecture weights ──────────────────────────────────
            TabNetSharedWeights        = weights.SharedW,
            TabNetSharedBiases         = weights.SharedB,
            TabNetSharedGateWeights    = weights.SharedGW,
            TabNetSharedGateBiases     = weights.SharedGB,
            TabNetStepFcWeights        = weights.StepW,
            TabNetStepFcBiases         = weights.StepB,
            TabNetStepGateWeights      = weights.StepGW,
            TabNetStepGateBiases       = weights.StepGB,
            TabNetAttentionFcWeights   = weights.AttnFcW,
            TabNetAttentionFcBiases    = weights.AttnFcB,
            TabNetBnGammas             = weights.BnGamma,
            TabNetBnBetas              = weights.BnBeta,
            TabNetBnRunningMeans       = weights.BnMean,
            TabNetBnRunningVars        = weights.BnVar,
            TabNetOutputHeadWeights    = weights.OutputW,
            TabNetOutputHeadBias       = weights.OutputB,
            TabNetRelaxationGamma      = gamma,
            TabNetHiddenDim            = hiddenDim,
            TabNetInitialBnFcW         = weights.InitialBnFcW,
            TabNetInitialBnFcB         = weights.InitialBnFcB,
            TabNetPerStepAttention     = perStepAttn,
            TabNetAttentionEntropy     = attnEntropy,
        };

        SanitizeSnapshotArrays(snapshot);

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "TabNetModelTrainer v3 complete: steps={S}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            nSteps, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
