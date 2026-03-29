using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade TabNet trainer (Rec #389). Sequential attentive feature selection across
/// N_steps with sparsemax-approximate softmax and per-step fully-connected processing.
/// Registered with key "tabnet".
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples.</item>
///   <item>Walk-forward cross-validation (expanding window, embargo, purging) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Final model splits: 70% train | 10% Platt calibration | ~20% held-out test with embargo.</item>
///   <item>Adam-optimised TabNet (wAttention, wf, wOut, bOut) with cosine LR schedule and early stopping.</item>
///   <item>Warm-start from prior TABNET snapshot (weight loading from Weights/Biases fields).</item>
///   <item>Adaptive label smoothing from magnitude-ambiguity proxy.</item>
///   <item>Temporal decay + density-ratio + covariate-shift sample weighting.</item>
///   <item>Platt scaling (A, B) fitted on the calibration fold after the model is frozen.</item>
///   <item>Class-conditional Platt scaling (separate Buy/Sell calibrators).</item>
///   <item>Isotonic calibration (PAVA) applied post-Platt for monotonic probability correction.</item>
///   <item>ECE (Expected Calibration Error) computed post-calibration on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set.</item>
///   <item>Magnitude linear regressor trained with Adam + Huber loss + cosine LR + early stopping.</item>
///   <item>Permutation feature importance with optional feature pruning re-train pass.</item>
///   <item>Conformal prediction (split-conformal qHat) for prediction set coverage.</item>
///   <item>Jackknife+ residuals for prediction intervals.</item>
///   <item>Meta-label secondary classifier for filtering low-quality signals.</item>
///   <item>Abstention gate (selective prediction) for suppressing uncertain signals.</item>
///   <item>Quantile magnitude regressor (pinball loss) for asymmetric risk sizing.</item>
///   <item>Decision boundary distance analytics.</item>
///   <item>Durbin-Watson autocorrelation test on magnitude residuals.</item>
///   <item>Average Kelly fraction for position sizing guidance.</item>
///   <item>Mutual-information feature redundancy check (Sturges' rule binning).</item>
///   <item>Temperature scaling alternative calibration.</item>
///   <item>Brier Skill Score computation.</item>
///   <item>NaN/Inf weight sanitization.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Stationarity gate (soft ADF check, variance-ratio proxy).</item>
///   <item>Incremental update fast-path for warm-start regime adaptation.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.TabNet)]
public sealed class TabNetModelTrainer : IMLModelTrainer
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string ModelType    = "TABNET";
    private const string ModelVersion = "2.0";

    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly ILogger<TabNetModelTrainer> _logger;

    public TabNetModelTrainer(ILogger<TabNetModelTrainer> logger) => _logger = logger;

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

    // ── Core training logic (synchronous, runs on thread-pool) ──────────────

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
        int nSteps  = hp.K > 0 ? hp.K : 3;
        double lr   = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        double sparsity = 0.0001;
        int epochs  = hp.MaxEpochs > 0 ? hp.MaxEpochs : 50;

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
            "TabNetModelTrainer: n={N} F={F} steps={S} epochs={E} lr={LR}",
            samples.Count, F, nSteps, epochs, lr);

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

        // ── 2. Walk-forward cross-validation ───────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, F, nSteps, lr, sparsity, epochs, ct);
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
        var (wAttention, wf, wOut, bOut, mtMagW, mtMagB) = FitTabNet(
            trainSet, F, nSteps, lr, sparsity, epochs, effectiveLabelSmoothing,
            warmStart, densityWeights, hp.TemporalDecayLambda, hp.L2Lambda,
            hp.EarlyStoppingPatience, hp.MagLossWeight, ct);

        _logger.LogInformation("TabNet fitted: steps={S}", nSteps);

        // ── 4b. Weight sanitization ────────────────────────────────────────
        int sanitizedCount = SanitizeWeights(wAttention, wf, ref wOut, ref bOut);
        if (sanitizedCount > 0)
            _logger.LogWarning("TabNet sanitized {N} non-finite weight values.", sanitizedCount);

        // ── 5. Platt calibration ───────────────────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, wAttention, wf, wOut, bOut, nSteps, F);
        _logger.LogDebug("TabNet Platt: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 5b. Class-conditional Platt ────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, wAttention, wf, wOut, bOut, nSteps, F);
        _logger.LogDebug(
            "TabNet class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 5c. Kelly fraction ─────────────────────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(
            calSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
        _logger.LogDebug("TabNet average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 6. Magnitude regressor — prefer jointly-trained head when available ──
        (double[] magWeights, double magBias) = hp.MagLossWeight > 0.0 && mtMagW.Length > 0
            ? (mtMagW, mtMagB)
            : FitLinearRegressor(trainSet, F, hp);

        // ── 7. Evaluation on held-out test set ─────────────────────────────
        var finalMetrics = EvaluateTabNet(
            testSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB, magWeights, magBias);
        _logger.LogInformation(
            "TabNet eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE ─────────────────────────────────────────────────────────
        double ece = ComputeEce(testSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
        _logger.LogInformation("TabNet post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal threshold ────────────────────────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        _logger.LogInformation("TabNet EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 10. Permutation feature importance ─────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: MLFeatureHelper.FeatureNames[idx]))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "TabNet top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Cal-set importance (for warm-start transfer) ──────────────
        double[] calImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, wAttention, wf, wOut, bOut, nSteps, F, ct)
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
            var (pwA, pwf, pwO, pbO, pMtMagW, pMtMagB) = FitTabNet(maskedTrain, maskedF, nSteps, lr, sparsity, prunedEpochs,
                effectiveLabelSmoothing, null, densityWeights, hp.TemporalDecayLambda, hp.L2Lambda,
                hp.EarlyStoppingPatience, hp.MagLossWeight, ct);
            var (pA, pB)               = FitPlattScaling(maskedCal, pwA, pwf, pwO, pbO, nSteps, maskedF);
            (double[] pmw, double pmb) = hp.MagLossWeight > 0.0 && pMtMagW.Length > 0
                ? (pMtMagW, pMtMagB)
                : FitLinearRegressor(maskedTrain, maskedF, hp);
            var prunedMetrics    = EvaluateTabNet(maskedTest, pwA, pwf, pwO, pbO, nSteps, maskedF, pA, pB, pmw, pmb);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation("TabNet pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                wAttention   = pwA; wf = pwf; wOut = pwO; bOut = pbO;
                magWeights   = pmw; magBias = pmb;
                plattA       = pA;  plattB  = pB;
                finalMetrics = prunedMetrics;
                F            = maskedF;
                trainSet     = maskedTrain;  // downstream DW/PSI use masked features
                testSet      = maskedTest;   // downstream BSS uses masked features
                calSet       = maskedCal;    // downstream conformal/temperature use masked features
                ece              = ComputeEce(maskedTest, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, wAttention, wf, wOut, bOut, nSteps, F,
                    plattA, plattB, hp.ThresholdSearchMin, hp.ThresholdSearchMax);
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(maskedCal, wAttention, wf, wOut, bOut, nSteps, F);
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
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
        var postPruneCalSet = prunedCount > 0 ? ApplyMask(calSet, activeMask) : calSet;

        double[] isotonicBp = FitIsotonicCalibration(
            postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
        _logger.LogInformation("TabNet isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(
            postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB, isotonicBp, conformalAlpha);
        _logger.LogInformation("TabNet conformal qHat={QHat:F4} ({Cov:P0} coverage)",
            conformalQHat, hp.ConformalCoverage);

        // ── 11c. Jackknife+ residuals ──────────────────────────────────────
        double[] jackknifeResiduals = ComputeJackknifeResiduals(
            trainSet, wAttention, wf, wOut, bOut, nSteps, F);
        _logger.LogInformation("TabNet Jackknife+ residuals: {N} samples", jackknifeResiduals.Length);

        // ── 11d. Meta-label model ──────────────────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F);
        _logger.LogDebug("TabNet meta-label model: bias={B:F4}", metaLabelBias);

        // ── 11e. Abstention gate ───────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
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
        var (dbMean, dbStd) = postPruneCalSet.Count >= 10
            ? ComputeDecisionBoundaryStats(postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F)
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
        if (hp.FitTemperatureScale && postPruneCalSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(postPruneCalSet, wAttention, wf, wOut, bOut, nSteps, F);
            _logger.LogDebug("TabNet temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 11k. Brier Skill Score ─────────────────────────────────────────
        double brierSkillScore = ComputeBrierSkillScore(
            testSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
        _logger.LogInformation("TabNet BSS={BSS:F4}", brierSkillScore);

        // ── 11l. PSI baseline ──────────────────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 12. Mean attention summary (for inspection / scoring) ──────────
        double[] meanAttn = new double[F];
        for (int s = 0; s < nSteps; s++)
            for (int j = 0; j < F; j++) meanAttn[j] += wAttention[s][j] / nSteps;

        // ── 13. Serialise model snapshot ───────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = nSteps,
            Weights                    = wf,
            Biases                     = [bOut],
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
            FeatureSubsetIndices       = polyTopIdx.Length > 0 ? [polyTopIdx] : null,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "TabNetModelTrainer complete: steps={S}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            nSteps, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TABNET FITTING
    //  Adam optimiser, cosine LR schedule, early stopping, warm-start
    // ═══════════════════════════════════════════════════════════════════════

    private (double[][] WAttention, double[][] Wf, double WOut, double BOut, double[] WMag, double BMag) FitTabNet(
        List<TrainingSample> trainSet,
        int                  F,
        int                  nSteps,
        double               baseLr,
        double               sparsity,
        int                  maxEpochs,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        double               temporalDecayLambda,
        double               l2Lambda,
        int                  patience,
        double               magLossWeight,
        CancellationToken    ct)
    {
        int n = trainSet.Count;
        const double HuberDelta = 1.0;
        bool useMagHead = magLossWeight > 0.0;
        var pool = ArrayPool<double>.Shared;

        // Temporal decay weights blended with density weights
        var temporalWeights = ComputeTemporalWeights(n, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length == n)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++) { temporalWeights[i] *= densityWeights[i]; sum += temporalWeights[i]; }
            if (sum > 1e-15) for (int i = 0; i < n; i++) temporalWeights[i] /= sum;
        }

        // ── Initialise weights ─────────────────────────────────────────────
        var rng = new Random(42);
        double[][] wAttention = Enumerable.Range(0, nSteps).Select(_ => RandomVec(rng, F, 0.1)).ToArray();
        double[][] wf         = Enumerable.Range(0, nSteps).Select(_ => RandomVec(rng, F, 0.1)).ToArray();
        double wOut = rng.NextDouble() * 0.1;
        double bOut = 0.0;

        // Magnitude head: linear hFinal → scalar prediction
        double[] wMag = useMagHead ? RandomVec(rng, F, 0.01) : [];
        double   bMag = 0.0;

        // ── Warm-start: load prior weights when type matches ───────────────
        if (warmStart?.Type == ModelType && warmStart.Weights?.Length == nSteps)
        {
            try
            {
                for (int s = 0; s < nSteps && s < warmStart.Weights.Length; s++)
                    if (warmStart.Weights[s]?.Length == F) wf[s] = (double[])warmStart.Weights[s].Clone();
                if (warmStart.Biases?.Length > 0) bOut = warmStart.Biases[0];
                if (useMagHead && warmStart.MagWeights?.Length == F)
                    wMag = (double[])warmStart.MagWeights.Clone();
                _logger.LogInformation(
                    "TabNet warm-start: loaded prior weights (gen={Gen})", warmStart.GenerationNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TabNet warm-start: failed to load weights, starting fresh.");
            }
        }

        // ── Adam moment buffers (allocated once for entire training run) ───
        double[][] mWA = Enumerable.Range(0, nSteps).Select(_ => new double[F]).ToArray();
        double[][] vWA = Enumerable.Range(0, nSteps).Select(_ => new double[F]).ToArray();
        double[][] mWf = Enumerable.Range(0, nSteps).Select(_ => new double[F]).ToArray();
        double[][] vWf = Enumerable.Range(0, nSteps).Select(_ => new double[F]).ToArray();
        double mWO = 0, vWO = 0, mBO = 0, vBO = 0;
        double[] mWMag = useMagHead ? new double[F] : [];
        double[] vWMag = useMagHead ? new double[F] : [];
        double mBMag = 0, vBMag = 0;

        int adamT = 0;

        // ── Validation split for early stopping (last 10% of train) ───────
        int valSize  = Math.Max(20, n / 10);
        var valSet   = trainSet[^valSize..];
        var fitSet   = trainSet[..^valSize];
        int nFit     = fitSet.Count;

        double bestValLoss = double.MaxValue;
        int    earlyCount  = 0;
        int    bestEpoch   = 0;
        double[][] bestWA  = wAttention.Select(w => (double[])w.Clone()).ToArray();
        double[][] bestWf  = wf.Select(w => (double[])w.Clone()).ToArray();
        double bestWOut = wOut, bestBOut = bOut;
        double[] bestWMag = useMagHead ? (double[])wMag.Clone() : [];
        double   bestBMag = bMag;

        // ── Rent per-sample scratch buffers from ArrayPool ─────────────────
        // Rented once and reused across all samples and epochs to avoid per-sample GC pressure.
        double[] xBuf           = pool.Rent(F);
        double[] priorScalesBuf = pool.Rent(F);
        double[] attLogitsBuf   = pool.Rent(F);
        double[] hFinalBuf      = useMagHead ? pool.Rent(F) : [];
        // Flat storage for per-step attn/masked arrays — avoids nSteps inner allocations per sample
        double[] stepAttnsFlat  = pool.Rent(nSteps * F);
        double[] stepMaskedFlat = pool.Rent(nSteps * F);
        double[] stepZs         = new double[nSteps];

        try
        {
            for (int ep = 0; ep < maxEpochs && !ct.IsCancellationRequested; ep++)
            {
                double cosLr = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * ep / maxEpochs));

                for (int i = 0; i < nFit; i++)
                {
                    adamT++;
                    double sampleWt = temporalWeights.Length > i ? temporalWeights[i] : 1.0 / nFit;

                    for (int j = 0; j < F; j++) xBuf[j] = fitSet[i].Features[j];
                    int rawY = fitSet[i].Direction > 0 ? 1 : 0;
                    double y = labelSmoothing > 0
                        ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                        : rawY;

                    // ── Forward pass ──────────────────────────────────────
                    Array.Fill(priorScalesBuf, 1.0, 0, F);
                    if (useMagHead) Array.Fill(hFinalBuf, 0.0, 0, F);
                    double stepOut = 0;

                    for (int s = 0; s < nSteps; s++)
                    {
                        int baseIdx = s * F;

                        // Attention logits → softmax into flat buffer
                        double maxA = double.MinValue;
                        for (int j = 0; j < F; j++)
                        {
                            attLogitsBuf[j] = priorScalesBuf[j] * wAttention[s][j] * xBuf[j];
                            if (attLogitsBuf[j] > maxA) maxA = attLogitsBuf[j];
                        }
                        double sumA = 0;
                        for (int j = 0; j < F; j++)
                        {
                            stepAttnsFlat[baseIdx + j] = Math.Exp(attLogitsBuf[j] - maxA);
                            sumA += stepAttnsFlat[baseIdx + j];
                        }
                        for (int j = 0; j < F; j++) stepAttnsFlat[baseIdx + j] /= sumA;

                        double z = 0;
                        for (int j = 0; j < F; j++)
                        {
                            priorScalesBuf[j] = Math.Max(1e-6, priorScalesBuf[j] * (1.0 - stepAttnsFlat[baseIdx + j]));
                            double maskedVal = xBuf[j] * stepAttnsFlat[baseIdx + j];
                            stepMaskedFlat[baseIdx + j] = maskedVal;
                            z += wf[s][j] * maskedVal;
                            if (useMagHead) hFinalBuf[j] += stepAttnsFlat[baseIdx + j] * xBuf[j];
                        }
                        stepZs[s] = z;
                        stepOut += Math.Max(z, 0.0);
                    }

                    // Mean attended feature vector across steps (for magnitude head)
                    if (useMagHead)
                        for (int j = 0; j < F; j++) hFinalBuf[j] /= nSteps;

                    double p     = Sigmoid(wOut * stepOut + bOut);
                    double errCE = p - y;

                    // ── Magnitude head forward + Huber gradient ───────────
                    double huberGrad = 0.0;
                    if (useMagHead)
                    {
                        double magPred = bMag;
                        for (int j = 0; j < F; j++) magPred += wMag[j] * hFinalBuf[j];
                        double magErr = magPred - fitSet[i].Magnitude;
                        huberGrad = Math.Abs(magErr) <= HuberDelta
                            ? magErr
                            : HuberDelta * Math.Sign(magErr);
                    }

                    // ── Backward pass (Adam) ──────────────────────────────
                    double bc1 = 1.0 - Math.Pow(AdamBeta1, adamT);
                    double bc2 = 1.0 - Math.Pow(AdamBeta2, adamT);

                    double gWOut = sampleWt * (errCE * stepOut + l2Lambda * wOut);
                    mWO = AdamBeta1 * mWO + (1 - AdamBeta1) * gWOut;
                    vWO = AdamBeta2 * vWO + (1 - AdamBeta2) * gWOut * gWOut;
                    wOut -= cosLr * (mWO / bc1) / (Math.Sqrt(vWO / bc2) + AdamEpsilon);

                    double gBOut = sampleWt * errCE;
                    mBO = AdamBeta1 * mBO + (1 - AdamBeta1) * gBOut;
                    vBO = AdamBeta2 * vBO + (1 - AdamBeta2) * gBOut * gBOut;
                    bOut -= cosLr * (mBO / bc1) / (Math.Sqrt(vBO / bc2) + AdamEpsilon);

                    // wMag and bMag gradients
                    if (useMagHead)
                    {
                        double scaledHuber = sampleWt * magLossWeight * huberGrad;
                        for (int j = 0; j < F; j++)
                        {
                            double gMj = scaledHuber * hFinalBuf[j] + l2Lambda * wMag[j];
                            mWMag[j] = AdamBeta1 * mWMag[j] + (1 - AdamBeta1) * gMj;
                            vWMag[j] = AdamBeta2 * vWMag[j] + (1 - AdamBeta2) * gMj * gMj;
                            wMag[j] -= cosLr * (mWMag[j] / bc1) / (Math.Sqrt(vWMag[j] / bc2) + AdamEpsilon);
                        }
                        double gBMag = sampleWt * magLossWeight * huberGrad;
                        mBMag = AdamBeta1 * mBMag + (1 - AdamBeta1) * gBMag;
                        vBMag = AdamBeta2 * vBMag + (1 - AdamBeta2) * gBMag * gBMag;
                        bMag -= cosLr * (mBMag / bc1) / (Math.Sqrt(vBMag / bc2) + AdamEpsilon);
                    }

                    // Per-step wf and wAttention gradients
                    double dStepOut = sampleWt * errCE * wOut;
                    for (int s = 0; s < nSteps; s++)
                    {
                        int baseIdx = s * F;
                        double dZ   = dStepOut * (stepZs[s] > 0 ? 1.0 : 0.0);

                        for (int j = 0; j < F; j++)
                        {
                            double attnSJ   = stepAttnsFlat[baseIdx + j];
                            double maskedSJ = stepMaskedFlat[baseIdx + j];

                            double gWf = dZ * maskedSJ + l2Lambda * wf[s][j];
                            mWf[s][j] = AdamBeta1 * mWf[s][j] + (1 - AdamBeta1) * gWf;
                            vWf[s][j] = AdamBeta2 * vWf[s][j] + (1 - AdamBeta2) * gWf * gWf;
                            wf[s][j] -= cosLr * (mWf[s][j] / bc1) / (Math.Sqrt(vWf[s][j] / bc2) + AdamEpsilon);

                            // Attention gradient: direction loss + sparsity + magnitude head contribution
                            double magAttnGrad = useMagHead
                                ? sampleWt * magLossWeight * huberGrad * wMag[j] * xBuf[j] / nSteps
                                : 0.0;
                            double gWA = dZ * wf[s][j] * xBuf[j] + sparsity * attnSJ + magAttnGrad;
                            mWA[s][j] = AdamBeta1 * mWA[s][j] + (1 - AdamBeta1) * gWA;
                            vWA[s][j] = AdamBeta2 * vWA[s][j] + (1 - AdamBeta2) * gWA * gWA;
                            wAttention[s][j] -= cosLr * (mWA[s][j] / bc1) / (Math.Sqrt(vWA[s][j] / bc2) + AdamEpsilon);
                        }
                    }
                }

                // ── Early stopping (evaluate every 5 epochs) ──────────────
                if (valSet.Count >= 10 && ep % 5 == 4)
                {
                    double valLoss = 0;
                    double[] xVal = pool.Rent(F);
                    try
                    {
                        foreach (var vs in valSet)
                        {
                            for (int j = 0; j < F; j++) xVal[j] = vs.Features[j];
                            double prob = TabNetRawProb(xVal, wAttention, wf, wOut, bOut, nSteps, F);
                            int vy = vs.Direction > 0 ? 1 : 0;
                            valLoss -= vy * Math.Log(prob + 1e-15) + (1 - vy) * Math.Log(1 - prob + 1e-15);
                        }
                    }
                    finally { pool.Return(xVal); }
                    valLoss /= valSet.Count;

                    if (valLoss < bestValLoss - 1e-6)
                    {
                        bestValLoss = valLoss;
                        bestEpoch   = ep;
                        bestWA  = wAttention.Select(w => (double[])w.Clone()).ToArray();
                        bestWf  = wf.Select(w => (double[])w.Clone()).ToArray();
                        bestWOut = wOut;
                        bestBOut = bOut;
                        if (useMagHead) { bestWMag = (double[])wMag.Clone(); bestBMag = bMag; }
                        earlyCount = 0;
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
            pool.Return(xBuf);
            pool.Return(priorScalesBuf);
            pool.Return(attLogitsBuf);
            pool.Return(stepAttnsFlat);
            pool.Return(stepMaskedFlat);
            if (useMagHead) pool.Return(hFinalBuf);
        }

        // ── Restore best weights ───────────────────────────────────────────
        if (bestEpoch > 0)
        {
            wAttention = bestWA; wf = bestWf; wOut = bestWOut; bOut = bestBOut;
            if (useMagHead) { wMag = bestWMag; bMag = bestBMag; }
        }

        return (wAttention, wf, wOut, bOut, wMag, bMag);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int F, int nSteps, double lr, double sparsity, int epochs,
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

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

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
            var (cvWA, cvWf, cvWO, cvBO, _, _) = FitTabNet(
                foldTrain, F, nSteps, lr, sparsity, cvEpochs,
                hp.LabelSmoothing, null, null, hp.TemporalDecayLambda, hp.L2Lambda,
                hp.EarlyStoppingPatience, 0.0 /* no mag head during CV */, ct);

            var m = EvaluateTabNet(foldTest, cvWA, cvWf, cvWO, cvBO, nSteps, F, 1.0, 0.0, [], 0);

            // Per-feature attention importance proxy from mean |wAttention[s][j]|
            var foldImp = new double[F];
            for (int s = 0; s < nSteps; s++)
                for (int j = 0; j < F; j++)
                    foldImp[j] += Math.Abs(cvWA[s][j]) / nSteps;

            // Equity-curve gate
            var preds = new (int Predicted, int Actual)[foldTest.Count];
            for (int i = 0; i < foldTest.Count; i++)
            {
                double[] x = new double[F];
                for (int j = 0; j < F; j++) x[j] = foldTest[i].Features[j];
                double prob = TabNetRawProb(x, cvWA, cvWf, cvWO, cvBO, nSteps, F);
                preds[i] = (prob >= 0.5 ? 1 : -1, foldTest[i].Direction > 0 ? 1 : -1);
            }
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(preds);

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBad);
        }

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImps   = new List<double[]>(folds);
        int badFolds   = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            foldImps.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
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

        // Feature stability scores: CV of per-fold attention importance
        double[]? featureStabilityScores = null;
        if (foldImps.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = foldImps.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImps[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++) { double d = foldImps[fi][j] - meanImp; varImp += d * d; }
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

    private static double TabNetRawProb(
        double[] x, double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        double[] priorScales = new double[F]; Array.Fill(priorScales, 1.0);
        double stepOut = 0;
        for (int s = 0; s < nSteps; s++)
        {
            var attLogits = new double[F];
            for (int j = 0; j < F; j++) attLogits[j] = priorScales[j] * wAttention[s][j] * x[j];
            double[] attn = Softmax(attLogits);
            for (int j = 0; j < F; j++) priorScales[j] = Math.Max(1e-6, priorScales[j] * (1.0 - attn[j]));
            double z = 0;
            for (int j = 0; j < F; j++) z += wf[s][j] * x[j] * attn[j];
            stepOut += Math.Max(z, 0.0);
        }
        return Sigmoid(wOut * stepOut + bOut);
    }

    private static double TabNetRawProbFromFloats(
        float[] x, double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        var xd = new double[F];
        for (int i = 0; i < F; i++) xd[i] = x[i];
        return TabNetRawProb(xd, wAttention, wf, wOut, bOut, nSteps, F);
    }

    private static double TabNetCalibProb(
        double[] x, double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        double raw = Math.Clamp(TabNetRawProb(x, wAttention, wf, wOut, bOut, nSteps, F), 1e-7, 1.0 - 1e-7);
        return Sigmoid(plattA * Logit(raw) + plattB);
    }

    private static double TabNetCalibProbFromFloats(
        float[] x, double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        var xd = new double[F];
        for (int i = 0; i < F; i++) xd[i] = x[i];
        return TabNetCalibProb(xd, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EVALUATION
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalMetrics EvaluateTabNet(
        IReadOnlyList<TrainingSample> evalSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut,
        int nSteps, int F, double plattA, double plattB,
        double[] magWeights, double magBias)
    {
        int correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, magSse = 0;
        double weightSum = 0, correctWeighted = 0;
        int n = evalSet.Count;

        for (int idx = 0; idx < n; idx++)
        {
            var s   = evalSet[idx];
            double p = TabNetCalibProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            int yHat = p >= 0.5 ? 1 : 0;
            int y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);

            if (magWeights.Length > 0)
            {
                double pred = magBias;
                for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                    pred += magWeights[j] * s.Features[j];
                magSse += (pred - s.Magnitude) * (pred - s.Magnitude);
            }

            // Time-weighted accuracy (more weight on recent)
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
        double ev        = accuracy > 0.5 ? accuracy - 0.5 : 0;
        double sharpe    = ev / (brier + 0.01);
        double wAcc      = weightSum > 0 ? correctWeighted / weightSum : accuracy;

        return new EvalMetrics(
            Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
            MagnitudeRmse: magRmse, ExpectedValue: ev, BrierScore: brier,
            WeightedAccuracy: wAcc, SharpeRatio: sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PLATT SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static (double A, double B) FitPlattScaling(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        if (calSet.Count < 10) return (1.0, 0.0);
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(
                TabNetRawProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F),
                1e-7, 1.0 - 1e-7);
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
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        var buySamples  = calSet.Where(s => s.Direction > 0).ToList();
        var sellSamples = calSet.Where(s => s.Direction <= 0).ToList();
        var (aBuy,  bBuy)  = buySamples.Count  >= 10 ? FitPlattScaling(buySamples,  wAttention, wf, wOut, bOut, nSteps, F) : (1.0, 0.0);
        var (aSell, bSell) = sellSamples.Count >= 10 ? FitPlattScaling(sellSamples, wAttention, wf, wOut, bOut, nSteps, F) : (1.0, 0.0);
        return (aBuy, bBuy, aSell, bSell);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISOTONIC CALIBRATION (PAVA)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] FitIsotonicCalibration(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        if (calSet.Count < 10) return [];

        var pairs = new (double X, double Y)[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            pairs[i] = (
                TabNetCalibProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB),
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
                if (prev.SumY / prev.Count <= last.SumY / last.Count) break;
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.SumY + last.SumY, prev.Count + last.Count, prev.XMin, last.XMax);
            }
        }

        var bp = new List<double>();
        foreach (var b in blocks)
        {
            bp.Add((b.XMin + b.XMax) / 2.0);
            bp.Add(b.SumY / b.Count);
        }
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
    //  ECE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeEce(
        IReadOnlyList<TrainingSample> testSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB, int bins = 10)
    {
        if (testSet.Count < bins) return 1.0;
        var binCorrect = new double[bins];
        var binConf    = new double[bins];
        var binCount   = new int[bins];

        foreach (var s in testSet)
        {
            double p = TabNetCalibProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            int bin  = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[bin]    += p;
            binCorrect[bin] += s.Direction > 0 ? 1 : 0; // positive-class frequency, not accuracy
            binCount[bin]++;
        }

        double ece = 0;
        int n = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCount[b] == 0) continue;
            double acc  = binCorrect[b] / binCount[b];
            double conf = binConf[b]    / binCount[b];
            ece += Math.Abs(acc - conf) * binCount[b] / n;
        }
        return ece;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EV-OPTIMAL THRESHOLD
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeOptimalThreshold(
        IReadOnlyList<TrainingSample> dataSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB, int searchMin = 30, int searchMax = 75)
    {
        if (dataSet.Count < 30) return 0.5;

        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
            probs[i] = TabNetCalibProbFromFloats(dataSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);

        double bestEv = double.MinValue;
        double bestT  = 0.5;
        for (int ti = searchMin; ti <= searchMax; ti++)
        {
            double threshold = ti / 100.0;
            double ev = 0;
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

    // ═══════════════════════════════════════════════════════════════════════
    //  PERMUTATION FEATURE IMPORTANCE
    // ═══════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        IReadOnlyList<TrainingSample> testSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB, CancellationToken ct)
    {
        int n = testSet.Count;
        double baseline = ComputeAccuracy(testSet, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
        var importance  = new float[F];

        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng  = new Random(j * 13 + 42);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = testSet[i].Features[j];
            for (int i = n - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (vals[k], vals[i]) = (vals[i], vals[k]);
            }

            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])testSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                double p = TabNetCalibProbFromFloats(scratch, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
                if ((p >= 0.5) == (testSet[idx].Direction > 0)) correct++;
            }
            importance[j] = (float)Math.Max(0, baseline - (double)correct / n);
        });

        float total = importance.Sum();
        if (total > 1e-6f)
            for (int j = 0; j < F; j++) importance[j] /= total;
        return importance;
    }

    private static double[] ComputeCalPermutationImportance(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        CancellationToken ct)
    {
        int n = calSet.Count;
        double baseAcc = ComputeRawAccuracy(calSet, wAttention, wf, wOut, bOut, nSteps, F);
        var importance = new double[F];

        Parallel.For(0, F, new ParallelOptions { CancellationToken = ct }, j =>
        {
            var rng  = new Random(j * 17 + 7);
            var vals = new float[n];
            for (int i = 0; i < n; i++) vals[i] = calSet[i].Features[j];
            for (int i = n - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (vals[k], vals[i]) = (vals[i], vals[k]);
            }

            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                var scratch = (float[])calSet[idx].Features.Clone();
                scratch[j] = vals[idx];
                double p = TabNetRawProbFromFloats(scratch, wAttention, wf, wOut, bOut, nSteps, F);
                if ((p >= 0.5) == (calSet[idx].Direction > 0)) correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    private static double ComputeAccuracy(
        IReadOnlyList<TrainingSample> set,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        int correct = 0;
        foreach (var s in set)
        {
            double p = TabNetCalibProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            if ((p >= 0.5) == (s.Direction > 0)) correct++;
        }
        return set.Count > 0 ? (double)correct / set.Count : 0;
    }

    private static double ComputeRawAccuracy(
        IReadOnlyList<TrainingSample> set,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        int correct = 0;
        foreach (var s in set)
        {
            double p = TabNetRawProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F);
            if ((p >= 0.5) == (s.Direction > 0)) correct++;
        }
        return set.Count > 0 ? (double)correct / set.Count : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAGNITUDE REGRESSOR  (Adam + cosine LR + Huber loss + early stopping)
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
        var    bestW       = new double[featureCount];
        double bestB       = 0.0;
        int    patience    = 0;

        int    epochs  = hp.MaxEpochs;
        double baseLr  = hp.LearningRate > 0 ? hp.LearningRate : 0.1;
        double l2      = hp.L2Lambda;

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
                double bc1       = 1.0 - beta1t;
                double bc2       = 1.0 - beta2t;
                double alphat    = alpha * Math.Sqrt(bc2) / bc1;

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
                bestB   = b;
                patience = 0;
            }
            else if (++patience >= hp.EarlyStoppingPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONFORMAL PREDICTION
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeConformalQHat(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB, double[] isotonicBp, double alpha)
    {
        if (calSet.Count < 10) return 0.5;

        var scores = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetCalibProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            if (isotonicBp.Length >= 2) p = ApplyIsotonic(p, isotonicBp);
            int y = calSet[i].Direction > 0 ? 1 : 0;
            scores[i] = 1.0 - (y == 1 ? p : 1.0 - p);
        }
        Array.Sort(scores);

        int qIdx = (int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1;
        qIdx = Math.Clamp(qIdx, 0, calSet.Count - 1);
        return scores[qIdx];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  JACKKNIFE+ RESIDUALS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeJackknifeResiduals(
        IReadOnlyList<TrainingSample> trainSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        int n = trainSet.Count;
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double p = TabNetRawProbFromFloats(trainSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F);
            residuals[i] = Math.Abs(p - (trainSet[i].Direction > 0 ? 1.0 : 0.0));
        }
        return residuals;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  META-LABEL MODEL
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        if (calSet.Count < 10) return ([0.0], 0.0);

        // Meta-label: predict whether the primary model's prediction was correct.
        // Feature: single value = primary model raw probability.
        int n = calSet.Count;
        double metaW = 0.0, metaB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0;
        int t = 0;
        const double sgdLr = 0.01;
        const int epochs   = 200;

        for (int ep = 0; ep < epochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double p       = TabNetRawProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F);
                int    correct = ((p >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double metaP   = Sigmoid(metaW * p + metaB);
                double err     = metaP - correct;
                dW += err * p;
                dB += err;
            }
            t++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, t);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n;
            vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n;
            vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            metaW -= sgdLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            metaB -= sgdLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }

        return ([metaW], metaB);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ABSTENTION GATE
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        if (calSet.Count < 10) return ([0.0], 0.0, 0.5);

        int n = calSet.Count;
        var probs = new double[n];
        for (int i = 0; i < n; i++)
            probs[i] = TabNetCalibProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);

        // Logistic on |p − 0.5| (distance from decision boundary)
        double absW = 0.0, absB = 0.0;
        double mW = 0, vW = 0, mB = 0, vB = 0;
        int t = 0;
        const double sgdLr = 0.01;
        const int epochs   = 200;

        for (int ep = 0; ep < epochs; ep++)
        {
            double dW = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double feat    = Math.Abs(probs[i] - 0.5);
                int    correct = ((probs[i] >= 0.5) == (calSet[i].Direction > 0)) ? 1 : 0;
                double abstP   = Sigmoid(absW * feat + absB);
                double err     = abstP - correct;
                dW += err * feat;
                dB += err;
            }
            t++;
            double bc1 = 1.0 - Math.Pow(AdamBeta1, t);
            double bc2 = 1.0 - Math.Pow(AdamBeta2, t);
            mW = AdamBeta1 * mW + (1 - AdamBeta1) * dW / n;
            vW = AdamBeta2 * vW + (1 - AdamBeta2) * (dW / n) * (dW / n);
            mB = AdamBeta1 * mB + (1 - AdamBeta1) * dB / n;
            vB = AdamBeta2 * vB + (1 - AdamBeta2) * (dB / n) * (dB / n);
            absW -= sgdLr * (mW / bc1) / (Math.Sqrt(vW / bc2) + AdamEpsilon);
            absB -= sgdLr * (mB / bc1) / (Math.Sqrt(vB / bc2) + AdamEpsilon);
        }

        // Optimal abstention threshold: sweep |p − 0.5| to maximise precision
        double bestPrec = 0, bestThresh = 0.1;
        for (int ti = 1; ti <= 40; ti++)
        {
            double thresh = ti / 100.0;
            int tpA = 0, fpA = 0;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(probs[i] - 0.5) < thresh) continue;
                bool correct = (probs[i] >= 0.5) == (calSet[i].Direction > 0);
                if (correct) tpA++; else fpA++;
            }
            double prec = (tpA + fpA) > 0 ? (double)tpA / (tpA + fpA) : 0;
            if (prec > bestPrec) { bestPrec = prec; bestThresh = thresh; }
        }

        return ([absW], absB, bestThresh);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  QUANTILE MAGNITUDE REGRESSOR  (pinball loss, SGD)
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        IReadOnlyList<TrainingSample> trainSet, int F, double tau)
    {
        if (trainSet.Count < 10) return (new double[F], 0.0);
        int n = trainSet.Count;
        var w = new double[F]; double b = 0.0;
        const double sgdLr = 0.001;
        const int epochs   = 100;

        for (int ep = 0; ep < epochs; ep++)
        {
            for (int i = 0; i < n; i++)
            {
                double pred = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    pred += w[j] * trainSet[i].Features[j];
                double residual  = trainSet[i].Magnitude - pred;
                double grad      = residual >= 0 ? -(1.0 - tau) : tau; // pinball subgradient
                b -= sgdLr * grad;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    w[j] -= sgdLr * grad * trainSet[i].Features[j];
            }
        }
        return (w, b);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  KELLY FRACTION
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeAvgKellyFraction(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        if (calSet.Count < 10) return 0;
        double kellySum = 0;
        foreach (var s in calSet)
        {
            double p  = TabNetCalibProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            // Half-Kelly: f = (2p − 1) × 0.5
            kellySum += Math.Max(0, (2 * p - 1) * 0.5);
        }
        return kellySum / calSet.Count;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DECISION BOUNDARY STATS
    // ═══════════════════════════════════════════════════════════════════════

    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        if (calSet.Count < 5) return (0, 0);
        var distances = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double p = TabNetRawProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F);
            distances[i] = Math.Abs(p - 0.5);
        }
        double mean = distances.Average();
        double std  = StdDev(distances.ToList(), mean);
        return (mean, std);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DURBIN-WATSON
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeDurbinWatson(
        IReadOnlyList<TrainingSample> trainSet, double[] magWeights, double magBias, int F)
    {
        if (trainSet.Count < 10 || magWeights.Length == 0) return 2.0;
        int n = trainSet.Count;
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, magWeights.Length) && j < trainSet[i].Features.Length; j++)
                pred += magWeights[j] * trainSet[i].Features[j];
            residuals[i] = trainSet[i].Magnitude - pred;
        }
        double numerator = 0, denominator = 0;
        for (int i = 0; i < n; i++) denominator += residuals[i] * residuals[i];
        for (int i = 1; i < n; i++) { double d = residuals[i] - residuals[i - 1]; numerator += d * d; }
        return denominator > 1e-15 ? numerator / denominator : 2.0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MUTUAL INFORMATION REDUNDANCY  (Sturges' rule binning)
    // ═══════════════════════════════════════════════════════════════════════

    private static string[] ComputeRedundantFeaturePairs(
        IReadOnlyList<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30 || F < 2) return [];

        int n       = Math.Min(trainSet.Count, 500);
        int numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));

        var redundant = new List<string>();
        for (int a = 0; a < F; a++)
        {
            for (int b = a + 1; b < F; b++)
            {
                var valsA = new double[n];
                var valsB = new double[n];
                for (int i = 0; i < n; i++)
                {
                    valsA[i] = trainSet[i].Features[a];
                    valsB[i] = trainSet[i].Features[b];
                }

                double mi   = ComputeMI(valsA, valsB, numBins);
                double hA   = ComputeEntropy(valsA, numBins);
                double hB   = ComputeEntropy(valsB, numBins);
                double norm = Math.Max(hA, hB);

                if (norm > 1e-10 && mi / norm > threshold)
                {
                    string nameA = a < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a] : $"F{a}";
                    string nameB = b < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[b] : $"F{b}";
                    redundant.Add($"{nameA}↔{nameB}:{mi / norm:F2}");
                }
            }
        }
        return redundant.ToArray();
    }

    private static double ComputeMI(double[] a, double[] b, int bins)
    {
        double minA = a.Min(), maxA = a.Max();
        double minB = b.Min(), maxB = b.Max();
        double widthA = (maxA - minA) / bins + 1e-15;
        double widthB = (maxB - minB) / bins + 1e-15;
        int n = a.Length;
        var joint = new int[bins, bins];
        var margA = new int[bins];
        var margB = new int[bins];

        for (int i = 0; i < n; i++)
        {
            int ia = Math.Clamp((int)((a[i] - minA) / widthA), 0, bins - 1);
            int ib = Math.Clamp((int)((b[i] - minB) / widthB), 0, bins - 1);
            joint[ia, ib]++; margA[ia]++; margB[ib]++;
        }

        double mi = 0;
        for (int i = 0; i < bins; i++)
            for (int j = 0; j < bins; j++)
            {
                if (joint[i, j] == 0) continue;
                double pxy = (double)joint[i, j] / n;
                double px  = (double)margA[i]   / n;
                double py  = (double)margB[j]   / n;
                mi += pxy * Math.Log(pxy / (px * py + 1e-15) + 1e-15);
            }
        return Math.Max(0, mi);
    }

    private static double ComputeEntropy(double[] vals, int bins)
    {
        double min = vals.Min(), max = vals.Max();
        double width = (max - min) / bins + 1e-15;
        int n = vals.Length;
        var counts = new int[bins];
        for (int i = 0; i < n; i++) counts[Math.Clamp((int)((vals[i] - min) / width), 0, bins - 1)]++;
        double h = 0;
        for (int i = 0; i < bins; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / n;
            h -= p * Math.Log(p);
        }
        return h;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPERATURE SCALING
    // ═══════════════════════════════════════════════════════════════════════

    private static double FitTemperatureScaling(
        IReadOnlyList<TrainingSample> calSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F)
    {
        if (calSet.Count < 10) return 1.0;
        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = Math.Clamp(
                TabNetRawProbFromFloats(calSet[i].Features, wAttention, wf, wOut, bOut, nSteps, F),
                1e-7, 1.0 - 1e-7);
            logits[i] = Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double T = 1.0;
        const double tLr = 0.01;
        const int epochs  = 100;
        for (int ep = 0; ep < epochs; ep++)
        {
            double dT = 0;
            for (int i = 0; i < n; i++)
            {
                double scaledP = Sigmoid(logits[i] / T);
                dT += (scaledP - labels[i]) * (-logits[i] / (T * T));
            }
            T -= tLr * dT / n;
            T  = Math.Max(0.01, T);
        }
        return T;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BRIER SKILL SCORE
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeBrierSkillScore(
        IReadOnlyList<TrainingSample> testSet,
        double[][] wAttention, double[][] wf, double wOut, double bOut, int nSteps, int F,
        double plattA, double plattB)
    {
        if (testSet.Count < 10) return 0;
        int n = testSet.Count;
        double baseRate  = testSet.Count(s => s.Direction > 0) / (double)n;
        double brierNaive = baseRate * (1 - baseRate);
        double brierModel = 0;

        foreach (var s in testSet)
        {
            double p = TabNetCalibProbFromFloats(s.Features, wAttention, wf, wOut, bOut, nSteps, F, plattA, plattB);
            int    y = s.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
        }
        brierModel /= n;
        return brierNaive > 1e-10 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STATIONARITY GATE  (variance-ratio proxy for soft ADF)
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountNonStationaryFeatures(IReadOnlyList<TrainingSample> trainSet, int F)
    {
        if (trainSet.Count < 30) return 0;
        int nonStat = 0;
        for (int j = 0; j < F; j++)
        {
            var vals = new double[trainSet.Count];
            for (int i = 0; i < trainSet.Count; i++) vals[i] = trainSet[i].Features[j];
            if (HasUnitRootProxy(vals)) nonStat++;
        }
        return nonStat;
    }

    private static bool HasUnitRootProxy(double[] vals)
    {
        int n = vals.Length;
        if (n < 10) return false;
        // Compare variance of first half vs second half; large ratio = non-stationary
        int half = n / 2;
        double varFirst  = Variance(new ArraySegment<double>(vals, 0, half));
        double varSecond = Variance(new ArraySegment<double>(vals, half, n - half));
        double ratio     = varSecond > 1e-15 ? varFirst / varSecond : 1.0;
        return ratio > 3.0 || ratio < 0.333;
    }

    private static double Variance(ArraySegment<double> seg)
    {
        if (seg.Count < 2) return 0;
        double mean = 0;
        foreach (double v in seg) mean += v;
        mean /= seg.Count;
        double var = 0;
        foreach (double v in seg) var += (v - mean) * (v - mean);
        return var / (seg.Count - 1);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DENSITY-RATIO IMPORTANCE WEIGHTS
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeDensityRatioWeights(
        IReadOnlyList<TrainingSample> trainSet, int F, int windowDays)
    {
        int n           = trainSet.Count;
        int recentCount = Math.Min(n / 3, windowDays * 24);
        if (recentCount < 20) return Enumerable.Repeat(1.0, n).ToArray();

        int cutoff = n - recentCount;
        var w = new double[F]; double bias = 0;
        const double sgdLr = 0.01;

        // Logistic discriminator: recent (1) vs historical (0) — 30 epochs SGD
        for (int ep = 0; ep < 30; ep++)
        {
            for (int i = 0; i < n; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                double z = bias;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    z += w[j] * trainSet[i].Features[j];
                double p   = Sigmoid(z);
                double err = p - label;
                bias -= sgdLr * err;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    w[j] -= sgdLr * err * trainSet[i].Features[j];
            }
        }

        var weights = new double[n];
        for (int i = 0; i < n; i++)
        {
            double z = bias;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                z += w[j] * trainSet[i].Features[j];
            double p   = Math.Clamp(Sigmoid(z), 0.01, 0.99);
            weights[i] = p / (1 - p);
        }

        double sum = weights.Sum();
        if (sum > 1e-15)
            for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COVARIATE SHIFT WEIGHTS  (parent model PSI novelty scoring)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeCovariateShiftWeights(
        IReadOnlyList<TrainingSample> trainSet, double[][] parentBp, int F)
    {
        int n = trainSet.Count;
        var weights = new double[n];

        for (int i = 0; i < n; i++)
        {
            int outsideCount = 0;
            int checkedCount = 0;
            for (int j = 0; j < F && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                // bp[0] ≈ q10, bp[^1] ≈ q90 (decile bin edges)
                if (v < bp[0] || v > bp[^1]) outsideCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0;
            weights[i] = 1.0 + noveltyFraction;
        }

        // Normalise to mean = 1.0
        double mean = weights.Average();
        if (mean > 1e-15)
            for (int i = 0; i < n; i++) weights[i] /= mean;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT SANITIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static int SanitizeWeights(double[][] wAttention, double[][] wf, ref double wOut, ref double bOut)
    {
        int count = 0;
        foreach (var w in wAttention)
            for (int j = 0; j < w.Length; j++)
                if (!double.IsFinite(w[j])) { w[j] = 0.0; count++; }
        foreach (var w in wf)
            for (int j = 0; j < w.Length; j++)
                if (!double.IsFinite(w[j])) { w[j] = 0.0; count++; }
        if (!double.IsFinite(wOut)) { wOut = 0.01; count++; }
        if (!double.IsFinite(bOut)) { bOut = 0.0;  count++; }
        return count;
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
        var result  = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var newFeatures = new float[maskedF];
            int ni = 0;
            for (int j = 0; j < mask.Length && j < s.Features.Length; j++)
                if (mask[j]) newFeatures[ni++] = s.Features[j];
            result.Add(s with { Features = newFeatures });
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  EQUITY CURVE STATS
    // ═══════════════════════════════════════════════════════════════════════

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);
        double equity = 0, peak = 0, maxDD = 0;
        var returns   = new List<double>(predictions.Length);
        foreach (var (pred, actual) in predictions)
        {
            double ret = pred == actual ? 1.0 : -1.0;
            returns.Add(ret);
            equity += ret;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }
        double avgRet = returns.Average();
        double stdRet = StdDev(returns, avgRet);
        double sharpe = stdRet > 1e-10 ? avgRet / stdRet * Math.Sqrt(252) : 0;
        return (maxDD, sharpe);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TEMPORAL WEIGHTING
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ComputeTemporalWeights(int n, double lambdaDecay)
    {
        var weights = new double[n];
        if (lambdaDecay <= 0)
        {
            Array.Fill(weights, 1.0 / Math.Max(1, n));
            return weights;
        }
        double sum = 0;
        for (int i = 0; i < n; i++) { weights[i] = Math.Exp(-lambdaDecay * (n - 1 - i)); sum += weights[i]; }
        if (sum > 1e-15) for (int i = 0; i < n; i++) weights[i] /= sum;
        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SHARPE TREND  (linear regression slope)
    // ═══════════════════════════════════════════════════════════════════════

    private static double ComputeSharpeTrend(IReadOnlyList<double> sharpeList)
    {
        int n = sharpeList.Count;
        if (n < 3) return 0;
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += i;           sumY  += sharpeList[i];
            sumXX += i * i;       sumXY += i * sharpeList[i];
        }
        double denom = n * sumXX - sumX * sumX;
        return Math.Abs(denom) > 1e-10 ? (n * sumXY - sumX * sumY) / denom : 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MATH UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x)
        => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -50, 50)));

    private static double Logit(double p)
        => Math.Log(p / (1.0 - p));

    private static double[] Softmax(double[] x)
    {
        double max = x.Max();
        double[] e = x.Select(v => Math.Exp(v - max)).ToArray();
        double sum = e.Sum() + 1e-10;
        return e.Select(v => v / sum).ToArray();
    }

    private static double StdDev(IReadOnlyList<double> vals, double mean)
    {
        if (vals.Count < 2) return 0;
        double variance = vals.Sum(v => (v - mean) * (v - mean)) / (vals.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double[] RandomVec(Random rng, int size, double scale)
        => Enumerable.Range(0, size).Select(_ => (rng.NextDouble() * 2 - 1) * scale).ToArray();

    // ═══════════════════════════════════════════════════════════════════════
    //  POLYNOMIAL FEATURE AUGMENTATION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Selects the top-N feature indices for pairwise polynomial augmentation.
    /// Uses warm-start calibration importance scores when available; falls back to
    /// per-feature variance over the training set (high-variance features are most
    /// likely to carry signal before any prior importance is established).
    /// </summary>
    private static int[] SelectPolyTopFeatureIndices(
        List<TrainingSample> samples, int F, ModelSnapshot? warmStart, int topN)
    {
        topN = Math.Min(topN, F);
        int n = samples.Count;
        double[] scores = new double[F];

        if (warmStart?.FeatureImportanceScores is { Length: > 0 } prior && prior.Length == F)
        {
            for (int j = 0; j < F; j++) scores[j] = prior[j];
        }
        else
        {
            // Feature variance as proxy for importance
            double[] means = new double[F];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < F; j++) means[j] += samples[i].Features[j];
            for (int j = 0; j < F; j++) means[j] /= n;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < F; j++) { double d = samples[i].Features[j] - means[j]; scores[j] += d * d; }
            for (int j = 0; j < F; j++) scores[j] /= n;
        }

        return scores
            .Select((s, idx) => (Score: s, Idx: idx))
            .OrderByDescending(t => t.Score)
            .Take(topN)
            .Select(t => t.Idx)
            .OrderBy(i => i) // ascending for reproducibility
            .ToArray();
    }

    /// <summary>
    /// Appends pairwise products of top-feature pairs to each sample's feature vector.
    /// Input features must already be Z-score standardised so the products have approximately
    /// zero mean and unit variance (product of independent N(0,1) variables).
    /// </summary>
    private static List<TrainingSample> AugmentSamplesWithPoly(
        List<TrainingSample> samples, int origF, int[] topIdx)
    {
        int pairCount = topIdx.Length * (topIdx.Length - 1) / 2;
        int newF = origF + pairCount;
        var augmented = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var newFeatures = new float[newF];
            for (int j = 0; j < origF; j++) newFeatures[j] = s.Features[j];
            int k = origF;
            for (int a = 0; a < topIdx.Length; a++)
                for (int b = a + 1; b < topIdx.Length; b++)
                    newFeatures[k++] = s.Features[topIdx[a]] * s.Features[topIdx[b]];
            augmented.Add(s with { Features = newFeatures });
        }
        return augmented;
    }
}
