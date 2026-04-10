using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Bagged logistic regression ensemble trainer.
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Run K-fold walk-forward CV (expanding window, embargo) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Train the final ensemble on 70 % of data with a 10 % Platt calibration fold and ~18 % hold-out test.</item>
///   <item>Each base learner is fitted on a <b>stratified</b> temporally-weighted biased bootstrap of the training split,
///         ensuring balanced buy/sell classes per bag to prevent direction bias in trending regimes.</item>
///   <item>Each learner sees a <b>random feature subset</b> (Random Forest-style) — √F features by default —
///         forcing ensemble diversity beyond what bootstrap alone provides.</item>
///   <item>Adam optimizer (β₁=0.9, β₂=0.999) + cosine-annealing LR schedule + per-learner early stopping.</item>
///   <item>Label smoothing (ε=LabelSmoothing) applied to cross-entropy targets.</item>
///   <item>Platt scaling (A, B) fitted on the calibration fold after the ensemble is frozen.</item>
///   <item>ECE (Expected Calibration Error) computed post-Platt on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set to maximise expected value.</item>
///   <item>A parallel linear regressor predicts magnitude in ATR-normalised units.</item>
///   <item>Optional feature pruning: low-importance features are masked and the ensemble is re-trained.</item>
///   <item>Optional warm-start: ensemble weights are initialised from the previous model snapshot.</item>
/// </list>
/// </para>
/// </summary>
[RegisterService]
public sealed partial class BaggedLogisticTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "BaggedLogisticEnsemble";
    private const string ModelVersion = "10.0";
    private const int    PolyTopN     = 5;

    // ── Meta-learner bundle (stacking) ────────────────────────────────────────

    /// <summary>Bundles meta-learner weights/bias so they can be threaded through helpers.</summary>
    internal readonly record struct MetaLearner(double[] Weights, double Bias)
    {
        public static readonly MetaLearner None = new([], 0.0);
        public bool IsActive => Weights is { Length: > 0 };
    }

    /// <summary>Bundles MLP hidden-layer parameters so they can be threaded through helpers.</summary>
    internal readonly record struct MlpState(double[][]? HiddenW, double[][]? HiddenB, int HiddenDim)
    {
        public static readonly MlpState None = new(null, null, 0);
        public bool IsActive => HiddenDim > 0 && HiddenW is not null && HiddenB is not null;
    }

    internal readonly record struct PolicyEvaluation(
        EvalMetrics Metrics,
        (int Predicted, int Actual)[] Predictions);

    internal readonly record struct WarmStartCompatibilityResult(
        bool IsCompatible,
        string[] Issues);

    internal readonly record struct HoldoutWindowPlan(
        int SelectionStart,
        int SelectionCount,
        int CalibrationStart,
        int CalibrationCount);

    // Adam hyper-parameters (fixed)
    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<BaggedLogisticTrainer> _logger;

    public BaggedLogisticTrainer(ILogger<BaggedLogisticTrainer> logger)
    {
        _logger = logger;
    }

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

        ValidateTrainingSamples(samples);
        var featureCount = samples[0].Features.Length;
        int sampleCount  = samples.Count;
        int barsPerDay   = BaggedLogisticTrainer.ResolveBarsPerDay(hp.BarsPerDay);
        int trainingRandomSeed = BaggedLogisticTrainer.ResolveTrainingRandomSeed(hp.TrainingRandomSeed);
        DateTime trainingRunUtc = DateTime.UtcNow;

        var snapshotFeatureNames = BuildFeatureNames(featureCount);
        string featureSchemaFingerprint = BaggedLogisticTrainer.ComputeFeatureSchemaFingerprint(snapshotFeatureNames);
        string preprocessingFingerprint = BaggedLogisticTrainer.ComputePreprocessingFingerprint(featureCount);
        string trainerFingerprint = BaggedLogisticTrainer.ComputeTrainerFingerprint(hp, featureCount);

        if (warmStart is not null)
        {
            var compatibility = BaggedLogisticTrainer.AssessWarmStartCompatibility(
                warmStart,
                featureSchemaFingerprint,
                preprocessingFingerprint,
                trainerFingerprint,
                featureCount);
            if (!compatibility.IsCompatible)
            {
                _logger.LogWarning(
                    "Warm-start snapshot rejected: {Issues}",
                    string.Join("; ", compatibility.Issues));
                warmStart = null;
            }
        }

        // ── 0. Incremental update fast-path ─────────────────────────────────
        // When UseIncrementalUpdate is enabled and a warm-start snapshot is available,
        // fine-tune the existing model on only the most recent data slice instead of
        // doing a full retrain. Much faster for adapting to regime changes.
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = BaggedLogisticTrainer.ComputeIncrementalRecentSampleCount(samples.Count, hp.DensityRatioWindowDays, barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "Incremental update: fine-tuning on last {N}/{Total} samples (≈{Days}d window)",
                    recentCount, samples.Count, hp.DensityRatioWindowDays);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    MaxEpochs             = Math.Max(20, hp.MaxEpochs / 5),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = hp.LearningRate / 5.0,
                    UseIncrementalUpdate  = false, // prevent recursion
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Final-model split boundaries + leakage-safe standardisation ───
        // Fit standardisation only on the actual training prefix so future cal/test
        // distribution does not leak into either walk-forward CV or the final model.
        int embargo     = hp.EmbargoBarCount;
        var (trainStdEnd, calStart, calEnd, testStart) = BaggedLogisticTrainer.ComputeFinalSplitBoundaries(
            sampleCount,
            embargo,
            hp.PurgeHorizonBars,
            MLFeatureHelper.LookbackWindow);

        var (means, stds) = ComputeStandardizationStats(samples[..trainStdEnd]);
        var allStd        = ApplyStandardization(samples, means, stds);

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(samples, hp, featureCount, trainingRunUtc, ct);
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: train | held-out selection/calibration | test ──
        var trainSet = allStd[..trainStdEnd];
        var calSet   = allStd[calStart..calEnd];
        var testSet  = allStd[testStart..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");
        if (calSet.Count < 30)
            throw new InvalidOperationException(
                $"Insufficient calibration samples after leakage-safe splits: {calSet.Count} < 30");
        if (testSet.Count < 20)
            throw new InvalidOperationException(
                $"Insufficient test samples after leakage-safe splits: {testSet.Count} < 20");

        var holdoutPlan = BaggedLogisticTrainer.ComputeHoldoutWindowPlan(calSet.Count);
        var selectionSet = calSet[holdoutPlan.SelectionStart..(holdoutPlan.SelectionStart + holdoutPlan.SelectionCount)];
        var calibrationSet = calSet[holdoutPlan.CalibrationStart..(holdoutPlan.CalibrationStart + holdoutPlan.CalibrationCount)];
        if (selectionSet.Count < 10)
            throw new InvalidOperationException(
                $"Insufficient selection samples after holdout split: {selectionSet.Count} < 10");
        if (calibrationSet.Count < 10)
            throw new InvalidOperationException(
                $"Insufficient calibration-fit samples after holdout split: {calibrationSet.Count} < 10");

        // Reduce epochs for warm-start runs — weights already near-optimal
        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 3b. Multi-signal stationarity gate ──────────────────────────────
        var driftArtifact = ComputeBaggedLogisticDriftDiagnostics(trainSet, featureCount, snapshotFeatureNames, hp.FracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"BaggedLogistic drift gate rejected: {driftArtifact.NonStationaryFeatureCount}/{featureCount} features flagged.");
            _logger.LogWarning("Stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, featureCount);
        }

        // ── 3b2. Class-imbalance gate ──────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"BaggedLogistic: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("BaggedLogistic class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3b3. Adversarial validation ────────────────────────────────────
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, featureCount, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, featureCount);
            _logger.LogInformation("BaggedLogistic adversarial AUC={AUC:F3}", advAuc);
            if (advAuc > 0.65) _logger.LogWarning("Adversarial AUC={AUC:F3} indicates covariate shift.", advAuc);
            if (hp.BaggedLogisticMaxAdversarialAuc > 0.0 && advAuc > hp.BaggedLogisticMaxAdversarialAuc)
                throw new InvalidOperationException($"BaggedLogistic: adversarial AUC={advAuc:F3} exceeds threshold.");
        }

        // ── 3c. Density-ratio importance weights ──────────────────────────────
        // Train a logistic discriminator to distinguish "recent" (last DensityRatioWindowDays
        // proxy samples) from "historical" samples. The resulting p/(1-p) weights are multiplied
        // into the temporal weights inside FitEnsemble to focus bootstrap on recent distribution.
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = TryComputeDensityRatioWeightsGpu(trainSet, featureCount, hp.DensityRatioWindowDays, barsPerDay, ct)
                             ?? BaggedLogisticTrainer.ComputeDensityRatioWeights(trainSet, featureCount, hp.DensityRatioWindowDays, barsPerDay);
            _logger.LogDebug("Density-ratio weights computed (recentWindow={W}% of train).",
                hp.DensityRatioWindowDays);
        }

        // ── 3d. Adaptive label smoothing from training label-ambiguity proxy ────
        double adaptiveLabelSmoothing = hp.LabelSmoothing; // default = fixed config value
        if (hp.UseAdaptiveLabelSmoothing && trainSet.Count > 0)
        {
            // Plain array sort — avoids LINQ Select+OrderBy+ToList+Count(predicate) overhead.
            var sortedMags = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) sortedMags[i] = Math.Abs(trainSet[i].Magnitude);
            Array.Sort(sortedMags);
            double p20Threshold = sortedMags[(int)(sortedMags.Length * 0.20)];
            int ambiguousCount = 0;
            foreach (var s in trainSet) if (Math.Abs(s.Magnitude) <= p20Threshold) ambiguousCount++;
            double ambiguousFraction = (double)ambiguousCount / trainSet.Count;
            adaptiveLabelSmoothing = Math.Clamp(ambiguousFraction * 0.5, 0.01, 0.20);
            effectiveHp = effectiveHp with { LabelSmoothing = adaptiveLabelSmoothing };
            _logger.LogInformation(
                "Adaptive label smoothing: ε={Eps:F3} (ambiguous-proxy fraction={Frac:P1})",
                adaptiveLabelSmoothing, ambiguousFraction);
        }

        // ── 3e. Covariate shift weight integration (parent model novelty scoring) ──
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = BaggedLogisticTrainer.ComputeCovariateShiftWeights(
                trainSet, parentBp, featureCount, warmStart.ActiveFeatureMask);
            if (densityWeights is not null)
            {
                // Plain loop — avoids LINQ Zip+ToArray allocation.
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug(
                "Covariate shift weights applied from parent model (generation={Gen}).",
                warmStart.GenerationNumber);
        }

        // ── 4. Fit ensemble (Adam + stratified bootstrap + label smoothing + feature subsampling) ──
        (double[][] weights, double[] biases, int[][]? featureSubsets, int polyLearnerStart,
         double[]? mtMagWeights, double mtMagBias,
         double[][]? ensembleMlpHiddenW, double[][]? ensembleMlpHiddenB,
         List<TrainingSample> oobTrainSet, double[] oobSamplingWeights) ensembleResult;
        try
        {
            ensembleResult = FitEnsemble(trainSet, effectiveHp, featureCount, warmStart, densityWeights, ct);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException!).Throw();
            throw; // unreachable, satisfies compiler
        }
        var (weights, biases, featureSubsets, polyLearnerStart, mtMagWeights, mtMagBias,
             ensMlpHW, ensMlpHB, selectedOobTrainSet, selectedOobSamplingWeights) = ensembleResult;

        // MLP state bundle — threaded through all post-training helpers
        var mlp = new MlpState(ensMlpHW, ensMlpHB, hp.MlpHiddenDim);
        int sanitizedCount = SanitizeLearners(weights, biases, mlp.HiddenW, mlp.HiddenB);
        if (sanitizedCount > 0)
            _logger.LogWarning("Post-fit sanitization: {N}/{K} learners had non-finite parameters.",
                sanitizedCount, weights.Length);

        // ── 5. Fit magnitude regressor ──────────────────────────────────────
        // When multi-task joint loss was active, FitEnsemble returns averaged magnitude
        // head weights; use those directly. Otherwise fall back to the standard OLS pass.
        var (magWeights, magBias) = mtMagWeights is { Length: > 0 }
            ? (mtMagWeights, mtMagBias)
            : FitLinearRegressor(trainSet, featureCount, hp, ct);

        // ── 5b. Fit stacking meta-learner on selection set ───────────────────
        // Meta-learner maps [p_0,...,p_{K-1}] → final probability via logistic regression,
        // learning optimal per-learner weights rather than enforcing uniform averaging.
        var meta = FitMetaLearner(selectionSet, weights, biases, featureCount, featureSubsets, mlp);
        _logger.LogDebug(
            "Stacking meta-learner: bias={B:F4} weights=[{W}]",
            meta.Bias, string.Join(",", meta.Weights.Select(w => w.ToString("F3"))));
        double[] gesWeights = this.MaybeRunGreedyEnsembleSelection(
            effectiveHp, selectionSet, weights, biases, featureCount, featureSubsets, meta, mlp: mlp);

        // ── 6. Platt calibration (on meta-learner output) ───────────────────
        var (plattA, plattB) = FitPlattScaling(calibrationSet, weights, biases, featureCount, featureSubsets, meta, mlp);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 6b. Class-conditional Platt (Buy / Sell separate scalers) ────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calibrationSet, weights, biases, featureCount, featureSubsets, meta, mlp, plattA, plattB);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        double temperatureScale = 0.0; // 0 = disabled until the final temperature search runs

        // ── 6c. Average Kelly fraction on calibration set ────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(
            calibrationSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 7. Final evaluation on held-out test set ────────────────────────
        var finalMetrics = EvaluateEnsemble(
            testSet, weights, biases, magWeights, magBias, plattA, plattB, featureCount, featureSubsets, meta, mlp);

        _logger.LogInformation(
            "Final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE post-Platt ─────────────────────────────────────────────────
        double ece = ComputeEce(testSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal decision threshold (tuned on selection set to avoid calibration/test leakage) ──
        double optimalThreshold = ComputeOptimalThreshold(
            selectionSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax, hp.ThresholdSearchStepBps, mlp);
        _logger.LogInformation(
            "EV-optimal threshold={Thr:F3} (step={Bps}bps)",
            optimalThreshold, hp.ThresholdSearchStepBps);

        // ── 10. Permutation feature importance ────────────────────────────────
        // Use the selection split for feature-pruning decisions so the held-out calibration
        // and test sets remain untouched until the post-selection policy fit.
        var featureImportance = selectionSet.Count >= 10
            ? ComputePermutationImportance(
                selectionSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp,
                optimalThreshold, ct)
            : new float[featureCount];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: GetFeatureDisplayName(idx)))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "Top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 11. Feature pruning re-train pass ─────────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, featureCount);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && featureCount - prunedCount >= 10)
        {
            _logger.LogInformation(
                "Feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, featureCount);

            var maskedTrain       = ApplyMask(trainSet, activeMask);
            var maskedSelection   = ApplyMask(selectionSet, activeMask);
            var maskedCalibration = ApplyMask(calibrationSet, activeMask);
            var maskedTest        = ApplyMask(testSet, activeMask);

            var currentLearnerCalAccuracies = ComputeLearnerCalAccuracies(
                selectionSet, weights, biases, featureCount, featureSubsets, mlp);
            var currentActiveLearnerMask = ComputeActiveLearnerMask(weights, biases);
            for (int k = 0; k < currentLearnerCalAccuracies.Length && k < currentActiveLearnerMask.Length; k++)
                if (!currentActiveLearnerMask[k]) currentLearnerCalAccuracies[k] = 0.0;
            var currentLearnerAccuracyWeights =
                BuildLearnerAccuracyWeights(currentLearnerCalAccuracies, currentActiveLearnerMask);

            double CurrentAcceptanceRawProb(float[] features)
            {
                var (rawProb, _) = ComputeEnsembleProbabilityAndStd(
                    features, weights, biases, featureCount, featureSubsets,
                    meta, gesWeights, currentLearnerAccuracyWeights, currentLearnerCalAccuracies,
                    currentActiveLearnerMask, mlp);
                return Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
            }

            var currentTrainedAtUtc = trainingRunUtc;
            var (currentA, currentB) = FitPlattScaling(maskedCalibration, CurrentAcceptanceRawProb);
            double currentTemp = 0.0;
            if (hp.FitTemperatureScale && maskedCalibration.Count >= 10)
            {
                currentTemp = FitTemperatureScaling(
                    maskedCalibration, CurrentAcceptanceRawProb,
                    currentA, currentB,
                    0.0, 0.0, 0.0, 0.0,
                    [],
                    0.0,
                    currentTrainedAtUtc);
            }

            var (currentABuy, currentBBuy, currentASell, currentBSell) = FitClassConditionalPlatt(
                maskedCalibration, CurrentAcceptanceRawProb, currentA, currentB, currentTemp);

            double CurrentPreIsotonicProb(float[] features)
            {
                return ApplyProductionCalibration(
                    CurrentAcceptanceRawProb(features), currentA, currentB, currentTemp,
                    currentABuy, currentBBuy, currentASell, currentBSell,
                    [],
                    0.0,
                    currentTrainedAtUtc);
            }

            double[] currentIso = FitIsotonicCalibration(maskedCalibration, CurrentPreIsotonicProb);
            if (hp.FitTemperatureScale && maskedCalibration.Count >= 10)
            {
                double refitCurrentTemp = FitTemperatureScaling(
                    maskedCalibration, CurrentAcceptanceRawProb,
                    currentA, currentB,
                    currentABuy, currentBBuy, currentASell, currentBSell,
                    currentIso,
                    hp.AgeDecayLambda,
                    currentTrainedAtUtc);

                if (Math.Abs(refitCurrentTemp - currentTemp) > 1e-6)
                {
                    currentTemp = refitCurrentTemp;
                    (currentABuy, currentBBuy, currentASell, currentBSell) = FitClassConditionalPlatt(
                        maskedCalibration, CurrentAcceptanceRawProb, currentA, currentB, currentTemp);
                    currentIso = FitIsotonicCalibration(maskedCalibration, CurrentPreIsotonicProb);
                }
            }

            double CurrentAcceptanceProb(float[] features)
            {
                return ApplyProductionCalibration(
                    CurrentAcceptanceRawProb(features),
                    currentA,
                    currentB,
                    currentTemp,
                    currentABuy,
                    currentBBuy,
                    currentASell,
                    currentBSell,
                    currentIso,
                    hp.AgeDecayLambda,
                    currentTrainedAtUtc);
            }

            double currentAcceptanceThreshold = ComputeOptimalThreshold(
                maskedSelection,
                CurrentAcceptanceProb,
                hp.ThresholdSearchMin,
                hp.ThresholdSearchMax,
                hp.ThresholdSearchStepBps);
            var currentAcceptanceMetrics = EvaluateEnsemble(
                maskedSelection, magWeights, magBias, CurrentAcceptanceProb, currentAcceptanceThreshold);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            (double[][] Weights, double[] Biases, int[][]? Subsets, int PolyStart,
             double[]? MtMagW, double MtMagB, double[][]? PrunedMlpHW, double[][]? PrunedMlpHB,
             List<TrainingSample> PrunedOobTrainSet, double[] PrunedOobSamplingWeights) prunedEnsemble;
            try
            {
                prunedEnsemble = FitEnsemble(maskedTrain, prunedHp, featureCount, null, densityWeights, ct);
            }
            catch (AggregateException pae) when (pae.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pae.InnerException!).Throw();
                throw; // unreachable
            }
            var (pw, pb, pSubsets, pPolyStart, pMtMagW, pMtMagB, _, _, prunedOobTrainSet, prunedOobSamplingWeights) = prunedEnsemble;
            var pMlp = new MlpState(prunedEnsemble.PrunedMlpHW, prunedEnsemble.PrunedMlpHB, prunedHp.MlpHiddenDim);
            int pSanitizedCount = SanitizeLearners(pw, pb, pMlp.HiddenW, pMlp.HiddenB);
            if (pSanitizedCount > 0)
                _logger.LogWarning("Pruned-model sanitization: {N}/{K} learners had non-finite parameters.",
                    pSanitizedCount, pw.Length);
            var (pmw, pmb)  = pMtMagW is { Length: > 0 }
                ? (pMtMagW, pMtMagB)
                : FitLinearRegressor(maskedTrain, featureCount, prunedHp, ct);
            var pMeta       = FitMetaLearner(maskedSelection, pw, pb, featureCount, pSubsets, pMlp);
            var pLearnerCalAccuracies = ComputeLearnerCalAccuracies(
                maskedSelection, pw, pb, featureCount, pSubsets, pMlp);
            var pActiveLearnerMask = ComputeActiveLearnerMask(pw, pb);
            for (int k = 0; k < pLearnerCalAccuracies.Length && k < pActiveLearnerMask.Length; k++)
                if (!pActiveLearnerMask[k]) pLearnerCalAccuracies[k] = 0.0;
            var pLearnerAccuracyWeights =
                BuildLearnerAccuracyWeights(pLearnerCalAccuracies, pActiveLearnerMask);
            var pGesWeights = this.MaybeRunGreedyEnsembleSelection(
                prunedHp, maskedSelection, pw, pb, featureCount, pSubsets, pMeta, pActiveLearnerMask, pMlp);

            double PFinalRawProb(float[] features)
            {
                var (rawProb, _) = ComputeEnsembleProbabilityAndStd(
                    features, pw, pb, featureCount, pSubsets,
                    pMeta, pGesWeights, pLearnerAccuracyWeights, pLearnerCalAccuracies,
                    pActiveLearnerMask, pMlp);
                return Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
            }

            var (pA, pB) = FitPlattScaling(maskedCalibration, PFinalRawProb);
            var pTrainedAtUtc = trainingRunUtc;
            double pTemp = 0.0;
            if (hp.FitTemperatureScale && maskedCalibration.Count >= 10)
            {
                pTemp = FitTemperatureScaling(
                    maskedCalibration, PFinalRawProb,
                    pA, pB,
                    0.0, 0.0, 0.0, 0.0,
                    [],
                    0.0,
                    pTrainedAtUtc);
            }

            var (pABuy, pBBuy, pASell, pBSell) = FitClassConditionalPlatt(
                maskedCalibration, PFinalRawProb, pA, pB, pTemp);

            double PPreIsotonicProb(float[] features)
            {
                return ApplyProductionCalibration(
                    PFinalRawProb(features), pA, pB, pTemp, pABuy, pBBuy, pASell, pBSell, [], 0.0, pTrainedAtUtc);
            }

            double[] pIso = FitIsotonicCalibration(maskedCalibration, PPreIsotonicProb);
            if (hp.FitTemperatureScale && maskedCalibration.Count >= 10)
            {
                double refitTemp = FitTemperatureScaling(
                    maskedCalibration, PFinalRawProb,
                    pA, pB,
                    pABuy, pBBuy, pASell, pBSell,
                    pIso,
                    hp.AgeDecayLambda,
                    pTrainedAtUtc);

                if (Math.Abs(refitTemp - pTemp) > 1e-6)
                {
                    pTemp = refitTemp;
                    (pABuy, pBBuy, pASell, pBSell) = FitClassConditionalPlatt(
                        maskedCalibration, PFinalRawProb, pA, pB, pTemp);
                    pIso = FitIsotonicCalibration(maskedCalibration, PPreIsotonicProb);
                }
            }

            double PFinalProductionProb(float[] features)
            {
                return ApplyProductionCalibration(
                    PFinalRawProb(features), pA, pB, pTemp, pABuy, pBBuy, pASell, pBSell, pIso, hp.AgeDecayLambda, pTrainedAtUtc);
            }

            double pOptimalThreshold = ComputeOptimalThreshold(
                maskedSelection,
                PFinalProductionProb,
                hp.ThresholdSearchMin,
                hp.ThresholdSearchMax,
                hp.ThresholdSearchStepBps);
            var prunedMetrics = EvaluateEnsemble(
                maskedSelection, pmw, pmb, PFinalProductionProb, pOptimalThreshold);

            if (prunedMetrics.Accuracy >= currentAcceptanceMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, currentAcceptanceMetrics.Accuracy);
                weights        = pw;     biases   = pb;
                magWeights     = pmw;    magBias  = pmb;
                plattA         = pA;     plattB   = pB;
                meta           = pMeta;
                finalMetrics   = prunedMetrics;
                featureSubsets = pSubsets;
                polyLearnerStart = pPolyStart;
                mlp            = pMlp;
                gesWeights = pGesWeights;
                selectedOobTrainSet     = prunedOobTrainSet;
                selectedOobSamplingWeights = prunedOobSamplingWeights;
                sanitizedCount          = pSanitizedCount;
                temperatureScale = pTemp;
                (plattABuy, plattBBuy, plattASell, plattBSell) = (pABuy, pBBuy, pASell, pBSell);
                ece = ComputeProductionEce(
                    maskedTest, pw, pb, pA, pB, pTemp, pABuy, pBBuy, pASell, pBSell, pIso,
                    hp.AgeDecayLambda, pTrainedAtUtc, featureCount, pSubsets, pMeta, pMlp);
                optimalThreshold = pOptimalThreshold;
            }
            else
            {
                _logger.LogInformation(
                    "Pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    currentAcceptanceMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask  = new bool[featureCount]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[featureCount]; Array.Fill(activeMask, true);
        }
        else
        {
            _logger.LogInformation(
                "Feature pruning skipped: only {Remaining}/{Total} features would remain after masking.",
                featureCount - prunedCount, featureCount);
            prunedCount = 0;
            activeMask = new bool[featureCount]; Array.Fill(activeMask, true);
        }

        // ── 11b. Finalize the deployed ensemble state ─────────────────────────
        // Any accepted feature pruning and any OOB learner pruning must settle first.
        var postPruneTrainSet       = prunedCount > 0 ? ApplyMask(trainSet, activeMask) : trainSet;
        var postPruneSelectionSet   = prunedCount > 0 ? ApplyMask(selectionSet, activeMask) : selectionSet;
        var postPruneCalibrationSet = prunedCount > 0 ? ApplyMask(calibrationSet, activeMask) : calibrationSet;
        var postPruneTestSet        = prunedCount > 0 ? ApplyMask(testSet, activeMask) : testSet;

        var provisionalActiveLearnerMask = ComputeActiveLearnerMask(weights, biases);
        var provisionalMeta = FitMetaLearner(postPruneSelectionSet, weights, biases, featureCount, featureSubsets, mlp);
        var provisionalLearnerCalAccuracies = ComputeLearnerCalAccuracies(
            postPruneSelectionSet, weights, biases, featureCount, featureSubsets, mlp);
        for (int k = 0; k < provisionalLearnerCalAccuracies.Length && k < provisionalActiveLearnerMask.Length; k++)
            if (!provisionalActiveLearnerMask[k]) provisionalLearnerCalAccuracies[k] = 0.0;
        var provisionalLearnerAccuracyWeights =
            BuildLearnerAccuracyWeights(provisionalLearnerCalAccuracies, provisionalActiveLearnerMask);
        double[] provisionalGesWeights = this.MaybeRunGreedyEnsembleSelection(
            hp,
            postPruneSelectionSet,
            weights,
            biases,
            featureCount,
            featureSubsets,
            provisionalMeta,
            provisionalActiveLearnerMask,
            mlp);

        double ProvisionalRawProb(float[] features)
        {
            var (rawProb, _) = ComputeEnsembleProbabilityAndStd(
                features, weights, biases, featureCount, featureSubsets,
                provisionalMeta, provisionalGesWeights, provisionalLearnerAccuracyWeights,
                provisionalLearnerCalAccuracies, provisionalActiveLearnerMask, mlp);
            return Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
        }

        var provisionalTrainedAtUtc = trainingRunUtc;
        double provisionalPlattA = 1.0;
        double provisionalPlattB = 0.0;
        if (postPruneCalibrationSet.Count >= 10)
            (provisionalPlattA, provisionalPlattB) = FitPlattScaling(postPruneCalibrationSet, ProvisionalRawProb);

        double provisionalTemperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalibrationSet.Count >= 10)
        {
            provisionalTemperatureScale = FitTemperatureScaling(
                postPruneCalibrationSet,
                ProvisionalRawProb,
                provisionalPlattA,
                provisionalPlattB,
                0.0,
                0.0,
                0.0,
                0.0,
                [],
                0.0,
                provisionalTrainedAtUtc);
        }

        var (provisionalPlattABuy, provisionalPlattBBuy, provisionalPlattASell, provisionalPlattBSell) =
            FitClassConditionalPlatt(
                postPruneCalibrationSet, ProvisionalRawProb, provisionalPlattA, provisionalPlattB, provisionalTemperatureScale);

        double ProvisionalPreIsotonicProb(float[] features)
        {
            return ApplyProductionCalibration(
                ProvisionalRawProb(features),
                provisionalPlattA,
                provisionalPlattB,
                provisionalTemperatureScale,
                provisionalPlattABuy,
                provisionalPlattBBuy,
                provisionalPlattASell,
                provisionalPlattBSell,
                [],
                0.0,
                provisionalTrainedAtUtc);
        }

        double[] provisionalIsotonicBp = FitIsotonicCalibration(postPruneCalibrationSet, ProvisionalPreIsotonicProb);
        if (hp.FitTemperatureScale && postPruneCalibrationSet.Count >= 10)
        {
            double refitProvisionalTemperatureScale = FitTemperatureScaling(
                postPruneCalibrationSet,
                ProvisionalRawProb,
                provisionalPlattA,
                provisionalPlattB,
                provisionalPlattABuy,
                provisionalPlattBBuy,
                provisionalPlattASell,
                provisionalPlattBSell,
                provisionalIsotonicBp,
                hp.AgeDecayLambda,
                provisionalTrainedAtUtc);

            if (Math.Abs(refitProvisionalTemperatureScale - provisionalTemperatureScale) > 1e-6)
            {
                provisionalTemperatureScale = refitProvisionalTemperatureScale;
                (provisionalPlattABuy, provisionalPlattBBuy, provisionalPlattASell, provisionalPlattBSell) =
                    FitClassConditionalPlatt(
                        postPruneCalibrationSet, ProvisionalRawProb, provisionalPlattA, provisionalPlattB,
                        provisionalTemperatureScale);
                provisionalIsotonicBp = FitIsotonicCalibration(postPruneCalibrationSet, ProvisionalPreIsotonicProb);
            }
        }

        double ProvisionalProductionProb(float[] features)
        {
            return ApplyProductionCalibration(
                ProvisionalRawProb(features),
                provisionalPlattA,
                provisionalPlattB,
                provisionalTemperatureScale,
                provisionalPlattABuy,
                provisionalPlattBBuy,
                provisionalPlattASell,
                provisionalPlattBSell,
                provisionalIsotonicBp,
                hp.AgeDecayLambda,
                provisionalTrainedAtUtc);
        }

        double ProvisionalProductionProbFromRaw(double rawProb)
        {
            return ApplyProductionCalibration(
                Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7),
                provisionalPlattA,
                provisionalPlattB,
                provisionalTemperatureScale,
                provisionalPlattABuy,
                provisionalPlattBBuy,
                provisionalPlattASell,
                provisionalPlattBSell,
                provisionalIsotonicBp,
                hp.AgeDecayLambda,
                provisionalTrainedAtUtc);
        }

        double provisionalOptimalThreshold = ComputeOptimalThreshold(
            postPruneSelectionSet,
            ProvisionalProductionProb,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.ThresholdSearchStepBps);

        int oobPrunedCount = 0;
        if (hp.OobPruningEnabled && hp.K >= 2)
        {
            oobPrunedCount = PruneByOobContribution(
                selectedOobTrainSet, weights, biases, selectedOobSamplingWeights, featureCount, featureSubsets, hp.K,
                provisionalMeta, provisionalGesWeights, provisionalLearnerAccuracyWeights,
                provisionalLearnerCalAccuracies, mlp, provisionalActiveLearnerMask,
                ProvisionalProductionProbFromRaw, provisionalOptimalThreshold);
            if (oobPrunedCount > 0)
                _logger.LogInformation(
                    "OOB pruning: removed {N}/{K} learners whose removal improved ensemble accuracy.",
                    oobPrunedCount, hp.K);
        }

        meta = FitMetaLearner(postPruneSelectionSet, weights, biases, featureCount, featureSubsets, mlp);
        var learnerCalAccuracies = ComputeLearnerCalAccuracies(
            postPruneSelectionSet, weights, biases, featureCount, featureSubsets, mlp);
        var activeLearnerMask = ComputeActiveLearnerMask(weights, biases);
        for (int k = 0; k < learnerCalAccuracies.Length && k < activeLearnerMask.Length; k++)
            if (!activeLearnerMask[k]) learnerCalAccuracies[k] = 0.0;
        var learnerAccuracyWeights = BuildLearnerAccuracyWeights(learnerCalAccuracies, activeLearnerMask);
        gesWeights = this.MaybeRunGreedyEnsembleSelection(
            hp,
            postPruneSelectionSet,
            weights,
            biases,
            featureCount,
            featureSubsets,
            meta,
            activeLearnerMask,
            mlp);
        if (learnerCalAccuracies.Length > 0)
            _logger.LogDebug("Per-learner cal accuracies: [{Accs}]",
                string.Join(", ", learnerCalAccuracies.Select(a => $"{a:P0}")));

        double FinalRawProb(float[] features)
        {
            var (rawProb, _) = ComputeEnsembleProbabilityAndStd(
                features, weights, biases, featureCount, featureSubsets,
                meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies, activeLearnerMask, mlp);
            return Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
        }

        double FinalGlobalCalibratedProb(float[] features)
        {
            double raw = FinalRawProb(features);
            return MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
        }

        plattA = 1.0;
        plattB = 0.0;
        if (postPruneCalibrationSet.Count >= 10)
            (plattA, plattB) = FitPlattScaling(postPruneCalibrationSet, FinalRawProb);
        avgKellyFraction = ComputeAvgKellyFraction(postPruneCalibrationSet, FinalGlobalCalibratedProb);
        _logger.LogDebug("Final stacking meta-learner: bias={B:F4} weights=[{W}]",
            meta.Bias, string.Join(",", meta.Weights.Select(w => w.ToString("F3"))));
        _logger.LogDebug("Final GES weights: [{W}]",
            gesWeights.Length > 0 ? string.Join(",", gesWeights.Select(w => w.ToString("F3"))) : string.Empty);
        _logger.LogDebug("Final Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        var trainedAtUtc = trainingRunUtc;
        temperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalibrationSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(
                postPruneCalibrationSet,
                FinalRawProb,
                plattA,
                plattB,
                0.0,
                0.0,
                0.0,
                0.0,
                [],
                0.0,
                trainedAtUtc);
            _logger.LogDebug("Temperature scaling: T={T:F4} (1.0=no correction)", temperatureScale);
        }

        (plattABuy, plattBBuy, plattASell, plattBSell) = FitClassConditionalPlatt(
            postPruneCalibrationSet, FinalRawProb, plattA, plattB, temperatureScale);

        double PreIsotonicProductionProb(float[] features)
        {
            return ApplyProductionCalibration(
                FinalRawProb(features),
                plattA,
                plattB,
                temperatureScale,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                [],
                0.0,
                trainedAtUtc);
        }

        double[] isotonicBp = FitIsotonicCalibrationGuarded(
            postPruneCalibrationSet, PreIsotonicProductionProb, hp.MinIsotonicCalibrationSamples);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        if (hp.FitTemperatureScale && postPruneCalibrationSet.Count >= 10)
        {
            double refitTemperatureScale = FitTemperatureScaling(
                postPruneCalibrationSet,
                FinalRawProb,
                plattA,
                plattB,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBp,
                hp.AgeDecayLambda,
                trainedAtUtc);

            if (Math.Abs(refitTemperatureScale - temperatureScale) > 1e-6)
            {
                temperatureScale = refitTemperatureScale;
                (plattABuy, plattBBuy, plattASell, plattBSell) = FitClassConditionalPlatt(
                    postPruneCalibrationSet, FinalRawProb, plattA, plattB, temperatureScale);
                isotonicBp = FitIsotonicCalibrationGuarded(
                    postPruneCalibrationSet, PreIsotonicProductionProb, hp.MinIsotonicCalibrationSamples);
            }
        }

        double FinalProductionProb(float[] features)
        {
            return ApplyProductionCalibration(
                FinalRawProb(features),
                plattA,
                plattB,
                temperatureScale,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBp,
                hp.AgeDecayLambda,
                trainedAtUtc);
        }

        double FinalProductionProbFromRaw(double rawProb)
        {
            return ApplyProductionCalibration(
                Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7),
                plattA,
                plattB,
                temperatureScale,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBp,
                hp.AgeDecayLambda,
                trainedAtUtc);
        }

        (double Probability, double EnsembleStd) FinalProductionProbAndStd(float[] features)
        {
            var (rawProb, ensembleStd) = ComputeEnsembleProbabilityAndStd(
                features, weights, biases, featureCount, featureSubsets,
                meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies, activeLearnerMask, mlp);
            return (FinalProductionProbFromRaw(rawProb), ensembleStd);
        }

        optimalThreshold = ComputeOptimalThreshold(
            postPruneSelectionSet,
            FinalProductionProb,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.ThresholdSearchStepBps);

        double oobAccuracy = ComputeOobAccuracy(
            selectedOobTrainSet, weights, biases, selectedOobSamplingWeights, featureCount, featureSubsets, hp.K,
            meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies,
            FinalProductionProbFromRaw, optimalThreshold, activeLearnerMask, mlp);
        _logger.LogInformation("OOB accuracy={OobAcc:P1}", oobAccuracy);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(postPruneCalibrationSet, FinalProductionProb, conformalAlpha);
        _logger.LogInformation("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        double[] finalSelectionImportanceScores = postPruneSelectionSet.Count >= 10
            ? ComputeCalPermutationImportance(postPruneSelectionSet, FinalRawProb, featureCount, ct)
            : new double[featureCount];
        int[] metaLabelTopFeatureIndices = ComputeTopFeatureIndices(finalSelectionImportanceScores, 5, featureCount);

        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            postPruneCalibrationSet,
            FinalProductionProb,
            weights,
            biases,
            featureCount,
            featureSubsets,
            meta,
            gesWeights,
            learnerAccuracyWeights,
            learnerCalAccuracies,
            optimalThreshold,
            activeLearnerMask,
            mlp,
            metaLabelTopFeatureIndices);
        double metaLabelThreshold = TuneMetaLabelThreshold(
            postPruneSelectionSet,
            FinalProductionProbAndStd,
            optimalThreshold,
            metaLabelWeights,
            metaLabelBias,
            metaLabelTopFeatureIndices);
        _logger.LogDebug(
            "Meta-label model: bias={B:F4} threshold={T:F3} topFeatures=[{Idx}]",
            metaLabelBias,
            metaLabelThreshold,
            string.Join(",", metaLabelTopFeatureIndices));

        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalibrationSet,
            FinalProductionProb,
            weights,
            biases,
            metaLabelWeights,
            metaLabelBias,
            featureCount,
            featureSubsets,
            meta,
            gesWeights,
            learnerAccuracyWeights,
            learnerCalAccuracies,
            optimalThreshold,
            activeLearnerMask,
            mlp,
            metaLabelTopFeatureIndices);
        var (abstentionThresholdGlobal, abstentionThresholdBuy, abstentionThresholdSell) = TuneAbstentionThresholds(
            postPruneSelectionSet,
            FinalProductionProbAndStd,
            optimalThreshold,
            metaLabelWeights,
            metaLabelBias,
            metaLabelThreshold,
            metaLabelTopFeatureIndices,
            abstentionWeights,
            abstentionBias,
            abstentionThreshold);
        abstentionThreshold = abstentionThresholdGlobal;

        double selectiveOptimalThreshold = TuneSelectiveDecisionThreshold(
            postPruneSelectionSet,
            magWeights,
            magBias,
            FinalProductionProbAndStd,
            metaLabelWeights,
            metaLabelBias,
            metaLabelThreshold,
            metaLabelTopFeatureIndices,
            abstentionWeights,
            abstentionBias,
            abstentionThreshold,
            abstentionThresholdBuy,
            abstentionThresholdSell,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.ThresholdSearchStepBps);
        if (Math.Abs(selectiveOptimalThreshold - optimalThreshold) > 1e-6)
        {
            optimalThreshold = selectiveOptimalThreshold;
            (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
                postPruneCalibrationSet,
                FinalProductionProb,
                weights,
                biases,
                featureCount,
                featureSubsets,
                meta,
                gesWeights,
                learnerAccuracyWeights,
                learnerCalAccuracies,
                optimalThreshold,
                activeLearnerMask,
                mlp,
                metaLabelTopFeatureIndices);
            metaLabelThreshold = TuneMetaLabelThreshold(
                postPruneSelectionSet,
                FinalProductionProbAndStd,
                optimalThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelTopFeatureIndices);
            (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
                postPruneCalibrationSet,
                FinalProductionProb,
                weights,
                biases,
                metaLabelWeights,
                metaLabelBias,
                featureCount,
                featureSubsets,
                meta,
                gesWeights,
                learnerAccuracyWeights,
                learnerCalAccuracies,
                optimalThreshold,
                activeLearnerMask,
                mlp,
                metaLabelTopFeatureIndices);
            (abstentionThresholdGlobal, abstentionThresholdBuy, abstentionThresholdSell) = TuneAbstentionThresholds(
                postPruneSelectionSet,
                FinalProductionProbAndStd,
                optimalThreshold,
                metaLabelWeights,
                metaLabelBias,
                metaLabelThreshold,
                metaLabelTopFeatureIndices,
                abstentionWeights,
                abstentionBias,
                abstentionThreshold);
            abstentionThreshold = abstentionThresholdGlobal;
        }
        _logger.LogDebug(
            "Abstention gate: bias={B:F4} threshold={T:F3} buy={TB:F3} sell={TS:F3}",
            abstentionBias,
            abstentionThreshold,
            abstentionThresholdBuy,
            abstentionThresholdSell);

        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && postPruneTrainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(postPruneTrainSet, featureCount, hp.MagnitudeQuantileTau);
            _logger.LogDebug("Quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        double[] jackknifeResiduals = ComputeJackknifeResiduals(
            selectedOobTrainSet, weights, biases, selectedOobSamplingWeights, featureCount, featureSubsets, hp.K,
            meta, gesWeights, learnerAccuracyWeights, learnerCalAccuracies, FinalProductionProbFromRaw,
            activeLearnerMask, mlp);
        _logger.LogInformation("Jackknife+ residuals computed: {N} samples", jackknifeResiduals.Length);

        var (dbMean, dbStd) = postPruneCalibrationSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalibrationSet, FinalProductionProb, activeMask)
            : (0.0, 0.0);
        _logger.LogDebug("Decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        double durbinWatson = ComputeDurbinWatson(postPruneTrainSet, magWeights, magBias, featureCount);
        _logger.LogDebug("Durbin-Watson statistic={DW:F4} (2=no autocorr, <1.5=positive autocorr)", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}). " +
                "Consider enabling AR feature injection in the next training cycle.",
                durbinWatson, hp.DurbinWatsonThreshold);

        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(postPruneTrainSet, featureCount, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "MI redundancy: {N} feature pairs exceed threshold {T:F2}×log(2): {Pairs}",
                    redundantPairs.Length, hp.MutualInfoRedundancyThreshold,
                    string.Join(", ", redundantPairs));
            else
                _logger.LogDebug("MI redundancy check: no redundant pairs above threshold {T:F2}.",
                    hp.MutualInfoRedundancyThreshold);
        }

        finalMetrics = BaggedLogisticTrainer.EvaluateSelectivePolicy(
            postPruneTestSet,
            magWeights,
            magBias,
            FinalProductionProbAndStd,
            optimalThreshold,
            metaLabelWeights,
            metaLabelBias,
            metaLabelThreshold,
            metaLabelTopFeatureIndices,
            abstentionWeights,
            abstentionBias,
            abstentionThreshold,
            abstentionThresholdBuy,
            abstentionThresholdSell).Metrics with { OobAccuracy = oobAccuracy };
        ece = ComputeProductionEce(postPruneTestSet, FinalProductionProb);
        _logger.LogInformation("Final deployed-base ECE={Ece:F4}", ece);
        _logger.LogInformation(
            "Final deployed eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        double ensembleDiversity = ComputeEnsembleDiversity(
            weights, featureCount, featureSubsets, activeLearnerMask, mlp);
        _logger.LogDebug("Ensemble diversity (avg pairwise ρ)={Div:F4}", ensembleDiversity);
        if (hp.MaxEnsembleDiversity < 1.0 && ensembleDiversity > hp.MaxEnsembleDiversity)
            _logger.LogWarning(
                "Ensemble diversity warning: avg ρ={Div:F3} > threshold {Max:F2}. " +
                "Consider increasing K or enabling MaxLearnerCorrelation enforcement.",
                ensembleDiversity, hp.MaxEnsembleDiversity);

        double brierSkillScore = ComputeBrierSkillScore(postPruneTestSet, FinalProductionProb);
        _logger.LogInformation("Brier Skill Score (BSS)={BSS:F4} (>0 beats naive predictor)", brierSkillScore);

        // Persist feature-importance weights from the calibration split so downstream workers
        // can consume them without pulling operational side-data from the held-out test split.
        var finalFeatureImportance = postPruneSelectionSet.Count >= 10
            ? ComputePermutationImportance(postPruneSelectionSet, FinalProductionProb, featureCount, optimalThreshold, ct)
            : new float[featureCount];
        var finalTopFeatures = finalFeatureImportance
            .Select((imp, idx) => (Importance: imp, Name: GetFeatureDisplayName(idx)))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "Final top 5 features: {Features}",
            string.Join(", ", finalTopFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        double[] finalCalImportanceScores = postPruneCalibrationSet.Count >= 10
            ? ComputeCalPermutationImportance(postPruneCalibrationSet, FinalRawProb, featureCount, ct)
            : new double[featureCount];

        var standardisedTrainFeatures = new List<float[]>(postPruneTrainSet.Count);
        foreach (var s in postPruneTrainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);
        avgKellyFraction = ComputeAvgKellyFraction(postPruneCalibrationSet, FinalProductionProb);

        var splitSummary = new TrainingSplitSummary
        {
            RawTrainCount = trainSet.Count,
            RawSelectionCount = selectionSet.Count,
            RawCalibrationCount = calibrationSet.Count,
            RawTestCount = testSet.Count,
            TrainStartIndex = 0,
            TrainCount = trainSet.Count,
            SelectionStartIndex = calStart + holdoutPlan.SelectionStart,
            SelectionCount = selectionSet.Count,
            SelectionPruningStartIndex = calStart + holdoutPlan.SelectionStart,
            SelectionPruningCount = selectionSet.Count,
            SelectionThresholdStartIndex = calStart + holdoutPlan.SelectionStart,
            SelectionThresholdCount = selectionSet.Count,
            SelectionKellyStartIndex = calStart + holdoutPlan.SelectionStart,
            SelectionKellyCount = selectionSet.Count,
            CalibrationStartIndex = calStart + holdoutPlan.CalibrationStart,
            CalibrationCount = calibrationSet.Count,
            CalibrationFitStartIndex = calStart + holdoutPlan.CalibrationStart,
            CalibrationFitCount = calibrationSet.Count,
            CalibrationDiagnosticsStartIndex = calStart + holdoutPlan.CalibrationStart,
            CalibrationDiagnosticsCount = calibrationSet.Count,
            ConformalStartIndex = calStart + holdoutPlan.CalibrationStart,
            ConformalCount = calibrationSet.Count,
            MetaLabelStartIndex = calStart + holdoutPlan.CalibrationStart,
            MetaLabelCount = calibrationSet.Count,
            AbstentionStartIndex = calStart + holdoutPlan.CalibrationStart,
            AbstentionCount = calibrationSet.Count,
            AdaptiveHeadSplitMode = "SELECTION_CALIBRATION",
            AdaptiveHeadCrossFitFoldCount = 0,
            TestStartIndex = testStart,
            TestCount = testSet.Count,
            EmbargoCount = embargo,
            TrainEmbargoDropped = Math.Max(0, calStart - trainStdEnd),
            SelectionEmbargoDropped = 0,
            CalibrationEmbargoDropped = Math.Max(0, testStart - calEnd),
        };

        // ── New evaluation metrics ───────────────────────────────────────────
        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(testSet, FinalProductionProb);
        var (murphyCalLoss, murphyRefLoss) =
            ComputeMurphyDecomposition(testSet, FinalProductionProb);
        var (calResidualMean, calResidualStd, calResidualThreshold) =
            ComputeCalibrationResidualStats(calibrationSet, FinalProductionProb);
        double predictionStability = ComputePredictionStabilityScore(testSet, FinalProductionProb);
        double[] featureVariances = ComputeFeatureVariances(trainSet, featureCount);

        var warmStartArtifact = BuildBaggedLogisticWarmStartArtifact(
            attempted: warmStart is not null,
            compatible: warmStart is not null,
            reusedLearnerCount: warmStart is not null ? weights.Length : 0,
            totalParentLearners: warmStart?.BaseLearnersK ?? 0,
            issues: []);

        // ── 12. Serialise model snapshot ──────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = snapshotFeatureNames,
            TrainingRandomSeed         = trainingRandomSeed,
            TrainingSplitSummary       = splitSummary,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = hp.K,
            Weights                    = weights,
            Biases                     = biases,
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calibrationSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = trainedAtUtc,
            TrainSamplesAtLastCalibration = postPruneTrainSet.Count,
            FeatureImportance          = finalFeatureImportance,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            FeatureSubsetIndices       = featureSubsets,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            MetaWeights                = meta.Weights,
            MetaBias                   = meta.Bias,
            LearnerAccuracyWeights     = learnerAccuracyWeights,
            IsotonicBreakpoints        = isotonicBp,
            OobAccuracy                = oobAccuracy,
            ConformalQHat              = conformalQHat,
            PolyLearnerStartIndex      = polyLearnerStart,
            FracDiffD                  = hp.FracDiffD,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = metaLabelThreshold,
            MetaLabelTopFeatureIndices = metaLabelTopFeatureIndices,
            JackknifeResiduals         = jackknifeResiduals,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            FeatureImportanceScores    = finalCalImportanceScores,
            EnsembleSelectionWeights   = gesWeights,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            AbstentionThresholdBuy     = abstentionThresholdBuy,
            AbstentionThresholdSell    = abstentionThresholdSell,
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
            OobPrunedLearnerCount      = oobPrunedCount,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            EnsembleDiversity          = ensembleDiversity,
            BrierSkillScore            = brierSkillScore,
            TrainedAtUtc               = trainedAtUtc,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            LearnerCalAccuracies       = learnerCalAccuracies,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            SanitizedLearnerCount      = sanitizedCount,
            MlpHiddenDim               = hp.MlpHiddenDim,
            MlpHiddenWeights           = mlp.HiddenW,
            MlpHiddenBiases            = mlp.HiddenB,
            ConformalCoverage          = hp.ConformalCoverage,
            FeatureSchemaFingerprint   = featureSchemaFingerprint,
            PreprocessingFingerprint   = preprocessingFingerprint,
            TrainerFingerprint         = trainerFingerprint,
            ReliabilityBinConfidence   = reliabilityBinConf.Length > 0 ? reliabilityBinConf : null,
            ReliabilityBinAccuracy     = reliabilityBinAcc.Length > 0 ? reliabilityBinAcc : null,
            ReliabilityBinCounts       = reliabilityBinCounts.Length > 0 ? reliabilityBinCounts : null,
            CalibrationLoss            = murphyCalLoss,
            RefinementLoss             = murphyRefLoss,
            PredictionStabilityScore   = predictionStability,
            FeatureVariances           = featureVariances,
            BaggedLogisticDriftArtifact       = driftArtifact,
            BaggedLogisticWarmStartArtifact   = warmStartArtifact,
            BaggedLogisticCalibrationResidualMean      = calResidualMean,
            BaggedLogisticCalibrationResidualStd       = calResidualStd,
            BaggedLogisticCalibrationResidualThreshold = calResidualThreshold,
        };

        SanitizeBaggedLogisticSnapshotArrays(snapshot);

        var auditResult = RunBaggedLogisticAudit(snapshot, testSet.Count > 0 ? testSet : calSet);
        snapshot.BaggedLogisticAuditArtifact = auditResult.Artifact;
        if (auditResult.Findings.Length > 0)
            _logger.LogWarning("BaggedLogistic audit findings: {Findings}", string.Join("; ", auditResult.Findings));

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
