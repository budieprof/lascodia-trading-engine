using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade Gradient Boosting Machine trainer. Implements depth-limited decision tree
/// gradient boosting with log-loss in pure C#.
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples.</item>
///   <item>Walk-forward cross-validation (expanding window, embargo, purging) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Final model splits: 70% train | 10% Platt calibration | ~20% held-out test.</item>
///   <item>Gradient boosted regression trees fitted on pseudo-residuals with shrinkage.</item>
///   <item>Histogram-based or exact split finding (configurable). Leaf-wise or level-wise growth.</item>
///   <item>DART dropout mode for combating over-specialization.</item>
///   <item>Platt scaling (A, B) fitted via SGD with convergence check on the calibration fold.</item>
///   <item>Isotonic calibration (PAVA) with boundary extrapolation, applied post-Platt.</item>
///   <item>Approximate Venn-Abers-style multi-probability bounds for diagnostic uncertainty reporting.</item>
///   <item>ECE (Expected Calibration Error) computed post-calibration on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set (with optional transaction cost adjustment).</item>
///   <item>Parallel magnitude linear regressor trained with Adam + Huber loss + early stopping.</item>
///   <item>Permutation feature importance + gain-weighted tree split importance.</item>
///   <item>Feature pruning re-train pass with deployable zero-mask replay.</item>
///   <item>OOB accuracy estimation from out-of-bag tree predictions.</item>
///   <item>Conformal prediction (split-conformal qHat) with probability-space nonconformity scores.</item>
///   <item>Jackknife+ residuals with empirical coverage validation.</item>
///   <item>Meta-label MLP (configurable hidden dim) for filtering low-quality signals.</item>
///   <item>Abstention gate with separate buy/sell thresholds and coverage-accuracy curve.</item>
///   <item>Quantile magnitude regressor (pinball loss, Adam optimizer) for asymmetric risk sizing.</item>
///   <item>Decision boundary distance analytics and prediction stability metric.</item>
///   <item>Durbin-Watson autocorrelation diagnostic on magnitude residuals.</item>
///   <item>Class-conditional Platt scaling (separate Buy/Sell calibrators).</item>
///   <item>Average Kelly fraction for position sizing guidance.</item>
///   <item>Mutual-information feature redundancy check with drop recommendation.</item>
///   <item>Temperature scaling via Brent's method.</item>
///   <item>Brier Skill Score + Murphy-style Brier decomposition (reliability + resolution).</item>
///   <item>TreeSHAP baseline for per-prediction feature attribution.</item>
///   <item>Partial dependence data for top features.</item>
///   <item>NaN/Inf tree sanitization + compact serialization (empty node pruning).</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Optional warm-start with tree pruning and OOB replay.</item>
///   <item>Incremental update fast-path for rapid regime adaptation.</item>
///   <item>Density-ratio importance weighting (MLP discriminator) for distribution shift.</item>
///   <item>Covariate shift weights with continuous novelty scoring.</item>
///   <item>Stationarity gate (ADF with interpolated critical values).</item>
///   <item>Concept drift gate: buy-rate drift exclusion heuristic.</item>
///   <item>Regime-conditioned GBM knobs are rejected until per-sample regime labels exist in the training contract.</item>
///   <item>Interaction constraints, shrinkage annealing, depth-decayed min split gain.</item>
///   <item>Training time budget enforcement, memory pre-check, deterministic mode.</item>
///   <item>Rank-dispersion feature stability across CV folds.</item>
/// </list>
/// </para>
/// Registered as a keyed IMLModelTrainer with key "gbm".
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Gbm)]
public sealed class GbmModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const string ModelType    = "GBM";
    private const string ModelVersion = "3.2"; // 3.2: snapshot contract hardening, learned conditional routing, stricter warm-start compatibility

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly ILogger<GbmModelTrainer> _logger;

    private readonly record struct GbmCalibrationState(
        double GlobalPlattA,
        double GlobalPlattB,
        double TemperatureScale,
        double PlattABuy,
        double PlattBBuy,
        double PlattASell,
        double PlattBSell,
        double ConditionalRoutingThreshold,
        double[] IsotonicBreakpoints)
    {
        internal static readonly GbmCalibrationState Default = new(
            GlobalPlattA: 1.0,
            GlobalPlattB: 0.0,
            TemperatureScale: 0.0,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: 0.5,
            IsotonicBreakpoints: []);
    }

    private readonly record struct ConditionalPlattBranchFit(
        int SampleCount,
        double BaselineLoss,
        double FittedLoss,
        double A,
        double B)
    {
        public bool Accepted => InferenceHelpers.HasMeaningfulConditionalCalibration(A, B);
    }

    private readonly record struct ClassConditionalPlattFit(
        ConditionalPlattBranchFit Buy,
        ConditionalPlattBranchFit Sell);

    private readonly record struct GbmCalibrationPartition(
        List<TrainingSample> FitSet,
        List<TrainingSample> DiagnosticsSet,
        List<TrainingSample> ConformalSet,
        List<TrainingSample> MetaLabelSet,
        List<TrainingSample> AbstentionSet,
        int FitStartIndex,
        int DiagnosticsStartIndex,
        int ConformalStartIndex,
        int MetaLabelStartIndex,
        int AbstentionStartIndex,
        string AdaptiveHeadSplitMode);

    private static ModelSnapshot CreateCalibrationSnapshot(in GbmCalibrationState state)
    {
        return new ModelSnapshot
        {
            PlattA = state.GlobalPlattA,
            PlattB = state.GlobalPlattB,
            TemperatureScale = state.TemperatureScale,
            PlattABuy = state.PlattABuy,
            PlattBBuy = state.PlattBBuy,
            PlattASell = state.PlattASell,
            PlattBSell = state.PlattBSell,
            ConditionalCalibrationRoutingThreshold = state.ConditionalRoutingThreshold,
            IsotonicBreakpoints = state.IsotonicBreakpoints,
        };
    }

    private static GbmCalibrationArtifact BuildCalibrationArtifact(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample> diagnosticsSet,
        IReadOnlyList<TrainingSample> conformalSet,
        IReadOnlyList<TrainingSample> metaLabelSet,
        IReadOnlyList<TrainingSample> abstentionSet,
        string adaptiveHeadMode,
        int adaptiveHeadCrossFitFoldCount,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double learningRate,
        int featureCount,
        in GbmCalibrationState state,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        var evalSet = diagnosticsSet.Count > 0 ? diagnosticsSet : fitSet;
        var globalPlattSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: 0.0,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: 0.5,
            IsotonicBreakpoints: []));
        var selectedGlobalSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: state.TemperatureScale,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: state.ConditionalRoutingThreshold,
            IsotonicBreakpoints: []));
        var preIsotonicSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
            GlobalPlattA: state.GlobalPlattA,
            GlobalPlattB: state.GlobalPlattB,
            TemperatureScale: state.TemperatureScale,
            PlattABuy: state.PlattABuy,
            PlattBBuy: state.PlattBBuy,
            PlattASell: state.PlattASell,
            PlattBSell: state.PlattBSell,
            ConditionalRoutingThreshold: state.ConditionalRoutingThreshold,
            IsotonicBreakpoints: []));
        var finalSnapshot = CreateCalibrationSnapshot(state);

        double globalPlattNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, globalPlattSnapshot, perTreeLearningRates);
        double temperatureNll = state.TemperatureScale > 0.0
            ? ComputeCalibrationNll(evalSet, trees, baseLogOdds, learningRate, featureCount, selectedGlobalSnapshot, perTreeLearningRates)
            : globalPlattNll;

        var conditionalFit = FitClassConditionalPlatt(
            fitSet,
            trees,
            baseLogOdds,
            learningRate,
            featureCount,
            perTreeLearningRates,
            state.ConditionalRoutingThreshold,
            selectedGlobalSnapshot);

        double preIsotonicNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, preIsotonicSnapshot, perTreeLearningRates);
        double postIsotonicNll = ComputeCalibrationNll(
            evalSet, trees, baseLogOdds, learningRate, featureCount, finalSnapshot, perTreeLearningRates);

        return new GbmCalibrationArtifact
        {
            SelectedGlobalCalibration = state.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
            CalibrationSelectionStrategy = diagnosticsSet.Count > 0
                ? "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS"
                : "FIT_AND_EVAL_ON_FIT",
            GlobalPlattNll = globalPlattNll,
            TemperatureNll = temperatureNll,
            TemperatureSelected = state.TemperatureScale > 0.0,
            FitSampleCount = fitSet.Count,
            DiagnosticsSampleCount = evalSet.Count,
            DiagnosticsSelectedGlobalNll = state.TemperatureScale > 0.0 ? temperatureNll : globalPlattNll,
            DiagnosticsSelectedStackNll = postIsotonicNll,
            ConformalSampleCount = conformalSet.Count,
            MetaLabelSampleCount = metaLabelSet.Count,
            AbstentionSampleCount = abstentionSet.Count,
            AdaptiveHeadMode = adaptiveHeadMode,
            AdaptiveHeadCrossFitFoldCount = adaptiveHeadCrossFitFoldCount,
            ConditionalRoutingThreshold = state.ConditionalRoutingThreshold,
            BuyBranchSampleCount = conditionalFit.Buy.SampleCount,
            BuyBranchBaselineNll = conditionalFit.Buy.BaselineLoss,
            BuyBranchFittedNll = conditionalFit.Buy.FittedLoss,
            BuyBranchAccepted = conditionalFit.Buy.Accepted,
            SellBranchSampleCount = conditionalFit.Sell.SampleCount,
            SellBranchBaselineNll = conditionalFit.Sell.BaselineLoss,
            SellBranchFittedNll = conditionalFit.Sell.FittedLoss,
            SellBranchAccepted = conditionalFit.Sell.Accepted,
            IsotonicSampleCount = fitSet.Count,
            IsotonicBreakpointCount = state.IsotonicBreakpoints.Length / 2,
            PreIsotonicNll = preIsotonicNll,
            PostIsotonicNll = postIsotonicNll,
            IsotonicAccepted = state.IsotonicBreakpoints.Length >= 4 && postIsotonicNll <= preIsotonicNll + 1e-6,
        };
    }

    public GbmModelTrainer(ILogger<GbmModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ─────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);
    }

    // ── Core training logic (synchronous, runs on thread-pool) ──────────────

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();
        if (samples is null || samples.Count == 0)
            throw new ArgumentException("GBM training requires at least one sample.", nameof(samples));

        var trainingStopwatch = Stopwatch.StartNew();

        // ── Item 41: Memory budget pre-check ───────────────────────────────
        int featureCount = samples[0].Features.Length;
        string[] snapshotFeatureNames = BuildSnapshotFeatureNames(featureCount);
        int[] rawFeatureIndices = Enumerable.Range(0, featureCount).ToArray();
        string featureSchemaFingerprint = GbmSnapshotSupport.ComputeFeatureSchemaFingerprint(snapshotFeatureNames, featureCount);
        long estimatedBytes = (long)samples.Count * featureCount * sizeof(float) * 3L; // features + gradients + hessians
        if (estimatedBytes > 2_000_000_000L)
        {
            throw new InvalidOperationException(
                $"GBM memory estimate exceeds the 2GB budget ({estimatedBytes / (1024 * 1024)}MB). " +
                "Reduce the sample count or feature count before training.");
        }

        int numRounds    = Math.Max(10, hp.K > 0 ? hp.K : 50);
        int maxDepth     = hp.GbmMaxDepth > 0 ? hp.GbmMaxDepth : 3;
        double lr        = hp.LearningRate > 0 ? hp.LearningRate : 0.1;
        int barsPerDay   = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
        string trainerFingerprint = GbmSnapshotSupport.ComputeTrainerFingerprint(hp, featureCount, numRounds, maxDepth, lr);
        int trainingRandomSeed = ComputeTrainingRandomSeed(featureSchemaFingerprint, trainerFingerprint, samples.Count);

        bool warmStartContractCompatible = false;
        if (warmStart is not null && warmStart.Type == ModelType)
        {
            string warmStartPreprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(
                featureCount,
                warmStart.RawFeatureIndices is { Length: > 0 }
                    ? warmStart.RawFeatureIndices
                    : rawFeatureIndices,
                warmStart.FeaturePipelineDescriptors ?? [],
                warmStart.ActiveFeatureMask);
            var compatibility = GbmSnapshotSupport.AssessWarmStartCompatibility(
                warmStart,
                featureSchemaFingerprint,
                warmStartPreprocessingFingerprint,
                trainerFingerprint,
                featureCount);
            warmStartContractCompatible = compatibility.IsCompatible;
            if (!warmStartContractCompatible)
            {
                _logger.LogWarning(
                    "GBM warm-start disabled due to incompatible snapshot contract: {Issues}",
                    string.Join("; ", compatibility.Issues));
            }
        }

        if (hp.GbmRegimeConditioned)
            throw new InvalidOperationException(
                "GBM regime-conditioned tree blocks are not supported by the current training contract. " +
                "TrainingSample does not carry per-sample regime labels.");

        if (hp.GbmRegimeAwareEarlyStopping)
            throw new InvalidOperationException(
                "GBM regime-aware early stopping is not supported by the current training contract. " +
                "TrainingSample does not carry per-sample regime labels.");

        // ── 0. Incremental update fast-path ─────────────────────────────────
        if (warmStartContractCompatible && warmStart is not null && warmStart.Type == ModelType && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "GBM incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    K                     = Math.Max(10, numRounds / 3),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = lr / 3.0,
                    DensityRatioWindowDays = 0, // prevent recursion
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Z-score standardisation ──────────────────────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (computedMeans, computedStds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        bool reuseWarmStartPreprocessing =
            warmStartContractCompatible &&
            warmStart is not null &&
            warmStart.Type == ModelType &&
            warmStart.GbmTreesJson is { Length: > 0 } &&
            warmStart.Means.Length == featureCount &&
            warmStart.Stds.Length == featureCount;

        var means = reuseWarmStartPreprocessing ? warmStart!.Means : computedMeans;
        var stds  = reuseWarmStartPreprocessing ? warmStart!.Stds  : computedStds;
        if (reuseWarmStartPreprocessing)
        {
            _logger.LogInformation(
                "GBM warm-start: reusing parent standardisation statistics for tree compatibility (gen={Gen}).",
                warmStart!.GenerationNumber);
        }

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── Item 28: Concept drift gate ─────────────────────────────────────
        if (hp.GbmConceptDriftGate && allStd.Count >= hp.MinSamples * 2)
        {
            allStd = ApplyConceptDriftGate(allStd, featureCount, hp.MinSamples);
        }

        FeatureTransformDescriptor[] featurePipelineDescriptors = [];
        bool[]? inheritedActiveMask = null;
        bool inheritedFeatureLayout = false;
        if (reuseWarmStartPreprocessing)
        {
            if (warmStart!.FeaturePipelineDescriptors is { Length: > 0 } warmDescriptors)
            {
                featurePipelineDescriptors = warmDescriptors
                    .Select(CloneFeatureTransformDescriptor)
                    .ToArray();
                allStd = ApplyFeatureTransforms(allStd, featurePipelineDescriptors);
                inheritedFeatureLayout = true;
            }

            if (warmStart.ActiveFeatureMask is { Length: > 0 } warmMask &&
                warmMask.Length == featureCount &&
                warmMask.Any(active => !active))
            {
                inheritedActiveMask = (bool[])warmMask.Clone();
                allStd = ApplyFeatureMask(allStd, inheritedActiveMask);
                inheritedFeatureLayout = true;
            }
        }

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, featureCount, numRounds, maxDepth, lr, ct);
        _logger.LogInformation(
            "GBM Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();
        CheckTimeoutBudget(trainingStopwatch, hp.TrainingTimeoutMinutes, "after CV"); // Item 40

        // ── 3. Final model splits: 70% train | 10% cal | ~20% test ──────────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];
        int calibrationStartIndex = Math.Min(trainEnd + embargo, allStd.Count);
        var calibrationPartition = BuildCalibrationPartition(calSet, calibrationStartIndex);
        var calibrationFitSet = calibrationPartition.FitSet;
        var calibrationDiagnosticsSet = calibrationPartition.DiagnosticsSet;
        var conformalSet = calibrationPartition.ConformalSet;
        var metaLabelSet = calibrationPartition.MetaLabelSet;
        var abstentionSet = calibrationPartition.AbstentionSet;

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"GBM: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 3b. Stationarity gate (Item 34: interpolated ADF critical values) ──
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, featureCount);
            double nonStatFraction = featureCount > 0 ? (double)nonStatCount / featureCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "GBM Stationarity gate: {NonStat}/{Total} features have unit root. Consider enabling FracDiffD.",
                    nonStatCount, featureCount);
        }

        // ── 3c. Density-ratio importance weights (Item 27: MLP discriminator) ──
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioImportanceWeights(trainSet, featureCount, hp.DensityRatioWindowDays, barsPerDay, trainingRandomSeed);
            _logger.LogDebug("GBM density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 3d. Covariate shift weight integration (Item 29: continuous novelty) ──
        if (warmStartContractCompatible &&
            hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, featureCount);
            if (densityWeights is not null)
            {
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("GBM covariate shift weights applied from parent model (gen={Gen}).",
                warmStart!.GenerationNumber);
        }

        // ── 3e. Adaptive label smoothing ────────────────────────────────────
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
                "GBM adaptive label smoothing: ε={Eps:F3} (ambiguous fraction={Frac:P1})",
                effectiveLabelSmoothing, ambiguousFraction);
        }

        // ── 3f. Parse interaction constraints (Item 18) ─────────────────────
        int[][]? interactionConstraints = null;
        if (!string.IsNullOrEmpty(hp.GbmInteractionConstraints))
        {
            try
            {
                interactionConstraints = JsonSerializer.Deserialize<int[][]>(hp.GbmInteractionConstraints);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "GBM: Failed to parse interaction constraints, ignoring.");
            }
        }

        // ── 3g. EFB: Exclusive Feature Bundling (Item 3) ────────────────────
        int effectiveFeatureCount = featureCount;
        if (!inheritedFeatureLayout && featureCount > 20 && featureCount <= 5000)
        {
            var (efbMapping, _) = BuildEfbMapping(trainSet, featureCount);
            var efbGroups = BuildEfbGroups(efbMapping, featureCount);
            if (efbGroups.Length > 0)
            {
                _logger.LogInformation(
                    "GBM EFB: applying {GroupCount} mutually-exclusive groups as in-place feature bundling",
                    efbGroups.Length);

                var efbDescriptor = FeaturePipelineTransformSupport.BuildGroupSumInPlaceDescriptor(featureCount, efbGroups);
                featurePipelineDescriptors = [.. featurePipelineDescriptors, efbDescriptor];
                trainSet = ApplyFeatureTransforms(trainSet, [efbDescriptor]);
                calSet   = ApplyFeatureTransforms(calSet, [efbDescriptor]);
                testSet  = ApplyFeatureTransforms(testSet, [efbDescriptor]);
            }
        }

        // ── 4. Fit GBM ensemble ─────────────────────────────────────────────
        var ensembleWarmStart = warmStartContractCompatible ? warmStart : null;
        var (trees, baseLogOdds, treeBagMasks, innerTrainCount, perTreeLrList) = FitGbmEnsemble(trainSet, effectiveFeatureCount, numRounds, maxDepth, lr,
            effectiveLabelSmoothing, ensembleWarmStart, densityWeights, hp, ct, interactionConstraints, trainingRandomSeed);

        _logger.LogInformation("GBM fitted: {R} trees, baseLogOdds={BLO:F4}", trees.Count, baseLogOdds);
        CheckTimeoutBudget(trainingStopwatch, hp.TrainingTimeoutMinutes, "after ensemble fit"); // Item 40

        // ── 5. Calibrated probability stack (fit on fit slice, select on diagnostics slice) ──
        var calibrationEvalSet = calibrationDiagnosticsSet.Count > 0 ? calibrationDiagnosticsSet : calibrationFitSet;
        var (plattA, plattB) = FitPlattScaling(calibrationFitSet, trees, baseLogOdds, lr, effectiveFeatureCount, perTreeLrList);
        _logger.LogDebug("GBM Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        var globalCalibrationState = new GbmCalibrationState(
            GlobalPlattA: plattA,
            GlobalPlattB: plattB,
            TemperatureScale: 0.0,
            PlattABuy: 0.0,
            PlattBBuy: 0.0,
            PlattASell: 0.0,
            PlattBSell: 0.0,
            ConditionalRoutingThreshold: 0.5,
            IsotonicBreakpoints: []);
        var globalCalibrationSnapshot = CreateCalibrationSnapshot(globalCalibrationState);
        double selectedGlobalNll = ComputeCalibrationNll(
            calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, globalCalibrationSnapshot, perTreeLrList);

        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calibrationFitSet.Count >= 10)
        {
            double candidateTemperature = FitTemperatureScaling(calibrationFitSet, trees, baseLogOdds, lr, effectiveFeatureCount, perTreeLrList);
            var candidateGlobalSnapshot = CreateCalibrationSnapshot(globalCalibrationState with { TemperatureScale = candidateTemperature });
            double candidateTemperatureNll = ComputeCalibrationNll(
                calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, candidateGlobalSnapshot, perTreeLrList);
            if (candidateTemperatureNll + 1e-6 < selectedGlobalNll)
            {
                temperatureScale = candidateTemperature;
                selectedGlobalNll = candidateTemperatureNll;
                globalCalibrationState = globalCalibrationState with { TemperatureScale = candidateTemperature };
                globalCalibrationSnapshot = candidateGlobalSnapshot;
            }
            _logger.LogDebug("GBM temperature scaling candidate: T={T:F4} evalNll={Nll:F6}", candidateTemperature, candidateTemperatureNll);
        }

        double routingThreshold = DetermineConditionalRoutingThreshold(
            calibrationFitSet,
            calibrationEvalSet,
            trees,
            baseLogOdds,
            lr,
            effectiveFeatureCount,
            globalCalibrationSnapshot,
            perTreeLrList);

        // ── 5b. Class-conditional Platt ─────────────────────────────────────
        var conditionalFit = FitClassConditionalPlatt(
            calibrationFitSet, trees, baseLogOdds, lr, effectiveFeatureCount, perTreeLrList, routingThreshold, globalCalibrationSnapshot);
        double plattABuy = conditionalFit.Buy.A;
        double plattBBuy = conditionalFit.Buy.B;
        double plattASell = conditionalFit.Sell.A;
        double plattBSell = conditionalFit.Sell.B;
        _logger.LogDebug(
            "GBM Class-conditional Platt — threshold={Thr:F3} buy(A={AB:F4}, B={BB:F4}) sell(A={AS:F4}, B={BS:F4})",
            routingThreshold, plattABuy, plattBBuy, plattASell, plattBSell);

        var calibrationState = new GbmCalibrationState(
            GlobalPlattA: plattA,
            GlobalPlattB: plattB,
            TemperatureScale: temperatureScale,
            PlattABuy: plattABuy,
            PlattBBuy: plattBBuy,
            PlattASell: plattASell,
            PlattBSell: plattBSell,
            ConditionalRoutingThreshold: routingThreshold,
            IsotonicBreakpoints: []);
        var calibrationSnapshot = CreateCalibrationSnapshot(calibrationState);

        // ── 5c. Isotonic calibration (fit on fit slice, accept on diagnostics slice) ─────
        double[] isotonicBp = [];
        double[] isotonicCandidate = FitIsotonicCalibration(
            calibrationFitSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        if (isotonicCandidate.Length >= 4)
        {
            var candidateCalibrationState = calibrationState with { IsotonicBreakpoints = isotonicCandidate };
            var candidateCalibrationSnapshot = CreateCalibrationSnapshot(candidateCalibrationState);
            double preIsotonicNll = ComputeCalibrationNll(
                calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
            double postIsotonicNll = ComputeCalibrationNll(
                calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, candidateCalibrationSnapshot, perTreeLrList);
            if (postIsotonicNll <= preIsotonicNll + 1e-6)
            {
                isotonicBp = isotonicCandidate;
                calibrationState = candidateCalibrationState;
                calibrationSnapshot = candidateCalibrationSnapshot;
            }
        }
        _logger.LogInformation("GBM isotonic calibration: {N} accepted PAVA breakpoints", isotonicBp.Length / 2);

        // ── 5d. EV-optimal threshold (deployed-calibrated stack) ───────────
        double optimalThreshold = ComputeOptimalThreshold(
            calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax, hp.GbmEvThresholdSpreadCost, hp.ThresholdSearchStepBps);
        _logger.LogInformation("GBM EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 5e. Kelly fraction ──────────────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        _logger.LogDebug("GBM average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 6. Magnitude linear regressor ───────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, effectiveFeatureCount, hp);

        // ── 7. Evaluation on held-out test set ──────────────────────────────
        var finalMetrics = EvaluateGbm(
            testSet, trees, baseLogOdds, lr, magWeights, magBias, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList, optimalThreshold);

        _logger.LogInformation(
            "GBM eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE ──────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        _logger.LogInformation("GBM deployed-stack ECE={Ece:F4}", ece);

        // ── 10. Permutation feature importance ──────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList, ct, trainingRandomSeed)
            : new float[effectiveFeatureCount];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: idx < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[idx] : $"F{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "GBM top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── Item 39: Gain-weighted tree split importance ─────────────────────
        var gainWeightedImportance = ComputeGainWeightedImportance(trees, effectiveFeatureCount);

        // ── 10b. Calibration-set importance (for warm-start transfer) ───────
        double[] calImportanceScores = calibrationEvalSet.Count >= 10
            ? ComputeCalPermutationImportance(calibrationEvalSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList, optimalThreshold, ct, trainingRandomSeed)
            : new double[effectiveFeatureCount];

        // ── 11. Feature pruning re-train pass (Item 15: zero-mask retrain) ───
        var activeMask = inheritedActiveMask is { Length: > 0 }
            ? (bool[])inheritedActiveMask.Clone()
            : BuildFeatureMask(featureImportance, hp.MinFeatureImportance, effectiveFeatureCount);
        int prunedCount = activeMask.Count(m => !m);

        if (!inheritedFeatureLayout && prunedCount > 0 && effectiveFeatureCount - prunedCount >= 10)
        {
            _logger.LogInformation("GBM feature pruning: removing {Pruned}/{Total} low-importance features",
                prunedCount, effectiveFeatureCount);

            var maskedTrain = ApplyFeatureMask(trainSet, activeMask);
            var maskedCal   = ApplyFeatureMask(calSet, activeMask);
            var maskedCalibrationFit = ApplyFeatureMask(calibrationFitSet, activeMask);
            var maskedCalibrationDiagnostics = ApplyFeatureMask(calibrationDiagnosticsSet, activeMask);
            var maskedConformal = ApplyFeatureMask(conformalSet, activeMask);
            var maskedMetaLabel = ApplyFeatureMask(metaLabelSet, activeMask);
            var maskedAbstention = ApplyFeatureMask(abstentionSet, activeMask);
            var maskedTest  = ApplyFeatureMask(testSet, activeMask);
            var maskedCalibrationEval = maskedCalibrationDiagnostics.Count > 0 ? maskedCalibrationDiagnostics : maskedCalibrationFit;

            int prunedRounds = Math.Max(10, numRounds / 2);
            var (pTrees, pBLO, pBagMasks, pInnerTrainCount, pPerTreeLr) = FitGbmEnsemble(maskedTrain, effectiveFeatureCount, prunedRounds, maxDepth, lr,
                effectiveLabelSmoothing, null, densityWeights, hp, ct, interactionConstraints, trainingRandomSeed + 1);
            var (pA, pB) = FitPlattScaling(maskedCalibrationFit, pTrees, pBLO, lr, effectiveFeatureCount, pPerTreeLr);
            var pGlobalCalibrationState = new GbmCalibrationState(
                GlobalPlattA: pA,
                GlobalPlattB: pB,
                TemperatureScale: 0.0,
                PlattABuy: 0.0,
                PlattBBuy: 0.0,
                PlattASell: 0.0,
                PlattBSell: 0.0,
                ConditionalRoutingThreshold: 0.5,
                IsotonicBreakpoints: []);
            var pGlobalCalibrationSnapshot = CreateCalibrationSnapshot(pGlobalCalibrationState);
            double pSelectedGlobalNll = ComputeCalibrationNll(
                maskedCalibrationEval, pTrees, pBLO, lr, effectiveFeatureCount, pGlobalCalibrationSnapshot, pPerTreeLr);
            double pTemp = 0.0;
            if (hp.FitTemperatureScale && maskedCalibrationFit.Count >= 10)
            {
                double pCandidateTemperature = FitTemperatureScaling(maskedCalibrationFit, pTrees, pBLO, lr, effectiveFeatureCount, pPerTreeLr);
                var pCandidateGlobalSnapshot = CreateCalibrationSnapshot(pGlobalCalibrationState with { TemperatureScale = pCandidateTemperature });
                double pCandidateTemperatureNll = ComputeCalibrationNll(
                    maskedCalibrationEval, pTrees, pBLO, lr, effectiveFeatureCount, pCandidateGlobalSnapshot, pPerTreeLr);
                if (pCandidateTemperatureNll + 1e-6 < pSelectedGlobalNll)
                {
                    pTemp = pCandidateTemperature;
                    pSelectedGlobalNll = pCandidateTemperatureNll;
                    pGlobalCalibrationState = pGlobalCalibrationState with { TemperatureScale = pCandidateTemperature };
                    pGlobalCalibrationSnapshot = pCandidateGlobalSnapshot;
                }
            }
            double pRoutingThreshold = DetermineConditionalRoutingThreshold(
                maskedCalibrationFit,
                maskedCalibrationEval,
                pTrees,
                pBLO,
                lr,
                effectiveFeatureCount,
                pGlobalCalibrationSnapshot,
                pPerTreeLr);
            var pConditionalFit = FitClassConditionalPlatt(
                maskedCalibrationFit, pTrees, pBLO, lr, effectiveFeatureCount, pPerTreeLr, pRoutingThreshold, pGlobalCalibrationSnapshot);
            double pABuy = pConditionalFit.Buy.A;
            double pBBuy = pConditionalFit.Buy.B;
            double pASell = pConditionalFit.Sell.A;
            double pBSell = pConditionalFit.Sell.B;
            var pCalibrationState = new GbmCalibrationState(
                GlobalPlattA: pA,
                GlobalPlattB: pB,
                TemperatureScale: pTemp,
                PlattABuy: pABuy,
                PlattBBuy: pBBuy,
                PlattASell: pASell,
                PlattBSell: pBSell,
                ConditionalRoutingThreshold: pRoutingThreshold,
                IsotonicBreakpoints: []);
            var pCalibrationSnapshot = CreateCalibrationSnapshot(pCalibrationState);
            double[] pIsotonicBp = [];
            double[] pIsotonicCandidate = FitIsotonicCalibration(
                maskedCalibrationFit, pTrees, pBLO, lr, effectiveFeatureCount, pCalibrationSnapshot, pPerTreeLr);
            if (pIsotonicCandidate.Length >= 4)
            {
                var pCandidateCalibrationState = pCalibrationState with { IsotonicBreakpoints = pIsotonicCandidate };
                var pCandidateCalibrationSnapshot = CreateCalibrationSnapshot(pCandidateCalibrationState);
                double pPreIsotonicNll = ComputeCalibrationNll(
                    maskedCalibrationEval, pTrees, pBLO, lr, effectiveFeatureCount, pCalibrationSnapshot, pPerTreeLr);
                double pPostIsotonicNll = ComputeCalibrationNll(
                    maskedCalibrationEval, pTrees, pBLO, lr, effectiveFeatureCount, pCandidateCalibrationSnapshot, pPerTreeLr);
                if (pPostIsotonicNll <= pPreIsotonicNll + 1e-6)
                {
                    pIsotonicBp = pIsotonicCandidate;
                    pCalibrationState = pCandidateCalibrationState;
                    pCalibrationSnapshot = pCandidateCalibrationSnapshot;
                }
            }
            double pOptimalThreshold = ComputeOptimalThreshold(
                maskedCalibrationEval, pTrees, pBLO, lr, effectiveFeatureCount, pCalibrationSnapshot, pPerTreeLr,
                hp.ThresholdSearchMin, hp.ThresholdSearchMax, hp.GbmEvThresholdSpreadCost, hp.ThresholdSearchStepBps);
            var (pmw, pmb) = FitLinearRegressor(maskedTrain, effectiveFeatureCount, hp);
            var prunedMetrics = EvaluateGbm(
                maskedTest, pTrees, pBLO, lr, pmw, pmb, effectiveFeatureCount,
                pCalibrationSnapshot, pPerTreeLr, pOptimalThreshold);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation("GBM pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                trees        = pTrees;    baseLogOdds = pBLO;
                magWeights   = pmw;       magBias     = pmb;
                plattA       = pA;        plattB      = pB;
                plattABuy    = pABuy;     plattBBuy   = pBBuy;
                plattASell   = pASell;    plattBSell  = pBSell;
                temperatureScale = pTemp;
                isotonicBp = pIsotonicBp;
                calibrationState = pCalibrationState;
                calibrationSnapshot = pCalibrationSnapshot;
                finalMetrics = prunedMetrics;
                treeBagMasks     = pBagMasks;
                innerTrainCount  = pInnerTrainCount;
                perTreeLrList    = pPerTreeLr;
                trainSet         = maskedTrain;
                calSet           = maskedCal;
                calibrationFitSet = maskedCalibrationFit;
                calibrationDiagnosticsSet = maskedCalibrationDiagnostics;
                conformalSet = maskedConformal;
                metaLabelSet = maskedMetaLabel;
                abstentionSet = maskedAbstention;
                testSet          = maskedTest;
                avgKellyFraction = ComputeAvgKellyFraction(maskedCalibrationEval, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
                ece = ComputeEce(maskedTest, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
                optimalThreshold = pOptimalThreshold;
                gainWeightedImportance = ComputeGainWeightedImportance(trees, effectiveFeatureCount);
                featureImportance = maskedTest.Count >= 10
                    ? ComputePermutationImportance(maskedTest, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList, ct, trainingRandomSeed)
                    : new float[effectiveFeatureCount];
                calImportanceScores = maskedCalibrationEval.Count >= 10
                    ? ComputeCalPermutationImportance(maskedCalibrationEval, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList, optimalThreshold, ct, trainingRandomSeed)
                    : new double[effectiveFeatureCount];
            }
            else
            {
                _logger.LogInformation("GBM pruned model rejected — keeping full model");
                prunedCount = 0;
                activeMask = BuildAllTrueMask(effectiveFeatureCount);
            }
        }
        else if (!inheritedFeatureLayout)
        {
            if (prunedCount > 0)
            {
                _logger.LogInformation(
                    "GBM feature pruning skipped: {Remaining} active features would remain, below the minimum deployable threshold of 10.",
                    effectiveFeatureCount - prunedCount);
            }

            prunedCount = 0;
            activeMask = BuildAllTrueMask(effectiveFeatureCount);
        }

        var postPruneCalibrationDiagnosticsSet = calibrationDiagnosticsSet.Count > 0 ? calibrationDiagnosticsSet : calibrationFitSet;

        // ── Item 7: Venn-ABERS calibration ──────────────────────────────────
        var vennAbersMultiP = ComputeVennAbers(postPruneCalibrationDiagnosticsSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);

        // ── Conformal (Item 8: probability-space nonconformity scores) ─────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        var (conformalQHat, conformalQHatBuy, conformalQHatSell) = ComputeConformalQHats(
            conformalSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList, conformalAlpha);
        _logger.LogInformation(
            "GBM conformal qHat={QHat:F4} buy={BuyQ:F4} sell={SellQ:F4} ({Cov:P0} coverage)",
            conformalQHat, conformalQHatBuy, conformalQHatSell, hp.ConformalCoverage);

        // ── 11c. OOB accuracy (true OOB using per-tree bag membership) ─────
        var oobTrainSet = trainSet[..Math.Min(innerTrainCount, trainSet.Count)];
        double oobAccuracy = ComputeOobAccuracy(
            oobTrainSet, trees, treeBagMasks, baseLogOdds, lr, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList, optimalThreshold);
        _logger.LogInformation("GBM OOB accuracy={OobAcc:P1}", oobAccuracy);
        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── 11d. Jackknife+ residuals (Item 9: coverage validation) ─────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(
            oobTrainSet, trees, treeBagMasks, baseLogOdds, lr, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList);
        _logger.LogInformation("GBM Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);
        double jackknifeCoverage = ValidateJackknifeCoverage(
            postPruneCalibrationDiagnosticsSet, trees, baseLogOdds, lr, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList, jackknifeResiduals, conformalAlpha);
        _logger.LogInformation("GBM Jackknife+ empirical coverage={Cov:P1} (target={Target:P0})", jackknifeCoverage, hp.ConformalCoverage);

        // ── 11e. Meta-label model (Item 20: MLP, Item 3: top-importance features) ──
        var topFeatureIndices = featureImportance
            .Select((imp, idx) => (imp, idx))
            .OrderByDescending(x => x.imp)
            .Take(3)
            .Select(x => x.idx)
            .ToArray();
        var (metaLabelWeights, metaLabelBias, metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim) = FitMetaLabelNetwork(
            metaLabelSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList,
            optimalThreshold, topFeatureIndices, hp.GbmMetaLabelHiddenDim, trainingRandomSeed);
        _logger.LogDebug("GBM meta-label model: bias={B:F4}", metaLabelBias);

        // ── 11f. Abstention gate (Items 21,22,24: finer sweep, coverage curve, separate buy/sell) ──
        var (abstentionWeights, abstentionBias, abstentionThreshold, abstentionThresholdBuy, abstentionThresholdSell, coverageAccCurve) =
            FitAbstentionModel(
                abstentionSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList,
                metaLabelWeights, metaLabelBias, metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim,
                optimalThreshold, topFeatureIndices, hp.GbmUseSeparateAbstention);
        _logger.LogDebug("GBM abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── 11g. Quantile magnitude regressor (Item 44: Adam + early stopping) ──
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, effectiveFeatureCount, hp.MagnitudeQuantileTau, hp);
            _logger.LogDebug("GBM quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 11h. Decision boundary stats + Item 38: prediction stability ────
        var (dbMean, dbStd) = postPruneCalibrationDiagnosticsSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalibrationDiagnosticsSet, trees, baseLogOdds, lr, effectiveFeatureCount, perTreeLrList)
            : (0.0, 0.0);
        double predictionStability = testSet.Count >= 10
            ? ComputePredictionStability(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, perTreeLrList)
            : 0.0;
        _logger.LogDebug("GBM decision boundary: mean={Mean:F4} std={Std:F4} stability={Stab:F4}", dbMean, dbStd, predictionStability);

        // ── 11i. Durbin-Watson on magnitude residuals (Item 37: diagnostic) ──
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, effectiveFeatureCount);
        _logger.LogDebug("GBM Durbin-Watson={DW:F4}", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
        {
            _logger.LogWarning("GBM magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2})",
                durbinWatson, hp.DurbinWatsonThreshold);
        }

        // ── 11j. MI redundancy (Item 35: drop recommendation) ───────────────
        string[] redundantPairs = [];
        int[] redundantDropIndices = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            (redundantPairs, redundantDropIndices) = ComputeRedundantFeaturePairs(trainSet, effectiveFeatureCount, hp.MutualInfoRedundancyThreshold, featureImportance);
            if (redundantPairs.Length > 0)
                _logger.LogWarning("GBM MI redundancy: {N} pairs exceed threshold", redundantPairs.Length);
        }

        // ── 11l. Brier Skill Score + Item 36: Murphy decomposition ──────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        var (calibrationLoss, refinementLoss) = ComputeMurphyDecomposition(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        _logger.LogInformation("GBM BSS={BSS:F4} calLoss={Cal:F4} refLoss={Ref:F4}", brierSkillScore, calibrationLoss, refinementLoss);

        // ── 11m. PSI baseline ───────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 11n. NaN/Inf tree sanitization ──────────────────────────────────
        int sanitizedCount = SanitizeTrees(trees);
        if (sanitizedCount > 0)
            _logger.LogWarning("GBM sanitized {N}/{Total} trees with non-finite values.", sanitizedCount, trees.Count);

        // ── Item 31: TreeSHAP baseline ──────────────────────────────────────
        double treeShapBaseline = ComputeTreeShapBaseline(trees, baseLogOdds, lr, trainSet, effectiveFeatureCount);

        // ── Item 32: Partial dependence data ────────────────────────────────
        var partialDependenceData = ComputePartialDependence(trainSet, trees, baseLogOdds, lr, effectiveFeatureCount, topFeatureIndices);

        int rawTrainCount = trainEnd;
        int rawCalibrationCount = Math.Max(0, calEnd - trainEnd);
        int rawTestCount = Math.Max(0, allStd.Count - calEnd);
        int splitCalibrationStartIndex = Math.Min(trainEnd + embargo, allStd.Count);
        int testStartIndex = Math.Min(calEnd + embargo, allStd.Count);
        var splitSummary = new TrainingSplitSummary
        {
            RawTrainCount = rawTrainCount,
            RawSelectionCount = rawCalibrationCount,
            RawCalibrationCount = rawCalibrationCount,
            RawTestCount = rawTestCount,
            TrainStartIndex = 0,
            TrainCount = trainSet.Count,
            SelectionStartIndex = splitCalibrationStartIndex,
            SelectionCount = calSet.Count,
            CalibrationStartIndex = splitCalibrationStartIndex,
            CalibrationCount = calSet.Count,
            CalibrationFitStartIndex = calibrationPartition.FitStartIndex,
            CalibrationFitCount = calibrationFitSet.Count,
            CalibrationDiagnosticsStartIndex = calibrationPartition.DiagnosticsStartIndex,
            CalibrationDiagnosticsCount = calibrationDiagnosticsSet.Count,
            ConformalStartIndex = calibrationPartition.ConformalStartIndex,
            ConformalCount = conformalSet.Count,
            MetaLabelStartIndex = calibrationPartition.MetaLabelStartIndex,
            MetaLabelCount = metaLabelSet.Count,
            AbstentionStartIndex = calibrationPartition.AbstentionStartIndex,
            AbstentionCount = abstentionSet.Count,
            AdaptiveHeadSplitMode = calibrationPartition.AdaptiveHeadSplitMode,
            AdaptiveHeadCrossFitFoldCount = 0,
            TestStartIndex = testStartIndex,
            TestCount = testSet.Count,
            EmbargoCount = embargo,
            TrainEmbargoDropped = rawTrainCount - trainSet.Count,
            SelectionEmbargoDropped = rawCalibrationCount - calSet.Count,
            CalibrationEmbargoDropped = rawCalibrationCount - calSet.Count,
        };

        var calibrationArtifact = BuildCalibrationArtifact(
            calibrationFitSet,
            calibrationDiagnosticsSet,
            conformalSet,
            metaLabelSet,
            abstentionSet,
            splitSummary.AdaptiveHeadSplitMode,
            splitSummary.AdaptiveHeadCrossFitFoldCount,
            trees,
            baseLogOdds,
            lr,
            effectiveFeatureCount,
            calibrationState,
            perTreeLrList);

        string preprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(
            featureCount,
            rawFeatureIndices,
            featurePipelineDescriptors,
            activeMask);

        CheckTimeoutBudget(trainingStopwatch, hp.TrainingTimeoutMinutes, "before serialization"); // Item 40

        // ── 12. Serialise model snapshot (Item 45: compact tree serialization) ──
        CompactTreeNodes(trees); // remove placeholder nodes

        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = snapshotFeatureNames,
            RawFeatureIndices          = rawFeatureIndices,
            FeatureSchemaFingerprint   = featureSchemaFingerprint,
            PreprocessingFingerprint   = preprocessingFingerprint,
            TrainerFingerprint         = trainerFingerprint,
            TrainingRandomSeed         = trainingRandomSeed,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = trees.Count,
            Weights                    = [],
            Biases                     = [],
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TrainSamplesAtLastCalibration = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = DateTime.UtcNow,
            TrainingSplitSummary       = splitSummary,
            GbmCalibrationArtifact     = calibrationArtifact,
            FeaturePipelineDescriptors = featurePipelineDescriptors,
            FeatureImportance          = featureImportance,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            IsotonicBreakpoints        = isotonicBp,
            OobAccuracy                = oobAccuracy,
            ConformalQHat              = conformalQHat,
            ConformalQHatBuy           = conformalQHatBuy,
            ConformalQHatSell          = conformalQHatSell,
            FracDiffD                  = hp.FracDiffD,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            MetaLabelTopFeatureIndices = topFeatureIndices,
            MetaLabelHiddenWeights     = metaLabelHiddenWeights,
            MetaLabelHiddenBiases      = metaLabelHiddenBiases,
            MetaLabelHiddenDim         = metaLabelHiddenDim,
            JackknifeResiduals         = jackknifeResiduals,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            FeatureImportanceScores    = calImportanceScores,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            AbstentionThresholdBuy     = abstentionThresholdBuy,
            AbstentionThresholdSell    = abstentionThresholdSell,
            AbstentionCoverageAccuracyCurve = coverageAccCurve,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = dbMean,
            DecisionBoundaryStd        = dbStd,
            DurbinWatsonStatistic      = durbinWatson,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            ConditionalCalibrationRoutingThreshold = calibrationState.ConditionalRoutingThreshold,
            AvgKellyFraction           = avgKellyFraction,
            RedundantFeaturePairs      = redundantPairs,
            RedundantFeatureDropIndices = redundantDropIndices,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            BrierSkillScore            = brierSkillScore,
            CalibrationLoss            = calibrationLoss,
            RefinementLoss             = refinementLoss,
            TrainedAtUtc               = DateTime.UtcNow,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            SanitizedLearnerCount      = sanitizedCount,
            AdaptiveLabelSmoothing     = effectiveLabelSmoothing,
            ConformalCoverage          = hp.ConformalCoverage,
            GbmTreesJson              = JsonSerializer.Serialize(trees, JsonOptions),
            GbmBaseLogOdds            = baseLogOdds,
            GbmLearningRate           = lr,
            GbmPerTreeLearningRates   = perTreeLrList.ToArray(),
            VennAbersMultiP           = vennAbersMultiP,
            PredictionStabilityScore  = predictionStability,
            TreeShapBaseline          = treeShapBaseline,
            GainWeightedImportance    = gainWeightedImportance,
            PartialDependenceData     = partialDependenceData,
            JackknifeCoverage         = jackknifeCoverage,
        };

        var snapshotValidation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);
        if (!snapshotValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"GBM snapshot validation failed before serialization: {string.Join("; ", snapshotValidation.Issues)}");
        }

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        _logger.LogInformation(
            "GbmModelTrainer complete: {R} trees, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}, elapsed={Elapsed}ms",
            trees.Count, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore, trainingStopwatch.ElapsedMilliseconds);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GBM ENSEMBLE FITTING
    // ═══════════════════════════════════════════════════════════════════════

    private (List<GbmTree> Trees, double BaseLogOdds, List<HashSet<int>> TreeBagMasks, int InnerTrainCount, List<double> PerTreeLr) FitGbmEnsemble(
        List<TrainingSample> train,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        TrainingHyperparams  hp,
        CancellationToken    ct,
        int[][]?             interactionConstraints = null,
        int                  baseSeed = 0)
    {
        double temporalDecayLambda = hp.TemporalDecayLambda;
        double colSampleRatio      = hp.FeatureSampleRatio;
        double l2Lambda            = hp.L2Lambda;
        bool   useClassWeights     = hp.UseClassWeights;
        double rowSubsampleRatio   = hp.GbmRowSubsampleRatio;
        int    minSamplesLeaf      = hp.GbmMinSamplesLeaf > 0 ? hp.GbmMinSamplesLeaf : 4;
        double minSplitGain        = hp.GbmMinSplitGain;
        double minSplitGainDecay   = hp.GbmMinSplitGainDecayPerDepth;
        bool   shrinkageAnnealing  = hp.GbmShrinkageAnnealing;
        double dartDropRate        = hp.GbmDartDropRate;
        bool   useHistogram        = hp.GbmUseHistogramSplits;
        int    histogramBins       = hp.GbmHistogramBins > 0 ? hp.GbmHistogramBins : 256;
        bool   leafWise            = hp.GbmUseLeafWiseGrowth;
        int    maxLeaves           = hp.GbmMaxLeaves > 0 ? hp.GbmMaxLeaves : (1 << maxDepth);
        int    valCheckFreq        = hp.GbmValCheckFrequency > 0 ? hp.GbmValCheckFrequency : (numRounds < 30 ? 1 : 5);
        int    earlyStoppingPatience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : Math.Max(3, numRounds / 10);

        // Clamp valSize so inner trainSet always has at least 10 samples
        int valSize  = Math.Min(Math.Max(20, train.Count / 10), Math.Max(0, train.Count - 10));
        if (valSize < 5) valSize = 0;
        var valSet   = valSize > 0 ? train[^valSize..] : new List<TrainingSample>();
        var trainSet = valSize > 0 ? train[..^valSize] : train;

        // Temporal + density blended weights
        var temporalWeights = ComputeTemporalWeights(trainSet.Count, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= temporalWeights.Length)
        {
            double sum = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                temporalWeights[i] *= densityWeights[i];
                sum += temporalWeights[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < temporalWeights.Length; i++) temporalWeights[i] /= sum;
        }

        int n = trainSet.Count;

        // Class weights
        double classWeightBuy  = 1.0;
        double classWeightSell = 1.0;
        if (useClassWeights)
        {
            int buyCount  = trainSet.Count(s => s.Direction > 0);
            int sellCount = n - buyCount;
            if (buyCount > 0 && sellCount > 0)
            {
                classWeightBuy  = (double)n / (2.0 * buyCount);
                classWeightSell = (double)n / (2.0 * sellCount);
            }
        }

        // Row subsampling (configurable)
        double rowSubsampleFrac = rowSubsampleRatio is > 0.0 and <= 1.0 ? rowSubsampleRatio : 0.8;
        int rowSubsampleCount   = Math.Max(10, (int)(n * rowSubsampleFrac));

        // Column subsampling
        bool useColSubsample = colSampleRatio > 0.0 && colSampleRatio < 1.0;
        int colSubsampleCount = useColSubsample
            ? Math.Max(1, (int)Math.Ceiling(colSampleRatio * featureCount))
            : featureCount;

        // Base rate log-odds
        double basePosRate = n > 0
            ? (double)trainSet.Count(s => s.Direction > 0) / n
            : 0.5;
        basePosRate = Math.Clamp(basePosRate, 1e-6, 1 - 1e-6);
        double baseLogOdds = Math.Log(basePosRate / (1 - basePosRate));

        // Warm-start: load prior trees (Item 48: with pruning)
        var trees = new List<GbmTree>(numRounds);
        var perTreeLr = new List<double>(numRounds); // per-tree effective learning rates

        if (warmStart?.GbmTreesJson is not null && warmStart.Type == ModelType)
        {
            bool versionCompatible = TryParseVersion(warmStart.Version, out var warmVersion)
                && warmVersion >= new Version(2, 1);
            if (!versionCompatible)
            {
                _logger.LogWarning(
                    "GBM warm-start: discarding prior trees from version {V} (leaf sign incompatible with ≥2.1)",
                    warmStart.Version);
            }
            else
            {
                try
                {
                    var priorTrees = JsonSerializer.Deserialize<List<GbmTree>>(warmStart.GbmTreesJson, JsonOptions);
                    if (priorTrees is { Count: > 0 })
                    {
                        // Item 48: prune tail of warm-start trees if max configured
                        int maxWarmStart = hp.GbmMaxWarmStartTrees > 0
                            ? hp.GbmMaxWarmStartTrees
                            : priorTrees.Count;
                        if (priorTrees.Count > maxWarmStart)
                        {
                            _logger.LogInformation("GBM warm-start: pruning {Old}→{New} prior trees",
                                priorTrees.Count, maxWarmStart);
                            priorTrees = priorTrees[..maxWarmStart];
                        }
                        trees.AddRange(priorTrees);

                        // Restore per-tree LRs from warm-start, or assume uniform
                        if (warmStart.GbmPerTreeLearningRates is { Length: > 0 } wsLr)
                        {
                            int count = Math.Min(wsLr.Length, trees.Count);
                            for (int ti = 0; ti < count; ti++) perTreeLr.Add(wsLr[ti]);
                            // Pad if warm-start had fewer LRs than trees
                            while (perTreeLr.Count < trees.Count) perTreeLr.Add(learningRate);
                        }
                        else
                        {
                            for (int ti = 0; ti < trees.Count; ti++) perTreeLr.Add(learningRate);
                        }

                        _logger.LogInformation("GBM warm-start: loaded {N} prior trees (gen={Gen})",
                            trees.Count, warmStart.GenerationNumber);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "GBM warm-start: failed to deserialise prior trees, starting fresh.");
                }
            }
        }

        // Initialise scores using per-tree LRs
        double[] scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            scores[i] = baseLogOdds;
            for (int ti = 0; ti < trees.Count; ti++)
                scores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
        }

        // Item 19: early stopping baseline accounts for warm-start quality on current data
        double bestValLoss = double.MaxValue;
        int patience = 0;
        int bestRound = trees.Count;
        var bestTrees = new List<GbmTree>(trees);
        var bestPerTreeLr = new List<double>(perTreeLr);

        // Compute initial warm-start val loss so we have a proper baseline (Item 19)
        if (valSet.Count >= 10 && trees.Count > 0)
        {
            bestValLoss = ComputeValLoss(valSet, trees, perTreeLr, baseLogOdds);
            bestRound = trees.Count;
            bestTrees = [..trees];
            bestPerTreeLr = [..perTreeLr];
        }

        // Item 25: replay warm-start bag masks with full trainSet coverage
        var bagMasks = new List<HashSet<int>>(numRounds);
        for (int w = 0; w < trees.Count; w++)
        {
            // Warm-start trees were fit on a prior generation. Relative to the current train window
            // they behave like externally supplied predictors, so treat them as OOB for replay metrics.
            bagMasks.Add([]);
        }
        var bestBagMasks = new List<HashSet<int>>(bagMasks);

        // Precompute histogram bins if using histogram-based splits (Item 1)
        int[][]? histBins = null;
        double[][]? histBinEdges = null;
        if (useHistogram)
        {
            (histBins, histBinEdges) = PrecomputeHistogramBins(trainSet, featureCount, histogramBins);
        }

        // CDF for weighted row sampling (Item 4: stall guard)
        var cdf = new double[n];
        cdf[0] = temporalWeights[0];
        for (int i = 1; i < n; i++) cdf[i] = cdf[i - 1] + temporalWeights[i];
        bool useWeightedSampling = rowSubsampleCount < n && cdf[^1] > 1e-15;

        // DART: track active tree mask
        var dartActiveFlags = new bool[numRounds + trees.Count];
        Array.Fill(dartActiveFlags, true);

        for (int r = 0; r < numRounds && !ct.IsCancellationRequested; r++)
        {
            // Item 47: fine-grained cancellation check
            ct.ThrowIfCancellationRequested();

            // Item 16: shrinkage annealing
            double effectiveLr = shrinkageAnnealing
                ? learningRate * (1.0 - (double)r / numRounds)
                : learningRate;
            effectiveLr = Math.Max(effectiveLr, learningRate * 0.01); // floor at 1% of base

            // Item 13: DART — randomly drop trees
            double[] dartScores = scores;
            HashSet<int>? droppedTrees = null;
            if (dartDropRate > 0.0 && trees.Count > 1)
            {
                var dartRng = CreateSeededRandom(baseSeed, r * 97 + 13);
                droppedTrees = new HashSet<int>();
                for (int ti = 0; ti < trees.Count; ti++)
                {
                    if (dartRng.NextDouble() < dartDropRate) droppedTrees.Add(ti);
                }
                if (droppedTrees.Count > 0 && droppedTrees.Count < trees.Count)
                {
                    dartScores = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        dartScores[i] = baseLogOdds;
                        for (int ti = 0; ti < trees.Count; ti++)
                        {
                            if (!droppedTrees.Contains(ti))
                                dartScores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
                        }
                    }
                }
                else droppedTrees = null;
            }

            // Row subsampling (Item 4: stall guard with fallback cap)
            var rng = CreateSeededRandom(baseSeed, r * 31 + 7);
            int[] rowSample;
            if (rowSubsampleCount < n)
            {
                if (useWeightedSampling)
                {
                    var selected = new HashSet<int>(rowSubsampleCount);
                    int maxAttempts = rowSubsampleCount * 3; // Item 4: stall guard
                    int attempts = 0;
                    while (selected.Count < rowSubsampleCount && attempts < maxAttempts)
                    {
                        double u = rng.NextDouble() * cdf[^1];
                        int lo = 0, hi = n - 1;
                        while (lo < hi)
                        {
                            int mid = (lo + hi) >> 1;
                            if (cdf[mid] < u) lo = mid + 1; else hi = mid;
                        }
                        selected.Add(lo);
                        attempts++;
                    }
                    // Pad with uniform if stalled
                    while (selected.Count < rowSubsampleCount)
                        selected.Add(rng.Next(n));
                    rowSample = [..selected];
                }
                else
                {
                    var allIdx = new int[n];
                    for (int i = 0; i < n; i++) allIdx[i] = i;
                    for (int i = 0; i < rowSubsampleCount; i++)
                    {
                        int j = i + rng.Next(n - i);
                        (allIdx[i], allIdx[j]) = (allIdx[j], allIdx[i]);
                    }
                    rowSample = allIdx[..rowSubsampleCount];
                }
            }
            else
            {
                rowSample = Enumerable.Range(0, n).ToArray();
            }

            bagMasks.Add(new HashSet<int>(rowSample));

            // Column subsampling (with interaction constraints — Item 18)
            int[] colSample;
            if (useColSubsample)
            {
                var allCols = new int[featureCount];
                for (int i = 0; i < featureCount; i++) allCols[i] = i;
                for (int i = 0; i < colSubsampleCount; i++)
                {
                    int j = i + rng.Next(featureCount - i);
                    (allCols[i], allCols[j]) = (allCols[j], allCols[i]);
                }
                colSample = allCols[..colSubsampleCount];
                Array.Sort(colSample);
            }
            else
            {
                colSample = Enumerable.Range(0, featureCount).ToArray();
            }

            // Compute pseudo-residuals (use dartScores if DART active)
            var activeScores = droppedTrees is not null ? dartScores : scores;
            var residuals     = new double[n];
            var hessians      = new double[n];
            var sampleWeights = new double[n];
            for (int i = 0; i < n; i++)
            {
                double p = Sigmoid(activeScores[i]);
                int rawY = trainSet[i].Direction > 0 ? 1 : 0;
                double y = labelSmoothing > 0
                    ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                    : rawY;
                double cw = trainSet[i].Direction > 0 ? classWeightBuy : classWeightSell;
                residuals[i]     = (y - p) * cw;
                hessians[i]      = p * (1 - p) * cw;
                sampleWeights[i] = temporalWeights[i];
            }

            // Item 14: depth-decayed min split gain
            double scaledMinCW = n > 0 ? MinChildWeight / n : MinChildWeight;
            var indices = rowSample.ToList();
            var tree = new GbmTree();

            if (leafWise)
            {
                // Item 2: Leaf-wise (best-first) tree growth
                BuildTreeLeafWise(tree, indices, trainSet, residuals, hessians, sampleWeights,
                    colSample, maxDepth, l2Lambda, scaledMinCW, minSamplesLeaf,
                    minSplitGain, minSplitGainDecay, maxLeaves,
                    interactionConstraints, useHistogram ? histBins : null,
                    useHistogram ? histBinEdges : null, histogramBins, ct);
            }
            else
            {
                BuildTree(tree, indices, trainSet, residuals, hessians, sampleWeights,
                    colSample, maxDepth, l2Lambda, scaledMinCW, minSamplesLeaf,
                    minSplitGain, minSplitGainDecay, interactionConstraints,
                    useHistogram ? histBins : null, useHistogram ? histBinEdges : null,
                    histogramBins, ct);
            }

            trees.Add(tree);
            ClipLeafValues(tree, LeafClipValue);

            // DART: correct rescaling per the DART paper
            // New tree: scale by 1/(D+1), each dropped tree: scale by D/(D+1)
            if (droppedTrees is { Count: > 0 })
            {
                int D = droppedTrees.Count;
                double newTreeScale = 1.0 / (D + 1);
                double droppedTreeScale = (double)D / (D + 1);
                ScaleTreeLeaves(tree, newTreeScale);
                foreach (int di in droppedTrees)
                    ScaleTreeLeaves(trees[di], droppedTreeScale);

                // Bug fix 1: recompute scores from scratch after DART rescaling
                // to avoid drift between scores[] and actual tree outputs
                perTreeLr.Add(effectiveLr);
                for (int i = 0; i < n; i++)
                {
                    scores[i] = baseLogOdds;
                    for (int ti = 0; ti < trees.Count; ti++)
                        scores[i] += perTreeLr[ti] * Predict(trees[ti], trainSet[i].Features);
                }
            }
            else
            {
                // Non-DART: record per-tree LR and incrementally update scores
                perTreeLr.Add(effectiveLr);
                for (int i = 0; i < n; i++)
                    scores[i] += effectiveLr * Predict(tree, trainSet[i].Features);
            }

            // Validation loss for early stopping (Item 17: configurable frequency)
            if (valSet.Count >= 10 && r % valCheckFreq == valCheckFreq - 1)
            {
                double valLoss = ComputeValLoss(valSet, trees, perTreeLr, baseLogOdds);

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss  = valLoss;
                    bestRound    = trees.Count;
                    bestTrees    = [..trees];
                    bestPerTreeLr = [..perTreeLr];
                    bestBagMasks = [..bagMasks];
                    patience     = 0;
                }
                else if (++patience >= earlyStoppingPatience)
                {
                    _logger.LogDebug("GBM early stopping at round {R} (best at {Best})", trees.Count, bestRound);
                    break;
                }
            }
        }

        // Restore best ensemble
        if (bestTrees.Count > 0 && bestTrees.Count < trees.Count)
        {
            trees     = bestTrees;
            bagMasks  = bestBagMasks;
            perTreeLr = bestPerTreeLr;
        }

        return (trees, baseLogOdds, bagMasks, trainSet.Count, perTreeLr);

        static bool TryParseVersion(string? rawVersion, out Version version)
        {
            if (Version.TryParse(rawVersion, out version!))
                return true;

            if (!string.IsNullOrWhiteSpace(rawVersion))
            {
                var numericPrefix = new string(rawVersion
                    .TakeWhile(c => char.IsDigit(c) || c == '.')
                    .ToArray());
                if (Version.TryParse(numericPrefix, out version!))
                    return true;
            }

            version = new Version(0, 0);
            return false;
        }
    }

    // ── Validation loss using per-tree learning rates ──────────────────────
    private static double ComputeValLoss(List<TrainingSample> valSet, List<GbmTree> trees,
        List<double> perTreeLr, double baseLogOdds)
    {
        double totalLoss = 0;
        foreach (var s in valSet)
        {
            double sc = baseLogOdds;
            for (int ti = 0; ti < trees.Count; ti++)
                sc += perTreeLr[ti] * Predict(trees[ti], s.Features);
            double p = Sigmoid(sc);
            int y    = s.Direction > 0 ? 1 : 0;
            totalLoss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
        }
        return totalLoss / valSet.Count;
    }

    // ── XGBoost-style constants ──────────────────────────────────────────────
    private const double MinChildWeight = 1.0;
    private const double LeafClipValue  = 5.0;

    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);
        int barsPerDay = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;

        if (foldSize < 50)
        {
            _logger.LogWarning("GBM walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];
        int[][]? interactionConstraints = null;
        if (!string.IsNullOrEmpty(hp.GbmInteractionConstraints))
        {
            try
            {
                interactionConstraints = JsonSerializer.Deserialize<int[][]>(hp.GbmInteractionConstraints);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "GBM CV: failed to parse interaction constraints, ignoring.");
            }
        }

        // Item 42: configurable CV parallelism; Item 46: deterministic = sequential
        int maxParallelism = hp.GbmDeterministic ? 1 : (hp.GbmCvMaxParallelism > 0 ? hp.GbmCvMaxParallelism : -1);
        var parallelOpts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallelism };

        Parallel.For(0, folds, parallelOpts, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) return;

            var foldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count)
                    foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            int cvCalSize = Math.Max(10, foldTrain.Count / 8);
            if (foldTrain.Count - cvCalSize < 20) return;
            var foldCal = foldTrain[^cvCalSize..];
            foldTrain = foldTrain[..^cvCalSize];
            if (foldTrain.Count < 20) return;

            double[]? foldDensityWeights = null;
            if (hp.DensityRatioWindowDays > 0 && foldTrain.Count >= 50)
                foldDensityWeights = ComputeDensityRatioImportanceWeights(foldTrain, featureCount, hp.DensityRatioWindowDays, barsPerDay);

            int cvRounds = Math.Max(10, numRounds / 3);
            var (cvTrees, cvBLO, _, _, cvPerTreeLr) = FitGbmEnsemble(
                foldTrain, featureCount, cvRounds, maxDepth, learningRate, hp.LabelSmoothing,
                null, foldDensityWeights, hp, ct, interactionConstraints);
            var (cvA, cvB) = FitPlattScaling(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr);
            double cvTemp = hp.FitTemperatureScale && foldCal.Count >= 10
                ? FitTemperatureScaling(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr)
                : 0.0;
            var cvGlobalCalibrationSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
                GlobalPlattA: cvA,
                GlobalPlattB: cvB,
                TemperatureScale: cvTemp,
                PlattABuy: 0.0,
                PlattBBuy: 0.0,
                PlattASell: 0.0,
                PlattBSell: 0.0,
                ConditionalRoutingThreshold: 0.5,
                IsotonicBreakpoints: []));
            int cvRoutingFitCount = Math.Max(10, foldCal.Count * 2 / 3);
            double cvRoutingThreshold = DetermineConditionalRoutingThreshold(
                foldCal[..Math.Min(cvRoutingFitCount, foldCal.Count)],
                foldCal[Math.Min(cvRoutingFitCount, foldCal.Count)..],
                cvTrees,
                cvBLO,
                learningRate,
                featureCount,
                cvGlobalCalibrationSnapshot,
                cvPerTreeLr);
            var cvConditionalFit = FitClassConditionalPlatt(
                foldCal, cvTrees, cvBLO, learningRate, featureCount, cvPerTreeLr, cvRoutingThreshold, cvGlobalCalibrationSnapshot);
            double cvABuy = cvConditionalFit.Buy.A;
            double cvBBuy = cvConditionalFit.Buy.B;
            double cvASell = cvConditionalFit.Sell.A;
            double cvBSell = cvConditionalFit.Sell.B;
            var cvCalibrationState = new GbmCalibrationState(
                GlobalPlattA: cvA,
                GlobalPlattB: cvB,
                TemperatureScale: cvTemp,
                PlattABuy: cvABuy,
                PlattBBuy: cvBBuy,
                PlattASell: cvASell,
                PlattBSell: cvBSell,
                ConditionalRoutingThreshold: cvRoutingThreshold,
                IsotonicBreakpoints: []);
            var cvCalibrationSnapshot = CreateCalibrationSnapshot(cvCalibrationState);
            double[] cvIsotonic = FitIsotonicCalibration(foldCal, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr);
            cvCalibrationState = cvCalibrationState with { IsotonicBreakpoints = cvIsotonic };
            cvCalibrationSnapshot = CreateCalibrationSnapshot(cvCalibrationState);
            double cvThreshold = ComputeOptimalThreshold(
                foldCal, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr,
                hp.ThresholdSearchMin, hp.ThresholdSearchMax, hp.GbmEvThresholdSpreadCost, hp.ThresholdSearchStepBps);
            var m = EvaluateGbm(foldTest, cvTrees, cvBLO, learningRate, [], 0, featureCount, cvCalibrationSnapshot, cvPerTreeLr, cvThreshold);

            var foldImpF = ComputeGainWeightedImportance(cvTrees, featureCount); // Item 39
            var foldImp = Array.ConvertAll(foldImpF, f => (double)f);

            var predictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int i = 0; i < foldTest.Count; i++)
            {
                double p = GbmCalibProb(foldTest[i].Features, cvTrees, cvBLO, learningRate, featureCount, cvCalibrationSnapshot, cvPerTreeLr);
                predictions[i] = (p >= cvThreshold ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(predictions);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        });

        // Aggregate
        var accList = new List<double>(folds);
        var f1List  = new List<double>(folds);
        var evList  = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds = 0;

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

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("GBM equity-curve gate: {BadFolds}/{TotalFolds} folds failed", badFolds, accList.Count);

        double avgAcc = accList.Average();
        double stdAcc = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("GBM Sharpe trend gate: slope={Slope:F3} < threshold", sharpeTrend);
            equityCurveGateFailed = true;
        }

        // Item 33: rank-dispersion feature stability across folds
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = ComputeRankStability(foldImportances, featureCount);
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

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE BUILDING — Level-wise (default)
    // ═══════════════════════════════════════════════════════════════════════

    private static void BuildTree(
        GbmTree tree, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int minSamplesLeaf = 4, double minSplitGain = 0.0,
        double minSplitGainDecay = 0.0, int[][]? interactionConstraints = null,
        int[][]? histBins = null, double[][]? histBinEdges = null, int histogramBinCount = 256,
        CancellationToken ct = default)
    {
        // Item 5: sequential node allocation (no gaps)
        tree.Nodes = new List<GbmNode>();
        BuildNodeSequential(tree.Nodes, indices, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, 0, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, null,
            histBins, histBinEdges, histogramBinCount, ct);
    }

    /// <summary>
    /// Builds nodes using sequential allocation (no heap gaps). Each node stores
    /// explicit LeftChild/RightChild indices into the flat Nodes list.
    /// </summary>
    private static void BuildNodeSequential(
        List<GbmNode> nodes, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int depth, int minSamplesLeaf, double minSplitGain,
        double minSplitGainDecay, int[][]? interactionConstraints,
        HashSet<int>? usedFeatureGroups,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        CancellationToken ct)
    {
        // Item 47: cancellation check in tree building
        if (ct.IsCancellationRequested) { AddLeafNode(nodes, 0); return; }

        int nodeIdx = nodes.Count;
        nodes.Add(new GbmNode());
        var node = nodes[nodeIdx];

        double G = 0, H = 0;
        foreach (int i in indices)
        {
            G += sampleWeights[i] * gradients[i];
            H += sampleWeights[i] * hessians[i];
        }

        double leafVal = (H + l2Lambda) > 1e-15 ? G / (H + l2Lambda) : 0;
        node.LeafValue = leafVal;

        if (depth >= maxDepth || indices.Count < minSamplesLeaf || H < minChildWeight)
        {
            node.IsLeaf = true;
            return;
        }

        // Item 14: depth-decayed min split gain
        double effectiveMinGain = minSplitGain;
        if (minSplitGainDecay > 0.0)
            effectiveMinGain = minSplitGain * Math.Pow(1.0 - minSplitGainDecay, depth);

        // Item 18: filter columns by interaction constraints
        int[] effectiveCols = colSubset;
        if (interactionConstraints is not null && usedFeatureGroups is not null)
        {
            effectiveCols = FilterByInteractionConstraints(colSubset, interactionConstraints, usedFeatureGroups);
            if (effectiveCols.Length == 0) effectiveCols = colSubset; // fallback
        }

        var (bestGain, bestFi, bestThresh) = histBins is not null
            ? FindBestSplitHistogram(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight, histBins, histBinEdges!, histogramBinCount)
            : FindBestSplitExact(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight);

        if (bestGain <= effectiveMinGain) { node.IsLeaf = true; return; }

        node.SplitFeature   = bestFi;
        node.SplitThreshold = bestThresh;
        node.SplitGain      = bestGain;

        var leftIdx  = indices.Where(i => samples[i].Features[bestFi] <= bestThresh).ToList();
        var rightIdx = indices.Where(i => samples[i].Features[bestFi] > bestThresh).ToList();

        if (leftIdx.Count < minSamplesLeaf || rightIdx.Count < minSamplesLeaf)
        {
            node.IsLeaf = true;
            return;
        }

        // Track used feature groups for interaction constraints
        var nextUsedGroups = usedFeatureGroups is not null ? new HashSet<int>(usedFeatureGroups) : new HashSet<int>();
        if (interactionConstraints is not null)
        {
            for (int g = 0; g < interactionConstraints.Length; g++)
                if (interactionConstraints[g].Contains(bestFi))
                    nextUsedGroups.Add(g);
        }

        node.LeftChild = nodes.Count; // will be next allocated
        BuildNodeSequential(nodes, leftIdx, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, nextUsedGroups,
            histBins, histBinEdges, histogramBinCount, ct);

        node.RightChild = nodes.Count; // will be next allocated
        BuildNodeSequential(nodes, rightIdx, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1, minSamplesLeaf, minSplitGain,
            minSplitGainDecay, interactionConstraints, nextUsedGroups,
            histBins, histBinEdges, histogramBinCount, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE BUILDING — Leaf-wise (best-first) (Item 2)
    // ═══════════════════════════════════════════════════════════════════════

    private static void BuildTreeLeafWise(
        GbmTree tree, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight,
        int minSamplesLeaf, double minSplitGain, double minSplitGainDecay,
        int maxLeaves, int[][]? interactionConstraints,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        CancellationToken ct)
    {
        tree.Nodes = new List<GbmNode>();

        // Priority queue caches (nodeIdx, indices, depth, usedGroups, splitFeature, splitThreshold, gain)
        // to avoid redundant split recomputation on dequeue
        var queue = new PriorityQueue<(int NodeIdx, List<int> Indices, int Depth, HashSet<int> UsedGroups, int SplitFi, double SplitThresh, double Gain), double>();
        int leafCount = 1;

        // Create root
        var root = CreateLeafNode(tree.Nodes, indices, gradients, hessians, sampleWeights, l2Lambda);
        var rootUsedGroups = new HashSet<int>();
        var (rootGain, rootFi, rootThresh) = FindBestSplitForNode(indices, samples, gradients, hessians,
            sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
            interactionConstraints, rootUsedGroups);
        if (rootGain > minSplitGain)
            queue.Enqueue((root, indices, 0, rootUsedGroups, rootFi, rootThresh, rootGain), -rootGain);

        while (queue.Count > 0 && leafCount < maxLeaves && !ct.IsCancellationRequested)
        {
            var (nodeIdx, nodeIndices, depth, usedGroups, fi, thresh, gain) = queue.Dequeue();
            var node = tree.Nodes[nodeIdx];

            double effectiveMinGain = minSplitGain;
            if (minSplitGainDecay > 0.0)
                effectiveMinGain = minSplitGain * Math.Pow(1.0 - minSplitGainDecay, depth);

            if (gain <= effectiveMinGain || depth >= maxDepth) continue;

            var leftIdx  = nodeIndices.Where(i => samples[i].Features[fi] <= thresh).ToList();
            var rightIdx = nodeIndices.Where(i => samples[i].Features[fi] > thresh).ToList();

            if (leftIdx.Count < minSamplesLeaf || rightIdx.Count < minSamplesLeaf) continue;

            node.IsLeaf = false;
            node.SplitFeature = fi;
            node.SplitThreshold = thresh;
            node.SplitGain = gain;
            node.LeftChild = tree.Nodes.Count;
            int leftNodeIdx = CreateLeafNode(tree.Nodes, leftIdx, gradients, hessians, sampleWeights, l2Lambda);
            node.RightChild = tree.Nodes.Count;
            int rightNodeIdx = CreateLeafNode(tree.Nodes, rightIdx, gradients, hessians, sampleWeights, l2Lambda);
            leafCount++; // net +1 (split one leaf into two)

            var nextUsedGroups = new HashSet<int>(usedGroups);
            if (interactionConstraints is not null)
            {
                for (int g = 0; g < interactionConstraints.Length; g++)
                {
                    if (interactionConstraints[g].Contains(fi))
                        nextUsedGroups.Add(g);
                }
            }

            // Enqueue children with pre-computed splits
            if (depth + 1 < maxDepth && leafCount < maxLeaves)
            {
                var (lGain, lFi, lThresh) = FindBestSplitForNode(leftIdx, samples, gradients, hessians,
                    sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
                    interactionConstraints, nextUsedGroups);
                if (lGain > 0)
                    queue.Enqueue((leftNodeIdx, leftIdx, depth + 1, new HashSet<int>(nextUsedGroups), lFi, lThresh, lGain), -lGain);

                var (rGain, rFi, rThresh) = FindBestSplitForNode(rightIdx, samples, gradients, hessians,
                    sampleWeights, colSubset, l2Lambda, minChildWeight, histBins, histBinEdges, histogramBinCount,
                    interactionConstraints, nextUsedGroups);
                if (rGain > 0)
                    queue.Enqueue((rightNodeIdx, rightIdx, depth + 1, new HashSet<int>(nextUsedGroups), rFi, rThresh, rGain), -rGain);
            }
        }
    }

    private static int CreateLeafNode(List<GbmNode> nodes, List<int> indices,
        double[] gradients, double[] hessians, double[] sampleWeights, double l2Lambda)
    {
        double G = 0, H = 0;
        foreach (int i in indices) { G += sampleWeights[i] * gradients[i]; H += sampleWeights[i] * hessians[i]; }
        int idx = nodes.Count;
        nodes.Add(new GbmNode { IsLeaf = true, LeafValue = (H + l2Lambda) > 1e-15 ? G / (H + l2Lambda) : 0 });
        return idx;
    }

    private static void AddLeafNode(List<GbmNode> nodes, double value)
    {
        nodes.Add(new GbmNode { IsLeaf = true, LeafValue = value });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SPLIT FINDING — Exact and Histogram
    // ═══════════════════════════════════════════════════════════════════════

    private static (double Gain, int Feature, double Threshold) FindBestSplitForNode(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double l2Lambda, double minChildWeight,
        int[][]? histBins, double[][]? histBinEdges, int histogramBinCount,
        int[][]? interactionConstraints = null, HashSet<int>? usedFeatureGroups = null)
    {
        double G = 0, H = 0;
        foreach (int i in indices) { G += sampleWeights[i] * gradients[i]; H += sampleWeights[i] * hessians[i]; }

        int[] effectiveCols = colSubset;
        if (interactionConstraints is not null && usedFeatureGroups is not null)
        {
            effectiveCols = FilterByInteractionConstraints(colSubset, interactionConstraints, usedFeatureGroups);
            if (effectiveCols.Length == 0)
                effectiveCols = colSubset;
        }

        return histBins is not null
            ? FindBestSplitHistogram(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight, histBins, histBinEdges!, histogramBinCount)
            : FindBestSplitExact(indices, samples, gradients, hessians, sampleWeights,
                effectiveCols, G, H, l2Lambda, minChildWeight);
    }

    /// <summary>Exact split search: O(n·m·log n) per node.</summary>
    private static (double Gain, int Feature, double Threshold) FindBestSplitExact(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double G, double H, double l2Lambda, double minChildWeight)
    {
        double bestGain = 0, bestThresh = 0;
        int bestFi = 0;
        double parentScore = G * G / (H + l2Lambda);
        var sortBuf = new int[indices.Count];

        foreach (int fi in colSubset)
        {
            indices.CopyTo(sortBuf);
            Array.Sort(sortBuf, 0, indices.Count,
                Comparer<int>.Create((a, b) => samples[a].Features[fi].CompareTo(samples[b].Features[fi])));

            double leftG = 0, leftH = 0;
            for (int ti = 0; ti < indices.Count - 1; ti++)
            {
                int idx = sortBuf[ti];
                double wi = sampleWeights[idx];
                leftG  += wi * gradients[idx];
                leftH  += wi * hessians[idx];
                double rightG = G - leftG, rightH = H - leftH;

                if (Math.Abs(samples[idx].Features[fi] - samples[sortBuf[ti + 1]].Features[fi]) < 1e-10)
                    continue;
                if (leftH < minChildWeight || rightH < minChildWeight) continue;

                double gain = 0.5 * (leftG * leftG / (leftH + l2Lambda)
                                   + rightG * rightG / (rightH + l2Lambda)
                                   - parentScore);

                if (gain > bestGain)
                {
                    bestGain = gain; bestFi = fi;
                    bestThresh = (samples[idx].Features[fi] + samples[sortBuf[ti + 1]].Features[fi]) / 2.0;
                }
            }
        }

        return (bestGain, bestFi, bestThresh);
    }

    /// <summary>Item 1: Histogram-based split search: O(n + bins·m) per node.</summary>
    private static (double Gain, int Feature, double Threshold) FindBestSplitHistogram(
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, double G, double H, double l2Lambda, double minChildWeight,
        int[][] histBins, double[][] histBinEdges, int numBins)
    {
        double bestGain = 0, bestThresh = 0;
        int bestFi = 0;
        double parentScore = G * G / (H + l2Lambda);

        foreach (int fi in colSubset)
        {
            var binG = new double[numBins];
            var binH = new double[numBins];

            foreach (int idx in indices)
            {
                int bin = histBins[fi][idx];
                double wi = sampleWeights[idx];
                binG[bin] += wi * gradients[idx];
                binH[bin] += wi * hessians[idx];
            }

            double leftG = 0, leftH = 0;
            for (int b = 0; b < numBins - 1; b++)
            {
                leftG += binG[b];
                leftH += binH[b];
                double rightG = G - leftG, rightH = H - leftH;

                if (leftH < minChildWeight || rightH < minChildWeight) continue;

                double gain = 0.5 * (leftG * leftG / (leftH + l2Lambda)
                                   + rightG * rightG / (rightH + l2Lambda)
                                   - parentScore);
                if (gain > bestGain)
                {
                    bestGain = gain; bestFi = fi;
                    bestThresh = histBinEdges[fi][b];
                }
            }
        }

        return (bestGain, bestFi, bestThresh);
    }

    /// <summary>Item 1: Precompute histogram bins for all features.</summary>
    private static (int[][] Bins, double[][] BinEdges) PrecomputeHistogramBins(
        List<TrainingSample> samples, int featureCount, int numBins)
    {
        int n = samples.Count;
        var bins = new int[featureCount][];
        var binEdges = new double[featureCount][];

        for (int fi = 0; fi < featureCount; fi++)
        {
            var values = new float[n];
            for (int i = 0; i < n; i++) values[i] = samples[i].Features[fi];

            float fmin = values.Min(), fmax = values.Max();
            double range = fmax - fmin;
            double binWidth = range > 1e-15 ? range / numBins : 1.0;

            bins[fi] = new int[n];
            binEdges[fi] = new double[numBins];
            for (int b = 0; b < numBins; b++)
                binEdges[fi][b] = fmin + (b + 1) * binWidth;

            for (int i = 0; i < n; i++)
                bins[fi][i] = Math.Clamp((int)((values[i] - fmin) / binWidth), 0, numBins - 1);
        }

        return (bins, binEdges);
    }

    /// <summary>Item 18: Filter columns by interaction constraints.</summary>
    private static int[] FilterByInteractionConstraints(int[] colSubset, int[][] constraints, HashSet<int> usedGroups)
    {
        if (usedGroups.Count == 0) return colSubset;
        var allowed = new HashSet<int>();
        foreach (int g in usedGroups)
            if (g < constraints.Length)
                foreach (int f in constraints[g])
                    allowed.Add(f);
        // Also allow features not in any group
        var allGrouped = new HashSet<int>();
        foreach (var group in constraints)
            foreach (int f in group)
                allGrouped.Add(f);

        var result = colSubset.Where(c => allowed.Contains(c) || !allGrouped.Contains(c)).ToArray();
        return result.Length > 0 ? result : colSubset;
    }

    private static void ClipLeafValues(GbmTree tree, double clipValue)
    {
        if (tree.Nodes is null) return;
        foreach (var node in tree.Nodes)
            if (node.IsLeaf)
                node.LeafValue = Math.Clamp(node.LeafValue, -clipValue, clipValue);
    }

    private static void ScaleTreeLeaves(GbmTree tree, double scale)
    {
        if (tree.Nodes is null) return;
        foreach (var node in tree.Nodes)
            if (node.IsLeaf)
                node.LeafValue *= scale;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PREDICTION HELPERS (Item 6: loop guard)
    // ═══════════════════════════════════════════════════════════════════════

    private static double Predict(GbmTree tree, float[] features)
    {
        if (tree.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        int maxIter = tree.Nodes.Count + 1; // Item 6: loop guard
        int iter = 0;
        while (nodeIdx >= 0 && nodeIdx < tree.Nodes.Count && iter++ < maxIter)
        {
            var node = tree.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < features.Length && features[node.SplitFeature] <= node.SplitThreshold)
                nodeIdx = node.LeftChild;
            else
                nodeIdx = node.RightChild;
        }
        return 0;
    }

    private static double GetTreeLearningRate(int treeIndex, double defaultLearningRate, IReadOnlyList<double>? perTreeLearningRates)
    {
        if (perTreeLearningRates is null || treeIndex < 0 || treeIndex >= perTreeLearningRates.Count)
            return defaultLearningRate;

        double treeLearningRate = perTreeLearningRates[treeIndex];
        return double.IsFinite(treeLearningRate) && treeLearningRate > 0.0
            ? treeLearningRate
            : defaultLearningRate;
    }

    private static double GbmScore(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double score = baseLogOdds;
        for (int ti = 0; ti < trees.Count; ti++)
            score += GetTreeLearningRate(ti, lr, perTreeLearningRates) * Predict(trees[ti], features);
        return score;
    }

    private static double GbmProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
        => Sigmoid(GbmScore(features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates));

    private static double GbmCalibProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot? calibrationSnapshot = null,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double rawP = Math.Clamp(
            GbmProb(features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates),
            1e-7, 1.0 - 1e-7);

        if (calibrationSnapshot is null)
            return rawP;

        return InferenceHelpers.ApplyDeployedCalibration(rawP, calibrationSnapshot);
    }

    private static double GbmCalibProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
        => GbmCalibProb(
            features, trees, baseLogOdds, lr, featureCount,
            CreateCalibrationSnapshot(new GbmCalibrationState(
                plattA,
                plattB,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.5,
                [])));

    internal static double? ComputeRawProbabilityFromSnapshotForAudit(float[] features, ModelSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Type, ModelType, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.GbmTreesJson))
        {
            return null;
        }

        ModelSnapshot normalized = GbmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        List<GbmTree>? trees;
        try
        {
            trees = JsonSerializer.Deserialize<List<GbmTree>>(normalized.GbmTreesJson!, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (trees is not { Count: > 0 })
            return null;

        int featureCount = normalized.Features.Length > 0
            ? normalized.Features.Length
            : features.Length;
        double learningRate = normalized.GbmLearningRate > 0.0
            ? normalized.GbmLearningRate
            : 0.1;
        return GbmProb(features, trees, normalized.GbmBaseLogOdds, learningRate, featureCount, normalized.GbmPerTreeLearningRates);
    }

    private static double ComputeEnsembleStd(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds,
        double lr, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (trees.Count <= 1)
            return 0.0;

        double score = baseLogOdds;
        var treeProbs = new double[trees.Count];
        for (int ti = 0; ti < trees.Count; ti++)
        {
            double treeLearningRate = GetTreeLearningRate(ti, lr, perTreeLearningRates);
            double leafValue = Predict(trees[ti], features);
            score += treeLearningRate * leafValue;
            treeProbs[ti] = Sigmoid(baseLogOdds + treeLearningRate * leafValue);
        }

        double rawProb = Sigmoid(score);
        double variance = 0.0;
        for (int ti = 0; ti < trees.Count; ti++)
        {
            double diff = treeProbs[ti] - rawProb;
            variance += diff * diff;
        }

        return Math.Sqrt(variance / (trees.Count - 1));
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
    private static double Logit(double p) => Math.Log(p / (1.0 - p));

    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateGbm(
        List<TrainingSample> evalSet, List<GbmTree> trees, double baseLogOdds, double lr,
        double[] magWeights, double magBias, int featureCount,
        ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        double decisionThreshold = 0.5)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;

        foreach (var s in evalSet)
        {
            double p    = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int    yHat = p >= decisionThreshold ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }
            else
            {
                double score = GbmScore(s.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
                magSse += (score - s.Magnitude) * (score - s.Magnitude);
            }
        }

        int evalN = evalSet.Count;
        double accuracy  = evalN > 0 ? (double)correct / evalN : 0;
        double brier     = evalN > 0 ? brierSum / evalN : 1;
        double magRmse   = evalN > 0 ? Math.Sqrt(magSse / evalN) : double.MaxValue;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        double sharpe    = ev / (brier + 0.01);

        double weightSum = 0, correctWeighted = 0;
        for (int i = 0; i < evalN; i++)
        {
            double wt = 1.0 + (double)i / evalN;
            double p  = GbmCalibProb(evalSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            weightSum += wt;
            if ((p >= decisionThreshold) == (evalSet[i].Direction > 0)) correctWeighted += wt;
        }
        double wAcc = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        return new EvalMetrics(accuracy, precision, recall, f1, magRmse, ev, brier, wAcc, sharpe, tp, fp, fn, tn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING (Item 10: convergence check)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            raw       = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double sgdLr = 0.01;
        const int maxEpochs = 200;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= sgdLr * dA / n;
            plattB -= sgdLr * dB / n;

            // Item 10: convergence check
            if (Math.Abs(dA / n) + Math.Abs(dB / n) < 1e-6) break;
        }

        return (plattA, plattB);
    }

    private static ClassConditionalPlattFit FitClassConditionalPlatt(
        IReadOnlyList<TrainingSample> calSet,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        IReadOnlyList<double>? perTreeLearningRates = null,
        double routingThreshold = 0.5,
        ModelSnapshot? globalCalibrationSnapshot = null)
    {
        if (calSet.Count < 20)
            return new ClassConditionalPlattFit(
                new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0),
                new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0));

        var buyPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);
        var sellPairs = new List<(double Logit, double BaseProb, double Y)>(calSet.Count);
        double effectiveRoutingThreshold = Math.Clamp(
            double.IsFinite(routingThreshold) ? routingThreshold : 0.5,
            0.01,
            0.99);

        foreach (var sample in calSet)
        {
            double raw = Math.Clamp(GbmProb(sample.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates), 1e-7, 1.0 - 1e-7);
            double rawLogit = Logit(raw);
            double baseProb = globalCalibrationSnapshot is null
                ? raw
                : InferenceHelpers.ApplyDeployedCalibration(raw, globalCalibrationSnapshot);
            double y = sample.Direction > 0 ? 1.0 : 0.0;

            if (baseProb >= effectiveRoutingThreshold)
                buyPairs.Add((rawLogit, baseProb, y));
            else
                sellPairs.Add((rawLogit, baseProb, y));
        }

        return new ClassConditionalPlattFit(
            FitConditionalPlattBranch(buyPairs),
            FitConditionalPlattBranch(sellPairs));
    }

    private static double DetermineConditionalRoutingThreshold(
        IReadOnlyList<TrainingSample> fitSet,
        IReadOnlyList<TrainingSample> evalSet,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        ModelSnapshot globalCalibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (fitSet.Count < 20 || evalSet.Count < 8)
            return 0.5;

        var fitProbs = new double[fitSet.Count];
        for (int i = 0; i < fitSet.Count; i++)
            fitProbs[i] = GbmCalibProb(fitSet[i].Features, trees, baseLogOdds, lr, featureCount, globalCalibrationSnapshot, perTreeLearningRates);

        var candidates = new SortedSet<double>
        {
            0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65
        };
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.33), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.50), 0.35, 0.65));
        candidates.Add(Math.Clamp(Quantile(fitProbs, 0.67), 0.35, 0.65));

        double bestThreshold = 0.5;
        double bestEvalNll = ComputeCalibrationNll(evalSet, trees, baseLogOdds, lr, featureCount, globalCalibrationSnapshot, perTreeLearningRates);
        foreach (double threshold in candidates)
        {
            var conditionalFit = FitClassConditionalPlatt(
                fitSet,
                trees,
                baseLogOdds,
                lr,
                featureCount,
                perTreeLearningRates,
                threshold,
                globalCalibrationSnapshot);
            var candidateSnapshot = CreateCalibrationSnapshot(new GbmCalibrationState(
                GlobalPlattA: globalCalibrationSnapshot.PlattA,
                GlobalPlattB: globalCalibrationSnapshot.PlattB,
                TemperatureScale: globalCalibrationSnapshot.TemperatureScale,
                PlattABuy: conditionalFit.Buy.A,
                PlattBBuy: conditionalFit.Buy.B,
                PlattASell: conditionalFit.Sell.A,
                PlattBSell: conditionalFit.Sell.B,
                ConditionalRoutingThreshold: threshold,
                IsotonicBreakpoints: []));
            double evalNll = ComputeCalibrationNll(evalSet, trees, baseLogOdds, lr, featureCount, candidateSnapshot, perTreeLearningRates);
            if (evalNll + 1e-6 < bestEvalNll)
            {
                bestEvalNll = evalNll;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    private static ConditionalPlattBranchFit FitConditionalPlattBranch(
        IReadOnlyList<(double Logit, double BaseProb, double Y)> pairs)
    {
        if (pairs.Count == 0)
            return new ConditionalPlattBranchFit(0, 0.0, 0.0, 0.0, 0.0);

        double baselineLoss = ComputeConditionalBranchNll(pairs);
        if (pairs.Count < 10)
            return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

        bool hasPositive = false, hasNegative = false;
        foreach (var (_, _, y) in pairs)
        {
            hasPositive |= y > 0.5;
            hasNegative |= y < 0.5;
            if (hasPositive && hasNegative)
                break;
        }

        if (!hasPositive || !hasNegative)
            return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

        int nPos = pairs.Count(pair => pair.Y > 0.5);
        int nNeg = pairs.Count - nPos;
        double targetPos = (nPos + 1.0) / (nPos + 2.0);
        double targetNeg = 1.0 / (nNeg + 2.0);
        var smoothedY = pairs.Select(pair => pair.Y > 0.5 ? targetPos : targetNeg).ToArray();

        const double sgdLr = 0.01;
        const int maxEpochs = 200;
        double a = 1.0, b = 0.0;
        double bestA = a, bestB = b, bestLoss = baselineLoss;

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            double dA = 0.0, dB = 0.0;
            for (int i = 0; i < pairs.Count; i++)
            {
                double calibP = Sigmoid(a * pairs[i].Logit + b);
                double err = calibP - smoothedY[i];
                dA += err * pairs[i].Logit;
                dB += err;
            }

            a -= sgdLr * dA / pairs.Count;
            b -= sgdLr * dB / pairs.Count;

            double loss = ComputeConditionalBranchNll(pairs, a, b);
            if (!double.IsFinite(loss))
                return new ConditionalPlattBranchFit(pairs.Count, baselineLoss, baselineLoss, 0.0, 0.0);

            if (loss < bestLoss)
            {
                bestLoss = loss;
                bestA = a;
                bestB = b;
            }
        }

        bool accepted = bestLoss + 1e-6 < baselineLoss;
        return new ConditionalPlattBranchFit(
            pairs.Count,
            baselineLoss,
            bestLoss,
            accepted ? bestA : 0.0,
            accepted ? bestB : 0.0);
    }

    private static double ComputeConditionalBranchNll(
        IReadOnlyList<(double Logit, double BaseProb, double Y)> pairs,
        double? plattA = null,
        double? plattB = null)
    {
        if (pairs.Count == 0)
            return 0.0;

        double loss = 0.0;
        for (int i = 0; i < pairs.Count; i++)
        {
            double p = plattA.HasValue && plattB.HasValue
                ? Sigmoid(plattA.Value * pairs[i].Logit + plattB.Value)
                : Math.Clamp(pairs[i].BaseProb, 1e-7, 1.0 - 1e-7);
            loss -= pairs[i].Y * Math.Log(Math.Max(p, 1e-7))
                  + (1.0 - pairs[i].Y) * Math.Log(Math.Max(1.0 - p, 1e-7));
        }

        return loss / pairs.Count;
    }

    private static double ComputeCalibrationNll(
        IReadOnlyList<TrainingSample> samples,
        IReadOnlyList<GbmTree> trees,
        double baseLogOdds,
        double lr,
        int featureCount,
        ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (samples.Count == 0)
            return 0.0;

        double loss = 0.0;
        for (int i = 0; i < samples.Count; i++)
        {
            double p = GbmCalibProb(samples[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double y = samples[i].Direction > 0 ? 1.0 : 0.0;
            loss -= y * Math.Log(Math.Max(p, 1e-7))
                  + (1.0 - y) * Math.Log(Math.Max(1.0 - p, 1e-7));
        }

        return loss / samples.Count;
    }

    private static double Quantile(double[] values, double probability)
    {
        if (values.Length == 0)
            return 0.5;

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Round(Math.Clamp(probability, 0.0, 1.0) * (sorted.Length - 1));
        return sorted[index];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (Item 11: boundary extrapolation)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10) return [];

        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            pairs[i] = (GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates),
                calSet[i].Direction > 0 ? 1.0 : 0.0);
        Array.Sort(pairs, (a, b) => a.X.CompareTo(b.X));

        var blocks = new List<(double SumY, int Count, double XMin, double XMax)>();
        foreach (var (x, y) in pairs)
        {
            blocks.Add((y, 1, x, x));
            while (blocks.Count >= 2)
            {
                var last = blocks[^1]; var prev = blocks[^2];
                if ((double)prev.SumY / prev.Count <= (double)last.SumY / last.Count) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks) { bp.Add((b.XMin + b.XMax) / 2.0); bp.Add(b.SumY / b.Count); }
        return bp.ToArray();
    }

    /// <summary>Item 11: Apply isotonic with linear extrapolation beyond boundaries.</summary>
    private static double ApplyIsotonic(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        // Below first breakpoint: linear extrapolation
        if (p <= bp[0])
        {
            if (bp.Length >= 4)
            {
                double slope = (bp[3] - bp[1]) / (bp[2] - bp[0] + 1e-15);
                return Math.Clamp(bp[1] + slope * (p - bp[0]), 0.0, 1.0);
            }
            return bp[1];
        }
        // Above last breakpoint: linear extrapolation
        if (p >= bp[^2])
        {
            if (bp.Length >= 4)
            {
                double slope = (bp[^1] - bp[^3]) / (bp[^2] - bp[^4] + 1e-15);
                return Math.Clamp(bp[^1] + slope * (p - bp[^2]), 0.0, 1.0);
            }
            return bp[^1];
        }
        // Interior: linear interpolation
        for (int i = 0; i < bp.Length - 2; i += 2)
        {
            if (i + 2 < bp.Length && p <= bp[i + 2])
            {
                double frac = (p - bp[i]) / (bp[i + 2] - bp[i] + 1e-15);
                return bp[i + 1] + frac * (bp[i + 3] - bp[i + 1]);
            }
        }
        return bp[^1];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  APPROXIMATE VENN-ABERS BOUNDS (Item 7)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[][] ComputeVennAbers(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count < 10)
            return [];

        // Persist approximate Venn-Abers bounds for diagnostics. This is not used by the
        // live scorer, so we keep the artifact explicit rather than implying exact Venn-Abers.
        var rawProbs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            rawProbs[i] = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);

        return TcnModelTrainer.FitVennAbers(calSet, rawProbs);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ECE, THRESHOLD, CONFORMAL, OOB, JACKKNIFE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binPositive = new double[bins]; var binConf = new double[bins]; var binCount = new int[bins];

        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p; binPositive[bin] += s.Direction > 0 ? 1 : 0; binCount[bin]++;
        }

        double ece = 0; int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binPositive[b] / binCount[b] - binConf[b] / binCount[b]) * binCount[b] / n;
        }
        return ece;
    }

    /// <summary>Item 23: EV-optimal threshold with optional transaction cost subtraction.</summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates = null,
        int searchMin = 30, int searchMax = 75, double spreadCost = 0.0, int stepBps = 50)
    {
        if (dataSet.Count < 30) return 0.5;
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = GbmCalibProb(dataSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);

        double bestEv = double.MinValue; double bestT = 0.5;
        int minBps = Math.Max(1, searchMin * 100);
        int maxBps = Math.Max(minBps, searchMax * 100);
        int effectiveStepBps = stepBps > 0 ? stepBps : 50;
        for (int thresholdBps = minBps; thresholdBps <= maxBps; thresholdBps += effectiveStepBps)
        {
            double t = thresholdBps / 10_000.0;
            double ev = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (dataSet[i].Direction > 0);
                double mag = Math.Abs(dataSet[i].Magnitude) - spreadCost;
                ev += (correct ? 1 : -1) * Math.Max(0, mag);
            }
            ev /= dataSet.Count;
            if (ev > bestEv) { bestEv = ev; bestT = t; }
        }
        return bestT;
    }

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        CancellationToken ct, int baseSeed = 0, double decisionThreshold = 0.5)
    {
        double baseline = ComputeAccuracy(testSet, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates, decisionThreshold);
        var importance = new float[featureCount];
        int tn = testSet.Count;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            int rngSeed = baseSeed != 0 ? baseSeed + (j * 13) + 42 : j * 13 + 42;
            var rng  = new Random(rngSeed);
            var vals = new float[tn];
            for (int i = 0; i < tn; i++) vals[i] = testSet[i].Features[j];
            for (int i = tn - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }

            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < tn; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p = GbmCalibProb(scratch, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                if ((p >= decisionThreshold) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / tn);
        });

        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    /// <summary>Item 39: Gain-weighted tree split importance.</summary>
    private static float[] ComputeGainWeightedImportance(List<GbmTree> trees, int featureCount)
    {
        var importance = new float[featureCount];
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            foreach (var node in tree.Nodes)
                if (!node.IsLeaf && node.SplitFeature < featureCount)
                    importance[node.SplitFeature] += (float)node.SplitGain;
        }
        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, CancellationToken ct, int baseSeed = 0)
    {
        int n = calSet.Count;
        int baseCorrect = 0;
        foreach (var s in calSet)
            if ((GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (s.Direction > 0))
                baseCorrect++;
        double baseAcc = (double)baseCorrect / n;

        var importance = new double[featureCount];
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            int rngSeed = baseSeed != 0 ? baseSeed + (j * 17) + 7 : j * 17 + 7;
            var rng = new Random(rngSeed);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }

            var scratch = new float[calSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((GbmCalibProb(scratch, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (calSet[idx].Direction > 0))
                    correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    private static double ComputeAccuracy(
        List<TrainingSample> set, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, double decisionThreshold = 0.5)
    {
        int correct = 0;
        foreach (var s in set)
            if ((GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates) >= decisionThreshold) == (s.Direction > 0))
                correct++;
        return set.Count > 0 ? (double)correct / set.Count : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OOB, CONFORMAL, JACKKNIFE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet, List<GbmTree> trees, List<HashSet<int>> bagMasks,
        double baseLogOdds, double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, double decisionThreshold = 0.5)
    {
        if (trainSet.Count < 10 || trees.Count < 2 || bagMasks.Count != trees.Count) return 0;
        int correct = 0, evaluated = 0;
        for (int i = 0; i < trainSet.Count; i++)
        {
            double oobScore = baseLogOdds;
            int oobTreeCount = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                if (bagMasks[t].Contains(i)) continue;
                oobScore += GetTreeLearningRate(t, lr, perTreeLearningRates) * Predict(trees[t], trainSet[i].Features);
                oobTreeCount++;
            }
            if (oobTreeCount == 0) continue;

            // OOB estimates are computed in raw-probability space so they do not apply
            // calibration artifacts fitted on the full ensemble to subset-tree predictions.
            double oobProb = Math.Clamp(Sigmoid(oobScore), 1e-7, 1.0 - 1e-7);
            if ((oobProb >= 0.5) == (trainSet[i].Direction > 0)) correct++;
            evaluated++;
        }
        return evaluated > 0 ? (double)correct / evaluated : 0;
    }

    /// <summary>Item 8: Conformal q-hats in probability space for Buy/Sell prediction sets.</summary>
    private static (double Overall, double Buy, double Sell) ComputeConformalQHats(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates, double alpha)
    {
        if (calSet.Count < 10) return (0.5, 0.5, 0.5);
        var scores = new List<double>(calSet.Count);
        var buyScores = new List<double>(calSet.Count);
        var sellScores = new List<double>(calSet.Count);
        for (int i = 0; i < calSet.Count; i++)
        {
            double calibP = GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double score = calSet[i].Direction > 0 ? 1.0 - calibP : calibP;
            score = Math.Clamp(score, 0.0, 1.0);
            scores.Add(score);
            if (calSet[i].Direction > 0) buyScores.Add(score); else sellScores.Add(score);
        }

        static double Quantile(List<double> values, double alphaValue)
        {
            if (values.Count == 0) return 0.5;
            values.Sort();
            int qIdx = (int)Math.Ceiling((1.0 - alphaValue) * (values.Count + 1)) - 1;
            qIdx = Math.Clamp(qIdx, 0, values.Count - 1);
            return Math.Clamp(values[qIdx], 1e-6, 1.0 - 1e-6);
        }

        double overall = Quantile(scores, alpha);
        double buy = buyScores.Count > 0 ? Quantile(buyScores, alpha) : overall;
        double sell = sellScores.Count > 0 ? Quantile(sellScores, alpha) : overall;
        return (overall, buy, sell);
    }

    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet, List<GbmTree> trees, List<HashSet<int>> bagMasks,
        double baseLogOdds, double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (trainSet.Count < 10 || trees.Count < 2 || bagMasks.Count != trees.Count) return [];
        var residuals = new List<double>(trainSet.Count);
        for (int i = 0; i < trainSet.Count; i++)
        {
            double oobScore = baseLogOdds; int oobTreeCount = 0;
            for (int t = 0; t < trees.Count; t++)
            {
                if (bagMasks[t].Contains(i)) continue;
                oobScore += GetTreeLearningRate(t, lr, perTreeLearningRates) * Predict(trees[t], trainSet[i].Features);
                oobTreeCount++;
            }
            if (oobTreeCount == 0) continue;
            // Keep jackknife residuals in raw-probability space for the same reason as OOB accuracy:
            // subset-tree predictions should not pass through full-ensemble calibration parameters.
            double oobP = Math.Clamp(Sigmoid(oobScore), 1e-7, 1.0 - 1e-7);
            double y = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(y - oobP));
        }
        residuals.Sort();
        return [..residuals];
    }

    /// <summary>Item 9: Validate Jackknife+ empirical coverage on calibration set.</summary>
    private static double ValidateJackknifeCoverage(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double[] jackknifeResiduals, double alpha)
    {
        if (calSet.Count < 10 || jackknifeResiduals.Length < 5) return 0;
        int qIdx = (int)Math.Ceiling((1.0 - alpha) * jackknifeResiduals.Length) - 1;
        qIdx = Math.Clamp(qIdx, 0, jackknifeResiduals.Length - 1);
        double qHat = jackknifeResiduals[qIdx];

        int covered = 0;
        foreach (var s in calSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double y = s.Direction > 0 ? 1.0 : 0.0;
            if (Math.Abs(y - p) <= qHat) covered++;
        }
        return (double)covered / calSet.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  META-LABEL MODEL (Item 20: MLP with configurable hidden dim)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double[] HiddenWeights, double[] HiddenBiases, int HiddenDim) FitMetaLabelNetwork(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, int[]? topFeatureIndices = null, int hiddenDim = 0, int baseSeed = 0)
    {
        if (calSet.Count < 20) return ([], 0.0, [], [], 0);

        int metaDim = 2 + Math.Min(3, topFeatureIndices?.Length ?? 3);

        // Item 20: MLP with hidden layer if configured
        if (hiddenDim > 0)
            return FitMetaLabelMLP(
                calSet, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates,
                decisionThreshold, topFeatureIndices, metaDim, hiddenDim, baseSeed);

        var w = new double[metaDim]; double b = 0;
        const double sgdLr = 0.01;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double[] metaF = BuildMetaLabelFeatureVector(s.Features, featureCount, calibP, ensembleStd, topFeatureIndices);

                double z = b; for (int j = 0; j < metaDim; j++) z += w[j] * metaF[j];
                double p = Sigmoid(z);
                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = p - (isCorrect ? 1.0 : 0.0);
                b -= sgdLr * err;
                for (int j = 0; j < metaDim; j++) w[j] -= sgdLr * err * metaF[j];
            }
        }
        return (w, b, [], [], 0);
    }

    /// <summary>Item 20: 2-layer MLP meta-label model.</summary>
    private static (double[] Weights, double Bias, double[] HiddenWeights, double[] HiddenBiases, int HiddenDim) FitMetaLabelMLP(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double decisionThreshold, int[]? topFeatureIndices, int inputDim, int hiddenDim, int baseSeed = 0)
    {
        // Hidden layer: inputDim → hiddenDim (ReLU), Output: hiddenDim → 1 (sigmoid)
        var wH = new double[inputDim * hiddenDim]; var bH = new double[hiddenDim];
        var wO = new double[hiddenDim]; double bO = 0;
        var rng = CreateSeededRandom(baseSeed, 42);
        for (int i = 0; i < wH.Length; i++) wH[i] = (rng.NextDouble() - 0.5) * 0.1;
        for (int i = 0; i < wO.Length; i++) wO[i] = (rng.NextDouble() - 0.5) * 0.1;

        const double sgdLr = 0.005;
        var hidden = new double[hiddenDim];

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double[] input = BuildMetaLabelFeatureVector(s.Features, featureCount, calibP, ensembleStd, topFeatureIndices);

                // Forward: hidden = ReLU(W_H · input + b_H)
                for (int h = 0; h < hiddenDim; h++)
                {
                    double z = bH[h];
                    int rowOffset = h * inputDim;
                    for (int j = 0; j < inputDim; j++) z += wH[rowOffset + j] * input[j];
                    hidden[h] = Math.Max(0, z); // ReLU
                }
                double output = bO;
                for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
                double pred = Sigmoid(output);

                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = pred - (isCorrect ? 1.0 : 0.0);

                // Backward: output layer
                bO -= sgdLr * err;
                var outputWeightsBefore = (double[])wO.Clone();
                for (int h = 0; h < hiddenDim; h++) wO[h] -= sgdLr * err * hidden[h];

                // Backward: hidden layer
                for (int h = 0; h < hiddenDim; h++)
                {
                    if (hidden[h] <= 0) continue; // ReLU gradient
                    double dh = err * outputWeightsBefore[h];
                    bH[h] -= sgdLr * dh;
                    int rowOffset = h * inputDim;
                    for (int j = 0; j < inputDim; j++) wH[rowOffset + j] -= sgdLr * dh * input[j];
                }
            }
        }

        return (wO, bO, wH, bH, hiddenDim);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ABSTENTION GATE (Items 21,22,24)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold, double ThresholdBuy, double ThresholdSell, double[] CoverageAccCurve)
        FitAbstentionModel(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot, IReadOnlyList<double>? perTreeLearningRates,
        double[] metaLabelWeights, double metaLabelBias,
        double[] metaLabelHiddenWeights, double[] metaLabelHiddenBiases, int metaLabelHiddenDim,
        double decisionThreshold, int[]? topFeatureIndices = null, bool separateThresholds = false)
    {
        if (calSet.Count < 20) return ([], 0.0, 0.5, 0.5, 0.5, []);

        int dim = 3;
        var w = new double[dim]; double b = 0;
        const double sgdLr = 0.01;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
                double ensembleStd = ComputeEnsembleStd(s.Features, trees, baseLogOdds, lr, perTreeLearningRates);
                double ms = ComputeMetaLabelScore(
                    calibP, ensembleStd, s.Features, featureCount,
                    metaLabelWeights, metaLabelBias, topFeatureIndices,
                    metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);

                var af = new[] { calibP, ensembleStd, ms };
                double z = b; for (int j = 0; j < dim; j++) z += w[j] * af[j];
                double p = Sigmoid(z);
                bool isCorrect = (calibP >= decisionThreshold) == (s.Direction > 0);
                double err = p - (isCorrect ? 1.0 : 0.0);
                b -= sgdLr * err;
                for (int j = 0; j < dim; j++) w[j] -= sgdLr * err * af[j];
            }
        }

        // Item 21: Finer sweep (0.5% steps) + Item 22: coverage-accuracy curve
        var curveEntries = new List<double>();
        double bestThreshold = 0.5, bestFilteredAcc = 0;
        double bestThresholdBuy = 0.5, bestThresholdSell = 0.5;

        // Precompute per-sample scores
        var sampleScores = new (double AbstScore, double CalibP, bool IsBuy, bool IsCorrect)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double calibP = GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            double ensembleStd = ComputeEnsembleStd(calSet[i].Features, trees, baseLogOdds, lr, perTreeLearningRates);
            double ms = ComputeMetaLabelScore(
                calibP, ensembleStd, calSet[i].Features, featureCount,
                metaLabelWeights, metaLabelBias, topFeatureIndices,
                metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);
            var af = new[] { calibP, ensembleStd, ms };
            double z = b; for (int j = 0; j < dim; j++) z += w[j] * af[j];
            bool predictedBuy = calibP >= decisionThreshold;
            sampleScores[i] = (Sigmoid(z), calibP, predictedBuy, predictedBuy == (calSet[i].Direction > 0));
        }

        for (int thresholdBps = 2000; thresholdBps <= 8000; thresholdBps += 50)
        {
            double t = thresholdBps / 10_000.0;
            int correct = 0, total = 0;
            foreach (var ss in sampleScores)
            {
                if (ss.AbstScore < t) continue;
                total++; if (ss.IsCorrect) correct++;
            }
            double acc = total > 0 ? (double)correct / total : 0;
            double coverage = (double)total / calSet.Count;
            curveEntries.AddRange([t, coverage, acc]); // Item 22

            if (acc > bestFilteredAcc && total >= calSet.Count / 4)
            { bestFilteredAcc = acc; bestThreshold = t; }
        }

        // Item 24: separate buy/sell thresholds
        if (separateThresholds)
        {
            double bestBuyAcc = 0, bestSellAcc = 0;
            for (int thresholdBps = 2000; thresholdBps <= 8000; thresholdBps += 50)
            {
                double t = thresholdBps / 10_000.0;
                int cBuy = 0, tBuy = 0, cSell = 0, tSell = 0;
                foreach (var ss in sampleScores)
                {
                    if (ss.AbstScore < t) continue;
                    if (ss.IsBuy) { tBuy++; if (ss.IsCorrect) cBuy++; }
                    else { tSell++; if (ss.IsCorrect) cSell++; }
                }
                double buyAcc = tBuy > 0 ? (double)cBuy / tBuy : 0;
                double sellAcc = tSell > 0 ? (double)cSell / tSell : 0;
                if (buyAcc > bestBuyAcc && tBuy >= calSet.Count / 8) { bestBuyAcc = buyAcc; bestThresholdBuy = t; }
                if (sellAcc > bestSellAcc && tSell >= calSet.Count / 8) { bestSellAcc = sellAcc; bestThresholdSell = t; }
            }
        }

        return (w, b, bestThreshold, bestThresholdBuy, bestThresholdSell, curveEntries.ToArray());
    }

    private static double[] BuildMetaLabelFeatureVector(
        float[] features, int featureCount, double calibP, double ensembleStd, int[]? topFeatureIndices)
    {
        int[] effectiveTopFeatures = topFeatureIndices is { Length: > 0 }
            ? topFeatureIndices.Take(3).ToArray()
            : [0, 1, 2];

        var metaFeatures = new double[2 + effectiveTopFeatures.Length];
        metaFeatures[0] = calibP;
        metaFeatures[1] = ensembleStd;
        for (int i = 0; i < effectiveTopFeatures.Length; i++)
        {
            int featureIndex = effectiveTopFeatures[i];
            if (featureIndex < 0 || featureIndex >= featureCount || featureIndex >= features.Length)
                continue;

            metaFeatures[2 + i] = features[featureIndex];
        }

        return metaFeatures;
    }

    private static double ComputeMetaLabelScore(
        double calibP, double ensembleStd, float[] features, int featureCount,
        double[] metaLabelWeights, double metaLabelBias, int[]? topFeatureIndices = null,
        double[]? metaLabelHiddenWeights = null, double[]? metaLabelHiddenBiases = null, int metaLabelHiddenDim = 0)
    {
        if (metaLabelWeights.Length == 0)
            return 0.5;

        decimal? score = ScoringEnrichmentCalculator.ComputeMetaLabelScore(
            calibP, ensembleStd, features, featureCount,
            metaLabelWeights, metaLabelBias, topFeatureIndices,
            metaLabelHiddenWeights, metaLabelHiddenBiases, metaLabelHiddenDim);
        return score.HasValue ? (double)score.Value : 0.5;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSORS (Item 44: quantile with Adam)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train, int featureCount, TrainingHyperparams hp)
    {
        var w = new double[featureCount]; double b = 0.0;
        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;
        if (trainSet.Count == 0) return (w, b);

        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0, beta1t = 1.0, beta2t = 1.0;
        int t = 0;
        double bestValLoss = double.MaxValue;
        var bestW = new double[featureCount]; double bestB = 0.0; int patience = 0;
        int epochs = hp.MaxEpochs;
        double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1;
        double l2 = hp.L2Lambda;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t;
                double alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5; valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }
        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    /// <summary>Item 44: Quantile regressor with Adam optimizer + early stopping.</summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int featureCount, double tau, TrainingHyperparams hp)
    {
        var w = new double[featureCount]; double b = 0;
        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0, vB = 0, beta1t = 1.0, beta2t = 1.0;
        int t = 0;

        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;

        double bestValLoss = double.MaxValue;
        var bestW = new double[featureCount]; double bestB = 0; int patience = 0;
        double baseLr = 0.001;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / 100.0));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                double grad = err >= 0 ? -tau : (1 - tau);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t;
                double alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * grad;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * grad * grad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = grad * s.Features[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                valLoss += err >= 0 ? tau * err : (1 - tau) * (-err); valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= Math.Max(3, hp.EarlyStoppingPatience / 2)) break;
        }
        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DECISION BOUNDARY, PREDICTION STABILITY, DURBIN-WATSON
    // ═══════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            norms[i] = p * (1 - p);
        }
        double mean = norms.Average();
        double std = 0;
        foreach (double v in norms) std += (v - mean) * (v - mean);
        std = norms.Length > 1 ? Math.Sqrt(std / (norms.Length - 1)) : 0;
        return (mean, std);
    }

    /// <summary>Item 38: Average distance-to-decision-boundary on test set.</summary>
    private static double ComputePredictionStability(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double sum = 0;
        foreach (var s in testSet)
        {
            double rawScore = GbmScore(s.Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates);
            sum += Math.Abs(rawScore); // distance from decision boundary (0 in log-odds)
        }
        return testSet.Count > 0 ? sum / testSet.Count : 0;
    }

    private static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int featureCount)
    {
        if (train.Count < 10 || magWeights.Length == 0) return 2.0;
        var residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, train[i].Features.Length); j++)
                pred += magWeights[j] * train[i].Features[j];
            residuals[i] = train[i].Magnitude - pred;
        }
        double numSum = 0, denSum = 0;
        for (int i = 1; i < residuals.Length; i++) { double d = residuals[i] - residuals[i - 1]; numSum += d * d; }
        for (int i = 0; i < residuals.Length; i++) denSum += residuals[i] * residuals[i];
        return denSum > 1e-15 ? numSum / denSum : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  KELLY, TEMPERATURE, BSS, MURPHY DECOMPOSITION
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (calSet.Count == 0) return 0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            sum += Math.Max(0, 2 * p - 1);
        }
        return sum / calSet.Count * 0.5;
    }

    /// <summary>Item 12: Temperature scaling via Brent's method (golden section search).</summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        // Precompute logits
        var logits = new double[calSet.Count]; var labels = new int[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double rawP = Math.Clamp(GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates), 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(rawP);
            labels[i] = calSet[i].Direction > 0 ? 1 : 0;
        }

        double TempLoss(double T)
        {
            double loss = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double p = Sigmoid(logits[i] / T);
                loss -= labels[i] * Math.Log(p + 1e-15) + (1 - labels[i]) * Math.Log(1 - p + 1e-15);
            }
            return loss / calSet.Count;
        }

        // Golden section search on [0.1, 10.0]
        double a = 0.1, bnd = 10.0;
        const double phi = 0.6180339887;
        double x1 = bnd - phi * (bnd - a), x2 = a + phi * (bnd - a);
        double f1 = TempLoss(x1), f2 = TempLoss(x2);

        for (int iter = 0; iter < 50; iter++)
        {
            if (f1 < f2) { bnd = x2; x2 = x1; f2 = f1; x1 = bnd - phi * (bnd - a); f1 = TempLoss(x1); }
            else { a = x1; x1 = x2; f1 = f2; x2 = a + phi * (bnd - a); f2 = TempLoss(x2); }
            if (Math.Abs(bnd - a) < 0.001) break;
        }
        return (a + bnd) / 2.0;
    }

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (testSet.Count < 10) return 0;
        double brier = 0; int posCount = 0;
        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int y = s.Direction > 0 ? 1 : 0;
            brier += (p - y) * (p - y); posCount += y;
        }
        brier /= testSet.Count;
        double baseRate = (double)posCount / testSet.Count;
        double naiveBrier = baseRate * (1 - baseRate);
        return naiveBrier > 1e-10 ? 1.0 - brier / naiveBrier : 0;
    }

    /// <summary>Item 36: Murphy-style Brier decomposition into calibration + refinement.</summary>
    private static (double CalibrationLoss, double RefinementLoss) ComputeMurphyDecomposition(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount, ModelSnapshot calibrationSnapshot,
        IReadOnlyList<double>? perTreeLearningRates = null, int bins = 10)
    {
        if (testSet.Count < bins) return (0, 0);
        var binSumP = new double[bins]; var binSumY = new double[bins]; var binCount = new int[bins];
        int totalPos = 0;

        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, featureCount, calibrationSnapshot, perTreeLearningRates);
            int y = s.Direction > 0 ? 1 : 0;
            int bin = Math.Clamp((int)(p * bins), 0, bins - 1);
            binSumP[bin] += p; binSumY[bin] += y; binCount[bin]++; totalPos += y;
        }

        double baseRate = (double)totalPos / testSet.Count;
        double calLoss = 0, refLoss = 0;
        int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgP = binSumP[b] / binCount[b];
            double avgY = binSumY[b] / binCount[b];
            calLoss += (avgP - avgY) * (avgP - avgY) * binCount[b] / n; // reliability
            refLoss += avgY * (1 - avgY) * binCount[b] / n; // within-bin variance (resolution proxy)
        }
        return (calLoss, refLoss);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MI REDUNDANCY (Item 35: drop recommendation)
    // ═══════════════════════════════════════════════════════════════════════

    private static (string[] Pairs, int[] DropIndices) ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int featureCount, double threshold, float[] importance)
    {
        if (trainSet.Count < 30) return ([], []);
        int n = Math.Min(trainSet.Count, 500);
        int numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
        var featureMin = new double[featureCount]; var featureMax = new double[featureCount];
        var featureBinIdx = new int[featureCount * n];
        Array.Fill(featureMin, double.MaxValue); Array.Fill(featureMax, double.MinValue);

        for (int j = 0; j < featureCount; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                if (v < featureMin[j]) featureMin[j] = v;
                if (v > featureMax[j]) featureMax[j] = v;
            }
            double range = featureMax[j] - featureMin[j];
            double binWidth = range > 1e-15 ? range / numBins : 1.0;
            for (int i = 0; i < n; i++)
                featureBinIdx[j * n + i] = Math.Clamp((int)((trainSet[i].Features[j] - featureMin[j]) / binWidth), 0, numBins - 1);
        }

        var pairs = new List<string>(); var dropIndices = new List<int>();
        double invN = 1.0 / n;

        for (int a = 0; a < featureCount; a++)
        {
            for (int bi = a + 1; bi < featureCount; bi++)
            {
                var joint = new int[numBins * numBins]; var margA = new int[numBins]; var margB = new int[numBins];
                for (int i = 0; i < n; i++)
                {
                    int ba = featureBinIdx[a * n + i]; int bb = featureBinIdx[bi * n + i];
                    joint[ba * numBins + bb]++; margA[ba]++; margB[bb]++;
                }

                double mi = 0;
                for (int ia = 0; ia < numBins; ia++)
                {
                    if (margA[ia] == 0) continue; double pA = margA[ia] * invN;
                    for (int ib = 0; ib < numBins; ib++)
                    {
                        int jCount = joint[ia * numBins + ib];
                        if (jCount == 0 || margB[ib] == 0) continue;
                        double pJ = jCount * invN, pB = margB[ib] * invN;
                        mi += pJ * Math.Log(pJ / (pA * pB));
                    }
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = bi < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[bi] : $"F{bi}";
                    pairs.Add($"{nameA}:{nameB}");
                    // Item 35: recommend dropping the less important feature
                    float impA = a < importance.Length ? importance[a] : 0;
                    float impB = bi < importance.Length ? importance[bi] : 0;
                    dropIndices.Add(impA >= impB ? bi : a);
                }
            }
        }
        return (pairs.ToArray(), dropIndices.ToArray());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREESHAP, PARTIAL DEPENDENCE (Items 31, 32)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 31: TreeSHAP baseline = mean prediction over training set.</summary>
    private static double ComputeTreeShapBaseline(List<GbmTree> trees, double baseLogOdds, double lr,
        List<TrainingSample> trainSet, int featureCount)
    {
        if (trainSet.Count == 0) return 0;
        double sum = 0;
        foreach (var s in trainSet) sum += GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
        return sum / trainSet.Count;
    }

    /// <summary>Item 32: Partial dependence for top features (marginal response curves).</summary>
    private static double[][] ComputePartialDependence(
        List<TrainingSample> trainSet, List<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, int[] topFeatureIndices, int gridPoints = 20)
    {
        if (trainSet.Count < 10 || topFeatureIndices.Length == 0) return [];
        int subsample = Math.Min(trainSet.Count, 200);
        var result = new double[topFeatureIndices.Length][];

        for (int fi = 0; fi < topFeatureIndices.Length; fi++)
        {
            int fIdx = topFeatureIndices[fi];
            if (fIdx >= featureCount) continue;

            // Get feature range
            float fmin = float.MaxValue, fmax = float.MinValue;
            for (int i = 0; i < subsample; i++)
            {
                float v = trainSet[i].Features[fIdx];
                if (v < fmin) fmin = v; if (v > fmax) fmax = v;
            }

            var pdp = new double[gridPoints * 2]; // [gridValue, avgPred, ...]
            float step = (fmax - fmin) / (gridPoints - 1);

            for (int g = 0; g < gridPoints; g++)
            {
                float gridVal = fmin + g * step;
                double avgPred = 0;
                var scratch = new float[trainSet[0].Features.Length];
                for (int i = 0; i < subsample; i++)
                {
                    Array.Copy(trainSet[i].Features, scratch, scratch.Length);
                    scratch[fIdx] = gridVal;
                    avgPred += GbmProb(scratch, trees, baseLogOdds, lr, featureCount);
                }
                pdp[g * 2] = gridVal;
                pdp[g * 2 + 1] = avgPred / subsample;
            }
            result[fi] = pdp;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY (Item 34: interpolated ADF critical values)
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        if (samples.Count < 50) return 0;
        int nonStationary = 0;
        int maxObs = Math.Min(samples.Count, 500);
        for (int j = 0; j < featureCount; j++)
        {
            var series = new double[maxObs];
            for (int i = 0; i < maxObs; i++) series[i] = samples[i].Features[j];
            if (IsNonStationary(series)) nonStationary++;
        }
        return nonStationary;
    }

    private static bool IsNonStationary(double[] series)
    {
        int N = series.Length;
        if (N < 20) return false;
        var dx = new double[N - 1];
        for (int i = 0; i < dx.Length; i++) dx[i] = series[i + 1] - series[i];

        int p = Math.Min(12, (int)Math.Floor(Math.Pow(N - 1, 1.0 / 3.0)));
        int start = p + 1;
        int nObs = dx.Length - start;
        if (nObs < 10) return false;

        int cols = 2 + p;
        var X = new double[nObs * cols]; var Y = new double[nObs];
        for (int t = 0; t < nObs; t++)
        {
            int ti = start + t; Y[t] = dx[ti];
            X[t * cols + 0] = 1.0; X[t * cols + 1] = series[ti];
            for (int k = 0; k < p; k++) X[t * cols + 2 + k] = dx[ti - 1 - k];
        }

        var xtx = new double[cols * cols]; var xty = new double[cols];
        for (int t = 0; t < nObs; t++)
            for (int a = 0; a < cols; a++)
            {
                double xa = X[t * cols + a]; xty[a] += xa * Y[t];
                for (int b2 = a; b2 < cols; b2++)
                { double v = xa * X[t * cols + b2]; xtx[a * cols + b2] += v; if (a != b2) xtx[b2 * cols + a] += v; }
            }

        var L = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            for (int j2 = 0; j2 <= i; j2++)
            {
                double sum = 0;
                for (int k = 0; k < j2; k++) sum += L[i * cols + k] * L[j2 * cols + k];
                if (i == j2) { double diag = xtx[i * cols + i] - sum; if (diag <= 1e-15) return false; L[i * cols + j2] = Math.Sqrt(diag); }
                else L[i * cols + j2] = (xtx[i * cols + j2] - sum) / L[j2 * cols + j2];
            }
        }

        var z = new double[cols];
        for (int i = 0; i < cols; i++) { double sum = 0; for (int k = 0; k < i; k++) sum += L[i * cols + k] * z[k]; z[i] = (xty[i] - sum) / L[i * cols + i]; }
        var beta = new double[cols];
        for (int i = cols - 1; i >= 0; i--) { double sum = 0; for (int k = i + 1; k < cols; k++) sum += L[k * cols + i] * beta[k]; beta[i] = (z[i] - sum) / L[i * cols + i]; }

        double gamma = beta[1];
        double sse = 0;
        for (int t = 0; t < nObs; t++) { double pred = 0; for (int c = 0; c < cols; c++) pred += X[t * cols + c] * beta[c]; double resid = Y[t] - pred; sse += resid * resid; }
        double sigma2 = sse / Math.Max(1, nObs - cols);

        var Linv = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            Linv[i * cols + i] = 1.0 / L[i * cols + i];
            for (int j2 = i + 1; j2 < cols; j2++)
            { double sum = 0; for (int k = i; k < j2; k++) sum += L[j2 * cols + k] * Linv[k * cols + i]; Linv[j2 * cols + i] = -sum / L[j2 * cols + j2]; }
        }

        double varGamma = 0;
        for (int k = 0; k < cols; k++) varGamma += Linv[k * cols + 1] * Linv[k * cols + 1];
        varGamma *= sigma2;
        if (varGamma <= 1e-15) return false;

        double tStat = gamma / Math.Sqrt(varGamma);

        // Item 34: Interpolated ADF critical values (5% level, with constant)
        double criticalValue = InterpolateAdfCritical(nObs);
        return tStat > criticalValue;
    }

    /// <summary>Item 34: Interpolate ADF critical values from standard table.</summary>
    private static double InterpolateAdfCritical(int n)
    {
        // (N, critical_value_5pct) from Fuller's table (with constant, no trend)
        ReadOnlySpan<(int N, double CV)> table = [(25, -3.00), (50, -2.93), (100, -2.89), (250, -2.88), (500, -2.86), (1000, -2.86)];
        if (n <= table[0].N) return table[0].CV;
        if (n >= table[^1].N) return table[^1].CV;
        for (int i = 0; i < table.Length - 1; i++)
        {
            if (n <= table[i + 1].N)
            {
                double frac = (double)(n - table[i].N) / (table[i + 1].N - table[i].N);
                return table[i].CV + frac * (table[i + 1].CV - table[i].CV);
            }
        }
        return -2.86;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO (Item 27: MLP), COVARIATE SHIFT (Item 29: continuous)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 27: Density-ratio with 2-layer MLP discriminator.</summary>
    private static double[] ComputeDensityRatioImportanceWeights(
        List<TrainingSample> trainSet, int featureCount, int windowDays, int barsPerDay, int baseSeed = 0)
    {
        int effectiveBarsPerDay = barsPerDay > 0 ? barsPerDay : 24;
        int recentCount = Math.Min(trainSet.Count / 3, windowDays * effectiveBarsPerDay);
        if (recentCount < 20) return Enumerable.Repeat(1.0, trainSet.Count).ToArray();
        int cutoff = trainSet.Count - recentCount;

        // MLP: featureCount → 8 → 1
        int hiddenDim = Math.Min(8, featureCount);
        var wH = new double[featureCount * hiddenDim]; var bH = new double[hiddenDim];
        var wO = new double[hiddenDim]; double bO = 0;
        var rng = CreateSeededRandom(baseSeed, 42);
        for (int i = 0; i < wH.Length; i++) wH[i] = (rng.NextDouble() - 0.5) * 0.1;
        for (int i = 0; i < wO.Length; i++) wO[i] = (rng.NextDouble() - 0.5) * 0.1;
        var hidden = new double[hiddenDim];

        const double sgdLr = 0.005;
        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < trainSet.Count; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                // Forward
                for (int h = 0; h < hiddenDim; h++)
                {
                    double z = bH[h];
                    for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                        z += wH[j * hiddenDim + h] * trainSet[i].Features[j];
                    hidden[h] = Math.Max(0, z);
                }
                double output = bO;
                for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
                double p = Sigmoid(output);
                double err = p - label;
                // Backward
                bO -= sgdLr * err;
                for (int h = 0; h < hiddenDim; h++)
                {
                    wO[h] -= sgdLr * err * hidden[h];
                    if (hidden[h] <= 0) continue;
                    double dh = err * wO[h];
                    bH[h] -= sgdLr * dh;
                    for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                        wH[j * hiddenDim + h] -= sgdLr * dh * trainSet[i].Features[j];
                }
            }
        }

        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            for (int h = 0; h < hiddenDim; h++)
            {
                double z = bH[h];
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    z += wH[j * hiddenDim + h] * trainSet[i].Features[j];
                hidden[h] = Math.Max(0, z);
            }
            double output = bO;
            for (int h = 0; h < hiddenDim; h++) output += wO[h] * hidden[h];
            double prob = Math.Clamp(Sigmoid(output), 0.01, 0.99);
            weights[i] = prob / (1 - prob);
        }

        double sum = weights.Sum();
        if (sum > 1e-15) for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        return weights;
    }

    /// <summary>Item 29: Continuous novelty scoring for covariate shift weights.</summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBp, int featureCount)
    {
        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double totalNovelty = 0; int checkedCount = 0;
            for (int j = 0; j < featureCount && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                // Continuous: distance from nearest boundary, normalised by range
                double range = bp[^1] - bp[0];
                if (range < 1e-15) continue;
                double distBelow = v < bp[0] ? (bp[0] - v) / range : 0;
                double distAbove = v > bp[^1] ? (v - bp[^1]) / range : 0;
                totalNovelty += distBelow + distAbove;
            }
            weights[i] = 1.0 + (checkedCount > 0 ? totalNovelty / checkedCount : 0);
        }
        double mean = weights.Average();
        if (mean > 1e-15) for (int i = 0; i < weights.Length; i++) weights[i] /= mean;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONCEPT DRIFT GATE (Item 28)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 28: Sliding-window loss comparison — exclude stale early segments.</summary>
    private List<TrainingSample> ApplyConceptDriftGate(List<TrainingSample> samples, int featureCount, int minSamples)
    {
        int windowSize = Math.Max(minSamples, samples.Count / 5);
        if (samples.Count < windowSize * 2) return samples;

        // Compare loss of earliest window vs latest window using simple accuracy proxy
        var earlyWindow = samples[..windowSize];
        var lateWindow = samples[^windowSize..];

        int earlyBuyCount = earlyWindow.Count(s => s.Direction > 0);
        int lateBuyCount = lateWindow.Count(s => s.Direction > 0);

        double earlyBuyRate = (double)earlyBuyCount / windowSize;
        double lateBuyRate = (double)lateBuyCount / windowSize;

        // If distribution has shifted significantly, trim early data
        if (Math.Abs(earlyBuyRate - lateBuyRate) > 0.15)
        {
            int trimTo = Math.Max(minSamples, samples.Count / 2);
            _logger.LogInformation("GBM concept drift gate: trimming {Old}→{New} samples (buyRate drift {Early:P1}→{Late:P1})",
                samples.Count, trimTo, earlyBuyRate, lateBuyRate);
            return samples[^trimTo..];
        }
        return samples;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EFB: EXCLUSIVE FEATURE BUNDLING (Item 3)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 3: Build EFB mapping — bundles mutually exclusive features.</summary>
    private static (int[] Mapping, int EffectiveCount) BuildEfbMapping(
        List<TrainingSample> samples, int featureCount)
    {
        int n = Math.Min(samples.Count, 500);
        // Count non-zero overlap between features
        var conflictCount = new int[featureCount * featureCount];
        for (int i = 0; i < n; i++)
        {
            for (int a = 0; a < featureCount; a++)
            {
                if (Math.Abs(samples[i].Features[a]) < 1e-10) continue;
                for (int b2 = a + 1; b2 < featureCount; b2++)
                {
                    if (Math.Abs(samples[i].Features[b2]) < 1e-10) continue;
                    conflictCount[a * featureCount + b2]++;
                }
            }
        }

        // Greedy bundling: features with < 1% mutual non-zero rate can be bundled
        var mapping = new int[featureCount];
        for (int i = 0; i < featureCount; i++) mapping[i] = i; // default: map to self

        int maxConflicts = (int)(n * 0.01);
        var bundled = new bool[featureCount];
        int nextBundle = 0;
        var bundles = new List<List<int>>();

        for (int a = 0; a < featureCount; a++)
        {
            if (bundled[a]) continue;
            var bundle = new List<int> { a };
            for (int b2 = a + 1; b2 < featureCount; b2++)
            {
                if (bundled[b2]) continue;
                bool canBundle = true;
                foreach (int existing in bundle)
                {
                    int key = existing < b2 ? existing * featureCount + b2 : b2 * featureCount + existing;
                    if (conflictCount[key] > maxConflicts) { canBundle = false; break; }
                }
                if (canBundle) { bundle.Add(b2); bundled[b2] = true; }
            }
            foreach (int f in bundle) mapping[f] = nextBundle;
            bundled[a] = true;
            bundles.Add(bundle);
            nextBundle++;
        }

        return (mapping, nextBundle);
    }

    private static int[][] BuildEfbGroups(int[] mapping, int featureCount)
    {
        return mapping
            .Take(featureCount)
            .Select((bundle, featureIndex) => (bundle, featureIndex))
            .GroupBy(x => x.bundle)
            .Select(g => g.Select(x => x.featureIndex).OrderBy(i => i).ToArray())
            .Where(group => group.Length > 1)
            .ToArray();
    }

    private static FeatureTransformDescriptor CloneFeatureTransformDescriptor(FeatureTransformDescriptor descriptor)
    {
        return new FeatureTransformDescriptor
        {
            Kind = descriptor.Kind,
            Version = descriptor.Version,
            Operation = descriptor.Operation,
            InputFeatureCount = descriptor.InputFeatureCount,
            OutputStartIndex = descriptor.OutputStartIndex,
            OutputCount = descriptor.OutputCount,
            SourceIndexGroups = descriptor.SourceIndexGroups
                .Select(group => (int[])group.Clone())
                .ToArray(),
        };
    }

    private static List<TrainingSample> ApplyFeatureTransforms(
        List<TrainingSample> samples, IReadOnlyList<FeatureTransformDescriptor> descriptors)
    {
        if (samples.Count == 0 || descriptors.Count == 0)
            return samples;

        return samples.Select(sample =>
        {
            var features = (float[])sample.Features.Clone();
            foreach (var descriptor in descriptors)
            {
                FeaturePipelineTransformSupport.TryApplyInPlace(features, descriptor);
            }
            return sample with { Features = features };
        }).ToList();
    }

    private static string[] BuildSnapshotFeatureNames(int featureCount)
    {
        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    private static bool[] BuildAllTrueMask(int featureCount)
    {
        var mask = new bool[featureCount];
        Array.Fill(mask, true);
        return mask;
    }

    private static int ComputeTrainingRandomSeed(
        string featureSchemaFingerprint,
        string trainerFingerprint,
        int sampleCount)
    {
        string payload = $"gbm-seed-v1|{featureSchemaFingerprint}|{trainerFingerprint}|{sampleCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        int seed = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return seed == 0 ? 1 : seed;
    }

    private static Random CreateSeededRandom(int baseSeed, int salt)
    {
        int seed = baseSeed != 0
            ? unchecked((baseSeed * 16777619) ^ salt)
            : salt;
        if (seed == 0)
            seed = 1;
        return new Random(seed);
    }

    private static GbmCalibrationPartition BuildCalibrationPartition(
        List<TrainingSample> calibrationSet,
        int calibrationStartIndex)
    {
        if (calibrationSet.Count == 0)
        {
            return new GbmCalibrationPartition(
                FitSet: [],
                DiagnosticsSet: [],
                ConformalSet: [],
                MetaLabelSet: [],
                AbstentionSet: [],
                FitStartIndex: calibrationStartIndex,
                DiagnosticsStartIndex: calibrationStartIndex,
                ConformalStartIndex: calibrationStartIndex,
                MetaLabelStartIndex: calibrationStartIndex,
                AbstentionStartIndex: calibrationStartIndex,
                AdaptiveHeadSplitMode: "SHARED_FALLBACK");
        }

        if (calibrationSet.Count < 40)
        {
            return new GbmCalibrationPartition(
                FitSet: calibrationSet,
                DiagnosticsSet: calibrationSet,
                ConformalSet: calibrationSet,
                MetaLabelSet: calibrationSet,
                AbstentionSet: calibrationSet,
                FitStartIndex: calibrationStartIndex,
                DiagnosticsStartIndex: calibrationStartIndex,
                ConformalStartIndex: calibrationStartIndex,
                MetaLabelStartIndex: calibrationStartIndex,
                AbstentionStartIndex: calibrationStartIndex,
                AdaptiveHeadSplitMode: "SHARED_FALLBACK");
        }

        int fitCount = calibrationSet.Count >= 20
            ? Math.Clamp(calibrationSet.Count / 2, 10, calibrationSet.Count - 10)
            : calibrationSet.Count;
        fitCount = Math.Clamp(fitCount, 1, calibrationSet.Count);
        int diagnosticsCount = calibrationSet.Count - fitCount;

        var fitSet = calibrationSet[..fitCount];
        var diagnosticsSet = diagnosticsCount > 0 ? calibrationSet[fitCount..] : calibrationSet;
        int diagnosticsStartIndex = diagnosticsCount > 0
            ? calibrationStartIndex + fitCount
            : calibrationStartIndex;
        var conformalSet = diagnosticsSet;
        var metaLabelSet = diagnosticsSet;
        var abstentionSet = diagnosticsSet;
        int conformalStartIndex = diagnosticsStartIndex;
        int metaLabelStartIndex = diagnosticsStartIndex;
        int abstentionStartIndex = diagnosticsStartIndex;
        string mode = "SHARED_FALLBACK";

        const int minConformalSamples = 10;
        const int minAdaptiveHeadSamples = 10;
        if (diagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples + minAdaptiveHeadSamples)
        {
            int conformalCount = Math.Max(minConformalSamples, diagnosticsSet.Count / 3);
            conformalCount = Math.Min(conformalCount, diagnosticsSet.Count - (minAdaptiveHeadSamples * 2));
            int remaining = diagnosticsSet.Count - conformalCount;
            int metaCount = remaining / 2;
            int abstentionCount = diagnosticsSet.Count - conformalCount - metaCount;
            if (metaCount >= minAdaptiveHeadSamples && abstentionCount >= minAdaptiveHeadSamples)
            {
                conformalSet = diagnosticsSet[..conformalCount];
                metaLabelSet = diagnosticsSet[conformalCount..(conformalCount + metaCount)];
                abstentionSet = diagnosticsSet[(conformalCount + metaCount)..];
                conformalStartIndex = diagnosticsStartIndex;
                metaLabelStartIndex = diagnosticsStartIndex + conformalCount;
                abstentionStartIndex = diagnosticsStartIndex + conformalCount + metaCount;
                mode = "DISJOINT";
            }
        }
        else if (diagnosticsSet.Count >= minConformalSamples + minAdaptiveHeadSamples)
        {
            conformalSet = diagnosticsSet[..minConformalSamples];
            metaLabelSet = diagnosticsSet[minConformalSamples..];
            abstentionSet = metaLabelSet;
            conformalStartIndex = diagnosticsStartIndex;
            metaLabelStartIndex = diagnosticsStartIndex + minConformalSamples;
            abstentionStartIndex = metaLabelStartIndex;
            mode = "CONFORMAL_DISJOINT_SHARED_ADAPTIVE";
        }

        return new GbmCalibrationPartition(
            FitSet: fitSet,
            DiagnosticsSet: diagnosticsSet,
            ConformalSet: conformalSet,
            MetaLabelSet: metaLabelSet,
            AbstentionSet: abstentionSet,
            FitStartIndex: calibrationStartIndex,
            DiagnosticsStartIndex: diagnosticsStartIndex,
            ConformalStartIndex: conformalStartIndex,
            MetaLabelStartIndex: metaLabelStartIndex,
            AbstentionStartIndex: abstentionStartIndex,
            AdaptiveHeadSplitMode: mode);
    }

    private static List<TrainingSample> ApplyFeatureMask(List<TrainingSample> samples, bool[] mask)
    {
        if (samples.Count == 0 || mask.Length == 0)
            return samples;

        return samples.Select(sample =>
        {
            var features = (float[])sample.Features.Clone();
            for (int j = 0; j < features.Length && j < mask.Length; j++)
            {
                if (!mask[j])
                    features[j] = 0f;
            }
            return sample with { Features = features };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  RANK STABILITY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Item 33: Rank-dispersion feature stability across fold importance rankings.</summary>
    private static double[] ComputeRankStability(List<double[]> foldImportances, int featureCount)
    {
        int k = foldImportances.Count;
        if (k < 2) return new double[featureCount];

        // Compute ranks for each fold
        var ranks = new double[k][];
        for (int fi = 0; fi < k; fi++)
        {
            var imp = foldImportances[fi];
            var indexed = imp.Select((v, idx) => (v, idx)).OrderByDescending(x => x.v).ToArray();
            ranks[fi] = new double[featureCount];
            for (int r = 0; r < indexed.Length && r < featureCount; r++)
                ranks[fi][indexed[r].idx] = r + 1;
        }

        // Compute per-feature rank stability: coefficient of concordance per feature
        var stability = new double[featureCount];
        for (int j = 0; j < featureCount; j++)
        {
            double sumRank = 0;
            for (int fi = 0; fi < k; fi++) sumRank += ranks[fi][j];
            double meanRank = sumRank / k;
            double variance = 0;
            for (int fi = 0; fi < k; fi++)
            {
                double d = ranks[fi][j] - meanRank;
                variance += d * d;
            }
            // Normalised stability: 0 = perfect agreement, 1 = maximum discord
            stability[j] = k > 1 ? Math.Sqrt(variance / (k - 1)) / featureCount : 0;
        }
        return stability;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0;
        int n = sharpePerFold.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++) { sumX += i; sumY += sharpePerFold[i]; sumXY += i * sharpePerFold[i]; sumXX += i * i; }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) > 1e-15 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count == 0) return [];
        var w = new double[count];
        for (int i = 0; i < count; i++) w[i] = Math.Exp(lambda * ((double)i / Math.Max(1, count - 1) - 1.0));
        double sum = w.Sum();
        if (sum > 1e-15) for (int i = 0; i < count; i++) w[i] /= sum;
        return w;
    }

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats((int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);
        var returns = new double[predictions.Length];
        double equity = 1.0, peak = 1.0, maxDD = 0;
        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted == predictions[i].Actual ? 0.01 : -0.01;
            returns[i] = r; equity += r;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }
        double mean = returns.Average();
        double varSum = 0;
        foreach (double r in returns) varSum += (r - mean) * (r - mean);
        double std = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        return (maxDD, std > 1e-10 ? mean / std * Math.Sqrt(252) : 0);
    }

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        if (threshold <= 0.0 || featureCount == 0)
            return BuildAllTrueMask(featureCount);
        double minImportance = threshold / featureCount;
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++) mask[j] = importance[j] >= minImportance;
        return mask;
    }

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        foreach (double v in values) sum += (v - mean) * (v - mean);
        return Math.Sqrt(sum / (values.Count - 1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE SANITIZATION + COMPACT SERIALIZATION (Items 45, Item 5)
    // ═══════════════════════════════════════════════════════════════════════

    private static int SanitizeTrees(List<GbmTree> trees)
    {
        int count = 0;
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            bool needsSanitize = false;
            foreach (var node in tree.Nodes)
                if (!double.IsFinite(node.LeafValue) || !double.IsFinite(node.SplitThreshold)) { needsSanitize = true; break; }
            if (needsSanitize)
            {
                foreach (var node in tree.Nodes)
                {
                    if (!double.IsFinite(node.LeafValue)) node.LeafValue = 0;
                    if (!double.IsFinite(node.SplitThreshold)) node.SplitThreshold = 0;
                    node.IsLeaf = true;
                }
                count++;
            }
        }
        return count;
    }

    /// <summary>Item 45: Remove placeholder/unreachable nodes to reduce serialization size.</summary>
    private static void CompactTreeNodes(List<GbmTree> trees)
    {
        foreach (var tree in trees)
        {
            if (tree.Nodes is null || tree.Nodes.Count <= 1) continue;

            // Mark reachable nodes via BFS from root
            var reachable = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                if (idx < 0 || idx >= tree.Nodes.Count) continue;
                var node = tree.Nodes[idx];
                if (node.IsLeaf) continue;
                if (node.LeftChild >= 0 && node.LeftChild < tree.Nodes.Count && reachable.Add(node.LeftChild))
                    queue.Enqueue(node.LeftChild);
                if (node.RightChild >= 0 && node.RightChild < tree.Nodes.Count && reachable.Add(node.RightChild))
                    queue.Enqueue(node.RightChild);
            }

            if (reachable.Count == tree.Nodes.Count) continue; // all reachable

            // Remap indices
            var indexMap = new Dictionary<int, int>();
            var compacted = new List<GbmNode>();
            var sortedReachable = reachable.OrderBy(x => x).ToList();
            for (int i = 0; i < sortedReachable.Count; i++)
                indexMap[sortedReachable[i]] = i;

            foreach (int oldIdx in sortedReachable)
            {
                var node = tree.Nodes[oldIdx];
                var newNode = new GbmNode
                {
                    IsLeaf = node.IsLeaf, LeafValue = node.LeafValue,
                    SplitFeature = node.SplitFeature, SplitThreshold = node.SplitThreshold,
                    SplitGain = node.SplitGain,
                    LeftChild = node.IsLeaf ? -1 : (indexMap.TryGetValue(node.LeftChild, out int lc) ? lc : -1),
                    RightChild = node.IsLeaf ? -1 : (indexMap.TryGetValue(node.RightChild, out int rc) ? rc : -1),
                };
                compacted.Add(newNode);
            }
            tree.Nodes = compacted;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TIMEOUT (Item 40)
    // ═══════════════════════════════════════════════════════════════════════

    private void CheckTimeoutBudget(Stopwatch sw, int timeoutMinutes, string phase)
    {
        if (timeoutMinutes <= 0) return;
        if (sw.Elapsed.TotalMinutes > timeoutMinutes)
        {
            _logger.LogWarning("GBM training timeout exceeded ({Elapsed:F1}m > {Budget}m) at {Phase}",
                sw.Elapsed.TotalMinutes, timeoutMinutes, phase);
            throw new OperationCanceledException(
                $"GBM training exceeded {timeoutMinutes}m budget at {phase}");
        }
    }

    // ── Item 43: Progress reporting via structured log at milestones ─────
    // (Implemented via _logger.LogInformation calls at key pipeline stages above)
}

/// <summary>Serialisable node in a GBM regression tree. Item 5: explicit child pointers (no heap gaps).</summary>
public sealed class GbmNode
{
    public bool   IsLeaf         { get; set; }
    public double LeafValue      { get; set; }
    public int    SplitFeature   { get; set; }
    public double SplitThreshold { get; set; }
    public int    LeftChild      { get; set; } = -1;
    public int    RightChild     { get; set; } = -1;
    /// <summary>Split gain at this node (for gain-weighted importance — Item 39).</summary>
    public double SplitGain      { get; set; }
}

/// <summary>Serialisable regression tree used by <see cref="GbmModelTrainer"/>.</summary>
public sealed class GbmTree
{
    public List<GbmNode>? Nodes { get; set; }
}
