using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Domain-Adversarial Neural Network (DANN) trainer (Rec #500).
/// Production-grade pure-C# implementation following BaggedLogistic / TabNet / FtTransformer
/// hardening standards.
/// <para>
/// Architecture:
/// <list type="number">
///   <item>Feature extractor F → <see cref="featDim"/> (Linear + ReLU).</item>
///   <item>Label classifier <see cref="featDim"/> → 1 (Linear + Sigmoid) — source domain only during training.</item>
///   <item>Domain classifier <see cref="featDim"/> → <see cref="domHid"/> → 1 (Linear + ReLU + Linear + Sigmoid)
///         connected via a Gradient Reversal Layer (GRL) so the feature extractor learns domain-invariant
///         representations while the domain classifier learns to distinguish source from target.</item>
/// </list>
/// </para>
/// <para>
/// Training pipeline:
/// <list type="number">
///   <item>Z-score standardisation over all samples.</item>
///   <item>Incremental update fast-path for regime adaptation.</item>
///   <item>Walk-forward cross-validation (expanding window, embargo + purging) with equity-curve gating.</item>
///   <item>Final splits: 70% train | 10% Platt calibration | ~20% hold-out test (embargo-gapped).</item>
///   <item>Stationarity gate (soft ADF / variance-ratio proxy).</item>
///   <item>Density-ratio importance weights (recent-distribution focus).</item>
///   <item>Covariate-shift weights from parent model quantile breakpoints.</item>
///   <item>Adaptive label smoothing from magnitude-ambiguity proxy.</item>
///   <item>Warm-start from prior DANN snapshot (all weight matrices).</item>
///   <item>Mini-batch Adam (β₁=0.9, β₂=0.999) with cosine-annealing LR, gradient norm clipping,
///         per-parameter weight-magnitude clipping, and best-model early stopping.</item>
///   <item>Progressive GRL λ-annealing: λ(p) = 2/(1+e^{−10p})−1, p ∈ [0,1].</item>
///   <item>Temporal decay + density sample weighting inside the training loop.</item>
///   <item>Post-training NaN/Inf weight sanitisation.</item>
///   <item>Magnitude linear regressor (Adam + Huber loss + Durbin-Watson check).</item>
///   <item>Platt scaling (A, B) + class-conditional Platt on calibration fold.</item>
///   <item>Isotonic calibration (PAVA) post-Platt.</item>
///   <item>Average Kelly fraction for position-size guidance.</item>
///   <item>ECE (Expected Calibration Error) on held-out test set.</item>
///   <item>EV-optimal decision threshold swept on calibration set.</item>
///   <item>Permutation feature importance (multi-round, test set).</item>
///   <item>Feature pruning re-train pass.</item>
///   <item>Calibration-set permutation importance (for warm-start transfer).</item>
///   <item>Split-conformal qHat (coverage guarantee).</item>
///   <item>Jackknife+ magnitude residuals.</item>
///   <item>Meta-label secondary classifier.</item>
///   <item>Abstention gate (selective prediction).</item>
///   <item>Quantile magnitude regressor (pinball loss, τ = MagnitudeQuantileTau).</item>
///   <item>Decision boundary distance analytics.</item>
///   <item>Durbin-Watson autocorrelation check on magnitude residuals.</item>
///   <item>Mutual-information redundancy check.</item>
///   <item>Temperature scaling alternative calibration.</item>
///   <item>Brier Skill Score.</item>
///   <item>PSI baseline (feature quantile breakpoints).</item>
///   <item>Full <see cref="ModelSnapshot"/> population for downstream scoring and drift monitoring.</item>
/// </list>
/// </para>
/// Registered as keyed <see cref="IMLModelTrainer"/> with key "dann".
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Dann)]
public sealed class DannModelTrainer : IMLModelTrainer
{
    // ── Architecture constants ─────────────────────────────────────────────────
    private const int    DefaultFeatDim = 32;
    private const int    DefaultDomHid  = 16;
    private const int    DefaultBatch   = 64;

    // ── Model identifiers ─────────────────────────────────────────────────────
    private const string ModelType    = "DANN";
    private const string ModelVersion = "4.0";

    // ── Adam hyper-parameters ─────────────────────────────────────────────────
    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    // ── Per-parameter weight-magnitude clip ───────────────────────────────────
    // Applied after every optimizer step to prevent runaway weights that
    // evade gradient-norm clipping (which only bounds the update direction).
    private const float WeightClipMagnitude = 10.0f;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly ILogger<DannModelTrainer> _logger;

    public DannModelTrainer(ILogger<DannModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ───────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);
    }

    // ── Core training logic (synchronous, runs on thread-pool) ────────────────

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
                $"DannModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        int    F       = samples[0].Features.Length;
        double lr      = hp.LearningRate > 0 ? hp.LearningRate : 0.005;
        int    epochs  = hp.MaxEpochs  > 0 ? hp.MaxEpochs  : 50;
        double lamBase = (hp.DannLambda ?? 10) / 10.0;
        int    featDim = hp.DannFeatDim is > 0 ? hp.DannFeatDim.Value : DefaultFeatDim;
        int    domHid  = hp.DannDomHid  is > 0 ? hp.DannDomHid.Value  : DefaultDomHid;

        // ── 0. Incremental update fast-path ───────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int bpd         = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * bpd);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "DANN incremental update: fine-tuning on last {N}/{Total} samples (≈{Days}d window)",
                    recentCount, samples.Count, hp.DensityRatioWindowDays);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    MaxEpochs             = Math.Max(20, epochs / 5),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = lr / 5.0,
                    UseIncrementalUpdate  = false,
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Z-score standardisation ────────────────────────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 2. Walk-forward cross-validation ──────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, F, lr, epochs, lamBase, ct);
        _logger.LogInformation(
            "DANN Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final splits: 70% train | 10% cal | ~20% test ─────────────────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"DANN: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, epochs / 2), LearningRate = lr / 3.0 }
            : hp;

        _logger.LogInformation(
            "DANN: n={N} F={F} featDim={FD} domHid={DH} train={Train} cal={Cal} test={Test} embargo={Embargo}",
            allStd.Count, F, featDim, domHid, trainSet.Count, calSet.Count, testSet.Count, embargo);

        // ── 3b. Stationarity gate ─────────────────────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, F);
            double nonStatFrac = F > 0 ? (double)nonStatCount / F : 0.0;
            if (nonStatFrac > 0.30 && hp.FracDiffD == 0.0)
            {
                _logger.LogWarning(
                    "DANN stationarity gate: {NonStat}/{Total} features ({Frac:P0}) have unit-root signals. " +
                    "Aborting training — set FracDiffD > 0 to enable fractional differencing.",
                    nonStatCount, F, nonStatFrac);
                return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
            }
        }

        // ── 3c. Density-ratio weights ─────────────────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            int bpdMain = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays, bpdMain);
            _logger.LogDebug("DANN density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 3d. Covariate shift weights ───────────────────────────────────────
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, F);
            if (densityWeights is not null)
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            else
                densityWeights = csWeights;
            _logger.LogDebug("DANN covariate shift weights applied (gen={Gen}).",
                warmStart.GenerationNumber);
        }

        // ── 3e. Adaptive label smoothing ──────────────────────────────────────
        double effectiveLabelSmoothing = hp.LabelSmoothing;
        if (hp.UseAdaptiveLabelSmoothing && trainSet.Count > 0)
        {
            var sortedMags = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) sortedMags[i] = Math.Abs(trainSet[i].Magnitude);
            Array.Sort(sortedMags);
            double p20 = sortedMags[(int)(sortedMags.Length * 0.20)];
            int ambigCount = 0;
            foreach (var s in trainSet) if (Math.Abs(s.Magnitude) <= p20) ambigCount++;
            double ambigFrac = (double)ambigCount / trainSet.Count;
            effectiveLabelSmoothing = Math.Clamp(ambigFrac * 0.5, 0.01, 0.20);
            _logger.LogInformation(
                "DANN adaptive label smoothing: ε={Eps:F3} (ambiguous fraction={Frac:P1})",
                effectiveLabelSmoothing, ambigFrac);
        }

        // ── 4. Fit DANN model ─────────────────────────────────────────────────
        var model = FitDann(trainSet, effectiveHp, F, featDim, domHid, lr, epochs, lamBase,
            effectiveLabelSmoothing, warmStart, densityWeights, ct);

        // ── 5. Weight sanitisation ────────────────────────────────────────────
        int sanitizedCount = SanitizeWeights(model);
        if (sanitizedCount > 0)
            _logger.LogWarning("DANN sanitised {N} non-finite weight values.", sanitizedCount);

        // ── 6. Magnitude regressor ────────────────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, F, effectiveHp);

        // ── 7. Platt calibration ──────────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, model, F);
        _logger.LogDebug("DANN Platt: A={A:F4} B={B:F4}", plattA, plattB);

        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, model, F);
        _logger.LogDebug(
            "DANN class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        double avgKellyFraction = ComputeAvgKellyFraction(calSet, model, plattA, plattB, F);
        _logger.LogDebug("DANN average Kelly fraction={Kelly:F4}", avgKellyFraction);

        // ── 8. Final evaluation ───────────────────────────────────────────────
        var finalMetrics = EvaluateModel(testSet, model, magWeights, magBias, plattA, plattB, F);
        _logger.LogInformation(
            "DANN final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 9. ECE ────────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, model, plattA, plattB, F);
        _logger.LogInformation("DANN post-Platt ECE={Ece:F4}", ece);

        // ── 10. EV-optimal threshold ──────────────────────────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, model, plattA, plattB, F, hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("DANN EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 11. Permutation feature importance ────────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, model, plattA, plattB, F, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: MLFeatureHelper.FeatureNames[idx]))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "DANN top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f2 => $"{f2.Name}={f2.Importance:P1}")));

        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, model, F, ct)
            : new double[F];

        // ── 12. Feature pruning re-train pass ─────────────────────────────────
        var activeMask  = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m2 => !m2);
        int activeF     = F - prunedCount;

        if (prunedCount > 0 && activeF >= 10)
        {
            _logger.LogInformation(
                "DANN feature pruning: removing {Pruned}/{Total} low-importance features",
                prunedCount, F);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(20, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            var prunedModel = FitDann(maskedTrain, prunedHp, activeF, featDim, domHid, lr, prunedHp.MaxEpochs,
                lamBase, effectiveLabelSmoothing, null, densityWeights, ct);
            var (pmw, pmb) = FitLinearRegressor(maskedTrain, activeF, prunedHp);
            var (pA, pB)   = FitPlattScaling(maskedCal, prunedModel, activeF);
            var prunedMetrics = EvaluateModel(maskedTest, prunedModel, pmw, pmb, pA, pB, activeF);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "DANN pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                model        = prunedModel;
                magWeights   = pmw;  magBias  = pmb;
                plattA       = pA;   plattB   = pB;
                finalMetrics = prunedMetrics;
                F            = activeF;
                trainSet     = maskedTrain; // downstream Jackknife/PSI/DW use masked features
                testSet      = maskedTest;  // downstream BSS uses masked features
                ece          = ComputeEce(maskedTest, model, plattA, plattB, F);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, model, plattA, plattB, F,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax);
                var (pAB, pBB, pAS, pBS) = FitClassConditionalPlatt(maskedCal, model, F);
                plattABuy = pAB; plattBBuy = pBB; plattASell = pAS; plattBSell = pBS;
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, model, plattA, plattB, F);
            }
            else
            {
                _logger.LogInformation(
                    "DANN pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask  = new bool[F]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
        }

        // ── 13. Isotonic calibration (PAVA) ───────────────────────────────────
        var postPruneCalSet = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;
        double[] isotonicBp = FitIsotonicCalibration(postPruneCalSet, model, plattA, plattB, F);
        _logger.LogInformation("DANN isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 14. Conformal qHat ────────────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat  = ComputeConformalQHat(
            postPruneCalSet, model, plattA, plattB, F, isotonicBp, conformalAlpha);
        _logger.LogInformation("DANN conformal qHat={QHat:F4} ({Cov:P0} coverage)",
            conformalQHat, hp.ConformalCoverage);

        // ── 15. Jackknife+ residuals ──────────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(trainSet, magWeights, magBias, F);
        _logger.LogInformation("DANN Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── 16. Meta-label secondary classifier ───────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            postPruneCalSet, model, plattA, plattB, F);
        _logger.LogDebug("DANN meta-label model: bias={B:F4}", metaLabelBias);

        // ── 17. Abstention gate ───────────────────────────────────────────────
        var (abstWeights, abstBias, abstThreshold) = FitAbstentionModel(
            postPruneCalSet, model, plattA, plattB, F, hp.DannAbstentionF1Sweep);
        _logger.LogDebug("DANN abstention gate: threshold={T:F2}", abstThreshold);

        // ── 18. Quantile magnitude regressor ──────────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            // Floor at 1e-4 so warm-start LR reductions (lr/3 → lr/10) don't starve the regressor.
            double qrLr = hp.DannQuantileRegressorLr > 0.0 ? hp.DannQuantileRegressorLr : Math.Max(lr / 10.0, 1e-4);
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau, qrLr);
            _logger.LogDebug("DANN quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 19. Decision boundary stats ───────────────────────────────────────
        var (dbMean, dbStd) = postPruneCalSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalSet, model, plattA, plattB, F)
            : (0.0, 0.0);
        _logger.LogDebug("DANN decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        // ── 20. Durbin-Watson on magnitude residuals ──────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        _logger.LogDebug("DANN Durbin-Watson={DW:F4}", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "DANN magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2})",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 21. MI redundancy ─────────────────────────────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning("DANN MI redundancy: {N} pairs exceed threshold", redundantPairs.Length);
        }

        // ── 22. Temperature scaling ───────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(postPruneCalSet, model, plattA, plattB, F);
            _logger.LogDebug("DANN temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 23. Brier Skill Score ─────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, model, plattA, plattB, F);
        _logger.LogInformation("DANN BSS={BSS:F4}", brierSkillScore);

        // ── 24. PSI baseline ──────────────────────────────────────────────────
        var stdTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) stdTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(stdTrainFeatures);

        // ── 25. Pack DANN weights for snapshot ────────────────────────────────
        var dannWeights = ExtractDannWeightsForSnapshot(model);

        // ── 26. Serialise model snapshot ──────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                        = ModelType,
            Version                     = ModelVersion,
            Features                    = MLFeatureHelper.FeatureNames,
            Means                       = means,
            Stds                        = stds,
            BaseLearnersK               = 1,
            // Primary weights — feature extractor + label classifier
            Weights                     = model.WFeat,
            Biases                      = [model.bCls],
            MagWeights                  = magWeights,
            MagBias                     = magBias,
            PlattA                      = plattA,
            PlattB                      = plattB,
            PlattABuy                   = plattABuy,
            PlattBBuy                   = plattBBuy,
            PlattASell                  = plattASell,
            PlattBSell                  = plattBSell,
            AvgKellyFraction            = avgKellyFraction,
            Metrics                     = finalMetrics,
            TrainSamples                = trainSet.Count,
            TestSamples                 = testSet.Count,
            CalSamples                  = calSet.Count,
            EmbargoSamples              = embargo,
            TrainedOn                   = DateTime.UtcNow,
            TrainedAtUtc                = DateTime.UtcNow,
            FeatureImportance           = featureImportance,
            FeatureImportanceScores     = calImportanceScores,
            ActiveFeatureMask           = activeMask,
            PrunedFeatureCount          = prunedCount,
            OptimalThreshold            = optimalThreshold,
            Ece                         = ece,
            IsotonicBreakpoints         = isotonicBp,
            ConformalQHat               = conformalQHat,
            JackknifeResiduals          = jackknifeResiduals,
            MetaLabelWeights            = metaLabelWeights,
            MetaLabelBias               = metaLabelBias,
            MetaLabelThreshold          = 0.5,
            AbstentionWeights           = abstWeights,
            AbstentionBias              = abstBias,
            AbstentionThreshold         = abstThreshold,
            MagQ90Weights               = magQ90Weights,
            MagQ90Bias                  = magQ90Bias,
            DecisionBoundaryMean        = dbMean,
            DecisionBoundaryStd         = dbStd,
            DurbinWatsonStatistic       = durbinWatson,
            RedundantFeaturePairs       = redundantPairs,
            TemperatureScale            = temperatureScale,
            BrierSkillScore             = brierSkillScore,
            FeatureQuantileBreakpoints  = featureQuantileBreakpoints,
            ParentModelId               = parentModelId ?? 0,
            GenerationNumber            = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            WalkForwardSharpeTrend      = cvResult.SharpeTrend,
            FeatureStabilityScores      = cvResult.FeatureStabilityScores ?? [],
            FracDiffD                   = hp.FracDiffD,
            HyperparamsJson             = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount       = sanitizedCount,
            AdaptiveLabelSmoothing      = effectiveLabelSmoothing,
            ConformalCoverage           = hp.ConformalCoverage,
            AgeDecayLambda              = hp.AgeDecayLambda,
            // DANN-specific: all layer weights for full model reconstruction
            DannWeights                 = dannWeights,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "DannModelTrainer complete: accuracy={Acc:P1} brier={B:F4} BSS={BSS:F4} snapshotBytes={Bytes}",
            finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore, modelBytes.Length);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN MODEL STATE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete DANN weight state: feature extractor + label classifier + domain classifier.
    /// Inference requires only WFeat, bFeat, wCls, bCls.
    /// </summary>
    private sealed class DannModel
    {
        public int F;
        public int featDim;
        public int domHid;

        // ── Feature extractor Layer 1: F → featDim ────────────────────────────
        public double[][] WFeat;    // [featDim][F]
        public double[]   bFeat;    // [featDim]

        // ── Feature extractor Layer 2: featDim → featDim ──────────────────────
        public double[][] WFeat2;   // [featDim][featDim]
        public double[]   bFeat2;   // [featDim]

        // ── Label classifier: featDim → 1 ─────────────────────────────────────
        public double[] wCls;       // [featDim]
        public double   bCls;

        // ── Domain classifier: featDim → domHid → 1 ───────────────────────────
        public double[][] WDom1;    // [domHid][featDim]
        public double[]   bDom1;    // [domHid]
        public double[]   wDom2;    // [domHid]
        public double     bDom2;

        public DannModel(int f, int featDim, int domHid)
        {
            F            = f;
            this.featDim = featDim;
            this.domHid  = domHid;

            WFeat  = new double[featDim][]; for (int j = 0; j < featDim; j++) WFeat[j]  = new double[f];
            bFeat  = new double[featDim];
            WFeat2 = new double[featDim][]; for (int j = 0; j < featDim; j++) WFeat2[j] = new double[featDim];
            bFeat2 = new double[featDim];
            wCls   = new double[featDim];
            bCls   = 0.0;
            WDom1  = new double[domHid][]; for (int k = 0; k < domHid; k++) WDom1[k] = new double[featDim];
            bDom1  = new double[domHid];
            wDom2  = new double[domHid];
            bDom2  = 0.0;
        }

        /// <summary>Deep-copy for checkpointing best weights during early stopping.</summary>
        public DannModel Clone()
        {
            var c = new DannModel(F, featDim, domHid);
            for (int j = 0; j < featDim; j++) Array.Copy(WFeat[j],  c.WFeat[j],  F);
            Array.Copy(bFeat,  c.bFeat,  featDim);
            for (int j = 0; j < featDim; j++) Array.Copy(WFeat2[j], c.WFeat2[j], featDim);
            Array.Copy(bFeat2, c.bFeat2, featDim);
            Array.Copy(wCls,   c.wCls,   featDim);
            c.bCls = bCls;
            for (int k = 0; k < domHid; k++) Array.Copy(WDom1[k], c.WDom1[k], featDim);
            Array.Copy(bDom1, c.bDom1, domHid);
            Array.Copy(wDom2, c.wDom2, domHid);
            c.bDom2 = bDom2;
            return c;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN PARAMETER HOLDER  (raw Parameters, mirrors SvgpModelTrainer pattern)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds raw <see cref="Parameter"/> tensors for the 2-layer DANN architecture.
    /// Feature extractor:  F → featDim (ReLU) → featDim (ReLU)
    /// Label classifier:   featDim → 1
    /// Domain classifier:  featDim → domHid (ReLU) → 1
    /// GRL is applied externally via the detach-trick before calling <see cref="DomainLogit"/>.
    /// </summary>
    private sealed class DannNet : IDisposable
    {
        // ── Feat1: F → featDim ────────────────────────────────────────────────
        public readonly Parameter Feat1W;  // [featDim, F]
        public readonly Parameter Feat1B;  // [featDim]
        // ── Feat2: featDim → featDim ──────────────────────────────────────────
        public readonly Parameter Feat2W;  // [featDim, featDim]
        public readonly Parameter Feat2B;  // [featDim]
        // ── Cls: featDim → 1 ──────────────────────────────────────────────────
        public readonly Parameter ClsW;    // [1, featDim]
        public readonly Parameter ClsB;    // [1]
        // ── Dom1: featDim → domHid, Dom2: domHid → 1 ─────────────────────────
        public readonly Parameter Dom1W;   // [domHid, featDim]
        public readonly Parameter Dom1B;   // [domHid]
        public readonly Parameter Dom2W;   // [1, domHid]
        public readonly Parameter Dom2B;   // [1]

        public readonly int F, FeatDim, DomHid;
        public readonly Device DeviceType;

        /// <summary>
        /// Controls whether dropout is applied in <see cref="Features"/>.
        /// Set to <c>false</c> during validation / evaluation to get deterministic predictions.
        /// </summary>
        public bool Training { get; set; } = true;

        private const float DropoutRate = 0.10f;

        // All parameters, passed directly to torch.optim.Adam
        public Parameter[] AllParams => [Feat1W, Feat1B, Feat2W, Feat2B,
                                          ClsW,   ClsB,   Dom1W,  Dom1B,
                                          Dom2W,  Dom2B];

        public DannNet(int F, int featDim, int domHid, Device device)
        {
            this.F       = F;
            this.FeatDim = featDim;
            this.DomHid  = domHid;
            DeviceType   = device;

            Feat1W = new Parameter(KaimingUniform(featDim, F,       device));
            Feat1B = new Parameter(zeros(featDim,          device: device));
            Feat2W = new Parameter(KaimingUniform(featDim, featDim, device));
            Feat2B = new Parameter(zeros(featDim,          device: device));
            ClsW   = new Parameter(KaimingUniform(1,       featDim, device));
            ClsB   = new Parameter(zeros(1,                device: device));
            Dom1W  = new Parameter(KaimingUniform(domHid,  featDim, device));
            Dom1B  = new Parameter(zeros(domHid,           device: device));
            Dom2W  = new Parameter(KaimingUniform(1,       domHid,  device));
            Dom2B  = new Parameter(zeros(1,                device: device));
        }

        private static Tensor KaimingUniform(int fanOut, int fanIn, Device device)
        {
            float bound = (float)Math.Sqrt(2.0 / fanIn);
            return (torch.rand(fanOut, fanIn, device: device) * 2f - 1f) * bound;
        }

        /// <summary>
        /// Returns hidden representation [B, featDim] after both ReLU layers.
        /// When <see cref="Training"/> is <c>true</c>, applies 10 % dropout after layer-1
        /// to regularise the feature extractor. Dropout is disabled during evaluation.
        /// </summary>
        public Tensor Features(Tensor x)
        {
            var h1 = functional.relu(torch.mm(x, Feat1W.t()) + Feat1B);
            if (Training)
            {
                var h1d = functional.dropout(h1, p: DropoutRate, training: true);
                h1.Dispose();
                h1 = h1d;
            }
            var result = functional.relu(torch.mm(h1, Feat2W.t()) + Feat2B);
            h1.Dispose();
            return result;
        }

        /// <summary>Returns raw classification logit [B, 1].</summary>
        public Tensor ClassifyLogit(Tensor h) => torch.mm(h, ClsW.t()) + ClsB;

        /// <summary>Returns raw domain logit [B, 1]. Pass GRL-transformed features.</summary>
        public Tensor DomainLogit(Tensor hGrl)
        {
            using var hd = functional.relu(torch.mm(hGrl, Dom1W.t()) + Dom1B);
            return torch.mm(hd, Dom2W.t()) + Dom2B;
        }

        /// <summary>Zero out all parameter gradients before each batch.</summary>
        public void ZeroGrad()
        {
            foreach (var p in AllParams) p.grad?.zero_();
        }

        /// <summary>Clip gradient global norm to <paramref name="maxNorm"/>.</summary>
        public void ClipGradNorm(float maxNorm)
        {
            double totalNorm = 0.0;
            foreach (var p in AllParams)
            {
                if (p.grad is { } g)
                    totalNorm += g.pow(2).sum().item<float>();
            }
            double norm = Math.Sqrt(totalNorm);
            if (norm > maxNorm)
            {
                float coeff = (float)(maxNorm / norm);
                foreach (var p in AllParams) p.grad?.mul_(coeff);
            }
        }

        public void Dispose()
        {
            Feat1W.Dispose(); Feat1B.Dispose();
            Feat2W.Dispose(); Feat2B.Dispose();
            ClsW.Dispose();   ClsB.Dispose();
            Dom1W.Dispose();  Dom1B.Dispose();
            Dom2W.Dispose();  Dom2B.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN FITTING  (TorchSharp: 2-layer extractor + GRL + Adam + cosine LR)
    // ═══════════════════════════════════════════════════════════════════════════

    private DannModel FitDann(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        int                  featDim,
        int                  domHid,
        double               baseLr,
        int                  maxEpochs,
        double               lamBase,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct)
    {
        int n        = trainSet.Count;
        int batchSz  = Math.Min(DefaultBatch, n);
        int patience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 10;

        // Validation split (last 10% of training data)
        int valSize = Math.Max(10, n / 10);
        int trainN  = n - valSize;
        var valSet  = trainSet.GetRange(trainN, valSize);

        // Temporal decay weights for training samples
        double[] tempWeights = ComputeTemporalWeights(trainN, hp.TemporalDecayLambda);

        // Blend with density weights; normalise to mean 1
        double[] sampleWeights = new double[trainN];
        for (int i = 0; i < trainN; i++)
        {
            sampleWeights[i] = tempWeights[i];
            if (densityWeights is not null && i < densityWeights.Length)
                sampleWeights[i] *= densityWeights[i];
            sampleWeights[i] = Math.Max(sampleWeights[i], 1e-8);
        }
        double wSum = sampleWeights.Sum();
        if (wSum > 0) for (int i = 0; i < trainN; i++) sampleWeights[i] = sampleWeights[i] * trainN / wSum;

        // Source/target domain boundary — use BarsPerDay instead of hardcoded 24
        int barsPerDay = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
        // When an explicit window is set, the domain boundary is trainN minus that window (older = source,
        // recent = target). When no window is configured, use the last 30% as the target domain so that
        // the GRL always adapts toward recent market conditions rather than splitting arbitrarily at the midpoint.
        int domainBoundary = hp.DensityRatioWindowDays > 0
            ? Math.Max(1, trainN - Math.Min(trainN - 1, hp.DensityRatioWindowDays * barsPerDay))
            : Math.Max(1, trainN - trainN / 3);

        // ── Select compute device (GPU when available, CPU otherwise) ─────────
        var device = torch.cuda.is_available() ? CUDA : CPU;
        _logger.LogInformation("DANN: training on {Device}", device.type);

        // ── Build TorchSharp module ───────────────────────────────────────────
        using var net = new DannNet(F, featDim, domHid, device);

        // Warm-start from parent snapshot if geometry matches
        if (warmStart?.DannWeights is { Length: > 0 } ws)
            TryLoadWarmStartWeights(net, ws, F, featDim, domHid, device, _logger);
        else
            _logger.LogDebug("DANN: cold init (no warm-start).");

        double weightDecay = hp.L2Lambda > 0 ? hp.L2Lambda : 0.0;
        using var opt       = optim.Adam(net.AllParams, lr: baseLr, weight_decay: weightDecay);
        var scheduler = optim.lr_scheduler.CosineAnnealingLR(opt, T_max: maxEpochs, eta_min: baseLr * 0.01);

        var rng     = new Random(42);
        var indices = Enumerable.Range(0, trainN).ToArray();

        double    bestValAcc      = -1.0;
        DannModel? bestModel      = null;
        int        noImproveTicks = 0;

        // Pre-build validation batch tensors once (avoids per-epoch allocation)
        int nVal = valSet.Count;
        var valXArr = new float[nVal * F];
        var valYArr = new int[nVal];
        for (int vi = 0; vi < nVal; vi++)
        {
            Array.Copy(valSet[vi].Features, 0, valXArr, vi * F, F);
            valYArr[vi] = valSet[vi].Direction;
        }

        for (int epoch = 0; epoch < maxEpochs && !ct.IsCancellationRequested; epoch++)
        {
            // Progressive GRL λ: λ(p) = 2/(1+e^{-10p}) − 1,  p ∈ [0,1]
            double p   = (double)epoch / maxEpochs;
            float  lam = (float)(lamBase * (2.0 / (1.0 + Math.Exp(-10.0 * p)) - 1.0));

            // Shuffle training indices
            for (int i = trainN - 1; i > 0; i--)
            {
                int j2 = rng.Next(i + 1);
                (indices[i], indices[j2]) = (indices[j2], indices[i]);
            }

            for (int start = 0; start < trainN && !ct.IsCancellationRequested; start += batchSz)
            {
                int end = Math.Min(start + batchSz, trainN);
                int bsz = end - start;

                // Build batch arrays
                var xArr    = new float[bsz * F];
                var yClsArr = new float[bsz];
                var yDomArr = new float[bsz];
                var wArr    = new float[bsz];
                var srcList = new List<int>(bsz);

                for (int bi = 0; bi < bsz; bi++)
                {
                    int idx    = indices[start + bi];
                    var s      = trainSet[idx];
                    bool isSrc = idx < domainBoundary;
                    Array.Copy(s.Features, 0, xArr, bi * F, F);
                    yClsArr[bi] = s.Direction == 1
                        ? (float)(1.0 - labelSmoothing * 0.5)
                        : (float)(labelSmoothing * 0.5);
                    yDomArr[bi] = isSrc ? 0f : 1f;
                    wArr[bi]    = (float)(idx < sampleWeights.Length ? sampleWeights[idx] : 1.0);
                    if (isSrc) srcList.Add(bi);
                }

                opt.zero_grad();

                using var xT    = torch.tensor(xArr,    device: device).reshape(bsz, F);
                using var yDomT = torch.tensor(yDomArr, device: device).reshape(bsz, 1);
                using var wDomT = torch.tensor(wArr,    device: device).reshape(bsz, 1);

                // Forward: 2-layer feature extraction
                using var h = net.Features(xT);   // [bsz, featDim]

                // ── Classification loss (source samples only) ──────────────────
                Tensor? clsLoss = null;
                if (srcList.Count > 0)
                {
                    using var srcIdxT  = torch.tensor(srcList.ToArray(), dtype: ScalarType.Int64, device: device);
                    using var hSrc     = h.index_select(0, srcIdxT);      // [nSrc, featDim]
                    using var logitSrc = net.ClassifyLogit(hSrc);          // [nSrc, 1]
                    using var predSrc  = torch.sigmoid(logitSrc);

                    var yClsSrc = srcList.Select(bi => yClsArr[bi]).ToArray();
                    var wClsSrc = srcList.Select(bi => wArr[bi]).ToArray();
                    using var yClsSrcT = torch.tensor(yClsSrc, device: device).reshape(srcList.Count, 1);
                    using var wClsSrcT = torch.tensor(wClsSrc, device: device).reshape(srcList.Count, 1);

                    clsLoss = functional.binary_cross_entropy(predSrc, yClsSrcT, weight: wClsSrcT);
                }

                // ── Domain loss with GRL ───────────────────────────────────────
                // h_grl = h.detach()*(1+λ) − h*λ  ⟹  forward=h, ∂/∂h = −λ (reversal)
                using var hGrl    = h.detach() * (1f + lam) - h * lam;
                using var domLogit = net.DomainLogit(hGrl);                // [bsz, 1]
                using var domPred  = torch.sigmoid(domLogit);
                using var domLoss  = functional.binary_cross_entropy(domPred, yDomT, weight: wDomT);

                // Total loss + backward
                Tensor totalLoss = clsLoss is not null ? clsLoss + domLoss : domLoss;
                totalLoss.backward();

                // Gradient norm clipping (manual — avoids torch.nn.utils ambiguity)
                net.ClipGradNorm(5.0f);
                opt.step();

                // Per-parameter weight-magnitude clipping — guards against runaway
                // weights that survive gradient-norm clipping (different failure mode).
                using (no_grad())
                {
                    foreach (var param in net.AllParams)
                        param.clamp_(-WeightClipMagnitude, WeightClipMagnitude);
                }

                clsLoss?.Dispose();
                if (!ReferenceEquals(totalLoss, domLoss)) totalLoss.Dispose();
            }

            // Cosine annealing LR step — updates the optimizer's LR for the next epoch
            scheduler.step();

            // ── Early stopping: batch-validate on full val set ─────────────────
            net.Training = false;
            using (no_grad())
            {
                using var valXT    = torch.tensor(valXArr, device: device).reshape(nVal, F);
                using var valH     = net.Features(valXT);
                using var valLogit = net.ClassifyLogit(valH);
                using var valProb  = torch.sigmoid(valLogit).squeeze(1);
                var probs = valProb.cpu().data<float>().ToArray();
                int correct = 0;
                for (int vi = 0; vi < nVal; vi++)
                    if ((probs[vi] >= 0.5f ? 1 : 0) == valYArr[vi]) correct++;
                double valAcc = (double)correct / nVal;

                if (valAcc > bestValAcc + 1e-4)
                {
                    bestValAcc     = valAcc;
                    bestModel      = ExtractToDannModel(net, F, featDim, domHid);
                    noImproveTicks = 0;
                }
                else if (++noImproveTicks >= patience)
                {
                    break;  // Fixed: patience is now in epochs (1 check per epoch)
                }
            }
            net.Training = true;
        }

        return bestModel ?? ExtractToDannModel(net, F, featDim, domHid);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WEIGHT EXTRACTION: TorchSharp → DannModel (for C# inference pipeline)
    // ═══════════════════════════════════════════════════════════════════════════

    private static DannModel ExtractToDannModel(DannNet net, int F, int featDim, int domHid)
    {
        var m = new DannModel(F, featDim, domHid);
        using (no_grad())
        {
            // Parameters are raw tensors; .cpu().contiguous().data<float>() reads values safely.
            // Feat1W shape: [featDim, F]
            var w1 = net.Feat1W.cpu().contiguous().data<float>().ToArray();
            var b1 = net.Feat1B.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++)
            {
                m.bFeat[j] = b1[j];
                for (int fi = 0; fi < F; fi++) m.WFeat[j][fi] = w1[j * F + fi];
            }

            // Feat2W shape: [featDim, featDim]
            var w2 = net.Feat2W.cpu().contiguous().data<float>().ToArray();
            var b2 = net.Feat2B.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++)
            {
                m.bFeat2[j] = b2[j];
                for (int k = 0; k < featDim; k++) m.WFeat2[j][k] = w2[j * featDim + k];
            }

            // ClsW shape: [1, featDim]
            var wCls = net.ClsW.cpu().contiguous().data<float>().ToArray();
            var bCls = net.ClsB.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++) m.wCls[j] = wCls[j];
            m.bCls = bCls[0];

            // Dom1W shape: [domHid, featDim]
            var wd1 = net.Dom1W.cpu().contiguous().data<float>().ToArray();
            var bd1 = net.Dom1B.cpu().contiguous().data<float>().ToArray();
            for (int k = 0; k < domHid; k++)
            {
                m.bDom1[k] = bd1[k];
                for (int j = 0; j < featDim; j++) m.WDom1[k][j] = wd1[k * featDim + j];
            }

            // Dom2W shape: [1, domHid]
            var wd2 = net.Dom2W.cpu().contiguous().data<float>().ToArray();
            var bd2 = net.Dom2B.cpu().contiguous().data<float>().ToArray();
            for (int k = 0; k < domHid; k++) m.wDom2[k] = wd2[k];
            m.bDom2 = bd2[0];
        }
        return m;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WARM-START: load DannWeights snapshot → TorchSharp module
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads weights from a serialised <see cref="ModelSnapshot.DannWeights"/> into <paramref name="net"/>.
    /// Supports both the v4 format (2-layer extractor) and the legacy v3 format (1-layer).
    /// When a geometry mismatch is detected the module is left at default Kaiming init.
    /// </summary>
    private static void TryLoadWarmStartWeights(
        DannNet net, double[][] ws, int F, int featDim, int domHid,
        Device device, ILogger logger)
    {
        // v4 row layout: featDim + featDim + 1 + domHid + 1  (2-layer extractor)
        // v3 row layout: featDim + 1 + domHid + 1            (1-layer extractor)
        int newRows = 2 * featDim + 1 + domHid + 1;
        int oldRows = featDim + 1 + domHid + 1;

        bool isNew = ws.Length == newRows && ws[0].Length == F + 1;
        bool isOld = ws.Length == oldRows && ws[0].Length == F + 1;

        if (!isNew && !isOld)
        {
            logger.LogDebug("DANN warm-start geometry mismatch — keeping Kaiming init.");
            return;
        }

        using (no_grad())
        {
            // ── Feat1W / Feat1B ───────────────────────────────────────────────
            var w1f = new float[featDim * F];
            var b1f = new float[featDim];
            for (int j = 0; j < featDim; j++)
            {
                b1f[j] = (float)ws[j][F];
                for (int fi = 0; fi < F; fi++) w1f[j * F + fi] = (float)ws[j][fi];
            }
            net.Feat1W.copy_(torch.tensor(w1f, device: device).reshape(featDim, F));
            net.Feat1B.copy_(torch.tensor(b1f, device: device));

            // ── Feat2W / Feat2B (v4 only; v3 keeps Kaiming init) ──────────────
            int baseRow;
            if (isNew)
            {
                var w2f = new float[featDim * featDim];
                var b2f = new float[featDim];
                for (int j = 0; j < featDim; j++)
                {
                    b2f[j] = (float)ws[featDim + j][featDim];
                    for (int k = 0; k < featDim; k++) w2f[j * featDim + k] = (float)ws[featDim + j][k];
                }
                net.Feat2W.copy_(torch.tensor(w2f, device: device).reshape(featDim, featDim));
                net.Feat2B.copy_(torch.tensor(b2f, device: device));
                baseRow = 2 * featDim;
            }
            else
            {
                logger.LogDebug("DANN warm-start: upgrading v3→v4 (Feat2 cold-init).");
                baseRow = featDim;
            }

            // ── ClsW / ClsB ───────────────────────────────────────────────────
            var wClsF = new float[featDim];
            var bClsF = new float[1];
            for (int j = 0; j < featDim; j++) wClsF[j] = (float)ws[baseRow][j];
            bClsF[0] = (float)ws[baseRow][featDim];
            net.ClsW.copy_(torch.tensor(wClsF, device: device).reshape(1, featDim));
            net.ClsB.copy_(torch.tensor(bClsF, device: device));

            // ── Dom1W / Dom1B ─────────────────────────────────────────────────
            var wd1f = new float[domHid * featDim];
            var bd1f = new float[domHid];
            for (int k = 0; k < domHid; k++)
            {
                bd1f[k] = (float)ws[baseRow + 1 + k][featDim];
                for (int j = 0; j < featDim; j++) wd1f[k * featDim + j] = (float)ws[baseRow + 1 + k][j];
            }
            net.Dom1W.copy_(torch.tensor(wd1f, device: device).reshape(domHid, featDim));
            net.Dom1B.copy_(torch.tensor(bd1f, device: device));

            // ── Dom2W / Dom2B ─────────────────────────────────────────────────
            var wd2f = new float[domHid];
            var bd2f = new float[1];
            int lastRow = baseRow + 1 + domHid;
            for (int k = 0; k < domHid; k++) wd2f[k] = (float)ws[lastRow][k];
            bd2f[0] = (float)ws[lastRow][domHid];
            net.Dom2W.copy_(torch.tensor(wd2f, device: device).reshape(1, domHid));
            net.Dom2B.copy_(torch.tensor(bd2f, device: device));
        }
        logger.LogDebug("DANN warm-start: weights loaded ({Format}).", isNew ? "v4" : "v3→v4");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INFERENCE FORWARD PASS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns P(Buy|x) ∈ (0,1) using the 2-layer feature extractor + label classifier.
    /// Layer1: h1 = ReLU(WFeat  · x   + bFeat)
    /// Layer2: h2 = ReLU(WFeat2 · h1  + bFeat2)
    /// Output: σ(wCls · h2 + bCls)
    /// </summary>
    private static double ForwardCls(DannModel m, float[] x)
    {
        // ── Layer 1: F → featDim ─────────────────────────────────────────────
        var h1 = new double[m.featDim];
        for (int j = 0; j < m.featDim; j++)
        {
            double pre = m.bFeat[j];
            for (int fi = 0; fi < m.F; fi++) pre += m.WFeat[j][fi] * x[fi];
            h1[j] = Math.Max(0.0, pre);
        }

        // ── Layer 2: featDim → featDim ───────────────────────────────────────
        double logit = m.bCls;
        for (int j = 0; j < m.featDim; j++)
        {
            double pre2 = m.bFeat2[j];
            for (int k = 0; k < m.featDim; k++) pre2 += m.WFeat2[j][k] * h1[k];
            logit += m.wCls[j] * Math.Max(0.0, pre2);
        }
        return Sigmoid(logit);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        double               lr,
        int                  epochs,
        double               lamBase,
        CancellationToken    ct)
    {
        int folds    = hp.WalkForwardFolds;
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);
        int featDim  = hp.DannFeatDim is > 0 ? hp.DannFeatDim.Value : DefaultFeatDim;
        int domHid   = hp.DannDomHid  is > 0 ? hp.DannDomHid.Value  : DefaultDomHid;

        if (foldSize < 50)
        {
            _logger.LogWarning("DANN walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImpList = new List<double[]>(folds);
        int badFolds   = 0;
        int evaluatedFolds = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd  = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) { continue; }

            var foldTrain = samples[..trainEnd].ToList();
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count) foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) continue;
            evaluatedFolds++;

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(20, epochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            var cvModel = FitDann(foldTrain, cvHp, F, featDim, domHid, lr, cvHp.MaxEpochs, lamBase,
                hp.LabelSmoothing, null, null, ct);
            var m = EvaluateModel(foldTest.ToList(), cvModel, new double[F], 0.0, 1.0, 0.0, F);

            // Per-feature stability: single-round permutation importance when the fold test
            // set is large enough for a reliable estimate; fall back to mean |WFeat| otherwise.
            var foldImp = new double[F];
            if (foldTest.Count >= 50)
            {
                var foldRng  = new Random(42);
                var foldList = foldTest.ToList();
                double foldBase = foldList.Average(s =>
                    (ForwardCls(cvModel, s.Features) >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0);
                for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
                {
                    var shuffled = foldList.Select(s => ((float[])s.Features.Clone(), s.Direction)).ToList();
                    for (int i = shuffled.Count - 1; i > 0; i--)
                    {
                        int j2 = foldRng.Next(i + 1);
                        (shuffled[i].Item1[fi], shuffled[j2].Item1[fi]) =
                            (shuffled[j2].Item1[fi], shuffled[i].Item1[fi]);
                    }
                    double shuffleAcc = shuffled.Average(s =>
                        (ForwardCls(cvModel, s.Item1) >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0);
                    foldImp[fi] = Math.Max(0.0, foldBase - shuffleAcc);
                }
            }
            else
            {
                for (int fi = 0; fi < F; fi++)
                {
                    double s2 = 0;
                    for (int j = 0; j < cvModel.featDim; j++) s2 += Math.Abs(cvModel.WFeat[j][fi]);
                    foldImp[fi] = s2 / cvModel.featDim;
                }
            }

            // Equity-curve gate: reject if max-drawdown is catastrophic
            if (m.SharpeRatio < -2.0 || (m.ExpectedValue < -0.05 && m.Accuracy < 0.40))
            {
                badFolds++;
                _logger.LogDebug("DANN fold {Fold} failed equity gate (sharpe={S:F2} acc={A:P1})",
                    fold, m.SharpeRatio, m.Accuracy);
                continue;
            }

            accList.Add(m.Accuracy);
            f1List.Add(m.F1);
            evList.Add(m.ExpectedValue);
            sharpeList.Add(m.SharpeRatio);
            foldImpList.Add(foldImp);
        }

        if (accList.Count == 0)
        {
            // Mirror the other trainers: a small/hostile CV window should not force
            // an empty snapshot when final training can still proceed.
            _logger.LogWarning("DANN: no usable CV folds were retained — continuing without a CV gate.");
            return (new WalkForwardResult(0, 0, 0, 0, 0, evaluatedFolds), false);
        }

        double avgAcc  = accList.Average();
        double stdAcc  = Std(accList);
        double avgF1   = f1List.Average();
        double avgEV   = evList.Average();
        double avgShrp = sharpeList.Average();

        // Equity curve gate: fraction of bad folds exceeds MaxBadFoldFraction (default 0.5).
        double maxBadFrac = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool gateFailed = folds > 0 && (double)badFolds / folds > maxBadFrac;

        // Walk-forward Sharpe trend (linear regression slope)
        double sharpeTrend = ComputeLinearSlope(sharpeList);

        // Feature stability: per-feature coefficient of variation across folds
        double[]? featureStability = null;
        if (foldImpList.Count >= 2)
        {
            featureStability = new double[F];
            for (int fi = 0; fi < F; fi++)
            {
                var vals = foldImpList.Select(imp => imp[fi]).ToList();
                double mean = vals.Average();
                double std2 = Std(vals);
                featureStability[fi] = mean > 1e-9 ? std2 / mean : 0.0;
            }
        }

        // Cross-fold variance gate
        if (stdAcc > hp.MaxWalkForwardStdDev && hp.MaxWalkForwardStdDev < 1.0)
        {
            _logger.LogWarning(
                "DANN walk-forward std={Std:P1} exceeds gate {Gate:P1} — model may be unstable.",
                stdAcc, hp.MaxWalkForwardStdDev);
        }

        return (new WalkForwardResult(avgAcc, stdAcc, avgF1, avgEV, avgShrp,
            accList.Count, SharpeTrend: sharpeTrend, FeatureStabilityScores: featureStability), gateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CALIBRATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, DannModel model, int F)
    {
        if (calSet.Count < 4) return (1.0, 0.0);

        double[] probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = Math.Clamp(ForwardCls(model, calSet[i].Features), 1e-7, 1.0 - 1e-7);

        // Fit A, B via L-BFGS-style gradient descent on NLL
        double A = 1.0, B = 0.0;
        for (int iter = 0; iter < 200; iter++)
        {
            double dA = 0.0, dB = 0.0;
            foreach (var (s, p) in calSet.Zip(probs))
            {
                double logit = A * Math.Log(p / (1.0 - p)) + B;
                double q     = Sigmoid(logit);
                double err   = q - s.Direction;
                dA += err * Math.Log(p / (1.0 - p));
                dB += err;
            }
            double lr2 = 0.01 / calSet.Count;
            A -= lr2 * dA;
            B -= lr2 * dB;
        }
        return (A, B);
    }

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet, DannModel model, int F)
    {
        var buySamples  = calSet.Where(s => s.Direction == 1).ToList();
        var sellSamples = calSet.Where(s => s.Direction == 0).ToList();

        var (ab, bb) = buySamples.Count  >= 4 ? FitPlattScaling(buySamples,  model, F) : (1.0, 0.0);
        var (as2, bs) = sellSamples.Count >= 4 ? FitPlattScaling(sellSamples, model, F) : (1.0, 0.0);
        return (ab, bb, as2, bs);
    }

    private static double ApplyPlatt(double rawP, double A, double B)
    {
        double logit = A * Math.Log(Math.Clamp(rawP, 1e-7, 1.0 - 1e-7) /
                                    (1.0 - Math.Clamp(rawP, 1e-7, 1.0 - 1e-7))) + B;
        return Sigmoid(logit);
    }

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        if (calSet.Count < 4) return [];

        var pairs = calSet
            .Select(s => (Score: ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB),
                          Label: (double)s.Direction))
            .OrderBy(p => p.Score)
            .ToList();

        // PAVA (pool adjacent violators)
        var blocks = new List<(double MeanScore, double MeanLabel, int Count)>();
        foreach (var (score, label, _) in pairs.Select((p, _) => (p.Score, p.Label, 1)))
        {
            blocks.Add((score, label, 1));
            while (blocks.Count > 1 &&
                   blocks[^1].MeanLabel < blocks[^2].MeanLabel)
            {
                var b1 = blocks[^2]; var b2 = blocks[^1];
                int  tc = b1.Count + b2.Count;
                blocks.RemoveAt(blocks.Count - 1);
                blocks.RemoveAt(blocks.Count - 1);
                blocks.Add((
                    (b1.MeanScore * b1.Count + b2.MeanScore * b2.Count) / tc,
                    (b1.MeanLabel * b1.Count + b2.MeanLabel * b2.Count) / tc,
                    tc));
            }
        }

        // Flatten to [x0, y0, x1, y1, ...]
        var result = new double[blocks.Count * 2];
        for (int i = 0; i < blocks.Count; i++)
        {
            result[i * 2]     = blocks[i].MeanScore;
            result[i * 2 + 1] = blocks[i].MeanLabel;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        double T = 1.0;
        for (int iter = 0; iter < 100; iter++)
        {
            double dT = 0.0;
            foreach (var s in calSet)
            {
                double rawP   = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double logit  = Math.Log(Math.Clamp(rawP, 1e-7, 1.0 - 1e-7) /
                                         (1.0 - Math.Clamp(rawP, 1e-7, 1.0 - 1e-7))) / T;
                double q      = Sigmoid(logit);
                dT           += (q - s.Direction) * (-logit / T);
            }
            T = Math.Max(0.1, T - 0.01 * dT / calSet.Count);
        }
        return T;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        DannModel            model,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  F)
    {
        if (testSet.Count == 0) return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, wAccSum = 0, wSum = 0;
        var returns = new List<double>();

        for (int i = 0; i < testSet.Count; i++)
        {
            var   s     = testSet[i];
            double rawP = ForwardCls(model, s.Features);
            double p    = ApplyPlatt(rawP, plattA, plattB);
            int    pred = p >= 0.5 ? 1 : 0;

            if (pred == 1 && s.Direction == 1) tp++;
            else if (pred == 1 && s.Direction == 0) fp++;
            else if (pred == 0 && s.Direction == 1) fn++;
            else tn++;

            brierSum += (p - s.Direction) * (p - s.Direction);
            double ev = (2.0 * p - 1.0) * Math.Sign(s.Magnitude);
            evSum += ev;
            double magPred = magBias;
            if (magWeights.Length == F)
                for (int fi = 0; fi < F; fi++) magPred += magWeights[fi] * s.Features[fi];
            magSse += (magPred - s.Magnitude) * (magPred - s.Magnitude);

            double confidence = Math.Abs(p - 0.5) * 2.0;
            bool correct = pred == s.Direction;
            wAccSum += confidence * (correct ? 1.0 : 0.0);
            wSum    += confidence;

            double ret = (s.Direction == 1 ? 1.0 : -1.0) * (pred == s.Direction ? Math.Abs((double)s.Magnitude) : -Math.Abs((double)s.Magnitude));
            returns.Add(ret);
        }

        int n = testSet.Count;
        double acc       = (double)(tp + tn) / n;
        double prec      = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
        double rec       = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
        double f1        = prec + rec > 0 ? 2.0 * prec * rec / (prec + rec) : 0.0;
        double brier     = brierSum / n;
        double ev2       = evSum / n;
        double magRmse   = Math.Sqrt(magSse / n);
        double wAcc      = wSum > 0 ? wAccSum / wSum : acc;

        double retMean   = returns.Count > 0 ? returns.Average() : 0.0;
        double retStd    = returns.Count > 1 ? Math.Sqrt(returns.Sum(r => (r - retMean) * (r - retMean)) / (returns.Count - 1)) : 1.0;
        double sharpe    = retStd > 1e-9 ? retMean / retStd * Math.Sqrt(252.0) : 0.0;

        return new EvalMetrics(acc, prec, rec, f1, magRmse, ev2, brier, wAcc, sharpe, tp, fp, fn, tn);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ECE, THRESHOLD, KELLY, BRIER SKILL SCORE
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        List<TrainingSample> testSet, DannModel model, double plattA, double plattB, int F,
        int bins = 10)
    {
        if (testSet.Count == 0) return 0.0;

        // Precompute probabilities once — avoids 3× redundant forward passes per bin
        var probs = testSet.Select(s =>
            ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB)).ToArray();

        double binWidth = 1.0 / bins;
        double ece = 0.0;
        for (int b = 0; b < bins; b++)
        {
            double lo = b * binWidth, hi = lo + binWidth;
            double confSum = 0.0, accSum = 0.0;
            int count = 0;
            for (int i = 0; i < testSet.Count; i++)
            {
                double p2 = probs[i];
                if (p2 < lo || p2 >= hi) continue;
                confSum += p2;
                accSum  += testSet[i].Direction; // empirical positive rate (ECE calibration, not accuracy)
                count++;
            }
            if (count == 0) continue;
            ece += Math.Abs(accSum / count - confSum / count) * count / testSet.Count;
        }
        return ece;
    }

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F,
        double searchMin, double searchMax)
    {
        if (calSet.Count == 0) return 0.5;
        double min = searchMin > 0 ? searchMin : 0.30;
        double max = searchMax > 0 ? searchMax : 0.70;
        double bestThr = 0.5, bestEV = double.MinValue;
        for (double thr = min; thr <= max; thr += 0.01)
        {
            double ev = 0.0;
            foreach (var s in calSet)
            {
                double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                if (p2 >= thr) ev += (2.0 * p2 - 1.0) * Math.Sign(s.Magnitude);
            }
            if (ev > bestEV) { bestEV = ev; bestThr = thr; }
        }
        return bestThr;
    }

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F)
    {
        if (calSet.Count == 0) return 0.0;
        double kelly = 0.0;
        foreach (var s in calSet)
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            kelly += Math.Max(0.0, 2.0 * p2 - 1.0);
        }
        return kelly * 0.5 / calSet.Count;
    }

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, DannModel model,
        double plattA, double plattB, int F)
    {
        if (testSet.Count == 0) return 0.0;
        double baseFrac  = testSet.Average(s => s.Direction);
        double naiveBrier = baseFrac * (1.0 - baseFrac);
        double modelBrier = testSet.Average(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return (p2 - s.Direction) * (p2 - s.Direction);
        });
        return naiveBrier > 1e-9 ? 1.0 - modelBrier / naiveBrier : 0.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONFORMAL + JACKKNIFE+
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet, DannModel model,
        double plattA, double plattB, int F,
        double[] isotonicBp, double alpha)
    {
        if (calSet.Count == 0) return 0.5;
        var scores = calSet.Select(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return s.Direction == 1 ? 1.0 - p2 : p2;
        }).OrderBy(x => x).ToArray();
        int idx = (int)Math.Ceiling((1.0 - alpha) * (scores.Length + 1)) - 1;
        idx = Math.Clamp(idx, 0, scores.Length - 1);
        return scores[idx];
    }

    /// <summary>
    /// Computes leverage-corrected LOO residuals for jackknife+ coverage intervals.
    /// Uses the OLS hat-matrix formula: LOO_i = e_i / (1 − h_ii), where h_ii = x_i^T (X^T X)^{-1} x_i.
    /// This is an approximation for the Huber-trained magnitude regressor but gives proper
    /// coverage guarantees much closer to true jackknife+ than sorted raw residuals.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (magWeights.Length != F || trainSet.Count == 0) return [];

        // Build X^T X (F×F) with small ridge for numerical stability
        var XtX = new double[F, F];
        foreach (var s in trainSet)
            for (int a = 0; a < F; a++)
                for (int b = 0; b < F; b++)
                    XtX[a, b] += s.Features[a] * s.Features[b];
        for (int a = 0; a < F; a++) XtX[a, a] += 1e-6;

        var XtX_inv = InvertMatrix(XtX, F);

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            var s = trainSet[i];
            double pred = magBias;
            for (int fi = 0; fi < F; fi++) pred += magWeights[fi] * s.Features[fi];
            double e_i = s.Magnitude - pred;

            // h_ii = x_i^T (X^T X)^{-1} x_i
            double h_ii = 0.0;
            for (int a = 0; a < F; a++)
            {
                double v = 0.0;
                for (int b = 0; b < F; b++) v += XtX_inv[a, b] * s.Features[b];
                h_ii += s.Features[a] * v;
            }
            h_ii = Math.Clamp(h_ii, 0.0, 0.999);
            residuals[i] = Math.Abs(e_i / (1.0 - h_ii));
        }
        Array.Sort(residuals);
        return residuals;
    }

    /// <summary>Inverts an n×n matrix using Gauss-Jordan elimination with partial pivoting.</summary>
    private static double[,] InvertMatrix(double[,] A, int n)
    {
        // Build augmented matrix [A | I]
        var aug = new double[n, 2 * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) aug[i, j] = A[i, j];
            aug[i, n + i] = 1.0;
        }

        for (int col = 0; col < n; col++)
        {
            // Partial pivot
            int pivot = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(aug[row, col]) > Math.Abs(aug[pivot, col])) pivot = row;
            if (pivot != col)
                for (int j = 0; j < 2 * n; j++)
                    (aug[col, j], aug[pivot, j]) = (aug[pivot, j], aug[col, j]);

            double diag = aug[col, col];
            if (Math.Abs(diag) < 1e-14) continue; // numerically singular column — skip
            for (int j = 0; j < 2 * n; j++) aug[col, j] /= diag;

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                double factor = aug[row, col];
                for (int j = 0; j < 2 * n; j++) aug[row, j] -= factor * aug[col, j];
            }
        }

        var inv = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                inv[i, j] = aug[i, n + j];
        return inv;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  META-LABEL + ABSTENTION
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        if (calSet.Count < 10) return ([], 0.0);

        // Features: [p, |p-0.5|, p*(1-p), |logit(p)|]
        // — p and |logit| capture different saturations; p*(1-p) adds orthogonal uncertainty signal.
        const int MetaF = 4;
        var w = new double[MetaF];
        double b = 0.0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[MetaF]; double db = 0.0;
            foreach (var s in calSet)
            {
                double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
                double logit  = Math.Log(pClamp / (1.0 - pClamp));
                double[] feat = [p2, Math.Abs(p2 - 0.5), p2 * (1.0 - p2), Math.Abs(logit)];
                bool correct  = (p2 >= 0.5 ? 1 : 0) == s.Direction;
                double y      = correct ? 1.0 : 0.0;
                double dot    = b;
                for (int fi = 0; fi < MetaF; fi++) dot += w[fi] * feat[fi];
                double pred   = Sigmoid(dot);
                double err    = pred - y;
                db += err;
                for (int fi = 0; fi < MetaF; fi++) dw[fi] += err * feat[fi];
            }
            double lr2 = 0.01 / calSet.Count;
            b -= lr2 * db;
            for (int fi = 0; fi < MetaF; fi++) w[fi] -= lr2 * dw[fi];
        }
        return (w, b);
    }

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F,
        bool f1Sweep = false)
    {
        if (calSet.Count < 10) return ([], 0.0, 0.5);

        // Features: [p, |p-0.5|, p*(1-p), |logit(p)|] — same expanded set as meta-label.
        const int AbstF = 4;
        var w = new double[AbstF];
        double b = 0.0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[AbstF]; double db = 0.0;
            foreach (var s in calSet)
            {
                double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
                double logit  = Math.Log(pClamp / (1.0 - pClamp));
                double[] feat = [p2, Math.Abs(p2 - 0.5), p2 * (1.0 - p2), Math.Abs(logit)];
                bool tradeable = Math.Abs(p2 - 0.5) > 0.10; // >60% or <40% confidence = tradeable
                double y      = tradeable ? 1.0 : 0.0;
                double dot    = b;
                for (int fi = 0; fi < AbstF; fi++) dot += w[fi] * feat[fi];
                double pred   = Sigmoid(dot);
                double err    = pred - y;
                db += err;
                for (int fi = 0; fi < AbstF; fi++) dw[fi] += err * feat[fi];
            }
            double lr2 = 0.01 / calSet.Count;
            b -= lr2 * db;
            for (int fi = 0; fi < AbstF; fi++) w[fi] -= lr2 * dw[fi];
        }

        // Compute abstention scores for all cal samples
        var scoredCal = calSet.Select(s =>
        {
            double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
            double logit  = Math.Log(pClamp / (1.0 - pClamp));
            double dot    = b + w[0] * p2 + w[1] * Math.Abs(p2 - 0.5)
                              + w[2] * p2 * (1.0 - p2) + w[3] * Math.Abs(logit);
            return (Score: Sigmoid(dot), Label: s.Direction, Pred: p2 >= 0.5 ? 1 : 0);
        }).OrderBy(x => x.Score).ToArray();

        double thr;
        if (f1Sweep && scoredCal.Length >= 10)
        {
            // Sweep threshold and pick the value that maximises F1 on the cal set.
            double bestF1 = -1.0;
            thr = 0.5;
            for (int ti = 0; ti < scoredCal.Length; ti++)
            {
                double candidate = scoredCal[ti].Score;
                int tp = 0, fp = 0, fn = 0;
                foreach (var (sc, lbl, pred) in scoredCal)
                {
                    if (sc < candidate) continue; // abstain on low-score samples
                    if (pred == 1 && lbl == 1) tp++;
                    else if (pred == 1 && lbl == 0) fp++;
                    else if (pred == 0 && lbl == 1) fn++;
                }
                double prec = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
                double rec  = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
                double f1   = prec + rec > 0 ? 2.0 * prec * rec / (prec + rec) : 0.0;
                if (f1 > bestF1) { bestF1 = f1; thr = candidate; }
            }
        }
        else
        {
            // Default: threshold at the 40th percentile of scores (top 60% pass).
            int tIdx = (int)(scoredCal.Length * 0.40);
            thr = tIdx < scoredCal.Length ? scoredCal[tIdx].Score : 0.5;
        }

        return (w, b, thr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  QUANTILE MAGNITUDE REGRESSOR (pinball loss)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet, int F, double tau, double overrideLr = 0.0)
    {
        var w  = new double[F];
        double b = 0.0;
        double lr2 = overrideLr > 0.0 ? overrideLr : 0.001;

        // Adam moment buffers
        var mw = new double[F]; var vw = new double[F];
        double mb = 0.0, vb = 0.0;
        int step = 0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * s.Features[fi];
                double resid = s.Magnitude - pred;
                double grad  = resid >= 0 ? -tau : (1.0 - tau);
                db += grad;
                for (int fi = 0; fi < F; fi++) dw[fi] += grad * s.Features[fi];
            }
            double n = trainSet.Count;
            for (int fi = 0; fi < F; fi++) dw[fi] /= n;
            db /= n;
            step++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, step);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, step);
            b = AdamScalar(b, db, ref mb, ref vb, lr2, bc1, bc2);
            for (int fi = 0; fi < F; fi++)
            {
                mw[fi] = AdamBeta1 * mw[fi] + (1.0 - AdamBeta1) * dw[fi];
                vw[fi] = AdamBeta2 * vw[fi] + (1.0 - AdamBeta2) * dw[fi] * dw[fi];
                w[fi] -= lr2 * (mw[fi] / bc1) / (Math.Sqrt(vw[fi] / bc2) + AdamEpsilon);
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSOR (Adam + Huber)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet, int F, TrainingHyperparams hp)
    {
        if (trainSet.Count == 0) return (new double[F], 0.0);

        var w  = new double[F]; double b = 0.0;
        var mw = new double[F]; var vw = new double[F];
        double mb = 0.0, vb = 0.0;
        double lr2 = 0.001;
        double delta = 1.0; // Huber delta
        int step = 0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * s.Features[fi];
                double resid = pred - s.Magnitude;
                double grad  = Math.Abs(resid) <= delta ? resid : delta * Math.Sign(resid);
                db += grad;
                for (int fi = 0; fi < F; fi++) dw[fi] += grad * s.Features[fi];
            }
            double n = trainSet.Count;
            for (int fi = 0; fi < F; fi++) dw[fi] /= n;
            db /= n;
            step++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, step);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, step);
            b  = AdamScalar(b,  db,  ref mb, ref vb, lr2, bc1, bc2);
            for (int fi = 0; fi < F; fi++)
            {
                mw[fi] = AdamBeta1 * mw[fi] + (1.0 - AdamBeta1) * dw[fi];
                vw[fi] = AdamBeta2 * vw[fi] + (1.0 - AdamBeta2) * dw[fi] * dw[fi];
                double mHat = mw[fi] / bc1;
                double vHat = vw[fi] / bc2;
                w[fi] -= lr2 * mHat / (Math.Sqrt(vHat) + AdamEpsilon);
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, DannModel model,
        double plattA, double plattB, int F,
        CancellationToken ct)
    {
        if (testSet.Count == 0) return new float[F];

        double baseAcc = testSet.Average(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        });

        var importance = new float[F];
        var rng = new Random(42);

        for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
        {
            double shuffleAcc = 0.0;
            const int Rounds = 3;
            for (int r = 0; r < Rounds; r++)
            {
                var shuffled = testSet.Select(s =>
                {
                    var f2 = (float[])s.Features.Clone();
                    return (Features: f2, s.Direction);
                }).ToList();

                // Shuffle feature fi
                for (int i = shuffled.Count - 1; i > 0; i--)
                {
                    int j2 = rng.Next(i + 1);
                    (shuffled[i].Features[fi], shuffled[j2].Features[fi]) =
                        (shuffled[j2].Features[fi], shuffled[i].Features[fi]);
                }

                shuffleAcc += shuffled.Average(s =>
                {
                    double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                    return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
                });
            }
            importance[fi] = (float)Math.Max(0.0, baseAcc - shuffleAcc / Rounds);
        }

        // Normalise to sum to 1
        float impSum = importance.Sum();
        if (impSum > 0) for (int fi = 0; fi < F; fi++) importance[fi] /= impSum;

        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet, DannModel model, int F, CancellationToken ct)
    {
        if (calSet.Count == 0) return new double[F];

        double baseAcc = calSet.Average(s =>
        {
            double p2 = ForwardCls(model, s.Features);
            return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        });

        var importance = new double[F];
        var rng = new Random(99);

        for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
        {
            var shuffled = calSet.Select(s => ((float[])s.Features.Clone(), s.Direction)).ToList();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j2 = rng.Next(i + 1);
                (shuffled[i].Item1[fi], shuffled[j2].Item1[fi]) =
                    (shuffled[j2].Item1[fi], shuffled[i].Item1[fi]);
            }
            double shuffleAcc = shuffled.Average(s =>
            {
                double p2 = ForwardCls(model, s.Item1);
                return (p2 >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0;
            });
            importance[fi] = Math.Max(0.0, baseAcc - shuffleAcc);
        }
        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FEATURE PRUNING + MASKING
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double minImportance, int F)
    {
        var mask = new bool[F];
        if (minImportance <= 0.0) { Array.Fill(mask, true); return mask; }
        double equalShare = 1.0 / F;
        for (int fi = 0; fi < F; fi++)
            mask[fi] = importance[fi] >= minImportance * equalShare;
        // Never prune everything
        if (mask.All(m2 => !m2)) Array.Fill(mask, true);
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        int activeF = mask.Count(m2 => m2);
        return samples.Select(s =>
        {
            var f2 = new float[activeF];
            int idx = 0;
            for (int fi = 0; fi < mask.Length; fi++) if (mask[fi]) f2[idx++] = s.Features[fi];
            return s with { Features = f2 };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DECISION BOUNDARY + DURBIN-WATSON + MI REDUNDANCY
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        // Approximate boundary distance as |p - 0.5| normalised by variance
        var dists = calSet.Select(s =>
        {
            double p2 = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            return Math.Abs(p2 - 0.5) * 2.0;
        }).ToList();
        double mean = dists.Average();
        double std2 = dists.Count > 1
            ? Math.Sqrt(dists.Sum(d => (d - mean) * (d - mean)) / (dists.Count - 1))
            : 0.0;
        return (mean, std2);
    }

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (trainSet.Count < 3 || magWeights.Length != F) return 2.0;
        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int fi = 0; fi < F; fi++) pred += magWeights[fi] * trainSet[i].Features[fi];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double num = 0.0, den = 0.0;
        for (int i = 1; i < residuals.Length; i++) num += (residuals[i] - residuals[i - 1]) * (residuals[i] - residuals[i - 1]);
        for (int i = 0; i < residuals.Length; i++) den += residuals[i] * residuals[i];
        return den > 1e-9 ? num / den : 2.0;
    }

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 20 || F < 2) return [];
        int bins = (int)Math.Ceiling(1.0 + Math.Log2(trainSet.Count)); // Sturges' rule
        var pairs = new List<string>();

        for (int a = 0; a < F - 1; a++)
        for (int b2 = a + 1; b2 < F; b2++)
        {
            double mi = ComputeMutualInfo(trainSet, a, b2, bins);
            if (mi > threshold)
                pairs.Add($"{MLFeatureHelper.FeatureNames[a]}:{MLFeatureHelper.FeatureNames[b2]}");
        }
        return pairs.ToArray();
    }

    private static double ComputeMutualInfo(List<TrainingSample> samples, int a, int b2, int bins)
    {
        double minA = double.MaxValue, maxA = double.MinValue;
        double minB = double.MaxValue, maxB = double.MinValue;
        foreach (var s in samples)
        {
            if (s.Features[a] < minA) minA = s.Features[a];
            if (s.Features[a] > maxA) maxA = s.Features[a];
            if (s.Features[b2] < minB) minB = s.Features[b2];
            if (s.Features[b2] > maxB) maxB = s.Features[b2];
        }
        if (maxA <= minA || maxB <= minB) return 0.0;
        double wA = (maxA - minA) / bins, wB = (maxB - minB) / bins;
        int n = samples.Count;
        var joint = new int[bins, bins]; var mA = new int[bins]; var mB = new int[bins];
        foreach (var s in samples)
        {
            int bA = Math.Min(bins - 1, (int)((s.Features[a]  - minA) / wA));
            int bB = Math.Min(bins - 1, (int)((s.Features[b2] - minB) / wB));
            joint[bA, bB]++; mA[bA]++; mB[bB]++;
        }
        double mi = 0.0;
        for (int i = 0; i < bins; i++)
        for (int j = 0; j < bins; j++)
        {
            if (joint[i, j] == 0) continue;
            double pij = (double)joint[i, j] / n;
            double pi  = (double)mA[i] / n;
            double pj  = (double)mB[j] / n;
            mi += pij * Math.Log(pij / (pi * pj));
        }
        return mi;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  STATIONARITY GATE (variance-ratio proxy ADF)
    // ═══════════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int count = 0;
        for (int fi = 0; fi < F; fi++)
        {
            double prevMean = 0.0, currMean = 0.0;
            int half = samples.Count / 2;
            for (int i = 0;    i < half;           i++) prevMean += samples[i].Features[fi];
            for (int i = half; i < samples.Count;  i++) currMean += samples[i].Features[fi];
            prevMean /= Math.Max(1, half);
            currMean /= Math.Max(1, samples.Count - half);
            double prevVar = 0.0, currVar = 0.0;
            for (int i = 0;    i < half;          i++) { double d = samples[i].Features[fi] - prevMean; prevVar += d * d; }
            for (int i = half; i < samples.Count; i++) { double d = samples[i].Features[fi] - currMean; currVar += d * d; }
            prevVar /= Math.Max(1, half - 1);
            currVar /= Math.Max(1, samples.Count - half - 1);
            // Non-stationary proxy: variance ratio > 3 or mean shift > 2 std devs
            double baseStd = Math.Sqrt(Math.Max(prevVar, 1e-12));
            if (prevVar > 0 && currVar / prevVar > 3.0)     count++;
            else if (Math.Abs(currMean - prevMean) > 2.0 * baseStd) count++;
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO + COVARIATE-SHIFT WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int F, int recentWindowDays, int barsPerDay = 24)
    {
        int recentCount = Math.Min(trainSet.Count / 2,
            Math.Max(10, recentWindowDays * barsPerDay));
        int n = trainSet.Count;

        // Train a logistic discriminator: label=1 for "recent", 0 for "historical"
        var w = new double[F]; double b = 0.0;
        for (int iter = 0; iter < 50; iter++)
        {
            var dw = new double[F]; double db = 0.0;
            for (int i = 0; i < n; i++)
            {
                double y  = i >= n - recentCount ? 1.0 : 0.0;
                double pred = b;
                for (int fi = 0; fi < F; fi++) pred += w[fi] * trainSet[i].Features[fi];
                double p  = Sigmoid(pred);
                double err = p - y;
                db += err;
                for (int fi = 0; fi < F; fi++) dw[fi] += err * trainSet[i].Features[fi];
            }
            double lr2 = 0.01 / n;
            b -= lr2 * db;
            for (int fi = 0; fi < F; fi++) w[fi] -= lr2 * dw[fi];
        }

        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = b;
            for (int fi = 0; fi < F; fi++) pred += w[fi] * trainSet[i].Features[fi];
            double p = Math.Clamp(Sigmoid(pred), 0.05, 0.95);
            weights[i] = p / (1.0 - p);
        }
        // Clip to [0.1, 10] to prevent extreme upweighting
        for (int i = 0; i < n; i++) weights[i] = Math.Clamp(weights[i], 0.1, 10.0);
        return weights;
    }

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBreakpoints, int F)
    {
        int n = trainSet.Count;
        var weights = new double[n];
        Array.Fill(weights, 1.0);

        int usedF = Math.Min(F, parentBreakpoints.Length);
        for (int i = 0; i < n; i++)
        {
            double novelty = 0.0;
            for (int fi = 0; fi < usedF; fi++)
            {
                var bp = parentBreakpoints[fi];
                if (bp is not { Length: > 0 }) continue;
                double val = trainSet[i].Features[fi];
                if (val < bp[0] || val > bp[^1]) novelty += 1.0;
            }
            weights[i] = 1.0 + novelty / Math.Max(1, usedF);
        }
        // Normalise
        double wMax = weights.Max();
        if (wMax > 0) for (int i = 0; i < n; i++) weights[i] /= wMax;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TEMPORAL DECAY WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int n, double lambda)
    {
        var weights = new double[n];
        if (lambda <= 0)
        {
            Array.Fill(weights, 1.0);
            return weights;
        }
        for (int i = 0; i < n; i++)
            weights[i] = Math.Exp(-lambda * (n - 1 - i));
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WEIGHT SANITISATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static int SanitizeWeights(DannModel model)
    {
        int count = 0;
        count += SanitizeMatrix(model.WFeat);
        count += SanitizeVector(model.bFeat);
        count += SanitizeMatrix(model.WFeat2);
        count += SanitizeVector(model.bFeat2);
        count += SanitizeVector(model.wCls);
        if (!double.IsFinite(model.bCls)) { model.bCls = 0.0; count++; }
        count += SanitizeMatrix(model.WDom1);
        count += SanitizeVector(model.bDom1);
        count += SanitizeVector(model.wDom2);
        if (!double.IsFinite(model.bDom2)) { model.bDom2 = 0.0; count++; }
        return count;
    }

    private static int SanitizeMatrix(double[][] m)
    {
        int count = 0;
        for (int i = 0; i < m.Length; i++) count += SanitizeVector(m[i]);
        return count;
    }

    private static int SanitizeVector(double[] v)
    {
        int count = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if (!double.IsFinite(v[i])) { v[i] = 0.0; count++; }
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN WEIGHTS SNAPSHOT SERIALISATION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Packs all DANN layer weights into a jagged array for snapshot serialisation (v4 format):
    /// rows 0..featDim-1         = WFeat layer-1   (each row: F weights + bias)
    /// rows featDim..2*featDim-1 = WFeat2 layer-2  (each row: featDim weights + bias)
    /// row  2*featDim            = wCls + bCls appended
    /// rows 2*featDim+1..2*featDim+domHid = WDom1  (each row: featDim weights + bias)
    /// row  2*featDim+domHid+1   = wDom2 + bDom2 appended
    /// </summary>
    private static double[][] ExtractDannWeightsForSnapshot(DannModel model)
    {
        int totalRows = 2 * model.featDim + 1 + model.domHid + 1;
        var result    = new double[totalRows][];

        // Feature extractor layer-1 rows
        for (int j = 0; j < model.featDim; j++)
        {
            result[j] = new double[model.F + 1];
            Array.Copy(model.WFeat[j], result[j], model.F);
            result[j][model.F] = model.bFeat[j];
        }

        // Feature extractor layer-2 rows
        for (int j = 0; j < model.featDim; j++)
        {
            int row = model.featDim + j;
            result[row] = new double[model.featDim + 1];
            Array.Copy(model.WFeat2[j], result[row], model.featDim);
            result[row][model.featDim] = model.bFeat2[j];
        }

        // Label classifier row (wCls + bCls)
        int clsRow = 2 * model.featDim;
        result[clsRow] = new double[model.featDim + 1];
        Array.Copy(model.wCls, result[clsRow], model.featDim);
        result[clsRow][model.featDim] = model.bCls;

        // Domain layer-1 rows
        for (int k = 0; k < model.domHid; k++)
        {
            int row = 2 * model.featDim + 1 + k;
            result[row] = new double[model.featDim + 1];
            Array.Copy(model.WDom1[k], result[row], model.featDim);
            result[row][model.featDim] = model.bDom1[k];
        }

        // Domain layer-2 row (wDom2 + bDom2)
        int lastRow = 2 * model.featDim + 1 + model.domHid;
        result[lastRow] = new double[model.domHid + 1];
        Array.Copy(model.wDom2, result[lastRow], model.domHid);
        result[lastRow][model.domHid] = model.bDom2;

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MATHS HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x) =>
        x >= 0 ? 1.0 / (1.0 + Math.Exp(-x)) : Math.Exp(x) / (1.0 + Math.Exp(x));

    private static double AdamScalar(
        double param, double grad,
        ref double m, ref double v,
        double lr, double bc1, double bc2)
    {
        m = AdamBeta1 * m + (1.0 - AdamBeta1) * grad;
        v = AdamBeta2 * v + (1.0 - AdamBeta2) * grad * grad;
        return param - lr * (m / bc1) / (Math.Sqrt(v / bc2) + AdamEpsilon);
    }

    private static double Std(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double mean = values.Average();
        double sumSq = values.Sum(x => (x - mean) * (x - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    private static double ComputeLinearSlope(List<double> values)
    {
        if (values.Count < 2) return 0.0;
        double n    = values.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < values.Count; i++)
        {
            sumX  += i; sumY  += values[i];
            sumXY += i * values[i]; sumX2 += i * i;
        }
        double denom = n * sumX2 - sumX * sumX;
        return Math.Abs(denom) > 1e-9 ? (n * sumXY - sumX * sumY) / denom : 0.0;
    }
}
