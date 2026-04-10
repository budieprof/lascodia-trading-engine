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
public sealed partial class SmoteModelTrainer : IMLModelTrainer
{
    private const string ModelType    = "SMOTE";
    private const string ModelVersion = "3.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private const int PolyTopN = 5;
    private const int PolyPairCount = PolyTopN * (PolyTopN - 1) / 2; // = 10

    private const double TrainSplitRatio          = 0.60;
    private const double SelectionSplitRatio      = 0.70;
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

    internal readonly record struct EnsembleTrainResult(
        double[][] Weights, double[] Biases, int[][]? FeatureSubsets, int PolyStart,
        bool[][] OobMasks, double[][]? MlpHW, double[][]? MlpHB, int SwaCount);

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

    private static double[] ComputeJackknifeResiduals(List<TrainingSample> s, in EnsembleState es, bool[][] oobMasks)
        => ComputeJackknifeResiduals(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, oobMasks, es.MlpHW, es.MlpHB, es.HidDim);

    private static (double[] Weights, double Bias, int[] TopFeatureIndices) FitMetaLabelModel(List<TrainingSample> s, in EnsembleState es, double[]? importanceScores = null)
        => FitMetaLabelModel(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim, importanceScores);

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> s, in EnsembleState es, double pA, double pB, double[] metaLabelW, double metaLabelB, int[] metaLabelTopFeatures)
        => FitAbstentionModel(s, es.Weights, es.Biases, pA, pB, metaLabelW, metaLabelB, metaLabelTopFeatures, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

    private static double FitTemperatureScaling(List<TrainingSample> s, in EnsembleState es)
        => FitTemperatureScaling(s, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.Meta, es.MlpHW, es.MlpHB, es.HidDim);

    private static double ComputeEnsembleDiversity(List<TrainingSample> calSet, in EnsembleState es)
        => ComputeEnsembleDiversity(calSet, es.Weights, es.Biases, es.F, es.FeatureSubsets, es.MlpHW, es.MlpHB, es.HidDim);

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
            // Floor: after 60/10/10/20 split + three embargo gaps the train fold must still have
            // at least MinSamples rows. Solving 0.60×w - 3×embargo ≥ MinSamples:
            // w ≥ (MinSamples + 3×embargo) / 0.60
            int minWindowForSplit = (int)Math.Ceiling((hp.MinSamples + 3 * hp.EmbargoBarCount) / TrainSplitRatio);
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

        if (warmStart is not null)
        {
            bool mismatch = false;
            if (warmStart.Version != ModelVersion)
            {
                _logger.LogWarning("Warm-start version mismatch: snapshot={SnapVer} current={CurVer} — falling back to cold start.",
                    warmStart.Version, ModelVersion);
                mismatch = true;
            }
            if (warmStart.MlpHiddenDim != hp.MlpHiddenDim)
            {
                _logger.LogWarning("Warm-start MLP dim mismatch: snapshot={SnapDim} hp={HpDim} — falling back to cold start.",
                    warmStart.MlpHiddenDim, hp.MlpHiddenDim);
                mismatch = true;
            }
            if (warmStart.BaseLearnersK > 0 && warmStart.BaseLearnersK != (hp.K > 0 ? hp.K : 50))
            {
                _logger.LogWarning("Warm-start K mismatch: snapshot={SnapK} hp={HpK} — falling back to cold start.",
                    warmStart.BaseLearnersK, hp.K > 0 ? hp.K : 50);
                mismatch = true;
            }
            if (mismatch) warmStart = null;
        }

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

        // ── M14: Multi-signal drift gate + fractional differencing ────────────
        double fracDiffD = hp.FracDiffD;
        var driftArtifact = ComputeSmoteDriftDiagnostics(allStd, F, MLFeatureHelper.FeatureNames, fracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SMOTE drift gate rejected: {driftArtifact.NonStationaryFeatureCount}/{F} features flagged.");
            _logger.LogWarning("Stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, F);

            if (fracDiffD == 0.0)
            {
                fracDiffD = 0.4;
                _logger.LogWarning("Auto-applying FracDiffD={D:F1} due to drift gate.", fracDiffD);
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

        // ── 3. 4-way split: 60% train | 10% selection | 10% cal | ~20% test ──
        int embargo  = hp.EmbargoBarCount;
        int n        = allStd.Count;
        int trainEnd    = (int)(n * TrainSplitRatio);
        int selEnd      = (int)(n * SelectionSplitRatio);
        int calEnd      = (int)(n * CalSplitRatio);

        var trainSet     = allStd[..Math.Max(0, trainEnd - embargo)];
        var selectionSet = allStd[Math.Min(trainEnd + embargo, n)..Math.Min(selEnd, n)];
        var calSet       = allStd[Math.Min(selEnd + embargo, n)..Math.Min(calEnd, n)];
        var testSet      = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"SmoteModelTrainer: insufficient training samples after split: {trainSet.Count} < {hp.MinSamples}");
        if (selectionSet.Count < MinEvalSamples)
            _logger.LogWarning("SmoteModelTrainer: selection set very small ({Count} samples).", selectionSet.Count);

        // ── 3b. Class-imbalance gate ──────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = trainSet.Count > 0 ? (double)posCount / trainSet.Count : 0.5;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"SmoteModelTrainer: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("SmoteModelTrainer class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3c. Adversarial validation ────────────────────────────────────────
        double adversarialAuc = 0.5;
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            adversarialAuc = TryComputeAdversarialAucGpu(trainSet, testSet, F, ct)
                             ?? ComputeAdversarialAuc(trainSet, testSet, F);
            _logger.LogInformation("SmoteModelTrainer adversarial AUC={AUC:F3}", adversarialAuc);
            if (adversarialAuc > 0.65)
                _logger.LogWarning("Adversarial AUC={AUC:F3} indicates covariate shift.", adversarialAuc);
            if (hp.SmoteMaxAdversarialAuc > 0.0 && adversarialAuc > hp.SmoteMaxAdversarialAuc)
                throw new InvalidOperationException($"SmoteModelTrainer: adversarial AUC={adversarialAuc:F3} exceeds threshold.");
        }

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

        // ── M4: Density-ratio importance weights (GPU+CPU) ─────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            int bpd = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            densityWeights = TryComputeDensityRatioWeightsGpu(trainSet, F, hp.DensityRatioWindowDays, bpd, ct)
                             ?? ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays, bpd);
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
        var (balancedTrain, syntheticCount, smoteSeed) = ApplySmote(trainSet, hp, F, ct);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "SMOTE: trainN={Train} → balancedN={Balanced} (+{Synth} synthetic)",
                trainSet.Count, balancedTrain.Count, syntheticCount);

        ct.ThrowIfCancellationRequested();

        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 6. Fit ensemble ───────────────────────────────────────────────────
        var ensResult = FitEnsemble(balancedTrain, effectiveHp, F, K, labelSmoothing, warmStart, densityWeights, ct,
                originalCount: trainSet.Count);
        var weights = ensResult.Weights; var biases = ensResult.Biases;
        var featureSubsets = ensResult.FeatureSubsets; var polyStart = ensResult.PolyStart;
        var oobMasks = ensResult.OobMasks; var ensMlpHW = ensResult.MlpHW;
        var ensMlpHB = ensResult.MlpHB; var swaCount = ensResult.SwaCount;

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

        // ── 12. EV-optimal threshold (tuned on selection set) ──────────────────
        double lo = hp.ThresholdSearchMin > 0 ? hp.ThresholdSearchMin / 100.0 : 0.30;
        double hi = hp.ThresholdSearchMax > 0 ? hp.ThresholdSearchMax / 100.0 : 0.70;
        double optimalThreshold = selectionSet.Count >= MinEvalSamples
            ? ComputeOptimalThreshold(selectionSet, ens, plattA, plattB, lo, hi) : 0.5;
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

        // ── 14. Permutation feature importance (selection set) ──────────────────
        float[] featureImportance = selectionSet.Count >= MinEvalSamples
            ? ComputePermutationImportance(selectionSet, ens, plattA, plattB, new Random(77), ct)
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

        // ── M7: Selection-set permutation importance (for warm-start biased sampling) ─
        double[] calImportanceScores = selectionSet.Count >= MinEvalSamples
            ? ComputeCalPermutationImportance(selectionSet, ens, plattA, plattB, ct)
            : new double[F];

        // ── H13: Feature pruning retrain pass ─────────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Feature pruning: masking {Pruned}/{Total} low-importance features",
                    prunedCount, F);

            var maskedTrain     = ApplyMask(balancedTrain, activeMask);
            var maskedSelection = ApplyMask(selectionSet,  activeMask);
            var maskedCal       = ApplyMask(calSet,        activeMask);
            var maskedTest      = ApplyMask(testSet,       activeMask);
            int reducedF        = activeMask.Count(m => m);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            var pEnsResult = FitEnsemble(
                maskedTrain, prunedHp, reducedF, K, labelSmoothing, null, densityWeights, ct,
                originalCount: trainSet.Count);
            var pw = pEnsResult.Weights; var pb = pEnsResult.Biases;
            var pOobMasks = pEnsResult.OobMasks; var pMlpHW = pEnsResult.MlpHW; var pMlpHB = pEnsResult.MlpHB;
            var pMeta       = maskedSelection.Count >= MinEvalSamples
                ? FitMetaLearner(maskedSelection, pw, pb, reducedF, null, pMlpHW, pMlpHB, prunedHp.MlpHiddenDim)
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
                optimalThreshold = maskedSelection.Count >= MinEvalSamples
                    ? ComputeOptimalThreshold(maskedSelection, pEns, pA, pB, lo, hi)
                    : optimalThreshold;
                gesWeights = hp.EnableGreedyEnsembleSelection && maskedSelection.Count >= MinEvalSamples
                    ? RunGreedyEnsembleSelection(maskedSelection, pEns) : [];
                calImportanceScores = maskedSelection.Count >= MinEvalSamples
                    ? ComputeCalPermutationImportance(maskedSelection, pEns, pA, pB, ct)
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
                // Recompute OOB accuracy for the pruned model
                oobAccuracy = ComputeOobAccuracy(maskedTrain, pEns, pOobMasks);
                oobMasks = pOobMasks;
                // Reassign balancedTrain, selectionSet and F so downstream uses masked features
                balancedTrain = maskedTrain;
                selectionSet  = maskedSelection;
                F = reducedF;
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
        var postPruneCalSet       = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;
        var postPruneSelectionSet = prunedCount > 0 ? ApplyMask(selectionSet, activeMask) : selectionSet;

        // ── H8: Isotonic calibration ──────────────────────────────────────────
        // Rebuild EnsembleState: when pruning was accepted, use reducedF with null featureSubsets
        // (matching pEns) so weight indexing is sequential 0..reducedF-1. The keptIndices featureSubsets
        // are only used at serialization time for the inference engine's feature-space mapping.
        // F is already reducedF when pruning was accepted (reassigned in acceptance block),
        // or the original F when pruning was rejected/skipped.
        ens = prunedCount > 0
            ? new EnsembleState(weights, biases, F, null, meta, ensMlpHW, ensMlpHB, hp.MlpHiddenDim)
            : new EnsembleState(weights, biases, F, featureSubsets, meta, ensMlpHW, ensMlpHB, hp.MlpHiddenDim);
        double[] isotonicBp = FitIsotonicCalibration(postPruneCalSet, ens, plattA, plattB);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── H9: Conformal qHat ────────────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(postPruneCalSet, ens, plattA, plattB, isotonicBp, conformalAlpha);
        _logger.LogDebug("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── H10: Jackknife+ residuals (OOB-approximated) ─────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(balancedTrain, ens, oobMasks);
        _logger.LogDebug("Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── H11: Meta-label secondary classifier ─────────────────────────────
        var (metaLabelWeights, metaLabelBias, metaLabelTopFeatures) = FitMetaLabelModel(
            postPruneCalSet, ens, importanceScores: calImportanceScores);
        _logger.LogDebug("Meta-label model: bias={B:F4}", metaLabelBias);

        // ── H12: Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalSet, ens, plattA, plattB, metaLabelWeights, metaLabelBias, metaLabelTopFeatures);
        _logger.LogDebug("Abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── M11: Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= MinEvalSamples)
        {
            temperatureScale = FitTemperatureScaling(postPruneCalSet, ens);
            _logger.LogDebug("Temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── M10: Ensemble diversity ───────────────────────────────────────────
        double ensembleDiversity = ComputeEnsembleDiversity(postPruneCalSet, ens);
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
                balancedTrain, weights, biases, F, ens.FeatureSubsets,
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

        // ── 16. Calibrated probability function for new metrics ──────────────
        double SmoteCalibratedProb(float[] x)
        {
            double rawP = EnsembleProb(x, ens);
            double logitP = Math.Log(Math.Max(rawP, 1e-9) / Math.Max(1 - rawP, 1e-9));
            return Sigmoid(plattA * logitP + plattB);
        }

        // ── 16a. Murphy decomposition ─��──────────────────────────────────────
        var (murphyCalLoss, murphyRefLoss) = testSet.Count >= MinEvalSamples
            ? ComputeMurphyDecomposition(testSet, SmoteCalibratedProb)
            : (0.0, 0.0);
        _logger.LogDebug("Murphy decomposition: cal={Cal:F4} ref={Ref:F4}", murphyCalLoss, murphyRefLoss);

        // ── 16b. Reliability diagram ─────────────────────────────────────────
        var (relBinConf, relBinAcc, relBinCounts) = testSet.Count >= MinEvalSamples
            ? ComputeReliabilityDiagram(testSet, SmoteCalibratedProb)
            : (new double[10], new double[10], new int[10]);

        // ── 16c. Calibration residual stats ──────────────────────────────────
        var (calResMean, calResStd, calResThreshold) = postPruneCalSet.Count >= MinEvalSamples
            ? ComputeCalibrationResidualStats(postPruneCalSet, SmoteCalibratedProb)
            : (0.0, 0.0, 1.0);
        _logger.LogDebug("Cal residuals: mean={M:F4} std={S:F4} thr={T:F4}", calResMean, calResStd, calResThreshold);

        // ── 16d. Feature variances ───────────────────────────────────────────
        double[] featureVariances = ComputeFeatureVariancesSmote(balancedTrain, F);

        // ── 16e. Prediction stability score ──────────────────────────────────
        double predictionStability = testSet.Count >= MinEvalSamples
            ? ComputePredictionStabilityScore(testSet, SmoteCalibratedProb) : 0.0;
        _logger.LogDebug("Prediction stability={S:F4}", predictionStability);

        // ── 16f. Warm-start artifact ─────────────────────────────────────────
        var warmStartArtifact = BuildSmoteWarmStartArtifact(
            attempted:            warmStart is not null,
            compatible:           warmStart is not null,
            reusedLearnerCount:   warmStart?.BaseLearnersK ?? 0,
            totalParentLearners:  warmStart?.BaseLearnersK ?? 0,
            issues:               []);

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
            PlattA                     = SafeSmote(plattA, 1.0),
            PlattB                     = SafeSmote(plattB),
            PlattABuy                  = SafeSmote(plattABuy, 1.0),
            PlattBBuy                  = SafeSmote(plattBBuy),
            PlattASell                 = SafeSmote(plattASell, 1.0),
            PlattBSell                 = SafeSmote(plattBSell),
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
            OptimalThreshold           = SafeSmote(optimalThreshold, 0.5),
            Ece                        = SafeSmote(ece),
            MetaWeights                = meta.Weights,
            MetaBias                   = meta.Bias,
            IsotonicBreakpoints        = isotonicBp,
            OobAccuracy                = SafeSmote(oobAccuracy),
            ConformalQHat              = SafeSmote(conformalQHat),
            JackknifeResiduals         = jackknifeResiduals,
            MetaLabelWeights              = metaLabelWeights,
            MetaLabelBias                 = metaLabelBias,
            MetaLabelThreshold            = 0.5,
            MetaLabelTopFeatureIndices    = metaLabelTopFeatures,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = SafeSmote(abstentionThreshold, 0.5),
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
            WalkForwardSharpeTrend     = SafeSmote(cvResult.SharpeTrend),
            TemperatureScale           = SafeSmote(temperatureScale),
            EnsembleDiversity          = SafeSmote(ensembleDiversity),
            BrierSkillScore            = SafeSmote(bss),
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AvgKellyFraction           = SafeSmote(avgKellyFraction),
            RedundantFeaturePairs      = redundantPairs,
            OobPrunedLearnerCount      = oobPrunedCount,
            DecisionBoundaryMean       = SafeSmote(dbMean),
            DecisionBoundaryStd        = SafeSmote(dbStd),
            DurbinWatsonStatistic      = SafeSmote(durbinWatson),
            PolyLearnerStartIndex      = polyStart,
            MlpHiddenDim               = hp.MlpHiddenDim,
            MlpHiddenWeights           = ensMlpHW,
            MlpHiddenBiases            = ensMlpHB,
            SwaCheckpointCount         = swaCount,
            SmoteSeed                  = smoteSeed,
            SmoteDriftArtifact                = driftArtifact,
            SmoteWarmStartArtifact            = warmStartArtifact,
            SmoteCalibrationResidualMean      = SafeSmote(calResMean),
            SmoteCalibrationResidualStd       = SafeSmote(calResStd),
            SmoteCalibrationResidualThreshold = SafeSmote(calResThreshold, 1.0),
            SmoteSelectionSamples             = selectionSet.Count,
            SmoteAdversarialAuc               = SafeSmote(adversarialAuc, 0.5),
            SmoteCalibrationLoss              = SafeSmote(murphyCalLoss),
            SmoteRefinementLoss               = SafeSmote(murphyRefLoss),
            SmotePredictionStabilityScore     = SafeSmote(predictionStability),
            FeatureVariances                  = featureVariances,
            ReliabilityBinConfidence          = relBinConf,
            ReliabilityBinAccuracy            = relBinAcc,
            ReliabilityBinCounts              = relBinCounts,
            CalibrationLoss                   = SafeSmote(murphyCalLoss),
            RefinementLoss                    = SafeSmote(murphyRefLoss),
            PredictionStabilityScore          = SafeSmote(predictionStability),
        };

        // ── 18. Sanitize + audit before serialization ─────────────────────────
        SanitizeSmoteSnapshotArrays(snapshot);

        var auditResult = RunSmoteAudit(snapshot, testSet.Count > 0 ? testSet : calSet);
        snapshot.SmoteAuditArtifact = auditResult.Artifact;
        if (auditResult.Findings.Length > 0 && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("SMOTE audit findings: {Findings}", string.Join("; ", auditResult.Findings));

        byte[] modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);
        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
