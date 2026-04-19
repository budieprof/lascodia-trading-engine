using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade SAMME AdaBoost trainer (Rec #125).
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples (means/stds stored in snapshot for inference parity).</item>
///   <item>Walk-forward cross-validation (expanding window, embargo) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Final splits: 60 % train | 10 % selection | 10 % calibration | ~20 % held-out test (with embargo gaps).</item>
///   <item>Optional warm-start: loads existing stumps from a parent AdaBoost snapshot; adds only K/3 residual rounds.</item>
///   <item>Boosting weights initialised with exponential temporal-decay + class-balance correction.</item>
///   <item>Warm-start weight replay: parent stump updates replayed on new training set so new rounds focus on parent's failures.</item>
///   <item>Stump search uses an O(m log m) sorted prefix-sum sweep — faster than the naïve O(V×m) scan.</item>
///   <item>Early degenerate-stump detection: stops boosting when no split beats random chance.</item>
///   <item>NaN/Inf alpha sanitization before snapshot serialization.</item>
///   <item>Platt scaling (A, B) fitted via SGD on the frozen calibration fold.</item>
///   <item>Isotonic calibration (PAVA) applied post-Platt for monotone probability correction.</item>
///   <item>ECE (Expected Calibration Error) computed on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the selection set (no test-set leakage).</item>
///   <item>Magnitude linear regressor trained with Adam + Huber loss + cosine-annealing LR + early stopping.</item>
///   <item>Permutation feature importance computed on the held-out test set (Fisher-Yates shuffle, fixed seed).</item>
///   <item>Split-conformal q̂ computed at the configured coverage level for prediction-set guarantees.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Brier Skill Score vs. naïve base-rate predictor.</item>
///   <item>Multi-signal stationarity gate: ACF, PSI, CUSUM, ADF-like, KPSS-like (warns or rejects).</item>
///   <item>Class-imbalance warning when Buy/Sell split is outside 35/65; rejection at 15/85.</item>
///   <item>Incremental update fast-path: fine-tunes on the most recent DensityRatioWindowDays of data when warm-starting.</item>
///   <item>GPU acceleration for density-ratio weights, adversarial validation, and Platt scaling when CUDA is available.</item>
///   <item>Cross-fit adaptive heads: meta-label and abstention models fitted via K-fold cross-validation.</item>
/// </list>
/// </para>
/// Alphas stored in <c>ModelSnapshot.Weights[0]</c>; stump trees in <c>ModelSnapshot.GbmTreesJson</c>.
/// Registered as a keyed <see cref="IMLModelTrainer"/> with key <c>"adaboost"</c>.
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.AdaBoost)]
public sealed partial class AdaBoostModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "AdaBoost";
    private const string ModelVersion = "2.2";

    private const double Eps = 1e-10;
    private const double DefaultConditionalRoutingThreshold = 0.5;
    private const int MinimumCalibrationSampleCount = 20;
    private const int MinimumTestSampleCount = 20;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<AdaBoostModelTrainer> _logger;

    public AdaBoostModelTrainer(ILogger<AdaBoostModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ───────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct, recursionDepth: 0), ct);
    }

    // ── Core training orchestrator (synchronous, runs on thread-pool via Task.Run) ──

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct,
        int                  recursionDepth = 0)
    {
        ct.ThrowIfCancellationRequested();

        if (samples.Count == 0)
            throw new InvalidOperationException("AdaBoostModelTrainer requires at least one sample.");

        if (samples[0].Features is null || samples[0].Features.Length == 0)
            throw new InvalidOperationException("AdaBoostModelTrainer requires non-empty feature vectors.");

        int F = samples[0].Features.Length;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Features is null)
                throw new InvalidOperationException($"Sample {i} has a null feature vector.");
            if (samples[i].Features.Length != F)
                throw new InvalidOperationException(
                    $"AdaBoostModelTrainer requires consistent feature counts; sample {i} has {samples[i].Features.Length}, expected {F}.");
        }

        string[] snapshotFeatureNames = AdaBoostSnapshotSupport.ResolveFeatureNames(F);
        string featureSchemaFingerprint = AdaBoostSnapshotSupport.ComputeFeatureSchemaFingerprint(snapshotFeatureNames);
        int K = hp.K > 0 ? hp.K : 20;
        string initialPreprocessingFingerprint = AdaBoostSnapshotSupport.ComputePreprocessingFingerprint(F, null);
        string trainerFingerprint = AdaBoostSnapshotSupport.ComputeTrainerFingerprint(hp, F, K, hp.AdaBoostMaxTreeDepth >= 2 ? 2 : 1);
        int trainingRandomSeed = ComputeTrainingRandomSeed(featureSchemaFingerprint, trainerFingerprint, samples.Count);
        TrySeedTorch(trainingRandomSeed);

        // ── 0. Incremental update fast-path ──────────────────────────────────
        if (recursionDepth > 1)
            throw new InvalidOperationException(
                "AdaBoost Train recursion depth exceeded (max 1); incremental update cannot recurse.");

        if (warmStart?.Type == ModelType && hp.UseIncrementalUpdate && hp.DensityRatioWindowDays > 0)
        {
            int barsPerDay  = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "AdaBoost incremental update: fine-tuning on last {N}/{Total} samples (≈{Days}d window)",
                    recentCount, samples.Count, hp.DensityRatioWindowDays);
                var incrHp = hp with { K = Math.Max(5, K / 3), UseIncrementalUpdate = false };
                return Train(samples[^recentCount..].ToList(), incrHp, warmStart, parentModelId, ct,
                             recursionDepth: recursionDepth + 1);
            }
        }

        // ── 1. Input validation ───────────────────────────────────────────────
        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"AdaBoostModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        _logger.LogInformation(
            "AdaBoostModelTrainer starting: {N} samples, F={F}, K={K}", samples.Count, F, K);

        // ── 1b. Outlier winsorization ─────────────────────────────────────────
        int winsorBound = (int)(samples.Count * 0.60);
        if (hp.AdaBoostWinsorizePercentile > 0.0)
        {
            WinsorizeFeatures(samples, F, winsorBound, hp.AdaBoostWinsorizePercentile);
            _logger.LogInformation(
                "AdaBoost winsorized features at p={Pctile:F3}", hp.AdaBoostWinsorizePercentile);
        }

        // ── 2. Final splits: 60 % train | 10 % selection | 10 % cal | ~20 % test ──
        int trainEnd     = (int)(samples.Count * 0.60);
        int selectionEnd = (int)(samples.Count * 0.70);
        int calEnd       = (int)(samples.Count * 0.80);
        int embargo      = hp.EmbargoBarCount;

        int trainLimit     = Math.Max(0, trainEnd - embargo);
        int selectionStart = Math.Min(trainEnd + embargo, selectionEnd);
        int calStart       = Math.Min(selectionEnd + embargo, calEnd);
        int testStart      = Math.Min(calEnd + embargo, samples.Count);

        int selectionCount   = Math.Max(0, selectionEnd - selectionStart);
        int calibrationCount = Math.Max(0, calEnd - calStart);
        int testCount        = Math.Max(0, samples.Count - testStart);

        // ── 3. Z-score standardisation — fit scaler on the actual embargo-purged training slice ──
        if (trainLimit == 0)
            throw new InvalidOperationException("Insufficient training samples remain after the embargo split.");
        if (selectionCount < MinimumCalibrationSampleCount)
            throw new InvalidOperationException(
                $"AdaBoostModelTrainer requires at least {MinimumCalibrationSampleCount} selection samples after embargo; got {selectionCount}.");
        if (calibrationCount < MinimumCalibrationSampleCount)
            throw new InvalidOperationException(
                $"AdaBoostModelTrainer requires at least {MinimumCalibrationSampleCount} calibration samples after embargo; got {calibrationCount}.");
        if (testCount < MinimumTestSampleCount)
            throw new InvalidOperationException(
                $"AdaBoostModelTrainer requires at least {MinimumTestSampleCount} test samples after embargo; got {testCount}.");

        var trainRawFeatures = new List<float[]>(trainLimit);
        for (int i = 0; i < trainLimit; i++) trainRawFeatures.Add(samples[i].Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(trainRawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 4. Walk-forward cross-validation ─────────────────────────────────
        var (cvResult, cvEquityCurveGateFailed) = RunWalkForwardCV(samples, hp, F, K, ct);
        if (cvEquityCurveGateFailed)
        {
            _logger.LogWarning("AdaBoost CV equity-curve/Sharpe-trend gate failed: model rejected.");
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        }
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2} sharpeTrend={Trend:F3}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe, cvResult.SharpeTrend);

        ct.ThrowIfCancellationRequested();

        var trainSet     = allStd[..trainLimit];
        var selectionSet = selectionStart < selectionEnd ? allStd[selectionStart..selectionEnd] : [];
        var calSet       = calStart < calEnd             ? allStd[calStart..calEnd]             : [];
        var testSet      = testStart < allStd.Count      ? allStd[testStart..]                 : [];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 4a. Adversarial validation (train vs test covariate shift) ────────
        if (testSet.Count >= 20 && trainSet.Count >= 20 && IsTorchCpuAvailable)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, F, _logger, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, F, _logger, ct);
            _logger.LogInformation(
                "Adversarial validation AUC={AUC:F3} (0.50=no shift, >0.65=significant shift)", advAuc);
            if (advAuc > 0.65)
                _logger.LogWarning(
                    "Adversarial AUC={AUC:F3} indicates meaningful train/test covariate shift.", advAuc);
            if (hp.AdaBoostMaxAdversarialAuc > 0.0 && advAuc > hp.AdaBoostMaxAdversarialAuc)
                throw new InvalidOperationException(
                    $"AdaBoost: adversarial AUC={advAuc:F3} exceeds rejection threshold {hp.AdaBoostMaxAdversarialAuc:F3}.");
        }

        // ── 4b. Multi-signal stationarity gate ────────────────────────────────
        var driftArtifact = ComputeDriftDiagnostics(trainSet, F, snapshotFeatureNames, hp.FracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"AdaBoost drift gate rejected training: {driftArtifact.NonStationaryFeatureCount}/{F} features flagged.");
            _logger.LogWarning(
                "Stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, F);
        }

        // ── 4c. Class-imbalance check ──────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException(
                    $"AdaBoost: extreme class imbalance (Buy={buyRatio:P1}). Training would produce a degenerate model.");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning(
                    "AdaBoost class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 4d. Density-ratio importance weights (GPU if available) ───────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50 && IsTorchCpuAvailable)
        {
            densityWeights = TryComputeDensityRatioWeightsGpu(trainSet, F, hp.DensityRatioWindowDays, hp.BarsPerDay, ct)
                             ?? ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays, hp.BarsPerDay, ct);
            _logger.LogDebug("Density-ratio weights computed (recentWindow≈{W}d).", hp.DensityRatioWindowDays);
        }

        // ── 4e. Covariate shift weights ───────────────────────────────────────
        if (hp.UseCovariateShiftWeights && IsTorchCpuAvailable &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, F);
            if (densityWeights is not null)
            {
                double blendSum = 0;
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                {
                    densityWeights[i] *= csWeights[i];
                    blendSum += densityWeights[i];
                }
                if (blendSum > 0)
                    for (int i = 0; i < densityWeights.Length; i++) densityWeights[i] /= blendSum;
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("Covariate shift weights applied from parent model (generation={Gen}).",
                warmStart.GenerationNumber);
        }

        // ── 5. Warm-start: load existing stumps from parent snapshot ──────────
        int effectiveK    = K;
        int generationNum = 0;
        var warmStumps    = new List<GbmTree>();
        var warmAlphas    = new List<double>();
        var warmStartArtifact = BuildWarmStartArtifact(false, false, 0, 0, false, false, []);

        if (warmStart?.Type == ModelType &&
            warmStart.GbmTreesJson is { Length: > 0 } &&
            warmStart.Weights is { Length: > 0 })
        {
            // Attempt schema migration for older snapshots
            if (warmStart.Version != ModelVersion)
            {
                var migrated = MigrateWarmStartSnapshot(warmStart, _logger);
                if (migrated is not null) warmStart = migrated;
                else warmStart = null;
            }

            if (warmStart is not null)
            {
                try
                {
                    var compatibility = AdaBoostSnapshotSupport.AssessWarmStartCompatibility(
                        warmStart, snapshotFeatureNames, featureSchemaFingerprint,
                        initialPreprocessingFingerprint, trainerFingerprint, F);
                    if (!compatibility.IsCompatible)
                        throw new InvalidOperationException(string.Join("; ", compatibility.Issues));

                    var loaded = JsonSerializer.Deserialize<List<GbmTree>>(warmStart.GbmTreesJson, JsonOptions);
                    if (loaded is { Count: > 0 } && warmStart.Weights[0].Length == loaded.Count)
                    {
                        warmStumps    = loaded;
                        warmAlphas    = warmStart.Weights[0].ToList();
                        double wsFrac = hp.AdaBoostWarmStartRoundsFraction > 0.0
                                        ? hp.AdaBoostWarmStartRoundsFraction : 1.0 / 3.0;
                        effectiveK    = Math.Max(5, (int)(K * wsFrac));
                        generationNum = warmStart.GenerationNumber + 1;
                        _logger.LogInformation(
                            "AdaBoost warm-start: loaded {N} stumps from parent (gen={Gen}); adding up to {New} residual rounds.",
                            warmStumps.Count, warmStart.GenerationNumber, effectiveK);
                    }
                    else
                    {
                        throw new InvalidOperationException("Warm-start stump/alpha counts do not match.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "AdaBoost warm-start deserialization failed ({Msg}); starting cold.", ex.Message);
                    warmStumps = [];
                    warmAlphas = [];
                }
            }
        }

        // ── 6. Fit AdaBoost stumps ────────────────────────────────────────────
        int m      = trainSet.Count;
        var labels = new int[m];
        for (int i = 0; i < m; i++) labels[i] = trainSet[i].Direction > 0 ? 1 : -1;

        // ── 6a. Per-sample adaptive label smoothing ───────────────────────────
        double adaptiveLabelSmoothing = 0.0;
        var    softLabels             = new double[m];
        if (hp.UseAdaptiveLabelSmoothing)
        {
            double maxMag = 0.0;
            foreach (var s in trainSet)
            {
                double mag = Math.Abs((double)s.Magnitude);
                if (mag > maxMag) maxMag = mag;
            }
            double epsSum = 0.0;
            for (int i = 0; i < m; i++)
            {
                double eps_i  = maxMag > 1e-9
                    ? Math.Clamp(1.0 - Math.Abs((double)trainSet[i].Magnitude) / maxMag, 0.0, 0.20)
                    : 0.0;
                softLabels[i] = labels[i] * (1.0 - eps_i);
                epsSum       += eps_i;
            }
            adaptiveLabelSmoothing = epsSum / m;
            _logger.LogInformation("Adaptive label smoothing (per-sample): avgε={Eps:F3}", adaptiveLabelSmoothing);
        }
        else
        {
            for (int i = 0; i < m; i++) softLabels[i] = labels[i];
        }

        double[] boostWeights = InitialiseBoostWeights(trainSet, hp.TemporalDecayLambda, densityWeights);

        bool replayWarmStartWeights = warmStumps.Count > 0 && warmAlphas.Count == warmStumps.Count;
        if (replayWarmStartWeights &&
            hp.DurbinWatsonThreshold > 0.0 &&
            warmStart?.DurbinWatsonStatistic > 0.0 &&
            warmStart.DurbinWatsonStatistic < hp.DurbinWatsonThreshold)
        {
            _logger.LogWarning(
                "Warm-start weight replay skipped: parent DW={DW:F3} < threshold {Thr:F2}.",
                warmStart.DurbinWatsonStatistic, hp.DurbinWatsonThreshold);
            replayWarmStartWeights = false;
        }
        if (replayWarmStartWeights)
            AdjustWarmStartWeights(boostWeights, labels, trainSet, warmStumps, warmAlphas);

        // Build warm-start artifact now that we know the outcome
        warmStartArtifact = BuildWarmStartArtifact(
            attempted: warmStumps.Count > 0 || (warmStart?.Type == ModelType),
            compatible: warmStumps.Count > 0,
            reusedStumpCount: warmStumps.Count,
            totalParentStumps: warmStart?.BaseLearnersK ?? 0,
            weightReplayApplied: replayWarmStartWeights,
            weightReplaySkippedDueToRegimeChange: warmStumps.Count > 0 && !replayWarmStartWeights,
            compatibilityIssues: []);

        var stumps = new List<GbmTree>(warmStumps);
        var alphas = new List<double>(warmAlphas);

        var    sortKeys    = new double[m];
        var    sortIndices = new int[m];
        double shrinkage   = hp.AdaBoostAlphaShrinkage > 0.0 ? hp.AdaBoostAlphaShrinkage : 1.0;
        bool   sammeR      = hp.UseSammeR;
        int    treeDepth   = hp.AdaBoostMaxTreeDepth >= 2 ? 2 : 1;
        bool   jointDepth2 = treeDepth == 2 && hp.UseJointDepth2Search;

        for (int round = 0; round < effectiveK && !ct.IsCancellationRequested; round++)
        {
            var (bestFi, bestThresh, bestParity, bestErr) =
                FindBestStump(trainSet, labels, boostWeights, F, sortKeys, sortIndices);

            if (!double.IsFinite(bestErr) || bestErr >= 0.5 - Eps)
            {
                _logger.LogWarning(
                    "AdaBoost round {R}: degenerate stump (err={Err:F4} ≥ 0.5), stopping early.",
                    round + 1, bestErr);
                break;
            }

            GbmTree tree;
            double  alpha;

            if (sammeR)
            {
                tree  = treeDepth == 2
                    ? (jointDepth2
                        ? BuildJointDepth2Tree(trainSet, labels, boostWeights, F, sortKeys, sortIndices, true, null)
                        : BuildDepth2Tree(bestFi, bestThresh, trainSet, labels,
                                          boostWeights, F, sortKeys, sortIndices, true))
                    : BuildSammeRStump(bestFi, bestThresh, trainSet, labels, boostWeights, m);
                alpha = 1.0;
                alphas.Add(alpha);
                stumps.Add(tree);

                double wSum = 0;
                for (int i = 0; i < m; i++)
                {
                    double hR = PredictStump(tree, trainSet[i].Features);
                    boostWeights[i] *= Math.Exp(-softLabels[i] * hR);
                    wSum += boostWeights[i];
                }
                if (wSum > 0) for (int i = 0; i < m; i++) boostWeights[i] /= wSum;
            }
            else
            {
                double err = Math.Max(Eps, Math.Min(1 - Eps, bestErr));
                alpha = shrinkage * 0.5 * Math.Log((1 - err) / err);
                tree  = treeDepth == 2
                    ? (jointDepth2
                        ? BuildJointDepth2Tree(trainSet, labels, boostWeights, F, sortKeys, sortIndices, false, null)
                        : BuildDepth2Tree(bestFi, bestThresh, trainSet, labels,
                                          boostWeights, F, sortKeys, sortIndices, false))
                    : BuildStump(bestFi, bestThresh, bestParity);
                alphas.Add(alpha);
                stumps.Add(tree);

                double wSum = 0;
                for (int i = 0; i < m; i++)
                {
                    double pred = PredictStump(tree, trainSet[i].Features);
                    boostWeights[i] *= Math.Exp(-alpha * softLabels[i] * pred);
                    wSum += boostWeights[i];
                }
                if (wSum > 0) for (int i = 0; i < m; i++) boostWeights[i] /= wSum;
            }

            _logger.LogDebug(
                "AdaBoost round {R}/{K}: feature={Fi}, err={Err:F4}, alpha={A:F4} sammeR={SR} depth={D}",
                round + 1, effectiveK, bestFi, bestErr, alpha, sammeR, treeDepth);
        }

        ct.ThrowIfCancellationRequested();

        // ── 7. NaN/Inf alpha sanitization ─────────────────────────────────────
        int sanitizedCount = 0;
        for (int k = 0; k < stumps.Count; k++)
        {
            if (!double.IsFinite(alphas[k]) || stumps[k].Nodes is not { Count: > 0 })
            {
                alphas[k] = 0.0;
                sanitizedCount++;
                _logger.LogWarning("AdaBoost sanitized stump {K}: non-finite alpha zeroed.", k);
            }
        }

        // ── 8. Platt scaling on calibration fold (GPU if available) ───────────
        var gpuPlatt = TryFitPlattScalingGpu(calSet, stumps, alphas, ct);
        var (plattA, plattB) = gpuPlatt ?? FitPlattScaling(calSet, stumps, alphas);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 8b. Temperature scaling selection ─────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            double candidateTemperature = FitTemperatureScaling(calSet, stumps, alphas, ct);
            double plattNll = ComputeCalibrationNll(calSet, stumps, alphas, plattA, plattB);
            double tempNll  = ComputeCalibrationNll(calSet, stumps, alphas, plattA, plattB, candidateTemperature);
            if (tempNll + 1e-6 < plattNll) temperatureScale = candidateTemperature;
        }

        // ── 8c. Class-conditional Platt (Buy / Sell separate scalers) ─────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, stumps, alphas, plattA, plattB, temperatureScale,
                                     DefaultConditionalRoutingThreshold);

        // ── 9. Isotonic calibration (PAVA) ────────────────────────────────────
        double[] isotonicBp = FitIsotonicCalibration(
            calSet, stumps, alphas, plattA, plattB, temperatureScale,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 9b. Average Kelly fraction on calibration diagnostics set ─────────
        double avgKellyFraction = ComputeAvgKellyFraction(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        // ── 10. ECE on held-out test set ──────────────────────────────────────
        double ece = ComputeEce(testSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                                plattABuy: plattABuy, plattBBuy: plattBBuy,
                                plattASell: plattASell, plattBSell: plattBSell,
                                routingThreshold: DefaultConditionalRoutingThreshold);

        // ── 11. EV-optimal threshold (tuned on SELECTION set to avoid cal/test leakage) ──
        double optimalThreshold = ComputeOptimalThreshold(
            selectionSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        _logger.LogInformation("EV-optimal threshold={Thr:F2} (default 0.50)", optimalThreshold);

        // ── 12. Magnitude linear regressor (Adam + Huber loss) ────────────────
        // Gated on torch availability because the regressor is torch-backed and would
        // otherwise fail the whole training run on containers where libtorch fails to
        // initialise. Without torch, the magnitude head is zero-weight — predictions
        // have no magnitude signal but direction still works.
        double[] magWeights;
        double   magBias;
        if (IsTorchCpuAvailable)
        {
            (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp, ct);
        }
        else
        {
            magWeights = new double[F];
            magBias    = 0.0;
        }

        // ── 12-pre. Quantile magnitude regressor ──────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= 10 && IsTorchCpuAvailable)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau, ct);
            _logger.LogDebug("Quantile regressor fitted (τ={Tau}).", hp.MagnitudeQuantileTau);
        }

        // ── 12b. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
        {
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}).",
                durbinWatson, hp.DurbinWatsonThreshold);
            double originalKelly = avgKellyFraction;
            double dwScaleFactor = Math.Clamp(durbinWatson / 2.0, 0.1, 1.0);
            avgKellyFraction    *= dwScaleFactor;
            _logger.LogDebug("Kelly fraction DW-adjusted: {Orig:F4} → {Adj:F4}", originalKelly, avgKellyFraction);
        }

        // ── 13. Selection-set permutation importance ──────────────────────────
        float[] selectionImportance = selectionSet.Count >= 10
            ? ComputePermutationImportance(
                selectionSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, F, optimalThreshold,
                trainingRandomSeed, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold)
            : new float[F];

        var topFeatures = selectionImportance
            .Select((imp, idx) => (
                Importance: imp,
                Name:       idx < snapshotFeatureNames.Length ? snapshotFeatureNames[idx] : $"F{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation("Top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 13b. Feature pruning re-train ─────────────────────────────────────
        var activeMask  = BuildFeatureMask(selectionImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(mask => !mask);

        if (prunedCount > 0 && F - prunedCount < 10)
        {
            prunedCount = 0;
            Array.Fill(activeMask, true);
        }

        if (prunedCount > 0)
        {
            var pruneResult = TrainPrunedModel(
                trainSet, calSet, testSet, activeMask, stumps, alphas,
                warmStumps, warmAlphas, replayWarmStartWeights, densityWeights,
                magWeights, magBias, plattA, plattB, plattABuy, plattBBuy, plattASell, plattBSell,
                temperatureScale, isotonicBp, hp, F, effectiveK, shrinkage, sammeR, treeDepth,
                optimalThreshold, prunedCount, ct);

            if (pruneResult is not null)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Was:P1}), {P} features removed.",
                    pruneResult.Value.Metrics.Accuracy, pruneResult.Value.BaseAccuracy, prunedCount);
                stumps       = pruneResult.Value.Stumps;
                alphas       = pruneResult.Value.Alphas;
                plattA       = pruneResult.Value.PlattA;
                plattB       = pruneResult.Value.PlattB;
                plattABuy    = pruneResult.Value.PlattABuy;    plattBBuy  = pruneResult.Value.PlattBBuy;
                plattASell   = pruneResult.Value.PlattASell;   plattBSell = pruneResult.Value.PlattBSell;
                trainSet     = pruneResult.Value.MaskedTrain;
                isotonicBp   = pruneResult.Value.IsotonicBp;
                calSet       = pruneResult.Value.MaskedCal;
                testSet      = pruneResult.Value.MaskedTest;
                selectionSet = ApplyMask(selectionSet, activeMask);
                magWeights   = pruneResult.Value.MagWeights;
                magBias      = pruneResult.Value.MagBias;
                magQ90Weights = pruneResult.Value.MagQ90Weights;
                magQ90Bias   = pruneResult.Value.MagQ90Bias;
                durbinWatson = pruneResult.Value.DurbinWatson;
                avgKellyFraction = pruneResult.Value.AvgKellyFraction;
                ece              = pruneResult.Value.Ece;
                optimalThreshold = pruneResult.Value.OptimalThreshold;
                temperatureScale = pruneResult.Value.TemperatureScale;
            }
            else
            {
                _logger.LogInformation("Pruned model rejected; keeping original.");
                prunedCount = 0;
                Array.Fill(activeMask, true);
            }
        }

        // ── 14. Split-conformal qHat ──────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat  = ComputeConformalQHat(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, conformalAlpha,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        double conformalQHatBuy = ComputeConformalQHatForLabel(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, conformalAlpha,
            label: 1, fallbackQHat: conformalQHat,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        double conformalQHatSell = ComputeConformalQHatForLabel(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, conformalAlpha,
            label: 0, fallbackQHat: conformalQHat,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        // ── 15. Final-model feature importance and PSI baselines ──────────────
        float[] featureImportance = calSet.Count >= 10
            ? ComputePermutationImportance(
                calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp, F, optimalThreshold,
                trainingRandomSeed, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold)
            : new float[F];
        double[] featureImportanceScores = featureImportance.Select(static v => (double)v).ToArray();
        int[] metaLabelTopFeatureIndices = featureImportanceScores
            .Select((importance, index) => (Index: index, Importance: importance))
            .OrderByDescending(entry => entry.Importance)
            .ThenBy(entry => entry.Index)
            .Take(Math.Min(5, F))
            .Select(entry => entry.Index)
            .ToArray();

        var trainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) trainFeatures.Add(s.Features);
        var featureQuantileBp = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(trainFeatures);

        // ── 16. Full evaluation on held-out test set ───────────────────────────
        var evalMetrics = EvaluateModel(
            testSet, stumps, alphas, magWeights, magBias, plattA, plattB, temperatureScale, isotonicBp,
            optimalThreshold, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        // ── 17. Brier Skill Score ─────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(
            testSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        _logger.LogInformation(
            "AdaBoostModelTrainer complete: K={K} stumps, accuracy={Acc:P1}, Brier={B:F4}, Sharpe={Sharpe:F2}",
            stumps.Count, evalMetrics.Accuracy, evalMetrics.BrierScore, evalMetrics.SharpeRatio);

        // ── 18a. Cross-fit adaptive heads (meta-label + abstention) ───────────
        double sumAlphaFinal = 0.0;
        foreach (var a in alphas) sumAlphaFinal += a;

        double[] metaLabelWeights;
        double   metaLabelBias;
        double[] abstentionWeights;
        double   abstentionBias;
        double   abstentionThreshold;
        // Downstream telemetry references crossFitUsed/FoldCount regardless of which
        // path produced the heads, so surface those as locals that default to the
        // non-crossfit fallback when torch is unavailable.
        bool crossFitUsed = false;
        int  crossFitFoldCount = 0;

        if (IsTorchCpuAvailable)
        {
            var crossFit = CrossFitAdaptiveHeads(
                calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                optimalThreshold, metaLabelTopFeatureIndices, F,
                plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold,
                minSamples: MinimumCalibrationSampleCount, ct: ct);

            crossFitUsed      = crossFitUsed;
            crossFitFoldCount = crossFitFoldCount;

            if (crossFitUsed)
            {
                metaLabelWeights    = crossFit.MetaLabelWeights;
                metaLabelBias       = crossFit.MetaLabelBias;
                abstentionWeights   = crossFit.AbstentionWeights;
                abstentionBias      = crossFit.AbstentionBias;
                abstentionThreshold = crossFit.AbstentionThreshold;
                _logger.LogDebug("Cross-fit adaptive heads: {Folds} folds.", crossFitFoldCount);
            }
            else
            {
                (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
                    calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                    optimalThreshold, metaLabelTopFeatureIndices, F,
                    plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold, ct);

                (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
                    calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                    metaLabelWeights, metaLabelBias, optimalThreshold, metaLabelTopFeatureIndices, F,
                    plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold, ct);
            }
        }
        else
        {
            // Torch native library not available — skip adaptive heads and fall back to
            // empty-weight models. AdaBoost will still train its core stumps via the
            // pure-C# path; only the torch-based meta-label / abstention heads are
            // bypassed. The snapshot audit accepts empty weights (length 0 bypasses the
            // shape check), so empty arrays are the correct "no-op head" signal here.
            _logger.LogWarning(
                "AdaBoost: TorchSharp CPU backend unavailable — skipping adaptive heads (meta-label + abstention).");
            metaLabelWeights    = [];
            metaLabelBias       = 0.0;
            abstentionWeights   = [];
            abstentionBias      = 0.0;
            abstentionThreshold = 0.5;
        }

        // ── 18b. Decision boundary stats ──────────────────────────────────────
        var (decisionBoundaryMean, decisionBoundaryStd) =
            ComputeDecisionBoundaryStats(calSet, stumps, alphas, sumAlphaFinal);

        // ── 18c. MI redundancy check ───────────────────────────────────────────
        int miTopN = hp.MutualInfoRedundancyTopN > 0 ? hp.MutualInfoRedundancyTopN : 10;
        var redundantFeaturePairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold, miTopN);

        // ── 18d. Jackknife+ residuals ─────────────────────────────────────────
        var jackknifeResiduals = stumps.Count >= 4 && testSet.Count >= 4
            ? ComputeJackknifeResiduals(testSet, stumps, alphas)
            : [];

        // ── 18e. New evaluation metrics ───────────────────────────────────────
        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(testSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        var (calibrationLoss, refinementLoss) =
            ComputeMurphyDecomposition(testSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        var (calResidualMean, calResidualStd, calResidualThreshold) =
            ComputeCalibrationResidualStats(calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
                plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        double predictionStability = ComputePredictionStabilityScore(
            testSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);

        double[] featureVariances = ComputeFeatureVariances(trainSet, F);

        // ── 18f. Metric summaries ─────────────────────────────────────────────
        var selectionMetrics = EvaluateModel(
            selectionSet, stumps, alphas, magWeights, magBias, plattA, plattB, temperatureScale, isotonicBp,
            optimalThreshold, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        double selectionEce = ComputeEce(
            selectionSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy: plattABuy, plattBBuy: plattBBuy, plattASell: plattASell, plattBSell: plattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);

        var calMetrics = EvaluateModel(
            calSet, stumps, alphas, magWeights, magBias, plattA, plattB, temperatureScale, isotonicBp,
            optimalThreshold, plattABuy, plattBBuy, plattASell, plattBSell, DefaultConditionalRoutingThreshold);
        double calEce = ComputeEce(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy: plattABuy, plattBBuy: plattBBuy, plattASell: plattASell, plattBSell: plattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);

        var selectionMetricSummary = CreateAdaBoostMetricSummary("SELECTION", selectionMetrics, selectionEce, optimalThreshold, selectionSet.Count);
        var calibrationMetricSummary = CreateAdaBoostMetricSummary("CALIBRATION", calMetrics, calEce, optimalThreshold, calSet.Count);
        var testMetricSummary = CreateAdaBoostMetricSummary("TEST", evalMetrics, ece, optimalThreshold, testSet.Count);

        int rawSelectionCount = Math.Max(0, selectionEnd - trainEnd);
        int rawCalibrationCount = Math.Max(0, calEnd - selectionEnd);
        var splitSummary = new TrainingSplitSummary
        {
            RawTrainCount = trainEnd,
            RawSelectionCount = rawSelectionCount,
            RawCalibrationCount = rawCalibrationCount,
            RawTestCount = Math.Max(0, samples.Count - calEnd),
            TrainStartIndex = 0,
            TrainCount = trainSet.Count,
            SelectionStartIndex = selectionStart,
            SelectionCount = selectionSet.Count,
            SelectionPruningStartIndex = selectionStart,
            SelectionPruningCount = selectionSet.Count,
            SelectionThresholdStartIndex = selectionStart,
            SelectionThresholdCount = selectionSet.Count,
            SelectionKellyStartIndex = calStart,
            SelectionKellyCount = calSet.Count,
            CalibrationStartIndex = calStart,
            CalibrationCount = calSet.Count,
            CalibrationFitStartIndex = calStart,
            CalibrationFitCount = calSet.Count,
            CalibrationDiagnosticsStartIndex = calStart,
            CalibrationDiagnosticsCount = calSet.Count,
            ConformalStartIndex = calStart,
            ConformalCount = calSet.Count,
            MetaLabelStartIndex = calStart,
            MetaLabelCount = crossFitUsed ? calSet.Count : metaLabelWeights.Length > 0 ? calSet.Count : 0,
            AbstentionStartIndex = calStart,
            AbstentionCount = crossFitUsed ? calSet.Count : abstentionWeights.Length > 0 ? calSet.Count : 0,
            AdaptiveHeadSplitMode = crossFitUsed ? "CROSSFIT" : "SHARED",
            AdaptiveHeadCrossFitFoldCount = crossFitFoldCount,
            TestStartIndex = testStart,
            TestCount = testSet.Count,
            EmbargoCount = embargo,
            TrainEmbargoDropped = trainEnd - trainLimit,
            SelectionEmbargoDropped = Math.Max(0, selectionStart - trainEnd),
            CalibrationEmbargoDropped = Math.Max(0, calStart - selectionEnd),
        };

        // ── 18g. Calibration artifact ─────────────────────────────────────────
        double globalPlattNll = ComputeCalibrationNll(calSet, stumps, alphas, plattA, plattB);
        double temperatureNll = hp.FitTemperatureScale
            ? ComputeCalibrationNll(calSet, stumps, alphas, plattA, plattB, temperatureScale)
            : globalPlattNll;
        double preIsotonicNll = ComputeCalibrationStackNll(
            calSet, stumps, alphas, plattA, plattB, temperatureScale,
            isotonicBp: null, plattABuy: plattABuy, plattBBuy: plattBBuy,
            plattASell: plattASell, plattBSell: plattBSell, routingThreshold: DefaultConditionalRoutingThreshold);
        double postIsotonicNll = ComputeCalibrationStackNll(
            calSet, stumps, alphas, plattA, plattB, temperatureScale,
            isotonicBp, plattABuy: plattABuy, plattBBuy: plattBBuy,
            plattASell: plattASell, plattBSell: plattBSell, routingThreshold: DefaultConditionalRoutingThreshold);
        var buyBranchStats = ComputeConditionalBranchStats(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, label: 1,
            plattABuy: plattABuy, plattBBuy: plattBBuy, plattASell: plattASell, plattBSell: plattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);
        var sellBranchStats = ComputeConditionalBranchStats(
            calSet, stumps, alphas, plattA, plattB, temperatureScale, label: 0,
            plattABuy: plattABuy, plattBBuy: plattBBuy, plattASell: plattASell, plattBSell: plattBSell,
            routingThreshold: DefaultConditionalRoutingThreshold);
        var calibrationArtifact = new AdaBoostCalibrationArtifact
        {
            SelectedGlobalCalibration = temperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
            CalibrationSelectionStrategy = "FIT_ON_CALIBRATION_EVAL_ON_SELECTION",
            GlobalPlattNll = globalPlattNll,
            TemperatureNll = temperatureNll,
            TemperatureSelected = temperatureScale > 0.0,
            FitSampleCount = calSet.Count,
            DiagnosticsSampleCount = calSet.Count,
            ThresholdSelectionSampleCount = selectionSet.Count,
            KellySelectionSampleCount = calSet.Count,
            DiagnosticsSelectedGlobalNll = temperatureScale > 0.0 ? temperatureNll : globalPlattNll,
            DiagnosticsSelectedStackNll = postIsotonicNll,
            ConformalSampleCount = calSet.Count,
            BuyConformalSampleCount = calSet.Count(sample => sample.Direction > 0),
            SellConformalSampleCount = calSet.Count(sample => sample.Direction <= 0),
            MetaLabelSampleCount = crossFitUsed ? calSet.Count : metaLabelWeights.Length > 0 ? calSet.Count : 0,
            AbstentionSampleCount = crossFitUsed ? calSet.Count : abstentionWeights.Length > 0 ? calSet.Count : 0,
            AdaptiveHeadMode = crossFitUsed ? "CROSSFIT" : "SHARED",
            AdaptiveHeadCrossFitFoldCount = crossFitFoldCount,
            ConditionalRoutingThreshold = DefaultConditionalRoutingThreshold,
            BuyBranchSampleCount = buyBranchStats.SampleCount,
            BuyBranchBaselineNll = buyBranchStats.BaselineNll,
            BuyBranchFittedNll = buyBranchStats.FittedNll,
            BuyBranchAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(plattABuy, plattBBuy),
            SellBranchSampleCount = sellBranchStats.SampleCount,
            SellBranchBaselineNll = sellBranchStats.BaselineNll,
            SellBranchFittedNll = sellBranchStats.FittedNll,
            SellBranchAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(plattASell, plattBSell),
            IsotonicSampleCount = calSet.Count,
            IsotonicBreakpointCount = isotonicBp.Length / 2,
            PreIsotonicNll = preIsotonicNll,
            PostIsotonicNll = postIsotonicNll,
            IsotonicAccepted = isotonicBp.Length >= 4 && postIsotonicNll <= preIsotonicNll + 1e-9,
        };

        // ── 19. Scalar sanitization ───────────────────────────────────────────
        ece                  = SafeDouble(ece, 1.0);
        optimalThreshold     = SafeDouble(optimalThreshold, 0.5);
        avgKellyFraction     = SafeDouble(avgKellyFraction);
        durbinWatson         = SafeDouble(durbinWatson, 2.0);
        temperatureScale     = SafeDouble(temperatureScale);
        brierSkillScore      = SafeDouble(brierSkillScore);
        calibrationLoss      = SafeDouble(calibrationLoss);
        refinementLoss       = SafeDouble(refinementLoss);
        predictionStability  = SafeDouble(predictionStability);
        calResidualMean      = SafeDouble(calResidualMean);
        calResidualStd       = SafeDouble(calResidualStd);
        calResidualThreshold = SafeDouble(calResidualThreshold);
        conformalQHat        = Math.Clamp(SafeDouble(conformalQHat, 0.5), 1e-7, 1.0 - 1e-7);
        conformalQHatBuy     = Math.Clamp(SafeDouble(conformalQHatBuy, conformalQHat), 1e-7, 1.0 - 1e-7);
        conformalQHatSell    = Math.Clamp(SafeDouble(conformalQHatSell, conformalQHat), 1e-7, 1.0 - 1e-7);

        // ── 20. Serialise model snapshot ──────────────────────────────────────
        var trainedAt = DateTime.UtcNow;

        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = snapshotFeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = stumps.Count,
            Weights                    = [alphas.ToArray()],
            Biases                     = [],
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            AvgKellyFraction           = avgKellyFraction,
            TemperatureScale           = temperatureScale,
            DurbinWatsonStatistic      = durbinWatson,
            Metrics                    = evalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = trainedAt,
            TrainedAtUtc               = trainedAt,
            FeatureImportance          = featureImportance,
            FeatureImportanceScores    = featureImportanceScores,
            FeatureVariances           = featureVariances,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            ReliabilityBinConfidence   = reliabilityBinConf.Length > 0 ? reliabilityBinConf : null,
            ReliabilityBinAccuracy     = reliabilityBinAcc.Length > 0  ? reliabilityBinAcc  : null,
            ReliabilityBinCounts       = reliabilityBinCounts.Length > 0 ? reliabilityBinCounts : null,
            CalibrationLoss            = calibrationLoss,
            RefinementLoss             = refinementLoss,
            PredictionStabilityScore   = predictionStability,
            IsotonicBreakpoints        = isotonicBp,
            ConformalQHat              = conformalQHat,
            ConformalQHatBuy           = conformalQHatBuy,
            ConformalQHatSell          = conformalQHatSell,
            ConformalCoverage          = hp.ConformalCoverage,
            FeatureQuantileBreakpoints = featureQuantileBp,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = generationNum,
            BrierSkillScore            = brierSkillScore,
            SanitizedLearnerCount      = sanitizedCount,
            FracDiffD                  = hp.FracDiffD,
            AgeDecayLambda             = hp.AgeDecayLambda,
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            MetaLabelTopFeatureIndices = metaLabelTopFeatureIndices,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = decisionBoundaryMean,
            DecisionBoundaryStd        = decisionBoundaryStd,
            RedundantFeaturePairs      = redundantFeaturePairs,
            JackknifeResiduals         = jackknifeResiduals,
            TrainSamplesAtLastCalibration = trainSet.Count,
            ConditionalCalibrationRoutingThreshold = DefaultConditionalRoutingThreshold,
            FeatureSchemaFingerprint   = featureSchemaFingerprint,
            PreprocessingFingerprint   = AdaBoostSnapshotSupport.ComputePreprocessingFingerprint(F, activeMask),
            TrainerFingerprint         = AdaBoostSnapshotSupport.ComputeTrainerFingerprint(hp, F, stumps.Count, treeDepth),
            TrainingRandomSeed         = trainingRandomSeed,
            TrainingSplitSummary       = splitSummary,
            AdaBoostSelectionMetrics   = selectionMetricSummary,
            AdaBoostCalibrationMetrics = calibrationMetricSummary,
            AdaBoostTestMetrics        = testMetricSummary,
            AdaBoostCalibrationArtifact = calibrationArtifact,
            AdaBoostDriftArtifact       = driftArtifact,
            AdaBoostWarmStartArtifact   = warmStartArtifact,
            AdaBoostCalibrationResidualMean      = calResidualMean,
            AdaBoostCalibrationResidualStd       = calResidualStd,
            AdaBoostCalibrationResidualThreshold = calResidualThreshold,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            GbmTreesJson               = JsonSerializer.Serialize(stumps, JsonOptions),
        };

        // ── 21. Snapshot array sanitization ───────────────────────────────────
        SanitizeSnapshotArrays(snapshot);

        // ── 22. Train/inference parity audit ──────────────────────────────────
        var auditResult = CreateAdaBoostAuditArtifact(snapshot, testSet.Count > 0 ? testSet : calSet);
        snapshot.AdaBoostAuditArtifact = auditResult.Artifact;
        if (auditResult.Findings.Length > 0)
        {
            throw new InvalidOperationException(
                $"AdaBoost train/inference audit failed: {string.Join("; ", auditResult.Findings)}");
        }

        var snapshotValidation = AdaBoostSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);
        if (!snapshotValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"AdaBoost snapshot validation failed after training: {string.Join("; ", snapshotValidation.Issues)}");
        }

        byte[] modelBytes = JsonSerializer.SerializeToUtf8Bytes(
            AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snapshot),
            JsonOptions);
        return new TrainingResult(evalMetrics, cvResult, modelBytes);
    }
}
