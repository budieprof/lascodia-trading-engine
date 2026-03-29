using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Extreme Learning Machine (ELM) bagged ensemble trainer (Rec #449).
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Run K-fold walk-forward CV (expanding window, embargo, equity-curve gate, Sharpe trend) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Train the final bagged ensemble on 70 % of data with a 10 % Platt calibration fold and ~20 % hold-out test.</item>
///   <item>Each base learner constructs a random hidden layer with configurable activations (sigmoid/tanh/ReLU, Xavier init),
///         computes H = activation(X W_input + b), then solves the output layer analytically via
///         ridge regression: W_out = (H^T H + λI)^{-1} H^T Y (Cholesky solver).</item>
///   <item>Each learner is fitted on a <b>stratified</b> temporally-weighted biased bootstrap of the training split,
///         ensuring balanced buy/sell classes per bag to prevent direction bias.</item>
///   <item>Each learner sees a <b>random feature subset</b> (√F features by default) for ensemble diversity.</item>
///   <item>Each learner may use a <b>different hidden size</b> (±ElmHiddenSizeVariation) for architectural diversity.</item>
///   <item>Label smoothing (ε=LabelSmoothing) applied to regression targets.</item>
///   <item>Configurable per-sample dropout rate on hidden units (ElmDropoutRate).</item>
///   <item>Platt scaling (A, B) fitted on the calibration fold after the ensemble is frozen.</item>
///   <item>ECE (Expected Calibration Error) computed post-Platt on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set to maximise expected value.</item>
///   <item>A parallel linear regressor predicts magnitude in ATR-normalised units, with optional walk-forward CV.</item>
///   <item>Isotonic calibration (PAVA), conformal prediction, meta-label and abstention gates.</item>
///   <item>Optional feature pruning: low-importance features are masked and the ensemble is re-trained.</item>
///   <item>Optional warm-start: input weights are initialised from the previous model snapshot.</item>
///   <item>Post-training NaN/Inf weight sanitisation.</item>
///   <item>SMOTE synthetic samples weighted by ElmSmoteSampleWeight to prevent dominating the ridge solve.</item>
///   <item>SIMD-accelerated dot products for hidden-layer forward pass.</item>
/// </list>
/// </para>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Elm)]
public sealed class ElmModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "elm";
    private const string ModelVersion = "3.0";
    private const int    DefaultHiddenSize = 128;
    private const double DefaultSharpeAnnualisationFactor = 252.0;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<ElmModelTrainer> _logger;

    public ElmModelTrainer(ILogger<ElmModelTrainer> logger) => _logger = logger;

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

        if (samples.Count == 0)
            throw new InvalidOperationException("ElmModelTrainer: no training samples provided.");

        int featureCount = samples[0].Features.Length;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Features.Length != featureCount)
                throw new InvalidOperationException(
                    $"ElmModelTrainer: inconsistent feature count — sample 0 has {featureCount} features, sample {i} has {samples[i].Features.Length}.");
        }
        int hiddenSize   = hp.ElmHiddenSize is > 0 ? hp.ElmHiddenSize.Value : DefaultHiddenSize;
        int K            = Math.Max(1, hp.K);

        // ── 0. Incremental update fast-path ─────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart is not null && hp.DensityRatioWindowDays > 0)
        {
            int barsPerDay  = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "ELM incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);

                var recentSamples = samples[^recentCount..];
                var incrementalHp = hp with
                {
                    MaxEpochs             = Math.Max(20, hp.MaxEpochs / 5),
                    EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 3),
                    LearningRate          = hp.LearningRate / 5.0,
                    UseIncrementalUpdate  = false,
                };
                // Pass raw (un-standardised) recent samples. Train() will compute fresh
                // standardisation statistics from them and normalise internally.
                // Pre-standardising here would cause double-standardisation because Train()
                // always re-standardises from scratch, regardless of any prior normalisation.
                return Train(recentSamples.ToList(), incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Compute split indices first to avoid leaking cal/test distribution
        //        into the standardisation statistics (data leakage fix).
        int n       = samples.Count;
        int embargo = hp.EmbargoBarCount;

        double trainRatio, calRatio;
        if (hp.ElmTrainSplitRatio > 0.0 && hp.ElmTrainSplitRatio < 1.0)
        {
            trainRatio = hp.ElmTrainSplitRatio;
            calRatio = hp.ElmCalSplitRatio > 0.0 && hp.ElmCalSplitRatio < 1.0
                ? hp.ElmCalSplitRatio
                : Math.Min(0.15, (1.0 - trainRatio) / 2.0);
        }
        else
        {
            double t = Math.Clamp((n - 500.0) / 1500.0, 0.0, 1.0);
            trainRatio = 0.80 - t * 0.10;
            calRatio = hp.ElmCalSplitRatio > 0.0 && hp.ElmCalSplitRatio < 1.0
                ? hp.ElmCalSplitRatio
                : 0.10 + t * 0.05;
        }
        if (trainRatio + calRatio > 0.95) calRatio = 0.95 - trainRatio;

        int trainEnd    = (int)(n * trainRatio);
        int calEnd      = (int)(n * (trainRatio + calRatio));
        int trainStdEnd = Math.Max(0, trainEnd - embargo); // excludes embargo tail

        if (trainStdEnd < 2)
            throw new InvalidOperationException(
                $"ElmModelTrainer: embargo ({embargo}) consumes the entire training window ({trainEnd} samples). " +
                $"Reduce EmbargoBarCount or provide more data.");

        // ── 2. Z-score standardisation on training samples only ─────────────────
        // Using all samples would leak the future cal/test distribution into the
        // standardisation statistics, inflating apparent out-of-sample performance.
        var rawTrainFeatures = new List<float[]>(trainStdEnd);
        for (int i = 0; i < trainStdEnd; i++) rawTrainFeatures.Add(samples[i].Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawTrainFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        if (hp.FracDiffD > 0.0)
        {
            allStd = MLFeatureHelper.ApplyFractionalDifferencing(allStd, featureCount, hp.FracDiffD);
            _logger.LogInformation("ELM fractional differencing applied: d={D:F2}", hp.FracDiffD);
        }

        double sharpeAnnFactor = hp.SharpeAnnualisationFactor > 0.0
            ? hp.SharpeAnnualisationFactor : DefaultSharpeAnnualisationFactor;

        _logger.LogInformation(
            "ElmModelTrainer starting: N={N} F={F} Hidden={H} K={K} Activation={Act} Dropout={Drop:P0}",
            samples.Count, featureCount, hiddenSize, K, hp.ElmActivation, hp.ElmDropoutRate);

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, featureCount, hiddenSize, ct, sharpeAnnFactor);
        _logger.LogInformation(
            "ELM walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: adaptive train | cal | test ──────────────
        // Split indices (n, embargo, trainEnd, calEnd, trainStdEnd) were computed
        // above in step 1 before standardisation to avoid data leakage.
        var trainSet = allStd[..trainStdEnd];
        var calSet   = allStd[(calEnd > trainEnd ? trainEnd + embargo : trainEnd)
                               ..(calEnd < n ? calEnd : n)];
        var testSet  = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"ElmModelTrainer: insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        // ── 3b. Stationarity gate ────────────────────────────────────────────
        {
            int nonStatCount = ElmEvaluationHelper.CountNonStationaryFeatures(trainSet, featureCount);
            double nonStatFraction = featureCount > 0 ? (double)nonStatCount / featureCount : 0.0;
            if (nonStatFraction > 0.30 && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "ELM stationarity gate: {NonStat}/{Total} features have unit root. Consider enabling FracDiffD.",
                    nonStatCount, featureCount);
        }

        // ── 3c. Density-ratio importance weights ──────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = ElmBootstrapHelper.ComputeDensityRatioWeights(trainSet, featureCount, hp.DensityRatioWindowDays);
            _logger.LogDebug("ELM density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
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
            _logger.LogInformation(
                "ELM adaptive label smoothing: ε={Eps:F3} (ambiguous-proxy fraction={Frac:P1})",
                adaptiveLabelSmoothing, ambiguousFraction);
        }

        // ── 3e. Covariate shift weight integration ──────────────────────────────
        if (hp.UseCovariateShiftWeights &&
            warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ElmBootstrapHelper.ComputeCovariateShiftWeights(trainSet, parentBp, featureCount);
            if (densityWeights is not null)
            {
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("ELM covariate shift weights applied from parent model.");
        }

        // ── 3f. Ridge lambda auto-selection ─────────────────────────────────
        if (hp.L2Lambda <= 0 && trainSet.Count >= hp.MinSamples + 50)
        {
            double bestLambda = SelectRidgeLambda(trainSet, featureCount, hiddenSize,
                adaptiveLabelSmoothing, hp, ct);
            hp = hp with { L2Lambda = bestLambda };
            _logger.LogInformation("ELM ridge lambda auto-selected: λ={Lambda:E2}", bestLambda);
        }

        // ── 4. Fit bagged ELM ensemble ──────────────────────────────────────
        (double[][] weights, double[] biases, double[][] inputWeights, double[][] inputBiases,
         int[][]? featureSubsets, int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
         double[][,] inverseGrams) ensembleResult;
        try
        {
            ensembleResult = FitBaggedElm(trainSet, hp, featureCount, hiddenSize, K,
                                          adaptiveLabelSmoothing, warmStart, densityWeights, ct);
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException!).Throw();
            throw;
        }
        var (weights, biases, inputWeights, inputBiases, featureSubsets, learnerHiddenSizes, learnerActivations, inverseGrams) = ensembleResult;

        // ── 4b. Post-training NaN/Inf weight sanitisation ──────────────────
        int sanitizedCount = 0;
        for (int k = 0; k < K; k++)
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
                _logger.LogWarning("ELM: sanitized learner {K}: non-finite weights replaced with zeros.", k);
            }
        }
        if (sanitizedCount > 0)
            _logger.LogWarning("ELM post-training sanitization: {N}/{K} learners had non-finite weights.",
                sanitizedCount, K);

        ct.ThrowIfCancellationRequested();

        // ── 4c. Accuracy-weighted ensemble averaging ─────────────────────────
        var (learnerCalAccuracies, learnerAccWeights) = ComputeLearnerCalibrationStats(
            calSet, weights, biases, inputWeights, inputBiases,
            featureCount, featureSubsets, learnerHiddenSizes, learnerActivations, hp.ElmActivation);

        // ── 4e. Stacking meta-learner ────────────────────────────────────────
        var (stackingWeights, stackingBias) = FitStackingMetaLearner(
            calSet, weights, biases, inputWeights, inputBiases,
            featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations, ct);

        ct.ThrowIfCancellationRequested();

        double RawEnsembleProb(float[] features) => EnsembleRawProb(
            features, weights, biases, inputWeights, inputBiases,
            featureCount, hiddenSize, featureSubsets, learnerAccWeights, learnerHiddenSizes, learnerActivations,
            stackingWeights, stackingBias);

        // ── 5. Platt calibration (cross-validated, on stacking output) ──────
        var (plattA, plattB) = ElmCalibrationHelper.FitPlattScalingCV(
            calSet, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));
        _logger.LogDebug("ELM Platt calibration (CV): A={A:F4} B={B:F4}", plattA, plattB);

        // ── 5b. Temperature scaling (fit before class-conditional gating so the
        //        branch selection mirrors production scoring) ───────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = ElmCalibrationHelper.FitTemperatureScaling(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));
            _logger.LogDebug("ELM temperature scaling: T={T:F4}", temperatureScale);
        }

        // ── 5c. Class-conditional Platt ──────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
            calSet, weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets,
            plattA, plattB, temperatureScale,
            (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));

        double PrimaryCalibProb(float[] features) => ApplyProductionCalibration(
            RawEnsembleProb(features),
            plattA, plattB, temperatureScale,
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 5d. Average Kelly fraction ───────────────────────────────────────
        double avgKellyFraction = ElmCalibrationHelper.ComputeAvgKellyFraction(
            calSet, weights, biases, inputWeights, inputBiases, plattA, plattB,
            featureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f));

        // ── 6. Fit magnitude regressor (with optional CV) ───────────────────
        double[] magWeights;
        double magBias;
        double[] magAugWeights;
        double magAugBias;
        double[][]? magAugWeightsFolds = null;
        double[]? magAugBiasFolds = null;

        if (hp.ElmMagRegressorCvFolds > 1 && trainSet.Count >= hp.MinSamples * 2)
        {
            var magCvResult = FitElmMagnitudeRegressorCV(
                trainSet, featureCount, hiddenSize, inputWeights, inputBiases, featureSubsets, learnerActivations,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience,
                hp.ElmMagRegressorCvFolds, embargo, ct);
            magWeights = magCvResult.EquivWeights;
            magBias = magCvResult.EquivBias;
            magAugWeights = magCvResult.AugWeights;
            magAugBias = magCvResult.AugBias;
            magAugWeightsFolds = magCvResult.FoldAugWeights;
            magAugBiasFolds = magCvResult.FoldAugBiases;
            _logger.LogInformation(
                "ELM magnitude regressor fitted with {Folds}-fold walk-forward CV (prediction-averaged, {FoldCount} valid folds).",
                hp.ElmMagRegressorCvFolds, magAugWeightsFolds?.Length ?? 0);
        }
        else
        {
            (magWeights, magBias, magAugWeights, magAugBias) = FitElmMagnitudeRegressor(
                trainSet, featureCount, hiddenSize, inputWeights, inputBiases, featureSubsets, learnerActivations,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience, ct);
        }

        // ── 6b. Quantile magnitude regressor ─────────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(
                trainSet, featureCount, hp.MagnitudeQuantileTau, hiddenSize,
                inputWeights, inputBiases, featureSubsets, learnerActivations, ct);
            _logger.LogDebug("ELM quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 7. Final evaluation on held-out test set ────────────────────────
        var finalMetrics = ElmEvaluationHelper.EvaluateEnsemble(
            testSet, weights, biases, inputWeights, inputBiases,
            magWeights, magBias, plattA, plattB, featureCount, hiddenSize, featureSubsets,
            magAugWeights, magAugBias, sharpeAnnFactor,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f),
            (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations));

        _logger.LogInformation(
            "ELM final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        ct.ThrowIfCancellationRequested();

        // ── 8. ECE post-Platt ───────────────────────────────────────────────
        double ece = ElmEvaluationHelper.ComputeEce(
            testSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f));
        _logger.LogInformation("ELM post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal decision threshold ───────────────────────────────
        double optimalThreshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            calSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f));
        _logger.LogInformation("ELM EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 10. Permutation feature importance ────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ElmEvaluationHelper.ComputePermutationImportance(
                testSet, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets,
                (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f), ct)
            : new float[featureCount];

        if (featureImportance.Length > 0)
        {
            var topFeatures = featureImportance
                .Select((imp, idx) => (Importance: imp, Name: idx < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[idx] : $"F{idx}"))
                .OrderByDescending(x => x.Importance)
                .Take(5);
            _logger.LogInformation(
                "ELM top 5 features: {Features}",
                string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        // ── 10b. Calibration-set permutation importance ──────────────────────
        double[] calImportanceScores = calSet.Count >= 10
            ? ElmEvaluationHelper.ComputeCalPermutationImportance(
                calSet, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias), ct)
            : new double[featureCount];

        ct.ThrowIfCancellationRequested();

        // Effective data views — replaced below when the pruned model is accepted so that
        // the entire post-training calibration pipeline (steps 12-22) operates in the same
        // feature space as the accepted model. Using the originals here is safe when no
        // pruning fires (prunedCount == 0 or the pruned model is rejected).
        var effectiveCalSet       = calSet;
        var effectiveTestSet      = testSet;
        var effectiveTrainSet     = trainSet;
        int effectiveFeatureCount = featureCount;

        // ── 11. Feature pruning re-train pass ───────────────────────────────
        var activeMask = ElmBootstrapHelper.BuildFeatureMask(featureImportance, hp.MinFeatureImportance, featureCount);

        // ── 11a. MI redundancy-driven pruning: for each highly-correlated pair,
        //         mask the less-important feature using permutation importance.
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            var miRedundantIndices = ElmEvaluationHelper.ComputeRedundantFeaturePairIndices(
                trainSet, featureCount, hp.MutualInfoRedundancyThreshold);
            int miPruned = 0;
            foreach (var (fi, fj) in miRedundantIndices)
            {
                if (fi >= featureCount || fj >= featureCount) continue;
                if (!activeMask[fi] || !activeMask[fj]) continue;

                // Mask the feature with lower importance
                float impI = fi < featureImportance.Length ? featureImportance[fi] : 0;
                float impJ = fj < featureImportance.Length ? featureImportance[fj] : 0;
                int toDrop = impI <= impJ ? fi : fj;
                activeMask[toDrop] = false;
                miPruned++;
            }
            if (miPruned > 0)
                _logger.LogInformation(
                    "ELM MI-redundancy pruning: masked {N} collinear features (threshold={Thr:F2})",
                    miPruned, hp.MutualInfoRedundancyThreshold);
        }

        int prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && featureCount - prunedCount >= 10)
        {
            int activeFeatureCount = featureCount - prunedCount;
            _logger.LogInformation(
                "ELM feature pruning: masking {Pruned}/{Total} low-importance features (active={Active})",
                prunedCount, featureCount, activeFeatureCount);

            var maskedTrain = ElmBootstrapHelper.ApplyZeroMask(trainSet, activeMask);
            var maskedCal   = ElmBootstrapHelper.ApplyZeroMask(calSet, activeMask);
            var maskedTest  = ElmBootstrapHelper.ApplyZeroMask(testSet, activeMask);

            var prunedHp = hp with
            {
                FeatureSampleRatio = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0
                    ? Math.Min(1.0, hp.FeatureSampleRatio * featureCount / activeFeatureCount)
                    : hp.FeatureSampleRatio
            };

            ModelSnapshot? prunedWarmStart = RemapWarmStartForPruning(warmStart, activeMask, featureCount, hiddenSize);

            (double[][] pw, double[] pb, double[][] piw, double[][] pib,
             int[][]? psub, int[] phs, ElmActivation[] pla, double[][,] pInvGram) prunedEnsemble;
            try
            {
                prunedEnsemble = FitBaggedElm(
                    maskedTrain, prunedHp, featureCount, hiddenSize, K,
                    adaptiveLabelSmoothing, prunedWarmStart, densityWeights, ct, activeMask);
            }
            catch (AggregateException pae) when (pae.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pae.InnerException!).Throw();
                throw;
            }
            var (pw, pb, piw, pib, psub, phs, pla, pInvGram) = prunedEnsemble;

            // ── Mirror the magnitude-regressor CV path used for the full model ──
            double[] pmw, pmaw;
            double   pmb, pmab;
            double[][]? pMagAugWeightsFolds = null;
            double[]?   pMagAugBiasFolds    = null;
            if (hp.ElmMagRegressorCvFolds > 1 && maskedTrain.Count >= hp.MinSamples * 2)
            {
                (pmw, pmb, pmaw, pmab, pMagAugWeightsFolds, pMagAugBiasFolds) = FitElmMagnitudeRegressorCV(
                    maskedTrain, featureCount, hiddenSize, piw, pib, psub, pla,
                    hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience,
                    hp.ElmMagRegressorCvFolds, embargo, ct);
            }
            else
            {
                (pmw, pmb, pmaw, pmab) = FitElmMagnitudeRegressor(
                    maskedTrain, featureCount, hiddenSize, piw, pib, psub, pla,
                    hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience, ct);
            }

            var (pLearnerCalAccuracies, pLearnerAccWeights) = ComputeLearnerCalibrationStats(
                maskedCal, pw, pb, piw, pib,
                featureCount, psub, phs, pla, hp.ElmActivation);

            // Fit stacking meta-learner on the pruned model before computing comparison
            // metrics. The full model already uses stacking in EnsembleRawProb/CalibProb;
            // omitting it here made the pruned vs full comparison apples-to-oranges and
            // systematically penalised the pruned model by ~1–3 % accuracy.
            var (pSw, pSb) = FitStackingMetaLearner(
                maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub, phs, pla, ct);

            // Use CV Platt so the acceptance comparison uses the same calibration
            // method as the full model (FitPlattScalingCV at step 5), making it
            // apples-to-apples.
            var (pA, pB) = ElmCalibrationHelper.FitPlattScalingCV(
                maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb));
            var pTrainedAtUtc = DateTime.UtcNow;
            double pTemp = 0.0;
            if (hp.FitTemperatureScale && maskedCal.Count >= 10)
            {
                pTemp = ElmCalibrationHelper.FitTemperatureScaling(
                    maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                    (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                        f, w, b, iw, ib, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb));
            }
            var (pABuy, pBBuy, pASell, pBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                pA, pB, pTemp,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb));

            double PPreIsotonicCalibProb(float[] features) => ApplyProductionCalibration(
                EnsembleRawProb(features, pw, pb, piw, pib, featureCount, hiddenSize, psub, pLearnerAccWeights, phs, pla, pSw, pSb),
                pA, pB, pTemp, pABuy, pBBuy, pASell, pBSell);

            double[] pIso = ElmCalibrationHelper.FitIsotonicCalibration(
                maskedCal, pw, pb, piw, pib, pA, pB, featureCount, hiddenSize, psub,
                (f, w, b, iw, ib, pAp, pBp, fc, hs, fs, lw) => PPreIsotonicCalibProb(f));

            if (hp.FitTemperatureScale && maskedCal.Count >= 10)
            {
                double refitPTemp = ElmCalibrationHelper.FitTemperatureScaling(
                    maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                    (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                        f, w, b, iw, ib, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb),
                    pA, pB, pABuy, pBBuy, pASell, pBSell, pIso, hp.AgeDecayLambda, pTrainedAtUtc);

                if (Math.Abs(refitPTemp - pTemp) > 1e-6)
                {
                    pTemp = refitPTemp;
                    (pABuy, pBBuy, pASell, pBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                        maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                        pA, pB, pTemp,
                        (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                            f, w, b, iw, ib, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb));
                    pIso = ElmCalibrationHelper.FitIsotonicCalibration(
                        maskedCal, pw, pb, piw, pib, pA, pB, featureCount, hiddenSize, psub,
                        (f, w, b, iw, ib, pAp, pBp, fc, hs, fs, lw) => PPreIsotonicCalibProb(f));
                }
            }

            double PPrimaryCalibProb(float[] features)
            {
                double calib = PPreIsotonicCalibProb(features);
                calib = pIso.Length >= 4 ? ElmCalibrationHelper.ApplyIsotonicCalibration(calib, pIso) : calib;

                if (hp.AgeDecayLambda > 0.0)
                {
                    double daysSinceTrain = (DateTime.UtcNow - pTrainedAtUtc).TotalDays;
                    double decayFactor = Math.Exp(-hp.AgeDecayLambda * Math.Max(0.0, daysSinceTrain));
                    calib = 0.5 + (calib - 0.5) * decayFactor;
                }

                return calib;
            }

            var prunedMetrics = ElmEvaluationHelper.EvaluateEnsemble(
                maskedTest, pw, pb, piw, pib, pmw, pmb, pA, pB, featureCount, hiddenSize, psub,
                pmaw, pmab, sharpeAnnFactor,
                (f, w, b, iw, ib, pAp, pBp, fc, hs, fs, lw) => PPrimaryCalibProb(f),
                (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, pla));

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "ELM pruned model accepted: acc={Acc:P1} (was {Old:P1}), reduced features {Full}→{Active}",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy, featureCount, activeFeatureCount);
                weights = pw; biases = pb;
                inputWeights = piw; inputBiases = pib;
                featureSubsets = psub;
                learnerHiddenSizes = phs;
                learnerActivations = pla;
                inverseGrams = pInvGram;
                learnerCalAccuracies = pLearnerCalAccuracies;
                learnerAccWeights = pLearnerAccWeights;
                magWeights = pmw; magBias = pmb;
                magAugWeights = pmaw; magAugBias = pmab;
                magAugWeightsFolds = pMagAugWeightsFolds;
                magAugBiasFolds    = pMagAugBiasFolds;
                plattA = pA; plattB = pB;
                temperatureScale = pTemp;
                finalMetrics = prunedMetrics;

                // Reuse the stacking weights already fitted on the pruned model (pSw, pSb)
                // — same inputs would produce identical results.
                (stackingWeights, stackingBias) = (pSw, pSb);

                (plattABuy, plattBBuy, plattASell, plattBSell) = (pABuy, pBBuy, pASell, pBSell);
                avgKellyFraction = ElmCalibrationHelper.ComputeAvgKellyFraction(
                    maskedCal, pw, pb, piw, pib, plattA, plattB, featureCount, hiddenSize, psub,
                    (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => PPrimaryCalibProb(f));
                ece = ElmEvaluationHelper.ComputeEce(maskedTest, pw, pb, piw, pib, plattA, plattB, featureCount, hiddenSize, psub,
                    (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => PPrimaryCalibProb(f));
                optimalThreshold = ElmCalibrationHelper.ComputeOptimalThreshold(
                    maskedCal, pw, pb, piw, pib, plattA, plattB, featureCount, hiddenSize, psub,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax,
                    (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => PPrimaryCalibProb(f));

                featureImportance = maskedTest.Count >= 10
                    ? ElmEvaluationHelper.ComputePermutationImportance(
                        maskedTest, pw, pb, piw, pib, pA, pB, featureCount, hiddenSize, psub,
                        (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => PPrimaryCalibProb(f), ct)
                    : new float[featureCount];
                calImportanceScores = maskedCal.Count >= 10
                    ? ElmEvaluationHelper.ComputeCalPermutationImportance(
                        maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                        (f, w2, b2, iw2, ib2, fc, hs, fs, lw) => EnsembleRawProb(
                            f, w2, b2, iw2, ib2, fc, hs, fs, lw ?? pLearnerAccWeights, phs, pla, pSw, pSb), ct)
                    : new double[featureCount];

                // Advance the effective views so that steps 12-22 use the pruned model's
                // feature space rather than the original unmasked data.
                effectiveCalSet       = maskedCal;
                effectiveTestSet      = maskedTest;
                effectiveTrainSet     = maskedTrain;
                effectiveFeatureCount = featureCount;
            }
            else
            {
                _logger.LogInformation(
                    "ELM pruned model rejected (acc drop {Drop:P1}) — keeping full model",
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

        var trainedAtUtc = DateTime.UtcNow;

        double PrimaryEffectiveCalibProb(float[] features) => ApplyProductionCalibration(
            EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
                effectiveFeatureCount, hiddenSize, featureSubsets, learnerAccWeights,
                learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias),
            plattA, plattB, temperatureScale, plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 12. Isotonic calibration (PAVA) ──────────────────────────────────
        double[] isotonicBp = ElmCalibrationHelper.FitIsotonicCalibration(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryEffectiveCalibProb(f));
        _logger.LogInformation("ELM isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        if (hp.FitTemperatureScale && effectiveCalSet.Count >= 10)
        {
            double finalTemperatureScale = ElmCalibrationHelper.FitTemperatureScaling(
                effectiveCalSet,
                weights,
                biases,
                inputWeights,
                inputBiases,
                effectiveFeatureCount,
                hiddenSize,
                featureSubsets,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias),
                plattA,
                plattB,
                plattABuy,
                plattBBuy,
                plattASell,
                plattBSell,
                isotonicBp,
                hp.AgeDecayLambda,
                trainedAtUtc);

            if (Math.Abs(finalTemperatureScale - temperatureScale) > 1e-6)
            {
                temperatureScale = finalTemperatureScale;
                (plattABuy, plattBBuy, plattASell, plattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                    effectiveCalSet, weights, biases, inputWeights, inputBiases, effectiveFeatureCount, hiddenSize, featureSubsets,
                    plattA, plattB, temperatureScale,
                    (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                        f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                        learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias));

                double RefitPrimaryCalibProb(float[] features) => ApplyProductionCalibration(
                    EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
                        effectiveFeatureCount, hiddenSize, featureSubsets, learnerAccWeights,
                        learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias),
                    plattA, plattB, temperatureScale, plattABuy, plattBBuy, plattASell, plattBSell);

                isotonicBp = ElmCalibrationHelper.FitIsotonicCalibration(
                    effectiveCalSet, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
                    (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => RefitPrimaryCalibProb(f));
            }
        }

        double FinalEffectiveCalibProb(float[] features)
        {
            double calib = PrimaryEffectiveCalibProb(features);
            calib = isotonicBp.Length >= 4
                ? ElmCalibrationHelper.ApplyIsotonicCalibration(calib, isotonicBp)
                : calib;

            if (hp.AgeDecayLambda > 0.0)
            {
                double daysSinceTrain = (DateTime.UtcNow - trainedAtUtc).TotalDays;
                double decayFactor = Math.Exp(-hp.AgeDecayLambda * Math.Max(0.0, daysSinceTrain));
                calib = 0.5 + (calib - 0.5) * decayFactor;
            }

            return calib;
        }

        finalMetrics = ElmEvaluationHelper.EvaluateEnsemble(
            effectiveTestSet, weights, biases, inputWeights, inputBiases,
            magWeights, magBias, plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            magAugWeights, magAugBias, sharpeAnnFactor,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f),
            (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations));
        ece = ElmEvaluationHelper.ComputeEce(
            effectiveTestSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f));
        optimalThreshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f));

        // ── 13. Conformal prediction threshold ───────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ElmCalibrationHelper.ComputeConformalQHat(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, isotonicBp, effectiveFeatureCount, hiddenSize, featureSubsets, conformalAlpha,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryEffectiveCalibProb(f));
        _logger.LogInformation("ELM conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        ct.ThrowIfCancellationRequested();

        // ── 14. Meta-label secondary classifier ──────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            effectiveFeatureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
            optimalThreshold, FinalEffectiveCalibProb,
            stackingWeights, stackingBias,
            hp.ElmSubModelLr, hp.ElmSubModelMaxEpochs, hp.ElmSubModelPatience, ct);
        _logger.LogDebug("ELM meta-label: bias={B:F4}", metaLabelBias);

        // ── 15. Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, metaLabelWeights, metaLabelBias,
            effectiveFeatureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
            optimalThreshold, FinalEffectiveCalibProb,
            stackingWeights, stackingBias,
            hp.ElmSubModelLr, hp.ElmSubModelMaxEpochs, hp.ElmSubModelPatience, ct);
        _logger.LogDebug("ELM abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);

        // ── 16. Decision boundary distance ───────────────────────────────────
        var (dbMean, dbStd) = effectiveCalSet.Count >= 10
            ? ElmCalibrationHelper.ComputeDecisionBoundaryStats(
                effectiveCalSet, weights, biases, inputWeights, inputBiases,
                effectiveFeatureCount, hiddenSize, featureSubsets,
                (f, w, b, iw, ib, fc, hs, fs, lw) => EnsembleRawProb(
                    f, w, b, iw, ib, fc, hs, fs, lw ?? learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias))
            : (0.0, 0.0);

        // ── 17. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ElmEvaluationHelper.ComputeDurbinWatson(effectiveTrainSet, magWeights, magBias, effectiveFeatureCount,
            magAugWeights, magAugBias, hiddenSize, inputWeights, inputBiases, featureSubsets,
            (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations));
        _logger.LogDebug("ELM Durbin-Watson={DW:F4}", durbinWatson);

        // ── 18c. True OOB accuracy ─────────────────────────────────────────
        double oobAccuracy = 0.0;
        {
            var temporalW = ElmBootstrapHelper.ComputeTemporalWeights(effectiveTrainSet.Count, hp.TemporalDecayLambda);
            if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalW.Length)
            {
                var blended = new double[temporalW.Length];
                double bSum = 0.0;
                for (int i = 0; i < temporalW.Length; i++)
                {
                    blended[i] = temporalW[i] * densityWeights[i];
                    bSum += blended[i];
                }
                if (bSum > 1e-15)
                    for (int i = 0; i < blended.Length; i++) blended[i] /= bSum;
                temporalW = blended;
            }

            var oobMask = new bool[K][];
            for (int k = 0; k < K; k++)
            {
                oobMask[k] = new bool[effectiveTrainSet.Count];
                Array.Fill(oobMask[k], true);
                int oobSeed = ElmMathHelper.HashSeed(hp.ElmOuterSeed, k, 7);
                var bootstrapIndices = ElmBootstrapHelper.ReplayBootstrapIndices(effectiveTrainSet, temporalW, effectiveTrainSet.Count, seed: oobSeed);
                foreach (int idx in bootstrapIndices)
                    oobMask[k][idx] = false;
            }

            int oobCorrect = 0, oobTotal = 0;
            for (int i = 0; i < effectiveTrainSet.Count; i++)
            {
                double oobSum = 0;
                int oobLearners = 0;
                for (int k = 0; k < K; k++)
                {
                    if (!oobMask[k][i]) continue;
                    oobSum += ElmLearnerProb(
                        effectiveTrainSet[i].Features, weights[k], biases[k],
                        inputWeights[k], inputBiases[k],
                        effectiveFeatureCount, learnerHiddenSizes[k], featureSubsets?[k],
                        k < learnerActivations.Length ? learnerActivations[k] : hp.ElmActivation);
                    oobLearners++;
                }
                if (oobLearners == 0) continue;

                double oobProb = oobSum / oobLearners;
                oobTotal++;
                if ((oobProb >= 0.5 ? 1 : 0) == effectiveTrainSet[i].Direction) oobCorrect++;
            }
            oobAccuracy = oobTotal > 0 ? (double)oobCorrect / oobTotal : 0;
            _logger.LogInformation("ELM true OOB accuracy={OobAcc:P1} ({OobN}/{Total} samples had OOB learners)",
                oobAccuracy, oobTotal, effectiveTrainSet.Count);
        }
        finalMetrics = finalMetrics with { OobAccuracy = oobAccuracy };

        // ── 18d. Jackknife+ residuals ────────────────────────────────────────
        double[] jackknifeResiduals = [];
        if (effectiveCalSet.Count >= 10)
        {
            var residuals = new double[effectiveCalSet.Count];
            for (int i = 0; i < effectiveCalSet.Count; i++)
            {
                double y = effectiveCalSet[i].Direction > 0 ? 1.0 : 0.0;
                residuals[i] = Math.Abs(y - FinalEffectiveCalibProb(effectiveCalSet[i].Features));
            }
            Array.Sort(residuals);
            jackknifeResiduals = residuals;
        }

        // ── 19. Ensemble diversity metric ────────────────────────────────────
        double ensembleDiversity = ElmEvaluationHelper.ComputeEnsembleDiversity(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            effectiveFeatureCount, hiddenSize, featureSubsets,
            (f, wk, bk, iwk, ibk, fc, hs, sub, learnerIdx) => ElmLearnerProb(
                f, wk, bk, iwk, ibk, fc, hs, sub,
                learnerIdx < learnerActivations.Length ? learnerActivations[learnerIdx] : hp.ElmActivation));
        _logger.LogDebug("ELM ensemble diversity={Div:F4}", ensembleDiversity);

        const double minDiversityThreshold = 0.05;
        if (K > 1 && ensembleDiversity < minDiversityThreshold)
        {
            _logger.LogWarning(
                "ELM diversity gate: ensemble diversity {Div:F4} < {Thr:F4}. " +
                "Learners are highly correlated — consider increasing hidden size variation or feature subset ratio.",
                ensembleDiversity, minDiversityThreshold);
        }

        // ── 20. Brier Skill Score ────────────────────────────────────────────
        double brierSkillScore = ElmEvaluationHelper.ComputeBrierSkillScore(
            effectiveTestSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f));
        _logger.LogInformation("ELM BSS={BSS:F4}", brierSkillScore);

        // ── 20b. Drift detection statistics ───────────────────────────────────
        double driftMeanProb = 0, driftStdProb = 0, driftScoreThreshold = 0;
        double[] driftFeatureMeans = [];
        double[] driftFeatureStds = [];
        if (effectiveCalSet.Count >= 10)
        {
            // Compute prediction distribution on calibration set
            var calProbs = new double[effectiveCalSet.Count];
            for (int i = 0; i < effectiveCalSet.Count; i++)
            {
                calProbs[i] = EnsembleRawProb(
                    effectiveCalSet[i].Features, weights, biases, inputWeights, inputBiases,
                    effectiveFeatureCount, hiddenSize, featureSubsets, learnerAccWeights,
                    learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);
            }
            double probSum = 0, probSumSq = 0;
            for (int i = 0; i < calProbs.Length; i++)
            {
                probSum += calProbs[i];
                probSumSq += calProbs[i] * calProbs[i];
            }
            driftMeanProb = probSum / calProbs.Length;
            driftStdProb = Math.Sqrt(Math.Max(0, probSumSq / calProbs.Length - driftMeanProb * driftMeanProb));

            // Compute z-scores and find 95th percentile as drift threshold
            if (driftStdProb > 1e-10)
            {
                var zScores = new double[calProbs.Length];
                for (int i = 0; i < calProbs.Length; i++)
                    zScores[i] = Math.Abs(calProbs[i] - driftMeanProb) / driftStdProb;
                Array.Sort(zScores);
                driftScoreThreshold = zScores[(int)(zScores.Length * 0.95)];
            }

            // Compute per-feature means and stds on calibration set
            int fc = effectiveCalSet[0].Features.Length;
            driftFeatureMeans = new double[fc];
            driftFeatureStds = new double[fc];
            for (int j = 0; j < fc; j++)
            {
                double fSum = 0, fSumSq = 0;
                for (int i = 0; i < effectiveCalSet.Count; i++)
                {
                    double v = effectiveCalSet[i].Features[j];
                    fSum += v;
                    fSumSq += v * v;
                }
                driftFeatureMeans[j] = fSum / effectiveCalSet.Count;
                driftFeatureStds[j] = Math.Sqrt(Math.Max(0, fSumSq / effectiveCalSet.Count - driftFeatureMeans[j] * driftFeatureMeans[j]));
            }

            _logger.LogInformation(
                "ELM drift detection: calMeanP={Mean:F4} calStdP={Std:F4} threshold={Thr:F2}",
                driftMeanProb, driftStdProb, driftScoreThreshold);
        }

        // ── 21. Feature quantile breakpoints ──────────────────────────────────
        var standardisedTrainFeatures = new List<float[]>(effectiveTrainSet.Count);
        foreach (var s in effectiveTrainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 22. Mutual-information feature redundancy ────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ElmEvaluationHelper.ComputeRedundantFeaturePairs(effectiveTrainSet, effectiveFeatureCount, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "ELM MI redundancy: {N} feature pairs exceed threshold: {Pairs}",
                    redundantPairs.Length, string.Join(", ", redundantPairs));
        }

        // ── 23. Serialise model snapshot ────────────────────────────────────
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
            MagAugWeights              = magAugWeights,
            MagAugBias                 = magAugBias,
            LearnerAccuracyWeights     = learnerAccWeights ?? [],
            PlattA                     = plattA,
            PlattB                     = plattB,
            Metrics                    = finalMetrics,
            TrainSamples               = trainSet.Count,
            TestSamples                = testSet.Count,
            CalSamples                 = calSet.Count,
            EmbargoSamples             = embargo,
            TrainedOn                  = trainedAtUtc,
            FeatureImportance          = featureImportance,
            FeatureImportanceScores    = calImportanceScores,
            ActiveFeatureMask          = activeMask,
            PrunedFeatureCount         = prunedCount,
            FeatureSubsetIndices       = featureSubsets,
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
            EnsembleDiversity          = ensembleDiversity,
            BrierSkillScore            = brierSkillScore,
            TrainedAtUtc               = trainedAtUtc,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            LearnerCalAccuracies       = learnerCalAccuracies,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount      = sanitizedCount,
            ConformalCoverage          = hp.ConformalCoverage,
            ElmOutputWeights           = null,
            ElmInverseGram             = inverseGrams,
            ElmInputWeights            = inputWeights,
            ElmInputBiases             = inputBiases,
            ElmHiddenDim               = hiddenSize,
            ElmDropoutRate             = Math.Clamp(hp.ElmDropoutRate, 0.0, 0.5),
            LearnerActivations         = learnerActivations.Select(a => (int)a).ToArray(),
            MagAugWeightsFolds         = magAugWeightsFolds,
            MagAugBiasFolds            = magAugBiasFolds,
            DriftDetectionMeanProb     = driftMeanProb,
            DriftDetectionStdProb      = driftStdProb,
            DriftScoreThreshold        = driftScoreThreshold,
            DriftDetectionFeatureMeans = driftFeatureMeans,
            DriftDetectionFeatureStds  = driftFeatureStds,
            MetaWeights                = stackingWeights ?? [],
            MetaBias                   = stackingBias,
            EnsembleSelectionWeights   = [],
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "ElmModelTrainer complete: K={K}, hidden={H}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            K, hiddenSize, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Walk-forward cross-validation
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  hiddenSize,
        CancellationToken    ct,
        double               sharpeAnnualisationFactor = DefaultSharpeAnnualisationFactor)
    {
        int folds   = hp.WalkForwardFolds;
        int embargo = hp.EmbargoBarCount;
        int K       = Math.Max(1, hp.K);

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("ELM walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        int cvInnerParallelism = Math.Max(1, Environment.ProcessorCount / Math.Max(1, folds));
        Parallel.For(0, folds, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(folds, Environment.ProcessorCount))
        }, fold =>
        {
            ct.ThrowIfCancellationRequested();

            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("ELM CV fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                return;
            }

            var fullFoldTrain = samples[..trainEnd].ToList();

            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < fullFoldTrain.Count)
                    fullFoldTrain = fullFoldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            // Carve a mini-cal set from the tail of the fold-train window BEFORE fitting
            // the ensemble, so base learners never see the cal samples via bootstrap.
            int cvCalSize = fullFoldTrain.Count / 7; // ~14 % of fold-train as mini cal
            List<TrainingSample> foldTrain;
            List<TrainingSample>? cvCalSet = null;
            if (cvCalSize >= 20)
            {
                int calStart = fullFoldTrain.Count - cvCalSize;
                cvCalSet  = fullFoldTrain[calStart..];
                foldTrain = fullFoldTrain[..calStart];
            }
            else
            {
                foldTrain = fullFoldTrain;
            }

            if (foldTrain.Count < hp.MinSamples) return;

            var cvLabelSmoothing = hp.LabelSmoothing;
            var (w, b, iw, ib, subs, lhs, cvla, _) = FitBaggedElm(
                foldTrain, hp, featureCount, hiddenSize, Math.Max(1, K / 2),
                cvLabelSmoothing, null, null, ct,
                maxInnerParallelism: cvInnerParallelism);
            var (mw, mb, maw, mab) = FitElmMagnitudeRegressor(
                foldTrain, featureCount, hiddenSize, iw, ib, subs, cvla,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience);

            // Fit a lightweight Platt calibration on the held-out mini-cal set so
            // that CV fold evaluation uses calibrated probabilities, matching the full
            // training pipeline. Using raw probabilities (plattA=1, plattB=0) overstates
            // Brier/EV metrics on folds where the ensemble is poorly calibrated.
            double cvPlattA = 1.0, cvPlattB = 0.0;
            double cvTemp = 0.0;
            double cvPlattABuy = 0.0, cvPlattBBuy = 0.0, cvPlattASell = 0.0, cvPlattBSell = 0.0;
            double[]? cvLearnerAccWeights = null;
            if (cvCalSet is not null)
            {
                (_, cvLearnerAccWeights) = ComputeLearnerCalibrationStats(
                    cvCalSet, w, b, iw, ib, featureCount, subs, lhs, cvla, hp.ElmActivation);
                (cvPlattA, cvPlattB) = ElmCalibrationHelper.FitPlattScalingCV(
                    cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                    (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                        f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
                if (hp.FitTemperatureScale && cvCalSet.Count >= 10)
                {
                    cvTemp = ElmCalibrationHelper.FitTemperatureScaling(
                        cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                        (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                            f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
                }
                (cvPlattABuy, cvPlattBBuy, cvPlattASell, cvPlattBSell) = ElmCalibrationHelper.FitClassConditionalPlatt(
                    cvCalSet, w, b, iw, ib, featureCount, hiddenSize, subs,
                    cvPlattA, cvPlattB, cvTemp,
                    (f, ww, bb, iww, ibb, fc, hs, fs, lw) => EnsembleRawProb(
                        f, ww, bb, iww, ibb, fc, hs, fs, lw ?? cvLearnerAccWeights, lhs, cvla));
            }

            double CvPrimaryCalibProb(float[] features) => ApplyProductionCalibration(
                EnsembleRawProb(features, w, b, iw, ib, featureCount, hiddenSize, subs, cvLearnerAccWeights, lhs, cvla),
                cvPlattA, cvPlattB, cvTemp, cvPlattABuy, cvPlattBBuy, cvPlattASell, cvPlattBSell);

            var m = ElmEvaluationHelper.EvaluateEnsemble(foldTest, w, b, iw, ib, mw, mb, cvPlattA, cvPlattB, featureCount, hiddenSize, subs,
                maw, mab, sharpeAnnualisationFactor,
                (f, ww, bb, iww, ibb, pA, pB, fc, hs, fs, lw) => CvPrimaryCalibProb(f),
                (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, cvla));

            var foldImp = new double[featureCount];
            for (int ki = 0; ki < w.Length; ki++)
            {
                int[] sub = subs is not null && ki < subs.Length ? subs[ki]
                    : Enumerable.Range(0, featureCount).ToArray();
                int subLen = sub.Length;
                for (int h = 0; h < Math.Min(w[ki].Length, lhs[ki]); h++)
                {
                    double outMag = Math.Abs(w[ki][h]);
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen; si++)
                    {
                        int fi = sub[si];
                        if (fi < featureCount && rowOff + si < iw[ki].Length)
                            foldImp[fi] += outMag * Math.Abs(iw[ki][rowOff + si]);
                    }
                }
            }
            double kCount = w.Length;
            for (int j = 0; j < featureCount; j++) foldImp[j] /= kCount;

            var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
            for (int pi = 0; pi < foldTest.Count; pi++)
            {
                double calibP = CvPrimaryCalibProb(foldTest[pi].Features);
                foldPredictions[pi] = (calibP >= 0.5 ? 1 : -1,
                                       foldTest[pi].Direction > 0 ? 1 : -1);
            }

            var (foldMaxDD, foldCurveSharpe) = ElmMathHelper.ComputeEquityCurveStats(foldPredictions, sharpeAnnualisationFactor);

            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                isBadFold = true;
            if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                isBadFold = true;

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

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

        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "ELM equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc      = accList.Average();
        double stdAcc      = ElmMathHelper.StdDev(accList, avgAcc);
        double sharpeTrend = ElmMathHelper.ComputeSharpeTrend(sharpeList);

        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "ELM Sharpe trend gate: slope={Slope:F3} < threshold {Thr:F3}. Model rejected.",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        double[]? featureStabilityScores = null;
        if (foldImps.Count >= 2)
        {
            featureStabilityScores = new double[featureCount];
            int foldCount = foldImps.Count;
            for (int j = 0; j < featureCount; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += foldImps[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = foldImps[fi][j] - meanImp;
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

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ridge lambda auto-selection
    // ═══════════════════════════════════════════════════════════════════════════

    private static double SelectRidgeLambda(
        List<TrainingSample> trainSet,
        int featureCount, int hiddenSize, double labelSmoothing,
        TrainingHyperparams hp, CancellationToken ct)
    {
        double[] candidates = [1e-6, 3e-6, 1e-5, 3e-5, 1e-4, 3e-4, 1e-3, 3e-3, 1e-2, 3e-2, 1e-1, 3e-1];
        double bestLambda = 1e-3;
        double bestAvgAcc = -1;

        int embargo = hp.EmbargoBarCount;
        const int cvFolds = 5;
        int foldSize = trainSet.Count / (cvFolds + 1);
        if (foldSize < 10) return bestLambda;

        // Use multiple random projections so the selected lambda generalises
        // across diverse hidden layers, not just a single lucky projection.
        // When mixed activations are enabled, rotate activations across probes
        // to match FitBaggedElm's per-learner activation diversity.
        int nProbes   = Math.Max(1, Math.Min(Math.Max(1, hp.K), 3));
        int subsetLen = featureCount;
        double scale  = Math.Sqrt(2.0 / (subsetLen + hiddenSize));

        var availableActivations = new[] { ElmActivation.Sigmoid, ElmActivation.Tanh, ElmActivation.Relu };
        var probeWIn = new double[nProbes][];
        var probeBIn = new double[nProbes][];
        var probeActivations = new ElmActivation[nProbes];
        for (int pi = 0; pi < nProbes; pi++)
        {
            var probeRng = new Random(ElmMathHelper.HashSeed(hp.ElmOuterSeed, pi, 777));
            probeWIn[pi] = new double[hiddenSize * subsetLen];
            probeBIn[pi] = new double[hiddenSize];
            for (int i = 0; i < probeWIn[pi].Length; i++) probeWIn[pi][i] = ElmMathHelper.SampleGaussian(probeRng) * scale;
            for (int h = 0; h < hiddenSize; h++) probeBIn[pi][h] = ElmMathHelper.SampleGaussian(probeRng) * scale;
            probeActivations[pi] = hp.ElmMixActivations
                ? availableActivations[pi % availableActivations.Length]
                : hp.ElmActivation;
        }

        int solveSize = hiddenSize + 1;
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;

        foreach (double lambda in candidates)
        {
            ct.ThrowIfCancellationRequested();
            double totalAccSum = 0;
            int    totalValid  = 0;

            for (int pi = 0; pi < nProbes; pi++)
            {
                double[] wIn = probeWIn[pi];
                double[] bIn = probeBIn[pi];
                ElmActivation probeAct = probeActivations[pi];

                for (int fold = 0; fold < cvFolds; fold++)
                {
                    int valEnd = (fold + 2) * foldSize;
                    int valStart = valEnd - foldSize;
                    int trainEndIdx = Math.Max(0, valStart - embargo);
                    if (trainEndIdx < 20) continue;
                    int actualValEnd = Math.Min(valEnd, trainSet.Count);
                    int actualValStart = Math.Min(valStart + embargo, actualValEnd);
                    if (actualValEnd - actualValStart < 5) continue;

                    double[,] HtH = new double[solveSize, solveSize];
                    double[] HtY = new double[solveSize];
                    double[] hRow = new double[solveSize];

                    for (int t = 0; t < trainEndIdx; t++)
                    {
                        var features = trainSet[t].Features;
                        for (int h = 0; h < hiddenSize; h++)
                        {
                            double z = bIn[h];
                            int rowOff = h * subsetLen;
                            for (int si = 0; si < subsetLen; si++)
                                if (si < features.Length) z += wIn[rowOff + si] * features[si];
                            hRow[h] = ElmMathHelper.Activate(z, probeAct);
                        }
                        hRow[hiddenSize] = 1.0;
                        double yt = trainSet[t].Direction > 0 ? posLabel : negLabel;
                        for (int ri = 0; ri < solveSize; ri++)
                        {
                            HtY[ri] += hRow[ri] * yt;
                            for (int j = ri; j < solveSize; j++)
                                HtH[ri, j] += hRow[ri] * hRow[j];
                        }
                    }

                    for (int ri = 0; ri < solveSize; ri++)
                    {
                        if (ri < hiddenSize) HtH[ri, ri] += lambda;
                        for (int j = ri + 1; j < solveSize; j++)
                            HtH[j, ri] = HtH[ri, j];
                    }

                    double[] wSolve = new double[solveSize];
                    ElmMathHelper.CholeskySolve(HtH, HtY, wSolve, solveSize);

                    int correct = 0, valCount = 0;
                    for (int vi = actualValStart; vi < actualValEnd; vi++)
                    {
                        var s = trainSet[vi];
                        double score = wSolve[hiddenSize];
                        for (int h = 0; h < hiddenSize; h++)
                        {
                            double z = bIn[h];
                            int rowOff = h * subsetLen;
                            for (int si = 0; si < subsetLen; si++)
                                if (si < s.Features.Length) z += wIn[rowOff + si] * s.Features[si];
                            score += wSolve[h] * ElmMathHelper.Activate(z, probeAct);
                        }
                        int pred = MLFeatureHelper.Sigmoid(score) >= 0.5 ? 1 : 0;
                        if (pred == s.Direction) correct++;
                        valCount++;
                    }

                    if (valCount > 0)
                    {
                        totalAccSum += (double)correct / valCount;
                        totalValid++;
                    }
                }
            }

            if (totalValid == 0) continue;
            double avgAcc = totalAccSum / totalValid;
            if (avgAcc > bestAvgAcc) { bestAvgAcc = avgAcc; bestLambda = lambda; }
        }
        return bestLambda;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Bagged ELM ensemble fitting
    // ═══════════════════════════════════════════════════════════════════════════

    private (double[][] Weights, double[] Biases,
             double[][] InputWeights, double[][] InputBiases,
             int[][]? FeatureSubsets, int[] LearnerHiddenSizes,
             ElmActivation[] LearnerActivations, double[][,] InverseGrams) FitBaggedElm(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  hiddenSize,
        int                  K,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct,
        bool[]?              activeFeatureMask = null,
        int                  maxInnerParallelism = 0)
    {
        var weights      = new double[K][];
        var biases       = new double[K];
        var inputWeights = new double[K][];
        var inputBiases  = new double[K][];
        var cgDidNotConverge = new bool[K];
        var learnerHiddenSizes = new int[K];
        var learnerActivations = new ElmActivation[K];
        var inverseGrams = new double[K][,];

        bool useSubsampling = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0;
        var featureSubsets   = useSubsampling ? new int[K][] : null;

        int outerSeed = hp.ElmOuterSeed;

        var temporalWeights = ElmBootstrapHelper.ComputeTemporalWeights(train.Count, hp.TemporalDecayLambda);

        if (densityWeights is { Length: > 0 } && densityWeights.Length == temporalWeights.Length)
        {
            var blended = new double[temporalWeights.Length];
            double sum = 0.0;
            for (int i = 0; i < temporalWeights.Length; i++)
            {
                blended[i] = temporalWeights[i] * densityWeights[i];
                sum += blended[i];
            }
            if (sum > 1e-15)
                for (int i = 0; i < blended.Length; i++) blended[i] /= sum;
            temporalWeights = blended;
        }

        // ── Class imbalance handling: class weights (preferred) or SMOTE (legacy) ──
        bool useClassWeights = hp.ElmUseClassWeights;
        double classWeightBuy = 1.0, classWeightSell = 1.0;
        bool smoteEnabled = false;
        List<TrainingSample>? smoteMinoritySamples = null;
        int smoteSyntheticNeeded = 0;
        int smoteKNeighbors = 5;

        {
            int buyCount = 0, sellCount = 0;
            foreach (var s in train)
            {
                if (s.Direction > 0) buyCount++;
                else sellCount++;
            }

            if (useClassWeights && buyCount > 0 && sellCount > 0)
            {
                // Inverse-frequency class weights: minority class gets higher weight
                double total = buyCount + sellCount;
                classWeightBuy = total / (2.0 * buyCount);
                classWeightSell = total / (2.0 * sellCount);
                _logger.LogDebug(
                    "ELM class weights: buy={Buy:F3} sell={Sell:F3} (buy={BuyN} sell={SellN})",
                    classWeightBuy, classWeightSell, buyCount, sellCount);
            }
            else if (hp.ElmUseSmote && !useClassWeights)
            {
                // Legacy SMOTE path — only used when ElmUseClassWeights is explicitly disabled
                int majCount = Math.Max(buyCount, sellCount);
                int minCount = Math.Min(buyCount, sellCount);
                double minRatio = majCount > 0 ? (double)minCount / majCount : 1.0;

                smoteKNeighbors = hp.SmoteKNeighbors is > 0 ? hp.SmoteKNeighbors.Value : 5;
                int minSmoteFloor = smoteKNeighbors + 1;
                double extremeImbalanceFloor = 0.05;
                if (minCount >= minSmoteFloor && minRatio >= extremeImbalanceFloor && minRatio < hp.ElmSmoteMinorityRatioThreshold)
                {
                    smoteEnabled = true;
                    var buyList = new List<TrainingSample>();
                    var sellList = new List<TrainingSample>();
                    foreach (var s in train)
                    {
                        if (s.Direction > 0) buyList.Add(s);
                        else sellList.Add(s);
                    }
                    smoteMinoritySamples = buyList.Count < sellList.Count ? buyList : sellList;
                    smoteSyntheticNeeded = majCount - minCount;
                }
                else if (minCount < minSmoteFloor || minRatio < extremeImbalanceFloor)
                {
                    _logger.LogWarning(
                        "ELM SMOTE skipped: minority count {MinCount} or ratio {Ratio:P1} too low for reliable interpolation (need ≥{Floor} samples and ≥{FloorRatio:P0} ratio).",
                        minCount, minRatio, minSmoteFloor, extremeImbalanceFloor);
                }
            }
        }

        double ridgeLambda = Math.Max(1e-6, hp.L2Lambda > 0 ? hp.L2Lambda : 1e-3);
        double maxWeightMag = hp.MaxWeightMagnitude > 0 ? hp.MaxWeightMagnitude : 10.0;
        double dropRate = Math.Clamp(hp.ElmDropoutRate, 0.0, 0.5);
        double dropScale = dropRate > 0.0 ? 1.0 / (1.0 - dropRate) : 1.0;
        double smoteSampleWeight = Math.Clamp(hp.ElmSmoteSampleWeight, 0.01, 1.0);
        ElmActivation activation = hp.ElmActivation;

        bool useBiasedFeatureSampling =
            warmStart is not null &&
            warmStart.FeatureImportanceScores.Length == featureCount &&
            hp.FeatureSampleRatio > 0.0;

        int effectiveParallelism = maxInnerParallelism > 0
            ? maxInnerParallelism
            : Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, K, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = effectiveParallelism
        }, k =>
        {
            ct.ThrowIfCancellationRequested();

            // ── Per-learner hidden size variation ──────────────────────────
            int learnerHidden = hiddenSize;
            if (hp.ElmHiddenSizeVariation > 0.0)
            {
                var hiddenRng = new Random(ElmMathHelper.HashSeed(outerSeed, k, 99));
                double variation = hp.ElmHiddenSizeVariation;
                double factor = 1.0 + (hiddenRng.NextDouble() * 2.0 - 1.0) * variation;
                learnerHidden = Math.Max(8, (int)(hiddenSize * factor));
            }
            // Safe in Parallel.For: each iteration writes to its own index k — no contention.
            learnerHiddenSizes[k] = learnerHidden;

            // ── Per-learner activation (mixed activation ensemble) ─────────
            ElmActivation learnerAct;
            if (hp.ElmMixActivations)
            {
                var availableActivations = new[] { ElmActivation.Sigmoid, ElmActivation.Tanh, ElmActivation.Relu };
                learnerAct = availableActivations[k % availableActivations.Length];
            }
            else
            {
                learnerAct = activation;
            }
            learnerActivations[k] = learnerAct;

            int learnerSeed = ElmMathHelper.HashSeed(outerSeed, k, 42);
            int featureSeed = ElmMathHelper.HashSeed(outerSeed, k, 13);
            var rng = new Random(learnerSeed);

            // ── Feature subset ──────────────────────────────────────────────
            int[] eligibleIndices = activeFeatureMask is not null
                ? Enumerable.Range(0, featureCount).Where(i => activeFeatureMask[i]).ToArray()
                : Enumerable.Range(0, featureCount).ToArray();

            int[] subset;
            if (useSubsampling)
            {
                if (activeFeatureMask is not null)
                {
                    subset = useBiasedFeatureSampling
                        ? ElmBootstrapHelper.GenerateBiasedFeatureSubsetFromPool(eligibleIndices, hp.FeatureSampleRatio,
                            warmStart!.FeatureImportanceScores, seed: featureSeed)
                        : ElmBootstrapHelper.GenerateFeatureSubsetFromPool(eligibleIndices, hp.FeatureSampleRatio, seed: featureSeed);
                }
                else
                {
                    subset = useBiasedFeatureSampling
                        ? ElmBootstrapHelper.GenerateBiasedFeatureSubset(featureCount, hp.FeatureSampleRatio,
                            warmStart!.FeatureImportanceScores, seed: featureSeed)
                        : ElmBootstrapHelper.GenerateFeatureSubset(featureCount, hp.FeatureSampleRatio, seed: featureSeed);
                }
            }
            else
            {
                subset = eligibleIndices;
            }
            if (featureSubsets is not null) featureSubsets[k] = subset;

            int subsetLen = subset.Length;

            // ── Xavier-init random input weights ───
            double scale = Math.Sqrt(2.0 / (subsetLen + learnerHidden));
            double[] wIn = new double[learnerHidden * subsetLen];
            double[] bIn = new double[learnerHidden];

            if (warmStart?.ElmInputWeights is not null &&
                k < warmStart.ElmInputWeights.Length &&
                warmStart.ElmInputWeights[k]?.Length == learnerHidden * subsetLen)
            {
                Array.Copy(warmStart.ElmInputWeights[k], wIn, wIn.Length);
                if (warmStart.ElmInputBiases is not null && k < warmStart.ElmInputBiases.Length)
                    Array.Copy(warmStart.ElmInputBiases[k], bIn, bIn.Length);
            }
            else
            {
                if (warmStart?.ElmInputWeights is not null && k < warmStart.ElmInputWeights.Length)
                    _logger.LogWarning(
                        "ELM learner {K}: warm-start dimension mismatch (expected {Expected}, got {Actual}). Falling back to random init.",
                        k, learnerHidden * subsetLen, warmStart.ElmInputWeights[k]?.Length ?? 0);

                for (int i = 0; i < wIn.Length; i++) wIn[i] = ElmMathHelper.SampleGaussian(rng) * scale;
                for (int h = 0; h < learnerHidden; h++) bIn[h] = ElmMathHelper.SampleGaussian(rng) * scale;
            }

            inputWeights[k] = wIn;
            inputBiases[k]  = bIn;

            // ── Stratified biased bootstrap ──
            int bootstrapSeed = ElmMathHelper.HashSeed(outerSeed, k, 7);
            var bootstrap = ElmBootstrapHelper.StratifiedBiasedBootstrap(train, temporalWeights, train.Count, seed: bootstrapSeed);

            // ── Per-learner SMOTE with sample weighting ──
            // Track which samples are synthetic to apply differential weighting
            bool[]? isSynthetic = null;
            if (smoteEnabled && smoteMinoritySamples is not null)
            {
                int smoteSeed = ElmMathHelper.HashSeed(outerSeed, k, 9999);
                var syntheticPairs = ElmBootstrapHelper.GenerateSmoteSamples(smoteMinoritySamples, smoteSyntheticNeeded, smoteKNeighbors, smoteSeed);
                int originalCount = bootstrap.Count;
                isSynthetic = new bool[originalCount + syntheticPairs.Count];
                foreach (var (sample, _) in syntheticPairs)
                    bootstrap.Add(sample);
                for (int i = originalCount; i < bootstrap.Count; i++) isSynthetic[i] = true;
            }
            int N = bootstrap.Count;

            // ── Compute H^TH and H^TY ──
            double posLabel = 1.0 - labelSmoothing;
            double negLabel = labelSmoothing;

            int solveSize = learnerHidden + 1;
            double[,] HtH = new double[solveSize, solveSize];
            double[] HtY  = new double[solveSize];

            var dropMask = new bool[learnerHidden];
            double[] hRow = new double[solveSize];

            for (int t = 0; t < N; t++)
            {
                // Per-sample dropout mask
                if (dropRate > 0.0)
                    for (int h = 0; h < learnerHidden; h++)
                        dropMask[h] = rng.NextDouble() >= dropRate;

                var features = bootstrap[t].Features;
                for (int h = 0; h < learnerHidden; h++)
                {
                    if (dropRate > 0.0 && !dropMask[h]) { hRow[h] = 0.0; continue; }

                    double z = bIn[h];
                    int rowOff = h * subsetLen;
                    // SIMD-accelerated dot product
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, subset, subsetLen);
                    double act = ElmMathHelper.Activate(z, learnerAct);
                    hRow[h] = (double.IsFinite(act) ? act : 0.5) * (dropRate > 0.0 ? dropScale : 1.0);
                }
                hRow[learnerHidden] = 1.0;
                double yt = bootstrap[t].Direction > 0 ? posLabel : negLabel;

                // Apply sample weighting: class weights (inverse-frequency) and/or SMOTE down-weighting
                double sampleW = 1.0;
                if (useClassWeights)
                    sampleW *= bootstrap[t].Direction > 0 ? classWeightBuy : classWeightSell;
                if (isSynthetic is not null && t < isSynthetic.Length && isSynthetic[t])
                    sampleW *= smoteSampleWeight;

                for (int i = 0; i < solveSize; i++)
                {
                    HtY[i] += hRow[i] * yt * sampleW;
                    for (int j = i; j < solveSize; j++)
                        HtH[i, j] += hRow[i] * hRow[j] * sampleW;
                }
            }

            // ── Degenerate activation detection ──
            {
                int saturatedUnits = 0;
                for (int h = 0; h < learnerHidden; h++)
                {
                    double diagH = HtH[h, h];
                    double saturationThreshold = 1e-6 * N;
                    if (diagH < saturationThreshold || diagH > N - saturationThreshold)
                        saturatedUnits++;
                }
                if (saturatedUnits > learnerHidden / 2)
                    _logger.LogWarning(
                        "ELM learner {K}: {Sat}/{H} hidden units are saturated. " +
                        "Consider reducing input weight scale, increasing ridge lambda, or trying a different activation.",
                        k, saturatedUnits, learnerHidden);
            }

            // Symmetric fill + ridge
            for (int i = 0; i < solveSize; i++)
            {
                if (i < learnerHidden) HtH[i, i] += ridgeLambda;
                for (int j = i + 1; j < solveSize; j++)
                    HtH[j, i] = HtH[i, j];
            }

            var hiddenGram = new double[learnerHidden, learnerHidden];
            for (int i = 0; i < learnerHidden; i++)
                for (int j = 0; j < learnerHidden; j++)
                    hiddenGram[i, j] = HtH[i, j];

            var inverseGram = new double[learnerHidden, learnerHidden];
            if (ElmMathHelper.TryInvertSpd(hiddenGram, inverseGram, learnerHidden))
            {
                inverseGrams[k] = inverseGram;
            }
            else
            {
                _logger.LogWarning(
                    "ELM learner {K}: failed to invert hidden Gram matrix; online updates disabled for this learner",
                    k);
            }

            // ── Cholesky solve ──
            double[] wSolve = new double[solveSize];
            bool choleskyOk = ElmMathHelper.CholeskySolve(HtH, HtY, wSolve, solveSize);

            if (!choleskyOk)
                cgDidNotConverge[k] = true;

            double[] wOut = new double[learnerHidden];
            Array.Copy(wSolve, wOut, learnerHidden);
            double outBias = wSolve[learnerHidden];

            bool solveIsFinite = double.IsFinite(outBias);
            if (solveIsFinite)
                for (int i = 0; i < learnerHidden; i++)
                    if (!double.IsFinite(wSolve[i])) { solveIsFinite = false; break; }

            if (!solveIsFinite)
            {
                Array.Clear(wOut, 0, learnerHidden);
                outBias = 0.0;
            }
            else
            {
                for (int i = 0; i < learnerHidden; i++)
                    wOut[i] = Math.Clamp(wOut[i], -maxWeightMag, maxWeightMag);
            }

            weights[k] = wOut;
            biases[k]  = Math.Clamp(outBias, -maxWeightMag, maxWeightMag);
        });

        int cgFailCount = cgDidNotConverge.Count(f => f);
        if (cgFailCount > 0)
            _logger.LogWarning(
                "ELM Cholesky solver failed for {N}/{K} learners — consider increasing ridge lambda.",
                cgFailCount, K);

        return (weights, biases, inputWeights, inputBiases, featureSubsets, learnerHiddenSizes, learnerActivations, inverseGrams);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ELM inference helpers (with SIMD + configurable activation)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ElmLearnerProb(
        float[] features, double[] wOut, double bias,
        double[] wIn, double[] bIn,
        int featureCount, int hiddenSize, int[]? subset,
        ElmActivation activation)
    {
        int subsetLen = subset?.Length ?? featureCount;
        double score = bias;
        for (int h = 0; h < hiddenSize; h++)
        {
            double z = bIn[h];
            int rowOff = h * subsetLen;
            if (subset is not null)
                z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, subset, subsetLen);
            else
            {
                int len = Math.Min(subsetLen, features.Length);
                z += ElmMathHelper.DotProductSimdContiguous(wIn, rowOff, features, len);
            }
            double hAct = ElmMathHelper.Activate(z, activation);
            if (h < wOut.Length) score += wOut[h] * hAct;
        }
        return MLFeatureHelper.Sigmoid(score);
    }

    private static double EnsembleRawProb(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double[]? learnerWeights,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        int K = weights.Length;

        // Stacking meta-learner: logistic regression over per-learner probabilities.
        // When active, this replaces both uniform and accuracy-weighted averaging,
        // learning optimal per-learner combination weights.
        if (stackingWeights is not null && stackingWeights.Length == K)
        {
            double z = stackingBias;
            for (int k = 0; k < K; k++)
            {
                double pk = ElmLearnerProb(
                    features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                    k < learnerActivations.Length ? learnerActivations[k] : learnerActivations[0]);
                z += stackingWeights[k] * pk;
            }
            return MLFeatureHelper.Sigmoid(z);
        }

        if (learnerWeights is not null && learnerWeights.Length == K)
        {
            double sum = 0;
            for (int k = 0; k < K; k++)
            {
                sum += learnerWeights[k] * ElmLearnerProb(
                    features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                    k < learnerActivations.Length ? learnerActivations[k] : learnerActivations[0]);
            }
            return sum;
        }

        double uniSum = 0;
        for (int k = 0; k < K; k++)
        {
            uniSum += ElmLearnerProb(
                features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                k < learnerActivations.Length ? learnerActivations[k] : learnerActivations[0]);
        }
        return uniSum / K;
    }

    private static double EnsembleCalibProb(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        double[]? learnerWeights,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        double raw = EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
            featureCount, hiddenSize, featureSubsets, learnerWeights, learnerHiddenSizes, learnerActivations,
            stackingWeights, stackingBias);
        double logit = MLFeatureHelper.Logit(Math.Clamp(raw, 1e-7, 1.0 - 1e-7));
        return MLFeatureHelper.Sigmoid(plattA * logit + plattB);
    }

    private static double ApplyProductionCalibration(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale,
        double plattABuy,
        double plattBBuy,
        double plattASell,
        double plattBSell)
    {
        double rawLogit = MLFeatureHelper.Logit(Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7));
        double globalCalibP = ElmCalibrationHelper.ApplyGlobalCalibration(rawProb, plattA, plattB, temperatureScale);

        if (globalCalibP >= 0.5 && plattABuy != 0.0)
            return MLFeatureHelper.Sigmoid(plattABuy * rawLogit + plattBBuy);
        if (globalCalibP < 0.5 && plattASell != 0.0)
            return MLFeatureHelper.Sigmoid(plattASell * rawLogit + plattBSell);

        return globalCalibP;
    }

    private (double[] Accuracies, double[]? AccuracyWeights) ComputeLearnerCalibrationStats(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        ElmActivation defaultActivation)
    {
        int K = weights.Length;
        var accuracies = new double[K];
        if (calSet.Count == 0) return (accuracies, null);

        for (int k = 0; k < K; k++)
        {
            int correct = 0;
            foreach (var s in calSet)
            {
                double prob = ElmLearnerProb(
                    s.Features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                    k < learnerActivations.Length ? learnerActivations[k] : defaultActivation);
                if ((prob >= 0.5 ? 1 : 0) == s.Direction) correct++;
            }
            accuracies[k] = (double)correct / calSet.Count;
        }

        _logger.LogDebug("ELM per-learner cal accuracies: [{Accs}]",
            string.Join(", ", accuracies.Select(a => $"{a:P0}")));

        var accuracyWeights = new double[K];
        const double tempScale = 5.0;
        double maxShifted = accuracies.Max() - 0.5;
        double expSum = 0.0;
        for (int k = 0; k < K; k++)
        {
            double shifted = accuracies[k] - 0.5;
            accuracyWeights[k] = Math.Exp(tempScale * (shifted - maxShifted));
            expSum += accuracyWeights[k];
        }

        if (expSum <= 1e-15) return (accuracies, null);
        for (int k = 0; k < K; k++) accuracyWeights[k] /= expSum;
        return (accuracies, accuracyWeights);
    }

    private static double ComputeEnsembleStd(
        float[] features, double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double[]? learnerWeights = null,
        double[]? stackingWeights = null, double stackingBias = 0.0)
    {
        int K = weights.Length;
        if (K <= 1) return 0.0;

        var probs = new double[K];
        for (int k = 0; k < K; k++)
        {
            probs[k] = ElmLearnerProb(
                features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                k < learnerActivations.Length ? learnerActivations[k] : learnerActivations[0]);
        }

        double avg;
        if (stackingWeights is { Length: > 0 } sw && sw.Length == K)
        {
            double z = stackingBias;
            for (int k = 0; k < K; k++) z += sw[k] * probs[k];
            avg = MLFeatureHelper.Sigmoid(z);
        }
        else if (learnerWeights is { Length: > 0 } lw && lw.Length == K)
        {
            avg = 0.0;
            for (int k = 0; k < K; k++) avg += lw[k] * probs[k];
        }
        else
        {
            avg = probs.Average();
        }

        double variance = 0.0;
        for (int k = 0; k < K; k++)
        {
            double d = probs[k] - avg;
            variance += d * d;
        }

        return Math.Sqrt(variance / (K - 1));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Magnitude regressors
    // ═══════════════════════════════════════════════════════════════════════════

    private static double PredictMagnitudeAug(
        float[] features, double[] augWeights, double augBias,
        int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        int K = elmInputWeights.Length;
        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();
        double pred = augBias;

        for (int j = 0; j < Math.Min(featureCount, features.Length); j++)
            pred += augWeights[j] * features[j];

        for (int h = 0; h < hiddenSize; h++)
        {
            if (featureCount + h >= augWeights.Length) break;
            double hSum = 0;
            int hCount = 0;
            for (int ki = 0; ki < K; ki++)
            {
                var bIn = elmInputBiases[ki];
                if (h >= bIn.Length) continue; // learner has fewer hidden units

                var wIn = elmInputWeights[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                double z = bIn[h];
                int rowOff = h * subLen;
                z += ElmMathHelper.DotProductSimd(wIn, rowOff, features, sub, subLen);
                ElmActivation learnerAct = learnerActivations.Length > 0
                    ? learnerActivations[Math.Min(ki, learnerActivations.Length - 1)]
                    : ElmActivation.Sigmoid;
                hSum += ElmMathHelper.Activate(z, learnerAct);
                hCount++;
            }
            pred += augWeights[featureCount + h] * (hCount > 0 ? hSum / hCount : 0.0);
        }

        return pred;
    }

    private static (double[] EquivWeights, double EquivBias,
                    double[] AugWeights, double AugBias) FitElmMagnitudeRegressor(
        List<TrainingSample> train, int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        CancellationToken ct = default)
    {
        if (train.Count < 10) return (new double[featureCount], 0.0,
                                      new double[featureCount + hiddenSize], 0.0);

        int K = elmInputWeights.Length;
        int augDim = featureCount + hiddenSize;
        int valSize = Math.Max(10, train.Count / 10);
        var trainSubset = train[..^valSize];

        double[] w = new double[augDim];
        double   b = 0.0;
        double   magBaseLr = configLr > 0.0 ? configLr : 0.001;
        const double magL2Lambda = 1e-4;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double   bestLoss = double.MaxValue;
        double[] bestW = new double[augDim];
        double   bestB = 0.0;
        int      patience = 0;
        int      magMaxEpochs = configMaxEpochs > 0 ? configMaxEpochs : 200;
        int      magMaxPatience = configPatience > 0 ? configPatience : 15;

        double[] adamMW = new double[augDim], adamVW = new double[augDim];
        double   adamMB = 0, adamVB = 0;

        var augFeatures = BuildAugmentedFeatures(train, featureCount, hiddenSize, K, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);

        const int magBatchSize = 256;
        int trainSubCount = trainSubset.Count;
        bool magUseBatch = trainSubCount > magBatchSize * 2;
        var magBatchRng = magUseBatch ? new Random(trainSubCount + 19) : null;
        int[] magBatchOrder = magUseBatch ? Enumerable.Range(0, trainSubCount).ToArray() : [];
        int magGlobalStep = 0;

        double[] magGradW = new double[augDim]; // Pre-allocated to reduce GC pressure
        for (int epoch = 0; epoch < magMaxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = ElmMathHelper.CosineAnnealLr(magBaseLr, epoch, magMaxEpochs);
            if (magUseBatch) ElmMathHelper.ShuffleArray(magBatchOrder, magBatchRng!);

            int batchCount = magUseBatch ? (trainSubCount + magBatchSize - 1) / magBatchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = magUseBatch ? bi * magBatchSize : 0;
                int bEnd = magUseBatch ? Math.Min(bStart + magBatchSize, trainSubCount) : trainSubCount;
                int bLen = bEnd - bStart;

                double gradB = 0;
                Array.Clear(magGradW, 0, augDim);
                double[] gradW = magGradW;

                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int i = magUseBatch ? magBatchOrder[bIdx] : bIdx;
                    double pred = b;
                    for (int j = 0; j < augDim; j++)
                        pred += w[j] * augFeatures[i][j];
                    double err = pred - trainSubset[i].Magnitude;
                    double clipped = Math.Abs(err) > 1.35 ? 1.35 * Math.Sign(err) : err;
                    gradB += clipped;
                    for (int j = 0; j < augDim; j++)
                        gradW[j] += clipped * augFeatures[i][j];
                }

                double gB = gradB / bLen + 2.0 * magL2Lambda * b;
                magGlobalStep++;
                adamMB = beta1 * adamMB + (1 - beta1) * gB;
                adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
                b -= lr * (adamMB / (1 - Math.Pow(beta1, magGlobalStep))) / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, magGlobalStep))) + eps);

                for (int j = 0; j < augDim; j++)
                {
                    double gW = gradW[j] / bLen + 2.0 * magL2Lambda * w[j];
                    adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                    adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                    w[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, magGlobalStep))) / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, magGlobalStep))) + eps);
                }
            }

            double valLoss = 0;
            for (int i = trainSubCount; i < train.Count; i++)
            {
                double pred = b;
                for (int j = 0; j < augDim; j++)
                    pred += w[j] * augFeatures[i][j];
                double e = pred - train[i].Magnitude;
                valLoss += e * e;
            }
            valLoss /= valSize;

            if (valLoss < bestLoss - 1e-6)
            {
                bestLoss = valLoss;
                Array.Copy(w, bestW, augDim);
                bestB = b;
                patience = 0;
            }
            else if (++patience >= magMaxPatience) break;
        }

        double[] equivW = ProjectAugWeightsToFeatureSpace(bestW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, bestB, bestW, bestB);
    }

    /// <summary>
    /// Walk-forward CV for the magnitude regressor: trains on expanding windows with embargo.
    /// Stores per-fold weights for prediction averaging at inference time (avoids the lossy
    /// weight-averaging approach which can cancel useful asymmetric patterns).
    /// Returns the mean-averaged weights for backward-compatible single-model inference,
    /// plus per-fold weight arrays for prediction-averaged inference.
    /// </summary>
    private static (double[] EquivWeights, double EquivBias,
                    double[] AugWeights, double AugBias,
                    double[][]? FoldAugWeights, double[]? FoldAugBiases) FitElmMagnitudeRegressorCV(
        List<TrainingSample> train, int featureCount, int hiddenSize,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations,
        double configLr, int configMaxEpochs, int configPatience,
        int cvFolds, int embargo,
        CancellationToken ct = default)
    {
        int foldSize = train.Count / (cvFolds + 1);
        if (foldSize < 20)
        {
            var single = FitElmMagnitudeRegressor(train, featureCount, hiddenSize,
                elmInputWeights, elmInputBiases, featureSubsets, learnerActivations,
                configLr, configMaxEpochs, configPatience, ct);
            return (single.EquivWeights, single.EquivBias, single.AugWeights, single.AugBias, null, null);
        }

        int augDim = featureCount + hiddenSize;
        var allFoldWeights = new List<double[]>();
        var allFoldBiases = new List<double>();

        for (int fold = 0; fold < cvFolds; fold++)
        {
            ct.ThrowIfCancellationRequested();
            int testEnd   = (fold + 2) * foldSize;
            int trainEnd  = Math.Max(0, testEnd - foldSize - embargo);
            if (trainEnd < 20) continue;

            var foldTrain = train[..trainEnd];
            var (_, _, foldAugW, foldAugB) = FitElmMagnitudeRegressor(
                foldTrain, featureCount, hiddenSize, elmInputWeights, elmInputBiases, featureSubsets,
                learnerActivations, configLr, configMaxEpochs, configPatience, ct);

            allFoldWeights.Add(foldAugW);
            allFoldBiases.Add(foldAugB);
        }

        if (allFoldWeights.Count == 0)
        {
            var single = FitElmMagnitudeRegressor(train, featureCount, hiddenSize,
                elmInputWeights, elmInputBiases, featureSubsets, learnerActivations,
                configLr, configMaxEpochs, configPatience, ct);
            return (single.EquivWeights, single.EquivBias, single.AugWeights, single.AugBias, null, null);
        }

        // Compute mean-averaged weights as fallback / backward-compatible single model
        int validFolds = allFoldWeights.Count;
        double[] avgAugW = new double[augDim];
        double avgAugB = 0.0;
        for (int fi = 0; fi < validFolds; fi++)
        {
            for (int j = 0; j < augDim && j < allFoldWeights[fi].Length; j++)
                avgAugW[j] += allFoldWeights[fi][j];
            avgAugB += allFoldBiases[fi];
        }
        for (int j = 0; j < augDim; j++) avgAugW[j] /= validFolds;
        avgAugB /= validFolds;

        int K = elmInputWeights.Length;
        double[] equivW = ProjectAugWeightsToFeatureSpace(avgAugW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, avgAugB, avgAugW, avgAugB, allFoldWeights.ToArray(), allFoldBiases.ToArray());
    }

    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int featureCount, double tau,
        int hiddenSize, double[][] elmInputWeights, double[][] elmInputBiases,
        int[][]? featureSubsets, ElmActivation[] learnerActivations,
        CancellationToken ct = default)
    {
        if (train.Count < 10) return (new double[featureCount], 0.0);

        int K = elmInputWeights.Length;
        int augDim = featureCount + hiddenSize;
        int valSize = Math.Max(10, train.Count / 10);
        var trainSubset = train[..^valSize];
        int trainSubCount = trainSubset.Count;

        var augFeatures = BuildAugmentedFeatures(train, featureCount, hiddenSize, K, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);

        double[] w = new double[augDim];
        double   b = 0.0;
        const double qBaseLr = 0.001;
        const double qL2Lambda = 1e-4;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        const int qMaxEpochs = 200;
        const int qMaxPatience = 15;

        double bestLoss = double.MaxValue;
        double[] bestW  = new double[augDim];
        double   bestB  = 0.0;
        int patience = 0;

        double[] adamMW = new double[augDim], adamVW = new double[augDim];
        double   adamMB = 0, adamVB = 0;
        int globalStep = 0;

        const int qBatchSize = 256;
        bool useBatch = trainSubCount > qBatchSize * 2;
        var batchRng = useBatch ? new Random(trainSubCount + 31) : null;
        int[] batchOrder = useBatch ? Enumerable.Range(0, trainSubCount).ToArray() : [];

        double[] qGradW = new double[augDim]; // Pre-allocated to reduce GC pressure
        for (int epoch = 0; epoch < qMaxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = ElmMathHelper.CosineAnnealLr(qBaseLr, epoch, qMaxEpochs);
            if (useBatch) ElmMathHelper.ShuffleArray(batchOrder, batchRng!);

            int batchCount = useBatch ? (trainSubCount + qBatchSize - 1) / qBatchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = useBatch ? bi * qBatchSize : 0;
                int bEnd = useBatch ? Math.Min(bStart + qBatchSize, trainSubCount) : trainSubCount;
                int bLen = bEnd - bStart;

                double gradB = 0;
                Array.Clear(qGradW, 0, augDim);
                double[] gradW = qGradW;

                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int idx = useBatch ? batchOrder[bIdx] : bIdx;
                    double pred = b;
                    for (int j = 0; j < augDim; j++)
                        pred += w[j] * augFeatures[idx][j];
                    double err = trainSubset[idx].Magnitude - pred;
                    double sign = err >= 0 ? -tau : (1.0 - tau);
                    gradB += sign;
                    for (int j = 0; j < augDim; j++)
                        gradW[j] += sign * augFeatures[idx][j];
                }

                double gB = gradB / bLen + 2.0 * qL2Lambda * b;
                globalStep++;
                adamMB = beta1 * adamMB + (1 - beta1) * gB;
                adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
                b -= lr * (adamMB / (1 - Math.Pow(beta1, globalStep))) / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, globalStep))) + eps);

                for (int j = 0; j < augDim; j++)
                {
                    double gW = gradW[j] / bLen + 2.0 * qL2Lambda * w[j];
                    adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                    adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                    w[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, globalStep))) / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, globalStep))) + eps);
                }
            }

            double valLoss = 0;
            for (int i = trainSubCount; i < train.Count; i++)
            {
                double pred = b;
                for (int j = 0; j < augDim; j++)
                    pred += w[j] * augFeatures[i][j];
                double e = train[i].Magnitude - pred;
                valLoss += e >= 0 ? tau * e : (tau - 1.0) * e;
            }
            valLoss /= valSize;

            if (valLoss < bestLoss - 1e-6)
            {
                bestLoss = valLoss;
                Array.Copy(w, bestW, augDim);
                bestB = b;
                patience = 0;
            }
            else if (++patience >= qMaxPatience) break;
        }

        double[] equivW = ProjectAugWeightsToFeatureSpace(bestW, featureCount, hiddenSize, K, train, elmInputWeights, elmInputBiases, featureSubsets, learnerActivations);
        return (equivW, bestB);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Shared magnitude helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static double[][] BuildAugmentedFeatures(
        List<TrainingSample> samples, int featureCount, int hiddenSize, int K,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        int augDim = featureCount + hiddenSize;

        // Pre-compute default subset once — avoids per-sample per-learner allocation
        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();

        // Pre-compute per-learner effective hidden size (clamped to augDim slots)
        int[] learnerH = new int[K];
        for (int ki = 0; ki < K; ki++)
            learnerH[ki] = Math.Min(hiddenSize, elmInputBiases[ki].Length);

        var augFeatures = new double[samples.Count][];
        for (int i = 0; i < samples.Count; i++)
        {
            augFeatures[i] = new double[augDim];
            var f = samples[i].Features;
            for (int j = 0; j < Math.Min(featureCount, f.Length); j++)
                augFeatures[i][j] = f[j];

            var hSum   = new double[hiddenSize];
            var hCount = new int[hiddenSize];
            for (int ki = 0; ki < K; ki++)
            {
                var wIn = elmInputWeights[ki];
                var bIn = elmInputBiases[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                int effH = learnerH[ki];

                for (int h = 0; h < effH; h++)
                {
                    double z = bIn[h];
                    int rowOff = h * subLen;
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, f, sub, subLen);
                    ElmActivation learnerAct = learnerActivations.Length > 0
                        ? learnerActivations[Math.Min(ki, learnerActivations.Length - 1)]
                        : ElmActivation.Sigmoid;
                    hSum[h] += ElmMathHelper.Activate(z, learnerAct);
                    hCount[h]++;
                }
            }
            for (int h = 0; h < hiddenSize; h++)
                augFeatures[i][featureCount + h] = hCount[h] > 0 ? hSum[h] / hCount[h] : 0.0;
        }
        return augFeatures;
    }

    private static double[] ProjectAugWeightsToFeatureSpace(
        double[] augWeights, int featureCount, int hiddenSize, int K,
        List<TrainingSample> train,
        double[][] elmInputWeights, double[][] elmInputBiases, int[][]? featureSubsets,
        ElmActivation[] learnerActivations)
    {
        double[] equivW = new double[featureCount];
        Array.Copy(augWeights, equivW, featureCount);

        int[] defaultSubset = Enumerable.Range(0, featureCount).ToArray();

        double[] meanActivationDeriv = new double[hiddenSize];
        int[] derivContributors = new int[hiddenSize];
        for (int ki = 0; ki < K; ki++)
        {
            var wIn = elmInputWeights[ki];
            var bIn = elmInputBiases[ki];
            int effH = Math.Min(hiddenSize, bIn.Length);
            int[] sub = featureSubsets is not null && ki < featureSubsets.Length
                ? featureSubsets[ki]
                : defaultSubset;
            int subLen = sub.Length;

            for (int h = 0; h < effH; h++)
            {
                double derivSum = 0;
                int rowOff = h * subLen;
                for (int i = 0; i < train.Count; i++)
                {
                    double z = bIn[h];
                    var f = train[i].Features;
                    z += ElmMathHelper.DotProductSimd(wIn, rowOff, f, sub, subLen);
                    ElmActivation learnerAct = learnerActivations.Length > 0
                        ? learnerActivations[Math.Min(ki, learnerActivations.Length - 1)]
                        : ElmActivation.Sigmoid;
                    derivSum += ActivationDerivative(z, learnerAct);
                }
                meanActivationDeriv[h] += derivSum / train.Count;
                derivContributors[h]++;
            }
        }
        for (int h = 0; h < hiddenSize; h++)
            if (derivContributors[h] > 0) meanActivationDeriv[h] /= derivContributors[h];

        for (int h = 0; h < hiddenSize; h++)
        {
            double hiddenW = augWeights[featureCount + h];
            if (Math.Abs(hiddenW) < 1e-10) continue;

            double deriv = meanActivationDeriv[h];
            int contributors = derivContributors[h];
            if (contributors == 0) continue;

            for (int ki = 0; ki < K; ki++)
            {
                var bIn = elmInputBiases[ki];
                if (h >= bIn.Length) continue; // learner has fewer hidden units

                var wIn = elmInputWeights[ki];
                int[] sub = featureSubsets is not null && ki < featureSubsets.Length
                    ? featureSubsets[ki]
                    : defaultSubset;
                int subLen = sub.Length;
                int rowOff = h * subLen;

                for (int si = 0; si < subLen; si++)
                {
                    int fi = sub[si];
                    if (fi < featureCount && rowOff + si < wIn.Length)
                        equivW[fi] += hiddenW * deriv * wIn[rowOff + si] / contributors;
                }
            }
        }

        return equivW;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Activation derivative (for Taylor projection in magnitude regressor)
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ActivationDerivative(double z, ElmActivation activation)
    {
        switch (activation)
        {
            case ElmActivation.Tanh:
                var t = Math.Tanh(z);
                return 1.0 - t * t;
            case ElmActivation.Relu:
                return z > 0.0 ? 1.0 : 0.0;
            default: // Sigmoid
                var s = MLFeatureHelper.Sigmoid(z);
                return s * (1.0 - s);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Meta-label & abstention (kept inline — they use ensemble inference)
    // ═══════════════════════════════════════════════════════════════════════════

    private (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double decisionThreshold,
        Func<float[], double>? calibratedProb = null,
        double[]? stackingWeights = null, double stackingBias = 0.0,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        CancellationToken ct = default)
    {
        if (calSet.Count < 10) return ([], 0.0);

        int metaDim = 2 + Math.Min(5, featureCount);
        double[] mw = new double[metaDim];
        double[] bestMw = new double[metaDim];
        double   mb = 0.0, bestMb = 0.0;
        double metaBaseLr = configLr > 0.0 ? configLr : 0.01;
        const double l2Lambda = 1e-4;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double   bestLoss = double.MaxValue;
        int      patience = 0;
        int maxPatience = configPatience > 0 ? configPatience : 25;

        double[] adamMW = new double[metaDim], adamVW = new double[metaDim];
        double   adamMB = 0, adamVB = 0;

        var metaXs = new double[calSet.Count][];
        var targets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s = calSet[i];
            double calibP = calibratedProb is not null
                ? calibratedProb(s.Features)
                : EnsembleCalibProb(
                    s.Features, weights, biases, inputWeights, inputBiases,
                    1.0, 0.0, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);

            double ensStd = ComputeEnsembleStd(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, featureSubsets, learnerHiddenSizes, learnerActivations,
                stackingWeights: stackingWeights, stackingBias: stackingBias);

            metaXs[i] = new double[metaDim];
            metaXs[i][0] = calibP;
            metaXs[i][1] = ensStd;
            for (int j = 0; j < metaDim - 2; j++)
                if (j < s.Features.Length) metaXs[i][j + 2] = s.Features[j];

            targets[i] = (calibP >= decisionThreshold ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        }

        int metaMaxEpochs = configMaxEpochs > 0 ? configMaxEpochs : 200;
        int metaTrainCount = Math.Max(1, (int)(calSet.Count * 0.8));
        int metaValStart = metaTrainCount;
        int metaValCount = calSet.Count - metaTrainCount;
        const int metaBatchSize = 256;
        bool metaUseBatch = metaTrainCount > metaBatchSize * 2;
        var metaBatchRng = metaUseBatch ? new Random(calSet.Count + 7) : null;
        int[] metaBatchOrder = metaUseBatch ? Enumerable.Range(0, metaTrainCount).ToArray() : [];
        int metaGlobalStep = 0;

        double[] metaGradW = new double[metaDim]; // Pre-allocated to reduce GC pressure
        for (int epoch = 0; epoch < metaMaxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = ElmMathHelper.CosineAnnealLr(metaBaseLr, epoch, metaMaxEpochs);
            if (metaUseBatch) ElmMathHelper.ShuffleArray(metaBatchOrder, metaBatchRng!);

            int batchCount = metaUseBatch ? (metaTrainCount + metaBatchSize - 1) / metaBatchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = metaUseBatch ? bi * metaBatchSize : 0;
                int bEnd = metaUseBatch ? Math.Min(bStart + metaBatchSize, metaTrainCount) : metaTrainCount;
                int bLen = bEnd - bStart;

                double gradB = 0;
                Array.Clear(metaGradW, 0, metaDim);
                double[] gradW = metaGradW;

                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int i = metaUseBatch ? metaBatchOrder[bIdx] : bIdx;
                    double z = mb;
                    for (int j = 0; j < metaDim; j++) z += mw[j] * metaXs[i][j];
                    double p = MLFeatureHelper.Sigmoid(z);

                    double err = p - targets[i];
                    gradB += err;
                    for (int j = 0; j < metaDim; j++) gradW[j] += err * metaXs[i][j];
                }

                double gB = gradB / bLen + 2.0 * l2Lambda * mb;
                metaGlobalStep++;
                adamMB = beta1 * adamMB + (1 - beta1) * gB;
                adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
                mb -= lr * (adamMB / (1 - Math.Pow(beta1, metaGlobalStep))) / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, metaGlobalStep))) + eps);

                for (int j = 0; j < metaDim; j++)
                {
                    double gW = gradW[j] / bLen + 2.0 * l2Lambda * mw[j];
                    adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                    adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                    mw[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, metaGlobalStep))) / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, metaGlobalStep))) + eps);
                }
            }

            double loss = 0;
            int evalCount = metaValCount > 0 ? metaValCount : calSet.Count;
            int evalStart = metaValCount > 0 ? metaValStart : 0;
            for (int i = evalStart; i < evalStart + evalCount; i++)
            {
                double z = mb;
                for (int j = 0; j < metaDim; j++) z += mw[j] * metaXs[i][j];
                double p = MLFeatureHelper.Sigmoid(z);
                loss -= targets[i] * Math.Log(Math.Max(p, 1e-10)) + (1 - targets[i]) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            loss /= evalCount;
            double l2Penalty = mb * mb;
            for (int j = 0; j < metaDim; j++) l2Penalty += mw[j] * mw[j];
            loss += l2Lambda * l2Penalty;

            if (loss < bestLoss - 1e-7)
            {
                bestLoss = loss; bestMb = mb;
                Array.Copy(mw, bestMw, metaDim);
                patience = 0;
            }
            else if (++patience >= maxPatience) break;
        }

        return (bestMw, bestMb);
    }

    private (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        double plattA, double plattB,
        double[] metaLabelWeights, double metaLabelBias,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        double decisionThreshold,
        Func<float[], double>? calibratedProb = null,
        double[]? stackingWeights = null, double stackingBias = 0.0,
        double configLr = 0.0, int configMaxEpochs = 0, int configPatience = 0,
        CancellationToken ct = default)
    {
        if (calSet.Count < 10) return ([], 0.0, 0.5);

        int dim = 3;
        double[] aw = new double[dim];
        double[] bestAw = new double[dim];
        double   ab = 0.0, bestAb = 0.0;
        double absBaseLr = configLr > 0.0 ? configLr : 0.01;
        const double l2Lambda = 1e-4;
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        double   bestLoss = double.MaxValue;
        int      patience = 0;
        int maxPatience = configPatience > 0 ? configPatience : 25;

        double[] adamMW = new double[dim], adamVW = new double[dim];
        double   adamMB = 0, adamVB = 0;

        var absXs = new double[calSet.Count][];
        var absTargets = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var s = calSet[i];
            double calibP = calibratedProb is not null
                ? calibratedProb(s.Features)
                : EnsembleCalibProb(
                    s.Features, weights, biases, inputWeights, inputBiases,
                    plattA, plattB, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);

            double ensStd = ComputeEnsembleStd(
                s.Features, weights, biases, inputWeights, inputBiases,
                featureCount, featureSubsets, learnerHiddenSizes, learnerActivations,
                stackingWeights: stackingWeights, stackingBias: stackingBias);

            double mlScore = metaLabelBias;
            double[] mlX = [calibP, ensStd, ..Enumerable.Range(0, Math.Min(5, featureCount)).Select(j => (double)s.Features[j])];
            for (int j = 0; j < Math.Min(metaLabelWeights.Length, mlX.Length); j++)
                mlScore += metaLabelWeights[j] * mlX[j];
            mlScore = MLFeatureHelper.Sigmoid(mlScore);

            absXs[i] = [calibP, ensStd, mlScore];
            absTargets[i] = (calibP >= decisionThreshold ? 1 : 0) == s.Direction ? 1.0 : 0.0;
        }

        int absMaxEpochs = configMaxEpochs > 0 ? configMaxEpochs : 200;
        int absTrainCount = Math.Max(1, (int)(calSet.Count * 0.8));
        int absValStart = absTrainCount;
        int absValCount = calSet.Count - absTrainCount;
        const int absBatchSize = 256;
        bool absUseBatch = absTrainCount > absBatchSize * 2;
        var absBatchRng = absUseBatch ? new Random(calSet.Count + 13) : null;
        int[] absBatchOrder = absUseBatch ? Enumerable.Range(0, absTrainCount).ToArray() : [];
        int absGlobalStep = 0;

        double[] absGradW = new double[dim]; // Pre-allocated to reduce GC pressure
        for (int epoch = 0; epoch < absMaxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            double lr = ElmMathHelper.CosineAnnealLr(absBaseLr, epoch, absMaxEpochs);
            if (absUseBatch) ElmMathHelper.ShuffleArray(absBatchOrder, absBatchRng!);

            int batchCount = absUseBatch ? (absTrainCount + absBatchSize - 1) / absBatchSize : 1;
            for (int bi = 0; bi < batchCount; bi++)
            {
                int bStart = absUseBatch ? bi * absBatchSize : 0;
                int bEnd = absUseBatch ? Math.Min(bStart + absBatchSize, absTrainCount) : absTrainCount;
                int bLen = bEnd - bStart;

                double gradB = 0;
                Array.Clear(absGradW, 0, dim);
                double[] gradW = absGradW;

                for (int bIdx = bStart; bIdx < bEnd; bIdx++)
                {
                    int i = absUseBatch ? absBatchOrder[bIdx] : bIdx;
                    double z = ab;
                    for (int j = 0; j < dim; j++) z += aw[j] * absXs[i][j];
                    double p = MLFeatureHelper.Sigmoid(z);

                    double err = p - absTargets[i];
                    gradB += err;
                    for (int j = 0; j < dim; j++) gradW[j] += err * absXs[i][j];
                }

                double gB = gradB / bLen + 2.0 * l2Lambda * ab;
                absGlobalStep++;
                adamMB = beta1 * adamMB + (1 - beta1) * gB;
                adamVB = beta2 * adamVB + (1 - beta2) * gB * gB;
                ab -= lr * (adamMB / (1 - Math.Pow(beta1, absGlobalStep))) / (Math.Sqrt(adamVB / (1 - Math.Pow(beta2, absGlobalStep))) + eps);

                for (int j = 0; j < dim; j++)
                {
                    double gW = gradW[j] / bLen + 2.0 * l2Lambda * aw[j];
                    adamMW[j] = beta1 * adamMW[j] + (1 - beta1) * gW;
                    adamVW[j] = beta2 * adamVW[j] + (1 - beta2) * gW * gW;
                    aw[j] -= lr * (adamMW[j] / (1 - Math.Pow(beta1, absGlobalStep))) / (Math.Sqrt(adamVW[j] / (1 - Math.Pow(beta2, absGlobalStep))) + eps);
                }
            }

            double loss = 0;
            int absEvalCount = absValCount > 0 ? absValCount : calSet.Count;
            int absEvalStart = absValCount > 0 ? absValStart : 0;
            for (int i = absEvalStart; i < absEvalStart + absEvalCount; i++)
            {
                double z = ab;
                for (int j = 0; j < dim; j++) z += aw[j] * absXs[i][j];
                double p = MLFeatureHelper.Sigmoid(z);
                loss -= absTargets[i] * Math.Log(Math.Max(p, 1e-10)) + (1 - absTargets[i]) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            loss /= absEvalCount;
            double l2Penalty = ab * ab;
            for (int j = 0; j < dim; j++) l2Penalty += aw[j] * aw[j];
            loss += l2Lambda * l2Penalty;

            if (loss < bestLoss - 1e-7)
            {
                bestLoss = loss; bestAb = ab;
                Array.Copy(aw, bestAw, dim);
                patience = 0;
            }
            else if (++patience >= maxPatience) break;
        }

        aw = bestAw;
        ab = bestAb;

        double bestThr = 0.5;
        double bestAcc = 0;
        for (int pct = 0; pct <= 30; pct++)
        {
            double margin = pct / 100.0;
            int c = 0, t = 0;
            foreach (var s in calSet)
            {
                double calibP = calibratedProb is not null
                    ? calibratedProb(s.Features)
                    : EnsembleCalibProb(
                        s.Features, weights, biases, inputWeights, inputBiases,
                        plattA, plattB, featureCount, hiddenSize, featureSubsets, null, learnerHiddenSizes, learnerActivations, stackingWeights, stackingBias);
                if (Math.Abs(calibP - decisionThreshold) < margin) continue;
                t++;
                if ((calibP >= decisionThreshold ? 1 : 0) == s.Direction) c++;
            }
            if (t < 5) continue;
            double acc = (double)c / t;
            if (acc > bestAcc) { bestAcc = acc; bestThr = 0.5 + margin; }
        }

        return (aw, ab, bestThr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Stacking meta-learner
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Trains a logistic meta-learner that maps per-base-learner probabilities [p_0,..,p_{K-1}]
    /// to a final probability via σ(Σ w_k·p_k + b). Fitted on the calibration set which base
    /// learners never saw. When the meta-learner is active, it replaces simple/weighted averaging,
    /// learning optimal per-learner combination weights.
    /// </summary>
    private (double[] Weights, double Bias) FitStackingMetaLearner(
        List<TrainingSample> calSet,
        double[][] weights, double[] biases,
        double[][] inputWeights, double[][] inputBiases,
        int featureCount, int hiddenSize, int[][]? featureSubsets,
        int[] learnerHiddenSizes, ElmActivation[] learnerActivations,
        CancellationToken ct = default)
    {
        int K = weights.Length;
        if (calSet.Count < 20 || K < 2) return ([], 0.0);

        int n = calSet.Count;
        var calLp = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLp[i] = new double[K];
            for (int k = 0; k < K; k++)
            {
                calLp[i][k] = ElmLearnerProb(
                    calSet[i].Features, weights[k], biases[k], inputWeights[k], inputBiases[k],
                    featureCount, learnerHiddenSizes[k], featureSubsets?[k],
                    k < learnerActivations.Length ? learnerActivations[k] : learnerActivations[0]);
            }
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        // 80/20 train/val split for early stopping (consistent with FitMetaLabelModel)
        int trainCount = Math.Max(1, (int)(n * 0.8));
        int valStart   = trainCount;
        int valCount   = n - trainCount;

        var mw = new double[K];
        for (int k = 0; k < K; k++) mw[k] = 1.0 / K; // uniform init
        double mb = 0.0;

        var bestMw = new double[K];
        Array.Copy(mw, bestMw, K);
        double bestMb   = 0.0;
        double bestLoss = double.MaxValue;
        int    patience = 0;

        const double lr = 0.01;
        const double l2Lambda = 1e-4;
        const int maxEpochs = 300;
        const int maxPatience = 30;

        var dW = new double[K]; // pre-allocated
        for (int epoch = 0; epoch < maxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(dW, 0, K);
            double dB = 0;

            for (int i = 0; i < trainCount; i++)
            {
                var lp = calLp[i];
                double z = mb;
                for (int k = 0; k < K; k++) z += mw[k] * lp[k];
                double p = MLFeatureHelper.Sigmoid(z);
                double err = p - calLabels[i];
                for (int k = 0; k < K; k++) dW[k] += err * lp[k];
                dB += err;
            }

            for (int k = 0; k < K; k++) mw[k] -= lr * (dW[k] / trainCount + 2.0 * l2Lambda * mw[k]);
            mb -= lr * (dB / trainCount + 2.0 * l2Lambda * mb);

            // Validation loss for early stopping
            int evalCount = valCount > 0 ? valCount : n;
            int evalStart = valCount > 0 ? valStart : 0;
            double loss = 0;
            for (int i = evalStart; i < evalStart + evalCount; i++)
            {
                var lp = calLp[i];
                double z = mb;
                for (int k = 0; k < K; k++) z += mw[k] * lp[k];
                double p = MLFeatureHelper.Sigmoid(z);
                loss -= calLabels[i] * Math.Log(Math.Max(p, 1e-10))
                      + (1 - calLabels[i]) * Math.Log(Math.Max(1 - p, 1e-10));
            }
            loss /= evalCount;

            if (loss < bestLoss - 1e-7)
            {
                bestLoss = loss;
                bestMb = mb;
                Array.Copy(mw, bestMw, K);
                patience = 0;
            }
            else if (++patience >= maxPatience) break;
        }

        _logger.LogDebug(
            "ELM stacking meta-learner: bias={B:F4} weights=[{W}]",
            bestMb, string.Join(",", bestMw.Select(w => w.ToString("F3"))));

        return (bestMw, bestMb);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Warm-start pruning remap
    // ═══════════════════════════════════════════════════════════════════════════

    private static ModelSnapshot? RemapWarmStartForPruning(
        ModelSnapshot? warmStart, bool[] activeMask, int featureCount, int hiddenSize)
    {
        if (warmStart?.ElmInputWeights is null) return null;

        int activeFeatureCount = activeMask.Count(m => m);
        int[] activeOriginalIndices = Enumerable.Range(0, featureCount)
            .Where(i => i < activeMask.Length && activeMask[i])
            .ToArray();

        int wsK = warmStart.ElmInputWeights.Length;
        var remappedInputWeights = new double[wsK][];
        var remappedInputBiases  = new double[wsK][];
        int[][]? remappedSubsets = warmStart.FeatureSubsetIndices is not null
            ? new int[wsK][] : null;

        for (int ki = 0; ki < wsK; ki++)
        {
            var oldWIn = warmStart.ElmInputWeights[ki];
            var oldBIn = warmStart.ElmInputBiases is not null && ki < warmStart.ElmInputBiases.Length
                ? warmStart.ElmInputBiases[ki] : new double[hiddenSize];

            int[] oldSub = warmStart.FeatureSubsetIndices is not null && ki < warmStart.FeatureSubsetIndices.Length
                ? warmStart.FeatureSubsetIndices[ki]
                : Enumerable.Range(0, featureCount).ToArray();
            int oldSubLen = oldSub.Length;

            var newSubList = new List<int>();
            var oldSubPositions = new List<int>();
            for (int si = 0; si < oldSubLen; si++)
            {
                int oldFi = oldSub[si];
                if (oldFi < featureCount && oldFi < activeMask.Length && activeMask[oldFi])
                {
                    newSubList.Add(oldFi);
                    oldSubPositions.Add(si);
                }
            }

            int newSubLen = newSubList.Count;
            if (newSubLen == 0)
            {
                remappedInputWeights[ki] = new double[hiddenSize * activeFeatureCount];
                remappedInputBiases[ki]  = (double[])oldBIn.Clone();
                if (remappedSubsets is not null)
                    remappedSubsets[ki] = activeOriginalIndices;
                continue;
            }

            var newWIn = new double[hiddenSize * newSubLen];
            for (int h = 0; h < hiddenSize; h++)
            {
                int oldRowOff = h * oldSubLen;
                int newRowOff = h * newSubLen;
                for (int nsi = 0; nsi < newSubLen; nsi++)
                {
                    int oldPos = oldSubPositions[nsi];
                    if (oldRowOff + oldPos < oldWIn.Length)
                        newWIn[newRowOff + nsi] = oldWIn[oldRowOff + oldPos];
                }
            }

            remappedInputWeights[ki] = newWIn;
            remappedInputBiases[ki]  = (double[])oldBIn.Clone();
            if (remappedSubsets is not null)
                remappedSubsets[ki] = newSubList.ToArray();
        }

        return new ModelSnapshot
        {
            ElmInputWeights      = remappedInputWeights,
            ElmInputBiases       = remappedInputBiases,
            FeatureSubsetIndices  = remappedSubsets,
            FeatureImportanceScores = warmStart.FeatureImportanceScores,
            GenerationNumber       = warmStart.GenerationNumber,
        };
    }

    // ── Online incremental update (Sherman-Morrison) ────────────────────────

    /// <summary>
    /// Incrementally updates an already-trained ELM model with a single new
    /// training sample using the Sherman-Morrison rank-1 formula.
    /// Cost: O(H²) per sample per learner — sub-millisecond for typical H=64.
    /// <para>
    /// The inverse Gram matrix is stored on <see cref="ModelSnapshot.ElmInverseGram"/>
    /// and updated in-place. If not available (older model), the update is skipped.
    /// </para>
    /// </summary>
    /// <param name="snapshot">
    /// The current model snapshot. <c>ElmInverseGram</c>, <c>Weights</c>, <c>Biases</c>
    /// are updated in-place.
    /// </param>
    /// <param name="sample">The new labelled training sample.</param>
    /// <returns><c>true</c> if the update was applied; <c>false</c> if skipped.</returns>
    public bool UpdateOnline(ModelSnapshot snapshot, TrainingSample sample)
    {
        if (snapshot.ElmInverseGram is null || snapshot.ElmInverseGram.Length == 0)
        {
            _logger.LogDebug("ELM online update skipped — no inverse Gram matrix in snapshot");
            return false;
        }

        if (snapshot.FracDiffD > 0.0)
        {
            _logger.LogDebug(
                "ELM online update skipped — FracDiffD={D:F2} requires historical context not available to single-sample updates",
                snapshot.FracDiffD);
            return false;
        }

        if (snapshot.Weights is null || snapshot.ElmInputWeights is null)
            return false;

        int K = snapshot.Weights.Length;
        double target = sample.Direction > 0 ? 1.0 : 0.0;

        // Standardise using the snapshot's stored means/stds
        var stdFeatures = snapshot.Means is not null && snapshot.Stds is not null
            ? MLFeatureHelper.Standardize(sample.Features, snapshot.Means, snapshot.Stds)
            : sample.Features;

        int updatedLearners = 0;
        for (int k = 0; k < K; k++)
        {
            if (k >= snapshot.ElmInverseGram.Length || snapshot.ElmInverseGram[k] is null)
                continue;
            if (snapshot.ElmInputBiases is null || k >= snapshot.ElmInputBiases.Length)
                continue;

            var inputW = snapshot.ElmInputWeights[k];
            var inputB = snapshot.ElmInputBiases[k];
            int H      = snapshot.Weights[k].Length;
            if (snapshot.ElmInverseGram[k].GetLength(0) != H || snapshot.ElmInverseGram[k].GetLength(1) != H)
                continue;

            // Resolve feature subset for this learner
            float[] features = stdFeatures;
            if (snapshot.FeatureSubsetIndices is not null && k < snapshot.FeatureSubsetIndices.Length)
            {
                var subset = snapshot.FeatureSubsetIndices[k];
                features = new float[subset.Length];
                for (int i = 0; i < subset.Length; i++)
                    features[i] = stdFeatures[subset[i]];
            }

            // Compute hidden activation: h = activation(W_in × features + b_in)
            int inputDim = features.Length;
            var hidden   = new double[H];
            ElmActivation learnerAct = snapshot.LearnerActivations is not null && k < snapshot.LearnerActivations.Length
                ? (ElmActivation)snapshot.LearnerActivations[k]
                : ElmActivation.Sigmoid;
            for (int h = 0; h < H; h++)
            {
                double z = h < inputB.Length ? inputB[h] : 0.0;
                int rowOff = h * inputDim;
                for (int f = 0; f < inputDim && rowOff + f < inputW.Length; f++)
                    z += inputW[rowOff + f] * features[f];
                hidden[h] = ElmMathHelper.Activate(z, learnerAct);
            }

            // Sherman-Morrison rank-1 update
            double bias = snapshot.Biases![k];
            ElmMathHelper.ShermanMorrisonUpdate(
                snapshot.ElmInverseGram[k],
                snapshot.Weights[k],
                ref bias,
                hidden,
                target,
                snapshot.TrainSamples);
            snapshot.Biases[k] = bias;
            updatedLearners++;
        }

        if (updatedLearners == 0)
        {
            _logger.LogDebug("ELM online update skipped — no learner had a usable inverse Gram matrix");
            return false;
        }

        snapshot.TrainSamples++;

        _logger.LogDebug("ELM online update applied: {K} learners updated with 1 sample", updatedLearners);
        return true;
    }
}
