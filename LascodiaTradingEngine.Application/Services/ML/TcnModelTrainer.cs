using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade Temporal Convolutional Network (TCN) trainer implementing
/// <see cref="IMLModelTrainer"/>.
/// <para>
/// Architecture: causal dilated 1D convolutions over a per-bar feature sequence.
/// Input is [T, C_in] where T = <see cref="MLFeatureHelper.LookbackWindow"/> (30 timesteps)
/// and C_in = <see cref="MLFeatureHelper.SequenceChannelCount"/> (9 channels).
/// </para>
/// <para>
/// Network topology (configurable via <see cref="TrainingHyperparams"/>):
/// <list type="number">
///   <item>Block 0: CausalConv1D(in=C_in, out=F, kernel=3, dilation=1) + LayerNorm + Activation + Dropout + Residual(1×1)</item>
///   <item>Block 1..N-1: CausalConv1D(in=F, out=F, kernel=3, dilation=2^b) + LayerNorm + Activation + Dropout + Residual(identity)</item>
///   <item>Attention pooling over all timestep hidden states (or last-timestep extraction).</item>
///   <item>Dense(F→2) direction head + Dense(F→1) Huber magnitude head.</item>
/// </list>
/// Default: F=32 filters, N=4 blocks, receptive field = 1 + (3−1)×(1+2+4+8) = 31 ≥ 30 timesteps.
/// </para>
/// <para>
/// Training pipeline:
/// <list type="number">
///   <item>Per-channel Z-score standardisation over all sequence timesteps.</item>
///   <item>Stationarity gate (ADF-based warning for non-stationary channels).</item>
///   <item>Walk-forward cross-validation (expanding window, embargo, purging).</item>
///   <item>Final model: 70% train | 10% Platt calibration | ~18% hold-out test (with embargo).</item>
///   <item>Adaptive label smoothing from training label ambiguity.</item>
///   <item>Adam optimizer (β₁=0.9, β₂=0.999) with cosine-annealing LR + adaptive LR decay.</item>
///   <item>Full backpropagation through causal dilated conv + LayerNorm + residual-projection layers.</item>
///   <item>Mini-batch training, global gradient norm clipping, weight magnitude clipping.</item>
///   <item>Inverted dropout after activation (rate from <c>DropoutRate</c>).</item>
///   <item>Temporal decay weighting (recent samples weighted higher).</item>
///   <item>Early stopping with patience + best-weight checkpointing + SWA (all layers).</item>
///   <item>NaN/Inf weight sanitization per-sample and post-training.</item>
///   <item>Platt scaling + isotonic calibration (PAVA) + temperature scaling.</item>
///   <item>Conformal calibration (qHat) on calibration set.</item>
///   <item>ECE, Brier Skill Score, EV-optimal threshold, Durbin-Watson.</item>
///   <item>Multi-round permutation feature importance.</item>
///   <item>Comprehensive <see cref="ModelSnapshot"/> population for downstream scoring.</item>
///   <item>Optional warm-start and incremental update fast-path.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.TemporalConvNet)]
public sealed class TcnModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "TCN";
    private const string ModelVersion = "6.0";

    private const int DefaultFilters   = 32;
    private const int KernelSize       = 3;
    private const int DefaultNumBlocks = 4;

    private const double DropoutRate             = 0.1;
    private const double DropoutScale            = 1.0 / (1.0 - DropoutRate);
    private const int    PermutationRounds       = 5;

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<TcnModelTrainer> _logger;

    public TcnModelTrainer(ILogger<TcnModelTrainer> logger)
    {
        _logger = logger;
    }

    // ── IMLModelTrainer ──────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);
    }

    // ── Core training logic (synchronous, runs on thread-pool) ───────────────

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
                $"TcnModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        // Resolve configurable architecture params
        int filters   = hp.TcnFilters > 0 ? hp.TcnFilters : DefaultFilters;
        int numBlocks = hp.TcnNumBlocks > 0 ? hp.TcnNumBlocks : DefaultNumBlocks;
        int[] dilations = BuildDilations(numBlocks);
        bool useLayerNorm       = hp.TcnUseLayerNorm;
        bool useAttentionPool   = hp.TcnUseAttentionPooling;
        TcnActivation activation = hp.TcnActivation;
        int attentionHeads = hp.TcnAttentionHeads > 0 ? hp.TcnAttentionHeads : 1;
        if (useAttentionPool && attentionHeads > 1 && filters % attentionHeads != 0)
        {
            _logger.LogWarning("TcnAttentionHeads={Heads} does not divide filters={Filters}; falling back to 1 head",
                attentionHeads, filters);
            attentionHeads = 1;
        }

        int T = MLFeatureHelper.LookbackWindow;
        int C = MLFeatureHelper.SequenceChannelCount;

        // Validate sequence data availability
        bool hasSequence = samples[0].SequenceFeatures is not null;
        if (!hasSequence)
            _logger.LogWarning(
                "TcnModelTrainer: samples lack SequenceFeatures — build samples via " +
                "MLFeatureHelper.BuildTrainingSamplesWithSequence for proper temporal convolution.");

        int F = hasSequence ? C : samples[0].Features.Length;

        _logger.LogInformation(
            "TcnModelTrainer starting: {N} samples, T={T}, C={C}, hasSequence={HasSeq}, epochs={E}, " +
            "filters={Filters}, blocks={Blocks}, activation={Act}, layerNorm={LN}, attentionPool={AP}, " +
            "attnHeads={Heads}, warmupEpochs={Warmup}",
            samples.Count, T, F, hasSequence, hp.MaxEpochs,
            filters, numBlocks, activation, useLayerNorm, useAttentionPool,
            attentionHeads, hp.TcnWarmupEpochs);

        // ── 0. Incremental update fast-path ──────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * 24);
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
                    UseIncrementalUpdate  = false,
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Sequence standardisation ──────────────────────────────────────
        float[] seqMeans, seqStds;
        float[] flatMeans = [], flatStds = [];
        List<TrainingSample> allStd;

        if (hasSequence)
        {
            var allSeqs = new List<float[][]>(samples.Count);
            foreach (var s in samples) allSeqs.Add(s.SequenceFeatures!);
            (seqMeans, seqStds) = MLFeatureHelper.ComputeSequenceStandardization(allSeqs);

            // Also standardise flat features for snapshot compatibility
            var rawFlat = new List<float[]>(samples.Count);
            foreach (var s in samples) rawFlat.Add(s.Features);
            (flatMeans, flatStds) = MLFeatureHelper.ComputeStandardization(rawFlat);

            allStd = new List<TrainingSample>(samples.Count);
            foreach (var s in samples)
                allStd.Add(s with
                {
                    Features         = MLFeatureHelper.Standardize(s.Features, flatMeans, flatStds),
                    SequenceFeatures = MLFeatureHelper.StandardizeSequence(s.SequenceFeatures!, seqMeans, seqStds),
                });
        }
        else
        {
            // Fallback: treat flat features as single-timestep sequence
            var rawFlat = new List<float[]>(samples.Count);
            foreach (var s in samples) rawFlat.Add(s.Features);
            (flatMeans, flatStds) = MLFeatureHelper.ComputeStandardization(rawFlat);
            seqMeans = flatMeans;
            seqStds  = flatStds;

            allStd = new List<TrainingSample>(samples.Count);
            foreach (var s in samples)
            {
                var stdFeatures = MLFeatureHelper.Standardize(s.Features, flatMeans, flatStds);
                // Wrap flat features as single-timestep [1][F] sequence
                allStd.Add(s with
                {
                    Features         = stdFeatures,
                    SequenceFeatures = [stdFeatures],
                });
            }
        }

        // ── 1b. Stationarity gate (soft ADF check on sequence channels) ─────
        {
            int channelCount = hasSequence ? C : F;
            int nonStatCount = CountNonStationaryChannels(allStd, channelCount);
            double nonStatFraction = channelCount > 0 ? (double)nonStatCount / channelCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "Stationarity gate: {NonStat}/{Total} channels have unit root (p>0.05). Consider enabling FracDiffD.",
                    nonStatCount, channelCount);
        }

        // ── 2. Walk-forward cross-validation ─────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(
            allStd, hp, filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads, ct);
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(
                new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70% train | 10% Platt cal | ~18% test ────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                              ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // Reduce epochs for warm-start runs
        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 3b. Density-ratio importance weights ────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays);
            _logger.LogDebug("Density-ratio weights computed (recentWindow={W}d).",
                hp.DensityRatioWindowDays);
        }

        // ── 3c. Covariate shift weight integration (parent model novelty scoring) ──
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
            _logger.LogDebug(
                "Covariate shift weights applied from parent model (generation={Gen}).",
                warmStart.GenerationNumber);
        }

        // ── 3d. Adaptive label smoothing ─────────────────────────────────────
        double adaptiveLabelSmoothing = effectiveHp.LabelSmoothing;
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
                "Adaptive label smoothing: ε={Eps:F3} (ambiguous-proxy fraction={Frac:P1})",
                adaptiveLabelSmoothing, ambiguousFraction);
        }

        // ── 4. Fit TCN model ─────────────────────────────────────────────────
        var tcn = FitTcnModel(trainSet, effectiveHp, warmStart,
            filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads,
            densityWeights, ct);

        // ── 5. Platt calibration on cal set ──────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, tcn, filters, useAttentionPool);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 5a-ii. Class-conditional Platt (Buy / Sell separate scalers) ────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, tcn, filters, useAttentionPool);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 5a-iii. Average Kelly fraction on cal set ───────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(
            calSet, tcn, plattA, plattB, filters, useAttentionPool);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 5b. Isotonic calibration (PAVA) ──────────────────────────────────
        double[] isotonicBp = FitIsotonicCalibration(calSet, tcn, plattA, plattB, filters, useAttentionPool);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 5c. Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calSet, tcn, filters, useAttentionPool);
            _logger.LogDebug("Temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 5d. Conformal calibration (qHat) ────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(calSet, tcn, plattA, plattB, conformalAlpha, filters, useAttentionPool);
        _logger.LogInformation("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 6. Final evaluation on held-out test set ─────────────────────────
        var finalMetrics = Evaluate(testSet, tcn, plattA, plattB, filters, useAttentionPool);

        _logger.LogInformation(
            "Final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 7. ECE ───────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, tcn, plattA, plattB, filters, useAttentionPool);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);

        // ── 8. EV-optimal threshold (tuned on cal set) ───────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, tcn, plattA, plattB,
            hp.ThresholdSearchMin / 100.0, hp.ThresholdSearchMax / 100.0,
            filters, useAttentionPool);
        _logger.LogInformation("EV-optimal threshold={Thr:F2} (default 0.50)", optimalThreshold);

        // ── 9. Permutation feature importance ───────────────────────────────
        int channelCountForImp = tcn.ChannelIn;
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, tcn, plattA, plattB, filters, useAttentionPool, ct)
            : new float[channelCountForImp];

        if (featureImportance.Length >= 5)
        {
            var channelNames = hasSequence
                ? MLFeatureHelper.SequenceChannelNames
                : MLFeatureHelper.FeatureNames;
            var topFeatures = featureImportance
                .Select((imp, idx) => (Importance: imp, Name: idx < channelNames.Length
                    ? channelNames[idx] : $"ch{idx}"))
                .OrderByDescending(x => x.Importance)
                .Take(5);
            _logger.LogInformation(
                "Top 5 channels: {Features}",
                string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        // ── 10. Brier Skill Score ────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, tcn, plattA, plattB, filters, useAttentionPool);
        _logger.LogInformation("Brier Skill Score (BSS)={BSS:F4} (>0 beats naive predictor)", brierSkillScore);

        // ── 10b. Durbin-Watson on magnitude residuals ────────────────────────
        double durbinWatson = ComputeDurbinWatson(testSet, tcn, filters, useAttentionPool);
        _logger.LogDebug("Durbin-Watson statistic={DW:F4}", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}).",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 10c. Calibration-set permutation importance (for warm-start transfer) ──
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, tcn, filters, useAttentionPool, ct)
            : new double[channelCountForImp];

        // ── 10d. Quantile magnitude regressor (pinball loss) ────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau);
            _logger.LogDebug("Quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 10e. Decision boundary gradient stats ───────────────────────────
        var (dbMean, dbStd) = calSet.Count >= 10
            ? ComputeDecisionBoundaryStats(calSet, tcn, plattA, plattB, filters, useAttentionPool)
            : (0.0, 0.0);
        _logger.LogDebug("Decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        // ── 10f. Mutual-information feature redundancy check ────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "MI redundancy: {N} feature pairs exceed threshold {T:F2}×log(2): {Pairs}",
                    redundantPairs.Length, hp.MutualInfoRedundancyThreshold,
                    string.Join(", ", redundantPairs));
        }

        // ── 10g. Abstention gate (selective prediction) ─────────────────────
        double[] abstentionWeights = [];
        double   abstentionBias      = 0.0;
        double   abstentionThreshold = 0.5;
        if (calSet.Count >= 10)
        {
            (abstentionWeights, abstentionBias, abstentionThreshold) =
                FitTcnAbstentionModel(calSet, tcn, plattA, plattB, filters, useAttentionPool);
            _logger.LogDebug("Abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);
        }

        // ── 10h. Feature pruning re-train pass ─────────────────────────────
        var activeMask = BuildChannelMask(featureImportance, hp.MinFeatureImportance, channelCountForImp);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && channelCountForImp - prunedCount >= 3)
        {
            _logger.LogInformation(
                "Feature pruning: masking {Pruned}/{Total} low-importance channels",
                prunedCount, channelCountForImp);

            var maskedTrain = ApplySequenceMask(trainSet, activeMask);
            var maskedCal   = ApplySequenceMask(calSet,   activeMask);
            var maskedTest  = ApplySequenceMask(testSet,  activeMask);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            var prunedTcn = FitTcnModel(maskedTrain, prunedHp, null,
                filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads,
                densityWeights, ct);
            var (pA, pB) = FitPlattScaling(maskedCal, prunedTcn, filters, useAttentionPool);
            var prunedMetrics = Evaluate(maskedTest, prunedTcn, pA, pB, filters, useAttentionPool);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                tcn          = prunedTcn;
                plattA       = pA;
                plattB       = pB;
                finalMetrics = prunedMetrics;
                // Re-compute class-conditional Platt and Kelly on pruned model
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(maskedCal, prunedTcn, filters, useAttentionPool);
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, prunedTcn, pA, pB, filters, useAttentionPool);
                ece = ComputeEce(maskedTest, prunedTcn, pA, pB, filters, useAttentionPool);
                optimalThreshold = ComputeOptimalThreshold(
                    maskedCal, prunedTcn, pA, pB,
                    hp.ThresholdSearchMin / 100.0, hp.ThresholdSearchMax / 100.0,
                    filters, useAttentionPool);
            }
            else
            {
                _logger.LogInformation(
                    "Pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask = new bool[channelCountForImp]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[channelCountForImp]; Array.Fill(activeMask, true);
        }

        // ── 11. NaN/Inf weight sanitization ──────────────────────────────────
        int sanitizedCount = SanitizeTcnWeights(tcn);
        if (sanitizedCount > 0)
            _logger.LogWarning("Post-training sanitization: {N} weight arrays had non-finite values.", sanitizedCount);

        // ── 12. PSI baseline (feature quantile breakpoints) ──────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 13. Serialise model snapshot ─────────────────────────────────────
        var tcnWeights = new TcnSnapshotWeights
        {
            ConvW    = tcn.ConvW,
            ConvB    = tcn.ConvB,
            HeadW    = tcn.HeadW,
            HeadB    = tcn.HeadB,
            MagHeadW = tcn.MagHeadW,
            MagHeadB = tcn.MagHeadB,
            ResW     = tcn.ResW,
            ChannelIn  = tcn.ChannelIn,
            TimeSteps  = tcn.TimeSteps,
            Filters    = filters,
            LayerNormGamma = tcn.LayerNormGamma,
            LayerNormBeta  = tcn.LayerNormBeta,
            UseLayerNorm   = useLayerNorm,
            Activation     = (int)activation,
            UseAttentionPooling = useAttentionPool,
            AttentionHeads = attentionHeads,
            AttnQueryW = tcn.AttnQueryW,
            AttnKeyW   = tcn.AttnKeyW,
            AttnValueW = tcn.AttnValueW,
        };

        // Map feature importance back to flat feature space for snapshot compatibility.
        // Sequence channel importances are copied index-for-index into the flat array;
        // flat feature slots beyond the sequence channel count are left at zero.
        var flatFeatureImportance = new float[MLFeatureHelper.FeatureCount];
        int copyLen = Math.Min(featureImportance.Length, flatFeatureImportance.Length);
        for (int i = 0; i < copyLen; i++)
            flatFeatureImportance[i] = featureImportance[i];

        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = flatMeans,
            Stds                       = flatStds,
            BaseLearnersK              = 1,
            Weights                    = [tcn.HeadW.Take(filters).ToArray()],
            Biases                     = [(float)tcn.HeadB[1]],
            MagWeights                 = (double[])tcn.MagHeadW.Clone(),
            MagBias                    = tcn.MagHeadB,
            PlattA                     = plattA,
            PlattB                     = plattB,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            AvgKellyFraction           = avgKellyFraction,
            ActiveFeatureMask          = activeMask,
            FeatureImportanceScores    = calImportanceScores,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = dbMean,
            DecisionBoundaryStd        = dbStd,
            RedundantFeaturePairs      = redundantPairs,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = DateTime.UtcNow,
            TrainedAtUtc               = DateTime.UtcNow,
            FeatureImportance          = flatFeatureImportance,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            BrierSkillScore            = brierSkillScore,
            IsotonicBreakpoints        = isotonicBp,
            TemperatureScale           = temperatureScale,
            ConformalQHat              = conformalQHat,
            DurbinWatsonStatistic      = durbinWatson,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            FracDiffD                  = hp.FracDiffD,
            AgeDecayLambda             = hp.AgeDecayLambda,
            SanitizedLearnerCount      = sanitizedCount,
            ConformalCoverage          = hp.ConformalCoverage,
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            ConvWeightsJson            = JsonSerializer.Serialize(tcnWeights, JsonOptions),
            SeqMeans                   = seqMeans,
            SeqStds                    = seqStds,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        _logger.LogInformation(
            "TcnModelTrainer complete: accuracy={Acc:P1}, Brier={B:F4}, MagRMSE={RMSE:F4}",
            finalMetrics.Accuracy, finalMetrics.BrierScore, finalMetrics.MagnitudeRmse);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ── Walk-forward cross-validation ────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, bool useAttentionPool, TcnActivation activation,
        int attentionHeads,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int channelIn = samples[0].SequenceFeatures?[0].Length ?? samples[0].Features.Length;

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("Walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        // Folds are fully independent — parallelise across available cores.
        // Each fold writes to a distinct foldResults[fold] slot, which is thread-safe for reference arrays.
        var cvHp = hp with
        {
            MaxEpochs             = Math.Max(30, hp.MaxEpochs / 3),
            EarlyStoppingPatience = Math.Max(5, hp.EarlyStoppingPatience / 2),
        };

        // Cap CV parallelism: each fold trains a full TCN (large buffer allocation).
        // Using all cores simultaneously causes severe memory pressure on high core-count machines.
        int cvParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = cvParallelism }, fold =>
        {
            int testEnd    = (fold + 2) * foldSize;
            int testStart  = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("Fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                return;
            }

            var foldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count)
                    foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            var tcn = FitTcnModel(foldTrain, cvHp, null,
                filters, numBlocks, dilations, useLayerNorm, useAttentionPool, activation, attentionHeads, null, ct);
            var m = Evaluate(foldTest, tcn, 1.0, 0.0, filters, useAttentionPool);

            // Aggregate first-block conv weights by input channel to get true channel-level
            // importance. HeadW indices refer to filters, not input channels, so using them
            // directly as a channel proxy was incorrect.
            var foldImp = new double[channelIn];
            for (int ci = 0; ci < channelIn; ci++)
            {
                double imp = 0;
                for (int o = 0; o < filters; o++)
                    for (int k = 0; k < KernelSize; k++)
                        imp += Math.Abs(tcn.ConvW[0][(o * channelIn + ci) * KernelSize + k]);
                foldImp[ci] = imp;
            }

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 || hp.MinFoldCurveSharpe > -99.0)
            {
                var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
                for (int pi = 0; pi < foldTest.Count; pi++)
                {
                    double rawP = TcnProb(foldTest[pi], tcn, filters, useAttentionPool);
                    foldPredictions[pi] = (rawP >= 0.5 ? 1 : -1,  // TcnProb already uses Softmax2P
                                           foldTest[pi].Direction > 0 ? 1 : -1);
                }
                var (maxDD, curveSharpe) = ComputeEquityCurveStats(foldPredictions);
                if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBadFold = true;
                if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBadFold = true;
            }

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        var accList = new List<double>(folds); var f1List = new List<double>(folds);
        var evList = new List<double>(folds); var sharpeList = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc); f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV); sharpeList.Add(r.Value.Sharpe);
            foldImportances.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("Equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc = accList.Average();
        double stdAcc = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("Sharpe trend gate: slope={Slope:F3} < {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[channelIn];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < channelIn; j++)
            {
                double sumImp = 0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp = 0;
                for (int fi = 0; fi < foldCount; fi++)
                { double d = foldImportances[fi][j] - meanImp; varImp += d * d; }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0;
            }
        }

        return (new WalkForwardResult(avgAcc, stdAcc, f1List.Average(), evList.Average(),
            sharpeList.Average(), accList.Count, sharpeTrend, featureStabilityScores), equityCurveGateFailed);
    }

    // ── TCN model fitting (causal dilated 1D convolution) ─────────────────────

    private TcnWeights FitTcnModel(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, bool useAttentionPool, TcnActivation activation,
        int attentionHeads,
        double[]?            densityWeights,
        CancellationToken    ct)
    {
        // Deterministic but dataset-dependent seed for reproducibility.
        // NOTE: The seed is derived from sample count, epoch count, and block count so that
        // identical datasets + hyperparams produce identical results. Dropout masks during
        // training also depend on the sample ordering after Fisher-Yates shuffle, so changing
        // the training set composition will produce different dropout patterns even with the
        // same seed.
        var rng = new Random(HashCode.Combine(train.Count, hp.MaxEpochs, numBlocks));

        // Infer dimensions from sequence data
        int T = train[0].SequenceFeatures!.Length;      // timesteps
        int channelIn = train[0].SequenceFeatures![0].Length; // input channels

        // ── Validation split ─────────────────────────────────────────────────
        int valSize  = Math.Max(20, train.Count / 10);
        var valSet   = train[^valSize..];
        var trainSet = train[..^valSize];

        if (trainSet.Count == 0)
            throw new InvalidOperationException("No training samples after validation split.");

        // ── Temporal decay weights (blended with density-ratio weights) ─────
        double[] temporalWeights = ComputeTemporalWeights(trainSet.Count, hp.TemporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalWeights.Length)
        {
            var blended = new double[temporalWeights.Length];
            double blendSum = 0;
            for (int i = 0; i < blended.Length; i++)
            { blended[i] = temporalWeights[i] * densityWeights[i]; blendSum += blended[i]; }
            double blendMean = blendSum / blended.Length;
            if (blendMean > 1e-15)
                for (int i = 0; i < blended.Length; i++) blended[i] /= blendMean;
            temporalWeights = blended;
        }

        // ── Initialise conv weights ──────────────────────────────────────────
        // Conv kernel shape: [outChannels, inChannels, kernelSize]
        // Stored flat as double[outChannels * inChannels * kernelSize]
        int[] blockInC = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++) blockInC[b] = b == 0 ? channelIn : filters;

        double[][] convW = new double[numBlocks][];
        double[][] convB = new double[numBlocks][];
        double[]?[] resW = new double[numBlocks][];  // 1×1 residual projection [outC, inC]

        // LayerNorm parameters: per-block gamma (scale) and beta (shift), each [filters]
        double[][] lnGamma = new double[numBlocks][];
        double[][] lnBeta  = new double[numBlocks][];

        for (int b = 0; b < numBlocks; b++)
        {
            int inC = blockInC[b];
            // He initialization scaled by fan_in × kernel_size
            convW[b] = InitWeights(filters * inC * KernelSize, rng, Math.Sqrt(2.0 / (inC * KernelSize)));
            convB[b] = new double[filters];
            if (inC != filters)
                resW[b] = InitWeights(filters * inC, rng, Math.Sqrt(2.0 / inC));

            // LayerNorm: gamma=1, beta=0
            lnGamma[b] = new double[filters];
            lnBeta[b]  = new double[filters];
            Array.Fill(lnGamma[b], 1.0);
        }

        double[] headW = InitWeights(2 * filters, rng, Math.Sqrt(2.0 / filters));
        double[] headB = new double[2];
        double[] magHeadW = InitWeights(filters, rng, Math.Sqrt(2.0 / filters));
        double   magHeadB = 0.0;

        // ── Attention pooling weights (query, key, value projections) ────────
        double[] attnQueryW = useAttentionPool
            ? InitWeights(filters * filters, rng, Math.Sqrt(2.0 / filters))
            : [];
        double[] attnKeyW = useAttentionPool
            ? InitWeights(filters * filters, rng, Math.Sqrt(2.0 / filters))
            : [];
        double[] attnValueW = useAttentionPool
            ? InitWeights(filters * filters, rng, Math.Sqrt(2.0 / filters))
            : [];

        // ── Warm-start ───────────────────────────────────────────────────────
        if (warmStart?.ConvWeightsJson != null)
        {
            try
            {
                var prior = JsonSerializer.Deserialize<TcnSnapshotWeights>(warmStart.ConvWeightsJson, JsonOptions);
                if (prior != null)
                {
                    RestoreFromSnapshot(prior, convW, convB, headW, headB, magHeadW, ref magHeadB, resW,
                        lnGamma, lnBeta, attnQueryW, attnKeyW, attnValueW);
                    _logger.LogInformation("TCN warm-started from parent model");
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load TCN warm-start; using cold start"); }
        }

        // ── Adam state ───────────────────────────────────────────────────────
        var pool = ArrayPool<double>.Shared;

        var mConvW = new double[numBlocks][];
        var vConvW = new double[numBlocks][];
        var mConvB = new double[numBlocks][];
        var vConvB = new double[numBlocks][];
        var mResW = new double[numBlocks][];
        var vResW = new double[numBlocks][];
        var mLnG = new double[numBlocks][];
        var vLnG = new double[numBlocks][];
        var mLnB = new double[numBlocks][];
        var vLnB = new double[numBlocks][];

        for (int b = 0; b < numBlocks; b++)
        {
            mConvW[b] = pool.Rent(convW[b].Length); Array.Clear(mConvW[b], 0, convW[b].Length);
            vConvW[b] = pool.Rent(convW[b].Length); Array.Clear(vConvW[b], 0, convW[b].Length);
            mConvB[b] = pool.Rent(filters); Array.Clear(mConvB[b], 0, filters);
            vConvB[b] = pool.Rent(filters); Array.Clear(vConvB[b], 0, filters);
            if (resW[b] != null)
            {
                mResW[b] = pool.Rent(resW[b]!.Length); Array.Clear(mResW[b], 0, resW[b]!.Length);
                vResW[b] = pool.Rent(resW[b]!.Length); Array.Clear(vResW[b], 0, resW[b]!.Length);
            }
            if (useLayerNorm)
            {
                mLnG[b] = pool.Rent(filters); Array.Clear(mLnG[b], 0, filters);
                vLnG[b] = pool.Rent(filters); Array.Clear(vLnG[b], 0, filters);
                mLnB[b] = pool.Rent(filters); Array.Clear(mLnB[b], 0, filters);
                vLnB[b] = pool.Rent(filters); Array.Clear(vLnB[b], 0, filters);
            }
        }

        var mHeadW = pool.Rent(headW.Length); Array.Clear(mHeadW, 0, headW.Length);
        var vHeadW = pool.Rent(headW.Length); Array.Clear(vHeadW, 0, headW.Length);
        double mHeadB0 = 0, vHeadB0 = 0, mHeadB1 = 0, vHeadB1 = 0;

        var mMagW = pool.Rent(filters); Array.Clear(mMagW, 0, filters);
        var vMagW = pool.Rent(filters); Array.Clear(vMagW, 0, filters);
        double mMagB = 0, vMagB = 0;

        // Attention Adam state
        double[] mAttnQW = [], vAttnQW = [], mAttnKW = [], vAttnKW = [], mAttnVW = [], vAttnVW = [];
        if (useAttentionPool)
        {
            int attnSize = filters * filters;
            mAttnQW = pool.Rent(attnSize); Array.Clear(mAttnQW, 0, attnSize);
            vAttnQW = pool.Rent(attnSize); Array.Clear(vAttnQW, 0, attnSize);
            mAttnKW = pool.Rent(attnSize); Array.Clear(mAttnKW, 0, attnSize);
            vAttnKW = pool.Rent(attnSize); Array.Clear(vAttnKW, 0, attnSize);
            mAttnVW = pool.Rent(attnSize); Array.Clear(mAttnVW, 0, attnSize);
            vAttnVW = pool.Rent(attnSize); Array.Clear(vAttnVW, 0, attnSize);
        }

        int    adamT  = 0;
        double bestValLoss = double.MaxValue;
        int    patience    = 0;

        // Best-weight checkpoint
        var bestConvW = DeepCopy2D(convW);
        var bestConvB = DeepCopy2D(convB);
        var bestResW  = DeepCopyNullable2D(resW);
        var bestHeadW = (double[])headW.Clone();
        var bestHeadB = (double[])headB.Clone();
        var bestMagHeadW = (double[])magHeadW.Clone();
        double bestMagHeadB = magHeadB;
        var bestLnGamma = DeepCopy2D(lnGamma);
        var bestLnBeta  = DeepCopy2D(lnBeta);
        var bestAttnQW = (double[])attnQueryW.Clone();
        var bestAttnKW = (double[])attnKeyW.Clone();
        var bestAttnVW = (double[])attnValueW.Clone();

        double labelSmoothing = hp.LabelSmoothing;
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;
        double maxGradNorm = hp.MaxGradNorm > 0.0 ? hp.MaxGradNorm : 5.0;
        double maxWeightMag = hp.MaxWeightMagnitude > 0.0 ? hp.MaxWeightMagnitude : 0.0;
        bool   useMagTask = hp.MagLossWeight > 0.0;
        int    epochs     = hp.MaxEpochs;

        // Mini-batch
        int  batchSize    = Math.Max(1, hp.MiniBatchSize > 0 ? hp.MiniBatchSize : 32);
        bool useMiniBatch = batchSize > 1;

        // SWA state
        bool useSwa = hp.SwaStartEpoch > 0 && hp.SwaFrequency > 0;
        var swaHeadW = useSwa ? new double[headW.Length] : null;
        var swaHeadB = useSwa ? new double[2] : null;
        var swaConvW = useSwa ? DeepCopy2D(convW) : null;
        var swaConvB = useSwa ? DeepCopy2D(convB) : null;
        var swaResW  = useSwa ? DeepCopyNullable2D(resW) : null;
        var swaMagW  = useSwa ? new double[filters] : null;
        double swaMagB = 0;
        var swaLnGamma = useSwa ? DeepCopy2D(lnGamma) : null;
        var swaLnBeta  = useSwa ? DeepCopy2D(lnBeta) : null;
        var swaAttnQW  = useSwa && useAttentionPool ? new double[filters * filters] : null;
        var swaAttnKW  = useSwa && useAttentionPool ? new double[filters * filters] : null;
        var swaAttnVW  = useSwa && useAttentionPool ? new double[filters * filters] : null;
        int swaCount = 0;
        if (useSwa)
        {
            for (int b = 0; b < numBlocks; b++)
            {
                Array.Clear(swaConvW![b]); Array.Clear(swaConvB![b]);
                if (swaResW![b] != null) Array.Clear(swaResW[b]!);
                Array.Clear(swaLnGamma![b]); Array.Clear(swaLnBeta![b]);
            }
        }

        // Adaptive LR decay
        double lrScale    = 1.0;
        double peakValAcc = 0.0;

        // ── Pre-allocated buffers ────────────────────────────────────────────
        // Block activations: [numBlocks+1][T][channels]
        var blockOut = new double[numBlocks + 1][][];
        var preAct   = new double[numBlocks][][];
        // Pre-LayerNorm activations (before normalization)
        var preLnAct = useLayerNorm ? new double[numBlocks][][] : null;
        // LayerNorm cached values for backward pass
        var lnNorm  = useLayerNorm ? new double[numBlocks][][] : null;
        var lnInvStd = useLayerNorm ? new double[numBlocks][] : null;

        blockOut[0] = AllocTimeChannels(T, channelIn);
        for (int b = 0; b < numBlocks; b++)
        {
            blockOut[b + 1] = AllocTimeChannels(T, filters);
            preAct[b]       = AllocTimeChannels(T, filters);
            if (useLayerNorm)
            {
                preLnAct![b] = AllocTimeChannels(T, filters);
                lnNorm![b]   = AllocTimeChannels(T, filters);
                lnInvStd![b] = new double[T];
            }
        }

        // Dropout masks: [numBlocks][T][filters]
        var dropMasks = new double[numBlocks][][];
        for (int b = 0; b < numBlocks; b++) dropMasks[b] = AllocTimeChannels(T, filters);

        // Head gradient buffers
        var dH         = new double[filters];
        var dHeadWGrad = new double[2 * filters];

        // LayerNorm backward buffer: holds per-filter gradient for a single timestep
        var dPreConvBuf = useLayerNorm ? new double[filters] : null;

        // Attention buffers
        var attnScores = useAttentionPool ? new double[T] : null;
        var attnOutput = useAttentionPool ? new double[filters] : null;
        var queryBuf   = useAttentionPool ? new double[filters] : null;
        var keyBuf     = useAttentionPool ? AllocTimeChannels(T, filters) : null;
        var valBuf     = useAttentionPool ? AllocTimeChannels(T, filters) : null;

        // Pre-allocated backward scratch buffers for attention (avoids per-sample heap allocation
        // in the training hot path; these are reused every sample via Array.Clear).
        var attnBwdHeadScores = useAttentionPool ? new double[T] : null;
        var attnBwdDScores    = useAttentionPool ? new double[T] : null;
        var attnBwdDPreScores = useAttentionPool ? new double[T] : null;
        var attnBwdDQueryBuf  = useAttentionPool ? new double[filters] : null;
        // Flat layouts [t * filters + f] replace jagged [seqT][] to avoid inner array allocs.
        var attnBwdDValFlat   = useAttentionPool ? new double[T * filters] : null;
        var attnBwdDKeyFlat   = useAttentionPool ? new double[T * filters] : null;

        // Gradient accumulators for backprop through time
        var dBlockOut = new double[numBlocks + 1][][];
        dBlockOut[0] = AllocTimeChannels(T, channelIn);
        for (int b = 0; b < numBlocks; b++)
            dBlockOut[b + 1] = AllocTimeChannels(T, filters);

        // Mini-batch gradient accumulators
        var batchGradConvW = new double[numBlocks][];
        var batchGradConvB = new double[numBlocks][];
        var batchGradResW  = new double[numBlocks][];
        var batchGradHeadW = new double[headW.Length];
        double batchGradHB0 = 0, batchGradHB1 = 0;
        var batchGradMagW  = new double[filters];
        double batchGradMagB = 0;
        var batchGradLnG = useLayerNorm ? new double[numBlocks][] : null;
        var batchGradLnB = useLayerNorm ? new double[numBlocks][] : null;
        var batchGradAttnQW = useAttentionPool ? new double[filters * filters] : null;
        var batchGradAttnKW = useAttentionPool ? new double[filters * filters] : null;
        var batchGradAttnVW = useAttentionPool ? new double[filters * filters] : null;
        for (int b = 0; b < numBlocks; b++)
        {
            batchGradConvW[b] = new double[convW[b].Length];
            batchGradConvB[b] = new double[filters];
            if (resW[b] != null) batchGradResW[b] = new double[resW[b]!.Length];
            if (useLayerNorm)
            {
                batchGradLnG![b] = new double[filters];
                batchGradLnB![b] = new double[filters];
            }
        }

        // Shuffle index array
        var indices = new int[trainSet.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        // ── Training loop ────────────────────────────────────────────────────
        for (int epoch = 0; epoch < epochs && !ct.IsCancellationRequested; epoch++)
        {
            double epochLoss = 0;
            int warmupEpochs = hp.TcnWarmupEpochs;
            double alpha;
            if (warmupEpochs > 0 && epoch < warmupEpochs)
            {
                // Linear warmup: ramp from 0 to base LR
                alpha = hp.LearningRate * lrScale * ((double)(epoch + 1) / warmupEpochs);
            }
            else
            {
                // Cosine annealing (adjusted for warmup offset)
                int cosineEpoch = epoch - warmupEpochs;
                int cosineTotal = Math.Max(1, epochs - warmupEpochs);
                alpha = hp.LearningRate * lrScale * 0.5 * (1.0 + Math.Cos(Math.PI * cosineEpoch / cosineTotal));
            }

            // Fisher-Yates shuffle
            for (int i = indices.Length - 1; i > 0; i--)
            { int j = rng.Next(i + 1); (indices[i], indices[j]) = (indices[j], indices[i]); }

            // Clear batch accumulators
            ClearBatchGrads(batchGradConvW, batchGradConvB, batchGradResW, batchGradHeadW,
                ref batchGradHB0, ref batchGradHB1, batchGradMagW, ref batchGradMagB,
                resW, batchGradLnG, batchGradLnB, useLayerNorm,
                batchGradAttnQW, batchGradAttnKW, batchGradAttnVW, useAttentionPool);

            for (int si = 0; si < trainSet.Count; si++)
            {
                int sampleIdx = indices[si];
                var s = trainSet[sampleIdx];
                var seq = s.SequenceFeatures!;
                int seqT = seq.Length;

                // Advance Adam timestep per batch boundary
                if (!useMiniBatch || si % batchSize == 0)
                {
                    adamT++;
                }

                double sampleWeight = sampleIdx < temporalWeights.Length ? temporalWeights[sampleIdx] : 1.0;
                double yLabel = s.Direction > 0 ? posLabel : negLabel;

                // ── Forward pass: causal dilated 1D convolution ──────────
                // Copy input sequence into block 0 output
                for (int t = 0; t < seqT; t++)
                    for (int c = 0; c < channelIn; c++)
                        blockOut[0][t][c] = seq[t][c];

                for (int b = 0; b < numBlocks; b++)
                {
                    int inC = blockInC[b];
                    int dilation = dilations[b];

                    for (int t = 0; t < seqT; t++)
                    {
                        for (int o = 0; o < filters; o++)
                        {
                            double sum = convB[b][o];
                            // Causal dilated convolution: kernel positions 0..KernelSize-1
                            for (int k = 0; k < KernelSize; k++)
                            {
                                int srcT = t - k * dilation;
                                if (srcT < 0) continue; // causal zero-padding

                                for (int c = 0; c < inC; c++)
                                    sum += convW[b][(o * inC + c) * KernelSize + k] * blockOut[b][srcT][c];
                            }

                            preAct[b][t][o] = sum;
                        }

                        // ── LayerNorm across filters dimension ───────────
                        if (useLayerNorm)
                        {
                            double mean = 0;
                            for (int o = 0; o < filters; o++) mean += preAct[b][t][o];
                            mean /= filters;
                            double variance = 0;
                            for (int o = 0; o < filters; o++)
                            { double d = preAct[b][t][o] - mean; variance += d * d; }
                            variance /= filters;
                            double invStd = 1.0 / Math.Sqrt(variance + 1e-5);
                            lnInvStd![b][t] = invStd;

                            for (int o = 0; o < filters; o++)
                            {
                                double normalized = (preAct[b][t][o] - mean) * invStd;
                                lnNorm![b][t][o] = normalized;
                                preLnAct![b][t][o] = preAct[b][t][o]; // save for backward
                                preAct[b][t][o] = lnGamma[b][o] * normalized + lnBeta[b][o];
                            }
                        }

                        for (int o = 0; o < filters; o++)
                        {
                            double act = Activate(preAct[b][t][o], activation);

                            // Inverted dropout (training only)
                            bool dropped = rng.NextDouble() < DropoutRate;
                            dropMasks[b][t][o] = dropped ? 0.0 : DropoutScale;
                            act *= dropMasks[b][t][o];

                            // Residual connection
                            if (inC == filters)
                                act += blockOut[b][t][o]; // identity shortcut
                            else if (resW[b] != null)
                            {
                                // 1×1 projection
                                double res = 0;
                                int resOff = o * inC;
                                for (int c = 0; c < inC; c++)
                                    res += resW[b]![resOff + c] * blockOut[b][t][c];
                                act += res;
                            }

                            blockOut[b + 1][t][o] = act;
                        }
                    }
                }

                // ── Pooling: attention or last-timestep ──────────────────
                double[] h;
                if (useAttentionPool)
                {
                    h = AttentionPoolForward(blockOut[numBlocks], seqT, filters,
                        attnQueryW, attnKeyW, attnValueW,
                        queryBuf!, keyBuf!, valBuf!, attnScores!, attnOutput!, attentionHeads);
                }
                else
                {
                    h = blockOut[numBlocks][seqT - 1];
                }

                double logit0 = headB[0], logit1 = headB[1];
                for (int fi = 0; fi < filters; fi++)
                { logit0 += headW[fi] * h[fi]; logit1 += headW[filters + fi] * h[fi]; }
                double[] probs = Softmax2(logit0, logit1);
                double p = Math.Clamp(probs[1], 1e-7, 1 - 1e-7);
                if (!double.IsFinite(p)) continue;

                epochLoss += -(yLabel * Math.Log(p) + (1.0 - yLabel) * Math.Log(1.0 - p));

                // Magnitude head forward (uses shared TCN backbone representation)
                double magPred = magHeadB;
                for (int fi = 0; fi < filters; fi++) magPred += magHeadW[fi] * h[fi];

                // ── Backward pass ────────────────────────────────────────
                double dLogit0 = (probs[0] - (1.0 - yLabel)) * sampleWeight;
                double dLogit1 = (probs[1] - yLabel) * sampleWeight;

                // Gradient for head weights
                Array.Clear(dH, 0, filters);
                for (int fi = 0; fi < filters; fi++)
                {
                    dHeadWGrad[fi]           = dLogit0 * h[fi];
                    dHeadWGrad[filters + fi] = dLogit1 * h[fi];
                    dH[fi] = headW[fi] * dLogit0 + headW[filters + fi] * dLogit1;
                }

                // Magnitude head backward (joint multi-task gradient through shared backbone)
                if (useMagTask)
                {
                    double magErr = magPred - s.Magnitude;
                    double huberGrad = (Math.Abs(magErr) <= 1.0 ? magErr : Math.Sign(magErr))
                                       * hp.MagLossWeight * sampleWeight;

                    for (int fi = 0; fi < filters; fi++)
                    {
                        batchGradMagW[fi] += huberGrad * h[fi];
                        // magHeadW is safe to read here — Adam updates only happen at batch boundaries
                        dH[fi] += huberGrad * magHeadW[fi];
                    }
                    batchGradMagB += huberGrad;
                }

                // Initialise dBlockOut: zero all, set pooling gradient
                for (int b = 0; b <= numBlocks; b++)
                    for (int t = 0; t < seqT; t++)
                        Array.Clear(dBlockOut[b][t], 0, dBlockOut[b][t].Length);

                if (useAttentionPool)
                {
                    // Backprop through attention pooling (scratch buffers pre-allocated above)
                    AttentionPoolBackward(dH, blockOut[numBlocks], seqT, filters,
                        attnQueryW, attnKeyW, attnValueW,
                        queryBuf!, keyBuf!, valBuf!,
                        dBlockOut[numBlocks],
                        batchGradAttnQW!, batchGradAttnKW!, batchGradAttnVW!,
                        attnBwdHeadScores!, attnBwdDScores!, attnBwdDPreScores!,
                        attnBwdDQueryBuf!, attnBwdDValFlat!, attnBwdDKeyFlat!,
                        attentionHeads);
                }
                else
                {
                    for (int fi = 0; fi < filters; fi++)
                        dBlockOut[numBlocks][seqT - 1][fi] = dH[fi];
                }

                // Backprop through TCN blocks (reverse order)
                for (int b = numBlocks - 1; b >= 0; b--)
                {
                    int inC = blockInC[b];
                    int dilation = dilations[b];

                    for (int t = 0; t < seqT; t++)
                    {
                        // Phase 1: Compute gradients through residual, dropout, activation
                        // for all filters at this timestep
                        for (int o = 0; o < filters; o++)
                        {
                            double dOut = dBlockOut[b + 1][t][o];

                            // Residual backward: identity or 1×1 projection
                            if (inC == filters)
                            {
                                dBlockOut[b][t][o] += dOut;
                            }
                            else if (resW[b] != null)
                            {
                                int resOff = o * inC;
                                for (int c = 0; c < inC; c++)
                                {
                                    dBlockOut[b][t][c] += dOut * resW[b]![resOff + c];
                                    batchGradResW[b][resOff + c] += dOut * blockOut[b][t][c];
                                }
                            }

                            // Through dropout mask and activation derivative
                            double actDeriv = ActivateDerivative(preAct[b][t][o], activation);
                            double dPreActivation = actDeriv != 0.0
                                ? dOut * dropMasks[b][t][o] * actDeriv
                                : 0;

                            if (useLayerNorm)
                                dPreConvBuf![o] = dPreActivation;
                            else
                            {
                                // No LayerNorm: propagate directly to conv gradients
                                if (dPreActivation == 0) continue;

                                batchGradConvB[b][o] += dPreActivation;
                                for (int k = 0; k < KernelSize; k++)
                                {
                                    int srcT = t - k * dilation;
                                    if (srcT < 0) continue;
                                    for (int c = 0; c < inC; c++)
                                    {
                                        int wIdx = (o * inC + c) * KernelSize + k;
                                        batchGradConvW[b][wIdx] += dPreActivation * blockOut[b][srcT][c];
                                        dBlockOut[b][srcT][c] += dPreActivation * convW[b][wIdx];
                                    }
                                }
                            }
                        }

                        // Phase 2: Full LayerNorm backward (requires all filters' gradients)
                        if (useLayerNorm)
                        {
                            // dPreConvBuf[o] = d_loss/d_postLN[o] (gradient at LayerNorm output)
                            // postLN[o] = gamma[o] * normalized[o] + beta[o]
                            // Accumulate gamma/beta gradients and compute d_loss/d_normalized
                            double sumDNorm = 0, sumDNormTimesNorm = 0;
                            for (int o = 0; o < filters; o++)
                            {
                                batchGradLnG![b][o] += dPreConvBuf![o] * lnNorm![b][t][o];
                                batchGradLnB![b][o] += dPreConvBuf[o];
                                dPreConvBuf[o] *= lnGamma[b][o]; // now d_loss/d_normalized[o]
                                sumDNorm += dPreConvBuf[o];
                                sumDNormTimesNorm += dPreConvBuf[o] * lnNorm![b][t][o];
                            }

                            // Full LayerNorm backward:
                            // dx[o] = invStd/N * (N*dNorm[o] - sum(dNorm) - norm[o]*sum(dNorm*norm))
                            double invStd = lnInvStd![b][t];
                            double invN = 1.0 / filters;
                            for (int o = 0; o < filters; o++)
                            {
                                double dPre = invStd * (dPreConvBuf![o]
                                    - invN * (sumDNorm + lnNorm![b][t][o] * sumDNormTimesNorm));

                                if (dPre == 0) continue;

                                // Phase 3: Conv gradients (with correct LayerNorm gradient)
                                batchGradConvB[b][o] += dPre;
                                for (int k = 0; k < KernelSize; k++)
                                {
                                    int srcT = t - k * dilation;
                                    if (srcT < 0) continue;
                                    for (int c = 0; c < inC; c++)
                                    {
                                        int wIdx = (o * inC + c) * KernelSize + k;
                                        batchGradConvW[b][wIdx] += dPre * blockOut[b][srcT][c];
                                        dBlockOut[b][srcT][c] += dPre * convW[b][wIdx];
                                    }
                                }
                            }
                        }
                    }
                }

                // Accumulate head gradients
                for (int fi = 0; fi < headW.Length; fi++)
                    batchGradHeadW[fi] += dHeadWGrad[fi];
                batchGradHB0 += dLogit0;
                batchGradHB1 += dLogit1;

                // ── Apply gradients at batch boundary ────────────────────
                bool isBatchEnd = (si + 1) % batchSize == 0 || si == trainSet.Count - 1;
                if (!isBatchEnd) continue;

                int actualBatch = useMiniBatch ? (si % batchSize) + 1 : 1;
                double invBatch = 1.0 / actualBatch;
                double bc1    = 1.0 - Math.Pow(AdamBeta1, adamT);
                double bc2    = 1.0 - Math.Pow(AdamBeta2, adamT);
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                // Global gradient norm clipping
                double gnormSq = batchGradHB0 * batchGradHB0 * invBatch * invBatch
                               + batchGradHB1 * batchGradHB1 * invBatch * invBatch;
                for (int fi = 0; fi < headW.Length; fi++)
                    gnormSq += batchGradHeadW[fi] * invBatch * batchGradHeadW[fi] * invBatch;
                for (int b = 0; b < numBlocks; b++)
                {
                    for (int wi = 0; wi < convW[b].Length; wi++)
                        gnormSq += batchGradConvW[b][wi] * invBatch * batchGradConvW[b][wi] * invBatch;
                    for (int o = 0; o < filters; o++)
                        gnormSq += batchGradConvB[b][o] * invBatch * batchGradConvB[b][o] * invBatch;
                    if (resW[b] != null)
                        for (int wi = 0; wi < resW[b]!.Length; wi++)
                            gnormSq += batchGradResW[b][wi] * invBatch * batchGradResW[b][wi] * invBatch;
                    if (useLayerNorm)
                    {
                        for (int o = 0; o < filters; o++)
                        {
                            gnormSq += batchGradLnG![b][o] * invBatch * batchGradLnG[b][o] * invBatch;
                            gnormSq += batchGradLnB![b][o] * invBatch * batchGradLnB[b][o] * invBatch;
                        }
                    }
                }
                if (useMagTask)
                {
                    for (int fi = 0; fi < filters; fi++)
                        gnormSq += batchGradMagW[fi] * invBatch * batchGradMagW[fi] * invBatch;
                    gnormSq += batchGradMagB * invBatch * batchGradMagB * invBatch;
                }
                if (useAttentionPool)
                {
                    for (int wi = 0; wi < attnQueryW.Length; wi++)
                    {
                        gnormSq += batchGradAttnQW![wi] * invBatch * batchGradAttnQW[wi] * invBatch;
                        gnormSq += batchGradAttnKW![wi] * invBatch * batchGradAttnKW[wi] * invBatch;
                        gnormSq += batchGradAttnVW![wi] * invBatch * batchGradAttnVW[wi] * invBatch;
                    }
                }
                double gnorm = Math.Sqrt(gnormSq);
                double clipScale = gnorm > maxGradNorm ? maxGradNorm / gnorm : 1.0;

                // Head Adam update
                for (int fi = 0; fi < headW.Length; fi++)
                {
                    double grad = batchGradHeadW[fi] * invBatch * clipScale;
                    mHeadW[fi] = AdamBeta1 * mHeadW[fi] + (1 - AdamBeta1) * grad;
                    vHeadW[fi] = AdamBeta2 * vHeadW[fi] + (1 - AdamBeta2) * grad * grad;
                    headW[fi] -= alphAt * mHeadW[fi] / (Math.Sqrt(vHeadW[fi]) + AdamEpsilon);
                }
                {
                    double g0 = batchGradHB0 * invBatch * clipScale;
                    mHeadB0 = AdamBeta1 * mHeadB0 + (1 - AdamBeta1) * g0;
                    vHeadB0 = AdamBeta2 * vHeadB0 + (1 - AdamBeta2) * g0 * g0;
                    headB[0] -= alphAt * mHeadB0 / (Math.Sqrt(vHeadB0) + AdamEpsilon);

                    double g1 = batchGradHB1 * invBatch * clipScale;
                    mHeadB1 = AdamBeta1 * mHeadB1 + (1 - AdamBeta1) * g1;
                    vHeadB1 = AdamBeta2 * vHeadB1 + (1 - AdamBeta2) * g1 * g1;
                    headB[1] -= alphAt * mHeadB1 / (Math.Sqrt(vHeadB1) + AdamEpsilon);
                }

                // Conv + ResW + LayerNorm Adam update
                for (int b = 0; b < numBlocks; b++)
                {
                    for (int wi = 0; wi < convW[b].Length; wi++)
                    {
                        double grad = batchGradConvW[b][wi] * invBatch * clipScale;
                        mConvW[b][wi] = AdamBeta1 * mConvW[b][wi] + (1 - AdamBeta1) * grad;
                        vConvW[b][wi] = AdamBeta2 * vConvW[b][wi] + (1 - AdamBeta2) * grad * grad;
                        convW[b][wi] -= alphAt * mConvW[b][wi] / (Math.Sqrt(vConvW[b][wi]) + AdamEpsilon);
                    }
                    for (int o = 0; o < filters; o++)
                    {
                        double grad = batchGradConvB[b][o] * invBatch * clipScale;
                        mConvB[b][o] = AdamBeta1 * mConvB[b][o] + (1 - AdamBeta1) * grad;
                        vConvB[b][o] = AdamBeta2 * vConvB[b][o] + (1 - AdamBeta2) * grad * grad;
                        convB[b][o] -= alphAt * mConvB[b][o] / (Math.Sqrt(vConvB[b][o]) + AdamEpsilon);
                    }
                    if (resW[b] != null)
                    {
                        for (int wi = 0; wi < resW[b]!.Length; wi++)
                        {
                            double grad = batchGradResW[b][wi] * invBatch * clipScale;
                            mResW[b][wi] = AdamBeta1 * mResW[b][wi] + (1 - AdamBeta1) * grad;
                            vResW[b][wi] = AdamBeta2 * vResW[b][wi] + (1 - AdamBeta2) * grad * grad;
                            resW[b]![wi] -= alphAt * mResW[b][wi] / (Math.Sqrt(vResW[b][wi]) + AdamEpsilon);
                        }
                    }
                    if (useLayerNorm)
                    {
                        for (int o = 0; o < filters; o++)
                        {
                            double gG = batchGradLnG![b][o] * invBatch * clipScale;
                            mLnG[b][o] = AdamBeta1 * mLnG[b][o] + (1 - AdamBeta1) * gG;
                            vLnG[b][o] = AdamBeta2 * vLnG[b][o] + (1 - AdamBeta2) * gG * gG;
                            lnGamma[b][o] -= alphAt * mLnG[b][o] / (Math.Sqrt(vLnG[b][o]) + AdamEpsilon);

                            double gB = batchGradLnB![b][o] * invBatch * clipScale;
                            mLnB[b][o] = AdamBeta1 * mLnB[b][o] + (1 - AdamBeta1) * gB;
                            vLnB[b][o] = AdamBeta2 * vLnB[b][o] + (1 - AdamBeta2) * gB * gB;
                            lnBeta[b][o] -= alphAt * mLnB[b][o] / (Math.Sqrt(vLnB[b][o]) + AdamEpsilon);
                        }
                    }
                }

                // Magnitude head Adam update
                if (useMagTask)
                {
                    for (int fi = 0; fi < filters; fi++)
                    {
                        double gm = batchGradMagW[fi] * invBatch * clipScale;
                        mMagW[fi] = AdamBeta1 * mMagW[fi] + (1 - AdamBeta1) * gm;
                        vMagW[fi] = AdamBeta2 * vMagW[fi] + (1 - AdamBeta2) * gm * gm;
                        magHeadW[fi] -= alphAt * mMagW[fi] / (Math.Sqrt(vMagW[fi]) + AdamEpsilon);
                    }
                    double gmb = batchGradMagB * invBatch * clipScale;
                    mMagB = AdamBeta1 * mMagB + (1 - AdamBeta1) * gmb;
                    vMagB = AdamBeta2 * vMagB + (1 - AdamBeta2) * gmb * gmb;
                    magHeadB -= alphAt * mMagB / (Math.Sqrt(vMagB) + AdamEpsilon);
                }

                // Attention weights Adam update
                if (useAttentionPool)
                {
                    AdamUpdate(attnQueryW, mAttnQW, vAttnQW, batchGradAttnQW!, invBatch, clipScale, alphAt);
                    AdamUpdate(attnKeyW, mAttnKW, vAttnKW, batchGradAttnKW!, invBatch, clipScale, alphAt);
                    AdamUpdate(attnValueW, mAttnVW, vAttnVW, batchGradAttnVW!, invBatch, clipScale, alphAt);
                }

                // AdamW decoupled weight decay (applied after Adam update, not in gradient)
                if (hp.L2Lambda > 0)
                {
                    double decay = 1.0 - alpha * hp.L2Lambda;
                    for (int fi = 0; fi < headW.Length; fi++) headW[fi] *= decay;
                    for (int b = 0; b < numBlocks; b++)
                    {
                        for (int wi = 0; wi < convW[b].Length; wi++) convW[b][wi] *= decay;
                        for (int o = 0; o < filters; o++) convB[b][o] *= decay;
                        if (resW[b] != null)
                            for (int wi = 0; wi < resW[b]!.Length; wi++) resW[b]![wi] *= decay;
                    }
                    if (useMagTask)
                        for (int fi = 0; fi < filters; fi++) magHeadW[fi] *= decay;
                    if (useAttentionPool)
                    {
                        for (int wi = 0; wi < attnQueryW.Length; wi++)
                        {
                            attnQueryW[wi] *= decay;
                            attnKeyW[wi] *= decay;
                            attnValueW[wi] *= decay;
                        }
                    }
                }

                // Weight magnitude clipping
                if (maxWeightMag > 0)
                {
                    ClipArray(headW, maxWeightMag);
                    headB[0] = Math.Clamp(headB[0], -maxWeightMag, maxWeightMag);
                    headB[1] = Math.Clamp(headB[1], -maxWeightMag, maxWeightMag);
                    ClipArray(magHeadW, maxWeightMag);
                    for (int b = 0; b < numBlocks; b++)
                    {
                        ClipArray(convW[b], maxWeightMag);
                        ClipArray(convB[b], maxWeightMag);
                        if (resW[b] != null) ClipArray(resW[b]!, maxWeightMag);
                    }
                    if (useAttentionPool)
                    {
                        ClipArray(attnQueryW, maxWeightMag);
                        ClipArray(attnKeyW, maxWeightMag);
                        ClipArray(attnValueW, maxWeightMag);
                    }
                }

                // NaN/Inf guard
                if (!IsFiniteWeights(headW) || !double.IsFinite(headB[0]) || !double.IsFinite(headB[1]))
                {
                    RestoreCheckpoint(convW, bestConvW, convB, bestConvB, resW, bestResW,
                        headW, bestHeadW, headB, bestHeadB, magHeadW, bestMagHeadW, ref magHeadB, bestMagHeadB,
                        lnGamma, bestLnGamma, lnBeta, bestLnBeta,
                        attnQueryW, bestAttnQW, attnKeyW, bestAttnKW, attnValueW, bestAttnVW);
                    goto EndEpoch;
                }

                // Clear batch accumulators
                ClearBatchGrads(batchGradConvW, batchGradConvB, batchGradResW, batchGradHeadW,
                    ref batchGradHB0, ref batchGradHB1, batchGradMagW, ref batchGradMagB,
                    resW, batchGradLnG, batchGradLnB, useLayerNorm,
                    batchGradAttnQW, batchGradAttnKW, batchGradAttnVW, useAttentionPool);
            }

            // ── Epoch-end: validation ────────────────────────────────────
            double valLoss = 0;
            int valCorrect = 0;
            foreach (var sv in valSet)
            {
                double[] hv;
                if (useAttentionPool)
                    hv = CausalConvForwardWithAttention(sv.SequenceFeatures!, convW, convB, resW, blockInC,
                        filters, numBlocks, dilations, useLayerNorm, lnGamma, lnBeta, activation,
                        attnQueryW, attnKeyW, attnValueW, attentionHeads);
                else
                    hv = CausalConvForwardFull(sv.SequenceFeatures!, convW, convB, resW, blockInC,
                        filters, numBlocks, dilations, useLayerNorm, lnGamma, lnBeta, activation);
                double l0 = headB[0], l1 = headB[1];
                for (int fi = 0; fi < filters; fi++)
                { l0 += headW[fi] * hv[fi]; l1 += headW[filters + fi] * hv[fi]; }
                double yv = sv.Direction > 0 ? posLabel : negLabel;
                double pDir = Math.Clamp(Softmax2P(l0, l1), 1e-7, 1 - 1e-7);
                valLoss += -(yv * Math.Log(pDir) + (1.0 - yv) * Math.Log(1.0 - pDir));
                if ((pDir >= 0.5) == (sv.Direction == 1)) valCorrect++;
            }
            valLoss /= Math.Max(1, valSet.Count);

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                CopyCheckpoint(convW, bestConvW, convB, bestConvB, resW, bestResW,
                    headW, bestHeadW, headB, bestHeadB, magHeadW, bestMagHeadW, magHeadB, out bestMagHeadB,
                    lnGamma, bestLnGamma, lnBeta, bestLnBeta,
                    attnQueryW, bestAttnQW, attnKeyW, bestAttnKW, attnValueW, bestAttnVW);
                patience = 0;
            }
            else if (++patience >= hp.EarlyStoppingPatience)
            {
                if (!useSwa || epoch < hp.SwaStartEpoch)
                {
                    _logger.LogDebug("TCN early stopped at epoch {E}", epoch);
                    break;
                }
            }

            // Adaptive LR decay (multi-fire, floored at 1e-3 of the initial scale)
            if (hp.AdaptiveLrDecayFactor > 0.0 && epoch % 5 == 0 && lrScale > 1e-3)
            {
                double curAcc = valSet.Count > 0 ? (double)valCorrect / valSet.Count : 0;
                if (curAcc > peakValAcc) peakValAcc = curAcc;
                else if (peakValAcc > 0 && curAcc < peakValAcc - 0.05)
                { lrScale *= hp.AdaptiveLrDecayFactor; peakValAcc = curAcc; }
            }

            // SWA accumulation
            if (useSwa && epoch >= hp.SwaStartEpoch &&
                (epoch - hp.SwaStartEpoch) % Math.Max(1, hp.SwaFrequency) == 0)
            {
                for (int fi = 0; fi < headW.Length; fi++) swaHeadW![fi] += headW[fi];
                swaHeadB![0] += headB[0]; swaHeadB[1] += headB[1];
                for (int b = 0; b < numBlocks; b++)
                {
                    for (int wi = 0; wi < convW[b].Length; wi++) swaConvW![b][wi] += convW[b][wi];
                    for (int o = 0; o < filters; o++) swaConvB![b][o] += convB[b][o];
                    if (resW[b] != null)
                        for (int wi = 0; wi < resW[b]!.Length; wi++) swaResW![b]![wi] += resW[b]![wi];
                    if (useLayerNorm)
                    {
                        for (int o = 0; o < filters; o++)
                        {
                            swaLnGamma![b][o] += lnGamma[b][o];
                            swaLnBeta![b][o]  += lnBeta[b][o];
                        }
                    }
                }
                for (int fi = 0; fi < filters; fi++) swaMagW![fi] += magHeadW[fi];
                swaMagB += magHeadB;
                if (useAttentionPool)
                {
                    for (int wi = 0; wi < attnQueryW.Length; wi++)
                    {
                        swaAttnQW![wi] += attnQueryW[wi];
                        swaAttnKW![wi] += attnKeyW[wi];
                        swaAttnVW![wi] += attnValueW[wi];
                    }
                }
                swaCount++;
            }

            if (epoch % 10 == 0)
                _logger.LogDebug("TCN epoch {E}: loss={L:F4}, valLoss={V:F4}",
                    epoch, epochLoss / Math.Max(1, trainSet.Count), valLoss);

            EndEpoch:;
        }

        // SWA: use averaged weights if they improve validation loss
        if (useSwa && swaCount > 0)
        {
            var swaAvgW = new double[headW.Length];
            for (int fi = 0; fi < headW.Length; fi++) swaAvgW[fi] = swaHeadW![fi] / swaCount;
            double swaB0 = swaHeadB![0] / swaCount, swaB1 = swaHeadB[1] / swaCount;

            var swaAvgConvW = DeepCopy2D(swaConvW!);
            var swaAvgConvB = DeepCopy2D(swaConvB!);
            var swaAvgResW  = DeepCopyNullable2D(swaResW!);
            var swaAvgLnG   = DeepCopy2D(swaLnGamma!);
            var swaAvgLnB   = DeepCopy2D(swaLnBeta!);
            for (int b = 0; b < numBlocks; b++)
            {
                for (int wi = 0; wi < swaAvgConvW[b].Length; wi++) swaAvgConvW[b][wi] /= swaCount;
                for (int o = 0; o < filters; o++) swaAvgConvB[b][o] /= swaCount;
                if (swaAvgResW[b] != null)
                    for (int wi = 0; wi < swaAvgResW[b]!.Length; wi++) swaAvgResW[b]![wi] /= swaCount;
                if (useLayerNorm)
                    for (int o = 0; o < filters; o++)
                    { swaAvgLnG[b][o] /= swaCount; swaAvgLnB[b][o] /= swaCount; }
            }
            var swaAvgMagW = new double[filters];
            for (int fi = 0; fi < filters; fi++) swaAvgMagW[fi] = swaMagW![fi] / swaCount;
            double swaAvgMagB = swaMagB / swaCount;

            // Pre-compute SWA-averaged attention weights outside the loop
            double[] swaFinalAttnQW = attnQueryW, swaFinalAttnKW = attnKeyW, swaFinalAttnVW = attnValueW;
            if (useAttentionPool && swaAttnQW != null)
            {
                swaFinalAttnQW = new double[attnQueryW.Length];
                swaFinalAttnKW = new double[attnKeyW.Length];
                swaFinalAttnVW = new double[attnValueW.Length];
                for (int wi = 0; wi < attnQueryW.Length; wi++)
                {
                    swaFinalAttnQW[wi] = swaAttnQW[wi] / swaCount;
                    swaFinalAttnKW[wi] = swaAttnKW![wi] / swaCount;
                    swaFinalAttnVW[wi] = swaAttnVW![wi] / swaCount;
                }
            }

            double swaLoss = 0;
            foreach (var sv in valSet)
            {
                double[] hv;
                if (useAttentionPool)
                    hv = CausalConvForwardWithAttention(sv.SequenceFeatures!, swaAvgConvW, swaAvgConvB, swaAvgResW, blockInC,
                        filters, numBlocks, dilations, useLayerNorm, swaAvgLnG, swaAvgLnB, activation,
                        swaFinalAttnQW, swaFinalAttnKW, swaFinalAttnVW, attentionHeads);
                else
                    hv = CausalConvForwardFull(sv.SequenceFeatures!, swaAvgConvW, swaAvgConvB, swaAvgResW, blockInC,
                        filters, numBlocks, dilations, useLayerNorm, swaAvgLnG, swaAvgLnB, activation);
                double l0 = swaB0, l1 = swaB1;
                for (int fi = 0; fi < filters; fi++)
                { l0 += swaAvgW[fi] * hv[fi]; l1 += swaAvgW[filters + fi] * hv[fi]; }
                double yv = sv.Direction > 0 ? posLabel : negLabel;
                double pDir = Math.Clamp(Softmax2P(l0, l1), 1e-7, 1 - 1e-7);
                swaLoss += -(yv * Math.Log(pDir) + (1.0 - yv) * Math.Log(1.0 - pDir));
            }
            swaLoss /= Math.Max(1, valSet.Count);

            if (swaLoss <= bestValLoss)
            {
                Array.Copy(swaAvgW, bestHeadW, headW.Length);
                bestHeadB[0] = swaB0; bestHeadB[1] = swaB1;
                for (int b = 0; b < numBlocks; b++)
                {
                    Array.Copy(swaAvgConvW[b], bestConvW[b], convW[b].Length);
                    Array.Copy(swaAvgConvB[b], bestConvB[b], convB[b].Length);
                    if (swaAvgResW[b] != null && bestResW[b] != null)
                        Array.Copy(swaAvgResW[b]!, bestResW[b]!, resW[b]!.Length);
                    if (useLayerNorm)
                    {
                        Array.Copy(swaAvgLnG[b], bestLnGamma[b], filters);
                        Array.Copy(swaAvgLnB[b], bestLnBeta[b], filters);
                    }
                }
                Array.Copy(swaAvgMagW, bestMagHeadW, filters);
                bestMagHeadB = swaAvgMagB;
                if (useAttentionPool && swaAttnQW != null)
                {
                    Array.Copy(swaFinalAttnQW, bestAttnQW, attnQueryW.Length);
                    Array.Copy(swaFinalAttnKW, bestAttnKW, attnKeyW.Length);
                    Array.Copy(swaFinalAttnVW, bestAttnVW, attnValueW.Length);
                }
                _logger.LogDebug("SWA improved validation loss ({N} checkpoints averaged, all layers)", swaCount);
            }
        }

        // Restore best checkpoint
        RestoreCheckpoint(convW, bestConvW, convB, bestConvB, resW, bestResW,
            headW, bestHeadW, headB, bestHeadB, magHeadW, bestMagHeadW, ref magHeadB, bestMagHeadB,
            lnGamma, bestLnGamma, lnBeta, bestLnBeta,
            attnQueryW, bestAttnQW, attnKeyW, bestAttnKW, attnValueW, bestAttnVW);

        // Return ArrayPool buffers
        for (int b = 0; b < numBlocks; b++)
        {
            pool.Return(mConvW[b]); pool.Return(vConvW[b]);
            pool.Return(mConvB[b]); pool.Return(vConvB[b]);
            if (mResW[b] != null) { pool.Return(mResW[b]); pool.Return(vResW[b]); }
            if (useLayerNorm)
            {
                pool.Return(mLnG[b]); pool.Return(vLnG[b]);
                pool.Return(mLnB[b]); pool.Return(vLnB[b]);
            }
        }
        pool.Return(mHeadW); pool.Return(vHeadW);
        pool.Return(mMagW);  pool.Return(vMagW);
        if (useAttentionPool)
        {
            pool.Return(mAttnQW); pool.Return(vAttnQW);
            pool.Return(mAttnKW); pool.Return(vAttnKW);
            pool.Return(mAttnVW); pool.Return(vAttnVW);
        }

        return new TcnWeights(convW, convB, headW, headB, magHeadW, magHeadB, resW, blockInC,
            channelIn, T, lnGamma, lnBeta, attnQueryW, attnKeyW, attnValueW,
            useLayerNorm, activation, attentionHeads);
    }

    // ── Causal dilated conv forward (inference, no dropout) ──────────────────

    /// <summary>
    /// Runs the causal dilated 1D conv stack in inference mode (no dropout).
    /// Returns the hidden representation at the last timestep [filters].
    /// Uses pre-allocated buffers to avoid GC pressure in high-frequency scoring.
    /// </summary>
    public static double[] CausalConvForward(
        float[][] seq, double[][] convW, double[][] convB, double[]?[] resW, int[] blockInC)
    {
        return CausalConvForwardFull(seq, convW, convB, resW, blockInC,
            DefaultFilters, DefaultNumBlocks, BuildDilations(DefaultNumBlocks),
            false, null, null, TcnActivation.Relu);
    }

    /// <summary>
    /// Full-featured causal dilated conv forward pass with configurable architecture.
    /// Returns the hidden representation at the last timestep [filters].
    /// Uses double-buffering to minimise GC pressure in high-frequency scoring.
    /// </summary>
    public static double[] CausalConvForwardFull(
        float[][] seq, double[][] convW, double[][] convB, double[]?[] resW, int[] blockInC,
        int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, double[][]? lnGamma, double[][]? lnBeta,
        TcnActivation activation)
    {
        int seqT = seq.Length;
        int channelIn = seq[0].Length;
        int maxC = Math.Max(channelIn, filters);

        // Double-buffer: allocate two [seqT][maxC] buffers and swap each block
        var bufA = new double[seqT][];
        var bufB = new double[seqT][];
        for (int t = 0; t < seqT; t++) { bufA[t] = new double[maxC]; bufB[t] = new double[maxC]; }

        // Copy input into bufA
        for (int t = 0; t < seqT; t++)
            for (int c = 0; c < channelIn; c++) bufA[t][c] = seq[t][c];

        var current = bufA;
        var next = bufB;
        var preActRow = new double[filters]; // reused per timestep

        for (int b = 0; b < numBlocks; b++)
        {
            int inC = blockInC[b];
            int dilation = dilations[b];

            for (int t = 0; t < seqT; t++)
            {
                for (int o = 0; o < filters; o++)
                {
                    double sum = convB[b][o];

                    for (int k = 0; k < KernelSize; k++)
                    {
                        int srcT = t - k * dilation;
                        if (srcT < 0) continue;

                        for (int c = 0; c < inC; c++)
                            sum += convW[b][(o * inC + c) * KernelSize + k] * current[srcT][c];
                    }

                    preActRow[o] = sum;
                }

                // LayerNorm across filters
                if (useLayerNorm && lnGamma != null && lnBeta != null)
                {
                    double mean = 0;
                    for (int o = 0; o < filters; o++) mean += preActRow[o];
                    mean /= filters;
                    double variance = 0;
                    for (int o = 0; o < filters; o++)
                    { double d = preActRow[o] - mean; variance += d * d; }
                    variance /= filters;
                    double invStd = 1.0 / Math.Sqrt(variance + 1e-5);
                    for (int o = 0; o < filters; o++)
                        preActRow[o] = lnGamma[b][o] * ((preActRow[o] - mean) * invStd) + lnBeta[b][o];
                }

                for (int o = 0; o < filters; o++)
                {
                    double act = Activate(preActRow[o], activation); // No dropout at inference

                    // Residual
                    if (inC == filters)
                        act += current[t][o];
                    else if (resW[b] != null)
                    {
                        double res = 0;
                        int resOff = o * inC;
                        for (int c = 0; c < inC; c++)
                            res += resW[b]![resOff + c] * current[t][c];
                        act += res;
                    }

                    next[t][o] = act;
                }
            }
            // Swap buffers
            (current, next) = (next, current);
        }

        // Return last timestep (copy to exact-sized array)
        var result = new double[filters];
        Array.Copy(current[seqT - 1], result, filters);
        return result;
    }

    /// <summary>
    /// Full inference path with attention pooling. Returns the pooled representation.
    /// </summary>
    public static double[] CausalConvForwardWithAttention(
        float[][] seq, double[][] convW, double[][] convB, double[]?[] resW, int[] blockInC,
        int filters, int numBlocks, int[] dilations,
        bool useLayerNorm, double[][]? lnGamma, double[][]? lnBeta,
        TcnActivation activation,
        double[] attnQueryW, double[] attnKeyW, double[] attnValueW,
        int numHeads = 1)
    {
        int seqT = seq.Length;
        int channelIn = seq[0].Length;
        int maxC = Math.Max(channelIn, filters);

        // Double-buffer to minimise allocations
        var bufA = new double[seqT][];
        var bufB = new double[seqT][];
        for (int t = 0; t < seqT; t++) { bufA[t] = new double[maxC]; bufB[t] = new double[maxC]; }

        for (int t = 0; t < seqT; t++)
            for (int c = 0; c < channelIn; c++) bufA[t][c] = seq[t][c];

        var current = bufA;
        var next = bufB;
        var preActRow = new double[filters];

        for (int b = 0; b < numBlocks; b++)
        {
            int inC = blockInC[b];
            int dilation = dilations[b];

            for (int t = 0; t < seqT; t++)
            {
                for (int o = 0; o < filters; o++)
                {
                    double sum = convB[b][o];
                    for (int k = 0; k < KernelSize; k++)
                    {
                        int srcT = t - k * dilation;
                        if (srcT < 0) continue;
                        for (int c = 0; c < inC; c++)
                            sum += convW[b][(o * inC + c) * KernelSize + k] * current[srcT][c];
                    }
                    preActRow[o] = sum;
                }

                if (useLayerNorm && lnGamma != null && lnBeta != null)
                {
                    double mean = 0;
                    for (int o = 0; o < filters; o++) mean += preActRow[o];
                    mean /= filters;
                    double variance = 0;
                    for (int o = 0; o < filters; o++)
                    { double d = preActRow[o] - mean; variance += d * d; }
                    variance /= filters;
                    double invStd = 1.0 / Math.Sqrt(variance + 1e-5);
                    for (int o = 0; o < filters; o++)
                        preActRow[o] = lnGamma[b][o] * ((preActRow[o] - mean) * invStd) + lnBeta[b][o];
                }

                for (int o = 0; o < filters; o++)
                {
                    double act = Activate(preActRow[o], activation);
                    if (inC == filters) act += current[t][o];
                    else if (resW[b] != null)
                    {
                        double res = 0;
                        int resOff = o * inC;
                        for (int c = 0; c < inC; c++) res += resW[b]![resOff + c] * current[t][c];
                        act += res;
                    }
                    next[t][o] = act;
                }
            }
            (current, next) = (next, current);
        }

        // Attention pooling over all timesteps
        return AttentionPoolInferenceFromHidden(current, seqT, filters, attnQueryW, attnKeyW, attnValueW, numHeads);
    }

    // ── Attention pooling ────────────────────────────────────────────────────

    /// <summary>
    /// Multi-head scaled dot-product attention pooling: Q=lastTimestep(H), K=H*Wk, V=H*Wv.
    /// Each head operates on filters/numHeads dimensions independently.
    /// Produces a single [filters]-dimensional output by attending over all timesteps.
    /// </summary>
    private static double[] AttentionPoolForward(
        double[][] hiddenStates, int seqT, int filters,
        double[] queryW, double[] keyW, double[] valueW,
        double[] queryBuf, double[][] keyBuf, double[][] valBuf, double[] scores, double[] output,
        int numHeads = 1)
    {
        int headDim = filters / Math.Max(1, numHeads);

        // Query = last timestep projected through W_q (better temporal signal than mean)
        var lastH = hiddenStates[seqT - 1];
        MatVec(queryW, lastH, queryBuf, filters, filters);

        // K = H * W_k  [seqT][filters], V = H * W_v  [seqT][filters]
        for (int t = 0; t < seqT; t++)
        {
            MatVec(keyW, hiddenStates[t], keyBuf[t], filters, filters);
            MatVec(valueW, hiddenStates[t], valBuf[t], filters, filters);
        }

        Array.Clear(output, 0, filters);

        // Per-head attention
        for (int head = 0; head < numHeads; head++)
        {
            int hOff = head * headDim;
            double scale = 1.0 / Math.Sqrt(headDim);
            double maxScore = double.MinValue;

            for (int t = 0; t < seqT; t++)
            {
                double dot = 0;
                for (int f = 0; f < headDim; f++) dot += queryBuf[hOff + f] * keyBuf[t][hOff + f];
                scores[t] = dot * scale;
                if (scores[t] > maxScore) maxScore = scores[t];
            }

            double sumExp = 0;
            for (int t = 0; t < seqT; t++) { scores[t] = Math.Exp(scores[t] - maxScore); sumExp += scores[t]; }
            for (int t = 0; t < seqT; t++) scores[t] /= sumExp;

            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    output[hOff + f] += scores[t] * valBuf[t][hOff + f];
        }

        return output;
    }

    private static void AttentionPoolBackward(
        double[] dOutput, double[][] hiddenStates, int seqT, int filters,
        double[] queryW, double[] keyW, double[] valueW,
        double[] queryBuf, double[][] keyBuf, double[][] valBuf,
        double[][] dHiddenStates,
        double[] dQueryW, double[] dKeyW, double[] dValueW,
        // Pre-allocated scratch buffers (size >= seqT for scores; size >= filters for query;
        // size >= seqT*filters for flat val/key delta buffers). Callers allocate once and reuse.
        double[] scratchHeadScores, double[] scratchDScores, double[] scratchDPreScores,
        double[] scratchDQueryBuf, double[] scratchDValFlat, double[] scratchDKeyFlat,
        int numHeads = 1)
    {
        int headDim = filters / Math.Max(1, numHeads);

        for (int head = 0; head < numHeads; head++)
        {
            int hOff = head * headDim;
            double scale = 1.0 / Math.Sqrt(headDim);

            // Clear scratch buffers for this head
            Array.Clear(scratchHeadScores, 0, seqT);
            Array.Clear(scratchDScores,    0, seqT);
            Array.Clear(scratchDPreScores, 0, seqT);
            Array.Clear(scratchDQueryBuf,  0, headDim);
            Array.Clear(scratchDValFlat,   0, seqT * headDim);
            Array.Clear(scratchDKeyFlat,   0, seqT * headDim);

            // Recompute attention scores for this head
            double maxScore = double.MinValue;
            for (int t = 0; t < seqT; t++)
            {
                double dot = 0;
                for (int f = 0; f < headDim; f++) dot += queryBuf[hOff + f] * keyBuf[t][hOff + f];
                scratchHeadScores[t] = dot * scale;
                if (scratchHeadScores[t] > maxScore) maxScore = scratchHeadScores[t];
            }
            double sumExp = 0;
            for (int t = 0; t < seqT; t++) { scratchHeadScores[t] = Math.Exp(scratchHeadScores[t] - maxScore); sumExp += scratchHeadScores[t]; }
            for (int t = 0; t < seqT; t++) scratchHeadScores[t] /= sumExp;

            // d_scores[t] = Σ_f dOutput[hOff+f] * valBuf[t][hOff+f]
            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    scratchDScores[t] += dOutput[hOff + f] * valBuf[t][hOff + f];

            // d_valFlat[t * headDim + f] = scratchHeadScores[t] * dOutput[hOff + f]
            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    scratchDValFlat[t * headDim + f] = scratchHeadScores[t] * dOutput[hOff + f];

            // Softmax backward
            double dotSD = 0;
            for (int t = 0; t < seqT; t++) dotSD += scratchHeadScores[t] * scratchDScores[t];
            for (int t = 0; t < seqT; t++) scratchDPreScores[t] = scratchHeadScores[t] * (scratchDScores[t] - dotSD);

            // scratchDQueryBuf[f] = Σ_t scratchDPreScores[t] * scale * keyBuf[t][hOff+f]
            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    scratchDQueryBuf[f] += scratchDPreScores[t] * scale * keyBuf[t][hOff + f];

            // scratchDKeyFlat[t * headDim + f] = scratchDPreScores[t] * scale * queryBuf[hOff+f]
            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    scratchDKeyFlat[t * headDim + f] = scratchDPreScores[t] * scale * queryBuf[hOff + f];

            // Query uses last timestep — propagate gradient back
            var lastH = hiddenStates[seqT - 1];

            // dW_q += scratchDQueryBuf ⊗ lastH
            for (int i = 0; i < headDim; i++)
                for (int j = 0; j < filters; j++)
                    dQueryW[(hOff + i) * filters + j] += scratchDQueryBuf[i] * lastH[j];

            // dH[last] via Q path
            for (int i = 0; i < headDim; i++)
                for (int j = 0; j < filters; j++)
                    dHiddenStates[seqT - 1][j] += queryW[(hOff + i) * filters + j] * scratchDQueryBuf[i];

            // dH via K and V paths
            for (int t = 0; t < seqT; t++)
            {
                for (int i = 0; i < headDim; i++)
                {
                    int flatIdx = t * headDim + i;
                    for (int j = 0; j < filters; j++)
                    {
                        dKeyW[(hOff + i) * filters + j]   += scratchDKeyFlat[flatIdx] * hiddenStates[t][j];
                        dValueW[(hOff + i) * filters + j] += scratchDValFlat[flatIdx] * hiddenStates[t][j];
                        dHiddenStates[t][j] += keyW[(hOff + i) * filters + j]   * scratchDKeyFlat[flatIdx]
                                             + valueW[(hOff + i) * filters + j] * scratchDValFlat[flatIdx];
                    }
                }
            }
        }
    }

    private static double[] AttentionPoolInferenceFromHidden(
        double[][] hiddenStates, int seqT, int filters,
        double[] queryW, double[] keyW, double[] valueW,
        int numHeads = 1)
    {
        int headDim = filters / Math.Max(1, numHeads);

        // Query = last timestep projected through W_q
        var q = new double[filters];
        MatVec(queryW, hiddenStates[seqT - 1], q, filters, filters);

        var output = new double[filters];

        // Pre-compute all key and value projections once before the head loop.
        // Without this, for numHeads > 1 each timestep's full MatVec is recomputed
        // once per head — an O(numHeads) redundancy over the O(seqT * filters^2) cost.
        var kCache = new double[seqT][];
        var vCache = new double[seqT][];
        for (int t = 0; t < seqT; t++)
        {
            kCache[t] = new double[filters];
            vCache[t] = new double[filters];
            MatVec(keyW,   hiddenStates[t], kCache[t], filters, filters);
            MatVec(valueW, hiddenStates[t], vCache[t], filters, filters);
        }

        // headScores allocated once and reused across heads
        var headScores = new double[seqT];

        for (int head = 0; head < numHeads; head++)
        {
            int hOff = head * headDim;
            double scale = 1.0 / Math.Sqrt(headDim);
            double maxScore = double.MinValue;

            for (int t = 0; t < seqT; t++)
            {
                double dot = 0;
                for (int f = 0; f < headDim; f++) dot += q[hOff + f] * kCache[t][hOff + f];
                headScores[t] = dot * scale;
                if (headScores[t] > maxScore) maxScore = headScores[t];
            }

            double sumExp = 0;
            for (int t = 0; t < seqT; t++) { headScores[t] = Math.Exp(headScores[t] - maxScore); sumExp += headScores[t]; }
            for (int t = 0; t < seqT; t++) headScores[t] /= sumExp;

            for (int t = 0; t < seqT; t++)
                for (int f = 0; f < headDim; f++)
                    output[hOff + f] += headScores[t] * vCache[t][hOff + f];
        }
        return output;
    }

    // ── Activation functions ─────────────────────────────────────────────────

    private static double Activate(double x, TcnActivation activation) => activation switch
    {
        TcnActivation.Gelu => x * 0.5 * (1.0 + Math.Tanh(Math.Sqrt(2.0 / Math.PI) * (x + 0.044715 * x * x * x))),
        TcnActivation.Swish => x * Sigmoid(x),
        _ => Math.Max(0, x), // ReLU
    };

    private static double ActivateDerivative(double preActivation, TcnActivation activation)
    {
        if (activation == TcnActivation.Gelu)
        {
            double x = preActivation;
            double k = Math.Sqrt(2.0 / Math.PI);
            double inner = k * (x + 0.044715 * x * x * x);
            double tanh = Math.Tanh(inner);
            double sech2 = 1.0 - tanh * tanh;
            double dInner = k * (1.0 + 3.0 * 0.044715 * x * x);
            return 0.5 * (1.0 + tanh) + 0.5 * x * sech2 * dInner;
        }
        if (activation == TcnActivation.Swish)
        {
            double sig = Sigmoid(preActivation);
            return sig + preActivation * sig * (1.0 - sig);
        }
        return preActivation > 0 ? 1.0 : 0.0; // ReLU derivative
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    /// <summary>
    /// Computes the raw probability (before calibration) for a sample through the full TCN.
    /// </summary>
    private static double TcnProb(TrainingSample sample, TcnWeights tcn, int filters, bool useAttentionPool)
    {
        int numBlocks = tcn.ConvW.Length;
        int[] dilations = BuildDilations(numBlocks);
        double[] h;
        if (useAttentionPool)
        {
            h = CausalConvForwardWithAttention(
                sample.SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                filters, numBlocks, dilations,
                tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                tcn.Activation,
                tcn.AttnQueryW, tcn.AttnKeyW, tcn.AttnValueW, tcn.AttentionHeads);
        }
        else
        {
            h = CausalConvForwardFull(
                sample.SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                filters, numBlocks, dilations,
                tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                tcn.Activation);
        }
        double l0 = tcn.HeadB[0], l1 = tcn.HeadB[1];
        for (int fi = 0; fi < filters; fi++)
        { l0 += tcn.HeadW[fi] * h[fi]; l1 += tcn.HeadW[filters + fi] * h[fi]; }
        return Softmax2P(l0, l1);
    }

    // ── Platt scaling ────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, TcnWeights tcn, int filters, bool useAttentionPool)
    {
        if (calSet.Count < 10) return (1.0, 0.0);
        int n = calSet.Count;
        var logits = new double[n]; var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TcnProb(calSet[i], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }
        double pA = 1, pB = 0; const double lr = 0.01;
        for (int ep = 0; ep < 200; ep++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            { double cp = MLFeatureHelper.Sigmoid(pA * logits[i] + pB); double e = cp - labels[i]; dA += e * logits[i]; dB += e; }
            pA -= lr * dA / n; pB -= lr * dB / n;
        }
        return (pA, pB);
    }

    // ── Isotonic calibration (PAVA) ──────────────────────────────────────────

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet, TcnWeights tcn, double plattA, double plattB,
        int filters, bool useAttentionPool)
    {
        if (calSet.Count < 10) return [];

        var pairs = new (double Prob, double Label)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = Math.Clamp(TcnProb(calSet[i], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            pairs[i] = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.Prob.CompareTo(b.Prob));

        int n = pairs.Length;
        var blockSum = new double[n]; var blockCount = new int[n]; var blockRight = new int[n];
        for (int i = 0; i < n; i++) { blockSum[i] = pairs[i].Label; blockCount[i] = 1; blockRight[i] = i; }

        int cur = 0;
        while (cur < n)
        {
            int next = blockRight[cur] + 1;
            if (next >= n) { cur = next; continue; }
            double avg1 = blockSum[cur] / blockCount[cur];
            double avg2 = blockSum[next] / blockCount[next];
            if (avg1 <= avg2) { cur = next; continue; }
            blockSum[cur] += blockSum[next]; blockCount[cur] += blockCount[next]; blockRight[cur] = blockRight[next];
            if (cur > 0) cur = FindBlockStart(blockRight, cur - 1);
        }

        var bp = new List<double>();
        cur = 0;
        while (cur < n)
        {
            double mappedP = blockSum[cur] / blockCount[cur];
            bp.Add(pairs[cur].Prob); bp.Add(mappedP);
            int nextBlock = blockRight[cur] + 1;
            if (nextBlock < n) { bp.Add(pairs[nextBlock - 1].Prob + 1e-10); bp.Add(mappedP); }
            cur = nextBlock;
        }
        return bp.ToArray();
    }

    private static int FindBlockStart(int[] blockRight, int idx)
    {
        while (idx > 0 && blockRight[idx - 1] >= idx) idx--;
        return idx;
    }

    // ── Temperature scaling ──────────────────────────────────────────────────

    private static double FitTemperatureScaling(List<TrainingSample> calSet, TcnWeights tcn,
        int filters, bool useAttentionPool)
    {
        double T = 1.0; const double lr = 0.01;
        int n = calSet.Count;
        var logits = new double[n]; var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TcnProb(calSet[i], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }
        for (int ep = 0; ep < 100; ep++)
        {
            double dT = 0;
            for (int i = 0; i < n; i++)
            {
                double p = MLFeatureHelper.Sigmoid(logits[i] / T);
                dT += (p - labels[i]) * logits[i] / (T * T);
            }
            T -= lr * dT / n;
            T = Math.Clamp(T, 0.1, 10.0);
        }
        return T;
    }

    // ── Conformal calibration (qHat) ─────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet, TcnWeights tcn, double plattA, double plattB, double alpha,
        int filters, bool useAttentionPool)
    {
        if (calSet.Count < 10) return 1.0;
        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = Math.Clamp(TcnProb(calSet[i], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            double y = calSet[i].Direction > 0 ? 1.0 : 0.0;
            scores[i] = 1.0 - (y == 1.0 ? p : 1.0 - p);
        }
        Array.Sort(scores);
        int qIdx = Math.Min((int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1, calSet.Count - 1);
        return scores[Math.Max(0, qIdx)];
    }

    // ── Evaluation ───────────────────────────────────────────────────────────

    private static EvalMetrics Evaluate(
        List<TrainingSample> testSet, TcnWeights tcn,
        double plattA, double plattB, int filters, bool useAttentionPool)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int n = testSet.Count, tp = 0, fp = 0, fn = 0, tn = 0;
        double sumMagSqErr = 0, sumBrier = 0, sumEV = 0;
        var retBuf = ArrayPool<double>.Shared.Rent(n); int retCount = 0;
        try
        {
            int numBlocks = tcn.ConvW.Length;
            int[] dilations = BuildDilations(numBlocks);
            foreach (var s in testSet)
            {
                double[] h;
                if (useAttentionPool)
                    h = CausalConvForwardWithAttention(
                        s.SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                        filters, numBlocks, dilations,
                        tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                        tcn.Activation,
                        tcn.AttnQueryW, tcn.AttnKeyW, tcn.AttnValueW, tcn.AttentionHeads);
                else
                    h = CausalConvForwardFull(
                        s.SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                        filters, numBlocks, dilations,
                        tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                        tcn.Activation);

                double l0 = tcn.HeadB[0], l1 = tcn.HeadB[1];
                for (int fi = 0; fi < filters; fi++)
                { l0 += tcn.HeadW[fi] * h[fi]; l1 += tcn.HeadW[filters + fi] * h[fi]; }
                double raw = Math.Clamp(Softmax2P(l0, l1), 1e-7, 1 - 1e-7);
                double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
                bool predUp = calibP >= 0.5, actUp = s.Direction == 1, correct = predUp == actUp;
                sumBrier += (calibP - (actUp ? 1.0 : 0.0)) * (calibP - (actUp ? 1.0 : 0.0));

                double magPred = tcn.MagHeadB;
                for (int fi = 0; fi < filters; fi++) magPred += tcn.MagHeadW[fi] * h[fi];
                sumMagSqErr += (magPred - s.Magnitude) * (magPred - s.Magnitude);

                sumEV += (correct ? 1 : -1) * Math.Abs(calibP - 0.5) * Math.Abs(s.Magnitude);
                retBuf[retCount++] = (predUp ? 1 : -1) * (actUp ? 1 : -1) * Math.Abs(s.Magnitude);
                if (correct && predUp) tp++; else if (!correct && predUp) fp++;
                else if (!correct && !predUp) fn++; else tn++;
            }
            double acc = (tp + tn) / (double)n;
            double prec = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0;
            double rec = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0;
            double f1 = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
            return new EvalMetrics(acc, prec, rec, f1, Math.Sqrt(sumMagSqErr / n),
                sumEV / n, sumBrier / n, acc, ComputeSharpe(retBuf, retCount), tp, fp, fn, tn);
        }
        finally { ArrayPool<double>.Shared.Return(retBuf); }
    }

    // ── ECE ──────────────────────────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet, TcnWeights tcn, double plattA, double plattB,
        int filters, bool useAttentionPool)
    {
        if (testSet.Count < 20) return 0.5;
        const int B = 10;
        var binConfidenceSum = new double[B];
        var binPositiveCount = new int[B];
        var binCount         = new int[B];
        foreach (var s in testSet)
        {
            double raw = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            int bin = Math.Clamp((int)(p * B), 0, B - 1);
            binConfidenceSum[bin] += p;
            if (s.Direction == 1) binPositiveCount[bin]++;
            binCount[bin]++;
        }
        double ece = 0; int n = testSet.Count;
        for (int b = 0; b < B; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConfidence     = binConfidenceSum[b] / binCount[b];
            double actualPosFraction = (double)binPositiveCount[b] / binCount[b];
            ece += Math.Abs(avgConfidence - actualPosFraction) * binCount[b] / n;
        }
        return ece;
    }

    // ── EV-optimal threshold ─────────────────────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet, TcnWeights tcn, double plattA, double plattB,
        double searchMin, double searchMax, int filters, bool useAttentionPool)
    {
        if (calSet.Count < 10) return 0.5;
        double bestThr = 0.5, bestEV = double.MinValue;
        for (double thr = Math.Max(0.30, searchMin); thr <= Math.Min(0.70, searchMax); thr += 0.01)
        {
            double ev = 0;
            foreach (var s in calSet)
            {
                double raw = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
                double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
                ev += ((p >= thr) == (s.Direction == 1) ? 1 : -1) * Math.Abs(p - 0.5) * Math.Abs(s.Magnitude);
            }
            ev /= calSet.Count;
            if (ev > bestEV) { bestEV = ev; bestThr = thr; }
        }
        return bestThr;
    }

    // ── Brier Skill Score ────────────────────────────────────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, TcnWeights tcn, double plattA, double plattB,
        int filters, bool useAttentionPool)
    {
        if (testSet.Count < 10) return 0;
        double bm = 0, br = 0, cp = testSet.Count(s => s.Direction == 1) / (double)testSet.Count;
        foreach (var s in testSet)
        {
            double raw = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            double y = s.Direction == 1 ? 1 : 0;
            bm += (p - y) * (p - y); br += (cp - y) * (cp - y);
        }
        return br > 1e-10 ? 1 - bm / br : 0;
    }

    // ── Durbin-Watson ────────────────────────────────────────────────────────

    private static double ComputeDurbinWatson(List<TrainingSample> data, TcnWeights tcn,
        int filters, bool useAttentionPool)
    {
        if (data.Count < 3) return 2.0;
        int numBlocks = tcn.ConvW.Length;
        int[] dilations = BuildDilations(numBlocks);
        double prevResid = 0; double sumDiffSq = 0, sumResidSq = 0;
        for (int i = 0; i < data.Count; i++)
        {
            double[] h;
            if (useAttentionPool)
                h = CausalConvForwardWithAttention(
                    data[i].SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                    filters, numBlocks, dilations,
                    tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                    tcn.Activation,
                    tcn.AttnQueryW, tcn.AttnKeyW, tcn.AttnValueW, tcn.AttentionHeads);
            else
                h = CausalConvForwardFull(
                    data[i].SequenceFeatures!, tcn.ConvW, tcn.ConvB, tcn.ResW, tcn.BlockInC,
                    filters, numBlocks, dilations,
                    tcn.UseLayerNorm, tcn.LayerNormGamma, tcn.LayerNormBeta,
                    tcn.Activation);

            double pred = tcn.MagHeadB;
            for (int fi = 0; fi < filters; fi++) pred += tcn.MagHeadW[fi] * h[fi];
            double resid = pred - data[i].Magnitude;
            sumResidSq += resid * resid;
            if (i > 0) { double d = resid - prevResid; sumDiffSq += d * d; }
            prevResid = resid;
        }
        return sumResidSq > 1e-15 ? sumDiffSq / sumResidSq : 2.0;
    }

    // ── Permutation feature importance (channel-level) ───────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, TcnWeights tcn, double plattA, double plattB,
        int filters, bool useAttentionPool,
        CancellationToken ct)
    {
        int channelCount = testSet[0].SequenceFeatures![0].Length;
        int baseCorrect = 0;
        foreach (var s in testSet)
        {
            double raw = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            if ((p >= 0.5) == (s.Direction == 1)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / testSet.Count;
        var importance = new float[channelCount];

        // Parallelise across channels — each channel's permutation is independent
        Parallel.For(0, channelCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            ci =>
        {
            double totalDrop = 0;
            for (int round = 0; round < PermutationRounds; round++)
            {
                var shuffleRng = new Random(42 + round * 1000 + ci);

                var samplePerm = new int[testSet.Count];
                for (int i = 0; i < samplePerm.Length; i++) samplePerm[i] = i;
                for (int i = samplePerm.Length - 1; i > 0; i--)
                { int j = shuffleRng.Next(i + 1); (samplePerm[i], samplePerm[j]) = (samplePerm[j], samplePerm[i]); }

                int permCorrect = 0;
                for (int si = 0; si < testSet.Count; si++)
                {
                    var origSeq = testSet[si].SequenceFeatures!;
                    var permSeq = testSet[samplePerm[si]].SequenceFeatures!;

                    var modifiedSeq = new float[origSeq.Length][];
                    for (int t = 0; t < origSeq.Length; t++)
                    {
                        modifiedSeq[t] = (float[])origSeq[t].Clone();
                        modifiedSeq[t][ci] = permSeq[t][ci];
                    }

                    var modifiedSample = testSet[si] with { SequenceFeatures = modifiedSeq };
                    double raw = Math.Clamp(TcnProb(modifiedSample, tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
                    double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
                    if ((p >= 0.5) == (testSet[si].Direction == 1)) permCorrect++;
                }
                totalDrop += Math.Max(0, baseAcc - (double)permCorrect / testSet.Count);
            }
            importance[ci] = (float)(totalDrop / PermutationRounds);
        });
        return importance;
    }

    // ── Stationarity gate ────────────────────────────────────────────────────

    private static int CountNonStationaryChannels(List<TrainingSample> samples, int channelCount)
    {
        int nonStat = 0;
        int n = Math.Min(samples.Count, 500);
        for (int ci = 0; ci < channelCount; ci++)
        {
            // Extract channel values from the last timestep across samples
            var series = new double[n];
            for (int i = 0; i < n; i++)
            {
                var seq = samples[i].SequenceFeatures;
                if (seq is not null && seq.Length > 0)
                    series[i] = seq[^1][ci]; // last timestep of each sample
                else
                    series[i] = ci < samples[i].Features.Length ? samples[i].Features[ci] : 0;
            }
            double pValue = MLFeatureHelper.AdfTest(series, Math.Min(12, n / 5));
            if (pValue > 0.05) nonStat++;
        }
        return nonStat;
    }

    // ── Temporal decay weights ───────────────────────────────────────────────

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        var w = new double[count];
        if (lambda <= 0) { Array.Fill(w, 1.0); return w; }
        double sum = 0;
        for (int i = 0; i < count; i++) { w[i] = Math.Exp(lambda * (i - count + 1)); sum += w[i]; }
        // Normalise to mean=1 (not sum=1) so effective gradient magnitude is data-size-independent
        if (sum > 1e-15) { double mean = sum / count; for (int i = 0; i < count; i++) w[i] /= mean; }
        return w;
    }

    // ── NaN/Inf sanitization ─────────────────────────────────────────────────

    private static int SanitizeTcnWeights(TcnWeights tcn)
    {
        int c = 0;
        for (int b = 0; b < tcn.ConvW.Length; b++)
        { if (SanitizeArray(tcn.ConvW[b])) c++; if (SanitizeArray(tcn.ConvB[b])) c++; }
        if (SanitizeArray(tcn.HeadW)) c++; if (SanitizeArray(tcn.HeadB)) c++;
        if (SanitizeArray(tcn.MagHeadW)) c++;
        for (int b = 0; b < tcn.ResW.Length; b++) if (tcn.ResW[b] != null && SanitizeArray(tcn.ResW[b]!)) c++;
        for (int b = 0; b < tcn.LayerNormGamma.Length; b++)
        { if (SanitizeArray(tcn.LayerNormGamma[b])) c++; if (SanitizeArray(tcn.LayerNormBeta[b])) c++; }
        if (tcn.AttnQueryW.Length > 0 && SanitizeArray(tcn.AttnQueryW)) c++;
        if (tcn.AttnKeyW.Length > 0 && SanitizeArray(tcn.AttnKeyW)) c++;
        if (tcn.AttnValueW.Length > 0 && SanitizeArray(tcn.AttnValueW)) c++;
        return c;
    }

    private static bool SanitizeArray(double[] arr)
    {
        bool bad = false;
        for (int i = 0; i < arr.Length; i++) if (!double.IsFinite(arr[i])) { arr[i] = 0; bad = true; }
        return bad;
    }

    // ── Math helpers ─────────────────────────────────────────────────────────

    private static double[] Softmax2(double a, double b)
    {
        double m = Math.Max(a, b); double ea = Math.Exp(a - m), eb = Math.Exp(b - m); double s = ea + eb;
        return new[] { ea / s, eb / s };
    }

    /// <summary>Returns only P(class=1) from a 2-class softmax. Zero heap allocation.</summary>
    private static double Softmax2P(double a, double b)
    {
        double m = Math.Max(a, b); double ea = Math.Exp(a - m), eb = Math.Exp(b - m);
        return eb / (ea + eb);
    }

    private static double[] InitWeights(int count, Random rng, double scale)
    {
        var w = new double[count];
        for (int i = 0; i < count; i++) w[i] = SampleGaussian(rng, scale);
        return w;
    }

    private static double SampleGaussian(Random rng, double std)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return std * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static bool IsFiniteWeights(double[] w)
    { for (int i = 0; i < w.Length; i++) if (!double.IsFinite(w[i])) return false; return true; }

    private static void ClipArray(double[] arr, double max)
    { for (int i = 0; i < arr.Length; i++) arr[i] = Math.Clamp(arr[i], -max, max); }

    private static double ComputeSharpe(double[] returns, int count)
    {
        if (count < 2) return 0;
        double sum = 0, sumSq = 0;
        for (int i = 0; i < count; i++) { sum += returns[i]; sumSq += returns[i] * returns[i]; }
        double mean = sum / count, std = Math.Sqrt(Math.Max(0, sumSq / count - mean * mean));
        return std > 1e-10 ? mean / std : 0;
    }

    private static double StdDev(List<double> v, double mean)
    {
        if (v.Count < 2) return 0; double s = 0;
        foreach (double x in v) { double d = x - mean; s += d * d; }
        return Math.Sqrt(s / (v.Count - 1));
    }

    private static double ComputeSharpeTrend(List<double> sharpes)
    {
        if (sharpes.Count < 2) return 0;
        double xM = (sharpes.Count - 1) / 2.0, yM = sharpes.Average(), num = 0, den = 0;
        for (int i = 0; i < sharpes.Count; i++) { double dx = i - xM; num += dx * (sharpes[i] - yM); den += dx * dx; }
        return den > 1e-10 ? num / den : 0;
    }

    private static (double MaxDrawdown, double CurveSharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] preds)
    {
        if (preds.Length == 0) return (0, 0);
        double eq = 0, peak = 0, maxDD = 0, sr = 0, sr2 = 0;
        for (int i = 0; i < preds.Length; i++)
        {
            double r = preds[i].Predicted * preds[i].Actual; eq += r; sr += r; sr2 += r * r;
            if (eq > peak) peak = eq;
            double dd = peak > 0 ? (peak - eq) / peak : 0; if (dd > maxDD) maxDD = dd;
        }
        int n = preds.Length; double mean = sr / n, std = Math.Sqrt(Math.Max(0, sr2 / n - mean * mean));
        return (maxDD, std > 1e-10 ? mean / std : 0);
    }

    /// <summary>Builds exponentially increasing dilation pattern for the given block count.</summary>
    public static int[] BuildDilations(int numBlocks)
    {
        var d = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++) d[b] = 1 << b;
        return d;
    }

    /// <summary>Matrix-vector product: out[i] = Σ_j W[i*cols+j] * v[j].</summary>
    private static void MatVec(double[] w, double[] v, double[] result, int rows, int cols)
    {
        for (int i = 0; i < rows; i++)
        {
            double sum = 0;
            int off = i * cols;
            for (int j = 0; j < cols; j++) sum += w[off + j] * v[j];
            result[i] = sum;
        }
    }

    /// <summary>Adam update for a weight array.</summary>
    private static void AdamUpdate(double[] w, double[] m, double[] v, double[] grad,
        double invBatch, double clipScale, double alphAt)
    {
        for (int i = 0; i < w.Length; i++)
        {
            double g = grad[i] * invBatch * clipScale;
            m[i] = AdamBeta1 * m[i] + (1 - AdamBeta1) * g;
            v[i] = AdamBeta2 * v[i] + (1 - AdamBeta2) * g * g;
            w[i] -= alphAt * m[i] / (Math.Sqrt(v[i]) + AdamEpsilon);
        }
    }

    // ── Buffer allocation helpers ────────────────────────────────────────────

    private static double[][] AllocTimeChannels(int timeSteps, int channels)
    {
        var buf = new double[timeSteps][];
        for (int t = 0; t < timeSteps; t++) buf[t] = new double[channels];
        return buf;
    }

    // ── Checkpoint helpers ────────────────────────────────────────────────────

    private static double[][] DeepCopy2D(double[][] src)
    { var d = new double[src.Length][]; for (int i = 0; i < src.Length; i++) d[i] = (double[])src[i].Clone(); return d; }

    private static double[]?[] DeepCopyNullable2D(double[]?[] src)
    { var d = new double[]?[src.Length]; for (int i = 0; i < src.Length; i++) d[i] = src[i] != null ? (double[])src[i]!.Clone() : null; return d; }

    private static void ClearBatchGrads(
        double[][] bgCW, double[][] bgCB, double[][] bgRW, double[] bgHW,
        ref double bgHB0, ref double bgHB1, double[] bgMW, ref double bgMB,
        double[]?[] resW, double[][]? bgLnG, double[][]? bgLnB, bool useLayerNorm,
        double[]? bgAttnQW, double[]? bgAttnKW, double[]? bgAttnVW, bool useAttentionPool)
    {
        for (int b = 0; b < bgCW.Length; b++)
        {
            Array.Clear(bgCW[b]); Array.Clear(bgCB[b]);
            if (resW[b] != null && bgRW[b] != null) Array.Clear(bgRW[b]);
            if (useLayerNorm) { Array.Clear(bgLnG![b]); Array.Clear(bgLnB![b]); }
        }
        Array.Clear(bgHW); bgHB0 = 0; bgHB1 = 0; Array.Clear(bgMW); bgMB = 0;
        if (useAttentionPool)
        { Array.Clear(bgAttnQW!); Array.Clear(bgAttnKW!); Array.Clear(bgAttnVW!); }
    }

    private static void CopyCheckpoint(
        double[][] cw, double[][] bcw, double[][] cb, double[][] bcb, double[]?[] rw, double[]?[] brw,
        double[] hw, double[] bhw, double[] hb, double[] bhb, double[] mw, double[] bmw,
        double mb, out double bmb,
        double[][] lnG, double[][] blnG, double[][] lnB, double[][] blnB,
        double[] aqw, double[] baqw, double[] akw, double[] bakw, double[] avw, double[] bavw)
    {
        for (int b = 0; b < cw.Length; b++)
        { Array.Copy(cw[b], bcw[b], cw[b].Length); Array.Copy(cb[b], bcb[b], cb[b].Length);
          if (rw[b] != null && brw[b] != null) Array.Copy(rw[b]!, brw[b]!, rw[b]!.Length);
          Array.Copy(lnG[b], blnG[b], lnG[b].Length); Array.Copy(lnB[b], blnB[b], lnB[b].Length); }
        Array.Copy(hw, bhw, hw.Length); Array.Copy(hb, bhb, hb.Length); Array.Copy(mw, bmw, mw.Length); bmb = mb;
        if (aqw.Length > 0) { Array.Copy(aqw, baqw, aqw.Length); Array.Copy(akw, bakw, akw.Length); Array.Copy(avw, bavw, avw.Length); }
    }

    private static void RestoreCheckpoint(
        double[][] cw, double[][] bcw, double[][] cb, double[][] bcb, double[]?[] rw, double[]?[] brw,
        double[] hw, double[] bhw, double[] hb, double[] bhb, double[] mw, double[] bmw,
        ref double mb, double bmb,
        double[][] lnG, double[][] blnG, double[][] lnB, double[][] blnB,
        double[] aqw, double[] baqw, double[] akw, double[] bakw, double[] avw, double[] bavw)
    {
        for (int b = 0; b < cw.Length; b++)
        { Array.Copy(bcw[b], cw[b], cw[b].Length); Array.Copy(bcb[b], cb[b], cb[b].Length);
          if (rw[b] != null && brw[b] != null) Array.Copy(brw[b]!, rw[b]!, rw[b]!.Length);
          Array.Copy(blnG[b], lnG[b], lnG[b].Length); Array.Copy(blnB[b], lnB[b], lnB[b].Length); }
        Array.Copy(bhw, hw, hw.Length); Array.Copy(bhb, hb, hb.Length); Array.Copy(bmw, mw, mw.Length); mb = bmb;
        if (aqw.Length > 0) { Array.Copy(baqw, aqw, aqw.Length); Array.Copy(bakw, akw, akw.Length); Array.Copy(bavw, avw, avw.Length); }
    }

    private static void RestoreFromSnapshot(
        TcnSnapshotWeights prior, double[][] convW, double[][] convB,
        double[] headW, double[] headB, double[] magHeadW, ref double magHeadB, double[]?[] resW,
        double[][] lnGamma, double[][] lnBeta,
        double[] attnQueryW, double[] attnKeyW, double[] attnValueW)
    {
        int numBlocks = convW.Length;
        if (prior.ConvW != null)
            for (int b = 0; b < Math.Min(numBlocks, prior.ConvW.Length); b++)
                if (prior.ConvW[b]?.Length == convW[b].Length) Array.Copy(prior.ConvW[b], convW[b], convW[b].Length);
        if (prior.ConvB != null)
            for (int b = 0; b < Math.Min(numBlocks, prior.ConvB.Length); b++)
                if (prior.ConvB[b]?.Length == convB[b].Length) Array.Copy(prior.ConvB[b], convB[b], convB[b].Length);
        if (prior.HeadW?.Length == headW.Length) Array.Copy(prior.HeadW, headW, headW.Length);
        if (prior.HeadB?.Length == headB.Length) Array.Copy(prior.HeadB, headB, headB.Length);
        if (prior.MagHeadW?.Length == magHeadW.Length) Array.Copy(prior.MagHeadW, magHeadW, magHeadW.Length);
        if (prior.MagHeadB != null) magHeadB = prior.MagHeadB.Value;
        if (prior.ResW != null)
            for (int b = 0; b < Math.Min(numBlocks, prior.ResW.Length); b++)
                if (prior.ResW[b] != null && resW[b] != null && prior.ResW[b]!.Length == resW[b]!.Length)
                    Array.Copy(prior.ResW[b]!, resW[b]!, resW[b]!.Length);
        if (prior.LayerNormGamma != null)
            for (int b = 0; b < Math.Min(numBlocks, prior.LayerNormGamma.Length); b++)
                if (prior.LayerNormGamma[b]?.Length == lnGamma[b].Length)
                    Array.Copy(prior.LayerNormGamma[b]!, lnGamma[b], lnGamma[b].Length);
        if (prior.LayerNormBeta != null)
            for (int b = 0; b < Math.Min(numBlocks, prior.LayerNormBeta.Length); b++)
                if (prior.LayerNormBeta[b]?.Length == lnBeta[b].Length)
                    Array.Copy(prior.LayerNormBeta[b]!, lnBeta[b], lnBeta[b].Length);
        if (prior.AttnQueryW?.Length == attnQueryW.Length) Array.Copy(prior.AttnQueryW, attnQueryW, attnQueryW.Length);
        if (prior.AttnKeyW?.Length == attnKeyW.Length) Array.Copy(prior.AttnKeyW, attnKeyW, attnKeyW.Length);
        if (prior.AttnValueW?.Length == attnValueW.Length) Array.Copy(prior.AttnValueW, attnValueW, attnValueW.Length);
    }

    // ── Density-ratio importance weights ─────────────────────────────────────

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  featureCount,
        int                  recentWindowDays)
    {
        int n = trainSet.Count;
        if (n < 50) { var uniform = new double[n]; Array.Fill(uniform, 1.0 / n); return uniform; }

        int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * 24));
        recentCount     = Math.Min(recentCount, n - 10);
        int histCount   = n - recentCount;

        // For sequence-based samples, use flat features for the discriminator
        var dw  = new double[featureCount];
        double db = 0.0;
        const double lr = 0.01;
        const double l2 = 0.01;

        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < n; i++)
            {
                double y = i >= histCount ? 1.0 : 0.0;
                double z = db;
                for (int j = 0; j < featureCount; j++) z += dw[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - y;
                for (int j = 0; j < featureCount; j++)
                    dw[j] -= lr * (err * trainSet[i].Features[j] + l2 * dw[j]);
                db -= lr * err;
            }
        }

        var weights = new double[n];
        double sum  = 0.0;
        for (int i = 0; i < n; i++)
        {
            double z = db;
            for (int j = 0; j < featureCount; j++) z += dw[j] * trainSet[i].Features[j];
            double p = MLFeatureHelper.Sigmoid(z);
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i] = ratio;
            sum += ratio;
        }
        for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ── Covariate shift weights ───────────────────────────────────────────────

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> samples,
        double[][]           parentQuantileBreakpoints,
        int                  featureCount)
    {
        int n = samples.Count;
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            float[] feat = samples[i].Features;
            int outsideCount = 0;
            int checkedCount = 0;
            for (int j = 0; j < featureCount; j++)
            {
                if (j >= parentQuantileBreakpoints.Length) continue;
                var bp = parentQuantileBreakpoints[j];
                if (bp.Length < 2) continue;
                double q10 = bp[0];
                double q90 = bp[bp.Length - 1];
                if ((double)feat[j] < q10 || (double)feat[j] > q90) outsideCount++;
                checkedCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0.0;
            weights[i] = 1.0 + noveltyFraction;
        }

        double mean = 0;
        for (int i = 0; i < n; i++) mean += weights[i];
        mean /= n;
        if (mean > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    // ── Class-conditional Platt scaling ───────────────────────────────────────

    /// <summary>
    /// Fits separate Platt scalers for Buy (raw prob ≥ 0.5) and Sell (raw prob &lt; 0.5) subsets
    /// of the calibration set to correct directional calibration bias.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample> calSet,
            TcnWeights           tcn,
            int                  filters,
            bool                 useAttentionPool)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double rawP  = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(rawP);
            double y     = s.Direction > 0 ? 1.0 : 0.0;
            if (rawP >= 0.5) buySamples.Add((logit, y));
            else             sellSamples.Add((logit, y));
        }

        static (double A, double B) FitSgd(List<(double Logit, double Y)> pairs)
        {
            if (pairs.Count < 5) return (0.0, 0.0);
            double a = 1.0, b = 0.0;
            for (int ep = 0; ep < epochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var (logit, y) in pairs)
                {
                    double calibP = MLFeatureHelper.Sigmoid(a * logit + b);
                    double err    = calibP - y;
                    dA += err * logit;
                    dB += err;
                }
                int n = pairs.Count;
                a -= lr * dA / n;
                b -= lr * dB / n;
            }
            return (a, b);
        }

        var (aBuy,  bBuy)  = FitSgd(buySamples);
        var (aSell, bSell) = FitSgd(sellSamples);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ── Average Kelly fraction ────────────────────────────────────────────────

    /// <summary>
    /// Computes the half-Kelly fraction averaged over the calibration set:
    ///   mean( max(0, 2·calibP − 1) ) × 0.5
    /// where calibP uses the already-fitted global Platt (A, B).
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        TcnWeights           tcn,
        double               plattA,
        double               plattB,
        int                  filters,
        bool                 useAttentionPool)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (var s in calSet)
        {
            double rawP   = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1.0 - 1e-7);
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return sum / calSet.Count * 0.5;
    }

    // ── Feature (channel) pruning helpers ─────────────────────────────────────

    private static bool[] BuildChannelMask(float[] importance, double threshold, int channelCount)
    {
        if (threshold <= 0.0 || channelCount == 0)
        {
            var allTrue = new bool[channelCount];
            Array.Fill(allTrue, true);
            return allTrue;
        }

        double minImportance = threshold / channelCount;
        var mask = new bool[channelCount];
        for (int j = 0; j < channelCount; j++)
            mask[j] = j < importance.Length && importance[j] >= minImportance;

        return mask;
    }

    /// <summary>
    /// Zeros out masked channels in both flat features and sequence features.
    /// </summary>
    private static List<TrainingSample> ApplySequenceMask(List<TrainingSample> samples, bool[] mask)
    {
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;

            float[][]? seq = null;
            if (s.SequenceFeatures is not null)
            {
                seq = new float[s.SequenceFeatures.Length][];
                for (int t = 0; t < s.SequenceFeatures.Length; t++)
                {
                    seq[t] = (float[])s.SequenceFeatures[t].Clone();
                    for (int c = 0; c < seq[t].Length && c < mask.Length; c++)
                        if (!mask[c]) seq[t][c] = 0f;
                }
            }

            result.Add(s with { Features = f, SequenceFeatures = seq });
        }
        return result;
    }

    // ── Cal-set permutation importance ──────────────────────────────────────

    /// <summary>
    /// Computes single-round permutation importance on the calibration set (no Platt),
    /// normalised to sum=1. Used to bias warm-start feature sampling in the next retrain.
    /// </summary>
    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        TcnWeights           tcn,
        int                  filters,
        bool                 useAttentionPool,
        CancellationToken    ct = default)
    {
        int channelCount = calSet[0].SequenceFeatures?[0].Length ?? calSet[0].Features.Length;
        if (calSet.Count < 10 || channelCount == 0) return new double[channelCount];

        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double p = TcnProb(s, tcn, filters, useAttentionPool);
            if ((p >= 0.5) == (s.Direction == 1)) baseCorrect++;
        }
        double baselineAcc = (double)baseCorrect / calSet.Count;
        var importance = new double[channelCount];

        Parallel.For(0, channelCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            ci =>
        {
            var localRng = new Random(ci * 17 + 99);
            var perm = new int[calSet.Count];
            for (int i = 0; i < perm.Length; i++) perm[i] = i;
            for (int i = perm.Length - 1; i > 0; i--)
            { int j = localRng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }

            int shuffledCorrect = 0;
            for (int si = 0; si < calSet.Count; si++)
            {
                var origSeq = calSet[si].SequenceFeatures!;
                var permSeq = calSet[perm[si]].SequenceFeatures!;
                var modifiedSeq = new float[origSeq.Length][];
                for (int t = 0; t < origSeq.Length; t++)
                {
                    modifiedSeq[t] = (float[])origSeq[t].Clone();
                    modifiedSeq[t][ci] = permSeq[t][ci];
                }
                var modifiedSample = calSet[si] with { SequenceFeatures = modifiedSeq };
                double p = TcnProb(modifiedSample, tcn, filters, useAttentionPool);
                if ((p >= 0.5) == (calSet[si].Direction == 1)) shuffledCorrect++;
            }
            importance[ci] = Math.Max(0.0, baselineAcc - (double)shuffledCorrect / calSet.Count);
        });

        double total = 0;
        for (int i = 0; i < importance.Length; i++) total += importance[i];
        if (total > 1e-10)
            for (int i = 0; i < importance.Length; i++) importance[i] /= total;
        return importance;
    }

    // ── Quantile magnitude regressor (pinball loss) ──────────────────────────

    /// <summary>
    /// Fits a linear quantile regressor using the pinball (check) loss for the τ-th
    /// conditional quantile of magnitude. Used for asymmetric magnitude bounds.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train,
        int                  featureCount,
        double               tau)
    {
        var w    = new double[featureCount];
        double b = 0.0;
        const double lr = 0.005;
        const double l2 = 1e-4;
        const int    passes = 5;

        for (int pass = 0; pass < passes; pass++)
        {
            foreach (var s in train)
            {
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double r    = s.Magnitude - pred;
                double grad = r >= 0 ? -tau : -(tau - 1.0);
                for (int j = 0; j < featureCount; j++)
                    w[j] -= lr * (grad * s.Features[j] + l2 * w[j]);
                b -= lr * grad;
            }
        }

        return (w, b);
    }

    // ── Decision boundary gradient stats (TCN-adapted) ──────────────────────

    /// <summary>
    /// Computes the mean and std of ‖∇_x P(Buy|x)‖ over the calibration set.
    /// For the TCN direction head: ∇_x P ≈ P(1−P) × ‖w_head‖ (approximation using
    /// the classification head weight norm as a proxy for the full Jacobian norm).
    /// </summary>
    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        TcnWeights           tcn,
        double               plattA,
        double               plattB,
        int                  filters,
        bool                 useAttentionPool)
    {
        if (calSet.Count == 0) return (0.0, 0.0);

        // Compute ‖w_head‖ (direction head weight norm, buy class)
        double wNorm = 0.0;
        for (int fi = 0; fi < filters; fi++)
            wNorm += tcn.HeadW[filters + fi] * tcn.HeadW[filters + fi];
        wNorm = Math.Sqrt(wNorm);

        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double rawP = Math.Clamp(TcnProb(calSet[i], tcn, filters, useAttentionPool), 1e-7, 1 - 1e-7);
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            norms[i] = calibP * (1.0 - calibP) * wNorm;
        }

        double mean = 0;
        for (int i = 0; i < norms.Length; i++) mean += norms[i];
        mean /= norms.Length;
        double variance = 0;
        for (int i = 0; i < norms.Length; i++) { double d = norms[i] - mean; variance += d * d; }
        double std = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0.0;
        return (mean, std);
    }

    // ── Mutual-information feature redundancy ────────────────────────────────

    /// <summary>
    /// Computes pairwise mutual information between the top-N features on the training set
    /// (discretised into 10 equal-width bins). Returns pairs whose MI exceeds
    /// threshold × log(2) as "FeatureA:FeatureB" strings.
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  featureCount,
        double               threshold)
    {
        if (threshold <= 0.0 || trainSet.Count < 20) return [];

        const int TopN   = 10;
        const int NumBin = 10;

        int checkCount = Math.Min(TopN, featureCount);
        var result     = new List<string>();
        double maxMi   = threshold * Math.Log(2);

        for (int i = 0; i < checkCount; i++)
        {
            for (int j = i + 1; j < checkCount; j++)
            {
                var joint  = new double[NumBin, NumBin];
                var margI  = new double[NumBin];
                var margJ  = new double[NumBin];
                int n      = 0;

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
                    string nameI = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"f{i}";
                    string nameJ = j < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[j] : $"f{j}";
                    result.Add($"{nameI}:{nameJ}");
                }
            }
        }

        return [.. result];
    }

    // ── Abstention gate (selective prediction, TCN-adapted) ──────────────────

    /// <summary>
    /// Trains a 2-feature logistic gate on [calibP, |calibP − 0.5|] (confidence proxy).
    /// Label: 1 if the TCN prediction was correct for that calibration sample.
    /// At inference, signals below the abstention threshold are suppressed.
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitTcnAbstentionModel(
        List<TrainingSample> calSet,
        TcnWeights           tcn,
        double               plattA,
        double               plattB,
        int                  filters,
        bool                 useAttentionPool)
    {
        const int    Dim    = 2;   // [calibP, |calibP - 0.5|]
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return (new double[Dim], 0.0, 0.5);

        var    aw = new double[Dim];
        double ab = 0.0;
        var    dW = new double[Dim];
        var    af = new double[Dim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, Dim);

            foreach (var s in calSet)
            {
                double rawP   = Math.Clamp(TcnProb(s, tcn, filters, useAttentionPool), 1e-7, 1.0 - 1e-7);
                double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);

                af[0] = calibP;
                af[1] = Math.Abs(calibP - 0.5);
                double lbl = (calibP >= 0.5) == (s.Direction == 1) ? 1.0 : 0.0;

                double z   = ab;
                for (int i = 0; i < Dim; i++) z += aw[i] * af[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - lbl;

                for (int i = 0; i < Dim; i++) dW[i] += err * af[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < Dim; i++)
                aw[i] -= Lr * (dW[i] / n + L2 * aw[i]);
            ab -= Lr * dB / n;
        }

        return (aw, ab, 0.5);
    }

    // ── Internal types ───────────────────────────────────────────────────────

    /// <summary>
    /// Holds all trained TCN weights. Conv kernel layout: [outC * inC * kernelSize] per block,
    /// indexed as weight[(o * inC + c) * KernelSize + k].
    /// </summary>
    public sealed record TcnWeights(
        double[][] ConvW, double[][] ConvB, double[] HeadW, double[] HeadB,
        double[] MagHeadW, double MagHeadB, double[]?[] ResW, int[] BlockInC,
        int ChannelIn, int TimeSteps,
        double[][] LayerNormGamma, double[][] LayerNormBeta,
        double[] AttnQueryW, double[] AttnKeyW, double[] AttnValueW,
        bool UseLayerNorm, TcnActivation Activation,
        int AttentionHeads = 1);

    /// <summary>JSON-serialisable snapshot of TCN weights for model persistence and inference.</summary>
    public sealed class TcnSnapshotWeights
    {
        public double[][]? ConvW { get; set; }
        public double[][]? ConvB { get; set; }
        public double[]?   HeadW { get; set; }
        public double[]?   HeadB { get; set; }
        public double[]?   MagHeadW { get; set; }
        public double?     MagHeadB { get; set; }
        public double[]?[]? ResW { get; set; }
        /// <summary>Number of input channels (9 for sequence, 33 for flat fallback).</summary>
        public int ChannelIn { get; set; }
        /// <summary>Number of timesteps in the input sequence.</summary>
        public int TimeSteps { get; set; }
        /// <summary>Number of convolutional filters per block.</summary>
        public int Filters { get; set; }
        /// <summary>LayerNorm scale parameters per block [numBlocks][filters].</summary>
        public double[][]? LayerNormGamma { get; set; }
        /// <summary>LayerNorm shift parameters per block [numBlocks][filters].</summary>
        public double[][]? LayerNormBeta { get; set; }
        /// <summary>Whether LayerNorm was used during training.</summary>
        public bool UseLayerNorm { get; set; }
        /// <summary>Activation function index (0=ReLU, 1=GELU).</summary>
        public int Activation { get; set; }
        /// <summary>Whether attention pooling was used instead of last-timestep extraction.</summary>
        public bool UseAttentionPooling { get; set; }
        /// <summary>Number of attention heads. 1 = single-head (legacy).</summary>
        public int AttentionHeads { get; set; } = 1;
        /// <summary>Attention query projection weights [filters×filters].</summary>
        public double[]? AttnQueryW { get; set; }
        /// <summary>Attention key projection weights [filters×filters].</summary>
        public double[]? AttnKeyW { get; set; }
        /// <summary>Attention value projection weights [filters×filters].</summary>
        public double[]? AttnValueW { get; set; }
    }
}
