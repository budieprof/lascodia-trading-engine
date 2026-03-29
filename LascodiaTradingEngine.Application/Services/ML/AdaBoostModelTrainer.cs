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
/// Production-grade SAMME AdaBoost trainer (Rec #125).
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples (means/stds stored in snapshot for inference parity).</item>
///   <item>Walk-forward cross-validation (expanding window, embargo) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Final splits: 70 % train | 10 % Platt calibration | ~20 % held-out test (with embargo gaps).</item>
///   <item>Optional warm-start: loads existing stumps from a parent AdaBoost snapshot; adds only K/3 residual rounds.</item>
///   <item>Boosting weights initialised with exponential temporal-decay + class-balance correction.</item>
///   <item>Warm-start weight replay: parent stump updates replayed on new training set so new rounds focus on parent's failures.</item>
///   <item>Stump search uses an O(m log m) sorted prefix-sum sweep — faster than the naïve O(V×m) scan.</item>
///   <item>Early degenerate-stump detection: stops boosting when no split beats random chance.</item>
///   <item>NaN/Inf alpha sanitization before snapshot serialization.</item>
///   <item>Platt scaling (A, B) fitted via SGD on the frozen calibration fold.</item>
///   <item>Isotonic calibration (PAVA) applied post-Platt for monotone probability correction.</item>
///   <item>ECE (Expected Calibration Error) computed on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set (no test-set leakage).</item>
///   <item>Magnitude linear regressor trained with Adam + Huber loss + cosine-annealing LR + early stopping.</item>
///   <item>Permutation feature importance computed on the held-out test set (Fisher-Yates shuffle, fixed seed).</item>
///   <item>Split-conformal q̂ computed at the configured coverage level for prediction-set guarantees.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Brier Skill Score vs. naïve base-rate predictor.</item>
///   <item>Stationarity gate: lag-1 correlation ADF proxy warns when &gt;30 % of features appear non-stationary.</item>
///   <item>Class-imbalance warning when Buy/Sell split is outside 35/65.</item>
///   <item>Incremental update fast-path: fine-tunes on the most recent DensityRatioWindowDays of data when warm-starting.</item>
/// </list>
/// </para>
/// Alphas stored in <c>ModelSnapshot.Weights[0]</c>; stump trees in <c>ModelSnapshot.GbmTreesJson</c>.
/// Registered as a keyed <see cref="IMLModelTrainer"/> with key <c>"adaboost"</c>.
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.AdaBoost)]
public sealed class AdaBoostModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "AdaBoost";
    private const string ModelVersion = "2.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;
    private const double Eps         = 1e-10;

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

    // ── Core training logic (synchronous, runs on thread-pool via Task.Run) ──

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct,
        int                  recursionDepth = 0)
    {
        ct.ThrowIfCancellationRequested();

        int F = samples[0].Features.Length;
        int K = hp.K > 0 ? hp.K : 20;

        // ── 0. Incremental update fast-path ──────────────────────────────────
        // Guard against unbounded recursion: the fast-path disables UseIncrementalUpdate,
        // but a caller could theoretically re-enable it externally. Depth > 1 is never valid.
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

        // ── 2. Z-score standardisation — fit scaler on training portion only ──────────────────
        // Computing stats over ALL samples (including cal/test) is look-ahead bias: the scaler
        // would observe future feature distributions before they are seen in time.  We approximate
        // the training boundary (70 % of samples, without the embargo gap) to maximise the number
        // of stat-fitting samples while still excluding holdout data.
        int rawTrainLimit = Math.Max(1, (int)(samples.Count * 0.70));
        var trainRawFeatures = new List<float[]>(rawTrainLimit);
        for (int i = 0; i < rawTrainLimit; i++) trainRawFeatures.Add(samples[i].Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(trainRawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 3. Walk-forward cross-validation ─────────────────────────────────
        // Pass raw (unstandardised) samples so each fold can fit its own scaler,
        // eliminating the look-ahead bias caused by a global scaler trained on data
        // that overlaps future CV test folds.
        var (cvResult, cvEquityCurveGateFailed) = RunWalkForwardCV(samples, hp, F, K, ct);
        if (cvEquityCurveGateFailed)
        {
            _logger.LogWarning(
                "AdaBoost CV equity-curve/Sharpe-trend gate failed: model rejected.");
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        }
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2} sharpeTrend={Trend:F3}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe, cvResult.SharpeTrend);

        ct.ThrowIfCancellationRequested();

        // ── 4. Final splits: 70 % train | 10 % cal | ~20 % test ─────────────
        int trainEnd  = (int)(allStd.Count * 0.70);
        int calEnd    = (int)(allStd.Count * 0.80);
        int embargo   = hp.EmbargoBarCount;

        int trainLimit = Math.Max(0, trainEnd - embargo);
        int calStart   = Math.Min(trainEnd + embargo, calEnd);
        int testStart  = Math.Min(calEnd   + embargo, allStd.Count);

        var trainSet = allStd[..trainLimit];
        var calSet   = calStart < calEnd        ? allStd[calStart..calEnd] : [];
        var testSet  = testStart < allStd.Count ? allStd[testStart..]     : [];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 4a-extra. Adversarial validation (train vs test covariate shift) ──
        // Train a TorchSharp logistic classifier to distinguish trainSet features
        // from testSet features.  AUC > 0.65 signals meaningful covariate shift
        // that could cause the model to overestimate OOS performance.
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = ComputeAdversarialAuc(trainSet, testSet, F, _logger);
            _logger.LogInformation(
                "Adversarial validation AUC={AUC:F3} (0.50=no shift, >0.65=significant shift)",
                advAuc);
            if (advAuc > 0.65)
                _logger.LogWarning(
                    "Adversarial AUC={AUC:F3} indicates meaningful train/test covariate shift. " +
                    "Review feature engineering or extend the train window.",
                    advAuc);
        }

        // ── 4b. Stationarity gate (lag-1 correlation ADF proxy) ───────────────
        {
            int    nonStat  = CountNonStationaryFeatures(trainSet, F);
            double fraction = F > 0 ? (double)nonStat / F : 0;
            if (fraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "Stationarity gate: {N}/{T} features have unit root (|ρ₁| > 0.97). " +
                    "Consider enabling FracDiffD.", nonStat, F);
        }

        // ── 4c. Class-imbalance warning ────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning(
                    "AdaBoost class imbalance: Buy={Buy:P1}, Sell={Sell:P1}. " +
                    "Boosting weights will compensate, but severe imbalance may reduce stump diversity.",
                    buyRatio, 1.0 - buyRatio);
        }

        // ── 4d. Density-ratio importance weights ──────────────────────────────
        // Trains a logistic discriminator (recent vs historical) and produces p/(1-p)
        // importance weights blended into the initial boosting weight distribution.
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays);
            _logger.LogDebug(
                "Density-ratio weights computed (recentWindow≈{W}d of train).",
                hp.DensityRatioWindowDays);
        }

        // ── 4e. Covariate shift weights (novelty scoring from parent quantile breakpoints) ──
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, F);
            if (densityWeights is not null)
            {
                // Blend: product of both weight vectors, then renormalise to sum=1
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
            _logger.LogDebug(
                "Covariate shift weights applied from parent model (generation={Gen}).",
                warmStart.GenerationNumber);
        }

        // ── 5. Warm-start: load existing stumps from parent snapshot ──────────
        int effectiveK    = K;
        int generationNum = 1;
        var warmStumps    = new List<GbmTree>();
        var warmAlphas    = new List<double>();

        if (warmStart?.Type == ModelType &&
            warmStart.GbmTreesJson is { Length: > 0 } &&
            warmStart.Weights is { Length: > 0 })
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<GbmTree>>(warmStart.GbmTreesJson, JsonOptions);
                if (loaded is { Count: > 0 } && warmStart.Weights[0].Length > 0)
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "AdaBoost warm-start deserialization failed ({Msg}); starting cold.", ex.Message);
                warmStumps = [];
                warmAlphas = [];
            }
        }

        // ── 6. Fit AdaBoost stumps ────────────────────────────────────────────
        int m      = trainSet.Count;
        var labels = new int[m];
        for (int i = 0; i < m; i++) labels[i] = trainSet[i].Direction > 0 ? 1 : -1;

        // ── 6a. Per-sample adaptive label smoothing (soft margin in weight update) ──
        // ε_i = clip(1 − |Magnitude_i| / maxMagnitude, 0, 0.20):
        // high-magnitude bars → hard labels (ε_i ≈ 0); low-magnitude / ambiguous bars →
        // dampened labels so AdaBoost neither over-penalises ambiguous misses nor
        // over-discards ambiguous correct predictions.
        // softLabel_i = y_i · (1 − ε_i)  is used in place of y_i in the weight update:
        //   w_i ← w_i · exp(−α · softLabel_i · h(x_i))
        double adaptiveLabelSmoothing = 0.0; // mean ε stored in snapshot
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
            _logger.LogInformation(
                "Adaptive label smoothing (per-sample): avgε={Eps:F3}", adaptiveLabelSmoothing);
        }
        else
        {
            // No smoothing — soft labels equal hard labels
            for (int i = 0; i < m; i++) softLabels[i] = labels[i];
        }

        // Temporally-decayed + class-balanced initial weights, blended with density/covariate weights
        double[] boostWeights = InitialiseBoostWeights(trainSet, hp.TemporalDecayLambda, densityWeights);

        // For warm-start: replay parent weight updates so new rounds focus on parent's failures.
        // Skip when the parent model's DW statistic falls below the autocorrelation threshold —
        // low DW signals a regime change that makes the parent's failure pattern unreliable.
        bool replayWarmStartWeights = warmStumps.Count > 0 && warmAlphas.Count == warmStumps.Count;
        if (replayWarmStartWeights &&
            hp.DurbinWatsonThreshold > 0.0 &&
            warmStart?.DurbinWatsonStatistic > 0.0 &&
            warmStart.DurbinWatsonStatistic < hp.DurbinWatsonThreshold)
        {
            _logger.LogWarning(
                "Warm-start weight replay skipped: parent DW={DW:F3} < threshold {Thr:F2} " +
                "indicates regime change; new rounds will start from class-balanced weights.",
                warmStart.DurbinWatsonStatistic, hp.DurbinWatsonThreshold);
            replayWarmStartWeights = false;
        }
        if (replayWarmStartWeights)
            AdjustWarmStartWeights(boostWeights, labels, trainSet, warmStumps, warmAlphas);

        var stumps = new List<GbmTree>(warmStumps);
        var alphas = new List<double>(warmAlphas);

        // Pre-allocate sort buffers for O(m log m) stump search (avoid per-round allocation)
        var    sortKeys    = new double[m];
        var    sortIndices = new int[m];
        double shrinkage   = hp.AdaBoostAlphaShrinkage > 0.0 ? hp.AdaBoostAlphaShrinkage : 1.0;
        bool   sammeR      = hp.UseSammeR;
        int    treeDepth   = hp.AdaBoostMaxTreeDepth >= 2 ? 2 : 1;

        for (int round = 0; round < effectiveK && !ct.IsCancellationRequested; round++)
        {
            var (bestFi, bestThresh, bestParity, bestErr) =
                FindBestStump(trainSet, labels, boostWeights, F, sortKeys, sortIndices);

            // Degenerate-stump guard: stop when no split beats random chance
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
                // SAMME.R: leaf values are ½·logit(p_leaf); alpha = 1.0 (absorbed into leaf).
                // Weight update: w_i ← w_i · exp(−y_i · h^R(x_i)), using hard ±1 labels since
                // the exponential loss gradient already handles the continuous probability space.
                tree  = treeDepth == 2
                    ? BuildDepth2Tree(bestFi, bestThresh, trainSet, labels,
                                      boostWeights, F, sortKeys, sortIndices, true)
                    : BuildSammeRStump(bestFi, bestThresh, trainSet, labels, boostWeights, m);
                alpha = 1.0;
                alphas.Add(alpha);
                stumps.Add(tree);

                double wSum = 0;
                for (int i = 0; i < m; i++)
                {
                    double hR = PredictStump(tree, trainSet[i].Features);
                    boostWeights[i] *= Math.Exp(-labels[i] * hR);
                    wSum += boostWeights[i];
                }
                if (wSum > 0) for (int i = 0; i < m; i++) boostWeights[i] /= wSum;
            }
            else
            {
                // Discrete SAMME: alpha from weighted error; soft-label weight update.
                double err = Math.Max(Eps, Math.Min(1 - Eps, bestErr));
                alpha = shrinkage * 0.5 * Math.Log((1 - err) / err);
                tree  = treeDepth == 2
                    ? BuildDepth2Tree(bestFi, bestThresh, trainSet, labels,
                                      boostWeights, F, sortKeys, sortIndices, false)
                    : BuildStump(bestFi, bestThresh, bestParity);
                alphas.Add(alpha);
                stumps.Add(tree);

                // Soft labels (ỹ_i = y_i·(1−ε_i)) prevent over-penalising ambiguous samples.
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
        if (sanitizedCount > 0)
            _logger.LogWarning(
                "Post-training sanitization: {N}/{K} stumps had non-finite alphas.",
                sanitizedCount, stumps.Count);

        // ── 8. Platt scaling on calibration fold ──────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, stumps, alphas);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 8b. Class-conditional Platt (Buy / Sell separate scalers) ─────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, stumps, alphas);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 8c. Average Kelly fraction on cal set ─────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, stumps, alphas, plattA, plattB);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 9. Isotonic calibration (PAVA) ────────────────────────────────────
        double[] isotonicBp = FitIsotonicCalibration(calSet, stumps, alphas, plattA, plattB,
                                                     plattABuy, plattBBuy, plattASell, plattBSell);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 10. ECE on held-out test set ──────────────────────────────────────
        double ece = ComputeEce(testSet, stumps, alphas, plattA, plattB, isotonicBp,
                                plattABuy: plattABuy, plattBBuy: plattBBuy,
                                plattASell: plattASell, plattBSell: plattBSell);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);

        // ── 11. EV-optimal threshold (tuned on cal set to avoid test-set leakage) ──
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, stumps, alphas, plattA, plattB, isotonicBp,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            plattABuy, plattBBuy, plattASell, plattBSell);
        _logger.LogInformation("EV-optimal threshold={Thr:F2} (default 0.50)", optimalThreshold);

        // ── 11b. Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calSet, stumps, alphas);
            _logger.LogDebug("Temperature scaling: T={T:F4} (1.0=no correction)", temperatureScale);
        }

        // ── 12. Magnitude linear regressor (Adam + Huber loss) ────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp);

        // ── 12b. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        _logger.LogDebug(
            "Durbin-Watson={DW:F4} (2=no autocorr, <1.5=positive autocorr)", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}). " +
                "Consider enabling AR feature injection in the next training cycle.",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 12c. Kelly fraction serial-correlation adjustment ─────────────────
        // Standard Kelly assumes i.i.d. returns. When DW < threshold the magnitude
        // residuals exhibit positive autocorrelation, meaning effective variance is
        // understated and the raw Kelly fraction overstates safe position size.
        // Scale factor DW/2 ∈ (0,1]: approaches 1 (no adjustment) as DW→2, shrinks
        // Kelly toward 0 as autocorrelation increases. Clamped to [0.1, 1.0].
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
        {
            double originalKelly   = avgKellyFraction;
            double dwScaleFactor   = Math.Clamp(durbinWatson / 2.0, 0.1, 1.0);
            avgKellyFraction      *= dwScaleFactor;
            _logger.LogDebug(
                "Kelly fraction DW-adjusted: {Orig:F4} → {Adj:F4} (DW={DW:F3}, scale={S:F3})",
                originalKelly, avgKellyFraction, durbinWatson, dwScaleFactor);
        }

        // ── 13. Permutation feature importance on test set ────────────────────
        float[] featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, stumps, alphas, plattA, plattB, isotonicBp, F,
                                           plattABuy, plattBBuy, plattASell, plattBSell)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (
                Importance: imp,
                Name:       idx < MLFeatureHelper.FeatureNames.Length
                            ? MLFeatureHelper.FeatureNames[idx]
                            : $"F{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation("Top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 13b. Feature pruning re-train ─────────────────────────────────────
        var activeMask  = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            _logger.LogInformation(
                "Feature pruning: masking {Pruned}/{Total} low-importance features; re-training.",
                prunedCount, F);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            // ── Warm-start pruned retrain from filtered existing stumps ──────────
            // Instead of training from scratch (effectiveK rounds), we initialise the
            // pruned ensemble from the current model's stumps whose root-split feature
            // is still active, then add only K/3 residual rounds.  This halves typical
            // training time for the pruning pass while allowing new rounds to correct
            // the few residual errors the pruned feature set cannot address.
            var filteredPStumps = new List<GbmTree>(stumps.Count);
            var filteredPAlphas = new List<double>(stumps.Count);
            for (int i = 0; i < Math.Min(stumps.Count, alphas.Count); i++)
            {
                int sf = stumps[i].Nodes?[0].SplitFeature ?? -1;
                if (sf >= 0 && sf < activeMask.Length && activeMask[sf])
                {
                    filteredPStumps.Add(stumps[i]);
                    filteredPAlphas.Add(alphas[i]);
                }
            }
            int pResidualRounds = filteredPStumps.Count > 0
                ? Math.Max(5, effectiveK / 3)
                : effectiveK;   // cold start if no stumps survived the mask

            int      pM      = maskedTrain.Count;
            var      pLabels = new int[pM];
            for (int i = 0; i < pM; i++) pLabels[i] = maskedTrain[i].Direction > 0 ? 1 : -1;

            // Compute soft labels from maskedTrain directly — avoids the silent index-alignment
            // assumption that ApplyMask preserves sample order/count without subsampling.
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

            // densityWeights is indexed over trainSet samples. ApplyMask preserves sample count
            // and order (it only zeroes feature values), so maskedTrain.Count == trainSet.Count
            // and the index alignment is guaranteed. Guard against future changes that might
            // subsample: if lengths diverge, pass null so blending is skipped rather than silently
            // partially applied.
            double[]? pDensityWeights = densityWeights is null || densityWeights.Length >= maskedTrain.Count
                ? densityWeights
                : null;
            if (densityWeights is not null && densityWeights.Length < maskedTrain.Count)
                _logger.LogWarning(
                    "AdaBoost pruned retrain: densityWeights length ({DW}) < maskedTrain count ({MT}); " +
                    "density blending skipped to avoid partial index misalignment.",
                    densityWeights.Length, maskedTrain.Count);

            double[] pWeights = InitialiseBoostWeights(maskedTrain, hp.TemporalDecayLambda, pDensityWeights);

            // Replay warm-start weight updates on the masked feature set so new rounds
            // focus on the parent's failures (mirrors the full-feature warm-start path).
            // Only replay stumps whose root split feature is retained in the active mask so
            // degenerate warm-start stumps on zeroed features don't corrupt weights.
            if (replayWarmStartWeights)
            {
                var filteredStumps = new List<GbmTree>(warmStumps.Count);
                var filteredAlphas = new List<double>(warmStumps.Count);
                foreach (var (ws, wa) in warmStumps.Zip(warmAlphas))
                {
                    int sf = ws.Nodes?[0].SplitFeature ?? -1;
                    if (sf >= 0 && sf < activeMask.Length && activeMask[sf])
                    { filteredStumps.Add(ws); filteredAlphas.Add(wa); }
                }
                if (filteredStumps.Count > 0)
                    AdjustWarmStartWeights(pWeights, pLabels, maskedTrain, filteredStumps, filteredAlphas);
            }

            // Initialise from filtered parent stumps; new rounds extend from this base
            var pStumps = new List<GbmTree>(filteredPStumps);
            var pAlphas = new List<double>(filteredPAlphas);
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
                        ? BuildDepth2Tree(bFi, bThresh, maskedTrain, pLabels,
                                          pWeights, F, pSortK, pSortIdx, true, activeMask)
                        : BuildSammeRStump(bFi, bThresh, maskedTrain, pLabels, pWeights, pM);
                    pAlpha = 1.0;
                    pAlphas.Add(pAlpha);
                    pStumps.Add(pTree);
                    double wSum = 0;
                    for (int i = 0; i < pM; i++)
                    {
                        double hR = PredictStump(pTree, maskedTrain[i].Features);
                        pWeights[i] *= Math.Exp(-pLabels[i] * hR);
                        wSum += pWeights[i];
                    }
                    if (wSum > 0) for (int i = 0; i < pM; i++) pWeights[i] /= wSum;
                }
                else
                {
                    double cErr = Math.Max(Eps, Math.Min(1 - Eps, bErr));
                    pAlpha = shrinkage * 0.5 * Math.Log((1 - cErr) / cErr);
                    pTree  = treeDepth == 2
                        ? BuildDepth2Tree(bFi, bThresh, maskedTrain, pLabels,
                                          pWeights, F, pSortK, pSortIdx, false, activeMask)
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
            var (pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell) =
                FitClassConditionalPlatt(maskedCal, pStumps, pAlphas);
            double[] pIsotonicBp    = FitIsotonicCalibration(maskedCal, pStumps, pAlphas, pPlattA, pPlattB,
                                                              pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell);
            var pMetrics            = EvaluateModel(maskedTest, pStumps, pAlphas,
                                                    magWeights, magBias, pPlattA, pPlattB, pIsotonicBp,
                                                    pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell);

            // Accept pruned model only if accuracy doesn't degrade by more than 1 %
            var baseMetrics = EvaluateModel(testSet, stumps, alphas,
                                            magWeights, magBias, plattA, plattB, isotonicBp,
                                            plattABuy, plattBBuy, plattASell, plattBSell);
            if (pMetrics.Accuracy >= baseMetrics.Accuracy - 0.01)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Was:P1}), {P} features removed.",
                    pMetrics.Accuracy, baseMetrics.Accuracy, prunedCount);
                stumps     = pStumps;
                alphas     = pAlphas;
                plattA     = pPlattA;
                plattB     = pPlattB;
                plattABuy  = pPlattABuy;   plattBBuy  = pPlattBBuy;
                plattASell = pPlattASell;   plattBSell = pPlattBSell;
                isotonicBp = pIsotonicBp;
                calSet     = maskedCal;    // downstream conformalQHat/evalMetrics use masked features
                testSet    = maskedTest;   // downstream brierSkillScore uses masked features
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, pStumps, pAlphas, pPlattA, pPlattB);
                ece = ComputeEce(maskedTest, pStumps, pAlphas, pPlattA, pPlattB, pIsotonicBp,
                                  plattABuy: pPlattABuy, plattBBuy: pPlattBBuy,
                                  plattASell: pPlattASell, plattBSell: pPlattBSell);
                optimalThreshold = ComputeOptimalThreshold(
                    maskedCal, pStumps, pAlphas, pPlattA, pPlattB, pIsotonicBp,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax,
                    pPlattABuy, pPlattBBuy, pPlattASell, pPlattBSell);
                if (hp.FitTemperatureScale && maskedCal.Count >= 10)
                    temperatureScale = FitTemperatureScaling(maskedCal, pStumps, pAlphas);
                // conformalQHat and brierSkillScore are recomputed below at their declaration sites
                // using the now-updated stumps/alphas/platt variables.
            }
            else
            {
                _logger.LogInformation(
                    "Pruned model rejected (acc={Acc:P1} < {Was:P1} − 1 %); keeping original.",
                    pMetrics.Accuracy, baseMetrics.Accuracy);
                prunedCount = 0;
                Array.Fill(activeMask, true);
            }
        }

        // ── 14. Split-conformal qHat ──────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat  = ComputeConformalQHat(
            calSet, stumps, alphas, plattA, plattB, isotonicBp, conformalAlpha,
            plattABuy, plattBBuy, plattASell, plattBSell);
        _logger.LogInformation(
            "Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 15. Feature quantile breakpoints for PSI drift monitoring ──────────
        var trainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) trainFeatures.Add(s.Features);
        var featureQuantileBp = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(trainFeatures);

        // ── 16. Full evaluation on held-out test set ───────────────────────────
        var evalMetrics = EvaluateModel(
            testSet, stumps, alphas, magWeights, magBias, plattA, plattB, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 17. Brier Skill Score ─────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(
            testSet, stumps, alphas, plattA, plattB, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell);
        _logger.LogInformation(
            "Brier Skill Score (BSS)={BSS:F4} (>0 beats naive predictor)", brierSkillScore);

        _logger.LogInformation(
            "AdaBoostModelTrainer complete: K={K} stumps, accuracy={Acc:P1}, Brier={B:F4}, Sharpe={Sharpe:F2}",
            stumps.Count, evalMetrics.Accuracy, evalMetrics.BrierScore, evalMetrics.SharpeRatio);

        // ── 18a. Cal-set permutation importance ───────────────────────────────
        var calPermImportance = calSet.Count >= 20
            ? ComputeCalPermutationImportance(calSet, stumps, alphas, F)
            : new double[F];
        _logger.LogDebug("Cal-set permutation importance: {N} features scored.", calPermImportance.Length);

        // ── 18b. Meta-label model (correctness predictor on cal set) ──────────
        double sumAlphaFinal = 0.0;
        foreach (var a in alphas) sumAlphaFinal += a;
        var (metaLabelWeights, metaLabelBias) =
            FitMetaLabelModel(calSet, stumps, alphas, sumAlphaFinal, F);
        _logger.LogDebug("Meta-label model fitted ({Dim} meta-features).", metaLabelWeights.Length);

        // ── 18c. Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) =
            FitAbstentionModel(calSet, stumps, alphas, plattA, plattB,
                               metaLabelWeights, metaLabelBias, sumAlphaFinal, F);
        _logger.LogDebug("Abstention gate fitted (threshold={Thr:F2}).", abstentionThreshold);

        // ── 18d. Quantile magnitude regressor (pinball loss, τ = MagnitudeQuantileTau) ──
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= 10)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau);
            _logger.LogDebug("Quantile regressor fitted (τ={Tau}).", hp.MagnitudeQuantileTau);
        }

        // ── 18e. Decision boundary stats ──────────────────────────────────────
        var (decisionBoundaryMean, decisionBoundaryStd) =
            ComputeDecisionBoundaryStats(calSet, stumps, alphas, sumAlphaFinal);
        _logger.LogDebug(
            "Decision boundary: mean={Mean:F4}, std={Std:F4}", decisionBoundaryMean, decisionBoundaryStd);

        // ── 18f. MI redundancy check ───────────────────────────────────────────
        var redundantFeaturePairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
        if (redundantFeaturePairs.Length > 0)
        {
            _logger.LogWarning(
                "MI redundancy: {N} high-MI feature pairs detected (showing first 5): {Pairs}",
                redundantFeaturePairs.Length,
                redundantFeaturePairs.Length <= 5
                    ? string.Join(", ", redundantFeaturePairs)
                    : string.Join(", ", redundantFeaturePairs[..5]) + ", …");
        }

        // ── 18g. Jackknife+ residuals (half-ensemble LOO proxy) ───────────────
        // Computed on the held-out test set so residuals reflect OOS uncertainty
        // rather than in-sample fit, which makes them meaningful as inference bounds.
        var jackknifeResiduals = stumps.Count >= 4 && testSet.Count >= 4
            ? ComputeJackknifeResiduals(testSet, stumps, alphas)
            : [];
        _logger.LogDebug("Jackknife residuals: {N} computed.", jackknifeResiduals.Length);

        // ── 19. Serialise model snapshot ──────────────────────────────────────
        // activeMask and prunedCount are set by step 13b (feature pruning); if pruning was
        // not attempted (no low-importance features) activeMask defaults to all-true there.
        var trainedAt = DateTime.UtcNow;

        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = stumps.Count,
            Weights                    = [alphas.ToArray()],   // Weights[0] = alpha vector
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
            FeatureImportanceScores    = calPermImportance,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            IsotonicBreakpoints        = isotonicBp,
            ConformalQHat              = conformalQHat,
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
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = decisionBoundaryMean,
            DecisionBoundaryStd        = decisionBoundaryStd,
            RedundantFeaturePairs      = redundantFeaturePairs,
            JackknifeResiduals         = jackknifeResiduals,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            GbmTreesJson               = JsonSerializer.Serialize(stumps, JsonOptions),
        };

        byte[] modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return new TrainingResult(evalMetrics, cvResult, modelBytes);
    }

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        int                  K,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds > 0 ? hp.WalkForwardFolds : 3;
        int embargo = hp.EmbargoBarCount;
        int cvK     = Math.Max(5, K / 2); // fewer rounds per fold for speed

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning(
                "AdaBoost CV: fold size too small ({Size} < 50), skipping CV.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // Purged CV: lookback-window embargo prevents feature-lookback leakage
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug(
                    "AdaBoost CV fold {Fold} skipped (trainEnd={N} < minSamples)", fold, trainEnd);
                continue;
            }

            var rawFoldTrain = samples[..trainEnd].ToList();

            // Time-series purging: remove trailing train samples whose label horizon
            // overlaps the test fold start (PurgeHorizonBars bars ahead)
            if (hp.PurgeHorizonBars > 0 && rawFoldTrain.Count > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < rawFoldTrain.Count)
                {
                    int purgeCount = rawFoldTrain.Count - purgeFrom;
                    rawFoldTrain = rawFoldTrain[..purgeFrom];
                    if (purgeCount > 0)
                        _logger.LogDebug(
                            "Purging: removed {N} train samples overlapping test fold.", purgeCount);
                }
            }

            var rawFoldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (rawFoldTest.Count < 20 || rawFoldTrain.Count < hp.MinSamples) continue;

            // ── Per-fold Z-score standardisation (no look-ahead) ──────────────
            // Fit scaler only on the fold's purged training window, then apply it
            // to both train and test slices.  This removes the subtle bias that
            // occurs when a global scaler trained on the full 70 % boundary leaks
            // the distribution of folds that appear later in the expanding window.
            var foldRawFeats = new List<float[]>(rawFoldTrain.Count);
            foreach (var s in rawFoldTrain) foldRawFeats.Add(s.Features);
            var (foldMeans, foldStds) = MLFeatureHelper.ComputeStandardization(foldRawFeats);

            var foldTrain = new List<TrainingSample>(rawFoldTrain.Count);
            foreach (var s in rawFoldTrain)
                foldTrain.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, foldMeans, foldStds) });

            var foldTest = new List<TrainingSample>(rawFoldTest.Count);
            foreach (var s in rawFoldTest)
                foldTest.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, foldMeans, foldStds) });

            int      foldM       = foldTrain.Count;
            var      foldLb      = new int[foldM];
            for (int i = 0; i < foldM; i++) foldLb[i] = foldTrain[i].Direction > 0 ? 1 : -1;

            // Mirror the main training loop: use soft labels in SAMME weight updates so
            // CV Sharpe/EV metrics reflect the same algorithm as the final model.
            var foldSoftLabels = new double[foldM];
            if (hp.UseAdaptiveLabelSmoothing)
            {
                double foldMaxMag = 0.0;
                foreach (var s in foldTrain) { double mag = Math.Abs((double)s.Magnitude); if (mag > foldMaxMag) foldMaxMag = mag; }
                for (int i = 0; i < foldM; i++)
                {
                    double eps_i = foldMaxMag > 1e-9
                        ? Math.Clamp(1.0 - Math.Abs((double)foldTrain[i].Magnitude) / foldMaxMag, 0.0, 0.20)
                        : 0.0;
                    foldSoftLabels[i] = foldLb[i] * (1.0 - eps_i);
                }
            }
            else
            {
                for (int i = 0; i < foldM; i++) foldSoftLabels[i] = foldLb[i];
            }

            double[] foldW       = InitialiseBoostWeights(foldTrain, hp.TemporalDecayLambda);
            var      foldStumps  = new List<GbmTree>(cvK);
            var      foldAlphas  = new List<double>(cvK);
            var      foldKeys    = new double[foldM];
            var      foldIndices = new int[foldM];

            double cvShrinkage = hp.AdaBoostAlphaShrinkage > 0.0 ? hp.AdaBoostAlphaShrinkage : 1.0;
            bool   cvSammeR    = hp.UseSammeR;
            int    cvDepth     = hp.AdaBoostMaxTreeDepth >= 2 ? 2 : 1;

            for (int r = 0; r < cvK && !ct.IsCancellationRequested; r++)
            {
                var (fi, thresh, parity, err) =
                    FindBestStump(foldTrain, foldLb, foldW, F, foldKeys, foldIndices);

                if (!double.IsFinite(err) || err >= 0.5 - Eps) break;

                GbmTree cvTree;
                double  alpha;

                if (cvSammeR)
                {
                    cvTree = cvDepth == 2
                        ? BuildDepth2Tree(fi, thresh, foldTrain, foldLb, foldW, F, foldKeys, foldIndices, true)
                        : BuildSammeRStump(fi, thresh, foldTrain, foldLb, foldW, foldM);
                    alpha = 1.0;
                    foldAlphas.Add(alpha);
                    foldStumps.Add(cvTree);
                    double wSum = 0;
                    for (int i = 0; i < foldM; i++)
                    {
                        double hR = PredictStump(cvTree, foldTrain[i].Features);
                        foldW[i] *= Math.Exp(-foldLb[i] * hR);
                        wSum += foldW[i];
                    }
                    if (wSum > 0) for (int i = 0; i < foldM; i++) foldW[i] /= wSum;
                }
                else
                {
                    double cErr = Math.Max(Eps, Math.Min(1 - Eps, err));
                    alpha  = cvShrinkage * 0.5 * Math.Log((1 - cErr) / cErr);
                    cvTree = cvDepth == 2
                        ? BuildDepth2Tree(fi, thresh, foldTrain, foldLb, foldW, F, foldKeys, foldIndices, false)
                        : BuildStump(fi, thresh, parity);
                    foldAlphas.Add(alpha);
                    foldStumps.Add(cvTree);
                    double wSum = 0;
                    for (int i = 0; i < foldM; i++)
                    {
                        double pred = PredictStump(cvTree, foldTrain[i].Features);
                        foldW[i] *= Math.Exp(-alpha * foldSoftLabels[i] * pred);
                        wSum += foldW[i];
                    }
                    if (wSum > 0) for (int i = 0; i < foldM; i++) foldW[i] /= wSum;
                }
            }

            // Fold feature importance: alpha-weighted stump selection frequency
            var foldImp  = new double[F];
            double sumAl = 0;
            foreach (var a in foldAlphas) sumAl += a;
            if (sumAl > 0)
            {
                for (int k = 0; k < foldStumps.Count && k < foldAlphas.Count; k++)
                {
                    var root = foldStumps[k].Nodes?[0];
                    if (root is { IsLeaf: false, SplitFeature: >= 0 } && root.SplitFeature < F)
                        foldImp[root.SplitFeature] += foldAlphas[k] / sumAl;
                }
            }
            foldImportances.Add(foldImp);

            // Evaluate fold (no Platt for speed)
            int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
            double brierSum = 0, evSum = 0;
            var    foldPredictions = new (int Predicted, int Actual)[foldTest.Count];

            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                var    s     = foldTest[pi];
                double score = PredictScore(s.Features, foldStumps, foldAlphas);
                double p     = MLFeatureHelper.Sigmoid(2 * score);
                int    yHat  = score >= 0 ? 1 : 0;
                int    y     = s.Direction > 0 ? 1 : 0;
                if (yHat == y) correct++;
                if (yHat == 1 && y == 1) tp++;
                if (yHat == 1 && y == 0) fp++;
                if (yHat == 0 && y == 1) fn++;
                if (yHat == 0 && y == 0) tn++;
                brierSum += (p - y) * (p - y);
                evSum    += (yHat == y ? 1 : -1) * (double)s.Magnitude;
                foldPredictions[pi] = (score >= 0 ? 1 : -1, s.Direction > 0 ? 1 : -1);
            }

            int    nFold = foldTest.Count;
            double acc   = (double)correct / nFold;
            double prec  = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
            double rec   = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
            double f1    = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
            double brier = brierSum / nFold;
            double ev    = evSum / nFold;

            // ── Equity-curve gate — proper return-series Sharpe ───────────────
            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions);
            double sharpe = foldCurveSharpe;

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;
            if (isBadFold) badFolds++;

            accList.Add(acc);
            f1List.Add(f1);
            evList.Add(ev);
            sharpeList.Add(sharpe);

            _logger.LogDebug(
                "AdaBoost CV fold {Fold}: acc={Acc:P1}, f1={F1:F3}, ev={EV:F4}, sharpe={Sharpe:F2}, maxDD={DD:P2}, bad={Bad}",
                fold, acc, f1, ev, sharpe, foldMaxDD, isBadFold);
        }

        if (accList.Count == 0) return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // ── Equity-curve gate: bad-fold fraction threshold ─────────────────────
        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{Total} folds failed (maxDD or Sharpe).",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // ── Sharpe trend gate ─────────────────────────────────────────────────
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model flagged.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // ── Cross-fold variance gate ──────────────────────────────────────────
        if (hp.MaxWalkForwardStdDev > 0.0 && stdAcc > hp.MaxWalkForwardStdDev)
            _logger.LogWarning(
                "CV high variance: stdAcc={Std:P1} > threshold {Max:P1}.",
                stdAcc, hp.MaxWalkForwardStdDev);

        // ── Feature stability: CV = σ/μ of alpha-weighted selection freq ──────
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < F; j++)
            {
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
            AvgAccuracy:            avgAcc,
            StdAccuracy:            stdAcc,
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ── Boosting weight initialisation ────────────────────────────────────────

    /// <summary>
    /// Initialises per-sample boosting weights with exponential temporal-decay (most recent samples
    /// receive the highest base weight) and class-balance correction (Buy and Sell classes each
    /// receive equal total initial weight = 0.5 before temporal rescaling).
    /// Weights are normalised to sum to 1.
    /// </summary>
    private static double[] InitialiseBoostWeights(
        List<TrainingSample> train,
        double               temporalDecayLambda,
        double[]?            blendWeights = null)
    {
        int n = train.Count;

        int posCount = 0;
        foreach (var s in train) if (s.Direction > 0) posCount++;
        int negCount = n - posCount;

        // Equal total weight per class; uniform within each class
        double posBase = (posCount > 0 && negCount > 0) ? 0.5 / posCount : 1.0 / n;
        double negBase = (posCount > 0 && negCount > 0) ? 0.5 / negCount : 1.0 / n;

        double lambda  = temporalDecayLambda > 0 ? temporalDecayLambda : 0.0;
        double wSum    = 0;
        var    weights = new double[n];

        for (int i = 0; i < n; i++)
        {
            double w = train[i].Direction > 0 ? posBase : negBase;
            if (lambda > 0)
            {
                // t in [0,1]: 0 = oldest, 1 = most recent → recency boost
                double t = (double)i / Math.Max(1, n - 1);
                w *= Math.Exp(lambda * t);
            }
            // Blend in density-ratio / covariate-shift weights (normalised to mean=1 externally)
            if (blendWeights is { Length: > 0 } && i < blendWeights.Length)
                w *= blendWeights[i];
            weights[i] = w;
            wSum += w;
        }

        if (wSum > 0)
            for (int i = 0; i < n; i++) weights[i] /= wSum;
        else
            Array.Fill(weights, 1.0 / n);

        return weights;
    }

    // ── Warm-start weight adjustment ──────────────────────────────────────────

    /// <summary>
    /// Replays the boosting weight updates of the parent ensemble on the current training
    /// set, focusing new residual rounds on the parent's failures.
    /// Supports both SAMME (leaf values ±1, alpha from error) and SAMME.R (leaf values
    /// ½·logit(p), alpha=1.0) via the unified formula:
    ///   w_i ← w_i · exp(−α · y_i · h_k(x_i))
    /// where <c>h_k(x_i)</c> is obtained by the generalized <see cref="PredictStump"/>
    /// traversal — correct for depth-1 stumps and depth-2 trees alike.
    /// Weights are re-normalised after each tree to maintain the AdaBoost sum=1 invariant.
    /// </summary>
    private static void AdjustWarmStartWeights(
        double[]             weights,
        int[]                labels,
        List<TrainingSample> train,
        List<GbmTree>        warmStumps,
        List<double>         warmAlphas)
    {
        int n = train.Count;
        for (int k = 0; k < warmStumps.Count && k < warmAlphas.Count; k++)
        {
            double alpha = warmAlphas[k];
            if (!double.IsFinite(alpha)) continue;

            var stump = warmStumps[k];
            if (stump.Nodes is not { Count: >= 3 }) continue;

            double wSum = 0;
            for (int i = 0; i < n; i++)
            {
                // PredictStump handles depth-1 and depth-2 trees, SAMME (±1) and SAMME.R
                // (½·logit) leaf values. Returns 0 for feature-index out-of-bounds → weight
                // unchanged (exp(0)=1), which is the safest no-op for mismatched features.
                double leafVal = PredictStump(stump, train[i].Features);
                weights[i] *= Math.Exp(-alpha * labels[i] * leafVal);
                wSum += weights[i];
            }
            if (wSum > 0)
                for (int i = 0; i < n; i++) weights[i] /= wSum;
        }
    }

    // ── Best stump search (O(m log m) sorted prefix-sum sweep) ───────────────

    /// <summary>
    /// Finds the weighted-error-minimising decision stump across all F features.
    /// Uses a sorted prefix-sum sweep: O(m log m) per feature vs the naïve O(V×m) scan.
    /// <paramref name="sortKeys"/> and <paramref name="sortIndices"/> are caller-owned reusable
    /// buffers of length ≥ m that are overwritten each call; pre-allocating them outside the
    /// boosting loop eliminates repeated heap allocations.
    /// </summary>
    private static (int Fi, double Thresh, int Parity, double Err) FindBestStump(
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  F,
        double[]             sortKeys,
        int[]                sortIndices,
        bool[]?              activeMask = null)
    {
        int    m          = train.Count;
        double bestErr    = double.MaxValue;
        int    bestFi     = 0;
        double bestThresh = 0;
        int    bestParity = 1;

        for (int fi = 0; fi < F; fi++)
        {
            if (activeMask is not null && fi < activeMask.Length && !activeMask[fi]) continue;
            // Fill sort buffers for this feature
            for (int i = 0; i < m; i++)
            {
                sortKeys[i]    = train[i].Features[fi];
                sortIndices[i] = i;
            }
            // Co-sort keys and indices so indices track original positions
            Array.Sort(sortKeys, sortIndices, 0, m);

            // Compute total positive and negative weight sums
            double totalPos = 0, totalNeg = 0;
            for (int i = 0; i < m; i++)
            {
                int oi = sortIndices[i];
                if (labels[oi] > 0) totalPos += weights[oi];
                else                totalNeg += weights[oi];
            }

            // Sweep thresholds via cumulative prefix sums
            double cumPosLeft = 0, cumNegLeft = 0;
            for (int ti = 0; ti < m - 1; ti++)
            {
                int oi = sortIndices[ti];
                if (labels[oi] > 0) cumPosLeft += weights[oi];
                else                cumNegLeft += weights[oi];

                // Threshold only between adjacent distinct feature values
                if (sortKeys[ti + 1] <= sortKeys[ti] + 1e-12) continue;

                double thresh = (sortKeys[ti] + sortKeys[ti + 1]) * 0.5;

                // Parity +1: predict +1 when x ≤ thresh, −1 when x > thresh
                // err1 = Σ w[i∈neg-left]  + Σ w[i∈pos-right]
                double err1 = cumNegLeft + (totalPos - cumPosLeft);
                double err2 = 1.0 - err1; // parity −1 has exactly complementary error

                if (err1 < bestErr) { bestErr = err1; bestFi = fi; bestThresh = thresh; bestParity =  1; }
                if (err2 < bestErr) { bestErr = err2; bestFi = fi; bestThresh = thresh; bestParity = -1; }
            }
        }

        return (bestFi, bestThresh, bestParity, bestErr);
    }

    // ── Stump construction ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a depth-1 decision tree (stump).
    /// For discrete SAMME <paramref name="leftLeafValue"/> / <paramref name="rightLeafValue"/>
    /// are omitted and the leaves get ±1 values derived from <paramref name="parity"/>.
    /// For SAMME.R supply pre-computed half-logit leaf contributions
    /// (½·logit(p_leaf)) so the existing <see cref="PredictStump"/> path works unchanged.
    /// </summary>
    private static GbmTree BuildStump(
        int    featureIndex,
        double threshold,
        int    parity,
        double leftLeafValue  = double.NaN,
        double rightLeafValue = double.NaN)
    {
        double lv = double.IsNaN(leftLeafValue)  ? (parity > 0 ?  1.0 : -1.0) : leftLeafValue;
        double rv = double.IsNaN(rightLeafValue) ? (parity > 0 ? -1.0 :  1.0) : rightLeafValue;
        return new GbmTree
        {
            Nodes =
            [
                new GbmNode
                {
                    IsLeaf         = false,
                    SplitFeature   = featureIndex,
                    SplitThreshold = threshold,
                    LeftChild      = 1,
                    RightChild     = 2,
                    LeafValue      = 0,
                },
                new GbmNode { IsLeaf = true, LeafValue = lv },
                new GbmNode { IsLeaf = true, LeafValue = rv },
            ]
        };
    }

    // ── Prediction helpers ─────────────────────────────────────────────────────

    private static double PredictScore(float[] features, List<GbmTree> stumps, List<double> alphas)
    {
        double score = 0;
        int    count = Math.Min(stumps.Count, alphas.Count);
        for (int k = 0; k < count; k++)
            score += alphas[k] * PredictStump(stumps[k], features);
        return score;
    }

    /// <summary>
    /// Traverses a base-learner tree of arbitrary depth and returns the leaf value.
    /// For SAMME stumps the leaf value is ±1; for SAMME.R it is ½·logit(p_leaf).
    /// Handles depth-1 (stump) and depth-2 trees transparently.
    /// Returns 0 on any structural anomaly (null nodes, out-of-bounds feature index).
    /// </summary>
    private static double PredictStump(GbmTree stump, float[] features)
    {
        if (stump.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        while (true)
        {
            var node = stump.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < 0 || node.SplitFeature >= features.Length) return 0;
            bool goLeft  = features[node.SplitFeature] <= node.SplitThreshold;
            int  nextIdx = goLeft ? node.LeftChild : node.RightChild;
            if (nextIdx < 0 || nextIdx >= stump.Nodes.Count) return 0;
            nodeIdx = nextIdx;
        }
    }

    /// <summary>
    /// Full AdaBoost probability pipeline:
    /// raw margin → sigmoid(2·score) → logit → Platt(A,B) → optional isotonic correction.
    /// When class-conditional Platt params are supplied (non-NaN), the Buy scaler is used
    /// for raw predictions ≥ 0.5 and the Sell scaler for predictions &lt; 0.5.
    /// </summary>
    private static double PredictProb(
        float[]       features,
        List<GbmTree> stumps,
        List<double>  alphas,
        double        plattA,
        double        plattB,
        double[]?     isotonicBp = null,
        double        plattABuy  = double.NaN,
        double        plattBBuy  = double.NaN,
        double        plattASell = double.NaN,
        double        plattBSell = double.NaN)
    {
        double score = PredictScore(features, stumps, alphas);
        double raw   = MLFeatureHelper.Sigmoid(2 * score);
        raw          = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
        double logit = MLFeatureHelper.Logit(raw);

        double useA, useB;
        if (!double.IsNaN(plattABuy) && !double.IsNaN(plattASell))
        {
            useA = raw >= 0.5 ? plattABuy  : plattASell;
            useB = raw >= 0.5 ? plattBBuy  : plattBSell;
        }
        else
        {
            useA = plattA;
            useB = plattB;
        }

        double calibP = MLFeatureHelper.Sigmoid(useA * logit + useB);
        if (isotonicBp is { Length: >= 4 })
            calibP = ApplyIsotonicCalibration(calibP, isotonicBp);
        return calibP;
    }

    // ── Platt scaling ─────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];

        for (int i = 0; i < n; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double raw   = MLFeatureHelper.Sigmoid(2 * score);
            raw          = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]    = MLFeatureHelper.Logit(raw);
            labels[i]    = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr      = 0.01;
        const int    epochs  = 200;
        const double tol     = 1e-6;
        int          noImpro = 0;
        double       prevLoss = double.MaxValue;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0, loss = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA   += err * logits[i];
                dB   += err;
                loss -= labels[i] * Math.Log(calibP + Eps) + (1 - labels[i]) * Math.Log(1 - calibP + Eps);
            }
            plattA -= lr * dA / n;
            plattB -= lr * dB / n;

            double curLoss = loss / n;
            if (prevLoss - curLoss < tol) { if (++noImpro >= 5) break; }
            else                          noImpro = 0;
            prevLoss = curLoss;
        }

        return (plattA, plattB);
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (calSet.Count < 30) return [];

        int cn    = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double raw   = MLFeatureHelper.Sigmoid(2 * score);
            raw          = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(raw);
            double useA  = (!double.IsNaN(plattABuy) && raw >= 0.5) ? plattABuy
                         : (!double.IsNaN(plattASell) && raw < 0.5)  ? plattASell
                         : plattA;
            double useB  = (!double.IsNaN(plattABuy) && raw >= 0.5) ? plattBBuy
                         : (!double.IsNaN(plattASell) && raw < 0.5)  ? plattBSell
                         : plattB;
            double p     = MLFeatureHelper.Sigmoid(useA * logit + useB);
            pairs[i]     = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Stack-based Pool Adjacent Violators Algorithm (PAVA)
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var (lastSumY, lastSumP, lastCount) = stack[^1];
                var (prevSumY, prevSumP, prevCount) = stack[^2];
                if (prevSumY / prevCount > lastSumY / lastCount)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prevSumY + lastSumY,
                                 prevSumP + lastSumP,
                                 prevCount + lastCount);
                }
                else break;
            }
        }

        // Merge pools with fewer than MinBlockSize samples into their smaller neighbour
        // to prevent overfitting on tiny PAVA segments.
        const int MinBlockSize = 5;
        bool merged = true;
        while (merged && stack.Count > 2)
        {
            merged = false;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i].Count >= MinBlockSize) continue;
                int neighbour = (i == 0) ? 1
                              : (i == stack.Count - 1) ? i - 1
                              : (stack[i - 1].Count <= stack[i + 1].Count ? i - 1 : i + 1);
                int lo = Math.Min(i, neighbour);
                int hi = Math.Max(i, neighbour);
                var (lSumY, lSumP, lCount) = stack[lo];
                var (hSumY, hSumP, hCount) = stack[hi];
                stack[lo] = (lSumY + hSumY, lSumP + hSumP, lCount + hCount);
                stack.RemoveAt(hi);
                merged = true;
                break;
            }
        }

        // Interleaved [x₀,y₀,x₁,y₁,...] breakpoints — one per PAVA block
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    private static double ApplyIsotonicCalibration(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        int nPoints = bp.Length / 2;
        if (p <= bp[0])                  return bp[1];
        if (p >= bp[(nPoints - 1) * 2])  return bp[(nPoints - 1) * 2 + 1];

        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (bp[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }
        double x0 = bp[lo * 2],       y0 = bp[lo * 2 + 1];
        double x1 = bp[(lo + 1) * 2], y1 = bp[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15
            ? (y0 + y1) * 0.5
            : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    // ── ECE computation (10 equal-width bins) ─────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  bins       = 10,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (testSet.Count < bins) return 1.0;

        var binAcc  = new double[bins];
        var binConf = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, stumps, alphas, plattA, plattB, isotonicBp,
                                      plattABuy, plattBBuy, plattASell, plattBSell);
            int    binI = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[binI] += p;
            if (s.Direction > 0) binAcc[binI]++; // positive-class frequency, not classification accuracy
            binCnt[binI]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            double acc  = binAcc[b]  / binCnt[b];
            double conf = binConf[b] / binCnt[b];
            ece += (double)binCnt[b] / n * Math.Abs(acc - conf);
        }
        return ece;
    }

    // ── EV-optimal decision threshold sweep ───────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  searchMin  = 30,
        int                  searchMax  = 75,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (calSet.Count < 30) return 0.5;

        var probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = PredictProb(calSet[i].Features, stumps, alphas, plattA, plattB, isotonicBp,
                                   plattABuy, plattBBuy, plattASell, plattBSell);

        double bestEv        = double.MinValue;
        double bestThreshold = 0.5;

        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (calSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * (double)calSet[i].Magnitude;
            }
            ev /= calSet.Count;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }
        return bestThreshold;
    }

    // ── Magnitude linear regressor (TorchSharp: Adam + Huber + weight_decay + cosine LR) ──

    /// <summary>
    /// Fits a linear magnitude regressor using TorchSharp's vectorised Adam optimizer.
    /// Huber loss (δ=1) is computed as a tensor expression for full batch-level
    /// parallelism.  L2 regularisation is applied via Adam's <c>weight_decay</c>
    /// argument rather than manually adding it to the gradient, which is the
    /// numerically stable formulation used by PyTorch and TorchSharp.
    /// Cosine-annealed LR is approximated by scaling the loss (avoids resetting
    /// moment buffers that would occur if the optimizer were re-created each epoch).
    /// </summary>
    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  F,
        TrainingHyperparams  hp)
    {
        if (train.Count == 0) return (new double[F], 0.0);

        int    n          = train.Count;
        bool   canEs      = n >= 30;
        int    valSize    = canEs ? Math.Max(5, n / 10) : 0;
        int    trainN     = n - valSize;
        if (trainN == 0) return (new double[F], 0.0);

        double baseLr     = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2         = hp.L2Lambda;
        int    epochs     = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);
        int    batchSz    = Math.Min(256, trainN);

        // Build flat arrays once; mini-batches slice into them
        var xArr = new float[trainN * F];
        var yArr = new float[trainN];
        for (int i = 0; i < trainN; i++)
        {
            Array.Copy(train[i].Features, 0, xArr, i * F, F);
            yArr[i] = train[i].Magnitude;
        }

        float[]? vxArr = null; float[]? vyArr = null;
        if (canEs)
        {
            vxArr = new float[valSize * F];
            vyArr = new float[valSize];
            for (int i = 0; i < valSize; i++)
            {
                Array.Copy(train[trainN + i].Features, 0, vxArr, i * F, F);
                vyArr[i] = train[trainN + i].Magnitude;
            }
        }

        // TorchSharp parameters: weight [F,1] and bias [1]
        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: baseLr, weight_decay: l2);

        float[]? bestW       = null;
        float    bestB       = 0f;
        double   bestValLoss = double.MaxValue;
        int      noImprove   = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            // Cosine LR scaling: approximate schedule by scaling loss (preserves moments)
            double cosScale = 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            for (int start = 0; start < trainN; start += batchSz)
            {
                int end = Math.Min(start + batchSz, trainN);
                int bsz = end - start;

                var xB = new float[bsz * F]; var yB = new float[bsz];
                Array.Copy(xArr, start * F, xB, 0, bsz * F);
                Array.Copy(yArr, start,     yB, 0, bsz);

                opt.zero_grad();
                using var xT    = torch.tensor(xB, device: CPU).reshape(bsz, F);
                using var yT    = torch.tensor(yB, device: CPU).reshape(bsz, 1);
                using var pred  = torch.mm(xT, wP) + bP;
                using var err   = pred - yT;
                using var abse  = err.abs();
                using var huber = torch.where(abse <= 1.0f,
                                              err.pow(2f) * 0.5f,
                                              abse - 0.5f).mean();
                // Apply cosine scaling to approximate LR annealing
                (cosScale < 1.0 ? huber * (float)cosScale : huber).backward();
                opt.step();
            }

            if (!canEs) continue;

            using (no_grad())
            {
                using var vxT   = torch.tensor(vxArr!, device: CPU).reshape(valSize, F);
                using var vyT   = torch.tensor(vyArr!, device: CPU).reshape(valSize, 1);
                using var vpred = torch.mm(vxT, wP) + bP;
                using var verr  = vpred - vyT;
                using var vabse = verr.abs();
                using var vloss = torch.where(vabse <= 1.0f,
                                              verr.pow(2f) * 0.5f,
                                              vabse - 0.5f).mean();
                double vl = vloss.item<float>();
                if (vl < bestValLoss)
                {
                    bestValLoss = vl;
                    bestW       = wP.cpu().data<float>().ToArray();
                    bestB       = bP.cpu().data<float>()[0];
                    noImprove   = 0;
                }
                else if (++noImprove >= esPatience) break;
            }
        }

        float[] finalW = bestW ?? wP.cpu().data<float>().ToArray();
        float   finalB = bestW is null ? bP.cpu().data<float>()[0] : bestB;

        var wOut = new double[F];
        for (int j = 0; j < F; j++) wOut[j] = finalW[j];
        return (wOut, finalB);
    }

    // ── Permutation feature importance (Fisher-Yates shuffle, fixed seed) ─────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  F,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        int n = testSet.Count;

        // Baseline accuracy with original features
        int baseCorrect = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, stumps, alphas, plattA, plattB, isotonicBp,
                                   plattABuy, plattBBuy, plattASell, plattBSell);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / n;

        var importance = new float[F];
        var shuffled   = new float[n];     // column buffer
        var featBuf    = new float[F];     // per-sample mutable feature vector
        var rng        = new Random(42);   // fixed seed for reproducibility

        for (int fi = 0; fi < F; fi++)
        {
            // Copy and Fisher-Yates shuffle the fi-th feature column
            for (int i = 0; i < n; i++) shuffled[i] = testSet[i].Features[fi];
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int correct = 0;
            for (int i = 0; i < n; i++)
            {
                var orig = testSet[i].Features;
                int fLen = Math.Min(orig.Length, F);
                for (int j = 0; j < fLen; j++) featBuf[j] = orig[j];
                featBuf[fi] = shuffled[i];
                double p = PredictProb(featBuf, stumps, alphas, plattA, plattB, isotonicBp,
                                       plattABuy, plattBBuy, plattASell, plattBSell);
                if ((p >= 0.5 ? 1 : 0) == (testSet[i].Direction > 0 ? 1 : 0)) correct++;
            }

            double shuffledAcc = (double)correct / n;
            importance[fi] = (float)Math.Max(0.0, baseAcc - shuffledAcc);
        }

        // Normalise to sum to 1
        double total = 0;
        foreach (var v in importance) total += v;
        if (total > 0)
            for (int i = 0; i < importance.Length; i++) importance[i] = (float)(importance[i] / total);

        return importance;
    }

    // ── Split-conformal qHat ──────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        double               alpha      = 0.10,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double p = PredictProb(s.Features, stumps, alphas, plattA, plattB, isotonicBp,
                                   plattABuy, plattBBuy, plattASell, plattBSell);
            scores.Add(s.Direction > 0 ? 1.0 - p : p); // nonconformity score
        }
        scores.Sort();

        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ── Full evaluation on held-out test set ──────────────────────────────────

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, double.MaxValue, 0, 1, 0, 0, 0, 0, 0, 0);

        int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, retSumSq = 0;

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, stumps, alphas, plattA, plattB, isotonicBp,
                                      plattABuy, plattBBuy, plattASell, plattBSell);
            int    yHat = p >= 0.5 ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);
            double ret = (yHat == y ? 1 : -1) * (double)s.Magnitude;
            evSum    += ret;
            retSumSq += ret * ret;

            double magPred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                magPred += magWeights[j] * s.Features[j];
            double magErr = magPred - s.Magnitude;
            magSse += magErr * magErr;
        }

        int    n         = testSet.Count;
        double accuracy  = (double)correct / n;
        double brier     = brierSum / n;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = evSum / n;
        double magRmse   = Math.Sqrt(magSse / n);

        // Proper equity-curve Sharpe: mean(r) / std(r) over magnitude-weighted returns
        double retMean = ev;
        double retVar  = retSumSq / n - retMean * retMean;
        double retStd  = retVar > 1e-15 ? Math.Sqrt(retVar) : 0.0;
        double sharpe  = retStd < 1e-10 ? 0.0 : retMean / retStd;

        return new EvalMetrics(
            Accuracy:         accuracy,
            Precision:        precision,
            Recall:           recall,
            F1:               f1,
            MagnitudeRmse:    magRmse,
            ExpectedValue:    ev,
            BrierScore:       brier,
            WeightedAccuracy: accuracy,
            SharpeRatio:      sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ── Brier Skill Score vs. naïve base-rate predictor ───────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        double               plattABuy  = double.NaN,
        double               plattBBuy  = double.NaN,
        double               plattASell = double.NaN,
        double               plattBSell = double.NaN)
    {
        if (testSet.Count == 0) return 0;

        int posCount = 0;
        foreach (var s in testSet) if (s.Direction > 0) posCount++;
        double pBase = (double)posCount / testSet.Count;

        double brierModel = 0, brierNaive = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, stumps, alphas, plattA, plattB, isotonicBp,
                                   plattABuy, plattBBuy, plattASell, plattBSell);
            int    y = s.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
            brierNaive += (pBase - y) * (pBase - y);
        }
        brierModel /= testSet.Count;
        brierNaive /= testSet.Count;
        return brierNaive > 1e-15 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ── Class-conditional Platt scaling (Buy / Sell separate scalers) ─────────

    /// <summary>
    /// Fits separate Platt scalers for Buy (raw prob ≥ 0.5) and Sell (raw prob &lt; 0.5) subsets
    /// of the calibration set to correct directional calibration bias.
    /// Returns (ABuy, BBuy, ASell, BSell); returns (1,0,1,0) when a class subset has &lt; 5 samples.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample> calSet,
            List<GbmTree>        stumps,
            List<double>         alphas)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double score = PredictScore(s.Features, stumps, alphas);
            double rawP  = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(rawP);
            double y     = s.Direction > 0 ? 1.0 : 0.0;
            if (rawP >= 0.5) buySamples.Add((logit, y));
            else             sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (1.0, 0.0);
            double a = 1.0, b = 0.0;
            double prevL   = double.MaxValue;
            int    noImpro = 0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0, loss = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA   += err * logit;
                    dB   += err;
                    loss -= y * Math.Log(calibP + 1e-10) + (1 - y) * Math.Log(1 - calibP + 1e-10);
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;

                double curL = loss / n;
                if (prevL - curL < 1e-6) { if (++noImpro >= 5) break; }
                else                     noImpro = 0;
                prevL = curL;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ── Average Kelly fraction ─────────────────────────────────────────────────

    /// <summary>
    /// Computes the half-Kelly fraction averaged over the calibration set:
    ///   mean( max(0, 2·calibP − 1) ) × 0.5
    /// where calibP uses the already-fitted global Platt (A, B).
    /// Returns 0.0 if the calibration set is empty.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (var s in calSet)
        {
            double score  = PredictScore(s.Features, stumps, alphas);
            double rawP   = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return sum / calSet.Count * 0.5;
    }

    // ── Temperature scaling ────────────────────────────────────────────────────

    /// <summary>
    /// Fits a single temperature scalar T on the calibration set via grid search over
    /// [0.1, 3.0] in 30 steps, selecting T that minimises binary cross-entropy.
    /// calibP = σ(logit(rawP) / T). Returns 1.0 (no-op) when the cal set is too small.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas)
    {
        if (calSet.Count < 10) return 1.0;

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double rawP  = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
            logits[i]    = MLFeatureHelper.Logit(rawP);
            labels[i]    = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double bestT    = 1.0;
        double bestLoss = double.MaxValue;

        for (int step = 0; step <= 30; step++)
        {
            double T    = 0.1 + step * (3.0 - 0.1) / 30.0;
            double loss = 0.0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(logits[i] / T);
                double y      = labels[i];
                loss += -(y * Math.Log(calibP + Eps) + (1 - y) * Math.Log(1 - calibP + Eps));
            }
            if (loss / n < bestLoss) { bestLoss = loss / n; bestT = T; }
        }
        return bestT;
    }

    // ── Durbin-Watson autocorrelation test ─────────────────────────────────────

    /// <summary>
    /// Computes the Durbin-Watson statistic on magnitude regressor residuals over the
    /// training set. DW = Σ(e_t − e_{t-1})² / Σe_t².
    /// DW ≈ 2 → no autocorrelation; DW &lt; 1.5 → positive autocorrelation.
    /// Returns 2.0 when the training set is too small to compute reliably.
    /// </summary>
    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  F)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < F && j < magWeights.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumSqDiff = 0.0, sumSqRes = 0.0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double diff = residuals[i] - residuals[i - 1];
            sumSqDiff  += diff * diff;
        }
        foreach (double e in residuals) sumSqRes += e * e;

        return sumSqRes < 1e-15 ? 2.0 : sumSqDiff / sumSqRes;
    }

    // ── Density-ratio covariate reweighting (TorchSharp logistic, Adam + weight_decay) ──

    /// <summary>
    /// Trains a TorchSharp logistic discriminator to distinguish "recent" samples
    /// (label=1) from "historical" samples (label=0).  Returns importance weights
    /// w_i = p_i/(1−p_i) normalised to sum=1, blended into initial boosting weights
    /// to focus boosting on the current distribution.
    /// L2 regularisation is applied via Adam's <c>weight_decay</c> argument, which
    /// is more numerically stable than the per-sample gradient addition used by the
    /// previous hand-rolled SGD.  Batch training also enables vectorised operations.
    /// </summary>
    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  F,
        int                  recentWindowDays)
    {
        int n = trainSet.Count;
        if (n < 50) { var u = new double[n]; Array.Fill(u, 1.0 / n); return u; }

        // Treat last min(n/5, recentWindowDays×barsPerDay) samples as "recent"
        int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * 24));
        recentCount     = Math.Min(recentCount, n - 10);
        int histCount   = n - recentCount;

        var xArr = new float[n * F];
        var yArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(trainSet[i].Features, 0, xArr, i * F, F);
            yArr[i] = i >= histCount ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        // weight_decay provides L2 regularisation; avoids the gradient-addition
        // instability of the previous per-sample approach.
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.01);

        for (int epoch = 0; epoch < 40; epoch++)
        {
            opt.zero_grad();
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, F);
            using var yT    = torch.tensor(yArr, device: CPU).reshape(n, 1);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yT);
            loss.backward();
            opt.step();
        }

        // Extract probabilities and compute importance ratios
        float[] scoreArr;
        using (no_grad())
        {
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, F);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit).squeeze(1);
            scoreArr = prob.cpu().data<float>().ToArray();
        }

        var    weights = new double[n];
        double sum     = 0.0;
        for (int i = 0; i < n; i++)
        {
            double p     = scoreArr[i];
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i]   = ratio;
            sum          += ratio;
        }
        if (sum > 0) for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ── Covariate shift weights ────────────────────────────────────────────────

    /// <summary>
    /// Computes per-sample novelty scores using the parent model's feature quantile
    /// breakpoints. Each sample's weight = 1 + fraction_of_features_outside_[q10,q90].
    /// Normalised to mean = 1.0 so the effective gradient scale is unchanged.
    /// </summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> samples,
        double[][]           parentQuantileBreakpoints,
        int                  F)
    {
        int n       = samples.Count;
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            float[] feat         = samples[i].Features;
            int     outsideCount = 0;
            int     checkedCount = 0;
            for (int j = 0; j < F; j++)
            {
                if (j >= parentQuantileBreakpoints.Length) continue;
                var bp = parentQuantileBreakpoints[j];
                if (bp.Length < 2) continue;
                double q10 = bp[0];
                double q90 = bp[^1];
                if (feat[j] < q10 || feat[j] > q90) outsideCount++;
                checkedCount++;
            }
            weights[i] = 1.0 + (checkedCount > 0 ? (double)outsideCount / checkedCount : 0.0);
        }

        double mean = 0;
        foreach (var w in weights) mean += w;
        mean /= n;
        if (mean > 1e-10) for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    // ── Feature pruning helpers ────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        var mask = new bool[F];
        if (threshold <= 0.0 || F == 0) { Array.Fill(mask, true); return mask; }
        double minImp = threshold / F;
        for (int j = 0; j < F; j++) mask[j] = importance[j] >= minImp;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            result.Add(s with { Features = f });
        }
        return result;
    }

    // ── Stationarity gate (lag-1 Pearson correlation as ADF proxy) ────────────

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int nonStat = 0;
        int n       = samples.Count;
        if (n < 3) return 0;

        for (int fi = 0; fi < F; fi++)
        {
            // |ρ₁| > 0.97 is a conservative proxy for a unit root (I(1) process)
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            int    nc   = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = samples[i].Features[fi];
                double y = samples[i + 1].Features[fi];
                sumX  += x; sumY  += y;
                sumXY += x * y;
                sumX2 += x * x; sumY2 += y * y;
            }
            double varX  = sumX2 - sumX * sumX / nc;
            double varY  = sumY2 - sumY * sumY / nc;
            double denom = Math.Sqrt(Math.Max(0, varX * varY));
            double rho   = denom > 1e-12 ? (sumXY - sumX * sumY / nc) / denom : 0;
            if (Math.Abs(rho) > 0.97) nonStat++;
        }
        return nonStat;
    }

    // ── Standard deviation helper ─────────────────────────────────────────────

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sumSq = 0;
        foreach (double v in values) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    // ── Equity-curve statistics (max drawdown + Sharpe on fold predictions) ───

    /// <summary>
    /// Computes the maximum drawdown and Sharpe ratio of the simulated equity curve
    /// from an array of (Predicted, Actual) binary outcomes.
    /// Each correct prediction contributes +1, each error −1 to the equity series.
    /// Returns (MaxDrawdown, Sharpe); both 0.0 for empty input.
    /// </summary>
    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0.0, 0.0);

        var    returns = new double[predictions.Length];
        double equity  = 0.0;
        double peak    = 0.0;
        double maxDD   = 0.0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == predictions[i].Actual ? +1.0 : -1.0;
            returns[i] = ret;
            equity    += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0.0;
            if (dd > maxDD) maxDD = dd;
        }

        double mean = 0.0;
        foreach (double r in returns) mean += r;
        mean /= returns.Length;

        double variance = 0.0;
        foreach (double r in returns) { double d = r - mean; variance += d * d; }
        double std    = returns.Length > 1 ? Math.Sqrt(variance / (returns.Length - 1)) : 0.0;
        double sharpe = std < 1e-10 ? 0.0 : mean / std;

        return (maxDD, sharpe);
    }

    // ── Walk-forward Sharpe trend (OLS slope through per-fold Sharpe series) ──

    /// <summary>
    /// Fits a least-squares linear regression slope through the per-fold Sharpe series.
    /// Returns 0.0 when fewer than 3 folds are available.
    /// A negative slope indicates degrading out-of-sample performance over time.
    /// </summary>
    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        int n = sharpePerFold.Count;
        if (n < 3) return 0.0;

        // OLS: slope = (n·Σxy − Σx·Σy) / (n·Σx² − (Σx)²)
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            double x  = i;
            double y  = sharpePerFold[i];
            sumX  += x;
            sumY  += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) < 1e-12 ? 0.0 : (n * sumXY - sumX * sumY) / denom;
    }

    // ── Cal-set permutation importance ────────────────────────────────────────

    /// <summary>
    /// Computes permutation feature importance on the calibration set using the raw AdaBoost
    /// score (no Platt) for speed. Each feature is Fisher-Yates shuffled with a per-feature
    /// fixed seed; drop in accuracy relative to baseline = importance.
    /// Returns importance normalised to sum to 1; length F.
    /// </summary>
    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        int                  F)
    {
        if (calSet.Count < 10 || F == 0) return new double[F];

        int m = calSet.Count;

        // Baseline accuracy (no Platt — consistency with fast CV evaluation)
        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double score = PredictScore(s.Features, stumps, alphas);
            if ((score >= 0 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / m;

        var importance = new double[F];
        var scratch    = new float[calSet[0].Features.Length];

        for (int j = 0; j < F; j++)
        {
            // Clone column j and Fisher-Yates shuffle (per-feature seed for reproducibility)
            var vals = new float[m];
            for (int i = 0; i < m; i++) vals[i] = calSet[i].Features[j];
            var localRng = new Random(j * 17 + 99);
            for (int i = vals.Length - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            int shuffledCorrect = 0;
            for (int idx = 0; idx < m; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double score = PredictScore(scratch, stumps, alphas);
                if ((score >= 0 ? 1 : 0) == (calSet[idx].Direction > 0 ? 1 : 0))
                    shuffledCorrect++;
            }
            importance[j] = Math.Max(0.0, baseAcc - (double)shuffledCorrect / m);
        }

        // Normalise to sum to 1
        double total = 0;
        foreach (double v in importance) total += v;
        if (total > 1e-10)
            for (int j = 0; j < F; j++) importance[j] /= total;

        return importance;
    }

    // ── Meta-label model (TorchSharp logistic correctness predictor) ─────────

    /// <summary>
    /// Trains a 7-feature TorchSharp logistic meta-classifier on the calibration set.
    /// Meta-features: [rawP, scoreNorm, feat[0..4]] where rawP = σ(2·score) and
    /// scoreNorm = |score|/sumAlpha is the normalised ensemble confidence margin.
    /// Label = 1 if the AdaBoost prediction was correct for that sample.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns (weights[7], bias).
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               sumAlpha,
        int                  F)
    {
        const int MetaDim = 7;   // rawP + scoreNorm + feat[0..4]
        if (calSet.Count < 10) return (new double[MetaDim], 0.0);

        int    n         = calSet.Count;
        double alphaNorm = Math.Max(sumAlpha, 1e-6);
        int    rawTop    = Math.Min(5, F);

        var xArr = new float[n * MetaDim];
        var yArr = new float[n];

        for (int i = 0; i < n; i++)
        {
            var    s         = calSet[i];
            double score     = PredictScore(s.Features, stumps, alphas);
            double rawP      = MLFeatureHelper.Sigmoid(2 * score);
            double scoreNorm = Math.Abs(score) / alphaNorm;

            xArr[i * MetaDim + 0] = (float)rawP;
            xArr[i * MetaDim + 1] = (float)scoreNorm;
            for (int j = 0; j < rawTop; j++)
                xArr[i * MetaDim + 2 + j] = s.Features[j];

            int predicted = score >= 0 ? 1 : -1;
            int actual    = s.Direction > 0 ? 1 : -1;
            yArr[i] = predicted == actual ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(MetaDim, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.001);

        for (int epoch = 0; epoch < 40; epoch++)
        {
            opt.zero_grad();
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, MetaDim);
            using var yT    = torch.tensor(yArr, device: CPU).reshape(n, 1);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yT);
            loss.backward();
            opt.step();
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var mw = new double[MetaDim];
        for (int i = 0; i < MetaDim; i++) mw[i] = finalW[i];
        return (mw, finalB);
    }

    // ── Abstention gate (TorchSharp logistic selective predictor) ────────────

    /// <summary>
    /// Trains a 3-feature TorchSharp logistic gate on [calibP, scoreNorm, metaLabelScore].
    /// Label = 1 if the AdaBoost prediction was correct for that calibration sample.
    /// scoreNorm = |score|/sumAlpha replaces ensStd since AdaBoost has no bagging.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns (weights[3], bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        double               sumAlpha,
        int                  F)
    {
        const int Dim     = 3;   // [calibP, scoreNorm, metaLabelScore]
        const int MetaDim = 7;
        if (calSet.Count < 10) return (new double[Dim], 0.0, 0.5);

        int    n        = calSet.Count;
        double alphaNorm = Math.Max(sumAlpha, 1e-6);
        int    rawTop    = Math.Min(5, F);

        var xArr = new float[n * Dim];
        var yArr = new float[n];
        var mf   = new double[MetaDim];

        for (int i = 0; i < n; i++)
        {
            var    s         = calSet[i];
            double score     = PredictScore(s.Features, stumps, alphas);
            double rawP      = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
            double calibP    = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            double scoreNorm = Math.Abs(score) / alphaNorm;

            // Meta-label score
            mf[0] = rawP; mf[1] = scoreNorm;
            for (int j = 0; j < rawTop; j++) mf[2 + j] = s.Features[j];
            double mz = metaLabelBias;
            for (int j = 0; j < MetaDim && j < metaLabelWeights.Length; j++)
                mz += metaLabelWeights[j] * mf[j];
            double metaScore = MLFeatureHelper.Sigmoid(mz);

            xArr[i * Dim + 0] = (float)calibP;
            xArr[i * Dim + 1] = (float)scoreNorm;
            xArr[i * Dim + 2] = (float)metaScore;
            yArr[i] = (calibP >= 0.5) == (s.Direction > 0) ? 1f : 0f;
        }

        using var wP  = new Parameter(zeros(Dim, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.01, weight_decay: 0.001);

        for (int epoch = 0; epoch < 60; epoch++)
        {
            opt.zero_grad();
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, Dim);
            using var yT    = torch.tensor(yArr, device: CPU).reshape(n, 1);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yT);
            loss.backward();
            opt.step();
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var aw = new double[Dim];
        for (int i = 0; i < Dim; i++) aw[i] = finalW[i];
        return (aw, finalB, 0.5);
    }

    // ── Quantile magnitude regressor (TorchSharp: Adam + pinball loss + weight_decay) ──

    /// <summary>
    /// Fits a linear quantile regressor using TorchSharp's vectorised Adam optimizer.
    /// The pinball (check) loss: L = τ·max(r,0) − (1−τ)·min(r,0), where r = y − ŷ,
    /// is computed as a tensor expression for batch-level parallelism.
    /// L2 regularisation applied via Adam weight_decay.
    /// Returns weights and bias for the τ-th conditional quantile.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train,
        int                  F,
        double               tau)
    {
        if (train.Count == 0) return (new double[F], 0.0);

        int n = train.Count;
        var xArr = new float[n * F];
        var yArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(train[i].Features, 0, xArr, i * F, F);
            yArr[i] = train[i].Magnitude;
        }

        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.005, weight_decay: 1e-4);

        float tauF    = (float)tau;
        float tauMF   = (float)(1.0 - tau);  // 1 - τ

        for (int pass = 0; pass < 8; pass++)
        {
            opt.zero_grad();
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, F);
            using var yT    = torch.tensor(yArr, device: CPU).reshape(n, 1);
            using var pred  = torch.mm(xT, wP) + bP;
            using var r     = yT - pred;                                   // residual r = y - ŷ
            // Pinball: τ·max(r,0) − (1-τ)·min(r,0)  = τ·relu(r) + (1-τ)·relu(-r)
            using var loss  = (functional.relu(r) * tauF + functional.relu(-r) * tauMF).mean();
            loss.backward();
            opt.step();
        }

        float[] finalW;
        float   finalB;
        using (no_grad())
        {
            finalW = wP.cpu().data<float>().ToArray();
            finalB = bP.cpu().data<float>()[0];
        }

        var wOut = new double[F];
        for (int j = 0; j < F; j++) wOut[j] = finalW[j];
        return (wOut, finalB);
    }

    // ── Decision boundary distance stats ──────────────────────────────────────

    /// <summary>
    /// Computes mean and standard deviation of the normalised score magnitude
    /// |score|/sumAlpha over the calibration set. Higher values indicate the sample
    /// is far from the decision boundary (AdaBoost analog of ‖∇_x P‖).
    /// Returns (Mean, Std); both 0.0 when the cal set is empty.
    /// </summary>
    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        List<GbmTree>        stumps,
        List<double>         alphas,
        double               sumAlpha)
    {
        if (calSet.Count == 0) return (0.0, 0.0);

        double alphaNorm = Math.Max(sumAlpha, 1e-6);
        var    norms     = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double score = PredictScore(calSet[i].Features, stumps, alphas);
            double rawP  = MLFeatureHelper.Sigmoid(2 * score);
            // Combine normalised margin with P(1−P) to match logistic convention
            norms[i] = rawP * (1.0 - rawP) * (Math.Abs(score) / alphaNorm);
        }

        double mean = 0.0;
        foreach (double v in norms) mean += v;
        mean /= norms.Length;

        double variance = 0.0;
        foreach (double v in norms) { double d = v - mean; variance += d * d; }
        double std = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0.0;
        return (mean, std);
    }

    // ── Mutual-information redundancy check ───────────────────────────────────

    /// <summary>
    /// Checks pairwise mutual information (MI) between the first 10 features using
    /// a 10×10 bin joint histogram (features assumed to be z-scored, centred near 0).
    /// Feature pairs with MI ≥ threshold × log(2) are flagged as redundant.
    /// Returns an array of "Name_i:Name_j" strings; empty when threshold is 0.
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  F,
        double               threshold)
    {
        if (threshold <= 0.0 || trainSet.Count < 20) return [];

        const int TopN   = 10;
        const int NumBin = 10;

        int        checkCount = Math.Min(TopN, F);
        var        result     = new List<string>();
        double     maxMi      = threshold * Math.Log(2);

        for (int i = 0; i < checkCount; i++)
        {
            for (int j = i + 1; j < checkCount; j++)
            {
                var    joint = new double[NumBin, NumBin];
                var    margI = new double[NumBin];
                var    margJ = new double[NumBin];
                int    n     = 0;

                foreach (var s in trainSet)
                {
                    double vi = s.Features[i];
                    double vj = s.Features[j];
                    int bi = Math.Clamp((int)((vi + 3.0) / 6.0 * NumBin), 0, NumBin - 1);
                    int bj = Math.Clamp((int)((vj + 3.0) / 6.0 * NumBin), 0, NumBin - 1);
                    joint[bi, bj]++;
                    margI[bi]++;
                    margJ[bj]++;
                    n++;
                }

                if (n == 0) continue;
                double mi = 0.0;
                for (int bi = 0; bi < NumBin; bi++)
                    for (int bj = 0; bj < NumBin; bj++)
                    {
                        double pij = joint[bi, bj] / n;
                        double pi  = margI[bi]      / n;
                        double pj  = margJ[bj]      / n;
                        if (pij > 0 && pi > 0 && pj > 0)
                            mi += pij * Math.Log(pij / (pi * pj));
                    }

                if (mi >= maxMi)
                {
                    string nameI = i < MLFeatureHelper.FeatureNames.Length
                        ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
                    string nameJ = j < MLFeatureHelper.FeatureNames.Length
                        ? MLFeatureHelper.FeatureNames[j] : $"F{j}";
                    result.Add($"{nameI}:{nameJ}");
                }
            }
        }

        return [.. result];
    }

    // ── Jackknife+ residuals (half-ensemble LOO proxy) ────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals for AdaBoost.
    /// Since AdaBoost has no bootstrap, we use a half-ensemble leave-K/2-rounds-out proxy:
    /// the "base" prediction uses only the first ⌈K/2⌉ stumps; the residual
    /// r_i = |trueLabel − firstHalfP_i| captures how much residual rounds had to
    /// correct the base prediction for each training sample.
    /// Returns residuals sorted in ascending order; empty when too few stumps.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        List<GbmTree>        stumps,
        List<double>         alphas)
    {
        int K = Math.Min(stumps.Count, alphas.Count);
        if (K < 4 || trainSet.Count < 20) return [];

        int halfK = (K + 1) / 2;
        var halfStumps = stumps[..halfK];
        var halfAlphas = alphas[..halfK];

        var residuals = new List<double>(trainSet.Count);
        foreach (var s in trainSet)
        {
            double halfScore = PredictScore(s.Features, halfStumps, halfAlphas);
            double halfP     = MLFeatureHelper.Sigmoid(2 * halfScore);
            double trueLabel = s.Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - halfP));
        }

        residuals.Sort();
        return [.. residuals];
    }

    // ── SAMME.R stump constructor ──────────────────────────────────────────────

    /// <summary>
    /// Builds a depth-1 SAMME.R stump.  Each leaf stores ½·logit(p_leaf) where
    /// p_leaf = weighted fraction of positive samples in that partition.
    /// Clamped to [Eps, 1−Eps] before logit to avoid ±∞ leaf values.
    /// </summary>
    private static GbmTree BuildSammeRStump(
        int                  featureIndex,
        double               threshold,
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  m)
    {
        double posLeft = 0, totLeft = 0, posRight = 0, totRight = 0;
        for (int i = 0; i < m; i++)
        {
            double xf = train[i].Features[featureIndex];
            if (xf <= threshold) { totLeft  += weights[i]; if (labels[i] > 0) posLeft  += weights[i]; }
            else                 { totRight += weights[i]; if (labels[i] > 0) posRight += weights[i]; }
        }
        double pL = totLeft  > 1e-15 ? Math.Clamp(posLeft  / totLeft,  Eps, 1.0 - Eps) : 0.5;
        double pR = totRight > 1e-15 ? Math.Clamp(posRight / totRight, Eps, 1.0 - Eps) : 0.5;
        double lv = 0.5 * MLFeatureHelper.Logit(pL);
        double rv = 0.5 * MLFeatureHelper.Logit(pR);
        // parity is implicit: lv > rv means left leaf favours Buy, which is consistent with
        // how FindBestStump assigned parity.  We pass parity=1 as a placeholder; BuildStump
        // ignores it when explicit leaf values are provided.
        return BuildStump(featureIndex, threshold, 1, leftLeafValue: lv, rightLeafValue: rv);
    }

    // ── FindBestStumpInSubset ─────────────────────────────────────────────────

    /// <summary>
    /// Same O(m log m) prefix-sum sweep as <see cref="FindBestStump"/> but restricted to
    /// a caller-supplied subset of sample indices.  Used by <see cref="BuildDepth2Tree"/>
    /// to find the best split within the left or right partition of the root node.
    /// Returns (Fi, Thresh, Parity, BestErr, ProbLeft, ProbRight) where ProbLeft/Right are
    /// the weighted fractions of positive samples in each resulting leaf (for SAMME.R).
    /// </summary>
    private static (int Fi, double Thresh, int Parity, double BestErr, double ProbLeft, double ProbRight)
        FindBestStumpInSubset(
            List<TrainingSample> train,
            int[]                labels,
            double[]             weights,
            int                  F,
            List<int>            subset,
            bool[]?              activeMask,
            double[]             tmpKeys,
            int[]                tmpIdx)
    {
        int sz = subset.Count;
        if (sz == 0) return (-1, 0, 1, 0.5, 0.5, 0.5);

        double bestErr    = double.MaxValue;
        int    bestFi     = 0;
        double bestThresh = 0;
        int    bestParity = 1;

        for (int fi = 0; fi < F; fi++)
        {
            if (activeMask is not null && fi < activeMask.Length && !activeMask[fi]) continue;

            for (int k = 0; k < sz; k++)
            {
                int oi = subset[k];
                tmpKeys[k] = train[oi].Features[fi];
                tmpIdx[k]  = oi;
            }
            Array.Sort(tmpKeys, tmpIdx, 0, sz);

            double totalPos = 0, totalNeg = 0;
            for (int k = 0; k < sz; k++)
            {
                int oi = tmpIdx[k];
                if (labels[oi] > 0) totalPos += weights[oi]; else totalNeg += weights[oi];
            }

            double cumPosLeft = 0, cumNegLeft = 0;
            for (int ti = 0; ti < sz - 1; ti++)
            {
                int oi = tmpIdx[ti];
                if (labels[oi] > 0) cumPosLeft += weights[oi]; else cumNegLeft += weights[oi];
                if (tmpKeys[ti + 1] <= tmpKeys[ti] + 1e-12) continue;
                double thresh = (tmpKeys[ti] + tmpKeys[ti + 1]) * 0.5;
                double err1   = cumNegLeft + (totalPos - cumPosLeft);
                double err2   = 1.0 - err1;
                if (err1 < bestErr) { bestErr = err1; bestFi = fi; bestThresh = thresh; bestParity =  1; }
                if (err2 < bestErr) { bestErr = err2; bestFi = fi; bestThresh = thresh; bestParity = -1; }
            }
        }

        // Compute leaf probabilities for SAMME.R from the winning split
        double posL = 0, totL = 0, posR = 0, totR = 0;
        foreach (int oi in subset)
        {
            double xf = bestFi < train[oi].Features.Length ? train[oi].Features[bestFi] : 0;
            if (xf <= bestThresh) { totL += weights[oi]; if (labels[oi] > 0) posL += weights[oi]; }
            else                  { totR += weights[oi]; if (labels[oi] > 0) posR += weights[oi]; }
        }
        double pLeft  = totL > 1e-15 ? Math.Clamp(posL / totL, Eps, 1.0 - Eps) : 0.5;
        double pRight = totR > 1e-15 ? Math.Clamp(posR / totR, Eps, 1.0 - Eps) : 0.5;

        return (bestFi, bestThresh, bestParity, bestErr, pLeft, pRight);
    }

    // ── Adversarial validation (TorchSharp logistic, train vs test AUC) ──────

    /// <summary>
    /// Trains a TorchSharp logistic classifier to distinguish training-set samples
    /// (label=0) from test-set samples (label=1).  Returns the ROC-AUC as a measure
    /// of covariate shift: 0.50 = no shift (random), 1.0 = perfect discrimination.
    /// Uses L2 regularisation (weight_decay) to prevent overfitting on small sets.
    /// Train size is capped at 5× test size to keep class balance reasonable.
    /// </summary>
    private static double ComputeAdversarialAuc(
        List<TrainingSample>         trainSet,
        List<TrainingSample>         testSet,
        int                          F,
        ILogger<AdaBoostModelTrainer> logger)
    {
        int n1    = testSet.Count;
        int n0    = Math.Min(trainSet.Count, n1 * 5);
        int n     = n0 + n1;
        if (n < 20) return 0.5;

        // Use the most-recent n0 training samples (closest in time to test set)
        var trainSlice = trainSet.Count > n0 ? trainSet[^n0..] : trainSet;

        var xArr = new float[n * F];
        var yArr = new float[n];

        for (int i = 0; i < n0; i++)
        {
            Array.Copy(trainSlice[i].Features, 0, xArr, i * F, F);
            yArr[i] = 0f;
        }
        for (int i = 0; i < n1; i++)
        {
            Array.Copy(testSet[i].Features, 0, xArr, (n0 + i) * F, F);
            yArr[n0 + i] = 1f;
        }

        // TorchSharp logistic regression: weight [F,1], bias [1]
        using var wP  = new Parameter(zeros(F, 1));
        using var bP  = new Parameter(zeros(1));
        using var opt = optim.Adam(new Parameter[] { wP, bP }, lr: 0.005, weight_decay: 0.01);

        for (int epoch = 0; epoch < 60; epoch++)
        {
            opt.zero_grad();
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, F);
            using var yT    = torch.tensor(yArr, device: CPU).reshape(n, 1);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit);
            using var loss  = functional.binary_cross_entropy(prob, yT);
            loss.backward();
            opt.step();
        }

        // Extract scores for Wilcoxon AUC
        float[] scoreArr;
        using (no_grad())
        {
            using var xT    = torch.tensor(xArr, device: CPU).reshape(n, F);
            using var logit = torch.mm(xT, wP) + bP;
            using var prob  = torch.sigmoid(logit).squeeze(1);
            scoreArr = prob.cpu().data<float>().ToArray();
        }

        // ROC-AUC via Wilcoxon rank statistic: P(score(pos) > score(neg))
        var scores = new (float Score, int Label)[n];
        for (int i = 0; i < n; i++) scores[i] = (scoreArr[i], (int)yArr[i]);
        Array.Sort(scores, (a, b) => b.Score.CompareTo(a.Score)); // descending

        long tp = 0, aucNum = 0;
        foreach (var (_, lbl) in scores)
        {
            if (lbl == 1) tp++;
            else          aucNum += tp;
        }
        long pos = n1;
        long neg = n0;
        return (pos > 0 && neg > 0) ? (double)aucNum / (pos * neg) : 0.5;
    }

    // ── Depth-2 tree builder ──────────────────────────────────────────────────

    /// <summary>
    /// Greedily builds a depth-2 classification tree.
    /// <list type="number">
    ///   <item>Partition samples by the already-found root split (rootFi, rootThresh).</item>
    ///   <item>For each partition run <see cref="FindBestStumpInSubset"/> to find the best child split.</item>
    ///   <item>Assemble a 7-node tree:
    ///     node 0 = root; 1 = left-child split; 2 = right-child split;
    ///     3 = LL leaf; 4 = LR leaf; 5 = RL leaf; 6 = RR leaf.</item>
    /// </list>
    /// For SAMME (<paramref name="sammeR"/>=false) leaf values are ±1 from child parity.
    /// For SAMME.R leaf values are ½·logit(p_leaf) derived from weighted class fractions.
    /// Re-uses the caller's <paramref name="sortKeys"/>/<paramref name="sortIndices"/> buffers
    /// (length ≥ m) sequentially — never concurrently.
    /// </summary>
    private static GbmTree BuildDepth2Tree(
        int                  rootFi,
        double               rootThresh,
        List<TrainingSample> train,
        int[]                labels,
        double[]             weights,
        int                  F,
        double[]             sortKeys,
        int[]                sortIndices,
        bool                 sammeR,
        bool[]?              activeMask = null)
    {
        int m = train.Count;

        var leftIdx  = new List<int>(m / 2 + 1);
        var rightIdx = new List<int>(m / 2 + 1);
        for (int i = 0; i < m; i++)
        {
            if (rootFi < train[i].Features.Length && train[i].Features[rootFi] <= rootThresh)
                leftIdx.Add(i);
            else
                rightIdx.Add(i);
        }

        // Find best child splits (reuse sort buffers sequentially)
        var (lFi, lThresh, lParity, _, lProbL, lProbR) =
            FindBestStumpInSubset(train, labels, weights, F, leftIdx,  activeMask, sortKeys, sortIndices);
        var (rFi, rThresh, rParity, _, rProbL, rProbR) =
            FindBestStumpInSubset(train, labels, weights, F, rightIdx, activeMask, sortKeys, sortIndices);

        // Leaf values: ±1 for SAMME, ½·logit(p) for SAMME.R
        double llv, lrv, rlv, rrv;
        if (sammeR)
        {
            llv = 0.5 * MLFeatureHelper.Logit(lProbL);
            lrv = 0.5 * MLFeatureHelper.Logit(lProbR);
            rlv = 0.5 * MLFeatureHelper.Logit(rProbL);
            rrv = 0.5 * MLFeatureHelper.Logit(rProbR);
        }
        else
        {
            llv = lParity > 0 ?  1.0 : -1.0;
            lrv = lParity > 0 ? -1.0 :  1.0;
            rlv = rParity > 0 ?  1.0 : -1.0;
            rrv = rParity > 0 ? -1.0 :  1.0;
        }

        // Node layout: 0=root, 1=left split, 2=right split, 3=LL, 4=LR, 5=RL, 6=RR
        return new GbmTree
        {
            Nodes =
            [
                new GbmNode { IsLeaf = false, SplitFeature = rootFi, SplitThreshold = rootThresh, LeftChild = 1, RightChild = 2 },
                new GbmNode { IsLeaf = false, SplitFeature = lFi,    SplitThreshold = lThresh,    LeftChild = 3, RightChild = 4 },
                new GbmNode { IsLeaf = false, SplitFeature = rFi,    SplitThreshold = rThresh,    LeftChild = 5, RightChild = 6 },
                new GbmNode { IsLeaf = true,  LeafValue = llv },
                new GbmNode { IsLeaf = true,  LeafValue = lrv },
                new GbmNode { IsLeaf = true,  LeafValue = rlv },
                new GbmNode { IsLeaf = true,  LeafValue = rrv },
            ]
        };
    }
}
