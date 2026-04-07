using System.Buffers;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Feature Tokenizer Transformer (FT-Transformer) trainer (Rec #390).
/// <para>
/// Architecture:
/// <list type="number">
///   <item>Per-feature affine embedding: e_f = We[f] * x_f + Be[f].</item>
///   <item>Multi-head self-attention with separate Q, K, V projections and output projection Wo.</item>
///   <item>Pre-norm: LayerNorm → Attention → Residual connection.</item>
///   <item>Pre-norm: LayerNorm → FFN (Linear(D, FfnDim) → GELU → Dropout → Linear(FfnDim, D)) → Residual connection.</item>
///   <item>Learnable [CLS] token prepended to feature token sequence.</item>
///   <item>[CLS] output → Final LayerNorm → Linear classifier head → Sigmoid.</item>
/// </list>
/// </para>
/// <para>
/// Training pipeline:
/// <list type="number">
///   <item>Z-score standardise all samples from computed means/stds.</item>
///   <item>Run K-fold walk-forward CV (expanding window, embargo + purging) with equity-curve gating.</item>
///   <item>Train the final model on 70 % of data with a 10 % Platt calibration fold and ~18 % hold-out test.</item>
///   <item>Mini-batch Adam optimizer (β₁=0.9, β₂=0.999) with cosine-annealing LR schedule and early stopping.</item>
///   <item>Label smoothing (ε=LabelSmoothing) applied to cross-entropy targets.</item>
///   <item>Attention + FFN dropout for regularisation.</item>
///   <item>Platt scaling (A, B) + class-conditional Platt on the calibration fold.</item>
///   <item>Temperature scaling on the calibration fold.</item>
///   <item>ECE (Expected Calibration Error) computed post-Platt on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set.</item>
///   <item>Average Kelly fraction on calibration set.</item>
///   <item>Permutation feature importance on the test set.</item>
///   <item>Feature pruning re-train pass (remove low-importance features, retrain if accuracy holds).</item>
///   <item>Magnitude linear regressor with Huber loss + Durbin-Watson autocorrelation check.</item>
///   <item>Post-training NaN/Inf weight sanitisation.</item>
///   <item>Full model serialisation for inference reconstruction.</item>
///   <item>Optional warm-start: all weight matrices initialised from previous model snapshot.</item>
///   <item>Optional incremental update fast-path for adapting to regime changes.</item>
/// </list>
/// </para>
/// Registered with key "fttransformer".
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.FtTransformer)]
public sealed partial class FtTransformerModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "FTTRANSFORMER";
    private const string ModelVersion = "5.0";
    private const int    DefaultEmbedDim   = 16;
    private const int    DefaultNumHeads   = 4;
    private const int    DefaultFfnDim     = 64; // 4 × EmbedDim
    private const int    DefaultNumLayers  = 3;
    private const int    DefaultBatchSize  = 32;
    private const double DefaultDropoutRate = 0.1;

    // Adam hyper-parameters (fixed)
    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<FtTransformerModelTrainer> _logger;

    public FtTransformerModelTrainer(ILogger<FtTransformerModelTrainer> logger) => _logger = logger;

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

        int F = samples[0].Features.Length;
        double sharpeAnnual = hp.SharpeAnnualisationFactor > 0.0 ? hp.SharpeAnnualisationFactor : 252.0;

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"FtTransformerModelTrainer requires at least {hp.MinSamples} samples; got {samples.Count}.");

        // Resolve architecture hyper-parameters
        int embedDim = warmStart?.FtTransformerEmbedDim > 0
            ? warmStart.FtTransformerEmbedDim : DefaultEmbedDim;
        int numHeads = warmStart?.FtTransformerNumHeads > 0
            ? warmStart.FtTransformerNumHeads : DefaultNumHeads;
        int ffnDim = warmStart?.FtTransformerFfnDim > 0
            ? warmStart.FtTransformerFfnDim : DefaultFfnDim;

        int numLayers = warmStart?.FtTransformerNumLayers > 0
            ? warmStart.FtTransformerNumLayers : DefaultNumLayers;

        if (embedDim % numHeads != 0)
            throw new InvalidOperationException(
                $"EmbedDim ({embedDim}) must be divisible by NumHeads ({numHeads}).");

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
                    UseIncrementalUpdate  = false, // prevent recursion
                };
                return Train(recentSamples, incrementalHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Z-score standardisation over ALL samples ──────────────────────
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var s in samples) rawFeatures.Add(s.Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawFeatures);

        var allStd = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        // ── 2. Walk-forward cross-validation ─────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(
            allStd, hp, F, embedDim, numHeads, ffnDim, numLayers, sharpeAnnual, ct);
        _logger.LogInformation(
            "FT-Transformer walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe);

        if (equityCurveGateFailed)
            return new TrainingResult(new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);

        ct.ThrowIfCancellationRequested();

        // ── 3. Final model splits: 70 % train | 10 % cal | ~18 % test ────────
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

        _logger.LogInformation(
            "FT-Transformer: n={N} F={F} dim={D} heads={H} ffn={Ffn} layers={L} train={Train} cal={Cal} test={Test} embargo={Embargo}",
            allStd.Count, F, embedDim, numHeads, ffnDim, numLayers,
            trainSet.Count, calSet.Count, testSet.Count, embargo);

        // ── 4. Train the FT-Transformer model ────────────────────────────────
        var model = FitTransformer(trainSet, effectiveHp, F, embedDim, numHeads, ffnDim, numLayers, warmStart, ct);

        // ── 5. Fit magnitude regressor ────────────────────────────────────────
        var (magWeights, magBias) = FitLinearRegressor(trainSet, F, effectiveHp, ct);

        // ── 6. Platt calibration (on calibration set) ─────────────────────────
        var calBuf = new InferenceBuffers(F, embedDim, numHeads, ffnDim);
        var (plattA, plattB) = FitPlattScaling(calSet, model, F, calBuf);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 6b. Class-conditional Platt ───────────────────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, model, F, calBuf);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);

        // ── 6c. Average Kelly fraction on cal set ─────────────────────────────
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, model, plattA, plattB, F, calBuf);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);

        // ── 7. Final evaluation on held-out test set ──────────────────────────
        var testBuf = new InferenceBuffers(F, embedDim, numHeads, ffnDim);
        var finalMetrics = EvaluateModel(testSet, model, magWeights, magBias, plattA, plattB, F, testBuf, sharpeAnnual);

        _logger.LogInformation(
            "FT-Transformer final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE post-Platt ────────────────────────────────────────────────
        var (ece, eceBinConf, eceBinAcc, eceBinCount) = ComputeEce(testSet, model, plattA, plattB, F, testBuf);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal decision threshold (tuned on cal set) ──────────────
        int thrMinBps  = hp.ThresholdSearchMin  * 100;  // e.g. 30 → 3000
        int thrMaxBps  = hp.ThresholdSearchMax  * 100;  // e.g. 75 → 7500
        int thrStepBps = hp.ThresholdSearchStepBps > 0 ? hp.ThresholdSearchStepBps : 50;
        double optimalThreshold = ComputeOptimalThreshold(calSet, model, plattA, plattB, F, calBuf, thrMinBps, thrMaxBps, thrStepBps);
        _logger.LogInformation("EV-optimal threshold={Thr:F2} (default 0.50)", optimalThreshold);

        // ── 10. Permutation feature importance ───────────────────────────────
        var featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, model, plattA, plattB, F, testBuf, ct)
            : new float[F];

        var topFeatures = featureImportance
            .Select((imp, idx) => (Importance: imp, Name: MLFeatureHelper.FeatureNames[idx]))
            .OrderByDescending(x => x.Importance)
            .Take(5);
        _logger.LogInformation(
            "Top 5 features: {Features}",
            string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));

        // ── 10b. Feature pruning re-train pass ───────────────────────────────
        var activeMask = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int prunedCount = activeMask.Count(m => !m);
        int activeF = F - prunedCount;

        if (prunedCount > 0 && activeF >= 10)
        {
            _logger.LogInformation(
                "Feature pruning: removing {Pruned}/{Total} low-importance features (keeping {Active})",
                prunedCount, F, activeF);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            var prunedHp = effectiveHp with
            {
                MaxEpochs             = Math.Max(30, effectiveHp.MaxEpochs / 2),
                EarlyStoppingPatience = Math.Max(5,  effectiveHp.EarlyStoppingPatience / 2),
            };

            // Build a partial warm-start from the already-trained full model:
            // copy transformer layer weights (feature-count-independent) and extract
            // only the active features' embedding weights.
            var prunedWarmStart = BuildPrunedWarmStart(model, activeMask, activeF);

            var prunedModel = FitTransformer(maskedTrain, prunedHp, activeF, embedDim, numHeads, ffnDim, numLayers, prunedWarmStart, ct);
            var (pmw, pmb) = FitLinearRegressor(maskedTrain, activeF, prunedHp, ct);
            var prunedBuf = new InferenceBuffers(activeF, embedDim, numHeads, ffnDim);
            var (pA, pB) = FitPlattScaling(maskedCal, prunedModel, activeF, prunedBuf);
            var prunedMetrics = EvaluateModel(maskedTest, prunedModel, pmw, pmb, pA, pB, activeF, prunedBuf, sharpeAnnual);

            if (prunedMetrics.Accuracy >= finalMetrics.Accuracy - 0.005)
            {
                _logger.LogInformation(
                    "Pruned model accepted: acc={Acc:P1} (was {Old:P1})",
                    prunedMetrics.Accuracy, finalMetrics.Accuracy);
                model        = prunedModel;
                magWeights   = pmw;  magBias  = pmb;
                plattA       = pA;   plattB   = pB;
                finalMetrics = prunedMetrics;
                F            = activeF;
                trainSet     = maskedTrain;  // downstream DW/PSI use masked features
                testSet      = maskedTest;   // downstream BSS uses masked features
                calSet       = maskedCal;    // downstream conformal/temperature use masked features
                calBuf       = prunedBuf;    // inference buffers sized for activeF
                (ece, eceBinConf, eceBinAcc, eceBinCount) = ComputeEce(maskedTest, model, pA, pB, F, prunedBuf);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, model, pA, pB, F, prunedBuf, thrMinBps, thrMaxBps, thrStepBps);
                (plattABuy, plattBBuy, plattASell, plattBSell) =
                    FitClassConditionalPlatt(maskedCal, model, F, prunedBuf);
                avgKellyFraction = ComputeAvgKellyFraction(maskedCal, model, pA, pB, F, prunedBuf);
            }
            else
            {
                _logger.LogInformation(
                    "Pruned model rejected (acc drop {Drop:P1}) — keeping full model",
                    finalMetrics.Accuracy - prunedMetrics.Accuracy);
                prunedCount = 0;
                activeMask = new bool[F]; Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F]; Array.Fill(activeMask, true);
        }

        // ── 11. Brier Skill Score ─────────────────────────────────────────────
        var bssBuf = new InferenceBuffers(F, embedDim, numHeads, ffnDim);
        double brierSkillScore = ComputeBrierSkillScore(testSet, model, plattA, plattB, F, bssBuf);
        _logger.LogInformation("Brier Skill Score (BSS)={BSS:F4} (>0 beats naive predictor)", brierSkillScore);

        // ── 12. Conformal prediction threshold ───────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat = ComputeConformalQHat(calSet, model, plattA, plattB, F, calBuf, conformalAlpha);
        _logger.LogInformation("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 13. Temperature scaling ──────────────────────────────────────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calSet, model, F, calBuf);
            _logger.LogDebug("Temperature scaling: T={T:F4} (1.0=no correction)", temperatureScale);
        }

        // ── 14. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        _logger.LogDebug("Durbin-Watson statistic={DW:F4} (2=no autocorr, <1.5=positive autocorr)", durbinWatson);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "Magnitude residuals are autocorrelated (DW={DW:F3} < threshold {Thr:F2}).",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 15. PSI baseline (feature quantile breakpoints) ──────────────────
        var standardisedTrainFeatures = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) standardisedTrainFeatures.Add(s.Features);
        var featureQuantileBreakpoints = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(standardisedTrainFeatures);

        // ── 16. Post-training NaN/Inf weight sanitisation ────────────────────
        int sanitizedCount = SanitiseWeights(model);
        if (model.UsePositionalBias)
            for (int l = 0; l < model.NumLayers; l++)
                if (model.Layers[l].PosBias is not null)
                    for (int h = 0; h < model.NumHeads; h++)
                        if (HasNonFiniteArray(model.Layers[l].PosBias![h]))
                            { Array.Clear(model.Layers[l].PosBias[h]); sanitizedCount++; }
        if (sanitizedCount > 0)
            _logger.LogWarning("Post-training sanitisation: {N} weight arrays had non-finite values.", sanitizedCount);

        // ── 17. Serialise model snapshot ─────────────────────────────────────
        var snapshot = new ModelSnapshot
        {
            Type                        = ModelType,
            Version                     = ModelVersion,
            Features                    = MLFeatureHelper.FeatureNames,
            Means                       = means,
            Stds                        = stds,
            BaseLearnersK               = F,
            Weights                     = model.We,
            Biases                      = [model.BOut],
            MagWeights                  = magWeights,
            MagBias                     = magBias,
            PlattA                      = plattA,
            PlattB                      = plattB,
            PlattABuy                   = plattABuy,
            PlattBBuy                   = plattBBuy,
            PlattASell                  = plattASell,
            PlattBSell                  = plattBSell,
            AvgKellyFraction            = avgKellyFraction,
            Metrics                     = finalMetrics,
            TrainSamples                = trainSet.Count,
            TestSamples                 = testSet.Count,
            CalSamples                  = calSet.Count,
            EmbargoSamples              = embargo,
            TrainedOn                   = DateTime.UtcNow,
            TrainedAtUtc                = DateTime.UtcNow,
            FeatureImportance           = featureImportance,
            ActiveFeatureMask           = activeMask,
            PrunedFeatureCount          = prunedCount,
            OptimalThreshold            = optimalThreshold,
            Ece                         = ece,
            ReliabilityBinConfidence    = eceBinConf,
            ReliabilityBinAccuracy      = eceBinAcc,
            ReliabilityBinCounts        = eceBinCount,
            ConformalQHat               = conformalQHat,
            BrierSkillScore             = brierSkillScore,
            TemperatureScale            = temperatureScale,
            DurbinWatsonStatistic       = durbinWatson,
            FeatureQuantileBreakpoints  = featureQuantileBreakpoints,
            // FT-Transformer specific weights
            FtTransformerEmbedWeights   = model.We,
            FtTransformerEmbedBiases    = model.Be,
            FtTransformerWq             = model.Wq,
            FtTransformerWk             = model.Wk,
            FtTransformerWv             = model.Wv,
            FtTransformerWo             = model.Wo,
            FtTransformerWff1           = model.Wff1,
            FtTransformerBff1           = model.Bff1,
            FtTransformerWff2           = model.Wff2,
            FtTransformerBff2           = model.Bff2,
            FtTransformerGamma1         = model.Gamma1,
            FtTransformerBeta1          = model.Beta1,
            FtTransformerGamma2         = model.Gamma2,
            FtTransformerBeta2          = model.Beta2,
            FtTransformerOutputWeights  = model.WOut,
            FtTransformerOutputBias     = model.BOut,
            FtTransformerEmbedDim       = embedDim,
            FtTransformerNumHeads       = numHeads,
            FtTransformerFfnDim         = ffnDim,
            FtTransformerNumLayers      = model.NumLayers,
            FtTransformerAdditionalLayersJson = model.NumLayers > 1
                ? JsonSerializer.Serialize(
                    Enumerable.Range(1, model.NumLayers - 1).Select(l => new SerializedLayerWeights
                    {
                        Wq = model.Layers[l].Wq, Wk = model.Layers[l].Wk,
                        Wv = model.Layers[l].Wv, Wo = model.Layers[l].Wo,
                        Gamma1 = model.Layers[l].Gamma1, Beta1 = model.Layers[l].Beta1,
                        Wff1 = model.Layers[l].Wff1, Bff1 = model.Layers[l].Bff1,
                        Wff2 = model.Layers[l].Wff2, Bff2 = model.Layers[l].Bff2,
                        Gamma2 = model.Layers[l].Gamma2, Beta2 = model.Layers[l].Beta2,
                        PosBias = model.Layers[l].PosBias,
                    }).ToList(), JsonOpts)
                : null,
            FtTransformerAdditionalLayersBytes = model.NumLayers > 1
                ? SerializeAdditionalLayersBinary(model)
                : null,
            FtTransformerPosBias = model.UsePositionalBias && model.Layers[0].PosBias is not null
                ? model.Layers[0].PosBias.Select(h => (double[])h.Clone()).ToArray()
                : null,
            FtTransformerClsToken       = model.ClsToken,
            FtTransformerGammaFinal     = model.GammaFinal,
            FtTransformerBetaFinal      = model.BetaFinal,
            ParentModelId               = parentModelId ?? 0,
            GenerationNumber            = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            WalkForwardSharpeTrend      = cvResult.SharpeTrend,
            FeatureStabilityScores      = cvResult.FeatureStabilityScores ?? [],
            HyperparamsJson             = JsonSerializer.Serialize(hp, JsonOpts),
            SanitizedLearnerCount       = sanitizedCount,
            ConformalCoverage           = hp.ConformalCoverage,
        };

        var modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);

        _logger.LogInformation(
            "FtTransformerModelTrainer complete: accuracy={Acc:P1}, Brier={B:F4}, snapshotBytes={Bytes}",
            finalMetrics.Accuracy, finalMetrics.BrierScore, modelBytes.Length);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ── Transformer model types are in FtTransformerModelTrainer.Types.cs ───

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  embedDim,
        int                  numHeads,
        int                  ffnDim,
        int                  numLayers,
        double               sharpeAnnualisation,
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

        var foldResults = new (double Acc, double F1, double EV, double Sharpe, double[] FoldImp, bool IsBad)?[folds];
        int parallelism = Math.Min(folds, Math.Max(1, Environment.ProcessorCount / 2));

        Parallel.For(0, folds, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
                return; // skip fold

            var foldTrain = samples[..trainEnd].ToList();

            // Time-series purging
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
                MaxEpochs             = Math.Max(30, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            // Per-fold Platt calibration: reserve last 10% of fold train for Platt fitting
            int plattSize = Math.Max(10, foldTrain.Count / 10);
            var foldTrainOnly = foldTrain.Count > plattSize + hp.MinSamples
                ? foldTrain[..^plattSize]
                : foldTrain;
            var foldPlattSet = foldTrain.Count > plattSize + hp.MinSamples
                ? foldTrain[^plattSize..]
                : foldTrain[^Math.Min(plattSize, foldTrain.Count)..];

            var cvModel = FitTransformer(foldTrainOnly, cvHp, featureCount, embedDim, numHeads, ffnDim, numLayers, null, ct);
            var cvBuf = new InferenceBuffers(featureCount, embedDim, numHeads, ffnDim);

            // Fit per-fold Platt scaling
            var (foldPlattA, foldPlattB) = FitPlattScaling(foldPlattSet, cvModel, featureCount, cvBuf);
            var m = EvaluateModel(foldTest, cvModel, new double[featureCount], 0.0, foldPlattA, foldPlattB, featureCount, cvBuf, sharpeAnnualisation);

            // Compute per-feature mean |embedding weight| for stability scoring
            var foldImp = new double[featureCount];
            for (int f = 0; f < featureCount; f++)
            {
                double sum = 0;
                for (int d = 0; d < embedDim; d++)
                    sum += Math.Abs(cvModel.We[f][d]);
                foldImp[f] = sum / embedDim;
            }

            // ── Equity-curve gate ────────────────────────────────────────────
            bool isBadFold = false;
            if (hp.MaxFoldDrawdown < 1.0 || hp.MinFoldCurveSharpe > -99.0)
            {
                var foldPredictions = new (int Predicted, int Actual)[foldTest.Count];
                for (int pi = 0; pi < foldTest.Count; pi++)
                {
                    double rawP = ForwardPass(foldTest[pi].Features, cvModel, featureCount, cvBuf);
                    foldPredictions[pi] = (rawP >= 0.5 ? 1 : -1,
                                           foldTest[pi].Direction > 0 ? 1 : -1);
                }

                var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions, sharpeAnnualisation);

                if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                    isBadFold = true;
                if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                    isBadFold = true;
            }

            foldResults[fold] = (m.Accuracy, m.F1, m.ExpectedValue, m.SharpeRatio, foldImp, isBadFold);
        });

        // Collect results
        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds = 0;
        foreach (var fr in foldResults)
        {
            if (fr is null) continue;
            accList.Add(fr.Value.Acc);
            f1List.Add(fr.Value.F1);
            evList.Add(fr.Value.EV);
            sharpeList.Add(fr.Value.Sharpe);
            foldImportances.Add(fr.Value.FoldImp);
            if (fr.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0)
        {
            _logger.LogWarning("Walk-forward CV: all folds skipped — failing equity-curve gate.");
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), true);
        }

        // Check equity-curve gate
        double badFoldThreshold = hp.MaxBadFoldFraction > 0.0 && hp.MaxBadFoldFraction < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "Equity-curve gate: {BadFolds}/{TotalFolds} folds failed. Model rejected.",
                badFolds, accList.Count);

        double avgAcc = accList.Average();
        double stdAcc = StdDev(accList, avgAcc);
        double sharpeTrend = ComputeSharpeTrend(sharpeList);

        // Sharpe trend gate
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
            AvgAccuracy:            avgAcc,
            StdAccuracy:            stdAcc,
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), equityCurveGateFailed);
    }

    // ── Transformer fitting ──────────────────────────────────────────────────

    private TransformerModel FitTransformer(
        List<TrainingSample> train,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  embedDim,
        int                  numHeads,
        int                  ffnDim,
        int                  numLayers,
        ModelSnapshot?       warmStart,
        CancellationToken    ct)
    {
        // Issue #7: Quadratic memory guard for large feature counts
        if (featureCount > 500)
        {
            int S = featureCount + 1;
            _logger.LogWarning(
                "FT-Transformer: F={F} (S={S}). Attention allocates O(S²) = O({Mem}) per head per layer. " +
                "Consider reducing feature count or using a sparse attention variant.",
                featureCount, S, (long)S * S);
        }

        var model = new TransformerModel(featureCount, embedDim, numHeads, ffnDim, numLayers);
        model.UsePositionalBias = hp.FtUsePositionalEncoding;
        if (model.UsePositionalBias)
        {
            int S = featureCount + 1;
            for (int l = 0; l < numLayers; l++)
            {
                model.Layers[l].PosBias = new double[numHeads][];
                for (int h = 0; h < numHeads; h++)
                    model.Layers[l].PosBias[h] = new double[S * S];
            }
        }

        int seed = HashCode.Combine(train.Count, featureCount, embedDim, train[0].Direction);
        var rng = new Random(seed);

        // ── Initialise or warm-start weights ─────────────────────────────────
        bool hasFullWarmStart =
            warmStart?.FtTransformerEmbedWeights is { Length: > 0 } warmWe &&
            warmWe.Length == featureCount &&
            warmWe[0].Length == embedDim &&
            warmStart.FtTransformerWq is { Length: > 0 };

        if (hasFullWarmStart)
        {
            LoadWarmStartWeights(model, warmStart!, featureCount, embedDim, ffnDim, rng, _logger);
            _logger.LogDebug("FT-Transformer warm-start: loaded weights from parent model (generation={Gen}).",
                warmStart!.GenerationNumber);
        }
        else
        {
            InitialiseWeights(model, featureCount, embedDim, ffnDim, rng);
        }

        // ── Validation split (10%) for early stopping ─────────────────────────
        int valSize  = Math.Max(20, train.Count / 10);
        var valSet   = train[^valSize..];
        var trainSet = train[..^valSize];

        if (trainSet.Count == 0) return model;

        // ── Mini-batch setup ────────────────────────────────────────────────
        int batchSize = hp.MiniBatchSize > 1 ? hp.MiniBatchSize : DefaultBatchSize;

        double labelSmoothing = hp.LabelSmoothing;
        double posLabel = 1.0 - labelSmoothing;
        double negLabel = labelSmoothing;
        double dropoutRate = hp.FtDropoutRate > 0.0 ? hp.FtDropoutRate : DefaultDropoutRate;

        double bestValLoss = double.MaxValue;
        int patience = 0;
        int nanReversions = 0;
        const int MaxNanReversions = 3;
        double lrScale = 1.0;

        var bestModel = CloneModelWithPosBias(model);
        double l2 = hp.L2Lambda;

        // Shuffled index array for epoch-level randomisation
        var indices = new int[trainSet.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        // ── Gradient accumulator ────────────────────────────────────────────
        var grad = new TransformerGrad(featureCount, embedDim, ffnDim, numLayers);
        if (model.UsePositionalBias)
        {
            int S = featureCount + 1;
            for (int l = 0; l < numLayers; l++)
            {
                grad.LayerGrads[l].dPosBias = new double[numHeads][];
                for (int h = 0; h < numHeads; h++)
                    grad.LayerGrads[l].dPosBias[h] = new double[S * S];
            }
        }

        // ── Adam state ──────────────────────────────────────────────────────
        var adam = new AdamState(featureCount, embedDim, ffnDim, numLayers);
        if (model.UsePositionalBias)
        {
            int S = featureCount + 1;
            for (int l = 0; l < numLayers; l++)
            {
                adam.LayerStates[l].mPosBias = new double[numHeads][];
                adam.LayerStates[l].vPosBias = new double[numHeads][];
                for (int h = 0; h < numHeads; h++)
                {
                    adam.LayerStates[l].mPosBias[h] = new double[S * S];
                    adam.LayerStates[l].vPosBias[h] = new double[S * S];
                }
            }
        }

        // ── Forward/backward pass buffers ───────────────────────────────────
        var fwdBuf = new ForwardBuffers(featureCount, embedDim, numHeads, ffnDim, numLayers);
        var valBuf = new InferenceBuffers(featureCount, embedDim, numHeads, ffnDim);

        int warmupEpochs = hp.FtWarmupEpochs;

        for (int epoch = 0; epoch < hp.MaxEpochs && !ct.IsCancellationRequested; epoch++)
            {
                ct.ThrowIfCancellationRequested();

                // Linear warmup then cosine annealing
                double alpha;
                if (warmupEpochs > 0 && epoch < warmupEpochs)
                    alpha = hp.LearningRate * lrScale * ((epoch + 1.0) / warmupEpochs);
                else
                {
                    int cosineEpoch = epoch - warmupEpochs;
                    int cosineTotal = Math.Max(1, hp.MaxEpochs - warmupEpochs);
                    alpha = hp.LearningRate * lrScale * 0.5 *
                        (1.0 + Math.Cos(Math.PI * cosineEpoch / cosineTotal));
                }

                // Fisher-Yates shuffle of training indices each epoch
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (indices[i], indices[j]) = (indices[j], indices[i]);
                }

                int numBatches = (trainSet.Count + batchSize - 1) / batchSize;
                for (int batch = 0; batch < numBatches; batch++)
                {
                    adam.Step++;
                    adam.Beta1t *= AdamBeta1;
                    adam.Beta2t *= AdamBeta2;

                    int bStart = batch * batchSize;
                    int bEnd   = Math.Min(bStart + batchSize, trainSet.Count);
                    int bCount = bEnd - bStart;

                    // Zero gradients
                    grad.Zero();

                    // Accumulate gradients over mini-batch (using shuffled indices)
                    for (int bi = bStart; bi < bEnd; bi++)
                    {
                        int idx = indices[bi];
                        float[] xRaw = trainSet[idx].Features;
                        double  y    = trainSet[idx].Direction > 0 ? posLabel : negLabel;

                        double p = ForwardPassTraining(xRaw, model, featureCount, fwdBuf, rng, dropoutRate);
                        if (!double.IsFinite(p)) continue;

                        double err = p - y;
                        BackwardPass(err, model, featureCount, fwdBuf, xRaw, grad, dropoutRate);
                    }

                    // Average gradients
                    grad.Scale(1.0 / bCount);

                    // ── Gradient norm clipping ────────────────────────────────
                    if (hp.MaxGradNorm > 0.0)
                        grad.ClipNorm(hp.MaxGradNorm);

                    // ── Adam updates ──────────────────────────────────────────
                    double bc1    = 1.0 - adam.Beta1t;
                    double bc2    = 1.0 - adam.Beta2t;
                    double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                    double effectiveWd = l2;
                    if (warmupEpochs > 0 && epoch < warmupEpochs)
                        effectiveWd = l2 * ((epoch + 1.0) / warmupEpochs);

                    ApplyAdamUpdates(model, grad, adam, alphAt, featureCount, embedDim, ffnDim, effectiveWd);

                    // PosBias AdamW update (conditionally allocated, outside ApplyAdamUpdates)
                    if (model.UsePositionalBias)
                    {
                        for (int l = 0; l < model.NumLayers; l++)
                        {
                            var L  = model.Layers[l];
                            var lg = grad.LayerGrads[l];
                            var la = adam.LayerStates[l];
                            if (L.PosBias is not null && lg.dPosBias is not null && la.mPosBias is not null)
                                AdamWUpdate2D(L.PosBias, lg.dPosBias, la.mPosBias, la.vPosBias!,
                                    alphAt, model.NumHeads, model.SeqLen * model.SeqLen, effectiveWd);
                        }
                    }

                    // ── NaN/Inf guard with backoff ────────────────────────────
                    bool hasNan = !double.IsFinite(model.BOut) || HasNonFinite(model);
                    if (!hasNan && model.UsePositionalBias)
                        for (int l = 0; l < model.NumLayers && !hasNan; l++)
                            if (model.Layers[l].PosBias is not null)
                                for (int h = 0; h < model.NumHeads && !hasNan; h++)
                                    if (HasNonFiniteArray(model.Layers[l].PosBias![h])) hasNan = true;
                    if (hasNan)
                    {
                        CopyModel(bestModel, model);
                        CopyPosBias(bestModel, model);
                        nanReversions++;
                        _logger.LogWarning(
                            "NaN at epoch {Epoch}, batch {Batch} — reverting to checkpoint (reversion {N}/{Max}).",
                            epoch, batch, nanReversions, MaxNanReversions);

                        if (nanReversions >= MaxNanReversions)
                        {
                            _logger.LogWarning("Max NaN reversions reached — stopping training early.");
                            goto EndTraining;
                        }

                        // Halve the effective LR to reduce explosion risk
                        lrScale *= 0.5;
                        goto EndEpochLoop;
                    }

                    if (hp.MaxWeightMagnitude > 0.0)
                    {
                        ClipWeights(model, hp.MaxWeightMagnitude);
                        if (model.UsePositionalBias)
                            for (int l = 0; l < model.NumLayers; l++)
                                if (model.Layers[l].PosBias is not null)
                                    for (int h = 0; h < model.NumHeads; h++)
                                        ClipArray(model.Layers[l].PosBias![h], hp.MaxWeightMagnitude);
                    }
                }

                // ── Early stopping ───────────────────────────────────────────
                double valLoss = ComputeLogLoss(valSet, model, featureCount, labelSmoothing, valBuf);
                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    CopyModel(model, bestModel);
                    CopyPosBias(model, bestModel);
                    patience = 0;
                }
                else if (++patience >= hp.EarlyStoppingPatience)
                {
                    _logger.LogDebug("Early stopping at epoch {Epoch} (patience={Pat})", epoch, patience);
                    break;
                }

                EndEpochLoop:;
            }
            EndTraining:

        CopyModel(bestModel, model);
        CopyPosBias(bestModel, model);
        return model;
    }

    // ── Weight initialisation ─────────────────────────────────────────────────

    private static void InitialiseWeights(
        TransformerModel model, int F, int embedDim, int ffnDim, Random rng)
    {
        double xavierEmbed = Math.Sqrt(2.0 / (1 + embedDim));
        for (int f = 0; f < F; f++)
        {
            model.We[f] = new double[embedDim];
            model.Be[f] = new double[embedDim];
            for (int d = 0; d < embedDim; d++)
                model.We[f][d] = SampleGaussian(rng, xavierEmbed);
        }

        // [CLS] token: small random init
        for (int d = 0; d < embedDim; d++)
            model.ClsToken[d] = SampleGaussian(rng, 0.02);

        for (int l = 0; l < model.NumLayers; l++)
            InitialiseLayerWeights(model.Layers[l], embedDim, ffnDim, rng);

        if (model.UsePositionalBias)
            for (int l = 0; l < model.NumLayers; l++)
                if (model.Layers[l].PosBias is not null)
                    for (int h = 0; h < model.NumHeads; h++)
                        for (int i = 0; i < model.Layers[l].PosBias![h].Length; i++)
                            model.Layers[l].PosBias[h][i] = SampleGaussian(rng, 0.02);

        // Final LayerNorm (pre-norm output)
        Array.Fill(model.GammaFinal, 1.0);
        Array.Fill(model.BetaFinal, 0.0);

        // Classifier head
        double xavierOut = Math.Sqrt(2.0 / (embedDim + 1));
        for (int d = 0; d < embedDim; d++)
            model.WOut[d] = SampleGaussian(rng, xavierOut);
        model.BOut = 0.0;
    }

    private static void InitialiseLayerWeights(
        TransformerLayer layer, int embedDim, int ffnDim, Random rng)
    {
        double xavierAttn = Math.Sqrt(2.0 / (embedDim + embedDim));
        for (int d = 0; d < embedDim; d++)
        {
            layer.Wq[d] = InitRow(rng, embedDim, xavierAttn);
            layer.Wk[d] = InitRow(rng, embedDim, xavierAttn);
            layer.Wv[d] = InitRow(rng, embedDim, xavierAttn);
            layer.Wo[d] = InitRow(rng, embedDim, xavierAttn);
        }

        Array.Fill(layer.Gamma1, 1.0);
        Array.Fill(layer.Beta1, 0.0);

        double xavierFfn1 = Math.Sqrt(2.0 / (embedDim + ffnDim));
        for (int d = 0; d < embedDim; d++)
            layer.Wff1[d] = InitRow(rng, ffnDim, xavierFfn1);
        Array.Fill(layer.Bff1, 0.0);

        double xavierFfn2 = Math.Sqrt(2.0 / (ffnDim + embedDim));
        for (int d = 0; d < ffnDim; d++)
            layer.Wff2[d] = InitRow(rng, embedDim, xavierFfn2);
        Array.Fill(layer.Bff2, 0.0);

        Array.Fill(layer.Gamma2, 1.0);
        Array.Fill(layer.Beta2, 0.0);
    }

    private static double[] InitRow(Random rng, int size, double std)
    {
        var row = new double[size];
        for (int i = 0; i < size; i++)
            row[i] = SampleGaussian(rng, std);
        return row;
    }

    private static void LoadWarmStartWeights(
        TransformerModel model, ModelSnapshot ws, int F, int embedDim, int ffnDim, Random rng,
        ILogger? logger = null)
    {
        // Embeddings
        for (int f = 0; f < F; f++)
        {
            var warmRow = ws.FtTransformerEmbedWeights![f];
            model.We[f] = warmRow.Length == embedDim && HasFiniteArray(warmRow)
                ? [..warmRow]
                : InitRow(rng, embedDim, Math.Sqrt(2.0 / (1 + embedDim)));
        }

        if (ws.FtTransformerEmbedBiases is { Length: > 0 } warmBe && warmBe.Length == F && warmBe[0].Length == embedDim)
            for (int f = 0; f < F; f++)
                model.Be[f] = HasFiniteArray(warmBe[f]) ? [..warmBe[f]] : new double[embedDim];
        else
            for (int f = 0; f < F; f++) model.Be[f] = new double[embedDim];

        // [CLS] token
        if (ws.FtTransformerClsToken is { Length: > 0 } warmCls
            && warmCls.Length == embedDim
            && HasFiniteArray(warmCls))
            Array.Copy(warmCls, model.ClsToken, embedDim);
        else
            for (int d = 0; d < embedDim; d++) model.ClsToken[d] = SampleGaussian(rng, 0.02);

        // Layer 0 from explicit snapshot fields
        var L0 = model.Layers[0];
        double xavierAttn = Math.Sqrt(2.0 / (embedDim + embedDim));
        LoadMatrix(L0.Wq, ws.FtTransformerWq, embedDim, embedDim, rng, xavierAttn);
        LoadMatrix(L0.Wk, ws.FtTransformerWk, embedDim, embedDim, rng, xavierAttn);
        LoadMatrix(L0.Wv, ws.FtTransformerWv, embedDim, embedDim, rng, xavierAttn);
        LoadMatrix(L0.Wo, ws.FtTransformerWo, embedDim, embedDim, rng, xavierAttn);

        LoadVector(L0.Gamma1, ws.FtTransformerGamma1, embedDim, 1.0);
        LoadVector(L0.Beta1,  ws.FtTransformerBeta1,  embedDim, 0.0);

        double xavierFfn1 = Math.Sqrt(2.0 / (embedDim + ffnDim));
        LoadMatrix(L0.Wff1, ws.FtTransformerWff1, embedDim, ffnDim, rng, xavierFfn1);
        LoadVector(L0.Bff1, ws.FtTransformerBff1, ffnDim, 0.0);

        double xavierFfn2 = Math.Sqrt(2.0 / (ffnDim + embedDim));
        LoadMatrix(L0.Wff2, ws.FtTransformerWff2, ffnDim, embedDim, rng, xavierFfn2);
        LoadVector(L0.Bff2, ws.FtTransformerBff2, embedDim, 0.0);

        LoadVector(L0.Gamma2, ws.FtTransformerGamma2, embedDim, 1.0);
        LoadVector(L0.Beta2,  ws.FtTransformerBeta2,  embedDim, 0.0);

        // Layers 1..N-1: try binary first, fall back to JSON
        if (model.NumLayers > 1)
        {
            List<SerializedLayerWeights>? additionalLayers = null;
            if (ws.FtTransformerAdditionalLayersBytes is { Length: > 4 })
            {
                try { additionalLayers = DeserializeAdditionalLayers(ws.FtTransformerAdditionalLayersBytes, embedDim, ffnDim); }
                catch { /* fall through to JSON */ }
            }
            if (additionalLayers is null && ws.FtTransformerAdditionalLayersJson is { Length: > 0 })
            {
                try
                {
                    additionalLayers = JsonSerializer.Deserialize<List<SerializedLayerWeights>>(
                        ws.FtTransformerAdditionalLayersJson, JsonOpts);
                }
                catch (JsonException ex)
                {
                    logger?.LogDebug(ex, "Failed to deserialise additional layer weights from warm-start — layers 1..N will use fresh init.");
                }
            }
            if (additionalLayers is not null)
            {
                for (int l = 0; l < Math.Min(additionalLayers.Count, model.NumLayers - 1); l++)
                {
                    var sl = additionalLayers[l];
                    var tl = model.Layers[l + 1];
                    LoadMatrix(tl.Wq, sl.Wq, embedDim, embedDim, rng, xavierAttn);
                    LoadMatrix(tl.Wk, sl.Wk, embedDim, embedDim, rng, xavierAttn);
                    LoadMatrix(tl.Wv, sl.Wv, embedDim, embedDim, rng, xavierAttn);
                    LoadMatrix(tl.Wo, sl.Wo, embedDim, embedDim, rng, xavierAttn);
                    LoadVector(tl.Gamma1, sl.Gamma1, embedDim, 1.0);
                    LoadVector(tl.Beta1,  sl.Beta1,  embedDim, 0.0);
                    LoadMatrix(tl.Wff1, sl.Wff1, embedDim, ffnDim, rng, xavierFfn1);
                    LoadVector(tl.Bff1, sl.Bff1, ffnDim, 0.0);
                    LoadMatrix(tl.Wff2, sl.Wff2, ffnDim, embedDim, rng, xavierFfn2);
                    LoadVector(tl.Bff2, sl.Bff2, embedDim, 0.0);
                    LoadVector(tl.Gamma2, sl.Gamma2, embedDim, 1.0);
                    LoadVector(tl.Beta2,  sl.Beta2,  embedDim, 0.0);
                }
            }
        }

        // Any remaining layers beyond what warm-start covers get fresh init
        int warmLayers = 1 + (ws.FtTransformerAdditionalLayersBytes is { Length: > 0 } || ws.FtTransformerAdditionalLayersJson is { Length: > 0 } ? ws.FtTransformerNumLayers - 1 : 0);
        for (int l = Math.Max(1, warmLayers); l < model.NumLayers; l++)
            InitialiseLayerWeights(model.Layers[l], embedDim, ffnDim, rng);

        // Final LayerNorm
        if (ws.FtTransformerGammaFinal is { Length: > 0 } warmGF
            && warmGF.Length == embedDim
            && HasFiniteArray(warmGF))
            Array.Copy(warmGF, model.GammaFinal, embedDim);
        else
            Array.Fill(model.GammaFinal, 1.0);

        if (ws.FtTransformerBetaFinal is { Length: > 0 } warmBF
            && warmBF.Length == embedDim
            && HasFiniteArray(warmBF))
            Array.Copy(warmBF, model.BetaFinal, embedDim);
        else
            Array.Fill(model.BetaFinal, 0.0);

        if (ws.FtTransformerOutputWeights is { Length: > 0 } warmOut
            && warmOut.Length == embedDim
            && HasFiniteArray(warmOut))
        {
            model.WOut = [..warmOut];
            model.BOut = double.IsFinite(ws.FtTransformerOutputBias) ? ws.FtTransformerOutputBias : 0.0;
        }
        else
        {
            double xavierOut = Math.Sqrt(2.0 / (embedDim + 1));
            for (int d = 0; d < embedDim; d++)
                model.WOut[d] = SampleGaussian(rng, xavierOut);
        }
    }

    // SerializedLayerWeights → FtTransformerModelTrainer.Types.cs

    private static void LoadMatrix(double[][] dst, double[][]? src, int rows, int cols, Random rng, double std)
    {
        if (src is not { Length: > 0 } || src.Length != rows)
        {
            for (int r = 0; r < rows; r++) dst[r] = InitRow(rng, cols, std);
            return;
        }

        for (int r = 0; r < rows; r++)
        {
            if (src[r].Length == cols && HasFiniteArray(src[r]))
                dst[r] = [..src[r]];
            else
                dst[r] = InitRow(rng, cols, std);
        }
    }

    private static void LoadVector(double[] dst, double[]? src, int len, double fill)
    {
        if (src is { Length: > 0 } && src.Length == len && HasFiniteArray(src))
            Array.Copy(src, dst, len);
        else
            Array.Fill(dst, fill);
    }

    private static bool HasFiniteArray(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
            if (!double.IsFinite(values[i]))
                return false;

        return true;
    }

    // ── Build pruned warm-start from already-trained full model ──────────────

    /// <summary>
    /// Creates a ModelSnapshot containing only the weights that can be transferred
    /// from a full-feature model to a pruned-feature model. Transformer layer weights
    /// (Wq, Wk, Wv, Wo, FFN, LN) are feature-count-independent and transfer directly.
    /// Embedding weights are extracted only for active features.
    /// </summary>
    private static ModelSnapshot BuildPrunedWarmStart(
        TransformerModel fullModel, bool[] activeMask, int activeF)
    {
        // Extract active features' embedding weights
        var prunedWe = new double[activeF][];
        var prunedBe = new double[activeF][];
        int idx = 0;
        for (int f = 0; f < fullModel.F; f++)
        {
            if (!activeMask[f]) continue;
            prunedWe[idx] = [..fullModel.We[f]];
            prunedBe[idx] = [..fullModel.Be[f]];
            idx++;
        }

        // Build additional layers JSON from the full model
        string? additionalLayersJson = null;
        if (fullModel.NumLayers > 1)
        {
            additionalLayersJson = JsonSerializer.Serialize(
                Enumerable.Range(1, fullModel.NumLayers - 1).Select(l => new SerializedLayerWeights
                {
                    Wq = fullModel.Layers[l].Wq, Wk = fullModel.Layers[l].Wk,
                    Wv = fullModel.Layers[l].Wv, Wo = fullModel.Layers[l].Wo,
                    Gamma1 = fullModel.Layers[l].Gamma1, Beta1 = fullModel.Layers[l].Beta1,
                    Wff1 = fullModel.Layers[l].Wff1, Bff1 = fullModel.Layers[l].Bff1,
                    Wff2 = fullModel.Layers[l].Wff2, Bff2 = fullModel.Layers[l].Bff2,
                    Gamma2 = fullModel.Layers[l].Gamma2, Beta2 = fullModel.Layers[l].Beta2,
                }).ToList(), JsonOpts);
        }

        return new ModelSnapshot
        {
            FtTransformerEmbedWeights         = prunedWe,
            FtTransformerEmbedBiases          = prunedBe,
            FtTransformerWq                   = fullModel.Layers[0].Wq,
            FtTransformerWk                   = fullModel.Layers[0].Wk,
            FtTransformerWv                   = fullModel.Layers[0].Wv,
            FtTransformerWo                   = fullModel.Layers[0].Wo,
            FtTransformerGamma1               = fullModel.Layers[0].Gamma1,
            FtTransformerBeta1                = fullModel.Layers[0].Beta1,
            FtTransformerWff1                 = fullModel.Layers[0].Wff1,
            FtTransformerBff1                 = fullModel.Layers[0].Bff1,
            FtTransformerWff2                 = fullModel.Layers[0].Wff2,
            FtTransformerBff2                 = fullModel.Layers[0].Bff2,
            FtTransformerGamma2               = fullModel.Layers[0].Gamma2,
            FtTransformerBeta2                = fullModel.Layers[0].Beta2,
            FtTransformerOutputWeights        = [..fullModel.WOut],
            FtTransformerOutputBias           = fullModel.BOut,
            FtTransformerClsToken             = [..fullModel.ClsToken],
            FtTransformerGammaFinal           = [..fullModel.GammaFinal],
            FtTransformerBetaFinal            = [..fullModel.BetaFinal],
            FtTransformerEmbedDim             = fullModel.EmbedDim,
            FtTransformerNumHeads             = fullModel.NumHeads,
            FtTransformerFfnDim               = fullModel.FfnDim,
            FtTransformerNumLayers            = fullModel.NumLayers,
            FtTransformerAdditionalLayersJson = additionalLayersJson,
        };
    }

    // LayerForwardCache, ForwardBuffers → FtTransformerModelTrainer.Types.cs

    // ── Forward pass (inference, no dropout, pre-norm with [CLS]) ────────────

    private static double ForwardPass(
        float[] xRaw, TransformerModel model, int F, InferenceBuffers buf)
    {
        int D  = model.EmbedDim;
        int H  = model.NumHeads;
        int Dh = model.HeadDim;
        int Ff = model.FfnDim;
        int S  = F + 1; // [CLS] + F feature tokens

        // 1. Place [CLS] token at position 0
        Array.Copy(model.ClsToken, buf.E[0], D);

        // 2. Feature embedding at positions 1..S-1: e_f = We[f] * x_f + Be[f]
        for (int f = 0; f < F; f++)
        {
            double xf = xRaw[f];
            for (int d = 0; d < D; d++)
                buf.E[f + 1][d] = model.We[f][d] * xf + model.Be[f][d];
        }

        // 3. Process each transformer layer (pre-norm)
        for (int layer = 0; layer < model.NumLayers; layer++)
        {
            var L = model.Layers[layer];
            ForwardLayer(buf.E, L, S, D, H, Dh, Ff, buf);
            // Copy Res2 → E for the next layer's input
            for (int i = 0; i < S; i++)
                Array.Copy(buf.Res2[i], buf.E[i], D);
        }

        // 4. Final LayerNorm on [CLS] position (position 0) only
        LayerNormForward(buf.E[0], model.GammaFinal, model.BetaFinal, buf.FinalLn, D);

        // 5. Classifier head reads from [CLS] output
        double logit = model.BOut;
        for (int d = 0; d < D; d++)
            logit += model.WOut[d] * buf.FinalLn[d];

        return MLFeatureHelper.Sigmoid(logit);
    }

    /// <summary>Runs one transformer layer's forward pass (inference, no dropout, pre-norm). Writes to buf.Res2.</summary>
    private static void ForwardLayer(
        double[][] input, TransformerLayer L, int S, int D, int H, int Dh, int Ff,
        InferenceBuffers buf)
    {
        // Pre-norm: LN1 before attention
        for (int i = 0; i < S; i++)
            LayerNormForward(input[i], L.Gamma1, L.Beta1, buf.LnIn[i], D);

        // Q, K, V projections from LN output
        MatMul(buf.LnIn, L.Wq, buf.Q, S, D, D);
        MatMul(buf.LnIn, L.Wk, buf.K, S, D, D);
        MatMul(buf.LnIn, L.Wv, buf.V, S, D, D);

        // Multi-head scaled dot-product attention
        double sqrtDh = Math.Sqrt(Dh);
        for (int h = 0; h < H; h++)
        {
            int hOff = h * Dh;

            for (int r = 0; r < S; r++)
                for (int c = 0; c < S; c++)
                {
                    double dot = 0;
                    for (int d = 0; d < Dh; d++)
                        dot += buf.Q[r][hOff + d] * buf.K[c][hOff + d];
                    buf.Scores[h][r * S + c] = dot / sqrtDh;
                    if (L.PosBias is not null && h < L.PosBias.Length)
                        buf.Scores[h][r * S + c] += L.PosBias[h][r * S + c];
                }

            for (int r = 0; r < S; r++)
            {
                int rowOff = r * S;
                double max = double.MinValue;
                for (int c = 0; c < S; c++)
                    if (buf.Scores[h][rowOff + c] > max) max = buf.Scores[h][rowOff + c];
                double sum = 0;
                for (int c = 0; c < S; c++)
                {
                    buf.AttnW[h][rowOff + c] = Math.Exp(buf.Scores[h][rowOff + c] - max);
                    sum += buf.AttnW[h][rowOff + c];
                }
                sum += 1e-10;
                for (int c = 0; c < S; c++)
                    buf.AttnW[h][rowOff + c] /= sum;
            }

            for (int r = 0; r < S; r++)
            {
                int rowOff = r * S;
                for (int d = 0; d < Dh; d++)
                {
                    double s = 0;
                    for (int c = 0; c < S; c++)
                        s += buf.AttnW[h][rowOff + c] * buf.V[c][hOff + d];
                    buf.AttnOut[r][hOff + d] = s;
                }
            }
        }

        // Output projection Wo + residual (from input, NOT from LN output)
        for (int i = 0; i < S; i++)
        {
            for (int d = 0; d < D; d++)
            {
                double s = 0;
                for (int k = 0; k < D; k++)
                    s += buf.AttnOut[i][k] * L.Wo[k][d];
                buf.Res1[i][d] = s + input[i][d]; // residual from input
            }
        }

        // Pre-norm: LN2 before FFN
        for (int i = 0; i < S; i++)
            LayerNormForward(buf.Res1[i], L.Gamma2, L.Beta2, buf.LnIn2[i], D);

        // FFN: Linear → GELU → Linear + residual from Res1
        for (int i = 0; i < S; i++)
        {
            for (int h = 0; h < Ff; h++)
            {
                double s = L.Bff1[h];
                for (int d = 0; d < D; d++)
                    s += buf.LnIn2[i][d] * L.Wff1[d][h];
                buf.FfnH[i][h] = GELU(s);
            }

            for (int d = 0; d < D; d++)
            {
                double s = L.Bff2[d];
                for (int h = 0; h < Ff; h++)
                    s += buf.FfnH[i][h] * L.Wff2[h][d];
                buf.Res2[i][d] = s + buf.Res1[i][d]; // residual from Res1
            }
        }
    }

    // ── Forward pass (training, with dropout + cached intermediates, pre-norm with [CLS]) ─────────

    private static double ForwardPassTraining(
        float[] xRaw, TransformerModel model, int F, ForwardBuffers buf,
        Random rng, double dropoutRate)
    {
        int D  = model.EmbedDim;
        int H  = model.NumHeads;
        int Dh = model.HeadDim;
        int Ff = model.FfnDim;
        int S  = F + 1; // [CLS] + F feature tokens
        double dropScale = dropoutRate > 0.0 ? 1.0 / (1.0 - dropoutRate) : 1.0;

        // 1. Place [CLS] token at position 0
        Array.Copy(model.ClsToken, buf.E[0], D);

        // 2. Feature embedding at positions 1..S-1
        for (int f = 0; f < F; f++)
        {
            double xf = xRaw[f];
            for (int d = 0; d < D; d++)
                buf.E[f + 1][d] = model.We[f][d] * xf + model.Be[f][d];
        }

        // 3. Process each transformer layer (pre-norm)
        for (int layer = 0; layer < model.NumLayers; layer++)
        {
            var L  = model.Layers[layer];
            var lc = buf.LayerCaches[layer];

            // Snapshot input for this layer (needed by backprop)
            for (int i = 0; i < S; i++)
                Array.Copy(buf.E[i], lc.Input[i], D);

            // Pre-norm: LN1 before attention
            for (int i = 0; i < S; i++)
                LayerNormForwardCached(
                    lc.Input[i], L.Gamma1, L.Beta1, lc.LnIn[i],
                    D, ref lc.Ln1Mean[i], ref lc.Ln1InvStd[i], lc.Ln1Norm[i]);

            // Q, K, V projections from LN1 output
            MatMul(lc.LnIn, L.Wq, lc.Q, S, D, D);
            MatMul(lc.LnIn, L.Wk, lc.K, S, D, D);
            MatMul(lc.LnIn, L.Wv, lc.V, S, D, D);

            // Multi-head attention with dropout
            double sqrtDh = Math.Sqrt(Dh);
            for (int h = 0; h < H; h++)
            {
                int hOff = h * Dh;

                for (int r = 0; r < S; r++)
                    for (int c = 0; c < S; c++)
                    {
                        double dot = 0;
                        for (int d = 0; d < Dh; d++)
                            dot += lc.Q[r][hOff + d] * lc.K[c][hOff + d];
                        lc.HeadScores[h][r][c] = dot / sqrtDh;
                        if (L.PosBias is not null && h < L.PosBias.Length)
                            lc.HeadScores[h][r][c] += L.PosBias[h][r * S + c];
                    }

                for (int r = 0; r < S; r++)
                {
                    double max = double.MinValue;
                    for (int c = 0; c < S; c++)
                        if (lc.HeadScores[h][r][c] > max) max = lc.HeadScores[h][r][c];
                    double sum = 0;
                    for (int c = 0; c < S; c++)
                    {
                        lc.HeadAttnW[h][r][c] = Math.Exp(lc.HeadScores[h][r][c] - max);
                        sum += lc.HeadAttnW[h][r][c];
                    }
                    sum += 1e-10;
                    for (int c = 0; c < S; c++)
                        lc.HeadAttnW[h][r][c] /= sum;
                }

                // Cache pre-dropout softmax for backward pass
                for (int r = 0; r < S; r++)
                    Array.Copy(lc.HeadAttnW[h][r], lc.PreDropAttnW[h][r], S);

                // Attention dropout with inverted scaling (consistent with FFN dropout)
                if (dropoutRate > 0.0)
                {
                    for (int r = 0; r < S; r++)
                        for (int c = 0; c < S; c++)
                        {
                            bool keep = rng.NextDouble() >= dropoutRate;
                            lc.AttnDropMask[h][r * S + c] = keep;
                            lc.HeadAttnW[h][r][c] = keep ? lc.HeadAttnW[h][r][c] * dropScale : 0.0;
                        }
                }
                else
                {
                    for (int r = 0; r < S; r++)
                        for (int c = 0; c < S; c++)
                            lc.AttnDropMask[h][r * S + c] = true;
                }

                for (int r = 0; r < S; r++)
                    for (int d = 0; d < Dh; d++)
                    {
                        double s = 0;
                        for (int c = 0; c < S; c++)
                            s += lc.HeadAttnW[h][r][c] * lc.V[c][hOff + d];
                        lc.AttnOut[r][hOff + d] = s;
                    }
            }

            // Output projection Wo + residual from Input (not LN output)
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                {
                    double s = 0;
                    for (int k = 0; k < D; k++)
                        s += lc.AttnOut[i][k] * L.Wo[k][d];
                    lc.Res1[i][d] = s + lc.Input[i][d]; // residual from input
                }

            // Pre-norm: LN2 before FFN
            for (int i = 0; i < S; i++)
                LayerNormForwardCached(
                    lc.Res1[i], L.Gamma2, L.Beta2, lc.LnIn2[i],
                    D, ref lc.Ln2Mean[i], ref lc.Ln2InvStd[i], lc.Ln2Norm[i]);

            // FFN with dropout + residual from Res1
            for (int i = 0; i < S; i++)
            {
                for (int h = 0; h < Ff; h++)
                {
                    double s = L.Bff1[h];
                    for (int d = 0; d < D; d++)
                        s += lc.LnIn2[i][d] * L.Wff1[d][h];
                    lc.FfnHPreAct[i][h] = s;
                    double act = GELU(s);

                    // FFN dropout uses inverted dropout (correct for FFN)
                    if (dropoutRate > 0.0)
                    {
                        bool keep = rng.NextDouble() >= dropoutRate;
                        lc.FfnDropMask[i][h] = keep;
                        lc.FfnH[i][h] = keep ? act * dropScale : 0.0;
                    }
                    else
                    {
                        lc.FfnDropMask[i][h] = true;
                        lc.FfnH[i][h] = act;
                    }
                }

                for (int d = 0; d < D; d++)
                {
                    double s = L.Bff2[d];
                    for (int h = 0; h < Ff; h++)
                        s += lc.FfnH[i][h] * L.Wff2[h][d];
                    lc.Res2[i][d] = s + lc.Res1[i][d]; // residual from Res1
                }
            }

            // Copy output → E for next layer
            for (int i = 0; i < S; i++)
                Array.Copy(lc.Res2[i], buf.E[i], D);
        }

        // 4. Final LayerNorm on [CLS] position (position 0) only
        var lastCache = buf.LayerCaches[model.NumLayers - 1];
        LayerNormForwardCached(
            buf.E[0], model.GammaFinal, model.BetaFinal, lastCache.FinalLnOut,
            D, ref lastCache.FinalLnMean, ref lastCache.FinalLnInvStd, lastCache.FinalLnNorm);

        // 5. Classifier head reads from [CLS]
        double logit = model.BOut;
        for (int d = 0; d < D; d++)
            logit += model.WOut[d] * lastCache.FinalLnOut[d];

        return MLFeatureHelper.Sigmoid(logit);
    }

    // ── Backward pass (full gradient computation, pre-norm with [CLS]) ───────

    private static void BackwardPass(
        double err, TransformerModel model, int F,
        ForwardBuffers buf, float[] xRaw, TransformerGrad grad,
        double dropoutRate = 0.0)
    {
        int D  = model.EmbedDim;
        int H  = model.NumHeads;
        int Dh = model.HeadDim;
        double dropScale = dropoutRate > 0.0 ? 1.0 / (1.0 - dropoutRate) : 1.0;
        int Ff = model.FfnDim;
        int S  = F + 1;

        var lastCache = buf.LayerCaches[model.NumLayers - 1];

        // ── Classifier head ─────────────────────────────────────────────
        grad.dBOut += err;
        for (int d = 0; d < D; d++)
            grad.dWOut[d] += err * lastCache.FinalLnOut[d];

        // dFinalLn[d] = err * WOut[d]
        var dFinalLn = grad.Scratch1;
        for (int d = 0; d < D; d++)
            dFinalLn[d] = err * model.WOut[d];

        // Final LayerNorm backward (on [CLS] position 0 only)
        var dClsFromFinalLn = grad.ScratchD;
        LayerNormBackward(dFinalLn, lastCache.FinalLnNorm, model.GammaFinal,
            lastCache.FinalLnInvStd, D, dClsFromFinalLn,
            grad.dGammaFinal, grad.dBetaFinal);

        // dInput: gradient flowing into the last layer's output
        // Only [CLS] position (0) receives gradient from the classifier head
        var dInput = grad.dInput;
        for (int i = 0; i < S; i++)
            Array.Clear(dInput[i], 0, D);
        Array.Copy(dClsFromFinalLn, dInput[0], D);

        // ── Backward through layers (reverse order) ─────────────────────
        for (int layer = model.NumLayers - 1; layer >= 0; layer--)
        {
            var L  = model.Layers[layer];
            var lc = buf.LayerCaches[layer];
            var lg = grad.LayerGrads[layer];

            // Pre-norm backward: Res2 = FFN_out + Res1
            // dInput flows into both FFN_out and Res1 (residual)

            // FFN backward
            var dRes1 = grad.dRes1;

            // FFN backward: weight gradients and dLnIn2 in a single pass
            var dLnIn2 = grad.dLnIn2;
            for (int i = 0; i < S; i++)
            {
                Array.Clear(dLnIn2[i], 0, D);

                for (int d = 0; d < D; d++)
                    lg.dBff2[d] += dInput[i][d];

                for (int h = 0; h < Ff; h++)
                {
                    double dh = 0;
                    for (int d = 0; d < D; d++)
                    {
                        dh += dInput[i][d] * L.Wff2[h][d];
                        lg.dWff2[h][d] += dInput[i][d] * lc.FfnH[i][h];
                    }
                    double dhDropped = lc.FfnDropMask[i][h] ? dh : 0.0;
                    double dPreAct = dhDropped * GELUGrad(lc.FfnHPreAct[i][h]);

                    for (int d = 0; d < D; d++)
                    {
                        lg.dWff1[d][h] += dPreAct * lc.LnIn2[i][d];
                        dLnIn2[i][d] += dPreAct * L.Wff1[d][h];
                    }
                    lg.dBff1[h] += dPreAct;
                }
            }

            // LN2 backward: dLnIn2 → dRes1 (accumulate, not replace)
            var dRes1FromLn2 = grad.dRes1FromLn2;
            for (int i = 0; i < S; i++)
                LayerNormBackward(dLnIn2[i], lc.Ln2Norm[i], L.Gamma2,
                    lc.Ln2InvStd[i], D, dRes1FromLn2[i],
                    lg.dGamma2, lg.dBeta2);

            // dRes1 = skip from Res2 + gradient through LN2
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dRes1[i][d] = dInput[i][d] + dRes1FromLn2[i][d];

            // Res1 = Wo @ AttnOut + Input, so dInput flows to both
            // dE = gradient flowing to this layer's input (from residual of Res1)
            var dE = grad.dE;
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dE[i][d] = dRes1[i][d]; // residual path

            // Wo backward: dRes1 → dAttnOut
            var dAttnOut = grad.dAttnOut;
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                {
                    double s = 0;
                    for (int k = 0; k < D; k++)
                    {
                        s += dRes1[i][k] * L.Wo[d][k];
                        lg.dWo[d][k] += dRes1[i][k] * lc.AttnOut[i][d];
                    }
                    dAttnOut[i][d] = s;
                }

            // Multi-head attention backward
            var dQ = grad.dQ;
            var dK = grad.dK;
            var dV = grad.dV;
            for (int i = 0; i < S; i++)
            {
                Array.Clear(dQ[i], 0, D);
                Array.Clear(dK[i], 0, D);
                Array.Clear(dV[i], 0, D);
            }

            double sqrtDh = Math.Sqrt(Dh);
            for (int h = 0; h < H; h++)
            {
                int hOff = h * Dh;
                for (int r = 0; r < S; r++)
                {
                    for (int c = 0; c < S; c++)
                    {
                        double daw = 0;
                        for (int d = 0; d < Dh; d++)
                        {
                            daw += dAttnOut[r][hOff + d] * lc.V[c][hOff + d];
                            dV[c][hOff + d] += dAttnOut[r][hOff + d] * lc.HeadAttnW[h][r][c];
                        }
                        // Dropout backward: mask + inverted scaling
                        if (!lc.AttnDropMask[h][r * S + c])
                            daw = 0.0;
                        else if (dropScale != 1.0)
                            daw *= dropScale;
                        grad.dAttnWeightPerCol[c] = daw;
                    }

                    // Softmax backward uses PRE-dropout probabilities
                    double dotSum = 0;
                    for (int c = 0; c < S; c++)
                        dotSum += lc.PreDropAttnW[h][r][c] * grad.dAttnWeightPerCol[c];

                    for (int c = 0; c < S; c++)
                    {
                        double rawDScore = lc.PreDropAttnW[h][r][c] * (grad.dAttnWeightPerCol[c] - dotSum);
                        if (lg.dPosBias is not null && h < lg.dPosBias.Length)
                            lg.dPosBias[h][r * S + c] += rawDScore;
                        double dScore = rawDScore / sqrtDh;
                        for (int d = 0; d < Dh; d++)
                        {
                            dQ[r][hOff + d] += dScore * lc.K[c][hOff + d];
                            dK[c][hOff + d] += dScore * lc.Q[r][hOff + d];
                        }
                    }
                }
            }

            // Q, K, V projection backward → gradient w.r.t. LN1 output
            var dLnIn = grad.dLnIn;
            for (int i = 0; i < S; i++)
                Array.Clear(dLnIn[i], 0, D);

            for (int i = 0; i < S; i++)
            {
                for (int d1 = 0; d1 < D; d1++)
                    for (int d2 = 0; d2 < D; d2++)
                    {
                        lg.dWq[d1][d2] += dQ[i][d2] * lc.LnIn[i][d1];
                        lg.dWk[d1][d2] += dK[i][d2] * lc.LnIn[i][d1];
                        lg.dWv[d1][d2] += dV[i][d2] * lc.LnIn[i][d1];
                    }

                for (int d = 0; d < D; d++)
                {
                    double s = 0;
                    for (int k = 0; k < D; k++)
                        s += dQ[i][k] * L.Wq[d][k] + dK[i][k] * L.Wk[d][k] + dV[i][k] * L.Wv[d][k];
                    dLnIn[i][d] = s;
                }
            }

            // LN1 backward: dLnIn → gradient w.r.t. layer input
            var dInputFromLn1 = grad.dInputFromLn1;
            for (int i = 0; i < S; i++)
                LayerNormBackward(dLnIn[i], lc.Ln1Norm[i], L.Gamma1,
                    lc.Ln1InvStd[i], D, dInputFromLn1[i],
                    lg.dGamma1, lg.dBeta1);

            // dE = skip from Res1 + gradient through LN1
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dE[i][d] += dInputFromLn1[i][d];

            // dE is now the gradient flowing into this layer's input.
            // Copy it to dInput for the previous layer (or embedding backward).
            for (int i = 0; i < S; i++)
                Array.Copy(dE[i], dInput[i], D);
        }

        // ── [CLS] token backward (position 0) ──────────────────────────
        for (int d = 0; d < D; d++)
            grad.dClsToken[d] += dInput[0][d];

        // ── Embedding backward (positions 1..S-1) ──────────────────────
        for (int f = 0; f < F; f++)
            for (int d = 0; d < D; d++)
            {
                grad.dWe[f][d] += dInput[f + 1][d] * xRaw[f];
                grad.dBe[f][d] += dInput[f + 1][d];
            }
    }

    // LayerGrad → FtTransformerModelTrainer.Types.cs

    // TransformerGrad → FtTransformerModelTrainer.Types.cs

    // LayerAdamState, AdamState → FtTransformerModelTrainer.Types.cs


    // ── Magnitude regressor (mini-batch Adam) ────────────────────────────────

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train, int featureCount, TrainingHyperparams hp,
        CancellationToken ct = default)
    {
        var w    = new double[featureCount];
        double b = 0.0;

        bool   canEarlyStop = train.Count >= 30;
        int    valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var    valSet       = canEarlyStop ? train[^valSize..] : train;
        var    trainSet     = canEarlyStop ? train[..^valSize] : train;

        if (trainSet.Count == 0) return (w, b);

        int batchSize = hp.MiniBatchSize > 1 ? hp.MiniBatchSize : DefaultBatchSize;

        var    mW     = new double[featureCount];
        var    vW     = new double[featureCount];
        double mB     = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int    t      = 0;

        double bestValLoss = double.MaxValue;
        var    bestW       = new double[featureCount];
        double bestB       = 0.0;
        int    patience    = 0;

        // Shuffled index array for epoch-level randomisation
        var indices = new int[trainSet.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        int rngSeed = HashCode.Combine(trainSet.Count, featureCount);
        var rng = new Random(rngSeed);

        var gW = new double[featureCount]; // batch gradient accumulator

        for (int epoch = 0; epoch < hp.MaxEpochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double alpha = hp.LearningRate * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / hp.MaxEpochs));

            // Fisher-Yates shuffle per epoch
            for (int i = indices.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            int numBatches = (trainSet.Count + batchSize - 1) / batchSize;
            for (int batch = 0; batch < numBatches; batch++)
            {
                t++; beta1t *= AdamBeta1; beta2t *= AdamBeta2;

                int bStart = batch * batchSize;
                int bEnd   = Math.Min(bStart + batchSize, trainSet.Count);
                int bCount = bEnd - bStart;

                // Zero batch gradient accumulators
                Array.Clear(gW);
                double gB = 0.0;

                // Accumulate gradients over the mini-batch
                for (int bi = bStart; bi < bEnd; bi++)
                {
                    var s = trainSet[indices[bi]];
                    double pred = b;
                    for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                    double err = pred - s.Magnitude;
                    if (!double.IsFinite(err)) continue;

                    double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                    gB += huberGrad;
                    for (int j = 0; j < featureCount; j++)
                        gW[j] += huberGrad * s.Features[j] + hp.L2Lambda * w[j];
                }

                // Average gradients
                double invCount = 1.0 / bCount;
                gB *= invCount;
                for (int j = 0; j < featureCount; j++) gW[j] *= invCount;

                // Adam update
                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = alpha * Math.Sqrt(bc2) / bc1;

                mB = AdamBeta1 * mB + (1.0 - AdamBeta1) * gB;
                vB = AdamBeta2 * vB + (1.0 - AdamBeta2) * gB * gB;
                b -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                for (int j = 0; j < featureCount; j++)
                {
                    mW[j] = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * gW[j];
                    vW[j] = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * gW[j] * gW[j];
                    w[j] -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;
            double valLoss = 0.0; int valN = 0;
            foreach (var s in valSet)
            {
                double pred = b;
                for (int j = 0; j < featureCount; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
                valN++;
            }
            valLoss = valN > 0 ? valLoss / valN : double.MaxValue;

            if (valLoss < bestValLoss - 1e-6) { bestValLoss = valLoss; Array.Copy(w, bestW, featureCount); bestB = b; patience = 0; }
            else if (++patience >= hp.EarlyStoppingPatience) break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ── Platt scaling ────────────────────────────────────────────────────────

    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet, TransformerModel model, int featureCount, InferenceBuffers buf)
    {
        if (calSet.Count < 10) return (1.0, 0.0);

        int n = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];
        for (int i = 0; i < n; i++)
        {
            double raw = ForwardPass(calSet[i].Features, model, featureCount, buf);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]  = MLFeatureHelper.Logit(raw);
            labels[i]  = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double A = 1.0, B = 0.0;
        double prevLoss = double.MaxValue;
        for (int iter = 0; iter < 200; iter++)
        {
            double dA = 0, dB = 0, loss = 0;
            for (int i = 0; i < n; i++)
            {
                double p = MLFeatureHelper.Sigmoid(A * logits[i] + B);
                double err = p - labels[i];
                dA += err * logits[i];
                dB += err;
                double pc = Math.Clamp(p, 1e-7, 1.0 - 1e-7);
                loss += -(labels[i] * Math.Log(pc) + (1.0 - labels[i]) * Math.Log(1.0 - pc));
            }
            A -= 0.01 * dA / n;
            B -= 0.01 * dB / n;

            loss /= n;
            if (Math.Abs(prevLoss - loss) < 1e-8) break;
            prevLoss = loss;
        }
        return (A, B);
    }

    // ── Class-conditional Platt scaling ───────────────────────────────────────

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet, TransformerModel model, int featureCount, InferenceBuffers buf)
    {
        if (calSet.Count < 20) return (1.0, 0.0, 1.0, 0.0); // identity on logit scale

        var buyLogits  = new List<double>();
        var buyLabels  = new List<double>();
        var sellLogits = new List<double>();
        var sellLabels = new List<double>();

        foreach (var s in calSet)
        {
            double raw = ForwardPass(s.Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double logit = MLFeatureHelper.Logit(raw);
            double label = s.Direction > 0 ? 1.0 : 0.0;

            if (raw >= 0.5) { buyLogits.Add(logit); buyLabels.Add(label); }
            else            { sellLogits.Add(logit); sellLabels.Add(label); }
        }

        var (aBuy, bBuy)   = FitPlattOnSubset(buyLogits, buyLabels);
        var (aSell, bSell) = FitPlattOnSubset(sellLogits, sellLabels);
        return (aBuy, bBuy, aSell, bSell);
    }

    private static (double A, double B) FitPlattOnSubset(List<double> logits, List<double> labels)
    {
        if (logits.Count < 5) return (1.0, 0.0); // identity on logit scale
        double A = 1.0, B = 0.0;
        int n = logits.Count;
        double prevLoss = double.MaxValue;
        for (int iter = 0; iter < 200; iter++)
        {
            double dA = 0, dB = 0, loss = 0;
            for (int i = 0; i < n; i++)
            {
                double p = MLFeatureHelper.Sigmoid(A * logits[i] + B);
                double err = p - labels[i];
                dA += err * logits[i];
                dB += err;
                double pc = Math.Clamp(p, 1e-7, 1.0 - 1e-7);
                loss += -(labels[i] * Math.Log(pc) + (1.0 - labels[i]) * Math.Log(1.0 - pc));
            }
            A -= 0.01 * dA / n;
            B -= 0.01 * dB / n;

            loss /= n;
            if (Math.Abs(prevLoss - loss) < 1e-8) break;
            prevLoss = loss;
        }
        return (A, B);
    }

    // ── Kelly fraction ───────────────────────────────────────────────────────

    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
    {
        if (calSet.Count == 0) return 0.0;
        double sum = 0;
        foreach (var s in calSet)
        {
            double raw = ForwardPass(s.Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            double edge = Math.Max(0, 2 * p - 1);
            sum += edge * 0.5; // half-Kelly
        }
        return sum / calSet.Count;
    }

    // ── Temperature scaling ──────────────────────────────────────────────────

    private static double FitTemperatureScaling(
        List<TrainingSample> calSet, TransformerModel model, int featureCount, InferenceBuffers buf)
    {
        if (calSet.Count < 10) return 1.0;

        var logits = new double[calSet.Count];
        var labels = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = ForwardPass(calSet[i].Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i] = MLFeatureHelper.Logit(raw);
            labels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double T = 1.0;
        for (int iter = 0; iter < 100; iter++)
        {
            double dT = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                double scaledLogit = logits[i] / T;
                double p = MLFeatureHelper.Sigmoid(scaledLogit);
                dT += (p - labels[i]) * (-logits[i] / (T * T));
            }
            T -= 0.01 * dT / calSet.Count;
            T = Math.Clamp(T, 0.1, 10.0);
        }
        return T;
    }

    // ── Durbin-Watson ────────────────────────────────────────────────────────

    private static double ComputeDurbinWatson(
        List<TrainingSample> samples, double[] magWeights, double magBias, int featureCount)
    {
        if (samples.Count < 3) return 2.0;

        double prevResidual = 0;
        double sumSqDiff = 0, sumSqRes = 0;
        bool hasPrev = false;

        foreach (var s in samples)
        {
            double pred = magBias;
            for (int j = 0; j < featureCount; j++) pred += magWeights[j] * s.Features[j];
            double residual = s.Magnitude - pred;

            sumSqRes += residual * residual;
            if (hasPrev)
            {
                double diff = residual - prevResidual;
                sumSqDiff += diff * diff;
            }
            prevResidual = residual;
            hasPrev = true;
        }

        return sumSqRes > 1e-15 ? sumSqDiff / sumSqRes : 2.0;
    }

    // ── Equity curve stats ───────────────────────────────────────────────────

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions, double sharpeAnnualisation = 252.0)
    {
        if (predictions.Length == 0) return (0, 0);

        double equity = 0, peak = 0, maxDD = 0;
        var returns = new double[predictions.Length];

        for (int i = 0; i < predictions.Length; i++)
        {
            double ret = predictions[i].Predicted == predictions[i].Actual ? 1.0 : -1.0;
            returns[i] = ret;
            equity += ret;
            if (equity > peak) peak = equity;
            // Use absolute drawdown when peak is non-positive (strategy never profitable),
            // otherwise use relative drawdown from peak.
            double dd = peak > 0
                ? (peak - equity) / peak
                : (equity < 0 ? -equity / predictions.Length : 0);
            if (dd > maxDD) maxDD = dd;
        }

        return (maxDD, ComputeSharpe(returns, returns.Length, sharpeAnnualisation));
    }

    // ── Feature pruning helpers ──────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double minImportance, int featureCount)
    {
        var mask = new bool[featureCount];
        if (minImportance <= 0.0 || featureCount == 0)
        {
            Array.Fill(mask, true);
            return mask;
        }
        double equalShare = 1.0 / featureCount;
        double threshold = equalShare * minImportance;
        for (int j = 0; j < featureCount; j++)
            mask[j] = importance[j] >= threshold;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        int activeCount = mask.Count(m => m);
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var compressed = new float[activeCount];
            int idx = 0;
            for (int j = 0; j < s.Features.Length; j++)
                if (mask[j]) compressed[idx++] = s.Features[j];
            result.Add(s with { Features = compressed });
        }
        return result;
    }

    // ── Evaluation ───────────────────────────────────────────────────────────

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet, TransformerModel model,
        double[] magWeights, double magBias,
        double plattA, double plattB, int featureCount, InferenceBuffers buf,
        double sharpeAnnualisation = 252.0)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        int tp = 0, fp = 0, fn = 0, tn = 0;
        double sumMagSqErr = 0, sumBrier = 0, sumEV = 0;
        int n = testSet.Count;

        var returns = ArrayPool<double>.Shared.Rent(n);
        int retCount = 0;

        try
        {
            foreach (var s in testSet)
            {
                double rawProb = ForwardPass(s.Features, model, featureCount, buf);
                rawProb        = Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
                double calibP  = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawProb) + plattB);
                bool predictedUp = calibP >= 0.5;
                bool actualUp    = s.Direction == 1;
                bool correct     = predictedUp == actualUp;

                double y = actualUp ? 1.0 : 0.0;
                sumBrier += (calibP - y) * (calibP - y);

                double magPred = MLFeatureHelper.DotProduct(magWeights, s.Features) + magBias;
                sumMagSqErr += (magPred - s.Magnitude) * (magPred - s.Magnitude);

                double edge = calibP - 0.5;
                sumEV += (correct ? 1 : -1) * Math.Abs(edge) * Math.Abs(s.Magnitude);

                returns[retCount++] = (predictedUp ? 1 : -1) * (actualUp ? 1 : -1) * Math.Abs(s.Magnitude);

                if (correct && predictedUp)        tp++;
                else if (!correct && predictedUp)  fp++;
                else if (!correct && !predictedUp) fn++;
                else                               tn++;
            }

            double accuracy  = (tp + tn) / (double)n;
            double precision = (tp + fp) > 0 ? tp / (double)(tp + fp) : 0;
            double recall    = (tp + fn) > 0 ? tp / (double)(tp + fn) : 0;
            double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;

            return new EvalMetrics(
                Accuracy: accuracy, Precision: precision, Recall: recall, F1: f1,
                MagnitudeRmse: Math.Sqrt(sumMagSqErr / n), ExpectedValue: sumEV / n,
                BrierScore: sumBrier / n, WeightedAccuracy: accuracy,
                SharpeRatio: ComputeSharpe(returns, retCount, sharpeAnnualisation),
                TP: tp, FP: fp, FN: fn, TN: tn);
        }
        finally { ArrayPool<double>.Shared.Return(returns); }
    }

    // ── ECE ──────────────────────────────────────────────────────────────────

    private static (double Ece, double[]? BinConf, double[]? BinAcc, int[]? BinCount) ComputeEce(
        List<TrainingSample> testSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
    {
        if (testSet.Count < 20) return (0.5, null, null, null);

        int NumBins = Math.Max(5, Math.Min(20, (int)Math.Sqrt(testSet.Count)));
        var binConfSum = new double[NumBins];
        var binCorrect = new int[NumBins];
        var binCount   = new int[NumBins];

        foreach (var s in testSet)
        {
            double raw = ForwardPass(s.Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            int bin = Math.Clamp((int)(p * NumBins), 0, NumBins - 1);
            binConfSum[bin] += p;
            if (s.Direction == 1) binCorrect[bin]++; // positive-class frequency, not classification accuracy
            binCount[bin]++;
        }

        double ece = 0;
        var outBinConf = new double[NumBins];
        var outBinAcc  = new double[NumBins];
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            double avgConf = binConfSum[b] / binCount[b];
            double avgAcc  = binCorrect[b] / (double)binCount[b];
            outBinConf[b] = avgConf;
            outBinAcc[b]  = avgAcc;
            ece += Math.Abs(avgAcc - avgConf) * binCount[b] / testSet.Count;
        }
        return (ece, outBinConf, outBinAcc, binCount);
    }

    // ── EV-optimal threshold ─────────────────────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf,
        int searchMinBps = 3000, int searchMaxBps = 7500, int stepBps = 50)
    {
        if (dataSet.Count < 30) return 0.5;
        if (stepBps <= 0) stepBps = 50;

        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
        {
            double raw = ForwardPass(dataSet[i].Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            probs[i] = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
        }

        double bestEV = double.MinValue, bestThr = 0.5;
        for (int t = searchMinBps; t <= searchMaxBps; t += stepBps)
        {
            double thr = t / 10000.0, sumEV = 0;
            for (int i = 0; i < dataSet.Count; i++)
            {
                bool correct = (probs[i] >= thr) == (dataSet[i].Direction == 1);
                sumEV += (correct ? 1 : -1) * Math.Abs(probs[i] - 0.5) * Math.Abs(dataSet[i].Magnitude);
            }
            double ev = sumEV / dataSet.Count;
            if (ev > bestEV) { bestEV = ev; bestThr = thr; }
        }
        return bestThr;
    }

    // ── Permutation feature importance ────────────────────────────────────────

    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf, CancellationToken ct)
    {
        const int PermutationRuns = 3;

        int baselineCorrect = 0;
        foreach (var s in testSet)
        {
            double raw = ForwardPass(s.Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            if ((p >= 0.5) == (s.Direction == 1)) baselineCorrect++;
        }
        double baselineAcc = baselineCorrect / (double)testSet.Count;

        var importance = new float[featureCount];
        int permSeed = HashCode.Combine(testSet.Count, featureCount, baselineCorrect);
        var rng = new Random(permSeed);

        var shuffledIdx = Enumerable.Range(0, testSet.Count).ToArray();
        var scratch = new float[featureCount];

        for (int j = 0; j < featureCount && !ct.IsCancellationRequested; j++)
        {
            double dropSum = 0;
            for (int run = 0; run < PermutationRuns; run++)
            {
                // Re-shuffle indices for each run
                for (int i = shuffledIdx.Length - 1; i > 0; i--)
                {
                    int swap = rng.Next(i + 1);
                    (shuffledIdx[i], shuffledIdx[swap]) = (shuffledIdx[swap], shuffledIdx[i]);
                }

                int correct = 0;
                for (int i = 0; i < testSet.Count; i++)
                {
                    Array.Copy(testSet[i].Features, scratch, featureCount);
                    scratch[j] = testSet[shuffledIdx[i]].Features[j];
                    double raw = ForwardPass(scratch, model, featureCount, buf);
                    raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
                    double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
                    if ((p >= 0.5) == (testSet[i].Direction == 1)) correct++;
                }
                dropSum += baselineAcc - correct / (double)testSet.Count;
            }
            importance[j] = (float)(dropSum / PermutationRuns);
        }

        float sumImp = importance.Sum();
        if (sumImp > 1e-6f)
            for (int j = 0; j < featureCount; j++) importance[j] /= sumImp;
        return importance;
    }

    // ── Brier Skill Score ────────────────────────────────────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
    {
        if (testSet.Count < 10) return 0.0;
        int buyCount = 0;
        foreach (var s in testSet) if (s.Direction == 1) buyCount++;
        double pBase = buyCount / (double)testSet.Count;
        double brierNaive = pBase * (1.0 - pBase);
        if (brierNaive < 1e-10) return 0.0;

        double brierSum = 0;
        foreach (var s in testSet)
        {
            double raw = ForwardPass(s.Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            double y = s.Direction > 0 ? 1.0 : 0.0;
            brierSum += (p - y) * (p - y);
        }
        return 1.0 - brierSum / testSet.Count / brierNaive;
    }

    // ── Conformal prediction ─────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf, double alpha)
    {
        if (calSet.Count < 10) return 0.5;
        var nonconf = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            double raw = ForwardPass(calSet[i].Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            nonconf[i] = 1.0 - (calSet[i].Direction > 0 ? p : 1.0 - p);
        }
        Array.Sort(nonconf);
        int qIndex = Math.Clamp((int)Math.Ceiling((1.0 - alpha) * (calSet.Count + 1)) - 1, 0, calSet.Count - 1);
        return nonconf[qIndex];
    }

    // ── Validation log loss ──────────────────────────────────────────────────

    private static double ComputeLogLoss(
        List<TrainingSample> valSet, TransformerModel model,
        int featureCount, double labelSmoothing, InferenceBuffers buf)
    {
        double loss = 0; int count = 0;
        foreach (var s in valSet)
        {
            double p = ForwardPass(s.Features, model, featureCount, buf);
            if (!double.IsFinite(p)) continue;
            p = Math.Clamp(p, 1e-7, 1.0 - 1e-7);
            double y = s.Direction > 0 ? 1.0 - labelSmoothing : labelSmoothing;
            loss += -(y * Math.Log(p) + (1.0 - y) * Math.Log(1.0 - p));
            count++;
        }
        return count > 0 ? loss / count : double.MaxValue;
    }

    // ── Positional bias helpers ──────────────────────────────────────────────

    private static void CopyPosBias(TransformerModel src, TransformerModel dst)
    {
        if (!src.UsePositionalBias) return;
        for (int l = 0; l < src.NumLayers; l++)
            if (src.Layers[l].PosBias is not null && dst.Layers[l].PosBias is not null)
                for (int h = 0; h < src.NumHeads; h++)
                    Array.Copy(src.Layers[l].PosBias![h], dst.Layers[l].PosBias![h],
                        src.Layers[l].PosBias[h].Length);
    }

    private static TransformerModel CloneModelWithPosBias(TransformerModel src)
    {
        var dst = CloneModel(src);
        if (src.UsePositionalBias)
        {
            dst.UsePositionalBias = true;
            for (int l = 0; l < src.NumLayers; l++)
                if (src.Layers[l].PosBias is not null)
                {
                    dst.Layers[l].PosBias = new double[src.NumHeads][];
                    for (int h = 0; h < src.NumHeads; h++)
                    {
                        dst.Layers[l].PosBias[h] = new double[src.Layers[l].PosBias![h].Length];
                        Array.Copy(src.Layers[l].PosBias[h], dst.Layers[l].PosBias[h],
                            src.Layers[l].PosBias[h].Length);
                    }
                }
        }
        return dst;
    }

    // ── Binary serialisation for additional layers (improvement 6+7) ─────────

    private static byte[] SerializeAdditionalLayersBinary(TransformerModel model)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int numAdditional = model.NumLayers - 1;
        int S = model.SeqLen;
        bool hasPosBias = model.UsePositionalBias;

        bw.Write(numAdditional);
        bw.Write(model.NumHeads);
        bw.Write(hasPosBias ? S * S : 0);

        for (int l = 1; l < model.NumLayers; l++)
        {
            var layer = model.Layers[l];
            WriteBinaryMatrix(bw, layer.Wq, model.EmbedDim, model.EmbedDim);
            WriteBinaryMatrix(bw, layer.Wk, model.EmbedDim, model.EmbedDim);
            WriteBinaryMatrix(bw, layer.Wv, model.EmbedDim, model.EmbedDim);
            WriteBinaryMatrix(bw, layer.Wo, model.EmbedDim, model.EmbedDim);
            WriteBinaryVector(bw, layer.Gamma1);
            WriteBinaryVector(bw, layer.Beta1);
            WriteBinaryMatrix(bw, layer.Wff1, model.EmbedDim, model.FfnDim);
            WriteBinaryVector(bw, layer.Bff1);
            WriteBinaryMatrix(bw, layer.Wff2, model.FfnDim, model.EmbedDim);
            WriteBinaryVector(bw, layer.Bff2);
            WriteBinaryVector(bw, layer.Gamma2);
            WriteBinaryVector(bw, layer.Beta2);
            if (hasPosBias && layer.PosBias is not null)
                WriteBinaryMatrix(bw, layer.PosBias, model.NumHeads, S * S);
        }

        bw.Flush();
        byte[] payload = ms.ToArray();

        // Append CRC32 trailer
        uint crc = ComputeCrc32(payload);
        var result = new byte[payload.Length + 4];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        BitConverter.TryWriteBytes(result.AsSpan(payload.Length), crc);
        return result;
    }

    private static List<SerializedLayerWeights> DeserializeAdditionalLayers(byte[] data, int D, int Ff)
    {
        if (data.Length < 4) throw new InvalidOperationException("Binary blob too short");
        int payloadLen = data.Length - 4;
        uint storedCrc = BitConverter.ToUInt32(data, payloadLen);
        uint computedCrc = ComputeCrc32(data[..payloadLen]);
        if (storedCrc != computedCrc)
            throw new InvalidOperationException("CRC32 mismatch");

        var result = new List<SerializedLayerWeights>();
        using var ms = new MemoryStream(data, 0, payloadLen);
        using var br = new BinaryReader(ms);
        int numLayers = br.ReadInt32();
        int numHeads = br.ReadInt32();
        int seqSq = br.ReadInt32();
        for (int l = 0; l < numLayers; l++)
        {
            var lw = new SerializedLayerWeights
            {
                Wq = ReadBinaryMatrix(br, D, D), Wk = ReadBinaryMatrix(br, D, D),
                Wv = ReadBinaryMatrix(br, D, D), Wo = ReadBinaryMatrix(br, D, D),
                Gamma1 = ReadBinaryVector(br, D), Beta1 = ReadBinaryVector(br, D),
                Wff1 = ReadBinaryMatrix(br, D, Ff), Bff1 = ReadBinaryVector(br, Ff),
                Wff2 = ReadBinaryMatrix(br, Ff, D), Bff2 = ReadBinaryVector(br, D),
                Gamma2 = ReadBinaryVector(br, D), Beta2 = ReadBinaryVector(br, D),
            };
            if (seqSq > 0 && numHeads > 0)
                lw.PosBias = ReadBinaryMatrix(br, numHeads, seqSq);
            result.Add(lw);
        }
        return result;
    }

    private static void WriteBinaryMatrix(BinaryWriter bw, double[][] m, int rows, int cols)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                bw.Write(m[r][c]);
    }

    private static void WriteBinaryVector(BinaryWriter bw, double[] v)
    {
        for (int i = 0; i < v.Length; i++)
            bw.Write(v[i]);
    }

    private static double[][] ReadBinaryMatrix(BinaryReader br, int rows, int cols)
    {
        var m = new double[rows][];
        for (int r = 0; r < rows; r++)
        {
            m[r] = new double[cols];
            for (int c = 0; c < cols; c++) m[r][c] = br.ReadDouble();
        }
        return m;
    }

    private static double[] ReadBinaryVector(BinaryReader br, int len)
    {
        var v = new double[len];
        for (int i = 0; i < len; i++) v[i] = br.ReadDouble();
        return v;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
        }
        return ~crc;
    }
}
