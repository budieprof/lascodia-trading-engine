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
public sealed partial class DannModelTrainer : IMLModelTrainer
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

        // ── 3. Final splits: 60% train | 10% selection | 10% cal | ~20% test ─
        int trainEnd     = (int)(allStd.Count * 0.60);
        int selectionEnd = (int)(allStd.Count * 0.70);
        int calEnd       = (int)(allStd.Count * 0.80);
        int embargo      = hp.EmbargoBarCount;

        var trainSet     = allStd[..Math.Max(0, trainEnd - embargo)];
        var selectionSet = allStd[(selectionEnd > trainEnd ? trainEnd + embargo : trainEnd)
                                   ..(selectionEnd < allStd.Count ? selectionEnd : allStd.Count)];
        var calSet       = allStd[(calEnd > selectionEnd ? selectionEnd + embargo : selectionEnd)
                                   ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet      = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"DANN: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, epochs / 2), LearningRate = lr / 3.0 }
            : hp;

        _logger.LogInformation(
            "DANN: n={N} F={F} featDim={FD} domHid={DH} train={Train} sel={Sel} cal={Cal} test={Test} embargo={Embargo}",
            allStd.Count, F, featDim, domHid, trainSet.Count, selectionSet.Count, calSet.Count, testSet.Count, embargo);

        // ── 3b. Multi-signal drift diagnostics ──────────────────────────────────
        var driftArtifact = ComputeDannDriftDiagnostics(trainSet, F, MLFeatureHelper.FeatureNames, hp.FracDiffD);
        _logger.LogInformation(
            "DANN drift diagnostics: {Flagged}/{Total} features non-stationary ({Action})",
            driftArtifact.NonStationaryFeatureCount, driftArtifact.TotalFeatureCount, driftArtifact.GateAction);
        if (driftArtifact.GateAction == "REJECT" && hp.FracDiffD == 0.0)
        {
            _logger.LogWarning(
                "DANN drift gate REJECT: {Frac:P0} features flagged. Aborting — set FracDiffD > 0.",
                driftArtifact.NonStationaryFraction);
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        }

        // ── 3b2. Class-imbalance gate ────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"DANN: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("DANN class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3b3. Adversarial validation ───────────────────────────────────────
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, F, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, F);
            _logger.LogInformation("DANN adversarial AUC={AUC:F3}", advAuc);
            if (advAuc > 0.65) _logger.LogWarning("DANN adversarial AUC={AUC:F3} indicates covariate shift.", advAuc);
            if (hp.DannMaxAdversarialAuc > 0.0 && advAuc > hp.DannMaxAdversarialAuc)
                throw new InvalidOperationException($"DANN: adversarial AUC={advAuc:F3} exceeds threshold.");
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
            selectionSet, model, plattA, plattB, F, hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("DANN EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 11. Permutation feature importance ────────────────────────────────
        var featureImportance = selectionSet.Count >= 10
            ? ComputePermutationImportance(selectionSet, model, plattA, plattB, F, ct)
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
            var maskedCal       = ApplyMask(calSet,       activeMask);
            var maskedTest      = ApplyMask(testSet,      activeMask);
            var maskedSelection = ApplyMask(selectionSet, activeMask);

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
                trainSet     = maskedTrain;     // downstream Jackknife/PSI/DW use masked features
                testSet      = maskedTest;      // downstream BSS uses masked features
                selectionSet = maskedSelection; // downstream threshold/importance use masked features
                ece          = ComputeEce(maskedTest, model, plattA, plattB, F);
                optimalThreshold = ComputeOptimalThreshold(selectionSet, model, plattA, plattB, F,
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

        // ── 25a. Calibrated probability function ────────────────────────────
        Func<float[], double> CalibratedProb = feats =>
            ApplyPlatt(ForwardCls(model, feats), plattA, plattB);

        // ── 25b. Reliability diagram ──────────────────────────────────────────
        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(testSet, CalibratedProb);
        var (murphyCalLoss, murphyRefLoss) = ComputeMurphyDecomposition(testSet, CalibratedProb);
        var (calResidualMean, calResidualStd, calResidualThreshold) =
            ComputeCalibrationResidualStats(calSet, CalibratedProb);
        double predictionStability = ComputePredictionStabilityScore(testSet, CalibratedProb);
        double[] featureVariances = ComputeFeatureVariances(trainSet, F);
        var warmStartArtifact = BuildDannWarmStartArtifact(warmStart, featDim, domHid, F);

        // ── 25c. Scalar sanitization ──────────────────────────────────────────
        ece = SafeDann(ece, 1.0);
        optimalThreshold = SafeDann(optimalThreshold, 0.5);
        brierSkillScore = SafeDann(brierSkillScore, 0.0);
        avgKellyFraction = SafeDann(avgKellyFraction, 0.0);
        temperatureScale = SafeDann(temperatureScale, 0.0);
        durbinWatson = SafeDann(durbinWatson, 2.0);
        dbMean = SafeDann(dbMean, 0.0);
        dbStd = SafeDann(dbStd, 0.0);
        plattA = SafeDann(plattA, 1.0);
        plattB = SafeDann(plattB, 0.0);
        calResidualMean = SafeDann(calResidualMean, 0.0);
        calResidualStd = SafeDann(calResidualStd, 0.0);
        calResidualThreshold = SafeDann(calResidualThreshold, 1.0);
        predictionStability = SafeDann(predictionStability, 0.0);
        murphyCalLoss = SafeDann(murphyCalLoss, 0.0);
        murphyRefLoss = SafeDann(murphyRefLoss, 0.0);

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
            // Hardening: reliability, Murphy, calibration residuals, stability, drift, warm-start
            ReliabilityBinConfidence    = reliabilityBinConf,
            ReliabilityBinAccuracy      = reliabilityBinAcc,
            ReliabilityBinCounts        = reliabilityBinCounts,
            CalibrationLoss             = murphyCalLoss,
            RefinementLoss              = murphyRefLoss,
            PredictionStabilityScore    = predictionStability,
            FeatureVariances            = featureVariances,
            DannDriftArtifact           = driftArtifact,
            DannWarmStartArtifact       = warmStartArtifact,
            DannCalibrationResidualMean      = calResidualMean,
            DannCalibrationResidualStd       = calResidualStd,
            DannCalibrationResidualThreshold = calResidualThreshold,
        };

        // ── Snapshot array sanitization ───────────────────────────────────────
        SanitizeDannSnapshotArrays(snapshot);

        // ── Train/inference parity audit ──────────────────────────────────────
        var auditResult = RunDannAudit(snapshot, testSet.Count > 0 ? testSet : calSet);
        snapshot.DannAuditArtifact = auditResult.Artifact;

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "DannModelTrainer complete: accuracy={Acc:P1} brier={B:F4} BSS={BSS:F4} snapshotBytes={Bytes}",
            finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore, modelBytes.Length);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
