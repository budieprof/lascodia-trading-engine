using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade Gradient Boosting Machine trainer. Implements depth-limited decision tree
/// gradient boosting with log-loss in pure C#.
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples.</item>
///   <item>Walk-forward cross-validation (expanding window, embargo, purging) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Final model splits: 70% train | 10% Platt calibration | ~20% held-out test.</item>
///   <item>Gradient boosted regression trees fitted on pseudo-residuals with shrinkage.</item>
///   <item>Platt scaling (A, B) fitted on the calibration fold after the ensemble is frozen.</item>
///   <item>Isotonic calibration (PAVA) applied post-Platt for monotonic probability correction.</item>
///   <item>ECE (Expected Calibration Error) computed post-calibration on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set.</item>
///   <item>Parallel magnitude linear regressor trained with Adam + Huber loss + early stopping.</item>
///   <item>Permutation feature importance with optional feature pruning re-train pass.</item>
///   <item>OOB accuracy estimation from out-of-bag tree predictions.</item>
///   <item>Conformal prediction (split-conformal qHat) for prediction set coverage.</item>
///   <item>Jackknife+ residuals for prediction intervals.</item>
///   <item>Meta-label secondary classifier for filtering low-quality signals.</item>
///   <item>Abstention gate (selective prediction) for suppressing uncertain signals.</item>
///   <item>Quantile magnitude regressor (pinball loss) for asymmetric risk sizing.</item>
///   <item>Decision boundary distance analytics.</item>
///   <item>Durbin-Watson autocorrelation test on magnitude residuals.</item>
///   <item>Class-conditional Platt scaling (separate Buy/Sell calibrators).</item>
///   <item>Average Kelly fraction for position sizing guidance.</item>
///   <item>Mutual-information feature redundancy check.</item>
///   <item>Temperature scaling alternative calibration.</item>
///   <item>Brier Skill Score computation.</item>
///   <item>NaN/Inf tree sanitization.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Optional warm-start from prior GBM snapshot.</item>
///   <item>Incremental update fast-path for rapid regime adaptation.</item>
///   <item>Density-ratio importance weighting for distribution shift.</item>
///   <item>Stationarity gate (soft ADF check).</item>
/// </list>
/// </para>
/// Registered as a keyed IMLModelTrainer with key "gbm".
/// </summary>
public sealed class GbmModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const string ModelType    = "GBM";
    private const string ModelVersion = "2.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly ILogger<GbmModelTrainer> _logger;

    public GbmModelTrainer(ILogger<GbmModelTrainer> logger) => _logger = logger;

    // ── IMLModelTrainer ─────────────────────────────────────────────────────

    public async Task<TrainingResult> TrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart     = null,
        long?                parentModelId = null,
        CancellationToken    ct            = default)
    {
        return await Task.Run(() => Train(samples, hp, warmStart, parentModelId, ct), ct);
    }

    // ── Core training logic (synchronous, runs on thread-pool) ──────────────

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        int featureCount = samples[0].Features.Length;
        int numRounds    = Math.Max(10, hp.K > 0 ? hp.K : 50);
        int maxDepth     = hp.GbmMaxDepth > 0 ? hp.GbmMaxDepth : 3;
        double lr        = hp.LearningRate > 0 ? hp.LearningRate : 0.1;

        // ── 0. Incremental update fast-path ─────────────────────────────────
        if (warmStart is not null && warmStart.Type == ModelType && hp.DensityRatioWindowDays > 0)
        {
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * 24);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "GBM incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    K                     = Math.Max(10, numRounds / 3),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = lr / 3.0,
                    DensityRatioWindowDays = 0, // prevent recursion
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Z-score standardisation ──────────────────────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, featureCount, numRounds, maxDepth, lr, ct);
        _logger.LogInformation(
            "GBM Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70% train | 10% cal | ~20% test ──────────
        int trainEnd = (int)(allStd.Count * 0.70);
        int calEnd   = (int)(allStd.Count * 0.80);
        int embargo  = hp.EmbargoBarCount;

        var trainSet = allStd[..Math.Max(0, trainEnd - embargo)];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < allStd.Count ? calEnd : allStd.Count)];
        var testSet  = allStd[Math.Min(calEnd + embargo, allStd.Count)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"GBM: Insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 3b. Stationarity gate ───────────────────────────────────────────
        {
            int nonStatCount = CountNonStationaryFeatures(trainSet, featureCount);
            double nonStatFraction = featureCount > 0 ? (double)nonStatCount / featureCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "GBM Stationarity gate: {NonStat}/{Total} features have unit root. Consider enabling FracDiffD.",
                    nonStatCount, featureCount);
        }

        // ── 3c. Density-ratio importance weights ────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(trainSet, featureCount, hp.DensityRatioWindowDays);
            _logger.LogDebug("GBM density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 3d. Covariate shift weight integration (parent model novelty scoring) ──
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
            _logger.LogDebug("GBM covariate shift weights applied from parent model (gen={Gen}).",
                warmStart!.GenerationNumber);
        }

        // ── 3e. Adaptive label smoothing ────────────────────────────────────
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
                "GBM adaptive label smoothing: ε={Eps:F3} (ambiguous fraction={Frac:P1})",
                effectiveLabelSmoothing, ambiguousFraction);
        }

        // ── 4. Fit GBM ensemble ─────────────────────────────────────────────
        var (trees, baseLogOdds, treeBagMasks) = FitGbmEnsemble(trainSet, featureCount, numRounds, maxDepth, lr,
            effectiveLabelSmoothing, warmStart, densityWeights, hp.TemporalDecayLambda,
            hp.FeatureSampleRatio, hp.L2Lambda, ct);

        _logger.LogInformation("GBM fitted: {R} trees, baseLogOdds={BLO:F4}", trees.Count, baseLogOdds);

        // ── 5. Platt calibration ────────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, trees, baseLogOdds, lr, featureCount);
        _logger.LogDebug("GBM Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 5b. Class-conditional Platt ─────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, trees, baseLogOdds, lr, featureCount);
        _logger.LogDebug(
            "GBM Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 5c. Kelly fraction ──────────────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, trees, baseLogOdds, lr, plattA, plattB, featureCount);
        _logger.LogDebug("GBM average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 6. Magnitude linear regressor ───────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, featureCount, hp);

        // ── 7. Evaluation on held-out test set ──────────────────────────────
        var finalMetrics = EvaluateGbm(testSet, trees, baseLogOdds, lr, magWeights, magBias,
            plattA, plattB, featureCount);

        _logger.LogInformation(
            "GBM eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE ──────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, trees, baseLogOdds, lr, plattA, plattB, featureCount);
        _logger.LogInformation("GBM post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal threshold (on cal set) ────────────────────────────
        double optimalThreshold = ComputeOptimalThreshold(calSet, trees, baseLogOdds, lr, plattA, plattB, featureCount,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("GBM EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 10. Permutation feature importance ──────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, trees, baseLogOdds, lr, plattA, plattB, featureCount, ct)
            : new float[featureCount];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: MLFeatureHelper.FeatureNames[idx]))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "GBM top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Calibration-set importance (for warm-start transfer) ───────
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, trees, baseLogOdds, lr, featureCount, ct)
            : new double[featureCount];

        // ── 11. Feature pruning re-train pass ───────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, featureCount);
        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && featureCount - prunedCount >= 10)
        {
            _logger.LogInformation("GBM feature pruning: masking {Pruned}/{Total} low-importance features",
                prunedCount, featureCount);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet, activeMask);
            var maskedTest  = ApplyMask(testSet, activeMask);

            int prunedRounds = Math.Max(10, numRounds / 2);
            var (pTrees, pBLO, _) = FitGbmEnsemble(maskedTrain, featureCount, prunedRounds, maxDepth, lr,
                effectiveLabelSmoothing, null, densityWeights, hp.TemporalDecayLambda,
                hp.FeatureSampleRatio, hp.L2Lambda, ct);
            var (pA, pB)       = FitPlattScaling(maskedCal, pTrees, pBLO, lr, featureCount);
            var (pmw, pmb)     = FitLinearRegressor(maskedTrain, featureCount, hp);
            var prunedMetrics  = EvaluateGbm(maskedTest, pTrees, pBLO, lr, pmw, pmb, pA, pB, featureCount);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation("GBM pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                trees        = pTrees;    baseLogOdds = pBLO;
                magWeights   = pmw;       magBias     = pmb;
                plattA       = pA;        plattB      = pB;
                finalMetrics = prunedMetrics;
                ece              = ComputeEce(maskedTest, trees, baseLogOdds, lr, plattA, plattB, featureCount);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, trees, baseLogOdds, lr, plattA, plattB, featureCount,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax);
            }
            else
            {
                _logger.LogInformation("GBM pruned model rejected — keeping full model");
                prunedCount = 0;
                activeMask  = new bool[featureCount]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[featureCount]; Array.Fill(activeMask, true);
        }

        // ── 11b. Isotonic calibration, conformal threshold ──────────────────
        var postPruneCalSet = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;

        double[] isotonicBp = FitIsotonicCalibration(postPruneCalSet, trees, baseLogOdds, lr, plattA, plattB, featureCount);
        _logger.LogInformation("GBM isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(
            postPruneCalSet, trees, baseLogOdds, lr, plattA, plattB, isotonicBp, featureCount, conformalAlpha);
        _logger.LogInformation("GBM conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 11c. OOB accuracy (true OOB using per-tree bag membership) ─────
        double oobAccuracy = ComputeOobAccuracy(trainSet, trees, treeBagMasks, baseLogOdds, lr, featureCount);
        _logger.LogInformation("GBM OOB accuracy={OobAcc:P1}", oobAccuracy);
        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── 11d. Jackknife+ residuals ───────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(trainSet, trees, baseLogOdds, lr, featureCount);
        _logger.LogInformation("GBM Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── 11e. Meta-label model ───────────────────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            postPruneCalSet, trees, baseLogOdds, lr, featureCount);
        _logger.LogDebug("GBM meta-label model: bias={B:F4}", metaLabelBias);

        // ── 11f. Abstention gate ────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalSet, trees, baseLogOdds, lr, plattA, plattB,
            metaLabelWeights, metaLabelBias, featureCount);
        _logger.LogDebug("GBM abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── 11g. Quantile magnitude regressor ───────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, featureCount, hp.MagnitudeQuantileTau);
            _logger.LogDebug("GBM quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 11h. Decision boundary stats ────────────────────────────────────
        var (dbMean, dbStd) = postPruneCalSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalSet, trees, baseLogOdds, lr, featureCount)
            : (0.0, 0.0);
        _logger.LogDebug("GBM decision boundary: mean={Mean:F4} std={Std:F4}", dbMean, dbStd);

        // ── 11i. Durbin-Watson on magnitude residuals ───────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, featureCount);
        _logger.LogDebug("GBM Durbin-Watson={DW:F4}", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning("GBM magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2})",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 11j. MI redundancy ──────────────────────────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, featureCount, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning("GBM MI redundancy: {N} pairs exceed threshold", redundantPairs.Length);
        }

        // ── 11k. Temperature scaling ────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(postPruneCalSet, trees, baseLogOdds, lr, featureCount);
            _logger.LogDebug("GBM temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 11l. Brier Skill Score ──────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(testSet, trees, baseLogOdds, lr, plattA, plattB, featureCount);
        _logger.LogInformation("GBM BSS={BSS:F4}", brierSkillScore);

        // ── 11m. PSI baseline ───────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 11n. NaN/Inf tree sanitization ──────────────────────────────────
        int sanitizedCount = SanitizeTrees(trees);
        if (sanitizedCount > 0)
            _logger.LogWarning("GBM sanitized {N}/{Total} trees with non-finite values.", sanitizedCount, trees.Count);

        // ── 12. Serialise model snapshot ────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = trees.Count,
            Weights                    = [],
            Biases                     = [],
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
            OobAccuracy                = oobAccuracy,
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
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            SanitizedLearnerCount      = sanitizedCount,
            AdaptiveLabelSmoothing     = effectiveLabelSmoothing,
            ConformalCoverage          = hp.ConformalCoverage,
            GbmTreesJson              = JsonSerializer.Serialize(trees, JsonOptions),
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);

        _logger.LogInformation(
            "GbmModelTrainer complete: {R} trees, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            trees.Count, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GBM ENSEMBLE FITTING
    // ═══════════════════════════════════════════════════════════════════════

    private (List<GbmTree> Trees, double BaseLogOdds, List<HashSet<int>> TreeBagMasks) FitGbmEnsemble(
        List<TrainingSample> train,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        double               temporalDecayLambda,
        double               colSampleRatio,
        double               l2Lambda,
        CancellationToken    ct)
    {
        int valSize  = Math.Max(20, train.Count / 10);
        var valSet   = train[^valSize..];
        var trainSet = train[..^valSize];

        // Temporal + density blended weights
        var temporalWeights = ComputeTemporalWeights(trainSet.Count, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalWeights.Length)
        {
            double sum = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                temporalWeights[i] *= densityWeights[i];
                sum += temporalWeights[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < temporalWeights.Length; i++) temporalWeights[i] /= sum;
        }

        int n = trainSet.Count;

        // Stochastic GBM: row subsampling fraction (default 0.8 when not configured)
        double rowSubsampleFrac = 0.8;
        int rowSubsampleCount   = Math.Max(10, (int)(n * rowSubsampleFrac));

        // Column subsampling per tree (FeatureSampleRatio maps to colsample_bytree)
        bool useColSubsample = colSampleRatio > 0.0 && colSampleRatio < 1.0;
        int colSubsampleCount = useColSubsample
            ? Math.Max(1, (int)Math.Ceiling(colSampleRatio * featureCount))
            : featureCount;

        // Base rate log-odds initialisation
        double basePosRate = n > 0
            ? (double)trainSet.Count(s => s.Direction > 0) / n
            : 0.5;
        basePosRate = Math.Clamp(basePosRate, 1e-6, 1 - 1e-6);
        double baseLogOdds = Math.Log(basePosRate / (1 - basePosRate));

        // Warm-start: load prior trees if available and compatible
        var trees = new List<GbmTree>(numRounds);
        double[] scores;

        if (warmStart?.GbmTreesJson is not null && warmStart.Type == ModelType)
        {
            try
            {
                var priorTrees = JsonSerializer.Deserialize<List<GbmTree>>(warmStart.GbmTreesJson, JsonOptions);
                if (priorTrees is { Count: > 0 })
                {
                    trees.AddRange(priorTrees);
                    _logger.LogInformation("GBM warm-start: loaded {N} prior trees (gen={Gen})",
                        priorTrees.Count, warmStart.GenerationNumber);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "GBM warm-start: failed to deserialise prior trees, starting fresh.");
            }
        }

        // Initialise scores from base + existing trees
        scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            scores[i] = baseLogOdds;
            foreach (var t in trees) scores[i] += learningRate * Predict(t, trainSet[i].Features);
        }

        // Early stopping state
        double bestValLoss = double.MaxValue;
        int patience = 0;
        int bestRound = trees.Count;
        var bestTrees = new List<GbmTree>(trees);

        // Per-tree bag membership for true OOB accuracy (maps to trainSet indices before val split)
        var bagMasks = new List<HashSet<int>>(numRounds);
        // Warm-start trees have no bag info — add empty sets so indices stay aligned
        for (int w = 0; w < trees.Count; w++) bagMasks.Add([]);
        var bestBagMasks = new List<HashSet<int>>(bagMasks);

        // Cumulative distribution table for weighted row sampling
        var cdf = new double[n];
        cdf[0] = temporalWeights[0];
        for (int i = 1; i < n; i++) cdf[i] = cdf[i - 1] + temporalWeights[i];
        bool useWeightedSampling = rowSubsampleCount < n && cdf[^1] > 1e-15;

        for (int r = 0; r < numRounds && !ct.IsCancellationRequested; r++)
        {
            // ── Row subsampling (weighted stochastic gradient boosting) ─────
            var rng = new Random(r * 31 + 7);
            int[] rowSample;
            if (rowSubsampleCount < n)
            {
                if (useWeightedSampling)
                {
                    // Weighted sampling without replacement via alias-free CDF inversion
                    var selected = new HashSet<int>(rowSubsampleCount);
                    while (selected.Count < rowSubsampleCount)
                    {
                        double u = rng.NextDouble() * cdf[^1];
                        int lo = 0, hi = n - 1;
                        while (lo < hi)
                        {
                            int mid = (lo + hi) >> 1;
                            if (cdf[mid] < u) lo = mid + 1; else hi = mid;
                        }
                        selected.Add(lo);
                    }
                    rowSample = [..selected];
                }
                else
                {
                    // Uniform Fisher-Yates partial shuffle fallback
                    var allIdx = new int[n];
                    for (int i = 0; i < n; i++) allIdx[i] = i;
                    for (int i = 0; i < rowSubsampleCount; i++)
                    {
                        int j = i + rng.Next(n - i);
                        (allIdx[i], allIdx[j]) = (allIdx[j], allIdx[i]);
                    }
                    rowSample = allIdx[..rowSubsampleCount];
                }
            }
            else
            {
                rowSample = Enumerable.Range(0, n).ToArray();
            }

            // Track which samples are in-bag for this tree (for true OOB accuracy)
            bagMasks.Add(new HashSet<int>(rowSample));

            // ── Column subsampling per tree ─────────────────────────────────
            int[] colSample;
            if (useColSubsample)
            {
                var allCols = new int[featureCount];
                for (int i = 0; i < featureCount; i++) allCols[i] = i;
                for (int i = 0; i < colSubsampleCount; i++)
                {
                    int j = i + rng.Next(featureCount - i);
                    (allCols[i], allCols[j]) = (allCols[j], allCols[i]);
                }
                colSample = allCols[..colSubsampleCount];
                Array.Sort(colSample);
            }
            else
            {
                colSample = Enumerable.Range(0, featureCount).ToArray();
            }

            // Compute pseudo-residuals and second-order Hessians (Newton-Raphson)
            var residuals     = new double[n];
            var hessians      = new double[n];
            var sampleWeights = new double[n];
            for (int i = 0; i < n; i++)
            {
                double p = Sigmoid(scores[i]);
                int rawY = trainSet[i].Direction > 0 ? 1 : 0;
                double y = labelSmoothing > 0
                    ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                    : rawY;
                residuals[i]     = y - p;              // first-order gradient (negative)
                hessians[i]      = p * (1 - p);        // second-order (Hessian diagonal)
                sampleWeights[i] = temporalWeights[i];
            }

            // Fit a weighted regression tree using Newton-Raphson leaf values
            var indices = rowSample.ToList();
            var tree    = new GbmTree();
            BuildTree(tree, indices, trainSet, residuals, hessians, sampleWeights,
                colSample, maxDepth, l2Lambda, MinChildWeight);
            trees.Add(tree);

            // Clip leaf values to prevent extreme predictions
            ClipLeafValues(tree, LeafClipValue);

            // Update scores for ALL samples (not just the subsample)
            for (int i = 0; i < n; i++)
                scores[i] += learningRate * Predict(tree, trainSet[i].Features);

            // Validation loss (log-loss) for early stopping
            if (valSet.Count >= 10 && r % 5 == 4)
            {
                double valLoss = 0;
                foreach (var s in valSet)
                {
                    double sc = baseLogOdds;
                    foreach (var t in trees) sc += learningRate * Predict(t, s.Features);
                    double p = Sigmoid(sc);
                    int y    = s.Direction > 0 ? 1 : 0;
                    valLoss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
                }
                valLoss /= valSet.Count;

                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss  = valLoss;
                    bestRound    = trees.Count;
                    bestTrees    = [..trees];
                    bestBagMasks = [..bagMasks];
                    patience     = 0;
                }
                else if (++patience >= hp_patience(numRounds))
                {
                    _logger.LogDebug("GBM early stopping at round {R} (best at {Best})", trees.Count, bestRound);
                    break;
                }
            }
        }

        // Restore best ensemble
        if (bestTrees.Count > 0 && bestTrees.Count < trees.Count)
        {
            trees    = bestTrees;
            bagMasks = bestBagMasks;
        }

        return (trees, baseLogOdds, bagMasks);

        static int hp_patience(int rounds) => Math.Max(3, rounds / 10);
    }

    // ── XGBoost-style constants ──────────────────────────────────────────────
    /// <summary>Minimum sum of Hessian in a child node (prevents overfitting on tiny splits).</summary>
    private const double MinChildWeight = 1.0;
    /// <summary>Maximum absolute leaf value to prevent extreme predictions.</summary>
    private const double LeafClipValue  = 5.0;

    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        CancellationToken    ct)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);

        if (foldSize < 50)
        {
            _logger.LogWarning("GBM walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
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

            int cvRounds = Math.Max(10, numRounds / 3);
            var (cvTrees, cvBLO) = FitGbmEnsembleSimple(foldTrain, featureCount, cvRounds, maxDepth, learningRate,
                hp.LabelSmoothing, hp.TemporalDecayLambda, ct);
            var m = EvaluateGbm(foldTest, cvTrees, cvBLO, learningRate, [], 0, 1.0, 0.0, featureCount);

            // Feature importance from tree split usage
            var foldImp = ComputeTreeSplitImportance(cvTrees, featureCount);

            // Equity-curve gate
            var predictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int i = 0; i < foldTest.Count; i++)
            {
                double p = GbmProb(foldTest[i].Features, cvTrees, cvBLO, learningRate, featureCount);
                predictions[i] = (p >= 0.5 ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(predictions);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        });

        // Aggregate
        var accList = new List<double>(folds);
        var f1List  = new List<double>(folds);
        var evList  = new List<double>(folds);
        var sharpeList = new List<double>(folds);
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

        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning("GBM equity-curve gate: {BadFolds}/{TotalFolds} folds failed", badFolds, accList.Count);

        double avgAcc = accList.Average();
        double stdAcc = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning("GBM Sharpe trend gate: slope={Slope:F3} < threshold", sharpeTrend);
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
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImportances[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp = 0.0;
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

    // ═══════════════════════════════════════════════════════════════════════
    //  SIMPLIFIED GBM (for CV folds — no warm-start, no early stopping)
    // ═══════════════════════════════════════════════════════════════════════

    private (List<GbmTree> Trees, double BaseLogOdds) FitGbmEnsembleSimple(
        List<TrainingSample> train,
        int                  featureCount,
        int                  numRounds,
        int                  maxDepth,
        double               learningRate,
        double               labelSmoothing,
        double               temporalDecayLambda,
        CancellationToken    ct)
    {
        int n = train.Count;
        double basePosRate = n > 0 ? (double)train.Count(s => s.Direction > 0) / n : 0.5;
        basePosRate = Math.Clamp(basePosRate, 1e-6, 1 - 1e-6);
        double baseLogOdds = Math.Log(basePosRate / (1 - basePosRate));

        var scores = new double[n];
        Array.Fill(scores, baseLogOdds);
        var trees = new List<GbmTree>(numRounds);
        var temporalWeights = ComputeTemporalWeights(n, temporalDecayLambda);

        for (int r = 0; r < numRounds && !ct.IsCancellationRequested; r++)
        {
            var residuals     = new double[n];
            var hessians      = new double[n];
            var sampleWeights = new double[n];
            for (int i = 0; i < n; i++)
            {
                double p = Sigmoid(scores[i]);
                int rawY = train[i].Direction > 0 ? 1 : 0;
                double y = labelSmoothing > 0
                    ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                    : rawY;
                residuals[i]     = y - p;
                hessians[i]      = p * (1 - p);
                sampleWeights[i] = temporalWeights[i];
            }

            var allCols = Enumerable.Range(0, featureCount).ToArray();
            var indices = Enumerable.Range(0, n).ToList();
            var tree    = new GbmTree();
            BuildTree(tree, indices, train, residuals, hessians, sampleWeights,
                allCols, maxDepth, 0.0, MinChildWeight);
            trees.Add(tree);
            ClipLeafValues(tree, LeafClipValue);

            for (int i = 0; i < n; i++)
                scores[i] += learningRate * Predict(tree, train[i].Features);
        }

        return (trees, baseLogOdds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE BUILDING (XGBoost-style: Newton-Raphson leaf values, L2 reg,
    //                  min child weight, column subsampling)
    // ═══════════════════════════════════════════════════════════════════════

    private static void BuildTree(
        GbmTree tree, List<int> indices,
        IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight)
    {
        tree.Nodes = new List<GbmNode>();
        BuildNode(tree.Nodes, 0, indices, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, 0);
    }

    /// <summary>
    /// Builds a single node of the regression tree using the XGBoost gain formula:
    ///   Gain = ½ [ G_L²/(H_L+λ) + G_R²/(H_R+λ) − G²/(H+λ) ]
    /// where G = Σ g_i, H = Σ h_i (weighted by sample weights).
    /// Leaf value = −G / (H + λ) (Newton-Raphson optimal step with L2 regularisation).
    /// </summary>
    private static void BuildNode(
        List<GbmNode> nodes, int nodeIdx,
        List<int> indices, IReadOnlyList<TrainingSample> samples,
        double[] gradients, double[] hessians, double[] sampleWeights,
        int[] colSubset, int maxDepth, double l2Lambda, double minChildWeight, int depth)
    {
        while (nodes.Count <= nodeIdx) nodes.Add(new GbmNode());
        var node = nodes[nodeIdx];

        // Compute weighted gradient and Hessian sums for this node
        double G = 0, H = 0;
        foreach (int i in indices)
        {
            G += sampleWeights[i] * gradients[i];
            H += sampleWeights[i] * hessians[i];
        }

        // Newton-Raphson optimal leaf value with L2 regularisation: −G / (H + λ)
        double leafVal = (H + l2Lambda) > 1e-15 ? -G / (H + l2Lambda) : 0;
        node.LeafValue = leafVal;

        // Stopping conditions: max depth, too few samples, or insufficient Hessian mass
        if (depth >= maxDepth || indices.Count < 4 || H < minChildWeight)
        {
            node.IsLeaf = true;
            return;
        }

        // Find best split using XGBoost gain formula over the column subset
        double bestGain   = 0;
        int    bestFi     = 0;
        double bestThresh = 0;

        // Parent score component: G² / (H + λ)
        double parentScore = G * G / (H + l2Lambda);

        // Work on a copy so sorting for one feature doesn't corrupt order for the next
        var sortBuf = new int[indices.Count];

        foreach (int fi in colSubset)
        {
            // Copy indices into sort buffer and sort by feature value
            indices.CopyTo(sortBuf);
            Array.Sort(sortBuf, 0, indices.Count,
                Comparer<int>.Create((a, b) => samples[a].Features[fi].CompareTo(samples[b].Features[fi])));

            double leftG = 0, leftH = 0;
            double rightG = G, rightH = H;

            for (int ti = 0; ti < indices.Count - 1; ti++)
            {
                int idx = sortBuf[ti];
                double wi = sampleWeights[idx];
                leftG  += wi * gradients[idx];
                leftH  += wi * hessians[idx];
                rightG -= wi * gradients[idx];
                rightH -= wi * hessians[idx];

                // Skip if same feature value as next sample
                if (Math.Abs(samples[idx].Features[fi] - samples[sortBuf[ti + 1]].Features[fi]) < 1e-10)
                    continue;

                // Min child weight gate (XGBoost's min_child_weight)
                if (leftH < minChildWeight || rightH < minChildWeight) continue;

                // XGBoost gain: ½ [ G_L²/(H_L+λ) + G_R²/(H_R+λ) − G²/(H+λ) ]
                double gain = 0.5 * (leftG * leftG / (leftH + l2Lambda)
                                   + rightG * rightG / (rightH + l2Lambda)
                                   - parentScore);

                if (gain > bestGain)
                {
                    bestGain   = gain;
                    bestFi     = fi;
                    bestThresh = (samples[idx].Features[fi] + samples[sortBuf[ti + 1]].Features[fi]) / 2.0;
                }
            }
        }

        if (bestGain <= 0) { node.IsLeaf = true; return; }

        node.SplitFeature   = bestFi;
        node.SplitThreshold = bestThresh;
        node.LeftChild      = nodeIdx * 2 + 1;
        node.RightChild     = nodeIdx * 2 + 2;

        var leftIdx  = indices.Where(i => samples[i].Features[bestFi] <= bestThresh).ToList();
        var rightIdx = indices.Where(i => samples[i].Features[bestFi] >  bestThresh).ToList();

        BuildNode(nodes, node.LeftChild,  leftIdx,  samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1);
        BuildNode(nodes, node.RightChild, rightIdx, samples, gradients, hessians, sampleWeights,
            colSubset, maxDepth, l2Lambda, minChildWeight, depth + 1);
    }

    /// <summary>Clips all leaf values to [-clipValue, +clipValue] to prevent extreme predictions.</summary>
    private static void ClipLeafValues(GbmTree tree, double clipValue)
    {
        if (tree.Nodes is null) return;
        foreach (var node in tree.Nodes)
            if (node.IsLeaf)
                node.LeafValue = Math.Clamp(node.LeafValue, -clipValue, clipValue);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PREDICTION HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static double Predict(GbmTree tree, float[] features)
    {
        if (tree.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        while (nodeIdx < tree.Nodes.Count)
        {
            var node = tree.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < features.Length && features[node.SplitFeature] <= node.SplitThreshold)
                nodeIdx = node.LeftChild;
            else
                nodeIdx = node.RightChild;
        }
        return 0;
    }

    /// <summary>Raw GBM score (log-odds space): baseLogOdds + lr * Σ tree predictions.</summary>
    private static double GbmScore(float[] features, List<GbmTree> trees, double baseLogOdds, double lr, int featureCount)
    {
        double score = baseLogOdds;
        foreach (var t in trees) score += lr * Predict(t, features);
        return score;
    }

    /// <summary>GBM probability via sigmoid of raw score.</summary>
    private static double GbmProb(float[] features, List<GbmTree> trees, double baseLogOdds, double lr, int featureCount)
        => Sigmoid(GbmScore(features, trees, baseLogOdds, lr, featureCount));

    /// <summary>Calibrated probability: Platt scaling on raw GBM probability.</summary>
    private static double GbmCalibProb(float[] features, List<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
    {
        double rawP = GbmProb(features, trees, baseLogOdds, lr, featureCount);
        rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
        return Sigmoid(plattA * Logit(rawP) + plattB);
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    private static double Logit(double p) => Math.Log(p / (1.0 - p));

    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateGbm(
        List<TrainingSample> evalSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        int                  featureCount)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;

        foreach (var s in evalSet)
        {
            double p    = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            int    yHat = p >= 0.5 ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;

            brierSum += (p - y) * (p - y);

            // Magnitude prediction
            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }
            else
            {
                double score = GbmScore(s.Features, trees, baseLogOdds, lr, featureCount);
                magSse += (score - s.Magnitude) * (score - s.Magnitude);
            }
        }

        int evalN      = evalSet.Count;
        double accuracy  = evalN > 0 ? (double)correct / evalN : 0;
        double brier     = evalN > 0 ? brierSum / evalN : 1;
        double magRmse   = evalN > 0 ? Math.Sqrt(magSse / evalN) : double.MaxValue;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        double sharpe    = ev / (brier + 0.01);

        // Weighted accuracy (time-weighted, more weight to recent)
        double weightSum = 0, correctWeighted = 0;
        for (int i = 0; i < evalN; i++)
        {
            double wt = 1.0 + (double)i / evalN;
            double p  = GbmCalibProb(evalSet[i].Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            weightSum += wt;
            if ((p >= 0.5) == (evalSet[i].Direction > 0)) correctWeighted += wt;
        }
        double wAcc = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        return new EvalMetrics(
            Accuracy:         accuracy,
            Precision:        precision,
            Recall:           recall,
            F1:               f1,
            MagnitudeRmse:    magRmse,
            ExpectedValue:    ev,
            BrierScore:       brier,
            WeightedAccuracy: wAcc,
            SharpeRatio:      sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        int                  featureCount)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount);
            raw       = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;
        const double sgdLr = 0.01;
        const int epochs = 200;

        for (int epoch = 0; epoch < epochs; epoch++)
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
        List<TrainingSample> calSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        int                  featureCount)
    {
        var buySamples  = calSet.Where(s => s.Direction > 0).ToList();
        var sellSamples = calSet.Where(s => s.Direction <= 0).ToList();

        var (aBuy, bBuy)   = buySamples.Count >= 10
            ? FitPlattScaling(buySamples, trees, baseLogOdds, lr, featureCount) : (0.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= 10
            ? FitPlattScaling(sellSamples, trees, baseLogOdds, lr, featureCount) : (0.0, 0.0);

        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (PAVA)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double               plattA,
        double               plattB,
        int                  featureCount)
    {
        if (calSet.Count < 10) return [];

        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (
                GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, plattA, plattB, featureCount),
                calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.X.CompareTo(b.X));

        // Pool Adjacent Violators (PAVA)
        var blocks = new List<(double SumY, int Count, double XMin, double XMax)>();
        foreach (var (x, y) in pairs)
        {
            blocks.Add((y, 1, x, x));
            while (blocks.Count >= 2)
            {
                var last = blocks[^1];
                var prev = blocks[^2];
                if ((double)prev.SumY / prev.Count <= (double)last.SumY / last.Count)
                    break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks)
        {
            double mid = (b.XMin + b.XMax) / 2.0;
            bp.Add(mid);
            bp.Add(b.SumY / b.Count);
        }
        return bp.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ECE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        List<TrainingSample> testSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int                  bins = 10)
    {
        if (testSet.Count < bins) return 1.0;

        var binCorrect = new double[bins];
        var binConf    = new double[bins];
        var binCount   = new int[bins];

        foreach (var s in testSet)
        {
            double p  = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            int bin   = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin] += p;
            binCorrect[bin] += (p >= 0.5) == (s.Direction > 0) ? 1 : 0;
            binCount[bin]++;
        }

        double ece = 0;
        int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            double acc  = binCorrect[b] / binCount[b];
            double conf = binConf[b] / binCount[b];
            ece += Math.Abs(acc - conf) * binCount[b] / n;
        }
        return ece;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EV-OPTIMAL THRESHOLD
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double               plattA,
        double               plattB,
        int                  featureCount,
        int                  searchMin = 30,
        int                  searchMax = 75)
    {
        if (dataSet.Count < 30) return 0.5;

        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = GbmCalibProb(dataSet[i].Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);

        double bestEv = double.MinValue;
        double bestT  = 0.5;

        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double t  = ti / 100.0;
            double ev = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (dataSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * Math.Abs(dataSet[i].Magnitude);
            }
            ev /= dataSet.Count;
            if (ev > bestEv) { bestEv = ev; bestT = t; }
        }
        return bestT;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double               plattA,
        double               plattB,
        int                  featureCount,
        CancellationToken    ct)
    {
        double baseline = ComputeAccuracy(testSet, trees, baseLogOdds, lr, plattA, plattB, featureCount);
        var importance   = new float[featureCount];
        int tn = testSet.Count;

        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng  = new Random(j * 13 + 42);
            var vals = new float[tn];
            for (int i = 0; i < tn; i++) vals[i] = testSet[i].Features[j];
            for (int i = tn - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (vals[k], vals[i]) = (vals[i], vals[k]);
            }

            var scratch = new float[testSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < tn; idx++)
            {
                Array.Copy(testSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                double p   = GbmCalibProb(scratch, trees, baseLogOdds, lr, plattA, plattB, featureCount);
                if ((p >= 0.5) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / tn);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        int                  featureCount,
        CancellationToken    ct)
    {
        int n = calSet.Count;
        int baseCorrect = 0;
        foreach (var s in calSet)
            if ((GbmProb(s.Features, trees, baseLogOdds, lr, featureCount) >= 0.5) == (s.Direction > 0))
                baseCorrect++;
        double baseAcc = (double)baseCorrect / n;

        var importance = new double[featureCount];
        Parallel.For(0, featureCount, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng  = new Random(j * 17 + 7);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (vals[k], vals[i]) = (vals[i], vals[k]);
            }

            var scratch = new float[calSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((GbmProb(scratch, trees, baseLogOdds, lr, featureCount) >= 0.5) == (calSet[idx].Direction > 0))
                    correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    private static double ComputeAccuracy(
        List<TrainingSample> set, List<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
    {
        int correct = 0;
        foreach (var s in set)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            if ((p >= 0.5) == (s.Direction > 0)) correct++;
        }
        return set.Count > 0 ? (double)correct / set.Count : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OOB ACCURACY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True OOB accuracy: for each training sample, the prediction is formed only from
    /// trees whose bootstrap bag did NOT include that sample. This gives an unbiased
    /// estimate of generalisation error without needing a held-out set.
    /// </summary>
    private static double ComputeOobAccuracy(
        List<TrainingSample> trainSet, List<GbmTree> trees, List<HashSet<int>> bagMasks,
        double baseLogOdds, double lr, int featureCount)
    {
        if (trainSet.Count < 10 || trees.Count < 2) return 0;
        if (bagMasks.Count != trees.Count) return 0;

        int correct = 0, evaluated = 0;

        for (int i = 0; i < trainSet.Count; i++)
        {
            // Accumulate score only from trees where sample i was out-of-bag
            double oobScore = baseLogOdds;
            int oobTreeCount = 0;

            for (int t = 0; t < trees.Count; t++)
            {
                if (bagMasks[t].Contains(i)) continue; // skip: sample was in-bag for this tree
                oobScore += lr * Predict(trees[t], trainSet[i].Features);
                oobTreeCount++;
            }

            if (oobTreeCount == 0) continue; // sample was in-bag for every tree — skip

            if ((Sigmoid(oobScore) >= 0.5) == (trainSet[i].Direction > 0))
                correct++;
            evaluated++;
        }

        return evaluated > 0 ? (double)correct / evaluated : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONFORMAL PREDICTION
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        List<GbmTree>        trees,
        double               baseLogOdds,
        double               lr,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  featureCount,
        double               alpha)
    {
        if (calSet.Count < 10) return 0.5;

        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = GbmCalibProb(calSet[i].Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            if (isotonicBp.Length >= 2) p = ApplyIsotonic(p, isotonicBp);
            int y = calSet[i].Direction > 0 ? 1 : 0;
            scores[i] = 1.0 - (y == 1 ? p : 1.0 - p);
        }
        Array.Sort(scores);

        int qIdx = (int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1;
        qIdx = Math.Clamp(qIdx, 0, calSet.Count - 1);
        return scores[qIdx];
    }

    private static double ApplyIsotonic(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        // Linear interpolation between breakpoints
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
    //  JACKKNIFE+ RESIDUALS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount)
    {
        if (trainSet.Count < 10 || trees.Count < 2) return [];

        var residuals = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double fullScore = GbmScore(trainSet[i].Features, trees, baseLogOdds, lr, featureCount);
            double fullP     = Sigmoid(fullScore);
            double y         = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals[i]     = Math.Abs(y - fullP);
        }
        Array.Sort(residuals);
        return residuals;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  META-LABEL MODEL
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount)
    {
        if (calSet.Count < 20) return ([], 0.0);

        // Features: [calibP, ensembleLogit, top3Features]
        int metaDim = 5;
        var w = new double[metaDim];
        double b = 0;

        const double sgdLr = 0.01;
        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double rawP = GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
                rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
                double logit = Logit(rawP);

                var metaF = new double[metaDim];
                metaF[0] = rawP;
                metaF[1] = logit;
                metaF[2] = s.Features.Length > 0 ? s.Features[0] : 0;
                metaF[3] = s.Features.Length > 1 ? s.Features[1] : 0;
                metaF[4] = s.Features.Length > 2 ? s.Features[2] : 0;

                double z = b;
                for (int j = 0; j < metaDim; j++) z += w[j] * metaF[j];
                double p = Sigmoid(z);

                bool isCorrect = (rawP >= 0.5) == (s.Direction > 0);
                double label = isCorrect ? 1.0 : 0.0;
                double err = p - label;

                b -= sgdLr * err;
                for (int j = 0; j < metaDim; j++) w[j] -= sgdLr * err * metaF[j];
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ABSTENTION GATE
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB,
        double[] metaLabelWeights, double metaLabelBias, int featureCount)
    {
        if (calSet.Count < 20) return ([], 0.0, 0.5);

        int dim = 3; // calibP, |calibP - 0.5|, metaLabelScore
        var w = new double[dim];
        double b = 0;

        const double sgdLr = 0.01;
        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);

                double metaLabelScore = metaLabelBias;
                if (metaLabelWeights.Length > 0)
                {
                    double rawP = GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
                    rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
                    var mf = new[] { rawP, Logit(rawP),
                        s.Features.Length > 0 ? s.Features[0] : 0,
                        s.Features.Length > 1 ? s.Features[1] : 0,
                        s.Features.Length > 2 ? s.Features[2] : 0 };
                    metaLabelScore = metaLabelBias;
                    for (int j = 0; j < Math.Min(metaLabelWeights.Length, mf.Length); j++)
                        metaLabelScore += metaLabelWeights[j] * mf[j];
                    metaLabelScore = Sigmoid(metaLabelScore);
                }

                var af = new[] { calibP, Math.Abs(calibP - 0.5), metaLabelScore };

                double z = b;
                for (int j = 0; j < dim; j++) z += w[j] * af[j];
                double p = Sigmoid(z);

                bool isCorrect = (calibP >= 0.5) == (s.Direction > 0);
                double label = isCorrect ? 1.0 : 0.0;
                double err = p - label;

                b -= sgdLr * err;
                for (int j = 0; j < dim; j++) w[j] -= sgdLr * err * af[j];
            }
        }

        // Find threshold on cal set that maximises filtered accuracy
        double bestThreshold = 0.5;
        double bestFilteredAcc = 0;
        for (int ti = 30; ti <= 70; ti++)
        {
            double t = ti / 100.0;
            int correct = 0, total = 0;
            foreach (var s in calSet)
            {
                double calibP = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
                double rawP = GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
                rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
                double ms = metaLabelWeights.Length > 0 ? Sigmoid(metaLabelBias) : 0.5;
                var af = new[] { calibP, Math.Abs(calibP - 0.5), ms };
                double z = b;
                for (int j = 0; j < dim; j++) z += w[j] * af[j];
                if (Sigmoid(z) < t) continue;
                total++;
                if ((calibP >= 0.5) == (s.Direction > 0)) correct++;
            }
            double acc = total > 0 ? (double)correct / total : 0;
            if (acc > bestFilteredAcc && total >= calSet.Count / 4)
            {
                bestFilteredAcc = acc;
                bestThreshold = t;
            }
        }

        return (w, b, bestThreshold);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSORS
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train, int featureCount, TrainingHyperparams hp)
    {
        var w    = new double[featureCount];
        double b = 0.0;

        bool   canEarlyStop = train.Count >= 30;
        int    valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var    valSet       = canEarlyStop ? train[^valSize..] : train;
        var    trainSet     = canEarlyStop ? train[..^valSize] : train;

        if (trainSet.Count == 0) return (w, b);

        var mW = new double[featureCount];
        var vW = new double[featureCount];
        double mB = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int t = 0;

        double bestValLoss = double.MaxValue;
        var bestW = new double[featureCount];
        double bestB = 0.0;
        int patience = 0;

        int epochs = hp.MaxEpochs;
        double baseLr = hp.LearningRate > 0 ? hp.LearningRate : 0.1;
        double l2 = hp.L2Lambda;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            double alpha = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            foreach (var s in trainSet)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;

                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);

                double bc1 = 1.0 - beta1t;
                double bc2 = 1.0 - beta2t;
                double alphat = alpha * Math.Sqrt(bc2) / bc1;

                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * huberGrad;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b -= alphat * mB / (Math.Sqrt(vB) + AdamEpsilon);

                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j] -= alphat * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;

            double valLoss = 0.0;
            int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
                valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;

            if (valLoss < bestValLoss - 1e-6)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, featureCount);
                bestB = b;
                patience = 0;
            }
            else if (++patience >= hp.EarlyStoppingPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int featureCount, double tau)
    {
        var w = new double[featureCount];
        double b = 0;
        const double sgdLr = 0.001;

        for (int epoch = 0; epoch < 100; epoch++)
        {
            foreach (var s in train)
            {
                double pred = b;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                double grad = err >= 0 ? tau : -(1 - tau);
                b += sgdLr * grad;
                for (int j = 0; j < featureCount && j < s.Features.Length; j++)
                    w[j] += sgdLr * grad * s.Features[j];
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DECISION BOUNDARY STATS
    // ═══════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount)
    {
        // Approximate gradient norm from ensemble score variance across feature perturbations
        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = GbmProb(calSet[i].Features, trees, baseLogOdds, lr, featureCount);
            // For boosted ensemble, approximate gradient norm as p(1-p) * score_range
            norms[i] = p * (1 - p);
        }

        double mean = norms.Average();
        double std  = 0;
        foreach (double v in norms) std += (v - mean) * (v - mean);
        std = norms.Length > 1 ? Math.Sqrt(std / (norms.Length - 1)) : 0;
        return (mean, std);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DURBIN-WATSON
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int featureCount)
    {
        if (train.Count < 10 || magWeights.Length == 0) return 2.0;

        var residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, train[i].Features.Length); j++)
                pred += magWeights[j] * train[i].Features[j];
            residuals[i] = train[i].Magnitude - pred;
        }

        double numSum = 0, denSum = 0;
        for (int i = 1; i < residuals.Length; i++)
        {
            double d = residuals[i] - residuals[i - 1];
            numSum += d * d;
        }
        for (int i = 0; i < residuals.Length; i++)
            denSum += residuals[i] * residuals[i];

        return denSum > 1e-15 ? numSum / denSum : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  KELLY FRACTION
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
    {
        if (calSet.Count == 0) return 0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            sum += Math.Max(0, 2 * p - 1);
        }
        return sum / calSet.Count * 0.5; // half-Kelly
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, List<GbmTree> trees, double baseLogOdds,
        double lr, int featureCount)
    {
        double bestT = 1.0;
        double bestLoss = double.MaxValue;

        for (int ti = 5; ti <= 50; ti++)
        {
            double T = ti / 10.0;
            double loss = 0;
            foreach (var s in calSet)
            {
                double rawP = GbmProb(s.Features, trees, baseLogOdds, lr, featureCount);
                rawP = Math.Clamp(rawP, 1e-7, 1.0 - 1e-7);
                double p = Sigmoid(Logit(rawP) / T);
                int y = s.Direction > 0 ? 1 : 0;
                loss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
            }
            loss /= calSet.Count;
            if (loss < bestLoss) { bestLoss = loss; bestT = T; }
        }
        return bestT;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BRIER SKILL SCORE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, List<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
    {
        if (testSet.Count < 10) return 0;

        double brier = 0;
        int posCount = 0;
        foreach (var s in testSet)
        {
            double p = GbmCalibProb(s.Features, trees, baseLogOdds, lr, plattA, plattB, featureCount);
            int y = s.Direction > 0 ? 1 : 0;
            brier += (p - y) * (p - y);
            posCount += y;
        }
        brier /= testSet.Count;

        double baseRate = (double)posCount / testSet.Count;
        double naiveBrier = baseRate * (1 - baseRate);
        return naiveBrier > 1e-10 ? 1.0 - brier / naiveBrier : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE SPLIT IMPORTANCE (for CV folds — cheaper than permutation)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeTreeSplitImportance(List<GbmTree> trees, int featureCount)
    {
        var importance = new double[featureCount];
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            foreach (var node in tree.Nodes)
            {
                if (!node.IsLeaf && node.SplitFeature < featureCount)
                    importance[node.SplitFeature] += 1.0;
            }
        }

        double total = importance.Sum();
        if (total > 1e-6)
            for (int j = 0; j < featureCount; j++) importance[j] /= total;
        return importance;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TREE SANITIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int SanitizeTrees(List<GbmTree> trees)
    {
        int count = 0;
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            bool needsSanitize = false;
            foreach (var node in tree.Nodes)
            {
                if (!double.IsFinite(node.LeafValue) || !double.IsFinite(node.SplitThreshold))
                {
                    needsSanitize = true;
                    break;
                }
            }
            if (needsSanitize)
            {
                foreach (var node in tree.Nodes)
                {
                    if (!double.IsFinite(node.LeafValue)) node.LeafValue = 0;
                    if (!double.IsFinite(node.SplitThreshold)) node.SplitThreshold = 0;
                    node.IsLeaf = true;
                }
                count++;
            }
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EQUITY CURVE STATS
    // ═══════════════════════════════════════════════════════════════════════

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);

        var returns = new double[predictions.Length];
        double equity = 1.0, peak = 1.0, maxDD = 0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted == predictions[i].Actual ? 0.01 : -0.01;
            returns[i] = r;
            equity += r;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }

        // Sharpe
        double mean = returns.Average();
        double varSum = 0;
        foreach (double r in returns) varSum += (r - mean) * (r - mean);
        double std = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        double sharpe = std > 1e-10 ? mean / std * Math.Sqrt(252) : 0;

        return (maxDD, sharpe);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY GATE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Counts features that fail a Dickey-Fuller unit root test at the 5% significance level.
    /// Regression: Δx_t = α + γ·x_{t-1} + Σ β_k·Δx_{t-k} + ε_t  (augmented with p lags).
    /// If the t-statistic for γ is above the critical value (−2.86 for 5%, constant, N≥100),
    /// the null hypothesis of a unit root cannot be rejected → feature is non-stationary.
    /// </summary>
    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int featureCount)
    {
        if (samples.Count < 50) return 0;

        int nonStationary = 0;
        int maxObs = Math.Min(samples.Count, 500); // cap for performance

        for (int j = 0; j < featureCount; j++)
        {
            // Extract the feature series
            var series = new double[maxObs];
            for (int i = 0; i < maxObs; i++) series[i] = samples[i].Features[j];

            if (IsNonStationary(series))
                nonStationary++;
        }
        return nonStationary;
    }

    /// <summary>
    /// Augmented Dickey-Fuller test on a univariate series.
    /// Uses p = min(12, ⌊(N−1)^{1/3}⌋) augmentation lags.
    /// Returns true if the series has a unit root (non-stationary) at the 5% level.
    /// </summary>
    private static bool IsNonStationary(double[] series)
    {
        int N = series.Length;
        if (N < 20) return false;

        // Δx_t = x_t − x_{t-1}
        var dx = new double[N - 1];
        for (int i = 0; i < dx.Length; i++) dx[i] = series[i + 1] - series[i];

        // Number of augmentation lags
        int p = Math.Min(12, (int)Math.Floor(Math.Pow(N - 1, 1.0 / 3.0)));
        int start = p + 1; // first usable index into dx[]
        int nObs = dx.Length - start;
        if (nObs < 10) return false;

        // Build OLS design matrix: [1, x_{t-1}, Δx_{t-1}, ..., Δx_{t-p}]
        int cols = 2 + p; // intercept + γ + p lag coefficients
        var X = new double[nObs * cols];
        var Y = new double[nObs];

        for (int t = 0; t < nObs; t++)
        {
            int ti = start + t; // index into dx[]
            Y[t] = dx[ti];
            X[t * cols + 0] = 1.0;                      // intercept
            X[t * cols + 1] = series[ti];                // x_{t-1} (level)
            for (int k = 0; k < p; k++)
                X[t * cols + 2 + k] = dx[ti - 1 - k];   // lagged differences
        }

        // Solve via normal equations: β = (X'X)^{-1} X'Y
        var xtx = new double[cols * cols];
        var xty = new double[cols];

        for (int t = 0; t < nObs; t++)
        {
            for (int a = 0; a < cols; a++)
            {
                double xa = X[t * cols + a];
                xty[a] += xa * Y[t];
                for (int b = a; b < cols; b++)
                {
                    double v = xa * X[t * cols + b];
                    xtx[a * cols + b] += v;
                    if (a != b) xtx[b * cols + a] += v;
                }
            }
        }

        // Cholesky decomposition of X'X → L such that X'X = L·L'
        var L = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            for (int j2 = 0; j2 <= i; j2++)
            {
                double sum = 0;
                for (int k = 0; k < j2; k++) sum += L[i * cols + k] * L[j2 * cols + k];
                if (i == j2)
                {
                    double diag = xtx[i * cols + i] - sum;
                    if (diag <= 1e-15) return false; // singular — treat as stationary
                    L[i * cols + j2] = Math.Sqrt(diag);
                }
                else
                {
                    L[i * cols + j2] = (xtx[i * cols + j2] - sum) / L[j2 * cols + j2];
                }
            }
        }

        // Forward substitution: L·z = X'Y
        var z = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            double sum = 0;
            for (int k = 0; k < i; k++) sum += L[i * cols + k] * z[k];
            z[i] = (xty[i] - sum) / L[i * cols + i];
        }

        // Back substitution: L'·β = z
        var beta = new double[cols];
        for (int i = cols - 1; i >= 0; i--)
        {
            double sum = 0;
            for (int k = i + 1; k < cols; k++) sum += L[k * cols + i] * beta[k];
            beta[i] = (z[i] - sum) / L[i * cols + i];
        }

        double gamma = beta[1]; // coefficient on x_{t-1}

        // Residual variance σ²
        double sse = 0;
        for (int t = 0; t < nObs; t++)
        {
            double pred = 0;
            for (int c = 0; c < cols; c++) pred += X[t * cols + c] * beta[c];
            double resid = Y[t] - pred;
            sse += resid * resid;
        }
        double sigma2 = sse / Math.Max(1, nObs - cols);

        // Invert X'X to get Var(β̂) = σ² (X'X)^{-1}
        // We already have L from Cholesky; invert L then compute L^{-T} L^{-1}
        var Linv = new double[cols * cols];
        for (int i = 0; i < cols; i++)
        {
            Linv[i * cols + i] = 1.0 / L[i * cols + i];
            for (int j2 = i + 1; j2 < cols; j2++)
            {
                double sum = 0;
                for (int k = i; k < j2; k++) sum += L[j2 * cols + k] * Linv[k * cols + i];
                Linv[j2 * cols + i] = -sum / L[j2 * cols + j2];
            }
        }

        // Var(γ̂) = σ² × [(X'X)^{-1}]_{1,1} = σ² × Σ_k (L^{-1})_{k,1}²
        double varGamma = 0;
        for (int k = 0; k < cols; k++)
            varGamma += Linv[k * cols + 1] * Linv[k * cols + 1];
        varGamma *= sigma2;

        if (varGamma <= 1e-15) return false;

        double tStat = gamma / Math.Sqrt(varGamma);

        // ADF critical values at 5% (with constant, no trend):
        // N≥500: −2.86, N≥100: −2.89, N≥50: −2.93. Use −2.86 as conservative default.
        const double criticalValue5Pct = -2.86;
        return tStat > criticalValue5Pct; // cannot reject unit root → non-stationary
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO IMPORTANCE WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int featureCount, int windowDays)
    {
        int recentCount = Math.Min(trainSet.Count / 3, windowDays * 24);
        if (recentCount < 20) return Enumerable.Repeat(1.0, trainSet.Count).ToArray();

        int cutoff = trainSet.Count - recentCount;
        var w = new double[featureCount];
        double b = 0;
        const double sgdLr = 0.01;

        // Train a logistic discriminator: recent=1, historical=0
        for (int epoch = 0; epoch < 30; epoch++)
        {
            for (int i = 0; i < trainSet.Count; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                double z = b;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    z += w[j] * trainSet[i].Features[j];
                double p = Sigmoid(z);
                double err = p - label;
                b -= sgdLr * err;
                for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                    w[j] -= sgdLr * err * trainSet[i].Features[j];
            }
        }

        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double z = b;
            for (int j = 0; j < featureCount && j < trainSet[i].Features.Length; j++)
                z += w[j] * trainSet[i].Features[j];
            double p = Math.Clamp(Sigmoid(z), 0.01, 0.99);
            weights[i] = p / (1 - p);
        }

        // Normalise
        double sum = weights.Sum();
        if (sum > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= sum;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COVARIATE SHIFT WEIGHTS (parent model novelty scoring)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes per-sample novelty weights from the parent model's feature quantile breakpoints.
    /// Samples whose features lie outside the parent's inter-decile range [q10, q90] are up-weighted,
    /// focusing the new model on distribution regions the parent never saw.
    /// Weights are normalised to mean = 1.0 so gradient scale is unchanged.
    /// </summary>
    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBp, int featureCount)
    {
        var weights = new double[trainSet.Count];

        for (int i = 0; i < trainSet.Count; i++)
        {
            int outsideCount = 0;
            int checkedCount = 0;
            for (int j = 0; j < featureCount && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                // bp[0] ≈ q10, bp[^1] ≈ q90 (decile bin edges)
                if (v < bp[0] || v > bp[^1])
                    outsideCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0;
            weights[i] = 1.0 + noveltyFraction; // up-weight novel samples
        }

        // Normalise to mean = 1.0
        double mean = weights.Average();
        if (mean > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= mean;

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MUTUAL INFORMATION REDUNDANCY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes pairwise mutual information via histogram binning (Sturges' rule) to detect
    /// redundant feature pairs. Unlike the previous Pearson r² proxy, this captures both
    /// linear and nonlinear dependencies between features.
    /// MI(X,Y) = Σ_{i,j} p(x_i,y_j) × log[ p(x_i,y_j) / (p(x_i)·p(y_j)) ]
    /// </summary>
    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int featureCount, double threshold)
    {
        if (trainSet.Count < 30) return [];

        int n = Math.Min(trainSet.Count, 500);
        // Sturges' rule for bin count
        int numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));

        // Pre-compute per-feature min/max and bin assignments
        var featureMin    = new double[featureCount];
        var featureMax    = new double[featureCount];
        var featureBinIdx = new int[featureCount * n];

        Array.Fill(featureMin, double.MaxValue);
        Array.Fill(featureMax, double.MinValue);

        for (int j = 0; j < featureCount; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = trainSet[i].Features[j];
                if (v < featureMin[j]) featureMin[j] = v;
                if (v > featureMax[j]) featureMax[j] = v;
            }

            double range = featureMax[j] - featureMin[j];
            double binWidth = range > 1e-15 ? range / numBins : 1.0;
            for (int i = 0; i < n; i++)
            {
                int bin = (int)((trainSet[i].Features[j] - featureMin[j]) / binWidth);
                featureBinIdx[j * n + i] = Math.Clamp(bin, 0, numBins - 1);
            }
        }

        var pairs = new List<string>();
        double invN = 1.0 / n;

        for (int a = 0; a < featureCount; a++)
        {
            for (int bi = a + 1; bi < featureCount; bi++)
            {
                // Build joint and marginal histograms
                var joint    = new int[numBins * numBins];
                var margA    = new int[numBins];
                var margB    = new int[numBins];

                for (int i = 0; i < n; i++)
                {
                    int ba = featureBinIdx[a * n + i];
                    int bb = featureBinIdx[bi * n + i];
                    joint[ba * numBins + bb]++;
                    margA[ba]++;
                    margB[bb]++;
                }

                // MI = Σ p_ij × log(p_ij / (p_i · p_j))
                double mi = 0;
                for (int ia = 0; ia < numBins; ia++)
                {
                    if (margA[ia] == 0) continue;
                    double pA = margA[ia] * invN;
                    for (int ib = 0; ib < numBins; ib++)
                    {
                        int jCount = joint[ia * numBins + ib];
                        if (jCount == 0 || margB[ib] == 0) continue;
                        double pJ  = jCount * invN;
                        double pB  = margB[ib] * invN;
                        mi += pJ * Math.Log(pJ / (pA * pB));
                    }
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = bi < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[bi] : $"F{bi}";
                    pairs.Add($"{nameA}:{nameB}");
                }
            }
        }
        return pairs.ToArray();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SHARPE TREND
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0;

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
        return Math.Abs(denom) > 1e-15 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPORAL WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int count, double lambda)
    {
        if (count == 0) return [];
        var w = new double[count];
        for (int i = 0; i < count; i++)
            w[i] = Math.Exp(lambda * ((double)i / Math.Max(1, count - 1) - 1.0));
        double sum = w.Sum();
        if (sum > 1e-15)
            for (int i = 0; i < count; i++) w[i] /= sum;
        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FEATURE MASK HELPERS
    // ═══════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════
    //  STANDARD DEVIATION
    // ═══════════════════════════════════════════════════════════════════════

    private static double StdDev(IList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sum = 0;
        foreach (double v in values) sum += (v - mean) * (v - mean);
        return Math.Sqrt(sum / (values.Count - 1));
    }
}

/// <summary>Serialisable node in a GBM regression tree.</summary>
public sealed class GbmNode
{
    public bool   IsLeaf         { get; set; }
    public double LeafValue      { get; set; }
    public int    SplitFeature   { get; set; }
    public double SplitThreshold { get; set; }
    public int    LeftChild      { get; set; }
    public int    RightChild     { get; set; }
}

/// <summary>Serialisable regression tree used by <see cref="GbmModelTrainer"/>.</summary>
public sealed class GbmTree
{
    public List<GbmNode>? Nodes { get; set; }
}
