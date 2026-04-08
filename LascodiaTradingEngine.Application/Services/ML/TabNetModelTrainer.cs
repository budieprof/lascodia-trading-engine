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
///   <item>Final splits: 60% train | 10% selection | 10% calibration | ~20% test with embargo.</item>
///   <item>Stationarity gate, density-ratio weights, covariate-shift weights, adaptive label smoothing.</item>
///   <item>Adam-optimised true TabNet with Ghost BN, sparsemax, GLU, cosine LR, gradient clipping, early stopping.</item>
///   <item>Weight sanitization.</item>
///   <item>Global Platt vs temperature selection, then class-conditional Platt, then isotonic calibration.</item>
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
    private const double DefaultHuberDelta = 1.0;
    private const double MaxWeightVal = 10.0;
    private const double MaxInvStd    = 1e4;     // prevent gradient explosion from near-zero BN variance
    private const double SqrtHalfResidualScale = 0.7071067811865476; // 1/√2
    private const double Eps          = 1e-15;    // zero guard for log/division
    private const double EarlyStopMinDelta = 1e-6;
    private const double ProbClampMin = 1e-7;
    private const int    DefaultBatchSize = 32;
    private const int    MeanAttentionSampleCap = 500;
    private const int    DefaultCalibrationEpochs = 200;
    private const double DefaultCalibrationLr = 0.01;
    private const int    DefaultMinCalibrationSamples = 10;
    private const double DefaultDensityRatioLr = 0.01;
    private const int    TrainerSeed = 42;

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

        var runContext = new TabNetRunContext
        {
            HuberDelta = hp.TabNetHuberDelta > 0 ? hp.TabNetHuberDelta : DefaultHuberDelta,
            CalibrationEpochs = hp.TabNetCalibrationEpochs > 0 ? hp.TabNetCalibrationEpochs : DefaultCalibrationEpochs,
            CalibrationLr = hp.TabNetCalibrationLr > 0 ? hp.TabNetCalibrationLr : DefaultCalibrationLr,
            MinCalibrationSamples = hp.TabNetMinCalibrationSamples > 0 ? hp.TabNetMinCalibrationSamples : DefaultMinCalibrationSamples,
        };

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNetModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        int F       = samples[0].Features.Length;

        // Validate all samples have consistent feature length
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Features.Length != F)
                throw new InvalidOperationException(
                    $"TabNetModelTrainer: sample {i} has {samples[i].Features.Length} features, expected {F}. " +
                    "All samples must have the same feature count.");
        }
        int nSteps  = hp.TabNetSteps > 0 ? hp.TabNetSteps : (hp.K > 0 ? hp.K : 3);
        double lr   = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        int epochs  = hp.MaxEpochs > 0 ? hp.MaxEpochs : 50;
        int hiddenDim    = hp.TabNetHiddenDim > 0 ? hp.TabNetHiddenDim : Math.Max(8, 8 * nSteps);
        int sharedLayers = hp.TabNetSharedLayers > 0 ? hp.TabNetSharedLayers : 2;
        int stepLayers   = hp.TabNetStepLayers > 0 ? hp.TabNetStepLayers : 2;
        int attentionDim = hp.TabNetAttentionDim > 0 ? Math.Clamp(hp.TabNetAttentionDim, 1, hiddenDim) : hiddenDim;
        double gamma     = Math.Clamp(hp.TabNetRelaxationGamma > 0 ? hp.TabNetRelaxationGamma : 1.5, 1.0, 2.0);
        double sparsityCoeff = hp.TabNetSparsity > 0 ? hp.TabNetSparsity : 0.0001;
        bool useSparsemax = hp.TabNetUseSparsemax;
        bool useGlu = hp.TabNetUseGlu;
        double dropoutRate = hp.TabNetDropoutRate;
        double bnMomentum  = hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98;
        int ghostBatchSize = hp.TabNetGhostBatchSize > 0 ? hp.TabNetGhostBatchSize : 128;
        string[] rawTabNetFeatureNames = BuildTabNetFeatureNames(F, TabNetFeatureExpansionPlan.Empty);
        string featureSchemaFingerprint = TabNetSnapshotSupport.ComputeFeatureSchemaFingerprint(rawTabNetFeatureNames, F);
        var warmStartCompatibility = new TabNetSnapshotSupport.CompatibilityResult(true, []);

        if (warmStart is not null && string.Equals(warmStart.Type, ModelType, StringComparison.OrdinalIgnoreCase))
        {
            warmStartCompatibility = TabNetSnapshotSupport.AssessWarmStartCompatibility(
                warmStart, featureSchemaFingerprint, string.Empty);
            if (!warmStartCompatibility.IsCompatible)
            {
                _logger.LogWarning(
                    "TabNet warm-start snapshot failed schema compatibility and will be ignored: {Issues}",
                    string.Join("; ", warmStartCompatibility.Issues));
                warmStart = null;
            }
        }

        if (warmStart is not null && string.Equals(warmStart.Type, ModelType, StringComparison.OrdinalIgnoreCase))
        {
            warmStart = TabNetSnapshotSupport.NormalizeSnapshotCopy(warmStart);
            var warmStartValidation = TabNetSnapshotSupport.ValidateNormalizedSnapshot(warmStart, allowLegacyV2: false);
            if (!warmStartValidation.IsValid)
            {
                warmStartCompatibility = new TabNetSnapshotSupport.CompatibilityResult(false, warmStartValidation.Issues);
                _logger.LogWarning(
                    "TabNet warm-start snapshot failed validation and will be ignored: {Issues}",
                    string.Join("; ", warmStartValidation.Issues));
                warmStart = null;
            }
        }

        if (hp.FracDiffD > 0.0)
            _logger.LogWarning(
                "TabNet requested FracDiffD={FracDiffD:F3}, but this trainer does not apply fractional differencing during training. Persisting FracDiffD=0 to keep inference aligned with the fitted model.",
                hp.FracDiffD);

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
        FeatureTransformDescriptor[] featurePipelineDescriptors = [];
        int origF = F;
        var featureExpansionPlan = TabNetFeatureExpansionPlan.Empty;
        if (hp.PolyLearnerFraction > 0.0)
        {
            const int PolyTopN = 5;
            featureExpansionPlan = BuildFeatureExpansionPlan(allStd, F, warmStart, PolyTopN, maxTerms: 10);
            if (featureExpansionPlan.IsEnabled)
            {
                var augmentedStd = AugmentSamplesWithPoly(allStd, F, featureExpansionPlan);
                int expandedFeatureCount = origF + featureExpansionPlan.AddedFeatureCount;
                bool acceptExpansion = ShouldAcceptFeatureExpansion(
                    allStd, augmentedStd, hp, origF, expandedFeatureCount, nSteps, hiddenDim, attentionDim,
                    sharedLayers, stepLayers, gamma, useSparsemax, useGlu, lr, sparsityCoeff, bnMomentum, runContext, ct);

                if (acceptExpansion)
                {
                    allStd = augmentedStd;
                    F = expandedFeatureCount;
                    _logger.LogInformation(
                        "TabNet feature expansion accepted: top-{N} raw features -> {Added} replayable product terms, F {Old}->{New}",
                        featureExpansionPlan.TopFeatureIndices.Length, featureExpansionPlan.AddedFeatureCount, origF, F);
                }
                else
                {
                    featureExpansionPlan = TabNetFeatureExpansionPlan.Empty;
                }
            }
        }
        polyTopIdx = featureExpansionPlan.TopFeatureIndices;
        featurePipelineDescriptors = TabNetSnapshotSupport.BuildFeaturePipelineDescriptors(origF, featureExpansionPlan.ProductTerms);
        string[] tabNetFeatureNames = BuildTabNetFeatureNames(origF, featureExpansionPlan);
        featureSchemaFingerprint = TabNetSnapshotSupport.ComputeFeatureSchemaFingerprint(tabNetFeatureNames, origF);
        string[] featurePipelineTransforms = featurePipelineDescriptors.Length > 0
            ? featurePipelineDescriptors.Select(d => d.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : TabNetSnapshotSupport.BuildFeaturePipelineTransforms(polyTopIdx);
        var (snapshotMeans, snapshotStds) = BuildTabNetSnapshotStats(means, stds, origF, featureExpansionPlan);
        string preprocessingFingerprint = TabNetSnapshotSupport.ComputePreprocessingFingerprint(origF, featurePipelineDescriptors, null);
        string trainerFingerprint = string.Empty;

        if (warmStart is not null && string.Equals(warmStart.Type, ModelType, StringComparison.OrdinalIgnoreCase))
        {
            var preprocessingCompatibility = TabNetSnapshotSupport.AssessWarmStartCompatibility(
                warmStart, featureSchemaFingerprint, preprocessingFingerprint);
            if (!preprocessingCompatibility.IsCompatible)
            {
                warmStartCompatibility = preprocessingCompatibility;
                _logger.LogWarning(
                    "TabNet warm-start snapshot failed preprocessing compatibility and will be ignored for weight reuse: {Issues}",
                    string.Join("; ", preprocessingCompatibility.Issues));
                warmStart = null;
            }
        }

        // ── 1c. Optional unsupervised pre-training ─────────────────────────
        TabNetWeights? pretrainedWeights = null;
        int pretrainEpochs = hp.TabNetPretrainEpochs;
        if (pretrainEpochs > 0 && allStd.Count >= hp.MinSamples * 2)
        {
            double maskFrac = hp.TabNetPretrainMaskFraction > 0 ? hp.TabNetPretrainMaskFraction : 0.3;

            // GPU pre-training when CUDA available, with CPU fallback
            if (allStd.Count >= GpuMinSamples && IsGpuAvailable())
            {
                try
                {
                    pretrainedWeights = RunUnsupervisedPretrainingGpu(
                        allStd, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                        gamma, useSparsemax, useGlu, lr, pretrainEpochs, maskFrac, bnMomentum, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TabNet GPU pre-training failed, falling back to CPU: {Message}", ex.Message);
                }
            }

            pretrainedWeights ??= RunUnsupervisedPretraining(
                allStd, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, pretrainEpochs, maskFrac, bnMomentum, ct);
            _logger.LogInformation("TabNet pre-training complete ({Epochs} epochs, mask={Mask:P0})",
                pretrainEpochs, maskFrac);
        }

        // ── 2. Walk-forward cross-validation ───────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(
            allStd, hp, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, lr, sparsityCoeff, epochs, bnMomentum, runContext, ct);
        _logger.LogInformation(
            "TabNet Walk-forward CV \u2014 folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        if (cvResult.FoldCount == 0)
            throw new InvalidOperationException("TabNet walk-forward CV produced no usable folds; training aborted.");

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 60% train | 10% selection | 10% calibration | ~20% test ──
        int totalCount = allStd.Count;
        int embargo = hp.EmbargoBarCount;
        int boundaryGapCount = embargo * 3;
        int usableCount = totalCount - boundaryGapCount;
        int minimumRequiredUsable =
            hp.MinSamples +
            runContext.MinCalibrationSamples +
            runContext.MinCalibrationSamples +
            runContext.MinCalibrationSamples;
        if (usableCount < minimumRequiredUsable)
        {
            throw new InvalidOperationException(
                $"TabNet: insufficient samples ({totalCount}) for train/selection/calibration/test splits with embargo={embargo}. " +
                $"Need at least {minimumRequiredUsable + boundaryGapCount} samples.");
        }

        int extraUsable = usableCount - minimumRequiredUsable;
        int trainCount = hp.MinSamples + (int)Math.Floor(extraUsable * 0.60);
        int selectionCount = runContext.MinCalibrationSamples + (int)Math.Floor(extraUsable * 0.10);
        int calibrationCount = runContext.MinCalibrationSamples + (int)Math.Floor(extraUsable * 0.10);
        int testCount = runContext.MinCalibrationSamples + extraUsable - (trainCount - hp.MinSamples) -
            (selectionCount - runContext.MinCalibrationSamples) - (calibrationCount - runContext.MinCalibrationSamples);

        int trainEnd = trainCount;
        int selectionStart = Math.Min(totalCount, trainEnd + embargo);
        int selectionEnd = Math.Min(totalCount, selectionStart + selectionCount);
        int calibrationStart = Math.Min(totalCount, selectionEnd + embargo);
        int calibrationEnd = Math.Min(totalCount, calibrationStart + calibrationCount);
        int testStart = Math.Min(totalCount, calibrationEnd + embargo);

        int rawTrainCount = Math.Min(totalCount, trainCount + embargo);
        int rawSelectionCount = Math.Min(Math.Max(0, totalCount - rawTrainCount), selectionCount + embargo);
        int rawCalibrationCount = Math.Min(Math.Max(0, totalCount - rawTrainCount - rawSelectionCount), calibrationCount + embargo);
        int rawTestCount = Math.Max(0, totalCount - rawTrainCount - rawSelectionCount - rawCalibrationCount);

        var trainSet = allStd[..trainEnd];
        var selectionSet = allStd[selectionStart..selectionEnd];
        var calibrationSet = allStd[calibrationStart..calibrationEnd];
        var testSet = allStd[testStart..];
        var rawAuditSet = samples[Math.Min(testStart, samples.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");
        if (selectionSet.Count < runContext.MinCalibrationSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient selection samples after embargo: {selectionSet.Count} < {runContext.MinCalibrationSamples}");
        if (calibrationSet.Count < runContext.MinCalibrationSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient calibration samples after embargo: {calibrationSet.Count} < {runContext.MinCalibrationSamples}");
        if (testSet.Count < runContext.MinCalibrationSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient test samples after embargo: {testSet.Count} < {runContext.MinCalibrationSamples}");

        int minConformalSamples = Math.Max(5, runContext.MinCalibrationSamples / 2);
        int minAdaptiveHeadSamples = Math.Max(5, runContext.MinCalibrationSamples / 2);
        var calibrationFitSet = calibrationSet;
        var calibrationDiagnosticsSet = calibrationSet;
        int calibrationFitStart = calibrationStart;
        int calibrationDiagnosticsStart = calibrationStart;
        if (calibrationSet.Count >= runContext.MinCalibrationSamples * 2)
        {
            int desiredDiagnosticsCount = Math.Max(
                runContext.MinCalibrationSamples,
                minConformalSamples + minAdaptiveHeadSamples + minAdaptiveHeadSamples);
            desiredDiagnosticsCount = Math.Min(
                desiredDiagnosticsCount,
                calibrationSet.Count - runContext.MinCalibrationSamples);
            int calibrationFitCount = Math.Max(
                runContext.MinCalibrationSamples,
                calibrationSet.Count - desiredDiagnosticsCount);
            calibrationFitCount = Math.Min(calibrationFitCount, calibrationSet.Count - runContext.MinCalibrationSamples);
            calibrationFitSet = calibrationSet[..calibrationFitCount];
            calibrationDiagnosticsSet = calibrationSet[calibrationFitCount..];
            calibrationDiagnosticsStart = calibrationStart + calibrationFitCount;
        }

        var conformalSet = calibrationDiagnosticsSet;
        var metaLabelSet = calibrationDiagnosticsSet;
        var abstentionSet = calibrationDiagnosticsSet;
        int conformalStart = calibrationDiagnosticsStart;
        int metaLabelStart = calibrationDiagnosticsStart;
        int abstentionStart = calibrationDiagnosticsStart;
        string adaptiveHeadSplitMode = "SHARED_FALLBACK";
        int adaptiveHeadCrossFitFoldCount = 0;
        if (calibrationDiagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples + minAdaptiveHeadSamples)
        {
            int extraAdaptiveCount = calibrationDiagnosticsSet.Count -
                (minConformalSamples + minAdaptiveHeadSamples + minAdaptiveHeadSamples);
            int conformalCount = minConformalSamples + extraAdaptiveCount / 3;
            int metaLabelCount = minAdaptiveHeadSamples + extraAdaptiveCount / 3;
            int abstentionCount = minAdaptiveHeadSamples + extraAdaptiveCount - (extraAdaptiveCount / 3) - (extraAdaptiveCount / 3);

            conformalSet = calibrationDiagnosticsSet[..conformalCount];
            metaLabelSet = calibrationDiagnosticsSet[conformalCount..(conformalCount + metaLabelCount)];
            abstentionSet = calibrationDiagnosticsSet[(conformalCount + metaLabelCount)..];
            metaLabelStart = calibrationDiagnosticsStart + conformalCount;
            abstentionStart = metaLabelStart + metaLabelCount;
            adaptiveHeadSplitMode = "DISJOINT";
        }
        else if (calibrationDiagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples)
        {
            conformalSet = calibrationDiagnosticsSet[..minConformalSamples];
            metaLabelSet = calibrationDiagnosticsSet[minConformalSamples..];
            abstentionSet = metaLabelSet;
            metaLabelStart = calibrationDiagnosticsStart + minConformalSamples;
            abstentionStart = metaLabelStart;
            adaptiveHeadSplitMode = "CONFORMAL_DISJOINT_SHARED_ADAPTIVE";
        }

        _logger.LogInformation(
            "TabNet final splits — train={Train} selection={Selection} calibrationFit={CalibrationFit} calibrationDiagnostics={CalibrationDiagnostics} conformal={Conformal} meta={Meta} abstention={Abstention} test={Test} embargo={Embargo}",
            trainSet.Count, selectionSet.Count, calibrationFitSet.Count, calibrationDiagnosticsSet.Count,
            conformalSet.Count, metaLabelSet.Count, abstentionSet.Count, testSet.Count, embargo);

        // ── 3b. Stationarity gate ──────────────────────────────────────────
        TabNetDriftArtifact driftArtifact;
        {
            driftArtifact = ComputeDriftDiagnostics(trainSet, F, tabNetFeatureNames, hp.FracDiffD);
            if (driftArtifact.GateTriggered)
                _logger.LogWarning(
                    "TabNet stationarity gate: {NonStat}/{Total} features flagged (acf={Acf:F3}, psi={Psi:F3}, cp={Cp:F3}). Top flagged: {Features}",
                    driftArtifact.NonStationaryFeatureCount,
                    F,
                    driftArtifact.MeanLag1Autocorrelation,
                    driftArtifact.MeanPopulationStabilityIndex,
                    driftArtifact.MeanChangePointScore,
                    driftArtifact.FlaggedFeatures.Length > 0 ? string.Join(", ", driftArtifact.FlaggedFeatures) : "n/a");
        }

        // ── 3c. Density-ratio importance weights ───────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays, DefaultDensityRatioLr);
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

        var selectedConfig = SelectArchitectureConfig(
            trainSet, selectionSet, hp, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, dropoutRate, sparsityCoeff, runContext, ct);
        nSteps = selectedConfig.NSteps;
        hiddenDim = selectedConfig.HiddenDim;
        attentionDim = Math.Clamp(selectedConfig.AttentionDim, 1, hiddenDim);
        gamma = selectedConfig.Gamma;
        dropoutRate = selectedConfig.DropoutRate;
        sparsityCoeff = selectedConfig.SparsityCoeff;
        trainerFingerprint = TabNetSnapshotSupport.ComputeTrainerFingerprint(
            hp, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers, gamma, useSparsemax, useGlu, dropoutRate, sparsityCoeff);

        // ── 4. Fit TabNet ──────────────────────────────────────────────────
        var weights = FitTabNet(
            trainSet, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, lr, sparsityCoeff, epochs, effectiveLabelSmoothing,
            warmStart, pretrainedWeights, densityWeights, hp.TemporalDecayLambda, hp.L2Lambda,
            hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
            dropoutRate, bnMomentum, ghostBatchSize, hp.TabNetWarmupEpochs, runContext, ct);

        _logger.LogInformation("TabNet fitted: steps={S} hidden={H}", nSteps, hiddenDim);

        // ── 4b. Weight sanitization ────────────────────────────────────────
        int sanitizedCount = SanitizeWeights(weights);
        if (sanitizedCount > 0)
            _logger.LogWarning("TabNet sanitized {N} non-finite weight values.", sanitizedCount);

        // ── 5. Calibration stack ───────────────────────────────────────────
        var calibrationFit = FitTabNetCalibrationStack(
            calibrationFitSet,
            weights,
            hp.FitTemperatureScale,
            runContext.MinCalibrationSamples,
            runContext.CalibrationEpochs,
            runContext.CalibrationLr);
        var calibrationSnapshot = calibrationFit.FinalSnapshot;
        var calibrationArtifact = calibrationFit.Artifact;
        calibrationArtifact.DiagnosticsSampleCount = calibrationDiagnosticsSet.Count;
        calibrationArtifact.ConformalSampleCount = conformalSet.Count;
        calibrationArtifact.MetaLabelSampleCount = metaLabelSet.Count;
        calibrationArtifact.AbstentionSampleCount = abstentionSet.Count;
        calibrationArtifact.AdaptiveHeadMode = adaptiveHeadSplitMode;
        double plattA = calibrationSnapshot.PlattA;
        double plattB = calibrationSnapshot.PlattB;
        double plattABuy = calibrationSnapshot.PlattABuy;
        double plattBBuy = calibrationSnapshot.PlattBBuy;
        double plattASell = calibrationSnapshot.PlattASell;
        double plattBSell = calibrationSnapshot.PlattBSell;
        double temperatureScale = calibrationSnapshot.TemperatureScale;
        double[] isotonicBp = calibrationSnapshot.IsotonicBreakpoints;

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
            (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp, runContext.HuberDelta);
        }

        // ── 7. Selection threshold + evaluation on held-out test set ──────
        double optimalThreshold = ComputeOptimalThreshold(
            selectionSet, weights, calibrationSnapshot,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        double avgKellyFraction = ComputeAvgKellyFraction(
            calibrationDiagnosticsSet, weights, calibrationSnapshot, runContext.MinCalibrationSamples);
        var selectionMetrics = EvaluateTabNet(
            selectionSet, weights, calibrationSnapshot, magWeights, magBias, F, optimalThreshold);
        var finalMetrics = EvaluateTabNet(
            testSet, weights, calibrationSnapshot, magWeights, magBias, F, optimalThreshold);
        _logger.LogInformation(
            "TabNet selection/test eval \u2014 selectionAcc={SelectionAcc:P1} testAcc={TestAcc:P1} testF1={F1:F3} testEV={EV:F4} testBrier={Brier:F4} testSharpe={Sharpe:F2} threshold={Threshold:F2}",
            selectionMetrics.Accuracy, finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio, optimalThreshold);

        // ── 8. ECE ─────────────────────────────────────────────────────────
        double selectionEce = ComputeEce(selectionSet, weights, calibrationSnapshot);
        double ece = ComputeEce(testSet, weights, calibrationSnapshot);
        double baselinePruningScore = ComputePruningCompositeScore(
            selectionMetrics, selectionEce, 0, F, cvResult.FeatureStabilityScores);

        // ── 10. Permutation feature importance ─────────────────────────────
        float[] featureImportance = selectionSet.Count >= runContext.MinCalibrationSamples
            ? ComputePermutationImportance(selectionSet, weights, calibrationSnapshot, optimalThreshold, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Index: idx, Importance: imp,
                Name: idx < tabNetFeatureNames.Length ? tabNetFeatureNames[idx] : $"F{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        int[] metaLabelTopFeatureIndices = topFeatures
            .Select(feature => feature.Index)
            .ToArray();
        _logger.LogInformation(
            "TabNet top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Cal-set importance (for warm-start transfer) ──────────────
        double[] calImportanceScores = calibrationDiagnosticsSet.Count >= runContext.MinCalibrationSamples
            ? ComputeCalPermutationImportance(calibrationDiagnosticsSet, weights, optimalThreshold, ct)
            : new double[F];

        // ── 11. Feature pruning re-train pass ──────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);
        bool pruningAccepted = false;
        double pruningScoreDelta = 0.0;
        var pruningReasons = new List<string>();
        var pruningDecision = new TabNetPruningDecisionArtifact
        {
            Accepted = false,
            BaselineScore = baselinePruningScore,
            CandidateScore = baselinePruningScore,
            ScoreDelta = 0.0,
            CandidateAccuracy = finalMetrics.Accuracy,
            CandidateBrier = finalMetrics.BrierScore,
            CandidateEce = ece,
            PrunedFeatureCount = prunedCount,
            RetainedFeatureCount = Math.Max(0, F - prunedCount),
            SelectionSampleCount = selectionSet.Count,
            CalibrationSampleCount = calibrationDiagnosticsSet.Count,
            BaselineThreshold = optimalThreshold,
            CandidateThreshold = optimalThreshold,
            Reasons = [],
        };

        if (prunedCount > 0)
        {
            _logger.LogInformation("TabNet feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, F);
            int candidatePrunedCount = prunedCount;

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedSelection = ApplyMask(selectionSet, activeMask);
            var maskedCalibrationFit = ApplyMask(calibrationFitSet, activeMask);
            var maskedCalibrationDiagnostics = ApplyMask(calibrationDiagnosticsSet, activeMask);
            var maskedConformal = ApplyMask(conformalSet, activeMask);
            var maskedMetaLabel = ApplyMask(metaLabelSet, activeMask);
            var maskedAbstention = ApplyMask(abstentionSet, activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            int prunedEpochs = Math.Max(10, epochs / 2);
            var prunedW = FitTabNet(
                maskedTrain, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, sparsityCoeff, prunedEpochs,
                effectiveLabelSmoothing, null, null, densityWeights, hp.TemporalDecayLambda,
                hp.L2Lambda, hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
                dropoutRate, bnMomentum, ghostBatchSize, 0, runContext, ct);
            var prunedCalibrationFit = FitTabNetCalibrationStack(
                maskedCalibrationFit,
                prunedW,
                hp.FitTemperatureScale,
                runContext.MinCalibrationSamples,
                runContext.CalibrationEpochs,
                runContext.CalibrationLr);
            var prunedCalibrationSnapshot = prunedCalibrationFit.FinalSnapshot;

            double[] pmw;
            double pmb;
            if (hp.MagLossWeight > 0.0 && prunedW.MagW.Length > 0)
            { pmw = prunedW.MagW; pmb = prunedW.MagB; }
            else
            { (pmw, pmb) = FitLinearRegressor(maskedTrain, F, hp, runContext.HuberDelta); }

            double prunedThreshold = ComputeOptimalThreshold(
                maskedSelection, prunedW, prunedCalibrationSnapshot,
                hp.ThresholdSearchMin, hp.ThresholdSearchMax);
            var prunedSelectionMetrics = EvaluateTabNet(
                maskedSelection, prunedW, prunedCalibrationSnapshot, pmw, pmb, F, prunedThreshold);
            var prunedMetrics = EvaluateTabNet(
                maskedTest, prunedW, prunedCalibrationSnapshot, pmw, pmb, F, prunedThreshold);
            double prunedSelectionEce = ComputeEce(maskedSelection, prunedW, prunedCalibrationSnapshot);
            double prunedEce = ComputeEce(maskedTest, prunedW, prunedCalibrationSnapshot);
            double prunedScore = ComputePruningCompositeScore(
                prunedSelectionMetrics, prunedSelectionEce, prunedCount, F, cvResult.FeatureStabilityScores);
            pruningScoreDelta = prunedScore - baselinePruningScore;
            bool scoreGate = prunedScore >= baselinePruningScore - 0.01;
            bool accuracyGate = prunedSelectionMetrics.Accuracy + 0.03 >= selectionMetrics.Accuracy;
            bool brierGate = prunedSelectionMetrics.BrierScore <= selectionMetrics.BrierScore + 0.02;
            bool eceGate = prunedSelectionEce <= selectionEce + 0.02;
            if (!scoreGate) pruningReasons.Add("Composite pruning score regressed beyond tolerance.");
            if (!accuracyGate) pruningReasons.Add("Accuracy degraded beyond tolerance.");
            if (!brierGate) pruningReasons.Add("Brier score degraded beyond tolerance.");
            if (!eceGate) pruningReasons.Add("ECE degraded beyond tolerance.");

            if (scoreGate && accuracyGate && brierGate && eceGate)
            {
                _logger.LogInformation(
                    "TabNet pruned model accepted: composite={Score:F4} delta={Delta:+0.0000;-0.0000} acc={Acc:P1}",
                    prunedScore, pruningScoreDelta, prunedMetrics.Accuracy);
                weights      = prunedW;
                magWeights   = pmw; magBias = pmb;
                finalMetrics = prunedMetrics;
                trainSet     = maskedTrain;
                selectionSet = maskedSelection;
                testSet      = maskedTest;
                calibrationFitSet = maskedCalibrationFit;
                calibrationDiagnosticsSet = maskedCalibrationDiagnostics;
                conformalSet = maskedConformal;
                metaLabelSet = maskedMetaLabel;
                abstentionSet = maskedAbstention;
                calibrationSnapshot = prunedCalibrationSnapshot;
                calibrationArtifact = prunedCalibrationFit.Artifact;
                calibrationArtifact.DiagnosticsSampleCount = calibrationDiagnosticsSet.Count;
                calibrationArtifact.ConformalSampleCount = conformalSet.Count;
                calibrationArtifact.MetaLabelSampleCount = metaLabelSet.Count;
                calibrationArtifact.AbstentionSampleCount = abstentionSet.Count;
                calibrationArtifact.AdaptiveHeadMode = adaptiveHeadSplitMode;
                plattA = calibrationSnapshot.PlattA;
                plattB = calibrationSnapshot.PlattB;
                plattABuy = calibrationSnapshot.PlattABuy;
                plattBBuy = calibrationSnapshot.PlattBBuy;
                plattASell = calibrationSnapshot.PlattASell;
                plattBSell = calibrationSnapshot.PlattBSell;
                temperatureScale = calibrationSnapshot.TemperatureScale;
                isotonicBp = calibrationSnapshot.IsotonicBreakpoints;
                selectionMetrics = prunedSelectionMetrics;
                selectionEce = prunedSelectionEce;
                ece              = prunedEce;
                optimalThreshold = prunedThreshold;
                avgKellyFraction = ComputeAvgKellyFraction(
                    maskedCalibrationDiagnostics, weights, calibrationSnapshot, runContext.MinCalibrationSamples);
                featureImportance = maskedSelection.Count >= runContext.MinCalibrationSamples
                    ? ComputePermutationImportance(maskedSelection, weights, calibrationSnapshot, optimalThreshold, ct)
                    : new float[F];
                calImportanceScores = maskedCalibrationDiagnostics.Count >= runContext.MinCalibrationSamples
                    ? ComputeCalPermutationImportance(maskedCalibrationDiagnostics, weights, optimalThreshold, ct)
                    : new double[F];
                pruningAccepted = true;
                pruningReasons.Add("Composite score and guardrail metrics stayed within acceptance tolerances.");
            }
            else
            {
                _logger.LogInformation(
                    "TabNet pruned model rejected: composite={Score:F4} delta={Delta:+0.0000;-0.0000}",
                    prunedScore, pruningScoreDelta);
                prunedCount = 0;
                activeMask  = new bool[F]; Array.Fill(activeMask, true);
            }

            pruningDecision = new TabNetPruningDecisionArtifact
            {
                Accepted = pruningAccepted,
                BaselineScore = baselinePruningScore,
                CandidateScore = prunedScore,
                ScoreDelta = pruningScoreDelta,
                CandidateAccuracy = prunedMetrics.Accuracy,
                CandidateBrier = prunedMetrics.BrierScore,
                CandidateEce = prunedEce,
                PrunedFeatureCount = candidatePrunedCount,
                RetainedFeatureCount = Math.Max(0, F - candidatePrunedCount),
                SelectionSampleCount = selectionSet.Count,
                CalibrationSampleCount = calibrationDiagnosticsSet.Count,
                BaselineThreshold = pruningDecision.BaselineThreshold,
                CandidateThreshold = pruningAccepted ? optimalThreshold : prunedThreshold,
                Reasons = pruningReasons.ToArray(),
            };
        }
        else
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
            pruningDecision.Reasons = ["Pruning threshold kept all features active."];
        }

        _logger.LogInformation(
            "TabNet deployed calibration: global={Global} tempSelected={TempSelected} buyBranch={BuyBranch} sellBranch={SellBranch} isotonicBreakpoints={Breakpoints}",
            calibrationArtifact.SelectedGlobalCalibration,
            calibrationArtifact.TemperatureSelected,
            calibrationArtifact.BuyBranchAccepted,
            calibrationArtifact.SellBranchAccepted,
            isotonicBp.Length / 2);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(
            conformalSet, weights, calibrationSnapshot, conformalAlpha, minConformalSamples);

        // ── 11c. Jackknife+ residuals ──────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(trainSet, weights);

        // ── 11d. Meta-label model ──────────────────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            metaLabelSet, weights, calibrationSnapshot, optimalThreshold,
            minAdaptiveHeadSamples, runContext.CalibrationEpochs, runContext.CalibrationLr);

        // ── 11e. Abstention gate ───────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            abstentionSet, weights, calibrationSnapshot, optimalThreshold,
            minAdaptiveHeadSamples, runContext.CalibrationEpochs, runContext.CalibrationLr);

        // ── 11f. Quantile magnitude regressor ─────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(
                trainSet, F, hp.MagnitudeQuantileTau, runContext.MinCalibrationSamples);
        }

        // ── 11g. Decision boundary stats ──────────────────────────────────
        var (dbMean, dbStd) = calibrationDiagnosticsSet.Count >= runContext.MinCalibrationSamples
            ? ComputeDecisionBoundaryStats(calibrationDiagnosticsSet, weights)
            : (0.0, 0.0);

        // ── 11h. Durbin-Watson on magnitude residuals ──────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F, runContext.MinCalibrationSamples);
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

        // ── 11j. Brier Skill Score ─────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(
            testSet, weights, calibrationSnapshot, runContext.MinCalibrationSamples);
        _logger.LogInformation("TabNet BSS={BSS:F4}", brierSkillScore);
        var (calibrationResidualMean, calibrationResidualStd, calibrationResidualThreshold) =
            ComputeCalibrationResidualStats(
                calibrationDiagnosticsSet, weights, calibrationSnapshot, runContext.MinCalibrationSamples);

        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(testSet, weights, calibrationSnapshot);
        var (calibrationLoss, refinementLoss) =
            ComputeMurphyDecomposition(testSet, weights, calibrationSnapshot);
        double predictionStability = ComputePredictionStabilityScore(testSet, weights, calibrationSnapshot);
        double[] featureVariances = ComputeFeatureVariances(trainSet, F);
        double calibrationEce = ComputeEce(calibrationDiagnosticsSet, weights, calibrationSnapshot);
        var calibrationMetrics = EvaluateTabNet(
            calibrationDiagnosticsSet, weights, calibrationSnapshot, magWeights, magBias, F, optimalThreshold);

        // ── 11l. PSI baseline ──────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 12. Mean attention + per-step attention + attention entropy ─────
        var (meanAttn, perStepAttn, attnEntropy) = ComputeAttentionStats(testSet, weights);
        double[] perStepSparsity = ComputePerStepSparsity(perStepAttn);
        double[] bnDriftByLayer = ComputeBnDriftByLayer(
            weights, trainSet, ghostBatchSize, runContext.MinCalibrationSamples);
        var (activationCentroid, activationDistanceMean, activationDistanceStd, attnEntropyThreshold, uncertaintyThreshold) =
            ComputeActivationReferenceStats(
                calibrationDiagnosticsSet.Count >= runContext.MinCalibrationSamples
                    ? calibrationDiagnosticsSet
                    : calibrationFitSet.Count >= runContext.MinCalibrationSamples
                        ? calibrationFitSet
                        : trainSet,
                weights);
        double warmStartReuseRatio = warmStart is not null
            ? EstimateWarmStartReuseRatio(warmStart, F, nSteps, hiddenDim, sharedLayers, stepLayers)
            : 0.0;
        SanitizeArr(meanAttn);
        foreach (var row in perStepAttn) SanitizeArr(row);
        SanitizeArr(attnEntropy);
        SanitizeArr(perStepSparsity);
        SanitizeArr(bnDriftByLayer);
        SanitizeArr(activationCentroid);

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
        calibrationLoss  = Safe(calibrationLoss);
        refinementLoss   = Safe(refinementLoss);
        predictionStability = Safe(predictionStability);
        activationDistanceMean = Safe(activationDistanceMean);
        activationDistanceStd = Safe(activationDistanceStd);
        attnEntropyThreshold = Safe(attnEntropyThreshold);
        uncertaintyThreshold = Safe(uncertaintyThreshold);
        warmStartReuseRatio = Safe(warmStartReuseRatio);
        pruningScoreDelta = Safe(pruningScoreDelta);
        calibrationResidualMean = Safe(calibrationResidualMean);
        calibrationResidualStd = Safe(calibrationResidualStd);
        calibrationResidualThreshold = Safe(calibrationResidualThreshold);
        calibrationEce = Safe(calibrationEce, 1.0);
        metaLabelTopFeatureIndices = featureImportance
            .Select((importance, index) => (Index: index, Importance: importance))
            .OrderByDescending(entry => entry.Importance)
            .ThenBy(entry => entry.Index)
            .Take(5)
            .Select(entry => entry.Index)
            .ToArray();

        var selectionMetricSummary = CreateMetricSummary(
            "SELECTION", selectionMetrics, selectionEce, optimalThreshold, selectionSet.Count);
        var calibrationMetricSummary = CreateMetricSummary(
            "CALIBRATION_DIAGNOSTICS", calibrationMetrics, calibrationEce, optimalThreshold, calibrationDiagnosticsSet.Count);
        var testMetricSummary = CreateMetricSummary(
            "TEST", finalMetrics, ece, optimalThreshold, testSet.Count);

        var splitSummary = new TrainingSplitSummary
        {
            RawTrainCount = rawTrainCount,
            RawSelectionCount = rawSelectionCount,
            RawCalibrationCount = rawCalibrationCount,
            RawTestCount = rawTestCount,
            TrainStartIndex = 0,
            TrainCount = trainSet.Count,
            SelectionStartIndex = selectionStart,
            SelectionCount = selectionSet.Count,
            CalibrationStartIndex = calibrationStart,
            CalibrationCount = calibrationSet.Count,
            CalibrationFitStartIndex = calibrationFitStart,
            CalibrationFitCount = calibrationFitSet.Count,
            CalibrationDiagnosticsStartIndex = calibrationDiagnosticsSet.Count > 0 ? calibrationDiagnosticsStart : 0,
            CalibrationDiagnosticsCount = calibrationDiagnosticsSet.Count,
            ConformalStartIndex = conformalSet.Count > 0 ? conformalStart : 0,
            ConformalCount = conformalSet.Count,
            MetaLabelStartIndex = metaLabelSet.Count > 0 ? metaLabelStart : 0,
            MetaLabelCount = metaLabelSet.Count,
            AbstentionStartIndex = abstentionSet.Count > 0 ? abstentionStart : 0,
            AbstentionCount = abstentionSet.Count,
            AdaptiveHeadSplitMode = adaptiveHeadSplitMode,
            AdaptiveHeadCrossFitFoldCount = adaptiveHeadCrossFitFoldCount,
            TestStartIndex = testStart,
            TestCount = testSet.Count,
            EmbargoCount = embargo,
            TrainEmbargoDropped = rawTrainCount - trainSet.Count,
            SelectionEmbargoDropped = rawSelectionCount - selectionSet.Count,
            CalibrationEmbargoDropped = rawCalibrationCount - calibrationSet.Count,
        };
        var warmStartArtifact = new TabNetWarmStartArtifact
        {
            Compatible = warmStartCompatibility.IsCompatible,
            CompatibilityIssues = warmStartCompatibility.Issues,
            Attempted = runContext.WarmStartLoadReport.Attempted,
            Reused = runContext.WarmStartLoadReport.Reused,
            Resized = runContext.WarmStartLoadReport.Resized,
            Skipped = runContext.WarmStartLoadReport.Skipped,
            Rejected = runContext.WarmStartLoadReport.Rejected,
            ReuseRatio = runContext.WarmStartLoadReport.ReuseRatio,
        };

        // ── 13. Serialise model snapshot ───────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = tabNetFeatureNames,
            FeaturePipelineTransforms  = featurePipelineTransforms,
            FeaturePipelineDescriptors = featurePipelineDescriptors,
            FeatureSchemaFingerprint   = featureSchemaFingerprint,
            PreprocessingFingerprint   = preprocessingFingerprint,
            TrainerFingerprint         = trainerFingerprint,
            TrainingRandomSeed         = TrainerSeed,
            TrainingSplitSummary       = splitSummary,
            TabNetSelectionMetrics     = selectionMetricSummary,
            TabNetCalibrationMetrics   = calibrationMetricSummary,
            TabNetTestMetrics          = testMetricSummary,
            Means                      = snapshotMeans,
            Stds                       = snapshotStds,
            BaseLearnersK              = nSteps,
            Weights                    = weights.AttnFcW.Length > 0
                                             ? weights.AttnFcW.Select(a => a.Length > 0 ? a[0] : []).ToArray()
                                             : [[]],
            Biases                     = [weights.OutputB],
            TabNetOutputWeight         = weights.OutputW.Length > 0 ? weights.OutputW[0] : 0.0,
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calibrationSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = DateTime.UtcNow,
            FeatureImportance          = featureImportance,
            FeatureVariances           = featureVariances,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            ReliabilityBinConfidence   = reliabilityBinConf.Length > 0 ? reliabilityBinConf : null,
            ReliabilityBinAccuracy     = reliabilityBinAcc.Length > 0 ? reliabilityBinAcc : null,
            ReliabilityBinCounts       = reliabilityBinCounts.Length > 0 ? reliabilityBinCounts : null,
            IsotonicBreakpoints        = isotonicBp,
            ConformalQHat              = conformalQHat,
            // TabNet training does not apply fractional differencing, so persisting a non-zero
            // value here would make deployed scoring drift away from the fitted model.
            FracDiffD                  = 0.0,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            MetaLabelTopFeatureIndices = metaLabelTopFeatureIndices,
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
            ConditionalCalibrationRoutingThreshold = calibrationSnapshot.ConditionalCalibrationRoutingThreshold,
            AvgKellyFraction           = avgKellyFraction,
            RedundantFeaturePairs      = redundantPairs,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            BrierSkillScore            = brierSkillScore,
            CalibrationLoss            = calibrationLoss,
            RefinementLoss             = refinementLoss,
            PredictionStabilityScore   = predictionStability,
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
            TabNetRawFeatureCount      = origF,
            TabNetPolyTopFeatureIndices = polyTopIdx,
            TabNetUseGlu              = useGlu,
            TabNetPerStepSparsity     = perStepSparsity,
            TabNetBnDriftByLayer      = bnDriftByLayer,
            TabNetActivationCentroid  = activationCentroid,
            TabNetActivationDistanceMean = activationDistanceMean,
            TabNetActivationDistanceStd  = activationDistanceStd,
            TabNetAttentionEntropyThreshold = attnEntropyThreshold,
            TabNetUncertaintyThreshold = uncertaintyThreshold,
            TabNetWarmStartReuseRatio = warmStartReuseRatio,
            TabNetPruningAccepted     = pruningAccepted,
            TabNetPruningScoreDelta   = pruningScoreDelta,
            TabNetAutoTuneTrace       = runContext.AutoTuneTrace.Length > 0 ? runContext.AutoTuneTrace : null,
            TabNetWarmStartArtifact   = warmStartArtifact,
            TabNetPruningDecision     = pruningDecision,
            TabNetCalibrationArtifact = calibrationArtifact,
            TabNetDriftArtifact       = driftArtifact,
            TabNetCalibrationResidualMean = calibrationResidualMean,
            TabNetCalibrationResidualStd = calibrationResidualStd,
            TabNetCalibrationResidualThreshold = calibrationResidualThreshold,

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
            TabNetUseSparsemax         = useSparsemax,
            TabNetHiddenDim            = hiddenDim,
            TabNetInitialBnFcW         = weights.InitialBnFcW,
            TabNetInitialBnFcB         = weights.InitialBnFcB,
            TabNetPerStepAttention     = perStepAttn,
            TabNetAttentionEntropy     = attnEntropy,
        };

        var audit = RunTabNetModelAudit(snapshot, weights, rawAuditSet);
        snapshot.TabNetAuditFindings = audit.Findings;
        snapshot.TabNetTrainInferenceParityMaxError = audit.MaxParityError;
        snapshot.TabNetAuditArtifact = audit.Artifact;

        SanitizeSnapshotArrays(snapshot);

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "TabNetModelTrainer v3 complete: steps={S}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            nSteps, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
