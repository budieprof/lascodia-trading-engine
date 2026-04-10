using System.Diagnostics;
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
public sealed partial class ElmModelTrainer : IMLModelTrainer
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

    private static int ToBinaryLabel(int direction) => direction > 0 ? 1 : 0;

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

        var pipelineStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();

        int featureCount = ValidateTrainingSamples(samples);
        int hiddenSize   = hp.ElmHiddenSize is > 0 ? hp.ElmHiddenSize.Value : DefaultHiddenSize;
        int K            = Math.Max(1, hp.K);
        string[] snapshotFeatureNames = BuildSnapshotFeatureNames(featureCount);
        string featureSchemaFingerprint = ElmSnapshotSupport.ComputeFeatureSchemaFingerprint(snapshotFeatureNames, featureCount);
        string basePreprocessingFingerprint = ElmSnapshotSupport.ComputePreprocessingFingerprint(
            featureCount,
            hp.FracDiffD,
            hp.ElmWinsorizePercentile > 0.0);
        string trainerFingerprint = ElmSnapshotSupport.ComputeTrainerFingerprint(hp, featureCount, hiddenSize, K);

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
        int purgeExtra = MLFeatureHelper.LookbackWindow - 1;

        // ── 4-way split: 60% train | 10% selection | 10% cal | ~20% test ──
        int trainEnd     = (int)(n * 0.60);
        int selectionEnd = (int)(n * 0.70);
        int calEnd       = (int)(n * 0.80);

        int trainSetEnd     = Math.Clamp(trainEnd - purgeExtra, 0, n);
        int selectionStart  = Math.Min(trainEnd + embargo, selectionEnd);
        int selectionSetEnd = Math.Clamp(selectionEnd, selectionStart, n);
        int calStart        = Math.Min(selectionEnd + embargo, calEnd);
        int calSetEnd       = Math.Clamp(calEnd, calStart, n);

        if (trainSetEnd < 2)
            throw new InvalidOperationException(
                $"ElmModelTrainer: training window is too small after split selection ({trainSetEnd} samples). " +
                $"Reduce EmbargoBarCount or provide more data.");

        var warmStartArtifact = BuildElmWarmStartArtifact(false, false, 0, 0, false, false, []);

        if (warmStart is not null)
        {
            if (!string.Equals(warmStart.Type, ModelType, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "ELM warm-start ignored: snapshot type {Type} is not compatible with {ExpectedType}",
                    warmStart.Type, ModelType);
                warmStart = null;
            }
            else
            {
                warmStart = ElmSnapshotSupport.NormalizeSnapshotCopy(warmStart);
                var compatibility = ElmSnapshotSupport.AssessWarmStartCompatibility(
                    warmStart,
                    featureSchemaFingerprint,
                    basePreprocessingFingerprint,
                    trainerFingerprint,
                    featureCount,
                    hiddenSize);
                if (!compatibility.IsCompatible)
                {
                    _logger.LogWarning(
                        "ELM warm-start ignored due to compatibility issues: {Issues}",
                        string.Join("; ", compatibility.Issues));
                    warmStart = null;
                }
            }
        }

        warmStartArtifact = BuildElmWarmStartArtifact(
            attempted: true,
            compatible: warmStart is not null,
            reusedLearnerCount: warmStart?.BaseLearnersK ?? 0,
            totalParentLearners: warmStart?.BaseLearnersK ?? 0,
            inputWeightsTransferred: warmStart is not null,
            pruningRemapped: false,
            compatibilityIssues: []);

        // ── 1b. Preprocess features without mutating the caller's samples ─────
        var preparedData = ElmFeaturePipelineHelper.PrepareTrainingSamples(
            samples,
            featureCount,
            trainSetEnd,
            hp.ElmWinsorizePercentile,
            hp.FracDiffD);
        var allStd = preparedData.Samples;
        var means = preparedData.Means;
        var stds = preparedData.Stds;
        var elmWinsorizeLowerBounds = preparedData.WinsorizeLowerBounds;
        var elmWinsorizeUpperBounds = preparedData.WinsorizeUpperBounds;

        if (elmWinsorizeLowerBounds.Length > 0)
            _logger.LogInformation(
                "ELM winsorized features at p={Pctile:F3} (quantiles from training split)",
                hp.ElmWinsorizePercentile);
        if (hp.FracDiffD > 0.0)
            _logger.LogInformation("ELM fractional differencing applied: d={D:F2}", hp.FracDiffD);

        double sharpeAnnFactor = hp.SharpeAnnualisationFactor > 0.0
            ? hp.SharpeAnnualisationFactor : DefaultSharpeAnnualisationFactor;

        _logger.LogInformation(
            "ElmModelTrainer starting: N={N} F={F} Hidden={H} K={K} Activation={Act} Dropout={Drop:P0}",
            samples.Count, featureCount, hiddenSize, K, hp.ElmActivation, hp.ElmDropoutRate);

        // ── 2. Walk-forward cross-validation ────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(samples, hp, featureCount, hiddenSize, ct, sharpeAnnFactor);
        _logger.LogInformation(
            "ELM walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);
        _logger.LogDebug("ELM stage timing: walk-forward CV = {Ms}ms", stageStopwatch.ElapsedMilliseconds);
        stageStopwatch.Restart();

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: adaptive train | cal | test ──────────────
        // Split indices (n, embargo, trainEnd, calEnd, trainSetEnd) were computed
        // above in step 1 before standardisation to avoid data leakage.
        var trainSet     = allStd[..trainSetEnd];
        var selectionSet = allStd[selectionStart..selectionSetEnd];
        var calSet       = allStd[calStart..calSetEnd];
        int testStart    = calSet.Count > 0 ? Math.Min(calSetEnd + embargo, n) : calStart;
        var testSet      = allStd[testStart..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"ElmModelTrainer: insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        const int minCalTestSize = 10;
        if (calSet.Count < minCalTestSize)
            _logger.LogWarning(
                "ELM calibration set too small ({CalCount} < {Min}). Platt scaling and threshold estimates may be unreliable.",
                calSet.Count, minCalTestSize);
        if (selectionSet.Count < minCalTestSize)
            _logger.LogWarning(
                "ELM selection set too small ({SelCount} < {Min}). Threshold tuning and pruning acceptance may be unreliable.",
                selectionSet.Count, minCalTestSize);
        if (testSet.Count < minCalTestSize)
            _logger.LogWarning(
                "ELM test set too small ({TestCount} < {Min}). Final evaluation metrics may be unreliable.",
                testSet.Count, minCalTestSize);

        // ── 3b. Multi-signal stationarity gate ──────────────────────────────
        var driftArtifact = ComputeElmDriftDiagnostics(trainSet, featureCount, snapshotFeatureNames, hp.FracDiffD);
        if (driftArtifact.GateTriggered)
        {
            if (string.Equals(driftArtifact.GateAction, "REJECT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"ELM drift gate rejected training: {driftArtifact.NonStationaryFeatureCount}/{featureCount} features flagged.");
            _logger.LogWarning(
                "ELM stationarity gate ({Action}): {NonStat}/{Total} features flagged.",
                driftArtifact.GateAction, driftArtifact.NonStationaryFeatureCount, featureCount);
        }

        // ── 3b2. Class-imbalance gate ──────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException(
                    $"ELM: extreme class imbalance (Buy={buyRatio:P1}). Training would produce a degenerate model.");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("ELM class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        // ── 3b3. Adversarial validation ────────────────────────────────────
        if (testSet.Count >= 20 && trainSet.Count >= 20)
        {
            double advAuc = TryComputeAdversarialAucGpu(trainSet, testSet, featureCount, ct)
                            ?? ComputeAdversarialAuc(trainSet, testSet, featureCount);
            _logger.LogInformation("ELM adversarial AUC={AUC:F3}", advAuc);
            if (advAuc > 0.65)
                _logger.LogWarning("ELM adversarial AUC={AUC:F3} indicates meaningful covariate shift.", advAuc);
            if (hp.ElmMaxAdversarialAuc > 0.0 && advAuc > hp.ElmMaxAdversarialAuc)
                throw new InvalidOperationException(
                    $"ELM: adversarial AUC={advAuc:F3} exceeds rejection threshold {hp.ElmMaxAdversarialAuc:F3}.");
        }

        // ── 3c. Density-ratio importance weights ──────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50)
        {
            densityWeights = TryComputeDensityRatioWeightsGpu(trainSet, featureCount, hp.DensityRatioWindowDays, hp.BarsPerDay, ct)
                             ?? ElmBootstrapHelper.ComputeDensityRatioWeights(
                                 trainSet, featureCount, hp.DensityRatioWindowDays, hp.BarsPerDay);
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
                    densityWeights[i] = Math.Clamp(densityWeights[i] * csWeights[i], 0.05, 20.0);
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("ELM covariate shift weights applied from parent model (clipped to [0.05, 20.0]).");
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
         double[][] inverseGramsFlat, int[] inverseGramDims) ensembleResult;
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
        var (weights, biases, inputWeights, inputBiases, featureSubsets, learnerHiddenSizes, learnerActivations, inverseGramsFlat, inverseGramDims) = ensembleResult;

        // ── 4b. Post-training NaN/Inf weight sanitisation ──────────────────
        int sanitizedCount = SanitizeLearnerOutputs(weights, biases, "ELM");
        _logger.LogDebug("ELM stage timing: ensemble fitting + sanitisation = {Ms}ms", stageStopwatch.ElapsedMilliseconds);
        stageStopwatch.Restart();

        ct.ThrowIfCancellationRequested();

        // ── 4c. Accuracy-weighted ensemble averaging ─────────────────────────
        var (learnerCalAccuracies, learnerAccWeights) = ComputeLearnerCalibrationStats(
            calSet, weights, biases, inputWeights, inputBiases,
            featureCount, featureSubsets, learnerHiddenSizes, learnerActivations);

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

        bool magQuad = hp.ElmMagQuadraticTerms;
        if (hp.ElmMagRegressorCvFolds > 1 && trainSet.Count >= hp.MinSamples * 2)
        {
            var magCvResult = FitElmMagnitudeRegressorCV(
                trainSet, featureCount, hiddenSize, inputWeights, inputBiases, featureSubsets, learnerActivations,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience,
                hp.ElmMagRegressorCvFolds, embargo, ct, magQuad);
            magWeights = magCvResult.EquivWeights;
            magBias = magCvResult.EquivBias;
            magAugWeights = magCvResult.AugWeights;
            magAugBias = magCvResult.AugBias;
            magAugWeightsFolds = magCvResult.FoldAugWeights;
            magAugBiasFolds = magCvResult.FoldAugBiases;
            _logger.LogInformation(
                "ELM magnitude regressor fitted with {Folds}-fold walk-forward CV (prediction-averaged, {FoldCount} valid folds{Quad}).",
                hp.ElmMagRegressorCvFolds, magAugWeightsFolds?.Length ?? 0, magQuad ? ", quadratic" : "");
        }
        else
        {
            (magWeights, magBias, magAugWeights, magAugBias) = FitElmMagnitudeRegressor(
                trainSet, featureCount, hiddenSize, inputWeights, inputBiases, featureSubsets, learnerActivations,
                hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience, embargo, ct, magQuad);
        }

        // ── 6b. Quantile magnitude regressor ─────────────────────────────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(
                trainSet, featureCount, hp.MagnitudeQuantileTau, hiddenSize,
                inputWeights, inputBiases, featureSubsets, learnerActivations, embargo, ct, magQuad);
            _logger.LogDebug("ELM quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }

        // ── 7. Final evaluation on held-out test set ────────────────────────
        var finalMetrics = ElmEvaluationHelper.EvaluateEnsemble(
            testSet, weights, biases, inputWeights, inputBiases,
            magWeights, magBias, plattA, plattB, featureCount, hiddenSize, featureSubsets,
            magAugWeights, magAugBias, sharpeAnnFactor,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f),
            (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations));

        ct.ThrowIfCancellationRequested();

        // ── 8. ECE post-Platt ───────────────────────────────────────────────
        double ece = ElmEvaluationHelper.ComputeEce(
            testSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f));
        _logger.LogInformation("ELM post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal decision threshold ───────────────────────────────
        double optimalThreshold = ElmCalibrationHelper.ComputeOptimalThreshold(
            selectionSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, featureCount, hiddenSize, featureSubsets,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f));
        _logger.LogInformation("ELM EV-optimal threshold={Thr:F2}", optimalThreshold);

        // ── 10. Permutation feature importance ────────────────────────────
        var featureImportance = selectionSet.Count >= 10
            ? ElmEvaluationHelper.ComputePermutationImportance(
                selectionSet, weights, biases, inputWeights, inputBiases,
                plattA, plattB, featureCount, hiddenSize, featureSubsets,
                (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PrimaryCalibProb(f), ct, optimalThreshold)
            : new float[featureCount];
        featureImportance = ElmEvaluationHelper.NormalisePositiveImportance(featureImportance, featureCount);

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
        var effectiveSelectionSet = selectionSet;
        int effectiveFeatureCount = featureCount;

        _logger.LogDebug("ELM stage timing: calibration + importance = {Ms}ms", stageStopwatch.ElapsedMilliseconds);
        stageStopwatch.Restart();

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

        const int maxPruningIterations = 3;
        for (int pruneIter = 0; pruneIter < maxPruningIterations; pruneIter++)
        {
            if (prunedCount <= 0 || featureCount - prunedCount < 10)
                break;

            int activeFeatureCount = featureCount - prunedCount;
            _logger.LogInformation(
                "ELM feature pruning iter {Iter}: masking {Pruned}/{Total} low-importance features (active={Active})",
                pruneIter + 1, prunedCount, featureCount, activeFeatureCount);

            var maskedTrain = ElmBootstrapHelper.ApplyZeroMask(trainSet, activeMask);
            var maskedCal   = ElmBootstrapHelper.ApplyZeroMask(calSet, activeMask);
            var maskedTest  = ElmBootstrapHelper.ApplyZeroMask(testSet, activeMask);

            // Run the full calibration pipeline on the unpruned model so the
            // acceptance comparison is apples-to-apples (both sides have isotonic).
            var fullCalib = FitCalibrationPipeline(
                maskedCal, weights, biases, inputWeights, inputBiases,
                featureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations, hp, ct);
            var fullCalibProb = BuildCalibratedProbFunc(
                weights, biases, inputWeights, inputBiases, featureCount, hiddenSize, featureSubsets,
                learnerHiddenSizes, learnerActivations, fullCalib, hp.AgeDecayLambda);
            var currentAcceptanceMetrics = ElmEvaluationHelper.EvaluateEnsemble(
                maskedCal, weights, biases, inputWeights, inputBiases,
                magWeights, magBias, fullCalib.PlattA, fullCalib.PlattB, featureCount, hiddenSize, featureSubsets,
                magAugWeights, magAugBias, sharpeAnnFactor,
                (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => fullCalibProb(f),
                (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations),
                fullCalib.OptimalThreshold);

            var prunedHp = hp with
            {
                FeatureSampleRatio = hp.FeatureSampleRatio > 0.0 && hp.FeatureSampleRatio < 1.0
                    ? Math.Min(1.0, hp.FeatureSampleRatio * featureCount / activeFeatureCount)
                    : hp.FeatureSampleRatio
            };

            ModelSnapshot? prunedWarmStart = RemapWarmStartForPruning(warmStart, activeMask, featureCount, hiddenSize);

            (double[][] pw, double[] pb, double[][] piw, double[][] pib,
             int[][]? psub, int[] phs, ElmActivation[] pla, double[][] pInvGramFlat, int[] pInvGramDims) prunedEnsemble;
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
            var (pw, pb, piw, pib, psub, phs, pla, pInvGramFlat, pInvGramDims) = prunedEnsemble;
            int pSanitizedCount = SanitizeLearnerOutputs(pw, pb, "ELM pruned");

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
                    hp.ElmMagRegressorCvFolds, embargo, ct, magQuad);
            }
            else
            {
                (pmw, pmb, pmaw, pmab) = FitElmMagnitudeRegressor(
                    maskedTrain, featureCount, hiddenSize, piw, pib, psub, pla,
                    hp.ElmMagRegressorLr, hp.ElmMagRegressorMaxEpochs, hp.ElmMagRegressorPatience, embargo, ct, magQuad);
            }

            var pCalib = FitCalibrationPipeline(
                maskedCal, pw, pb, piw, pib,
                featureCount, hiddenSize, psub, phs, pla, hp, ct);

            var pCalibProb = BuildCalibratedProbFunc(
                pw, pb, piw, pib, featureCount, hiddenSize, psub, phs, pla, pCalib, hp.AgeDecayLambda);

            var prunedMetrics = ElmEvaluationHelper.EvaluateEnsemble(
                maskedCal, pw, pb, piw, pib, pmw, pmb, pCalib.PlattA, pCalib.PlattB,
                featureCount, hiddenSize, psub, pmaw, pmab, sharpeAnnFactor,
                (f, w, b, iw, ib, pAp, pBp, fc, hs, fs, lw) => pCalibProb(f),
                (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, pla),
                pCalib.OptimalThreshold);

            if (prunedMetrics.Accuracy >= currentAcceptanceMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "ELM pruned model accepted (iter {Iter}): acc={Acc:P1} (was {Old:P1}), reduced features {Full}→{Active}",
                    pruneIter + 1, prunedMetrics.Accuracy, currentAcceptanceMetrics.Accuracy, featureCount, activeFeatureCount);
                weights = pw; biases = pb;
                inputWeights = piw; inputBiases = pib;
                featureSubsets = psub;
                learnerHiddenSizes = phs;
                learnerActivations = pla;
                inverseGramsFlat = pInvGramFlat;
                inverseGramDims = pInvGramDims;
                learnerCalAccuracies = pCalib.LearnerCalAccuracies;
                learnerAccWeights = pCalib.LearnerAccWeights;
                magWeights = pmw; magBias = pmb;
                magAugWeights = pmaw; magAugBias = pmab;
                magAugWeightsFolds = pMagAugWeightsFolds;
                magAugBiasFolds    = pMagAugBiasFolds;
                sanitizedCount = pSanitizedCount;
                plattA = pCalib.PlattA; plattB = pCalib.PlattB;
                temperatureScale = pCalib.TemperatureScale;
                finalMetrics = prunedMetrics;

                (stackingWeights, stackingBias) = (pCalib.StackingWeights, pCalib.StackingBias);
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    (pCalib.PlattABuy, pCalib.PlattBBuy, pCalib.PlattASell, pCalib.PlattBSell);

                avgKellyFraction = ElmCalibrationHelper.ComputeAvgKellyFraction(
                    maskedCal, pw, pb, piw, pib, plattA, plattB, featureCount, hiddenSize, psub,
                    (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => pCalibProb(f));
                ece = ElmEvaluationHelper.ComputeEce(maskedTest, pw, pb, piw, pib, plattA, plattB, featureCount, hiddenSize, psub,
                    (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => pCalibProb(f));
                optimalThreshold = pCalib.OptimalThreshold;

                featureImportance = maskedTest.Count >= 10
                    ? ElmEvaluationHelper.ComputePermutationImportance(
                        maskedTest, pw, pb, piw, pib, pCalib.PlattA, pCalib.PlattB, featureCount, hiddenSize, psub,
                        (f, w2, b2, iw2, ib2, pAp, pBp, fc, hs, fs, lw) => pCalibProb(f), ct, pCalib.OptimalThreshold)
                    : new float[featureCount];
                featureImportance = ElmEvaluationHelper.NormalisePositiveImportance(featureImportance, featureCount);
                calImportanceScores = maskedCal.Count >= 10
                    ? ElmEvaluationHelper.ComputeCalPermutationImportance(
                        maskedCal, pw, pb, piw, pib, featureCount, hiddenSize, psub,
                        (f, w2, b2, iw2, ib2, fc, hs, fs, lw) => EnsembleRawProb(
                            f, w2, b2, iw2, ib2, fc, hs, fs, lw ?? pCalib.LearnerAccWeights,
                            phs, pla, pCalib.StackingWeights, pCalib.StackingBias), ct)
                    : new double[featureCount];

                // Advance the effective views so that steps 12-22 use the pruned model's
                // feature space rather than the original unmasked data.
                effectiveCalSet       = maskedCal;
                effectiveTestSet      = maskedTest;
                effectiveTrainSet     = maskedTrain;
                var effectiveSelectionMasked = ElmBootstrapHelper.ApplyZeroMask(selectionSet, activeMask);
                effectiveSelectionSet = effectiveSelectionMasked;
                effectiveFeatureCount = featureCount;

                // Recompute feature importance for next iteration
                activeMask = ElmBootstrapHelper.BuildFeatureMask(featureImportance, hp.MinFeatureImportance, featureCount);
                if (hp.MutualInfoRedundancyThreshold > 0.0)
                {
                    var miRedundantIndices = ElmEvaluationHelper.ComputeRedundantFeaturePairIndices(
                        effectiveTrainSet, featureCount, hp.MutualInfoRedundancyThreshold);
                    foreach (var (fi, fj) in miRedundantIndices)
                    {
                        if (fi >= featureCount || fj >= featureCount) continue;
                        if (!activeMask[fi] || !activeMask[fj]) continue;
                        float impI = fi < featureImportance.Length ? featureImportance[fi] : 0;
                        float impJ = fj < featureImportance.Length ? featureImportance[fj] : 0;
                        activeMask[impI <= impJ ? fi : fj] = false;
                    }
                }
                prunedCount = activeMask.Count(m => !m);

                if (prunedCount == 0 || featureCount - prunedCount < 10)
                    break; // No more features to prune
            }
            else
            {
                _logger.LogInformation(
                    "ELM pruned model rejected (iter {Iter}, acc drop {Drop:P1}) — keeping current model",
                    pruneIter + 1, finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask = new bool[featureCount];
                Array.Fill(activeMask, true);
                break;
            }
        }
        // After loop: ensure activeMask is valid
        if (prunedCount == 0)
        {
            activeMask = new bool[featureCount];
            Array.Fill(activeMask, true);
        }

        _logger.LogDebug("ELM stage timing: feature pruning = {Ms}ms", stageStopwatch.ElapsedMilliseconds);
        stageStopwatch.Restart();

        // ── 12. Final calibration (isotonic + temperature refit + threshold) ──
        // Pass the existing upstream calibration (Platt, stacking, etc.) as prior
        // so only the isotonic tail runs. For the pruning-accepted case, those
        // variables were already overwritten from pCalib above.
        var priorCalib = new CalibrationResult(
            learnerCalAccuracies, learnerAccWeights,
            stackingWeights ?? [], stackingBias,
            plattA, plattB, temperatureScale,
            plattABuy, plattBBuy, plattASell, plattBSell,
            [], optimalThreshold, DateTime.UtcNow);

        var finalCalib = FitCalibrationPipeline(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            effectiveFeatureCount, hiddenSize, featureSubsets,
            learnerHiddenSizes, learnerActivations, hp, ct, prior: priorCalib);

        var trainedAtUtc = finalCalib.TrainedAtUtc;
        temperatureScale = finalCalib.TemperatureScale;
        plattABuy  = finalCalib.PlattABuy;  plattBBuy  = finalCalib.PlattBBuy;
        plattASell = finalCalib.PlattASell; plattBSell = finalCalib.PlattBSell;
        var isotonicBp = finalCalib.IsotonicBreakpoints;
        _logger.LogInformation("ELM isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);

        var FinalEffectiveCalibProb = BuildCalibratedProbFunc(
            weights, biases, inputWeights, inputBiases,
            effectiveFeatureCount, hiddenSize, featureSubsets,
            learnerHiddenSizes, learnerActivations, finalCalib, hp.AgeDecayLambda);

        ece = ElmEvaluationHelper.ComputeEce(
            effectiveTestSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f));
        optimalThreshold = finalCalib.OptimalThreshold;
        finalMetrics = ElmEvaluationHelper.EvaluateEnsemble(
            effectiveTestSet, weights, biases, inputWeights, inputBiases,
            magWeights, magBias, plattA, plattB, effectiveFeatureCount, hiddenSize, featureSubsets,
            magAugWeights, magAugBias, sharpeAnnFactor,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => FinalEffectiveCalibProb(f),
            (f, aw, ab, fc, hs, eiw, eib, fs) => PredictMagnitudeAug(f, aw, ab, fc, hs, eiw, eib, fs, learnerActivations),
            optimalThreshold);

        _logger.LogInformation(
            "ELM final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2} thr={Thr:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio, optimalThreshold);
        _logger.LogDebug("ELM stage timing: final calibration = {Ms}ms", stageStopwatch.ElapsedMilliseconds);
        stageStopwatch.Restart();

        // ── 13. Conformal prediction threshold ───────────────────────────────
        // Conformal uses pre-isotonic calibration (Platt + temp + class-conditional)
        double PreIsoEffectiveCalibProb(float[] features) => ApplyProductionCalibration(
            EnsembleRawProb(features, weights, biases, inputWeights, inputBiases,
                effectiveFeatureCount, hiddenSize, featureSubsets, finalCalib.LearnerAccWeights,
                learnerHiddenSizes, learnerActivations, finalCalib.StackingWeights, finalCalib.StackingBias),
            plattA, plattB, temperatureScale, plattABuy, plattBBuy, plattASell, plattBSell);

        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ElmCalibrationHelper.ComputeConformalQHat(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, isotonicBp, effectiveFeatureCount, hiddenSize, featureSubsets, conformalAlpha,
            (f, w, b, iw, ib, pA, pB, fc, hs, fs, lw) => PreIsoEffectiveCalibProb(f));
        _logger.LogInformation("ELM conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 13b. Mondrian (per-class) conformal prediction ───────────────────
        double conformalQHatBuy = conformalQHat, conformalQHatSell = conformalQHat;
        {
            var buyResiduals = new List<double>();
            var sellResiduals = new List<double>();
            foreach (var s in effectiveCalSet)
            {
                double calibP = PreIsoEffectiveCalibProb(s.Features);
                double y = ToBinaryLabel(s.Direction);
                double residual = Math.Abs(y - calibP);
                if (s.Direction > 0)
                    buyResiduals.Add(residual);
                else
                    sellResiduals.Add(residual);
            }
            if (buyResiduals.Count >= 5)
            {
                buyResiduals.Sort();
                int idx = Math.Min((int)Math.Ceiling((1.0 - conformalAlpha) * (buyResiduals.Count + 1)) - 1, buyResiduals.Count - 1);
                conformalQHatBuy = buyResiduals[Math.Max(0, idx)];
            }
            if (sellResiduals.Count >= 5)
            {
                sellResiduals.Sort();
                int idx = Math.Min((int)Math.Ceiling((1.0 - conformalAlpha) * (sellResiduals.Count + 1)) - 1, sellResiduals.Count - 1);
                conformalQHatSell = sellResiduals[Math.Max(0, idx)];
            }
            _logger.LogInformation(
                "ELM Mondrian conformal: qHatBuy={QBuy:F4} qHatSell={QSell:F4}",
                conformalQHatBuy, conformalQHatSell);
        }

        ct.ThrowIfCancellationRequested();

        double[] metaLabelRankingScores = calImportanceScores.Any(score => score > 0.0)
            ? calImportanceScores
            : featureImportance.Select(static value => (double)value).ToArray();
        int[] metaLabelTopFeatureIndices = metaLabelRankingScores
            .Select((score, index) => (Score: score, Index: index))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(Math.Min(5, effectiveFeatureCount))
            .Select(item => item.Index)
            .ToArray();
        if (metaLabelTopFeatureIndices.Length == 0)
            metaLabelTopFeatureIndices = Enumerable.Range(0, Math.Min(5, effectiveFeatureCount)).ToArray();

        // ── 14. Meta-label secondary classifier ──────────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            effectiveFeatureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
            optimalThreshold, metaLabelTopFeatureIndices, FinalEffectiveCalibProb,
            stackingWeights, stackingBias,
            hp.ElmSubModelLr, hp.ElmSubModelMaxEpochs, hp.ElmSubModelPatience, embargo, ct);
        _logger.LogDebug("ELM meta-label: bias={B:F4}", metaLabelBias);

        // ── 15. Abstention gate ──────────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionModel(
            effectiveCalSet, weights, biases, inputWeights, inputBiases,
            plattA, plattB, metaLabelWeights, metaLabelBias,
            effectiveFeatureCount, hiddenSize, featureSubsets, learnerHiddenSizes, learnerActivations,
            optimalThreshold, metaLabelTopFeatureIndices, FinalEffectiveCalibProb,
            stackingWeights, stackingBias,
            hp.ElmSubModelLr, hp.ElmSubModelMaxEpochs, hp.ElmSubModelPatience, embargo, ct);
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
                    oobSum += ClampProbabilityOrNeutral(ElmLearnerProb(
                        effectiveTrainSet[i].Features, weights[k], biases[k],
                        inputWeights[k], inputBiases[k],
                        effectiveFeatureCount,
                        ResolveLearnerHiddenSize(learnerHiddenSizes, k, hiddenSize, inputBiases[k]),
                        ResolveLearnerSubset(featureSubsets, k),
                        ResolveLearnerActivation(learnerActivations, k)));
                    oobLearners++;
                }
                if (oobLearners == 0) continue;

                double oobProb = oobSum / oobLearners;
                oobTotal++;
                if ((oobProb >= 0.5 ? 1 : 0) == ToBinaryLabel(effectiveTrainSet[i].Direction)) oobCorrect++;
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
                double y = ToBinaryLabel(effectiveCalSet[i].Direction);
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
                f, wk, bk, iwk, ibk, fc,
                learnerIdx >= 0 && learnerIdx < learnerHiddenSizes.Length ? learnerHiddenSizes[learnerIdx] : hs, sub,
                ResolveLearnerActivation(learnerActivations, learnerIdx)));
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

        _logger.LogDebug("ELM stage timing: post-training metrics = {Ms}ms", stageStopwatch.ElapsedMilliseconds);

        // ── New evaluation metrics ───────────────────────────────────────────
        var (reliabilityBinConf, reliabilityBinAcc, reliabilityBinCounts) =
            ComputeReliabilityDiagram(effectiveTestSet, FinalEffectiveCalibProb);
        var (calibrationLoss, refinementLoss) =
            ComputeMurphyDecomposition(effectiveTestSet, FinalEffectiveCalibProb);
        var (calResidualMean, calResidualStd, calResidualThreshold) =
            ComputeCalibrationResidualStats(effectiveCalSet, FinalEffectiveCalibProb);
        double predictionStability = ComputePredictionStabilityScore(effectiveTestSet, FinalEffectiveCalibProb);
        double[] featureVariances = ComputeFeatureVariances(effectiveTrainSet, effectiveFeatureCount);

        // ── Scalar sanitization ──────────────────────────────────────────────
        ece = SafeElm(ece, 1.0);
        optimalThreshold = SafeElm(optimalThreshold, 0.5);
        avgKellyFraction = SafeElm(avgKellyFraction);
        durbinWatson = SafeElm(durbinWatson, 2.0);
        brierSkillScore = SafeElm(brierSkillScore);
        calibrationLoss = SafeElm(calibrationLoss);
        refinementLoss = SafeElm(refinementLoss);
        predictionStability = SafeElm(predictionStability);
        calResidualMean = SafeElm(calResidualMean);
        calResidualStd = SafeElm(calResidualStd);
        calResidualThreshold = SafeElm(calResidualThreshold);
        conformalQHat = Math.Clamp(SafeElm(conformalQHat, 0.5), 1e-7, 1.0 - 1e-7);
        conformalQHatBuy = Math.Clamp(SafeElm(conformalQHatBuy, conformalQHat), 1e-7, 1.0 - 1e-7);
        conformalQHatSell = Math.Clamp(SafeElm(conformalQHatSell, conformalQHat), 1e-7, 1.0 - 1e-7);

        // ── 23. Serialise model snapshot ────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = snapshotFeatureNames,
            Means                      = means,
            Stds                       = stds,
            ElmWinsorizeLowerBounds    = elmWinsorizeLowerBounds,
            ElmWinsorizeUpperBounds    = elmWinsorizeUpperBounds,
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
            TrainSamplesAtLastCalibration = trainSet.Count,
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
            ConformalQHatBuy           = conformalQHatBuy,
            ConformalQHatSell          = conformalQHatSell,
            FracDiffD                  = hp.FracDiffD,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            MetaLabelTopFeatureIndices = metaLabelTopFeatureIndices,
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
            ReliabilityBinConfidence   = reliabilityBinConf.Length > 0 ? reliabilityBinConf : null,
            ReliabilityBinAccuracy     = reliabilityBinAcc.Length > 0 ? reliabilityBinAcc : null,
            ReliabilityBinCounts       = reliabilityBinCounts.Length > 0 ? reliabilityBinCounts : null,
            CalibrationLoss            = calibrationLoss,
            RefinementLoss             = refinementLoss,
            PredictionStabilityScore   = predictionStability,
            FeatureVariances           = featureVariances,
            ElmDriftArtifact           = driftArtifact,
            ElmWarmStartArtifact       = warmStartArtifact,
            ElmCalibrationResidualMean      = calResidualMean,
            ElmCalibrationResidualStd       = calResidualStd,
            ElmCalibrationResidualThreshold = calResidualThreshold,
            TrainedAtUtc               = trainedAtUtc,
            AgeDecayLambda             = hp.AgeDecayLambda,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            AdaptiveLabelSmoothing     = adaptiveLabelSmoothing,
            LearnerCalAccuracies       = learnerCalAccuracies,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount      = sanitizedCount,
            ConformalCoverage          = hp.ConformalCoverage,
            ElmOutputWeights           = null,
            ElmInverseGram             = inverseGramsFlat,
            ElmInverseGramDim          = inverseGramDims,
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
            FeatureSchemaFingerprint   = featureSchemaFingerprint,
            PreprocessingFingerprint   = basePreprocessingFingerprint,
            TrainerFingerprint         = trainerFingerprint,
        };

        SanitizeElmSnapshotArrays(snapshot);

        var auditResult = RunElmModelAudit(snapshot, testSet.Count > 0 ? testSet : calSet);
        snapshot.ElmAuditArtifact = auditResult.Artifact;
        if (auditResult.Findings.Length > 0)
            _logger.LogWarning("ELM audit findings: {Findings}", string.Join("; ", auditResult.Findings));

        snapshot = ElmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var snapshotValidation = ElmSnapshotSupport.ValidateNormalizedSnapshot(snapshot, allowLegacy: false);
        if (!snapshotValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"ELM snapshot self-audit failed: {string.Join("; ", snapshotValidation.Issues)}");
        }

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "ElmModelTrainer complete: K={K}, hidden={H}, accuracy={Acc:P1}, Brier={B:F4}, BSS={BSS:F4}",
            K, hiddenSize, finalMetrics.Accuracy, finalMetrics.BrierScore, brierSkillScore);

        _logger.LogInformation("ELM total training time: {Ms}ms", pipelineStopwatch.ElapsedMilliseconds);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }
}
