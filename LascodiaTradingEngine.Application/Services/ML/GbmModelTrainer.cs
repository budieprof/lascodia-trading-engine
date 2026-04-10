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
///   <item>Z-score standardisation fit on each historical training slice and replayed onto holdouts.</item>
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
public sealed partial class GbmModelTrainer : IMLModelTrainer
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

        bool reuseWarmStartPreprocessing =
            warmStartContractCompatible &&
            warmStart is not null &&
            warmStart.Type == ModelType &&
            warmStart.GbmTreesJson is { Length: > 0 } &&
            warmStart.Means.Length == featureCount &&
            warmStart.Stds.Length == featureCount;

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
                inheritedFeatureLayout = true;
            }

            if (warmStart.ActiveFeatureMask is { Length: > 0 } warmMask &&
                warmMask.Length == featureCount &&
                warmMask.Any(active => !active))
            {
                inheritedActiveMask = (bool[])warmMask.Clone();
                inheritedFeatureLayout = true;
            }
        }

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(samples, hp, featureCount, numRounds, maxDepth, lr, ct);
        _logger.LogInformation(
            "GBM Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();
        CheckTimeoutBudget(trainingStopwatch, hp.TrainingTimeoutMinutes, "after CV"); // Item 40

        // ── 3. Final model splits: 60% train | 10% selection | 10% cal | ~20% test ──
        int trainEnd     = (int)(samples.Count * 0.60);
        int selectionEnd = (int)(samples.Count * 0.70);
        int calEnd       = (int)(samples.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var rawTrainSet     = samples[..Math.Max(0, trainEnd - embargo)];
        int selectionStart  = Math.Min(trainEnd + embargo, selectionEnd);
        var rawSelectionSet = samples[selectionStart..selectionEnd];
        var rawCalSet       = samples[Math.Min(selectionEnd + embargo, calEnd)..Math.Min(calEnd, samples.Count)];
        var rawTestSet      = samples[Math.Min(calEnd + embargo, samples.Count)..];

        if (hp.GbmConceptDriftGate && rawTrainSet.Count >= hp.MinSamples * 2)
            rawTrainSet = ApplyConceptDriftGate(rawTrainSet, featureCount, hp.MinSamples);

        if (rawTrainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"GBM: Insufficient training samples after splits: {rawTrainSet.Count} < {hp.MinSamples}");

        float[] means;
        float[] stds;
        if (reuseWarmStartPreprocessing)
        {
            means = warmStart!.Means;
            stds = warmStart.Stds;
            _logger.LogInformation(
                "GBM warm-start: reusing parent standardisation statistics for tree compatibility (gen={Gen}).",
                warmStart.GenerationNumber);
        }
        else
        {
            (means, stds) = ComputeStandardizationFromSamples(rawTrainSet);
        }

        var trainSet     = StandardizeSamples(rawTrainSet, means, stds);
        var selectionSet = StandardizeSamples(rawSelectionSet, means, stds);
        var calSet       = StandardizeSamples(rawCalSet, means, stds);
        var testSet      = StandardizeSamples(rawTestSet, means, stds);

        if (featurePipelineDescriptors.Length > 0)
        {
            trainSet     = ApplyFeatureTransforms(trainSet, featurePipelineDescriptors);
            selectionSet = ApplyFeatureTransforms(selectionSet, featurePipelineDescriptors);
            calSet       = ApplyFeatureTransforms(calSet, featurePipelineDescriptors);
            testSet      = ApplyFeatureTransforms(testSet, featurePipelineDescriptors);
        }

        if (inheritedActiveMask is { Length: > 0 })
        {
            trainSet     = ApplyFeatureMask(trainSet, inheritedActiveMask);
            selectionSet = ApplyFeatureMask(selectionSet, inheritedActiveMask);
            calSet       = ApplyFeatureMask(calSet, inheritedActiveMask);
            testSet      = ApplyFeatureMask(testSet, inheritedActiveMask);
        }

        int calibrationStartIndex = Math.Min(trainEnd + embargo, samples.Count);
        var calibrationPartition = BuildCalibrationPartition(calSet, calibrationStartIndex);
        var calibrationFitSet = calibrationPartition.FitSet;
        var calibrationDiagnosticsSet = calibrationPartition.DiagnosticsSet;
        var conformalSet = calibrationPartition.ConformalSet;
        var metaLabelSet = calibrationPartition.MetaLabelSet;
        var abstentionSet = calibrationPartition.AbstentionSet;

        // ── 3b. Multi-signal stationarity gate ──────────────────────────────
        var driftArtifact = ComputeGbmDriftDiagnostics(trainSet, featureCount, snapshotFeatureNames, hp.FracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"GBM drift gate rejected training: {driftArtifact.NonStationaryFeatureCount}/{featureCount} features flagged.");
            _logger.LogWarning("GBM stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, featureCount);
        }

        // ── 3b2. Class-imbalance gate ────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"GBM: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("GBM class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3b3. Adversarial validation ──────────────────────────────────────
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, featureCount, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, featureCount);
            _logger.LogInformation("GBM adversarial AUC={AUC:F3}", advAuc);
            if (advAuc > 0.65) _logger.LogWarning("GBM adversarial AUC={AUC:F3} indicates covariate shift.", advAuc);
            if (hp.GbmMaxAdversarialAuc > 0.0 && advAuc > hp.GbmMaxAdversarialAuc)
                throw new InvalidOperationException($"GBM: adversarial AUC={advAuc:F3} exceeds threshold {hp.GbmMaxAdversarialAuc:F3}.");
        }

        // ── 3c. Density-ratio importance weights (Item 27: MLP discriminator) ──
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = TryComputeDensityRatioWeightsGpu(trainSet, featureCount, hp.DensityRatioWindowDays, barsPerDay, trainingRandomSeed, ct)
                             ?? ComputeDensityRatioImportanceWeights(trainSet, featureCount, hp.DensityRatioWindowDays, barsPerDay, trainingRandomSeed);
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
                trainSet     = ApplyFeatureTransforms(trainSet, [efbDescriptor]);
                selectionSet = ApplyFeatureTransforms(selectionSet, [efbDescriptor]);
                calSet       = ApplyFeatureTransforms(calSet, [efbDescriptor]);
                testSet      = ApplyFeatureTransforms(testSet, [efbDescriptor]);
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
            selectionSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList,
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
            var maskedSelection = ApplyFeatureMask(selectionSet, activeMask);
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
                selectionSet     = maskedSelection;
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

        var selectionMetrics = EvaluateGbm(
            selectionSet, trees, baseLogOdds, lr, magWeights, magBias, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList, optimalThreshold);
        double selectionEce = ComputeEce(
            selectionSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        var selectionMetricSummary = CreateGbmMetricSummary(
            "SELECTION", selectionMetrics, selectionEce, optimalThreshold, selectionSet.Count);

        var calibrationMetrics = EvaluateGbm(
            postPruneCalibrationDiagnosticsSet, trees, baseLogOdds, lr, magWeights, magBias, effectiveFeatureCount,
            calibrationSnapshot, perTreeLrList, optimalThreshold);
        double calibrationDiagnosticsEce = ComputeEce(
            postPruneCalibrationDiagnosticsSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        var calibrationMetricSummary = CreateGbmMetricSummary(
            "CALIBRATION_DIAGNOSTICS",
            calibrationMetrics,
            calibrationDiagnosticsEce,
            optimalThreshold,
            postPruneCalibrationDiagnosticsSet.Count);
        var testMetricSummary = CreateGbmMetricSummary(
            "TEST", finalMetrics, ece, optimalThreshold, testSet.Count);

        int rawTrainCount = trainEnd;
        int rawSelectionCount = Math.Max(0, selectionEnd - trainEnd);
        int rawCalibrationCount = Math.Max(0, calEnd - selectionEnd);
        int rawTestCount = Math.Max(0, samples.Count - calEnd);
        int splitSelectionStartIndex = Math.Min(trainEnd + embargo, samples.Count);
        int splitCalibrationStartIndex = Math.Min(selectionEnd + embargo, samples.Count);
        int testStartIndex = Math.Min(calEnd + embargo, samples.Count);
        var splitSummary = new TrainingSplitSummary
        {
            RawTrainCount = rawTrainCount,
            RawSelectionCount = rawSelectionCount,
            RawCalibrationCount = rawCalibrationCount,
            RawTestCount = rawTestCount,
            TrainStartIndex = 0,
            TrainCount = trainSet.Count,
            SelectionStartIndex = splitSelectionStartIndex,
            SelectionCount = selectionSet.Count,
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

        // ── New evaluation metrics ───────────────────────────────────────────
        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(testSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        var (calResidualMean, calResidualStd, calResidualThreshold) =
            ComputeCalibrationResidualStats(calSet, trees, baseLogOdds, lr, effectiveFeatureCount, calibrationSnapshot, perTreeLrList);
        double[] featureVariances = ComputeFeatureVariances(trainSet, effectiveFeatureCount);

        // ── Warm-start artifact ──────────────────────────────────────────────
        var warmStartArtifact = BuildGbmWarmStartArtifact(
            attempted: warmStart is not null,
            compatible: warmStartContractCompatible,
            reusedTreeCount: warmStartContractCompatible ? (warmStart?.BaseLearnersK ?? 0) : 0,
            totalParentTrees: warmStart?.BaseLearnersK ?? 0,
            preprocessingReused: reuseWarmStartPreprocessing,
            featureLayoutInherited: inheritedFeatureLayout,
            oobReplayApplied: false,
            issues: []);

        // ── Scalar sanitization ──────────────────────────────────────────────
        ece = SafeGbm(ece, 1.0);
        optimalThreshold = SafeGbm(optimalThreshold, 0.5);
        avgKellyFraction = SafeGbm(avgKellyFraction);
        durbinWatson = SafeGbm(durbinWatson, 2.0);
        brierSkillScore = SafeGbm(brierSkillScore);
        calibrationLoss = SafeGbm(calibrationLoss);
        refinementLoss = SafeGbm(refinementLoss);
        predictionStability = SafeGbm(predictionStability);
        calResidualMean = SafeGbm(calResidualMean);
        calResidualStd = SafeGbm(calResidualStd);
        calResidualThreshold = SafeGbm(calResidualThreshold);
        conformalQHat = Math.Clamp(SafeGbm(conformalQHat, 0.5), 1e-7, 1.0 - 1e-7);
        conformalQHatBuy = Math.Clamp(SafeGbm(conformalQHatBuy, conformalQHat), 1e-7, 1.0 - 1e-7);
        conformalQHatSell = Math.Clamp(SafeGbm(conformalQHatSell, conformalQHat), 1e-7, 1.0 - 1e-7);

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
            GbmSelectionMetrics        = selectionMetricSummary,
            GbmCalibrationMetrics      = calibrationMetricSummary,
            GbmTestMetrics             = testMetricSummary,
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
            ReliabilityBinConfidence  = reliabilityBinConf.Length > 0 ? reliabilityBinConf : null,
            ReliabilityBinAccuracy    = reliabilityBinAcc.Length > 0 ? reliabilityBinAcc : null,
            ReliabilityBinCounts      = reliabilityBinCounts.Length > 0 ? reliabilityBinCounts : null,
            FeatureVariances          = featureVariances,
            GbmDriftArtifact          = driftArtifact,
            GbmWarmStartArtifact      = warmStartArtifact,
            GbmCalibrationResidualMean      = calResidualMean,
            GbmCalibrationResidualStd       = calResidualStd,
            GbmCalibrationResidualThreshold = calResidualThreshold,
        };

        SanitizeGbmSnapshotArrays(snapshot);

        var snapshotValidation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);
        if (!snapshotValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"GBM snapshot validation failed before serialization: {string.Join("; ", snapshotValidation.Issues)}");
        }

        var rawAuditSet = rawTestSet.Count > 0 ? rawTestSet : rawCalSet;
        var audit = RunGbmModelAudit(snapshot, rawAuditSet);
        snapshot.GbmTrainInferenceParityMaxError = audit.MaxParityError;
        snapshot.GbmAuditArtifact = audit.Artifact;

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        _logger.LogInformation(
            "GbmModelTrainer complete: {R} trees, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}, elapsed={Elapsed}ms",
            trees.Count, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore, trainingStopwatch.ElapsedMilliseconds);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
