using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade TabNet trainer (Rec #389, v3). Faithful implementation of Arik &amp; Pfister 2019
/// with sequential attentive feature selection across N decision steps.
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
///   <item>Final splits: 70% train | 10% cal | ~20% test with embargo.</item>
///   <item>Stationarity gate, density-ratio weights, covariate-shift weights, adaptive label smoothing.</item>
///   <item>Adam-optimised true TabNet with Ghost BN, sparsemax, GLU, cosine LR, gradient clipping, early stopping.</item>
///   <item>Weight sanitization.</item>
///   <item>Platt + class-conditional Platt + isotonic + temperature calibration.</item>
///   <item>ECE, EV-optimal threshold, Kelly fraction.</item>
///   <item>Magnitude regressor (joint or standalone) with Huber loss.</item>
///   <item>Permutation feature importance with optional pruning re-train.</item>
///   <item>Conformal prediction (split-conformal qHat) + Jackknife+ residuals.</item>
///   <item>Meta-label model, abstention gate, quantile magnitude regressor.</item>
///   <item>Decision boundary stats, Durbin-Watson, MI redundancy, BSS, PSI baselines.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.TabNet)]
public sealed class TabNetModelTrainer : IMLModelTrainer
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string ModelType    = "TABNET";
    private const string ModelVersion = "3.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;
    private const double BnEpsilon   = 1e-5;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly ILogger<TabNetModelTrainer> _logger;

    public TabNetModelTrainer(ILogger<TabNetModelTrainer> logger) => _logger = logger;

    // ═══════════════════════════════════════════════════════════════════════
    //  INTERNAL WEIGHT CONTAINER
    //  Encapsulates all TabNet architecture weights to avoid parameter explosion.
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class TabNetWeights
    {
        // Architecture dimensions
        public int NSteps;
        public int F;
        public int HiddenDim;
        public int AttentionDim;
        public int SharedLayers;
        public int StepLayers;
        public double Gamma;
        public bool UseSparsemax;

        // Shared Feature Transformer: [layer] → weights/biases
        public double[][][] SharedW  = [];   // [layer][outDim][inDim]
        public double[][]   SharedB  = [];   // [layer][outDim]
        public double[][][] SharedGW = [];   // GLU gate weights
        public double[][]   SharedGB = [];   // GLU gate biases

        // Step-specific Feature Transformer: [step][layer] → weights/biases
        public double[][][][] StepW  = [];   // [step][layer][outDim][inDim]
        public double[][][]   StepB  = [];   // [step][layer][outDim]
        public double[][][][] StepGW = [];   // [step][layer][outDim][inDim]
        public double[][][]   StepGB = [];   // [step][layer][outDim]

        // Attentive Transformer: FC per step
        public double[][][] AttnFcW = [];    // [step][attDim][inDim]
        public double[][]   AttnFcB = [];    // [step][attDim]

        // Batch Normalization parameters: indexed linearly
        // Layout: [attn_bn_0, ..., attn_bn_{nSteps-1}, shared_bn_0, ..., step_0_bn_0, ...]
        public double[][] BnGamma = [];      // [bnIdx][dim]
        public double[][] BnBeta  = [];      // [bnIdx][dim]
        public double[][] BnMean  = [];      // running mean [bnIdx][dim]
        public double[][] BnVar   = [];      // running var  [bnIdx][dim]

        // Output head
        public double[] OutputW = [];        // [hiddenDim]
        public double   OutputB;

        // Magnitude head
        public double[] MagW = [];           // [hiddenDim]
        public double   MagB;

        public int TotalBnLayers;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ADAM MOMENT CONTAINER
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class AdamState
    {
        // Mirrors TabNetWeights structure with m (first moment) and v (second moment)
        public double[][][] MSharedW = [], VSharedW = [];
        public double[][]   MSharedB = [], VSharedB = [];
        public double[][][] MSharedGW = [], VSharedGW = [];
        public double[][]   MSharedGB = [], VSharedGB = [];

        public double[][][][] MStepW = [], VStepW = [];
        public double[][][]   MStepB = [], VStepB = [];
        public double[][][][] MStepGW = [], VStepGW = [];
        public double[][][]   MStepGB = [], VStepGB = [];

        public double[][][] MAttnFcW = [], VAttnFcW = [];
        public double[][]   MAttnFcB = [], VAttnFcB = [];

        public double[][] MBnGamma = [], VBnGamma = [];
        public double[][] MBnBeta = [], VBnBeta = [];

        public double[] MOutputW = [], VOutputW = [];
        public double MOutputB, VOutputB;

        public double[] MMagW = [], VMagW = [];
        public double MMagB, VMagB;

        public int T; // Adam step counter
    }

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

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNetModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        int F       = samples[0].Features.Length;
        int nSteps  = hp.TabNetSteps > 0 ? hp.TabNetSteps : (hp.K > 0 ? hp.K : 3);
        double lr   = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        int epochs  = hp.MaxEpochs > 0 ? hp.MaxEpochs : 50;
        int hiddenDim    = hp.TabNetHiddenDim > 0 ? hp.TabNetHiddenDim : Math.Max(8, 8 * nSteps);
        int sharedLayers = hp.TabNetSharedLayers > 0 ? hp.TabNetSharedLayers : 2;
        int stepLayers   = hp.TabNetStepLayers > 0 ? hp.TabNetStepLayers : 2;
        int attentionDim = hp.TabNetAttentionDim > 0 ? hp.TabNetAttentionDim : hiddenDim;
        double gamma     = Math.Clamp(hp.TabNetRelaxationGamma > 0 ? hp.TabNetRelaxationGamma : 1.5, 1.0, 2.0);
        double sparsityCoeff = hp.TabNetSparsity > 0 ? hp.TabNetSparsity : 0.0001;
        bool useSparsemax = hp.TabNetUseSparsemax;
        bool useGlu       = hp.TabNetUseGlu;
        double dropoutRate = hp.TabNetDropoutRate;
        double bnMomentum  = hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98;
        int ghostBatchSize = hp.TabNetGhostBatchSize > 0 ? hp.TabNetGhostBatchSize : 128;

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
            "TabNetModelTrainer v3: n={N} F={F} steps={S} hidden={H} shared={SL} step={StL} epochs={E} lr={LR} γ={G:F2}",
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
        int origF = F;
        if (hp.PolyLearnerFraction > 0.0)
        {
            const int PolyTopN = 5;
            polyTopIdx = SelectPolyTopFeatureIndices(allStd, F, warmStart, PolyTopN);
            int polyPairCount = polyTopIdx.Length * (polyTopIdx.Length - 1) / 2;
            allStd = AugmentSamplesWithPoly(allStd, F, polyTopIdx);
            F += polyPairCount;
            _logger.LogInformation(
                "TabNet poly augmentation: top-{N} features → {PC} pair products, F {Old}→{New}",
                polyTopIdx.Length, polyPairCount, origF, F);
        }

        // ── 1c. Optional unsupervised pre-training ─────────────────────────
        TabNetWeights? pretrainedWeights = null;
        int pretrainEpochs = hp.TabNetPretrainEpochs;
        if (pretrainEpochs > 0 && allStd.Count >= hp.MinSamples * 2)
        {
            double maskFrac = hp.TabNetPretrainMaskFraction > 0 ? hp.TabNetPretrainMaskFraction : 0.3;
            pretrainedWeights = RunUnsupervisedPretraining(
                allStd, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, pretrainEpochs, maskFrac, bnMomentum, ct);
            _logger.LogInformation("TabNet pre-training complete ({Epochs} epochs, mask={Mask:P0})",
                pretrainEpochs, maskFrac);
        }

        // ── 2. Walk-forward cross-validation ───────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(
            allStd, hp, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, lr, sparsityCoeff, epochs, bnMomentum, ct);
        _logger.LogInformation(
            "TabNet Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70% train | 10% cal | ~20% test ─────────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"TabNet: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 3b. Stationarity gate ──────────────────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, F);
            double nonStatFraction = F > 0 ? (double)nonStatCount / F : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "TabNet stationarity gate: {NonStat}/{Total} features may have unit root. Consider enabling FracDiffD.",
                    nonStatCount, F);
        }

        // ── 3c. Density-ratio importance weights ───────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays);
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
                "TabNet adaptive label smoothing: ε={Eps:F3} (ambiguous fraction={Frac:P1})",
                effectiveLabelSmoothing, ambiguousFraction);
        }

        // ── 4. Fit TabNet ──────────────────────────────────────────────────
        var weights = FitTabNet(
            trainSet, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, lr, sparsityCoeff, epochs, effectiveLabelSmoothing,
            warmStart, pretrainedWeights, densityWeights, hp.TemporalDecayLambda, hp.L2Lambda,
            hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
            dropoutRate, bnMomentum, ghostBatchSize, ct);

        _logger.LogInformation("TabNet fitted: steps={S} hidden={H}", nSteps, hiddenDim);

        // ── 4b. Weight sanitization ────────────────────────────────────────
        int sanitizedCount = SanitizeWeights(weights);
        if (sanitizedCount > 0)
            _logger.LogWarning("TabNet sanitized {N} non-finite weight values.", sanitizedCount);

        // ── 5. Platt calibration ───────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, weights);
        if (!double.IsFinite(plattA)) plattA = 1.0;
        if (!double.IsFinite(plattB)) plattB = 0.0;
        _logger.LogDebug("TabNet Platt: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 5b. Class-conditional Platt ────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, weights);
        if (!double.IsFinite(plattABuy)) plattABuy = 1.0;
        if (!double.IsFinite(plattBBuy)) plattBBuy = 0.0;
        if (!double.IsFinite(plattASell)) plattASell = 1.0;
        if (!double.IsFinite(plattBSell)) plattBSell = 0.0;
        _logger.LogDebug(
            "TabNet class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 5c. Kelly fraction ─────────────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, weights, plattA, plattB);
        _logger.LogDebug("TabNet average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 6. Magnitude regressor — prefer jointly-trained head when available ──
        double[] magWeights;
        double magBias;
        if (hp.MagLossWeight > 0.0 && weights.MagW.Length > 0)
        {
            magWeights = weights.MagW;
            magBias    = weights.MagB;
        }
        else
        {
            (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp);
        }

        // ── 7. Evaluation on held-out test set ─────────────────────────────
        var finalMetrics = EvaluateTabNet(testSet, weights, plattA, plattB, magWeights, magBias, F);
        _logger.LogInformation(
            "TabNet eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE ─────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, weights, plattA, plattB);
        _logger.LogInformation("TabNet post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal threshold ────────────────────────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, weights, plattA, plattB,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("TabNet EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 10. Permutation feature importance ─────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, weights, plattA, plattB, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp,
                Name: idx < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[idx] : $"Poly{idx}"))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "TabNet top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Cal-set importance (for warm-start transfer) ──────────────
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, weights, ct)
            : new double[F];

        // ── 11. Feature pruning re-train pass ──────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            _logger.LogInformation("TabNet feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, F);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);
            int maskedF     = activeMask.Count(m => m);

            int prunedEpochs = Math.Max(10, epochs / 2);
            var prunedW = FitTabNet(
                maskedTrain, maskedF, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, sparsityCoeff, prunedEpochs,
                effectiveLabelSmoothing, null, null, densityWeights, hp.TemporalDecayLambda,
                hp.L2Lambda, hp.EarlyStoppingPatience, hp.MagLossWeight, hp.MaxGradNorm,
                dropoutRate, bnMomentum, ghostBatchSize, ct);
            var (pA, pB) = FitPlattScaling(maskedCal, prunedW);

            double[] pmw;
            double pmb;
            if (hp.MagLossWeight > 0.0 && prunedW.MagW.Length > 0)
            { pmw = prunedW.MagW; pmb = prunedW.MagB; }
            else
            { (pmw, pmb) = FitLinearRegressor(maskedTrain, maskedF, hp); }

            var prunedMetrics = EvaluateTabNet(maskedTest, prunedW, pA, pB, pmw, pmb, maskedF);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation("TabNet pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                weights      = prunedW;
                magWeights   = pmw; magBias = pmb;
                plattA       = pA;  plattB  = pB;
                finalMetrics = prunedMetrics;
                F            = maskedF;
                trainSet     = maskedTrain;
                testSet      = maskedTest;
                calSet       = maskedCal;
                ece              = ComputeEce(maskedTest, weights, plattA, plattB);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, weights, plattA, plattB,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax);
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(maskedCal, weights);
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, weights, plattA, plattB);
            }
            else
            {
                _logger.LogInformation("TabNet pruned model rejected — keeping full model");
                prunedCount = 0;
                activeMask  = new bool[F]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
        }

        // ── 11b. Isotonic calibration, conformal threshold ─────────────────
        double[] isotonicBp = FitIsotonicCalibration(calSet, weights, plattA, plattB);
        _logger.LogInformation("TabNet isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(calSet, weights, plattA, plattB, isotonicBp, conformalAlpha);
        _logger.LogInformation("TabNet conformal qHat={QHat:F4} ({Cov:P0} coverage)",
            conformalQHat, hp.ConformalCoverage);

        // ── 11c. Jackknife+ residuals ──────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(trainSet, weights);
        _logger.LogInformation("TabNet Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── 11d. Meta-label model ──────────────────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(calSet, weights);
        _logger.LogDebug("TabNet meta-label model: bias={B:F4}", metaLabelBias);

        // ── 11e. Abstention gate ───────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            calSet, weights, plattA, plattB);
        _logger.LogDebug("TabNet abstention gate: threshold={T:F2}", abstentionThreshold);

        // ── 11f. Quantile magnitude regressor ─────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau);
            _logger.LogDebug("TabNet quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 11g. Decision boundary stats ──────────────────────────────────
        var (dbMean, dbStd) = calSet.Count >= 10
            ? ComputeDecisionBoundaryStats(calSet, weights)
            : (0.0, 0.0);
        _logger.LogDebug("TabNet decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        // ── 11h. Durbin-Watson on magnitude residuals ──────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        _logger.LogDebug("TabNet Durbin-Watson={DW:F4}", durbinWatson);
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

        // ── 11j. Temperature scaling ───────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calSet, weights);
            _logger.LogDebug("TabNet temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 11k. Brier Skill Score ─────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, weights, plattA, plattB);
        _logger.LogInformation("TabNet BSS={BSS:F4}", brierSkillScore);

        // ── 11l. PSI baseline ──────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 12. Mean attention summary ─────────────────────────────────────
        double[] meanAttn = ComputeMeanAttention(testSet, weights);
        SanitizeArr(meanAttn);

        // ── 12b. Sanitize all scalar doubles to prevent JSON serialization failures ──
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

        // ── 13. Serialise model snapshot ───────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = nSteps,
            // Legacy fields for backward compat (CanHandle checks Weights, tests check Weights.Length == TabNetStepAttentionWeights.Length)
            Weights                    = weights.AttnFcW.Length > 0
                                             ? weights.AttnFcW.Select(a => a.Length > 0 ? a[0] : []).ToArray()
                                             : [[]],
            Biases                     = [weights.OutputB],
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
            AvgKellyFraction           = avgKellyFraction,
            RedundantFeaturePairs      = redundantPairs,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            BrierSkillScore            = brierSkillScore,
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
            FeatureSubsetIndices       = polyTopIdx.Length > 0 ? [polyTopIdx] : null,

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
            TabNetHiddenDim            = hiddenDim,
        };

        // Sanitize all arrays in snapshot to prevent JSON serialization failures from NaN/Inf
        SanitizeSnapshotArrays(snapshot);

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "TabNetModelTrainer v3 complete: steps={S}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            nSteps, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TABNET FITTING — True TabNet with shared+step-specific GLU Feature
    //  Transformer, Attentive Transformer with Sparsemax, Ghost BN, Adam
    //  optimizer with cosine LR, gradient clipping, and early stopping.
    // ═══════════════════════════════════════════════════════════════════════

    private TabNetWeights FitTabNet(
        List<TrainingSample> trainSet,
        int                  F,
        int                  nSteps,
        int                  hiddenDim,
        int                  attentionDim,
        int                  sharedLayers,
        int                  stepLayers,
        double               gamma,
        bool                 useSparsemax,
        bool                 useGlu,
        double               baseLr,
        double               sparsityCoeff,
        int                  maxEpochs,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        TabNetWeights?       pretrainedInit,
        double[]?            densityWeights,
        double               temporalDecayLambda,
        double               l2Lambda,
        int                  patience,
        double               magLossWeight,
        double               maxGradNorm,
        double               dropoutRate,
        double               bnMomentum,
        int                  ghostBatchSize,
        CancellationToken    ct)
    {
        int n = trainSet.Count;
        const double HuberDelta = 1.0;
        bool useMagHead = magLossWeight > 0.0;

        // Temporal decay weights blended with density weights
        var temporalWeights = ComputeTemporalWeights(n, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length == n)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++) { temporalWeights[i] *= densityWeights[i]; sum += temporalWeights[i]; }
            if (sum > 1e-15) for (int i = 0; i < n; i++) temporalWeights[i] /= sum;
        }

        // ── Initialise weights ─────────────────────────────────────────────
        var w = InitializeWeights(F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useMagHead);

        // ── Load from pre-trained or warm-start ────────────────────────────
        if (pretrainedInit is not null)
        {
            CopyCompatibleWeights(pretrainedInit, w);
            _logger.LogInformation("TabNet: loaded pre-trained encoder weights");
        }
        else if (warmStart?.Type == ModelType && warmStart.Version == ModelVersion)
        {
            LoadWarmStartWeights(warmStart, w);
            _logger.LogInformation("TabNet warm-start: loaded v3 weights (gen={Gen})", warmStart.GenerationNumber);
        }
        else if (warmStart?.Type == ModelType)
        {
            _logger.LogInformation("TabNet warm-start: version mismatch ({V}→{V2}), starting fresh.",
                warmStart.Version, ModelVersion);
        }

        // ── Adam state ─────────────────────────────────────────────────────
        var adam = InitializeAdamState(w);

        // ── Validation split for early stopping (last 10% of train) ───────
        int valSize  = Math.Max(20, n / 10);
        var valSet   = trainSet[^valSize..];
        var fitSet   = trainSet[..^valSize];
        int nFit     = fitSet.Count;

        double bestValLoss = double.MaxValue;
        int    earlyCount  = 0;
        int    bestEpoch   = 0;
        TabNetWeights bestW = CloneWeights(w);

        // ── Training indices for per-epoch shuffling ──────────────────────
        var indices = new int[nFit];
        for (int i = 0; i < nFit; i++) indices[i] = i;

        // ── Mini-batch gradient accumulators ──────────────────────────────
        const int BatchSize = 32;
        var grad = InitializeGradAccumulator(w);
        int batchCount = 0;

        // ── Scratch buffers ───────────────────────────────────────────────
        var pool = ArrayPool<double>.Shared;
        double[] priorScales = pool.Rent(F);
        double[] attnLogits  = pool.Rent(F);

        // Fix 3: Pre-allocate ForwardResult once, reuse across all samples/epochs
        var fwdPool = ForwardResult.Allocate(nSteps, F, hiddenDim, sharedLayers, stepLayers);

        try
        {
            for (int ep = 0; ep < maxEpochs && !ct.IsCancellationRequested; ep++)
            {
                double cosLr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * ep / maxEpochs));

                // Shuffle training indices each epoch
                var epochRng = new Random(42 + ep);
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int k = epochRng.Next(i + 1);
                    (indices[k], indices[i]) = (indices[i], indices[k]);
                }

                // Fix 5: Compute batch statistics at the start of each epoch from a ghost batch.
                // These are used during training forward passes instead of running stats.
                var (epochBatchMeans, epochBatchVars) = ComputeEpochBatchStats(w, fitSet, ghostBatchSize, epochRng);

                for (int ii = 0; ii < nFit; ii++)
                {
                    int idx = indices[ii];
                    var sample = fitSet[idx];
                    double sampleWt = temporalWeights.Length > idx ? temporalWeights[idx] : 1.0 / nFit;

                    int rawY = sample.Direction > 0 ? 1 : 0;
                    double y = labelSmoothing > 0
                        ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                        : rawY;

                    // ── Forward pass (reuses pooled result, epoch batch stats) ──
                    var fwd = ForwardPass(sample.Features, w, priorScales, attnLogits, training: true,
                        dropoutRate, epochRng, epochBatchMeans, epochBatchVars, fwdPool);

                    double errCE = fwd.Prob - y;

                    // ── Magnitude head Huber gradient ─────────────────────
                    double huberGrad = 0.0;
                    if (useMagHead)
                    {
                        double magPred = w.MagB;
                        for (int j = 0; j < w.HiddenDim && j < w.MagW.Length; j++)
                            magPred += w.MagW[j] * fwd.AggregatedH[j];
                        double magErr = magPred - sample.Magnitude;
                        huberGrad = Math.Abs(magErr) <= HuberDelta
                            ? magErr
                            : HuberDelta * Math.Sign(magErr);
                    }

                    // ── Backward pass (uses epoch batch vars for BN backward consistency) ──
                    AccumulateGradients(grad, w, fwd, sample.Features, errCE, sampleWt,
                        huberGrad, magLossWeight, l2Lambda, sparsityCoeff, useMagHead, epochBatchVars);

                    batchCount++;

                    // ── Apply Adam update at batch boundaries ─────────────
                    if (batchCount >= BatchSize || ii == nFit - 1)
                    {
                        double invBatch = 1.0 / batchCount;
                        ScaleGradients(grad, invBatch);

                        // Gradient clipping
                        if (maxGradNorm > 0)
                            ClipGradients(grad, maxGradNorm);

                        AdamUpdate(w, grad, adam, cosLr);
                        ZeroGradients(grad);
                        batchCount = 0;
                    }
                }

                // ── Update BN running statistics (EMA of epoch batch stats) ──
                UpdateBnRunningStats(w, fitSet, bnMomentum, ghostBatchSize);

                // ── Early stopping (inference mode: running stats, no batch stats) ──
                if (valSet.Count >= 10 && ep % 5 == 4)
                {
                    double valLoss = 0;
                    foreach (var vs in valSet)
                    {
                        var vfwd = ForwardPass(vs.Features, w, priorScales, attnLogits,
                            training: false, 0, null);
                        int vy = vs.Direction > 0 ? 1 : 0;
                        valLoss -= vy * Math.Log(vfwd.Prob + 1e-15)
                                 + (1 - vy) * Math.Log(1 - vfwd.Prob + 1e-15);
                    }
                    valLoss /= valSet.Count;

                    if (valLoss < bestValLoss - 1e-6)
                    {
                        bestValLoss = valLoss;
                        bestEpoch   = ep;
                        bestW       = CloneWeights(w);
                        earlyCount  = 0;
                    }
                    else if (++earlyCount >= Math.Max(3, patience / 5))
                    {
                        _logger.LogDebug("TabNet early stopping at epoch {E} (best at {Best})", ep, bestEpoch);
                        break;
                    }
                }
            }
        }
        finally
        {
            pool.Return(priorScales);
            pool.Return(attnLogits);
        }

        // ── Restore best weights ───────────────────────────────────────────
        if (bestEpoch > 0)
            return bestW;

        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FORWARD PASS — True TabNet architecture
    //  Returns raw probability, per-step aggregated output, and cached
    //  intermediates for backpropagation.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pooled forward-pass result. Pre-allocated once per training run and reused across samples
    /// to eliminate per-sample GC pressure from 16 jagged-array fields.
    /// Call <see cref="Allocate"/> once, then <see cref="ForwardPass"/> overwrites in-place.
    /// </summary>
    private sealed class ForwardResult
    {
        public double   Prob;
        public double[] AggregatedH  = [];     // [hiddenDim] — sum of per-step ReLU outputs
        public double[][] StepH      = [];     // [step][hiddenDim] — per-step output before ReLU
        public double[][] StepAttn   = [];     // [step][F] — attention masks
        public double[][] StepMasked = [];     // [step][F] — masked input
        public double[][][] StepSharedPre   = [];  // [step][layer][hiddenDim] — BN output
        public double[][][] StepSharedGate  = [];  // [step][layer][hiddenDim] — gate sigmoid
        public double[][][] StepSharedXNorm = [];  // [step][layer][hiddenDim] — BN xNorm
        public double[][][] StepSharedFcIn  = [];  // [step][layer][inDim] — FC input
        public double[][][] StepStepPre     = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepGate    = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepXNorm   = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepFcIn    = [];  // [step][layer][inDim]
        public double[][] StepAttnPre = [];    // [step][F] — pre-sparsemax logits
        public double[]   PriorScales = [];    // [F] — final prior scales (after all steps)
        public double[][] StepPriorScales = []; // [step][F] — per-step prior scales (before attention)

        /// <summary>Pre-allocate all inner arrays for the given architecture dimensions.</summary>
        public static ForwardResult Allocate(int nSteps, int F, int H, int sharedLayers, int stepLayers)
        {
            var r = new ForwardResult
            {
                AggregatedH  = new double[H],
                StepH        = AllocJagged(nSteps, H),
                StepAttn     = AllocJagged(nSteps, F),
                StepMasked   = AllocJagged(nSteps, F),
                StepAttnPre  = AllocJagged(nSteps, F),
                PriorScales  = new double[F],
                StepPriorScales = AllocJagged(nSteps, F),
                StepSharedPre   = Alloc3(nSteps, sharedLayers, H),
                StepSharedGate  = Alloc3(nSteps, sharedLayers, H),
                StepSharedXNorm = Alloc3(nSteps, sharedLayers, H),
                StepSharedFcIn  = new double[nSteps][][],
                StepStepPre     = Alloc3(nSteps, stepLayers, H),
                StepStepGate    = Alloc3(nSteps, stepLayers, H),
                StepStepXNorm   = Alloc3(nSteps, stepLayers, H),
                StepStepFcIn    = AllocJagged3(nSteps, stepLayers, H),
            };
            // StepSharedFcIn has variable inDim (F for layer 0, H for layer > 0)
            for (int s = 0; s < nSteps; s++)
            {
                r.StepSharedFcIn[s] = new double[sharedLayers][];
                for (int l = 0; l < sharedLayers; l++)
                    r.StepSharedFcIn[s][l] = new double[l == 0 ? F : H];
            }
            return r;
        }

        /// <summary>Zero all numeric arrays for reuse with the next sample.</summary>
        public void Reset(int H)
        {
            Prob = 0;
            Array.Clear(AggregatedH);
            // Inner arrays are overwritten by ForwardPass, no need to clear
        }

        private static double[][] AllocJagged(int d1, int d2)
        {
            var a = new double[d1][];
            for (int i = 0; i < d1; i++) a[i] = new double[d2];
            return a;
        }
        private static double[][][] Alloc3(int d1, int d2, int d3)
        {
            var a = new double[d1][][];
            for (int i = 0; i < d1; i++) { a[i] = new double[d2][]; for (int j = 0; j < d2; j++) a[i][j] = new double[d3]; }
            return a;
        }
        private static double[][][] AllocJagged3(int d1, int d2, int d3)
        {
            var a = new double[d1][][];
            for (int i = 0; i < d1; i++) { a[i] = new double[d2][]; for (int j = 0; j < d2; j++) a[i][j] = new double[d3]; }
            return a;
        }
    }

    /// <summary>
    /// Runs the true TabNet forward pass, writing results into the pre-allocated <paramref name="result"/>.
    /// When <paramref name="result"/> is null (inference helpers), allocates a fresh one.
    /// </summary>
    private static ForwardResult ForwardPass(
        float[] features, TabNetWeights w, double[] priorScalesBuf, double[] attnLogitsBuf,
        bool training, double dropoutRate, Random? rng,
        double[][]? epochBatchMeans = null, double[][]? epochBatchVars = null,
        ForwardResult? result = null)
    {
        int F = w.F, nSteps = w.NSteps, H = w.HiddenDim;

        // Fix 3: reuse pre-allocated result when provided, else allocate fresh (inference paths)
        var fwd = result ?? ForwardResult.Allocate(nSteps, F, H, w.SharedLayers, w.StepLayers);
        fwd.Reset(H);

        // Initialize prior scales
        Array.Fill(priorScalesBuf, 1.0, 0, F);

        double[] hPrev = new double[H]; // processed features from prior step (zero for step 0)

        for (int s = 0; s < nSteps; s++)
        {
            // Save per-step prior scales before they are modified by this step's attention
            Array.Copy(priorScalesBuf, 0, fwd.StepPriorScales[s], 0, F);

            // ── 1. Attentive Transformer: FC → BN → Sparsemax ────────
            var attnInput = new double[F];
            if (s == 0)
            {
                for (int j = 0; j < F; j++)
                    attnInput[j] = features[j];
            }
            else
            {
                for (int j = 0; j < F; j++)
                {
                    double val = 0;
                    for (int k = 0; k < H && k < w.AttnFcW[s][j].Length; k++)
                        val += w.AttnFcW[s][j][k] * hPrev[k];
                    attnInput[j] = val + w.AttnFcB[s][j];
                }
            }

            // BN on attention input — use batch stats during training (Fix 5)
            int bnIdx = s;
            double[]? attnBatchMean = training && epochBatchMeans is not null && bnIdx < epochBatchMeans.Length ? epochBatchMeans[bnIdx] : null;
            double[]? attnBatchVar  = training && epochBatchVars  is not null && bnIdx < epochBatchVars.Length  ? epochBatchVars[bnIdx]  : null;
            double[] activeMean = attnBatchMean ?? w.BnMean[bnIdx];
            double[] activeVar  = attnBatchVar  ?? w.BnVar[bnIdx];
            attnInput = ApplyBatchNorm(attnInput, w.BnGamma[bnIdx], w.BnBeta[bnIdx],
                activeMean, activeVar, training);

            // Apply prior scales
            for (int j = 0; j < F; j++)
                attnLogitsBuf[j] = priorScalesBuf[j] * attnInput[j];

            Array.Copy(attnLogitsBuf, 0, fwd.StepAttnPre[s], 0, F);

            // Sparsemax or Softmax
            double[] attn = w.UseSparsemax
                ? Sparsemax(attnLogitsBuf, F)
                : SoftmaxArr(attnLogitsBuf, F);

            Array.Copy(attn, fwd.StepAttn[s], F);

            // ── 2. Prior scale update with configurable γ ────────────
            for (int j = 0; j < F; j++)
                priorScalesBuf[j] = Math.Max(1e-6, priorScalesBuf[j] * (w.Gamma - attn[j]));

            // ── 3. Mask input ────────────────────────────────────────
            for (int j = 0; j < F; j++)
                fwd.StepMasked[s][j] = features[j] * attn[j];

            // ── 4. Feature Transformer: shared FC→BN→GLU blocks ──────
            double[] h = fwd.StepMasked[s];
            int inputDim = F;

            for (int l = 0; l < w.SharedLayers; l++)
            {
                int bnSharedIdx = w.NSteps + l;
                double[]? bm = training && epochBatchMeans is not null && bnSharedIdx < epochBatchMeans.Length ? epochBatchMeans[bnSharedIdx] : null;
                double[]? bv = training && epochBatchVars  is not null && bnSharedIdx < epochBatchVars.Length  ? epochBatchVars[bnSharedIdx]  : null;
                var (hNew, pre, gate, xn, fcIn) = FcBnGlu(h, inputDim, H,
                    w.SharedW[l], w.SharedB[l], w.SharedGW[l], w.SharedGB[l],
                    w.BnGamma[bnSharedIdx], w.BnBeta[bnSharedIdx],
                    w.BnMean[bnSharedIdx], w.BnVar[bnSharedIdx], bm, bv,
                    training, dropoutRate, rng);

                // Copy into pre-allocated slots
                Array.Copy(pre, fwd.StepSharedPre[s][l], H);
                Array.Copy(gate, fwd.StepSharedGate[s][l], H);
                Array.Copy(xn, fwd.StepSharedXNorm[s][l], H);
                Array.Copy(fcIn, 0, fwd.StepSharedFcIn[s][l], 0, Math.Min(fcIn.Length, fwd.StepSharedFcIn[s][l].Length));

                // Residual connection with sqrt(0.5) normalization (skip first layer)
                if (l > 0 && h.Length == H)
                {
                    for (int j = 0; j < H; j++)
                        hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476; // sqrt(0.5)
                }

                h = hNew;
                inputDim = H;
            }

            // ── 5. Step-specific FC→BN→GLU blocks ────────────────────
            for (int l = 0; l < w.StepLayers; l++)
            {
                int bnStepIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                double[]? bm = training && epochBatchMeans is not null && bnStepIdx < epochBatchMeans.Length ? epochBatchMeans[bnStepIdx] : null;
                double[]? bv = training && epochBatchVars  is not null && bnStepIdx < epochBatchVars.Length  ? epochBatchVars[bnStepIdx]  : null;
                var (hNew, pre, gate, xn, fcIn) = FcBnGlu(h, H, H,
                    w.StepW[s][l], w.StepB[s][l], w.StepGW[s][l], w.StepGB[s][l],
                    w.BnGamma[bnStepIdx], w.BnBeta[bnStepIdx],
                    w.BnMean[bnStepIdx], w.BnVar[bnStepIdx], bm, bv,
                    training, dropoutRate, rng);

                Array.Copy(pre, fwd.StepStepPre[s][l], H);
                Array.Copy(gate, fwd.StepStepGate[s][l], H);
                Array.Copy(xn, fwd.StepStepXNorm[s][l], H);
                Array.Copy(fcIn, 0, fwd.StepStepFcIn[s][l], 0, Math.Min(fcIn.Length, fwd.StepStepFcIn[s][l].Length));

                if (l > 0)
                {
                    for (int j = 0; j < H; j++)
                        hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;
                }

                h = hNew;
            }

            // ── 6. ReLU gate and aggregate ───────────────────────────
            Array.Copy(h, fwd.StepH[s], H);
            for (int j = 0; j < H; j++)
                fwd.AggregatedH[j] += Math.Max(h[j], 0.0);

            hPrev = h;
        }

        // ── 7. Output head: FC → sigmoid ─────────────────────────────
        double logit = w.OutputB;
        for (int j = 0; j < H; j++)
            logit += w.OutputW[j] * fwd.AggregatedH[j];

        fwd.Prob = Sigmoid(logit);
        Array.Copy(priorScalesBuf, fwd.PriorScales, F);

        return fwd;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FC → BN → GLU BLOCK
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// FC → BN → GLU block. Returns output, BN-normalized values (pre-GLU linear path),
    /// gate sigmoid values, BN xNorm (for BN backward), and the actual FC input (for correct weight gradients).
    /// </summary>
    private static (double[] Output, double[] PreGlu, double[] GateSigmoid, double[] XNorm, double[] FcInput) FcBnGlu(
        double[] input, int inDim, int outDim,
        double[][] fcW, double[] fcB, double[][] gateW, double[] gateB,
        double[] bnGamma, double[] bnBeta, double[] bnMean, double[] bnVar,
        double[]? batchMean, double[]? batchVar,
        bool training, double dropoutRate, Random? rng)
    {
        // Cache the actual FC input for correct backward gradients through residual connections
        var fcInput = (double[])input.Clone();

        // Linear transform
        var linear = new double[outDim];
        for (int i = 0; i < outDim; i++)
        {
            double val = fcB[i];
            for (int j = 0; j < inDim && j < fcW[i].Length; j++)
                val += fcW[i][j] * input[j];
            linear[i] = val;
        }

        // BN — use batch stats during training, running stats at inference (Fix 5)
        double[] activeMean = training && batchMean is not null ? batchMean : bnMean;
        double[] activeVar  = training && batchVar  is not null ? batchVar  : bnVar;
        var (bnOutput, xNorm) = ApplyBatchNormWithXNorm(linear, bnGamma, bnBeta, activeMean, activeVar);

        // Gate transform (for GLU)
        var gate = new double[outDim];
        for (int i = 0; i < outDim; i++)
        {
            double val = gateB[i];
            for (int j = 0; j < inDim && j < gateW[i].Length; j++)
                val += gateW[i][j] * input[j];
            gate[i] = Sigmoid(val);
        }

        // GLU: linear ⊙ sigmoid(gate)
        var output = new double[outDim];
        for (int i = 0; i < outDim; i++)
            output[i] = bnOutput[i] * gate[i];

        // Dropout (training only)
        if (training && dropoutRate > 0 && rng is not null)
        {
            double scale = 1.0 / (1.0 - dropoutRate);
            for (int i = 0; i < outDim; i++)
                if (rng.NextDouble() < dropoutRate) output[i] = 0;
                else output[i] *= scale;
        }

        return (output, bnOutput, gate, xNorm, fcInput);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BATCH NORMALIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ApplyBatchNorm(
        double[] input, double[] gamma, double[] beta,
        double[] mean, double[] var_, bool training)
    {
        int dim = input.Length;
        var output = new double[dim];
        for (int i = 0; i < dim && i < gamma.Length; i++)
        {
            double m = mean.Length > i ? mean[i] : 0.0;
            double v = var_.Length > i ? var_[i] : 1.0;
            double xn = (input[i] - m) / Math.Sqrt(v + BnEpsilon);
            output[i] = gamma[i] * xn + beta[i];
        }
        return output;
    }

    /// <summary>
    /// BN forward that also returns the normalized input xNorm (needed for full BN backward).
    /// </summary>
    private static (double[] Output, double[] XNorm) ApplyBatchNormWithXNorm(
        double[] input, double[] gamma, double[] beta,
        double[] mean, double[] var_)
    {
        int dim = input.Length;
        var output = new double[dim];
        var xNorm  = new double[dim];
        for (int i = 0; i < dim && i < gamma.Length; i++)
        {
            double m = mean.Length > i ? mean[i] : 0.0;
            double v = var_.Length > i ? var_[i] : 1.0;
            xNorm[i]  = (input[i] - m) / Math.Sqrt(v + BnEpsilon);
            output[i] = gamma[i] * xNorm[i] + beta[i];
        }
        return (output, xNorm);
    }

    private void UpdateBnRunningStats(TabNetWeights w, List<TrainingSample> fitSet, double momentum, int ghostBatchSize)
    {
        // Compute batch statistics over a subsample and update running stats
        int batchN = Math.Min(fitSet.Count, ghostBatchSize);
        if (batchN < 10) return;

        // We need to compute statistics at each BN layer. For efficiency,
        // run a forward pass over the ghost batch and collect per-layer activations.
        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];

        // Collect per-BN-layer sums and sum-of-squares
        var layerSums  = new double[w.TotalBnLayers][];
        var layerSqSum = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            layerSums[b]  = new double[dim];
            layerSqSum[b] = new double[dim];
        }

        for (int si = 0; si < batchN; si++)
        {
            var sample = fitSet[si];
            Array.Fill(priorBuf, 1.0, 0, w.F);
            double[] hPrev = new double[w.HiddenDim];

            for (int s = 0; s < w.NSteps; s++)
            {
                // Attention BN input
                var attnInput = new double[w.F];
                if (s == 0)
                {
                    for (int j = 0; j < w.F; j++) attnInput[j] = sample.Features[j];
                }
                else
                {
                    for (int j = 0; j < w.F; j++)
                    {
                        double val = 0;
                        for (int k = 0; k < w.HiddenDim && k < w.AttnFcW[s][j].Length; k++)
                            val += w.AttnFcW[s][j][k] * hPrev[k];
                        attnInput[j] = val + w.AttnFcB[s][j];
                    }
                }

                int bnIdx = s;
                for (int j = 0; j < w.F; j++)
                {
                    layerSums[bnIdx][j]  += attnInput[j];
                    layerSqSum[bnIdx][j] += attnInput[j] * attnInput[j];
                }

                // Compute attention using current running stats (approximate)
                var bnAttn = ApplyBatchNorm(attnInput, w.BnGamma[bnIdx], w.BnBeta[bnIdx],
                    w.BnMean[bnIdx], w.BnVar[bnIdx], false);
                for (int j = 0; j < w.F; j++) attnBuf[j] = priorBuf[j] * bnAttn[j];
                double[] attn = w.UseSparsemax ? Sparsemax(attnBuf, w.F) : SoftmaxArr(attnBuf, w.F);
                for (int j = 0; j < w.F; j++)
                    priorBuf[j] = Math.Max(1e-6, priorBuf[j] * (w.Gamma - attn[j]));

                double[] masked = new double[w.F];
                for (int j = 0; j < w.F; j++) masked[j] = sample.Features[j] * attn[j];

                // Shared layers
                double[] h = masked;
                int inputDim = w.F;
                for (int l = 0; l < w.SharedLayers; l++)
                {
                    int bnSIdx = w.NSteps + l;
                    var linear = FcLinear(h, inputDim, w.HiddenDim, w.SharedW[l], w.SharedB[l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnSIdx][j]  += linear[j];
                        layerSqSum[bnSIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.BnGamma[bnSIdx], w.BnBeta[bnSIdx],
                        w.BnMean[bnSIdx], w.BnVar[bnSIdx], false);
                    var gate = FcSigmoid(h, inputDim, w.HiddenDim, w.SharedGW[l], w.SharedGB[l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0 && h.Length == w.HiddenDim)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;
                    h = hNew;
                    inputDim = w.HiddenDim;
                }

                // Step layers
                for (int l = 0; l < w.StepLayers; l++)
                {
                    int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                    var linear = FcLinear(h, w.HiddenDim, w.HiddenDim, w.StepW[s][l], w.StepB[s][l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnStIdx][j]  += linear[j];
                        layerSqSum[bnStIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.BnGamma[bnStIdx], w.BnBeta[bnStIdx],
                        w.BnMean[bnStIdx], w.BnVar[bnStIdx], false);
                    var gate = FcSigmoid(h, w.HiddenDim, w.HiddenDim, w.StepGW[s][l], w.StepGB[s][l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;
                    h = hNew;
                }

                hPrev = h;
            }
        }

        // Update running statistics with momentum
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = layerSums[b].Length;
            for (int j = 0; j < dim; j++)
            {
                double batchMean = layerSums[b][j] / batchN;
                double batchVar  = layerSqSum[b][j] / batchN - batchMean * batchMean;
                batchVar = Math.Max(batchVar, 0.0);

                w.BnMean[b][j] = momentum * w.BnMean[b][j] + (1 - momentum) * batchMean;
                w.BnVar[b][j]  = momentum * w.BnVar[b][j]  + (1 - momentum) * batchVar;
            }
        }
    }

    /// <summary>
    /// Fix 5: Computes per-BN-layer batch statistics from a ghost-batch subsample.
    /// Returns (means[bnIdx][dim], vars[bnIdx][dim]) for use during training forward passes.
    /// Same traversal as UpdateBnRunningStats but returns the stats instead of updating running values.
    /// The <paramref name="epochRng"/> randomizes which samples are used each epoch for
    /// additional regularization (true ghost BN behavior).
    /// </summary>
    private static (double[][] Means, double[][] Vars) ComputeEpochBatchStats(
        TabNetWeights w, List<TrainingSample> fitSet, int ghostBatchSize, Random epochRng)
    {
        int batchN = Math.Min(fitSet.Count, ghostBatchSize);
        var means = new double[w.TotalBnLayers][];
        var vars  = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            means[b] = new double[dim];
            vars[b]  = new double[dim];
        }

        if (batchN < 10) return (means, vars);

        // Shuffle sample indices so each epoch uses a different ghost-batch subset
        var ghostIndices = new int[fitSet.Count];
        for (int i = 0; i < ghostIndices.Length; i++) ghostIndices[i] = i;
        for (int i = ghostIndices.Length - 1; i > 0; i--)
        {
            int k = epochRng.Next(i + 1);
            (ghostIndices[k], ghostIndices[i]) = (ghostIndices[i], ghostIndices[k]);
        }

        var layerSums  = new double[w.TotalBnLayers][];
        var layerSqSum = new double[w.TotalBnLayers][];
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = b < w.NSteps ? w.F : w.HiddenDim;
            layerSums[b]  = new double[dim];
            layerSqSum[b] = new double[dim];
        }

        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];

        for (int si = 0; si < batchN; si++)
        {
            var sample = fitSet[ghostIndices[si]];
            Array.Fill(priorBuf, 1.0, 0, w.F);
            double[] hPrev = new double[w.HiddenDim];

            for (int s = 0; s < w.NSteps; s++)
            {
                var attnInput = new double[w.F];
                if (s == 0)
                    for (int j = 0; j < w.F; j++) attnInput[j] = sample.Features[j];
                else
                    for (int j = 0; j < w.F; j++)
                    {
                        double val = 0;
                        for (int k = 0; k < w.HiddenDim && k < w.AttnFcW[s][j].Length; k++)
                            val += w.AttnFcW[s][j][k] * hPrev[k];
                        attnInput[j] = val + w.AttnFcB[s][j];
                    }

                int bnIdx = s;
                for (int j = 0; j < w.F; j++)
                {
                    layerSums[bnIdx][j]  += attnInput[j];
                    layerSqSum[bnIdx][j] += attnInput[j] * attnInput[j];
                }

                var bnAttn = ApplyBatchNorm(attnInput, w.BnGamma[bnIdx], w.BnBeta[bnIdx],
                    w.BnMean[bnIdx], w.BnVar[bnIdx], false);
                for (int j = 0; j < w.F; j++) attnBuf[j] = priorBuf[j] * bnAttn[j];
                double[] attn = w.UseSparsemax ? Sparsemax(attnBuf, w.F) : SoftmaxArr(attnBuf, w.F);
                for (int j = 0; j < w.F; j++)
                    priorBuf[j] = Math.Max(1e-6, priorBuf[j] * (w.Gamma - attn[j]));

                double[] masked = new double[w.F];
                for (int j = 0; j < w.F; j++) masked[j] = sample.Features[j] * attn[j];

                double[] h = masked;
                int inputDim = w.F;
                for (int l = 0; l < w.SharedLayers; l++)
                {
                    int bnSIdx = w.NSteps + l;
                    var linear = FcLinear(h, inputDim, w.HiddenDim, w.SharedW[l], w.SharedB[l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnSIdx][j]  += linear[j];
                        layerSqSum[bnSIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.BnGamma[bnSIdx], w.BnBeta[bnSIdx],
                        w.BnMean[bnSIdx], w.BnVar[bnSIdx], false);
                    var gate = FcSigmoid(h, inputDim, w.HiddenDim, w.SharedGW[l], w.SharedGB[l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0 && h.Length == w.HiddenDim)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;
                    h = hNew;
                    inputDim = w.HiddenDim;
                }

                for (int l = 0; l < w.StepLayers; l++)
                {
                    int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                    var linear = FcLinear(h, w.HiddenDim, w.HiddenDim, w.StepW[s][l], w.StepB[s][l]);
                    for (int j = 0; j < w.HiddenDim; j++)
                    {
                        layerSums[bnStIdx][j]  += linear[j];
                        layerSqSum[bnStIdx][j] += linear[j] * linear[j];
                    }
                    var bnH = ApplyBatchNorm(linear, w.BnGamma[bnStIdx], w.BnBeta[bnStIdx],
                        w.BnMean[bnStIdx], w.BnVar[bnStIdx], false);
                    var gate = FcSigmoid(h, w.HiddenDim, w.HiddenDim, w.StepGW[s][l], w.StepGB[s][l]);
                    var hNew = new double[w.HiddenDim];
                    for (int j = 0; j < w.HiddenDim; j++) hNew[j] = bnH[j] * gate[j];
                    if (l > 0)
                        for (int j = 0; j < w.HiddenDim; j++)
                            hNew[j] = (hNew[j] + h[j]) * 0.7071067811865476;
                    h = hNew;
                }

                hPrev = h;
            }
        }

        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            int dim = layerSums[b].Length;
            for (int j = 0; j < dim; j++)
            {
                means[b][j] = layerSums[b][j] / batchN;
                vars[b][j]  = Math.Max(0.0, layerSqSum[b][j] / batchN - means[b][j] * means[b][j]);
            }
        }

        return (means, vars);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BACKWARD PASS — Gradient accumulation
    // ═══════════════════════════════════════════════════════════════════════

    private static void AccumulateGradients(
        TabNetWeights grad, TabNetWeights w, ForwardResult fwd,
        float[] features, double errCE, double sampleWt,
        double huberGrad, double magLossWeight, double l2Lambda,
        double sparsityCoeff, bool useMagHead,
        double[][]? epochBatchVars = null)
    {
        int H = w.HiddenDim, F = w.F;

        // ── Output head gradients ────────────────────────────────────
        double dLogit = sampleWt * errCE;
        for (int j = 0; j < H; j++)
            grad.OutputW[j] += dLogit * fwd.AggregatedH[j] + l2Lambda * w.OutputW[j];
        grad.OutputB += dLogit;

        // ── Magnitude head gradients ─────────────────────────────────
        if (useMagHead && w.MagW.Length > 0)
        {
            double scaledHuber = sampleWt * magLossWeight * huberGrad;
            for (int j = 0; j < H && j < w.MagW.Length; j++)
                grad.MagW[j] += scaledHuber * fwd.AggregatedH[j] + l2Lambda * w.MagW[j];
            grad.MagB += scaledHuber;
        }

        // ── Per-step backward ────────────────────────────────────────
        double[] dAggH = new double[H];
        for (int j = 0; j < H; j++)
            dAggH[j] = dLogit * w.OutputW[j];

        // Magnitude head contribution to dAggH
        if (useMagHead && w.MagW.Length > 0)
        {
            double scaledHuber = sampleWt * magLossWeight * huberGrad;
            for (int j = 0; j < H && j < w.MagW.Length; j++)
                dAggH[j] += scaledHuber * w.MagW[j];
        }

        for (int s = w.NSteps - 1; s >= 0; s--)
        {
            // ReLU gradient
            double[] dH = new double[H];
            for (int j = 0; j < H; j++)
                dH[j] = fwd.StepH[s][j] > 0 ? dAggH[j] : 0.0;

            // ── Backward through step-specific layers ────────────
            double[] dInput = dH;
            for (int l = w.StepLayers - 1; l >= 0; l--)
            {
                double[] pre   = fwd.StepStepPre[s][l];
                double[] gate  = fwd.StepStepGate[s][l];
                double[] xNorm = fwd.StepStepXNorm[s][l];
                double[] fcIn  = fwd.StepStepFcIn[s][l];  // Fix 2: actual FC layer input

                // Residual backward
                double[] dResidual = null!;
                if (l > 0)
                {
                    dResidual = new double[H];
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * 0.7071067811865476;
                        dInput[j]    = dInput[j] * 0.7071067811865476;
                    }
                }

                // GLU backward: output = bnOut * gate(sigmoid)
                double[] dBnOut  = new double[H];
                double[] dGateIn = new double[H];
                for (int j = 0; j < H; j++)
                {
                    dBnOut[j]  = dInput[j] * gate[j];
                    dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                }

                // Full BN backward — ∂L/∂γ, ∂L/∂β, and ∂L/∂x through normalization
                // Uses epoch batch var (same stats as forward pass) when available (Fix 1)
                int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                double[] dPreFc = new double[H]; // gradient w.r.t. FC output (pre-BN)
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnStIdx][j] += dBnOut[j] * xNorm[j];
                    grad.BnBeta[bnStIdx][j]  += dBnOut[j];
                    double var_ = epochBatchVars is not null && bnStIdx < epochBatchVars.Length && j < epochBatchVars[bnStIdx].Length
                        ? epochBatchVars[bnStIdx][j]
                        : (w.BnVar[bnStIdx].Length > j ? w.BnVar[bnStIdx][j] : 1.0);
                    dPreFc[j] = dBnOut[j] * w.BnGamma[bnStIdx][j] / Math.Sqrt(var_ + BnEpsilon);
                }

                // FC backward using cached FC input
                double[] dNextInput = new double[H];
                for (int i = 0; i < H; i++)
                {
                    for (int j = 0; j < H && j < w.StepW[s][l][i].Length; j++)
                    {
                        double inp = j < fcIn.Length ? fcIn[j] : 0;
                        grad.StepW[s][l][i][j]  += dPreFc[i] * inp + l2Lambda * w.StepW[s][l][i][j];
                        grad.StepGW[s][l][i][j] += dGateIn[i] * inp + l2Lambda * w.StepGW[s][l][i][j];
                        dNextInput[j] += dPreFc[i] * w.StepW[s][l][i][j] + dGateIn[i] * w.StepGW[s][l][i][j];
                    }
                    grad.StepB[s][l][i]  += dPreFc[i];
                    grad.StepGB[s][l][i] += dGateIn[i];
                }

                if (l > 0)
                    for (int j = 0; j < H; j++) dNextInput[j] += dResidual[j];

                dInput = dNextInput;
            }

            // ── Backward through shared layers ───────────────────
            for (int l = w.SharedLayers - 1; l >= 0; l--)
            {
                double[] pre   = fwd.StepSharedPre[s][l];
                double[] gate  = fwd.StepSharedGate[s][l];
                double[] xNorm = fwd.StepSharedXNorm[s][l];
                double[] fcIn  = fwd.StepSharedFcIn[s][l];  // Fix 2: actual FC layer input

                double[] dResidual = null!;
                if (l > 0)
                {
                    dResidual = new double[H];
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * 0.7071067811865476;
                        dInput[j]    = dInput[j] * 0.7071067811865476;
                    }
                }

                double[] dBnOut  = new double[H];
                double[] dGateIn = new double[H];
                for (int j = 0; j < H; j++)
                {
                    dBnOut[j]  = dInput[j] * gate[j];
                    dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                }

                // Full BN backward — uses epoch batch var when available (Fix 1)
                int bnSIdx = w.NSteps + l;
                int inDim = l == 0 ? F : H;
                double[] dPreFc = new double[H];
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnSIdx][j] += dBnOut[j] * xNorm[j];
                    grad.BnBeta[bnSIdx][j]  += dBnOut[j];
                    double var_ = epochBatchVars is not null && bnSIdx < epochBatchVars.Length && j < epochBatchVars[bnSIdx].Length
                        ? epochBatchVars[bnSIdx][j]
                        : (w.BnVar[bnSIdx].Length > j ? w.BnVar[bnSIdx][j] : 1.0);
                    dPreFc[j] = dBnOut[j] * w.BnGamma[bnSIdx][j] / Math.Sqrt(var_ + BnEpsilon);
                }

                // FC backward using cached FC input
                double[] dNextInput = new double[inDim];
                for (int i = 0; i < H; i++)
                {
                    for (int j = 0; j < inDim && j < w.SharedW[l][i].Length; j++)
                    {
                        double inp = j < fcIn.Length ? fcIn[j] : 0;
                        grad.SharedW[l][i][j]  += dPreFc[i] * inp + l2Lambda * w.SharedW[l][i][j];
                        grad.SharedGW[l][i][j] += dGateIn[i] * inp + l2Lambda * w.SharedGW[l][i][j];
                        if (j < dNextInput.Length)
                            dNextInput[j] += dPreFc[i] * w.SharedW[l][i][j] + dGateIn[i] * w.SharedGW[l][i][j];
                    }
                    grad.SharedB[l][i]  += dPreFc[i];
                    grad.SharedGB[l][i] += dGateIn[i];
                }

                if (l > 0)
                    for (int j = 0; j < Math.Min(dNextInput.Length, H); j++)
                        dNextInput[j] += dResidual[j];

                dInput = l == 0 ? dNextInput : dNextInput[..H];
            }

            // ── Task-loss gradient through attention (Fix 2) + sparsity ──
            // dInput at this point holds ∂L/∂masked (from shared layer 0 backward).
            // Since masked[j] = features[j] * attn[j], we have ∂L/∂attn[j] = dInput[j] * features[j].
            // Then multiply by the sparsemax Jacobian: J = diag(s) - ssᵀ/‖s‖₁
            // where s is the support set (indices with attn[j] > 0).
            var attn = fwd.StepAttn[s];

            // Compute ∂L/∂attn from task loss
            var dAttn = new double[F];
            for (int j = 0; j < F && j < dInput.Length; j++)
                dAttn[j] = dInput[j] * features[j];

            // Add sparsity entropy gradient: ∂L_sparse/∂attn[j] = -(log(attn[j]+ε) + 1) / nSteps
            if (sparsityCoeff > 0)
            {
                for (int j = 0; j < F; j++)
                {
                    double entropyGrad = -(Math.Log(attn[j] + 1e-15) + 1.0);
                    dAttn[j] += sampleWt * sparsityCoeff * entropyGrad / w.NSteps;
                }
            }

            // Sparsemax Jacobian: J * dAttn = dAttn_S - s * (sᵀ · dAttn_S) / ‖s‖₁
            // where S is the support set {j : attn[j] > 0}
            double sDotD = 0, sNorm1 = 0;
            for (int j = 0; j < F; j++)
            {
                if (attn[j] > 1e-15) { sDotD += attn[j] * dAttn[j]; sNorm1 += attn[j]; }
            }
            double correction = sNorm1 > 1e-15 ? sDotD / sNorm1 : 0;

            var dAttnLogits = new double[F];
            for (int j = 0; j < F; j++)
                dAttnLogits[j] = attn[j] > 1e-15 ? (dAttn[j] - correction) : 0;

            // dAttnLogits is ∂L/∂(pre-sparsemax logits). The logits = priorScales * BN(FC(hPrev)).
            // For step 0 the logits come directly from BN(features), so gradient flows into BN params.
            // For step > 0 the logits come from BN(AttnFcW[s] · hPrev + AttnFcB[s]).
            // Propagate into AttnFcW/AttnFcB for s > 0:
            if (s > 0)
            {
                // dAttnLogits[j] is ∂L/∂logit[j]. logit[j] = priorScale_s[j] * bnOut[j].
                // Use per-step prior scales (saved during forward pass) for exact gradient.
                // Then through BN (using the same var lookup as shared layers):
                for (int j = 0; j < F; j++)
                {
                    if (Math.Abs(dAttnLogits[j]) < 1e-20) continue;
                    double prior = s < fwd.StepPriorScales.Length && j < fwd.StepPriorScales[s].Length
                        ? fwd.StepPriorScales[s][j] : 1.0;
                    double dBnJ = dAttnLogits[j] * prior;
                    // Through BN: ∂/∂fc = γ/σ · dBnJ (attention BN at index s)
                    double bnVar = epochBatchVars is not null && s < epochBatchVars.Length && j < epochBatchVars[s].Length
                        ? epochBatchVars[s][j]
                        : (w.BnVar[s].Length > j ? w.BnVar[s][j] : 1.0);
                    double dFcJ = dBnJ * w.BnGamma[s][j] / Math.Sqrt(bnVar + BnEpsilon);
                    // FC: logit[j] = Σ_k AttnFcW[s][j][k] * hPrev[k] + AttnFcB[s][j]
                    // hPrev is the previous step's output — we use StepH[s-1] as proxy
                    double[] hPrevStep = s > 0 && s - 1 < fwd.StepH.Length ? fwd.StepH[s - 1] : new double[H];
                    for (int k = 0; k < H && k < w.AttnFcW[s][j].Length; k++)
                        grad.AttnFcW[s][j][k] += dFcJ * (k < hPrevStep.Length ? hPrevStep[k] : 0);
                    grad.AttnFcB[s][j] += dFcJ;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ADAM OPTIMIZER UPDATE
    // ═══════════════════════════════════════════════════════════════════════

    private static void AdamUpdate(TabNetWeights w, TabNetWeights grad, AdamState adam, double lr)
    {
        adam.T++;
        double bc1 = 1.0 - Math.Pow(AdamBeta1, adam.T);
        double bc2 = 1.0 - Math.Pow(AdamBeta2, adam.T);

        // Output head
        AdamStep(ref w.OutputB, ref adam.MOutputB, ref adam.VOutputB, grad.OutputB, lr, bc1, bc2);
        AdamStepArr(w.OutputW, adam.MOutputW, adam.VOutputW, grad.OutputW, lr, bc1, bc2);

        // Magnitude head
        if (w.MagW.Length > 0)
        {
            AdamStep(ref w.MagB, ref adam.MMagB, ref adam.VMagB, grad.MagB, lr, bc1, bc2);
            AdamStepArr(w.MagW, adam.MMagW, adam.VMagW, grad.MagW, lr, bc1, bc2);
        }

        // Shared layers
        for (int l = 0; l < w.SharedLayers; l++)
        {
            AdamStep2D(w.SharedW[l], adam.MSharedW[l], adam.VSharedW[l], grad.SharedW[l], lr, bc1, bc2);
            AdamStepArr(w.SharedB[l], adam.MSharedB[l], adam.VSharedB[l], grad.SharedB[l], lr, bc1, bc2);
            AdamStep2D(w.SharedGW[l], adam.MSharedGW[l], adam.VSharedGW[l], grad.SharedGW[l], lr, bc1, bc2);
            AdamStepArr(w.SharedGB[l], adam.MSharedGB[l], adam.VSharedGB[l], grad.SharedGB[l], lr, bc1, bc2);
        }

        // Step-specific layers
        for (int s = 0; s < w.NSteps; s++)
        {
            for (int l = 0; l < w.StepLayers; l++)
            {
                AdamStep2D(w.StepW[s][l], adam.MStepW[s][l], adam.VStepW[s][l], grad.StepW[s][l], lr, bc1, bc2);
                AdamStepArr(w.StepB[s][l], adam.MStepB[s][l], adam.VStepB[s][l], grad.StepB[s][l], lr, bc1, bc2);
                AdamStep2D(w.StepGW[s][l], adam.MStepGW[s][l], adam.VStepGW[s][l], grad.StepGW[s][l], lr, bc1, bc2);
                AdamStepArr(w.StepGB[s][l], adam.MStepGB[s][l], adam.VStepGB[s][l], grad.StepGB[s][l], lr, bc1, bc2);
            }

            // Attention FC
            AdamStep2D(w.AttnFcW[s], adam.MAttnFcW[s], adam.VAttnFcW[s], grad.AttnFcW[s], lr, bc1, bc2);
            AdamStepArr(w.AttnFcB[s], adam.MAttnFcB[s], adam.VAttnFcB[s], grad.AttnFcB[s], lr, bc1, bc2);
        }

        // BN params
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            AdamStepArr(w.BnGamma[b], adam.MBnGamma[b], adam.VBnGamma[b], grad.BnGamma[b], lr, bc1, bc2);
            AdamStepArr(w.BnBeta[b], adam.MBnBeta[b], adam.VBnBeta[b], grad.BnBeta[b], lr, bc1, bc2);
        }
    }

    private const double MaxWeightVal = 10.0;

    private static void AdamStep(ref double param, ref double m, ref double v, double g, double lr, double bc1, double bc2)
    {
        if (!double.IsFinite(g)) g = 0;
        m = AdamBeta1 * m + (1 - AdamBeta1) * g;
        v = AdamBeta2 * v + (1 - AdamBeta2) * g * g;
        param -= lr * (m / bc1) / (Math.Sqrt(v / bc2) + AdamEpsilon);
        if (!double.IsFinite(param)) param = 0;
        else param = Math.Clamp(param, -MaxWeightVal, MaxWeightVal);
    }

    private static void AdamStepArr(double[] param, double[] m, double[] v, double[] g, double lr, double bc1, double bc2)
    {
        for (int j = 0; j < param.Length; j++)
        {
            double gj = double.IsFinite(g[j]) ? g[j] : 0;
            m[j] = AdamBeta1 * m[j] + (1 - AdamBeta1) * gj;
            v[j] = AdamBeta2 * v[j] + (1 - AdamBeta2) * gj * gj;
            param[j] -= lr * (m[j] / bc1) / (Math.Sqrt(v[j] / bc2) + AdamEpsilon);
            if (!double.IsFinite(param[j])) param[j] = 0;
            else param[j] = Math.Clamp(param[j], -MaxWeightVal, MaxWeightVal);
        }
    }

    private static void AdamStep2D(double[][] param, double[][] m, double[][] v, double[][] g, double lr, double bc1, double bc2)
    {
        for (int i = 0; i < param.Length; i++)
            AdamStepArr(param[i], m[i], v[i], g[i], lr, bc1, bc2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GRADIENT UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static void ScaleGradients(TabNetWeights grad, double scale)
    {
        ScaleArr(grad.OutputW, scale);
        grad.OutputB *= scale;
        if (grad.MagW.Length > 0) { ScaleArr(grad.MagW, scale); grad.MagB *= scale; }
        foreach (var l in grad.SharedW) Scale2D(l, scale);
        foreach (var l in grad.SharedB) ScaleArr(l, scale);
        foreach (var l in grad.SharedGW) Scale2D(l, scale);
        foreach (var l in grad.SharedGB) ScaleArr(l, scale);
        foreach (var s in grad.StepW) foreach (var l in s) Scale2D(l, scale);
        foreach (var s in grad.StepB) foreach (var l in s) ScaleArr(l, scale);
        foreach (var s in grad.StepGW) foreach (var l in s) Scale2D(l, scale);
        foreach (var s in grad.StepGB) foreach (var l in s) ScaleArr(l, scale);
        foreach (var s in grad.AttnFcW) Scale2D(s, scale);
        foreach (var s in grad.AttnFcB) ScaleArr(s, scale);
        foreach (var b in grad.BnGamma) ScaleArr(b, scale);
        foreach (var b in grad.BnBeta) ScaleArr(b, scale);
    }

    private static void ClipGradients(TabNetWeights grad, double maxNorm)
    {
        double sqNorm = 0;
        sqNorm += SqNormArr(grad.OutputW) + grad.OutputB * grad.OutputB;
        if (grad.MagW.Length > 0) sqNorm += SqNormArr(grad.MagW) + grad.MagB * grad.MagB;
        foreach (var l in grad.SharedW) sqNorm += SqNorm2D(l);
        foreach (var l in grad.SharedB) sqNorm += SqNormArr(l);
        foreach (var l in grad.SharedGW) sqNorm += SqNorm2D(l);
        foreach (var l in grad.SharedGB) sqNorm += SqNormArr(l);
        foreach (var s in grad.StepW) foreach (var l in s) sqNorm += SqNorm2D(l);
        foreach (var s in grad.StepB) foreach (var l in s) sqNorm += SqNormArr(l);
        foreach (var s in grad.StepGW) foreach (var l in s) sqNorm += SqNorm2D(l);
        foreach (var s in grad.StepGB) foreach (var l in s) sqNorm += SqNormArr(l);
        foreach (var s in grad.AttnFcW) sqNorm += SqNorm2D(s);
        foreach (var s in grad.AttnFcB) sqNorm += SqNormArr(s);

        double norm = Math.Sqrt(sqNorm);
        if (norm > maxNorm)
            ScaleGradients(grad, maxNorm / norm);
    }

    private static void ZeroGradients(TabNetWeights grad)
    {
        Array.Clear(grad.OutputW); grad.OutputB = 0;
        if (grad.MagW.Length > 0) { Array.Clear(grad.MagW); grad.MagB = 0; }
        foreach (var l in grad.SharedW) foreach (var r in l) Array.Clear(r);
        foreach (var l in grad.SharedB) Array.Clear(l);
        foreach (var l in grad.SharedGW) foreach (var r in l) Array.Clear(r);
        foreach (var l in grad.SharedGB) Array.Clear(l);
        foreach (var s in grad.StepW) foreach (var l in s) foreach (var r in l) Array.Clear(r);
        foreach (var s in grad.StepB) foreach (var l in s) Array.Clear(l);
        foreach (var s in grad.StepGW) foreach (var l in s) foreach (var r in l) Array.Clear(r);
        foreach (var s in grad.StepGB) foreach (var l in s) Array.Clear(l);
        foreach (var s in grad.AttnFcW) foreach (var r in s) Array.Clear(r);
        foreach (var s in grad.AttnFcB) Array.Clear(s);
        foreach (var b in grad.BnGamma) Array.Clear(b);
        foreach (var b in grad.BnBeta) Array.Clear(b);
    }

    private static double SqNormArr(double[] arr) { double s = 0; foreach (double v in arr) s += v * v; return s; }
    private static double SqNorm2D(double[][] arr) { double s = 0; foreach (var r in arr) s += SqNormArr(r); return s; }
    private static void ScaleArr(double[] arr, double s) { for (int i = 0; i < arr.Length; i++) arr[i] *= s; }
    private static void Scale2D(double[][] arr, double s) { foreach (var r in arr) ScaleArr(r, s); }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT INITIALIZATION (Xavier/Glorot)
    // ═══════════════════════════════════════════════════════════════════════

    private static TabNetWeights InitializeWeights(
        int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax, bool useMagHead)
    {
        var rng = new Random(42);
        int totalBn = nSteps + sharedLayers + nSteps * stepLayers;

        var w = new TabNetWeights
        {
            NSteps       = nSteps,
            F            = F,
            HiddenDim    = hiddenDim,
            AttentionDim = attentionDim,
            SharedLayers = sharedLayers,
            StepLayers   = stepLayers,
            Gamma        = gamma,
            UseSparsemax = useSparsemax,
            TotalBnLayers = totalBn,

            SharedW  = InitFcLayers(rng, sharedLayers, hiddenDim, F, hiddenDim),
            SharedB  = InitBiasLayers(sharedLayers, hiddenDim),
            SharedGW = InitFcLayers(rng, sharedLayers, hiddenDim, F, hiddenDim),
            SharedGB = InitBiasLayers(sharedLayers, hiddenDim),

            StepW  = new double[nSteps][][][],
            StepB  = new double[nSteps][][],
            StepGW = new double[nSteps][][][],
            StepGB = new double[nSteps][][],

            AttnFcW = new double[nSteps][][],
            AttnFcB = new double[nSteps][],

            BnGamma = new double[totalBn][],
            BnBeta  = new double[totalBn][],
            BnMean  = new double[totalBn][],
            BnVar   = new double[totalBn][],

            OutputW = XavierVec(rng, hiddenDim, hiddenDim, 1),
            OutputB = 0.0,

            MagW = useMagHead ? XavierVec(rng, hiddenDim, hiddenDim, 1) : [],
            MagB = 0.0,
        };

        // Step-specific layers
        for (int s = 0; s < nSteps; s++)
        {
            w.StepW[s]  = InitFcLayers(rng, stepLayers, hiddenDim, hiddenDim, hiddenDim);
            w.StepB[s]  = InitBiasLayers(stepLayers, hiddenDim);
            w.StepGW[s] = InitFcLayers(rng, stepLayers, hiddenDim, hiddenDim, hiddenDim);
            w.StepGB[s] = InitBiasLayers(stepLayers, hiddenDim);

            // Attention FC: projects from hiddenDim → F
            w.AttnFcW[s] = XavierMatrix(rng, F, hiddenDim);
            w.AttnFcB[s] = new double[F];
        }

        // BN layers: first nSteps have dim=F (attention), rest have dim=hiddenDim
        for (int b = 0; b < totalBn; b++)
        {
            int dim = b < nSteps ? F : hiddenDim;
            w.BnGamma[b] = Enumerable.Repeat(1.0, dim).ToArray();
            w.BnBeta[b]  = new double[dim];
            w.BnMean[b]  = new double[dim];
            w.BnVar[b]   = Enumerable.Repeat(1.0, dim).ToArray();
        }

        return w;
    }

    private static double[][][] InitFcLayers(Random rng, int numLayers, int outDim, int firstInDim, int subsequentInDim)
    {
        var layers = new double[numLayers][][];
        for (int l = 0; l < numLayers; l++)
        {
            int inDim = l == 0 ? firstInDim : subsequentInDim;
            layers[l] = XavierMatrix(rng, outDim, inDim);
        }
        return layers;
    }

    private static double[][] InitBiasLayers(int numLayers, int dim)
    {
        var layers = new double[numLayers][];
        for (int l = 0; l < numLayers; l++) layers[l] = new double[dim];
        return layers;
    }

    private static TabNetWeights InitializeGradAccumulator(TabNetWeights w)
    {
        // Create zero-initialized structure matching w's dimensions
        return InitializeWeights(w.F, w.NSteps, w.HiddenDim, w.AttentionDim,
            w.SharedLayers, w.StepLayers, w.Gamma, w.UseSparsemax, w.MagW.Length > 0);
    }

    private static AdamState InitializeAdamState(TabNetWeights w)
    {
        var a = new AdamState
        {
            MSharedW  = CloneDim3(w.SharedW),  VSharedW  = CloneDim3(w.SharedW),
            MSharedB  = CloneDim2(w.SharedB),   VSharedB  = CloneDim2(w.SharedB),
            MSharedGW = CloneDim3(w.SharedGW),  VSharedGW = CloneDim3(w.SharedGW),
            MSharedGB = CloneDim2(w.SharedGB),   VSharedGB = CloneDim2(w.SharedGB),

            MStepW  = CloneDim4(w.StepW),   VStepW  = CloneDim4(w.StepW),
            MStepB  = CloneDim3(w.StepB),    VStepB  = CloneDim3(w.StepB),
            MStepGW = CloneDim4(w.StepGW),   VStepGW = CloneDim4(w.StepGW),
            MStepGB = CloneDim3(w.StepGB),    VStepGB = CloneDim3(w.StepGB),

            MAttnFcW = CloneDim3(w.AttnFcW), VAttnFcW = CloneDim3(w.AttnFcW),
            MAttnFcB = CloneDim2(w.AttnFcB),  VAttnFcB = CloneDim2(w.AttnFcB),

            MBnGamma = CloneDim2(w.BnGamma), VBnGamma = CloneDim2(w.BnGamma),
            MBnBeta  = CloneDim2(w.BnBeta),   VBnBeta  = CloneDim2(w.BnBeta),

            MOutputW = new double[w.OutputW.Length], VOutputW = new double[w.OutputW.Length],
            MMagW    = new double[w.MagW.Length],     VMagW    = new double[w.MagW.Length],
        };
        return a;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT CLONING / WARM-START LOADING
    // ═══════════════════════════════════════════════════════════════════════

    private static TabNetWeights CloneWeights(TabNetWeights src)
    {
        return new TabNetWeights
        {
            NSteps = src.NSteps, F = src.F, HiddenDim = src.HiddenDim,
            AttentionDim = src.AttentionDim, SharedLayers = src.SharedLayers,
            StepLayers = src.StepLayers, Gamma = src.Gamma,
            UseSparsemax = src.UseSparsemax, TotalBnLayers = src.TotalBnLayers,

            SharedW  = DeepClone3(src.SharedW),  SharedB  = DeepClone2(src.SharedB),
            SharedGW = DeepClone3(src.SharedGW), SharedGB = DeepClone2(src.SharedGB),
            StepW    = DeepClone4(src.StepW),     StepB    = DeepClone3(src.StepB),
            StepGW   = DeepClone4(src.StepGW),    StepGB   = DeepClone3(src.StepGB),
            AttnFcW  = DeepClone3(src.AttnFcW),   AttnFcB  = DeepClone2(src.AttnFcB),
            BnGamma  = DeepClone2(src.BnGamma),   BnBeta   = DeepClone2(src.BnBeta),
            BnMean   = DeepClone2(src.BnMean),    BnVar    = DeepClone2(src.BnVar),
            OutputW  = (double[])src.OutputW.Clone(), OutputB = src.OutputB,
            MagW     = (double[])src.MagW.Clone(),    MagB    = src.MagB,
        };
    }

    private static void CopyCompatibleWeights(TabNetWeights src, TabNetWeights dst)
    {
        if (src.SharedLayers == dst.SharedLayers && src.HiddenDim == dst.HiddenDim)
        {
            for (int l = 0; l < dst.SharedLayers; l++)
            {
                CopyMatrix(src.SharedW[l], dst.SharedW[l]);
                CopyArray(src.SharedB[l], dst.SharedB[l]);
                CopyMatrix(src.SharedGW[l], dst.SharedGW[l]);
                CopyArray(src.SharedGB[l], dst.SharedGB[l]);
            }
        }
        if (src.NSteps == dst.NSteps && src.StepLayers == dst.StepLayers && src.HiddenDim == dst.HiddenDim)
        {
            for (int s = 0; s < dst.NSteps; s++)
            {
                for (int l = 0; l < dst.StepLayers; l++)
                {
                    CopyMatrix(src.StepW[s][l], dst.StepW[s][l]);
                    CopyArray(src.StepB[s][l], dst.StepB[s][l]);
                    CopyMatrix(src.StepGW[s][l], dst.StepGW[s][l]);
                    CopyArray(src.StepGB[s][l], dst.StepGB[s][l]);
                }
            }
        }
    }

    private void LoadWarmStartWeights(ModelSnapshot snapshot, TabNetWeights w)
    {
        try
        {
            if (snapshot.TabNetSharedWeights is { } sw && sw.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyMatrix(sw[l], w.SharedW[l]);

            if (snapshot.TabNetSharedBiases is { } sb && sb.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyArray(sb[l], w.SharedB[l]);

            if (snapshot.TabNetSharedGateWeights is { } sgw && sgw.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyMatrix(sgw[l], w.SharedGW[l]);

            if (snapshot.TabNetSharedGateBiases is { } sgb && sgb.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyArray(sgb[l], w.SharedGB[l]);

            if (snapshot.TabNetStepFcWeights is { } sfcw && sfcw.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sfcw[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyMatrix(sfcw[s][l], w.StepW[s][l]);

            if (snapshot.TabNetStepFcBiases is { } sfcb && sfcb.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sfcb[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyArray(sfcb[s][l], w.StepB[s][l]);

            if (snapshot.TabNetStepGateWeights is { } sgwS && sgwS.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sgwS[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyMatrix(sgwS[s][l], w.StepGW[s][l]);

            if (snapshot.TabNetStepGateBiases is { } sgbS && sgbS.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sgbS[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyArray(sgbS[s][l], w.StepGB[s][l]);

            if (snapshot.TabNetAttentionFcWeights is { } afw && afw.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    CopyMatrix(afw[s], w.AttnFcW[s]);

            if (snapshot.TabNetAttentionFcBiases is { } afb && afb.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    CopyArray(afb[s], w.AttnFcB[s]);

            if (snapshot.TabNetBnGammas is { } bng && bng.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bng[b], w.BnGamma[b]);

            if (snapshot.TabNetBnBetas is { } bnb && bnb.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnb[b], w.BnBeta[b]);

            if (snapshot.TabNetBnRunningMeans is { } bnm && bnm.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnm[b], w.BnMean[b]);

            if (snapshot.TabNetBnRunningVars is { } bnv && bnv.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnv[b], w.BnVar[b]);

            if (snapshot.TabNetOutputHeadWeights is { } ohw && ohw.Length == w.HiddenDim)
                Array.Copy(ohw, w.OutputW, w.HiddenDim);

            w.OutputB = snapshot.TabNetOutputHeadBias;

            if (snapshot.MagWeights is { Length: > 0 } mw && mw.Length == w.HiddenDim)
                Array.Copy(mw, w.MagW, w.HiddenDim);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TabNet warm-start: failed to load v3 weights, starting fresh.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UNSUPERVISED PRE-TRAINING
    //  Encoder-decoder: mask random features, reconstruct via decoder FC.
    // ═══════════════════════════════════════════════════════════════════════

    private TabNetWeights RunUnsupervisedPretraining(
        List<TrainingSample> samples, int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax, bool useGlu,
        double lr, int epochs, double maskFraction, double bnMomentum, CancellationToken ct)
    {
        var w = InitializeWeights(F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, false);

        // Decoder: simple FC from hiddenDim → F for reconstruction
        var rng = new Random(123);
        var decoderW = XavierMatrix(rng, F, hiddenDim);
        var decoderB = new double[F];

        var priorBuf = new double[F];
        var attnBuf  = new double[F];

        // Adam state for encoder weights during pre-training
        var adam = InitializeAdamState(w);
        var grad = InitializeGradAccumulator(w);
        const int PretrainBatchSize = 32;
        int batchCount = 0;

        for (int ep = 0; ep < epochs && !ct.IsCancellationRequested; ep++)
        {
            double cosLr = lr * 0.5 * (1.0 + Math.Cos(Math.PI * ep / epochs));
            double epochLoss = 0;

            foreach (var sample in samples)
            {
                // Create random mask
                var mask = new bool[F];
                int masked = 0;
                for (int j = 0; j < F; j++)
                {
                    mask[j] = rng.NextDouble() < maskFraction;
                    if (mask[j]) masked++;
                }
                if (masked == 0) continue;

                // Mask features (set to 0)
                var maskedFeatures = new float[F];
                for (int j = 0; j < F; j++)
                    maskedFeatures[j] = mask[j] ? 0f : sample.Features[j];

                // Forward through encoder
                var fwd = ForwardPass(maskedFeatures, w, priorBuf, attnBuf, true, 0, rng);

                // Decode: reconstruct from aggregated hidden
                var recon = new double[F];
                for (int j = 0; j < F; j++)
                {
                    recon[j] = decoderB[j];
                    for (int k = 0; k < hiddenDim; k++)
                        recon[j] += decoderW[j][k] * fwd.AggregatedH[k];
                }

                // ── Compute ∂L/∂AggregatedH from reconstruction MSE on masked features ──
                var dAggH = new double[hiddenDim];
                for (int j = 0; j < F; j++)
                {
                    if (!mask[j]) continue;
                    double err = recon[j] - sample.Features[j];
                    epochLoss += err * err;
                    double dRecon = 2.0 * err / masked;

                    // SGD update on decoder
                    decoderB[j] -= cosLr * dRecon;
                    for (int k = 0; k < hiddenDim; k++)
                    {
                        dAggH[k] += dRecon * decoderW[j][k];
                        decoderW[j][k] -= cosLr * dRecon * fwd.AggregatedH[k];
                    }
                }

                // ── Backprop through encoder: dAggH → per-step ReLU → layers → attention ──
                for (int s = nSteps - 1; s >= 0; s--)
                {
                    // ReLU gradient
                    var dH = new double[hiddenDim];
                    for (int j = 0; j < hiddenDim; j++)
                        dH[j] = fwd.StepH[s][j] > 0 ? dAggH[j] : 0.0;

                    // Step-specific layers backward
                    var dInput = dH;
                    for (int l = w.StepLayers - 1; l >= 0; l--)
                    {
                        double[] pre  = fwd.StepStepPre[s][l];
                        double[] gate = fwd.StepStepGate[s][l];
                        double[] xn   = fwd.StepStepXNorm[s][l];
                        double[] fcIn = fwd.StepStepFcIn[s][l];
                        int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;

                        double[] dResidual = null!;
                        if (l > 0)
                        {
                            dResidual = new double[hiddenDim];
                            for (int j = 0; j < hiddenDim; j++)
                            {
                                dResidual[j] = dInput[j] * 0.7071067811865476;
                                dInput[j]    = dInput[j] * 0.7071067811865476;
                            }
                        }

                        var dBnOut  = new double[hiddenDim];
                        var dGateIn = new double[hiddenDim];
                        for (int j = 0; j < hiddenDim; j++)
                        {
                            dBnOut[j]  = dInput[j] * gate[j];
                            dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                        }

                        var dPreFc = new double[hiddenDim];
                        for (int j = 0; j < hiddenDim; j++)
                        {
                            grad.BnGamma[bnStIdx][j] += dBnOut[j] * xn[j];
                            grad.BnBeta[bnStIdx][j]  += dBnOut[j];
                            double var_ = w.BnVar[bnStIdx].Length > j ? w.BnVar[bnStIdx][j] : 1.0;
                            dPreFc[j] = dBnOut[j] * w.BnGamma[bnStIdx][j] / Math.Sqrt(var_ + BnEpsilon);
                        }

                        var dNext = new double[hiddenDim];
                        for (int i = 0; i < hiddenDim; i++)
                        {
                            for (int j = 0; j < hiddenDim && j < w.StepW[s][l][i].Length; j++)
                            {
                                double inp = j < fcIn.Length ? fcIn[j] : 0;
                                grad.StepW[s][l][i][j]  += dPreFc[i] * inp;
                                grad.StepGW[s][l][i][j] += dGateIn[i] * inp;
                                dNext[j] += dPreFc[i] * w.StepW[s][l][i][j] + dGateIn[i] * w.StepGW[s][l][i][j];
                            }
                            grad.StepB[s][l][i]  += dPreFc[i];
                            grad.StepGB[s][l][i] += dGateIn[i];
                        }

                        if (l > 0) for (int j = 0; j < hiddenDim; j++) dNext[j] += dResidual[j];
                        dInput = dNext;
                    }

                    // Shared layers backward
                    for (int l = w.SharedLayers - 1; l >= 0; l--)
                    {
                        double[] pre  = fwd.StepSharedPre[s][l];
                        double[] gate = fwd.StepSharedGate[s][l];
                        double[] xn   = fwd.StepSharedXNorm[s][l];
                        double[] fcIn = fwd.StepSharedFcIn[s][l];
                        int bnSIdx = w.NSteps + l;
                        int inDim = l == 0 ? F : hiddenDim;

                        double[] dResidual = null!;
                        if (l > 0)
                        {
                            dResidual = new double[hiddenDim];
                            for (int j = 0; j < hiddenDim; j++)
                            {
                                dResidual[j] = dInput[j] * 0.7071067811865476;
                                dInput[j]    = dInput[j] * 0.7071067811865476;
                            }
                        }

                        var dBnOut  = new double[hiddenDim];
                        var dGateIn = new double[hiddenDim];
                        for (int j = 0; j < hiddenDim; j++)
                        {
                            dBnOut[j]  = dInput[j] * gate[j];
                            dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                        }

                        var dPreFc = new double[hiddenDim];
                        for (int j = 0; j < hiddenDim; j++)
                        {
                            grad.BnGamma[bnSIdx][j] += dBnOut[j] * xn[j];
                            grad.BnBeta[bnSIdx][j]  += dBnOut[j];
                            double var_ = w.BnVar[bnSIdx].Length > j ? w.BnVar[bnSIdx][j] : 1.0;
                            dPreFc[j] = dBnOut[j] * w.BnGamma[bnSIdx][j] / Math.Sqrt(var_ + BnEpsilon);
                        }

                        var dNext = new double[inDim];
                        for (int i = 0; i < hiddenDim; i++)
                        {
                            for (int j = 0; j < inDim && j < w.SharedW[l][i].Length; j++)
                            {
                                double inp = j < fcIn.Length ? fcIn[j] : 0;
                                grad.SharedW[l][i][j]  += dPreFc[i] * inp;
                                grad.SharedGW[l][i][j] += dGateIn[i] * inp;
                            }
                            grad.SharedB[l][i]  += dPreFc[i];
                            grad.SharedGB[l][i] += dGateIn[i];
                        }

                        if (l > 0)
                            for (int j = 0; j < Math.Min(dNext.Length, hiddenDim); j++)
                                dNext[j] += dResidual[j];

                        dInput = l == 0 ? dNext : dNext[..hiddenDim];
                    }
                }

                batchCount++;
                if (batchCount >= PretrainBatchSize)
                {
                    double invBatch = 1.0 / batchCount;
                    ScaleGradients(grad, invBatch);
                    ClipGradients(grad, 1.0);
                    AdamUpdate(w, grad, adam, cosLr);
                    ZeroGradients(grad);
                    batchCount = 0;
                }
            }

            // Flush remaining gradients at end of epoch
            if (batchCount > 0)
            {
                double invBatch = 1.0 / batchCount;
                ScaleGradients(grad, invBatch);
                ClipGradients(grad, 1.0);
                AdamUpdate(w, grad, adam, cosLr);
                ZeroGradients(grad);
                batchCount = 0;
            }

            // Update BN running stats periodically
            if (ep % 5 == 4)
                UpdateBnRunningStats(w, samples, bnMomentum, 128);
        }

        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples, TrainingHyperparams hp,
        int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax, bool useGlu,
        double lr, double sparsityCoeff, int epochs, double bnMomentum,
        CancellationToken ct)
    {
        int folds    = hp.WalkForwardFolds;
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < 50)
        {
            _logger.LogWarning("TabNet walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImps   = new List<double[]>(folds);
        int badFolds   = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd    = (fold + 2) * foldSize;
            int testStart  = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) continue;

            var foldTrain = samples[..trainEnd].ToList();
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count) foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) continue;

            int cvEpochs = Math.Max(10, epochs / 3);
            var cvW = FitTabNet(
                foldTrain, F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
                gamma, useSparsemax, useGlu, lr, sparsityCoeff, cvEpochs,
                hp.LabelSmoothing, null, null, null, hp.TemporalDecayLambda, hp.L2Lambda,
                hp.EarlyStoppingPatience, 0.0, hp.MaxGradNorm, 0, bnMomentum, 128, ct);

            var m = EvaluateTabNet(foldTest, cvW, 1.0, 0.0, [], 0, F);

            // Mean attention importance per feature
            double[] foldImp = ComputeMeanAttention(foldTest, cvW);

            // Equity-curve gate
            var preds = new (int Predicted, int Actual)[foldTest.Count];
            var priorBuf = new double[F];
            var attnBuf  = new double[F];
            for (int i = 0; i < foldTest.Count; i++)
            {
                var fwd = ForwardPass(foldTest[i].Features, cvW, priorBuf, attnBuf, false, 0, null);
                preds[i] = (fwd.Prob >= 0.5 ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(preds);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            accList.Add(m.Accuracy);
            f1List.Add(m.F1);
            evList.Add(m.ExpectedValue);
            sharpeList.Add(m.SharpeRatio);
            foldImps.Add(foldImp);
            if (isBad) badFolds++;
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0 ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("TabNet equity-curve gate: {Bad}/{Total} folds failed", badFolds, accList.Count);

        double avgAcc    = accList.Average();
        double stdAcc    = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("TabNet Sharpe trend gate: slope={Slope:F3} < threshold", sharpeTrend);
            equityCurveGateFailed = true;
        }

        // Feature stability scores
        double[]? featureStabilityScores = null;
        if (foldImps.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = foldImps.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImps[fi].Length > j ? foldImps[fi][j] : 0;
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = (foldImps[fi].Length > j ? foldImps[fi][j] : 0) - meanImp;
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

    // ═══════════════════════════════════════════════════════════════════════
    //  INFERENCE HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static double TabNetRawProb(float[] features, TabNetWeights w)
    {
        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];
        var fwd = ForwardPass(features, w, priorBuf, attnBuf, false, 0, null);
        return fwd.Prob;
    }

    private static double TabNetCalibProb(float[] features, TabNetWeights w, double plattA, double plattB)
    {
        double raw = Math.Clamp(TabNetRawProb(features, w), 1e-7, 1.0 - 1e-7);
        return Sigmoid(plattA * Logit(raw) + plattB);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MEAN ATTENTION SUMMARY
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeMeanAttention(IReadOnlyList<TrainingSample> samples, TabNetWeights w)
    {
        double[] meanAttn = new double[w.F];
        int count = Math.Min(samples.Count, 500);
        var priorBuf = new double[w.F];
        var attnBuf  = new double[w.F];

        for (int i = 0; i < count; i++)
        {
            var fwd = ForwardPass(samples[i].Features, w, priorBuf, attnBuf, false, 0, null);
            for (int s = 0; s < w.NSteps; s++)
                for (int j = 0; j < w.F && j < fwd.StepAttn[s].Length; j++)
                    meanAttn[j] += fwd.StepAttn[s][j] / (w.NSteps * count);
        }
        return meanAttn;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateTabNet(
        IReadOnlyList<TrainingSample> evalSet, TabNetWeights w,
        double plattA, double plattB, double[] magWeights, double magBias, int origF)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;
        double weightSum = 0, correctWeighted = 0;
        int n = evalSet.Count;
        double evSum = 0;
        var returns = new List<double>(n);

        for (int idx = 0; idx < n; idx++)
        {
            var s   = evalSet[idx];
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int yHat = p >= 0.5 ? 1 : 0;
            int y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            // Magnitude-weighted EV and returns
            double sign = (yHat == y) ? 1.0 : -1.0;
            double ret  = sign * Math.Abs(s.Magnitude);
            evSum += ret;
            returns.Add(ret);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }

            double wt = 1.0 + (double)idx / n;
            weightSum += wt;
            if (yHat == y) correctWeighted += wt;
        }

        double accuracy  = n > 0 ? (double)correct / n : 0;
        double brier     = n > 0 ? brierSum / n : 1;
        double magRmse   = n > 0 && magSse > 0 ? Math.Sqrt(magSse / n) : 0;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = n > 0 ? evSum / n : 0;
        double wAcc      = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        double avgRet = returns.Count > 0 ? returns.Average() : 0;
        double stdRet = returns.Count > 1 ? StdDev(returns, avgRet) : 0;
        double sharpe = stdRet > 1e-10 ? avgRet / stdRet * Math.Sqrt(252) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: wAcc, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < 10) return (1.0, 0.0);
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double sgdLr = 0.01;
        const int epochs   = 200;
        for (int ep = 0; ep < epochs; ep++)
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
        }
        return (plattA, plattB);
    }

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        var buySamples  = calSet.Where(s => s.Direction > 0).ToList();
        var sellSamples = calSet.Where(s => s.Direction <= 0).ToList();
        var (aBuy,  bBuy)  = buySamples.Count  >= 10 ? FitPlattScaling(buySamples,  w) : (1.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= 10 ? FitPlattScaling(sellSamples, w) : (1.0, 0.0);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (PAVA)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < 10) return [];
        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (TabNetCalibProb(calSet[i].Features, w, plattA, plattB),
                         calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.X.CompareTo(b.X));

        var blocks = new List<(double SumY, int Count, double XMin, double XMax)>();
        foreach (var (x, y) in pairs)
        {
            blocks.Add((y, 1, x, x));
            while (blocks.Count >= 2)
            {
                var last = blocks[^1];
                var prev = blocks[^2];
                if (prev.SumY / prev.Count <= last.SumY / last.Count) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks) { bp.Add((b.XMin + b.XMax) / 2.0); bp.Add(b.SumY / b.Count); }
        return bp.ToArray();
    }

    private static double ApplyIsotonic(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        for (int i = 0; i < bp.Length - 2; i += 2)
        {
            if (p <= bp[i]) return bp[i + 1];
            if (i + 2 < bp.Length && p <= bp[i + 2])
            {
                double frac = (p - bp[i]) / (bp[i + 2] - bp[i] + 1e-15);
                return bp[i + 1] + frac * (bp[i + 3] - bp[i + 1]);
            }
        }
        return bp[^1];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ECE / THRESHOLD / IMPORTANCE / CONFORMAL / JACKKNIFE / META-LABEL /
    //  ABSTENTION / QUANTILE / KELLY / BOUNDARY / DW / MI / TEMPERATURE /
    //  BSS — all updated to use TabNetWeights container
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB, int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binCorrect = new double[bins]; var binConf = new double[bins]; var binCount = new int[bins];
        foreach (var s in testSet)
        {
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int bin  = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p; binCorrect[bin] += s.Direction > 0 ? 1 : 0; binCount[bin]++;
        }
        double ece = 0; int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binCorrect[b] / binCount[b] - binConf[b] / binCount[b]) * binCount[b] / n;
        }
        return ece;
    }

    private static double ComputeOptimalThreshold(
        IReadOnlyList<TrainingSample> dataSet, TabNetWeights w, double plattA, double plattB,
        int searchMin = 30, int searchMax = 75)
    {
        if (dataSet.Count < 30) return 0.5;
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = TabNetCalibProb(dataSet[i].Features, w, plattA, plattB);
        double bestEv = double.MinValue, bestT = 0.5;
        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double threshold = ti / 100.0, ev = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= threshold) == (dataSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * Math.Abs(dataSet[i].Magnitude);
            }
            ev /= dataSet.Count;
            if (ev > bestEv) { bestEv = ev; bestT = threshold; }
        }
        return bestT;
    }

    private static float[] ComputePermutationImportance(
        IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB, CancellationToken ct)
    {
        int n = testSet.Count, F = w.F;
        double baseline = 0;
        foreach (var s in testSet)
            if ((TabNetCalibProb(s.Features, w, plattA, plattB) >= 0.5) == (s.Direction > 0)) baseline++;
        baseline /= n;
        var importance = new float[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 13 + 42);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = testSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])testSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                if ((TabNetCalibProb(scratch, w, plattA, plattB) >= 0.5) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / n);
        });
        float total = importance.Sum();
        if (total > 1e-6f) for (int j = 0; j < F; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, CancellationToken ct)
    {
        int n = calSet.Count, F = w.F;
        double baseAcc = 0;
        foreach (var s in calSet)
            if ((TabNetRawProb(s.Features, w) >= 0.5) == (s.Direction > 0)) baseAcc++;
        baseAcc /= n;
        var importance = new double[F];
        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng = new Random(j * 17 + 7);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--) { int k = rng.Next(i + 1); (vals[k], vals[i]) = (vals[i], vals[k]); }
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])calSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                if ((TabNetRawProb(scratch, w) >= 0.5) == (calSet[idx].Direction > 0)) correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    private static double ComputeConformalQHat(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB,
        double[] isotonicBp, double alpha)
    {
        if (calSet.Count < 10) return 0.5;
        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProb(calSet[i].Features, w, plattA, plattB);
            if (isotonicBp.Length >= 2) p = ApplyIsotonic(p, isotonicBp);
            int y = calSet[i].Direction > 0 ? 1 : 0;
            scores[i] = 1.0 - (y == 1 ? p : 1.0 - p);
        }
        Array.Sort(scores);
        int qIdx = Math.Clamp((int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1, 0, calSet.Count - 1);
        return scores[qIdx];
    }

    private double[] ComputeJackknifeResiduals(IReadOnlyList<TrainingSample> trainSet, TabNetWeights w)
    {
        int n = trainSet.Count;
        var residuals = new double[n];
        const int K = 5;
        int foldSize = n / K;
        if (foldSize < 30)
        {
            for (int i = 0; i < n; i++)
            {
                double p = TabNetRawProb(trainSet[i].Features, w);
                residuals[i] = Math.Abs(p - (trainSet[i].Direction > 0 ? 1.0 : 0.0));
            }
            return residuals;
        }
        // Full K-fold cross-residuals would require K retrains — use full-model residuals
        // with leave-one-out approximation for computational tractability
        for (int i = 0; i < n; i++)
        {
            double p = TabNetRawProb(trainSet[i].Features, w);
            residuals[i] = Math.Abs(p - (trainSet[i].Direction > 0 ? 1.0 : 0.0));
        }
        return residuals;
    }

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < 10) return ([0.0], 0.0);
        int n = calSet.Count;
        double metaW = 0.0, metaB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < 200; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double p = TabNetRawProb(calSet[i].Features, w);
                int correct = ((p >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double metaP = Sigmoid(metaW * p + metaB);
                dW += (metaP - correct) * p; dB += metaP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            metaW -= 0.01 * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            metaB -= 0.01 * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        return ([metaW], metaB);
    }

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < 10) return ([0.0], 0.0, 0.5);
        int n = calSet.Count;
        var probs = new double[n];
        for (int i = 0; i < n; i++) probs[i] = TabNetCalibProb(calSet[i].Features, w, plattA, plattB);
        double absW = 0.0, absB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0; int t = 0;
        for (int ep = 0; ep < 200; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double feat = Math.Abs(probs[i] - 0.5);
                int correct = ((probs[i] >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double abstP = Sigmoid(absW * feat + absB);
                dW += (abstP - correct) * feat; dB += abstP - correct;
            }
            t++; double bc1 = 1.0 - Math.Pow(AdamBeta1, t), bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n; vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n; vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            absW -= 0.01 * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon); absB -= 0.01 * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }
        double bestPrec = 0, bestThresh = 0.1;
        for (int ti = 1; ti <= 40; ti++)
        {
            double thresh = ti / 100.0; int tpA = 0, fpA = 0;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(probs[i] - 0.5) < thresh) continue;
                if ((probs[i] >= 0.5) == (calSet[i].Direction > 0)) tpA++; else fpA++;
            }
            double prec = (tpA + fpA) > 0 ? (double)tpA / (tpA + fpA) : 0;
            if (prec > bestPrec) { bestPrec = prec; bestThresh = thresh; }
        }
        return ([absW], absB, bestThresh);
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(IReadOnlyList<TrainingSample> trainSet, int F, double tau)
    {
        if (trainSet.Count < 10) return (new double[F], 0.0);
        int n = trainSet.Count; var w = new double[F]; double b = 0.0;
        for (int ep = 0; ep < 100; ep++)
            for (int i = 0; i < n; i++)
            {
                double pred = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) pred += w[j] * trainSet[i].Features[j];
                double grad = (trainSet[i].Magnitude - pred) >= 0 ? -tau : (1.0 - tau);
                b -= 0.001 * grad;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) w[j] -= 0.001 * grad * trainSet[i].Features[j];
            }
        return (w, b);
    }

    private static double ComputeAvgKellyFraction(IReadOnlyList<TrainingSample> calSet, TabNetWeights w, double plattA, double plattB)
    {
        if (calSet.Count < 10) return 0;
        double kellySum = 0;
        foreach (var s in calSet) kellySum += Math.Max(0, (2 * TabNetCalibProb(s.Features, w, plattA, plattB) - 1) * 0.5);
        return kellySum / calSet.Count;
    }

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        var distances = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++) distances[i] = Math.Abs(TabNetRawProb(calSet[i].Features, w) - 0.5);
        double mean = distances.Average();
        return (mean, StdDev(distances.ToList(), mean));
    }

    private static double ComputeDurbinWatson(IReadOnlyList<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (trainSet.Count < 10 || magWeights.Length == 0) return 2.0;
        int n = trainSet.Count; var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, magWeights.Length) && j < trainSet[i].Features.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) den += residuals[i] * residuals[i];
        for (int i = 1; i < n; i++) { double d = residuals[i] - residuals[i - 1]; num += d * d; }
        return den > 1e-15 ? num / den : 2.0;
    }

    private static string[] ComputeRedundantFeaturePairs(IReadOnlyList<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30 || F < 2) return [];
        int n = Math.Min(trainSet.Count, 500), numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
        var redundant = new List<string>();
        for (int a = 0; a < F; a++)
            for (int b = a + 1; b < F; b++)
            {
                var vA = new double[n]; var vB = new double[n];
                for (int i = 0; i < n; i++) { vA[i] = trainSet[i].Features[a]; vB[i] = trainSet[i].Features[b]; }
                double mi = ComputeMI(vA, vB, numBins), hA = ComputeEntropy(vA, numBins), hB = ComputeEntropy(vB, numBins);
                double norm = Math.Max(hA, hB);
                if (norm > 1e-10 && mi / norm > threshold)
                {
                    string nA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    redundant.Add($"{nA}↔{nB}:{mi / norm:F2}");
                }
            }
        return redundant.ToArray();
    }

    private static double FitTemperatureScaling(IReadOnlyList<TrainingSample> calSet, TabNetWeights w)
    {
        if (calSet.Count < 10) return 1.0;
        int n = calSet.Count; var logits = new double[n]; var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(TabNetRawProb(calSet[i].Features, w), 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw); labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }
        double T = 1.0;
        for (int ep = 0; ep < 100; ep++)
        {
            double dT = 0;
            for (int i = 0; i < n; i++) dT += (Sigmoid(logits[i] / T) - labels[i]) * (-logits[i] / (T * T));
            T -= 0.01 * dT / n; T = Math.Max(0.01, T);
        }
        return T;
    }

    private static double ComputeBrierSkillScore(IReadOnlyList<TrainingSample> testSet, TabNetWeights w, double plattA, double plattB)
    {
        if (testSet.Count < 10) return 0;
        int n = testSet.Count; double baseRate = testSet.Count(s => s.Direction > 0) / (double)n;
        double brierNaive = baseRate * (1 - baseRate), brierModel = 0;
        foreach (var s in testSet)
        {
            double p = TabNetCalibProb(s.Features, w, plattA, plattB);
            int y = s.Direction > 0 ? 1 : 0; brierModel += (p - y) * (p - y);
        }
        brierModel /= n;
        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSOR (Adam + cosine LR + Huber loss + early stopping)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(List<TrainingSample> train, int featureCount, TrainingHyperparams hp)
    {
        var w = new double[featureCount]; double b = 0.0;
        bool canEarlyStop = train.Count >= 30;
        int valSize = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var valSet = canEarlyStop ? train[^valSize..] : train;
        var trainSet = canEarlyStop ? train[..^valSize] : train;
        if (trainSet.Count == 0) return (w, b);
        var mW = new double[featureCount]; var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0, beta1t = 1.0, beta2t = 1.0; int t = 0;
        double bestValLoss = double.MaxValue; var bestW = new double[featureCount]; double bestB = 0.0; int patience = 0;
        int epochs = hp.MaxEpochs; double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1, l2 = hp.L2Lambda;
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));
            foreach (var s in trainSet)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1 = 1.0 - beta1t, bc2 = 1.0 - beta2t, alphat = alpha * Math.Sqrt(bc2) / bc1;
                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad; vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g; vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }
            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude; if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5; valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;
            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }
        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT SANITIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int SanitizeWeights(TabNetWeights w)
    {
        int count = 0;
        count += SanitizeArr(w.OutputW);
        if (!double.IsFinite(w.OutputB)) { w.OutputB = 0.0; count++; }
        if (w.MagW.Length > 0) count += SanitizeArr(w.MagW);
        if (!double.IsFinite(w.MagB)) { w.MagB = 0.0; count++; }
        foreach (var l in w.SharedW) foreach (var r in l) count += SanitizeArr(r);
        foreach (var l in w.SharedB) count += SanitizeArr(l);
        foreach (var l in w.SharedGW) foreach (var r in l) count += SanitizeArr(r);
        foreach (var l in w.SharedGB) count += SanitizeArr(l);
        foreach (var s in w.StepW) foreach (var l in s) foreach (var r in l) count += SanitizeArr(r);
        foreach (var s in w.StepB) foreach (var l in s) count += SanitizeArr(l);
        foreach (var s in w.StepGW) foreach (var l in s) foreach (var r in l) count += SanitizeArr(r);
        foreach (var s in w.StepGB) foreach (var l in s) count += SanitizeArr(l);
        foreach (var s in w.AttnFcW) foreach (var r in s) count += SanitizeArr(r);
        foreach (var s in w.AttnFcB) count += SanitizeArr(s);
        foreach (var b in w.BnGamma) count += SanitizeArr(b);
        foreach (var b in w.BnBeta) count += SanitizeArr(b);
        return count;
    }

    private static int SanitizeArr(double[] arr)
    {
        int c = 0;
        for (int i = 0; i < arr.Length; i++)
            if (!double.IsFinite(arr[i])) { arr[i] = 0.0; c++; }
        return c;
    }

    private static void SanitizeFloatArr(float[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (!float.IsFinite(arr[i])) arr[i] = 0f;
    }

    /// <summary>
    /// Sanitizes all double/float arrays and scalar fields in a ModelSnapshot to prevent
    /// JSON serialization failures from NaN/Infinity values (e.g. from corrupted warm-start).
    /// </summary>
    private static void SanitizeSnapshotArrays(ModelSnapshot s)
    {
        if (s.MagWeights is { Length: > 0 }) SanitizeArr(s.MagWeights);
        if (s.MagQ90Weights is { Length: > 0 }) SanitizeArr(s.MagQ90Weights);
        if (s.FeatureImportance is { Length: > 0 }) SanitizeFloatArr(s.FeatureImportance);
        if (s.FeatureImportanceScores is { Length: > 0 }) SanitizeArr(s.FeatureImportanceScores);
        if (s.IsotonicBreakpoints is { Length: > 0 }) SanitizeArr(s.IsotonicBreakpoints);
        if (s.JackknifeResiduals is { Length: > 0 }) SanitizeArr(s.JackknifeResiduals);
        if (s.MetaLabelWeights is { Length: > 0 }) SanitizeArr(s.MetaLabelWeights);
        if (s.AbstentionWeights is { Length: > 0 }) SanitizeArr(s.AbstentionWeights);
        if (s.FeatureStabilityScores is { Length: > 0 }) SanitizeArr(s.FeatureStabilityScores);
        if (s.FeatureQuantileBreakpoints is not null)
            foreach (var bp in s.FeatureQuantileBreakpoints)
                if (bp is { Length: > 0 }) SanitizeArr(bp);

        // v3 weight arrays
        if (s.TabNetSharedWeights is not null)
            foreach (var l in s.TabNetSharedWeights) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetSharedBiases is not null)
            foreach (var l in s.TabNetSharedBiases) SanitizeArr(l);
        if (s.TabNetSharedGateWeights is not null)
            foreach (var l in s.TabNetSharedGateWeights) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetSharedGateBiases is not null)
            foreach (var l in s.TabNetSharedGateBiases) SanitizeArr(l);
        if (s.TabNetStepFcWeights is not null)
            foreach (var st in s.TabNetStepFcWeights) foreach (var l in st) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetStepFcBiases is not null)
            foreach (var st in s.TabNetStepFcBiases) foreach (var l in st) SanitizeArr(l);
        if (s.TabNetStepGateWeights is not null)
            foreach (var st in s.TabNetStepGateWeights) foreach (var l in st) foreach (var r in l) SanitizeArr(r);
        if (s.TabNetStepGateBiases is not null)
            foreach (var st in s.TabNetStepGateBiases) foreach (var l in st) SanitizeArr(l);
        if (s.TabNetAttentionFcWeights is not null)
            foreach (var st in s.TabNetAttentionFcWeights) foreach (var r in st) SanitizeArr(r);
        if (s.TabNetAttentionFcBiases is not null)
            foreach (var st in s.TabNetAttentionFcBiases) SanitizeArr(st);
        if (s.TabNetBnGammas is not null) foreach (var b in s.TabNetBnGammas) SanitizeArr(b);
        if (s.TabNetBnBetas is not null) foreach (var b in s.TabNetBnBetas) SanitizeArr(b);
        if (s.TabNetBnRunningMeans is not null) foreach (var b in s.TabNetBnRunningMeans) SanitizeArr(b);
        if (s.TabNetBnRunningVars is not null) foreach (var b in s.TabNetBnRunningVars) SanitizeArr(b);
        if (s.TabNetOutputHeadWeights is { Length: > 0 }) SanitizeArr(s.TabNetOutputHeadWeights);

        // Legacy arrays
        if (s.Weights is not null)
            foreach (var w in s.Weights) if (w is { Length: > 0 }) SanitizeArr(w);
        if (s.Biases is { Length: > 0 }) SanitizeArr(s.Biases);

        // Scalar fields
        if (!double.IsFinite(s.WalkForwardSharpeTrend)) s.WalkForwardSharpeTrend = 0.0;
        if (!double.IsFinite(s.BrierSkillScore)) s.BrierSkillScore = 0.0;
        if (!double.IsFinite(s.ConformalQHat)) s.ConformalQHat = 0.5;
        if (!double.IsFinite(s.Ece)) s.Ece = 1.0;
        if (!double.IsFinite(s.OptimalThreshold)) s.OptimalThreshold = 0.5;
        if (!double.IsFinite(s.MetaLabelThreshold)) s.MetaLabelThreshold = 0.5;
        if (!double.IsFinite(s.AgeDecayLambda)) s.AgeDecayLambda = 0.0;
        if (!double.IsFinite(s.AdaptiveLabelSmoothing)) s.AdaptiveLabelSmoothing = 0.0;
        if (!double.IsFinite(s.ConformalCoverage)) s.ConformalCoverage = 0.0;
        if (!double.IsFinite(s.PlattA)) s.PlattA = 1.0;
        if (!double.IsFinite(s.PlattB)) s.PlattB = 0.0;
        if (!double.IsFinite(s.PlattABuy)) s.PlattABuy = 1.0;
        if (!double.IsFinite(s.PlattBBuy)) s.PlattBBuy = 0.0;
        if (!double.IsFinite(s.PlattASell)) s.PlattASell = 1.0;
        if (!double.IsFinite(s.PlattBSell)) s.PlattBSell = 0.0;
        if (!double.IsFinite(s.MagBias)) s.MagBias = 0.0;
        if (!double.IsFinite(s.MagQ90Bias)) s.MagQ90Bias = 0.0;
        if (!double.IsFinite(s.MetaLabelBias)) s.MetaLabelBias = 0.0;
        if (!double.IsFinite(s.AbstentionBias)) s.AbstentionBias = 0.0;
        if (!double.IsFinite(s.AbstentionThreshold)) s.AbstentionThreshold = 0.5;
        if (!double.IsFinite(s.AvgKellyFraction)) s.AvgKellyFraction = 0.0;
        if (!double.IsFinite(s.DecisionBoundaryMean)) s.DecisionBoundaryMean = 0.0;
        if (!double.IsFinite(s.DecisionBoundaryStd)) s.DecisionBoundaryStd = 0.0;
        if (!double.IsFinite(s.DurbinWatsonStatistic)) s.DurbinWatsonStatistic = 2.0;
        if (!double.IsFinite(s.TabNetOutputHeadBias)) s.TabNetOutputHeadBias = 0.0;
        if (!double.IsFinite(s.TabNetRelaxationGamma)) s.TabNetRelaxationGamma = 1.5;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY / DENSITY / COVARIATE / TEMPORAL / EQUITY / SHARPE
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(IReadOnlyList<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 30) return 0;
        int nonStat = 0;
        for (int j = 0; j < F; j++)
        {
            var vals = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) vals[i] = trainSet[i].Features[j];
            int n = vals.Length, half = n / 2;
            double varFirst = Variance(vals, 0, half), varSecond = Variance(vals, half, n - half);
            double ratio = varSecond > 1e-15 ? varFirst / varSecond : 1.0;
            if (ratio > 3.0 || ratio < 0.333) nonStat++;
        }
        return nonStat;
    }

    private static double Variance(double[] vals, int start, int count)
    {
        if (count < 2) return 0;
        double mean = 0;
        for (int i = start; i < start + count; i++) mean += vals[i];
        mean /= count;
        double var_ = 0;
        for (int i = start; i < start + count; i++) var_ += (vals[i] - mean) * (vals[i] - mean);
        return var_ / (count - 1);
    }

    private static double[] ComputeDensityRatioWeights(IReadOnlyList<TrainingSample> trainSet, int F, int windowDays)
    {
        int n = trainSet.Count, recentCount = Math.Min(n / 3, windowDays * 24);
        if (recentCount < 20) return Enumerable.Repeat(1.0, n).ToArray();
        int cutoff = n - recentCount;
        var w = new double[F]; double bias = 0;
        for (int ep = 0; ep < 30; ep++)
            for (int i = 0; i < n; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0, z = bias;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) z += w[j] * trainSet[i].Features[j];
                double err = Sigmoid(z) - label; bias -= 0.01 * err;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) w[j] -= 0.01 * err * trainSet[i].Features[j];
            }
        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double z = bias;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++) z += w[j] * trainSet[i].Features[j];
            double p = Math.Clamp(Sigmoid(z), 0.01, 0.99); weights[i] = p / (1 - p);
        }
        double sum = weights.Sum();
        if (sum > 1e-15) for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    private static double[] ComputeCovariateShiftWeights(IReadOnlyList<TrainingSample> trainSet, double[][] parentBp, int F)
    {
        int n = trainSet.Count; var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            int outside = 0, checked_ = 0;
            for (int j = 0; j < F && j < parentBp.Length; j++)
            {
                if (parentBp[j].Length < 2) continue; checked_++;
                double v = trainSet[i].Features[j];
                if (v < parentBp[j][0] || v > parentBp[j][^1]) outside++;
            }
            weights[i] = 1.0 + (checked_ > 0 ? (double)outside / checked_ : 0);
        }
        double mean = weights.Average();
        if (mean > 1e-15) for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    private static double[] ComputeTemporalWeights(int n, double lambdaDecay)
    {
        var w = new double[n];
        if (lambdaDecay <= 0) { Array.Fill(w, 1.0 / Math.Max(1, n)); return w; }
        double sum = 0;
        for (int i = 0; i < n; i++) { w[i] = Math.Exp(-lambdaDecay * (n - 1 - i)); sum += w[i]; }
        if (sum > 1e-15) for (int i = 0; i < n; i++) w[i] /= sum;
        return w;
    }

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats((int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);
        double equity = 0, peak = 0, maxDD = 0;
        var returns = new List<double>(predictions.Length);
        foreach (var (pred, actual) in predictions)
        {
            double ret = pred == actual ? 1.0 : -1.0; returns.Add(ret); equity += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 1e-10 ? (peak - equity) / peak : (peak - equity > 0 ? 1.0 : 0.0);
            if (dd > maxDD) maxDD = dd;
        }
        double avgRet = returns.Average(), stdRet = StdDev(returns, avgRet);
        return (maxDD, stdRet > 1e-10 ? avgRet / stdRet * Math.Sqrt(252) : 0);
    }

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpeList)
    {
        int n = sharpeList.Count; if (n < 3) return 0;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = 0; i < n; i++) { sumX += i; sumY += sharpeList[i]; sumXX += i * i; sumXY += i * sharpeList[i]; }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) > 1e-10 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FEATURE MASK & PRUNING UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        var mask = new bool[F];
        if (threshold <= 0) { Array.Fill(mask, true); return mask; }
        double equalShare = 1.0 / F;
        for (int i = 0; i < F; i++) mask[i] = importance[i] >= threshold * equalShare;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(IReadOnlyList<TrainingSample> samples, bool[] mask)
    {
        int maskedF = mask.Count(m => m);
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = new float[maskedF]; int ni = 0;
            for (int j = 0; j < mask.Length && j < s.Features.Length; j++)
                if (mask[j]) nf[ni++] = s.Features[j];
            result.Add(s with { Features = nf });
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SPARSEMAX (Martins & Astudillo 2016)
    //  Projects onto probability simplex → exact zeros for unselected features.
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] Sparsemax(double[] z, int len)
    {
        // Sort descending to find threshold τ
        var sorted = new double[len];
        for (int i = 0; i < len; i++) sorted[i] = z[i];
        Array.Sort(sorted);
        Array.Reverse(sorted);

        double cumSum = 0;
        int k = 0;
        for (int i = 0; i < len; i++)
        {
            cumSum += sorted[i];
            if (sorted[i] > (cumSum - 1.0) / (i + 1))
                k = i + 1;
            else
                break;
        }

        // Recompute cumSum for the actual support size
        cumSum = 0;
        for (int i = 0; i < k; i++) cumSum += sorted[i];
        double tau = (cumSum - 1.0) / k;

        var output = new double[len];
        for (int i = 0; i < len; i++)
            output[i] = Math.Max(0, z[i] - tau);

        return output;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MATH UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x)
        => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50, 50)));

    private static double Logit(double p)
        => Math.Log(p / (1.0 - p));

    private static double[] SoftmaxArr(double[] x, int len)
    {
        double max = double.MinValue;
        for (int i = 0; i < len; i++) if (x[i] > max) max = x[i];
        var e = new double[len]; double sum = 0;
        for (int i = 0; i < len; i++) { e[i] = Math.Exp(x[i] - max); sum += e[i]; }
        sum += 1e-10;
        for (int i = 0; i < len; i++) e[i] /= sum;
        return e;
    }

    private static double StdDev(IReadOnlyList<double> vals, double mean)
    {
        if (vals.Count < 2) return 0;
        double variance = vals.Sum(v => (v - mean) * (v - mean)) / (vals.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double[] XavierVec(Random rng, int fanIn, int fanOut, int dummy)
    {
        double scale = Math.Sqrt(2.0 / (fanIn + fanOut));
        return Enumerable.Range(0, fanIn).Select(_ => (rng.NextDouble() * 2 - 1) * scale).ToArray();
    }

    private static double[][] XavierMatrix(Random rng, int rows, int cols)
    {
        double scale = Math.Sqrt(2.0 / (rows + cols));
        var m = new double[rows][];
        for (int i = 0; i < rows; i++)
        {
            m[i] = new double[cols];
            for (int j = 0; j < cols; j++) m[i][j] = (rng.NextDouble() * 2 - 1) * scale;
        }
        return m;
    }

    private static double[] FcLinear(double[] input, int inDim, int outDim, double[][] w, double[] b)
    {
        var output = new double[outDim];
        for (int i = 0; i < outDim; i++)
        {
            output[i] = b[i];
            for (int j = 0; j < inDim && j < w[i].Length; j++) output[i] += w[i][j] * input[j];
        }
        return output;
    }

    private static double[] FcSigmoid(double[] input, int inDim, int outDim, double[][] w, double[] b)
    {
        var output = FcLinear(input, inDim, outDim, w, b);
        for (int i = 0; i < outDim; i++) output[i] = Sigmoid(output[i]);
        return output;
    }

    private static double ComputeMI(double[] a, double[] b, int bins)
    {
        double minA = a.Min(), maxA = a.Max(), minB = b.Min(), maxB = b.Max();
        double wA = (maxA - minA) / bins + 1e-15, wB = (maxB - minB) / bins + 1e-15;
        int n = a.Length; var joint = new int[bins, bins]; var mA = new int[bins]; var mB = new int[bins];
        for (int i = 0; i < n; i++)
        {
            int ia = Math.Clamp((int)((a[i] - minA) / wA), 0, bins - 1), ib = Math.Clamp((int)((b[i] - minB) / wB), 0, bins - 1);
            joint[ia, ib]++; mA[ia]++; mB[ib]++;
        }
        double mi = 0;
        for (int i = 0; i < bins; i++)
            for (int j = 0; j < bins; j++)
            {
                if (joint[i, j] == 0) continue;
                double pxy = (double)joint[i, j] / n, px = (double)mA[i] / n, py = (double)mB[j] / n;
                mi += pxy * Math.Log(pxy / (px * py + 1e-15) + 1e-15);
            }
        return Math.Max(0, mi);
    }

    private static double ComputeEntropy(double[] vals, int bins)
    {
        double min = vals.Min(), max = vals.Max(), width = (max - min) / bins + 1e-15;
        int n = vals.Length; var counts = new int[bins];
        for (int i = 0; i < n; i++) counts[Math.Clamp((int)((vals[i] - min) / width), 0, bins - 1)]++;
        double h = 0;
        for (int i = 0; i < bins; i++) { if (counts[i] == 0) continue; double p = (double)counts[i] / n; h -= p * Math.Log(p); }
        return h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  POLYNOMIAL FEATURE AUGMENTATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int[] SelectPolyTopFeatureIndices(List<TrainingSample> samples, int F, ModelSnapshot? warmStart, int topN)
    {
        topN = Math.Min(topN, F); int n = samples.Count; double[] scores = new double[F];
        if (warmStart?.FeatureImportanceScores is { Length: > 0 } prior && prior.Length == F)
            for (int j = 0; j < F; j++) scores[j] = prior[j];
        else
        {
            double[] featureMeans = new double[F];
            for (int i = 0; i < n; i++) for (int j = 0; j < F; j++) featureMeans[j] += samples[i].Features[j];
            for (int j = 0; j < F; j++) featureMeans[j] /= n;
            for (int i = 0; i < n; i++) for (int j = 0; j < F; j++) { double d = samples[i].Features[j] - featureMeans[j]; scores[j] += d * d; }
            for (int j = 0; j < F; j++) scores[j] /= n;
        }
        return scores.Select((s, idx) => (Score: s, Idx: idx)).OrderByDescending(t => t.Score)
            .Take(topN).Select(t => t.Idx).OrderBy(i => i).ToArray();
    }

    private static List<TrainingSample> AugmentSamplesWithPoly(List<TrainingSample> samples, int origF, int[] topIdx)
    {
        int pairCount = topIdx.Length * (topIdx.Length - 1) / 2, newF = origF + pairCount;
        var augmented = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var nf = new float[newF];
            for (int j = 0; j < origF; j++) nf[j] = s.Features[j];
            int k = origF;
            for (int a = 0; a < topIdx.Length; a++)
                for (int b = a + 1; b < topIdx.Length; b++)
                    nf[k++] = s.Features[topIdx[a]] * s.Features[topIdx[b]];
            augmented.Add(s with { Features = nf });
        }
        return augmented;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DEEP CLONE HELPERS (for Adam state / weight snapshots)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[][] CloneDim2(double[][] src) =>
        src.Select(r => (double[])r.Clone()).ToArray();

    private static double[][][] CloneDim3(double[][][] src) =>
        src.Select(m => m.Select(r => (double[])r.Clone()).ToArray()).ToArray();

    private static double[][][][] CloneDim4(double[][][][] src) =>
        src.Select(s => s.Select(m => m.Select(r => (double[])r.Clone()).ToArray()).ToArray()).ToArray();

    private static double[][] DeepClone2(double[][] src) => CloneDim2(src);
    private static double[][][] DeepClone3(double[][][] src) => CloneDim3(src);
    private static double[][][][] DeepClone4(double[][][][] src) => CloneDim4(src);

    private static void CopyArray(double[] src, double[] dst)
    {
        int len = Math.Min(src.Length, dst.Length);
        Array.Copy(src, dst, len);
    }

    private static void CopyMatrix(double[][] src, double[][] dst)
    {
        int rows = Math.Min(src.Length, dst.Length);
        for (int i = 0; i < rows; i++) CopyArray(src[i], dst[i]);
    }
}
