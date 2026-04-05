using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// ROCKET (RandOm Convolutional KErnel Transform) trainer (Rec #388).
/// Generates random convolutional kernels applied to the feature vector (treated as a
/// 1-D sequence), extracts max-pooling and PPV (proportion of positive values), then
/// trains ridge regression on the resulting 2K-dimensional feature map.
/// Registered with key "rocket".
/// <para>
/// Production-grade features (mirroring BaggedLogisticTrainer):
/// <list type="number">
///   <item>Z-score standardisation of raw features before ROCKET transform.</item>
///   <item>Walk-forward cross-validation with purging, embargo, equity-curve gate, and Sharpe trend.</item>
///   <item>70 / 10 / 20 train / calibration / test splits with embargo gaps.</item>
///   <item>Adam optimizer with cosine-annealing LR schedule + per-epoch early stopping.</item>
///   <item>Label smoothing + adaptive label smoothing.</item>
///   <item>Warm-start: reuse kernels and weights from a previous model snapshot.</item>
///   <item>Platt scaling + class-conditional Platt on the calibration fold.</item>
///   <item>ECE (Expected Calibration Error) computed post-Platt on the test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set.</item>
///   <item>Magnitude regressor (linear OLS) for ATR-normalised move-size prediction.</item>
///   <item>Quantile magnitude regressor (pinball loss).</item>
///   <item>Isotonic calibration (PAVA) after Platt scaling.</item>
///   <item>Conformal prediction threshold (split-conformal q̂).</item>
///   <item>Meta-label secondary classifier for selective prediction.</item>
///   <item>Abstention gate for low-confidence environments.</item>
///   <item>Permutation feature importance on original features.</item>
///   <item>Decision boundary distance statistics.</item>
///   <item>Durbin-Watson autocorrelation on magnitude residuals.</item>
///   <item>Temperature scaling on calibration fold.</item>
///   <item>Brier Skill Score for model quality assessment.</item>
///   <item>Average Kelly fraction for position sizing.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Post-training NaN/Inf weight sanitisation.</item>
///   <item>Stationarity gate (soft ADF check).</item>
///   <item>Density-ratio importance weights.</item>
///   <item>Covariate shift weights from parent model.</item>
///   <item>Incremental update fast-path for warm-start fine-tuning.</item>
///   <item>MiniRocket ternary kernel weights {-1, 0, 1}.</item>
///   <item>Multi-variate channel-independent kernels.</item>
///   <item>Learning rate warmup before cosine annealing.</item>
///   <item>Gradient norm clipping.</item>
///   <item>Fractional differencing of non-stationary features.</item>
///   <item>Outlier winsorization before standardization.</item>
///   <item>Combinatorial purged cross-validation.</item>
///   <item>Per-fold Platt calibration in CV.</item>
///   <item>Venn-ABERS calibration bounds.</item>
///   <item>Per-stage ECE logging (post-Platt, post-isotonic, post-temperature).</item>
///   <item>Kernel subset dropout for epistemic uncertainty.</item>
///   <item>Extended abstention gate (5 features).</item>
///   <item>ROCKET-space magnitude regressor.</item>
///   <item>Magnitude R² metric.</item>
///   <item>Fast weight-based feature attribution.</item>
///   <item>Feature interaction detection (synergistic pairs).</item>
///   <item>Kernel evolution (retain top kernels from warm-start).</item>
///   <item>Parent importance for feature pruning.</item>
///   <item>Graceful per-weight NaN/Inf sanitization.</item>
///   <item>Bias regularization in ridge training.</item>
///   <item>Empty test/cal set guards.</item>
///   <item>Deterministic parallel execution.</item>
///   <item>Per-kernel accuracy contribution diagnostics.</item>
///   <item>Training convergence metadata (early stop epoch, best val loss, SWA count).</item>
///   <item>Reliability diagram per-bin data.</item>
///   <item>Consistent RocketModelParams parameter struct.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Rocket)]
public sealed class RocketModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "ROCKET";
    private const string ModelVersion = "3.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;
    private const int    DefaultBatchSize = 32;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    private static readonly int[] KernelLengths = { 7, 9, 11 };

    // ── Consistent parameter struct (#28) ────────────────────────────────────

    private readonly record struct RocketModelParams(
        double[] W, double Bias, double PlattA, double PlattB, int Dim);

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<RocketModelTrainer> _logger;

    public RocketModelTrainer(ILogger<RocketModelTrainer> logger) => _logger = logger;

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

        int featureCount = samples[0].Features.Length;

        // ── 0. Incremental update fast-path ─────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * 24);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "RocketModelTrainer incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    MaxEpochs             = Math.Max(20, hp.MaxEpochs / 5),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = hp.LearningRate / 5.0,
                    UseIncrementalUpdate  = false,
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 0b. Outlier winsorization (#7) ──────────────────────────────────
        if (hp.RocketWinsorizePercentile > 0)
        {
            double pctile = hp.RocketWinsorizePercentile;
            int nSamp = samples.Count;
            for (int j = 0; j < featureCount; j++)
            {
                var vals = new float[nSamp];
                for (int i = 0; i < nSamp; i++) vals[i] = samples[i].Features[j];
                Array.Sort(vals);
                int loIdx = Math.Clamp((int)(pctile * nSamp), 0, nSamp - 1);
                int hiIdx = Math.Clamp((int)((1.0 - pctile) * nSamp), 0, nSamp - 1);
                float lo = vals[loIdx];
                float hi = vals[hiIdx];
                for (int i = 0; i < nSamp; i++)
                    samples[i].Features[j] = Math.Clamp(samples[i].Features[j], lo, hi);
            }
            _logger.LogInformation("ROCKET winsorized features at p={Pctile:F3}", pctile);
        }

        // ── 1. Z-score standardisation over ALL samples ──────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        int numKernels = hp.K > 0 ? hp.K : 1000;

        // ── 1b. Fractional differencing (#6) ────────────────────────────────
        if (hp.FracDiffD > 0)
        {
            int nonStatCount = CountNonStationaryFeatures(allStd, featureCount);
            int diffCount = 0;
            int nStd = allStd.Count;
            for (int j = 0; j < featureCount; j++)
            {
                // Check if feature j is non-stationary (simple AR(1) proxy)
                double sumXY = 0, sumXX = 0;
                for (int i = 1; i < nStd; i++)
                {
                    double x = allStd[i - 1].Features[j];
                    double y = allStd[i].Features[j];
                    sumXY += x * y;
                    sumXX += x * x;
                }
                double rho = sumXX > 1e-10 ? sumXY / sumXX : 0;
                if (Math.Abs(rho) > 0.95)
                {
                    for (int i = nStd - 1; i >= 1; i--)
                        allStd[i].Features[j] -= (float)(hp.FracDiffD * allStd[i - 1].Features[j]);
                    diffCount++;
                }
            }
            if (diffCount > 0)
                _logger.LogInformation("ROCKET fractional differencing: applied d={D:F2} to {Count}/{Total} non-stationary features",
                    hp.FracDiffD, diffCount, featureCount);
        }

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, featureCount, numKernels, ct);
        _logger.LogInformation(
            "ROCKET walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70 % train | 10 % cal | ~20 % test ──────
        int n        = allStd.Count;
        int trainEnd = (int)(n * 0.70);
        int calEnd   = (int)(n * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < n ? calEnd : n)];
        var testSet  = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        if (testSet.Count < 5)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient test samples after splits: {testSet.Count} < 5");

        if (calSet.Count < 5)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient calibration samples after splits: {calSet.Count} < 5");

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 3b. Stationarity gate (soft ADF check) ────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, featureCount);
            double nonStatFraction = featureCount > 0 ? (double)nonStatCount / featureCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "ROCKET stationarity gate: {NonStat}/{Total} features have unit root. Consider enabling FracDiffD.",
                    nonStatCount, featureCount);
        }

        // ── 3c. Density-ratio importance weights ──────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, featureCount, hp.DensityRatioWindowDays);
            _logger.LogDebug("ROCKET density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 3d. Adaptive label smoothing ──────────────────────────────────────
        double adaptiveLabelSmoothing = hp.LabelSmoothing;
        if (hp.UseAdaptiveLabelSmoothing && trainSet.Count > 0)
        {
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
                "ROCKET adaptive label smoothing: ε={Eps:F3} (ambiguous-proxy fraction={Frac:P1})",
                adaptiveLabelSmoothing, ambiguousFraction);
        }

        // ── 3e. Covariate shift weight integration ──────────────────────────────
        if (hp.UseCovariateShiftWeights &&
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
            _logger.LogDebug("ROCKET covariate shift weights applied from parent model.");
        }

        // ── 4. Generate ROCKET kernels ──────────────────────────────────────
        int kernelSeed = HashCode.Combine(samples.Count, featureCount, numKernels, samples[0].Direction);
        var rng = new Random(kernelSeed);
        var (kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, channelStarts, channelEnds) =
            GenerateKernels(numKernels, featureCount, rng, hp.RocketUseMiniWeights, hp.RocketMultivariate);

        // ── 4b. Kernel evolution (#18) ──────────────────────────────────────
        if (hp.RocketKernelRetentionFraction > 0 && warmStart?.RocketKernelWeights is { Length: > 0 } parentKW
            && warmStart.Weights is { Length: > 0 } parentW && parentW[0].Length >= numKernels)
        {
            int retainCount = Math.Clamp((int)(numKernels * hp.RocketKernelRetentionFraction), 1, numKernels - 1);
            // Sort kernel indices by |rw[k]| descending from parent weights
            var parentRw = parentW[0];
            var sortedKernels = Enumerable.Range(0, Math.Min(numKernels, parentKW.Length))
                .OrderByDescending(k => k < parentRw.Length ? Math.Abs(parentRw[k]) : 0)
                .Take(retainCount).ToArray();

            foreach (int k in sortedKernels)
            {
                if (k < parentKW.Length && parentKW[k] is { Length: > 0 })
                {
                    kernelWeights[k] = [..parentKW[k]];
                    if (warmStart.RocketKernelDilations is { Length: > 0 } && k < warmStart.RocketKernelDilations.Length)
                        kernelDilations[k] = warmStart.RocketKernelDilations[k];
                    if (warmStart.RocketKernelPaddings is { Length: > 0 } && k < warmStart.RocketKernelPaddings.Length)
                        kernelPaddings[k] = warmStart.RocketKernelPaddings[k];
                    if (warmStart.RocketKernelLengths is { Length: > 0 } && k < warmStart.RocketKernelLengths.Length)
                        kernelLengthArr[k] = warmStart.RocketKernelLengths[k];
                }
            }
            _logger.LogInformation("ROCKET kernel evolution: retained {Retained}/{Total} top kernels from parent", retainCount, numKernels);
        }

        // ── 5. Extract ROCKET features for all splits ────────────────────────
        var trainRocket = ExtractRocketFeatures(trainSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
        var calRocket   = ExtractRocketFeatures(calSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
        var testRocket  = ExtractRocketFeatures(testSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);

        ct.ThrowIfCancellationRequested();

        int dim = 2 * numKernels;

        // ── 5b. Z-score ROCKET features ──────────────────────────────────────
        var (rocketMeans, rocketStds) = ComputeRocketStandardization(trainRocket, dim);
        StandardizeRocketInPlace(trainRocket, rocketMeans, rocketStds, dim);
        StandardizeRocketInPlace(calRocket, rocketMeans, rocketStds, dim);
        StandardizeRocketInPlace(testRocket, rocketMeans, rocketStds, dim);

        // ── 6. Train ridge regression (Adam + early stopping + label smoothing) ──
        var (rw, rb, earlyStopEpoch, finalValLoss, swaCheckpointCount) = TrainRidgeAdam(trainRocket, trainSet, dim, effectiveHp, densityWeights, warmStart, ct);

        // ── 6b. Post-training weight sanitisation (#20: graceful per-weight) ──
        int sanitizedCount = 0;
        {
            int nonFiniteCount = 0;
            if (!double.IsFinite(rb)) nonFiniteCount++;
            for (int j = 0; j < rw.Length; j++)
                if (!double.IsFinite(rw[j])) nonFiniteCount++;

            if (nonFiniteCount > 0)
            {
                if (nonFiniteCount > (rw.Length + 1) / 2)
                {
                    // More than 50% non-finite: zero everything
                    Array.Clear(rw, 0, rw.Length);
                    rb = 0.0;
                    _logger.LogWarning("RocketModelTrainer: >50% non-finite weights — zeroed all.");
                }
                else
                {
                    // Replace individual non-finite weights with 0
                    for (int j = 0; j < rw.Length; j++)
                        if (!double.IsFinite(rw[j])) rw[j] = 0.0;
                    if (!double.IsFinite(rb)) rb = 0.0;
                    _logger.LogWarning("RocketModelTrainer: sanitized {Count} individual non-finite weights.", nonFiniteCount);
                }
                sanitizedCount = 1;
            }
        }

        // ── 7. Platt calibration ─────────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calRocket, calSet, rw, rb, dim);
        _logger.LogDebug("ROCKET Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 7b. Class-conditional Platt ──────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calRocket, calSet, rw, rb, dim);

        // ── 7c. Average Kelly fraction ───────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calRocket, calSet, rw, rb, plattA, plattB, dim);

        // ── 8. Fit magnitude regressor ────────────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, featureCount, ct);

        // ── 8b. Quantile magnitude regressor ─────────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, featureCount, hp.MagnitudeQuantileTau, ct);
            _logger.LogDebug("ROCKET quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 8c. ROCKET-space magnitude regressor (#14) ──────────────────────
        double[] rocketMagWeights = [];
        double   rocketMagBias    = 0.0;
        if (trainRocket.Count >= hp.MinSamples)
        {
            (rocketMagWeights, rocketMagBias) = TrainAdamRegressor(trainRocket, trainSet, dim, ct);
            _logger.LogDebug("ROCKET-space magnitude regressor fitted (dim={Dim}).", dim);
        }

        // ── 9. Final evaluation on held-out test set ────────────────────────
        var finalMetrics = EvaluateModel(testRocket, testSet, rw, rb, magWeights, magBias, plattA, plattB, dim, featureCount,
            rocketMagWeights, rocketMagBias);

        _logger.LogInformation(
            "ROCKET final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 10. ECE post-Platt ───────────────────────────────────────────────
        var (ece, reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeEce(testRocket, testSet, rw, rb, plattA, plattB, dim);
        _logger.LogInformation("ROCKET post-Platt ECE={Ece:F4}", ece);

        // ── 11. EV-optimal decision threshold (on cal set) ───────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calRocket, calSet, rw, rb, plattA, plattB, dim,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("ROCKET EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 12. Permutation feature importance (on original features) ────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, testRocket, rw, rb, plattA, plattB, dim,
                kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels,
                featureCount, rocketMeans, rocketStds, ct, hp.RocketDeterministicParallel)
            : new float[featureCount];

        if (featureImportance.Length > 0)
        {
            var topFeatures = featureImportance
                .Select((imp, idx) => (Importance: imp, Name: idx < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[idx] : $"F{idx}"))
                .OrderByDescending(x => x.Importance)
                .Take(5);
            _logger.LogInformation(
                "ROCKET top 5 features: {Features}",
                string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        // ── 12b. Calibration-set permutation importance (for warm-start transfer) ──
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, calRocket, rw, rb, dim,
                kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels,
                featureCount, rocketMeans, rocketStds, ct, hp.RocketDeterministicParallel)
            : new double[featureCount];

        // ── 12c. Feature pruning re-train pass ───────────────────────────────
        // #19: Use parent importance for initial mask if available
        float[] pruningImportance = featureImportance;
        if (hp.RocketUseParentImportanceForPruning
            && warmStart?.FeatureImportanceScores is { Length: > 0 } parentImp
            && parentImp.Length == featureCount)
        {
            pruningImportance = new float[featureCount];
            for (int j = 0; j < featureCount; j++)
                pruningImportance[j] = (float)parentImp[j];
            _logger.LogInformation("ROCKET using parent importance scores for feature pruning mask.");
        }
        var activeMask = BuildFeatureMask(pruningImportance, hp.MinFeatureImportance, featureCount);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && featureCount - prunedCount >= 10)
        {
            _logger.LogInformation(
                "ROCKET feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, featureCount);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet, activeMask);
            var maskedTest  = ApplyMask(testSet, activeMask);

            // Re-extract ROCKET features on masked data
            var maskedTrainRocket = ExtractRocketFeatures(maskedTrain, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
            var maskedCalRocket   = ExtractRocketFeatures(maskedCal, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
            var maskedTestRocket  = ExtractRocketFeatures(maskedTest, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);

            var (mrm, mrs) = ComputeRocketStandardization(maskedTrainRocket, dim);
            StandardizeRocketInPlace(maskedTrainRocket, mrm, mrs, dim);
            StandardizeRocketInPlace(maskedCalRocket, mrm, mrs, dim);
            StandardizeRocketInPlace(maskedTestRocket, mrm, mrs, dim);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5, effectiveHp.EarlyStoppingPatience / 2),
            };

            var (pw, pb, _, _, _) = TrainRidgeAdam(maskedTrainRocket, maskedTrain, dim, prunedHp, null, null, ct);
            var (pmw, pmb) = FitLinearRegressor(maskedTrain, featureCount, ct);
            var (pA, pB) = FitPlattScaling(maskedCalRocket, maskedCal, pw, pb, dim);
            var prunedMetrics = EvaluateModel(maskedTestRocket, maskedTest, pw, pb, pmw, pmb, pA, pB, dim, featureCount);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "ROCKET pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                rw = pw; rb = pb;
                magWeights = pmw; magBias = pmb;
                plattA = pA; plattB = pB;
                finalMetrics = prunedMetrics;
                trainRocket = maskedTrainRocket;
                calRocket = maskedCalRocket;
                testRocket = maskedTestRocket;
                rocketMeans = mrm;
                rocketStds = mrs;

                // Re-compute class-conditional Platt + Kelly on pruned model
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(calRocket, maskedCal, rw, rb, dim);
                avgKellyFraction = ComputeAvgKellyFraction(calRocket, maskedCal, rw, rb, plattA, plattB, dim);
                (ece, reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
                    ComputeEce(testRocket, maskedTest, rw, rb, plattA, plattB, dim);
                optimalThreshold = ComputeOptimalThreshold(calRocket, maskedCal, rw, rb, plattA, plattB, dim,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax);
            }
            else
            {
                _logger.LogInformation(
                    "ROCKET pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask = new bool[featureCount];
                Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[featureCount];
            Array.Fill(activeMask, true);
        }

        // ── 13. Isotonic calibration (PAVA) ──────────────────────────────────
        double[] isotonicBp = FitIsotonicCalibration(calRocket, calSet, rw, rb, plattA, plattB, dim);
        _logger.LogInformation("ROCKET isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 13b. Per-stage ECE: post-isotonic (#11) ──────────────────────────
        {
            // Compute ECE with isotonic calibration applied
            double isoEce = 0;
            const int NumBinsIso = 10;
            var isoBinConfSum = new double[NumBinsIso];
            var isoBinCorrect = new int[NumBinsIso];
            var isoBinCount   = new int[NumBinsIso];
            for (int i = 0; i < testSet.Count; i++)
            {
                double p = CalibratedProb(testRocket[i], rw, rb, plattA, plattB, dim);
                if (isotonicBp.Length >= 4) p = ApplyIsotonicCalibration(p, isotonicBp);
                int bin = Math.Clamp((int)(p * NumBinsIso), 0, NumBinsIso - 1);
                isoBinConfSum[bin] += p;
                if (testSet[i].Direction == 1) isoBinCorrect[bin]++;
                isoBinCount[bin]++;
            }
            int isoN = testSet.Count;
            for (int bx = 0; bx < NumBinsIso; bx++)
            {
                if (isoBinCount[bx] == 0) continue;
                double avgConf = isoBinConfSum[bx] / isoBinCount[bx];
                double acc = isoBinCorrect[bx] / (double)isoBinCount[bx];
                isoEce += Math.Abs(acc - avgConf) * isoBinCount[bx] / isoN;
            }
            _logger.LogInformation("ROCKET post-isotonic ECE={Ece:F4}", isoEce);
        }

        // ── 14. Conformal prediction threshold ───────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(
            calRocket, calSet, rw, rb, plattA, plattB, isotonicBp, dim, conformalAlpha);
        _logger.LogInformation("ROCKET conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 15. Meta-label secondary classifier ──────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(calRocket, calSet, rw, rb, dim, featureCount, ct);
        _logger.LogDebug("ROCKET meta-label: bias={B:F4}", metaLabelBias);

        // ── 16. Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            calRocket, calSet, rw, rb, plattA, plattB, metaLabelWeights, metaLabelBias, dim, ct, numKernels);
        _logger.LogDebug("ROCKET abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── 17. Decision boundary distance ───────────────────────────────────
        var (dbMean, dbStd) = calRocket.Count >= 10
            ? ComputeDecisionBoundaryStats(calRocket, rw, rb, dim)
            : (0.0, 0.0);

        // ── 18. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, featureCount);
        _logger.LogDebug("ROCKET Durbin-Watson={DW:F4}", durbinWatson);

        // ── 19. Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calRocket.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calRocket, calSet, rw, rb, dim);
            _logger.LogDebug("ROCKET temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 19b. Per-stage ECE: post-temperature (#11) ───────────��──────────
        if (temperatureScale > 0)
        {
            double tempEce = 0;
            const int NumBinsT = 10;
            var tBinConfSum = new double[NumBinsT];
            var tBinCorrect = new int[NumBinsT];
            var tBinCount   = new int[NumBinsT];
            for (int i = 0; i < testSet.Count; i++)
            {
                double rawP = RocketProb(testRocket[i], rw, rb, dim);
                rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
                double scaledLogit = MLFeatureHelper.Logit(rawP) / temperatureScale;
                double p = MLFeatureHelper.Sigmoid(scaledLogit);
                int bin = Math.Clamp((int)(p * NumBinsT), 0, NumBinsT - 1);
                tBinConfSum[bin] += p;
                if (testSet[i].Direction == 1) tBinCorrect[bin]++;
                tBinCount[bin]++;
            }
            int tN = testSet.Count;
            for (int bx = 0; bx < NumBinsT; bx++)
            {
                if (tBinCount[bx] == 0) continue;
                double avgConf = tBinConfSum[bx] / tBinCount[bx];
                double acc = tBinCorrect[bx] / (double)tBinCount[bx];
                tempEce += Math.Abs(acc - avgConf) * tBinCount[bx] / tN;
            }
            _logger.LogInformation("ROCKET post-temperature ECE={Ece:F4}", tempEce);
        }

        // ── 19c. Holdout-based OOB accuracy proxy ─────────────────────────────
        // ROCKET is a single model (not ensemble), so true OOB isn't available.
        // Use the calibration set (independent of the early-stopping validation split)
        // to avoid optimistic bias from evaluating on model-selection data.
        double oobAccuracy = 0.0;
        {
            int oobCorrect = 0;
            for (int i = 0; i < calRocket.Count; i++)
            {
                double p = CalibratedProb(calRocket[i], rw, rb, plattA, plattB, dim);
                if ((p >= 0.5) == (calSet[i].Direction == 1)) oobCorrect++;
            }
            oobAccuracy = calRocket.Count > 0 ? (double)oobCorrect / calRocket.Count : 0;
            _logger.LogInformation("ROCKET holdout OOB accuracy proxy={OobAcc:P1} (cal set)", oobAccuracy);
        }
        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── 19c. Jackknife+ residuals ────────────────────────────────────────
        // Compute nonconformity residuals on calibration set: |y - calibP|
        double[] jackknifeResiduals = [];
        if (calRocket.Count >= 10)
        {
            var residuals = new double[calRocket.Count];
            for (int i = 0; i < calRocket.Count; i++)
            {
                double calibP = CalibratedProb(calRocket[i], rw, rb, plattA, plattB, dim);
                double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
                residuals[i] = Math.Abs(y - calibP);
            }
            Array.Sort(residuals);
            jackknifeResiduals = residuals;
            _logger.LogInformation("ROCKET jackknife+ residuals computed: {N} samples", jackknifeResiduals.Length);
        }

        // ── 20. Brier Skill Score ────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testRocket, testSet, rw, rb, plattA, plattB, dim);
        _logger.LogInformation("ROCKET BSS={BSS:F4}", brierSkillScore);

        // ── 21. Feature quantile breakpoints (PSI baseline) ──────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 22. Mutual-information feature redundancy ────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, featureCount, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "ROCKET MI redundancy: {N} feature pairs exceed threshold: {Pairs}",
                    redundantPairs.Length, string.Join(", ", redundantPairs));
        }

        // ── 22b. Venn-ABERS calibration bounds (#10) ────────────────────────
        double[] vennAbersCalBounds = [];
        if (calRocket.Count >= 20)
        {
            // Lightweight Venn-ABERS proxy: per-decile [min, max] calibrated P
            const int vBins = 10;
            var vBinMin = new double[vBins];
            var vBinMax = new double[vBins];
            Array.Fill(vBinMin, double.MaxValue);
            Array.Fill(vBinMax, double.MinValue);
            for (int i = 0; i < calRocket.Count; i++)
            {
                double p = CalibratedProb(calRocket[i], rw, rb, plattA, plattB, dim);
                int bin = Math.Clamp((int)(p * vBins), 0, vBins - 1);
                if (p < vBinMin[bin]) vBinMin[bin] = p;
                if (p > vBinMax[bin]) vBinMax[bin] = p;
            }
            vennAbersCalBounds = new double[vBins * 2];
            for (int bi = 0; bi < vBins; bi++)
            {
                vennAbersCalBounds[bi * 2]     = vBinMin[bi] == double.MaxValue ? 0.0 : vBinMin[bi];
                vennAbersCalBounds[bi * 2 + 1] = vBinMax[bi] == double.MinValue ? 1.0 : vBinMax[bi];
            }
            _logger.LogInformation("ROCKET Venn-ABERS calibration bounds: {N} bins", vBins);
        }

        // ── 22c. Magnitude R² (#15) ─────────────────────────────────────────
        double magnitudeR2 = 0.0;
        {
            double sumMagRes = 0, sumMagTot = 0, meanMag = 0;
            for (int i = 0; i < testSet.Count; i++) meanMag += testSet[i].Magnitude;
            meanMag /= testSet.Count > 0 ? testSet.Count : 1;
            for (int i = 0; i < testSet.Count; i++)
            {
                double magPred = featureCount <= magWeights.Length
                    ? MLFeatureHelper.DotProduct(magWeights, testSet[i].Features) + magBias : 0;
                if (rocketMagWeights is { Length: > 0 })
                {
                    double rmp = rocketMagBias;
                    int rLen = Math.Min(rocketMagWeights.Length, testRocket[i].Length);
                    for (int j = 0; j < rLen; j++) rmp += rocketMagWeights[j] * testRocket[i][j];
                    magPred = (magPred + rmp) * 0.5;
                }
                double res = magPred - testSet[i].Magnitude;
                sumMagRes += res * res;
                double tot = testSet[i].Magnitude - meanMag;
                sumMagTot += tot * tot;
            }
            magnitudeR2 = sumMagTot > 1e-10 ? 1.0 - sumMagRes / sumMagTot : 0.0;
            _logger.LogInformation("ROCKET magnitude R²={R2:F4}", magnitudeR2);
        }

        // ── 22d. Fast weight-based attribution (#16) ────────────────────────
        var fastFeatureAttribution = new float[featureCount];
        {
            double attrSum = 0;
            for (int j = 0; j < featureCount; j++)
            {
                double sumAttr = 0;
                for (int k = 0; k < numKernels; k++)
                {
                    int len = kernelLengthArr[k];
                    int dil = kernelDilations[k];
                    bool pad = kernelPaddings[k];
                    int padding = pad ? (len - 1) * dil / 2 : 0;
                    int chStart = channelStarts is not null ? channelStarts[k] : 0;

                    for (int li = 0; li < len; li++)
                    {
                        int srcIdx = j - chStart + li * dil - padding;
                        if (srcIdx == j - chStart)
                            sumAttr += Math.Abs(rw[k]) * Math.Abs(kernelWeights[k][li]);
                    }
                }
                fastFeatureAttribution[j] = (float)sumAttr;
                attrSum += sumAttr;
            }
            if (attrSum > 1e-10)
                for (int j = 0; j < featureCount; j++) fastFeatureAttribution[j] /= (float)attrSum;
        }

        // ── 22e. Feature interaction detection (#17) ─────────────────────────
        string[] synergisticFeaturePairs = [];
        {
            int topN = Math.Min(10, featureCount);
            var topIdx = fastFeatureAttribution
                .Select((v, i) => (v, i))
                .OrderByDescending(x => x.v)
                .Take(topN)
                .Select(x => x.i)
                .ToArray();

            var pairCounts = new Dictionary<(int, int), int>();
            for (int ai = 0; ai < topIdx.Length; ai++)
            for (int bi = ai + 1; bi < topIdx.Length; bi++)
            {
                int a = topIdx[ai], bFeat = topIdx[bi];
                int count = 0;
                for (int k = 0; k < numKernels; k++)
                {
                    int len = kernelLengthArr[k];
                    int dil = kernelDilations[k];
                    bool pad = kernelPaddings[k];
                    int padding = pad ? (len - 1) * dil / 2 : 0;
                    int chStart = channelStarts is not null ? channelStarts[k] : 0;
                    int chEnd = channelEnds is not null ? channelEnds[k] : featureCount;

                    bool touchesA = false, touchesB = false;
                    for (int li = 0; li < len && !(touchesA && touchesB); li++)
                    {
                        int srcMin = chStart - padding + li * dil;
                        int srcMax = srcMin + (featureCount - 1);
                        if (a >= chStart && a < chEnd) touchesA = true;
                        if (bFeat >= chStart && bFeat < chEnd) touchesB = true;
                    }
                    if (touchesA && touchesB) count++;
                }
                if (count > numKernels / 10) pairCounts[(a, bFeat)] = count;
            }
            synergisticFeaturePairs = pairCounts
                .Select(kv =>
                {
                    string nameA = kv.Key.Item1 < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[kv.Key.Item1] : $"F{kv.Key.Item1}";
                    string nameB = kv.Key.Item2 < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[kv.Key.Item2] : $"F{kv.Key.Item2}";
                    return $"{nameA}:{nameB}:{kv.Value}";
                }).ToArray();
            if (synergisticFeaturePairs.Length > 0)
                _logger.LogInformation("ROCKET synergistic feature pairs: {Pairs}", string.Join(", ", synergisticFeaturePairs));
        }

        // ── 22f. Kernel subset dropout (#12) ─────────────────────────────────
        double meanKernelEntropy = 0.0;
        if (hp.RocketKernelDropoutSubsets > 0)
        {
            int subsets = hp.RocketKernelDropoutSubsets;
            var dropRng = new Random(42);
            var variances = new double[testSet.Count];
            var subsetPreds = new double[subsets];

            for (int i = 0; i < testSet.Count; i++)
            {
                for (int s = 0; s < subsets; s++)
                {
                    var maskedFeat = new double[dim];
                    Array.Copy(testRocket[i], maskedFeat, dim);
                    // Zero out 20% of random kernels
                    for (int k = 0; k < numKernels; k++)
                    {
                        if (dropRng.NextDouble() < 0.2)
                        {
                            maskedFeat[k] = 0;
                            maskedFeat[numKernels + k] = 0;
                        }
                    }
                    subsetPreds[s] = CalibratedProb(maskedFeat, rw, rb, plattA, plattB, dim);
                }
                double mean = 0;
                for (int s = 0; s < subsets; s++) mean += subsetPreds[s];
                mean /= subsets;
                double var_ = 0;
                for (int s = 0; s < subsets; s++) { double d = subsetPreds[s] - mean; var_ += d * d; }
                variances[i] = var_ / subsets;
            }
            meanKernelEntropy = variances.Average();
            _logger.LogInformation("ROCKET kernel dropout epistemic uncertainty={Entropy:F6}", meanKernelEntropy);
        }

        // ── 22g. Per-kernel accuracy contribution (#24) ──────────────────────
        double[] perKernelAccContrib = [];
        {
            int diagKernels = Math.Min(numKernels, 100);
            int baseCorrect = 0;
            for (int i = 0; i < testSet.Count; i++)
            {
                double p = CalibratedProb(testRocket[i], rw, rb, plattA, plattB, dim);
                if ((p >= 0.5) == (testSet[i].Direction == 1)) baseCorrect++;
            }
            double baselineAcc = (double)baseCorrect / testSet.Count;

            perKernelAccContrib = new double[diagKernels];
            for (int k = 0; k < diagKernels; k++)
            {
                int correct = 0;
                for (int i = 0; i < testSet.Count; i++)
                {
                    var maskedFeat = new double[dim];
                    Array.Copy(testRocket[i], maskedFeat, dim);
                    maskedFeat[k] = 0;
                    maskedFeat[numKernels + k] = 0;
                    double p = CalibratedProb(maskedFeat, rw, rb, plattA, plattB, dim);
                    if ((p >= 0.5) == (testSet[i].Direction == 1)) correct++;
                }
                perKernelAccContrib[k] = baselineAcc - (double)correct / testSet.Count;
            }
        }

        // ── 23. Mean PPV per kernel (ROCKET-specific diagnostic) ─────────────
        double[] meanPpv = new double[numKernels];
        for (int k = 0; k < numKernels; k++)
        {
            double sum = 0;
            for (int i = 0; i < trainRocket.Count; i++) sum += trainRocket[i][numKernels + k];
            meanPpv[k] = sum / trainRocket.Count;
        }

        // ── 24. Serialise model snapshot ────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = numKernels,
            Weights                    = [rw],
            Biases                     = [rb],
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
            FeatureImportanceScores    = calImportanceScores,
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
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            JackknifeResiduals         = jackknifeResiduals,
            OobAccuracy                = oobAccuracy,
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
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount      = sanitizedCount,
            ConformalCoverage          = hp.ConformalCoverage,
            RocketFeatureStats         = meanPpv,
            RocketMagWeights           = rocketMagWeights,
            RocketMagBias              = rocketMagBias,
            MagnitudeR2                = magnitudeR2,
            FastFeatureAttribution     = fastFeatureAttribution,
            SynergisticFeaturePairs    = synergisticFeaturePairs,
            PerKernelAccuracyContribution = perKernelAccContrib,
            EarlyStoppingEpoch         = earlyStopEpoch,
            FinalValidationLoss        = finalValLoss,
            VennAbersCalBounds         = vennAbersCalBounds,
            MeanKernelEntropy          = meanKernelEntropy,
            ReliabilityBinConfidence   = reliabilityBinConf,
            ReliabilityBinAccuracy     = reliabilityBinAcc,
            ReliabilityBinCounts       = reliabilityBinCounts,
            SwaCheckpointCount         = swaCheckpointCount,
            RocketKernelWeights        = kernelWeights,
            RocketKernelDilations      = kernelDilations,
            RocketKernelPaddings       = kernelPaddings,
            RocketKernelLengths        = kernelLengthArr,
            RocketFeatureMeans         = rocketMeans,
            RocketFeatureStds          = rocketStds,
            RocketKernelSeed           = kernelSeed,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "RocketModelTrainer complete: kernels={K}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            numKernels, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET kernel generation
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[][] Weights, int[] Dilations, bool[] Paddings, int[] Lengths, int[]? ChannelStarts, int[]? ChannelEnds)
        GenerateKernels(int numKernels, int featureCount, Random rng,
            bool useMiniWeights = false, bool multivariate = false)
    {
        var weights   = new double[numKernels][];
        var dilations = new int[numKernels];
        var paddings  = new bool[numKernels];
        var lengths   = new int[numKernels];

        // #2: Multivariate channel-independent kernels
        int[]? channelStarts = null;
        int[]? channelEnds   = null;
        if (multivariate && featureCount >= 6)
        {
            channelStarts = new int[numKernels];
            channelEnds   = new int[numKernels];
            int groupSize = featureCount / 3;
            for (int k = 0; k < numKernels; k++)
            {
                int group = k % 3;
                channelStarts[k] = group * groupSize;
                channelEnds[k]   = group == 2 ? featureCount : (group + 1) * groupSize;
            }
        }

        for (int k = 0; k < numKernels; k++)
        {
            int len    = KernelLengths[rng.Next(KernelLengths.Length)];
            double[] w = new double[len];

            if (useMiniWeights)
            {
                // #1: MiniRocket ternary {-1, 0, 1} kernel weights
                for (int i = 0; i < len; i++) w[i] = rng.Next(3) - 1;
            }
            else
            {
                for (int i = 0; i < len; i++) w[i] = SampleGaussian(rng);
            }

            double wMean = 0;
            for (int i = 0; i < len; i++) wMean += w[i];
            wMean /= len;
            for (int i = 0; i < len; i++) w[i] -= wMean;

            int effectiveFeatureCount = (channelEnds is not null && channelStarts is not null)
                ? channelEnds[k] - channelStarts[k]
                : featureCount;
            double A = len > 1 ? Math.Log2((effectiveFeatureCount - 1.0) / (len - 1) + 1e-6) : 0;
            int dil  = A > 0 ? (int)Math.Floor(Math.Pow(2, rng.NextDouble() * A)) : 1;
            dil = Math.Max(1, dil);

            weights[k]   = w;
            dilations[k] = dil;
            paddings[k]  = rng.NextDouble() < 0.5;
            lengths[k]   = len;
        }

        return (weights, dilations, paddings, lengths, channelStarts, channelEnds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET feature extraction
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<double[]> ExtractRocketFeatures(
        List<TrainingSample> samples,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int[]? channelStarts = null, int[]? channelEnds = null)
    {
        int n = samples.Count;
        int F = samples.Count > 0 ? samples[0].Features.Length : 0;
        var result = new List<double[]>(n);

        for (int i = 0; i < n; i++)
        {
            double[] feat = new double[2 * numKernels];
            float[]  x    = samples[i].Features;

            for (int k = 0; k < numKernels; k++)
            {
                double[] w   = kernelWeights[k];
                int      len = kernelLengthArr[k];
                int      dil = kernelDilations[k];
                bool     pad = kernelPaddings[k];

                // #2: Channel-independent convolution range
                int chStart = channelStarts is not null ? channelStarts[k] : 0;
                int chEnd   = channelEnds is not null ? channelEnds[k] : F;
                int chLen   = chEnd - chStart;

                int padding   = pad ? (len - 1) * dil / 2 : 0;
                int outputLen = chLen + 2 * padding - (len - 1) * dil;

                double maxVal  = double.MinValue;
                int    ppvPos  = 0;
                int    posCount = 0;

                for (int pos = 0; pos < outputLen; pos++)
                {
                    double dot = 0;
                    for (int j = 0; j < len; j++)
                    {
                        int srcIdx = chStart + pos + j * dil - padding;
                        double xVal = (srcIdx >= 0 && srcIdx < F) ? x[srcIdx] : 0;
                        dot += w[j] * xVal;
                    }
                    if (dot > maxVal) maxVal = dot;
                    if (dot > 0) ppvPos++;
                    posCount++;
                }

                feat[k]              = maxVal == double.MinValue ? 0 : maxVal;
                feat[numKernels + k] = posCount > 0 ? (double)ppvPos / posCount : 0;
            }

            result.Add(feat);
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET feature standardisation
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Means, double[] Stds) ComputeRocketStandardization(
        List<double[]> features, int dim)
    {
        var rMeans = new double[dim];
        var rStds  = new double[dim];
        int n = features.Count;

        for (int j = 0; j < dim; j++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += features[i][j];
            double mean = sum / n;

            double varSum = 0;
            for (int i = 0; i < n; i++)
            {
                double d = features[i][j] - mean;
                varSum += d * d;
            }
            double std = n > 1 ? Math.Sqrt(varSum / n) : 1.0;

            rMeans[j] = mean;
            rStds[j]  = std < 1e-10 ? 1.0 : std;
        }

        return (rMeans, rStds);
    }

    private static void StandardizeRocketInPlace(
        List<double[]> features, double[] rMeans, double[] rStds, int dim)
    {
        foreach (var f in features)
        {
            for (int j = 0; j < dim; j++)
                f[j] = (f[j] - rMeans[j]) / rStds[j];
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ridge regression with Adam optimiser + cosine LR + early stopping
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, int EarlyStopEpoch, double BestValLoss, int SwaCount) TrainRidgeAdam(
        List<double[]>       features,
        List<TrainingSample> labels,
        int                  dim,
        TrainingHyperparams  hp,
        double[]?            densityWeights,
        ModelSnapshot?       warmStart,
        CancellationToken    ct)
    {
        int n       = features.Count;
        int valSize = Math.Max(20, n / 10);
        int trainN  = n - valSize;

        double l2    = hp.L2Lambda > 0 ? hp.L2Lambda : 0.01;
        double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        int maxEpochs = hp.MaxEpochs > 0 ? hp.MaxEpochs : 200;
        int patience  = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 20;
        double labelSmoothing = hp.LabelSmoothing;

        // Temporal weights
        var temporalWeights = ComputeTemporalWeights(trainN, hp.TemporalDecayLambda);

        // Blend density-ratio weights
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= trainN)
        {
            var blended = new double[trainN];
            double sum = 0;
            for (int i = 0; i < trainN; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < trainN; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // Initialise weights (warm-start or zeros)
        double[] w;
        double   bias;
        if (warmStart?.Weights is { Length: > 0 } && warmStart.Weights[0].Length == dim)
        {
            w    = [..warmStart.Weights[0]];
            bias = warmStart.Biases is { Length: > 0 } ? warmStart.Biases[0] : 0.0;
        }
        else
        {
            w    = new double[dim];
            bias = 0.0;
        }

        // Adam moment vectors
        var mW = new double[dim];
        var vW = new double[dim];
        double mB = 0, vB = 0;
        double beta1t = 1.0, beta2t = 1.0;
        int t = 0;

        double bestValLoss = double.MaxValue;
        int    patienceCounter = 0;
        int    earlyStopEpoch = 0; // #25: track early stopping epoch
        double[] bestW = [..w];
        double   bestBias = bias;

        // Soft labels
        var softLabels = new double[trainN];
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;
        for (int i = 0; i < trainN; i++)
            softLabels[i] = labels[i].Direction > 0 ? posLabel : negLabel;

        // Class weights for balanced training
        double rocketCwBuy = 1.0, rocketCwSell = 1.0;
        if (hp.UseClassWeights)
        {
            int bc = 0; for (int ii = 0; ii < trainN; ii++) if (labels[ii].Direction > 0) bc++;
            int sc = trainN - bc;
            if (bc > 0 && sc > 0) { rocketCwBuy = (double)trainN / (2.0 * bc); rocketCwSell = (double)trainN / (2.0 * sc); }
        }

        // Mixup augmentation (post-ROCKET feature space) — re-sampled each epoch
        bool useMixup = hp.MixupAlpha > 0.0;
        double[][]? mixupFeatures = null;
        double[]?   mixupLabels   = null;
        Random? mixRng = useMixup ? new Random(42) : null;
        if (useMixup)
        {
            mixupFeatures = new double[trainN][];
            mixupLabels   = new double[trainN];
        }

        // SWA state
        bool     useSwa      = hp.SwaStartEpoch > 0 && hp.SwaFrequency > 0;
        double[] swaW        = useSwa ? new double[dim] : [];
        double   swaBias     = 0.0;
        int      swaCount    = 0;

        int batchSize = hp.MiniBatchSize > 1 ? hp.MiniBatchSize : DefaultBatchSize;
        var shuffledIdx = new int[trainN];
        for (int i = 0; i < trainN; i++) shuffledIdx[i] = i;
        var shuffleRng = new Random(trainN ^ dim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            // Fisher-Yates shuffle of training indices each epoch
            for (int i = shuffledIdx.Length - 1; i > 0; i--)
            {
                int swapIdx = shuffleRng.Next(i + 1);
                (shuffledIdx[i], shuffledIdx[swapIdx]) = (shuffledIdx[swapIdx], shuffledIdx[i]);
            }

            // Cosine-annealed learning rate with warmup (#4)
            int warmupEpochs = hp.RocketWarmupEpochs;
            double lr = epoch < warmupEpochs
                ? baseLr * (epoch + 1.0) / warmupEpochs
                : baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * (epoch - warmupEpochs) / (maxEpochs - warmupEpochs)));

            // Re-sample mixup partners and lambdas each epoch for maximum diversity
            if (useMixup)
            {
                for (int i = 0; i < trainN; i++)
                {
                    int j2     = mixRng!.Next(trainN);
                    double lam = SampleBeta(mixRng, hp.MixupAlpha);
                    var mixed  = new double[dim];
                    for (int j = 0; j < dim; j++)
                        mixed[j] = lam * features[i][j] + (1.0 - lam) * features[j2][j];
                    mixupFeatures![i] = mixed;
                    mixupLabels![i]   = lam * softLabels[i] + (1.0 - lam) * softLabels[j2];
                }
            }

            // Mini-batched training pass
            for (int bStart = 0; bStart < trainN; bStart += batchSize)
            {
                if (bStart % (batchSize * 20) == 0 && bStart > 0) ct.ThrowIfCancellationRequested();
                int bEnd = Math.Min(bStart + batchSize, trainN);
                int bLen = bEnd - bStart;

                // Accumulate gradients over the mini-batch
                var gW = new double[dim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = shuffledIdx[bi];
                    double[] feat = useMixup ? mixupFeatures![si] : features[si];
                    double   yVal = useMixup ? mixupLabels![si]   : softLabels[si];

                    double logit = bias;
                    for (int j = 0; j < dim; j++) logit += w[j] * feat[j];
                    double p   = MLFeatureHelper.Sigmoid(logit);
                    double rcw = labels[si].Direction > 0 ? rocketCwBuy : rocketCwSell;
                    double err = (p - yVal) * temporalWeights[si] * rcw * trainN;

                    for (int j = 0; j < dim; j++)
                        gW[j] += err * feat[j] + l2 * w[j];
                    gBatch += err;
                }

                // Average gradients over the mini-batch
                double invBLen = 1.0 / bLen;
                for (int j = 0; j < dim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                // #5: Gradient norm clipping
                if (hp.MaxGradNorm > 0)
                {
                    double gradNorm = 0;
                    for (int j = 0; j < dim; j++) gradNorm += gW[j] * gW[j];
                    gradNorm += gBatch * gBatch;
                    gradNorm = Math.Sqrt(gradNorm);
                    if (gradNorm > hp.MaxGradNorm)
                    {
                        double scale = hp.MaxGradNorm / gradNorm;
                        for (int j = 0; j < dim; j++) gW[j] *= scale;
                        gBatch *= scale;
                    }
                }

                // #21: Bias regularization
                if (hp.RocketRegularizeBias)
                    gBatch += l2 * bias;

                // Single Adam step per mini-batch
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                for (int j = 0; j < dim; j++)
                {
                    double g = gW[j];
                    mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * g * g;
                    double mHat = mW[j] / (1 - beta1t);
                    double vHat = vW[j] / (1 - beta2t);
                    w[j] -= lr * mHat / (Math.Sqrt(vHat) + AdamEpsilon);

                    if (hp.MaxWeightMagnitude > 0)
                        w[j] = Math.Clamp(w[j], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                }

                mB = AdamBeta1 * mB + (1 - AdamBeta1) * gBatch;
                vB = AdamBeta2 * vB + (1 - AdamBeta2) * gBatch * gBatch;
                double mBHat = mB / (1 - beta1t);
                double vBHat = vB / (1 - beta2t);
                bias -= lr * mBHat / (Math.Sqrt(vBHat) + AdamEpsilon);
            }

            // SWA accumulation phase
            if (useSwa && epoch >= hp.SwaStartEpoch && (epoch - hp.SwaStartEpoch) % hp.SwaFrequency == 0)
            {
                swaCount++;
                for (int j = 0; j < dim; j++)
                    swaW[j] += (w[j] - swaW[j]) / swaCount;
                swaBias += (bias - swaBias) / swaCount;
            }

            // Validation loss (cross-entropy)
            double valLoss = 0;
            for (int i = trainN; i < n; i++)
            {
                double logit = bias;
                for (int j = 0; j < dim; j++) logit += w[j] * features[i][j];
                double p = MLFeatureHelper.Sigmoid(logit);
                double y = labels[i].Direction > 0 ? 1.0 : 0.0;
                valLoss += -(y * Math.Log(p + 1e-10) + (1 - y) * Math.Log(1 - p + 1e-10));
            }
            valLoss /= (n - trainN);

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, dim);
                bestBias = bias;
                patienceCounter = 0;
                earlyStopEpoch = epoch; // #25: track best epoch
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        // Use SWA-averaged weights if accumulated enough checkpoints
        if (useSwa && swaCount >= 3)
        {
            // Validate SWA weights are better than best early-stopped weights
            double swaValLoss = 0;
            for (int i = trainN; i < n; i++)
            {
                double logit = swaBias;
                for (int j = 0; j < dim; j++) logit += swaW[j] * features[i][j];
                double p = MLFeatureHelper.Sigmoid(logit);
                double y = labels[i].Direction > 0 ? 1.0 : 0.0;
                swaValLoss += -(y * Math.Log(p + 1e-10) + (1 - y) * Math.Log(1 - p + 1e-10));
            }
            swaValLoss /= (n - trainN);

            if (swaValLoss <= bestValLoss)
            {
                Array.Copy(swaW, bestW, dim);
                bestBias = swaBias;
            }
        }

        return (bestW, bestBias, earlyStopEpoch, bestValLoss, swaCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Walk-forward cross-validation
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  numKernels,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < 50)
        {
            _logger.LogWarning("ROCKET walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[] foldResults;
        int cvKernelSeed = HashCode.Combine(samples.Count, featureCount, numKernels, samples[0].Direction);
        var rng = new Random(cvKernelSeed);
        var (kWeights, kDilations, kPaddings, kLengths, kChStarts, kChEnds) =
            GenerateKernels(numKernels, featureCount, rng, hp.RocketUseMiniWeights, hp.RocketMultivariate);

        // #8: Generate fold definitions (CPCV or expanding-window)
        var foldDefs = new List<(int TrainEnd, int TestStart, int TestEnd)>();
        if (hp.RocketUseCpcv)
        {
            // Combinatorial purged CV: all pairs of consecutive blocks
            int numBlocks = folds + 1;
            for (int a = 0; a < numBlocks; a++)
            for (int b = a + 1; b < numBlocks; b++)
            {
                int tStart = b * foldSize;
                int tEnd   = Math.Min((b + 1) * foldSize, samples.Count);
                // Train = everything except the test block, with purging
                int trnEnd = Math.Max(0, tStart - embargo - (MLFeatureHelper.LookbackWindow - 1));
                if (trnEnd >= hp.MinSamples && tEnd - tStart >= 20)
                    foldDefs.Add((trnEnd, tStart, tEnd));
            }
            if (foldDefs.Count == 0) // fallback to standard
                for (int f = 0; f < folds; f++)
                    foldDefs.Add((Math.Max(0, (f + 2) * foldSize - foldSize - embargo - (MLFeatureHelper.LookbackWindow - 1)),
                        (f + 2) * foldSize - foldSize, (f + 2) * foldSize));
        }
        else
        {
            for (int f = 0; f < folds; f++)
            {
                int tEnd = (f + 2) * foldSize;
                int tStart = tEnd - foldSize;
                int trnEnd = Math.Max(0, tStart - embargo - (MLFeatureHelper.LookbackWindow - 1));
                foldDefs.Add((trnEnd, tStart, tEnd));
            }
        }

        int actualFolds = foldDefs.Count;
        foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[actualFolds];

        Parallel.For(0, actualFolds, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = hp.RocketDeterministicParallel ? 1 : -1
        }, fold =>
        {
            var (trainEnd, testStart, testEnd) = foldDefs[fold];

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

            // Extract ROCKET features for this fold
            var foldTrainRocket = ExtractRocketFeatures(foldTrain, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            var foldTestRocket  = ExtractRocketFeatures(foldTest, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);

            int dim = 2 * numKernels;
            var (rm, rs) = ComputeRocketStandardization(foldTrainRocket, dim);
            StandardizeRocketInPlace(foldTrainRocket, rm, rs, dim);
            StandardizeRocketInPlace(foldTestRocket, rm, rs, dim);

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(50, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5, hp.EarlyStoppingPatience / 2),
            };

            // #9: Split fold train into 80% train / 20% cal for per-fold Platt
            int foldCalStart = (int)(foldTrain.Count * 0.80);
            var foldTrainActual = foldTrain[..foldCalStart];
            var foldCal = foldTrain[foldCalStart..];

            var foldTrainActualRocket = ExtractRocketFeatures(foldTrainActual, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            var foldCalRocket = foldTrainRocket[foldCalStart..];
            foldTrainRocket = foldTrainRocket[..foldCalStart];

            var (rmT, rsT) = ComputeRocketStandardization(foldTrainActualRocket, dim);
            StandardizeRocketInPlace(foldTrainActualRocket, rmT, rsT, dim);
            StandardizeRocketInPlace(foldCalRocket, rmT, rsT, dim);
            // Re-standardize test with train-actual stats
            foldTestRocket = ExtractRocketFeatures(foldTest, kWeights, kDilations, kPaddings, kLengths, numKernels, kChStarts, kChEnds);
            StandardizeRocketInPlace(foldTestRocket, rmT, rsT, dim);

            var (w, b, _, _, _) = TrainRidgeAdam(foldTrainActualRocket, foldTrainActual, dim, cvHp, null, null, ct);

            // Fit Platt scaling on fold calibration slice
            double foldPlattA = 1.0, foldPlattB = 0.0;
            if (foldCal.Count >= 5)
                (foldPlattA, foldPlattB) = FitPlattScaling(foldCalRocket, foldCal, w, b, dim);

            var (mw, mb) = FitLinearRegressor(foldTrainActual, featureCount, ct);
            var m = EvaluateModel(foldTestRocket, foldTest, w, b, mw, mb, foldPlattA, foldPlattB, dim, featureCount);

            // Feature importance proxy: mean absolute weight contribution per original feature
            var foldImp = new double[featureCount];
            // Each kernel operates on the feature vector; approximate importance by
            // aggregating weight magnitudes across kernels that touch each feature position
            for (int j = 0; j < featureCount; j++)
            {
                double sumAbs = 0;
                for (int k = 0; k < numKernels; k++)
                {
                    int len = kLengths[k];
                    int dil = kDilations[k];
                    bool pad = kPaddings[k];
                    int padding = pad ? (len - 1) * dil / 2 : 0;

                    for (int li = 0; li < len; li++)
                    {
                        int srcIdx = j + li * dil - padding;
                        if (srcIdx == j)
                            sumAbs += Math.Abs(w[k]) * Math.Abs(kWeights[k][li]);
                    }
                }
                foldImp[j] = sumAbs;
            }
            double impSum = 0;
            for (int j = 0; j < featureCount; j++) impSum += foldImp[j];
            if (impSum > 1e-10)
                for (int j = 0; j < featureCount; j++) foldImp[j] /= impSum;

            // Equity-curve gate
            var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double logit = b;
                for (int j = 0; j < dim; j++) logit += w[j] * foldTestRocket[pi][j];
                double rawP = MLFeatureHelper.Sigmoid(logit);
                foldPredictions[pi] = (rawP >= 0.5 ? 1 : -1,
                                       foldTest[pi].Direction > 0 ? 1 : -1);
            }

            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown) isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe) isBadFold = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        // Aggregate
        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
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

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "ROCKET equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "ROCKET Sharpe trend gate: slope={Slope:F3} < threshold. Model rejected.", sharpeTrend);
            equityCurveGateFailed = true;
        }

        // Feature stability scores
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < featureCount; j++)
            {
                double sumImp = 0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp = 0;
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
            AvgAccuracy:           avgAcc,
            StdAccuracy:           stdAcc,
            AvgF1:                 f1List.Average(),
            AvgEV:                 evList.Average(),
            AvgSharpe:             sharpeList.Average(),
            FoldCount:             accList.Count,
            SharpeTrend:           sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Model probability computation
    // ═══════════════════════════════════════════════════════════════════════════

    private static double RocketProb(double[] rocketFeatures, double[] w, double bias, int dim)
    {
        double logit = bias;
        for (int j = 0; j < dim; j++) logit += w[j] * rocketFeatures[j];
        return MLFeatureHelper.Sigmoid(logit);
    }

    private static double CalibratedProb(
        double[] rocketFeatures, double[] w, double bias, double plattA, double plattB, int dim)
    {
        double raw = RocketProb(rocketFeatures, w, bias, dim);
        raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
        return MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
    }

    /// <summary>#28: Overload accepting <see cref="RocketModelParams"/>.</summary>
    private static double CalibratedProb(double[] rocketFeatures, in RocketModelParams p)
        => CalibratedProb(rocketFeatures, p.W, p.Bias, p.PlattA, p.PlattB, p.Dim);

    /// <summary>#28: Overload accepting <see cref="RocketModelParams"/>.</summary>
    private static double RocketProb(double[] rocketFeatures, in RocketModelParams p)
        => RocketProb(rocketFeatures, p.W, p.Bias, p.Dim);

    // ═══════════════════════════════════════════════════════════════════════════
    //  Platt scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        int n = calRocket.Count;
        if (n < 5) return (1.0, 0.0);

        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = RocketProb(calRocket[i], w, bias, dim);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double lr = 0.01;
        const int epochs = 200;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err    = calibP - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            plattA -= lr * dA / n;
            plattB -= lr * dB / n;
        }

        return (plattA, plattB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Class-conditional Platt scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();
        const int epochs = 200;

        for (int i = 0; i < calRocket.Count; i++)
        {
            double raw = RocketProb(calRocket[i], w, bias, dim);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(raw);
            double y     = calSet[i].Direction > 0 ? 1.0 : 0.0;
            if (raw >= 0.5) buySamples.Add((logit, y));
            else            sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs, int ep)
        {
            if (pairs.Count < 5) return (1.0, 0.0); // identity on logit scale
            double a = 1.0, b = 0.0;
            for (int e = 0; e < ep; e++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                a -= 0.01 * dA / pairs.Count;
                b -= 0.01 * dB / pairs.Count;
            }
            return (a, b);
        }

        var (aBuy, bBuy)   = FitSgd(buySamples, epochs);
        var (aSell, bSell) = FitSgd(sellSamples, epochs);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Model evaluation
    // ═══════════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateModel(
        List<double[]>       rocketFeatures,
        List<TrainingSample> samples,
        double[]             w, double bias,
        double[]             magWeights, double magBias,
        double               plattA, double plattB,
        int                  dim, int featureCount,
        double[]?            rocketMagW = null, double rocketMagB = 0.0)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double sumBrier = 0, sumMagSqErr = 0, sumEV = 0;
        var retBuf = new double[samples.Count];
        int retCount = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            double calibP = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            bool predictedUp = calibP >= 0.5;
            bool actualUp    = samples[i].Direction == 1;
            bool isCorrect   = predictedUp == actualUp;

            double y = actualUp ? 1.0 : 0.0;
            sumBrier += (calibP - y) * (calibP - y);

            double magPred = featureCount <= magWeights.Length
                ? MLFeatureHelper.DotProduct(magWeights, samples[i].Features) + magBias
                : 0;

            // #14: Ensemble with ROCKET-space magnitude prediction
            if (rocketMagW is { Length: > 0 })
            {
                double rocketMagPred = rocketMagB;
                int rLen = Math.Min(rocketMagW.Length, rocketFeatures[i].Length);
                for (int j = 0; j < rLen; j++)
                    rocketMagPred += rocketMagW[j] * rocketFeatures[i][j];
                magPred = (magPred + rocketMagPred) * 0.5;
            }

            double magErr = magPred - samples[i].Magnitude;
            sumMagSqErr += magErr * magErr;

            double edge = calibP - 0.5;
            sumEV += (isCorrect ? 1 : -1) * Math.Abs(edge) * Math.Abs(samples[i].Magnitude);

            int predDir = predictedUp ? 1 : -1;
            int actDir  = actualUp    ? 1 : -1;
            retBuf[retCount++] = predDir * actDir * Math.Abs(samples[i].Magnitude);

            if (isCorrect) correct++;
            if (predictedUp && actualUp)   tp++;
            if (predictedUp && !actualUp)  fp++;
            if (!predictedUp && actualUp)  fn++;
            if (!predictedUp && !actualUp) tn++;
        }

        int    evalN     = samples.Count;
        double accuracy  = evalN > 0 ? (double)correct / evalN : 0;
        double brier     = evalN > 0 ? sumBrier / evalN : 1;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = evalN > 0 ? sumEV / evalN : 0;
        double magRmse   = evalN > 0 ? Math.Sqrt(sumMagSqErr / evalN) : 0;

        // Sharpe ratio from directional returns
        double retMean = 0;
        for (int i = 0; i < retCount; i++) retMean += retBuf[i];
        retMean /= retCount > 0 ? retCount : 1;
        double retVar = 0;
        for (int i = 0; i < retCount; i++)
        {
            double d = retBuf[i] - retMean;
            retVar += d * d;
        }
        double retStd = retCount > 1 ? Math.Sqrt(retVar / (retCount - 1)) : 1.0;
        double sharpe = retStd > 1e-10 ? retMean / retStd * Math.Sqrt(252) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: accuracy, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ECE (Expected Calibration Error)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Ece, double[]? BinConf, double[]? BinAcc, int[]? BinCounts) ComputeEce(
        List<double[]> rocketFeatures, List<TrainingSample> samples,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        const int NumBins = 10;
        var binConfSum = new double[NumBins];
        var binCorrect = new int[NumBins];
        var binCount   = new int[NumBins];

        for (int i = 0; i < samples.Count; i++)
        {
            double p   = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            int    bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);
            binConfSum[bin] += p;
            if (samples[i].Direction == 1) binCorrect[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = samples.Count;
        var binConf = new double[NumBins];
        var binAcc  = new double[NumBins];
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double acc     = binCorrect[b] / (double)binCount[b];
            binConf[b] = avgConf;
            binAcc[b]  = acc;
            ece += Math.Abs(acc - avgConf) * binCount[b] / n;
        }

        return (ece, binConf, binAcc, binCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  EV-optimal threshold
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeOptimalThreshold(
        List<double[]> rocketFeatures, List<TrainingSample> samples,
        double[] w, double bias, double plattA, double plattB, int dim,
        int searchMin, int searchMax)
    {
        int n = samples.Count;
        var probs = new double[n];
        for (int i = 0; i < n; i++)
            probs[i] = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);

        double bestEv = double.MinValue;
        double bestThreshold = 0.5;

        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;
            for (int i = 0; i < n; i++)
            {
                bool predictedUp = probs[i] >= t;
                bool actualUp    = samples[i].Direction == 1;
                bool correct     = predictedUp == actualUp;
                ev += (correct ? 1 : -1) * Math.Abs(samples[i].Magnitude);
            }
            ev /= n;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }

        return bestThreshold;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Permutation feature importance (on original features)
    // ═══════════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<double[]>       testRocket,
        double[]             w, double bias, double plattA, double plattB, int dim,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int featureCount,
        double[] rocketMeans, double[] rocketStds,
        CancellationToken ct, bool deterministic = false)
    {
        // Baseline accuracy
        int baseCorrect = 0;
        for (int i = 0; i < testSet.Count; i++)
        {
            double p = CalibratedProb(testRocket[i], w, bias, plattA, plattB, dim);
            if ((p >= 0.5) == (testSet[i].Direction == 1)) baseCorrect++;
        }
        double baseline = (double)baseCorrect / testSet.Count;

        var importance = new float[featureCount];
        int tn = testSet.Count;
        const int numRuns = 3;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = deterministic ? 1 : -1 }, j =>
        {
            if (ct.IsCancellationRequested) return;

            double totalDrop = 0;
            for (int run = 0; run < numRuns; run++)
            {
                if (ct.IsCancellationRequested) return;

                var shuffleRng = new Random(j * 71 + 13 + run * 997);
                var indices = Enumerable.Range(0, tn).ToArray();
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int swap = shuffleRng.Next(i + 1);
                    (indices[i], indices[swap]) = (indices[swap], indices[i]);
                }

                int correct = 0;
                for (int i = 0; i < tn; i++)
                {
                    // Create shuffled feature vector
                    var scratch = new float[testSet[i].Features.Length];
                    Array.Copy(testSet[i].Features, scratch, scratch.Length);
                    scratch[j] = testSet[indices[i]].Features[j];

                    // Re-extract ROCKET features for this shuffled sample
                    var shuffledSample = new List<TrainingSample>(1) { new(scratch, testSet[i].Direction, testSet[i].Magnitude) };
                    var shuffledRocket = ExtractRocketFeatures(shuffledSample, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
                    // Standardize
                    for (int d = 0; d < dim; d++)
                        shuffledRocket[0][d] = (shuffledRocket[0][d] - rocketMeans[d]) / rocketStds[d];

                    double p = CalibratedProb(shuffledRocket[0], w, bias, plattA, plattB, dim);
                    if ((p >= 0.5) == (testSet[i].Direction == 1)) correct++;
                }
                double shuffledAcc = (double)correct / tn;
                totalDrop += Math.Max(0, baseline - shuffledAcc);
            }
            importance[j] = (float)(totalDrop / numRuns);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Isotonic calibration (PAVA)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        int n = calRocket.Count;
        if (n < 5) return [];

        var pairs = new (double P, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double p = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            pairs[i] = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        var stack = new List<(double SumY, double SumP, int Count)>(n);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var last = stack[^1];
                var prev = stack[^2];
                if (prev.SumY / prev.Count > last.SumY / last.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prev.SumY + last.SumY,
                                 prev.SumP + last.SumP,
                                 prev.Count + last.Count);
                }
                else break;
            }
        }

        var breakpoints = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            breakpoints[i * 2]     = stack[i].SumP / stack[i].Count;
            breakpoints[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }

        return breakpoints;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Conformal prediction threshold
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB,
        double[] isotonicBp, int dim, double alpha)
    {
        var scores = new List<double>(calRocket.Count);
        for (int i = 0; i < calRocket.Count; i++)
        {
            double p = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            if (isotonicBp.Length >= 4) p = ApplyIsotonicCalibration(p, isotonicBp);
            scores.Add(calSet[i].Direction > 0 ? 1.0 - p : p);
        }

        scores.Sort();
        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    private static double ApplyIsotonicCalibration(double p, double[] bp)
    {
        int count = bp.Length / 2;
        if (count < 2) return p;

        if (p <= bp[0]) return bp[1];
        if (p >= bp[(count - 1) * 2]) return bp[(count - 1) * 2 + 1];

        for (int i = 0; i < count - 1; i++)
        {
            double x0 = bp[i * 2],     y0 = bp[i * 2 + 1];
            double x1 = bp[(i + 1) * 2], y1 = bp[(i + 1) * 2 + 1];
            if (p >= x0 && p <= x1)
            {
                double frac = (x1 - x0) > 1e-10 ? (p - x0) / (x1 - x0) : 0;
                return y0 + frac * (y1 - y0);
            }
        }

        return p;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Meta-label secondary classifier
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim, int featureCount,
        CancellationToken ct = default)
    {
        int n = calRocket.Count;
        if (n < 10) return ([], 0.0);

        // 80/20 train/val split for early stopping
        int metaTrainN = (int)(n * 0.80);
        int metaValN   = n - metaTrainN;
        if (metaTrainN < 5) return ([], 0.0);

        // Features: [calibP, top-5 original feature values]
        int metaDim = 6;
        var metaW = new double[metaDim];
        double metaB = 0;

        // Adam state (#3: shared helper)
        var metaAdam = AdamState.Create(metaDim);

        int fLimit = Math.Min(5, featureCount);
        const double baseLr = 0.01;
        const int maxEpochs = 100;
        const int patience = 10;
        int batchSize = Math.Min(DefaultBatchSize, metaTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestMetaW = new double[metaDim];
        double bestMetaB = 0;

        var idx = new int[metaTrainN];
        for (int i = 0; i < metaTrainN; i++) idx[i] = i;
        var rng = new Random(metaTrainN ^ metaDim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training
            for (int bStart = 0; bStart < metaTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, metaTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[metaDim];
                double gB = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double rawP = RocketProb(calRocket[si], w, bias, dim);
                    bool predictedUp = rawP >= 0.5;
                    bool actualUp    = calSet[si].Direction == 1;
                    double metaLabel = predictedUp == actualUp ? 1.0 : 0.0;

                    double z = metaB + metaW[0] * rawP;
                    for (int j = 0; j < fLimit; j++)
                        z += metaW[j + 1] * calSet[si].Features[j];

                    double metaP = MLFeatureHelper.Sigmoid(z);
                    double err   = metaP - metaLabel;

                    gW[0] += err * rawP;
                    for (int j = 0; j < fLimit; j++)
                        gW[j + 1] += err * calSet[si].Features[j];
                    gB += err;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < metaDim; j++) gW[j] *= invBLen;
                gB *= invBLen;

                AdamState.AdamStep(ref metaAdam, gW, gB, metaW, ref metaB, lr, metaDim);
            }

            // Validation loss
            double valLoss = 0;
            for (int i = metaTrainN; i < n; i++)
            {
                double rawP = RocketProb(calRocket[i], w, bias, dim);
                bool predictedUp = rawP >= 0.5;
                bool actualUp    = calSet[i].Direction == 1;
                double metaLabel = predictedUp == actualUp ? 1.0 : 0.0;

                double z = metaB + metaW[0] * rawP;
                for (int j = 0; j < fLimit; j++)
                    z += metaW[j + 1] * calSet[i].Features[j];
                double metaP = MLFeatureHelper.Sigmoid(z);
                valLoss += -(metaLabel * Math.Log(metaP + 1e-10) + (1 - metaLabel) * Math.Log(1 - metaP + 1e-10));
            }
            valLoss /= metaValN;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(metaW, bestMetaW, metaDim);
                bestMetaB = metaB;
                patienceCounter = 0;
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        return (bestMetaW, bestMetaB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Abstention gate
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB,
        double[] metaLabelW, double metaLabelB, int dim,
        CancellationToken ct = default,
        int numKernels = 0)
    {
        int n = calRocket.Count;
        if (n < 10) return ([], 0.0, 0.5);

        // 80/20 train/val split for early stopping
        int absTrainN = (int)(n * 0.80);
        int absValN   = n - absTrainN;
        if (absTrainN < 5) return ([], 0.0, 0.5);

        // Features: [calibP, |calibP - 0.5|, metaLabelScore, kernelEntropy, |magPred|] (#13)
        int absDim = 5;
        var absW = new double[absDim];
        double absB = 0;

        // Adam state (#3: shared helper)
        var absAdam = AdamState.Create(absDim);

        const double baseLr = 0.01;
        const int maxEpochs = 100;
        const int patience = 10;
        int batchSize = Math.Min(DefaultBatchSize, absTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestAbsW = new double[absDim];
        double bestAbsB = 0;

        var idx = new int[absTrainN];
        for (int i = 0; i < absTrainN; i++) idx[i] = i;
        var rng = new Random(absTrainN ^ absDim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training
            for (int bStart = 0; bStart < absTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, absTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[absDim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double calibP = CalibratedProb(calRocket[si], w, bias, plattA, plattB, dim);
                    bool predictedUp = calibP >= 0.5;
                    bool actualUp    = calSet[si].Direction == 1;
                    double label = predictedUp == actualUp ? 1.0 : 0.0;

                    double metaScore = 0;
                    if (metaLabelW.Length > 0)
                    {
                        double rawP = RocketProb(calRocket[si], w, bias, dim);
                        metaScore = metaLabelB + metaLabelW[0] * rawP;
                    }

                    // #13: 5 features — kernel PPV entropy + |logit| as magnitude confidence
                    double kernelEntropy = 0;
                    if (numKernels > 0)
                    {
                        for (int ki = 0; ki < Math.Min(numKernels, calRocket[si].Length - numKernels); ki++)
                        {
                            double ppv = Math.Clamp(calRocket[si][numKernels + ki], 1e-7, 1.0 - 1e-7);
                            kernelEntropy += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                        }
                        kernelEntropy /= numKernels;
                    }
                    double magConf = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                    double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropy, magConf];
                    double z = absB;
                    for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                    double p   = MLFeatureHelper.Sigmoid(z);
                    double err = p - label;

                    for (int j = 0; j < absDim; j++)
                        gW[j] += err * feat[j];
                    gBatch += err;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < absDim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref absAdam, gW, gBatch, absW, ref absB, lr, absDim);
            }

            // Validation loss
            double valLoss = 0;
            for (int i = absTrainN; i < n; i++)
            {
                double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
                bool predictedUp = calibP >= 0.5;
                bool actualUp    = calSet[i].Direction == 1;
                double label = predictedUp == actualUp ? 1.0 : 0.0;

                double metaScore = 0;
                if (metaLabelW.Length > 0)
                {
                    double rawP = RocketProb(calRocket[i], w, bias, dim);
                    metaScore = metaLabelB + metaLabelW[0] * rawP;
                }

                double kernelEntropyI = 0;
                if (numKernels > 0)
                {
                    for (int ki = 0; ki < Math.Min(numKernels, calRocket[i].Length - numKernels); ki++)
                    {
                        double ppv = Math.Clamp(calRocket[i][numKernels + ki], 1e-7, 1.0 - 1e-7);
                        kernelEntropyI += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                    }
                    kernelEntropyI /= numKernels;
                }
                double magConfI = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropyI, magConfI];
                double z = absB;
                for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                double p = MLFeatureHelper.Sigmoid(z);
                valLoss += -(label * Math.Log(p + 1e-10) + (1 - label) * Math.Log(1 - p + 1e-10));
            }
            valLoss /= absValN;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(absW, bestAbsW, absDim);
                bestAbsB = absB;
                patienceCounter = 0;
            }
            else
            {
                patienceCounter++;
                if (patienceCounter >= patience) break;
            }
        }

        absW = bestAbsW;
        absB = bestAbsB;

        // Sweep threshold for best accuracy on cal set
        double bestAcc = 0, bestThr = 0.5;
        for (int ti = 30; ti <= 70; ti++)
        {
            double thr = ti / 100.0;
            int correct = 0, total = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
                double metaScore = 0;
                if (metaLabelW.Length > 0)
                {
                    double rawP = RocketProb(calRocket[i], w, bias, dim);
                    metaScore = metaLabelB + metaLabelW[0] * rawP;
                }
                double kernelEntropyI = 0;
                if (numKernels > 0)
                {
                    for (int ki = 0; ki < Math.Min(numKernels, calRocket[i].Length - numKernels); ki++)
                    {
                        double ppv = Math.Clamp(calRocket[i][numKernels + ki], 1e-7, 1.0 - 1e-7);
                        kernelEntropyI += -(ppv * Math.Log(ppv) + (1.0 - ppv) * Math.Log(1.0 - ppv));
                    }
                    kernelEntropyI /= numKernels;
                }
                double magConfI = Math.Abs(MLFeatureHelper.Logit(Math.Clamp(calibP, 1e-7, 1.0 - 1e-7)));
                double[] feat = [calibP, Math.Abs(calibP - 0.5), metaScore, kernelEntropyI, magConfI];
                double z = absB;
                for (int j = 0; j < absDim; j++) z += absW[j] * feat[j];
                double absP = MLFeatureHelper.Sigmoid(z);
                if (absP >= thr)
                {
                    total++;
                    if ((calibP >= 0.5) == (calSet[i].Direction == 1)) correct++;
                }
            }
            double acc = total > 0 ? (double)correct / total : 0;
            if (acc > bestAcc) { bestAcc = acc; bestThr = thr; }
        }

        return (absW, absB, bestThr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Magnitude regressor (linear OLS)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet, int featureCount,
        CancellationToken ct = default)
    {
        int n = trainSet.Count;
        if (n < 5) return (new double[featureCount], 0.0);

        // 90/10 train/val split for early stopping
        int magTrainN = (int)(n * 0.90);
        int magValN   = n - magTrainN;
        if (magTrainN < 5) magTrainN = n;

        var w = new double[featureCount];
        double b = 0;

        // Adam state (#3: shared helper)
        var linAdam = AdamState.Create(featureCount);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        const double huberDelta = 1.0;
        int batchSize = Math.Min(DefaultBatchSize, magTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[featureCount];
        double bestB = 0;

        var idx = new int[magTrainN];
        for (int i = 0; i < magTrainN; i++) idx[i] = i;
        var rng = new Random(magTrainN ^ featureCount);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training with Huber loss
            for (int bStart = 0; bStart < magTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, magTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[featureCount];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[si].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[si].Features[j];
                    double residual = pred - trainSet[si].Magnitude;

                    // Huber loss gradient
                    double grad;
                    if (Math.Abs(residual) <= huberDelta)
                        grad = residual;
                    else
                        grad = huberDelta * Math.Sign(residual);

                    for (int j = 0; j < fLen; j++)
                        gW[j] += grad * trainSet[si].Features[j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < featureCount; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref linAdam, gW, gBatch, w, ref b, lr, featureCount);
            }

            // Validation loss (Huber)
            if (magValN > 0)
            {
                double valLoss = 0;
                for (int i = magTrainN; i < n; i++)
                {
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[i].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[i].Features[j];
                    double residual = Math.Abs(pred - trainSet[i].Magnitude);
                    valLoss += residual <= huberDelta
                        ? 0.5 * residual * residual
                        : huberDelta * (residual - 0.5 * huberDelta);
                }
                valLoss /= magValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, featureCount);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (magValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Quantile magnitude regressor (pinball loss)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet, int featureCount, double tau,
        CancellationToken ct = default)
    {
        int n = trainSet.Count;
        if (n < 5) return (new double[featureCount], 0.0);

        // 90/10 train/val split for early stopping
        int qTrainN = (int)(n * 0.90);
        int qValN   = n - qTrainN;
        if (qTrainN < 5) qTrainN = n;

        var w = new double[featureCount];
        double b = 0;

        // Adam state (#3: shared helper)
        var qAdam = AdamState.Create(featureCount);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        int batchSize = Math.Min(DefaultBatchSize, qTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[featureCount];
        double bestB = 0;

        var idx = new int[qTrainN];
        for (int i = 0; i < qTrainN; i++) idx[i] = i;
        var rng = new Random(qTrainN ^ featureCount ^ (int)(tau * 1000));

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            // Fisher-Yates shuffle
            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            // Mini-batched Adam training with pinball loss
            for (int bStart = 0; bStart < qTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, qTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[featureCount];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[si].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[si].Features[j];
                    double err = trainSet[si].Magnitude - pred;
                    double grad = err >= 0 ? -tau : (1.0 - tau);

                    for (int j = 0; j < fLen; j++)
                        gW[j] += grad * trainSet[si].Features[j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < featureCount; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref qAdam, gW, gBatch, w, ref b, lr, featureCount);
            }

            // Validation loss (pinball)
            if (qValN > 0)
            {
                double valLoss = 0;
                for (int i = qTrainN; i < n; i++)
                {
                    double pred = b;
                    int fLen = Math.Min(featureCount, trainSet[i].Features.Length);
                    for (int j = 0; j < fLen; j++)
                        pred += w[j] * trainSet[i].Features[j];
                    double err = trainSet[i].Magnitude - pred;
                    valLoss += err >= 0 ? tau * err : -(1.0 - tau) * err;
                }
                valLoss /= qValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, featureCount);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (qValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROCKET-space magnitude regressor (#14)
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) TrainAdamRegressor(
        List<double[]> rocketFeatures, List<TrainingSample> labels, int dim,
        CancellationToken ct = default)
    {
        int n = rocketFeatures.Count;
        if (n < 5) return (new double[dim], 0.0);

        int magTrainN = (int)(n * 0.90);
        int magValN   = n - magTrainN;
        if (magTrainN < 5) magTrainN = n;

        var w = new double[dim];
        double b = 0;
        var adam = AdamState.Create(dim);

        const double baseLr = 0.01;
        const int maxEpochs = 200;
        const int patience = 15;
        const double huberDelta = 1.0;
        int batchSize = Math.Min(DefaultBatchSize, magTrainN);

        double bestValLoss = double.MaxValue;
        int patienceCounter = 0;
        var bestW = new double[dim];
        double bestB = 0;

        var idx = new int[magTrainN];
        for (int i = 0; i < magTrainN; i++) idx[i] = i;
        var rng = new Random(magTrainN ^ dim);

        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / maxEpochs));

            for (int i = idx.Length - 1; i > 0; i--)
            {
                int sw = rng.Next(i + 1);
                (idx[i], idx[sw]) = (idx[sw], idx[i]);
            }

            for (int bStart = 0; bStart < magTrainN; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, magTrainN);
                int bLen = bEnd - bStart;
                var gW = new double[dim];
                double gBatch = 0;

                for (int bi = bStart; bi < bEnd; bi++)
                {
                    int si = idx[bi];
                    double pred = b;
                    for (int j = 0; j < dim; j++)
                        pred += w[j] * rocketFeatures[si][j];
                    double residual = pred - labels[si].Magnitude;
                    double grad = Math.Abs(residual) <= huberDelta ? residual : huberDelta * Math.Sign(residual);

                    for (int j = 0; j < dim; j++)
                        gW[j] += grad * rocketFeatures[si][j];
                    gBatch += grad;
                }

                double invBLen = 1.0 / bLen;
                for (int j = 0; j < dim; j++) gW[j] *= invBLen;
                gBatch *= invBLen;

                AdamState.AdamStep(ref adam, gW, gBatch, w, ref b, lr, dim);
            }

            if (magValN > 0)
            {
                double valLoss = 0;
                for (int i = magTrainN; i < n; i++)
                {
                    double pred = b;
                    for (int j = 0; j < dim; j++)
                        pred += w[j] * rocketFeatures[i][j];
                    double residual = Math.Abs(pred - labels[i].Magnitude);
                    valLoss += residual <= huberDelta
                        ? 0.5 * residual * residual
                        : huberDelta * (residual - 0.5 * huberDelta);
                }
                valLoss /= magValN;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    Array.Copy(w, bestW, dim);
                    bestB = b;
                    patienceCounter = 0;
                }
                else
                {
                    patienceCounter++;
                    if (patienceCounter >= patience) break;
                }
            }
        }

        if (magValN > 0 && bestValLoss < double.MaxValue)
            return (bestW, bestB);
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Decision boundary distance statistics
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<double[]> calRocket, double[] w, double bias, int dim)
    {
        double wNorm = 0;
        for (int j = 0; j < dim; j++) wNorm += w[j] * w[j];
        wNorm = Math.Sqrt(wNorm);
        if (wNorm < 1e-10) return (0, 0);

        var norms = new double[calRocket.Count];
        for (int i = 0; i < calRocket.Count; i++)
        {
            double p = RocketProb(calRocket[i], w, bias, dim);
            norms[i] = p * (1.0 - p) * wNorm;
        }

        double mean = norms.Average();
        double variance = 0;
        for (int i = 0; i < norms.Length; i++)
        {
            double d = norms[i] - mean;
            variance += d * d;
        }
        double std = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0;
        return (mean, std);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Durbin-Watson autocorrelation test
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet, double[] magW, double magB, int featureCount)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magB;
            for (int j = 0; j < featureCount && j < trainSet[i].Features.Length && j < magW.Length; j++)
                pred += magW[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumSqDiff = 0, sumSqRes = 0;
        for (int i = 0; i < residuals.Length; i++)
        {
            sumSqRes += residuals[i] * residuals[i];
            if (i > 0)
            {
                double diff = residuals[i] - residuals[i - 1];
                sumSqDiff += diff * diff;
            }
        }

        return sumSqRes > 1e-10 ? sumSqDiff / sumSqRes : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Temperature scaling
    // ═══════════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, int dim)
    {
        int n = calRocket.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double rawP = RocketProb(calRocket[i], w, bias, dim);
            rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(rawP);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        // Gradient descent on T to minimise NLL
        double T = 1.0;
        const double lr = 0.01;
        const int steps = 100;

        for (int step = 0; step < steps; step++)
        {
            double gradT = 0;
            for (int i = 0; i < n; i++)
            {
                double scaled = logits[i] / T;
                double calibP = MLFeatureHelper.Sigmoid(scaled);
                // dNLL/dT = sum_i (calibP_i - y_i) * (-logit_i / T^2)
                gradT += (calibP - labels[i]) * (-logits[i] / (T * T));
            }
            gradT /= n;
            T -= lr * gradT;
            T = Math.Clamp(T, 0.1, 10.0);
        }

        return T;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Brier Skill Score
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeBrierSkillScore(
        List<double[]> rocketFeatures, List<TrainingSample> testSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        double sumBrier = 0;
        int buyCount = 0;

        for (int i = 0; i < testSet.Count; i++)
        {
            double calibP = CalibratedProb(rocketFeatures[i], w, bias, plattA, plattB, dim);
            double y      = testSet[i].Direction > 0 ? 1.0 : 0.0;
            double diff   = calibP - y;
            sumBrier += diff * diff;
            if (testSet[i].Direction > 0) buyCount++;
        }

        int    n          = testSet.Count;
        double brierModel = sumBrier / n;
        double pBase      = (double)buyCount / n;
        double brierNaive = pBase * (1.0 - pBase);

        return brierNaive < 1e-10 ? 0.0 : 1.0 - brierModel / brierNaive;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Average Kelly fraction
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ComputeAvgKellyFraction(
        List<double[]> calRocket, List<TrainingSample> calSet,
        double[] w, double bias, double plattA, double plattB, int dim)
    {
        double sum = 0;
        for (int i = 0; i < calRocket.Count; i++)
        {
            double calibP = CalibratedProb(calRocket[i], w, bias, plattA, plattB, dim);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return calRocket.Count > 0 ? sum / calRocket.Count * 0.5 : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Mutual-information feature redundancy
    // ═══════════════════════════════════════════════════════════════════════════

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int featureCount, double threshold)
    {
        if (trainSet.Count < 20 || featureCount < 2) return [];

        const int NumBins = 10;
        int topN = Math.Min(10, featureCount);
        var pairs = new List<string>();

        for (int a = 0; a < topN; a++)
        {
            float minA = float.MaxValue, maxA = float.MinValue;
            foreach (var s in trainSet)
            {
                if (s.Features[a] < minA) minA = s.Features[a];
                if (s.Features[a] > maxA) maxA = s.Features[a];
            }
            float rangeA = maxA - minA;
            if (rangeA < 1e-6f) continue;

            for (int b = a + 1; b < topN; b++)
            {
                float minB = float.MaxValue, maxB = float.MinValue;
                foreach (var s in trainSet)
                {
                    if (s.Features[b] < minB) minB = s.Features[b];
                    if (s.Features[b] > maxB) maxB = s.Features[b];
                }
                float rangeB = maxB - minB;
                if (rangeB < 1e-6f) continue;

                var joint = new int[NumBins, NumBins];
                var margA = new int[NumBins];
                var margB = new int[NumBins];
                int n = trainSet.Count;

                foreach (var s in trainSet)
                {
                    int binA = Math.Clamp((int)((s.Features[a] - minA) / rangeA * NumBins), 0, NumBins - 1);
                    int binB = Math.Clamp((int)((s.Features[b] - minB) / rangeB * NumBins), 0, NumBins - 1);
                    joint[binA, binB]++;
                    margA[binA]++;
                    margB[binB]++;
                }

                double mi = 0;
                for (int ia = 0; ia < NumBins; ia++)
                for (int ib = 0; ib < NumBins; ib++)
                {
                    if (joint[ia, ib] == 0) continue;
                    double pJoint = (double)joint[ia, ib] / n;
                    double pA = (double)margA[ia] / n;
                    double pB = (double)margB[ib] / n;
                    mi += pJoint * Math.Log(pJoint / (pA * pB + 1e-15));
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    pairs.Add($"{nameA}:{nameB}");
                }
            }
        }

        return [.. pairs];
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stationarity gate helper
    // ═══════════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        int nonStationary = 0;
        int n = samples.Count;
        if (n < 30) return 0;

        for (int j = 0; j < featureCount; j++)
        {
            // Simple Dickey-Fuller proxy: compute AR(1) coefficient
            double sumXY = 0, sumXX = 0;
            for (int i = 1; i < n; i++)
            {
                double x = samples[i - 1].Features[j];
                double y = samples[i].Features[j];
                sumXY += x * y;
                sumXX += x * x;
            }
            double rho = sumXX > 1e-10 ? sumXY / sumXX : 0;
            if (Math.Abs(rho) > 0.95) nonStationary++;
        }

        return nonStationary;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Density-ratio importance weights
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int featureCount, int windowDays)
    {
        int n = trainSet.Count;
        int recentN = Math.Min(n / 4, windowDays * 24);
        if (recentN < 10) return Enumerable.Repeat(1.0 / n, n).ToArray();

        int splitIdx = n - recentN;

        // Train a simple logistic discriminator: recent (1) vs historical (0)
        var w = new double[featureCount];
        double b = 0;

        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < n; i++)
            {
                double label = i >= splitIdx ? 1.0 : 0.0;
                double logit = b;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    logit += w[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(logit);
                double err = p - label;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    w[j] -= 0.01 * err * trainSet[i].Features[j] / n;
                b -= 0.01 * err / n;
            }
        }

        var weights = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double logit = b;
            for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                logit += w[j] * trainSet[i].Features[j];
            double p = MLFeatureHelper.Sigmoid(logit);
            p = Math.Clamp(p, 0.01, 0.99);
            weights[i] = p / (1.0 - p);
            sum += weights[i];
        }

        if (sum > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Covariate shift weights from parent model
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBreakpoints, int featureCount)
    {
        int n = trainSet.Count;
        var weights = new double[n];
        Array.Fill(weights, 1.0);

        int usableFeatures = Math.Min(featureCount, parentBreakpoints.Length);
        if (usableFeatures == 0) return weights;

        for (int i = 0; i < n; i++)
        {
            double novelty = 0;
            for (int j = 0; j < usableFeatures; j++)
            {
                double val = trainSet[i].Features[j];
                var bp = parentBreakpoints[j];
                if (bp.Length == 0) continue;

                // Count which bin the value falls into; extreme bins get higher novelty
                int bin = 0;
                while (bin < bp.Length && val > bp[bin]) bin++;
                double binFrac = (double)bin / (bp.Length + 1);
                novelty += Math.Abs(binFrac - 0.5);
            }
            weights[i] = 1.0 + novelty / usableFeatures;
        }

        double sum = 0;
        for (int i = 0; i < n; i++) sum += weights[i];
        if (sum > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Equity-curve statistics
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);

        double equity = 0, peak = 0, maxDD = 0;
        var returns = new double[predictions.Length];

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == predictions[i].Actual ? 1.0 : -1.0;
            returns[i] = ret;
            equity += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 0
                ? (peak - equity) / peak
                : (equity < 0 ? -equity / predictions.Length : 0);
            if (dd > maxDD) maxDD = dd;
        }

        double mean = 0;
        for (int i = 0; i < returns.Length; i++) mean += returns[i];
        mean /= returns.Length;
        double var_ = 0;
        for (int i = 0; i < returns.Length; i++)
        {
            double d = returns[i] - mean;
            var_ += d * d;
        }
        double std = returns.Length > 1 ? Math.Sqrt(var_ / (returns.Length - 1)) : 1;
        double sharpe = std > 1e-10 ? mean / std : 0;

        return (maxDD, sharpe);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Temporal weights
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        var weights = new double[count];
        if (lambda <= 0 || count == 0)
        {
            double uniform = count > 0 ? 1.0 / count : 1.0;
            Array.Fill(weights, uniform);
            return weights;
        }

        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            weights[i] = Math.Exp(lambda * i / count);
            sum += weights[i];
        }
        for (int i = 0; i < count; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Shared Adam helper (#3)
    // ═══════════════════════════════════════════════════════════════════════════

    private struct AdamState
    {
        public double[] MW;
        public double[] VW;
        public double MB;
        public double VB;
        public double Beta1T;
        public double Beta2T;
        public int T;

        public static AdamState Create(int dim)
            => new()
            {
                MW = new double[dim],
                VW = new double[dim],
                MB = 0, VB = 0,
                Beta1T = 1.0, Beta2T = 1.0,
                T = 0,
            };

        public static void AdamStep(
            ref AdamState state, double[] gradW, double gradB,
            double[] weights, ref double bias, double lr, int dim)
        {
            state.T++;
            state.Beta1T *= AdamBeta1;
            state.Beta2T *= AdamBeta2;

            for (int j = 0; j < dim; j++)
            {
                double g = gradW[j];
                state.MW[j] = AdamBeta1 * state.MW[j] + (1 - AdamBeta1) * g;
                state.VW[j] = AdamBeta2 * state.VW[j] + (1 - AdamBeta2) * g * g;
                double mHat = state.MW[j] / (1 - state.Beta1T);
                double vHat = state.VW[j] / (1 - state.Beta2T);
                weights[j] -= lr * mHat / (Math.Sqrt(vHat) + AdamEpsilon);
            }

            state.MB = AdamBeta1 * state.MB + (1 - AdamBeta1) * gradB;
            state.VB = AdamBeta2 * state.VB + (1 - AdamBeta2) * gradB * gradB;
            double mBHat = state.MB / (1 - state.Beta1T);
            double vBHat = state.VB / (1 - state.Beta2T);
            bias -= lr * mBHat / (Math.Sqrt(vBHat) + AdamEpsilon);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Statistical helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double d = values[i] - mean;
            sum += d * d;
        }
        return Math.Sqrt(sum / (values.Count - 1));
    }

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0.0;

        int n = sharpePerFold.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += i;
            sumY  += sharpePerFold[i];
            sumXY += i * sharpePerFold[i];
            sumXX += i * i;
        }

        double denom = n * sumXX - sumX * sumX;
        return denom > 1e-10 ? (n * sumXY - sumX * sumY) / denom : 0.0;
    }

    private static double SampleGaussian(Random rng)
    {
        double u1 = Math.Max(1e-10, rng.NextDouble());
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Feature pruning helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++)
            mask[j] = j < importance.Length && importance[j] >= threshold;

        // Ensure at least 10 features remain active
        int active = mask.Count(m => m);
        if (active < 10)
        {
            Array.Fill(mask, true);
        }

        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var f = new float[s.Features.Length];
            for (int j = 0; j < f.Length; j++)
                f[j] = j < mask.Length && mask[j] ? s.Features[j] : 0f;
            result.Add(s with { Features = f });
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Calibration-set permutation importance (for warm-start transfer)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<double[]>       calRocket,
        double[]             w, double bias, int dim,
        double[][] kernelWeights, int[] kernelDilations, bool[] kernelPaddings, int[] kernelLengthArr,
        int numKernels, int featureCount,
        double[] rocketMeans, double[] rocketStds,
        CancellationToken ct, bool deterministic = false)
    {
        // Baseline accuracy on cal set
        int baseCorrect = 0;
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = RocketProb(calRocket[i], w, bias, dim);
            if ((p >= 0.5) == (calSet[i].Direction == 1)) baseCorrect++;
        }
        double baseline = (double)baseCorrect / calSet.Count;

        var importance = new double[featureCount];
        int m = calSet.Count;
        const int numRuns = 3;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = deterministic ? 1 : -1 }, j =>
        {
            double totalDrop = 0;
            for (int run = 0; run < numRuns; run++)
            {
                var shuffleRng = new Random(j * 71 + 17 + run * 997);
                var indices = Enumerable.Range(0, m).ToArray();
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int swap = shuffleRng.Next(i + 1);
                    (indices[i], indices[swap]) = (indices[swap], indices[i]);
                }

                int correct = 0;
                for (int i = 0; i < m; i++)
                {
                    var scratch = new float[calSet[i].Features.Length];
                    Array.Copy(calSet[i].Features, scratch, scratch.Length);
                    scratch[j] = calSet[indices[i]].Features[j];

                    var shuffledSample = new List<TrainingSample>(1) { new(scratch, calSet[i].Direction, calSet[i].Magnitude) };
                    var shuffledRocket = ExtractRocketFeatures(shuffledSample, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
                    for (int d = 0; d < dim; d++)
                        shuffledRocket[0][d] = (shuffledRocket[0][d] - rocketMeans[d]) / rocketStds[d];

                    double p = RocketProb(shuffledRocket[0], w, bias, dim);
                    if ((p >= 0.5) == (calSet[i].Direction == 1)) correct++;
                }
                double shuffledAcc = (double)correct / m;
                totalDrop += Math.Max(0.0, baseline - shuffledAcc);
            }
            importance[j] = totalDrop / numRuns;
        });

        double total = importance.Sum();
        if (total > 1e-10)
            for (int j = 0; j < featureCount; j++) importance[j] /= total;

        return importance;
    }

    /// <summary>Samples from Gamma(shape, 1) using the Marsaglia-Tsang method.</summary>
    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            double u = rng.NextDouble();
            return SampleGamma(rng, shape + 1.0) * Math.Pow(u, 1.0 / shape);
        }

        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = SampleGaussian(rng);
                v = 1.0 + c * x;
            } while (v <= 0);

            v = v * v * v;
            double u2 = rng.NextDouble();
            if (u2 < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v;
            if (Math.Log(u2) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }

    /// <summary>Samples from Beta(alpha, alpha) for Mixup augmentation.</summary>
    private static double SampleBeta(Random rng, double alpha)
    {
        double x = SampleGamma(rng, alpha);
        double y = SampleGamma(rng, alpha);
        double sum = x + y;
        return sum > 1e-15 ? x / sum : 0.5;
    }
}
