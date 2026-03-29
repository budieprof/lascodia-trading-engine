using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
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
///   <item>EV-optimal decision threshold swept on the test set to maximise expected value.</item>
///   <item>A parallel linear regressor predicts magnitude in ATR-normalised units.</item>
///   <item>Optional feature pruning: low-importance features are masked and the ensemble is re-trained.</item>
///   <item>Optional warm-start: ensemble weights are initialised from the previous model snapshot.</item>
/// </list>
/// </para>
/// </summary>
[RegisterService]
public sealed class BaggedLogisticTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "BaggedLogisticEnsemble";
    private const string ModelVersion = "9.0";

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

        var featureCount = samples[0].Features.Length;

        // ── 0. Incremental update fast-path ─────────────────────────────────
        // When UseIncrementalUpdate is enabled and a warm-start snapshot is available,
        // fine-tune the existing model on only the most recent data slice instead of
        // doing a full retrain. Much faster for adapting to regime changes.
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * 24); // approx hourly bars
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

        // ── 1. Z-score standardisation over ALL samples ──────────────────────
        // Plain loops — avoids two LINQ Select+ToList/ToArray passes over the full sample set.
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, featureCount, ct);
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70 % train | 10 % Platt cal | ~18 % test ──
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

        // Reduce epochs for warm-start runs — weights already near-optimal
        var effectiveHp = warmStart is not null
            ? hp with { MaxEpochs = Math.Max(30, hp.MaxEpochs / 2), LearningRate = hp.LearningRate / 3.0 }
            : hp;

        // ── 3b. Stationarity gate (soft ADF check) ────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, featureCount);
            double nonStatFraction = featureCount > 0 ? (double)nonStatCount / featureCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
            {
                _logger.LogWarning(
                    "Stationarity gate: {NonStat}/{Total} features have unit root (p>0.05). Consider enabling FracDiffD.",
                    nonStatCount, featureCount);
            }
        }

        // ── 3c. Density-ratio importance weights ──────────────────────────────
        // Train a logistic discriminator to distinguish "recent" (last DensityRatioWindowDays
        // proxy samples) from "historical" samples. The resulting p/(1-p) weights are multiplied
        // into the temporal weights inside FitEnsemble to focus bootstrap on recent distribution.
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, featureCount, hp.DensityRatioWindowDays);
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
            var csWeights = ComputeCovariateShiftWeights(trainSet, parentBp, featureCount);
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
         double[][]? ensembleMlpHiddenW, double[][]? ensembleMlpHiddenB) ensembleResult;
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
             ensMlpHW, ensMlpHB) = ensembleResult;

        // MLP state bundle — threaded through all post-training helpers
        var mlp = new MlpState(ensMlpHW, ensMlpHB, hp.MlpHiddenDim);

        // ── 4b. Greedy Ensemble Selection (GES) ──────────────────────────────
        double[] gesWeights = hp.EnableGreedyEnsembleSelection && calSet.Count >= 20
            ? RunGreedyEnsembleSelection(calSet, weights, biases, featureCount, featureSubsets, mlp: mlp)
            : [];
        if (gesWeights.Length > 0)
            _logger.LogDebug("GES weights: [{W}]",
                string.Join(",", gesWeights.Select(w => w.ToString("F3"))));

        // ── 5. Fit magnitude regressor ──────────────────────────────────────
        // When multi-task joint loss was active, FitEnsemble returns averaged magnitude
        // head weights; use those directly. Otherwise fall back to the standard OLS pass.
        var (magWeights, magBias) = mtMagWeights is { Length: > 0 }
            ? (mtMagWeights, mtMagBias)
            : FitLinearRegressor(trainSet, featureCount, hp, ct);

        // ── 5b. Fit stacking meta-learner on calibration set ─────────────────
        // Meta-learner maps [p_0,...,p_{K-1}] → final probability via logistic regression,
        // learning optimal per-learner weights rather than enforcing uniform averaging.
        var meta = FitMetaLearner(calSet, weights, biases, featureCount, featureSubsets, mlp);
        _logger.LogDebug(
            "Stacking meta-learner: bias={B:F4} weights=[{W}]",
            meta.Bias, string.Join(",", meta.Weights.Select(w => w.ToString("F3"))));

        // ── 6. Platt calibration (on meta-learner output) ───────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, weights, biases, featureCount, featureSubsets, meta, mlp);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 6b. Class-conditional Platt (Buy / Sell separate scalers) ────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, weights, biases, featureCount, featureSubsets, meta, mlp);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 6c. Average Kelly fraction on cal set ─────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(
            calSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp);
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

        // ── 9. EV-optimal decision threshold (tuned on cal set to avoid test-set leakage) ──
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax, mlp);
        _logger.LogInformation("EV-optimal threshold={Thr:F2} (default 0.50)", optimalThreshold);

        // ── 10. Permutation feature importance ────────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(
                testSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp, ct: ct)
            : new float[featureCount];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: MLFeatureHelper.FeatureNames[idx]))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "Top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Calibration-set permutation importance scores (for warm-start transfer) ──
        // Compute on calSet so warm-start retrain can bias feature sampling toward historically
        // important features. Stored separately from FeatureImportance (test-set float[] above).
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, weights, biases, featureCount, featureSubsets, mlp, ct)
            : new double[featureCount];

        // ── 11. Feature pruning re-train pass ─────────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, featureCount);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && featureCount - prunedCount >= 10)
        {
            _logger.LogInformation(
                "Feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, featureCount);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            (double[][] Weights, double[] Biases, int[][]? Subsets, int PolyStart,
             double[]? MtMagW, double MtMagB, double[][]? PrunedMlpHW, double[][]? PrunedMlpHB) prunedEnsemble;
            try
            {
                prunedEnsemble = FitEnsemble(maskedTrain, prunedHp, featureCount, null, densityWeights, ct);
            }
            catch (AggregateException pae) when (pae.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pae.InnerException!).Throw();
                throw; // unreachable
            }
            var (pw, pb, _, _, pMtMagW, pMtMagB, _, _) = prunedEnsemble;
            var pMlp = new MlpState(prunedEnsemble.PrunedMlpHW, prunedEnsemble.PrunedMlpHB, prunedHp.MlpHiddenDim);
            var (pmw, pmb)  = pMtMagW is { Length: > 0 }
                ? (pMtMagW, pMtMagB)
                : FitLinearRegressor(maskedTrain, featureCount, prunedHp, ct);
            var pMeta       = FitMetaLearner(maskedCal, pw, pb, featureCount, null, pMlp);
            var (pA, pB)    = FitPlattScaling(maskedCal, pw, pb, featureCount, null, pMeta, pMlp);
            var prunedMetrics = EvaluateEnsemble(maskedTest, pw, pb, pmw, pmb, pA, pB, featureCount, null, pMeta, pMlp);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                weights        = pw;     biases   = pb;
                magWeights     = pmw;    magBias  = pmb;
                plattA         = pA;     plattB   = pB;
                meta           = pMeta;
                finalMetrics   = prunedMetrics;
                featureSubsets = null; // pruned model uses full feature set
                mlp            = pMlp;
                // Re-run GES on pruned model (original gesWeights are now stale)
                gesWeights = hp.EnableGreedyEnsembleSelection && maskedCal.Count >= 20
                    ? RunGreedyEnsembleSelection(maskedCal, pw, pb, featureCount, null, mlp: pMlp)
                    : [];
                ece              = ComputeEce(maskedTest, pw, pb, pA, pB, featureCount, null, pMeta, pMlp);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, pw, pb, pA, pB, featureCount, null, pMeta,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax, pMlp);
            }
            else
            {
                _logger.LogInformation(
                    "Pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask  = new bool[featureCount]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[featureCount]; Array.Fill(activeMask, true);
        }

        // ── 11b. Isotonic calibration, OOB accuracy, conformal threshold ──────
        // Apply feature mask to cal set when pruning was accepted so probabilities
        // match the pruned weights' feature view.
        var postPruneCalSet = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;

        double[] isotonicBp = FitIsotonicCalibration(
            postPruneCalSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        var oobTemporalWeights = ComputeTemporalWeights(trainSet.Count, effectiveHp.TemporalDecayLambda);
        double oobAccuracy = ComputeOobAccuracy(
            trainSet, weights, biases, oobTemporalWeights, featureCount, featureSubsets, hp.K, mlp);
        _logger.LogInformation("OOB accuracy={OobAcc:P1}", oobAccuracy);

        // ── 11b-extra. Per-learner calibration-set accuracy (Round 9) ────────
        // Evaluate each base learner independently on the calibration set so that
        // MLSignalScorer can use softmax-weighted voting when GES is disabled.
        var learnerCalAccuracies = new double[weights.Length];
        if (postPruneCalSet.Count > 0)
        {
            // Pre-compute per-learner probs for each cal sample via GetLearnerProbs
            // (handles both linear and MLP forward passes correctly).
            foreach (var s in postPruneCalSet)
            {
                var lp = GetLearnerProbs(s.Features, weights, biases, featureCount, featureSubsets,
                    mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                for (int k = 0; k < weights.Length; k++)
                    if ((lp[k] >= 0.5 ? 1 : 0) == s.Direction)
                        learnerCalAccuracies[k]++;
            }
            for (int k = 0; k < weights.Length; k++)
                learnerCalAccuracies[k] /= postPruneCalSet.Count;
            _logger.LogDebug("Per-learner cal accuracies: [{Accs}]",
                string.Join(", ", learnerCalAccuracies.Select(a => $"{a:P0}")));
        }

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(
            postPruneCalSet, weights, biases, plattA, plattB, isotonicBp,
            featureCount, featureSubsets, meta, mlp, alpha: conformalAlpha);
        _logger.LogInformation("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── 11c. Jackknife+ residuals ─────────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(
            trainSet, weights, biases, oobTemporalWeights, featureCount, featureSubsets, hp.K, polyLearnerStart, mlp);
        _logger.LogInformation("Jackknife+ residuals computed: {N} samples", jackknifeResiduals.Length);

        // ── 11d. Meta-label secondary classifier ──────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(calSet, weights, biases, featureCount, featureSubsets, mlp);
        _logger.LogDebug("Meta-label model: bias={B:F4}", metaLabelBias);

        // ── 11e. Abstention gate (selective prediction) ───────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalSet, weights, biases, plattA, plattB,
            metaLabelWeights, metaLabelBias, featureCount, featureSubsets, mlp);
        _logger.LogDebug("Abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── 11f. Quantile magnitude regressor (pinball loss) ─────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, featureCount, hp.MagnitudeQuantileTau);
            _logger.LogDebug("Quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 11g. Decision boundary distance (analytic gradient norms) ─────────
        var (dbMean, dbStd) = postPruneCalSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalSet, weights, biases, featureCount, featureSubsets, mlp)
            : (0.0, 0.0);
        _logger.LogDebug("Decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        // ── 11h. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, featureCount);
        _logger.LogDebug("Durbin-Watson statistic={DW:F4} (2=no autocorr, <1.5=positive autocorr)", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}). " +
                "Consider enabling AR feature injection in the next training cycle.",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 11i. Mutual-information feature redundancy check ─────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, featureCount, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "MI redundancy: {N} feature pairs exceed threshold {T:F2}×log(2): {Pairs}",
                    redundantPairs.Length, hp.MutualInfoRedundancyThreshold,
                    string.Join(", ", redundantPairs));
            else
                _logger.LogDebug("MI redundancy check: no redundant pairs above threshold {T:F2}.",
                    hp.MutualInfoRedundancyThreshold);
        }

        // ── 11k. OOB-contribution ensemble pruning ────────────────────────────
        int oobPrunedCount = 0;
        if (hp.OobPruningEnabled && hp.K >= 2)
        {
            oobPrunedCount = PruneByOobContribution(
                trainSet, weights, biases, oobTemporalWeights, featureCount, featureSubsets, hp.K, mlp);
            if (oobPrunedCount > 0)
                _logger.LogInformation(
                    "OOB pruning: removed {N}/{K} learners whose removal improved ensemble accuracy.",
                    oobPrunedCount, hp.K);
        }

        // ── 11l. Temperature scaling ─────────────────────────────────────────
        double temperatureScale = 0.0; // 0 = disabled
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(
                postPruneCalSet, weights, biases, featureCount, featureSubsets, meta, mlp);
            _logger.LogDebug("Temperature scaling: T={T:F4} (1.0=no correction)", temperatureScale);
        }

        // ── 11m. Ensemble diversity metric ────────────────────────────────────
        double ensembleDiversity = ComputeEnsembleDiversity(weights, featureCount);
        _logger.LogDebug("Ensemble diversity (avg pairwise ρ)={Div:F4}", ensembleDiversity);
        if (hp.MaxEnsembleDiversity < 1.0 && ensembleDiversity > hp.MaxEnsembleDiversity)
            _logger.LogWarning(
                "Ensemble diversity warning: avg ρ={Div:F3} > threshold {Max:F2}. " +
                "Consider increasing K or enabling MaxLearnerCorrelation enforcement.",
                ensembleDiversity, hp.MaxEnsembleDiversity);

        // ── 11n. Brier Skill Score ────────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(
            testSet, weights, biases, plattA, plattB, featureCount, featureSubsets, meta, mlp);
        _logger.LogInformation("Brier Skill Score (BSS)={BSS:F4} (>0 beats naive predictor)", brierSkillScore);

        // ── 11j. PSI baseline (feature quantile breakpoints) ──────────────────
        // Plain loop — avoids LINQ Select+ToList allocation.
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 11p. Post-training NaN/Inf weight sanitization ────────────────────
        // Any learner with non-finite weights (from numerical instability) is zeroed out
        // to prevent poisoning the ensemble average.
        int sanitizedCount = 0;
        for (int k = 0; k < weights.Length; k++)
        {
            bool needsSanitize = !double.IsFinite(biases[k]);
            if (!needsSanitize)
            {
                for (int j = 0; j < weights[k].Length; j++)
                {
                    if (!double.IsFinite(weights[k][j])) { needsSanitize = true; break; }
                }
            }
            if (needsSanitize)
            {
                Array.Clear(weights[k], 0, weights[k].Length);
                biases[k] = 0.0;
                sanitizedCount++;
                _logger.LogWarning("Sanitized learner {K}: non-finite weights replaced with zeros.", k);
            }
        }
        if (sanitizedCount > 0)
            _logger.LogWarning("Post-training sanitization: {N}/{K} learners had non-finite weights.",
                sanitizedCount, weights.Length);

        // ── 12. Serialise model snapshot ──────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
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
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = DateTime.UtcNow,
            FeatureImportance          = featureImportance,
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
            PolyLearnerStartIndex      = polyLearnerStart,
            FracDiffD                  = hp.FracDiffD,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            JackknifeResiduals         = jackknifeResiduals,
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            FeatureImportanceScores    = calImportanceScores,
            EnsembleSelectionWeights   = gesWeights,
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
            OobPrunedLearnerCount      = oobPrunedCount,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            TemperatureScale           = temperatureScale,
            EnsembleDiversity          = ensembleDiversity,
            BrierSkillScore            = brierSkillScore,
            TrainedAtUtc               = DateTime.UtcNow,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            LearnerCalAccuracies       = learnerCalAccuracies,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            SanitizedLearnerCount      = sanitizedCount,
            MlpHiddenDim               = hp.MlpHiddenDim,
            MlpHiddenWeights           = ensMlpHW,
            MlpHiddenBiases            = ensMlpHB,
            ConformalCoverage          = hp.ConformalCoverage,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("Walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // Folds are independent — train them in parallel.
        // Each slot is null if the fold was skipped (insufficient data / too small test).
        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            // Purged CV: also remove samples whose feature-lookback window overlaps the test period.
            // Sample i uses candles [i .. i + LookbackWindow - 1]; overlap starts when
            // i + LookbackWindow - 1 >= testStart, i.e. i >= testStart - LookbackWindow + 1.
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("Fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                return;
            }

            var foldTrain = samples[..trainEnd].ToList();

            // ── Time-series purging: remove trailing training samples whose label horizon
            //    overlaps the test fold start (in addition to the lookback-window embargo above).
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeLimit = foldTrain.Count;
                // Remove samples at index i where i + PurgeHorizonBars >= testStart
                // i.e. i >= testStart - PurgeHorizonBars
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < purgeLimit)
                {
                    int purgeCount = purgeLimit - purgeFrom;
                    foldTrain  = foldTrain[..purgeFrom];
                    if (purgeCount > 0)
                        _logger.LogDebug(
                            "Purging: removed {N} train samples overlapping test fold start.",
                            purgeCount);
                }
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];

            if (foldTest.Count < 20) return;

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(50, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            var (w, b, subs, _, _, _, _, _) = FitEnsemble(foldTrain, cvHp, featureCount, null, null, ct, forceSequential: true);
            var (mw, mb) = FitLinearRegressor(foldTrain, featureCount, cvHp, ct);
            var m        = EvaluateEnsemble(foldTest, w, b, mw, mb, 1.0, 0.0, featureCount, subs);

            // Compute per-feature mean |weight| for walk-forward stability scoring
            var foldImp = new double[featureCount];
            for (int ki = 0; ki < w.Length; ki++)
            {
                int[] s2 = subs is not null && ki < subs.Length
                    ? subs[ki]
                    : [..Enumerable.Range(0, Math.Min(w[ki].Length, featureCount))];
                foreach (int j in s2)
                    if (j < featureCount) foldImp[j] += Math.Abs(w[ki][j]);
            }
            double kCount = w.Length;
            for (int j = 0; j < featureCount; j++) foldImp[j] /= kCount;

            // ── Equity-curve gate ──────────────────────────────────────────────
            // Plain array — avoids LINQ Select+ToList allocation inside parallel folds.
            var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double rawP = EnsembleProb(foldTest[pi].Features, w, b, featureCount, subs);
                foldPredictions[pi] = (rawP >= 0.5 ? 1 : -1,
                                       foldTest[pi].Direction > 0 ? 1 : -1);
            }

            var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;

            // Write to slot indexed by fold — each fold owns a unique index, no lock needed.
            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        // ── Aggregate parallel fold results (preserve fold order for Sharpe trend) ──
        var accList         = new List<double>(folds);
        var f1List          = new List<double>(folds);
        var evList          = new List<double>(folds);
        var sharpeList      = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds        = 0;

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

        // Check equity-curve gate: bad-fold fraction exceeds MaxBadFoldFraction (default 0.5)
        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
        {
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{TotalFolds} folds failed (maxDD or Sharpe). Model rejected.",
                badFolds, accList.Count);
        }

        double avgAcc      = accList.Average();
        double stdAcc      = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // Sharpe trend gate: if slope is significantly negative, treat as bad-fold majority
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "Walk-forward Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // Feature stability: CV = σ/μ of mean |weight| across folds per feature
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImportances.Count;
            for (int j = 0; j < featureCount; j++)
            {
                // Compute mean and std with plain loops — avoids LINQ Average+Select+ToList.
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
            AvgAccuracy:           avgAcc,
            StdAccuracy:           stdAcc,
            AvgF1:                 f1List.Average(),
            AvgEV:                 evList.Average(),
            AvgSharpe:             sharpeList.Average(),
            FoldCount:             accList.Count,
            SharpeTrend:           sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ── Ensemble fitting ──────────────────────────────────────────────────────

    /// <summary>
    /// Fits K base logistic regression learners.
    /// <list type="bullet">
    ///   <item><b>Feature subsampling</b> — each learner trains on a random subset of √F features.</item>
    ///   <item><b>Adam</b> optimizer (β₁=0.9, β₂=0.999) with cosine-annealed base LR.</item>
    ///   <item><b>Stratified biased bootstrap</b> — equal buy/sell class ratio per bag.</item>
    ///   <item><b>Label smoothing</b> — y_smooth = y(1−ε) + 0.5ε.</item>
    ///   <item><b>Warm-start</b> — initialise weights from previous snapshot when supplied.</item>
    /// </list>
    /// Returns weights, biases, and per-learner feature-subset indices (null when no subsampling).
    /// </summary>
    private (double[][] Weights, double[] Biases, int[][]? Subsets, int PolyStart,
             double[]? MtMagWeights, double MtMagBias,
             double[][]? MlpHiddenW, double[][]? MlpHiddenB) FitEnsemble(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        int                  featureCount,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool                 forceSequential = false)
    {
        var weights        = new double[hp.K][];
        var biases         = new double[hp.K];
        bool useSubsampling = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0;
        var featureSubsets  = useSubsampling ? new int[hp.K][] : null;

        // MLP hidden layer arrays (null when MlpHiddenDim == 0 → linear logistic)
        int hiddenDim       = Math.Max(0, hp.MlpHiddenDim);
        bool useMlp         = hiddenDim > 0;
        var mlpHiddenW      = useMlp ? new double[hp.K][] : null;  // [k][h * inputDim] row-major
        var mlpHiddenB      = useMlp ? new double[hp.K][] : null;  // [k][hiddenDim]

        // Polynomial learner support: last PolyLearnerFraction * K learners use augmented features
        int polyStart = hp.PolyLearnerFraction > 0
            ? (int)(hp.K * (1.0 - hp.PolyLearnerFraction))
            : hp.K;
        // Number of pairwise products for top-5 features: 5C2 = 10
        const int PolyTopN = 5;
        int polyPairCount = PolyTopN * (PolyTopN - 1) / 2; // = 10
        int polyFeatureCount = featureCount + polyPairCount;

        int valSize  = Math.Max(20, train.Count / 10);
        var valSet   = train[^valSize..];
        var trainSet = train[..^valSize];

        var temporalWeights = ComputeTemporalWeights(trainSet.Count, hp.TemporalDecayLambda);
        bool useNoise       = hp.NoiseSigma > 0.0;

        // Class weights: inverse-frequency balancing to prevent majority-class collapse.
        // classWeightBuy/Sell are multiplied into the per-sample gradient so the minority
        // class receives proportionally higher loss penalty.
        double classWeightBuy  = 1.0;
        double classWeightSell = 1.0;
        if (hp.UseClassWeights)
        {
            int buyCount  = trainSet.Count(s => s.Direction > 0);
            int sellCount = trainSet.Count - buyCount;
            if (buyCount > 0 && sellCount > 0)
            {
                classWeightBuy  = (double)trainSet.Count / (2.0 * buyCount);
                classWeightSell = (double)trainSet.Count / (2.0 * sellCount);
            }
        }

        // Blend density-ratio importance weights with temporal decay weights
        if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalWeights.Length)
        {
            var blended = new double[temporalWeights.Length];
            double sum  = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < blended.Length; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // Multi-task magnitude heads (one per learner, active when MagLossWeight > 0)
        bool useMagTask      = hp.MagLossWeight > 0.0;
        var  magWeightsK     = useMagTask ? new double[hp.K][] : null;
        var  magBiasesK      = useMagTask ? new double[hp.K]  : null;

        // Warm-start: check if prior importance scores are available for biased feature sampling
        bool useBiasedFeatureSampling =
            warmStart is not null &&
            warmStart.FeatureImportanceScores.Length == featureCount &&
            hp.FeatureSampleRatio > 0.0;

        if (useBiasedFeatureSampling)
            _logger.LogDebug("Warm-start: using prior feature importance scores for biased feature sampling.");

        // ── Determine parallelism gate ─────────────────────────────────────────
        // Learners are fully independent when sequential-coupling regularisers are
        // disabled (NCL, diversity, noise-correction all reference prior learners k'<k).
        bool learnersAreIndependent =
            hp.NclLambda              <= 0.0 &&
            hp.DiversityLambda        <= 0.0 &&
            hp.NoiseCorrectionThreshold <= 0.0;

        // Local function: train a single learner k and write into the shared arrays.
        // All captured arrays (weights, biases, featureSubsets, magWeightsK, magBiasesK)
        // are indexed by k, so no cross-slot races when running in parallel.
        void TrainLearner(int k)
        {
            if (!learnersAreIndependent) ct.ThrowIfCancellationRequested();

            // ── Feature subset for this learner ───────────────────────────────
            bool isPolyLearner = hp.PolyLearnerFraction > 0 && k >= polyStart;
            int effectiveDim   = isPolyLearner ? polyFeatureCount : featureCount;

            int[] subset;
            if (useSubsampling)
            {
                if (useBiasedFeatureSampling && !isPolyLearner)
                    subset = GenerateBiasedFeatureSubset(
                        effectiveDim, hp.FeatureSampleRatio, warmStart!.FeatureImportanceScores, seed: k * 97 + 13);
                else
                    subset = GenerateFeatureSubset(effectiveDim, hp.FeatureSampleRatio, seed: k * 97 + 13);
            }
            else
            {
                subset = Enumerable.Range(0, effectiveDim).ToArray();
            }

            if (featureSubsets is not null)
                featureSubsets[k] = subset;

            // ── Determine output-layer dimension ───────────────────────────────
            // MLP: output weights map from hiddenDim → scalar logit
            // Linear: output weights map from effectiveDim → scalar logit
            int outputDim = useMlp ? hiddenDim : effectiveDim;
            int subsetLen = subset.Length;

            // ── MLP hidden layer initialisation (Xavier) ──────────────────────
            double[]? hW = null;   // [hiddenDim × subsetLen] row-major
            double[]? hB = null;   // [hiddenDim]
            if (useMlp)
            {
                hW = new double[hiddenDim * subsetLen];
                hB = new double[hiddenDim];
                // Xavier initialisation: std = sqrt(2 / (fan_in + fan_out))
                double xavierStd = Math.Sqrt(2.0 / (subsetLen + hiddenDim));
                var initRng = new Random(k * 71 + 3);
                for (int i = 0; i < hW.Length; i++)
                    hW[i] = SampleGaussian(initRng, xavierStd);
            }

            // ── Warm-start: copy weights, zero non-subset features ────────────
            // Accept warm-start when the saved weight length matches the current output dimension
            // (handles both plain-feature and poly-learner dimension).
            if (warmStart is not null &&
                k < warmStart.Weights.Length &&
                warmStart.Weights[k].Length == outputDim)
            {
                weights[k] = [..warmStart.Weights[k]];
                biases[k]  = k < warmStart.Biases.Length ? warmStart.Biases[k] : 0.0;

                // Zero non-subset features so they don't pollute this learner (linear mode only)
                if (useSubsampling && !useMlp)
                {
                    var subsetSet = new HashSet<int>(subset);
                    int wLen = weights[k].Length;
                    for (int j = 0; j < wLen; j++)
                        if (!subsetSet.Contains(j)) weights[k][j] = 0.0;
                }

                // Warm-start hidden layer if available
                if (useMlp && warmStart.MlpHiddenWeights is not null &&
                    k < warmStart.MlpHiddenWeights.Length &&
                    warmStart.MlpHiddenWeights[k]?.Length == hW!.Length)
                {
                    Array.Copy(warmStart.MlpHiddenWeights[k], hW, hW.Length);
                    if (warmStart.MlpHiddenBiases is not null && k < warmStart.MlpHiddenBiases.Length)
                        Array.Copy(warmStart.MlpHiddenBiases[k], hB!, hB!.Length);
                }
            }
            else
            {
                weights[k] = new double[outputDim];
                biases[k]  = 0.0;
            }

            // Multi-task magnitude head for this learner
            if (useMagTask)
            {
                magWeightsK![k] = new double[effectiveDim];
                magBiasesK![k]  = 0.0;
            }

            // Stratified biased bootstrap: equal class balance per bag
            var bootstrap = StratifiedBiasedBootstrap(
                trainSet, temporalWeights, trainSet.Count, seed: k * 31 + 7);

            // Adam first and second moment vectors (rented from ArrayPool to reduce GC pressure
            // when K learners are trained in parallel — all K sets are alive simultaneously).
            var pool = ArrayPool<double>.Shared;
            var mW  = pool.Rent(outputDim);  Array.Clear(mW, 0, outputDim);
            var vW  = pool.Rent(outputDim);  Array.Clear(vW, 0, outputDim);
            // Hidden layer Adam moments (MLP only)
            int hWLen = hiddenDim * subsetLen;
            var mHW = useMlp ? pool.Rent(hWLen) : null;  if (mHW is not null) Array.Clear(mHW, 0, hWLen);
            var vHW = useMlp ? pool.Rent(hWLen) : null;  if (vHW is not null) Array.Clear(vHW, 0, hWLen);
            var mHB = useMlp ? pool.Rent(hiddenDim) : null;  if (mHB is not null) Array.Clear(mHB, 0, hiddenDim);
            var vHB = useMlp ? pool.Rent(hiddenDim) : null;  if (vHB is not null) Array.Clear(vHB, 0, hiddenDim);
            double mB = 0, vB = 0;
            int t = 0;
            double beta1t = 1.0; // running product: AdamBeta1^t — avoids Math.Pow per gradient step
            double beta2t = 1.0; // running product: AdamBeta2^t
            var noiseRng = useNoise ? new Random(k * 137 + 41) : null;

            // Adam moments for multi-task magnitude head
            var    mWmag = useMagTask ? pool.Rent(effectiveDim) : null;  if (mWmag is not null) Array.Clear(mWmag, 0, effectiveDim);
            var    vWmag = useMagTask ? pool.Rent(effectiveDim) : null;  if (vWmag is not null) Array.Clear(vWmag, 0, effectiveDim);
            double mBmag = 0, vBmag = 0;

            double bias         = biases[k];
            double bestValLoss  = double.MaxValue;
            double peakValAcc   = 0.0;
            double lrScale      = 1.0;   // adaptive LR multiplier (rec 2)
            bool   lrDecayed    = false;
            int    patience     = 0;
            double[] bestW      = [..weights[k]];
            double   bestB      = bias;
            double[]? bestHW    = useMlp ? (double[])hW!.Clone() : null;
            double[]? bestHB    = useMlp ? (double[])hB!.Clone() : null;

            // ── Soft labels + optional Mixup ──────────────────────────────────
            List<TrainingSample> trainingBootstrap;
            double[] bootstrapSoftLabels;
            if (hp.MixupAlpha > 0.0)
            {
                var mixRng        = new Random(k * 53 + 19 + bootstrap.Count);
                var mixedList     = new List<TrainingSample>(bootstrap.Count);
                bootstrapSoftLabels = new double[bootstrap.Count];
                for (int si = 0; si < bootstrap.Count; si++)
                {
                    int    sj  = mixRng.Next(bootstrap.Count);
                    double lam = SampleBeta(mixRng, hp.MixupAlpha);
                    var    sx  = bootstrap[si];
                    var    sy  = bootstrap[sj];
                    var    fm  = new float[sx.Features.Length];
                    for (int fi = 0; fi < sx.Features.Length; fi++)
                        fm[fi] = (float)(lam * sx.Features[fi] + (1.0 - lam) * sy.Features[fi]);
                    double lx = hp.AtrLabelSensitivity > 0.0
                        ? MLFeatureHelper.Sigmoid(sx.Magnitude * (sx.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity)
                        : (sx.Direction > 0 ? 1.0 - hp.LabelSmoothing : (double)hp.LabelSmoothing);
                    double ly = hp.AtrLabelSensitivity > 0.0
                        ? MLFeatureHelper.Sigmoid(sy.Magnitude * (sy.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity)
                        : (sy.Direction > 0 ? 1.0 - hp.LabelSmoothing : (double)hp.LabelSmoothing);
                    bootstrapSoftLabels[si] = lam * lx + (1.0 - lam) * ly;
                    mixedList.Add(new TrainingSample(fm, sx.Direction, sx.Magnitude));
                }
                trainingBootstrap = mixedList;
            }
            else if (hp.AtrLabelSensitivity > 0.0)
            {
                trainingBootstrap   = bootstrap;
                bootstrapSoftLabels = new double[bootstrap.Count];
                for (int si = 0; si < bootstrap.Count; si++)
                {
                    var s = bootstrap[si];
                    bootstrapSoftLabels[si] = MLFeatureHelper.Sigmoid(
                        s.Magnitude * (s.Direction > 0 ? 1.0 : -1.0) / hp.AtrLabelSensitivity);
                }
            }
            else
            {
                trainingBootstrap   = bootstrap;
                bootstrapSoftLabels = new double[bootstrap.Count];
                double posLabel = 1.0 - hp.LabelSmoothing;
                double negLabel = hp.LabelSmoothing;
                for (int si = 0; si < bootstrap.Count; si++)
                    bootstrapSoftLabels[si] = bootstrap[si].Direction > 0 ? posLabel : negLabel;
            }

            // ── SWA state ─────────────────────────────────────────────────────
            bool     useSwa   = hp.SwaStartEpoch > 0 && hp.SwaFrequency > 0;
            double[] swaW     = useSwa ? new double[outputDim] : [];
            double   swaB     = 0.0;
            int      swaCount = 0;

            // Pre-allocated gradient buffer (for gradient norm clipping)
            var rawGrads = new double[outputDim];
            // MLP hidden activation buffer (reused per sample)
            var hiddenAct = useMlp ? new double[hiddenDim] : null;
            int batchSize = Math.Max(1, hp.MiniBatchSize);
            bool useMiniBatch = batchSize > 1;
            // Mini-batch gradient accumulators (allocated once, zeroed per batch)
            // For MLP: batchGradW accumulates output-layer gradients (indexed 0..hiddenDim-1)
            // For linear: batchGradW accumulates weight gradients (indexed by feature subset)
            var batchGradW = useMiniBatch ? new double[useMlp ? hiddenDim : effectiveDim] : null;
            double batchGradB = 0.0;
            // MLP hidden-layer batch gradient accumulators
            var batchGradHW = useMiniBatch && useMlp ? new double[hiddenDim * subsetLen] : null;
            var batchGradHB = useMiniBatch && useMlp ? new double[hiddenDim] : null;
            var batchGradMagW = useMiniBatch && useMagTask ? new double[effectiveDim] : null;
            double batchGradMagB = 0.0;

            // Per-epoch shuffle index array for mini-batch training
            // Reduces gradient correlation within batches, improving convergence.
            int[] shuffleIdx = useMiniBatch
                ? Enumerable.Range(0, trainingBootstrap.Count).ToArray()
                : [];
            var shuffleRng = useMiniBatch ? new Random(k * 59 + 17) : null;

            for (int epoch = 0; epoch < hp.MaxEpochs; epoch++)
            {
                ct.ThrowIfCancellationRequested();

                // Cosine-annealing base LR (scaled by adaptive decay if triggered)
                double alpha = hp.LearningRate * lrScale * 0.5 *
                    (1.0 + Math.Cos(Math.PI * epoch / hp.MaxEpochs));

                if (useMiniBatch)
                {
                    if (useMlp)
                    {
                        Array.Clear(batchGradW!, 0, hiddenDim);
                        Array.Clear(batchGradHW!, 0, hWLen);
                        Array.Clear(batchGradHB!, 0, hiddenDim);
                    }
                    else
                    {
                        Array.Clear(batchGradW!, 0, effectiveDim);
                    }
                    batchGradB = 0.0;
                    if (batchGradMagW is not null) Array.Clear(batchGradMagW, 0, effectiveDim);
                    batchGradMagB = 0.0;

                    // Fisher-Yates shuffle for this epoch
                    for (int i = shuffleIdx.Length - 1; i > 0; i--)
                    {
                        int j2 = shuffleRng!.Next(i + 1);
                        (shuffleIdx[i], shuffleIdx[j2]) = (shuffleIdx[j2], shuffleIdx[i]);
                    }
                }

                for (int si = 0; si < trainingBootstrap.Count; si++)
                {
                    // Dense cancellation check every 2000 samples to allow prompt timeout
                    if (si % 2000 == 0 && si > 0) ct.ThrowIfCancellationRequested();

                    int sampleIdx = useMiniBatch ? shuffleIdx[si] : si;
                    var sample = trainingBootstrap[sampleIdx];
                    // Only advance Adam timestep per batch boundary (or every sample if batch=1)
                    if (!useMiniBatch || si % batchSize == 0)
                    {
                        t++;
                        beta1t *= AdamBeta1;
                        beta2t *= AdamBeta2;
                    }

                    // Soft label (Mixup / AtrLabelSensitivity / hard + label smoothing)
                    double y = bootstrapSoftLabels[sampleIdx];

                    // Build augmented features for poly learners
                    float[] sampleFeatures = isPolyLearner
                        ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                        : sample.Features;

                    // Build noisy feature view (shared by linear and MLP paths)
                    // For MLP, we gather subset features into a contiguous buffer
                    double z;
                    if (useMlp)
                    {
                        // Forward: hidden = ReLU(Wh × x_subset + bh), z = Wo · hidden + bias
                        for (int h = 0; h < hiddenDim; h++)
                        {
                            double act = hB![h];
                            int rowOff = h * subsetLen;
                            for (int si2 = 0; si2 < subsetLen; si2++)
                            {
                                double fv = sampleFeatures[subset[si2]];
                                if (useNoise) fv += SampleGaussian(noiseRng!, hp.NoiseSigma);
                                act += hW![rowOff + si2] * fv;
                            }
                            hiddenAct![h] = Math.Max(0.0, act); // ReLU
                        }
                        z = bias;
                        for (int h = 0; h < hiddenDim; h++)
                            z += weights[k][h] * hiddenAct![h];
                    }
                    else
                    {
                        // Linear logistic forward pass
                        z = bias;
                        foreach (int j in subset)
                        {
                            double fv = sampleFeatures[j];
                            if (useNoise) fv += SampleGaussian(noiseRng!, hp.NoiseSigma);
                            z += weights[k][j] * fv;
                        }
                    }

                    double p = MLFeatureHelper.Sigmoid(z);
                    if (!double.IsFinite(p)) continue; // NaN/Inf guard

                    // Asymmetric loss: FpCostWeight > 0.5 → more FP penalty → higher precision
                    double errWeight = Math.Abs(hp.FpCostWeight - 0.5) > 1e-6
                        ? (sample.Direction == 0 ? 2.0 * hp.FpCostWeight : 2.0 * (1.0 - hp.FpCostWeight))
                        : 1.0;
                    // Class weight: minority class gets higher gradient contribution
                    double cw = sample.Direction > 0 ? classWeightBuy : classWeightSell;
                    double err = (p - y) * errWeight * cw;

                    // Label noise correction via confident learning (from epoch 1 onward).
                    // Uses current ensemble average probability to estimate P(correct label).
                    // Samples with low P(correct) get their gradient soft-downweighted.
                    if (epoch > 0 && hp.NoiseCorrectionThreshold > 0.0 && k > 0)
                    {
                        // Compute ensemble average probability across already-fitted learners
                        double ensP = 0.0;
                        for (int kp = 0; kp < k; kp++)
                        {
                            bool priorIsPoly = hp.PolyLearnerFraction > 0 && kp >= polyStart;
                            float[] priorFeatures = priorIsPoly
                                ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                                : sample.Features;
                            double zp = biases[kp];
                            if (featureSubsets is not null && kp < featureSubsets.Length)
                                foreach (int j in featureSubsets[kp])
                                    zp += weights[kp][j] * priorFeatures[j];
                            else
                            {
                                int priorDim = weights[kp].Length;
                                for (int j = 0; j < priorDim; j++)
                                    zp += weights[kp][j] * priorFeatures[j];
                            }
                            ensP += MLFeatureHelper.Sigmoid(zp);
                        }
                        ensP /= k; // average over k prior learners

                        int label = sample.Direction > 0 ? 1 : 0;
                        double noiseWeight = ComputeNoiseCorrectionWeight(ensP, label, hp.NoiseCorrectionThreshold);
                        err *= noiseWeight;
                    }

                    // ── Shared pAvg computation for NCL and diversity regularisation ──
                    double pAvg = 0.0;
                    if ((hp.NclLambda > 0.0 || hp.DiversityLambda > 0.0) && k > 0)
                    {
                        double pSum = p;
                        for (int kp = 0; kp < k; kp++)
                        {
                            bool priorIsPoly = hp.PolyLearnerFraction > 0 && kp >= polyStart;
                            float[] priorFeatures = priorIsPoly
                                ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                                : sample.Features;
                            double zp = biases[kp];
                            if (featureSubsets is not null && kp < featureSubsets.Length)
                                foreach (int j in featureSubsets[kp])
                                    zp += weights[kp][j] * priorFeatures[j];
                            else
                            {
                                int priorDim = weights[kp].Length;
                                for (int j = 0; j < priorDim; j++)
                                    zp += weights[kp][j] * priorFeatures[j];
                            }
                            pSum += MLFeatureHelper.Sigmoid(zp);
                        }
                        pAvg = pSum / (k + 1);
                    }

                    // NCL: sequential negative-correlation regularisation
                    double nclGrad = hp.NclLambda > 0.0 && k > 0
                        ? hp.NclLambda * (p - pAvg) * p * (1.0 - p)
                        : 0.0;

                    // Symmetric Cross-Entropy (Wang et al. 2019): adds reverse-KL gradient
                    // d(L_RCE)/dz = |log(A)| × p(1−p) × (y==0 ? +1 : −1), A = 1e-4
                    // Saturates for confident-wrong predictions → robust to noisy timeout labels.
                    double sceGrad = hp.UseSymmetricCE && hp.SymmetricCeAlpha > 0.0
                        ? hp.SymmetricCeAlpha * 9.2103 * p * (1.0 - p) * (y < 0.5 ? 1.0 : -1.0)
                        : 0.0;

                    // Diversity regularisation: maximises (p_k − p̄)² across learners
                    // d(−λ(p−p̄)²)/dz_k = −2λ(p−p̄)·p(1−p)
                    double divGrad = hp.DiversityLambda > 0.0 && k > 0
                        ? -hp.DiversityLambda * 2.0 * (p - pAvg) * p * (1.0 - p)
                        : 0.0;

                    double totalErr = err + nclGrad + sceGrad + divGrad;

                    // ── Gradient computation + optional norm clipping ─────────
                    double bGrad = totalErr;
                    // rawGrads holds gradients for the output layer weights
                    if (useMlp)
                    {
                        // Output layer: dL/dWo[h] = totalErr * hidden[h] + L2 * Wo[h]
                        for (int h = 0; h < hiddenDim; h++)
                            rawGrads[h] = totalErr * hiddenAct![h] + hp.L2Lambda * weights[k][h];
                    }
                    else
                    {
                        foreach (int j in subset)
                            rawGrads[j] = totalErr * sampleFeatures[j] + hp.L2Lambda * weights[k][j];
                    }

                    // Magnitude head gradient (computed alongside direction gradient)
                    double magHuberGradSample = 0.0;
                    if (useMagTask)
                    {
                        double magPred = magBiasesK![k];
                        foreach (int j in subset) magPred += magWeightsK![k][j] * sampleFeatures[j];
                        double magErr = magPred - sample.Magnitude;
                        magHuberGradSample = Math.Abs(magErr) <= 1.0 ? magErr : Math.Sign(magErr);
                    }

                    if (hp.MaxGradNorm > 0.0)
                    {
                        double gnormSq = bGrad * bGrad;
                        if (useMlp)
                            for (int h = 0; h < hiddenDim; h++) gnormSq += rawGrads[h] * rawGrads[h];
                        else
                            foreach (int j in subset) gnormSq += rawGrads[j] * rawGrads[j];
                        double gnorm = Math.Sqrt(gnormSq);
                        if (gnorm > hp.MaxGradNorm)
                        {
                            double sc = hp.MaxGradNorm / gnorm;
                            bGrad *= sc;
                            if (useMlp)
                                for (int h = 0; h < hiddenDim; h++) rawGrads[h] *= sc;
                            else
                                foreach (int j in subset) rawGrads[j] *= sc;
                        }
                    }

                    // ── Mini-batch: accumulate gradients ──────────────────────
                    if (useMiniBatch)
                    {
                        if (useMlp)
                        {
                            // Output-layer gradients: rawGrads indexed 0..hiddenDim-1
                            for (int h = 0; h < hiddenDim; h++) batchGradW![h] += rawGrads[h];
                            // Hidden-layer backprop: accumulate dL/dWh and dL/dbh
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                if (hiddenAct![h] <= 0.0) continue; // ReLU gate
                                double dHidden = totalErr * weights[k][h];
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen; si2++)
                                    batchGradHW![rowOff + si2] += dHidden * sampleFeatures[subset[si2]] + hp.L2Lambda * hW![rowOff + si2];
                                batchGradHB![h] += dHidden;
                            }
                        }
                        else
                        {
                            foreach (int j in subset) batchGradW![j] += rawGrads[j];
                        }
                        batchGradB += bGrad;
                        if (useMagTask)
                        {
                            double sMag = hp.MagLossWeight * magHuberGradSample;
                            foreach (int j in subset) batchGradMagW![j] += sMag * sampleFeatures[j];
                            batchGradMagB += sMag;
                        }

                        // Apply accumulated gradients at batch boundary or end of epoch
                        bool isBatchEnd = (si + 1) % batchSize == 0 || si == trainingBootstrap.Count - 1;
                        if (!isBatchEnd) continue;

                        int actualBatch = (si % batchSize) + 1;
                        double invBatch = 1.0 / actualBatch;

                        double bc1    = 1.0 - beta1t;
                        double bc2    = 1.0 - beta2t;
                        double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                        // Direction head Adam update (averaged over batch)
                        if (useMlp)
                        {
                            // Output layer: weights indexed 0..hiddenDim-1
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double grad = batchGradW![h] * invBatch;
                                mW[h] = AdamBeta1 * mW[h] + (1 - AdamBeta1) * grad;
                                vW[h] = AdamBeta2 * vW[h] + (1 - AdamBeta2) * grad * grad;
                                weights[k][h] -= alphAt * mW[h] / (Math.Sqrt(vW[h]) + AdamEpsilon);
                            }
                            // Hidden layer backprop Adam update
                            for (int hi = 0; hi < hWLen; hi++)
                            {
                                double gH = batchGradHW![hi] * invBatch;
                                mHW![hi] = AdamBeta1 * mHW[hi] + (1 - AdamBeta1) * gH;
                                vHW![hi] = AdamBeta2 * vHW[hi] + (1 - AdamBeta2) * gH * gH;
                                hW![hi] -= alphAt * mHW[hi] / (Math.Sqrt(vHW[hi]) + AdamEpsilon);
                            }
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double gHB = batchGradHB![h] * invBatch;
                                mHB![h] = AdamBeta1 * mHB[h] + (1 - AdamBeta1) * gHB;
                                vHB![h] = AdamBeta2 * vHB[h] + (1 - AdamBeta2) * gHB * gHB;
                                hB![h] -= alphAt * mHB[h] / (Math.Sqrt(vHB[h]) + AdamEpsilon);
                            }
                        }
                        else
                        {
                            foreach (int j in subset)
                            {
                                double grad = batchGradW![j] * invBatch;
                                mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                                vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                                weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                            }
                        }
                        double bGradAvg = batchGradB * invBatch;
                        mB  = AdamBeta1 * mB + (1 - AdamBeta1) * bGradAvg;
                        vB  = AdamBeta2 * vB + (1 - AdamBeta2) * bGradAvg * bGradAvg;
                        bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                        // Magnitude head Adam update (averaged over batch)
                        if (useMagTask)
                        {
                            double magBAvg = batchGradMagB * invBatch;
                            mBmag = AdamBeta1 * mBmag + (1 - AdamBeta1) * magBAvg;
                            vBmag = AdamBeta2 * vBmag + (1 - AdamBeta2) * magBAvg * magBAvg;
                            magBiasesK![k] -= alphAt * mBmag / (Math.Sqrt(vBmag) + AdamEpsilon);
                            foreach (int j in subset)
                            {
                                double gm = batchGradMagW![j] * invBatch;
                                mWmag![j] = AdamBeta1 * mWmag[j] + (1 - AdamBeta1) * gm;
                                vWmag![j] = AdamBeta2 * vWmag[j] + (1 - AdamBeta2) * gm * gm;
                                magWeightsK![k][j] -= alphAt * mWmag[j] / (Math.Sqrt(vWmag[j]) + AdamEpsilon);
                            }
                        }

                        // Reset batch accumulators
                        if (useMlp)
                        {
                            Array.Clear(batchGradW!, 0, hiddenDim);
                            Array.Clear(batchGradHW!, 0, hWLen);
                            Array.Clear(batchGradHB!, 0, hiddenDim);
                        }
                        else
                        {
                            Array.Clear(batchGradW!, 0, effectiveDim);
                        }
                        batchGradB = 0.0;
                        if (batchGradMagW is not null) Array.Clear(batchGradMagW, 0, effectiveDim);
                        batchGradMagB = 0.0;
                    }
                    else
                    {
                        // ── Sample-by-sample Adam update (legacy path) ────────
                        double bc1    = 1.0 - beta1t;
                        double bc2    = 1.0 - beta2t;
                        double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                        if (useMlp)
                        {
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double grad = rawGrads[h];
                                mW[h] = AdamBeta1 * mW[h] + (1 - AdamBeta1) * grad;
                                vW[h] = AdamBeta2 * vW[h] + (1 - AdamBeta2) * grad * grad;
                                weights[k][h] -= alphAt * mW[h] / (Math.Sqrt(vW[h]) + AdamEpsilon);
                            }
                            // Hidden layer backprop: dL/dhidden[h] = totalErr * Wo[h] * ReLU'(preact)
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                if (hiddenAct![h] <= 0.0) continue; // ReLU gate
                                double dHidden = totalErr * weights[k][h]; // gradient through output
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen; si2++)
                                {
                                    double gH = dHidden * sampleFeatures[subset[si2]] + hp.L2Lambda * hW![rowOff + si2];
                                    mHW![rowOff + si2] = AdamBeta1 * mHW[rowOff + si2] + (1 - AdamBeta1) * gH;
                                    vHW![rowOff + si2] = AdamBeta2 * vHW[rowOff + si2] + (1 - AdamBeta2) * gH * gH;
                                    hW![rowOff + si2] -= alphAt * mHW[rowOff + si2] / (Math.Sqrt(vHW[rowOff + si2]) + AdamEpsilon);
                                }
                                // Hidden bias
                                mHB![h] = AdamBeta1 * mHB[h] + (1 - AdamBeta1) * dHidden;
                                vHB![h] = AdamBeta2 * vHB[h] + (1 - AdamBeta2) * dHidden * dHidden;
                                hB![h] -= alphAt * mHB[h] / (Math.Sqrt(vHB[h]) + AdamEpsilon);
                            }
                        }
                        else
                        {
                            foreach (int j in subset)
                            {
                                double grad = rawGrads[j];
                                mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                                vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                                weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                            }
                        }

                        mB  = AdamBeta1 * mB + (1 - AdamBeta1) * bGrad;
                        vB  = AdamBeta2 * vB + (1 - AdamBeta2) * bGrad * bGrad;
                        bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                        // Magnitude head
                        if (useMagTask)
                        {
                            double scaledMag = hp.MagLossWeight * magHuberGradSample;
                            mBmag = AdamBeta1 * mBmag + (1 - AdamBeta1) * scaledMag;
                            vBmag = AdamBeta2 * vBmag + (1 - AdamBeta2) * scaledMag * scaledMag;
                            magBiasesK![k] -= alphAt * mBmag / (Math.Sqrt(vBmag) + AdamEpsilon);
                            foreach (int j in subset)
                            {
                                double gm = scaledMag * sampleFeatures[j];
                                mWmag![j] = AdamBeta1 * mWmag[j] + (1 - AdamBeta1) * gm;
                                vWmag![j] = AdamBeta2 * vWmag[j] + (1 - AdamBeta2) * gm * gm;
                                magWeightsK![k][j] -= alphAt * mWmag[j] / (Math.Sqrt(vWmag[j]) + AdamEpsilon);
                            }
                        }
                    }

                    // ── L1 elastic-net proximal operator (soft thresholding) ───
                    if (hp.L1Lambda > 0.0)
                    {
                        double l1AlphAt = hp.L1Lambda * alpha;
                        for (int j2 = 0; j2 < outputDim; j2++)
                        {
                            double w = weights[k][j2];
                            weights[k][j2] = Math.Abs(w) <= l1AlphAt
                                ? 0.0
                                : w - Math.Sign(w) * l1AlphAt;
                        }
                    }

                    // ── Weight magnitude clipping ──────────────────────────────
                    if (hp.MaxWeightMagnitude > 0.0)
                    {
                        double wMax = hp.MaxWeightMagnitude;
                        for (int j2 = 0; j2 < outputDim; j2++)
                            weights[k][j2] = Math.Clamp(weights[k][j2], -wMax, wMax);
                        bias = Math.Clamp(bias, -wMax, wMax);
                        // Clip hidden layer weights too
                        if (useMlp)
                        {
                            for (int j2 = 0; j2 < hW!.Length; j2++)
                                hW[j2] = Math.Clamp(hW[j2], -wMax, wMax);
                            for (int h = 0; h < hiddenDim; h++)
                                hB![h] = Math.Clamp(hB![h], -wMax, wMax);
                        }
                    }

                    // ── NaN/Inf weight guard ───────────────────────────────────
                    // If any weight went non-finite (numerical instability), reset
                    // this learner to the best checkpoint and break the epoch loop.
                    bool hasNaN = !double.IsFinite(bias);
                    if (!hasNaN)
                    {
                        for (int j2 = 0; j2 < outputDim && !hasNaN; j2++)
                            if (!double.IsFinite(weights[k][j2])) hasNaN = true;
                    }
                    if (!hasNaN && useMlp)
                    {
                        for (int j2 = 0; j2 < hW!.Length && !hasNaN; j2++)
                            if (!double.IsFinite(hW[j2])) hasNaN = true;
                    }
                    if (hasNaN)
                    {
                        Array.Copy(bestW, weights[k], outputDim);
                        bias = bestB;
                        if (useMlp && bestHW is not null && bestHB is not null)
                        {
                            Array.Copy(bestHW, hW!, hW!.Length);
                            Array.Copy(bestHB, hB!, hB!.Length);
                        }
                        goto EndEpochLoop;
                    }
                }

                // Early stopping on validation loss (computed over subset features)
                double valLoss = ComputeLogLossSubset(valSet, weights[k], bias, subset, hp.LabelSmoothing,
                    isPolyLearner ? featureCount : -1, PolyTopN,
                    useMlp ? hW : null, useMlp ? hB : null, hiddenDim);
                // Use relative improvement threshold (0.1% of current loss or 0.001, whichever is smaller)
                // to avoid getting stuck when loss plateaus with sub-1e-6 fluctuations
                double lossThreshold = Math.Min(Math.Abs(bestValLoss) * 0.001, 0.001);
                if (valLoss < bestValLoss - lossThreshold)
                {
                    bestValLoss = valLoss;
                    bestW       = [..weights[k]];
                    bestB       = bias;
                    if (useMlp) { bestHW = (double[])hW!.Clone(); bestHB = (double[])hB!.Clone(); }
                    patience    = 0;
                }
                else if (++patience >= hp.EarlyStoppingPatience)
                {
                    // Always break on patience exhaustion — SWA will average the
                    // accumulated weights regardless. The previous logic skipped
                    // the break during the SWA phase, causing an infinite loop
                    // when validation loss plateaued.
                    break;
                }

                // ── Adaptive LR decay (rec 2) ──────────────────────────────────
                // Monitor val accuracy every 5 epochs; if it drops >5 % below peak,
                // decay the LR once by AdaptiveLrDecayFactor.
                if (!lrDecayed && hp.AdaptiveLrDecayFactor > 0.0 && epoch % 5 == 0)
                {
                    int correct = 0;
                    foreach (var sv in valSet)
                    {
                        double zv;
                        if (useMlp)
                        {
                            // MLP forward pass: hidden = ReLU(Wh × x_subset + bh), z = Wo · hidden + bias
                            zv = bias;
                            for (int h = 0; h < hiddenDim; h++)
                            {
                                double act = hB![h];
                                int rowOff = h * subsetLen;
                                for (int si2 = 0; si2 < subsetLen; si2++)
                                    act += hW![rowOff + si2] * sv.Features[subset[si2]];
                                double hidden = Math.Max(0.0, act); // ReLU
                                zv += weights[k][h] * hidden;
                            }
                        }
                        else
                        {
                            zv = bias;
                            foreach (int j in subset) zv += weights[k][j] * sv.Features[j];
                        }
                        if ((MLFeatureHelper.Sigmoid(zv) >= 0.5) == (sv.Direction == 1)) correct++;
                    }
                    double curAcc = valSet.Count > 0 ? (double)correct / valSet.Count : 0.0;
                    if (curAcc > peakValAcc) peakValAcc = curAcc;
                    else if (peakValAcc > 0.0 && curAcc < peakValAcc - 0.05)
                    {
                        lrScale  *= hp.AdaptiveLrDecayFactor;
                        lrDecayed = true;
                    }
                }

                // SWA accumulation (independent of early stopping)
                if (useSwa && epoch >= hp.SwaStartEpoch &&
                    (epoch - hp.SwaStartEpoch) % Math.Max(1, hp.SwaFrequency) == 0)
                {
                    for (int j = 0; j < outputDim; j++) swaW[j] += weights[k][j];
                    swaB += bias;
                    swaCount++;
                }

                EndEpochLoop:;
            }

            // ── SWA final: use average if it improves over early-stopped best ──
            if (useSwa && swaCount > 0 && !useMlp) // SWA disabled for MLP (hidden layer not averaged)
            {
                var swaAvgW = new double[outputDim];
                for (int j = 0; j < outputDim; j++) swaAvgW[j] = swaW[j] / swaCount;
                double swaAvgB = swaB / swaCount;
                double swaLoss = ComputeLogLossSubset(valSet, swaAvgW, swaAvgB, subset,
                    hp.LabelSmoothing, isPolyLearner ? featureCount : -1, PolyTopN,
                    useMlp ? hW : null, useMlp ? hB : null, hiddenDim);
                if (swaLoss <= bestValLoss)
                {
                    bestW = swaAvgW;
                    bestB = swaAvgB;
                }
            }

            weights[k] = bestW;
            biases[k]  = bestB;
            // Store MLP hidden layer weights (restored from best checkpoint)
            if (useMlp)
            {
                mlpHiddenW![k] = bestHW ?? hW!;
                mlpHiddenB![k] = bestHB ?? hB!;
            }

            // Return rented ArrayPool buffers
            pool.Return(mW);  pool.Return(vW);
            if (mHW is not null) pool.Return(mHW);
            if (vHW is not null) pool.Return(vHW);
            if (mHB is not null) pool.Return(mHB);
            if (vHB is not null) pool.Return(vHB);
            if (mWmag is not null) pool.Return(mWmag);
            if (vWmag is not null) pool.Return(vWmag);
        } // end TrainLearner

        // ── Dispatch: parallel when learners are independent and not inside nested Parallel.For ──
        if (learnersAreIndependent && !forceSequential)
        {
            Parallel.For(0, hp.K,
                new ParallelOptions { CancellationToken = ct },
                TrainLearner);
        }
        else
        {
            for (int k = 0; k < hp.K; k++)
            {
                ct.ThrowIfCancellationRequested();
                TrainLearner(k);
            }
        }

        // ── Ensemble weight diversity enforcement ─────────────────────────────
        // After all K learners are trained, check for redundant pairs (ρ > MaxLearnerCorrelation)
        // and re-initialise the redundant learner with a different seed, fine-tuning for 10 epochs.
        if (hp.MaxLearnerCorrelation < 1.0 && hp.K >= 2)
        {
            var diversityRng = new Random(42);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool foundViolation = false;
                for (int k1 = 0; k1 < hp.K && !foundViolation; k1++)
                {
                    for (int k2 = k1 + 1; k2 < hp.K && !foundViolation; k2++)
                    {
                        double rho = PearsonCorrelation(weights[k1], weights[k2], featureCount);
                        if (rho > hp.MaxLearnerCorrelation)
                        {
                            foundViolation = true;
                            // Pick the higher-indexed learner as the redundant one to re-init
                            int redundant = k2;
                            int other     = k1;
                            _logger.LogDebug(
                                "Diversity enforcement: learner {K} re-init (ρ={Rho:F3} with learner {Other}).",
                                redundant, rho, other);
                            int reinitSeed = redundant * 37 + 13;
                            ReinitLearner(redundant, weights, biases, trainSet, hp,
                                featureCount, new Random(reinitSeed), ct);
                        }
                    }
                }
                if (!foundViolation) break;
            }
        }

        // ── Average multi-task magnitude heads ────────────────────────────────
        double[]? avgMtMagWeights = null;
        double    avgMtMagBias    = 0.0;
        if (useMagTask && magWeightsK is { Length: > 0 })
        {
            avgMtMagWeights = new double[featureCount];
            for (int k = 0; k < hp.K; k++)
            {
                for (int j = 0; j < featureCount && j < magWeightsK[k].Length; j++)
                    avgMtMagWeights[j] += magWeightsK[k][j];
                avgMtMagBias += magBiasesK![k];
            }
            for (int j = 0; j < featureCount; j++)
                avgMtMagWeights[j] /= hp.K;
            avgMtMagBias /= hp.K;
        }

        return (weights, biases, featureSubsets, polyStart, avgMtMagWeights, avgMtMagBias, mlpHiddenW, mlpHiddenB);
    }

    // ── Magnitude regressor ───────────────────────────────────────────────────

    /// <summary>
    /// Fits a Huber-loss linear regressor for ATR-normalised magnitude prediction.
    /// Uses the same Adam + cosine-annealing + early-stopping treatment as the direction
    /// learners so the magnitude head quality matches the direction head quality.
    /// Falls back to a lightweight two-pass SGD when the training set is too small to
    /// hold out a validation split (fewer than 30 samples).
    /// </summary>
    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  featureCount,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var w    = new double[featureCount];
        double b = 0.0;

        // ── Validation split for early stopping ──────────────────────────────
        // Mirror the direction learners: hold out 10 % of samples (min 5) as a
        // val set. Skip early stopping when the set is too small to split safely.
        bool   canEarlyStop = train.Count >= 30;
        int    valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var    valSet       = canEarlyStop ? train[^valSize..] : train;
        var    trainSet     = canEarlyStop ? train[..^valSize] : train;

        if (trainSet.Count == 0)   // degenerate edge case
            return (w, b);

        // ── Adam state ────────────────────────────────────────────────────────
        var    mW     = new double[featureCount];
        var    vW     = new double[featureCount];
        double mB     = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int    t      = 0;

        double bestValLoss = double.MaxValue;
        var    bestW       = new double[featureCount];
        double bestB       = 0.0;
        int    patience    = 0;

        int    epochs = hp.MaxEpochs;
        double baseLr = hp.LearningRate;
        double l2     = hp.L2Lambda;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            // Cosine-annealing LR — matches the direction learner schedule exactly
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            int regSi = 0;
            foreach (var s in trainSet)
            {
                // Dense cancellation check every 2000 samples
                if (++regSi % 2000 == 0) ct.ThrowIfCancellationRequested();

                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                // Forward pass
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;

                // Huber loss gradient: L2 region when |err| ≤ 1, L1 region otherwise.
                // More robust to outlier magnitude values than plain MSE, and matches
                // the Huber gradient already used by the multi-task magnitude head.
                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);

                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                // Bias Adam step
                mB  = AdamBeta1 * mB  + (1.0 - AdamBeta1) * huberGrad;
                vB  = AdamBeta2 * vB  + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b  -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                // Weight Adam step + L2
                for (int j = 0; j < featureCount; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j]  = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j]  = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j]  -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;

            // ── Validation Huber loss ─────────────────────────────────────────
            double valLoss = 0.0;
            int    valN    = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
                valN++;
            }
            if (valN > 0) valLoss /= valN;
            else          valLoss  = double.MaxValue;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, featureCount);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= hp.EarlyStoppingPatience)
            {
                break;
            }
        }

        if (canEarlyStop)
        {
            w = bestW;
            b = bestB;
        }

        return (w, b);
    }

    // ── Platt scaling ─────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        // Pre-compute logits and labels once — ensemble weights are frozen before Platt fitting.
        // Avoids calling EnsembleProb (O(K×F) per sample) on every epoch of the 200-epoch SGD loop.
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            logits[i]  = MLFeatureHelper.Logit(raw);
            labels[i]  = calSet[i].Direction > 0 ? 1.0 : 0.0;
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

    // ── Stacking meta-learner ─────────────────────────────────────────────────

    /// <summary>
    /// Trains a logistic meta-learner that maps per-base-learner probabilities to a final
    /// probability. Fitted on the calibration set (which base learners never saw).
    /// When the cal set is too small, falls back to returning <see cref="MetaLearner.None"/>.
    /// </summary>
    private static MetaLearner FitMetaLearner(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        int K = weights.Length;
        if (calSet.Count < 20 || K < 2) return MetaLearner.None;

        // Pre-compute per-learner probabilities and labels once — ensemble weights are frozen.
        // Avoids calling GetLearnerProbs (O(K×F) per sample) on every epoch of the 300-epoch loop.
        int n = calSet.Count;
        var calLp     = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLp[i]     = GetLearnerProbs(calSet[i].Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        var mw = new double[K];
        for (int k = 0; k < K; k++) mw[k] = 1.0 / K;   // uniform init
        double mb = 0.0;

        const double lr     = 0.01;
        const int    epochs = 300;

        var dW = new double[K]; // pre-allocated — zeroed each epoch instead of re-allocated
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            Array.Clear(dW, 0, K);
            double dB = 0;

            for (int i = 0; i < n; i++)
            {
                var    lp  = calLp[i];
                double z   = mb;
                for (int k = 0; k < K; k++) z += mw[k] * lp[k];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - calLabels[i];
                for (int k = 0; k < K; k++) dW[k] += err * lp[k];
                dB += err;
            }

            for (int k = 0; k < K; k++) mw[k] -= lr * dW[k] / n;
            mb -= lr * dB / n;
        }

        return new MetaLearner(mw, mb);
    }

    // ── Evaluation ───────────────────────────────────────────────────────────

    private static EvalMetrics EvaluateEnsemble(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double sumMagSqErr = 0, sumBrier = 0, sumEV = 0;

        // Use ArrayPool to avoid a heap allocation proportional to testSet.Count.
        int    n           = testSet.Count;
        double[] retBuf    = ArrayPool<double>.Shared.Rent(n);
        int      retCount  = 0;
        try
        {
            foreach (var s in testSet)
            {
                double rawProb   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                rawProb          = Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
                double calibP    = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProb) + plattB);
                bool predictedUp = calibP >= 0.5;
                bool actualUp    = s.Direction == 1;
                bool correct     = predictedUp == actualUp;

                double y = actualUp ? 1.0 : 0.0;
                sumBrier += (calibP - y) * (calibP - y);

                double magPred = MLFeatureHelper.DotProduct(magWeights, s.Features) + magBias;
                double magErr  = magPred - s.Magnitude;
                sumMagSqErr += magErr * magErr;

                double edge = calibP - 0.5;
                sumEV += (correct ? 1 : -1) * Math.Abs(edge) * Math.Abs(s.Magnitude);

                int predDir = predictedUp ? 1 : -1;
                int actDir  = actualUp    ? 1 : -1;
                retBuf[retCount++] = predDir * actDir * Math.Abs(s.Magnitude);

                if (correct && predictedUp)        tp++;
                else if (!correct && predictedUp)  fp++;
                else if (!correct && !predictedUp) fn++;
                else                               tn++;
            }

            double accuracy  = (tp + tn) / (double)n;
            double precision = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0;
            double recall    = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0;
            double f1        = (precision + recall) > 0
                               ? 2 * precision * recall / (precision + recall) : 0;
            double wAcc      = WeightedAccuracy(testSet, weights, biases, plattA, plattB, featureCount, subsets, meta, mlp);

            return new EvalMetrics(
                Accuracy:         accuracy,
                Precision:        precision,
                Recall:           recall,
                F1:               f1,
                MagnitudeRmse:    Math.Sqrt(sumMagSqErr / n),
                ExpectedValue:    sumEV / n,
                BrierScore:       sumBrier / n,
                WeightedAccuracy: wAcc,
                SharpeRatio:      ComputeSharpe(retBuf, retCount),
                TP: tp, FP: fp, FN: fn, TN: tn);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(retBuf);
        }
    }

    // ── ECE (Expected Calibration Error) ──────────────────────────────────────

    /// <summary>
    /// Measures how well the calibrated probability outputs match actual positive-class
    /// frequencies. Uses 10 equal-width bins over [0, 1].
    /// ECE = Σ_b |freq_positive(b) − avg_conf(b)| × n_b / n.
    /// </summary>
    internal static double ComputeEce(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count < 20) return 0.5;

        const int NumBins = 10;
        var binConfSum   = new double[NumBins];
        var binPositive  = new int[NumBins];
        var binCount     = new int[NumBins];

        foreach (var s in testSet)
        {
            double raw  = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            raw         = Math.Clamp(raw, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double p    = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            int    bin  = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);

            binConfSum[bin] += p;
            if (s.Direction == 1) binPositive[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf    = binConfSum[b] / binCount[b];
            double posFreq    = binPositive[b] / (double)binCount[b];
            ece += Math.Abs(posFreq - avgConf) * binCount[b] / n;
        }

        return ece;
    }

    // ── EV-optimal decision threshold ─────────────────────────────────────────

    /// <summary>
    /// Sweeps decision thresholds in steps of 0.01 and returns the threshold that
    /// maximises mean expected value (signed magnitude-weighted accuracy).
    /// The search range is configurable via <paramref name="searchMin"/>/<paramref name="searchMax"/>.
    /// </summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta      = default,
        int                  searchMin = 30,
        int                  searchMax = 75,
        MlpState             mlp       = default)
    {
        if (dataSet.Count < 30) return 0.5;

        // Pre-compute calibrated probabilities — plain loop avoids LINQ Select+ToArray.
        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
        {
            double raw = EnsembleProb(dataSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            probs[i]   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
        }

        double bestEv        = double.MinValue;
        double bestThreshold = 0.5;

        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;

            for (int i = 0; i < dataSet.Count; i++)
            {
                bool predictedUp = probs[i] >= t;
                bool actualUp    = dataSet[i].Direction == 1;
                bool correct     = predictedUp == actualUp;
                ev += (correct ? 1 : -1) * Math.Abs(dataSet[i].Magnitude);
            }
            ev /= dataSet.Count;

            if (ev > bestEv)
            {
                bestEv        = ev;
                bestThreshold = t;
            }
        }

        return bestThreshold;
    }

    // ── Feature subsampling ────────────────────────────────────────────────────

    /// <summary>
    /// Samples <c>⌈ratio × featureCount⌉</c> feature indices without replacement.
    /// Indices are sorted for deterministic access order.
    /// </summary>
    private static int[] GenerateFeatureSubset(int featureCount, double ratio, int seed)
    {
        int subCount = Math.Max(1, (int)Math.Ceiling(ratio * featureCount));
        var rng      = new Random(seed);

        // Partial Fisher-Yates: O(subCount) vs the original O(F log F) double-sort via LINQ.
        // Only the first subCount positions are shuffled; the rest are discarded.
        var indices = new int[featureCount];
        for (int i = 0; i < featureCount; i++) indices[i] = i;
        for (int i = 0; i < subCount; i++)
        {
            int j = i + rng.Next(featureCount - i);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var result = indices[..subCount];
        Array.Sort(result); // sort for deterministic cache-friendly access order
        return result;
    }

    // ── Sharpe ratio ──────────────────────────────────────────────────────────

    /// <summary>Computes Sharpe ratio over the first <paramref name="count"/> entries of a buffer.</summary>
    private static double ComputeSharpe(double[] buffer, int count)
    {
        if (count < 2) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += buffer[i];
        double mean = sum / count;
        double varSum = 0;
        for (int i = 0; i < count; i++) { double d = buffer[i] - mean; varSum += d * d; }
        double std = Math.Sqrt(varSum / (count - 1));
        return std < 1e-10 ? 0 : mean / std * Math.Sqrt(252);
    }

    // ── Temporal weighting ────────────────────────────────────────────────────

    public static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count == 0) return [];
        var w = new double[count];
        for (int i = 0; i < count; i++)
            w[i] = Math.Exp(lambda * ((double)i / Math.Max(1, count - 1) - 1.0));
        double sum = w.Sum();
        for (int i = 0; i < count; i++)
            w[i] /= sum;
        return w;
    }

    // ── Stratified biased bootstrap ───────────────────────────────────────────

    private static List<TrainingSample> StratifiedBiasedBootstrap(
        List<TrainingSample> source,
        double[]             temporalWeights,
        int                  n,
        int                  seed)
    {
        var rng = new Random(seed);

        // Build pos/neg index lists with plain loops — avoids LINQ Select+Where allocation chain.
        var posIdx = new List<(TrainingSample s, int i)>();
        var negIdx = new List<(TrainingSample s, int i)>();
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Direction > 0) posIdx.Add((source[i], i));
            else                         negIdx.Add((source[i], i));
        }

        if (posIdx.Count < 5 || negIdx.Count < 5)
            return BiasedBootstrap(source, temporalWeights, n, seed);

        // Build CDF weight arrays with plain loops instead of LINQ Select+ToArray.
        var posW = new double[posIdx.Count];
        for (int i = 0; i < posIdx.Count; i++) posW[i] = temporalWeights[posIdx[i].i];
        var negW = new double[negIdx.Count];
        for (int i = 0; i < negIdx.Count; i++) negW[i] = temporalWeights[negIdx[i].i];

        double[] posCdf = BuildNormalisedCdf(posW);
        double[] negCdf = BuildNormalisedCdf(negW);

        int halfN  = n / 2;
        var result = new List<TrainingSample>(n);

        for (int i = 0; i < halfN; i++)
            result.Add(posIdx[SampleFromCdf(posCdf, rng)].s);
        for (int i = 0; i < n - halfN; i++)
            result.Add(negIdx[SampleFromCdf(negCdf, rng)].s);

        // Fisher-Yates shuffle
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (result[j], result[i]) = (result[i], result[j]);
        }

        return result;
    }

    private static List<TrainingSample> BiasedBootstrap(
        List<TrainingSample> source,
        double[]             weights,
        int                  n,
        int                  seed)
    {
        var rng    = new Random(seed);
        var result = new List<TrainingSample>(n);
        var cdf    = BuildNormalisedCdf(weights);

        for (int i = 0; i < n; i++)
            result.Add(source[SampleFromCdf(cdf, rng)]);

        return result;
    }

    private static double[] BuildNormalisedCdf(double[] weights)
    {
        double sum = weights.Sum();
        var cdf    = new double[weights.Length];
        if (sum <= 0)
        {
            // Degenerate case: all weights are zero — fall back to a uniform distribution
            // so bootstrap sampling remains random rather than always returning index 0.
            for (int i = 0; i < cdf.Length; i++) cdf[i] = (i + 1.0) / cdf.Length;
            return cdf;
        }
        cdf[0] = weights[0] / sum;
        for (int i = 1; i < weights.Length; i++)
            cdf[i] = cdf[i - 1] + weights[i] / sum;
        return cdf;
    }

    private static int SampleFromCdf(double[] cdf, Random rng)
    {
        double u   = rng.NextDouble();
        int    idx = Array.BinarySearch(cdf, u);
        if (idx < 0) idx = ~idx;
        return Math.Clamp(idx, 0, cdf.Length - 1);
    }

    // ── Feature pruning ───────────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int featureCount)
    {
        if (threshold <= 0.0 || featureCount == 0)
        {
            var allTrue = new bool[featureCount];
            Array.Fill(allTrue, true);
            return allTrue;
        }

        double minImportance = threshold / featureCount;
        var mask = new bool[featureCount];
        for (int j = 0; j < featureCount; j++)
            mask[j] = importance[j] >= minImportance;

        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        return samples.Select(s =>
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            return s with { Features = f };
        }).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the per-learner sigmoid probabilities (length = K).
    /// When <paramref name="mlpHiddenW"/> is non-null, uses MLP forward pass
    /// (hidden = ReLU(Wh × x + bh), z = Wo · hidden + bias) instead of linear logistic.
    /// </summary>
    private static double[] GetLearnerProbs(
        float[]      features,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets,
        double[][]?  mlpHiddenW = null,
        double[][]?  mlpHiddenB = null,
        int          mlpHiddenDim = 0)
    {
        const int PolyTopN = 5;
        bool useMlp = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        var probs = new double[weights.Length];
        for (int k = 0; k < weights.Length; k++)
        {
            // Detect poly learner by weight vector length (only for linear mode)
            bool isPolyLearner = !useMlp && weights[k].Length > featureCount;
            float[] kFeatures  = isPolyLearner
                ? AugmentWithPolyFeatures(features, featureCount, PolyTopN)
                : features;

            // MLP forward pass when hidden weights are available for this learner
            if (useMlp && k < mlpHiddenW!.Length && mlpHiddenW[k] is not null &&
                k < mlpHiddenB!.Length && mlpHiddenB[k] is not null)
            {
                var hW = mlpHiddenW[k];
                var hB = mlpHiddenB[k];
                int[] subset = subsets?.Length > k ? subsets[k] : [];
                int subLen = subset.Length > 0 ? subset.Length : featureCount;
                // If no subset stored, build a full-range index for contiguous access
                if (subset.Length == 0)
                {
                    subset = new int[featureCount];
                    for (int j = 0; j < featureCount; j++) subset[j] = j;
                    subLen = featureCount;
                }

                double z = biases[k];
                for (int h = 0; h < mlpHiddenDim; h++)
                {
                    double act = hB[h];
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen && rowOff + si < hW.Length; si++)
                        act += hW[rowOff + si] * kFeatures[subset[si]];
                    double hidden = Math.Max(0.0, act); // ReLU
                    if (h < weights[k].Length)
                        z += weights[k][h] * hidden;
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
            }
            else
            {
                // Linear logistic forward pass
                int kDim = weights[k].Length;
                double z = biases[k];
                if (subsets is not null && k < subsets.Length)
                {
                    foreach (int j in subsets[k])
                        z += weights[k][j] * kFeatures[j];
                }
                else
                {
                    for (int j = 0; j < kDim; j++)
                        z += weights[k][j] * kFeatures[j];
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
            }
        }
        return probs;
    }

    /// <summary>
    /// Computes the ensemble probability.
    /// When <paramref name="meta"/> is active, applies the stacking meta-learner over per-learner
    /// probabilities instead of simple averaging.
    /// </summary>
    internal static double EnsembleProb(
        float[]      features,
        double[][]   weights,
        double[]     biases,
        int          featureCount,
        int[][]?     subsets       = null,
        MetaLearner  meta          = default,
        double[][]?  mlpHiddenW    = null,
        double[][]?  mlpHiddenB    = null,
        int          mlpHiddenDim  = 0)
    {
        var lp = GetLearnerProbs(features, weights, biases, featureCount, subsets,
            mlpHiddenW, mlpHiddenB, mlpHiddenDim);

        if (meta.IsActive)
        {
            double z = meta.Bias;
            for (int k = 0; k < meta.Weights.Length && k < lp.Length; k++)
                z += meta.Weights[k] * lp[k];
            return MLFeatureHelper.Sigmoid(z);
        }

        return lp.Average();
    }

    /// <summary>Log-loss for a single learner using only the specified feature subset.
    /// Pass <paramref name="baseFeatureCount"/> >= 0 to enable poly feature augmentation.
    /// When <paramref name="mlpHiddenW"/> is non-null, uses MLP forward pass.</summary>
    private static double ComputeLogLossSubset(
        List<TrainingSample> set,
        double[]             w,
        double               b,
        int[]                subset,
        double               labelSmoothing   = 0.0,
        int                  baseFeatureCount = -1,
        int                  polyTopN         = 5,
        double[]?            mlpHiddenW       = null,
        double[]?            mlpHiddenB       = null,
        int                  mlpHiddenDim     = 0)
    {
        if (set.Count == 0) return double.MaxValue;
        bool augment = baseFeatureCount >= 0;
        bool useMlp  = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        int  subsetLen = subset.Length;
        double loss = 0;
        foreach (var s in set)
        {
            float[] features = augment
                ? AugmentWithPolyFeatures(s.Features, baseFeatureCount, polyTopN)
                : s.Features;
            double z;
            if (useMlp)
            {
                z = b;
                for (int h = 0; h < mlpHiddenDim; h++)
                {
                    double act = mlpHiddenB![h];
                    int rowOff = h * subsetLen;
                    for (int si = 0; si < subsetLen; si++)
                        act += mlpHiddenW![rowOff + si] * features[subset[si]];
                    double hidden = Math.Max(0.0, act); // ReLU
                    if (h < w.Length) z += w[h] * hidden;
                }
            }
            else
            {
                z = b;
                foreach (int j in subset)
                    z += w[j] * features[j];
            }
            double p = MLFeatureHelper.Sigmoid(z);
            double y = s.Direction > 0 ? 1.0 - labelSmoothing : labelSmoothing;
            loss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
        }
        return loss / set.Count;
    }

    private static double WeightedAccuracy(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        int n = testSet.Count;
        if (n == 0) return 0;
        double weightSum = 0, correctSum = 0;
        for (int i = 0; i < n; i++)
        {
            double wt     = 1.0 + (double)i / n;
            var    s      = testSet[i];
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP          = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            weightSum  += wt;
            correctSum += (calibP >= 0.5) == (s.Direction == 1) ? wt : 0;
        }
        return correctSum / weightSum;
    }

    private static double StdDev(IEnumerable<double> values, double mean)
    {
        var list = values as IList<double> ?? [..values];
        if (list.Count < 2) return 0;
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }

    // ── Permutation importance ────────────────────────────────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default,
        CancellationToken    ct   = default)
    {
        double baseline   = ComputeAccuracy(testSet, weights, biases, plattA, plattB, featureCount, subsets, meta, mlp);
        var    importance = new float[featureCount];

        // Each feature's shuffle-and-evaluate is independent — run in parallel.
        // Each feature gets its own seeded Random so results are deterministic.
        int tn = testSet.Count;
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 13 + 42);
            // Plain loop to extract column — avoids LINQ Select+ToArray.
            var vals = new float[tn];
            for (int i = 0; i < tn; i++) vals[i] = testSet[i].Features[j];
            for (int i = tn - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            // Score using thread-local scratch buffer — avoids cloning full feature array per sample.
            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < tn; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j]   = vals[idx];
                double rawP   = EnsembleProb(scratch, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
                if ((calibP >= 0.5) == (testSet[idx].Direction == 1)) correct++;
            }
            double shuffledAcc = (double)correct / tn;
            importance[j] = (float)Math.Max(0, baseline - shuffledAcc);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    private static double ComputeAccuracy(
        List<TrainingSample> set,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (set.Count == 0) return 0;
        int correct = 0;
        foreach (var s in set)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            if ((calibP >= 0.5) == (s.Direction == 1)) correct++;
        }
        return (double)correct / set.Count;
    }

    // ── Box-Muller Gaussian sampler ───────────────────────────────────────────

    private static double SampleGaussian(Random rng, double sigma)
    {
        // Box-Muller transform: two uniform samples → standard normal
        double u1 = Math.Max(1e-10, rng.NextDouble());
        double u2 = rng.NextDouble();
        double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return sigma * z;
    }

    // ── Bootstrap membership set ──────────────────────────────────────────────

    /// <summary>
    /// Returns the set of unique source indices sampled by
    /// <see cref="StratifiedBiasedBootstrap"/> for learner <paramref name="seed"/>.
    /// Mirrors the bootstrap sampling logic exactly so OOB sets are consistent.
    /// </summary>
    private static HashSet<int> GenerateBootstrapInSet(
        List<TrainingSample> source,
        double[]             temporalWeights,
        int                  n,
        int                  seed)
    {
        var rng   = new Random(seed);
        // Build pos/neg index lists with plain loops — avoids LINQ Select+Where allocation chain.
        var posIdx = new List<(TrainingSample s, int i)>();
        var negIdx = new List<(TrainingSample s, int i)>();
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Direction > 0) posIdx.Add((source[i], i));
            else                         negIdx.Add((source[i], i));
        }
        var inSet = new HashSet<int>();

        if (posIdx.Count < 5 || negIdx.Count < 5)
        {
            var cdf = BuildNormalisedCdf(temporalWeights);
            for (int i = 0; i < n; i++) inSet.Add(SampleFromCdf(cdf, rng));
            return inSet;
        }

        // Build CDF weight arrays with plain loops.
        var posW = new double[posIdx.Count];
        for (int i = 0; i < posIdx.Count; i++) posW[i] = temporalWeights[posIdx[i].i];
        var negW = new double[negIdx.Count];
        for (int i = 0; i < negIdx.Count; i++) negW[i] = temporalWeights[negIdx[i].i];

        double[] posCdf = BuildNormalisedCdf(posW);
        double[] negCdf = BuildNormalisedCdf(negW);

        int halfN = n / 2;
        for (int i = 0; i < halfN;     i++) inSet.Add(posIdx[SampleFromCdf(posCdf, rng)].i);
        for (int i = 0; i < n - halfN; i++) inSet.Add(negIdx[SampleFromCdf(negCdf, rng)].i);
        return inSet;
    }

    // ── OOB accuracy estimation ───────────────────────────────────────────────

    /// <summary>
    /// Estimates out-of-bag accuracy by averaging predictions from learners that
    /// did not include each training sample in their bootstrap (~37 % of samples per learner).
    /// </summary>
    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             featureSubsets,
        int                  K,
        MlpState             mlp = default)
    {
        if (trainSet.Count < 20) return 0.0;

        var inSets = new HashSet<int>[K];
        for (int k = 0; k < K; k++)
            inSets[k] = GenerateBootstrapInSet(
                trainSet, temporalWeights, trainSet.Count, seed: k * 31 + 7);

        int oobCorrect = 0, oobTotal = 0;

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Use GetLearnerProbs to handle both linear and MLP forward passes
            var lp = GetLearnerProbs(trainSet[i].Features, weights, biases, featureCount, featureSubsets,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

            double pSum  = 0.0;
            int    count = 0;

            for (int k = 0; k < K; k++)
            {
                if (inSets[k].Contains(i)) continue;
                pSum += lp[k];
                count++;
            }

            if (count == 0) continue;
            if (pSum / count >= 0.5 == (trainSet[i].Direction == 1)) oobCorrect++;
            oobTotal++;
        }

        return oobTotal > 0 ? (double)oobCorrect / oobTotal : 0.0;
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    /// <summary>
    /// Fits a monotone isotonic regression using the Pool Adjacent Violators Algorithm (PAVA)
    /// over Platt-calibrated probabilities vs. binary outcomes on the calibration set.
    /// Returns interleaved breakpoints [x₀,y₀,x₁,y₁,…] in ascending probability order.
    /// </summary>
    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count < 10) return [];

        // Build and sort pairs without LINQ Select+OrderBy overhead.
        int cn = calSet.Count;
        var pairs = new (double P, double Y)[cn];
        for (int i = 0; i < cn; i++)
        {
            double raw = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double p   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            pairs[i]   = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Stack-based PAVA: each entry is (sumY, sumP, count)
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
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

        // Interleaved [x₀,y₀,x₁,y₁,...] — one breakpoint per PAVA block
        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    /// <summary>
    /// Applies isotonic calibration via linear interpolation over the PAVA breakpoints.
    /// Returns <paramref name="p"/> unchanged when fewer than 4 breakpoint values exist.
    /// </summary>
    internal static double ApplyIsotonicCalibration(double p, double[] breakpoints)
    {
        if (breakpoints.Length < 4) return p;

        int nPoints = breakpoints.Length / 2;
        if (p <= breakpoints[0])                  return breakpoints[1];
        if (p >= breakpoints[(nPoints - 1) * 2])  return breakpoints[(nPoints - 1) * 2 + 1];

        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (breakpoints[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }

        double x0 = breakpoints[lo * 2],       y0 = breakpoints[lo * 2 + 1];
        double x1 = breakpoints[(lo + 1) * 2], y1 = breakpoints[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15
            ? (y0 + y1) / 2.0
            : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    // ── Split conformal prediction ─────────────────────────────────────────────

    /// <summary>
    /// Computes the split-conformal quantile <c>qHat</c> at coverage level 1−α (default 90%).
    /// Nonconformity score: <c>1−p</c> for Buy labels, <c>p</c> for Sell labels,
    /// where <c>p</c> is the Platt+isotonic calibrated probability.
    /// qHat = empirical ⌈(n+1)(1−α)⌉/n quantile.
    /// </summary>
    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta  = default,
        MlpState             mlp   = default,
        double               alpha = 0.10)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double raw = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            double p   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            if (isotonicBp.Length >= 4)
                p = ApplyIsotonicCalibration(p, isotonicBp);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }

        scores.Sort();
        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ── Stationarity gate helper ───────────────────────────────────────────────

    /// <summary>
    /// Counts how many features (columns) fail the ADF stationarity test at p > 0.05.
    /// </summary>
    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        int nonStationary = 0;
        int ns = samples.Count;
        var values = new double[ns]; // reuse buffer across features
        for (int j = 0; j < featureCount; j++)
        {
            for (int i = 0; i < ns; i++) values[i] = samples[i].Features[j];
            double pValue = MLFeatureHelper.AdfTest(values, maxLags: 4);
            if (pValue > 0.05) nonStationary++;
        }
        return nonStationary;
    }

    // ── Equity-curve gate helper ───────────────────────────────────────────────

    /// <summary>
    /// Computes max peak-to-trough drawdown and Sharpe ratio from a sequence of unit trade P&amp;L.
    /// Each prediction contributes +1 (correct) or -1 (incorrect) to the running P&amp;L.
    /// </summary>
    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0.0, 0.0);

        var returns = new double[predictions.Length];
        double equity = 0.0;
        double peak   = 0.0;
        double maxDD  = 0.0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == predictions[i].Actual ? +1.0 : -1.0;
            returns[i] = ret;
            equity    += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0.0;
            if (dd > maxDD) maxDD = dd;
        }

        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean));
        double std = returns.Length > 1 ? Math.Sqrt(variance / (returns.Length - 1)) : 0.0;
        double sharpe = std < 1e-10 ? 0.0 : mean / std;

        return (maxDD, sharpe);
    }

    // ── Polynomial feature augmentation ───────────────────────────────────────

    /// <summary>
    /// Augments the feature array with pairwise products of the top <paramref name="topN"/> features.
    /// Returns a new array of length <paramref name="baseFeatureCount"/> + topN*(topN-1)/2.
    /// </summary>
    private static float[] AugmentWithPolyFeatures(float[] features, int baseFeatureCount, int topN)
    {
        int actualTop = Math.Min(topN, baseFeatureCount);
        int pairCount = actualTop * (actualTop - 1) / 2;
        var result    = new float[baseFeatureCount + pairCount];

        // Copy base features
        Array.Copy(features, result, baseFeatureCount);

        // Append pairwise products
        int idx = baseFeatureCount;
        for (int i = 0; i < actualTop; i++)
            for (int j = i + 1; j < actualTop; j++)
                result[idx++] = features[i] * features[j];

        return result;
    }

    // ── Meta-label secondary classifier ───────────────────────────────────────

    /// <summary>
    /// Trains a simple logistic regression on meta-features: [ensP, ensStd, raw features 0..4].
    /// Labels: 1 if the ensemble prediction was correct for that calibration sample, else 0.
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        const int MetaFeatureDim = 7; // ensP + ensStd + 5 raw features
        const int Epochs         = 30;
        const double Lr          = 0.01;
        const double L2          = 0.001;

        if (calSet.Count < 10)
            return (new double[MetaFeatureDim], 0.0);

        int K     = weights.Length;
        var mw    = new double[MetaFeatureDim];
        double mb = 0.0;

        // Hoist allocations: dW reused each epoch, metaF reused each sample.
        var dW    = new double[MetaFeatureDim];
        var metaF = new double[MetaFeatureDim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, MetaFeatureDim);

            foreach (var s in calSet)
            {
                // Compute per-learner probs
                var lp      = GetLearnerProbs(s.Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                double ensP = lp.Average();

                // Ensemble std (plain loop — avoids LINQ Sum allocation)
                double variance = 0.0;
                for (int ki = 0; ki < lp.Length; ki++) { double d = lp[ki] - ensP; variance += d * d; }
                double ensStd   = K > 1 ? Math.Sqrt(variance / (K - 1)) : 0.0;

                // Build meta-features: [ensP, ensStd, feat[0..4]] — reuse pre-allocated array.
                metaF[0] = ensP;
                metaF[1] = ensStd;
                int rawTop = Math.Min(5, featureCount);
                for (int i = 0; i < rawTop; i++)
                    metaF[2 + i] = s.Features[i];

                // Label: 1 if ensemble prediction was correct
                int predicted = ensP >= 0.5 ? 1 : -1;
                int actual    = s.Direction > 0 ? 1 : -1;
                double label  = predicted == actual ? 1.0 : 0.0;

                // Forward pass
                double z = mb;
                for (int i = 0; i < MetaFeatureDim; i++) z += mw[i] * metaF[i];
                double pred = MLFeatureHelper.Sigmoid(z);
                double err  = pred - label;

                for (int i = 0; i < MetaFeatureDim; i++) dW[i] += err * metaF[i];
                dB += err;
            }

            int n = calSet.Count;
            for (int i = 0; i < MetaFeatureDim; i++)
                mw[i] -= Lr * (dW[i] / n + L2 * mw[i]);
            mb -= Lr * dB / n;
        }

        return (mw, mb);
    }

    // ── Jackknife+ residuals ───────────────────────────────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals: r_i = |trueLabel - oobP| for each training sample.
    /// Reuses the same bootstrap membership logic as OOB accuracy.
    /// Returns residuals sorted in ascending order.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             featureSubsets,
        int                  K,
        int                  polyStart,
        MlpState             mlp = default)
    {
        if (trainSet.Count < 20) return [];

        var inSets = new HashSet<int>[K];
        for (int k = 0; k < K; k++)
            inSets[k] = GenerateBootstrapInSet(
                trainSet, temporalWeights, trainSet.Count, seed: k * 31 + 7);

        var residuals = new List<double>(trainSet.Count);

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Use GetLearnerProbs to handle both linear and MLP forward passes
            var lp = GetLearnerProbs(trainSet[i].Features, weights, biases, featureCount, featureSubsets,
                mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

            double pSum  = 0.0;
            int    count = 0;

            for (int k = 0; k < K; k++)
            {
                if (inSets[k].Contains(i)) continue;
                pSum += lp[k];
                count++;
            }

            if (count == 0) continue;

            double oobP    = pSum / count;
            double trueLabel = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - oobP));
        }

        residuals.Sort();
        return [..residuals];
    }

    // ── Label noise correction ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a soft downweight factor in [0, 1] for a training sample's gradient.
    /// When <paramref name="ensP"/> indicates the ensemble is very confident that the label
    /// is wrong (P(correct) &lt; threshold), the gradient is scaled down proportionally.
    /// Returns 1.0 (no downweight) when threshold is 0 or P(correct) >= threshold.
    /// </summary>
    private static double ComputeNoiseCorrectionWeight(double ensP, int label, double threshold)
    {
        if (threshold <= 0.0) return 1.0;

        // P(correct label): for label=1 (Buy), P(correct) = ensP; for label=0 (Sell), P(correct) = 1-ensP
        double pCorrect = label == 1 ? ensP : 1.0 - ensP;
        if (pCorrect >= threshold) return 1.0;

        // Soft downweight: gradient × (P(correct) / threshold)
        return pCorrect / threshold;
    }

    // ── Pearson correlation between learner weight vectors ────────────────────

    /// <summary>
    /// Computes the Pearson correlation coefficient between two weight arrays
    /// using only the first <paramref name="len"/> elements.
    /// Returns 0.0 when either array has zero variance.
    /// </summary>
    private static double PearsonCorrelation(double[] a, double[] b, int len)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), len);
        if (n < 2) return 0.0;

        double sumA = 0, sumB = 0;
        for (int i = 0; i < n; i++) { sumA += a[i]; sumB += b[i]; }
        double meanA = sumA / n, meanB = sumB / n;

        double cov = 0, varA = 0, varB = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            cov  += da * db;
            varA += da * da;
            varB += db * db;
        }

        double denom = Math.Sqrt(varA * varB);
        return denom < 1e-15 ? 0.0 : cov / denom;
    }

    // ── Diversity enforcement: re-initialise a redundant learner ─────────────

    /// <summary>
    /// Re-initialises learner <paramref name="k"/> weights with a fresh random seed and
    /// fine-tunes for 10 epochs with Adam on the full (non-bootstrap) training set.
    /// The training set is expected to already be standardised (same transform as in <see cref="FitEnsemble"/>).
    /// </summary>
    private static void ReinitLearner(
        int                  k,
        double[][]           weights,
        double[]             biases,
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  featureCount,
        Random               rng,
        CancellationToken    ct)
    {
        const int FineTuneEpochs = 10;
        const int PolyTopN = 5;

        bool isPolyLearner = hp.PolyLearnerFraction > 0 && k >= (int)(hp.K * (1.0 - hp.PolyLearnerFraction));
        int polyPairCount   = PolyTopN * (PolyTopN - 1) / 2;
        int effectiveDim    = isPolyLearner ? featureCount + polyPairCount : featureCount;

        // Re-initialise weights to zero
        weights[k] = new double[effectiveDim];
        biases[k]  = 0.0;

        // Adam moments
        var mW  = new double[effectiveDim];
        var vW  = new double[effectiveDim];
        double mB = 0, vB = 0;
        int t = 0;
        double beta1t = 1.0; // running product: AdamBeta1^t — avoids Math.Pow per step
        double beta2t = 1.0; // running product: AdamBeta2^t

        double bias = 0.0;

        for (int epoch = 0; epoch < FineTuneEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double alpha = hp.LearningRate * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / FineTuneEpochs));

            foreach (var sample in trainSet)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;
                double y = sample.Direction > 0 ? 1.0 - hp.LabelSmoothing : hp.LabelSmoothing;

                float[] sampleFeatures = isPolyLearner
                    ? AugmentWithPolyFeatures(sample.Features, featureCount, PolyTopN)
                    : sample.Features;

                double z = bias;
                for (int j = 0; j < effectiveDim; j++)
                    z += weights[k][j] * sampleFeatures[j];

                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - y;

                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                for (int j = 0; j < effectiveDim; j++)
                {
                    double grad = err * sampleFeatures[j] + hp.L2Lambda * weights[k][j];
                    mW[j] = AdamBeta1 * mW[j] + (1 - AdamBeta1) * grad;
                    vW[j] = AdamBeta2 * vW[j] + (1 - AdamBeta2) * grad * grad;
                    weights[k][j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }

                mB  = AdamBeta1 * mB + (1 - AdamBeta1) * err;
                vB  = AdamBeta2 * vB + (1 - AdamBeta2) * err * err;
                bias -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);
            }
        }

        biases[k] = bias;
    }

    // ── Biased feature subset sampling (warm-start transfer) ─────────────────

    /// <summary>
    /// Samples feature indices with probability proportional to
    /// <c>importanceScores[j] + epsilon</c> where <c>epsilon = 1 / featureCount</c>.
    /// This biases feature subsets toward historically important features while keeping
    /// all features eligible. Returns a sorted array of sampled indices.
    /// </summary>
    private static int[] GenerateBiasedFeatureSubset(
        int     featureCount,
        double  ratio,
        double[] importanceScores,
        int     seed)
    {
        int subCount = Math.Max(1, (int)Math.Ceiling(ratio * featureCount));
        var rng      = new Random(seed);
        double epsilon = 1.0 / featureCount;

        // Build unnormalised weights: importance + epsilon
        var rawWeights = new double[featureCount];
        double sum = 0.0;
        for (int j = 0; j < featureCount; j++)
        {
            double w = (j < importanceScores.Length ? importanceScores[j] : 0.0) + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        // Build CDF
        var cdf = new double[featureCount];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < featureCount; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        // Sample without replacement using reservoir / rejection
        var selected = new HashSet<int>(subCount);
        int attempts = 0;
        while (selected.Count < subCount && attempts < featureCount * 10)
        {
            attempts++;
            double u   = rng.NextDouble();
            int    idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, featureCount - 1);
            selected.Add(idx);
        }

        // Fallback: pad with sequential indices if needed
        for (int j = 0; j < featureCount && selected.Count < subCount; j++)
            selected.Add(j);

        return [..selected.OrderBy(x => x)];
    }

    // ── Calibration-set permutation importance (double[] for warm-start transfer) ──

    /// <summary>
    /// Computes permutation importance on the calibration set using raw ensemble accuracy
    /// (no Platt scaling — intentional, so this is pure weight-space importance independent
    /// of the calibration transform).
    /// Returns importances normalised to sum to 1.0. Empty when cal set is too small.
    /// </summary>
    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default,
        CancellationToken    ct  = default)
    {
        if (calSet.Count < 10 || featureCount == 0) return new double[featureCount];

        // Baseline accuracy: raw ensemble (no Platt)
        int baseCorrect = 0;
        foreach (var s in calSet)
        {
            double p = EnsembleProb(s.Features, weights, biases, featureCount, subsets, default, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            if ((p >= 0.5) == (s.Direction == 1)) baseCorrect++;
        }
        double baselineAcc = (double)baseCorrect / calSet.Count;

        // Pre-extract feature columns once so parallel workers don't iterate calSet per column.
        int m = calSet.Count;
        var featureCols = new float[featureCount][];
        for (int j = 0; j < featureCount; j++)
        {
            var col = new float[m];
            for (int i = 0; i < m; i++) col[i] = calSet[i].Features[j];
            featureCols[j] = col;
        }

        var importance = new double[featureCount];

        // Each feature's shuffle is independent — run in parallel with per-feature seeded RNG.
        // Scoring avoids float[] clone by patching a thread-local copy once per feature.
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var localRng = new Random(j * 17 + 99);
            var vals     = (float[])featureCols[j].Clone(); // one clone per feature, not per sample
            for (int i = vals.Length - 1; i > 0; i--)
            {
                int ki = localRng.Next(i + 1);
                (vals[ki], vals[i]) = (vals[i], vals[ki]);
            }

            // Score without cloning the full feature array: use a thread-local scratch buffer.
            var scratch = new float[calSet[0].Features.Length];
            int shuffledCorrect = 0;
            for (int idx = 0; idx < m; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p = EnsembleProb(scratch, weights, biases, featureCount, subsets, default, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                if ((p >= 0.5) == (calSet[idx].Direction == 1)) shuffledCorrect++;
            }
            double shuffledAcc = (double)shuffledCorrect / m;
            importance[j] = Math.Max(0.0, baselineAcc - shuffledAcc);
        });

        // Normalise to sum to 1
        double total = importance.Sum();
        if (total > 1e-10)
            for (int j = 0; j < featureCount; j++)
                importance[j] /= total;

        return importance;
    }

    // ── Beta distribution sampler (for Mixup) ─────────────────────────────────

    /// <summary>Samples from Gamma(shape, 1) using the Marsaglia-Tsang method.</summary>
    private static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
            return SampleGamma(rng, shape + 1.0) * Math.Pow(Math.Max(1e-300, rng.NextDouble()), 1.0 / shape);
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do { x = SampleGaussian(rng, 1.0); v = 1.0 + c * x; } while (v <= 0.0);
            v = v * v * v;
            double u = rng.NextDouble();
            if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }

    /// <summary>
    /// Samples from Beta(alpha, alpha) and returns max(λ, 1−λ) ≥ 0.5
    /// so the first Mixup sample always dominates (standard practical Mixup convention).
    /// </summary>
    private static double SampleBeta(Random rng, double alpha)
    {
        if (alpha <= 0.0) return 0.5;
        double g1 = SampleGamma(rng, alpha);
        double g2 = SampleGamma(rng, alpha);
        double lam = g1 / (g1 + g2 + 1e-300);
        return Math.Max(lam, 1.0 - lam);
    }

    // ── Greedy Ensemble Selection (Caruana et al. 2004) ───────────────────────

    /// <summary>
    /// Greedily selects a subset/weighting of learners by minimising log-loss on
    /// the calibration set. Returns normalised usage frequencies (sum = 1) for
    /// all K learners, or an empty array when the cal set is too small.
    /// </summary>
    private static double[] RunGreedyEnsembleSelection(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        int                  rounds = 100,
        MlpState             mlp    = default)
    {
        int K = weights.Length;
        if (calSet.Count < 10 || K < 2) return [];

        // Pre-compute per-learner probabilities on cal set: [sample][learner] — plain loop.
        int gesN  = calSet.Count;
        var allLP = new double[gesN][];
        for (int i = 0; i < gesN; i++)
            allLP[i] = GetLearnerProbs(calSet[i].Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);

        var counts   = new int[K];
        var ensProbs = new double[calSet.Count]; // running ensemble average
        int ensSize  = 0;

        for (int round = 0; round < rounds; round++)
        {
            int    bestK    = -1;
            double bestLoss = double.MaxValue;

            for (int k = 0; k < K; k++)
            {
                double loss = 0.0;
                int    n1   = ensSize + 1;
                for (int i = 0; i < gesN; i++)
                {
                    double avg = (ensProbs[i] * ensSize + allLP[i][k]) / n1;
                    double y   = calSet[i].Direction > 0 ? 1.0 : 0.0;
                    loss -= y * Math.Log(avg + 1e-15) + (1 - y) * Math.Log(1 - avg + 1e-15);
                }
                if (loss < bestLoss) { bestLoss = loss; bestK = k; }
            }

            if (bestK < 0) break;
            for (int i = 0; i < gesN; i++)
                ensProbs[i] = (ensProbs[i] * ensSize + allLP[i][bestK]) / (ensSize + 1);
            counts[bestK]++;
            ensSize++;
        }

        double totalCount = counts.Sum();
        if (totalCount <= 0) return [];
        var result = new double[K];
        for (int k = 0; k < K; k++) result[k] = counts[k] / totalCount;
        return result;
    }

    // ── Density-ratio covariate reweighting ───────────────────────────────────

    /// <summary>
    /// Trains a logistic discriminator to distinguish "recent" samples (label=1) from
    /// "historical" samples (label=0), using sample index as a temporal proxy.
    /// The last <c>min(recentDays, 20%)</c> samples are treated as "recent".
    /// Returns importance weights w_i = p_i / (1 − p_i) normalised to sum to 1,
    /// which are blended with temporal decay weights to focus bootstrap sampling on
    /// samples from the current distribution.
    /// </summary>
    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet,
        int                  featureCount,
        int                  recentWindowDays)
    {
        int n = trainSet.Count;
        if (n < 50) { var uniform = new double[n]; Array.Fill(uniform, 1.0 / n); return uniform; }

        // Proxy: treat last 20 % (capped at recentWindowDays candle equivalents) as "recent"
        int recentCount = Math.Max(10, Math.Min(n / 5, recentWindowDays * 24)); // rough H1 candles
        recentCount     = Math.Min(recentCount, n - 10);
        int histCount   = n - recentCount;

        // Simple logistic discriminator: fit 30 epochs of SGD
        var dw  = new double[featureCount];
        double db = 0.0;
        const double lr = 0.01;
        const double l2 = 0.01;

        var rng = new Random(77);
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

        // Compute importance weights p/(1-p), clip, normalise
        var weights = new double[n];
        double sum  = 0.0;
        for (int i = 0; i < n; i++)
        {
            double z = db;
            for (int j = 0; j < featureCount; j++) z += dw[j] * trainSet[i].Features[j];
            double p = MLFeatureHelper.Sigmoid(z);
            // Clip ratio to [0.01, 10] for numerical stability
            double ratio = Math.Clamp(p / Math.Max(1.0 - p, 1e-6), 0.01, 10.0);
            weights[i] = ratio;
            sum += ratio;
        }
        for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ── Quantile magnitude regressor (pinball loss) ───────────────────────────

    /// <summary>
    /// Fits a linear quantile regressor using the pinball (check) loss:
    ///   L(r) = τ × r   if r ≥ 0
    ///         (τ − 1) × r  if r &lt; 0
    /// where r = y − ŷ.
    /// Returns regression weights and bias for the τ-th conditional quantile of magnitude.
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
                // Subgradient of pinball loss
                double grad = r >= 0 ? -tau : -(tau - 1.0);
                for (int j = 0; j < featureCount; j++)
                    w[j] -= lr * (grad * s.Features[j] + l2 * w[j]);
                b -= lr * grad;
            }
        }

        return (w, b);
    }

    // ── Decision boundary distance (analytic gradient norms) ──────────────────

    /// <summary>
    /// Computes the mean and standard deviation of the analytic gradient norm ‖∇_x P(Buy|x)‖
    /// over the supplied calibration set. For logistic regression with ensemble average:
    ///   ∇_x P ≈ P(1−P) × w_avg,  so ‖∇_x P‖ = P(1−P) × ‖w_avg‖.
    /// Higher values indicate the sample is farther from the decision boundary (more confident).
    /// </summary>
    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        if (calSet.Count == 0) return (0.0, 0.0);

        // Compute average weight vector across ensemble
        int K    = weights.Length;
        var wAvg = new double[featureCount];
        for (int k = 0; k < K; k++)
        {
            int kLen = Math.Min(weights[k].Length, featureCount);
            for (int j = 0; j < kLen; j++)
                wAvg[j] += weights[k][j];
        }
        double wNorm = 0.0;
        for (int j = 0; j < featureCount; j++)
        {
            wAvg[j] /= K;
            wNorm   += wAvg[j] * wAvg[j];
        }
        wNorm = Math.Sqrt(wNorm);

        // ‖∇_x P‖ = P(1−P) × ‖w_avg‖
        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p    = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, default, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            norms[i] = p * (1.0 - p) * wNorm;
        }

        double mean = norms.Average();
        double variance = norms.Sum(n => (n - mean) * (n - mean));
        double std  = norms.Length > 1 ? Math.Sqrt(variance / (norms.Length - 1)) : 0.0;
        return (mean, std);
    }

    // ── Durbin-Watson autocorrelation test ────────────────────────────────────

    /// <summary>
    /// Computes the Durbin-Watson statistic on magnitude regressor residuals over the
    /// training set. DW = Σ(e_t − e_{t-1})² / Σe_t².
    /// DW ≈ 2 → no autocorrelation; DW &lt; 1.5 → positive autocorrelation;
    /// DW > 2.5 → negative autocorrelation.
    /// Returns 2.0 when the training set is too small to compute reliably.
    /// </summary>
    private static double ComputeDurbinWatson(
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        int                  featureCount)
    {
        if (trainSet.Count < 10) return 2.0;

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < featureCount && j < magWeights.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }

        double sumSqDiff = 0.0;
        double sumSqRes  = 0.0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double diff   = residuals[i] - residuals[i - 1];
            sumSqDiff    += diff * diff;
        }
        for (int i = 0; i < residuals.Length; i++)
            sumSqRes += residuals[i] * residuals[i];

        return sumSqRes < 1e-15 ? 2.0 : sumSqDiff / sumSqRes;
    }

    // ── Abstention gate (selective prediction) ────────────────────────────────

    /// <summary>
    /// Trains a 3-feature logistic gate on [calibP, ensStd, metaLabelScore].
    /// Label: 1 if the ensemble prediction was correct for that calibration sample.
    /// Returns (weights, bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        int                  featureCount,
        int[][]?             subsets,
        MlpState             mlp = default)
    {
        const int    Dim    = 3;   // [calibP, ensStd, metaLabelScore]
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return (new double[Dim], 0.0, 0.5);

        int    K  = weights.Length;
        var    aw = new double[Dim];
        double ab = 0.0;

        // Hoist allocations out of loops to avoid per-epoch/per-sample heap pressure.
        const int MetaDim = 7;
        var dW = new double[Dim];
        var mf = new double[MetaDim];
        var af = new double[Dim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, Dim);

            foreach (var s in calSet)
            {
                var    lp       = GetLearnerProbs(s.Features, weights, biases, featureCount, subsets, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
                double ensP     = lp.Average();
                double ensPC    = Math.Clamp(ensP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
                double calibP   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(ensPC) + plattB);

                // Plain loop for variance — avoids LINQ Sum with delegate allocation.
                double variance = 0.0;
                for (int ki = 0; ki < lp.Length; ki++) { double d = lp[ki] - ensP; variance += d * d; }
                double ensStd   = K > 1 ? Math.Sqrt(variance / (K - 1)) : 0.0;

                // Compute meta-label score [ensP, ensStd, feat[0..4]] → logistic (reuse mf).
                mf[0] = ensP; mf[1] = ensStd;
                int top = Math.Min(5, featureCount);
                for (int i = 0; i < top; i++) mf[2 + i] = s.Features[i];
                double mz = metaLabelBias;
                for (int i = 0; i < MetaDim && i < metaLabelWeights.Length; i++)
                    mz += metaLabelWeights[i] * mf[i];
                double metaScore = MLFeatureHelper.Sigmoid(mz);

                // Reuse pre-allocated af array instead of creating new double[Dim] each sample.
                af[0] = calibP; af[1] = ensStd; af[2] = metaScore;
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

    // ── Class-conditional Platt scaling (Round 6) ─────────────────────────────

    /// <summary>
    /// Fits separate Platt scalers for Buy (raw prob ≥ 0.5) and Sell (raw prob &lt; 0.5) subsets
    /// of the calibration set to correct directional calibration bias.
    /// Returns (ABuy, BBuy, ASell, BSell); returns (0,0,0,0) when a class subset has &lt; 5 samples.
    /// </summary>
    private static (double ABuy, double BBuy, double ASell, double BSell)
        FitClassConditionalPlatt(
            List<TrainingSample> calSet,
            double[][]           weights,
            double[]             biases,
            int                  featureCount,
            int[][]?             subsets,
            MetaLearner          meta = default,
            MlpState             mlp  = default)
    {
        const double lr     = 0.01;
        const int    epochs = 200;

        var buySamples  = new List<(double Logit, double Y)>();
        var sellSamples = new List<(double Logit, double Y)>();

        foreach (var s in calSet)
        {
            double rawP  = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP         = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
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

    // ── Average Kelly fraction (Round 6) ──────────────────────────────────────

    /// <summary>
    /// Computes the half-Kelly fraction averaged over the calibration set:
    ///   mean( max(0, 2·calibP − 1) ) × 0.5
    /// where calibP uses the already-fitted global Platt (A, B).
    /// Returns 0.0 if the calibration set is empty.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (var s in calSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP          = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            sum += Math.Max(0.0, 2.0 * calibP - 1.0);
        }
        return sum / calSet.Count * 0.5;
    }

    // ── Mutual-information feature redundancy (Round 6) ───────────────────────

    /// <summary>
    /// Computes pairwise mutual information between the top-N features on the training set
    /// (discretised into 10 equal-width bins). Returns pairs whose MI exceeds
    /// <paramref name="threshold"/> × log(2) as "FeatureA:FeatureB" strings.
    /// Empty when disabled (threshold == 0) or fewer than 20 training samples.
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet,
        int                  featureCount,
        double               threshold)
    {
        if (threshold <= 0.0 || trainSet.Count < 20) return [];

        const int TopN   = 10;   // only check first TopN features to bound O(N²) cost
        const int NumBin = 10;

        int checkCount = Math.Min(TopN, featureCount);
        var result     = new List<string>();
        double maxMi   = threshold * Math.Log(2);

        for (int i = 0; i < checkCount; i++)
        {
            for (int j = i + 1; j < checkCount; j++)
            {
                // Build joint 2-D histogram
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
                    result.Add($"{MLFeatureHelper.FeatureNames[i]}:{MLFeatureHelper.FeatureNames[j]}");
            }
        }

        return [.. result];
    }

    // ── OOB-contribution ensemble pruning (Round 6) ───────────────────────────

    /// <summary>
    /// For each learner k, measures the marginal OOB accuracy contribution:
    /// ensemble accuracy with k  vs ensemble accuracy without k.
    /// Sets weights[k] to zero and increments <paramref name="prunedCount"/> for every
    /// learner whose removal improves accuracy.
    /// </summary>
    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        double[][]           weights,
        double[]             biases,
        double[]             temporalWeights,
        int                  featureCount,
        int[][]?             subsets,
        int                  K,
        MlpState             mlp = default)
    {
        if (trainSet.Count < 20 || K < 2) return 0;

        // Compute per-sample OOB probabilities for the full ensemble
        int prunedCount = 0;

        // Compute full-ensemble OOB accuracy
        double baseAcc = ComputeOobAccuracy(trainSet, weights, biases, temporalWeights, featureCount, subsets, K, mlp);

        for (int k = 0; k < K; k++)
        {
            // Temporarily zero out learner k
            var  savedW = weights[k];
            var  savedB = biases[k];
            weights[k]  = new double[savedW.Length];
            biases[k]   = 0.0;

            double accWithout = ComputeOobAccuracy(trainSet, weights, biases, temporalWeights, featureCount, subsets, K, mlp);

            if (accWithout > baseAcc)
            {
                // Removing learner k improved accuracy — keep it pruned
                prunedCount++;
                // Leave weights[k] as zeros
            }
            else
            {
                // Restore
                weights[k] = savedW;
                biases[k]  = savedB;
            }
        }

        return prunedCount;
    }

    // ── Temperature scaling (Round 7) ─────────────────────────────────────────

    /// <summary>
    /// Fits a single temperature scalar T on the calibration set via grid search over
    /// [0.1, 3.0] in 30 steps, selecting the T that minimises binary cross-entropy.
    /// calibP = σ(logit(rawP) / T).
    /// Returns 1.0 (no-op) when the cal set is too small.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        double[][]           weights,
        double[]             biases,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (calSet.Count < 10) return 1.0;

        // Pre-cache logits and labels once — EnsembleProb is O(K×F) per sample.
        // Avoids recomputing the same inference 31 times (once per T candidate).
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double rawP = EnsembleProb(calSet[i].Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP        = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            logits[i]   = MLFeatureHelper.Logit(rawP);
            labels[i]   = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double bestT    = 1.0;
        double bestLoss = double.MaxValue;

        for (int step = 0; step <= 30; step++)
        {
            double T    = 0.1 + step * (3.0 - 0.1) / 30.0;
            double loss = 0.0;
            const double eps = 1e-10;

            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(logits[i] / T);
                double y      = labels[i];
                loss += -(y * Math.Log(calibP + eps) + (1 - y) * Math.Log(1 - calibP + eps));
            }

            if (loss / n < bestLoss)
            {
                bestLoss = loss / n;
                bestT    = T;
            }
        }

        return bestT;
    }

    // ── Ensemble diversity (Round 7) ──────────────────────────────────────────

    /// <summary>
    /// Computes the average pairwise Pearson correlation between all K learner weight vectors.
    /// Returns 0.0 when K &lt; 2 or all weights are zero.
    /// </summary>
    private static double ComputeEnsembleDiversity(double[][] weights, int featureCount)
    {
        int K = weights.Length;
        if (K < 2) return 0.0;

        double sumCorr = 0.0;
        int    pairs   = 0;

        for (int i = 0; i < K; i++)
            for (int j = i + 1; j < K; j++)
            {
                double rho = PearsonCorrelation(weights[i], weights[j], featureCount);
                sumCorr += rho;
                pairs++;
            }

        return pairs > 0 ? sumCorr / pairs : 0.0;
    }

    // ── Brier Skill Score (Round 7) ───────────────────────────────────────────

    /// <summary>
    /// Computes BSS = 1 − Brier_model / Brier_naive on the test set.
    /// Brier_naive = p_base × (1 − p_base) where p_base = fraction of Buy labels.
    /// Returns 0.0 when the test set is empty.
    /// </summary>
    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        double[][]           weights,
        double[]             biases,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int[][]?             subsets,
        MetaLearner          meta = default,
        MlpState             mlp  = default)
    {
        if (testSet.Count == 0) return 0.0;

        double sumBrier = 0.0;
        int    buyCount = 0;

        foreach (var s in testSet)
        {
            double rawP   = EnsembleProb(s.Features, weights, biases, featureCount, subsets, meta, mlp.HiddenW, mlp.HiddenB, mlp.HiddenDim);
            rawP          = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7); // guard against ±Inf logits
            double calibP = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawP) + plattB);
            double y      = s.Direction > 0 ? 1.0 : 0.0;
            double diff   = calibP - y;
            sumBrier += diff * diff;
            if (s.Direction > 0) buyCount++;
        }

        int    n           = testSet.Count;
        double brierModel  = sumBrier / n;
        double pBase       = (double)buyCount / n;
        double brierNaive  = pBase * (1.0 - pBase);

        return brierNaive < 1e-10 ? 0.0 : 1.0 - brierModel / brierNaive;
    }

    // ── Walk-forward Sharpe trend (Round 6) ───────────────────────────────────

    /// <summary>
    /// Fits a least-squares linear regression slope through the per-fold Sharpe series.
    /// Returns 0.0 when fewer than 3 folds are available.
    /// A negative slope indicates degrading out-of-sample performance over time.
    /// </summary>
    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        int n = sharpePerFold.Count;
        if (n < 3) return 0.0;

        // Simple OLS: slope = ( n·Σxy − Σx·Σy ) / ( n·Σx² − (Σx)² )
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = sharpePerFold[i];
            sumX  += x;
            sumY  += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) < 1e-12 ? 0.0 : (n * sumXY - sumX * sumY) / denom;
    }

    // ── Covariate shift weight integration (Round 8) ──────────────────────────

    /// <summary>
    /// Computes per-sample novelty scores using the parent model's feature quantile
    /// breakpoints. Each sample's weight = 1 + fraction_of_features_outside_[q10,q90].
    /// Normalised to mean = 1.0 so the effective gradient scale is unchanged.
    /// </summary>
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
            weights[i] = 1.0 + noveltyFraction; // range [1, 2]
        }

        // Normalise to mean = 1.0
        double mean = weights.Average();
        if (mean > 1e-10)
            for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }
}
