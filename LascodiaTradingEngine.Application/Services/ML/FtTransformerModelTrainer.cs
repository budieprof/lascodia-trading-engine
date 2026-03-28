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
public sealed class FtTransformerModelTrainer : IMLModelTrainer
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
            allStd, hp, F, embedDim, numHeads, ffnDim, numLayers, ct);
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
        var finalMetrics = EvaluateModel(testSet, model, magWeights, magBias, plattA, plattB, F, testBuf);

        _logger.LogInformation(
            "FT-Transformer final eval — acc={Acc:P1} f1={F1:F3} ev={EV:F4} brier={Brier:F4} sharpe={Sharpe:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.BrierScore, finalMetrics.SharpeRatio);

        // ── 8. ECE post-Platt ────────────────────────────────────────────────
        double ece = ComputeEce(testSet, model, plattA, plattB, F, testBuf);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);

        // ── 9. EV-optimal decision threshold (tuned on cal set) ──────────────
        double optimalThreshold = ComputeOptimalThreshold(calSet, model, plattA, plattB, F, calBuf);
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
            var prunedMetrics = EvaluateModel(maskedTest, prunedModel, pmw, pmb, pA, pB, activeF, prunedBuf);

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
                ece          = ComputeEce(maskedTest, model, pA, pB, F, prunedBuf);
                optimalThreshold = ComputeOptimalThreshold(maskedCal, model, pA, pB, F, prunedBuf);
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
                    }).ToList(), JsonOpts)
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

    // ── Transformer model state ──────────────────────────────────────────────

    /// <summary>Per-block weights for one transformer layer.</summary>
    private sealed class TransformerLayer
    {
        public double[][] Wq;     // [EmbedDim][EmbedDim]
        public double[][] Wk;     // [EmbedDim][EmbedDim]
        public double[][] Wv;     // [EmbedDim][EmbedDim]
        public double[][] Wo;     // [EmbedDim][EmbedDim]
        public double[] Gamma1;   // [EmbedDim]
        public double[] Beta1;    // [EmbedDim]
        public double[][] Wff1;   // [EmbedDim][FfnDim]
        public double[]   Bff1;   // [FfnDim]
        public double[][] Wff2;   // [FfnDim][EmbedDim]
        public double[]   Bff2;   // [EmbedDim]
        public double[] Gamma2;   // [EmbedDim]
        public double[] Beta2;    // [EmbedDim]

        public TransformerLayer(int embedDim, int ffnDim)
        {
            Wq   = new double[embedDim][];
            Wk   = new double[embedDim][];
            Wv   = new double[embedDim][];
            Wo   = new double[embedDim][];
            Gamma1 = new double[embedDim];
            Beta1  = new double[embedDim];
            Wff1 = new double[embedDim][];
            Bff1 = new double[ffnDim];
            Wff2 = new double[ffnDim][];
            Bff2 = new double[embedDim];
            Gamma2 = new double[embedDim];
            Beta2  = new double[embedDim];
        }
    }

    /// <summary>Bundles all trained transformer parameters across all layers.</summary>
    private sealed class TransformerModel
    {
        // Per-feature embedding
        public double[][] We;     // [F][EmbedDim]
        public double[][] Be;     // [F][EmbedDim]

        // Learnable [CLS] token embedding
        public double[] ClsToken; // [EmbedDim]

        // Stacked transformer layers
        public TransformerLayer[] Layers;

        // Final LayerNorm (pre-norm architecture requires LN on the output)
        public double[] GammaFinal; // [EmbedDim]
        public double[] BetaFinal;  // [EmbedDim]

        // Classifier head
        public double[]   WOut;   // [EmbedDim]
        public double     BOut;   // scalar

        // Architecture dims
        public int F;          // number of features (excludes [CLS])
        public int SeqLen;     // F + 1 ([CLS] + features)
        public int EmbedDim;
        public int NumHeads;
        public int HeadDim;
        public int FfnDim;
        public int NumLayers;

        // Convenience accessors for layer 0 (backward compatibility with snapshot serialisation)
        public double[][] Wq   { get => Layers[0].Wq;   set => Layers[0].Wq   = value; }
        public double[][] Wk   { get => Layers[0].Wk;   set => Layers[0].Wk   = value; }
        public double[][] Wv   { get => Layers[0].Wv;   set => Layers[0].Wv   = value; }
        public double[][] Wo   { get => Layers[0].Wo;   set => Layers[0].Wo   = value; }
        public double[]   Gamma1 { get => Layers[0].Gamma1; set => Layers[0].Gamma1 = value; }
        public double[]   Beta1  { get => Layers[0].Beta1;  set => Layers[0].Beta1  = value; }
        public double[][] Wff1 { get => Layers[0].Wff1; set => Layers[0].Wff1 = value; }
        public double[]   Bff1 { get => Layers[0].Bff1; set => Layers[0].Bff1 = value; }
        public double[][] Wff2 { get => Layers[0].Wff2; set => Layers[0].Wff2 = value; }
        public double[]   Bff2 { get => Layers[0].Bff2; set => Layers[0].Bff2 = value; }
        public double[]   Gamma2 { get => Layers[0].Gamma2; set => Layers[0].Gamma2 = value; }
        public double[]   Beta2  { get => Layers[0].Beta2;  set => Layers[0].Beta2  = value; }

        public TransformerModel(int f, int embedDim, int numHeads, int ffnDim, int numLayers = 1)
        {
            F         = f;
            SeqLen    = f + 1; // [CLS] + F feature tokens
            EmbedDim  = embedDim;
            NumHeads  = numHeads;
            HeadDim   = embedDim / numHeads;
            FfnDim    = ffnDim;
            NumLayers = numLayers;

            We = new double[f][];
            Be = new double[f][];
            ClsToken = new double[embedDim];

            Layers = new TransformerLayer[numLayers];
            for (int l = 0; l < numLayers; l++)
                Layers[l] = new TransformerLayer(embedDim, ffnDim);

            GammaFinal = new double[embedDim];
            BetaFinal  = new double[embedDim];

            WOut = new double[embedDim];
            BOut = 0.0;
        }
    }

    // ── Reusable inference buffers ──────────────────────────────────────────

    /// <summary>
    /// Pre-allocated buffers for forward pass to eliminate GC pressure during
    /// repeated inference (Platt fitting, ECE, permutation importance, etc.).
    /// Supports multi-layer transformers by sharing Q/K/V/AttnOut/Res/Ffn buffers across layers.
    /// </summary>
    private sealed class InferenceBuffers
    {
        public readonly double[][] E;      // [S][EmbedDim] - embeddings / inter-layer input (S = F+1 with [CLS])
        public readonly double[][] Q;      // [S][EmbedDim] - queries
        public readonly double[][] K;      // [S][EmbedDim] - keys
        public readonly double[][] V;      // [S][EmbedDim] - values
        public readonly double[][] AttnOut;// [S][EmbedDim] - attention output
        public readonly double[][] Scores; // [NumHeads][S*S] - attention scores per head
        public readonly double[][] AttnW;  // [NumHeads][S*S] - attention weights per head
        public readonly double[][] LnIn;   // [S][EmbedDim] - pre-norm LN output
        public readonly double[][] Res1;   // [S][EmbedDim] - after attention + residual
        public readonly double[][] LnIn2;  // [S][EmbedDim] - pre-norm LN2 output
        public readonly double[][] FfnH;   // [S][FfnDim]   - FFN hidden
        public readonly double[][] FfnOut; // [S][EmbedDim] - FFN output
        public readonly double[][] Res2;   // [S][EmbedDim] - after FFN + residual
        public readonly double[]   FinalLn;// [EmbedDim]    - final LN output for [CLS]

        /// <param name="f">Number of features (SeqLen = f+1 with [CLS] token).</param>
        public InferenceBuffers(int f, int embedDim, int numHeads, int ffnDim)
        {
            int s = f + 1; // [CLS] + F feature tokens
            E      = Alloc2D(s, embedDim);
            Q      = Alloc2D(s, embedDim);
            K      = Alloc2D(s, embedDim);
            V      = Alloc2D(s, embedDim);
            AttnOut= Alloc2D(s, embedDim);
            Scores = Alloc2D(numHeads, s * s);
            AttnW  = Alloc2D(numHeads, s * s);
            LnIn   = Alloc2D(s, embedDim);
            Res1   = Alloc2D(s, embedDim);
            LnIn2  = Alloc2D(s, embedDim);
            FfnH   = Alloc2D(s, ffnDim);
            FfnOut = Alloc2D(s, embedDim);
            Res2   = Alloc2D(s, embedDim);
            FinalLn= new double[embedDim];
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }

    }

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  featureCount,
        int                  embedDim,
        int                  numHeads,
        int                  ffnDim,
        int                  numLayers,
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

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImportances = new List<double[]>(folds);
        int badFolds = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd   = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("Fold {Fold} skipped — insufficient training data ({N})", fold, trainEnd);
                continue;
            }

            var foldTrain = samples[..trainEnd].ToList();

            // Time-series purging
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count)
                    foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) continue;

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(30, hp.MaxEpochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            var cvModel = FitTransformer(foldTrain, cvHp, featureCount, embedDim, numHeads, ffnDim, numLayers, null, ct);
            var cvBuf = new InferenceBuffers(featureCount, embedDim, numHeads, ffnDim);
            var m = EvaluateModel(foldTest, cvModel, new double[featureCount], 0.0, 1.0, 0.0, featureCount, cvBuf);

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

                var (foldMaxDD, foldCurveSharpe) = ComputeEquityCurveStats(foldPredictions);

                if (hp.MaxFoldDrawdown < 1.0 && foldMaxDD > hp.MaxFoldDrawdown)
                    isBadFold = true;
                if (hp.MinFoldCurveSharpe > -99.0 && foldCurveSharpe < hp.MinFoldCurveSharpe)
                    isBadFold = true;
            }

            if (isBadFold) badFolds++;

            accList.Add(m.Accuracy);
            f1List.Add(m.F1);
            evList.Add(m.ExpectedValue);
            sharpeList.Add(m.SharpeRatio);
            foldImportances.Add(foldImp);
        }

        if (accList.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

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
            LoadWarmStartWeights(model, warmStart!, featureCount, embedDim, ffnDim, rng);
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
        double dropoutRate = DefaultDropoutRate;

        double bestValLoss = double.MaxValue;
        int patience = 0;
        int nanReversions = 0;
        const int MaxNanReversions = 3;
        double lrScale = 1.0;

        var bestModel = CloneModel(model);
        double l2 = hp.L2Lambda;

        // Shuffled index array for epoch-level randomisation
        var indices = new int[trainSet.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        // ── Gradient accumulator ────────────────────────────────────────────
        var grad = new TransformerGrad(featureCount, embedDim, ffnDim, numLayers);

        // ── Adam state ──────────────────────────────────────────────────────
        var adam = new AdamState(featureCount, embedDim, ffnDim, numLayers);

        // ── Forward/backward pass buffers ───────────────────────────────────
        var fwdBuf = new ForwardBuffers(featureCount, embedDim, numHeads, ffnDim, numLayers);
        var valBuf = new InferenceBuffers(featureCount, embedDim, numHeads, ffnDim);

        for (int epoch = 0; epoch < hp.MaxEpochs && !ct.IsCancellationRequested; epoch++)
            {
                ct.ThrowIfCancellationRequested();

                double alpha = hp.LearningRate * lrScale * 0.5 *
                    (1.0 + Math.Cos(Math.PI * epoch / hp.MaxEpochs));

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
                        BackwardPass(err, model, featureCount, fwdBuf, xRaw, grad, l2);
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

                    ApplyAdamUpdates(model, grad, adam, alphAt, featureCount, embedDim, ffnDim);

                    // ── NaN/Inf guard with backoff ────────────────────────────
                    if (!double.IsFinite(model.BOut) || HasNonFinite(model))
                    {
                        CopyModel(bestModel, model);
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
                        ClipWeights(model, hp.MaxWeightMagnitude);
                }

                // ── Early stopping ───────────────────────────────────────────
                double valLoss = ComputeLogLoss(valSet, model, featureCount, labelSmoothing, valBuf);
                if (valLoss < bestValLoss - 1e-6)
                {
                    bestValLoss = valLoss;
                    CopyModel(model, bestModel);
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
        TransformerModel model, ModelSnapshot ws, int F, int embedDim, int ffnDim, Random rng)
    {
        // Embeddings
        for (int f = 0; f < F; f++)
        {
            model.We[f] = ws.FtTransformerEmbedWeights![f].Length == embedDim
                ? [..ws.FtTransformerEmbedWeights[f]]
                : InitRow(rng, embedDim, Math.Sqrt(2.0 / (1 + embedDim)));
        }

        if (ws.FtTransformerEmbedBiases is { Length: > 0 } warmBe && warmBe.Length == F && warmBe[0].Length == embedDim)
            for (int f = 0; f < F; f++) model.Be[f] = [..warmBe[f]];
        else
            for (int f = 0; f < F; f++) model.Be[f] = new double[embedDim];

        // [CLS] token
        if (ws.FtTransformerClsToken is { Length: > 0 } warmCls && warmCls.Length == embedDim)
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

        // Layers 1..N-1 from additional layers JSON (if available)
        if (model.NumLayers > 1 && ws.FtTransformerAdditionalLayersJson is { Length: > 0 })
        {
            try
            {
                var additionalLayers = JsonSerializer.Deserialize<List<SerializedLayerWeights>>(
                    ws.FtTransformerAdditionalLayersJson, JsonOpts);
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
            catch { /* If deserialization fails, layers 1..N already have fresh init */ }
        }

        // Any remaining layers beyond what warm-start covers get fresh init
        int warmLayers = 1 + (ws.FtTransformerAdditionalLayersJson is { Length: > 0 } ? ws.FtTransformerNumLayers - 1 : 0);
        for (int l = Math.Max(1, warmLayers); l < model.NumLayers; l++)
            InitialiseLayerWeights(model.Layers[l], embedDim, ffnDim, rng);

        // Final LayerNorm
        if (ws.FtTransformerGammaFinal is { Length: > 0 } warmGF && warmGF.Length == embedDim)
            Array.Copy(warmGF, model.GammaFinal, embedDim);
        else
            Array.Fill(model.GammaFinal, 1.0);

        if (ws.FtTransformerBetaFinal is { Length: > 0 } warmBF && warmBF.Length == embedDim)
            Array.Copy(warmBF, model.BetaFinal, embedDim);
        else
            Array.Fill(model.BetaFinal, 0.0);

        if (ws.FtTransformerOutputWeights is { Length: > 0 } warmOut && warmOut.Length == embedDim)
        {
            model.WOut = [..warmOut];
            model.BOut = ws.FtTransformerOutputBias;
        }
        else
        {
            double xavierOut = Math.Sqrt(2.0 / (embedDim + 1));
            for (int d = 0; d < embedDim; d++)
                model.WOut[d] = SampleGaussian(rng, xavierOut);
        }
    }

    /// <summary>Serialised per-layer weights for additional layers (1..N-1).</summary>
    private sealed class SerializedLayerWeights
    {
        public double[][]? Wq { get; set; }
        public double[][]? Wk { get; set; }
        public double[][]? Wv { get; set; }
        public double[][]? Wo { get; set; }
        public double[]? Gamma1 { get; set; }
        public double[]? Beta1 { get; set; }
        public double[][]? Wff1 { get; set; }
        public double[]? Bff1 { get; set; }
        public double[][]? Wff2 { get; set; }
        public double[]? Bff2 { get; set; }
        public double[]? Gamma2 { get; set; }
        public double[]? Beta2 { get; set; }
    }

    private static void LoadMatrix(double[][] dst, double[][]? src, int rows, int cols, Random rng, double std)
    {
        if (src is { Length: > 0 } && src.Length == rows && src[0].Length == cols)
            for (int r = 0; r < rows; r++) dst[r] = [..src[r]];
        else
            for (int r = 0; r < rows; r++) dst[r] = InitRow(rng, cols, std);
    }

    private static void LoadVector(double[] dst, double[]? src, int len, double fill)
    {
        if (src is { Length: > 0 } && src.Length == len)
            Array.Copy(src, dst, len);
        else
            Array.Fill(dst, fill);
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

    // ── Forward pass buffers for training (includes dropout masks) ─────────

    /// <summary>Per-layer cached intermediates for backprop during training.</summary>
    private sealed class LayerForwardCache
    {
        public readonly double[][] Input;      // [S][EmbedDim] - input to this layer (snapshot)
        public readonly double[][] LnIn;       // [S][EmbedDim] - LN1 output (pre-norm before attention)
        public readonly double[][] Q;          // [S][EmbedDim]
        public readonly double[][] K;          // [S][EmbedDim]
        public readonly double[][] V;          // [S][EmbedDim]
        public readonly double[][] AttnOut;    // [S][EmbedDim]
        public readonly double[][][] HeadScores; // [NumHeads][S][S]
        public readonly double[][][] HeadAttnW;  // [NumHeads][S][S]
        public readonly double[][] Res1;       // [S][EmbedDim]
        public readonly double[][] LnIn2;      // [S][EmbedDim] - LN2 output (pre-norm before FFN)
        public readonly double[][] FfnH;       // [S][FfnDim]
        public readonly double[][] FfnHPreAct; // [S][FfnDim]
        public readonly double[][] FfnOut;     // [S][EmbedDim]
        public readonly double[][] Res2;       // [S][EmbedDim]

        public readonly double[] Ln1Mean;      // [S]
        public readonly double[] Ln1InvStd;    // [S]
        public readonly double[][] Ln1Norm;    // [S][EmbedDim]
        public readonly double[] Ln2Mean;      // [S]
        public readonly double[] Ln2InvStd;    // [S]
        public readonly double[][] Ln2Norm;    // [S][EmbedDim]

        public readonly bool[][] AttnDropMask; // [NumHeads][S*S]
        public readonly bool[][] FfnDropMask;  // [S][FfnDim]

        // Final LN cache (only used for last layer, position 0)
        public double FinalLnMean;
        public double FinalLnInvStd;
        public readonly double[] FinalLnNorm;  // [EmbedDim]
        public readonly double[] FinalLnOut;   // [EmbedDim]

        public LayerForwardCache(int s, int embedDim, int numHeads, int ffnDim)
        {
            Input    = Alloc2D(s, embedDim);
            LnIn     = Alloc2D(s, embedDim);
            Q        = Alloc2D(s, embedDim);
            K        = Alloc2D(s, embedDim);
            V        = Alloc2D(s, embedDim);
            AttnOut  = Alloc2D(s, embedDim);
            HeadScores = new double[numHeads][][];
            HeadAttnW  = new double[numHeads][][];
            for (int h = 0; h < numHeads; h++)
            {
                HeadScores[h] = Alloc2D(s, s);
                HeadAttnW[h]  = Alloc2D(s, s);
            }
            Res1       = Alloc2D(s, embedDim);
            LnIn2      = Alloc2D(s, embedDim);
            FfnH       = Alloc2D(s, ffnDim);
            FfnHPreAct = Alloc2D(s, ffnDim);
            FfnOut     = Alloc2D(s, embedDim);
            Res2       = Alloc2D(s, embedDim);

            Ln1Mean   = new double[s];
            Ln1InvStd = new double[s];
            Ln1Norm   = Alloc2D(s, embedDim);
            Ln2Mean   = new double[s];
            Ln2InvStd = new double[s];
            Ln2Norm   = Alloc2D(s, embedDim);

            AttnDropMask = new bool[numHeads][];
            for (int h = 0; h < numHeads; h++)
                AttnDropMask[h] = new bool[s * s];
            FfnDropMask = new bool[s][];
            for (int i = 0; i < s; i++)
                FfnDropMask[i] = new bool[ffnDim];

            FinalLnNorm = new double[embedDim];
            FinalLnOut  = new double[embedDim];
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }
    }

    private sealed class ForwardBuffers
    {
        public readonly double[][] E;        // [S][EmbedDim] — embedding / inter-layer carrier (S = F+1)
        public readonly double[]   FinalLn;  // [EmbedDim] — final LN output for [CLS]
        public readonly LayerForwardCache[] LayerCaches;

        public readonly int F;
        public readonly int S;        // SeqLen = F + 1
        public readonly int EmbedDim;
        public readonly int NumHeads;
        public readonly int HeadDim;
        public readonly int FfnDim;
        public readonly int NumLayers;

        public ForwardBuffers(int f, int embedDim, int numHeads, int ffnDim, int numLayers = 1)
        {
            F = f; S = f + 1; EmbedDim = embedDim; NumHeads = numHeads;
            HeadDim = embedDim / numHeads; FfnDim = ffnDim;
            NumLayers = numLayers;

            E      = Alloc2D(f + 1, embedDim);
            FinalLn = new double[embedDim];

            LayerCaches = new LayerForwardCache[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerCaches[l] = new LayerForwardCache(f + 1, embedDim, numHeads, ffnDim);
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }

    }

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

                // Issue #1 fix: Attention dropout zeroes weights WITHOUT rescaling
                if (dropoutRate > 0.0)
                {
                    for (int r = 0; r < S; r++)
                        for (int c = 0; c < S; c++)
                        {
                            bool keep = rng.NextDouble() >= dropoutRate;
                            lc.AttnDropMask[h][r * S + c] = keep;
                            lc.HeadAttnW[h][r][c] = keep ? lc.HeadAttnW[h][r][c] : 0.0;
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
        ForwardBuffers buf, float[] xRaw, TransformerGrad grad, double l2)
    {
        int D  = model.EmbedDim;
        int H  = model.NumHeads;
        int Dh = model.HeadDim;
        int Ff = model.FfnDim;
        int S  = F + 1;

        var lastCache = buf.LayerCaches[model.NumLayers - 1];

        // ── Classifier head ─────────────────────────────────────────────
        grad.dBOut += err;
        for (int d = 0; d < D; d++)
            grad.dWOut[d] += err * lastCache.FinalLnOut[d] + l2 * model.WOut[d];

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
        var dInput = grad.Scratch2D_SxD;
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
            var dRes1 = grad.Scratch2D_SxD3;
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dRes1[i][d] = dInput[i][d]; // residual path from Res2

            // FFN backward: weight gradients and dLnIn2 in a single pass
            var dLnIn2 = grad.Scratch2D_SxD2;
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
            var dRes1FromLn2 = grad.Scratch2D_SxD4;
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
            var dE = grad.Scratch2D_SxD5;
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dE[i][d] = dRes1[i][d]; // residual path

            // Wo backward: dRes1 → dAttnOut
            var dAttnOut = grad.Scratch2D_SxD6;
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
            var dQ = grad.Scratch2D_SxD7;
            var dK = grad.Scratch2D_SxD8;
            var dV = grad.Scratch2D_SxD9;
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
                        if (!lc.AttnDropMask[h][r * S + c])
                            daw = 0.0;
                        grad.SoftmaxTemp[c] = daw;
                    }

                    double dotSum = 0;
                    for (int c = 0; c < S; c++)
                        dotSum += lc.HeadAttnW[h][r][c] * grad.SoftmaxTemp[c];

                    for (int c = 0; c < S; c++)
                    {
                        double dScore = lc.HeadAttnW[h][r][c] * (grad.SoftmaxTemp[c] - dotSum);
                        dScore /= sqrtDh;
                        for (int d = 0; d < Dh; d++)
                        {
                            dQ[r][hOff + d] += dScore * lc.K[c][hOff + d];
                            dK[c][hOff + d] += dScore * lc.Q[r][hOff + d];
                        }
                    }
                }
            }

            // Q, K, V projection backward → gradient w.r.t. LN1 output
            var dLnIn = grad.Scratch2D_SxD10;
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
            var dInputFromLn1 = grad.Scratch2D_SxD11;
            for (int i = 0; i < S; i++)
                LayerNormBackward(dLnIn[i], lc.Ln1Norm[i], L.Gamma1,
                    lc.Ln1InvStd[i], D, dInputFromLn1[i],
                    lg.dGamma1, lg.dBeta1);

            // dE = skip from Res1 + gradient through LN1
            for (int i = 0; i < S; i++)
                for (int d = 0; d < D; d++)
                    dE[i][d] += dInputFromLn1[i][d];

            // L2 regularisation for shared projection matrices (once per layer, not per position)
            if (l2 > 0.0)
            {
                for (int d1 = 0; d1 < D; d1++)
                    for (int d2 = 0; d2 < D; d2++)
                    {
                        lg.dWq[d1][d2] += l2 * L.Wq[d1][d2];
                        lg.dWk[d1][d2] += l2 * L.Wk[d1][d2];
                        lg.dWv[d1][d2] += l2 * L.Wv[d1][d2];
                        lg.dWo[d1][d2] += l2 * L.Wo[d1][d2];
                    }
                for (int d = 0; d < D; d++)
                    for (int h = 0; h < Ff; h++)
                        lg.dWff1[d][h] += l2 * L.Wff1[d][h];
                for (int h = 0; h < Ff; h++)
                    for (int d = 0; d < D; d++)
                        lg.dWff2[h][d] += l2 * L.Wff2[h][d];
            }

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
                grad.dWe[f][d] += dInput[f + 1][d] * xRaw[f] + l2 * model.We[f][d];
                grad.dBe[f][d] += dInput[f + 1][d] + l2 * model.Be[f][d];
            }
    }

    // ── Per-layer gradient accumulator ────────────────────────────────────────

    private sealed class LayerGrad
    {
        public double[][] dWq, dWk, dWv, dWo;
        public double[] dGamma1, dBeta1, dGamma2, dBeta2;
        public double[][] dWff1;
        public double[] dBff1;
        public double[][] dWff2;
        public double[] dBff2;

        public LayerGrad(int D, int Ff)
        {
            dWq = Alloc2D(D, D); dWk = Alloc2D(D, D);
            dWv = Alloc2D(D, D); dWo = Alloc2D(D, D);
            dGamma1 = new double[D]; dBeta1 = new double[D];
            dGamma2 = new double[D]; dBeta2 = new double[D];
            dWff1 = Alloc2D(D, Ff); dBff1 = new double[Ff];
            dWff2 = Alloc2D(Ff, D); dBff2 = new double[D];
        }

        public void Zero()
        {
            Zero2D(dWq); Zero2D(dWk); Zero2D(dWv); Zero2D(dWo);
            Array.Clear(dGamma1); Array.Clear(dBeta1);
            Array.Clear(dGamma2); Array.Clear(dBeta2);
            Zero2D(dWff1); Array.Clear(dBff1);
            Zero2D(dWff2); Array.Clear(dBff2);
        }

        private static double[][] Alloc2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
        private static void Zero2D(double[][] a) { for (int i = 0; i < a.Length; i++) Array.Clear(a[i]); }
    }

    private sealed class TransformerGrad
    {
        public double[][] dWe, dBe;
        public double[] dClsToken;
        public LayerGrad[] LayerGrads;
        public double[] dWOut;
        public double dBOut;
        public double[] dGammaFinal, dBetaFinal;

        // Scratch buffers for backward pass (shared across layers, sized S = F+1)
        public double[] Scratch1;     // [D]
        public double[] ScratchD;     // [D]
        public double[][] Scratch2D_SxD, Scratch2D_SxD2, Scratch2D_SxD3;
        public double[][] Scratch2D_SxD4, Scratch2D_SxD5, Scratch2D_SxD6;
        public double[][] Scratch2D_SxD7, Scratch2D_SxD8, Scratch2D_SxD9;
        public double[][] Scratch2D_SxD10, Scratch2D_SxD11;
        public double[] SoftmaxTemp;

        public TransformerGrad(int F, int D, int Ff, int numLayers)
        {
            int S = F + 1;
            dWe = Alloc2D(F, D); dBe = Alloc2D(F, D);
            dClsToken = new double[D];
            dWOut = new double[D];
            dGammaFinal = new double[D];
            dBetaFinal = new double[D];

            LayerGrads = new LayerGrad[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerGrads[l] = new LayerGrad(D, Ff);

            Scratch1 = new double[D];
            ScratchD = new double[D];
            Scratch2D_SxD   = Alloc2D(S, D); Scratch2D_SxD2  = Alloc2D(S, D);
            Scratch2D_SxD3  = Alloc2D(S, D); Scratch2D_SxD4  = Alloc2D(S, D);
            Scratch2D_SxD5  = Alloc2D(S, D); Scratch2D_SxD6  = Alloc2D(S, D);
            Scratch2D_SxD7  = Alloc2D(S, D); Scratch2D_SxD8  = Alloc2D(S, D);
            Scratch2D_SxD9  = Alloc2D(S, D); Scratch2D_SxD10 = Alloc2D(S, D);
            Scratch2D_SxD11 = Alloc2D(S, D);
            SoftmaxTemp = new double[S];
        }

        public void Zero()
        {
            Zero2D(dWe); Zero2D(dBe);
            Array.Clear(dClsToken);
            foreach (var lg in LayerGrads) lg.Zero();
            Array.Clear(dWOut);
            dBOut = 0;
            Array.Clear(dGammaFinal);
            Array.Clear(dBetaFinal);
        }

        public void Scale(double s)
        {
            Scale2D(dWe, s); Scale2D(dBe, s);
            Scale1D(dClsToken, s);
            foreach (var lg in LayerGrads)
            {
                Scale2D(lg.dWq, s); Scale2D(lg.dWk, s); Scale2D(lg.dWv, s); Scale2D(lg.dWo, s);
                Scale1D(lg.dGamma1, s); Scale1D(lg.dBeta1, s);
                Scale1D(lg.dGamma2, s); Scale1D(lg.dBeta2, s);
                Scale2D(lg.dWff1, s); Scale1D(lg.dBff1, s);
                Scale2D(lg.dWff2, s); Scale1D(lg.dBff2, s);
            }
            Scale1D(dWOut, s);
            dBOut *= s;
            Scale1D(dGammaFinal, s);
            Scale1D(dBetaFinal, s);
        }

        public void ClipNorm(double maxNorm)
        {
            double normSq = dBOut * dBOut;
            normSq += NormSq2D(dWe) + NormSq2D(dBe);
            normSq += NormSq1D(dClsToken);
            foreach (var lg in LayerGrads)
            {
                normSq += NormSq2D(lg.dWq) + NormSq2D(lg.dWk) + NormSq2D(lg.dWv) + NormSq2D(lg.dWo);
                normSq += NormSq1D(lg.dGamma1) + NormSq1D(lg.dBeta1);
                normSq += NormSq1D(lg.dGamma2) + NormSq1D(lg.dBeta2);
                normSq += NormSq2D(lg.dWff1) + NormSq1D(lg.dBff1);
                normSq += NormSq2D(lg.dWff2) + NormSq1D(lg.dBff2);
            }
            normSq += NormSq1D(dWOut);
            normSq += NormSq1D(dGammaFinal) + NormSq1D(dBetaFinal);

            double norm = Math.Sqrt(normSq);
            if (norm > maxNorm)
                Scale(maxNorm / norm);
        }

        private static double[][] Alloc2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
        private static void Zero2D(double[][] a) { for (int i = 0; i < a.Length; i++) Array.Clear(a[i]); }
        private static void Scale2D(double[][] a, double s) { for (int i = 0; i < a.Length; i++) for (int j = 0; j < a[i].Length; j++) a[i][j] *= s; }
        private static void Scale1D(double[] a, double s) { for (int i = 0; i < a.Length; i++) a[i] *= s; }
        private static double NormSq2D(double[][] a) { double s = 0; for (int i = 0; i < a.Length; i++) for (int j = 0; j < a[i].Length; j++) s += a[i][j] * a[i][j]; return s; }
        private static double NormSq1D(double[] a) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * a[i]; return s; }
    }

    // ── Per-layer Adam state ────────────────────────────────────────────────

    private sealed class LayerAdamState
    {
        public double[][] mWq, vWq, mWk, vWk, mWv, vWv, mWo, vWo;
        public double[] mGamma1, vGamma1, mBeta1, vBeta1;
        public double[] mGamma2, vGamma2, mBeta2, vBeta2;
        public double[][] mWff1, vWff1;
        public double[] mBff1, vBff1;
        public double[][] mWff2, vWff2;
        public double[] mBff2, vBff2;

        public LayerAdamState(int D, int Ff)
        {
            mWq = Z2D(D, D); vWq = Z2D(D, D); mWk = Z2D(D, D); vWk = Z2D(D, D);
            mWv = Z2D(D, D); vWv = Z2D(D, D); mWo = Z2D(D, D); vWo = Z2D(D, D);
            mGamma1 = new double[D]; vGamma1 = new double[D];
            mBeta1  = new double[D]; vBeta1  = new double[D];
            mGamma2 = new double[D]; vGamma2 = new double[D];
            mBeta2  = new double[D]; vBeta2  = new double[D];
            mWff1 = Z2D(D, Ff); vWff1 = Z2D(D, Ff);
            mBff1 = new double[Ff]; vBff1 = new double[Ff];
            mWff2 = Z2D(Ff, D); vWff2 = Z2D(Ff, D);
            mBff2 = new double[D]; vBff2 = new double[D];
        }

        private static double[][] Z2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
    }

    private sealed class AdamState
    {
        public double[][] mWe, vWe, mBe, vBe;
        public double[] mClsToken, vClsToken;
        public LayerAdamState[] LayerStates;
        public double[] mGammaFinal, vGammaFinal, mBetaFinal, vBetaFinal;
        public double[] mWOut, vWOut;
        public double mBOut, vBOut;
        public int Step;
        public double Beta1t = 1.0, Beta2t = 1.0;

        public AdamState(int F, int D, int Ff, int numLayers)
        {
            mWe = Z2D(F, D); vWe = Z2D(F, D); mBe = Z2D(F, D); vBe = Z2D(F, D);
            mClsToken = new double[D]; vClsToken = new double[D];
            LayerStates = new LayerAdamState[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerStates[l] = new LayerAdamState(D, Ff);
            mGammaFinal = new double[D]; vGammaFinal = new double[D];
            mBetaFinal  = new double[D]; vBetaFinal  = new double[D];
            mWOut = new double[D]; vWOut = new double[D];
        }

        private static double[][] Z2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }

    }

    private static void ApplyAdamUpdates(
        TransformerModel model, TransformerGrad grad, AdamState adam, double alphAt,
        int F, int D, int Ff)
    {
        AdamUpdate2D(model.We, grad.dWe, adam.mWe, adam.vWe, alphAt, F, D);
        AdamUpdate2D(model.Be, grad.dBe, adam.mBe, adam.vBe, alphAt, F, D);
        AdamUpdate1D(model.ClsToken, grad.dClsToken, adam.mClsToken, adam.vClsToken, alphAt, D);

        for (int l = 0; l < model.NumLayers; l++)
        {
            var L  = model.Layers[l];
            var lg = grad.LayerGrads[l];
            var la = adam.LayerStates[l];

            AdamUpdate2D(L.Wq, lg.dWq, la.mWq, la.vWq, alphAt, D, D);
            AdamUpdate2D(L.Wk, lg.dWk, la.mWk, la.vWk, alphAt, D, D);
            AdamUpdate2D(L.Wv, lg.dWv, la.mWv, la.vWv, alphAt, D, D);
            AdamUpdate2D(L.Wo, lg.dWo, la.mWo, la.vWo, alphAt, D, D);
            AdamUpdate1D(L.Gamma1, lg.dGamma1, la.mGamma1, la.vGamma1, alphAt, D);
            AdamUpdate1D(L.Beta1,  lg.dBeta1,  la.mBeta1,  la.vBeta1,  alphAt, D);
            AdamUpdate2D(L.Wff1, lg.dWff1, la.mWff1, la.vWff1, alphAt, D, Ff);
            AdamUpdate1D(L.Bff1, lg.dBff1, la.mBff1, la.vBff1, alphAt, Ff);
            AdamUpdate2D(L.Wff2, lg.dWff2, la.mWff2, la.vWff2, alphAt, Ff, D);
            AdamUpdate1D(L.Bff2, lg.dBff2, la.mBff2, la.vBff2, alphAt, D);
            AdamUpdate1D(L.Gamma2, lg.dGamma2, la.mGamma2, la.vGamma2, alphAt, D);
            AdamUpdate1D(L.Beta2,  lg.dBeta2,  la.mBeta2,  la.vBeta2,  alphAt, D);
        }

        AdamUpdate1D(model.GammaFinal, grad.dGammaFinal, adam.mGammaFinal, adam.vGammaFinal, alphAt, D);
        AdamUpdate1D(model.BetaFinal,  grad.dBetaFinal,  adam.mBetaFinal,  adam.vBetaFinal,  alphAt, D);
        AdamUpdate1D(model.WOut, grad.dWOut, adam.mWOut, adam.vWOut, alphAt, D);

        adam.mBOut = AdamBeta1 * adam.mBOut + (1.0 - AdamBeta1) * grad.dBOut;
        adam.vBOut = AdamBeta2 * adam.vBOut + (1.0 - AdamBeta2) * grad.dBOut * grad.dBOut;
        model.BOut -= alphAt * adam.mBOut / (Math.Sqrt(adam.vBOut) + AdamEpsilon);
    }

    private static void AdamUpdate2D(double[][] w, double[][] g, double[][] m, double[][] v, double lr, int r, int c)
    {
        for (int i = 0; i < r; i++)
            for (int j = 0; j < c; j++)
            {
                m[i][j] = AdamBeta1 * m[i][j] + (1.0 - AdamBeta1) * g[i][j];
                v[i][j] = AdamBeta2 * v[i][j] + (1.0 - AdamBeta2) * g[i][j] * g[i][j];
                w[i][j] -= lr * m[i][j] / (Math.Sqrt(v[i][j]) + AdamEpsilon);
            }
    }

    private static void AdamUpdate1D(double[] w, double[] g, double[] m, double[] v, double lr, int n)
    {
        for (int i = 0; i < n; i++)
        {
            m[i] = AdamBeta1 * m[i] + (1.0 - AdamBeta1) * g[i];
            v[i] = AdamBeta2 * v[i] + (1.0 - AdamBeta2) * g[i] * g[i];
            w[i] -= lr * m[i] / (Math.Sqrt(v[i]) + AdamEpsilon);
        }
    }

    // ── LayerNorm ─────────────────────────────────────────────────────────────

    private static void LayerNormForward(double[] x, double[] gamma, double[] beta, double[] y, int D)
    {
        double mean = 0;
        for (int d = 0; d < D; d++) mean += x[d];
        mean /= D;

        double variance = 0;
        for (int d = 0; d < D; d++) { double diff = x[d] - mean; variance += diff * diff; }
        double invStd = 1.0 / Math.Sqrt(variance / D + 1e-8);

        for (int d = 0; d < D; d++)
            y[d] = gamma[d] * (x[d] - mean) * invStd + beta[d];
    }

    private static void LayerNormForwardCached(
        double[] x, double[] gamma, double[] beta, double[] y,
        int D, ref double outMean, ref double outInvStd, double[] outNorm)
    {
        double mean = 0;
        for (int d = 0; d < D; d++) mean += x[d];
        mean /= D;

        double variance = 0;
        for (int d = 0; d < D; d++) { double diff = x[d] - mean; variance += diff * diff; }
        double invStd = 1.0 / Math.Sqrt(variance / D + 1e-8);

        outMean   = mean;
        outInvStd = invStd;

        for (int d = 0; d < D; d++)
        {
            outNorm[d] = (x[d] - mean) * invStd;
            y[d] = gamma[d] * outNorm[d] + beta[d];
        }
    }

    private static void LayerNormBackward(
        double[] dOut, double[] norm, double[] gamma, double invStd,
        int D, double[] dx, double[] dGamma, double[] dBeta)
    {
        // dGamma, dBeta accumulate across positions
        for (int d = 0; d < D; d++)
        {
            dGamma[d] += dOut[d] * norm[d];
            dBeta[d]  += dOut[d];
        }

        // dx = invStd * (gamma * dOut - mean(gamma * dOut) - norm * mean(gamma * dOut * norm))
        double s1 = 0, s2 = 0;
        for (int d = 0; d < D; d++)
        {
            double gd = gamma[d] * dOut[d];
            s1 += gd;
            s2 += gd * norm[d];
        }
        s1 /= D;
        s2 /= D;

        for (int d = 0; d < D; d++)
            dx[d] = invStd * (gamma[d] * dOut[d] - s1 - norm[d] * s2);
    }

    // ── Activation functions ──────────────────────────────────────────────────

    private static double GELU(double x)
    {
        // Approximate GELU: x * 0.5 * (1 + tanh(sqrt(2/pi) * (x + 0.044715 * x^3)))
        const double sqrt2OverPi = 0.7978845608028654;
        double inner = sqrt2OverPi * (x + 0.044715 * x * x * x);
        return 0.5 * x * (1.0 + Math.Tanh(inner));
    }

    private static double GELUGrad(double x)
    {
        const double sqrt2OverPi = 0.7978845608028654;
        double x3 = x * x * x;
        double inner = sqrt2OverPi * (x + 0.044715 * x3);
        double tanh = Math.Tanh(inner);
        double sech2 = 1.0 - tanh * tanh;
        double dInner = sqrt2OverPi * (1.0 + 3.0 * 0.044715 * x * x);
        return 0.5 * (1.0 + tanh) + 0.5 * x * sech2 * dInner;
    }

    // ── Matrix multiply helper ──────────────────────────────────────────────

    private static void MatMul(double[][] A, double[][] B, double[][] C, int M, int K, int N)
    {
        for (int i = 0; i < M; i++)
            for (int j = 0; j < N; j++)
            {
                double s = 0;
                for (int k = 0; k < K; k++)
                    s += A[i][k] * B[k][j];
                C[i][j] = s;
            }
    }

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
        if (calSet.Count < 20) return (0, 0, 0, 0);

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
        if (logits.Count < 5) return (0, 0);
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
        (int Predicted, int Actual)[] predictions)
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

        return (maxDD, ComputeSharpe(returns, returns.Length));
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
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
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
                SharpeRatio: ComputeSharpe(returns, retCount),
                TP: tp, FP: fp, FN: fn, TN: tn);
        }
        finally { ArrayPool<double>.Shared.Return(returns); }
    }

    // ── ECE ──────────────────────────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
    {
        if (testSet.Count < 20) return 0.5;

        const int NumBins = 10;
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
            if ((p >= 0.5) == (s.Direction == 1)) binCorrect[bin]++;
            binCount[bin]++;
        }

        double ece = 0;
        for (int b = 0; b < NumBins; b++)
        {
            if (binCount[b] == 0) continue;
            ece += Math.Abs(binCorrect[b] / (double)binCount[b] - binConfSum[b] / binCount[b]) * binCount[b] / testSet.Count;
        }
        return ece;
    }

    // ── EV-optimal threshold ─────────────────────────────────────────────────

    private static double ComputeOptimalThreshold(
        List<TrainingSample> dataSet, TransformerModel model,
        double plattA, double plattB, int featureCount, InferenceBuffers buf)
    {
        if (dataSet.Count < 30) return 0.5;

        var probs = new double[dataSet.Count];
        for (int i = 0; i < dataSet.Count; i++)
        {
            double raw = ForwardPass(dataSet[i].Features, model, featureCount, buf);
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            probs[i] = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
        }

        double bestEV = double.MinValue, bestThr = 0.5;
        for (int t = 30; t <= 75; t++)
        {
            double thr = t / 100.0, sumEV = 0;
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

    // ── Weight sanitisation ──────────────────────────────────────────────────

    private static int SanitiseWeights(TransformerModel model)
    {
        int count = 0;
        count += SanitiseMatrix(model.We, model.F);
        count += SanitiseMatrix(model.Be, model.F);
        count += SanitiseArray(model.ClsToken);
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            count += SanitiseMatrix(L.Wq, model.EmbedDim);
            count += SanitiseMatrix(L.Wk, model.EmbedDim);
            count += SanitiseMatrix(L.Wv, model.EmbedDim);
            count += SanitiseMatrix(L.Wo, model.EmbedDim);
            count += SanitiseArray(L.Gamma1);
            count += SanitiseArray(L.Beta1);
            count += SanitiseMatrix(L.Wff1, model.EmbedDim);
            count += SanitiseArray(L.Bff1);
            count += SanitiseMatrix(L.Wff2, model.FfnDim);
            count += SanitiseArray(L.Bff2);
            count += SanitiseArray(L.Gamma2);
            count += SanitiseArray(L.Beta2);
        }
        count += SanitiseArray(model.GammaFinal);
        count += SanitiseArray(model.BetaFinal);
        count += SanitiseArray(model.WOut);
        if (!double.IsFinite(model.BOut)) { model.BOut = 0.0; count++; }
        return count;
    }

    private static int SanitiseMatrix(double[][] m, int rows)
    {
        int count = 0;
        for (int r = 0; r < rows; r++)
            if (HasNonFiniteArray(m[r])) { Array.Clear(m[r]); count++; }
        return count;
    }

    private static int SanitiseArray(double[] a)
    {
        if (HasNonFiniteArray(a)) { Array.Clear(a); return 1; }
        return 0;
    }

    // ── Model cloning ────────────────────────────────────────────────────────

    private static TransformerModel CloneModel(TransformerModel src)
    {
        var dst = new TransformerModel(src.F, src.EmbedDim, src.NumHeads, src.FfnDim, src.NumLayers);
        CopyModel(src, dst);
        return dst;
    }

    private static void CopyModel(TransformerModel src, TransformerModel dst)
    {
        int D  = src.EmbedDim;
        int Ff = src.FfnDim;

        for (int f = 0; f < src.F; f++) { dst.We[f] = [..src.We[f]]; dst.Be[f] = [..src.Be[f]]; }
        dst.ClsToken = [..src.ClsToken];
        for (int l = 0; l < src.NumLayers; l++)
        {
            var sL = src.Layers[l];
            var dL = dst.Layers[l];
            for (int d = 0; d < D; d++) { dL.Wq[d] = [..sL.Wq[d]]; dL.Wk[d] = [..sL.Wk[d]]; dL.Wv[d] = [..sL.Wv[d]]; dL.Wo[d] = [..sL.Wo[d]]; }
            dL.Gamma1 = [..sL.Gamma1]; dL.Beta1 = [..sL.Beta1];
            for (int d = 0; d < D; d++) dL.Wff1[d] = [..sL.Wff1[d]];
            dL.Bff1 = [..sL.Bff1];
            for (int d = 0; d < Ff; d++) dL.Wff2[d] = [..sL.Wff2[d]];
            dL.Bff2 = [..sL.Bff2];
            dL.Gamma2 = [..sL.Gamma2]; dL.Beta2 = [..sL.Beta2];
        }
        dst.GammaFinal = [..src.GammaFinal];
        dst.BetaFinal  = [..src.BetaFinal];
        dst.WOut = [..src.WOut];
        dst.BOut = src.BOut;
    }

    private static void ClipWeights(TransformerModel model, double maxMag)
    {
        ClipMatrix(model.We, model.F, maxMag);
        ClipMatrix(model.Be, model.F, maxMag);
        ClipArray(model.ClsToken, maxMag);
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            ClipMatrix(L.Wq, model.EmbedDim, maxMag);
            ClipMatrix(L.Wk, model.EmbedDim, maxMag);
            ClipMatrix(L.Wv, model.EmbedDim, maxMag);
            ClipMatrix(L.Wo, model.EmbedDim, maxMag);
            ClipArray(L.Gamma1, maxMag); ClipArray(L.Beta1, maxMag);
            ClipMatrix(L.Wff1, model.EmbedDim, maxMag);
            ClipArray(L.Bff1, maxMag);
            ClipMatrix(L.Wff2, model.FfnDim, maxMag);
            ClipArray(L.Bff2, maxMag);
            ClipArray(L.Gamma2, maxMag); ClipArray(L.Beta2, maxMag);
        }
        ClipArray(model.GammaFinal, maxMag);
        ClipArray(model.BetaFinal, maxMag);
        ClipArray(model.WOut, maxMag);
        model.BOut = Math.Clamp(model.BOut, -maxMag, maxMag);
    }

    private static void ClipMatrix(double[][] m, int rows, double maxMag)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < m[r].Length; c++)
                m[r][c] = Math.Clamp(m[r][c], -maxMag, maxMag);
    }

    private static void ClipArray(double[] a, double maxMag)
    {
        for (int i = 0; i < a.Length; i++)
            a[i] = Math.Clamp(a[i], -maxMag, maxMag);
    }

    private static bool HasNonFinite(TransformerModel model)
    {
        if (!double.IsFinite(model.BOut)) return true;
        if (HasNonFiniteArray(model.WOut)) return true;
        if (HasNonFiniteArray(model.ClsToken)) return true;
        if (HasNonFiniteArray(model.GammaFinal)) return true;
        if (HasNonFiniteArray(model.BetaFinal)) return true;
        for (int f = 0; f < model.F; f++)
            if (HasNonFiniteArray(model.We[f]) || HasNonFiniteArray(model.Be[f])) return true;
        for (int l = 0; l < model.NumLayers; l++)
        {
            var L = model.Layers[l];
            for (int d = 0; d < model.EmbedDim; d++)
                if (HasNonFiniteArray(L.Wq[d]) || HasNonFiniteArray(L.Wk[d]) ||
                    HasNonFiniteArray(L.Wv[d]) || HasNonFiniteArray(L.Wo[d])) return true;
            if (HasNonFiniteArray(L.Gamma1) || HasNonFiniteArray(L.Beta1)) return true;
            if (HasNonFiniteArray(L.Gamma2) || HasNonFiniteArray(L.Beta2)) return true;
            for (int d = 0; d < model.EmbedDim; d++)
                if (HasNonFiniteArray(L.Wff1[d])) return true;
            if (HasNonFiniteArray(L.Bff1)) return true;
            for (int d = 0; d < model.FfnDim; d++)
                if (HasNonFiniteArray(L.Wff2[d])) return true;
            if (HasNonFiniteArray(L.Bff2)) return true;
        }
        return false;
    }

    private static bool HasNonFiniteArray(double[] arr)
    {
        for (int i = 0; i < arr.Length; i++) if (!double.IsFinite(arr[i])) return true;
        return false;
    }

    private static double ComputeSharpe(double[] returns, int count)
    {
        if (count < 2) return 0.0;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += returns[i];
        double mean = sum / count;
        double varSum = 0;
        for (int i = 0; i < count; i++) { double d = returns[i] - mean; varSum += d * d; }
        double std = Math.Sqrt(varSum / (count - 1));
        return std > 1e-10 ? mean / std * Math.Sqrt(252) : 0.0;
    }

    private static double StdDev(IEnumerable<double> values, double mean)
    {
        double sum = 0; int count = 0;
        foreach (double v in values) { sum += (v - mean) * (v - mean); count++; }
        return count > 1 ? Math.Sqrt(sum / (count - 1)) : 0.0;
    }

    private static double ComputeSharpeTrend(List<double> sharpePerFold)
    {
        if (sharpePerFold.Count < 3) return 0.0;
        int n = sharpePerFold.Count;
        double xMean = (n - 1) / 2.0;
        double yMean = 0; foreach (var s in sharpePerFold) yMean += s; yMean /= n;
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) { double dx = i - xMean; num += dx * (sharpePerFold[i] - yMean); den += dx * dx; }
        return den > 1e-15 ? num / den : 0.0;
    }

    private static double SampleGaussian(Random rng, double std)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return std * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
