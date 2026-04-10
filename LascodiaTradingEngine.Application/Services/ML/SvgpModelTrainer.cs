using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;
using static LascodiaTradingEngine.Application.Services.ML.MLTrainerHelpers;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade Sparse Variational Gaussian Process (SVGP) trainer (Rec #447).
///
/// Implements the Titsias (2009) sparse GP framework with:
/// <list type="bullet">
///   <item>True variational inference: optimises q(u)=N(m,S) jointly with ARD kernel
///         hyperparameters via ELBO maximisation (Adam, TorchSharp GPU).</item>
///   <item>ARD RBF kernel: per-feature length scales l_d, optimised end-to-end.</item>
///   <item>Exact posterior predictive mean AND variance at inference time, providing
///         genuine Bayesian uncertainty quantification.</item>
///   <item>Gauss-Hermite quadrature (10-point) for the Bernoulli-sigmoid likelihood
///         expectation in the ELBO.</item>
///   <item>Leakage-free walk-forward CV with data-adaptive ARD heuristic per fold.</item>
///   <item>Multi-stage calibration (Platt → Isotonic PAVA → Temperature scaling).</item>
///   <item>GP posterior variance used directly as uncertainty proxy throughout the
///         pipeline (conformal, meta-label, abstention gate).</item>
/// </list>
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.Svgp)]
public sealed partial class SvgpModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "svgp";
    private const string ModelVersion = "6.0";
    private const int    DefaultM     = 64;    // adaptive: clamp(N/10, 20, 200) when not specified
    private const int    MaxDefaultM  = 200;
    private const int    MinDefaultM  = 20;
    private const double DefaultNoise = 0.1;
    private const double DefaultSf2   = 1.0;
    private const double DefaultSharpeAnnualisationFactor = 252.0;
    private const int    DefaultMiniBatchSize = 256;

    // Adam hyperparameters
    private const double AdamLr      = 0.02;
    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;
    private const int    ElboEpochs  = 250;
    private const int    ElboPatience = 30;
    private const int    CvElboEpochs   = 40;   // lighter per-fold pass
    private const int    CvElboPatience = 5;

    // ARD length-scale bounds (log-space clamps)
    private const float LsLogMin = -3f;   // l ≥ exp(-3) ≈ 0.05
    private const float LsLogMax =  3f;   // l ≤ exp( 3) ≈ 20
    private const float SfLogMin = -2f;   // σ_f ≥ exp(-2) ≈ 0.14
    private const float SfLogMax =  4f;   // σ_f ≤ exp( 4) ≈ 55
    private const float NoiseLogMin = -6f;
    private const float NoiseLogMax =  1f;
    private const float SDiagLogMin = -10f;
    private const float SDiagLogMax =  2f;
    private const float Jitter = 1e-5f;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false, MaxDepth = 64 };

    // ── 10-Point Gauss-Hermite quadrature (physicist convention) ─────────────
    // Nodes t_k and weights w_k satisfy: ∫ f(x) exp(-x²) dx ≈ Σ w_k f(t_k)
    // Σ w_k = √π.  For E_{Z~N(0,1)}[f(Z)] use: (1/√π) Σ w_k f(√2 t_k)

    private static readonly float[] GhNodes = [
        -3.4361591188377376f, -2.5327316742327897f, -1.7566836492998817f,
        -1.0366108297895136f, -0.3429013272237046f,
         0.3429013272237046f,  1.0366108297895136f,  1.7566836492998817f,
         2.5327316742327897f,  3.4361591188377376f,
    ];
    private static readonly float[] GhWeightsNorm = [   // w_k / √π
        4.310654368978817e-6f, 7.580709343866571e-4f, 1.911985333291540e-2f,
        1.354837029932780e-1f, 3.446423349376031e-1f,
        3.446423349376031e-1f, 1.354837029932780e-1f, 1.911985333291540e-2f,
        7.580709343866571e-4f, 4.310654368978817e-6f,
    ];

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<SvgpModelTrainer> _logger;

    public SvgpModelTrainer(ILogger<SvgpModelTrainer> logger) => _logger = logger;

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

    // ── Core training logic ───────────────────────────────────────────────────

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        // ── 0. Input validation ──────────────────────────────────────────────
        if (samples.Count == 0)
            throw new InvalidOperationException("SvgpModelTrainer: no training samples provided.");

        int F = samples[0].Features.Length;
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].Features.Length != F)
                throw new InvalidOperationException(
                    $"SvgpModelTrainer: inconsistent feature count — sample 0 has {F}, sample {i} has {samples[i].Features.Length}.");

        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"SvgpModelTrainer needs at least {hp.MinSamples} samples; got {samples.Count}.");

        // Adaptive M: if not specified, use clamp(N/10, MinDefaultM, MaxDefaultM)
        int M = Math.Min(
            hp.SvgpInducingM is > 0
                ? hp.SvgpInducingM.Value
                : Math.Clamp(samples.Count / 10, MinDefaultM, MaxDefaultM),
            samples.Count / 2);

        // ── 0b. Incremental update fast-path ─────────────────────────────────
        if (hp.UseIncrementalUpdate && warmStart?.Type == ModelType && hp.DensityRatioWindowDays > 0)
        {
            int barsPerDay  = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "SVGP incremental update: fine-tuning on last {N}/{Total} samples",
                    recentCount, samples.Count);
                return Train(samples[^recentCount..].ToList(),
                    hp with { UseIncrementalUpdate = false },
                    warmStart, parentModelId, ct);
            }
        }

        int n       = samples.Count;
        int embargo = hp.EmbargoBarCount;

        // ── 1. Leakage-free split indices ────────────────────────────────────
        double trainRatio = n < 500 ? 0.80 : 0.70;
        double calRatio   = n < 500 ? 0.10 : 0.15;
        int trainEnd    = (int)(n * trainRatio);
        int calEnd      = (int)(n * (trainRatio + calRatio));
        int trainStdEnd = Math.Max(0, trainEnd - embargo);

        // ── 2. Z-score standardisation (train split only, no leakage) ────────
        var rawTrainFeatures = new List<float[]>(trainStdEnd);
        for (int i = 0; i < trainStdEnd; i++) rawTrainFeatures.Add(samples[i].Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawTrainFeatures);

        var allStd = new List<TrainingSample>(n);
        foreach (var s in samples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });

        var trainSet = allStd[..trainStdEnd];
        int calStart = Math.Min(trainEnd + embargo, calEnd);
        var calSet   = allStd[calStart..(calEnd < n ? calEnd : n)];
        var testSet  = allStd[Math.Min(calEnd + embargo, n)..];

        if (trainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"SvgpModelTrainer: insufficient training samples after splits: {trainSet.Count} < {hp.MinSamples}");

        double sharpeAnnFactor = hp.SharpeAnnualisationFactor > 0.0
            ? hp.SharpeAnnualisationFactor : DefaultSharpeAnnualisationFactor;

        _logger.LogInformation(
            "SvgpModelTrainer starting: N={N} F={F} M={M} train={Tr} cal={Cal} test={Te}",
            n, F, M, trainSet.Count, calSet.Count, testSet.Count);

        // ── 2b. Stationarity gate (ADF) + fractional differencing ────────────
        double fracDiffD = hp.FracDiffD;
        {
            int nonStat = 0;
            for (int j = 0; j < F; j++)
            {
                var series = trainSet.Select(s => (double)s.Features[j]).ToArray();
                if (MLFeatureHelper.AdfTest(series) > 0.05) nonStat++;
            }
            if (nonStat > F * 0.30)
            {
                if (fracDiffD == 0.0)
                {
                    fracDiffD = 0.4;
                    _logger.LogWarning(
                        "SVGP stationarity gate: {NonStat}/{Total} features have unit root — auto-applying FracDiffD={D:F1}.",
                        nonStat, F, fracDiffD);
                }
                else if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "SVGP stationarity gate: {NonStat}/{Total} features have unit root — applying FracDiffD={D:F2} from hyperparams.",
                        nonStat, F, fracDiffD);
                }
            }
        }
        if (fracDiffD > 0.0)
        {
            allStd   = MLFeatureHelper.ApplyFractionalDifferencing(allStd, F, fracDiffD);
            trainSet = allStd[..trainStdEnd];
            calSet   = allStd[calStart..(calEnd < n ? calEnd : n)];
            testSet  = allStd[Math.Min(calEnd + embargo, n)..];
        }

        // ── 2c. Class-imbalance gate ─────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in trainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / trainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException($"SVGP: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < 0.35 || buyRatio > 0.65)
                _logger.LogWarning("SVGP class imbalance: Buy={Buy:P1}, Sell={Sell:P1}.", buyRatio, 1.0 - buyRatio);
        }

        var rng    = new Random(hp.ElmOuterSeed > 0 ? hp.ElmOuterSeed : 42);
        var device = cuda.is_available() ? CUDA : CPU;

        // ── 3. Walk-forward CV (with data-adaptive ARD per fold) ──────────────
        ct.ThrowIfCancellationRequested();
        var (cvResult, equityCurveGateFailed) =
            RunWalkForwardCV(allStd, hp, F, M, sharpeAnnFactor, rng, device, ct);
        _logger.LogInformation(
            "SVGP walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgSharpe={Sharpe:F2} gate={Gate}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgSharpe, equityCurveGateFailed ? "FAILED" : "passed");

        if (equityCurveGateFailed)
        {
            if (warmStart is not null)
            {
                _logger.LogWarning("SVGP equity-curve gate failed — returning warm-start snapshot as fallback.");
                byte[] fallbackBytes = JsonSerializer.SerializeToUtf8Bytes(warmStart, JsonOpts);
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, fallbackBytes);
            }
            return new TrainingResult(
                new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0), cvResult, []);
        }

        ct.ThrowIfCancellationRequested();

        // ── 4. K-means++ inducing point initialisation ────────────────────────
        double[][] inducingPts = KMeansInit(trainSet, M, F, new Random(rng.Next()));
        inducingPts            = KMeansRefine(trainSet, inducingPts, F, iters: 30);

        // Warm-start: import inducing points from prior snapshot when geometry matches.
        // New format: double[M][F] (each sub-array is one inducing point).
        // Legacy format: double[1][M*F] (single flat array) — handle both for backward compat.
        bool warmStartZImported = false;
        double warmStartZDisplacement = 0.0;
        if (warmStart?.SvgpInducingPoints is { Length: > 0 } priorIp)
        {
            bool isNewFormat = priorIp.Length == M && priorIp[0].Length == F;
            bool isLegacyFlat = priorIp.Length == 1 && priorIp[0].Length == M * F;

            if (isNewFormat)
            {
                _logger.LogInformation("SVGP warm-start: importing {M} inducing points from prior snapshot.", M);
                for (int m = 0; m < M; m++)
                    Array.Copy(priorIp[m], inducingPts[m], F);

                // Snapshot pre-refinement positions to measure displacement
                var preRefine = inducingPts.Select(p => p.ToArray()).ToArray();
                inducingPts = KMeansRefine(trainSet, inducingPts, F, iters: 5);
                warmStartZDisplacement = MeanInducingDisplacement(preRefine, inducingPts, M, F);
                warmStartZImported = true;
            }
            else if (isLegacyFlat)
            {
                _logger.LogInformation("SVGP warm-start: importing {M} inducing points (legacy flat format).", M);
                for (int m = 0; m < M; m++)
                    for (int fi = 0; fi < F; fi++)
                        inducingPts[m][fi] = priorIp[0][m * F + fi];

                var preRefine = inducingPts.Select(p => p.ToArray()).ToArray();
                inducingPts = KMeansRefine(trainSet, inducingPts, F, iters: 5);
                warmStartZDisplacement = MeanInducingDisplacement(preRefine, inducingPts, M, F);
                warmStartZImported = true;
            }
        }

        // ── 5. Importance weights (density-ratio × covariate-shift) ──────────
        double[] densityWeights = hp.DensityRatioWindowDays > 0 && trainSet.Count >= 50
            ? ComputeDensityRatioWeights(trainSet, F, hp.DensityRatioWindowDays)
            : UniformWeights(trainSet.Count);

        double[] covShiftWeights = warmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentQbp
            ? ComputeCovariateShiftWeights(trainSet, parentQbp, F)
            : UniformWeights(trainSet.Count);

        double[] combinedWeights = BlendImportanceWeights(densityWeights, covShiftWeights, trainSet.Count);

        // ── 6. True SVGP ELBO optimisation (TorchSharp, autograd) ────────────
        // (device already selected above, before CV)

        // Initialise ARD length scales from per-feature std of training data
        double[] initArdLs = ComputeArdLsFromData(trainSet, F);

        // Warm-start variational params if snapshot matches — but invalidate
        // if K-means refinement shifted the inducing geometry too far.
        const double MaxWarmStartDisplacement = 1.0; // in standardized feature space
        bool varParamsValid = warmStartZImported && warmStartZDisplacement <= MaxWarmStartDisplacement;
        if (warmStartZImported && !varParamsValid)
            _logger.LogWarning(
                "SVGP warm-start: inducing point displacement {Disp:F3} > {Max:F1} — discarding variational params, using fresh initialization.",
                warmStartZDisplacement, MaxWarmStartDisplacement);
        else if (warmStartZImported)
            _logger.LogInformation(
                "SVGP warm-start: inducing point displacement {Disp:F3} — variational params retained.",
                warmStartZDisplacement);

        double[]? warmM     = varParamsValid && warmStart?.SvgpVariationalMean?.Length == M
            ? warmStart.SvgpVariationalMean : null;
        double[]? warmLogS  = varParamsValid && warmStart?.SvgpVariationalLogSDiag?.Length == M
            ? warmStart.SvgpVariationalLogSDiag : null;
        double[]? warmLogLs = warmStartZImported && warmStart?.SvgpArdLengthScales?.Length == F
            ? warmStart.SvgpArdLengthScales.Select(v => Math.Log(v)).ToArray() : null;
        // Warm-start full-rank L_S off-diagonal if geometry matches and displacement is acceptable
        double[]? warmLSOffDiag = varParamsValid && warmLogS != null
            && warmStart?.SvgpVariationalLSOffDiag?.Length == M * M
            && warmStart.SvgpVariationalLSOffDiag.All(double.IsFinite)
            ? warmStart.SvgpVariationalLSOffDiag : null;
        // Warm-start kernel hyperparams (signal variance, noise) — these are global
        // scale parameters independent of inducing geometry, so always import when available.
        double? warmLogSf = warmStartZImported && warmStart!.SvgpSignalVariance > 1e-12
            ? 0.5 * Math.Log(warmStart.SvgpSignalVariance) : null;   // log(σ_f) = ½ log(σ_f²)
        double? warmLogNoi = warmStartZImported && warmStart!.SvgpNoiseVariance > 1e-12
            ? Math.Log(warmStart.SvgpNoiseVariance) : null;           // log(σ_n)

        int miniBatch = hp.SvgpMiniBatchSize is > 0
            ? hp.SvgpMiniBatchSize.Value : DefaultMiniBatchSize;

        ct.ThrowIfCancellationRequested();
        var svgpState = OptimizeSvgpElbo(
            trainSet, inducingPts, combinedWeights,
            M, F, initArdLs,
            warmM, warmLogS, warmLogLs, warmLSOffDiag,
            warmLogSf, warmLogNoi,
            ElboEpochs, ElboPatience, device, ct,
            miniBatchSize: miniBatch);

        _logger.LogInformation(
            "SVGP ELBO optimised: ELBO={Elbo:F2} sf²={Sf2:F4} noise={Noise:F4}",
            svgpState.Elbo,
            Math.Exp(2 * Math.Clamp(svgpState.LogSf, SfLogMin, SfLogMax)),
            Math.Exp(Math.Clamp(svgpState.LogNoise, NoiseLogMin, NoiseLogMax)));

        // Extract alpha = K_mm^{-1} m for backward-compatible mean-only inference.
        // inducingPts was already updated in-place by OptimizeSvgpElbo (joint Z opt).
        double[] alphaData = svgpState.Alpha;
        int badAlpha = 0;
        for (int m = 0; m < alphaData.Length; m++)
            if (!double.IsFinite(alphaData[m])) { alphaData[m] = 0.0; badAlpha++; }
        if (badAlpha > 0)
            _logger.LogWarning("SVGP: sanitized {N}/{M} non-finite alpha values.", badAlpha, M);

        double[] ardLs = svgpState.ArdLogLs.Select(v => Math.Exp(Math.Clamp(v, LsLogMin, LsLogMax))).ToArray();
        double sf2Val  = Math.Exp(2 * Math.Clamp(svgpState.LogSf, SfLogMin, SfLogMax));
        double noiseVal = Math.Exp(Math.Clamp(svgpState.LogNoise, NoiseLogMin, NoiseLogMax));

        _logger.LogInformation(
            "SVGP ARD ls (min={Min:F3} max={Max:F3} mean={Mean:F3})",
            ardLs.Min(), ardLs.Max(), ardLs.Average());

        ct.ThrowIfCancellationRequested();

        // ── 7. Posterior prediction (mean + variance) on cal and test sets ────
        var (calMeans,  calVars)  = PredictWithVariance(calSet,  svgpState, device);
        var (testMeans, testVars) = PredictWithVariance(testSet, svgpState, device);

        // ── 8. Platt calibration (Adam + L2, early stopping) ─────────────────
        var (plattA, plattB) = calSet.Count >= 5 ? FitPlatt(calMeans, calSet) : (1.0, 0.0);
        _logger.LogInformation("SVGP Platt: A={A:F4} B={B:F4}", plattA, plattB);

        // ── 8b. Class-conditional Platt (Buy / Sell) ─────────────────────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) = calSet.Count >= 10
            ? FitClassConditionalPlatt(calMeans, calSet)
            : (1.0, 0.0, 1.0, 0.0); // identity on logit scale

        // ── 9. Platt-calibrated probabilities ────────────────────────────────
        float[] calibCalProbs  = CalibratePlatt(calMeans,  plattA, plattB);
        float[] calibTestProbs = CalibratePlatt(testMeans, plattA, plattB);

        // ── 9b. Isotonic PAVA ─────────────────────────────────────────────────
        double[] isotonicBp = calSet.Count >= 10
            ? FitIsotonicCalibration(calibCalProbs, calSet)
            : [];
        _logger.LogInformation("SVGP isotonic: {N} PAVA breakpoints", isotonicBp.Length / 2);

        // ── 9c. Temperature scaling ───────────────────────────────────────────
        float[] isoCalProbs = ApplyIsotonicArray(calibCalProbs, isotonicBp);
        double tempScale = hp.FitTemperatureScale && calSet.Count >= 10
            ? FitTemperatureScaling(isoCalProbs, calSet)
            : 1.0;
        _logger.LogInformation("SVGP temperature T={T:F4}", tempScale);

        // ── 9d. Fully-calibrated probs (Platt + Isotonic + Temperature) ───────
        float[] finalCalProbs  = ApplyFullCalibration(calMeans,  plattA, plattB, isotonicBp, tempScale);
        float[] finalTestProbs = ApplyFullCalibration(testMeans, plattA, plattB, isotonicBp, tempScale);

        // ── 10. ECE and Brier Skill Score (fully-calibrated test set) ─────────
        double ece = ComputeEce(finalTestProbs, testSet);
        double bss = ComputeBss(finalTestProbs, testSet);
        _logger.LogInformation("SVGP ECE={Ece:F4} BSS={Bss:F4}", ece, bss);

        // ── 11. EV-optimal threshold and Kelly fraction ───────────────────────
        double optimalThreshold = ComputeOptimalThreshold(
            finalCalProbs, calSet, hp.ThresholdSearchMin, hp.ThresholdSearchMax);
        double avgKellyFraction = ComputeAvgKellyFraction(finalCalProbs, calSet);
        _logger.LogInformation(
            "SVGP threshold={Thr:F2} Kelly={Kelly:F4}", optimalThreshold, avgKellyFraction);

        // ── 12. Magnitude regressors (before final eval so RMSE is correct) ──
        var (magWeights, magBias) = FitMagnitudeRegressor(trainSet, F);

        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau);

        // ── 13. Final evaluation on test set ─────────────────────────────────
        var finalMetrics = ComputeFullMetrics(finalTestProbs, testSet, optimalThreshold, sharpeAnnFactor);
        _logger.LogInformation(
            "SVGP test — acc={Acc:P1} f1={F1:F3} ev={EV:F4} sharpe={Sharpe:F2} ELBO={Elbo:F2}",
            finalMetrics.Accuracy, finalMetrics.F1,
            finalMetrics.ExpectedValue, finalMetrics.SharpeRatio, svgpState.Elbo);

        ct.ThrowIfCancellationRequested();

        // ── 14. Permutation feature importance (test + cal, parallel) ─────────
        float[] featureImportance = testSet.Count >= 10
            ? ComputePermutationImportance(testSet, inducingPts, alphaData, F, M, ardLs, sf2Val, plattA, plattB, ct)
            : new float[F];

        if (featureImportance.Any(v => v > 0))
        {
            var top5 = featureImportance
                .Select((imp, idx) => (Importance: imp,
                    Name: idx < MLFeatureHelper.FeatureNames.Length
                        ? MLFeatureHelper.FeatureNames[idx] : $"F{idx}"))
                .OrderByDescending(x => x.Importance).Take(5);
            _logger.LogInformation(
                "SVGP top-5 features (test): {Features}",
                string.Join(", ", top5.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        float[] calImportanceScores = calSet.Count >= 10
            ? ComputePermutationImportance(calSet, inducingPts, alphaData, F, M, ardLs, sf2Val, plattA, plattB, ct)
            : new float[F];

        // ── 15. Feature quantile breakpoints (PSI monitoring) ─────────────────
        double[][] featureQuantileBreakpoints = ComputeQuantileBreakpoints(trainSet, F);

        // ── 16. Split-conformal q̂ using GP variance as nonconformity measure ──
        // Nonconformity: α_i = σ²*(x_i) + |p_i - y_i| (uncertainty-aware score)
        double conformalQHat = 0.5;
        if (calSet.Count >= 10)
        {
            double coverage = Math.Clamp(hp.ConformalCoverage, 0.5, 0.99);
            var scores = new double[calSet.Count];
            for (int i = 0; i < calSet.Count; i++)
            {
                double yv  = calSet[i].Direction > 0 ? 1.0 : 0.0;
                double gpU = calVars[i];  // GP posterior variance (true uncertainty)
                double calErr = Math.Abs(finalCalProbs[i] - (float)yv);
                scores[i] = gpU + calErr;   // uncertainty-aware nonconformity score
            }
            Array.Sort(scores);
            int confIdx = Math.Clamp(
                (int)Math.Ceiling(coverage * (scores.Length + 1)) - 1,
                0, scores.Length - 1);
            conformalQHat = scores[confIdx];
        }
        _logger.LogInformation(
            "SVGP conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);

        // ── 17. Meta-label secondary classifier (GP variance as uncertainty) ──
        // MetaDim = 2 + min(F, 5): [calibP, gpStd, top-min(F,5) raw features]
        int metaDim = 2 + Math.Min(F, 5);
        var (metaLabelWeights, metaLabelBias) = calSet.Count >= 10
            ? FitMetaLabelModel(finalCalProbs, calVars, calSet, F, metaDim)
            : (new double[metaDim], 0.0);

        // ── 17b. Abstention gate (GP variance + meta-score) ───────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = calSet.Count >= 10
            ? FitAbstentionModel(finalCalProbs, calVars, calSet, metaLabelWeights, metaLabelBias, F, metaDim)
            : (new double[3], 0.0, 0.5);

        // ── 17c. Decision boundary stats (GP gradient-norm mean/std) ──────────
        var (dbMean, dbStd) = calSet.Count >= 10
            ? ComputeDecisionBoundaryStats(calSet, inducingPts, alphaData, M, F, ardLs, sf2Val)
            : (0.0, 0.0);

        // ── 17d. Durbin-Watson on magnitude residuals ─────────────────────────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
            _logger.LogWarning(
                "SVGP magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2}). Consider AR features.",
                durbinWatson, hp.DurbinWatsonThreshold);

        // ── 17e. MI feature redundancy check ─────────────────────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "SVGP MI redundancy: {N} pairs above threshold {T:F2}: {Pairs}",
                    redundantPairs.Length, hp.MutualInfoRedundancyThreshold,
                    string.Join(", ", redundantPairs));
        }

        // ── 18. Build model snapshot ──────────────────────────────────────────
        // Store inducing points as double[M][F] so the inference engine reads M = Z.Length correctly.
        var svgpPoints = inducingPts.Select(p => p.ToArray()).ToArray();
        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = means,
            Stds                       = stds,
            BaseLearnersK              = 1,
            Weights                    = new[] { alphaData },           // K_mm^{-1}m for mean inference
            Biases                     = new[] { sf2Val },              // signal variance σ_f²
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
            TrainedAtUtc               = DateTime.UtcNow,
            SvgpInducingPoints         = svgpPoints,
            // ── True SVGP variational parameters (full-rank S = L_S L_S^T) ────
            SvgpVariationalMean        = svgpState.VarMean,
            SvgpVariationalLogSDiag    = svgpState.LogSDiag,
            SvgpVariationalLSOffDiag   = svgpState.LowerSFlat,
            SvgpArdLengthScales        = ardLs,
            SvgpSignalVariance         = sf2Val,
            SvgpNoiseVariance          = noiseVal,
            // ─────────────────────────────────────────────────────────────────
            OptimalThreshold           = optimalThreshold,
            Ece                        = ece,
            BrierSkillScore            = bss,
            FeatureImportance          = featureImportance,
            FeatureImportanceScores    = Array.ConvertAll(calImportanceScores, v => (double)v),
            FeatureQuantileBreakpoints = featureQuantileBreakpoints,
            ConformalQHat              = conformalQHat,
            IsotonicBreakpoints        = isotonicBp,
            TemperatureScale           = tempScale,
            PlattABuy                  = plattABuy,
            PlattBBuy                  = plattBBuy,
            PlattASell                 = plattASell,
            PlattBSell                 = plattBSell,
            AvgKellyFraction           = avgKellyFraction,
            MetaLabelWeights           = metaLabelWeights,
            MetaLabelBias              = metaLabelBias,
            MetaLabelThreshold         = 0.5,
            AbstentionWeights          = abstentionWeights,
            AbstentionBias             = abstentionBias,
            AbstentionThreshold        = abstentionThreshold,
            MagQ90Weights              = magQ90Weights,
            MagQ90Bias                 = magQ90Bias,
            DecisionBoundaryMean       = dbMean,
            DecisionBoundaryStd        = dbStd,
            DurbinWatsonStatistic      = durbinWatson,
            RedundantFeaturePairs      = redundantPairs,
            WalkForwardSharpeTrend     = cvResult.SharpeTrend,
            FeatureStabilityScores     = cvResult.FeatureStabilityScores ?? [],
            OobAccuracy                = 0.0,
            SanitizedLearnerCount      = badAlpha,
            GenerationNumber           = warmStart is not null ? warmStart.GenerationNumber + 1 : 1,
            ParentModelId              = parentModelId ?? 0,
            FracDiffD                  = fracDiffD,
            AgeDecayLambda             = hp.AgeDecayLambda,
            ConformalCoverage          = hp.ConformalCoverage,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOpts),
            MetaWeights                = [],
            MetaBias                   = 0.0,
            EnsembleDiversity          = 0.0,
            OobPrunedLearnerCount      = 0,
        };

        byte[] modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);
        _logger.LogInformation(
            "SvgpModelTrainer complete: acc={Acc:P1} sharpe={Sharpe:F2} ELBO={ELBO:F2} ece={ECE:F4} bss={BSS:F4} gen={Gen}",
            finalMetrics.Accuracy, finalMetrics.SharpeRatio, svgpState.Elbo, ece, bss, snapshot.GenerationNumber);

        return new TrainingResult(finalMetrics, cvResult, modelBytes);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // TRUE SVGP VARIATIONAL INFERENCE
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds the optimised SVGP variational state returned by <see cref="OptimizeSvgpElbo"/>.
    /// </summary>
    private sealed class SvgpState
    {
        public double[] InducingFlat { get; }  // [M*F]   flat row-major inducing points (updated by optimiser)
        public double[] VarMean      { get; }  // [M]     variational mean m
        public double[] LogSDiag     { get; }  // [M]     log diagonal of L_S (Cholesky of S)
        public double[] LowerSFlat   { get; }  // [M*M]   strictly-lower-triangular part of L_S, row-major
        public double[] ArdLogLs     { get; }  // [F]     per-feature log length-scales
        public double   LogSf        { get; }  // scalar  log signal std
        public double   LogNoise     { get; }  // scalar  log noise std
        public int      M            { get; }
        public int      F            { get; }
        public double   Elbo         { get; }
        public double[] Alpha        { get; }  // [M]     K_mm^{-1} m (derived, for fast mean inference)

        public SvgpState(
            double[] inducingFlat, double[] varMean, double[] logSDiag, double[] lowerSFlat,
            double[] ardLogLs, double logSf, double logNoise,
            int m, int f, double elbo, double[] alpha)
        {
            InducingFlat = inducingFlat; VarMean = varMean; LogSDiag = logSDiag;
            LowerSFlat = lowerSFlat; ArdLogLs = ardLogLs;
            LogSf = logSf; LogNoise = logNoise;
            M = m; F = f; Elbo = elbo; Alpha = alpha;
        }
    }

    /// <summary>
    /// Optimises SVGP variational parameters via TorchSharp Adam + ELBO maximisation.
    /// <list type="bullet">
    ///   <item>Inducing point locations Z are jointly optimised with the variational parameters.</item>
    ///   <item>Full-rank variational covariance S = L_S L_S^T (Cholesky parameterisation).</item>
    ///   <item>TorchSharp built-in Adam keeps all parameter updates on the GPU.</item>
    ///   <item>Mini-batch ELBO: random subsets of size <paramref name="miniBatchSize"/> per epoch.</item>
    ///   <item>Importance weights normalised to sum-to-1 before use.</item>
    /// </list>
    /// Variational posterior: q(u) = N(m, L_S L_S^T).
    /// Kernel: K(x,x') = σ_f² exp(-½ Σ_d (x_d-x'_d)²/l_d²) [ARD RBF].
    /// ELBO = Σ_i E_q[log p(y_i|f_i)] - KL[q(u)||p(u)].
    /// Expectation via 10-point Gauss-Hermite quadrature.
    /// </summary>
    private SvgpState OptimizeSvgpElbo(
        List<TrainingSample> trainSet,
        double[][]           inducingPts,
        double[]             importanceWeights,
        int M, int F,
        double[]             initArdLs,
        double[]?            warmVarMean,
        double[]?            warmLogSDiag,
        double[]?            warmArdLogLs,
        double[]?            warmLSOffDiag,
        double?              warmLogSf,
        double?              warmLogNoi,
        int epochs, int patience, Device device,
        CancellationToken ct,
        int miniBatchSize = DefaultMiniBatchSize)
    {
        int N = trainSet.Count;
        int B = Math.Min(N, Math.Max(miniBatchSize, 32));
        var batchRng = new Random(42);
        int[] allIdx = Enumerable.Range(0, N).ToArray();

        // ── Normalise importance weights to sum-to-1 ─────────────────────────
        double wSum = importanceWeights.Sum();
        float[] wNorm = importanceWeights.Select(v => (float)(v / Math.Max(wSum, 1e-15))).ToArray();

        // ── Parameter initialisations ─────────────────────────────────────────
        float[] zFlat       = inducingPts.SelectMany(p => p.Select(v => (float)v)).ToArray();
        float[] initM       = warmVarMean  != null ? warmVarMean.Select(v => (float)v).ToArray()
                                                   : new float[M];
        float[] initLogDiag = warmLogSDiag != null ? warmLogSDiag.Select(v => (float)v).ToArray()
                                                   : Enumerable.Repeat(-2.0f, M).ToArray();
        float[] initLogLs   = warmArdLogLs != null ? warmArdLogLs.Select(v => (float)v).ToArray()
                                                   : initArdLs.Select(v => (float)Math.Log(v)).ToArray();

        // ── GH quadrature constants (device-side) ────────────────────────────
        using var ghN = tensor(GhNodes,       new long[] { GhNodes.Length },       ScalarType.Float32).to(device);
        using var ghW = tensor(GhWeightsNorm, new long[] { GhWeightsNorm.Length }, ScalarType.Float32).to(device);

        // ── Optimisable parameters (Parameter = Tensor with requires_grad=true) ──
        // z_var  : inducing locations (now jointly optimised)
        // m_var  : variational mean
        // l_s_diag: log-diagonal of L_S (ensures positive diagonal in Cholesky)
        // l_s_lower: strictly-lower-triangular part of L_S (unconstrained)
        // log_ls : per-feature log ARD length scales
        // log_sf : log signal std
        // log_noi: log noise std
        using var z_var     = new Parameter(tensor(zFlat,      new long[] { M, F }, ScalarType.Float32).to(device));
        using var m_var     = new Parameter(tensor(initM,       new long[] { M },    ScalarType.Float32).to(device));
        using var l_s_diag  = new Parameter(tensor(initLogDiag, new long[] { M },    ScalarType.Float32).to(device));
        float[] initLSLower = warmLSOffDiag != null
            ? warmLSOffDiag.Select(v => (float)v).ToArray()
            : new float[M * M];
        using var l_s_lower = new Parameter(tensor(initLSLower, new long[] { M, M }, ScalarType.Float32).to(device));
        using var log_ls    = new Parameter(tensor(initLogLs,   new long[] { F },    ScalarType.Float32).to(device));
        using var log_sf    = new Parameter(tensor(new float[] { warmLogSf.HasValue ? (float)warmLogSf.Value : 0.0f }, ScalarType.Float32).to(device));
        using var log_noi   = new Parameter(tensor(new float[] { warmLogNoi.HasValue ? (float)warmLogNoi.Value : (float)Math.Log(DefaultNoise) }, ScalarType.Float32).to(device));

        // ── TorchSharp Adam + cosine annealing LR schedule ──────────────────
        using var optimizer = torch.optim.Adam(
            new Parameter[] { z_var, m_var, l_s_diag, l_s_lower, log_ls, log_sf, log_noi },
            AdamLr, AdamBeta1, AdamBeta2, AdamEpsilon);
        var scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(
            optimizer, T_max: epochs, eta_min: AdamLr * 0.01);

        double   bestElbo    = double.NegativeInfinity;
        float[]  bestZ       = zFlat.ToArray();
        float[]  bestM       = initM.ToArray();
        float[]  bestLogDiag = initLogDiag.ToArray();
        float[]  bestLower   = new float[M * M];
        float[]  bestLogLs   = initLogLs.ToArray();
        float    bestLogSf   = warmLogSf.HasValue ? (float)warmLogSf.Value : 0.0f;
        float    bestLogNoi  = warmLogNoi.HasValue ? (float)warmLogNoi.Value : (float)Math.Log(DefaultNoise);
        int      noImprove   = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            // ── Mini-batch sampling (Fisher-Yates partial shuffle) ────────────
            for (int i = N - 1; i >= N - B && i > 0; i--)
            {
                int j = batchRng.Next(i + 1);
                (allIdx[i], allIdx[j]) = (allIdx[j], allIdx[i]);
            }
            int[] bIdx = allIdx[(N - B)..];

            var xBuf = new float[B * F];
            var yBuf = new float[B];
            var wBuf = new float[B];
            float wBufSum = 0f;
            for (int bi = 0; bi < B; bi++)
            {
                int si = bIdx[bi];
                trainSet[si].Features.CopyTo(xBuf, bi * F);
                yBuf[bi] = trainSet[si].Direction == 1 ? 1.0f : -1.0f;
                wBuf[bi] = wNorm[si];
                wBufSum += wNorm[si];
            }
            // Re-normalise mini-batch weights
            if (wBufSum > 1e-15f)
                for (int bi = 0; bi < B; bi++) wBuf[bi] /= wBufSum;
            else
                Array.Fill(wBuf, 1.0f / B);

            using var scope = NewDisposeScope();

            using var X_b = tensor(xBuf, new long[] { B, F }, ScalarType.Float32).to(device);
            using var y_b = tensor(yBuf, new long[] { B },    ScalarType.Float32).to(device);
            using var w_b = tensor(wBuf, new long[] { B },    ScalarType.Float32).to(device);

            // ── ARD kernel params (clamped) ───────────────────────────────────
            var ls   = log_ls.clamp(LsLogMin,    LsLogMax).exp();         // [F]
            var sf2  = log_sf.clamp(SfLogMin,    SfLogMax).exp().pow(2);  // scalar
            var nvar = log_noi.clamp(NoiseLogMin, NoiseLogMax).exp();      // scalar

            // ── K_xz [B, M] ───────────────────────────────────────────────────
            var diff_xz = X_b.unsqueeze(1) - z_var.unsqueeze(0);          // [B, M, F]
            var K_xz    = sf2 * (-0.5f * (diff_xz / ls.unsqueeze(0).unsqueeze(0))
                              .pow(2).sum(2L)).exp();                       // [B, M]

            // ── K_mm [M, M] with jitter ───────────────────────────────────────
            var diff_mm  = z_var.unsqueeze(1) - z_var.unsqueeze(0);       // [M, M, F]
            var K_mm_raw = sf2 * (-0.5f * (diff_mm / ls.unsqueeze(0).unsqueeze(0))
                               .pow(2).sum(2L)).exp();                      // [M, M]
            var K_mm     = K_mm_raw + (nvar + Jitter)
                           * torch.eye((long)M, dtype: ScalarType.Float32, device: device);

            // ── Cholesky L_mm [M, M] (progressive jitter retry on failure) ────
            var L_mm = CholeskyWithRetry(K_mm, M, device, _logger);

            // ── Full-rank S = L_S L_S^T ───────────────────────────────────────
            // L_S is lower-triangular with positive diagonal.
            var L_S = torch.diag(l_s_diag.clamp(SDiagLogMin, SDiagLogMax).exp())
                    + torch.tril(l_s_lower, diagonal: -1);                 // [M, M]

            // ── alpha_hat = L_mm^{-1} m  [M] ─────────────────────────────────
            var alpha_hat = torch.linalg.solve_triangular(
                                L_mm, m_var.unsqueeze(1), upper: false).squeeze(1);

            // ── V = L_mm^{-1} K_xz^T  [M, B] ────────────────────────────────
            var V = torch.linalg.solve_triangular(L_mm, K_xz.t(), upper: false);

            // ── U = L_mm^{-T} V  [M, B] ──────────────────────────────────────
            var U = torch.linalg.solve_triangular(L_mm.t(), V, upper: true);

            // ── W_S = L_S^T U  [M, B] (for full-rank variance) ───────────────
            var W_S = L_S.t().mm(U);

            // ── Posterior mean [B] and variance [B] ───────────────────────────
            var q_mean = V.t().mv(alpha_hat);                              // [B]
            var K_xx_d = sf2.expand((long)B);                              // [B]
            var q_var  = (K_xx_d - V.pow(2f).sum(0L) + W_S.pow(2f).sum(0L))
                         .clamp(min: 1e-6f);                               // [B]
            var q_std  = q_var.sqrt();                                     // [B]

            // ── GH quadrature: E_q[log σ(y*f)] ──────────────────────────────
            var gh_pts             = q_mean.unsqueeze(1)
                                   + (float)Math.Sqrt(2.0) * q_std.unsqueeze(1) * ghN.unsqueeze(0);
            var y_gh               = y_b.unsqueeze(1) * gh_pts;            // [B, G]
            var exp_lik_per_sample = (functional.logsigmoid(y_gh) * ghW.unsqueeze(0)).sum(1L);
            // Mini-batch unbiased ELBO: scale weighted sum by N
            var lik_term = (w_b * exp_lik_per_sample).sum() * (float)N;

            // ── KL[q(u) || p(u)] — full-rank S via Cholesky ──────────────────
            // tr(K_mm^{-1} S) = ||L_mm^{-1} L_S||_F²
            var LS_inv_LS = torch.linalg.solve_triangular(L_mm, L_S, upper: false);
            var tr_KinvS  = LS_inv_LS.pow(2f).sum();
            var quad_m    = alpha_hat.pow(2f).sum();
            var log_det_Kmm = 2f * L_mm.diagonal().log().sum();
            var log_det_S   = 2f * l_s_diag.clamp(SDiagLogMin, SDiagLogMax).sum();
            var kl          = 0.5f * (tr_KinvS + quad_m - (float)M + log_det_Kmm - log_det_S);

            var elbo = lik_term - kl;

            // ── Backward + Adam step (entirely on GPU) ────────────────────────
            optimizer.zero_grad();
            (-elbo).backward();
            torch.nn.utils.clip_grad_norm_(
                new Parameter[] { z_var, m_var, l_s_diag, l_s_lower, log_ls, log_sf, log_noi },
                10.0);
            optimizer.step();
            scheduler.step();

            // ── Early stopping ────────────────────────────────────────────────
            double elboVal = elbo.item<float>();
            if (elboVal > bestElbo + 1e-4)
            {
                bestElbo    = elboVal;
                bestZ       = z_var.detach().cpu().data<float>().ToArray();
                bestM       = m_var.detach().cpu().data<float>().ToArray();
                bestLogDiag = l_s_diag.detach().cpu().data<float>().ToArray();
                bestLower   = l_s_lower.detach().cpu().data<float>().ToArray();
                bestLogLs   = log_ls.detach().cpu().data<float>().ToArray();
                bestLogSf   = log_sf.detach().cpu().data<float>()[0];
                bestLogNoi  = log_noi.detach().cpu().data<float>()[0];
                noImprove   = 0;
            }
            else if (++noImprove >= patience)
                break;
        }

        // ── Restore best params; update inducingPts from optimised Z ─────────
        for (int mi = 0; mi < M; mi++)
            for (int fi = 0; fi < F; fi++)
                inducingPts[mi][fi] = bestZ[mi * F + fi];

        double[] finalM2      = bestM.Select(v => (double)v).ToArray();
        double[] finalLogDiag = bestLogDiag.Select(v => (double)v).ToArray();
        double[] finalLower   = bestLower.Select(v => (double)v).ToArray();
        double[] finalLogLs   = bestLogLs.Select(v => (double)v).ToArray();

        double[] alpha = ComputeAlphaFromVariational(inducingPts, finalM2, finalLogLs, bestLogSf, bestLogNoi, M, F);

        return new SvgpState(
            inducingFlat: bestZ.Select(v => (double)v).ToArray(),
            varMean:      finalM2,
            logSDiag:     finalLogDiag,
            lowerSFlat:   finalLower,
            ardLogLs:     finalLogLs,
            logSf:        bestLogSf,
            logNoise:     bestLogNoi,
            m: M, f: F,
            elbo:  bestElbo,
            alpha: alpha);
    }

    /// <summary>Computes alpha = K_mm^{-1} m from the optimised variational parameters (pure C#).</summary>
    private static double[] ComputeAlphaFromVariational(
        double[][] Z, double[] m_var, double[] logLs, float logSf, float logNoise,
        int M, int F)
    {
        double sf2   = Math.Exp(2.0 * Math.Clamp(logSf,    SfLogMin, SfLogMax));
        double noise = Math.Exp(Math.Clamp((double)logNoise, NoiseLogMin, NoiseLogMax));
        double[] ls  = logLs.Select(v => Math.Exp(Math.Clamp(v, LsLogMin, LsLogMax))).ToArray();

        // Build K_mm (M×M)
        var L = new double[M][];
        for (int i = 0; i < M; i++) L[i] = new double[M];
        for (int i = 0; i < M; i++)
        for (int j = 0; j <= i; j++)
        {
            double sq = 0;
            for (int d = 0; d < F; d++)
            {
                double diff = (Z[i][d] - Z[j][d]) / ls[d];
                sq += diff * diff;
            }
            double k = sf2 * Math.Exp(-0.5 * sq);
            if (i == j) k += noise + 1e-5;
            L[i][j] = k;
            L[j][i] = k;
        }

        // Cholesky decomposition (Cholesky-Banachiewicz, lower triangular)
        var chol = new double[M][];
        for (int i = 0; i < M; i++) chol[i] = new double[M];
        for (int i = 0; i < M; i++)
        for (int j = 0; j <= i; j++)
        {
            double sum = L[i][j];
            for (int k = 0; k < j; k++) sum -= chol[i][k] * chol[j][k];
            chol[i][j] = j == i ? Math.Sqrt(Math.Max(sum, 1e-12)) : sum / chol[j][j];
        }

        // Solve K_mm @ alpha = m via Cholesky: forward then back substitution
        var tmp = new double[M];
        for (int i = 0; i < M; i++)
        {
            double s = m_var[i];
            for (int k = 0; k < i; k++) s -= chol[i][k] * tmp[k];
            tmp[i] = s / chol[i][i];
        }
        var alpha = new double[M];
        for (int i = M - 1; i >= 0; i--)
        {
            double s = tmp[i];
            for (int k = i + 1; k < M; k++) s -= chol[k][i] * alpha[k];
            alpha[i] = s / chol[i][i];
        }
        return alpha;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // POSTERIOR PREDICTION (MEAN + VARIANCE)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the SVGP posterior predictive mean p(y=1|x) and variance σ²*(x)
    /// for each sample using the true GP posterior (TorchSharp, GPU-accelerated).
    /// Variance uses full-rank S = L_S L_S^T: σ²*(x) = k(x,x) − ||V||² + ||L_S^T U||².
    /// Falls back to diagonal (legacy) when <see cref="SvgpState.LowerSFlat"/> is all-zero.
    /// </summary>
    private static (float[] Means, float[] Variances) PredictWithVariance(
        List<TrainingSample> samples, SvgpState state, Device device)
    {
        if (samples.Count == 0) return ([], []);

        int M = state.M, F = state.F, N = samples.Count;
        float[] ls   = state.ArdLogLs.Select(v => (float)Math.Exp(Math.Clamp(v, LsLogMin, LsLogMax))).ToArray();
        float sf2Val = (float)Math.Exp(2 * Math.Clamp(state.LogSf,    SfLogMin, SfLogMax));
        float noiseV = (float)Math.Exp(Math.Clamp(state.LogNoise, NoiseLogMin, NoiseLogMax));

        bool fullRank = state.LowerSFlat.Length == M * M;

        using var Z      = tensor(state.InducingFlat.Select(v => (float)v).ToArray(), new long[] { M, F }).to(device);
        using var X      = ToFeatureTensor(samples, device);
        using var ls_t   = tensor(ls, new long[] { F }, ScalarType.Float32).to(device);
        using var m_var  = tensor(state.VarMean.Select(v => (float)v).ToArray(),  new long[] { M }, ScalarType.Float32).to(device);
        using var log_sd = tensor(state.LogSDiag.Select(v => (float)v).ToArray(), new long[] { M }, ScalarType.Float32).to(device);

        using (no_grad())
        {
            // K_xz [N, M]
            using var diff_xz = X.unsqueeze(1) - Z.unsqueeze(0);
            using var K_xz    = sf2Val * (-0.5f * (diff_xz / ls_t.unsqueeze(0).unsqueeze(0))
                                .pow(2f).sum(2L)).exp();

            // K_mm [M, M] with jitter
            using var diff_mm  = Z.unsqueeze(1) - Z.unsqueeze(0);
            using var K_mm_raw = sf2Val * (-0.5f * (diff_mm / ls_t.unsqueeze(0).unsqueeze(0))
                                 .pow(2f).sum(2L)).exp();
            using var K_mm_base = K_mm_raw + (noiseV + Jitter)
                                  * torch.eye((long)M, dtype: ScalarType.Float32, device: device);

            using var L_mm = CholeskyWithRetry(K_mm_base, M, device);
            using var alpha_hat = torch.linalg.solve_triangular(
                                      L_mm, m_var.unsqueeze(1), upper: false).squeeze(1);

            // V = L_mm^{-1} K_xz^T  [M, N]
            using var V = torch.linalg.solve_triangular(L_mm, K_xz.t(), upper: false);
            // U = L_mm^{-T} V  [M, N]
            using var U = torch.linalg.solve_triangular(L_mm.t(), V, upper: true);

            using var q_mean = V.t().mv(alpha_hat);                                  // [N]
            using var K_xx_d = tensor(sf2Val).expand((long)N).to(device);

            Tensor q_var;
            if (fullRank)
            {
                // Full-rank S = L_S L_S^T: variance term = ||L_S^T U||²
                using var L_S = torch.diag(log_sd.clamp(SDiagLogMin, SDiagLogMax).exp())
                              + torch.tril(
                                    tensor(state.LowerSFlat.Select(v => (float)v).ToArray(),
                                           new long[] { M, M }, ScalarType.Float32).to(device),
                                    diagonal: -1);
                using var W_S = L_S.t().mm(U);                                       // [M, N]
                q_var = (K_xx_d - V.pow(2f).sum(0L) + W_S.pow(2f).sum(0L))
                        .clamp(min: 1e-6f);
            }
            else
            {
                // Legacy diagonal fallback: S = diag(s²) where s = diag(L_S)
                using var s_diag = log_sd.clamp(SDiagLogMin, SDiagLogMax).exp();
                q_var = (K_xx_d - V.pow(2f).sum(0L)
                       + (s_diag.pow(2f).unsqueeze(1) * U.pow(2f)).sum(0L))
                       .clamp(min: 1e-6f);
            }

            using var probs  = functional.sigmoid(q_mean);
            using (q_var) return (probs.cpu().data<float>().ToArray(),
                                  q_var.cpu().data<float>().ToArray());
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // WALK-FORWARD CROSS-VALIDATION (data-adaptive ARD per fold)
    // ═════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Cv, bool GateFailed) RunWalkForwardCV(
        List<TrainingSample> allStd,
        TrainingHyperparams  hp,
        int F, int M, double sharpeAnnFactor,
        Random rng, Device device, CancellationToken ct)
    {
        int n       = allStd.Count;
        int folds   = Math.Max(2, hp.WalkForwardFolds);
        int embargo = hp.EmbargoBarCount;

        int minTrainN = Math.Max(hp.MinSamples, (int)(n * 0.60));
        int remaining = n - minTrainN;
        if (remaining < embargo + 10)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        int foldSize    = Math.Max(10, remaining / folds);
        int actualFolds = Math.Min(folds, remaining / Math.Max(1, foldSize));
        if (actualFolds < 1)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        var foldAccuracies  = new List<double>(actualFolds);
        var foldSharpes     = new List<double>(actualFolds);
        var foldF1s         = new List<double>(actualFolds);
        var foldEvs         = new List<double>(actualFolds);
        var foldImportances = new List<double[]>(actualFolds);
        int badFolds        = 0;

        for (int fold = 0; fold < actualFolds; fold++)
        {
            ct.ThrowIfCancellationRequested();

            int trainEndFold  = minTrainN + fold * foldSize;
            int testStartFold = trainEndFold + embargo;
            int testEndFold   = Math.Min(n, testStartFold + foldSize);

            if (testStartFold >= testEndFold || testEndFold > n) break;

            var foldTrain = allStd[..trainEndFold].ToList();

            // Purge-horizon bars
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStartFold - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count)
                {
                    int purgeCount = foldTrain.Count - purgeFrom;
                    foldTrain = foldTrain[..purgeFrom];
                    if (purgeCount > 0)
                        _logger.LogDebug(
                            "SVGP CV fold {Fold}: purged {N} train samples.", fold, purgeCount);
                }
            }

            var foldTest = allStd[testStartFold..testEndFold];
            if (foldTrain.Count < hp.MinSamples || foldTest.Count < 5) continue;

            int foldM = Math.Min(M, foldTrain.Count / 2);
            if (foldM < 2) continue;

            // ── Data-adaptive ARD: use per-feature std from fold training data ──
            double[] foldArdLs = ComputeArdLsFromData(foldTrain, F);

            // K-means++ inducing points for this fold
            double[][] Z = KMeansInit(foldTrain, foldM, F, new Random(rng.Next()));
            Z = KMeansRefine(foldTrain, Z, F, iters: 10);

            // ── Lightweight ELBO optimisation for this fold ───────────────────
            // Uses CvElboEpochs (40) with reduced patience — same variational
            // inference as the final model, ensuring CV metrics are representative.
            double[] foldImportanceW = UniformWeights(foldTrain.Count);
            var foldState = OptimizeSvgpElbo(
                foldTrain, Z, foldImportanceW, foldM, F, foldArdLs,
                null, null, null, null,
                null, null,
                CvElboEpochs, CvElboPatience, device, ct,
                miniBatchSize: Math.Min(foldTrain.Count, DefaultMiniBatchSize));

            // Per-fold Platt on the last 15% of fold training set
            int foldCalStart = (int)(foldTrain.Count * 0.85);
            var foldCal      = foldTrain[foldCalStart..];
            var (foldCalMeans, _) = PredictWithVariance(foldCal, foldState, device);
            var (fpA, fpB)        = foldCal.Count >= 5 ? FitPlatt(foldCalMeans, foldCal) : (1.0, 0.0);

            var (rawMeans, _) = PredictWithVariance(foldTest, foldState, device);
            float[] calibProbs = CalibratePlatt(rawMeans, fpA, fpB);

            var foldMetrics = ComputeFullMetrics(calibProbs, foldTest, 0.5, sharpeAnnFactor);

            bool foldFailed = false;
            if (hp.MaxFoldDrawdown < 1.0
                && ComputeMaxDrawdown(calibProbs, foldTest) > hp.MaxFoldDrawdown)
                foldFailed = true;
            if (hp.MinFoldCurveSharpe > -90.0 && foldMetrics.SharpeRatio < hp.MinFoldCurveSharpe)
                foldFailed = true;

            if (foldFailed) badFolds++;
            foldAccuracies.Add(foldMetrics.Accuracy);
            foldSharpes.Add(foldMetrics.SharpeRatio);
            foldF1s.Add(foldMetrics.F1);
            foldEvs.Add(foldMetrics.ExpectedValue);

            // Per-fold permutation importance (for feature stability scoring)
            var foldImp = new double[F];
            if (foldTest.Count >= 10)
            {
                // Derive helpers from foldState for pure-C# permutation loop
                double[] foldAlpha = foldState.Alpha;
                double   foldSf2v  = Math.Exp(2 * Math.Clamp(foldState.LogSf, SfLogMin, SfLogMax));
                double[] foldArdLsOpt = foldState.ArdLogLs
                    .Select(v => Math.Exp(Math.Clamp(v, LsLogMin, LsLogMax))).ToArray();
                // Rebuild Z array from optimised inducing flat
                double[][] foldZ = new double[foldM][];
                for (int mi = 0; mi < foldM; mi++)
                {
                    foldZ[mi] = new double[F];
                    for (int fi = 0; fi < F; fi++)
                        foldZ[mi][fi] = foldState.InducingFlat[mi * F + fi];
                }

                float[] baseP = CalibratePlatt(
                    PredictProbsCSharpArd(foldTest, foldZ, foldAlpha, foldM, F, foldArdLsOpt, foldSf2v), fpA, fpB);
                int baseCor = 0;
                for (int i = 0; i < foldTest.Count; i++)
                    if ((baseP[i] >= 0.5 ? 1 : 0) == foldTest[i].Direction) baseCor++;
                double baseAcc = (double)baseCor / foldTest.Count;

                var buf = new float[F];
                for (int fi = 0; fi < F; fi++)
                {
                    var rng2     = new Random(fi * 31 + fold * 97);
                    var shuffled = new float[foldTest.Count];
                    for (int i = 0; i < foldTest.Count; i++) shuffled[i] = foldTest[i].Features[fi];
                    for (int i = shuffled.Length - 1; i > 0; i--)
                    {
                        int j = rng2.Next(i + 1);
                        (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                    }
                    int cor = 0;
                    for (int i = 0; i < foldTest.Count; i++)
                    {
                        Array.Copy(foldTest[i].Features, buf, F);
                        buf[fi] = shuffled[i];
                        double raw = PredictRawArd(buf, foldZ, foldAlpha, foldM, F, foldArdLsOpt, foldSf2v);
                        double p   = Sigmoid(fpA * Logit(Math.Clamp(Sigmoid(raw), 1e-7, 1.0 - 1e-7)) + fpB);
                        if ((p >= 0.5 ? 1 : 0) == foldTest[i].Direction) cor++;
                    }
                    foldImp[fi] = baseAcc - (double)cor / foldTest.Count;
                }
            }
            foldImportances.Add(foldImp);
        }

        if (foldAccuracies.Count == 0)
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        double avgAcc    = foldAccuracies.Average();
        double stdAcc    = foldAccuracies.Count > 1
            ? Math.Sqrt(foldAccuracies.Average(a => (a - avgAcc) * (a - avgAcc)))
            : 0.0;
        double avgSharpe = foldSharpes.Average();

        // Sharpe linear trend slope
        double sharpeTrend = 0.0;
        if (foldSharpes.Count >= 3)
        {
            double nn = foldSharpes.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (int i = 0; i < foldSharpes.Count; i++)
            {
                sumX += i; sumY += foldSharpes[i];
                sumXY += i * foldSharpes[i]; sumXX += i * i;
            }
            double denom = nn * sumXX - sumX * sumX;
            if (Math.Abs(denom) > 1e-15)
                sharpeTrend = (nn * sumXY - sumX * sumY) / denom;
        }

        // Feature stability: CV = σ/μ of per-fold permutation importance
        double[]? featureStabilityScores = null;
        if (foldImportances.Count >= 2)
        {
            int F2 = foldImportances[0].Length;
            featureStabilityScores = new double[F2];
            int fc = foldImportances.Count;
            for (int j = 0; j < F2; j++)
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

        bool gateOverall = badFolds > foldAccuracies.Count * hp.MaxBadFoldFraction;

        if (hp.MaxWalkForwardStdDev < 1.0 && stdAcc > hp.MaxWalkForwardStdDev)
        {
            _logger.LogWarning(
                "SVGP CV std-dev gate: stdAcc={Std:P1} > threshold={Thr:P1}",
                stdAcc, hp.MaxWalkForwardStdDev);
            gateOverall = true;
        }
        if (hp.MinSharpeTrendSlope > -90.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "SVGP CV Sharpe-trend gate: slope={Slope:F3} < threshold={Thr:F3}",
                sharpeTrend, hp.MinSharpeTrendSlope);
            gateOverall = true;
        }

        return (new WalkForwardResult(
            AvgAccuracy:            avgAcc,
            StdAccuracy:            stdAcc,
            AvgF1:                  foldF1s.Average(),
            AvgEV:                  foldEvs.Average(),
            AvgSharpe:              avgSharpe,
            FoldCount:              foldAccuracies.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores), gateOverall);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ARD HELPERS (pure C# — used for CV folds and permutation importance)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes per-feature ARD length scales as std(X_train[:,j]), clamped to [0.1, ∞).
    /// Used as a fast data-adaptive heuristic in CV folds.
    /// </summary>
    private static double[] ComputeArdLsFromData(List<TrainingSample> trainSet, int F)
    {
        var ls = new double[F];
        int n  = trainSet.Count;
        for (int j = 0; j < F; j++)
        {
            double mean = 0;
            for (int i = 0; i < n; i++) mean += trainSet[i].Features[j];
            mean /= n;
            double var = 0;
            for (int i = 0; i < n; i++) { double d = trainSet[i].Features[j] - mean; var += d * d; }
            ls[j] = Math.Max(Math.Sqrt(var / Math.Max(n - 1, 1)), 0.1);
        }
        return ls;
    }

    /// <summary>Single-sample raw GP prediction with ARD kernel (pure C#).</summary>
    private static double PredictRawArd(
        float[] features, double[][] Z, double[] alpha, int M, int F,
        double[] ardLs, double sf2)
    {
        double f = 0;
        for (int m = 0; m < M; m++)
        {
            double sq = 0;
            for (int fi = 0; fi < F; fi++)
            {
                double d = (Z[m][fi] - features[fi]) / ardLs[fi];
                sq += d * d;
            }
            f += alpha[m] * sf2 * Math.Exp(-0.5 * sq);
        }
        return f;
    }

    /// <summary>Batch probability prediction with ARD kernel (pure C#).</summary>
    private static float[] PredictProbsCSharpArd(
        List<TrainingSample> samples, double[][] Z, double[] alpha, int M, int F,
        double[] ardLs, double sf2)
    {
        var probs = new float[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            probs[i] = (float)Sigmoid(PredictRawArd(samples[i].Features, Z, alpha, M, F, ardLs, sf2));
        return probs;
    }

    // PERMUTATION FEATURE IMPORTANCE
    // ═════════════════════════════════════════════════════════════════════════

    private static float[] ComputePermutationImportance(
        List<TrainingSample> evalSet,
        double[][] inducingPts, double[] alpha,
        int F, int M, double[] ardLs, double sf2,
        double plattA, double plattB,
        CancellationToken ct)
    {
        float[] baseRaw   = PredictProbsCSharpArd(evalSet, inducingPts, alpha, M, F, ardLs, sf2);
        float[] calibBase = CalibratePlatt(baseRaw, plattA, plattB);
        int baseCorrect   = 0;
        for (int i = 0; i < evalSet.Count; i++)
            if ((calibBase[i] >= 0.5 ? 1 : 0) == evalSet[i].Direction) baseCorrect++;
        double baselineAcc = (double)baseCorrect / evalSet.Count;

        var importance = new float[F];
        Parallel.For(0, F, new ParallelOptions
        {
            CancellationToken      = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        }, fi =>
        {
            var rng      = new Random(fi * 71);
            var shuffled = new float[evalSet.Count];
            for (int i = 0; i < evalSet.Count; i++) shuffled[i] = evalSet[i].Features[fi];
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            int fLen = evalSet[0].Features.Length;
            var buf  = new float[fLen];
            int correct = 0;
            for (int i = 0; i < evalSet.Count; i++)
            {
                Array.Copy(evalSet[i].Features, buf, fLen);
                buf[fi] = shuffled[i];
                double raw = PredictRawArd(buf, inducingPts, alpha, M, F, ardLs, sf2);
                double p   = Sigmoid(plattA * Logit(Math.Clamp(Sigmoid(raw), 1e-7, 1.0 - 1e-7)) + plattB);
                if ((p >= 0.5 ? 1 : 0) == evalSet[i].Direction) correct++;
            }
            importance[fi] = (float)Math.Max(0.0, baselineAcc - (double)correct / evalSet.Count);
        });

        // Normalize to sum=1 for consistency with peer trainers
        float total = importance.Sum();
        if (total > 1e-6f)
            for (int fi = 0; fi < F; fi++) importance[fi] /= total;

        return importance;
    }

    // DECISION BOUNDARY STATS  (ARD gradient norm)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes mean/std of ‖∇_x f(x)‖₂ over the calibration set.
    /// ARD SVGP gradient: ∇_x f(x) = Σ_m α_m K(z_m,x)(z_m − x) / l_d² (per feature d).
    /// </summary>
    private static (double Mean, double Std) ComputeDecisionBoundaryStats(
        List<TrainingSample> calSet,
        double[][] Z, double[] alpha, int M, int F, double[] ardLs, double sf2)
    {
        if (calSet.Count < 10) return (0.0, 0.0);

        var norms = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
        {
            var    grad = new double[F];
            float[] x  = calSet[i].Features;
            for (int m = 0; m < M; m++)
            {
                double sq = 0;
                for (int fi = 0; fi < F; fi++)
                {
                    double d = (Z[m][fi] - x[fi]) / ardLs[fi];
                    sq += d * d;
                }
                double k   = sf2 * Math.Exp(-0.5 * sq);
                double amk = alpha[m] * k;
                for (int fi = 0; fi < F; fi++)
                    grad[fi] += amk * (Z[m][fi] - x[fi]) / (ardLs[fi] * ardLs[fi]);
            }
            double norm = 0;
            for (int fi = 0; fi < F; fi++) norm += grad[fi] * grad[fi];
            norms[i] = Math.Sqrt(norm);
        }

        double mean     = norms.Average();
        double variance = norms.Average(v => (v - mean) * (v - mean));
        return (mean, Math.Sqrt(variance));
    }

    // MATH HELPERS
    // ═════════════════════════════════════════════════════════════════════════


    /// <summary>Mean per-point L2 displacement between two inducing sets in standardized space.</summary>
    private static double MeanInducingDisplacement(double[][] before, double[][] after, int M, int F)
    {
        double sum = 0;
        for (int m = 0; m < M; m++)
        {
            double sq = 0;
            for (int fi = 0; fi < F; fi++)
            {
                double d = after[m][fi] - before[m][fi];
                sq += d * d;
            }
            sum += Math.Sqrt(sq);
        }
        return sum / M;
    }

    private static Tensor ToFeatureTensor(IReadOnlyList<TrainingSample> samples, Device device)
    {
        int N = samples.Count, F = samples[0].Features.Length;
        var flat = new float[N * F];
        for (int i = 0; i < N; i++) samples[i].Features.CopyTo(flat, i * F);
        return tensor(flat, new long[] { N, F }, ScalarType.Float32).to(device);
    }

    /// <summary>
    /// Attempts Cholesky decomposition with progressive jitter: 1e-5 → 1e-4 → 1e-3 → 1e-2.
    /// Logs a warning at each escalation level.
    /// </summary>
    private static Tensor CholeskyWithRetry(Tensor K, int M, Device device, ILogger? logger = null)
    {
        float[] jitters = [1e-5f, 1e-4f, 1e-3f, 1e-2f];
        for (int attempt = 0; attempt < jitters.Length; attempt++)
        {
            try
            {
                if (attempt == 0)
                    return torch.linalg.cholesky(K);

                using var K_jit = K + jitters[attempt] * torch.eye((long)M, dtype: ScalarType.Float32, device: device);
                var L = torch.linalg.cholesky(K_jit);
                logger?.LogWarning(
                    "SVGP Cholesky succeeded at jitter level {Jitter:E1} (attempt {Attempt}/{Max}).",
                    jitters[attempt], attempt + 1, jitters.Length);
                return L;
            }
            catch when (attempt < jitters.Length - 1)
            {
                logger?.LogWarning(
                    "SVGP Cholesky failed at jitter {Jitter:E1} — escalating.",
                    jitters[attempt]);
            }
        }
        // Final attempt — let it throw if it still fails.
        using var K_final = K + jitters[^1] * torch.eye((long)M, dtype: ScalarType.Float32, device: device);
        return torch.linalg.cholesky(K_final);
    }
}
