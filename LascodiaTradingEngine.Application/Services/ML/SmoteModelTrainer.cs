using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// SMOTE + Logistic ensemble trainer (Rec #400) — version 3.0.
///
/// Algorithm overview:
/// <list type="number">
///   <item>H2: Incremental update fast-path: fine-tune on recent window when UseIncrementalUpdate is set.</item>
///   <item>Z-score standardise all features; store means/stds in snapshot for inference parity.</item>
///   <item>M14: Stationarity gate (soft ADF approximation) warns when &gt;30% of features may have unit root.</item>
///   <item>H3/H4/H14/M15: Walk-forward CV with equity-curve gate, Sharpe trend gate, feature stability
///         scores, and PurgeHorizonBars time-series purging.</item>
///   <item>3-way train | cal | test split with embargo gaps.</item>
///   <item>Adaptive label smoothing from ATR-magnitude ambiguity proxy when enabled.</item>
///   <item>M4/M5: Density-ratio importance weights and covariate shift weights blended into bootstrap sampling.</item>
///   <item>SMOTE on train fold: KNN lists precomputed once (O(|minority|² × F)), deficit filled in O(deficit).</item>
///   <item>H1: Parallel ensemble fitting with Parallel.For (serialised when NCL/DiversityLambda active).
///         M19: ArrayPool&lt;double&gt; for Adam moment arrays to reduce GC pressure.
///         C1: Running beta1t/beta2t products — no Math.Pow per sample.
///         C2: Intra-epoch NaN/Inf guard with immediate rollback to bestW/bestB.
///         M1: Adaptive LR decay when rolling-val-acc drops &gt; 5%.
///         M2: Weight magnitude clipping to [−MaxWeightMagnitude, +MaxWeightMagnitude].
///         M8: Biased feature sampling toward warm-start important features.
///         M13: MaxLearnerCorrelation enforcement — re-init correlated pairs.
///         M17: SWA weight averaging over final training epochs.
///         L1: NCL gradient penalty to decorrelate learner errors.
///         L2: DiversityLambda gradient push away from ensemble mean.
///         L3: SCE (symmetric cross-entropy) — RCE term saturates mislabelled samples.
///         L4: FpCostWeight asymmetric BCE loss.
///         L5: NoiseCorrectionThreshold — downweight likely mislabelled samples.
///         L6: AtrLabelSensitivity — continuous soft labels from signed magnitude.
///         L7: Mixup — interpolate training pairs via Beta(α,α) mixing.
///         L8: Polynomial learners — augment last K×PolyFrac learners with top-5 pairwise products.
///         L9: MLP hidden layer — optional single-hidden-layer ReLU MLP per learner.
///         L10: Mini-batch SGD — accumulate gradients over MiniBatchSize samples per Adam step.</item>
///   <item>H5: Stacking meta-learner trained on per-learner OOF probabilities on the cal fold.</item>
///   <item>Platt calibration (A, B) fitted on cal fold.</item>
///   <item>M3: Class-conditional Platt — separate A/B for Buy and Sell directions.</item>
///   <item>M9: Average Kelly fraction on cal set.</item>
///   <item>EV-optimal threshold swept on cal fold.</item>
///   <item>Final evaluation on held-out test set.</item>
///   <item>H6: ECE (10-bin expected calibration error).</item>
///   <item>H7: Brier Skill Score (BSS = 1 − Brier / Brier_naive).</item>
///   <item>Permutation feature importance (test set); M7: cal-set importance for warm-start biased sampling.</item>
///   <item>H13: Feature pruning retrain pass when low-importance features exist.</item>
///   <item>H8: Isotonic calibration (PAVA) on cal fold.</item>
///   <item>H9: Split-conformal qHat at target coverage.</item>
///   <item>H10: Jackknife+ sorted OOB residuals.</item>
///   <item>H11: Meta-label secondary logistic classifier.</item>
///   <item>H12: Abstention gate trained on calibrated probability + uncertainty features.</item>
///   <item>L11: Quantile magnitude regressor (pinball loss, τ = MagnitudeQuantileTau).</item>
///   <item>M11: Temperature scaling (single-param, fitted on cal fold).</item>
///   <item>M10: Ensemble diversity (avg pairwise Pearson correlation).</item>
///   <item>M6: OOB-contribution pruning — remove learners hurting ensemble accuracy.</item>
///   <item>M18: Decision boundary distance stats (mean/std ‖∇P‖ over cal set).</item>
///   <item>M12: Durbin-Watson statistic on magnitude regressor residuals.</item>
///   <item>M16: Mutual-information feature redundancy check.</item>
///   <item>PSI quantile breakpoints for drift monitoring.</item>
///   <item>Full ModelSnapshot serialised with complete lineage for reproducibility.</item>
/// </list>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Smote)]
public sealed class SmoteModelTrainer : IMLModelTrainer
{
    private const string ModelType    = "SMOTE";
    private const string ModelVersion = "3.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private const int PolyTopN = 5;
    private const int PolyPairCount = PolyTopN * (PolyTopN - 1) / 2; // = 10

    private const double TrainSplitRatio          = 0.70;
    private const double CalSplitRatio            = 0.80;
    private const int    MinCalSamples            = 20;
    private const int    MinEvalSamples           = 10;
    private const int    DefaultCalibrationEpochs = 200;
    private const int    ClassCondPlattEpochs     = 150;
    private const int    GesRounds                = 50;
    private const int    MinFoldSize              = 50;
    private const double PruneAccuracyTolerance   = 0.005;
    private const double BiasedSamplingTemp       = 5.0;
    private const double ThresholdSearchStep      = 0.02;
    private const int    EceBinCount              = 10;
    private const int    MIBinCount               = 10;
    private const int    DensityRatioEpochs       = 50;
    private const int    MaxJackknifeResiduals     = 5_000;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    /// <summary>Bundles stacking meta-learner weights/bias for clean threading through helpers.</summary>
    internal readonly record struct MetaLearner(double[] Weights, double Bias)
    {
        public static readonly MetaLearner None = new([], 0.0);
        public bool IsActive => Weights is { Length: > 0 };
    }

    /// <summary>Bundles the ensemble state passed to most evaluation/calibration helpers.</summary>
    internal readonly record struct EnsembleState(
        double[][]  Weights,
        double[]    Biases,
        int         F,
        int[][]?    FeatureSubsets,
        MetaLearner Meta,
        double[][]? MlpHW,
        double[][]? MlpHB,
        int         HidDim);

    // ── EnsembleState forwarding helpers ────────────────────────────────────
    private static double EnsembleProb(float[] x, in EnsembleState es)
        => EnsembleProb(x, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double EvalAccuracy(List<TrainingSample> s, in EnsembleState es, double pA, double pB)
        => EvalAccuracy(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeEce(List<TrainingSample> s, in EnsembleState es, double pA, double pB)
        => ComputeEce(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeBrierSkillScore(List<TrainingSample> s, in EnsembleState es, double pA, double pB)
        => ComputeBrierSkillScore(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeOptimalThreshold(List<TrainingSample> s, in EnsembleState es, double pA, double pB, double lo, double hi)
        => ComputeOptimalThreshold(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim, lo, hi);

    private static EvalMetrics EvaluateEnsemble(List<TrainingSample> s, in EnsembleState es, double[] mw, double mb, double pA, double pB, double oobAcc)
        => EvaluateEnsemble(s, es.Weights, es.Biases, mw, mb, pA, pB, es.F, es.FeatureSubsets, es.Meta, oobAcc, es.MlpHW, es.MlpHB, es.HidDim);

    private static MetaLearner FitMetaLearner(List<TrainingSample> s, in EnsembleState es)
        => FitMetaLearner(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static (double A, double B) FitPlattScaling(List<TrainingSample> s, in EnsembleState es)
        => FitPlattScaling(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double[] FitIsotonicCalibration(List<TrainingSample> s, in EnsembleState es, double pA, double pB)
        => FitIsotonicCalibration(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static (double AvgP, double StdP) EnsembleProbAndStd(float[] x, in EnsembleState es)
        => EnsembleProbAndStd(x, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeConformalQHat(List<TrainingSample> s, in EnsembleState es, double pA, double pB, double[] isoBp, double alpha)
        => ComputeConformalQHat(s, es.Weights, es.Biases, pA, pB, isoBp, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim, alpha);

    private static double[] ComputeJackknifeResiduals(List<TrainingSample> s, in EnsembleState es)
        => ComputeJackknifeResiduals(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static (double[] Weights, double Bias) FitMetaLabelModel(List<TrainingSample> s, in EnsembleState es, double[]? importanceScores = null)
        => FitMetaLabelModel(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim, importanceScores);

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> s, in EnsembleState es, double pA, double pB, double[] metaLabelW, double metaLabelB)
        => FitAbstentionModel(s, es.Weights, es.Biases, pA, pB, metaLabelW, metaLabelB, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static double FitTemperatureScaling(List<TrainingSample> s, in EnsembleState es)
        => FitTemperatureScaling(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeEnsembleDiversity(in EnsembleState es)
        => ComputeEnsembleDiversity(es.Weights, es.F);

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(List<TrainingSample> s, in EnsembleState es)
        => ComputeDecisionBoundaryStats(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static double[] RunGreedyEnsembleSelection(List<TrainingSample> s, in EnsembleState es)
        => RunGreedyEnsembleSelection(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static float[] ComputePermutationImportance(List<TrainingSample> s, in EnsembleState es, double pA, double pB, Random rng, CancellationToken ct)
        => ComputePermutationImportance(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim, rng, ct);

    private static double[] ComputeCalPermutationImportance(List<TrainingSample> s, in EnsembleState es, double pA, double pB, CancellationToken ct)
        => ComputeCalPermutationImportance(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim, ct);

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(List<TrainingSample> s, in EnsembleState es)
        => FitClassConditionalPlatt(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeAvgKellyFraction(List<TrainingSample> s, in EnsembleState es, double pA, double pB)
        => ComputeAvgKellyFraction(s, es.Weights, es.Biases, pA, pB, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeOobAccuracy(List<TrainingSample> s, in EnsembleState es, bool[][] oobMasks)
        => ComputeOobAccuracy(s, es.Weights, es.Biases, oobMasks, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private readonly ILogger<SmoteModelTrainer> _logger;

    public SmoteModelTrainer(ILogger<SmoteModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ───────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
        => await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);

    // ── Core training logic ───────────────────────────────────────────────────

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        // ── H2: Incremental update fast-path ──────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int bpd = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * bpd);
            // Floor: after 70/10/20 split + two embargo gaps the train fold must still have
            // at least MinSamples rows. Solving 0.70×w - 2×embargo ≥ MinSamples:
            // w ≥ (MinSamples + 2×embargo) / 0.70
            int minWindowForSplit = (int)Math.Ceiling((hp.MinSamples + 2 * hp.EmbargoBarCount) / TrainSplitRatio);
            if (recentCount >= minWindowForSplit)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Incremental update: fine-tuning on last {N}/{Total} samples (~{Days}d window)",
                        recentCount, samples.Count, hp.DensityRatioWindowDays);
                var incrementalHp = hp with
                {
                    MaxEpochs             = Math.Max(20, hp.MaxEpochs / 5),
                    EarlyStoppingPatience = Math.Max(3,  hp.EarlyStoppingPatience / 3),
                    LearningRate          = hp.LearningRate / 5.0,
                    UseIncrementalUpdate  = false,
                };
                return Train(samples[^recentCount..], incrementalHp, warmStart, parentModelId, ct);
            }
        }

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"SmoteModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        int F = samples[0].Features.Length;
        int K = hp.K > 0 ? hp.K : 50;

        // ── Input validation: feature count consistency and NaN/Inf guard ─────
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].Features.Length != F)
                throw new InvalidOperationException(
                    $"SmoteModelTrainer: inconsistent feature count — sample 0 has {F}, sample {i} has {samples[i].Features.Length}.");
        for (int i = 0; i < samples.Count; i++)
            foreach (var fv in samples[i].Features)
                if (!float.IsFinite(fv))
                    throw new InvalidOperationException(
                        $"SmoteModelTrainer: non-finite feature value at sample index {i}.");

        // ── 1. Z-score standardisation ────────────────────────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── M14: Stationarity gate (ADF) + fractional differencing ────────────
        double fracDiffD = hp.FracDiffD;
        {
            int nonStatCount = 0;
            for (int j = 0; j < F; j++)
            {
                var col = new double[allStd.Count];
                for (int i = 0; i < allStd.Count; i++)
                    col[i] = allStd[i].Features[j];
                if (MLFeatureHelper.AdfTest(col) > 0.05) nonStatCount++;
            }
            double nonStatFrac = F > 0 ? (double)nonStatCount / F : 0.0;
            if (nonStatFrac > 0.30)
            {
                if (fracDiffD == 0.0)
                {
                    fracDiffD = 0.4;
                    _logger.LogWarning(
                        "Stationarity gate: {N}/{F} features have unit root — auto-applying FracDiffD={D:F1}.",
                        nonStatCount, F, fracDiffD);
                }
                else if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Stationarity gate: {N}/{F} features have unit root — applying FracDiffD={D:F2} from hyperparams.",
                        nonStatCount, F, fracDiffD);
                }
            }
        }
        if (fracDiffD > 0.0)
            allStd = MLFeatureHelper.ApplyFractionalDifferencing(allStd, F, fracDiffD);

        // ── 2. Walk-forward CV ────────────────────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, F, ct);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "SmoteModelTrainer CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sh:F2}",
                cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
                cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
        {
            if (warmStart is not null)
            {
                _logger.LogWarning("Equity-curve gate failed — returning warm-start snapshot as fallback.");
                byte[] fallbackBytes = JsonSerializer.SerializeToUtf8Bytes(warmStart, JsonOpts);
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, fallbackBytes);
            }
            return new TrainingResult(
                new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        }

        ct.ThrowIfCancellationRequested();

        // ── 3. 3-way split: 70% train | 10% cal | ~20% test ──────────────────
        int embargo  = hp.EmbargoBarCount;
        int n        = allStd.Count;
        int trainEnd = (int)(n * TrainSplitRatio);
        int calEnd   = (int)(n * CalSplitRatio);

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < n ? calEnd : n)];
        var testSet  = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"SmoteModelTrainer: insufficient training samples after split: {trainSet.Count} < {hp.MinSamples}");

        // ── 4. Adaptive label smoothing ───────────────────────────────────────
        double labelSmoothing = hp.LabelSmoothing;
        if (hp.UseAdaptiveLabelSmoothing && trainSet.Count > 0)
        {
            var sortedMags = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) sortedMags[i] = Math.Abs(trainSet[i].Magnitude);
            Array.Sort(sortedMags);
            double p20 = sortedMags[Math.Min((int)(sortedMags.Length * 0.20), sortedMags.Length - 1)];
            int ambigCount = 0;
            foreach (var s in trainSet)
                if (Math.Abs(s.Magnitude) <= p20) ambigCount++;
            double ambigFrac = (double)ambigCount / trainSet.Count;
            labelSmoothing = Math.Clamp(ambigFrac * 0.5, 0.01, 0.20);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Adaptive label smoothing: ε={Eps:F3} (ambiguous fraction={Frac:P1})",
                    labelSmoothing, ambigFrac);
        }

        // ── M4: Density-ratio importance weights ──────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            int bpd = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            densityWeights = ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays, bpd);
            _logger.LogDebug("Density-ratio weights computed (window={W}d).", hp.DensityRatioWindowDays);
        }

        // ── M5: Covariate shift weight integration ────────────────────────────
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, F);
            if (densityWeights is not null)
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            else
                densityWeights = csWeights;
            _logger.LogDebug("Covariate shift weights applied (gen={Gen}).", warmStart.GenerationNumber);
        }

        // ── 5. SMOTE oversampling on train fold ───────────────────────────────
        var (balancedTrain, syntheticCount) = ApplySmote(trainSet, hp, F, ct);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "SMOTE: trainN={Train} → balancedN={Balanced} (+{Synth} synthetic)",
                trainSet.Count, balancedTrain.Count, syntheticCount);

        ct.ThrowIfCancellationRequested();

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 6. Fit ensemble ───────────────────────────────────────────────────
        var (weights, biases, featureSubsets, polyStart, oobMasks, ensMlpHW, ensMlpHB, swaCount) =
            FitEnsemble(balancedTrain, effectiveHp, F, K, labelSmoothing, warmStart, densityWeights, ct,
                originalCount: trainSet.Count);

        // ── 7. Post-training NaN/Inf sanitization ─────────────────────────────
        int sanitizedCount = 0;
        for (int k = 0; k < K; k++)
        {
            bool bad = !double.IsFinite(biases[k]);
            if (!bad)
                foreach (var wv in weights[k])
                    if (!double.IsFinite(wv)) { bad = true; break; }

            if (!bad && ensMlpHW?[k] is not null)
                foreach (var wv in ensMlpHW[k])
                    if (!double.IsFinite(wv)) { bad = true; break; }

            if (bad)
            {
                Array.Clear(weights[k], 0, weights[k].Length);
                biases[k] = 0.0;
                if (ensMlpHW?[k] is not null) Array.Clear(ensMlpHW[k], 0, ensMlpHW[k].Length);
                if (ensMlpHB?[k] is not null) Array.Clear(ensMlpHB[k], 0, ensMlpHB[k].Length);
                sanitizedCount++;
                _logger.LogWarning("SmoteModelTrainer: sanitized learner {K} — non-finite weights.", k);
            }
        }
        if (sanitizedCount > 0)
            _logger.LogWarning("Post-training sanitization: {N}/{K} learners cleared.", sanitizedCount, K);

        // Ensemble state (meta-learner not yet fitted); will be rebuilt after meta-learner
        var ens = new EnsembleState(weights, biases, F, featureSubsets, MetaLearner.None, ensMlpHW, ensMlpHB, hp.MlpHiddenDim);

        // ── GES (greedy ensemble selection) ──────────────────────────────────
        double[] gesWeights = hp.EnableGreedyEnsembleSelection && calSet.Count >= MinCalSamples
            ? RunGreedyEnsembleSelection(calSet, ens) : [];

        // ── 8. OOB accuracy ───────────────────────────────────────────────────
        double oobAccuracy = ComputeOobAccuracy(balancedTrain, ens, oobMasks);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("OOB accuracy={Oob:P1}", oobAccuracy);

        // ── 9. Per-learner cal accuracy ───────────────────────────────────────
        var learnerCalAccuracies = new double[K];
        if (calSet.Count > 0)
        {
            for (int k = 0; k < K; k++)
            {
                int correct = 0;
                foreach (var s in calSet)
                {
                    double rawP = SingleLearnerProb(s.Features, weights[k], biases[k],
                        featureSubsets?[k], F, ensMlpHW?[k], ensMlpHB?[k], hp.MlpHiddenDim);
                    if ((rawP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
                }
                learnerCalAccuracies[k] = (double)correct / calSet.Count;
            }
        }

        // ── 10. Magnitude regressors ──────────────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(balancedTrain, F, effectiveHp);

        // ── L11: Quantile magnitude regressor ─────────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && balancedTrain.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(balancedTrain, F, hp.MagnitudeQuantileTau);
            _logger.LogDebug("Quantile regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── H5: Stacking meta-learner ─────────────────────────────────────────
        var meta = calSet.Count >= MinCalSamples
            ? FitMetaLearner(calSet, weights, biases, F, featureSubsets, ensMlpHW, ensMlpHB, hp.MlpHiddenDim)
            : MetaLearner.None;
        _logger.LogDebug("Stacking meta-learner: bias={B:F4} active={Active}", meta.Bias, meta.IsActive);

        // Rebuild ensemble state now that meta-learner is fitted
        ens = new EnsembleState(weights, biases, F, featureSubsets, meta, ensMlpHW, ensMlpHB, hp.MlpHiddenDim);

        // ── 11. Platt calibration ─────────────────────────────────────────────
        var (plattA, plattB) = calSet.Count >= MinCalSamples
            ? FitPlattScaling(calSet, ens) : (1.0, 0.0);
        _logger.LogDebug("Platt: A={A:F4} B={B:F4}", plattA, plattB);

        // ── M3: Class-conditional Platt ───────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) = calSet.Count >= MinCalSamples
            ? FitClassConditionalPlatt(calSet, ens) : (1.0, 0.0, 1.0, 0.0);

        // ── M9: Average Kelly fraction ────────────────────────────────────────
        double avgKellyFraction = calSet.Count >= MinEvalSamples
            ? ComputeAvgKellyFraction(calSet, ens, plattA, plattB) : 0.0;

        // ── 12. EV-optimal threshold ──────────────────────────────────────────
        double lo = hp.ThresholdSearchMin > 0 ? hp.ThresholdSearchMin / 100.0 : 0.30;
        double hi = hp.ThresholdSearchMax > 0 ? hp.ThresholdSearchMax / 100.0 : 0.70;
        double optimalThreshold = calSet.Count >= MinCalSamples
            ? ComputeOptimalThreshold(calSet, ens, plattA, plattB, lo, hi) : 0.5;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 13. Final evaluation ──────────────────────────────────────────────
        var finalMetrics = EvaluateEnsemble(testSet, ens, magWeights, magBias, plattA, plattB, oobAccuracy);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "SmoteModelTrainer: K={K} balancedN={BN} acc={Acc:P1} f1={F1:F3} brier={B:F4} sharpe={Sh:F2}",
                K, balancedTrain.Count, finalMetrics.Accuracy, finalMetrics.F1,
                finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── H6: ECE ───────────────────────────────────────────────────────────
        double ece = testSet.Count >= MinEvalSamples
            ? ComputeEce(testSet, ens, plattA, plattB) : 0.0;
        _logger.LogDebug("ECE={Ece:F4}", ece);

        // ── H7: Brier Skill Score ─────────────────────────────────────────────
        double bss = testSet.Count >= MinEvalSamples
            ? ComputeBrierSkillScore(testSet, ens, plattA, plattB) : 0.0;
        _logger.LogDebug("BSS={BSS:F4}", bss);

        // ── 14. Permutation feature importance (test set) ─────────────────────
        float[] featureImportance = testSet.Count >= MinEvalSamples
            ? ComputePermutationImportance(testSet, ens, plattA, plattB, new Random(77), ct)
            : new float[F];

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var top5 = featureImportance
                .Select((imp, idx) => (Importance: imp, Name: idx < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[idx] : $"f{idx}"))
                .OrderByDescending(x => x.Importance)
                .Take(5);
            _logger.LogInformation(
                "Top 5 features: {Features}",
                string.Join(", ", top5.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        // ── M7: Cal-set permutation importance (for warm-start biased sampling) ─
        double[] calImportanceScores = calSet.Count >= MinEvalSamples
            ? ComputeCalPermutationImportance(calSet, ens, plattA, plattB, ct)
            : new double[F];

        // ── H13: Feature pruning retrain pass ─────────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Feature pruning: masking {Pruned}/{Total} low-importance features",
                    prunedCount, F);

            var maskedTrain = ApplyMask(balancedTrain, activeMask);
            var maskedCal   = ApplyMask(calSet,        activeMask);
            var maskedTest  = ApplyMask(testSet,        activeMask);
            int reducedF    = activeMask.Count(m => m);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            var (pw, pb, _, _, _, pMlpHW, pMlpHB, _) = FitEnsemble(
                maskedTrain, prunedHp, reducedF, K, labelSmoothing, null, densityWeights, ct);
            var pMeta       = maskedCal.Count >= MinCalSamples
                ? FitMetaLearner(maskedCal, pw, pb, reducedF, null, pMlpHW, pMlpHB, prunedHp.MlpHiddenDim)
                : MetaLearner.None;
            var pEns = new EnsembleState(pw, pb, reducedF, null, pMeta, pMlpHW, pMlpHB, prunedHp.MlpHiddenDim);
            var (pA, pB)    = maskedCal.Count >= MinCalSamples
                ? FitPlattScaling(maskedCal, pEns) : (1.0, 0.0);
            var (pmw, pmb)  = FitLinearRegressor(maskedTrain, reducedF, prunedHp);
            var prunedMetrics = EvaluateEnsemble(maskedTest, pEns, pmw, pmb, pA, pB, 0.0);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - PruneAccuracyTolerance)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                        prunedMetrics.Accuracy, finalMetrics.Accuracy);
                weights = pw; biases = pb; ensMlpHW = pMlpHW; ensMlpHB = pMlpHB;
                magWeights = pmw; magBias = pmb;
                plattA = pA; plattB = pB; meta = pMeta;
                finalMetrics = prunedMetrics;
                // Use reduced-F EnsembleState for evaluating on masked data
                ece = ComputeEce(maskedTest, pEns, pA, pB);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, pEns, pA, pB, lo, hi);
                gesWeights = hp.EnableGreedyEnsembleSelection && maskedCal.Count >= MinCalSamples
                    ? RunGreedyEnsembleSelection(maskedCal, pEns) : [];
                calImportanceScores = maskedCal.Count >= MinEvalSamples
                    ? ComputeCalPermutationImportance(maskedCal, pEns, pA, pB, ct)
                    : new double[reducedF];
                // Set featureSubsets to keptIndices for inference (maps reduced weights → original features)
                int[] keptIndices = Enumerable.Range(0, F).Where(j => activeMask[j]).ToArray();
                featureSubsets = new int[K][];
                for (int ki = 0; ki < K; ki++) featureSubsets[ki] = keptIndices;
                // Recompute calibration artifacts stale from the unpruned model
                (plattABuy, plattBBuy, plattASell, plattBSell) = maskedCal.Count >= MinCalSamples
                    ? FitClassConditionalPlatt(maskedCal, pEns) : (1.0, 0.0, 1.0, 0.0);
                avgKellyFraction = maskedCal.Count >= MinEvalSamples
                    ? ComputeAvgKellyFraction(maskedCal, pEns, pA, pB) : 0.0;
            }
            else
            {
                _logger.LogInformation("Pruned model rejected (acc drop {Drop:P1}) — keeping full model.",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask = new bool[F]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
        }

        // ── Post-pruning calibration pipeline ─────────────────────────────────
        var postPruneCalSet = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;

        // ── H8: Isotonic calibration ──────────────────────────────────────────
        // Rebuild EnsembleState: when pruning was accepted, use reducedF with null featureSubsets
        // (matching pEns) so weight indexing is sequential 0..reducedF-1. The keptIndices featureSubsets
        // are only used at serialization time for the inference engine's feature-space mapping.
        int postF = prunedCount > 0 ? F - prunedCount : F;
        ens = prunedCount > 0
            ? new EnsembleState(weights, biases, postF, null, meta, ensMlpHW, ensMlpHB, hp.MlpHiddenDim)
            : new EnsembleState(weights, biases, F, featureSubsets, meta, ensMlpHW, ensMlpHB, hp.MlpHiddenDim);
        double[] isotonicBp = FitIsotonicCalibration(postPruneCalSet, ens, plattA, plattB);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── H9: Conformal qHat ────────────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(postPruneCalSet, ens, plattA, plattB, isotonicBp, conformalAlpha);
        _logger.LogDebug("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── H10: Jackknife+ residuals ─────────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(balancedTrain, ens);
        _logger.LogDebug("Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── H11: Meta-label secondary classifier ─────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            postPruneCalSet, ens, importanceScores: calImportanceScores);
        _logger.LogDebug("Meta-label model: bias={B:F4}", metaLabelBias);

        // ── H12: Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalSet, ens, plattA, plattB, metaLabelWeights, metaLabelBias);
        _logger.LogDebug("Abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── M11: Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= MinEvalSamples)
        {
            temperatureScale = FitTemperatureScaling(postPruneCalSet, ens);
            _logger.LogDebug("Temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── M10: Ensemble diversity ───────────────────────────────────────────
        double ensembleDiversity = ComputeEnsembleDiversity(ens);
        _logger.LogDebug("Ensemble diversity (avg ρ)={Div:F4}", ensembleDiversity);
        if (hp.MaxEnsembleDiversity < 1.0 && ensembleDiversity > hp.MaxEnsembleDiversity)
            _logger.LogWarning(
                "Ensemble diversity warning: avg ρ={Div:F3} > threshold {Max:F2}. Consider increasing K.",
                ensembleDiversity, hp.MaxEnsembleDiversity);

        // ── M6: OOB-contribution pruning ──────────────────────────────────────
        int oobPrunedCount = 0;
        if (hp.OobPruningEnabled && K >= 2)
        {
            oobPrunedCount = PruneByOobContribution(
                balancedTrain, weights, biases, F, featureSubsets,
                ensMlpHW, ensMlpHB, hp.MlpHiddenDim, K);
            if (oobPrunedCount > 0)
                _logger.LogInformation("OOB pruning: removed {N}/{K} learners.", oobPrunedCount, K);
        }

        // ── M18: Decision boundary stats ─────────────────────────────────────
        var (dbMean, dbStd) = postPruneCalSet.Count >= MinEvalSamples
            ? ComputeDecisionBoundaryStats(postPruneCalSet, ens) : (0.0, 0.0);

        // ── M12: Durbin-Watson ────────────────────────────────────────────────
        double durbinWatson = ComputeDurbinWatson(balancedTrain, magWeights, magBias, F);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2}). Consider AR features.",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── M16: MI redundancy ────────────────────────────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(
                balancedTrain, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0 && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("MI redundancy: {N} redundant pairs: {Pairs}",
                    redundantPairs.Length, string.Join(", ", redundantPairs));
        }

        // ── 15. PSI quantile breakpoints ──────────────────────────────────────
        var balancedFeatures = new List<float[]>(balancedTrain.Count);
        foreach (var s in balancedTrain) balancedFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(balancedFeatures);

        // ── 17. Serialise model snapshot ──────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = K,
            Weights                    = weights,
            Biases                     = biases,
            MagWeights                 = magWeights,
            MagBias                    = magBias,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            PlattA                     = plattA,
            PlattB                     = plattB,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            Metrics                    = finalMetrics,
            TrainSamples               = balancedTrain.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedAtUtc               = DateTime.UtcNow,
            FeatureImportance          = featureImportance,
            FeatureImportanceScores    = calImportanceScores,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            FeatureSubsetIndices       = featureSubsets,
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            MetaWeights                = meta.Weights,
            MetaBias                   = meta.Bias,
            IsotonicBreakpoints        = isotonicBp,
            OobAccuracy                = oobAccuracy,
            ConformalQHat              = conformalQHat,
            JackknifeResiduals         = jackknifeResiduals,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            EnsembleSelectionWeights   = gesWeights,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            ParentModelId              = parentModelId ?? 0,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            FracDiffD                  = fracDiffD,
            AdaptiveLabelSmoothing     = labelSmoothing,
            SanitizedLearnerCount      = sanitizedCount,
            AgeDecayLambda             = hp.AgeDecayLambda,
            LearnerCalAccuracies       = learnerCalAccuracies,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            ConformalCoverage          = hp.ConformalCoverage,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            EnsembleDiversity          = ensembleDiversity,
            BrierSkillScore            = bss,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AvgKellyFraction           = avgKellyFraction,
            RedundantFeaturePairs      = redundantPairs,
            OobPrunedLearnerCount      = oobPrunedCount,
            DecisionBoundaryMean       = dbMean,
            DecisionBoundaryStd        = dbStd,
            DurbinWatsonStatistic      = durbinWatson,
            PolyLearnerStartIndex      = polyStart,
            MlpHiddenDim               = hp.MlpHiddenDim,
            MlpHiddenWeights           = ensMlpHW,
            MlpHiddenBiases            = ensMlpHB,
            SwaCheckpointCount         = swaCount,
        };

        byte[] modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);
        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        CancellationToken    ct)
    {
        int folds    = Math.Clamp(hp.WalkForwardFolds, 2, 5);
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < MinFoldSize)
        {
            _logger.LogWarning("SmoteModelTrainer CV: fold size too small ({Size}), skipping.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // ── H3/H14/M15: Parallel fold results ────────────────────────────────
        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        // Limit CV parallelism to avoid thread pool starvation from nested Parallel.For
        int cvMaxDop = Math.Max(1, Environment.ProcessorCount / 2);
        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = cvMaxDop }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // M15: PurgeHorizonBars — remove samples whose label horizon overlaps test fold
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

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(20, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(3,  hp.EarlyStoppingPatience / 2),
                K                     = Math.Max(10, hp.K / 3),
                NclLambda             = 0.0,  // force independent/parallel in CV folds
                DiversityLambda       = 0.0,
            };

            int foldOrigCount = foldTrain.Count;
            var (balancedFold, _) = ApplySmote(foldTrain, hp, F, ct);
            var (w, b, subs, _, _, cvMlpHW, cvMlpHB, _) = FitEnsemble(
                balancedFold, cvHp, F, cvHp.K, hp.LabelSmoothing, null, null, ct,
                forceSequential: false, originalCount: foldOrigCount);
            var (mw, mb) = FitLinearRegressor(balancedFold, F, cvHp);
            var m = EvaluateEnsemble(foldTest, w, b, mw, mb, 1.0, 0.0, F, subs, MetaLearner.None, 0.0,
                cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim);

            // H14: Per-feature mean |weight| for stability scoring
            var foldImp = new double[F];
            for (int ki = 0; ki < w.Length; ki++)
            {
                int[] s2 = subs is not null && ki < subs.Length
                    ? subs[ki]
                    : [.. Enumerable.Range(0, Math.Min(w[ki].Length, F))];
                foreach (int j in s2)
                    if (j < F) foldImp[j] += Math.Abs(w[ki][j]);
            }
            double kCount = w.Length > 0 ? w.Length : 1.0;
            for (int j = 0; j < F; j++) foldImp[j] /= kCount;

            // H3: Equity-curve gate — simulate P&L on fold test
            var preds = new (int Pred, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double rawP = EnsembleProb(foldTest[pi].Features, w, b, F, subs, MetaLearner.None,
                    cvMlpHW, cvMlpHB, cvHp.MlpHiddenDim);
                preds[pi] = (rawP >= 0.5 ? 1 : -1, foldTest[pi].Direction > 0 ? 1 : -1);
            }
            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(preds);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        });

        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

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

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // H4: Sharpe trend gate
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // H14: Feature stability scores (CV = σ/μ of mean |weight| per feature)
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int fc = foldImportances.Count;
            for (int j = 0; j < F; j++)
            {
                double sumI = 0.0;
                for (int fi = 0; fi < fc; fi++) sumI += foldImportances[fi][j];
                double meanI = sumI / fc;
                double varI  = 0.0;
                for (int fi = 0; fi < fc; fi++) { double d = foldImportances[fi][j] - meanI; varI += d * d; }
                double stdI = fc > 1 ? Math.Sqrt(varI / (fc - 1)) : 0.0;
                featureStabilityScores[j] = meanI > 1e-10 ? stdI / meanI : 0.0;
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

    // ── Borderline-SMOTE oversampling ─────────────────────────────────────────
    //
    // Classifies each minority sample by its K nearest neighbours drawn from ALL
    // training samples:
    //   SAFE   (0 majority neighbours)              → skip (no augmentation needed)
    //   DANGER (1 ≤ majorityCount ≤ ⌈K/2⌉)         → oversample from these
    //   NOISE  (majorityCount > ⌈K/2⌉)             → skip (dominated by majority)
    //
    // Synthetics are generated by interpolating between a DANGER sample and one of
    // its minority-only K nearest neighbours, exactly as in classic SMOTE.
    // If no DANGER samples exist the method falls back to all minority samples.
    //
    // KNN searches use an O(n × K) insertion-sorted buffer (K is small, typically 5)
    // and are parallelised across minority samples.

    private static (List<TrainingSample> Balanced, int SyntheticCount) ApplySmote(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        CancellationToken    ct)
    {
        var pos   = trainSet.Where(s => s.Direction > 0).ToList();
        var neg   = trainSet.Where(s => s.Direction <= 0).ToList();
        double ratio = neg.Count > 0 ? (double)pos.Count / neg.Count : 1.0;

        if ((ratio >= 0.8 && ratio <= 1.2) || pos.Count == 0 || neg.Count == 0)
            return (new List<TrainingSample>(trainSet), 0);

        var minority = ratio < 1.0 ? pos : neg;
        var majority = ratio >= 1.0 ? pos : neg;
        int deficit  = majority.Count - minority.Count;
        int kSmote   = Math.Max(1, Math.Min(hp.SmoteKNeighbors ?? 5, minority.Count - 1));

        if (minority.Count < 2 || deficit <= 0)
            return (new List<TrainingSample>(trainSet), 0);

        // ── Step 1: classify minority samples using all-sample KNN (parallel) ──
        // For each minority[i], count how many of its K nearest (in trainSet) are majority.
        // O(|trainSet| × K) per minority sample; parallelised across i.
        var majorityNeighborCounts = new int[minority.Count];
        var popts = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, minority.Count, popts, i =>
        {
            // Insertion-sorted buffer of K best distances (K is small, typically 5)
            var buf = new (double D, bool IsMaj)[kSmote];
            for (int ki = 0; ki < kSmote; ki++) buf[ki] = (double.MaxValue, false);
            double threshold = double.MaxValue;

            for (int j = 0; j < trainSet.Count; j++)
            {
                if (ReferenceEquals(trainSet[j], minority[i])) continue;
                double d = EuclideanDistSq(minority[i].Features, trainSet[j].Features);
                if (d >= threshold) continue;

                // Insert at tail and bubble up to maintain ascending order
                buf[kSmote - 1] = (d, trainSet[j].Direction != minority[i].Direction);
                int ins = kSmote - 1;
                while (ins > 0 && buf[ins].D < buf[ins - 1].D)
                {
                    (buf[ins], buf[ins - 1]) = (buf[ins - 1], buf[ins]);
                    ins--;
                }
                threshold = buf[kSmote - 1].D;
            }

            int majCount = 0;
            for (int ki = 0; ki < kSmote; ki++)
                if (buf[ki].D < double.MaxValue && buf[ki].IsMaj) majCount++;
            majorityNeighborCounts[i] = majCount;
        });

        // Collect DANGER-zone indices; fall back to all minority when none found
        var borderline = new List<int>(minority.Count);
        int dangerThresh = (kSmote / 2) + 1;
        for (int i = 0; i < minority.Count; i++)
            if (majorityNeighborCounts[i] >= 1 && majorityNeighborCounts[i] <= dangerThresh)
                borderline.Add(i);
        if (borderline.Count == 0)
            borderline.AddRange(Enumerable.Range(0, minority.Count));

        // ── Step 2: minority-only KNN for DANGER samples (interpolation targets) ─
        // O(|minority|² × K) — parallelised across i.
        var knnLists = new int[minority.Count][];
        Parallel.For(0, minority.Count, popts, i =>
        {
            var buf = new (double D, int Idx)[kSmote];
            for (int ki = 0; ki < kSmote; ki++) buf[ki] = (double.MaxValue, -1);
            double threshold = double.MaxValue;

            for (int j = 0; j < minority.Count; j++)
            {
                if (j == i) continue;
                double d = EuclideanDistSq(minority[i].Features, minority[j].Features);
                if (d >= threshold) continue;

                buf[kSmote - 1] = (d, j);
                int ins = kSmote - 1;
                while (ins > 0 && buf[ins].D < buf[ins - 1].D)
                {
                    (buf[ins], buf[ins - 1]) = (buf[ins - 1], buf[ins]);
                    ins--;
                }
                threshold = buf[kSmote - 1].D;
            }
            knnLists[i] = [.. buf.Where(b => b.Idx >= 0).Select(b => b.Idx)];
        });

        // ── Step 3: generate synthetics from DANGER samples ───────────────────
        // Seed is derived from the training data so the same dataset always produces
        // the same synthetics (reproducible) while different datasets differ.
        int smoteSeed = hp.SmoteSeed ?? HashCode.Combine(trainSet.Count, minority.Count, kSmote);
        var rng       = new Random(smoteSeed);
        var synthetic = new List<TrainingSample>(deficit);

        for (int s = 0; s < deficit; s++)
        {
            if (ct.IsCancellationRequested) break;

            int        seedIdx    = borderline[rng.Next(borderline.Count)];
            var        seedSample = minority[seedIdx];
            var        neighbors  = knnLists[seedIdx];
            if (neighbors.Length == 0) continue;

            var        neighbor   = minority[neighbors[rng.Next(neighbors.Length)]];
            float[]    synth      = new float[F];
            double     t          = rng.NextDouble();

            for (int j = 0; j < F; j++)
                synth[j] = (float)(seedSample.Features[j] + t * (neighbor.Features[j] - seedSample.Features[j]));

            synthetic.Add(new TrainingSample(synth, seedSample.Direction, seedSample.Magnitude));
        }

        var balanced = new List<TrainingSample>(trainSet.Count + synthetic.Count);
        balanced.AddRange(trainSet);
        balanced.AddRange(synthetic);
        return (balanced, synthetic.Count);
    }

    // ── Ensemble fit ──────────────────────────────────────────────────────────

    private (double[][] Weights, double[] Biases, int[][]? FeatureSubsets, int PolyStart,
             bool[][] OobMasks, double[][]? MlpHW, double[][]? MlpHB, int SwaCount) FitEnsemble(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        int                  K,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool                 forceSequential = false,
        int                  originalCount   = 0)
    {
        int    n       = trainSet.Count;
        double lr0     = hp.LearningRate > 0  ? hp.LearningRate  : 0.01;
        double l2      = hp.L2Lambda     > 0  ? hp.L2Lambda      : 0.001;
        double l1      = hp.L1Lambda     > 0  ? hp.L1Lambda      : 0.0;
        double noise   = hp.NoiseSigma   > 0  ? hp.NoiseSigma    : 0.0;
        double maxGrad = hp.MaxGradNorm  > 0  ? hp.MaxGradNorm   : 0.0;
        int    epochs  = hp.MaxEpochs    > 0  ? hp.MaxEpochs     : 20;
        int    patience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 5;
        int    batchSize = Math.Max(1, hp.MiniBatchSize);
        int    hiddenDim = Math.Max(0, hp.MlpHiddenDim);
        bool   useMlp    = hiddenDim > 0;

        // L8: Polynomial learners
        int polyStart        = hp.PolyLearnerFraction > 0 ? (int)(K * (1.0 - hp.PolyLearnerFraction)) : K;
        int polyFeatureCount = F + PolyPairCount;
        int[] top5Indices    = GetTop5FeatureIndices(warmStart, F);

        // Feature subsampling
        double fsr     = hp.FeatureSampleRatio > 0 ? hp.FeatureSampleRatio : 0.0;
        bool useSubsets = fsr > 0 && fsr < 1.0;

        // M8: biased feature sampling from warm-start importance scores
        bool useBiasedSampling = warmStart?.FeatureImportanceScores?.Length == F && useSubsets;

        // Temporal weights (blended with density-ratio weights if provided)
        double[] temporalWeights = ComputeTemporalWeights(n, hp.TemporalDecayLambda);
        // densityWeights may be shorter than temporalWeights (pre-SMOTE vs post-SMOTE).
        // Blend only the overlapping prefix; synthetic samples keep temporal-only weights.
        if (densityWeights is { Length: > 0 } && densityWeights.Length >= 1)
        {
            var blended = new double[n];
            double wSum = 0;
            for (int i = 0; i < n; i++)
            {
                blended[i] = temporalWeights[i] * (i < densityWeights.Length ? densityWeights[i] : 1.0);
                wSum += blended[i];
            }
            if (wSum > 1e-15) for (int i = 0; i < n; i++) blended[i] /= wSum;
            temporalWeights = blended;
        }

        // Bootstrap/OOB only over original samples (not SMOTE synthetics)
        int origN = originalCount > 0 ? Math.Min(originalCount, n) : n;
        var posIdx = Enumerable.Range(0, origN).Where(i => trainSet[i].Direction > 0).ToArray();
        var negIdx = Enumerable.Range(0, origN).Where(i => trainSet[i].Direction <= 0).ToArray();

        // Result arrays
        var weights      = new double[K][];
        var biases       = new double[K];
        var oobMasks     = new bool[K][];
        int[][]? fsubs   = useSubsets ? new int[K][] : null;
        double[][]? mlpHW = useMlp ? new double[K][] : null;
        double[][]? mlpHB = useMlp ? new double[K][] : null;
        var swaCountPerLearner = new int[K];

        // Determine if learners are independent (can run in parallel)
        bool learnersIndependent =
            hp.NclLambda             <= 0.0 &&
            hp.DiversityLambda       <= 0.0 &&
            hp.NoiseCorrectionThreshold <= 0.0;
        bool runParallel = !forceSequential && learnersIndependent;

        // Split off a small validation set for adaptive LR decay (M1)
        // valSet indices are excluded from OOB to avoid feedback between LR decay and early stopping
        int valSize  = Math.Max(20, n / 10);
        int valStart = n - valSize;
        var valSet   = trainSet[valStart..];
        var fitSet   = trainSet[..valStart];

        // ── Per-learner training closure ──────────────────────────────────────
        void TrainLearner(int k)
        {
            ct.ThrowIfCancellationRequested();

            bool isPoly    = hp.PolyLearnerFraction > 0 && k >= polyStart;
            int effF       = isPoly ? polyFeatureCount : F;
            var rng        = new Random(42 + k * 97 + 13);
            // 2-layer MLP: pack L1 (hiddenDim×fk) + L2 (hiddenDim×hiddenDim) into hW,
            // and L1 biases (hiddenDim) + L2 biases (hiddenDim) into hB.
            bool isDeep2   = useMlp && hp.MlpHiddenLayers >= 2;

            // Feature subset
            int[] subset;
            if (useSubsets)
                subset = isPoly ? GenerateFeatureSubset(effF, fsr, 42 + k * 97)
                       : useBiasedSampling ? GenerateBiasedFeatureSubset(F, fsr, warmStart!.FeatureImportanceScores, 42 + k * 97)
                       : GenerateFeatureSubset(F, fsr, 42 + k * 97);
            else
                subset = [.. Enumerable.Range(0, effF)];

            if (fsubs is not null) fsubs[k] = subset;
            int fk = subset.Length;

            // MLP output dim = hiddenDim (MLP), or fk (linear)
            int outDim = useMlp ? hiddenDim : fk;

            // Weight initialisation
            double[] w;
            double   b;
            double[]? hW = null;
            double[]? hB = null;

            if (useMlp)
            {
                // Packed layout: L1 weights (hiddenDim×fk), then L2 weights (hiddenDim×hiddenDim) if deep.
                int initHWSize = isDeep2 ? hiddenDim * fk + hiddenDim * hiddenDim : hiddenDim * fk;
                int initHBSize = isDeep2 ? hiddenDim * 2 : hiddenDim;
                hW = new double[initHWSize];
                hB = new double[initHBSize];
                double xavStd1 = Math.Sqrt(2.0 / (fk + hiddenDim));
                for (int i = 0; i < hiddenDim * fk; i++) hW[i] = SampleNormal(rng) * xavStd1;
                if (isDeep2)
                {
                    double xavStd2 = Math.Sqrt(2.0 / (hiddenDim + hiddenDim));
                    for (int i = hiddenDim * fk; i < initHWSize; i++) hW[i] = SampleNormal(rng) * xavStd2;
                }

                if (warmStart is not null && k < warmStart.Weights?.Length &&
                    warmStart.Weights[k].Length == outDim)
                {
                    w = [.. warmStart.Weights[k]];
                    b = k < warmStart.Biases?.Length ? warmStart.Biases[k] : 0.0;
                    // Size check: exact match required (handles 1-layer ↔ 2-layer transition gracefully)
                    if (warmStart.MlpHiddenWeights?[k]?.Length == hW.Length)
                        Array.Copy(warmStart.MlpHiddenWeights[k], hW, hW.Length);
                    if (warmStart.MlpHiddenBiases?[k]?.Length == hB.Length)
                        Array.Copy(warmStart.MlpHiddenBiases[k], hB, hB.Length);
                }
                else
                {
                    w = new double[outDim];
                    double outScale = Math.Sqrt(6.0 / (hiddenDim + 1));
                    for (int ji = 0; ji < outDim; ji++) w[ji] = (rng.NextDouble() * 2.0 - 1.0) * outScale;
                    b = 0.0;
                }
            }
            else
            {
                w = InitWeights(warmStart, k, fk, subset, F, rng);
                b = warmStart is not null && k < warmStart.Biases?.Length ? warmStart.Biases[k] : 0.0;
            }

            // Bootstrap over original samples; synthetics always included in training
            int[] bootstrap = StratifiedBootstrap(posIdx, negIdx, origN, temporalWeights, rng);
            var   inBag     = new HashSet<int>(bootstrap);
            // OOB mask: original samples not in bag and not in valSet are OOB;
            // synthetics (index >= origN) and valSet samples (index >= valStart) are never OOB
            oobMasks[k] = [.. Enumerable.Range(0, n).Select(i => i < origN && i < valStart && !inBag.Contains(i))];

            // Adam state — M19: ArrayPool for Adam moment arrays
            int hWSize = useMlp ? hW!.Length : 0;
            int hBSize = useMlp ? hB!.Length : 0;
            double[] m1   = ArrayPool<double>.Shared.Rent(outDim);
            double[] v1   = ArrayPool<double>.Shared.Rent(outDim);
            double[] hm1  = useMlp ? ArrayPool<double>.Shared.Rent(hWSize) : [];
            double[] hv1  = useMlp ? ArrayPool<double>.Shared.Rent(hWSize) : [];
            double[] hbm1 = useMlp ? ArrayPool<double>.Shared.Rent(hBSize) : [];
            double[] hbv1 = useMlp ? ArrayPool<double>.Shared.Rent(hBSize) : [];
            Array.Clear(m1, 0, outDim);
            Array.Clear(v1, 0, outDim);
            if (useMlp) { Array.Clear(hm1, 0, hWSize); Array.Clear(hv1, 0, hWSize); Array.Clear(hbm1, 0, hBSize); Array.Clear(hbv1, 0, hBSize); }
            double mb = 0, vb = 0;

            // C1: Running bias-correction products (avoid per-sample Math.Pow)
            double beta1t = 1.0, beta2t = 1.0;
            int    step   = 0;

            // Early stopping
            double   bestLoss = double.MaxValue;
            int      noImprove = 0;
            double[] bestW = (double[])w.Clone();
            double   bestB = b;
            double[]? bestHW = useMlp ? (double[])hW!.Clone() : null;
            double[]? bestHB = useMlp ? (double[])hB!.Clone() : null;

            // SWA accumulators (M17)
            double[] swaW  = new double[outDim];
            double   swaB  = 0.0;
            double[]? swaHW = useMlp ? new double[hWSize] : null;
            double[]? swaHB = useMlp ? new double[hBSize] : null;
            int swaCount   = 0;

            // M1: Adaptive LR decay state
            double adaptedLr0 = lr0;
            double valAccBest = 0.0;
            bool   lrDecayed  = false;

            // Cosine-annealing LR
            double GetLr(int ep) => adaptedLr0 * 0.5 * (1.0 + Math.Cos(Math.PI * ep / epochs));

            // Preallocate gradient arrays (reused per batch)
            double[] gw   = new double[outDim];
            double[]? ghW = useMlp ? new double[hWSize] : null;
            double[]? ghB = useMlp ? new double[hBSize] : null;

            // Preallocate MLP activation arrays (reused per sample)
            double[]? hL1PreBuf = useMlp ? new double[hiddenDim] : null;
            double[]? hL1ActBuf = useMlp ? new double[hiddenDim] : null;
            double[]? hL2PreBuf = isDeep2 ? new double[hiddenDim] : null;
            double[]? hL2ActBuf = isDeep2 ? new double[hiddenDim] : null;
            double[]? dL1Buf    = isDeep2 ? new double[hiddenDim] : null;
            double[] hFinalRef  = isDeep2 ? hL2ActBuf! : (useMlp ? hL1ActBuf! : []);

            try
            {
                for (int ep = 0; ep < epochs && !ct.IsCancellationRequested; ep++)
                {
                    double lr = GetLr(ep);

                    // Shuffle bootstrap
                    for (int i = bootstrap.Length - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (bootstrap[i], bootstrap[j]) = (bootstrap[j], bootstrap[i]);
                    }

                    bool nanHit = false;

                    // L10: Mini-batch loop
                    for (int bStart = 0; bStart < bootstrap.Length && !nanHit; bStart += batchSize)
                    {
                        int actual = Math.Min(batchSize, bootstrap.Length - bStart);

                        // Clear gradient accumulators
                        Array.Clear(gw, 0, outDim);
                        double gb = 0;
                        if (useMlp) { Array.Clear(ghW!, 0, hWSize); Array.Clear(ghB!, 0, hBSize); }

                        for (int bi = 0; bi < actual; bi++)
                        {
                            int idx = bootstrap[bStart + bi];
                            float[] xFull = trainSet[idx].Features;

                            // L8: Augment features for poly learners
                            if (isPoly) xFull = BuildPolyAugmentedFeatures(xFull, top5Indices, F);

                            // L7: Mixup
                            if (hp.MixupAlpha > 0.0 && rng.NextDouble() < 0.5)
                            {
                                int partnerIdx = bootstrap[rng.Next(bootstrap.Length)];
                                float[] xPartner = trainSet[partnerIdx].Features;
                                if (isPoly) xPartner = BuildPolyAugmentedFeatures(xPartner, top5Indices, F);
                                double lam = SampleBeta(hp.MixupAlpha, rng);
                                var xMixed = new float[xFull.Length];
                                for (int j = 0; j < xFull.Length; j++)
                                    xMixed[j] = (float)(lam * xFull[j] + (1 - lam) * xPartner[j]);
                                xFull = xMixed;
                            }

                            // L6: AtrLabelSensitivity — soft labels
                            double yRaw;
                            if (hp.AtrLabelSensitivity > 0.0)
                            {
                                double signedMag = trainSet[idx].Magnitude * (trainSet[idx].Direction > 0 ? 1.0 : -1.0);
                                yRaw = Sigmoid(signedMag / hp.AtrLabelSensitivity);
                            }
                            else
                            {
                                yRaw = trainSet[idx].Direction > 0 ? 1.0 : 0.0;
                            }

                            double y = labelSmoothing > 0
                                ? yRaw * (1.0 - labelSmoothing) + 0.5 * labelSmoothing
                                : yRaw;

                            // Forward pass (reuse preallocated activation buffers)
                            double logit;

                            if (useMlp)
                            {
                                // Layer 1: input → hiddenDim
                                for (int hj = 0; hj < hiddenDim; hj++)
                                {
                                    double act = hB![hj];
                                    for (int ji = 0; ji < fk; ji++)
                                        act += hW![hj * fk + ji] * xFull[subset[ji]];
                                    hL1PreBuf![hj] = act;
                                    hL1ActBuf![hj] = Math.Max(0, act); // ReLU
                                }
                                if (isDeep2)
                                {
                                    // Layer 2: hiddenDim → hiddenDim
                                    int l2WOff = hiddenDim * fk;
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        double act = hB![hiddenDim + hj];
                                        for (int ji = 0; ji < hiddenDim; ji++)
                                            act += hW![l2WOff + hj * hiddenDim + ji] * hL1ActBuf![ji];
                                        hL2PreBuf![hj] = act;
                                        hL2ActBuf![hj] = Math.Max(0, act); // ReLU
                                    }
                                }
                                logit = b;
                                for (int hj = 0; hj < hiddenDim; hj++) logit += w[hj] * hFinalRef[hj];
                            }
                            else
                            {
                                logit = b;
                                for (int ji = 0; ji < fk; ji++)
                                {
                                    double xj = xFull[subset[ji]];
                                    if (noise > 0) xj += noise * SampleNormal(rng);
                                    logit += w[ji] * xj;
                                }
                            }

                            double p   = Sigmoid(logit);
                            double err = p - y;

                            // L4: FpCostWeight asymmetric BCE gradient
                            if (hp.FpCostWeight > 0.0 && Math.Abs(hp.FpCostWeight - 0.5) > 1e-6)
                            {
                                double yBin = yRaw > 0.5 ? 1.0 : 0.0;
                                double fpW  = yBin > 0.5 ? hp.FpCostWeight : (1.0 - hp.FpCostWeight);
                                err *= fpW / 0.5; // normalise so default 0.5 → no change
                            }

                            // L3: SCE — add reverse cross-entropy gradient
                            if (hp.UseSymmetricCE && hp.SymmetricCeAlpha > 0.0)
                            {
                                double rceGrad = (-Math.Log(Math.Max(y, 1e-7)) + Math.Log(Math.Max(1 - y, 1e-7)))
                                    * p * (1 - p);
                                err += hp.SymmetricCeAlpha * rceGrad;
                            }

                            // L1: NCL — gradient penalty using prior learners' avg prediction
                            if (hp.NclLambda > 0.0 && k > 0)
                            {
                                double avgP = ComputeAvgPriorLearners(xFull, weights, biases, k, F, fsubs,
                                    mlpHW, mlpHB, hiddenDim);
                                err += hp.NclLambda * p * (1 - p) * (2 * p - avgP);
                            }

                            // L2: DiversityLambda — push away from ensemble mean
                            if (hp.DiversityLambda > 0.0 && k > 0)
                            {
                                double avgP = ComputeAvgPriorLearners(xFull, weights, biases, k, F, fsubs,
                                    mlpHW, mlpHB, hiddenDim);
                                err += -hp.DiversityLambda * 2.0 * (p - avgP) * p * (1 - p);
                            }

                            // L5: Noise correction — downweight likely mislabelled samples
                            if (hp.NoiseCorrectionThreshold > 0.0)
                            {
                                double yBin = yRaw > 0.5 ? 1.0 : 0.0;
                                double noiseW = 1.0;
                                if (yBin == 1.0 && p < hp.NoiseCorrectionThreshold)
                                    noiseW = p;
                                else if (yBin == 0.0 && (1 - p) < hp.NoiseCorrectionThreshold)
                                    noiseW = 1 - p;
                                err *= noiseW;
                            }

                            // Accumulate bias gradient
                            gb += err;

                            if (useMlp)
                            {
                                if (isDeep2)
                                {
                                    int l2WOff = hiddenDim * fk;
                                    Array.Clear(dL1Buf!, 0, hiddenDim);
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        gw[hj] += err * hL2ActBuf![hj] + l2 * w[hj];
                                        double reluGate2 = hL2PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dOut = err * w[hj] * reluGate2;
                                        ghB![hiddenDim + hj] += dOut;
                                        for (int ji = 0; ji < hiddenDim; ji++)
                                        {
                                            int wIdx = l2WOff + hj * hiddenDim + ji;
                                            ghW![wIdx] += dOut * hL1ActBuf![ji] + l2 * hW![wIdx];
                                            dL1Buf![ji] += dOut * hW![wIdx];
                                        }
                                    }
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        double reluGate1 = hL1PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dAct1     = dL1Buf![hj] * reluGate1;
                                        ghB![hj] += dAct1;
                                        for (int ji2 = 0; ji2 < fk; ji2++)
                                            ghW![hj * fk + ji2] += dAct1 * xFull[subset[ji2]] + l2 * hW![hj * fk + ji2];
                                    }
                                }
                                else
                                {
                                    for (int hj = 0; hj < hiddenDim; hj++)
                                    {
                                        gw[hj] += err * hL1ActBuf![hj] + l2 * w[hj];
                                        double reluGate = hL1PreBuf![hj] > 0 ? 1.0 : 0.0;
                                        double dH = err * w[hj] * reluGate;
                                        ghB![hj] += dH;
                                        for (int ji2 = 0; ji2 < fk; ji2++)
                                            ghW![hj * fk + ji2] += dH * xFull[subset[ji2]] + l2 * hW![hj * fk + ji2];
                                    }
                                }
                            }
                            else
                            {
                                for (int ji = 0; ji < fk; ji++)
                                    gw[ji] += err * xFull[subset[ji]] + l2 * w[ji];
                            }
                        } // end per-sample accumulation

                        // Average gradients over batch
                        double invBatch = 1.0 / actual;
                        gb *= invBatch;
                        for (int ji = 0; ji < outDim; ji++) gw[ji] *= invBatch;
                        if (useMlp)
                        {
                            for (int i = 0; i < hWSize; i++) ghW![i] *= invBatch;
                            for (int hj = 0; hj < hBSize; hj++) ghB![hj] *= invBatch;
                        }

                        // Gradient clipping
                        if (maxGrad > 0)
                        {
                            double gnorm = gb * gb;
                            for (int ji = 0; ji < outDim; ji++) gnorm += gw[ji] * gw[ji];
                            if (useMlp)
                            {
                                for (int i = 0; i < hWSize; i++) gnorm += ghW![i] * ghW![i];
                                for (int hj = 0; hj < hBSize; hj++) gnorm += ghB![hj] * ghB![hj];
                            }
                            gnorm = Math.Sqrt(gnorm);
                            if (gnorm > maxGrad)
                            {
                                double scale = maxGrad / gnorm;
                                gb *= scale;
                                for (int ji = 0; ji < outDim; ji++) gw[ji] *= scale;
                                if (useMlp)
                                {
                                    for (int i = 0; i < hWSize; i++) ghW![i] *= scale;
                                    for (int hj = 0; hj < hBSize; hj++) ghB![hj] *= scale;
                                }
                            }
                        }

                        // C1: Running Adam bias-correction products (one step per batch)
                        step++;
                        beta1t *= AdamBeta1;
                        beta2t *= AdamBeta2;

                        // Adam update: bias
                        mb = AdamBeta1 * mb + (1 - AdamBeta1) * gb;
                        vb = AdamBeta2 * vb + (1 - AdamBeta2) * gb * gb;
                        b -= lr * (mb / (1 - beta1t)) / (Math.Sqrt(vb / (1 - beta2t)) + AdamEpsilon);

                        // Adam update: output weights
                        for (int ji = 0; ji < outDim; ji++)
                        {
                            m1[ji] = AdamBeta1 * m1[ji] + (1 - AdamBeta1) * gw[ji];
                            v1[ji] = AdamBeta2 * v1[ji] + (1 - AdamBeta2) * gw[ji] * gw[ji];
                            w[ji] -= lr * (m1[ji] / (1 - beta1t)) / (Math.Sqrt(v1[ji] / (1 - beta2t)) + AdamEpsilon);

                            // L1 proximal soft-thresholding
                            if (l1 > 0)
                                w[ji] = Math.Sign(w[ji]) * Math.Max(0, Math.Abs(w[ji]) - l1 * lr);

                            // M2: Weight magnitude clipping
                            if (hp.MaxWeightMagnitude > 0)
                                w[ji] = Math.Clamp(w[ji], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                        }

                        // Adam update: MLP hidden layers
                        if (useMlp)
                        {
                            for (int i = 0; i < hWSize; i++)
                            {
                                hm1[i] = AdamBeta1 * hm1[i] + (1 - AdamBeta1) * ghW![i];
                                hv1[i] = AdamBeta2 * hv1[i] + (1 - AdamBeta2) * ghW![i] * ghW![i];
                                hW![i] -= lr * (hm1[i] / (1 - beta1t)) / (Math.Sqrt(hv1[i] / (1 - beta2t)) + AdamEpsilon);
                                if (hp.MaxWeightMagnitude > 0)
                                    hW![i] = Math.Clamp(hW![i], -hp.MaxWeightMagnitude, hp.MaxWeightMagnitude);
                            }
                            for (int hj = 0; hj < hBSize; hj++)
                            {
                                hbm1[hj] = AdamBeta1 * hbm1[hj] + (1 - AdamBeta1) * ghB![hj];
                                hbv1[hj] = AdamBeta2 * hbv1[hj] + (1 - AdamBeta2) * ghB![hj] * ghB![hj];
                                hB![hj] -= lr * (hbm1[hj] / (1 - beta1t)) / (Math.Sqrt(hbv1[hj] / (1 - beta2t)) + AdamEpsilon);
                            }
                        }

                        // C2: Intra-epoch NaN/Inf guard — immediate rollback
                        bool hasNaN = !double.IsFinite(b);
                        if (!hasNaN)
                            for (int ji = 0; ji < outDim; ji++)
                                if (!double.IsFinite(w[ji])) { hasNaN = true; break; }

                        if (hasNaN)
                        {
                            w = (double[])bestW.Clone(); b = bestB;
                            if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                            nanHit = true;
                        }
                    } // end batch loop

                    if (nanHit) break;

                    // M17: SWA weight accumulation
                    if (hp.SwaStartEpoch > 0 && ep >= hp.SwaStartEpoch &&
                        hp.SwaFrequency > 0 && (ep - hp.SwaStartEpoch) % hp.SwaFrequency == 0)
                    {
                        swaCount++;
                        for (int ji = 0; ji < outDim; ji++)
                            swaW[ji] += (w[ji] - swaW[ji]) / swaCount;
                        swaB += (b - swaB) / swaCount;
                        if (useMlp && swaHW is not null)
                        {
                            for (int i = 0; i < hWSize; i++) swaHW![i] += (hW![i] - swaHW![i]) / swaCount;
                            for (int hj = 0; hj < hBSize; hj++) swaHB![hj] += (hB![hj] - swaHB![hj]) / swaCount;
                        }
                    }

                    // Per-learner early stopping via OOB cross-entropy loss
                    if (patience > 0 && oobMasks[k].Any(x => x))
                    {
                        double oobLoss = ComputeOobLoss(fitSet, oobMasks[k], w, b, subset, fk,
                            labelSmoothing, hW, hB, hiddenDim);
                        if (oobLoss < bestLoss - 1e-5)
                        {
                            bestLoss = oobLoss; noImprove = 0;
                            bestW = (double[])w.Clone(); bestB = b;
                            if (useMlp) { bestHW = (double[])hW!.Clone(); bestHB = (double[])hB!.Clone(); }
                        }
                        else if (++noImprove >= patience) break;
                    }

                    // M1: Adaptive LR decay — trigger once if rolling val acc drops > 5%
                    if (!lrDecayed && hp.AdaptiveLrDecayFactor > 0.0 && valSet.Count > 0 && ep > 0 && ep % 5 == 0)
                    {
                        double valAcc = ComputeValAccuracy(valSet, w, b, subset, fk, hW, hB, hiddenDim);
                        if (valAccBest == 0.0) valAccBest = valAcc;
                        else if (valAcc < valAccBest - 0.05)
                        {
                            adaptedLr0 *= hp.AdaptiveLrDecayFactor;
                            lrDecayed   = true;
                        }
                        else
                            valAccBest = Math.Max(valAccBest, valAcc);
                    }
                } // end epoch loop

                // Apply SWA weights if accumulated, but validate against early-stopped best
                if (swaCount > 0)
                {
                    Array.Copy(swaW, w, outDim);
                    b = swaB;
                    if (useMlp && swaHW is not null) { Array.Copy(swaHW, hW!, hW!.Length); Array.Copy(swaHB!, hB!, hB!.Length); }

                    // If early stopping tracked a best loss, verify SWA didn't degrade
                    if (patience > 0 && bestLoss < double.MaxValue && oobMasks[k].Any(x => x))
                    {
                        double swaLoss = ComputeOobLoss(fitSet, oobMasks[k], w, b, subset, fk,
                            labelSmoothing, hW, hB, hiddenDim);
                        if (swaLoss > bestLoss * 1.1) // SWA is >10% worse — fall back
                        {
                            w = bestW; b = bestB;
                            if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                            swaCount = 0; // mark as not used
                        }
                    }
                }
                else if (patience > 0)
                {
                    w = bestW; b = bestB;
                    if (useMlp && bestHW is not null) { Array.Copy(bestHW, hW!, hW!.Length); Array.Copy(bestHB!, hB!, hB!.Length); }
                }

                // Expand linear weights to full F (no expansion for MLP — output weights stay as [hiddenDim])
                if (!useMlp && useSubsets)
                {
                    var fullW = new double[F];
                    for (int ji = 0; ji < fk; ji++) fullW[subset[ji]] = w[ji];
                    weights[k] = fullW;
                }
                else
                {
                    weights[k] = w;
                }

                biases[k] = b;
                if (useMlp) { mlpHW![k] = hW!; mlpHB![k] = hB!; }
                swaCountPerLearner[k] = swaCount;
            }
            finally
            {
                ArrayPool<double>.Shared.Return(m1);
                ArrayPool<double>.Shared.Return(v1);
                if (useMlp)
                {
                    ArrayPool<double>.Shared.Return(hm1);
                    ArrayPool<double>.Shared.Return(hv1);
                    ArrayPool<double>.Shared.Return(hbm1);
                    ArrayPool<double>.Shared.Return(hbv1);
                }
            }
        } // end TrainLearner

        // ── Run learner training ──────────────────────────────────────────────
        if (runParallel)
        {
            Parallel.For(0, K, new ParallelOptions { CancellationToken = ct }, k =>
            {
                try { TrainLearner(k); }
                catch (OperationCanceledException) { throw; }
            });
        }
        else
        {
            for (int k = 0; k < K; k++)
            {
                ct.ThrowIfCancellationRequested();
                TrainLearner(k);
            }
        }

        // M13: MaxLearnerCorrelation enforcement — re-init highly correlated pairs
        if (hp.MaxLearnerCorrelation is > 0.0 and < 1.0)
        {
            for (int i = 0; i < K - 1; i++)
            for (int j = i + 1; j < K; j++)
            {
                double rho = PearsonCorrelation(weights[i], weights[j], F);
                if (rho > hp.MaxLearnerCorrelation)
                {
                    // Zero out the later learner — effectively prunes it from the ensemble
                    // rather than injecting untrained random noise
                    Array.Clear(weights[j], 0, weights[j].Length);
                    biases[j] = 0.0;
                    if (mlpHW?[j] is not null) Array.Clear(mlpHW[j], 0, mlpHW[j].Length);
                    if (mlpHB?[j] is not null) Array.Clear(mlpHB[j], 0, mlpHB[j].Length);
                }
            }
        }

        int totalSwaCount = swaCountPerLearner.Length > 0 ? swaCountPerLearner.Max() : 0;
        return (weights, biases, fsubs, polyStart, oobMasks, mlpHW, mlpHB, totalSwaCount);
    }

    // ── Magnitude regressors ──────────────────────────────────────────────────

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> trainSet,
        int                  F,
        TrainingHyperparams  hp)
    {
        double lr  = hp.LearningRate > 0 ? hp.LearningRate * 0.1 : 0.001;
        double l2  = hp.L2Lambda     > 0 ? hp.L2Lambda            : 0.001;
        int epochs = Math.Max(5, hp.MaxEpochs / 4);

        var    w = new double[F];
        double b = 0;
        var    bestW = new double[F];
        double bestB = 0;

        for (int ep = 0; ep < epochs; ep++)
        {
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = Math.Clamp(pred - s.Magnitude, -10.0, 10.0);
                b -= lr * err;
                for (int j = 0; j < F; j++)
                    w[j] -= lr * (err * s.Features[j] + l2 * w[j]);
            }

            // NaN/Inf guard — rollback to last good state
            bool bad = !double.IsFinite(b);
            if (!bad) for (int j = 0; j < F; j++) if (!double.IsFinite(w[j])) { bad = true; break; }
            if (bad) { Array.Copy(bestW, w, F); b = bestB; break; }
            Array.Copy(w, bestW, F); bestB = b;
        }

        return (w, b);
    }

    // L11: Quantile magnitude regressor (pinball loss)
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> trainSet,
        int                  F,
        double               tau)
    {
        double lr  = 0.001;
        double l2  = 0.001;
        int epochs = 20;

        var    w = new double[F];
        double b = 0;
        var    bestW = new double[F];
        double bestB = 0;

        for (int ep = 0; ep < epochs; ep++)
        {
            foreach (var s in trainSet)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double residual = s.Magnitude - pred;
                double dLdpred = residual >= 0 ? -tau : (1.0 - tau);
                b -= lr * dLdpred;
                for (int j = 0; j < F; j++)
                    w[j] -= lr * (dLdpred * s.Features[j] + l2 * w[j]);
            }

            bool bad = !double.IsFinite(b);
            if (!bad) for (int j = 0; j < F; j++) if (!double.IsFinite(w[j])) { bad = true; break; }
            if (bad) { Array.Copy(bestW, w, F); b = bestB; break; }
            Array.Copy(w, bestW, F); bestB = b;
        }

        return (w, b);
    }

    // ── H5: Stacking meta-learner ─────────────────────────────────────────────

    private static MetaLearner FitMetaLearner(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW   = null,
        double[][]?          mlpHB   = null,
        int                  hidDim  = 0)
    {
        int K = weights.Length;
        if (calSet.Count < MinCalSamples || K < 2) return MetaLearner.None;

        var mw = new double[K];
        double mb = 0;
        const double lr = 0.01;
        const int    ep = DefaultCalibrationEpochs;

        for (int e = 0; e < ep; e++)
        {
            double dmb = 0;
            var dw = new double[K];
            foreach (var s in calSet)
            {
                double metaLogit = mb;
                var    perK      = new double[K];
                for (int k = 0; k < K; k++)
                {
                    perK[k] = SingleLearnerProb(s.Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
                    metaLogit += mw[k] * perK[k];
                }
                double p   = Sigmoid(metaLogit);
                double err = p - (s.Direction > 0 ? 1.0 : 0.0);
                dmb += err;
                for (int k = 0; k < K; k++) dw[k] += err * perK[k];
            }
            double inv = 1.0 / calSet.Count;
            mb -= lr * dmb * inv;
            for (int k = 0; k < K; k++) mw[k] -= lr * dw[k] * inv;
        }

        return new MetaLearner(mw, mb);
    }

    // ── Platt calibration ─────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW   = null,
        double[][]?          mlpHB   = null,
        int                  hidDim  = 0)
    {
        double A = 1.0, B = 0.0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double dA = 0, dB = 0;
            foreach (var s in calSet)
            {
                double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double calibP = Sigmoid(A * logit + B);
                double err    = calibP - (s.Direction > 0 ? 1.0 : 0.0);
                dA += err * logit;
                dB += err;
            }
            double inv = 1.0 / calSet.Count;
            A -= lr * dA * inv;
            B -= lr * dB * inv;
        }

        return (A, B);
    }

    // ── M3: Class-conditional Platt ───────────────────────────────────────────

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW = null,
        double[][]?          mlpHB = null,
        int                  hidDim = 0)
    {
        // Split by predicted direction (not true label) so each subset contains
        // both correct and incorrect predictions — making calibration well-posed
        var buyPredSet  = new List<TrainingSample>();
        var sellPredSet = new List<TrainingSample>();
        foreach (var s in calSet)
        {
            double rawP = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            if (rawP >= 0.5)
                buyPredSet.Add(s);
            else
                sellPredSet.Add(s);
        }

        if (buyPredSet.Count < 5 || sellPredSet.Count < 5)
            return (1.0, 0.0, 1.0, 0.0); // identity on logit scale

        static (double A, double B) FitOnSubset(
            List<TrainingSample> sub, double[][] w, double[] b, int f,
            int[][]? subs, MetaLearner m, double[][]? mHW, double[][]? mHB, int hd)
        {
            double A = 1.0, B = 0.0;
            const double lr = 0.01;
            for (int ep = 0; ep < ClassCondPlattEpochs; ep++)
            {
                double dA = 0, dB = 0;
                foreach (var s in sub)
                {
                    double rawP   = EnsembleProb(s.Features, w, b, f, subs, m, mHW, mHB, hd);
                    double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                    double calibP = Sigmoid(A * logit + B);
                    double err    = calibP - (s.Direction > 0 ? 1.0 : 0.0);
                    dA += err * logit; dB += err;
                }
                double inv = 1.0 / sub.Count;
                A -= lr * dA * inv;
                B -= lr * dB * inv;
            }
            return (A, B);
        }

        var (AB, BB) = FitOnSubset(buyPredSet,  weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var (AS, BS) = FitOnSubset(sellPredSet, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        return (AB, BB, AS, BS);
    }

    // ── H8: Isotonic calibration (PAVA) ──────────────────────────────────────

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (calSet.Count < MinEvalSamples) return [];

        // Collect (platt-calibrated probability, label) pairs, sorted by probability
        var pairs = new List<(double P, double Y)>(calSet.Count);
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            pairs.Add((calibP, s.Direction > 0 ? 1.0 : 0.0));
        }
        pairs.Sort((a, b) => a.P.CompareTo(b.P));

        // Pool Adjacent Violators Algorithm (PAVA) — O(n) stack-based.
        // Each block stores (sum of Y values, count). Invariant: mean of each block
        // is non-decreasing from bottom to top of the stack.
        int n = pairs.Count;
        var stack = new List<(double SumY, int Count)>(n);
        foreach (var (_, y) in pairs)
        {
            stack.Add((y, 1));
            // Merge backward while the block below has a larger mean (violates isotonicity)
            while (stack.Count >= 2)
            {
                var (loSumY, loCount) = stack[^2];
                var (hiSumY, hiCount) = stack[^1];
                if (loSumY / loCount <= hiSumY / hiCount) break;
                stack.RemoveAt(stack.Count - 1);
                stack[^1] = (loSumY + hiSumY, loCount + hiCount);
            }
        }

        // Expand blocks back to per-sample isotonic values
        var isotonic = new double[n];
        int pos = 0;
        foreach (var (sumY, count) in stack)
        {
            double mean = sumY / count;
            for (int bi = 0; bi < count; bi++) isotonic[pos++] = mean;
        }

        // Encode as interleaved [x0, y0, x1, y1, ...] breakpoints
        // Stride-sample to cap size (preserves quantile distribution like jackknife residuals)
        const int MaxIsotonicBreakpoints = 1_000;
        int stride = n > MaxIsotonicBreakpoints ? n / MaxIsotonicBreakpoints : 1;
        int capacity = (n / stride + 1) * 2;
        var bps = new List<double>(capacity);
        for (int i = 0; i < n; i += stride) { bps.Add(pairs[i].P); bps.Add(isotonic[i]); }
        // Always include the last point for boundary coverage
        if (stride > 1 && n > 0) { bps.Add(pairs[n - 1].P); bps.Add(isotonic[n - 1]); }
        return [.. bps];
    }

    // ── M11: Temperature scaling ──────────────────────────────────────────────

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        double T = 1.0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double dT = 0;
            foreach (var s in calSet)
            {
                double rawP  = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double sP    = Sigmoid(logit / T);
                double err   = sP - (s.Direction > 0 ? 1.0 : 0.0);
                // dL/dT = err * sP * (1-sP) * (-logit/T²)
                dT += err * sP * (1 - sP) * (-logit / (T * T));
            }
            T -= lr * dT / calSet.Count;
            T  = Math.Max(0.1, Math.Min(10.0, T)); // sanity bounds
        }

        return T;
    }

    // ── H11: Meta-label secondary classifier ─────────────────────────────────

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW            = null,
        double[][]?          mlpHB            = null,
        int                  hidDim           = 0,
        double[]?            importanceScores = null)
    {
        if (calSet.Count < MinCalSamples) return ([], 0.0);

        // Determine top-3 feature indices by cal-set importance; fall back to [0,1,2]
        int[] top3 = importanceScores is { Length: > 0 }
            ? [.. Enumerable.Range(0, Math.Min(F, importanceScores.Length))
                            .OrderByDescending(j => importanceScores[j])
                            .Take(3)]
            : [0, Math.Min(1, F - 1), Math.Min(2, F - 1)];

        // Input features: [ensembleLogit, ensembleStd, top3[0], top3[1], top3[2]]
        const int MetaInputDim = 5;
        var mw = new double[MetaInputDim];
        double mb = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double[] dw = new double[MetaInputDim];
            double   db = 0;

            foreach (var s in calSet)
            {
                var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
                double logit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
                double[] x = [logit, stdP,
                               top3[0] < s.Features.Length ? s.Features[top3[0]] : 0.0f,
                               top3[1] < s.Features.Length ? s.Features[top3[1]] : 0.0f,
                               top3[2] < s.Features.Length ? s.Features[top3[2]] : 0.0f];

                double metaLogit = mb;
                for (int j = 0; j < MetaInputDim; j++) metaLogit += mw[j] * x[j];
                double p   = Sigmoid(metaLogit);
                // Label: 1 if correct prediction, 0 otherwise
                int yHat = avgP >= 0.5 ? 1 : 0;
                int yTrue = s.Direction > 0 ? 1 : 0;
                double y = yHat == yTrue ? 1.0 : 0.0;
                double err = p - y;
                db += err;
                for (int j = 0; j < MetaInputDim; j++) dw[j] += err * x[j];
            }
            double inv = 1.0 / calSet.Count;
            mb -= lr * db * inv;
            for (int j = 0; j < MetaInputDim; j++) mw[j] -= lr * dw[j] * inv;
        }

        return (mw, mb);
    }

    // ── H12: Abstention gate ──────────────────────────────────────────────────

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (calSet.Count < MinCalSamples || metaLabelWeights.Length == 0) return ([], 0.0, 0.5);

        // Input features for abstention gate: [calibP, ensStd, metaLabelScore]
        var aw = new double[3];
        double ab = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DefaultCalibrationEpochs; ep++)
        {
            double[] dw = new double[3];
            double   db = 0;

            foreach (var s in calSet)
            {
                var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
                double rawLogit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
                double calibP   = Sigmoid(plattA * rawLogit + plattB);

                // Meta-label score
                double metaLogit = metaLabelBias;
                double[] mx = [rawLogit, stdP, s.Features.Length > 0 ? s.Features[0] : 0,
                                s.Features.Length > 1 ? s.Features[1] : 0,
                                s.Features.Length > 2 ? s.Features[2] : 0];
                for (int j = 0; j < Math.Min(metaLabelWeights.Length, mx.Length); j++)
                    metaLogit += metaLabelWeights[j] * mx[j];
                double metaScore = Sigmoid(metaLogit);

                double[] x = [calibP, stdP, metaScore];
                double absLogit = ab;
                for (int j = 0; j < 3; j++) absLogit += aw[j] * x[j];
                double p = Sigmoid(absLogit);
                // Label: 1 if this signal would be correctly traded (using calibP vs actual)
                double y = (calibP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0) ? 1.0 : 0.0;
                double err = p - y;
                db += err;
                for (int j = 0; j < 3; j++) dw[j] += err * x[j];
            }
            double inv = 1.0 / calSet.Count;
            ab -= lr * db * inv;
            for (int j = 0; j < 3; j++) aw[j] -= lr * dw[j] * inv;
        }

        // Threshold = median score on cal set
        var scores = calSet.Select(s =>
        {
            var (avgP, stdP) = EnsembleProbAndStd(s.Features, weights, biases, F, featureSubsets, mlpHW, mlpHB, hidDim);
            double rawLogit = Math.Log(Math.Max(avgP, 1e-9) / Math.Max(1 - avgP, 1e-9));
            double calibP   = Sigmoid(plattA * rawLogit + plattB);
            double metaLogit = metaLabelBias;
            double[] mx = [rawLogit, stdP, s.Features.Length > 0 ? s.Features[0] : 0,
                           s.Features.Length > 1 ? s.Features[1] : 0,
                           s.Features.Length > 2 ? s.Features[2] : 0];
            for (int j = 0; j < Math.Min(metaLabelWeights.Length, mx.Length); j++)
                metaLogit += metaLabelWeights[j] * mx[j];
            double[] x = [calibP, stdP, Sigmoid(metaLogit)];
            double absLogit = ab;
            for (int j = 0; j < 3; j++) absLogit += aw[j] * x[j];
            return Sigmoid(absLogit);
        }).OrderBy(v => v).ToArray();

        double threshold = scores.Length > 0 ? scores[scores.Length / 2] : 0.5;
        return (aw, ab, threshold);
    }

    // ── H9: Conformal qHat ────────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        double               alpha  = 0.10)
    {
        if (calSet.Count < MinEvalSamples) return 0.5;

        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s       = calSet[i];
            double rawP = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            // Apply isotonic if available
            if (isotonicBp.Length >= 4) calibP = ApplyIsotonicCalibration(isotonicBp, calibP);

            int yTrue = s.Direction > 0 ? 1 : 0;
            // Non-conformity score = 1 - P(true class)
            scores[i] = yTrue == 1 ? 1.0 - calibP : calibP;
        }

        Array.Sort(scores);
        int idx = (int)Math.Ceiling((1.0 - alpha) * (scores.Length + 1)) - 1;
        idx = Math.Clamp(idx, 0, scores.Length - 1);
        return scores[idx];
    }

    // ── H10: Jackknife+ residuals ─────────────────────────────────────────────

    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K   = weights.Length;
        var res = new List<double>(trainSet.Count);

        for (int i = 0; i < trainSet.Count; i++)
        {
            var s = trainSet[i];
            double yTrue = s.Direction > 0 ? 1.0 : 0.0;
            double rawP  = EnsembleProb(s.Features, weights, biases, F, featureSubsets,
                MetaLearner.None, mlpHW, mlpHB, hidDim);
            res.Add(Math.Abs(yTrue - rawP));
        }

        res.Sort();

        // Cap serialised residuals to avoid outsized snapshot payloads.
        // Stride-sample the sorted list so the empirical quantile distribution
        // is preserved — quantile lookups in MLSignalScorer remain accurate.
        // MaxJackknifeResiduals is defined as a class-level constant
        if (res.Count > MaxJackknifeResiduals)
        {
            int stride  = res.Count / MaxJackknifeResiduals;
            var sampled = new List<double>(MaxJackknifeResiduals);
            for (int i = 0; i < res.Count; i += stride)
                sampled.Add(res[i]);
            return [.. sampled];
        }

        return [.. res];
    }

    // ── EV-optimal threshold ──────────────────────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        double               lo     = 0.30,
        double               hi     = 0.70)
    {
        double best   = 0.50;
        double bestEV = double.MinValue;

        for (double t = lo; t <= hi + 1e-9; t += ThresholdSearchStep)
        {
            int correct = 0;
            foreach (var s in calSet)
            {
                double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
                double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
                double calibP = Sigmoid(plattA * logit + plattB);
                if ((calibP >= t ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
            }
            double ev = calSet.Count > 0 ? (double)correct / calSet.Count : 0.0;
            if (ev > bestEV) { bestEV = ev; best = t; }
        }

        return best;
    }

    // ── Final evaluation ──────────────────────────────────────────────────────

    private static EvalMetrics EvaluateEnsemble(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double               oobAccuracy,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, oobAccuracy);

        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSqSum = 0;
        double sumR = 0, sumR2 = 0;

        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    yHat   = calibP >= 0.5 ? 1 : 0;
            int    yVal   = s.Direction > 0 ? 1 : 0;

            if (yHat == yVal) correct++;
            if (yHat == 1 && yVal == 1) tp++;
            if (yHat == 1 && yVal == 0) fp++;
            if (yHat == 0 && yVal == 1) fn++;
            if (yHat == 0 && yVal == 0) tn++;

            brierSum += (calibP - yVal) * (calibP - yVal);

            // Per-sample return: +1 correct, -1 incorrect
            double ret = yHat == yVal ? 1.0 : -1.0;
            sumR  += ret;
            sumR2 += ret * ret;

            double magPred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, F); j++)
                magPred += magWeights[j] * s.Features[j];
            magSqSum += (magPred - s.Magnitude) * (magPred - s.Magnitude);
        }

        int    evalN     = evalSet.Count;
        double accuracy  = (double)correct / evalN;
        double brier     = brierSum / evalN;
        double magRmse   = Math.Sqrt(magSqSum / evalN);
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        // Sharpe = mean(returns) / std(returns) where return is +1/-1 per prediction
        double meanR = sumR / evalN;
        double varR  = evalN > 1 ? (sumR2 / evalN - meanR * meanR) : 0;
        double sharpe = varR > 0 ? meanR / Math.Sqrt(varR) : 0;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: accuracy, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn, OobAccuracy: oobAccuracy);
    }

    // ── OOB accuracy ──────────────────────────────────────────────────────────

    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        bool[][]             oobMasks,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K = weights.Length;
        int correct = 0, total = 0;

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Average predictions only from learners where sample i is out-of-bag
            double sumP = 0;
            int oobCount = 0;
            for (int k = 0; k < K; k++)
            {
                if (i < oobMasks[k].Length && oobMasks[k][i])
                {
                    sumP += SingleLearnerProb(trainSet[i].Features, weights[k], biases[k],
                        featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
                    oobCount++;
                }
            }
            if (oobCount == 0) continue;
            double avgP = sumP / oobCount;
            if ((avgP >= 0.5 ? 1 : 0) == (trainSet[i].Direction > 0 ? 1 : 0)) correct++;
            total++;
        }

        return total > 0 ? (double)correct / total : 0.0;
    }

    // ── M6: OOB-contribution pruning ─────────────────────────────────────────

    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0,
        int                  K      = 0)
    {
        if (K <= 0) K = weights.Length;

        // Baseline accuracy
        double baselineAcc = 0;
        int baseCorrect = 0;
        foreach (var s in trainSet)
        {
            double p = EnsembleProb(s.Features, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        baselineAcc = (double)baseCorrect / Math.Max(1, trainSet.Count);

        int pruned = 0;
        for (int k = 0; k < K; k++)
        {
            if (weights[k].All(w => w == 0.0) && biases[k] == 0.0) continue;

            // Temporarily zero learner k
            var savedW = weights[k];
            var savedB = biases[k];
            weights[k] = new double[savedW.Length];
            biases[k]  = 0.0;

            int correct = 0;
            foreach (var s in trainSet)
            {
                double p = EnsembleProb(s.Features, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
                if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
            }
            double accWithout = (double)correct / Math.Max(1, trainSet.Count);

            if (accWithout >= baselineAcc)
            {
                // Removing learner k improved or maintained accuracy — keep zeroed
                pruned++;
                baselineAcc = accWithout;
            }
            else
            {
                // Restore
                weights[k] = savedW;
                biases[k]  = savedB;
            }
        }

        return pruned;
    }

    // ── H6: ECE ───────────────────────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0) return 0.0;

        var binAcc  = new double[EceBinCount];
        var binConf = new double[EceBinCount];
        var binCnt  = new int[EceBinCount];

        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    b      = Math.Min((int)(calibP * EceBinCount), EceBinCount - 1);
            binCnt[b]++;
            binConf[b] += calibP;
            binAcc[b]  += (s.Direction > 0 ? 1 : 0);
        }

        double ece = 0.0;
        for (int b = 0; b < EceBinCount; b++)
        {
            if (binCnt[b] == 0) continue;
            double conf = binConf[b] / binCnt[b];
            double acc  = binAcc[b]  / binCnt[b];
            ece += Math.Abs(acc - conf) * binCnt[b];
        }
        return ece / evalSet.Count;
    }

    // ── H7: Brier Skill Score ─────────────────────────────────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> evalSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (evalSet.Count == 0) return 0.0;

        double brierSum = 0;
        double posCount = 0;
        foreach (var s in evalSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            int    yVal   = s.Direction > 0 ? 1 : 0;
            brierSum += (calibP - yVal) * (calibP - yVal);
            posCount += yVal;
        }

        double brier      = brierSum / evalSet.Count;
        double pBase      = posCount / evalSet.Count;
        double brierNaive = pBase * (1 - pBase);
        return brierNaive > 0 ? 1.0 - brier / brierNaive : 0.0;
    }

    // ── M9: Average Kelly fraction ────────────────────────────────────────────

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            sum += Math.Max(0, 2 * calibP - 1) * 0.5; // half-Kelly
        }
        return sum / calSet.Count;
    }

    // ── M10: Ensemble diversity ───────────────────────────────────────────────

    private static double ComputeEnsembleDiversity(double[][] weights, int F)
    {
        int K = weights.Length;
        if (K < 2) return 0.0;

        double sumRho = 0;
        int    pairs  = 0;

        for (int i = 0; i < K - 1; i++)
        for (int j = i + 1; j < K; j++)
        {
            sumRho += Math.Abs(PearsonCorrelation(weights[i], weights[j], F));
            pairs++;
        }

        return pairs > 0 ? sumRho / pairs : 0.0;
    }

    // ── M18: Decision boundary stats ─────────────────────────────────────────

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        // Numerical gradient norm via finite differences — correct for both linear and MLP
        if (calSet.Count == 0 || weights.Length == 0) return (0.0, 0.0);

        const double eps = 1e-4;
        var gradNorms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var x = calSet[i].Features;
            double p0 = EnsembleProb(x, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
            double gradSq = 0;
            for (int j = 0; j < Math.Min(F, x.Length); j++)
            {
                float orig = x[j];
                x[j] = (float)(orig + eps);
                double pPlus = EnsembleProb(x, weights, biases, F, featureSubsets, MetaLearner.None, mlpHW, mlpHB, hidDim);
                x[j] = orig;
                double dP = (pPlus - p0) / eps;
                gradSq += dP * dP;
            }
            gradNorms[i] = Math.Sqrt(gradSq);
        }

        double mean = gradNorms.Average();
        double std  = StdDev(gradNorms, mean);
        return (mean, std);
    }

    // ── M12: Durbin-Watson ────────────────────────────────────────────────────

    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  F)
    {
        if (trainSet.Count < 3) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, F); j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumDiff = 0, sumSq = 0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double d = residuals[i] - residuals[i - 1];
            sumDiff += d * d;
        }
        for (int i = 0; i < residuals.Length; i++) sumSq += residuals[i] * residuals[i];

        return sumSq > 0 ? sumDiff / sumSq : 2.0;
    }

    // ── M16: MI redundancy check ──────────────────────────────────────────────

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  F,
        double               threshold)
    {
        if (trainSet.Count < MinCalSamples || F < 2) return [];

        // Approximate MI via binned joint entropy: MI(i,j) = H(i) + H(j) - H(i,j)
        // Precompute per-feature min/max/marginal entropy to avoid redundant scans
        var featureNames = MLFeatureHelper.FeatureNames;
        double maxMI = Math.Log(2);
        double n = trainSet.Count;

        // Precompute per-feature min, max, and binned marginal counts
        var fMin = new double[F];
        var fMax = new double[F];
        Array.Fill(fMin, double.MaxValue);
        Array.Fill(fMax, double.MinValue);
        foreach (var s in trainSet)
            for (int fi = 0; fi < Math.Min(F, s.Features.Length); fi++)
            {
                if (s.Features[fi] < fMin[fi]) fMin[fi] = s.Features[fi];
                if (s.Features[fi] > fMax[fi]) fMax[fi] = s.Features[fi];
            }

        // Build total pair count for parallel array sizing
        int totalPairs = F * (F - 1) / 2;
        var pairResults = new string?[totalPairs];

        Parallel.For(0, F - 1, i =>
        {
            double iRange = Math.Max(fMax[i] - fMin[i], 1e-9);

            for (int j = i + 1; j < F; j++)
            {
                double jRange = Math.Max(fMax[j] - fMin[j], 1e-9);

                var jointCounts = new double[MIBinCount, MIBinCount];
                double[] piCounts = new double[MIBinCount];
                double[] pjCounts = new double[MIBinCount];

                foreach (var s in trainSet)
                {
                    int bi = Math.Min((int)((s.Features[i] - fMin[i]) / iRange * MIBinCount), MIBinCount - 1);
                    int bj = Math.Min((int)((s.Features[j] - fMin[j]) / jRange * MIBinCount), MIBinCount - 1);
                    jointCounts[bi, bj]++;
                    piCounts[bi]++;
                    pjCounts[bj]++;
                }

                double Hi = 0, Hj = 0, Hij = 0;
                for (int bi = 0; bi < MIBinCount; bi++)
                {
                    if (piCounts[bi] > 0) { double p = piCounts[bi] / n; Hi -= p * Math.Log(p); }
                    if (pjCounts[bi] > 0) { double p = pjCounts[bi] / n; Hj -= p * Math.Log(p); }
                    for (int bj = 0; bj < MIBinCount; bj++)
                        if (jointCounts[bi, bj] > 0) { double p = jointCounts[bi, bj] / n; Hij -= p * Math.Log(p); }
                }

                double mi = Hi + Hj - Hij;
                if (mi > threshold * maxMI)
                {
                    string iName = i < featureNames.Length ? featureNames[i] : $"f{i}";
                    string jName = j < featureNames.Length ? featureNames[j] : $"f{j}";
                    // Compute flat pair index for lock-free storage
                    int pairIdx = i * (2 * F - i - 1) / 2 + (j - i - 1);
                    pairResults[pairIdx] = $"{iName}:{jName}";
                }
            }
        });

        return [.. pairResults.Where(r => r is not null).Cast<string>()];
    }

    // ── Permutation feature importance (test set) ─────────────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW,
        double[][]?          mlpHB,
        int                  hidDim,
        Random               _,
        CancellationToken    ct)
    {
        double baseline = EvalAccuracy(testSet, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var imp = new float[F];

        for (int j = 0; j < F; j++)
        {
            if (ct.IsCancellationRequested) break;

            // Per-feature deterministic seed — avoids ordering dependency between features
            var rng = new Random(77 * (j + 1));
            var origVals = testSet.Select(s => s.Features[j]).ToArray();
            for (int i = origVals.Length - 1; i > 0; i--)
            {
                int swap = rng.Next(i + 1);
                (origVals[i], origVals[swap]) = (origVals[swap], origVals[i]);
            }

            var permuted = testSet.Select((s, idx) =>
            {
                var f2 = (float[])s.Features.Clone(); f2[j] = origVals[idx];
                return s with { Features = f2 };
            }).ToList();

            double permAcc = EvalAccuracy(permuted, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            imp[j] = (float)Math.Max(0, baseline - permAcc);
        }

        float sum = imp.Sum();
        if (sum > 0) for (int j = 0; j < F; j++) imp[j] /= sum;
        return imp;
    }

    // ── M7: Cal-set permutation importance ────────────────────────────────────

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW,
        double[][]?          mlpHB,
        int                  hidDim,
        CancellationToken    ct)
    {
        double baseline = EvalAccuracy(calSet, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
        var imp = new double[F];

        for (int j = 0; j < F; j++)
        {
            if (ct.IsCancellationRequested) break;

            var rng = new Random(13 * (j + 1));
            var origVals = calSet.Select(s => s.Features[j]).ToArray();
            for (int i = origVals.Length - 1; i > 0; i--)
            {
                int swap = rng.Next(i + 1);
                (origVals[i], origVals[swap]) = (origVals[swap], origVals[i]);
            }

            var permuted = calSet.Select((s, idx) =>
            {
                var f2 = (float[])s.Features.Clone(); f2[j] = origVals[idx];
                return s with { Features = f2 };
            }).ToList();

            double permAcc = EvalAccuracy(permuted, weights, biases, plattA, plattB, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            imp[j] = Math.Max(0, baseline - permAcc);
        }

        return imp;
    }

    // ── M4: Density-ratio importance weights ──────────────────────────────────

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  F,
        int                  windowDays,
        int                  barsPerDay)
    {
        int recentCount = Math.Min(trainSet.Count, windowDays * barsPerDay);
        int n           = trainSet.Count;
        var weights     = new double[n]; Array.Fill(weights, 1.0);

        if (recentCount < 20 || n - recentCount < 20) return weights;

        // Train logistic discriminator: recent (y=1) vs historical (y=0)
        var dw = new double[F];
        double db = 0;
        const double lr = 0.01;

        for (int ep = 0; ep < DensityRatioEpochs; ep++)
        {
            double[] gdw = new double[F]; double gdb = 0;
            for (int i = 0; i < n; i++)
            {
                double y    = i >= (n - recentCount) ? 1.0 : 0.0;
                double logit = db;
                for (int j = 0; j < F; j++) logit += dw[j] * trainSet[i].Features[j];
                double p   = Sigmoid(logit);
                double err = p - y;
                gdb += err;
                for (int j = 0; j < F; j++) gdw[j] += err * trainSet[i].Features[j];
            }
            double inv = 1.0 / n;
            db -= lr * gdb * inv;
            for (int j = 0; j < F; j++) dw[j] -= lr * gdw[j] * inv;
        }

        for (int i = 0; i < n; i++)
        {
            double logit = db;
            for (int j = 0; j < F; j++) logit += dw[j] * trainSet[i].Features[j];
            double p = Sigmoid(logit);
            weights[i] = Math.Max(0.1, Math.Min(10.0, p / Math.Max(1 - p, 1e-9)));
        }

        return weights;
    }

    // ── M5: Covariate shift weights ───────────────────────────────────────────

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet,
        double[][]           parentBp,
        int                  F)
    {
        var weights = new double[trainSet.Count];
        int n = trainSet.Count;

        for (int i = 0; i < n; i++)
        {
            var s = trainSet[i];
            int outOfRange   = 0;
            int checkedCount = 0;

            for (int j = 0; j < Math.Min(F, parentBp.Length); j++)
            {
                var bp = parentBp[j];
                if (bp.Length < 2) continue;

                double val = j < s.Features.Length ? s.Features[j] : 0;
                double q10 = bp[0];
                double q90 = bp[^1];
                if (val < q10 || val > q90) outOfRange++;
                checkedCount++;
            }

            // Higher weight for novel samples outside parent's inter-decile range
            double noveltyFrac = checkedCount > 0 ? (double)outOfRange / checkedCount : 0.0;
            weights[i] = 1.0 + noveltyFrac; // [1.0, 2.0]
        }

        return weights;
    }

    // ── H3: Equity-curve gate ─────────────────────────────────────────────────

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Pred, int Actual)[] predictions)
    {
        double equity    = 0;
        double peak      = 0;
        double maxDD     = 0;
        double sumPnl    = 0, sumSqPnl = 0;
        int    n         = predictions.Length;

        for (int i = 0; i < n; i++)
        {
            double pnl = predictions[i].Pred == predictions[i].Actual ? 1.0 : -1.0;
            equity   += pnl;
            peak      = Math.Max(peak, equity);
            maxDD     = Math.Max(maxDD, peak - equity);
            sumPnl   += pnl;
            sumSqPnl += pnl * pnl;
        }

        double meanPnl = n > 0 ? sumPnl / n : 0;
        double varPnl  = n > 1 ? (sumSqPnl / n - meanPnl * meanPnl) : 0;
        double sharpe  = varPnl > 0 ? meanPnl / Math.Sqrt(varPnl) : 0;
        double ddFrac  = peak > 0 ? maxDD / peak : 0;

        return (ddFrac, sharpe);
    }

    // ── GES (greedy ensemble selection) ──────────────────────────────────────

    private static double[] RunGreedyEnsembleSelection(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  F,
        int[][]?             featureSubsets,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        int K = weights.Length;
        int N = calSet.Count;
        var useCounts = new int[K];

        // Cache per-learner predictions to avoid redundant computation
        var cache = new double[K][];
        var labels = new int[N];
        for (int k = 0; k < K; k++)
        {
            cache[k] = new double[N];
            for (int i = 0; i < N; i++)
                cache[k][i] = SingleLearnerProb(calSet[i].Features, weights[k], biases[k],
                    featureSubsets?[k], F, mlpHW?[k], mlpHB?[k], hidDim);
        }
        for (int i = 0; i < N; i++)
            labels[i] = calSet[i].Direction > 0 ? 1 : 0;

        var selected = new List<int>();
        var runningSum = new double[N];

        for (int r = 0; r < GesRounds; r++)
        {
            double bestAcc = -1;
            int    bestK   = 0;
            int    count   = selected.Count + 1;

            for (int k = 0; k < K; k++)
            {
                int correct = 0;
                for (int i = 0; i < N; i++)
                {
                    double avgP = (runningSum[i] + cache[k][i]) / count;
                    if ((avgP >= 0.5 ? 1 : 0) == labels[i]) correct++;
                }
                double acc = (double)correct / N;
                if (acc > bestAcc) { bestAcc = acc; bestK = k; }
            }

            selected.Add(bestK);
            useCounts[bestK]++;
            for (int i = 0; i < N; i++)
                runningSum[i] += cache[bestK][i];
        }

        double total = useCounts.Sum();
        if (total <= 0) return [];
        return useCounts.Select(c => c / total).ToArray();
    }

    // ── Helpers: ensemble probability computation ─────────────────────────────

    private static double EnsembleProb(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         F,
        int[][]?    featureSubsets,
        MetaLearner meta   = default,
        double[][]? mlpHW  = null,
        double[][]? mlpHB  = null,
        int         hidDim = 0)
    {
        int K = weights.Length;

        if (meta.IsActive)
        {
            double metaLogit = meta.Bias;
            for (int k = 0; k < Math.Min(K, meta.Weights.Length); k++)
            {
                double p = SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                    mlpHW?[k], mlpHB?[k], hidDim);
                metaLogit += meta.Weights[k] * p;
            }
            return Sigmoid(metaLogit);
        }

        double sumP = 0;
        for (int k = 0; k < K; k++)
            sumP += SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                mlpHW?[k], mlpHB?[k], hidDim);
        return sumP / K;
    }

    private static (double AvgP, double StdP) EnsembleProbAndStd(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         F,
        int[][]?    featureSubsets,
        double[][]? mlpHW  = null,
        double[][]? mlpHB  = null,
        int         hidDim = 0)
    {
        int K = weights.Length;
        if (K == 0) return (0.5, 0.0);

        var probs = new double[K];
        for (int k = 0; k < K; k++)
            probs[k] = SingleLearnerProb(x, weights[k], biases[k], featureSubsets?[k], F,
                mlpHW?[k], mlpHB?[k], hidDim);

        double mean = probs.Average();
        double var2 = probs.Sum(p => (p - mean) * (p - mean)) / K;
        return (mean, Math.Sqrt(var2));
    }

    private static double SingleLearnerProb(
        float[]  x,
        double[] w,
        double   b,
        int[]?   subset,
        int      F,
        double[]? hW    = null,
        double[]? hB    = null,
        int       hidDim = 0)
    {
        if (hidDim > 0 && hW is not null && hB is not null && w.Length == hidDim)
        {
            int fk = subset?.Length ?? F;
            // Detect packed 2-layer model: L1 weights (hidDim×fk) + L2 weights (hidDim×hidDim)
            bool isDeep = hW.Length > hidDim * fk;

            // Layer 1: input → hidDim
            var h1 = new double[hidDim];
            for (int hj = 0; hj < hidDim; hj++)
            {
                double act = hB[hj];
                for (int ji = 0; ji < fk; ji++)
                {
                    int fi = subset is not null ? subset[ji] : ji;
                    if (hj * fk + ji < hW.Length && fi < x.Length)
                        act += hW[hj * fk + ji] * x[fi];
                }
                h1[hj] = Math.Max(0, act); // ReLU
            }

            double[] hFinal = h1;
            if (isDeep)
            {
                // Layer 2: hidDim → hidDim (weights packed after layer 1)
                int l2WOff = hidDim * fk;
                int l2BOff = hidDim;
                var h2 = new double[hidDim];
                for (int hj = 0; hj < hidDim; hj++)
                {
                    double act = l2BOff + hj < hB.Length ? hB[l2BOff + hj] : 0.0;
                    for (int ji = 0; ji < hidDim; ji++)
                    {
                        int wIdx = l2WOff + hj * hidDim + ji;
                        if (wIdx < hW.Length) act += hW[wIdx] * h1[ji];
                    }
                    h2[hj] = Math.Max(0, act); // ReLU
                }
                hFinal = h2;
            }

            double logit = b;
            for (int hj = 0; hj < Math.Min(hidDim, w.Length); hj++) logit += w[hj] * hFinal[hj];
            return Sigmoid(logit);
        }
        else
        {
            double z = b;
            if (subset is { Length: > 0 })
            {
                foreach (int j in subset)
                    if (j < F && j < w.Length && j < x.Length) z += w[j] * x[j];
            }
            else
                for (int j = 0; j < Math.Min(w.Length, F); j++) z += w[j] * x[j];
            return Sigmoid(z);
        }
    }

    private static double EvalAccuracy(
        List<TrainingSample> samples,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  F,
        int[][]?             featureSubsets,
        MetaLearner          meta,
        double[][]?          mlpHW  = null,
        double[][]?          mlpHB  = null,
        int                  hidDim = 0)
    {
        if (samples.Count == 0) return 0.0;
        int correct = 0;
        foreach (var s in samples)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, F, featureSubsets, meta, mlpHW, mlpHB, hidDim);
            double logit  = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            double calibP = Sigmoid(plattA * logit + plattB);
            if ((calibP >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
        }
        return (double)correct / samples.Count;
    }



    // ── OOB cross-entropy loss (for per-learner early stopping) ───────────────

    private static double ComputeOobLoss(
        List<TrainingSample> trainSet,
        bool[]               oobMask,
        double[]             w,
        double               b,
        int[]                subset,
        int                  fk,
        double               labelSmoothing,
        double[]?            hW    = null,
        double[]?            hB    = null,
        int                  hidDim = 0)
    {
        double lossSum = 0; int cnt = 0;
        for (int i = 0; i < Math.Min(trainSet.Count, oobMask.Length); i++)
        {
            if (!oobMask[i]) continue;
            float[] x    = trainSet[i].Features;
            double  yRaw = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            double  y    = labelSmoothing > 0 ? yRaw * (1 - labelSmoothing) + 0.5 * labelSmoothing : yRaw;
            double  p    = SingleLearnerProb(x, w, b, subset, fk, hW, hB, hidDim);
            lossSum += -(y * Math.Log(Math.Max(p, 1e-9)) + (1 - y) * Math.Log(Math.Max(1 - p, 1e-9)));
            cnt++;
        }
        return cnt > 0 ? lossSum / cnt : 0.0;
    }

    // ── M1: Validation accuracy for adaptive LR decay ─────────────────────────

    private static double ComputeValAccuracy(
        List<TrainingSample> valSet,
        double[]             w,
        double               b,
        int[]                subset,
        int                  fk,
        double[]?            hW    = null,
        double[]?            hB    = null,
        int                  hidDim = 0)
    {
        if (valSet.Count == 0) return 0.0;
        int correct = 0;
        foreach (var s in valSet)
        {
            double p = SingleLearnerProb(s.Features, w, b, subset, fk, hW, hB, hidDim);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) correct++;
        }
        return (double)correct / valSet.Count;
    }

    // ── NCL: avg prediction from prior learners (sequential mode) ────────────

    private static double ComputeAvgPriorLearners(
        float[]     x,
        double[][]  weights,
        double[]    biases,
        int         k,
        int         F,
        int[][]?    featureSubsets,
        double[][]? mlpHW,
        double[][]? mlpHB,
        int         hidDim)
    {
        if (k == 0) return 0.5;
        double sum = 0;
        for (int ki = 0; ki < k; ki++)
        {
            if (weights[ki] is null) continue;
            sum += SingleLearnerProb(x, weights[ki], biases[ki], featureSubsets?[ki], F,
                mlpHW?[ki], mlpHB?[ki], hidDim);
        }
        return sum / k;
    }

    // ── Stratified temporally-weighted bootstrap ──────────────────────────────

    private static int[] StratifiedBootstrap(
        int[]    posIdx,
        int[]    negIdx,
        int      n,
        double[] temporalWeights,
        Random   rng)
    {
        int halfN     = n / 2;
        var bootstrap = new int[n];

        for (int i = 0; i < halfN; i++)
            bootstrap[i] = posIdx.Length > 0
                ? WeightedSample(posIdx, temporalWeights, rng)
                : rng.Next(n);

        for (int i = halfN; i < n; i++)
            bootstrap[i] = negIdx.Length > 0
                ? WeightedSample(negIdx, temporalWeights, rng)
                : rng.Next(n);

        return bootstrap;
    }

    private static int WeightedSample(int[] indices, double[] weights, Random rng)
    {
        double sum = 0;
        foreach (int i in indices) sum += weights[i];
        if (sum <= 0) return indices[rng.Next(indices.Length)];

        double target = rng.NextDouble() * sum, cum = 0;
        foreach (int i in indices)
        {
            cum += weights[i];
            if (cum >= target) return i;
        }
        return indices[^1];
    }

    // ── Warm-start weight initialisation ──────────────────────────────────────

    private static double[] InitWeights(
        ModelSnapshot? warmStart,
        int            k,
        int            fk,
        int[]          subset,
        int            F,
        Random         rng)
    {
        var w = new double[fk];
        if (warmStart?.Weights is { Length: > 0 } ws && k < ws.Length && ws[k].Length == F)
        {
            for (int ji = 0; ji < fk; ji++) w[ji] = ws[k][subset[ji]];
        }
        else
        {
            double scale = Math.Sqrt(6.0 / (fk + 1));
            for (int ji = 0; ji < fk; ji++) w[ji] = (rng.NextDouble() * 2.0 - 1.0) * scale;
        }
        return w;
    }

    // ── L8: Polynomial feature augmentation ──────────────────────────────────

    private static float[] BuildPolyAugmentedFeatures(float[] x, int[] top5, int F)
    {
        int    lenIn  = Math.Min(x.Length, F);
        var    aug    = new float[F + PolyPairCount];
        Array.Copy(x, aug, lenIn);
        int pairIdx = F;
        for (int i = 0; i < top5.Length - 1; i++)
        for (int j = i + 1; j < top5.Length; j++)
        {
            if (pairIdx < aug.Length)
            {
                float vi = top5[i] < x.Length ? x[top5[i]] : 0f;
                float vj = top5[j] < x.Length ? x[top5[j]] : 0f;
                aug[pairIdx++] = vi * vj;
            }
        }
        return aug;
    }

    private static int[] GetTop5FeatureIndices(ModelSnapshot? warmStart, int F)
    {
        if (warmStart?.FeatureImportance is { Length: > 0 } imp && imp.Length == F)
        {
            return [.. Enumerable.Range(0, F)
                .OrderByDescending(i => imp[i])
                .Take(PolyTopN)
                .OrderBy(i => i)];
        }
        return [0, 1, 2, 3, 4];
    }

    // ── Feature mask helpers ──────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double minImportance, int F)
    {
        var mask = new bool[F]; Array.Fill(mask, true);
        if (minImportance <= 0.0) return mask;

        double equalShare = 1.0 / Math.Max(1, F);
        for (int j = 0; j < F; j++)
            if (j < importance.Length && importance[j] < minImportance * equalShare)
                mask[j] = false;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        // Reduce dimensionality: only keep features where mask[j] is true
        int[] kept = Enumerable.Range(0, mask.Length).Where(j => mask[j]).ToArray();
        int newF = kept.Length;
        return samples.Select(s =>
        {
            var mf = new float[newF];
            for (int i = 0; i < newF; i++)
                mf[i] = kept[i] < s.Features.Length ? s.Features[kept[i]] : 0f;
            return s with { Features = mf };
        }).ToList();
    }

    // ── M8: Biased and random feature subset generation ───────────────────────

    private static int[] GenerateFeatureSubset(int F, double ratio, int seed)
    {
        int subF = Math.Max(3, (int)(F * ratio));
        var rng  = new Random(seed);
        return [.. Enumerable.Range(0, F).OrderBy(_ => rng.NextDouble()).Take(subF).OrderBy(x => x)];
    }

    private static int[] GenerateBiasedFeatureSubset(
        int      F,
        double   ratio,
        double[] importanceScores,
        int      seed)
    {
        int subF = Math.Max(3, (int)(F * ratio));
        var rng  = new Random(seed);

        // Softmax-weighted sampling from feature importance distribution
        double[] weights = new double[F];
        double   sum     = 0;
        for (int j = 0; j < F; j++)
        {
            double imp = j < importanceScores.Length ? Math.Max(0, importanceScores[j]) : 0;
            weights[j] = Math.Exp(imp * BiasedSamplingTemp);
            sum += weights[j];
        }
        for (int j = 0; j < F; j++) weights[j] /= sum;

        var selected = new HashSet<int>();
        while (selected.Count < subF)
        {
            double target = rng.NextDouble(), cum = 0;
            for (int j = 0; j < F; j++)
            {
                cum += weights[j];
                if (cum >= target) { selected.Add(j); break; }
            }
            if (selected.Count == 0) selected.Add(rng.Next(F));
        }
        return [.. selected.OrderBy(x => x)];
    }

    // ── Isotonic calibration application ─────────────────────────────────────

    private static double ApplyIsotonicCalibration(double[] bps, double prob)
    {
        if (bps.Length < 4) return prob;
        int n = bps.Length / 2;

        if (prob <= bps[0]) return bps[1];
        if (prob >= bps[(n - 1) * 2]) return bps[(n - 1) * 2 + 1];

        for (int i = 0; i < n - 1; i++)
        {
            double x0 = bps[i * 2], y0 = bps[i * 2 + 1];
            double x1 = bps[(i + 1) * 2], y1 = bps[(i + 1) * 2 + 1];
            if (prob >= x0 && prob <= x1 && x1 > x0)
                return y0 + (y1 - y0) * (prob - x0) / (x1 - x0);
        }
        return prob;
    }

    // ── Pearson correlation between learner weight vectors ────────────────────

    private static double PearsonCorrelation(double[] a, double[] b, int F)
    {
        int len = Math.Min(Math.Min(a.Length, b.Length), F);
        if (len == 0) return 0.0;

        double ma = 0, mb2 = 0;
        for (int j = 0; j < len; j++) { ma += a[j]; mb2 += b[j]; }
        ma /= len; mb2 /= len;

        double num = 0, da2 = 0, db2 = 0;
        for (int j = 0; j < len; j++)
        {
            double dA = a[j] - ma, dB = b[j] - mb2;
            num += dA * dB; da2 += dA * dA; db2 += dB * dB;
        }
        double denom = Math.Sqrt(da2 * db2);
        return denom > 1e-12 ? num / denom : 0.0;
    }

    // ── Temporal decay weights ────────────────────────────────────────────────

    private static double[] ComputeTemporalWeights(int n, double lambda)
    {
        var w = new double[n];
        if (lambda <= 0) { Array.Fill(w, 1.0); return w; }
        for (int i = 0; i < n; i++) w[i] = Math.Exp(-lambda * (n - 1 - i));
        return w;
    }

    // ── Math helpers ──────────────────────────────────────────────────────────

    private static double Sigmoid(double x) =>
        1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50.0, 50.0)));

    private static double EuclideanDistSq(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; sum += d * d; }
        return sum;
    }

    // Box-Muller transform for Gaussian noise
    private static double SampleNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // L7: Beta(α, α) sample via Gamma distribution (Marsaglia-Tsang method)
    private static double SampleBeta(double alpha, Random rng)
    {
        if (alpha <= 0) return 0.5;
        double x = SampleGamma(alpha, rng);
        double y = SampleGamma(alpha, rng);
        double sum = x + y;
        return sum > 0 ? x / sum : 0.5;
    }

    private static double SampleGamma(double shape, Random rng)
    {
        // For shape < 1, use Ahrens-Dieter correction: Gamma(a) = Gamma(a+1) * U^(1/a)
        if (shape < 1.0)
        {
            double u = rng.NextDouble();
            return SampleGamma(shape + 1.0, rng) * Math.Pow(Math.Max(u, 1e-15), 1.0 / shape);
        }
        // Marsaglia-Tsang method for shape >= 1
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = SampleNormal(rng);
                v = 1.0 + c * x;
            } while (v <= 0);
            v = v * v * v;
            double u2 = rng.NextDouble();
            if (u2 < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v;
            if (Math.Log(u2) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0.0;
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double StdDev(double[] values, double mean)
    {
        if (values.Length < 2) return 0.0;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) { double d = values[i] - mean; sum += d * d; }
        return Math.Sqrt(sum / (values.Length - 1));
    }

    private static double ComputeSharpeTrend(List<double> sharpeList)
    {
        int n = sharpeList.Count;
        if (n < 2) return 0.0;
        double xMean = (n - 1) / 2.0, yMean = sharpeList.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = i - xMean;
            num += dx * (sharpeList[i] - yMean);
            den += dx * dx;
        }
        return den > 0 ? num / den : 0.0;
    }
}
