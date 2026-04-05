using System.Diagnostics;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Production-grade Quantile Random Forest trainer (Rec #509).
/// <para>
/// Algorithm overview:
/// <list type="number">
///   <item>Z-score standardisation over all samples (means/stds stored in snapshot for inference parity).</item>
///   <item>Walk-forward cross-validation (expanding window, embargo, purge) to produce <see cref="WalkForwardResult"/>.</item>
///   <item>Equity-curve gate: aborts if majority of CV folds fail <c>MaxFoldDrawdown</c> or <c>MinFoldCurveSharpe</c>.</item>
///   <item>Sharpe-trend gate: aborts if Sharpe across CV folds is deteriorating (slope &lt; MinSharpeTrendSlope).</item>
///   <item>Feature stability scores computed across CV folds (importance std/mean CoV per feature).</item>
///   <item>Final splits: 70 % train | 10 % Platt calibration | ~20 % held-out test (with embargo gaps).</item>
///   <item>Density-ratio importance weighting: recent samples up-weighted via a logistic discriminator.</item>
///   <item>Covariate shift weights from parent model's feature quantile breakpoints.</item>
///   <item>Optional warm-start: loads existing forest from parent QRF snapshot, adds only T/3 new trees.</item>
///   <item>Per-tree bootstrap sampling (density-weighted when enabled); √F feature candidates per split.</item>
///   <item>OOB accuracy: unbiased generalisation estimate using out-of-bag leaf fractions (RF-native).</item>
///   <item>Degenerate-tree guard: empty trees removed; NaN/Inf node values sanitised to safe defaults.</item>
///   <item>Platt scaling (A, B) fitted via SGD on the frozen calibration fold.</item>
///   <item>Class-conditional Platt scaling (separate Buy/Sell calibrators).</item>
///   <item>Isotonic calibration (PAVA) applied post-Platt for monotone probability correction.</item>
///   <item>ECE (Expected Calibration Error) computed on the held-out test set.</item>
///   <item>EV-optimal decision threshold swept on the calibration set (no test-set leakage).</item>
///   <item>Average Kelly fraction (half-Kelly) for position sizing guidance.</item>
///   <item>Temperature scaling as an alternative calibration (grid search over T ∈ [0.5, 5.0]).</item>
///   <item>Magnitude linear regressor trained with Adam + Huber loss + cosine-annealing LR + early stopping.</item>
///   <item>Quantile magnitude regressor (pinball loss at τ) for asymmetric position sizing.</item>
///   <item>Permutation feature importance computed on the held-out test set (Fisher-Yates shuffle, fixed seed).</item>
///   <item>Calibration-set permutation importance (parallel) for warm-start feature transfer.</item>
///   <item>Feature pruning re-train pass: masks low-importance features, rebuilds forest, accepts if accuracy holds.</item>
///   <item>Durbin-Watson autocorrelation test on magnitude residuals.</item>
///   <item>Mutual-information feature redundancy check (histogram MI, Sturges' rule bins).</item>
///   <item>Split-conformal q̂ at the configured coverage level for prediction-set guarantees.</item>
///   <item>Feature quantile breakpoints for PSI drift monitoring.</item>
///   <item>Brier Skill Score vs. naïve base-rate predictor.</item>
///   <item>Stationarity gate: lag-1 correlation ADF proxy warns when &gt;30 % of features appear non-stationary.</item>
///   <item>Class-imbalance warning when Buy/Sell split is outside 35/65.</item>
///   <item>Incremental update fast-path: fine-tunes on the most recent DensityRatioWindowDays of data.</item>
///   <item>Stratified bootstrap: each bag samples equal Buy/Sell proportions (density-weighted within each class).</item>
///   <item>Stacking meta-learner: logistic regression over per-tree leaf-fraction outputs replaces uniform averaging; <c>MetaWeights/MetaBias</c> stored in snapshot.</item>
///   <item>Greedy Ensemble Selection (GES): forward pass selects trees by NLL contribution; usage-frequency weights in <c>EnsembleSelectionWeights</c>.</item>
///   <item>OOB-contribution tree pruning: removes individual new trees whose absence improves OOB accuracy; <c>OobPrunedLearnerCount</c> in snapshot.</item>
///   <item>Ensemble diversity metric: average pairwise Pearson correlation of per-tree prediction vectors; <c>EnsembleDiversity</c> in snapshot.</item>
///   <item>Abstention gate: 3-D logistic [calibP, treeStd, metaLabelScore] learns to suppress low-confidence signals; <c>AbstentionWeights/Bias/Threshold</c>.</item>
///   <item>Per-tree calibration-set accuracy: <c>LearnerCalAccuracies[t]</c> stored for downstream softmax-weighted inference.</item>
///   <item>Parallel walk-forward CV folds: each fold runs on its own thread-pool thread with an isolated RNG.</item>
///   <item>Jackknife+ nonconformity residuals: <c>r_i = |label - oobProb|</c> sorted ascending; stored in <c>JackknifeResiduals</c>.</item>
///   <item>Meta-label secondary classifier: 7-D logistic [rawProb, treeStd, feat[0..4]] predicts correctness; <c>MetaLabelWeights/Bias</c>.</item>
///   <item>LookbackWindow-aware purging in CV: additional <c>MLFeatureHelper.LookbackWindow − 1</c> bars removed beyond embargo.</item>
///   <item>Warm-start biased feature-candidate sampling: when parent <c>FeatureImportanceScores</c> are present, √F candidates are drawn proportional to importance.</item>
///   <item>RedundantFeaturePairs persisted to snapshot for operator review.</item>
/// </list>
/// </para>
/// Forest serialised as <c>ModelSnapshot.GbmTreesJson</c> (flat <c>GbmNode</c> list per tree).
/// Feature importance stored in <c>ModelSnapshot.Weights[0]</c> and <c>ModelSnapshot.QrfWeights[0]</c>.
/// Registered as a keyed <see cref="IMLModelTrainer"/> with key <c>"quantilerf"</c>.
/// </summary>
[RegisterKeyedService(typeof(IMLModelTrainer), LearnerArchitecture.QuantileRf)]
public sealed class QuantileRfModelTrainer : IMLModelTrainer
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ModelType    = "quantilerf";
    private const string ModelVersion = "4.1";

    // Tree defaults
    private const int    DefaultMaxDepth = 6;
    private const int    DefaultMinLeaf  = 3;
    private const double Eps             = 1e-10;

    // Adam optimizer
    private const double AdamBeta1   = 0.9;
    private const double AdamBeta2   = 0.999;
    private const double AdamEpsilon = 1e-8;

    // Split ratios for train / calibration / test
    private const double TrainSplitRatio  = 0.70;
    private const double CalEndSplitRatio = 0.80;

    // Platt scaling SGD
    private const double PlattLearningRate     = 0.01;
    private const int    PlattMaxEpochs        = 200;
    private const double PlattConvergenceDelta = 1e-7;

    // Stationarity gate
    private const double StationarityRhoThreshold   = 0.97;
    private const double NonStationaryFractionWarn   = 0.30;

    // Class imbalance warning bounds
    private const double ImbalanceLowerBound = 0.35;
    private const double ImbalanceUpperBound = 0.65;

    // Density-ratio discriminator
    private const double DensityRatioLr     = 0.01;
    private const int    DensityRatioEpochs = 30;

    // Feature pruning re-train acceptance tolerance
    private const double PruningAccuracyTolerance = 0.005;

    // Calibration-fold minimum sample counts
    private const int MinCalSamples      = 10;
    private const int MinCalSamplesPlatt = 20;

    // Isotonic min block size for regularisation
    private const int IsotonicMinBlockSize = 3;

    // Default GES rounds
    private const int DefaultGesRounds = 100;

    // Permutation importance
    private const int DefaultPermutationRepeats = 1;

    // Target-leakage warning threshold (single feature > 40 % of total importance)
    private const double LeakageImportanceWarnFraction = 0.40;

    // MI redundancy defaults
    private const int MaxMiDefaultSamples = 500;

    // Diversity computation
    private const int MaxDiversitySamples = 200;

    // Minimum class count for stratified bootstrap
    private const int MinStratifiedClassCount = 5;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = false, MaxDepth = 512 };

    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly ILogger<QuantileRfModelTrainer> _logger;

    public QuantileRfModelTrainer(ILogger<QuantileRfModelTrainer> logger)
        => _logger = logger;

    // ── Training diagnostics ─────────────────────────────────────────────────

    /// <summary>
    /// Structured training diagnostics emitted alongside the model for programmatic
    /// consumption (monitoring dashboards, auto-tuning, offline analysis).
    /// </summary>
    private sealed record TrainingDiagnostics(
        TimeSpan TreeBuildTime,
        TimeSpan CalibrationTime,
        TimeSpan EvaluationTime,
        TimeSpan FeatureImportanceTime,
        TimeSpan TotalTime,
        int      GatesPassed,
        int      GatesFailed,
        string[] FailedGateNames,
        double   PeakEstimatedMemoryMb,
        int      TreesBeforePruning,
        int      TreesAfterPruning,
        int      FeaturesBeforePruning,
        int      FeaturesAfterPruning);

    // ── Training context (inter-phase state) ────────────────────────────────

    /// <summary>
    /// Mutable state container passed between training phase methods.
    /// Avoids 40+ parameter signatures while keeping the phase extraction clean.
    /// Fields are mutable because the feature-pruning step conditionally reassigns
    /// trainSet, calSet, testSet, allTrees, and all calibration parameters.
    /// </summary>
    private sealed class TrainingContext
    {
        // ── Inputs ───────────────────────────────────────────────────────────
        public required List<TrainingSample> OriginalSamples { get; init; }
        public required TrainingHyperparams  Hp              { get; init; }
        public ModelSnapshot?                WarmStart       { get; init; }
        public long?                         ParentModelId   { get; init; }
        public CancellationToken             Ct              { get; init; }

        // ── Derived from inputs ──────────────────────────────────────────────
        public int    F                 { get; set; }
        public int    TreeCount         { get; set; }
        public int    SqrtF             { get; set; }
        public Random Rng               { get; set; } = new();
        public int    EffectiveMaxDepth { get; set; }
        public int    EffectiveMinLeaf  { get; set; }

        // ── Diagnostics ──────────────────────────────────────────────────────
        public Stopwatch      TotalStopwatch { get; } = Stopwatch.StartNew();
        public List<string>   FailedGates    { get; } = [];
        public int            GatesPassed    { get; set; }

        // ── Standardization ──────────────────────────────────────────────────
        public float[] Means { get; set; } = [];
        public float[] Stds  { get; set; } = [];
        public List<TrainingSample> AllStd { get; set; } = [];

        // ── CV ───────────────────────────────────────────────────────────────
        public WalkForwardResult CvResult { get; set; } = new(0, 0, 0, 0, 0, 0);

        // ── Data splits ──────────────────────────────────────────────────────
        public List<TrainingSample> TrainSet { get; set; } = [];
        public List<TrainingSample> CalSet   { get; set; } = [];
        public List<TrainingSample> TestSet  { get; set; } = [];

        // ── Density/covariate weights ────────────────────────────────────────
        public double[]? DensityWeights { get; set; }
        public double[]? CumDensity     { get; set; }

        // ── Warm-start ───────────────────────────────────────────────────────
        public List<List<TreeNode>> WarmTrees          { get; set; } = [];
        public int                  EffectiveTreeCount { get; set; }
        public int                  GenerationNum      { get; set; } = 1;
        public float[]?             ParentImportanceScores { get; set; }

        // ── Forest ───────────────────────────────────────────────────────────
        public List<List<TreeNode>> AllTrees  { get; set; } = [];
        public List<List<TreeNode>> NewTrees  { get; set; } = [];
        public List<HashSet<int>>   OobMasks  { get; set; } = [];
        public float[]              FeatureImportance { get; set; } = [];
        public double               OobAccuracy       { get; set; }
        public int                  SanitizedCount    { get; set; }

        // ── Calibration ──────────────────────────────────────────────────────
        public double   PlattA      { get; set; } = 1.0;
        public double   PlattB      { get; set; }
        public double   PlattABuy   { get; set; }
        public double   PlattBBuy   { get; set; }
        public double   PlattASell  { get; set; }
        public double   PlattBSell  { get; set; }
        public double[] IsotonicBp  { get; set; } = [];
        public double   Ece         { get; set; }
        public double   OptimalThreshold { get; set; } = 0.5;
        public double   AvgKellyFraction { get; set; }
        public double   TemperatureScale { get; set; }

        // ── Magnitude regressors ─────────────────────────────────────────────
        public double[] MagWeights    { get; set; } = [];
        public double   MagBias       { get; set; }
        public double[] MlpW1         { get; set; } = [];
        public double[] MlpB1         { get; set; } = [];
        public double[] MlpW2         { get; set; } = [];
        public double   MlpB2         { get; set; }
        public double[] MagQ90Weights { get; set; } = [];
        public double   MagQ90Bias    { get; set; }

        // ── Feature analysis ─────────────────────────────────────────────────
        public double[] CalImportanceScores { get; set; } = [];
        public bool[]   ActiveMask          { get; set; } = [];
        public int      PrunedCount         { get; set; }
        public double   DurbinWatson        { get; set; } = 2.0;
        public string[] RedundantPairs      { get; set; } = [];
        public double   ConformalQHat       { get; set; }
        public double[][] FeatureQuantileBp { get; set; } = [];
        public double   BrierSkillScore     { get; set; }

        // ── Evaluation ───────────────────────────────────────────────────────
        public EvalMetrics EvalMetrics { get; set; } = new(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);

        // ── Ensemble ─────────────────────────────────────────────────────────
        public double[] GesWeights           { get; set; } = [];
        public double[] MetaWeights          { get; set; } = [];
        public double   MetaBias             { get; set; }
        public double   EnsembleDiversity    { get; set; }
        public double[] TreeCalAccuracies    { get; set; } = [];
        public double[] JackknifeResiduals   { get; set; } = [];
        public double[] MetaLabelWeights     { get; set; } = [];
        public double   MetaLabelBias        { get; set; }
        public double[] AbstentionWeights    { get; set; } = [];
        public double   AbstentionBias       { get; set; }
        public double   AbstentionThreshold  { get; set; } = 0.5;
        public int      OobPrunedCount       { get; set; }
        public double   CalCiStdA            { get; set; }
        public double   CalCiStdB            { get; set; }
        public string[] FeatureInteractions  { get; set; } = [];
    }

    // ── Internal flat tree node ───────────────────────────────────────────────

    /// <summary>
    /// Compact decision tree node. <c>SplitFeat = -1</c> denotes a leaf.
    /// <c>LeafDirection</c> holds the fraction of positive (Buy) samples at the leaf.
    /// <c>LeafPosCount</c> / <c>LeafTotalCount</c> are the raw counts used for
    /// probability computation and warm-start repopulation.
    /// </summary>
    private sealed class TreeNode
    {
        public int    SplitFeat     { get; set; } = -1;
        public double SplitThresh   { get; set; }
        public int    LeftChild     { get; set; } = -1;
        public int    RightChild    { get; set; } = -1;
        public double LeafDirection { get; set; }
        public int    LeafPosCount  { get; set; }
        public int    LeafTotalCount { get; set; }
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

    // ══════════════════════════════════════════════════════════════════════════
    // CORE TRAINING — thin orchestrator calling named phase methods
    // ══════════════════════════════════════════════════════════════════════════

    private TrainingResult Train(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        ModelSnapshot?       warmStart,
        long?                parentModelId,
        CancellationToken    ct)
    {
        ct.ThrowIfCancellationRequested();

        // ── 0. Incremental update fast-path ───────────────────────────────────
        if (warmStart?.Type == ModelType && hp.UseIncrementalUpdate && hp.DensityRatioWindowDays > 0)
        {
            int barsPerDay  = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
            int recentCount = Math.Min(samples.Count, hp.DensityRatioWindowDays * barsPerDay);
            if (recentCount >= hp.MinSamples)
            {
                _logger.LogInformation(
                    "QuantileRF incremental update: fine-tuning on last {N}/{Total} samples (≈{Days}d window)",
                    recentCount, samples.Count, hp.DensityRatioWindowDays);
                int reducedTrees = Math.Max(10, (hp.QrfTrees ?? 50) / 3);
                var incrHp = hp with { QrfTrees = reducedTrees, UseIncrementalUpdate = false };
                return Train(samples[^recentCount..].ToList(), incrHp, warmStart, parentModelId, ct);
            }
        }

        // ── 1. Input validation ───────────────────────────────────────────────
        if (samples.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"QuantileRfModelTrainer needs at least {hp.MinSamples} samples; got {samples.Count}.");

        // ── Build context and run phases ──────────────────────────────────────
        var ctx = new TrainingContext
        {
            OriginalSamples = samples,
            Hp              = hp,
            WarmStart       = warmStart,
            ParentModelId   = parentModelId,
            Ct              = ct,
            F               = samples[0].Features.Length,
            TreeCount       = hp.QrfTrees ?? 50,
            SqrtF           = Math.Max(1, (int)Math.Round(Math.Sqrt(samples[0].Features.Length))),
            Rng             = hp.QrfSeed != 0 ? new Random(hp.QrfSeed) : new Random(),
            EffectiveMaxDepth = hp.QrfMaxDepth > 0 ? hp.QrfMaxDepth : DefaultMaxDepth,
            EffectiveMinLeaf  = hp.QrfMinLeaf  > 0 ? hp.QrfMinLeaf  : DefaultMinLeaf,
            EffectiveTreeCount = hp.QrfTrees ?? 50,
        };

        _logger.LogInformation(
            "QuantileRfModelTrainer starting: N={N} F={F} sqrtF={SF} T={T}",
            samples.Count, ctx.F, ctx.SqrtF, ctx.TreeCount);

        if (RunPreTrainingGates(ctx) is { } earlyExit1)  return earlyExit1;
        if (PrepareTrainingData(ctx) is { } earlyExit2)  return earlyExit2;
        BuildAndPruneForest(ctx);
        CalibrateModel(ctx);
        if (EvaluateAndRefine(ctx) is { } earlyExit3)    return earlyExit3;
        FitEnsembleMethods(ctx);
        return SerializeSnapshot(ctx);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 1: Pre-training gates (standardization, CV, stability/equity gates)
    // ══════════════════════════════════════════════════════════════════════════

    private TrainingResult? RunPreTrainingGates(TrainingContext ctx)
    {
        var hp = ctx.Hp;
        int F  = ctx.F;

        // ── 2. Z-score standardisation — statistics from the training fold only ─
        // Splitting must be established before computing means/stds so that the
        // scale of the calibration and test sets cannot leak into the normalisation
        // parameters used at inference time.
        int trainEndRaw = (int)(ctx.OriginalSamples.Count * TrainSplitRatio);
        var trainRawFeatures = new List<float[]>(trainEndRaw);
        for (int i = 0; i < trainEndRaw; i++) trainRawFeatures.Add(ctx.OriginalSamples[i].Features);
        var (means, stds) = MLFeatureHelper.ComputeStandardization(trainRawFeatures);
        ctx.Means = means;
        ctx.Stds  = stds;

        var allStd = new List<TrainingSample>(ctx.OriginalSamples.Count);
        foreach (var s in ctx.OriginalSamples)
            allStd.Add(s with { Features = MLFeatureHelper.Standardize(s.Features, means, stds) });
        ctx.AllStd = allStd;

        // ── 3. Walk-forward cross-validation ─────────────────────────────────
        var (cvResult, equityCurveGateFailed) = RunWalkForwardCV(allStd, hp, F, ctx.SqrtF, ctx.TreeCount, ctx.Ct,
                                                                   ctx.EffectiveMaxDepth, ctx.EffectiveMinLeaf);
        ctx.CvResult = cvResult;
        _logger.LogInformation(
            "Walk-forward CV — folds={Folds} avgAcc={Acc:P1} stdAcc={Std:P1} avgF1={F1:F3} avgEV={EV:F4} avgSharpe={Sharpe:F2} sharpeTrend={Trend:F3}",
            cvResult.FoldCount, cvResult.AvgAccuracy, cvResult.StdAccuracy,
            cvResult.AvgF1, cvResult.AvgEV, cvResult.AvgSharpe, cvResult.SharpeTrend);

        // ── 3b. Feature stability gate (#26) ──────────────────────────────────
        if (hp.QrfFeatureStabilityGateEnabled && hp.QrfMaxFeatureStabilityCov > 0.0
            && cvResult.FeatureStabilityScores is { Length: > 0 })
        {
            var sorted = cvResult.FeatureStabilityScores.OrderBy(x => x).ToArray();
            double medianCov = sorted[sorted.Length / 2];
            if (medianCov > hp.QrfMaxFeatureStabilityCov)
            {
                _logger.LogWarning(
                    "QuantileRF feature stability gate: median CoV={CoV:F3} > threshold={Thr:F3}. " +
                    "Model relies on unstable features — aborting.",
                    medianCov, hp.QrfMaxFeatureStabilityCov);
                ctx.FailedGates.Add("FeatureStability");
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                    cvResult, []);
            }
            ctx.GatesPassed++;
        }

        // ── 4. Equity-curve gate ──────────────────────────────────────────────
        if (equityCurveGateFailed)
        {
            _logger.LogWarning("QuantileRF equity-curve gate failed — returning zero-metric result.");
            return new TrainingResult(
                new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                cvResult, []);
        }

        ctx.Ct.ThrowIfCancellationRequested();
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 2: Prepare training data (splits, stationarity, density, warm-start)
    // ══════════════════════════════════════════════════════════════════════════

    private TrainingResult? PrepareTrainingData(TrainingContext ctx)
    {
        var hp     = ctx.Hp;
        int F      = ctx.F;
        var allStd = ctx.AllStd;

        // ── 5. Final splits: 70 % train | 10 % cal | ~20 % test + embargo ─────
        int trainEnd  = (int)(allStd.Count * TrainSplitRatio);
        int calEnd    = (int)(allStd.Count * CalEndSplitRatio);
        int embargo   = hp.EmbargoBarCount;

        int trainLimit = Math.Max(0, trainEnd - embargo);
        int calStart   = Math.Min(trainEnd + embargo, calEnd);
        int testStart  = Math.Min(calEnd   + embargo, allStd.Count);

        ctx.TrainSet = allStd[..trainLimit];
        ctx.CalSet   = calStart < calEnd       ? allStd[calStart..calEnd] : [];
        ctx.TestSet  = testStart < allStd.Count ? allStd[testStart..]    : [];

        if (ctx.TrainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {ctx.TrainSet.Count} < {hp.MinSamples}");

        // ── 5b. Stationarity gate ─────────────────────────────────────────────
        {
            int    nonStat  = CountNonStationaryFeatures(ctx.TrainSet, F);
            double fraction = F > 0 ? (double)nonStat / F : 0;
            if (fraction > NonStationaryFractionWarn && hp.FracDiffD == 0.0)
            {
                _logger.LogWarning(
                    "Stationarity gate: {N}/{T} features have unit root (|ρ₁| > {Thr:F2}). " +
                    "Consider enabling FracDiffD.", nonStat, F, StationarityRhoThreshold);
                if (hp.QrfStationarityGateEnabled)
                {
                    _logger.LogWarning(
                        "QuantileRF stationarity gate triggered — aborting training " +
                        "({NonStat}/{Total} non-stationary features, FracDiffD=0).", nonStat, F);
                    return new TrainingResult(
                        new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                        ctx.CvResult, []);
                }
            }
        }

        // ── 5c. Class-imbalance warning ────────────────────────────────────────
        {
            int posCount = 0;
            foreach (var s in ctx.TrainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / ctx.TrainSet.Count;
            if (buyRatio < ImbalanceLowerBound || buyRatio > ImbalanceUpperBound)
                _logger.LogWarning(
                    "QuantileRF class imbalance: Buy={Buy:P1}, Sell={Sell:P1}. " +
                    "Density-weighted bootstrap will partially compensate.",
                    buyRatio, 1.0 - buyRatio);
        }

        // ── 6. Density-ratio importance weights ───────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && ctx.TrainSet.Count >= 50)
        {
            densityWeights = ComputeDensityRatioWeights(ctx.TrainSet, F, hp.DensityRatioWindowDays);
            _logger.LogDebug("QuantileRF density-ratio weights computed (recentWindow={W}d).", hp.DensityRatioWindowDays);
        }

        // ── 6b. Covariate shift weights from parent model ─────────────────────
        if (hp.UseCovariateShiftWeights &&
            ctx.WarmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBp)
        {
            var csWeights = ComputeCovariateShiftWeights(ctx.TrainSet, parentBp, F);
            if (densityWeights is not null)
            {
                for (int i = 0; i < densityWeights.Length && i < csWeights.Length; i++)
                    densityWeights[i] *= csWeights[i];
                // Re-normalise combined weights
                double wSum = densityWeights.Sum();
                if (wSum > Eps)
                    for (int i = 0; i < densityWeights.Length; i++) densityWeights[i] /= wSum;
            }
            else
            {
                densityWeights = csWeights;
            }
            _logger.LogDebug("QuantileRF covariate shift weights applied from parent model (gen={Gen}).",
                ctx.WarmStart.GenerationNumber);
        }
        ctx.DensityWeights = densityWeights;

        // Pre-compute cumulative density weights for O(log N) bootstrap sampling
        double[]? cumDensity = null;
        if (densityWeights is not null)
        {
            cumDensity = new double[densityWeights.Length];
            cumDensity[0] = densityWeights[0];
            for (int i = 1; i < densityWeights.Length; i++)
                cumDensity[i] = cumDensity[i - 1] + densityWeights[i];
        }
        ctx.CumDensity = cumDensity;

        // ── 7. Warm-start: load existing forest from parent snapshot ──────────
        var warmTrees = new List<List<TreeNode>>();

        if (ctx.WarmStart?.Type == ModelType && ctx.WarmStart.GbmTreesJson is { Length: > 0 })
        {
            // #17: Snapshot version migration check
            if (!string.IsNullOrEmpty(ctx.WarmStart.Version) && ctx.WarmStart.Version != ModelVersion)
                _logger.LogWarning(
                    "QuantileRF warm-start version mismatch: snapshot={SnapVer}, trainer={TrainerVer}. " +
                    "New v4.1 fields will use defaults.",
                    ctx.WarmStart.Version, ModelVersion);

            try
            {
                var loadedGbm = JsonSerializer.Deserialize<List<GbmTree>>(ctx.WarmStart.GbmTreesJson, JsonOptions);
                if (loadedGbm is { Count: > 0 })
                {
                    // #18: Parallel repopulation of warm-start trees
                    var loadedNodes = new List<TreeNode>?[loadedGbm.Count];
                    Parallel.For(0, loadedGbm.Count, i =>
                    {
                        var nodes = ConvertGbmToTreeNodes(loadedGbm[i]);
                        if (nodes.Count > 0)
                        {
                            RepopulateLeafCounts(nodes, 0, ctx.TrainSet);
                            loadedNodes[i] = nodes;
                        }
                    });
                    foreach (var nodes in loadedNodes)
                        if (nodes is { Count: > 0 }) warmTrees.Add(nodes);

                    ctx.EffectiveTreeCount = Math.Max(10, ctx.TreeCount / 3);
                    ctx.GenerationNum      = ctx.WarmStart.GenerationNumber + 1;
                    _logger.LogInformation(
                        "QuantileRF warm-start: loaded {N} trees (gen={Gen}); adding up to {New} new trees.",
                        warmTrees.Count, ctx.WarmStart.GenerationNumber, ctx.EffectiveTreeCount);
                }
            }
            catch (Exception ex)
            {
                // #16: Log full exception (including stack trace) for debugging
                _logger.LogWarning(ex, "QuantileRF warm-start deserialization failed; starting cold.");
                warmTrees = [];
            }
        }
        ctx.WarmTrees = warmTrees;

        // Warm-start biased feature importance for candidate selection (#12)
        // FeatureImportanceScores is double[]; convert to float[] once for BuildTree
        if (ctx.WarmStart?.FeatureImportanceScores is { Length: > 0 } piScores)
        {
            var parentImportanceScores = new float[piScores.Length];
            for (int i = 0; i < piScores.Length; i++)
                parentImportanceScores[i] = (float)piScores[i];
            ctx.ParentImportanceScores = parentImportanceScores;
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 3: Build and prune forest
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildAndPruneForest(TrainingContext ctx)
    {
        var hp     = ctx.Hp;
        int F      = ctx.F;
        int sqrtF  = ctx.SqrtF;
        var rng    = ctx.Rng;
        int effectiveTreeCount = ctx.EffectiveTreeCount;
        int effectiveMaxDepth  = ctx.EffectiveMaxDepth;
        int effectiveMinLeaf   = ctx.EffectiveMinLeaf;
        var trainSet           = ctx.TrainSet;
        var cumDensity         = ctx.CumDensity;
        var densityWeights     = ctx.DensityWeights;

        // ── 8. Build new trees (stratified density-weighted bootstrap, OOB tracking) ──
        int trainCount     = trainSet.Count;
        double[] impAccum  = new double[F];
        int      impSplits = 0;
        var newTrees       = new List<List<TreeNode>>(effectiveTreeCount);
        var oobMasks       = new List<HashSet<int>>(effectiveTreeCount);

        // Pre-build pos/neg index lists for stratified bootstrap (#1)
        var posTrainIdx = new List<int>(trainCount);
        var negTrainIdx = new List<int>(trainCount);
        for (int i = 0; i < trainCount; i++)
        {
            if (trainSet[i].Direction > 0) posTrainIdx.Add(i);
            else                           negTrainIdx.Add(i);
        }
        bool useStratified = posTrainIdx.Count >= MinStratifiedClassCount && negTrainIdx.Count >= MinStratifiedClassCount;

        // Build per-class cumulative density arrays when density weights are available
        double[]? posCumDensity = null, negCumDensity = null;
        if (useStratified && densityWeights is not null)
        {
            var posW = new double[posTrainIdx.Count];
            for (int i = 0; i < posTrainIdx.Count; i++) posW[i] = densityWeights[posTrainIdx[i]];
            var negW = new double[negTrainIdx.Count];
            for (int i = 0; i < negTrainIdx.Count; i++) negW[i] = densityWeights[negTrainIdx[i]];

            double posSum = posW.Sum(); double negSum = negW.Sum();
            posCumDensity = new double[posW.Length];
            negCumDensity = new double[negW.Length];
            if (posSum > Eps)
            {
                posCumDensity[0] = posW[0] / posSum;
                for (int i = 1; i < posW.Length; i++) posCumDensity[i] = posCumDensity[i - 1] + posW[i] / posSum;
            }
            else { for (int i = 0; i < posW.Length; i++) posCumDensity[i] = (i + 1.0) / posW.Length; }
            if (negSum > Eps)
            {
                negCumDensity[0] = negW[0] / negSum;
                for (int i = 1; i < negW.Length; i++) negCumDensity[i] = negCumDensity[i - 1] + negW[i] / negSum;
            }
            else { for (int i = 0; i < negW.Length; i++) negCumDensity[i] = (i + 1.0) / negW.Length; }
        }

        var parentImportanceScores = ctx.ParentImportanceScores;

        // Pre-generate per-tree seeds from the master RNG (sequential) so that tree
        // construction is fully deterministic given hp.QrfSeed regardless of thread scheduling.
        var treeSeeds = new int[effectiveTreeCount];
        for (int i = 0; i < effectiveTreeCount; i++) treeSeeds[i] = rng.Next();

        // Pre-allocated per-tree result slots — no locking required inside Parallel.For.
        var newTreesArr  = new List<TreeNode>?[effectiveTreeCount];
        var oobMasksArr  = new HashSet<int>?[effectiveTreeCount];
        var impAccumArr  = new double[effectiveTreeCount][];
        var impSplitsArr = new int[effectiveTreeCount];

        Parallel.For(0, effectiveTreeCount, new ParallelOptions { CancellationToken = ctx.Ct }, tIdx =>
        {
            var localRng       = new Random(treeSeeds[tIdx]);
            var localImpAccum  = new double[F];
            int localImpSplits = 0;
            var inBagSet       = new HashSet<int>(trainCount);
            var bootstrapIdx   = new List<int>(trainCount);

            if (useStratified)
            {
                // Preserve natural class proportions — draw posTrainIdx.Count pos samples
                // and negTrainIdx.Count neg samples so the bootstrap has the same Buy/Sell
                // ratio as the training set. The old trainCount/2 forced 50/50 which
                // distorted posterior probabilities even after Platt calibration.
                for (int bi = 0; bi < posTrainIdx.Count; bi++)
                {
                    int local = posCumDensity is null
                        ? localRng.Next(posTrainIdx.Count)
                        : SampleWeighted(localRng, posCumDensity);
                    int drawn = posTrainIdx[local];
                    bootstrapIdx.Add(drawn);
                    inBagSet.Add(drawn);
                }
                for (int bi = 0; bi < negTrainIdx.Count; bi++)
                {
                    int local = negCumDensity is null
                        ? localRng.Next(negTrainIdx.Count)
                        : SampleWeighted(localRng, negCumDensity);
                    int drawn = negTrainIdx[local];
                    bootstrapIdx.Add(drawn);
                    inBagSet.Add(drawn);
                }
                for (int i = bootstrapIdx.Count - 1; i > 0; i--)
                {
                    int j = localRng.Next(i + 1);
                    (bootstrapIdx[i], bootstrapIdx[j]) = (bootstrapIdx[j], bootstrapIdx[i]);
                }
            }
            else
            {
                for (int bi = 0; bi < trainCount; bi++)
                {
                    int drawn = cumDensity is null
                        ? localRng.Next(trainCount)
                        : SampleWeighted(localRng, cumDensity);
                    bootstrapIdx.Add(drawn);
                    inBagSet.Add(drawn);
                }
            }

            var oobMask = new HashSet<int>();
            for (int i = 0; i < trainCount; i++)
                if (!inBagSet.Contains(i)) oobMask.Add(i);

            var treeNodes = new List<TreeNode>();
            BuildTree(trainSet, bootstrapIdx, 0, treeNodes, localRng, F, sqrtF,
                      localImpAccum, ref localImpSplits, parentImportanceScores,
                      effectiveMaxDepth, effectiveMinLeaf);

            newTreesArr[tIdx]  = treeNodes.Count > 0 ? treeNodes : null;
            oobMasksArr[tIdx]  = oobMask;
            impAccumArr[tIdx]  = localImpAccum;
            impSplitsArr[tIdx] = localImpSplits;
        });

        // Merge per-tree importance accumulators and assemble ordered result lists.
        for (int tIdx = 0; tIdx < effectiveTreeCount; tIdx++)
        {
            if (impAccumArr[tIdx] is { } localAcc)
                for (int fi = 0; fi < F; fi++) impAccum[fi] += localAcc[fi];
            impSplits += impSplitsArr[tIdx];

            if (newTreesArr[tIdx] is { } nodes && oobMasksArr[tIdx] is { } mask)
            {
                newTrees.Add(nodes);
                oobMasks.Add(mask);
            }
        }

        // Combine warm-start + new trees for inference
        var allTrees = new List<List<TreeNode>>(ctx.WarmTrees.Count + newTrees.Count);
        allTrees.AddRange(ctx.WarmTrees);
        allTrees.AddRange(newTrees);

        // ── 9. NaN/Inf node sanitization ─────────────────────────────────────
        int sanitizedCount = 0;
        // Remove empty trees
        for (int ti = allTrees.Count - 1; ti >= 0; ti--)
        {
            if (allTrees[ti].Count == 0)
            {
                allTrees.RemoveAt(ti);
                sanitizedCount++;
            }
        }
        // Sanitize non-finite node values
        sanitizedCount += SanitizeTreeNodes(allTrees);
        if (sanitizedCount > 0)
            _logger.LogWarning("QuantileRF: sanitized {N} degenerate nodes/trees.", sanitizedCount);

        // ── 9b. Leaf shrinkage (#11) ──────────────────────────────────────────
        if (hp.QrfLeafShrinkage > 0.0)
        {
            // Compute global base rate from training set
            int globalPos = 0;
            foreach (var s in trainSet) if (s.Direction > 0) globalPos++;
            double globalBaseRate = (double)globalPos / trainSet.Count;
            double shrink = Math.Clamp(hp.QrfLeafShrinkage, 0.0, 1.0);

            foreach (var tree in allTrees)
                foreach (var node in tree)
                    if (node.SplitFeat < 0) // leaf
                        node.LeafDirection = shrink * globalBaseRate + (1.0 - shrink) * node.LeafDirection;

            _logger.LogDebug("Leaf shrinkage applied: factor={S:F3}, baseRate={BR:F3}", shrink, globalBaseRate);
        }

        // ── 10. OOB accuracy (RF-native, bias-free generalisation estimate) ────
        double oobAccuracy = ComputeOobAccuracy(trainSet, newTrees, oobMasks);
        _logger.LogInformation("QuantileRF OOB accuracy={OobAcc:P1}", oobAccuracy);

        // ── 11. Normalise training-side feature importance ────────────────────
        // (overwritten by permutation importance in step 23)
        float[] featureImportance = new float[F];
        if (impSplits > 0)
        {
            double totalImp = 0;
            for (int fi = 0; fi < F; fi++) totalImp += impAccum[fi];
            for (int fi = 0; fi < F; fi++)
                featureImportance[fi] = totalImp > Eps ? (float)(impAccum[fi] / totalImp) : 0f;
        }

        // Write results to context
        ctx.AllTrees          = allTrees;
        ctx.NewTrees          = newTrees;
        ctx.OobMasks          = oobMasks;
        ctx.OobAccuracy       = oobAccuracy;
        ctx.FeatureImportance = featureImportance;
        ctx.SanitizedCount    = sanitizedCount;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 4: Calibrate model (Platt, isotonic, ECE, threshold, Kelly, temp)
    // ══════════════════════════════════════════════════════════════════════════

    private void CalibrateModel(TrainingContext ctx)
    {
        var hp       = ctx.Hp;
        var calSet   = ctx.CalSet;
        var testSet  = ctx.TestSet;
        var allTrees = ctx.AllTrees;
        var trainSet = ctx.TrainSet;

        // ── 12. Platt scaling on calibration fold ─────────────────────────────
        var (plattA, plattB) = FitPlattScaling(calSet, allTrees, trainSet);
        _logger.LogDebug("Platt calibration: A={A:F4} B={B:F4}", plattA, plattB);
        ctx.PlattA = plattA;
        ctx.PlattB = plattB;

        // ── 13. Class-conditional Platt (separate Buy/Sell calibrators) ────────
        var (plattABuy, plattBBuy, plattASell, plattBSell) =
            FitClassConditionalPlatt(calSet, allTrees, trainSet);
        _logger.LogDebug(
            "Class-conditional Platt — Buy: A={AB:F4} B={BB:F4}  Sell: A={AS:F4} B={BS:F4}",
            plattABuy, plattBBuy, plattASell, plattBSell);
        ctx.PlattABuy  = plattABuy;
        ctx.PlattBBuy  = plattBBuy;
        ctx.PlattASell = plattASell;
        ctx.PlattBSell = plattBSell;

        // ── 14. Isotonic calibration (PAVA) ────────────────────────────────────
        double[] isotonicBp = FitIsotonicCalibration(calSet, allTrees, trainSet, plattA, plattB);
        _logger.LogInformation("Isotonic calibration: {N} PAVA breakpoints", isotonicBp.Length / 2);
        ctx.IsotonicBp = isotonicBp;

        // ── 15. ECE on held-out test set ──────────────────────────────────────
        double ece = ComputeEce(testSet, allTrees, trainSet, plattA, plattB, isotonicBp);
        _logger.LogInformation("Post-Platt ECE={Ece:F4}", ece);
        ctx.Ece = ece;

        // ── 16. EV-optimal threshold (on cal set — no test-set leakage) ────────
        double optimalThreshold = ComputeOptimalThreshold(
            calSet, allTrees, trainSet, plattA, plattB, isotonicBp,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            stepBps: hp.ThresholdSearchStepBps);
        _logger.LogInformation("EV-optimal threshold={Thr:F3} (step={Bps}bps)", optimalThreshold, hp.ThresholdSearchStepBps);
        ctx.OptimalThreshold = optimalThreshold;

        // ── 17. Kelly fraction (half-Kelly or magnitude-adjusted, on cal set) ──
        double avgKellyFraction = ComputeAvgKellyFraction(calSet, allTrees, trainSet, plattA, plattB, isotonicBp,
            useAdjusted: hp.QrfUseAdjustedKelly);
        _logger.LogDebug("Average Kelly fraction (half-Kelly)={Kelly:F4}", avgKellyFraction);
        ctx.AvgKellyFraction = avgKellyFraction;

        // ── 18. Temperature scaling (optional alternative calibration) ─────────
        double temperatureScale = 0.0;
        if (hp.FitTemperatureScale && calSet.Count >= 10)
        {
            temperatureScale = FitTemperatureScaling(calSet, allTrees, trainSet);
            _logger.LogDebug("Temperature scaling: T={T:F4}", temperatureScale);
        }
        ctx.TemperatureScale = temperatureScale;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 5: Evaluate and refine (magnitude, importance, pruning, DW, MI, conformal, PSI)
    // ══════════════════════════════════════════════════════════════════════════

    private TrainingResult? EvaluateAndRefine(TrainingContext ctx)
    {
        var hp       = ctx.Hp;
        int F        = ctx.F;
        var trainSet = ctx.TrainSet;
        var calSet   = ctx.CalSet;
        var testSet  = ctx.TestSet;
        var allTrees = ctx.AllTrees;
        var newTrees = ctx.NewTrees;

        // ── 19. Magnitude regressor (linear, or 2-layer MLP when QrfMagHiddenDim > 0) ──
        var (magWeights, magBias) = FitLinearRegressor(trainSet, F, hp, ctx.Ct);
        ctx.MagWeights = magWeights;
        ctx.MagBias    = magBias;

        // ── 19b. MLP magnitude regressor — trained in addition to the linear one ─
        // The linear weights are retained for EvaluateModel RMSE consistency; the MLP
        // weights are stored in the snapshot and used by MLSignalScorer at inference.
        double[] mlpW1 = [], mlpB1 = [], mlpW2 = [];
        double   mlpB2 = 0.0;
        if (hp.QrfMagHiddenDim > 0 && trainSet.Count >= hp.MinSamples)
        {
            (mlpW1, mlpB1, mlpW2, mlpB2) = FitMlpMagnitudeRegressor(trainSet, F, hp.QrfMagHiddenDim, hp, ctx.Ct);
            _logger.LogInformation(
                "QRF MLP magnitude regressor fitted (H={H}, params={P}).",
                hp.QrfMagHiddenDim, F * hp.QrfMagHiddenDim + 2 * hp.QrfMagHiddenDim + 1);
        }
        ctx.MlpW1 = mlpW1;
        ctx.MlpB1 = mlpB1;
        ctx.MlpW2 = mlpW2;
        ctx.MlpB2 = mlpB2;

        // ── 20. Quantile magnitude regressor (pinball loss, optional) ──────────
        double[] magQ90Weights = [];
        double   magQ90Bias    = 0.0;
        if (hp.MagnitudeQuantileTau > 0.0 && trainSet.Count >= hp.MinSamples)
        {
            (magQ90Weights, magQ90Bias) = FitQuantileRegressor(trainSet, F, hp.MagnitudeQuantileTau,
                l2: hp.QrfQuantileL2, earlyStopPatience: hp.QrfQuantileEarlyStopPatience);
            _logger.LogDebug("Quantile magnitude regressor fitted (τ={Tau:F2}).", hp.MagnitudeQuantileTau);
        }
        ctx.MagQ90Weights = magQ90Weights;
        ctx.MagQ90Bias    = magQ90Bias;

        // ── 21. Permutation feature importance on test set ────────────────────
        var featureImportance = ctx.FeatureImportance;
        if (testSet.Count >= 10)
            featureImportance = ComputePermutationImportance(
                testSet, allTrees, trainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp, F, hp.QrfSeed,
                repeats: Math.Max(1, hp.QrfPermutationRepeats));
        ctx.FeatureImportance = featureImportance;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var topFeatures = featureImportance
                .Select((imp, idx) => (
                    Importance: imp,
                    Name: idx < MLFeatureHelper.FeatureNames.Length
                          ? MLFeatureHelper.FeatureNames[idx] : $"F{idx}"))
                .OrderByDescending(x => x.Importance)
                .Take(5);
            _logger.LogInformation("Top 5 features: {Features}",
                string.Join(", ", topFeatures.Select(f => $"{f.Name}={f.Importance:P1}")));
        }

        // ── 21b. Target leakage check (#13) ───────────────────────────────────
        {
            float maxImp = featureImportance.Length > 0 ? featureImportance.Max() : 0;
            if (maxImp > LeakageImportanceWarnFraction)
            {
                int leakIdx = Array.IndexOf(featureImportance, maxImp);
                string leakName = leakIdx < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[leakIdx] : $"F{leakIdx}";
                _logger.LogWarning(
                    "QuantileRF target leakage warning: feature '{Name}' has {Imp:P0} of total importance " +
                    "(threshold={Thr:P0}). Verify this feature does not contain forward-looking information.",
                    leakName, maxImp, LeakageImportanceWarnFraction);
            }
        }

        // ── 22. Calibration-set permutation importance (for warm-start transfer)
        ctx.CalImportanceScores = calSet.Count >= 10
            ? ComputeCalPermutationImportance(calSet, allTrees, trainSet, F, ctx.Ct)
            : new double[F];

        // ── 23. Feature pruning re-train pass ─────────────────────────────────
        var  activeMask  = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int  prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            _logger.LogInformation(
                "QuantileRF feature pruning: masking {Pruned}/{Total} low-importance features.",
                prunedCount, F);

            var maskedTrain = ApplyMask(trainSet, activeMask);
            var maskedCal   = ApplyMask(calSet,   activeMask);
            var maskedTest  = ApplyMask(testSet,  activeMask);

            // Rebuild forest on masked data using the same tree count and a deterministic
            // isolated RNG so the comparison to the full model is on equal footing.
            // Using treeCount/2 or the advancing master rng made the acceptance gate
            // inconsistent (smaller/differently-seeded forest vs. the full one).
            var pruneRng  = hp.QrfSeed != 0 ? new Random(hp.QrfSeed + 1999) : new Random();
            var pTrees    = BuildForestOnly(maskedTrain, ctx.TreeCount, F, ctx.SqrtF, ctx.CumDensity, pruneRng, ctx.Ct,
                                            importanceScores: null, maxDepth: ctx.EffectiveMaxDepth, minLeaf: ctx.EffectiveMinLeaf);
            var (pA, pB)  = FitPlattScaling(maskedCal, pTrees, maskedTrain);
            double[] pBp  = FitIsotonicCalibration(maskedCal, pTrees, maskedTrain, pA, pB);
            var pMetrics  = EvaluateModel(maskedTest, pTrees, maskedTrain, magWeights, magBias, pA, pB, pBp);

            // #9: Multi-metric pruning gate — check accuracy, Brier, and EV
            var fullMetrics = EvaluateModel(
                testSet, allTrees, trainSet, magWeights, magBias, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp);
            bool pruneAccepted = pMetrics.Accuracy >= fullMetrics.Accuracy - PruningAccuracyTolerance
                              && pMetrics.BrierScore <= fullMetrics.BrierScore + PruningAccuracyTolerance
                              && pMetrics.ExpectedValue >= fullMetrics.ExpectedValue - PruningAccuracyTolerance;

            if (pruneAccepted)
            {
                _logger.LogInformation(
                    "QuantileRF pruned model accepted: acc={Acc:P1}, BSS={B:F4}",
                    pMetrics.Accuracy, pMetrics.BrierScore);
                ctx.AllTrees = pTrees;
                ctx.TrainSet = maskedTrain;
                ctx.CalSet   = maskedCal;
                ctx.TestSet  = maskedTest;
                ctx.PlattA   = pA;  ctx.PlattB = pB;
                ctx.IsotonicBp = pBp;
                // Recompute calibration-dependent values
                var (pABuy, pBBuy, pASell, pBSell) =
                    FitClassConditionalPlatt(ctx.CalSet, ctx.AllTrees, ctx.TrainSet);
                ctx.PlattABuy  = pABuy;
                ctx.PlattBBuy  = pBBuy;
                ctx.PlattASell = pASell;
                ctx.PlattBSell = pBSell;
                if (hp.FitTemperatureScale && ctx.CalSet.Count >= 10)
                    ctx.TemperatureScale = FitTemperatureScaling(ctx.CalSet, ctx.AllTrees, ctx.TrainSet);
                ctx.Ece              = ComputeEce(ctx.TestSet, ctx.AllTrees, ctx.TrainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp);
                ctx.OptimalThreshold = ComputeOptimalThreshold(ctx.CalSet, ctx.AllTrees, ctx.TrainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp,
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax, stepBps: hp.ThresholdSearchStepBps);
                ctx.AvgKellyFraction = ComputeAvgKellyFraction(ctx.CalSet, ctx.AllTrees, ctx.TrainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp,
                    useAdjusted: hp.QrfUseAdjustedKelly);
                // Update local references for subsequent steps
                allTrees = ctx.AllTrees;
                trainSet = ctx.TrainSet;
                calSet   = ctx.CalSet;
                testSet  = ctx.TestSet;
            }
            else
            {
                _logger.LogInformation("QuantileRF pruned model rejected — keeping full model.");
                prunedCount = 0;
                activeMask  = new bool[F];
                Array.Fill(activeMask, true);
            }
        }
        else if (prunedCount == 0)
        {
            activeMask = new bool[F];
            Array.Fill(activeMask, true);
        }
        ctx.ActiveMask  = activeMask;
        ctx.PrunedCount = prunedCount;

        // ── 24. Durbin-Watson autocorrelation test on magnitude residuals ──────
        double durbinWatson = ComputeDurbinWatson(trainSet, magWeights, magBias, F);
        _logger.LogDebug("Durbin-Watson={DW:F4}", durbinWatson);
        ctx.DurbinWatson = durbinWatson;
        if (hp.DurbinWatsonThreshold > 0.0 && durbinWatson < hp.DurbinWatsonThreshold)
        {
            _logger.LogWarning(
                "QuantileRF magnitude residuals autocorrelated (DW={DW:F3} < {Thr:F2}).",
                durbinWatson, hp.DurbinWatsonThreshold);
            // #37: Optional hard gate on DW
            if (hp.QrfDurbinWatsonGateEnabled)
            {
                _logger.LogWarning("QuantileRF DW gate enabled — aborting training.");
                ctx.FailedGates.Add("DurbinWatson");
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                    ctx.CvResult, []);
            }
        }

        // ── 25. Mutual-information feature redundancy check ───────────────────
        string[] redundantPairs = [];
        if (hp.MutualInfoRedundancyThreshold > 0.0)
        {
            redundantPairs = ComputeRedundantFeaturePairs(trainSet, F, hp.MutualInfoRedundancyThreshold);
            if (redundantPairs.Length > 0)
                _logger.LogWarning(
                    "QuantileRF MI redundancy: {N} feature pairs exceed threshold.", redundantPairs.Length);
        }
        ctx.RedundantPairs = redundantPairs;

        // ── 26. Split-conformal q̂ ─────────────────────────────────────────────
        double conformalAlpha = Math.Clamp(1.0 - hp.ConformalCoverage, 0.01, 0.50);
        double conformalQHat  = ComputeConformalQHat(
            calSet, allTrees, trainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp, conformalAlpha);
        _logger.LogInformation("Conformal qHat={QHat:F4} ({Cov:P0} coverage)", conformalQHat, hp.ConformalCoverage);
        ctx.ConformalQHat = conformalQHat;

        // ── 27. Feature quantile breakpoints for PSI ──────────────────────────
        var trainFeatureList = new List<float[]>(trainSet.Count);
        foreach (var s in trainSet) trainFeatureList.Add(s.Features);
        ctx.FeatureQuantileBp = MLFeatureHelper.ComputeFeatureQuantileBreakpoints(trainFeatureList);

        // ── 27b. PSI-based feature drift gate ─────────────────────────────────
        // Bins current training samples into the parent model's quantile intervals and
        // computes PSI per feature. Emits a warning when the distribution has shifted
        // significantly (PSI > QrfPsiDriftWarnThreshold). This detects regime changes
        // that would invalidate warm-start weight transfer before training is accepted.
        if (ctx.WarmStart?.FeatureQuantileBreakpoints is { Length: > 0 } parentBpForPsi)
        {
            double avgPsi = ComputeAvgPsi(trainSet, parentBpForPsi, F);
            _logger.LogInformation("QRF PSI vs parent model: avgPSI={PSI:F4} (0.10=monitor, 0.25=significant)", avgPsi);
            if (hp.QrfPsiDriftWarnThreshold > 0.0 && avgPsi > hp.QrfPsiDriftWarnThreshold)
                _logger.LogWarning(
                    "QRF PSI drift gate: avgPSI={PSI:F4} exceeds warn threshold={Thr:F4}. " +
                    "Feature distribution has drifted significantly from parent model — " +
                    "consider triggering a cold retrain instead of warm-starting.",
                    avgPsi, hp.QrfPsiDriftWarnThreshold);

            // #39: Hard PSI gate — force cold retrain by discarding warm-start trees
            if (hp.QrfPsiDriftHardThreshold > 0.0 && avgPsi > hp.QrfPsiDriftHardThreshold)
            {
                _logger.LogWarning(
                    "QRF PSI hard gate: avgPSI={PSI:F4} > hard threshold={Thr:F4}. " +
                    "Discarding warm-start trees and forcing cold retrain.",
                    avgPsi, hp.QrfPsiDriftHardThreshold);
                ctx.AllTrees = [.. ctx.NewTrees];
                allTrees = ctx.AllTrees;
                ctx.FailedGates.Add("PsiDriftHard");
            }
        }

        // ── 28. Full evaluation on held-out test set ───────────────────────────
        var evalMetrics = EvaluateModel(
            testSet, allTrees, trainSet, magWeights, magBias, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp);
        ctx.EvalMetrics = evalMetrics with { OobAccuracy = ctx.OobAccuracy };

        // ── 29. Brier Skill Score ──────────────────────────────────────────────
        ctx.BrierSkillScore = ComputeBrierSkillScore(testSet, allTrees, trainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp);
        _logger.LogInformation("Brier Skill Score (BSS)={BSS:F4}", ctx.BrierSkillScore);

        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 6: Fit ensemble methods (OOB pruning, GES, stacking, diversity, etc.)
    // ══════════════════════════════════════════════════════════════════════════

    private void FitEnsembleMethods(TrainingContext ctx)
    {
        var hp       = ctx.Hp;
        var trainSet = ctx.TrainSet;
        var calSet   = ctx.CalSet;
        var allTrees = ctx.AllTrees;
        var newTrees = ctx.NewTrees;
        var oobMasks = ctx.OobMasks;

        // ── 30. OOB-contribution tree pruning (#4) — must run BEFORE GES/meta-learner
        //        so their weight arrays match the final tree count.
        int oobPrunedCount = 0;
        if (hp.OobPruningEnabled && newTrees.Count >= 2)
        {
            oobPrunedCount = PruneByOobContribution(trainSet, newTrees, oobMasks);
            // Rebuild allTrees with pruned newTrees
            allTrees = [.. ctx.WarmTrees, .. newTrees];
            ctx.AllTrees = allTrees;
            if (oobPrunedCount > 0)
                _logger.LogInformation(
                    "OOB pruning: removed {N}/{K} new trees whose removal improved OOB accuracy.",
                    oobPrunedCount, oobPrunedCount + newTrees.Count);
        }
        ctx.OobPrunedCount = oobPrunedCount;

        // ── 31. Greedy Ensemble Selection (#3) ────────────────────────────────
        int gesRounds = hp.QrfGesRounds > 0 ? hp.QrfGesRounds : DefaultGesRounds;
        double[] gesWeights = hp.EnableGreedyEnsembleSelection && calSet.Count >= MinCalSamplesPlatt
            ? RunGreedyTreeSelection(calSet, allTrees, trainSet,
                rounds: gesRounds, earlyStopPatience: hp.QrfGesEarlyStopPatience)
            : [];
        if (gesWeights.Length > 0)
        {
            int gesSelected = gesWeights.Count(w => w > 0);
            // #38: Log which trees were selected most frequently
            _logger.LogInformation("GES: {N}/{T} trees selected with non-zero weight (rounds={R}).",
                gesSelected, allTrees.Count, gesRounds);
        }
        ctx.GesWeights = gesWeights;

        // ── 32. Stacking meta-learner on per-tree probs (#2) ──────────────────
        var (metaWeights, metaBias) = FitMetaLearner(calSet, allTrees, trainSet);
        _logger.LogDebug("Meta-learner: {T} tree weights, bias={B:F4}", metaWeights.Length, metaBias);
        ctx.MetaWeights = metaWeights;
        ctx.MetaBias    = metaBias;

        // ── 33. Ensemble (tree) diversity metric (#5) ─────────────────────────
        double ensembleDiversity = ComputeTreeDiversity(allTrees, trainSet);
        _logger.LogDebug("Tree diversity (avg pairwise ρ)={Div:F4}", ensembleDiversity);
        if (hp.MaxEnsembleDiversity < 1.0 && ensembleDiversity > hp.MaxEnsembleDiversity)
            _logger.LogWarning(
                "QRF diversity warning: avg ρ={Div:F3} > threshold {Max:F2}. " +
                "Consider increasing tree count or adding more diversity via feature subsampling.",
                ensembleDiversity, hp.MaxEnsembleDiversity);
        ctx.EnsembleDiversity = ensembleDiversity;

        // ── 34. Per-tree calibration-set accuracy (#7) ────────────────────────
        ctx.TreeCalAccuracies = ComputePerTreeCalAccuracies(calSet, allTrees, trainSet);
        _logger.LogDebug("Per-tree cal accuracies computed for {T} trees.", ctx.TreeCalAccuracies.Length);

        // ── 35. Jackknife+ nonconformity residuals (#9) ───────────────────────
        ctx.JackknifeResiduals = ComputeJackknifeResiduals(trainSet, newTrees, oobMasks);
        _logger.LogInformation("Jackknife+ residuals: {N} samples", ctx.JackknifeResiduals.Length);

        // ── 36. Meta-label secondary classifier (#10) ─────────────────────────
        var (metaLabelWeights, metaLabelBias) = FitMetaLabelModel(calSet, allTrees, trainSet,
            featureImportance: ctx.FeatureImportance);
        _logger.LogDebug("Meta-label model: bias={B:F4}", metaLabelBias);
        ctx.MetaLabelWeights = metaLabelWeights;
        ctx.MetaLabelBias    = metaLabelBias;

        // ── 37. Abstention gate (#6) ──────────────────────────────────────────
        var (abstentionWeights, abstentionBias, abstentionThreshold) = FitAbstentionGate(
            calSet, allTrees, trainSet, ctx.PlattA, ctx.PlattB, metaLabelWeights, metaLabelBias,
            sweepThreshold: hp.QrfAbstentionSweepEnabled);
        _logger.LogDebug("Abstention gate: bias={B:F4} threshold={T:F2}", abstentionBias, abstentionThreshold);
        ctx.AbstentionWeights   = abstentionWeights;
        ctx.AbstentionBias      = abstentionBias;
        ctx.AbstentionThreshold = abstentionThreshold;

        // ── 37b. Calibration confidence interval (#8) ────────────────────────
        var (calCiStdA, calCiStdB) = ComputeCalibrationCI(calSet, allTrees, trainSet);
        if (calCiStdA > 0.0)
            _logger.LogDebug("Calibration CI: std(A)={StdA:F4}, std(B)={StdB:F4}", calCiStdA, calCiStdB);
        ctx.CalCiStdA = calCiStdA;
        ctx.CalCiStdB = calCiStdB;

        // ── 37c. Feature interactions (#27) ───────────────────────────────────
        ctx.FeatureInteractions = Compute2ndOrderFeatureInteractions(allTrees);
        if (ctx.FeatureInteractions.Length > 0)
            _logger.LogDebug("Top feature interactions: {Pairs}", string.Join(", ", ctx.FeatureInteractions.Take(5)));

        _logger.LogInformation(
            "QuantileRfModelTrainer complete: T={T} trees, acc={Acc:P1}, OOB={OOB:P1}, Brier={B:F4}, Sharpe={Sharpe:F2}",
            allTrees.Count, ctx.EvalMetrics.Accuracy, ctx.OobAccuracy, ctx.EvalMetrics.BrierScore, ctx.EvalMetrics.SharpeRatio);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PHASE 7: Serialize snapshot and return result
    // ══════════════════════════════════════════════════════════════════════════

    private TrainingResult SerializeSnapshot(TrainingContext ctx)
    {
        var hp       = ctx.Hp;
        var allTrees = ctx.AllTrees;

        // ── 38. Serialise model snapshot ──────────────────────────────────────
        // #22: Weight cascade at inference: InferenceHelpers.AggregateProbs combines
        // MetaWeights (stacking), EnsembleSelectionWeights (GES), and LearnerCalAccuracies
        // (per-tree cal accuracy) via softmax-weighted averaging. The three weighting
        // mechanisms are complementary: meta-learner handles tree correlation, GES handles
        // redundancy, and cal-accuracy handles individual tree reliability.
        var gbmTrees     = allTrees.Select(ConvertTreeNodesToGbm).ToList();
        var qrfWeightRow = ctx.FeatureImportance.Select(f => (double)f).ToArray();

        var snapshot = new ModelSnapshot
        {
            Type                       = ModelType,
            Version                    = ModelVersion,
            Features                   = MLFeatureHelper.FeatureNames,
            Means                      = ctx.Means,
            Stds                       = ctx.Stds,
            BaseLearnersK              = allTrees.Count,
            Weights                    = [qrfWeightRow],
            Biases                     = [],
            MagWeights                 = ctx.MagWeights,
            MagBias                    = ctx.MagBias,
            MagQ90Weights              = ctx.MagQ90Weights,
            MagQ90Bias                 = ctx.MagQ90Bias,
            PlattA                     = ctx.PlattA,
            PlattB                     = ctx.PlattB,
            PlattABuy                  = ctx.PlattABuy,
            PlattBBuy                  = ctx.PlattBBuy,
            PlattASell                 = ctx.PlattASell,
            PlattBSell                 = ctx.PlattBSell,
            AvgKellyFraction           = ctx.AvgKellyFraction,
            Metrics                    = ctx.EvalMetrics,
            OobAccuracy                = ctx.OobAccuracy,
            TrainSamples               = ctx.TrainSet.Count,
            TestSamples                = ctx.TestSet.Count,
            CalSamples                 = ctx.CalSet.Count,
            EmbargoSamples             = hp.EmbargoBarCount,
            TrainedOn                  = DateTime.UtcNow,
            TrainedAtUtc               = DateTime.UtcNow,
            FeatureImportance          = ctx.FeatureImportance,
            FeatureImportanceScores    = ctx.CalImportanceScores,
            FeatureStabilityScores     = ctx.CvResult.FeatureStabilityScores ?? [],
            ActiveFeatureMask          = ctx.ActiveMask,
            PrunedFeatureCount         = ctx.PrunedCount,
            OptimalThreshold           = ctx.OptimalThreshold,
            Ece                        = ctx.Ece,
            IsotonicBreakpoints        = ctx.IsotonicBp,
            ConformalQHat              = ctx.ConformalQHat,
            ConformalCoverage          = hp.ConformalCoverage,
            FeatureQuantileBreakpoints = ctx.FeatureQuantileBp,
            ParentModelId              = ctx.ParentModelId ?? 0,
            GenerationNumber           = ctx.GenerationNum,
            BrierSkillScore            = ctx.BrierSkillScore,
            SanitizedLearnerCount      = ctx.SanitizedCount,
            FracDiffD                  = hp.FracDiffD,
            AgeDecayLambda             = hp.AgeDecayLambda,
            DurbinWatsonStatistic      = ctx.DurbinWatson,
            TemperatureScale           = ctx.TemperatureScale,
            WalkForwardSharpeTrend     = ctx.CvResult.SharpeTrend,
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            GbmTreesJson               = JsonSerializer.Serialize(gbmTrees, JsonOptions),
            QrfWeights                 = [qrfWeightRow],
            // ── New v4.0 fields ───────────────────────────────────────────────
            MetaWeights                = ctx.MetaWeights,                   // #2
            MetaBias                   = ctx.MetaBias,                      // #2
            EnsembleSelectionWeights   = ctx.GesWeights,                    // #3
            OobPrunedLearnerCount      = ctx.OobPrunedCount,                // #4
            EnsembleDiversity          = ctx.EnsembleDiversity,             // #5
            AbstentionWeights          = ctx.AbstentionWeights,             // #6
            AbstentionBias             = ctx.AbstentionBias,                // #6
            AbstentionThreshold        = ctx.AbstentionThreshold,           // #6
            LearnerCalAccuracies       = ctx.TreeCalAccuracies,             // #7
            JackknifeResiduals         = ctx.JackknifeResiduals,            // #9
            MetaLabelWeights           = ctx.MetaLabelWeights,              // #10
            MetaLabelBias              = ctx.MetaLabelBias,                 // #10
            MetaLabelThreshold         = 0.5,                           // #10
            RedundantFeaturePairs      = ctx.RedundantPairs,                // #13
            // ── MLP magnitude regressor (QrfMagHiddenDim > 0) ─────────────────
            QrfMlpHiddenDim            = hp.QrfMagHiddenDim,
            QrfMlpW1                   = ctx.MlpW1,
            QrfMlpB1                   = ctx.MlpB1,
            QrfMlpW2                   = ctx.MlpW2,
            QrfMlpB2                   = ctx.MlpB2,
        };

        // #15: Memory pressure check — estimate serialized size before allocating
        byte[] modelBytes;
        try
        {
            modelBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        }
        catch (OutOfMemoryException)
        {
            _logger.LogError("QuantileRF: serialization failed due to OOM (estimated tree count={T}).", allTrees.Count);
            return new TrainingResult(ctx.EvalMetrics, ctx.CvResult, []);
        }

        double modelSizeMb = modelBytes.Length / (1024.0 * 1024.0);
        if (hp.QrfMaxModelSizeMb > 0 && modelSizeMb > hp.QrfMaxModelSizeMb)
        {
            _logger.LogWarning(
                "QuantileRF model size {Size:F1}MB exceeds limit {Limit}MB — returning empty model.",
                modelSizeMb, hp.QrfMaxModelSizeMb);
            return new TrainingResult(ctx.EvalMetrics, ctx.CvResult, []);
        }
        _logger.LogInformation("QuantileRF model serialized: {Size:F1}MB", modelSizeMb);

        // #36: Log total training time
        ctx.TotalStopwatch.Stop();
        _logger.LogInformation("QuantileRF training completed in {Elapsed}.", ctx.TotalStopwatch.Elapsed);

        return new TrainingResult(ctx.EvalMetrics, ctx.CvResult, modelBytes);
    }

    // ── Walk-forward cross-validation ─────────────────────────────────────────

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        int                  sqrtF,
        int                  treeCount,
        CancellationToken    ct,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        int folds   = hp.WalkForwardFolds > 0 ? hp.WalkForwardFolds : 3;
        int embargo = hp.EmbargoBarCount;
        int cvTrees = Math.Max(10, treeCount / 2);

        int foldSize = samples.Count / (folds + 1);
        if (foldSize < 50)
        {
            _logger.LogWarning("QuantileRF CV: fold size too small ({Size} < 50), skipping CV.", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        // Folds are independent — run in parallel (#8)
        var foldResults =
            new (double Acc, double F1, double EV, double Sharpe, double[] Imp, bool IsBad)?[folds];

        // LookbackWindow-aware purge extra bars (#11)
        int lookbackPurge = MLFeatureHelper.LookbackWindow - 1;

        Parallel.For(0, folds, new ParallelOptions { CancellationToken = ct }, fold =>
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;

            // Subtract LookbackWindow − 1 extra bars beyond the embargo so that no
            // feature computed from the test-period candles leaks into training (#11)
            int trainEnd  = Math.Max(0, testStart - embargo - lookbackPurge);

            // Purge horizon: remove samples whose forward label window overlaps test fold
            if (hp.PurgeHorizonBars > 0)
                trainEnd = Math.Max(0, Math.Min(trainEnd, testStart - hp.PurgeHorizonBars));

            if (trainEnd < hp.MinSamples)
            {
                _logger.LogDebug("QuantileRF CV fold {Fold} skipped (trainEnd={N} < minSamples)", fold, trainEnd);
                return;
            }

            var foldTrain = samples[..trainEnd];
            var foldTest  = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) return;

            // Per-fold isolated RNG — no shared state between parallel folds
            var foldRng = new Random(fold * 31 + 17);

            int foldTrainCount = foldTrain.Count;
            double[] foldImpAccum = new double[F];
            int      foldImpSplits = 0;
            var      foldTrees    = new List<List<TreeNode>>(cvTrees);

            // Reserve the last 20 % of foldTrain for intra-fold calibration so that
            // (a) trees are not evaluated on their own training data when calibrating, and
            // (b) the fold threshold is learned on calibrated probabilities — matching the
            // final model's inference path (Platt + EV-optimal threshold).
            int foldCalSize  = Math.Max(10, foldTrainCount / 5);
            int foldBuildCnt = foldTrainCount - foldCalSize;
            var foldBuildSet = foldTrain[..foldBuildCnt];
            var foldCalSlice = foldTrain[foldBuildCnt..];

            for (int tIdx = 0; tIdx < cvTrees; tIdx++)
            {
                var bootstrapIdx = new List<int>(foldBuildCnt);
                for (int bi = 0; bi < foldBuildCnt; bi++)
                    bootstrapIdx.Add(foldRng.Next(foldBuildCnt));

                var nodes = new List<TreeNode>();
                BuildTree(foldBuildSet, bootstrapIdx, 0, nodes, foldRng, F, sqrtF, foldImpAccum, ref foldImpSplits,
                          importanceScores: null, maxDepth: maxDepth, minLeaf: minLeaf);
                if (nodes.Count > 0) foldTrees.Add(nodes);
            }

            if (foldTrees.Count == 0) return;

            // Intra-fold Platt calibration + EV-optimal threshold (no test-set leakage)
            var (foldPlattA, foldPlattB) = foldCalSlice.Count >= 10
                ? FitPlattScaling(foldCalSlice, foldTrees, foldBuildSet)
                : (1.0, 0.0);
            double foldThreshold = foldCalSlice.Count >= 30
                ? ComputeOptimalThreshold(foldCalSlice, foldTrees, foldBuildSet, foldPlattA, foldPlattB, [],
                    hp.ThresholdSearchMin, hp.ThresholdSearchMax)
                : 0.5;

            int    nFold  = foldTest.Count;
            int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
            double brierSum = 0, evSum = 0;
            var    predictions = new (int Predicted, int Actual)[nFold];

            for (int i = 0; i < nFold; i++)
            {
                var    s    = foldTest[i];
                double prob = PredictProb(s.Features, foldTrees, foldBuildSet, foldPlattA, foldPlattB);
                int    yHat = prob >= foldThreshold ? 1 : 0;
                int    y    = s.Direction > 0 ? 1 : 0;
                if (yHat == y) correct++;
                if (yHat == 1 && y == 1) tp++;
                if (yHat == 1 && y == 0) fp++;
                if (yHat == 0 && y == 1) fn++;
                if (yHat == 0 && y == 0) tn++;
                brierSum      += (prob - y) * (prob - y);
                evSum         += (yHat == y ? 1 : -1) * (double)s.Magnitude;
                predictions[i] = (yHat, y);
            }

            double acc    = (double)correct / nFold;
            double prec   = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
            double rec    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
            double f1     = (prec + rec) > 0 ? 2 * prec * rec / (prec + rec) : 0;
            double brier  = brierSum / nFold;
            double ev     = evSum / nFold;

            // Equity-curve Sharpe replaces the (acc−0.5)/(brier+0.01) proxy so that
            // the Sharpe trend gate and AvgSharpe metric reflect actual risk-adjusted returns.
            var (maxDD, curveSharpe) = ComputeEquityCurveStats(predictions);
            double sharpe = curveSharpe;

            bool isBad = false;
            if (hp.MaxFoldDrawdown < 1.0 && maxDD > hp.MaxFoldDrawdown) isBad = true;
            if (hp.MinFoldCurveSharpe > -99.0 && curveSharpe < hp.MinFoldCurveSharpe) isBad = true;

            double totalImpFold = 0;
            for (int fi = 0; fi < F; fi++) totalImpFold += foldImpAccum[fi];
            var normImp = new double[F];
            for (int fi = 0; fi < F; fi++)
                normImp[fi] = totalImpFold > Eps ? foldImpAccum[fi] / totalImpFold : 0.0;

            // Write to fold-indexed slot — no cross-slot races
            foldResults[fold] = (acc, f1, ev, sharpe, normImp, isBad);

            _logger.LogDebug(
                "QuantileRF CV fold {Fold}: acc={Acc:P1}, f1={F1:F3}, ev={EV:F4}, sharpe={Sharpe:F2}, maxDD={DD:P1}",
                fold, acc, f1, ev, sharpe, maxDD);
        });

        // Aggregate parallel results (preserve fold order for Sharpe trend)
        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var impLists   = new List<double[]>(folds);
        int badFolds   = 0;

        foreach (var r in foldResults)
        {
            if (r is null) continue;
            accList.Add(r.Value.Acc);
            f1List.Add(r.Value.F1);
            evList.Add(r.Value.EV);
            sharpeList.Add(r.Value.Sharpe);
            impLists.Add(r.Value.Imp);
            if (r.Value.IsBad) badFolds++;
        }

        if (accList.Count == 0) return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);

        // Equity curve gate decision
        double badFoldThreshold = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool equityCurveGateFailed = badFolds > (int)(accList.Count * badFoldThreshold);
        if (equityCurveGateFailed)
            _logger.LogWarning(
                "QuantileRF equity-curve gate: {BadFolds}/{TotalFolds} CV folds failed.", badFolds, accList.Count);

        // Sharpe trend gate
        double sharpeTrend = ComputeSharpeTrend(sharpeList);
        if (hp.MinSharpeTrendSlope > -99.0 && sharpeTrend < hp.MinSharpeTrendSlope)
        {
            _logger.LogWarning(
                "QuantileRF Sharpe trend gate: slope={Slope:F3} < threshold={Thr:F3}",
                sharpeTrend, hp.MinSharpeTrendSlope);
            equityCurveGateFailed = true;
        }

        // Feature stability scores (importance CoV across folds)
        double[]? featureStabilityScores = null;
        if (impLists.Count >= 2)
        {
            featureStabilityScores = new double[F];
            int foldCount = impLists.Count;
            for (int j = 0; j < F; j++)
            {
                double sumImp = 0.0;
                for (int fi = 0; fi < foldCount; fi++) sumImp += impLists[fi][j];
                double meanImp = sumImp / foldCount;
                double varImp  = 0.0;
                for (int fi = 0; fi < foldCount; fi++)
                {
                    double d = impLists[fi][j] - meanImp;
                    varImp += d * d;
                }
                double stdImp = foldCount > 1 ? Math.Sqrt(varImp / (foldCount - 1)) : 0.0;
                featureStabilityScores[j] = meanImp > 1e-10 ? stdImp / meanImp : 0.0;
            }
        }

        double avgAcc = accList.Average();
        return (new WalkForwardResult(
            AvgAccuracy:            avgAcc,
            StdAccuracy:            StdDev(accList, avgAcc),
            AvgF1:                  f1List.Average(),
            AvgEV:                  evList.Average(),
            AvgSharpe:              sharpeList.Average(),
            FoldCount:              accList.Count,
            SharpeTrend:            sharpeTrend,
            FeatureStabilityScores: featureStabilityScores),
            equityCurveGateFailed);
    }

    // ── Tree building ─────────────────────────────────────────────────────────

    /// <summary>
    /// Recursively builds a single decision tree using variance-reduction (Gini-equivalent
    /// for binary labels). Uses histogram-based split finding (256 equal-width bins) for
    /// O(N + 256) per feature candidate per node instead of O(N log N) sort-based search.
    /// Feature candidates are sampled via Fisher-Yates partial shuffle (unbiased) or
    /// importance-weighted CDF when warm-start scores are available.
    /// </summary>
    private static void BuildTree(
        List<TrainingSample> trainSet,
        List<int>            sampleIdx,
        int                  depth,
        List<TreeNode>       nodes,
        Random               rng,
        int                  F,
        int                  sqrtF,
        double[]             impAccum,
        ref int              impSplits,
        float[]?             importanceScores = null,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        var node = new TreeNode();
        nodes.Add(node);

        if (sampleIdx.Count < minLeaf || depth >= maxDepth)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        var candidateFeats = importanceScores is { Length: > 0 }
            ? GenerateBiasedCandidateFeats(F, sqrtF, importanceScores, rng)
            : FisherYatesPartialShuffle(F, sqrtF, rng);

        int    bestFeat   = -1;
        double bestThresh = 0.0;
        double bestGain   = -1.0;

        // Inline parent variance: p*(1-p) for binary labels
        int parentPos = 0;
        foreach (int idx in sampleIdx) if (trainSet[idx].Direction > 0) parentPos++;
        int    totalN    = sampleIdx.Count;
        double parentP   = (double)parentPos / totalN;
        double parentVar = parentP * (1.0 - parentP);

        // Histogram-based split finding: 256 equal-width bins per feature.
        // O(N + 256) per feature candidate, replacing O(N log N) sort.
        // Bin arrays are allocated once and reused across feature candidates.
        const int numBins = 256;
        var binPos   = new int[numBins];
        var binTotal = new int[numBins];

        foreach (int fi in candidateFeats)
        {
            // Pass 1: find min/max of this feature across samples in sampleIdx
            double fMin = double.MaxValue, fMax = double.MinValue;
            foreach (int idx in sampleIdx)
            {
                double v = trainSet[idx].Features[fi];
                if (v < fMin) fMin = v;
                if (v > fMax) fMax = v;
            }

            double range = fMax - fMin;
            if (range < 1e-12) continue; // all values identical — no split possible

            double binWidth = range / numBins;

            // Pass 2: bin samples and count pos/total per bin
            Array.Clear(binPos, 0, numBins);
            Array.Clear(binTotal, 0, numBins);
            foreach (int idx in sampleIdx)
            {
                int bin = Math.Min(numBins - 1, (int)((trainSet[idx].Features[fi] - fMin) / binWidth));
                binTotal[bin]++;
                if (trainSet[idx].Direction > 0) binPos[bin]++;
            }

            // Pass 3: sweep bins left-to-right, accumulate posLeft/totalLeft
            int leftTotal = 0, leftPos = 0;
            for (int b = 0; b < numBins - 1; b++)
            {
                leftTotal += binTotal[b];
                leftPos   += binPos[b];

                if (leftTotal < minLeaf) continue;
                int rightTotal = totalN - leftTotal;
                if (rightTotal < minLeaf) break;

                int rightPos = parentPos - leftPos;
                double meanL = (double)leftPos  / leftTotal;
                double meanR = (double)rightPos / rightTotal;

                double weightedVar =
                    (leftTotal  * meanL * (1.0 - meanL)
                   + rightTotal * meanR * (1.0 - meanR))
                   / totalN;

                double gain = parentVar - weightedVar;
                if (gain > bestGain)
                {
                    bestGain   = gain;
                    bestFeat   = fi;
                    bestThresh = fMin + (b + 1) * binWidth; // bin boundary
                }
            }
        }

        if (bestFeat < 0 || bestGain <= 0)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        // Partition into left/right child sets using the chosen threshold
        var leftIndices  = new List<int>(totalN);
        var rightIndices = new List<int>(totalN);
        foreach (int idx in sampleIdx)
        {
            if ((double)trainSet[idx].Features[bestFeat] <= bestThresh)
                leftIndices.Add(idx);
            else
                rightIndices.Add(idx);
        }

        if (leftIndices.Count < minLeaf || rightIndices.Count < minLeaf)
        {
            PopulateLeafNode(node, trainSet, sampleIdx);
            return;
        }

        impAccum[bestFeat] += bestGain;
        impSplits++;

        node.SplitFeat   = bestFeat;
        node.SplitThresh = bestThresh;
        node.LeftChild   = nodes.Count;
        BuildTree(trainSet, leftIndices,  depth + 1, nodes, rng, F, sqrtF, impAccum, ref impSplits, importanceScores, maxDepth, minLeaf);
        node.RightChild  = nodes.Count;
        BuildTree(trainSet, rightIndices, depth + 1, nodes, rng, F, sqrtF, impAccum, ref impSplits, importanceScores, maxDepth, minLeaf);
    }

    /// <summary>Populates a leaf node with class counts from the sample indices.</summary>
    private static void PopulateLeafNode(TreeNode node, List<TrainingSample> trainSet, List<int> sampleIdx)
    {
        int posCount = 0;
        foreach (int idx in sampleIdx) if (trainSet[idx].Direction > 0) posCount++;
        node.LeafDirection  = sampleIdx.Count > 0 ? (double)posCount / sampleIdx.Count : 0.5;
        node.LeafPosCount   = posCount;
        node.LeafTotalCount = sampleIdx.Count;
    }

    /// <summary>
    /// #2: Unbiased Fisher-Yates partial shuffle — selects <paramref name="k"/> indices
    /// from 0…<paramref name="n"/>-1 without replacement. O(k) time and allocation.
    /// </summary>
    private static List<int> FisherYatesPartialShuffle(int n, int k, Random rng)
    {
        k = Math.Min(k, n);
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        for (int i = 0; i < k; i++)
        {
            int j = rng.Next(i, n);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return [.. indices.AsSpan(0, k)];
    }

    /// <summary>
    /// Builds a forest without OOB tracking — used for the feature pruning re-train pass
    /// where OOB masks are not needed and speed is preferred.
    /// </summary>
    private static List<List<TreeNode>> BuildForestOnly(
        List<TrainingSample> trainSet,
        int                  treeCount,
        int                  F,
        int                  sqrtF,
        double[]?            cumDensity,
        Random               rng,
        CancellationToken    ct,
        float[]?             importanceScores = null,
        int                  maxDepth = DefaultMaxDepth,
        int                  minLeaf  = DefaultMinLeaf)
    {
        int trainCount  = trainSet.Count;
        var trees       = new List<List<TreeNode>>(treeCount);

        // Build pos/neg index lists for stratified bootstrap (matches main training loop)
        var posIdx = new List<int>(trainCount);
        var negIdx = new List<int>(trainCount);
        for (int i = 0; i < trainCount; i++)
        {
            if (trainSet[i].Direction > 0) posIdx.Add(i);
            else                           negIdx.Add(i);
        }
        bool useStratified = posIdx.Count >= MinStratifiedClassCount && negIdx.Count >= MinStratifiedClassCount;

        // #33: Parallel tree construction (matching main training loop)
        var treeSeeds = new int[treeCount];
        for (int i = 0; i < treeCount; i++) treeSeeds[i] = rng.Next();

        var treeSlots = new List<TreeNode>?[treeCount];
        Parallel.For(0, treeCount, new ParallelOptions { CancellationToken = ct }, tIdx =>
        {
            var localRng = new Random(treeSeeds[tIdx]);
            var localAccum = new double[F];
            int localSplits = 0;
            var bootstrapIdx = new List<int>(trainCount);

            if (useStratified)
            {
                for (int bi = 0; bi < posIdx.Count; bi++)
                    bootstrapIdx.Add(posIdx[localRng.Next(posIdx.Count)]);
                for (int bi = 0; bi < negIdx.Count; bi++)
                    bootstrapIdx.Add(negIdx[localRng.Next(negIdx.Count)]);
                for (int i = bootstrapIdx.Count - 1; i > 0; i--)
                {
                    int j = localRng.Next(i + 1);
                    (bootstrapIdx[i], bootstrapIdx[j]) = (bootstrapIdx[j], bootstrapIdx[i]);
                }
            }
            else
            {
                for (int bi = 0; bi < trainCount; bi++)
                    bootstrapIdx.Add(cumDensity is null ? localRng.Next(trainCount) : SampleWeighted(localRng, cumDensity));
            }

            var nodes = new List<TreeNode>();
            BuildTree(trainSet, bootstrapIdx, 0, nodes, localRng, F, sqrtF, localAccum, ref localSplits, importanceScores, maxDepth, minLeaf);
            treeSlots[tIdx] = nodes.Count > 0 ? nodes : null;
        });

        foreach (var slot in treeSlots)
            if (slot is not null) trees.Add(slot);

        return trees;
    }

    // ── Per-tree leaf-fraction probability ───────────────────────────────────

    /// <summary>
    /// #30: Iterative (non-recursive) traversal to the matching leaf. Returns the
    /// fraction of positive (Buy) training samples: LeafPosCount / LeafTotalCount.
    /// Returns 0.5 for degenerate leaves with no samples or out-of-bounds indices.
    /// </summary>
    private static double GetLeafProb(List<TreeNode> nodes, int nodeIndex, float[] features)
    {
        int idx = nodeIndex;
        while (idx >= 0 && idx < nodes.Count)
        {
            var node = nodes[idx];
            if (node.SplitFeat < 0 || node.SplitFeat >= features.Length)
                return node.LeafTotalCount > 0
                    ? (double)node.LeafPosCount / node.LeafTotalCount
                    : 0.5;
            idx = features[node.SplitFeat] <= node.SplitThresh
                ? node.LeftChild
                : node.RightChild;
        }
        return 0.5;
    }

    // ── Raw QRF probability (leaf-fraction, no Platt) ─────────────────────────

    /// <summary>
    /// Average leaf-fraction probability across all trees (uncalibrated).
    /// #47: The <paramref name="trainSet"/> parameter is retained for call-site
    /// compatibility across trainers but is not used — leaf counts are pre-computed
    /// during tree construction and stored in the nodes directly.
    /// </summary>
    private static double PredictRawProb(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (allTrees.Count == 0) return 0.5;
        double sum = 0.0;
        foreach (var tree in allTrees)
            sum += GetLeafProb(tree, 0, features);
        double prob = sum / allTrees.Count;
        return double.IsFinite(prob) ? prob : 0.5;
    }

    // ── Calibrated probability (Platt + optional isotonic) ────────────────────

    /// <summary>
    /// Produces a calibrated probability: raw QRF leaf-fraction → Platt sigmoid →
    /// optional isotonic PAVA correction. Used for threshold decisions and evaluation.
    /// </summary>
    private static double PredictProb(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]?            isotonicBp = null)
    {
        double raw = PredictRawProb(features, allTrees, trainSet);
        raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
        double logit  = MLFeatureHelper.Logit(raw);
        double calibP = MLFeatureHelper.Sigmoid(plattA * logit + plattB);
        if (isotonicBp is { Length: >= 4 })
            calibP = ApplyIsotonicCalibration(calibP, isotonicBp);
        return calibP;
    }

    // ── Platt scaling (SGD, 200 epochs) ───────────────────────────────────────

    /// <summary>
    /// #4: Platt scaling with early convergence termination. SGD stops when the
    /// absolute change in both A and B falls below <see cref="PlattConvergenceDelta"/>.
    /// </summary>
    private static (double A, double B) FitPlattScaling(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (calSet.Count < MinCalSamples) return (1.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var labels = new double[n];

        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]  = MLFeatureHelper.Logit(raw);
            labels[i]  = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        double plattA = 1.0, plattB = 0.0;

        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double p   = MLFeatureHelper.Sigmoid(plattA * logits[i] + plattB);
                double err = p - labels[i];
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            plattA -= stepA;
            plattB -= stepB;

            // #4: Early convergence — stop when updates are negligible
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        return (double.IsFinite(plattA) ? plattA : 1.0,
                double.IsFinite(plattB) ? plattB : 0.0);
    }

    // ── Class-conditional Platt (full calset, class-weighted labels) ─────────
    //
    // Buy calibrator:  labels buy=1, sell=0, buy samples weighted 3:1 vs sell.
    // Sell calibrator:  labels sell=1, buy=0, sell samples weighted 3:1 vs buy.
    // Both calibrators see the full calibration set so both classes constrain the
    // sigmoid — matching the approach used by GbmModelTrainer.

    private static (double ABuy, double BBuy, double ASell, double BSell) FitClassConditionalPlatt(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (calSet.Count < MinCalSamplesPlatt) return (0.0, 0.0, 0.0, 0.0);

        int n      = calSet.Count;
        var logits = new double[n];
        var isBuy  = new bool[n];
        int buyCount = 0;
        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            logits[i]  = MLFeatureHelper.Logit(raw);
            isBuy[i]   = calSet[i].Direction > 0;
            if (isBuy[i]) buyCount++;
        }
        int sellCount = n - buyCount;

        // #5: Adaptive class weights proportional to inverse class frequency
        // (replaces hardcoded 3:1). Minority class gets higher weight so that
        // both classes contribute equally to the gradient regardless of imbalance.
        double buyWeightForBuyCal  = sellCount > 0 ? (double)sellCount / buyCount  : 1.0;
        double sellWeightForBuyCal = 1.0;
        double sellWeightForSellCal = buyCount > 0 ? (double)buyCount / sellCount : 1.0;
        double buyWeightForSellCal  = 1.0;

        // Clamp weights to avoid extreme ratios
        buyWeightForBuyCal   = Math.Clamp(buyWeightForBuyCal,   1.0, 10.0);
        sellWeightForSellCal = Math.Clamp(sellWeightForSellCal, 1.0, 10.0);

        // Buy calibrator: standard labels (buy=1, sell=0), upweight buy samples
        double aBuy = 1.0, bBuy = 0.0;
        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(aBuy * logits[i] + bBuy);
                double label  = isBuy[i] ? 1.0 : 0.0;
                double w      = isBuy[i] ? buyWeightForBuyCal : sellWeightForBuyCal;
                double err    = (calibP - label) * w;
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            aBuy -= stepA;
            bBuy -= stepB;
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        // Sell calibrator: inverted labels (sell=1, buy=0), upweight sell samples
        double aSell = 1.0, bSell = 0.0;
        for (int epoch = 0; epoch < PlattMaxEpochs; epoch++)
        {
            double dA = 0, dB = 0;
            for (int i = 0; i < n; i++)
            {
                double calibP = MLFeatureHelper.Sigmoid(aSell * logits[i] + bSell);
                double label  = isBuy[i] ? 0.0 : 1.0;
                double w      = isBuy[i] ? buyWeightForSellCal : sellWeightForSellCal;
                double err    = (calibP - label) * w;
                dA += err * logits[i];
                dB += err;
            }
            double stepA = PlattLearningRate * dA / n;
            double stepB = PlattLearningRate * dB / n;
            aSell -= stepA;
            bSell -= stepB;
            if (Math.Abs(stepA) < PlattConvergenceDelta && Math.Abs(stepB) < PlattConvergenceDelta)
                break;
        }

        return (double.IsFinite(aBuy)  ? aBuy  : 0.0,
                double.IsFinite(bBuy)  ? bBuy  : 0.0,
                double.IsFinite(aSell) ? aSell : 0.0,
                double.IsFinite(bSell) ? bSell : 0.0);
    }

    // ── Isotonic calibration (PAVA) ───────────────────────────────────────────

    /// <summary>
    /// #6: Isotonic calibration (PAVA) with minimum block size regularisation.
    /// Blocks smaller than <see cref="IsotonicMinBlockSize"/> are merged with their
    /// neighbour to prevent overfitting on small calibration sets.
    /// </summary>
    private static double[] FitIsotonicCalibration(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB)
    {
        if (calSet.Count < MinCalSamples) return [];

        int n     = calSet.Count;
        var pairs = new (double P, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double raw = PredictRawProb(calSet[i].Features, allTrees, trainSet);
            raw        = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            double p   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(raw) + plattB);
            pairs[i]   = (p, calSet[i].Direction > 0 ? 1.0 : 0.0);
        }
        Array.Sort(pairs, (a, b) => a.P.CompareTo(b.P));

        // Standard PAVA
        var stack = new List<(double SumY, double SumP, int Count)>(pairs.Length);
        foreach (var (P, Y) in pairs)
        {
            stack.Add((Y, P, 1));
            while (stack.Count >= 2)
            {
                var (lastSumY, lastSumP, lastCount) = stack[^1];
                var (prevSumY, prevSumP, prevCount) = stack[^2];
                if (prevSumY / prevCount > lastSumY / lastCount)
                {
                    stack.RemoveAt(stack.Count - 1);
                    stack[^1] = (prevSumY + lastSumY, prevSumP + lastSumP, prevCount + lastCount);
                }
                else break;
            }
        }

        // #6: Merge blocks smaller than IsotonicMinBlockSize with their right neighbour
        // to regularise against overfitting on small cal sets.
        for (int i = stack.Count - 2; i >= 0; i--)
        {
            if (stack[i].Count < IsotonicMinBlockSize && i + 1 < stack.Count)
            {
                var (sy, sp, sc) = stack[i];
                var (ny, np, nc) = stack[i + 1];
                stack[i + 1] = (sy + ny, sp + np, sc + nc);
                stack.RemoveAt(i);
            }
        }

        var bp = new double[stack.Count * 2];
        for (int i = 0; i < stack.Count; i++)
        {
            bp[i * 2]     = stack[i].SumP / stack[i].Count;
            bp[i * 2 + 1] = stack[i].SumY / stack[i].Count;
        }
        return bp;
    }

    private static double ApplyIsotonicCalibration(double p, double[] bp)
    {
        if (bp.Length < 4) return p;
        int nPoints = bp.Length / 2;
        if (p <= bp[0])                 return bp[1];
        if (p >= bp[(nPoints - 1) * 2]) return bp[(nPoints - 1) * 2 + 1];

        int lo = 0, hi = nPoints - 2;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (bp[(mid + 1) * 2] <= p) lo = mid + 1;
            else hi = mid;
        }
        double x0 = bp[lo * 2],       y0 = bp[lo * 2 + 1];
        double x1 = bp[(lo + 1) * 2], y1 = bp[(lo + 1) * 2 + 1];
        return Math.Abs(x1 - x0) < 1e-15
            ? (y0 + y1) * 0.5
            : y0 + (p - x0) * (y1 - y0) / (x1 - x0);
    }

    // ── ECE (10 equal-width bins) ─────────────────────────────────────────────

    private static double ComputeEce(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  bins = 10)
    {
        if (testSet.Count < bins) return 1.0;

        var binAcc  = new double[bins];
        var binConf = new double[bins];
        var binCnt  = new int[bins];

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    binI = Math.Clamp((int)(p * bins), 0, bins - 1);
            binConf[binI] += p;
            if (s.Direction > 0) binAcc[binI]++; // positive-class frequency, not classification accuracy
            binCnt[binI]++;
        }

        double ece = 0;
        int    n   = testSet.Count;
        for (int b = 0; b < bins; b++)
        {
            if (binCnt[b] == 0) continue;
            ece += (double)binCnt[b] / n * Math.Abs(binAcc[b] / binCnt[b] - binConf[b] / binCnt[b]);
        }
        return ece;
    }

    // ── EV-optimal threshold sweep ────────────────────────────────────────────

    /// <summary>
    /// #3: Uses <c>hp.ThresholdSearchStepBps</c> for finer-grained search.
    /// Default 50 bps = 0.5 % steps (vs. legacy 100 bps = 1 % steps).
    /// </summary>
    private static double ComputeOptimalThreshold(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  searchMin = 30,
        int                  searchMax = 75,
        int                  stepBps   = 50)
    {
        if (calSet.Count < 30) return 0.5;

        var probs = new double[calSet.Count];
        for (int i = 0; i < calSet.Count; i++)
            probs[i] = PredictProb(calSet[i].Features, allTrees, trainSet, plattA, plattB, isotonicBp);

        double bestEv = double.MinValue, bestThreshold = 0.5;
        int step = Math.Max(1, stepBps);
        for (int bps = searchMin * 100; bps <= searchMax * 100; bps += step)
        {
            double t = bps / 10000.0, ev = 0;
            for (int i = 0; i < calSet.Count; i++)
            {
                bool correct = (probs[i] >= t) == (calSet[i].Direction > 0);
                ev += (correct ? 1 : -1) * (double)calSet[i].Magnitude;
            }
            ev /= calSet.Count;
            if (ev > bestEv) { bestEv = ev; bestThreshold = t; }
        }
        return bestThreshold;
    }

    // ── Kelly fraction (half-Kelly or magnitude-adjusted) ────────────────────

    /// <summary>
    /// #44: When <c>useAdjusted</c> is true, uses the magnitude-aware Kelly:
    /// f = p − (1−p) × avgLoss/avgWin. Otherwise uses simplified 2p−1.
    /// Always applies half-Kelly (÷2) for conservatism.
    /// </summary>
    private static double ComputeAvgKellyFraction(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        bool                 useAdjusted = false)
    {
        if (calSet.Count == 0) return 0;

        if (useAdjusted)
        {
            // Compute average win/loss magnitudes on the calibration set
            double winSum = 0, lossSum = 0;
            int    winN = 0,   lossN = 0;
            foreach (var s in calSet)
            {
                double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
                bool correct = (p >= 0.5) == (s.Direction > 0);
                if (correct) { winSum  += (double)s.Magnitude; winN++; }
                else         { lossSum += (double)s.Magnitude; lossN++; }
            }
            double avgWin  = winN  > 0 ? winSum  / winN  : 1.0;
            double avgLoss = lossN > 0 ? lossSum / lossN : 1.0;
            double ratio   = avgWin > Eps ? avgLoss / avgWin : 1.0;

            double sum = 0;
            foreach (var s in calSet)
            {
                double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
                double kelly = p - (1.0 - p) * ratio;
                sum += Math.Max(0, kelly);
            }
            return sum / calSet.Count * 0.5;
        }

        // Simplified Kelly: 2p − 1 (assumes symmetric payoff)
        double simpleSum = 0;
        foreach (var s in calSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            simpleSum += Math.Max(0, 2 * p - 1);
        }
        return simpleSum / calSet.Count * 0.5;
    }

    // ── Temperature scaling (grid search T ∈ [0.5, 5.0], 0.01 steps) ────────

    /// <summary>
    /// #7: Finer grid search (0.01 steps vs. legacy 0.1 steps) for optimal NLL.
    /// </summary>
    private static double FitTemperatureScaling(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        double bestT = 1.0, bestLoss = double.MaxValue;

        for (int ti = 50; ti <= 500; ti++)
        {
            double T = ti / 100.0, loss = 0;
            foreach (var s in calSet)
            {
                double raw = PredictRawProb(s.Features, allTrees, trainSet);
                raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
                double p = MLFeatureHelper.Sigmoid(MLFeatureHelper.Logit(raw) / T);
                int    y = s.Direction > 0 ? 1 : 0;
                loss -= y * Math.Log(p + 1e-15) + (1 - y) * Math.Log(1 - p + 1e-15);
            }
            loss /= calSet.Count;
            if (loss < bestLoss) { bestLoss = loss; bestT = T; }
        }
        return bestT;
    }

    // ── Magnitude linear regressor (Adam + Huber + cosine LR + early stop) ────

    private static (double[] Weights, double Bias) FitLinearRegressor(
        List<TrainingSample> train,
        int                  F,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var    w = new double[F];
        double b = 0.0;

        bool canEarlyStop = train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;

        if (trainSlice.Count == 0) return (w, b);

        var    mW     = new double[F];
        var    vW     = new double[F];
        double mB     = 0.0, vB = 0.0;
        double beta1t = 1.0, beta2t = 1.0;
        int    t      = 0;

        double bestValLoss = double.MaxValue;
        var    bestW       = new double[F];
        double bestB       = 0.0;
        int    patience    = 0;

        int    epochs     = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        double baseLr     = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2         = hp.L2Lambda;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lrCosine = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            foreach (var s in trainSlice)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;

                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;

                double huberGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);
                double bc1       = 1.0 - beta1t;
                double bc2       = 1.0 - beta2t;
                double alphAt    = lrCosine * Math.Sqrt(bc2) / bc1;

                mB  = AdamBeta1 * mB  + (1.0 - AdamBeta1) * huberGrad;
                vB  = AdamBeta2 * vB  + (1.0 - AdamBeta2) * huberGrad * huberGrad;
                b  -= alphAt * mB / (Math.Sqrt(vB) + AdamEpsilon);

                for (int j = 0; j < F; j++)
                {
                    double g = huberGrad * s.Features[j] + l2 * w[j];
                    mW[j]   = AdamBeta1 * mW[j] + (1.0 - AdamBeta1) * g;
                    vW[j]   = AdamBeta2 * vW[j] + (1.0 - AdamBeta2) * g * g;
                    w[j]   -= alphAt * mW[j] / (Math.Sqrt(vW[j]) + AdamEpsilon);
                }
            }

            if (!canEarlyStop) continue;

            double valLoss = 0.0;
            foreach (var s in valSlice)
            {
                double pred = b;
                for (int j = 0; j < F; j++) pred += w[j] * s.Features[j];
                double err = pred - s.Magnitude;
                valLoss += Math.Abs(err) <= 1.0 ? 0.5 * err * err : Math.Abs(err) - 0.5;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestValLoss)
            {
                bestValLoss = valLoss;
                Array.Copy(w, bestW, F);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= esPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ── 2-layer MLP magnitude regressor (ReLU hidden, Adam + Huber, cosine LR) ─

    /// <summary>
    /// Trains a 2-layer MLP magnitude regressor: input → ReLU(W1·x + b1) → W2·h + b2.
    /// Uses Adam with cosine-annealing LR, Huber loss, L2 regularisation, and early stopping.
    /// He initialisation for ReLU hidden weights. Trained on the same train/val split as the
    /// linear regressor for fair early-stopping comparison.
    /// Returns (W1[H×F], b1[H], W2[H], b2) where H = hiddenDim.
    /// </summary>
    private static (double[] W1, double[] B1, double[] W2, double B2) FitMlpMagnitudeRegressor(
        List<TrainingSample> train,
        int                  F,
        int                  H,
        TrainingHyperparams  hp,
        CancellationToken    ct = default)
    {
        var zeroW1 = new double[H * F];
        var zeroB1 = new double[H];
        var zeroW2 = new double[H];
        if (train.Count == 0) return (zeroW1, zeroB1, zeroW2, 0.0);

        // He initialisation for ReLU hidden layer
        var rng    = hp.QrfSeed != 0 ? new Random(hp.QrfSeed + 997) : new Random();
        double sw1 = Math.Sqrt(2.0 / F);
        double sw2 = Math.Sqrt(2.0 / H);

        var    W1 = new double[H * F];
        var    b1 = new double[H];
        var    W2 = new double[H];
        double b2 = 0.0;

        for (int i = 0; i < W1.Length; i++) W1[i] = (rng.NextDouble() * 2 - 1) * sw1;
        for (int h = 0; h < H; h++)         W2[h] = (rng.NextDouble() * 2 - 1) * sw2;

        // Adam moment buffers
        var    mW1 = new double[H * F]; var vW1 = new double[H * F];
        var    mB1 = new double[H];     var vB1 = new double[H];
        var    mW2 = new double[H];     var vW2 = new double[H];
        double mB2 = 0, vB2 = 0;

        bool canEarlyStop = train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;
        if (trainSlice.Count == 0) return (zeroW1, zeroB1, zeroW2, 0.0);

        int    epochs    = hp.MaxEpochs > 0 ? hp.MaxEpochs : 100;
        double baseLr    = hp.LearningRate > 0 ? hp.LearningRate : 0.01;
        double l2        = hp.L2Lambda;
        int    esPatience = Math.Max(5, hp.EarlyStoppingPatience / 2);

        double bestVal = double.MaxValue;
        var    bW1 = (double[])W1.Clone(); var bB1 = (double[])b1.Clone();
        var    bW2 = (double[])W2.Clone(); double bB2 = b2;
        int    patience = 0, t = 0;
        double beta1t = 1.0, beta2t = 1.0;

        var hidden = new double[H];
        var dW1    = new double[H * F];
        var dB1    = new double[H];
        var dW2    = new double[H];

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            ct.ThrowIfCancellationRequested();

            double lrCos = baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * epoch / epochs));

            foreach (var s in trainSlice)
            {
                t++;
                beta1t *= AdamBeta1;
                beta2t *= AdamBeta2;
                double bc1    = 1.0 - beta1t;
                double bc2    = 1.0 - beta2t;
                double alphAt = lrCos * Math.Sqrt(bc2) / bc1;

                // Forward
                for (int h = 0; h < H; h++)
                {
                    double z = b1[h];
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        z += W1[h * F + j] * s.Features[j];
                    hidden[h] = Math.Max(0.0, z); // ReLU
                }
                double pred = b2;
                for (int h = 0; h < H; h++) pred += W2[h] * hidden[h];

                double err = pred - s.Magnitude;
                if (!double.IsFinite(err)) continue;
                double hGrad = Math.Abs(err) <= 1.0 ? err : Math.Sign(err);

                // Backward — output layer
                Array.Clear(dW1, 0, dW1.Length);
                Array.Clear(dB1, 0, H);
                for (int h = 0; h < H; h++) dW2[h] = hGrad * hidden[h];
                double dB2local = hGrad;

                // Backward — hidden layer (ReLU derivative)
                for (int h = 0; h < H; h++)
                {
                    double dZ = hGrad * W2[h] * (hidden[h] > 0 ? 1.0 : 0.0);
                    dB1[h] = dZ;
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        dW1[h * F + j] = dZ * s.Features[j];
                }

                // Adam updates
                for (int i = 0; i < H * F; i++)
                {
                    double g = dW1[i] + l2 * W1[i];
                    mW1[i] = AdamBeta1 * mW1[i] + (1 - AdamBeta1) * g;
                    vW1[i] = AdamBeta2 * vW1[i] + (1 - AdamBeta2) * g * g;
                    W1[i] -= alphAt * mW1[i] / (Math.Sqrt(vW1[i]) + AdamEpsilon);
                }
                for (int h = 0; h < H; h++)
                {
                    mB1[h] = AdamBeta1 * mB1[h] + (1 - AdamBeta1) * dB1[h];
                    vB1[h] = AdamBeta2 * vB1[h] + (1 - AdamBeta2) * dB1[h] * dB1[h];
                    b1[h] -= alphAt * mB1[h] / (Math.Sqrt(vB1[h]) + AdamEpsilon);
                }
                for (int h = 0; h < H; h++)
                {
                    double g = dW2[h] + l2 * W2[h];
                    mW2[h] = AdamBeta1 * mW2[h] + (1 - AdamBeta1) * g;
                    vW2[h] = AdamBeta2 * vW2[h] + (1 - AdamBeta2) * g * g;
                    W2[h] -= alphAt * mW2[h] / (Math.Sqrt(vW2[h]) + AdamEpsilon);
                }
                mB2 = AdamBeta1 * mB2 + (1 - AdamBeta1) * dB2local;
                vB2 = AdamBeta2 * vB2 + (1 - AdamBeta2) * dB2local * dB2local;
                b2 -= alphAt * mB2 / (Math.Sqrt(vB2) + AdamEpsilon);
            }

            if (!canEarlyStop) continue;

            // #42: Reuse a single hidden-activation buffer across validation samples
            double valLoss = 0.0;
            var valHidden = new double[H];
            foreach (var s in valSlice)
            {
                for (int h = 0; h < H; h++)
                {
                    double z = b1[h];
                    for (int j = 0; j < F && j < s.Features.Length; j++)
                        z += W1[h * F + j] * s.Features[j];
                    valHidden[h] = Math.Max(0.0, z);
                }
                double p2 = b2;
                for (int h = 0; h < H; h++) p2 += W2[h] * valHidden[h];
                double e = p2 - s.Magnitude;
                valLoss += Math.Abs(e) <= 1.0 ? 0.5 * e * e : Math.Abs(e) - 0.5;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestVal)
            {
                bestVal = valLoss;
                Array.Copy(W1, bW1, W1.Length);
                Array.Copy(b1, bB1, H);
                Array.Copy(W2, bW2, H);
                bB2     = b2;
                patience = 0;
            }
            else if (++patience >= esPatience)
                break;
        }

        if (canEarlyStop) { W1 = bW1; b1 = bB1; W2 = bW2; b2 = bB2; }
        return (W1, b1, W2, b2);
    }

    // ── Quantile magnitude regressor (pinball loss, SGD) ─────────────────────

    /// <summary>
    /// #40: Quantile regressor with early stopping on a validation split.
    /// #41: L2 regularisation via <paramref name="l2"/>.
    /// </summary>
    private static (double[] Weights, double Bias) FitQuantileRegressor(
        List<TrainingSample> train, int F, double tau,
        double l2 = 0.0, int earlyStopPatience = 0)
    {
        var    w  = new double[F];
        double b  = 0;
        const double sgdLr = 0.001;
        const int    epochs = 100;

        bool canEarlyStop = earlyStopPatience > 0 && train.Count >= 30;
        int  valSize      = canEarlyStop ? Math.Max(5, train.Count / 10) : 0;
        var  valSlice     = canEarlyStop ? train[^valSize..] : train;
        var  trainSlice   = canEarlyStop ? train[..^valSize] : train;

        var    bestW = new double[F];
        double bestB = 0, bestVal = double.MaxValue;
        int    patience = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            foreach (var s in trainSlice)
            {
                double pred = b;
                for (int j = 0; j < F && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err  = s.Magnitude - pred;
                double grad = err >= 0 ? tau : -(1 - tau);
                b += sgdLr * grad;
                for (int j = 0; j < F && j < s.Features.Length; j++)
                    w[j] += sgdLr * (grad * s.Features[j] - l2 * w[j]);
            }

            if (!canEarlyStop) continue;

            double valLoss = 0;
            foreach (var s in valSlice)
            {
                double pred = b;
                for (int j = 0; j < F && j < s.Features.Length; j++) pred += w[j] * s.Features[j];
                double err = s.Magnitude - pred;
                valLoss += err >= 0 ? tau * err : (tau - 1) * err;
            }
            valLoss /= valSlice.Count;

            if (valLoss < bestVal)
            {
                bestVal = valLoss;
                Array.Copy(w, bestW, F);
                bestB    = b;
                patience = 0;
            }
            else if (++patience >= earlyStopPatience)
                break;
        }

        if (canEarlyStop) { w = bestW; b = bestB; }
        return (w, b);
    }

    // ── Permutation feature importance on test set (Fisher-Yates, seed 42) ────

    /// <summary>
    /// #28: Multi-shuffle permutation importance. Averages across <paramref name="repeats"/>
    /// independent shuffles to reduce variance in importance estimates.
    /// </summary>
    private static float[] ComputePermutationImportance(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        int                  F,
        int                  seed = 42,
        int                  repeats = DefaultPermutationRepeats)
    {
        int n = testSet.Count;
        repeats = Math.Max(1, repeats);

        int baseCorrect = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            if ((p >= 0.5 ? 1 : 0) == (s.Direction > 0 ? 1 : 0)) baseCorrect++;
        }
        double baseAcc = (double)baseCorrect / n;

        var importance = new float[F];
        var shuffled   = new float[n];
        var featBuf    = new float[F];

        for (int fi = 0; fi < F; fi++)
        {
            double dropSum = 0;
            for (int rep = 0; rep < repeats; rep++)
            {
                var rng = seed != 0 ? new Random(seed + fi * repeats + rep + 1) : new Random();
                for (int i = 0; i < n; i++) shuffled[i] = testSet[i].Features[fi];
                for (int i = n - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }

                int correct = 0;
                for (int i = 0; i < n; i++)
                {
                    var orig = testSet[i].Features;
                    int fLen = Math.Min(orig.Length, F);
                    for (int j = 0; j < fLen; j++) featBuf[j] = orig[j];
                    featBuf[fi] = shuffled[i];
                    double p = PredictProb(featBuf, allTrees, trainSet, plattA, plattB, isotonicBp);
                    if ((p >= 0.5 ? 1 : 0) == (testSet[i].Direction > 0 ? 1 : 0)) correct++;
                }
                dropSum += Math.Max(0.0, baseAcc - (double)correct / n);
            }
            importance[fi] = (float)(dropSum / repeats);
        }

        double total = 0;
        foreach (var v in importance) total += v;
        if (total > 0)
            for (int i = 0; i < importance.Length; i++) importance[i] = (float)(importance[i] / total);

        return importance;
    }

    // ── Calibration-set permutation importance (parallel, for warm-start) ─────

    private static double[] ComputeCalPermutationImportance(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  F,
        CancellationToken    ct)
    {
        int n = calSet.Count;

        int baseCorrect = 0;
        foreach (var s in calSet)
            if ((PredictRawProb(s.Features, allTrees, trainSet) >= 0.5) == (s.Direction > 0))
                baseCorrect++;
        double baseAcc = (double)baseCorrect / n;

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

            var scratch = new float[calSet[0].Features.Length];
            int correct = 0;
            for (int idx = 0; idx < n; idx++)
            {
                Array.Copy(calSet[idx].Features, scratch, scratch.Length);
                scratch[j] = vals[idx];
                if ((PredictRawProb(scratch, allTrees, trainSet) >= 0.5) == (calSet[idx].Direction > 0))
                    correct++;
            }
            importance[j] = Math.Max(0, baseAcc - (double)correct / n);
        });
        return importance;
    }

    // ── Split-conformal q̂ ─────────────────────────────────────────────────────

    private static double ComputeConformalQHat(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp,
        double               alpha = 0.10)
    {
        if (calSet.Count < 20) return 0.5;

        var scores = new List<double>(calSet.Count);
        foreach (var s in calSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            scores.Add(s.Direction > 0 ? 1.0 - p : p);
        }
        scores.Sort();

        int n    = scores.Count;
        int qIdx = Math.Clamp((int)Math.Ceiling((n + 1) * (1.0 - alpha)) - 1, 0, n - 1);
        return scores[qIdx];
    }

    // ── Full evaluation on held-out test set ──────────────────────────────────

    private static EvalMetrics EvaluateModel(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double[]             magWeights,
        double               magBias,
        double               plattA,
        double               plattB,
        double[]             isotonicBp)
    {
        if (testSet.Count == 0)
            return new EvalMetrics(0, 0, 0, 0, double.MaxValue, 0, 1, 0, 0, 0, 0, 0, 0);

        int    correct = 0, tp = 0, fp = 0, fn = 0, tn = 0;
        double brierSum = 0, evSum = 0, magSse = 0, retSumSq = 0;

        foreach (var s in testSet)
        {
            double p    = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    yHat = p >= 0.5 ? 1 : 0;
            int    y    = s.Direction > 0 ? 1 : 0;

            if (yHat == y) correct++;
            if (yHat == 1 && y == 1) tp++;
            if (yHat == 1 && y == 0) fp++;
            if (yHat == 0 && y == 1) fn++;
            if (yHat == 0 && y == 0) tn++;
            brierSum += (p - y) * (p - y);
            double r  = (yHat == y ? 1 : -1) * (double)s.Magnitude;
            evSum    += r;
            retSumSq += r * r;

            double magPred = magBias;
            for (int j = 0; j < Math.Min(magWeights.Length, s.Features.Length); j++)
                magPred += magWeights[j] * s.Features[j];
            double magErr = magPred - s.Magnitude;
            magSse += magErr * magErr;
        }

        int    n         = testSet.Count;
        double accuracy  = (double)correct / n;
        double brier     = brierSum / n;
        double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        double recall    = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        double f1        = (precision + recall) > 0 ? 2 * precision * recall / (precision + recall) : 0;
        double ev        = evSum / n;
        double magRmse   = Math.Sqrt(magSse / n);

        // Magnitude-weighted Sharpe: mean(r) / std(r) × √252, where r = ±Magnitude per trade.
        // Replaces the (accuracy−0.5)/(brier+0.01) proxy which does not reflect risk-adjusted returns.
        double retMean = ev;
        double retVar  = retSumSq / n - retMean * retMean;
        double retStd  = retVar > Eps ? Math.Sqrt(retVar) : 0.0;
        double sharpe  = retStd > Eps ? retMean / retStd * Math.Sqrt(252) : 0.0;

        return new EvalMetrics(
            Accuracy:         accuracy,
            Precision:        precision,
            Recall:           recall,
            F1:               f1,
            MagnitudeRmse:    magRmse,
            ExpectedValue:    ev,
            BrierScore:       brier,
            WeightedAccuracy: accuracy,
            SharpeRatio:      sharpe,
            TP: tp, FP: fp, FN: fn, TN: tn);
    }

    // ── Brier Skill Score vs. naïve base-rate predictor ───────────────────────

    private static double ComputeBrierSkillScore(
        List<TrainingSample> testSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             isotonicBp)
    {
        if (testSet.Count == 0) return 0;

        int posCount = 0;
        foreach (var s in testSet) if (s.Direction > 0) posCount++;
        double pBase = (double)posCount / testSet.Count;

        double brierModel = 0, brierNaive = 0;
        foreach (var s in testSet)
        {
            double p = PredictProb(s.Features, allTrees, trainSet, plattA, plattB, isotonicBp);
            int    y = s.Direction > 0 ? 1 : 0;
            brierModel += (p - y) * (p - y);
            brierNaive += (pBase - y) * (pBase - y);
        }
        brierModel /= testSet.Count;
        brierNaive /= testSet.Count;
        return brierNaive > 1e-15 ? 1.0 - brierModel / brierNaive : 0;
    }

    // ── OOB accuracy (RF-native, uses OOB leaf fractions per new tree) ─────────

    private static double ComputeOobAccuracy(
        List<TrainingSample>  trainSet,
        List<List<TreeNode>>  newTrees,
        List<HashSet<int>>    oobMasks)
    {
        if (trainSet.Count < 10 || newTrees.Count < 2 || oobMasks.Count != newTrees.Count) return 0;

        int correct = 0, evaluated = 0;
        for (int i = 0; i < trainSet.Count; i++)
        {
            double probSum  = 0.0;
            int    oobCount = 0;
            for (int t = 0; t < newTrees.Count; t++)
            {
                if (!oobMasks[t].Contains(i)) continue;
                probSum += GetLeafProb(newTrees[t], 0, trainSet[i].Features);
                oobCount++;
            }

            if (oobCount == 0) continue;

            double oobProb = probSum / oobCount;
            if ((oobProb >= 0.5) == (trainSet[i].Direction > 0)) correct++;
            evaluated++;
        }

        return evaluated > 0 ? (double)correct / evaluated : 0;
    }

    // ── Equity curve stats (max drawdown + Sharpe on simulated ±1% returns) ───

    private static (double MaxDrawdown, double Sharpe) ComputeEquityCurveStats(
        (int Predicted, int Actual)[] predictions)
    {
        if (predictions.Length == 0) return (0, 0);

        var    returns = new double[predictions.Length];
        double equity  = 1.0, peak = 1.0, maxDD = 0;

        for (int i = 0; i < predictions.Length; i++)
        {
            double r = predictions[i].Predicted == predictions[i].Actual ? 0.01 : -0.01;
            returns[i] = r;
            equity    += r;
            if (equity > peak) peak = equity;
            double dd = peak > 0 ? (peak - equity) / peak : 0;
            if (dd > maxDD) maxDD = dd;
        }

        double mean = returns.Average();
        double varSum = 0;
        foreach (double r in returns) varSum += (r - mean) * (r - mean);
        double std    = returns.Length > 1 ? Math.Sqrt(varSum / (returns.Length - 1)) : 0;
        double sharpe = std > 1e-10 ? mean / std * Math.Sqrt(252) : 0;

        return (maxDD, sharpe);
    }

    // ── Sharpe trend (OLS slope across CV folds) ──────────────────────────────

    private static double ComputeSharpeTrend(List<double> sharpePerFold)
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

    // ── NaN/Inf node sanitization ─────────────────────────────────────────────

    private static int SanitizeTreeNodes(List<List<TreeNode>> allTrees)
    {
        int count = 0;
        foreach (var treeNodes in allTrees)
        {
            foreach (var node in treeNodes)
            {
                if (!double.IsFinite(node.LeafDirection))
                {
                    node.LeafDirection = 0.5;
                    count++;
                }
                if (!double.IsFinite(node.SplitThresh))
                {
                    // Convert to leaf — a split with a non-finite threshold is unusable
                    node.SplitFeat   = -1;
                    node.SplitThresh = 0.0;
                    count++;
                }
            }
        }
        return count;
    }

    // ── Stationarity gate (lag-1 Pearson correlation as ADF proxy) ────────────

    /// <summary>
    /// #12: Improved stationarity test using a sample-size-aware critical value.
    /// For N &gt; 100, the threshold is relaxed slightly (0.97 − 0.5/√N) to account for
    /// the fact that lag-1 correlation estimates are biased upward in small samples.
    /// </summary>
    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int n = samples.Count;
        if (n < 3) return 0;

        // Sample-size-aware critical value for lag-1 correlation
        double criticalRho = StationarityRhoThreshold - (n > 100 ? 0.5 / Math.Sqrt(n) : 0.0);

        int nonStat = 0;
        for (int fi = 0; fi < F; fi++)
        {
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            int    nc   = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = samples[i].Features[fi];
                double y = samples[i + 1].Features[fi];
                sumX  += x; sumY  += y;
                sumXY += x * y;
                sumX2 += x * x; sumY2 += y * y;
            }
            double varX  = sumX2 - sumX * sumX / nc;
            double varY  = sumY2 - sumY * sumY / nc;
            double denom = Math.Sqrt(Math.Max(0, varX * varY));
            double rho   = denom > 1e-12 ? (sumXY - sumX * sumY / nc) / denom : 0;
            if (Math.Abs(rho) > criticalRho) nonStat++;
        }
        return nonStat;
    }

    // ── Durbin-Watson test on magnitude residuals ─────────────────────────────

    private static double ComputeDurbinWatson(
        List<TrainingSample> train, double[] magWeights, double magBias, int F)
    {
        if (train.Count < 10 || magWeights.Length == 0) return 2.0;

        var residuals = new double[train.Count];
        for (int i = 0; i < train.Count; i++)
        {
            double pred = magBias;
            for (int j = 0; j < Math.Min(F, train[i].Features.Length); j++)
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

    // ── Density-ratio importance weights (logistic discriminator) ─────────────

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int F, int windowDays)
    {
        int recentCount = Math.Min(trainSet.Count / 3, windowDays * 24);
        if (recentCount < 20) return [.. Enumerable.Repeat(1.0 / trainSet.Count, trainSet.Count)];

        int cutoff = trainSet.Count - recentCount;
        var w      = new double[F];
        double b   = 0;

        // Logistic discriminator: recent = 1, historical = 0
        for (int epoch = 0; epoch < DensityRatioEpochs; epoch++)
        {
            for (int i = 0; i < trainSet.Count; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                double z     = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    z += w[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - label;
                b -= DensityRatioLr * err;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    w[j] -= DensityRatioLr * err * trainSet[i].Features[j];
            }
        }

        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double z = b;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                z += w[j] * trainSet[i].Features[j];
            double p   = Math.Clamp(MLFeatureHelper.Sigmoid(z), 0.01, 0.99);
            weights[i] = p / (1 - p); // importance ratio
        }

        double wSum = weights.Sum();
        if (wSum > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= wSum;

        return weights;
    }

    // ── Covariate shift weights (parent model novelty scoring) ────────────────

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBp, int F)
    {
        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            int outsideCount = 0, checkedCount = 0;
            for (int j = 0; j < F && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                if (v < bp[0] || v > bp[^1]) outsideCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0;
            weights[i] = 1.0 + noveltyFraction; // up-weight novel samples
        }

        double mean = weights.Average();
        if (mean > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= mean;

        return weights;
    }

    // ── Mutual information feature redundancy (histogram MI, Sturges' rule) ───

    private static string[] ComputeRedundantFeaturePairs(
        List<TrainingSample> trainSet, int F, double threshold)
    {
        if (trainSet.Count < 30) return [];

        // #24: Random subsample (seeded) instead of taking the first N samples
        int maxN = Math.Min(trainSet.Count, MaxMiDefaultSamples);
        List<TrainingSample> miSamples;
        if (trainSet.Count > maxN)
        {
            var miRng = new Random(42);
            var indices = Enumerable.Range(0, trainSet.Count).OrderBy(_ => miRng.Next()).Take(maxN).ToList();
            miSamples = [.. indices.Select(i => trainSet[i])];
        }
        else
        {
            miSamples = trainSet;
        }
        int n = miSamples.Count;

        // #25: Freedman-Diaconis bin count — adapts to actual data distribution.
        // Compute IQR of the first feature as representative, fall back to Sturges.
        int numBins;
        {
            var f0 = new double[n];
            for (int i = 0; i < n; i++) f0[i] = miSamples[i].Features.Length > 0 ? miSamples[i].Features[0] : 0;
            Array.Sort(f0);
            double q1 = f0[n / 4], q3 = f0[3 * n / 4];
            double iqr = q3 - q1;
            double range = f0[^1] - f0[0];
            if (iqr > 1e-12 && range > 1e-12)
            {
                double binWidth = 2.0 * iqr / Math.Cbrt(n);
                numBins = Math.Clamp((int)Math.Ceiling(range / binWidth), 5, 50);
            }
            else
            {
                numBins = Math.Max(5, (int)Math.Ceiling(1 + Math.Log2(n)));
            }
        }

        var featureMin    = new double[F];
        var featureMax    = new double[F];
        var featureBinIdx = new int[F * n];

        Array.Fill(featureMin, double.MaxValue);
        Array.Fill(featureMax, double.MinValue);

        for (int j = 0; j < F; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double v = miSamples[i].Features[j];
                if (v < featureMin[j]) featureMin[j] = v;
                if (v > featureMax[j]) featureMax[j] = v;
            }
            double range    = featureMax[j] - featureMin[j];
            double binWidth = range > 1e-15 ? range / numBins : 1.0;
            for (int i = 0; i < n; i++)
            {
                int bin = (int)((miSamples[i].Features[j] - featureMin[j]) / binWidth);
                featureBinIdx[j * n + i] = Math.Clamp(bin, 0, numBins - 1);
            }
        }

        var pairs  = new List<string>();
        double invN = 1.0 / n;

        for (int a = 0; a < F; a++)
        {
            for (int bj = a + 1; bj < F; bj++)
            {
                var joint = new int[numBins * numBins];
                var margA = new int[numBins];
                var margB = new int[numBins];

                for (int i = 0; i < n; i++)
                {
                    int ba = featureBinIdx[a  * n + i];
                    int bb = featureBinIdx[bj * n + i];
                    joint[ba * numBins + bb]++;
                    margA[ba]++;
                    margB[bb]++;
                }

                double mi = 0;
                for (int ia = 0; ia < numBins; ia++)
                {
                    if (margA[ia] == 0) continue;
                    double pA = margA[ia] * invN;
                    for (int ib = 0; ib < numBins; ib++)
                    {
                        int jCount = joint[ia * numBins + ib];
                        if (jCount == 0 || margB[ib] == 0) continue;
                        double pJ = jCount * invN;
                        double pB = margB[ib] * invN;
                        mi += pJ * Math.Log(pJ / (pA * pB));
                    }
                }

                if (mi > threshold * Math.Log(2))
                {
                    string nameA = a  < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[a]  : $"F{a}";
                    string nameB = bj < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[bj] : $"F{bj}";
                    pairs.Add($"{nameA}:{nameB}");
                }
            }
        }
        return [.. pairs];
    }

    // ── Feature mask + apply ──────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        if (threshold <= 0.0 || F == 0)
        {
            var all = new bool[F];
            Array.Fill(all, true);
            return all;
        }
        double minImportance = threshold / F;
        var mask = new bool[F];
        for (int j = 0; j < F; j++)
            mask[j] = importance[j] >= minImportance;
        return mask;
    }

    /// <summary>
    /// #34: Lightweight mask — skips clone when mask is all-true (no features pruned).
    /// When features are pruned, zeroes masked features in a cloned array.
    /// </summary>
    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        // Fast path: no-op when all features are active
        bool allActive = true;
        foreach (bool m in mask) if (!m) { allActive = false; break; }
        if (allActive) return samples;

        return [.. samples.Select(s =>
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            return s with { Features = f };
        })];
    }

    // ── Weighted bootstrap sampling ───────────────────────────────────────────

    private static int SampleWeighted(Random rng, double[] cumWeights)
    {
        double r   = rng.NextDouble() * cumWeights[^1];
        int    idx = Array.BinarySearch(cumWeights, r);
        return idx < 0 ? Math.Min(~idx, cumWeights.Length - 1) : idx;
    }

    // ── Standard deviation helper ─────────────────────────────────────────────

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sumSq = 0;
        foreach (double v in values) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    // ── Per-tree probability helpers (#2, #5, #7, #10) ───────────────────────

    /// <summary>Returns an array of T per-tree leaf-fraction probabilities for <paramref name="features"/>.</summary>
    private static double[] GetTreeProbs(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)   // trainSet retained for call-site compatibility
    {
        int T     = allTrees.Count;
        var probs = new double[T];
        for (int t = 0; t < T; t++)
            probs[t] = GetLeafProb(allTrees[t], 0, features);
        return probs;
    }

    // ── Stacking meta-learner over per-tree probs (#2) ────────────────────────

    /// <summary>
    /// #20: Meta-learner with Adam optimizer (replaces vanilla SGD) for faster and
    /// more stable convergence. Fits T-weight logistic regression over per-tree
    /// leaf-fraction probabilities. Uniform initialisation (1/T).
    /// </summary>
    private static (double[] MetaWeights, double MetaBias) FitMetaLearner(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        int T = allTrees.Count;
        if (calSet.Count < MinCalSamplesPlatt || T < 2) return (new double[T], 0.0);

        int n = calSet.Count;
        var calLP     = new double[n][];
        var calLabels = new double[n];
        for (int i = 0; i < n; i++)
        {
            calLP[i]     = GetTreeProbs(calSet[i].Features, allTrees, trainSet);
            calLabels[i] = calSet[i].Direction > 0 ? 1.0 : 0.0;
        }

        var mw = new double[T];
        for (int t = 0; t < T; t++) mw[t] = 1.0 / T;
        double mb = 0.0;

        // Adam moment buffers
        var    mMw = new double[T]; var vMw = new double[T];
        double mMb = 0, vMb = 0;
        double beta1t = 1.0, beta2t = 1.0;
        int    step = 0;

        const double Lr     = 0.01;
        const int    Epochs = 300;

        var dW = new double[T];
        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            Array.Clear(dW, 0, T);
            double dB = 0;
            for (int i = 0; i < n; i++)
            {
                var    lp  = calLP[i];
                double z   = mb;
                for (int t = 0; t < T; t++) z += mw[t] * lp[t];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - calLabels[i];
                for (int t = 0; t < T; t++) dW[t] += err * lp[t];
                dB += err;
            }

            step++;
            beta1t *= AdamBeta1;
            beta2t *= AdamBeta2;
            double bc1    = 1.0 - beta1t;
            double bc2    = 1.0 - beta2t;
            double alphAt = Lr * Math.Sqrt(bc2) / bc1;

            for (int t = 0; t < T; t++)
            {
                double g = dW[t] / n;
                mMw[t] = AdamBeta1 * mMw[t] + (1 - AdamBeta1) * g;
                vMw[t] = AdamBeta2 * vMw[t] + (1 - AdamBeta2) * g * g;
                mw[t] -= alphAt * mMw[t] / (Math.Sqrt(vMw[t]) + AdamEpsilon);
            }
            {
                double g = dB / n;
                mMb = AdamBeta1 * mMb + (1 - AdamBeta1) * g;
                vMb = AdamBeta2 * vMb + (1 - AdamBeta2) * g * g;
                mb -= alphAt * mMb / (Math.Sqrt(vMb) + AdamEpsilon);
            }
        }

        return (mw, mb);
    }

    // ── Greedy Ensemble Selection over trees (#3) ─────────────────────────────

    /// <summary>
    /// Forward greedy selection (Caruana et al. 2004) that minimises NLL on the cal set.
    /// Pre-computes allLP[n][T] once, then picks the tree that most reduces NLL each round.
    /// Returns normalised usage frequencies (sum = 1) for all T trees.
    /// Returns an empty array when the cal set is too small.
    /// </summary>
    /// <summary>
    /// #19: GES with configurable rounds and early stopping when NLL stops improving.
    /// #38: Returns tree selection counts for logging.
    /// </summary>
    private static double[] RunGreedyTreeSelection(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  rounds = DefaultGesRounds,
        int                  earlyStopPatience = 0)
    {
        int T = allTrees.Count;
        if (calSet.Count < MinCalSamples || T < 2) return [];

        int gesN  = calSet.Count;
        var allLP = new double[gesN][];
        for (int i = 0; i < gesN; i++)
            allLP[i] = GetTreeProbs(calSet[i].Features, allTrees, trainSet);

        var counts   = new int[T];
        var ensProbs = new double[gesN];
        int ensSize  = 0;
        double prevBestLoss = double.MaxValue;
        int    noImproveCnt = 0;

        for (int round = 0; round < rounds; round++)
        {
            int    bestT    = -1;
            double bestLoss = double.MaxValue;

            for (int t = 0; t < T; t++)
            {
                double loss = 0.0;
                int    n1   = ensSize + 1;
                for (int i = 0; i < gesN; i++)
                {
                    double avg = (ensProbs[i] * ensSize + allLP[i][t]) / n1;
                    double y   = calSet[i].Direction > 0 ? 1.0 : 0.0;
                    loss -= y * Math.Log(avg + 1e-15) + (1 - y) * Math.Log(1 - avg + 1e-15);
                }
                if (loss < bestLoss) { bestLoss = loss; bestT = t; }
            }

            if (bestT < 0) break;
            counts[bestT]++;
            ensSize++;
            for (int i = 0; i < gesN; i++)
                ensProbs[i] = (ensProbs[i] * (ensSize - 1) + allLP[i][bestT]) / ensSize;

            // #19: Early stop if NLL hasn't improved
            if (earlyStopPatience > 0)
            {
                if (bestLoss < prevBestLoss - 1e-8)
                {
                    prevBestLoss = bestLoss;
                    noImproveCnt = 0;
                }
                else if (++noImproveCnt >= earlyStopPatience)
                    break;
            }
        }

        double total = counts.Sum();
        if (total < 1e-10) return new double[T];
        return counts.Select(c => c / total).ToArray();
    }

    // ── OOB-contribution tree pruning (#4) ────────────────────────────────────

    /// <summary>
    /// For each newly built tree, measures whether removing it from the OOB ensemble
    /// improves OOB accuracy. Trees whose removal is beneficial are discarded from
    /// <paramref name="newTrees"/> and <paramref name="oobMasks"/>.
    /// Uses a skip-index approach to avoid list mutations during evaluation.
    /// Returns the count of pruned trees.
    /// </summary>
    private static int PruneByOobContribution(
        List<TrainingSample> trainSet,
        List<List<TreeNode>> newTrees,
        List<HashSet<int>>   oobMasks)
    {
        if (trainSet.Count < 20 || newTrees.Count < 2 || oobMasks.Count != newTrees.Count) return 0;

        static double ComputeOobAcc(
            List<TrainingSample> ts,
            List<List<TreeNode>> trees,
            List<HashSet<int>>   masks,
            int                  skipIdx = -1)
        {
            int correct = 0, evaluated = 0;
            for (int i = 0; i < ts.Count; i++)
            {
                double probSum  = 0.0;
                int    oobCount = 0;
                for (int t = 0; t < trees.Count; t++)
                {
                    if (t == skipIdx || !masks[t].Contains(i)) continue;
                    probSum += GetLeafProb(trees[t], 0, ts[i].Features);
                    oobCount++;
                }
                if (oobCount == 0) continue;
                if ((probSum / oobCount >= 0.5) == (ts[i].Direction > 0)) correct++;
                evaluated++;
            }
            return evaluated > 0 ? (double)correct / evaluated : 0.0;
        }

        // #10: Iterative re-evaluation — after removing one tree, recompute
        // baseline accuracy before evaluating the next candidate.
        int pruned = 0;
        bool changed = true;
        while (changed && newTrees.Count >= 2)
        {
            changed = false;
            double baseAcc = ComputeOobAcc(trainSet, newTrees, oobMasks);
            int    worstK  = -1;
            double bestAcc = baseAcc;

            for (int k = 0; k < newTrees.Count; k++)
            {
                if (newTrees.Count - 1 < 1) break;
                double accWithout = ComputeOobAcc(trainSet, newTrees, oobMasks, skipIdx: k);
                if (accWithout > bestAcc)
                {
                    bestAcc = accWithout;
                    worstK  = k;
                }
            }

            if (worstK >= 0)
            {
                newTrees.RemoveAt(worstK);
                oobMasks.RemoveAt(worstK);
                pruned++;
                changed = true;
            }
        }

        return pruned;
    }

    // ── Tree ensemble diversity (#5) ──────────────────────────────────────────

    /// <summary>
    /// Samples up to <paramref name="maxSamples"/> training points, computes per-tree
    /// leaf-fraction prediction vectors, and returns the average pairwise Pearson
    /// correlation (lower = more diverse).
    /// </summary>
    private static double ComputeTreeDiversity(
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  maxSamples = MaxDiversitySamples)
    {
        int T = allTrees.Count;
        if (T < 2 || trainSet.Count == 0) return 0.0;

        int sampleCount = Math.Min(maxSamples, trainSet.Count);
        // Deterministic even-spaced sample to avoid LINQ allocation
        var sampleIndices = new int[sampleCount];
        for (int s = 0; s < sampleCount; s++)
            sampleIndices[s] = (int)((long)s * trainSet.Count / sampleCount);

        var predictions = new double[T][];

        for (int t = 0; t < T; t++)
        {
            predictions[t] = new double[sampleCount];
            for (int s = 0; s < sampleCount; s++)
            {
                int    si = sampleIndices[s];
                double p  = GetLeafProb(allTrees[t], 0, trainSet[si].Features);
                predictions[t][s] = double.IsFinite(p) ? p : 0.5;
            }
        }

        // #31: For large T, sample a random subset of pairs instead of O(T²) full scan.
        int totalPairs = T * (T - 1) / 2;
        int maxPairs   = 500;
        double sumCorr = 0.0;
        int    pairs   = 0;

        if (totalPairs <= maxPairs)
        {
            for (int i = 0; i < T; i++)
                for (int j = i + 1; j < T; j++)
                {
                    sumCorr += PearsonCorrelation(predictions[i], predictions[j], sampleCount);
                    pairs++;
                }
        }
        else
        {
            var pairRng = new Random(42);
            for (int p = 0; p < maxPairs; p++)
            {
                int i = pairRng.Next(T);
                int j = pairRng.Next(T - 1);
                if (j >= i) j++;
                sumCorr += PearsonCorrelation(predictions[i], predictions[j], sampleCount);
                pairs++;
            }
        }

        return pairs > 0 ? sumCorr / pairs : 0.0;
    }

    /// <summary>
    /// Pearson correlation between the first <paramref name="len"/> elements of
    /// <paramref name="a"/> and <paramref name="b"/>. Returns 0.0 when either array
    /// has near-zero variance.
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

    // ── Meta-label secondary classifier (#10) ────────────────────────────────

    /// <summary>
    /// #23: Meta-label classifier using [rawProb, treeStd, top-5-by-importance features]
    /// instead of the first 5 by index. When <paramref name="featureImportance"/> is available,
    /// the 5 features with highest importance are selected; otherwise falls back to feat[0..4].
    /// </summary>
    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        float[]?             featureImportance = null)
    {
        const int    MetaFeatureDim = 7;   // rawProb + treeStd + 5 raw features
        const int    Epochs         = 30;
        const double Lr             = 0.01;
        const double L2             = 0.001;

        if (calSet.Count < MinCalSamples)
            return (new double[MetaFeatureDim], 0.0);

        // #23: Select top-5 feature indices by importance (or first 5 as fallback)
        int F = calSet.Count > 0 ? calSet[0].Features.Length : 0;
        int topN = Math.Min(5, F);
        int[] topFeatIdx;
        if (featureImportance is { Length: > 0 } && featureImportance.Length >= F)
        {
            topFeatIdx = Enumerable.Range(0, F)
                .OrderByDescending(i => featureImportance[i])
                .Take(topN)
                .ToArray();
        }
        else
        {
            topFeatIdx = Enumerable.Range(0, topN).ToArray();
        }

        int T     = allTrees.Count;
        var mw    = new double[MetaFeatureDim];
        double mb = 0.0;
        var dW    = new double[MetaFeatureDim];
        var metaF = new double[MetaFeatureDim];

        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            double dB = 0;
            Array.Clear(dW, 0, MetaFeatureDim);

            foreach (var s in calSet)
            {
                double[] tp       = GetTreeProbs(s.Features, allTrees, trainSet);
                double   rawProb  = tp.Average();
                double   variance = 0.0;
                for (int t = 0; t < tp.Length; t++) { double d = tp[t] - rawProb; variance += d * d; }
                double treeStd = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

                metaF[0] = rawProb;
                metaF[1] = treeStd;
                for (int i = 0; i < topN; i++)
                    metaF[2 + i] = topFeatIdx[i] < s.Features.Length ? s.Features[topFeatIdx[i]] : 0.0;

                int predicted = rawProb >= 0.5 ? 1 : -1;
                int actual    = s.Direction > 0 ? 1 : -1;
                double label  = predicted == actual ? 1.0 : 0.0;

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

    // ── Abstention gate (#6) ──────────────────────────────────────────────────

    /// <summary>
    /// Trains a 3-feature logistic gate on [calibP, treeStd, metaLabelScore].
    /// Label = 1 when the calibrated QRF prediction was correct on the calibration sample.
    /// 50 epochs SGD with L2 regularisation.
    /// Returns (weights, bias, threshold=0.5).
    /// </summary>
    private static (double[] Weights, double Bias, double Threshold) FitAbstentionGate(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]             metaLabelWeights,
        double               metaLabelBias,
        bool                 sweepThreshold = false)
    {
        const int    Dim    = 3;   // [calibP, treeStd, metaLabelScore]
        const int    Epochs = 50;
        const double Lr     = 0.01;
        const double L2     = 0.001;

        if (calSet.Count < 10)
            return (new double[Dim], 0.0, 0.5);

        int T  = allTrees.Count;
        var aw = new double[Dim];
        double ab = 0.0;

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
                double[] tp       = GetTreeProbs(s.Features, allTrees, trainSet);
                double   rawProb  = tp.Average();
                double   rawPC    = Math.Clamp(rawProb, 1e-7, 1.0 - 1e-7);
                double   calibP   = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawPC) + plattB);

                double variance = 0.0;
                for (int t = 0; t < tp.Length; t++) { double d = tp[t] - rawProb; variance += d * d; }
                double treeStd  = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

                mf[0] = rawProb; mf[1] = treeStd;
                int top = Math.Min(5, s.Features.Length);
                for (int i = 0; i < top; i++) mf[2 + i] = s.Features[i];
                double mz = metaLabelBias;
                for (int i = 0; i < MetaDim && i < metaLabelWeights.Length; i++)
                    mz += metaLabelWeights[i] * mf[i];
                double metaScore = MLFeatureHelper.Sigmoid(mz);

                af[0] = calibP; af[1] = treeStd; af[2] = metaScore;
                double lbl = (calibP >= 0.5) == (s.Direction > 0) ? 1.0 : 0.0;

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

        // #21: Learn optimal abstention threshold by sweeping cal set for best
        // precision at ≥50 % recall (when enabled)
        double threshold = 0.5;
        if (sweepThreshold)
        {
            double bestPrec = 0;
            for (int pct = 30; pct <= 70; pct++)
            {
                double thr = pct / 100.0;
                int tpA = 0, fpA = 0, fnA = 0;
                foreach (var s in calSet)
                {
                    double[] tp2 = GetTreeProbs(s.Features, allTrees, trainSet);
                    double rawP2 = tp2.Average();
                    double rawPC2 = Math.Clamp(rawP2, 1e-7, 1.0 - 1e-7);
                    double cP2 = MLFeatureHelper.Sigmoid(plattA * MLFeatureHelper.Logit(rawPC2) + plattB);
                    double lbl2 = (cP2 >= 0.5) == (s.Direction > 0) ? 1.0 : 0.0;

                    // Recompute gate features per sample for the threshold sweep
                    double vr2 = 0;
                    for (int t2 = 0; t2 < tp2.Length; t2++) { double d2 = tp2[t2] - rawP2; vr2 += d2 * d2; }
                    double ts2 = T > 1 ? Math.Sqrt(vr2 / (T - 1)) : 0.0;
                    double[] mf2 = [rawP2, ts2, 0, 0, 0, 0, 0];
                    int top2 = Math.Min(5, s.Features.Length);
                    for (int i2 = 0; i2 < top2; i2++) mf2[2 + i2] = s.Features[i2];
                    double mz2 = metaLabelBias;
                    for (int i2 = 0; i2 < MetaDim && i2 < metaLabelWeights.Length; i2++)
                        mz2 += metaLabelWeights[i2] * mf2[i2];
                    double ms2 = MLFeatureHelper.Sigmoid(mz2);

                    double[] af2 = [cP2, ts2, ms2];
                    double gz = ab;
                    for (int i2 = 0; i2 < Dim; i2++) gz += aw[i2] * af2[i2];
                    double gateP = MLFeatureHelper.Sigmoid(gz);
                    bool pass = gateP >= thr;
                    if (pass && lbl2 >= 0.5) tpA++;
                    else if (pass && lbl2 < 0.5) fpA++;
                    else if (!pass && lbl2 >= 0.5) fnA++;
                }
                double recall2 = (tpA + fnA) > 0 ? (double)tpA / (tpA + fnA) : 0;
                double prec2   = (tpA + fpA) > 0 ? (double)tpA / (tpA + fpA) : 0;
                if (recall2 >= 0.50 && prec2 > bestPrec)
                {
                    bestPrec = prec2;
                    threshold = thr;
                }
            }
        }

        return (aw, ab, threshold);
    }

    // ── Jackknife+ nonconformity residuals (#9) ───────────────────────────────

    /// <summary>
    /// Computes Jackknife+ nonconformity residuals for each training sample:
    /// r_i = |trueLabel − oobProb_i| where oobProb_i is the average leaf-fraction
    /// probability over trees for which sample i was out-of-bag.
    /// Returns residuals sorted in ascending order; empty when the training set is
    /// too small or no OOB membership exists.
    /// Only <paramref name="newTrees"/> have associated <paramref name="oobMasks"/>.
    /// </summary>
    private static double[] ComputeJackknifeResiduals(
        List<TrainingSample> trainSet,
        List<List<TreeNode>> newTrees,
        List<HashSet<int>>   oobMasks)
    {
        if (trainSet.Count < 20 || newTrees.Count == 0 || oobMasks.Count != newTrees.Count)
            return [];

        var residuals = new List<double>(trainSet.Count);

        for (int i = 0; i < trainSet.Count; i++)
        {
            double probSum  = 0.0;
            int    oobCount = 0;
            for (int t = 0; t < newTrees.Count; t++)
            {
                if (!oobMasks[t].Contains(i)) continue;
                probSum += GetLeafProb(newTrees[t], 0, trainSet[i].Features);
                oobCount++;
            }
            if (oobCount == 0) continue;

            double oobProb   = probSum / oobCount;
            double trueLabel = trainSet[i].Direction > 0 ? 1.0 : 0.0;
            residuals.Add(Math.Abs(trueLabel - oobProb));
        }

        residuals.Sort();
        return [.. residuals];
    }

    // ── Per-tree calibration-set accuracy (#7) ────────────────────────────────

    /// <summary>
    /// Computes the binary classification accuracy of each individual tree on
    /// <paramref name="calSet"/> using leaf-fraction probabilities (threshold = 0.5).
    /// Returns an array of T accuracy values in [0, 1].
    /// </summary>
    private static double[] ComputePerTreeCalAccuracies(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        int T = allTrees.Count;
        if (calSet.Count == 0 || T == 0) return new double[T];

        var accuracies = new double[T];

        for (int t = 0; t < T; t++)
        {
            int correct = 0;
            foreach (var s in calSet)
            {
                double p = GetLeafProb(allTrees[t], 0, s.Features);
                if ((p >= 0.5) == (s.Direction > 0)) correct++;
            }
            accuracies[t] = (double)correct / calSet.Count;
        }
        return accuracies;
    }

    // ── Biased importance-weighted feature candidate selection (#12) ──────────

    /// <summary>
    /// Samples exactly <paramref name="sqrtF"/> candidate split features without replacement
    /// from 0…F-1, biased toward features with higher importance scores.
    /// Uses an importance+ε unnormalised weight CDF with binary-search reservoir sampling.
    /// Falls back to sequential padding if the desired count is not reached.
    /// </summary>
    private static List<int> GenerateBiasedCandidateFeats(
        int     F,
        int     sqrtF,
        float[] importanceScores,
        Random  rng)
    {
        double epsilon = 1.0 / F;

        double sum = 0.0;
        var rawWeights = new double[F];
        for (int j = 0; j < F; j++)
        {
            double w = (j < importanceScores.Length ? importanceScores[j] : 0.0) + epsilon;
            rawWeights[j] = w;
            sum += w;
        }

        var cdf = new double[F];
        cdf[0] = rawWeights[0] / sum;
        for (int j = 1; j < F; j++)
            cdf[j] = cdf[j - 1] + rawWeights[j] / sum;

        var selected = new HashSet<int>(sqrtF);
        int attempts = 0;
        while (selected.Count < sqrtF && attempts < F * 10)
        {
            attempts++;
            double u   = rng.NextDouble();
            int    idx = Array.BinarySearch(cdf, u);
            if (idx < 0) idx = ~idx;
            idx = Math.Clamp(idx, 0, F - 1);
            selected.Add(idx);
        }

        for (int j = 0; j < F && selected.Count < sqrtF; j++)
            selected.Add(j);

        return [.. selected];
    }

    // ── Population Stability Index (PSI) across features ─────────────────────

    /// <summary>
    /// Computes the average PSI between the current training distribution and the parent
    /// model's distribution (represented by its quantile breakpoints).
    /// Current samples are binned into the parent's quantile intervals; the fraction in
    /// each bin is compared to the expected uniform fraction (1 / numBins).
    /// PSI = Σ_bins (actual% − expected%) × ln(actual% / expected%).
    /// Averaged over all features for which parent breakpoints are available.
    /// </summary>
    private static double ComputeAvgPsi(
        List<TrainingSample> trainSet,
        double[][]           parentBp,
        int                  F)
    {
        if (parentBp.Length == 0 || F == 0 || trainSet.Count == 0) return 0.0;

        int    n        = trainSet.Count;
        double totalPsi = 0.0;
        int    computed = 0;

        for (int fi = 0; fi < Math.Min(F, parentBp.Length); fi++)
        {
            double[] bp = parentBp[fi];
            if (bp is not { Length: >= 2 }) continue;

            int    numBins      = bp.Length + 1;          // n breakpoints → n+1 bins
            double expectedFrac = 1.0 / numBins;          // parent decile bins: ~1/numBins each
            var    binCounts    = new int[numBins];

            foreach (var s in trainSet)
            {
                double v   = fi < s.Features.Length ? s.Features[fi] : 0.0;
                int    bin = Array.BinarySearch(bp, v);
                if (bin < 0) bin = ~bin;
                bin = Math.Clamp(bin, 0, numBins - 1);
                binCounts[bin]++;
            }

            double psi = 0.0;
            for (int b = 0; b < numBins; b++)
            {
                double actual = Math.Max((double)binCounts[b] / n, 1e-4);
                double expect = Math.Max(expectedFrac, 1e-4);
                psi += (actual - expect) * Math.Log(actual / expect);
            }
            totalPsi += psi;
            computed++;
        }

        return computed > 0 ? totalPsi / computed : 0.0;
    }

    // ── GbmTree ↔ TreeNode conversion for JSON persistence ───────────────────
    //
    // Note on naming: QRF trees are serialised using the GbmTree / GbmNode types
    // (and stored in ModelSnapshot.GbmTreesJson) because those types already exist
    // in the shared snapshot format and cover exactly the fields needed (split feature,
    // threshold, left/right child indices, leaf value).  They carry no GBM-specific
    // semantics here — LeafValue holds the QRF leaf-fraction (P(Buy)) rather than a
    // gradient-boosting residual.  Downstream consumers must check ModelSnapshot.Type
    // == "quantilerf" to interpret the leaf values correctly.

    private static List<TreeNode> ConvertGbmToTreeNodes(GbmTree gbmTree)
    {
        if (gbmTree.Nodes is not { Count: > 0 }) return [];

        var nodes = new List<TreeNode>(gbmTree.Nodes.Count);
        foreach (var gn in gbmTree.Nodes)
        {
            nodes.Add(new TreeNode
            {
                SplitFeat     = gn.IsLeaf ? -1 : gn.SplitFeature,
                SplitThresh   = gn.SplitThreshold,
                LeftChild     = gn.LeftChild,
                RightChild    = gn.RightChild,
                LeafDirection = gn.LeafValue,
                // LeafPosCount / LeafTotalCount are rebuilt by RepopulateLeafCounts after loading
            });
        }
        return nodes;
    }

    /// <summary>
    /// Routes every training sample through the loaded warm-start tree and rebuilds the
    /// compact leaf counts (LeafPosCount / LeafTotalCount) from the current training set,
    /// replacing any stale values from the serialised snapshot.
    /// </summary>
    private static void RepopulateLeafCounts(
        List<TreeNode>       nodes,
        int                  rootIndex,
        List<TrainingSample> trainSet)
    {
        // Reset all leaf counts before repopulating
        foreach (var n in nodes) { n.LeafPosCount = 0; n.LeafTotalCount = 0; }

        foreach (var s in trainSet)
            IncrementLeafCount(nodes, rootIndex, s.Features, s.Direction > 0);

        // Sync LeafDirection with repopulated counts so serialization matches training-time probs
        foreach (var n in nodes)
            if (n.SplitFeat < 0) // leaf node
                n.LeafDirection = n.LeafTotalCount > 0
                    ? (double)n.LeafPosCount / n.LeafTotalCount
                    : 0.5;
    }

    private static void IncrementLeafCount(List<TreeNode> nodes, int nodeIndex, float[] features, bool isPositive)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Count) return;
        var node = nodes[nodeIndex];

        if (node.SplitFeat < 0 || node.SplitFeat >= features.Length)
        {
            node.LeafTotalCount++;
            if (isPositive) node.LeafPosCount++;
            return;
        }

        if (features[node.SplitFeat] <= node.SplitThresh)
            IncrementLeafCount(nodes, node.LeftChild,  features, isPositive);
        else
            IncrementLeafCount(nodes, node.RightChild, features, isPositive);
    }

    private static GbmTree ConvertTreeNodesToGbm(List<TreeNode> nodes)
    {
        var gbmNodes = new List<GbmNode>(nodes.Count);
        foreach (var n in nodes)
        {
            bool isLeaf = n.SplitFeat < 0;
            gbmNodes.Add(new GbmNode
            {
                IsLeaf         = isLeaf,
                LeafValue      = n.LeafDirection,
                SplitFeature   = isLeaf ? 0 : n.SplitFeat,
                SplitThreshold = n.SplitThresh,
                LeftChild      = n.LeftChild,
                RightChild     = n.RightChild,
            });
        }
        return new GbmTree { Nodes = gbmNodes };
    }

    // ── Calibration confidence interval (#8) ──────────────────────────────────

    /// <summary>
    /// #8: Bootstrap the calibration set to estimate uncertainty of Platt parameters.
    /// Returns (stdA, stdB) — the standard deviations of A and B across resamples.
    /// </summary>
    private static (double StdA, double StdB) ComputeCalibrationCI(
        List<TrainingSample> calSet,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        int                  bootstrapRounds = 50,
        int                  seed = 42)
    {
        if (calSet.Count < MinCalSamplesPlatt) return (0, 0);

        var aValues = new double[bootstrapRounds];
        var bValues = new double[bootstrapRounds];
        var rng     = new Random(seed);

        for (int r = 0; r < bootstrapRounds; r++)
        {
            var resample = new List<TrainingSample>(calSet.Count);
            for (int i = 0; i < calSet.Count; i++)
                resample.Add(calSet[rng.Next(calSet.Count)]);
            var (a, b) = FitPlattScaling(resample, allTrees, trainSet);
            aValues[r] = a;
            bValues[r] = b;
        }

        return (StdDevArr(aValues), StdDevArr(bValues));

        static double StdDevArr(double[] values)
        {
            double mean = values.Average();
            double varSum = 0;
            foreach (var v in values) varSum += (v - mean) * (v - mean);
            return values.Length > 1 ? Math.Sqrt(varSum / (values.Length - 1)) : 0;
        }
    }

    // ── 2nd-order feature interactions (#27) ──────────────────────────────────

    /// <summary>
    /// #27: Tracks which feature pairs co-occur on root→child paths across all trees.
    /// Returns a list of "FeatureA:FeatureB" pairs that co-occur in more than
    /// <paramref name="minCoOccurrenceFraction"/> of all trees.
    /// </summary>
    private static string[] Compute2ndOrderFeatureInteractions(
        List<List<TreeNode>> allTrees,
        double               minCoOccurrenceFraction = 0.5)
    {
        if (allTrees.Count < 2) return [];

        var pairCounts = new Dictionary<(int, int), int>();
        foreach (var tree in allTrees)
        {
            var featsInTree = new HashSet<int>();
            foreach (var node in tree)
                if (node.SplitFeat >= 0) featsInTree.Add(node.SplitFeat);

            var featList = featsInTree.OrderBy(f => f).ToList();
            for (int i = 0; i < featList.Count; i++)
                for (int j = i + 1; j < featList.Count; j++)
                {
                    var key = (featList[i], featList[j]);
                    pairCounts.TryGetValue(key, out int cnt);
                    pairCounts[key] = cnt + 1;
                }
        }

        int threshold = (int)(allTrees.Count * minCoOccurrenceFraction);
        return [.. pairCounts
            .Where(kv => kv.Value >= threshold)
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv =>
            {
                string a = kv.Key.Item1 < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[kv.Key.Item1] : $"F{kv.Key.Item1}";
                string b = kv.Key.Item2 < MLFeatureHelper.FeatureNames.Length
                    ? MLFeatureHelper.FeatureNames[kv.Key.Item2] : $"F{kv.Key.Item2}";
                return $"{a}×{b}";
            })];
    }
}
