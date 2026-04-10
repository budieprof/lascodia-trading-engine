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
///   <item>60 / 10 / 10 / 20 train / selection / calibration / test splits with embargo gaps.</item>
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
public sealed partial class RocketModelTrainer : IMLModelTrainer
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

        // ── 3. Final model splits: 60% train | 10% selection | 10% cal | ~20% test ──
        int n            = allStd.Count;
        int trainEnd     = (int)(n * 0.60);
        int selectionEnd = (int)(n * 0.70);
        int calEnd       = (int)(n * 0.80);
        int embargo      = hp.EmbargoBarCount;

        var trainSet     = allStd[..Math.Max(0, trainEnd - embargo)];
        int selStart     = Math.Min(trainEnd + embargo, selectionEnd);
        var selectionSet = allStd[selStart..selectionEnd];
        var calSet       = allStd[Math.Min(selectionEnd + embargo, calEnd)..Math.Min(calEnd, n)];
        var testSet      = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        if (testSet.Count < 5)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient test samples after splits: {testSet.Count} < 5");

        if (calSet.Count < 5)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient calibration samples after splits: {calSet.Count} < 5");

        if (selectionSet.Count < 5)
            throw new InvalidOperationException(
                $"RocketModelTrainer: insufficient selection samples after splits: {selectionSet.Count} < 5");

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 3b. Multi-signal drift gate ─────────────────────────────────────
        var driftArtifact = ComputeRocketDriftDiagnostics(trainSet, featureCount, MLFeatureHelper.FeatureNames, hp.FracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ROCKET drift gate rejected: {driftArtifact.NonStationaryFeatureCount}/{featureCount} features flagged.");
            _logger.LogWarning("ROCKET stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, featureCount);
        }

        // ── 3b-ii. Class-imbalance gate ──────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"ROCKET: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("ROCKET class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3b-iii. Adversarial validation ───────────────────────────────────
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, featureCount, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, featureCount);
            _logger.LogInformation("ROCKET adversarial AUC={AUC:F3}", advAuc);
            if (advAuc > 0.65) _logger.LogWarning("ROCKET adversarial AUC={AUC:F3} indicates covariate shift.", advAuc);
            if (hp.RocketMaxAdversarialAuc > 0.0 && advAuc > hp.RocketMaxAdversarialAuc)
                throw new InvalidOperationException($"ROCKET: adversarial AUC={advAuc:F3} exceeds threshold.");
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
        var trainRocket     = ExtractRocketFeatures(trainSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
        var selectionRocket = ExtractRocketFeatures(selectionSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
        var calRocket       = ExtractRocketFeatures(calSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
        var testRocket      = ExtractRocketFeatures(testSet, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);

        ct.ThrowIfCancellationRequested();

        int dim = 2 * numKernels;

        // ── 5b. Z-score ROCKET features ──────────────────────────────────────
        var (rocketMeans, rocketStds) = ComputeRocketStandardization(trainRocket, dim);
        StandardizeRocketInPlace(trainRocket, rocketMeans, rocketStds, dim);
        StandardizeRocketInPlace(selectionRocket, rocketMeans, rocketStds, dim);
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

        // ── 11. EV-optimal decision threshold (on selection set) ─────────────
        double optimalThreshold = ComputeOptimalThreshold(
            selectionRocket, selectionSet, rw, rb, plattA, plattB, dim,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("ROCKET EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 12. Permutation feature importance (on selection set) ────────────
        var featureImportance = selectionSet.Count >= 10
            ? ComputePermutationImportance(selectionSet, selectionRocket, rw, rb, plattA, plattB, dim,
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

            var maskedTrain     = ApplyMask(trainSet, activeMask);
            var maskedSelection = ApplyMask(selectionSet, activeMask);
            var maskedCal       = ApplyMask(calSet, activeMask);
            var maskedTest      = ApplyMask(testSet, activeMask);

            // Re-extract ROCKET features on masked data
            var maskedTrainRocket     = ExtractRocketFeatures(maskedTrain, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
            var maskedSelectionRocket = ExtractRocketFeatures(maskedSelection, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
            var maskedCalRocket       = ExtractRocketFeatures(maskedCal, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);
            var maskedTestRocket      = ExtractRocketFeatures(maskedTest, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels);

            var (mrm, mrs) = ComputeRocketStandardization(maskedTrainRocket, dim);
            StandardizeRocketInPlace(maskedTrainRocket, mrm, mrs, dim);
            StandardizeRocketInPlace(maskedSelectionRocket, mrm, mrs, dim);
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
                selectionRocket = maskedSelectionRocket;
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
                optimalThreshold = ComputeOptimalThreshold(selectionRocket, maskedSelection, rw, rb, plattA, plattB, dim,
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

        // ── 22h. New metrics: Murphy, calibration residuals, stability, feature variances ──
        Func<float[], double> calProbFn = features =>
        {
            var singleSample = new List<TrainingSample> { new(features, 0, 0) };
            var rocketFeatList = ExtractRocketFeatures(singleSample, kernelWeights, kernelDilations, kernelPaddings, kernelLengthArr, numKernels, channelStarts, channelEnds);
            StandardizeRocketInPlace(rocketFeatList, rocketMeans, rocketStds, dim);
            return CalibratedProb(rocketFeatList[0], rw, rb, plattA, plattB, dim);
        };

        var (murphyCalLoss, murphyRefLoss) = ComputeMurphyDecomposition(testSet, calProbFn);
        var (calResidualMean, calResidualStd, calResidualThreshold) = ComputeCalibrationResidualStats(calSet, calProbFn);
        double predictionStability = ComputePredictionStabilityScore(testSet, calProbFn);
        double[] featureVariances = ComputeFeatureVariancesRocket(trainSet, featureCount);

        _logger.LogInformation(
            "ROCKET Murphy decomposition: calLoss={CalLoss:F4} refLoss={RefLoss:F4}", murphyCalLoss, murphyRefLoss);
        _logger.LogInformation(
            "ROCKET calibration residuals: mean={Mean:F4} std={Std:F4} threshold={Thr:F4}",
            calResidualMean, calResidualStd, calResidualThreshold);
        _logger.LogInformation("ROCKET prediction stability={Stability:F4}", predictionStability);

        // ── 22i. SafeRocket scalar clamping ──────────────────────────────────
        ece                = SafeRocket(ece);
        brierSkillScore    = SafeRocket(brierSkillScore);
        avgKellyFraction   = SafeRocket(avgKellyFraction);
        optimalThreshold   = SafeRocket(optimalThreshold, 0.5);
        temperatureScale   = SafeRocket(temperatureScale);
        durbinWatson       = SafeRocket(durbinWatson);
        magnitudeR2        = SafeRocket(magnitudeR2);
        meanKernelEntropy  = SafeRocket(meanKernelEntropy);
        oobAccuracy        = SafeRocket(oobAccuracy);
        conformalQHat      = SafeRocket(conformalQHat);
        plattA             = SafeRocket(plattA);
        plattB             = SafeRocket(plattB);
        plattABuy          = SafeRocket(plattABuy);
        plattBBuy          = SafeRocket(plattBBuy);
        plattASell         = SafeRocket(plattASell);
        plattBSell         = SafeRocket(plattBSell);
        dbMean             = SafeRocket(dbMean);
        dbStd              = SafeRocket(dbStd);
        murphyCalLoss      = SafeRocket(murphyCalLoss);
        murphyRefLoss      = SafeRocket(murphyRefLoss);
        calResidualMean    = SafeRocket(calResidualMean);
        calResidualStd     = SafeRocket(calResidualStd);
        calResidualThreshold = SafeRocket(calResidualThreshold, 1.0);
        predictionStability = SafeRocket(predictionStability);

        SanitizeDoubleArr(featureVariances);
        SanitizeFloatArr(featureImportance);
        SanitizeFloatArr(fastFeatureAttribution);

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
            SelectionSamples           = selectionSet.Count,
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
            CalibrationLoss            = murphyCalLoss,
            RefinementLoss             = murphyRefLoss,
            PredictionStabilityScore   = predictionStability,
            FeatureVariances           = featureVariances,
            RocketCalibrationResidualMean      = calResidualMean,
            RocketCalibrationResidualStd       = calResidualStd,
            RocketCalibrationResidualThreshold = calResidualThreshold,
            RocketDriftArtifact        = driftArtifact,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "RocketModelTrainer complete: kernels={K}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            numKernels, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
