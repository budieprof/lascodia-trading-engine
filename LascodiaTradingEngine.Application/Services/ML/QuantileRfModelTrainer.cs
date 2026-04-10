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
public sealed partial class QuantileRfModelTrainer : IMLModelTrainer
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

    // Split ratios for train / selection / calibration / test
    private const double TrainSplitRatio      = 0.60;
    private const double SelectionEndRatio    = 0.70;
    private const double CalEndSplitRatio     = 0.80;

    // Platt scaling SGD
    private const double PlattLearningRate     = 0.01;
    private const int    PlattMaxEpochs        = 200;
    private const double PlattConvergenceDelta = 1e-7;

    // Stationarity gate (retained for backward compat — multi-signal drift is now primary)
    private const double StationarityRhoThreshold   = 0.97;

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

        // ── Data splits ──────────────────────────────────────────────���───────
        public List<TrainingSample> TrainSet      { get; set; } = [];
        public List<TrainingSample> SelectionSet  { get; set; } = [];
        public List<TrainingSample> CalSet        { get; set; } = [];
        public List<TrainingSample> TestSet       { get; set; } = [];

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

        // ── A+ hardening fields ──────────────────────────────────────────────
        public double AdversarialAuc { get; set; } = 0.5;
        public QrfDriftArtifact? DriftArtifact { get; set; }
        public QrfWarmStartArtifact? WarmStartArtifact { get; set; }
        public QrfAuditArtifact? AuditArtifact { get; set; }
        public double CalResidualMean      { get; set; }
        public double CalResidualStd       { get; set; }
        public double CalResidualThreshold { get; set; } = 1.0;
        public double[] FeatureVariances   { get; set; } = [];
        public double[] ReliabilityBinConf { get; set; } = [];
        public double[] ReliabilityBinAcc  { get; set; } = [];
        public int[]    ReliabilityBinCnts { get; set; } = [];
        public double MurphyCalLoss        { get; set; }
        public double MurphyRefLoss        { get; set; }
        public double PredictionStability  { get; set; }
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

        // ── 5. Final splits: 60 % train | 10 % selection | 10 % cal | ~20 % test + embargo ─
        int trainEnd     = (int)(allStd.Count * TrainSplitRatio);
        int selectionEnd = (int)(allStd.Count * SelectionEndRatio);
        int calEnd       = (int)(allStd.Count * CalEndSplitRatio);
        int embargo      = hp.EmbargoBarCount;

        int trainLimit     = Math.Max(0, trainEnd - embargo);
        int selectionStart = Math.Min(trainEnd + embargo, selectionEnd);
        int calStart       = Math.Min(selectionEnd + embargo, calEnd);
        int testStart      = Math.Min(calEnd + embargo, allStd.Count);

        ctx.TrainSet      = allStd[..trainLimit];
        ctx.SelectionSet  = selectionStart < selectionEnd ? allStd[selectionStart..selectionEnd] : [];
        ctx.CalSet        = calStart < calEnd             ? allStd[calStart..calEnd]             : [];
        ctx.TestSet       = testStart < allStd.Count      ? allStd[testStart..]                  : [];

        if (ctx.TrainSet.Count < hp.MinSamples)
            throw new InvalidOperationException(
                $"Insufficient training samples after splits: {ctx.TrainSet.Count} < {hp.MinSamples}");

        // ── 5b. Multi-signal drift diagnostics (replaces single-test stationarity gate) ─
        {
            var drift = ComputeQrfDriftDiagnostics(ctx.TrainSet, F, MLFeatureHelper.FeatureNames, hp.FracDiffD);
            ctx.DriftArtifact = drift;
            _logger.LogInformation(
                "QRF drift diagnostics: {N}/{T} non-stationary features ({Action}), " +
                "ACF={ACF:F3} PSI={PSI:F3} CUSUM={CP:F3} ADF={ADF:F3} KPSS={KPSS:F3}",
                drift.NonStationaryFeatureCount, drift.TotalFeatureCount, drift.GateAction,
                drift.MeanLag1Autocorrelation, drift.MeanPopulationStabilityIndex,
                drift.MeanChangePointScore, drift.MeanAdfLikeStatistic, drift.MeanKpssLikeStatistic);

            if (drift.GateAction == "REJECT" && hp.FracDiffD == 0.0)
            {
                _logger.LogWarning(
                    "QRF drift REJECT: {Frac:P1} features non-stationary (>=50%). " +
                    "Consider enabling FracDiffD. Aborting training.",
                    drift.NonStationaryFraction);
                ctx.FailedGates.Add("DriftDiagnostics");
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                    ctx.CvResult, []);
            }
            if (drift.GateTriggered && hp.FracDiffD == 0.0)
                _logger.LogWarning(
                    "QRF drift WARN: {Frac:P1} features non-stationary (>=15%). " +
                    "Consider enabling FracDiffD.",
                    drift.NonStationaryFraction);
        }

        // ── 5c. Class-imbalance gate (hard reject at 15/85, warn at 35/65) ────
        {
            int posCount = 0;
            foreach (var s in ctx.TrainSet) if (s.Direction > 0) posCount++;
            double buyRatio = (double)posCount / ctx.TrainSet.Count;
            if (buyRatio < 0.15 || buyRatio > 0.85)
                throw new InvalidOperationException(
                    $"QRF: extreme class imbalance (Buy={buyRatio:P1}).");
            if (buyRatio < ImbalanceLowerBound || buyRatio > ImbalanceUpperBound)
                _logger.LogWarning(
                    "QuantileRF class imbalance: Buy={Buy:P1}, Sell={Sell:P1}. " +
                    "Density-weighted bootstrap will partially compensate.",
                    buyRatio, 1.0 - buyRatio);
        }

        // ── 5d. Adversarial validation (train vs test distribution shift) ─────
        {
            double advAuc = TryComputeAdversarialAucGpu(ctx.TrainSet, ctx.TestSet, F, ctx.Ct)
                         ?? ComputeAdversarialAuc(ctx.TrainSet, ctx.TestSet, F);
            ctx.AdversarialAuc = advAuc;
            _logger.LogInformation("QRF adversarial AUC={AUC:F4}", advAuc);
            if (hp.QrfMaxAdversarialAuc > 0.0 && advAuc > hp.QrfMaxAdversarialAuc)
            {
                _logger.LogWarning(
                    "QRF adversarial validation failed: AUC={AUC:F4} > threshold={Thr:F4}. " +
                    "Train/test distributions differ significantly — aborting.",
                    advAuc, hp.QrfMaxAdversarialAuc);
                ctx.FailedGates.Add("AdversarialValidation");
                return new TrainingResult(
                    new EvalMetrics(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
                    ctx.CvResult, []);
            }
            ctx.GatesPassed++;
        }

        // ── 6. Density-ratio importance weights ───────────────────────────────
        double[]? densityWeights = null;
        if (hp.DensityRatioWindowDays > 0 && ctx.TrainSet.Count >= 50)
        {
            densityWeights = TryComputeDensityRatioWeightsGpu(ctx.TrainSet, F, hp.DensityRatioWindowDays, hp.BarsPerDay > 0 ? hp.BarsPerDay : 24, ctx.Ct)
                          ?? ComputeDensityRatioWeights(ctx.TrainSet, F, hp.DensityRatioWindowDays);
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

        // ── 16. EV-optimal threshold (on selectionSet — no test/cal leakage) ──
        var thresholdSet = ctx.SelectionSet.Count >= MinCalSamples ? ctx.SelectionSet : calSet;
        double optimalThreshold = ComputeOptimalThreshold(
            thresholdSet, allTrees, trainSet, plattA, plattB, isotonicBp,
            hp.ThresholdSearchMin, hp.ThresholdSearchMax,
            stepBps: hp.ThresholdSearchStepBps);
        _logger.LogInformation("EV-optimal threshold={Thr:F3} (step={Bps}bps, set={Set})",
            optimalThreshold, hp.ThresholdSearchStepBps,
            ctx.SelectionSet.Count >= MinCalSamples ? "selection" : "cal");
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

        // ── 22. Selection-set permutation importance (for warm-start transfer)
        var importanceRefSet = ctx.SelectionSet.Count >= 10 ? ctx.SelectionSet : calSet;
        ctx.CalImportanceScores = importanceRefSet.Count >= 10
            ? ComputeCalPermutationImportance(importanceRefSet, allTrees, trainSet, F, ctx.Ct)
            : new double[F];

        // ── 23. Feature pruning re-train pass ─────────────────────────────────
        var  activeMask  = BuildFeatureMask(featureImportance, hp.MinFeatureImportance, F);
        int  prunedCount = activeMask.Count(m => !m);

        if (prunedCount > 0 && F - prunedCount >= 10)
        {
            _logger.LogInformation(
                "QuantileRF feature pruning: masking {Pruned}/{Total} low-importance features.",
                prunedCount, F);

            var maskedTrain     = ApplyMask(trainSet, activeMask);
            var maskedSelection = ApplyMask(ctx.SelectionSet, activeMask);
            var maskedCal       = ApplyMask(calSet,   activeMask);
            var maskedTest      = ApplyMask(testSet,  activeMask);

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
                ctx.AllTrees     = pTrees;
                ctx.TrainSet     = maskedTrain;
                ctx.SelectionSet = maskedSelection;
                ctx.CalSet       = maskedCal;
                ctx.TestSet      = maskedTest;
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
                var postPruneThresholdSet = ctx.SelectionSet.Count >= MinCalSamples ? ctx.SelectionSet : ctx.CalSet;
                ctx.OptimalThreshold = ComputeOptimalThreshold(postPruneThresholdSet, ctx.AllTrees, ctx.TrainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp,
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

        // ── 29b. Murphy decomposition, reliability diagram, cal residuals, feature variances,
        //         prediction stability, warm-start artifact ──────────────────────
        {
            Func<float[], double> calibProb = f => PredictProb(f, allTrees, trainSet, ctx.PlattA, ctx.PlattB, ctx.IsotonicBp);

            var (murphyCal, murphyRef) = ComputeMurphyDecomposition(testSet, calibProb);
            ctx.MurphyCalLoss = SafeQrf(murphyCal);
            ctx.MurphyRefLoss = SafeQrf(murphyRef);
            _logger.LogInformation("Murphy decomposition: calLoss={Cal:F6} refLoss={Ref:F6}", murphyCal, murphyRef);

            var (relConf, relAcc, relCnts) = ComputeReliabilityDiagram(testSet, calibProb);
            ctx.ReliabilityBinConf = relConf;
            ctx.ReliabilityBinAcc  = relAcc;
            ctx.ReliabilityBinCnts = relCnts;

            var (calResMean, calResStd, calResThr) = ComputeCalibrationResidualStats(calSet, calibProb);
            ctx.CalResidualMean      = SafeQrf(calResMean);
            ctx.CalResidualStd       = SafeQrf(calResStd);
            ctx.CalResidualThreshold = SafeQrf(calResThr, 1.0);
            _logger.LogDebug("QRF cal residuals: mean={M:F4} std={S:F4} threshold={T:F4}",
                calResMean, calResStd, calResThr);

            ctx.FeatureVariances = ComputeFeatureVariancesQrf(trainSet, F);

            ctx.PredictionStability = SafeQrf(ComputePredictionStabilityScore(testSet, calibProb));
            _logger.LogDebug("QRF prediction stability={S:F4}", ctx.PredictionStability);

            ctx.WarmStartArtifact = BuildQrfWarmStartArtifact(ctx.WarmStart, ctx.WarmTrees, ctx.TreeCount);
        }

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
            MagBias                    = SafeQrf(ctx.MagBias),
            MagQ90Weights              = ctx.MagQ90Weights,
            MagQ90Bias                 = SafeQrf(ctx.MagQ90Bias),
            PlattA                     = SafeQrf(ctx.PlattA, 1.0),
            PlattB                     = SafeQrf(ctx.PlattB),
            PlattABuy                  = SafeQrf(ctx.PlattABuy),
            PlattBBuy                  = SafeQrf(ctx.PlattBBuy),
            PlattASell                 = SafeQrf(ctx.PlattASell),
            PlattBSell                 = SafeQrf(ctx.PlattBSell),
            AvgKellyFraction           = SafeQrf(ctx.AvgKellyFraction),
            Metrics                    = ctx.EvalMetrics,
            OobAccuracy                = SafeQrf(ctx.OobAccuracy),
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
            OptimalThreshold           = SafeQrf(ctx.OptimalThreshold, 0.5),
            Ece                        = SafeQrf(ctx.Ece),
            IsotonicBreakpoints        = ctx.IsotonicBp,
            ConformalQHat              = SafeQrf(ctx.ConformalQHat),
            ConformalCoverage          = hp.ConformalCoverage,
            FeatureQuantileBreakpoints = ctx.FeatureQuantileBp,
            ParentModelId              = ctx.ParentModelId ?? 0,
            GenerationNumber           = ctx.GenerationNum,
            BrierSkillScore            = SafeQrf(ctx.BrierSkillScore),
            SanitizedLearnerCount      = ctx.SanitizedCount,
            FracDiffD                  = hp.FracDiffD,
            AgeDecayLambda             = hp.AgeDecayLambda,
            DurbinWatsonStatistic      = SafeQrf(ctx.DurbinWatson, 2.0),
            TemperatureScale           = SafeQrf(ctx.TemperatureScale),
            WalkForwardSharpeTrend     = SafeQrf(ctx.CvResult.SharpeTrend),
            HyperparamsJson            = JsonSerializer.Serialize(hp, JsonOptions),
            GbmTreesJson               = JsonSerializer.Serialize(gbmTrees, JsonOptions),
            QrfWeights                 = [qrfWeightRow],
            // ── New v4.0 fields ───────────────────────────────────────────────
            MetaWeights                = ctx.MetaWeights,                   // #2
            MetaBias                   = SafeQrf(ctx.MetaBias),             // #2
            EnsembleSelectionWeights   = ctx.GesWeights,                    // #3
            OobPrunedLearnerCount      = ctx.OobPrunedCount,                // #4
            EnsembleDiversity          = SafeQrf(ctx.EnsembleDiversity),    // #5
            AbstentionWeights          = ctx.AbstentionWeights,             // #6
            AbstentionBias             = SafeQrf(ctx.AbstentionBias),       // #6
            AbstentionThreshold        = SafeQrf(ctx.AbstentionThreshold, 0.5), // #6
            LearnerCalAccuracies       = ctx.TreeCalAccuracies,             // #7
            JackknifeResiduals         = ctx.JackknifeResiduals,            // #9
            MetaLabelWeights           = ctx.MetaLabelWeights,              // #10
            MetaLabelBias              = SafeQrf(ctx.MetaLabelBias),        // #10
            MetaLabelThreshold         = 0.5,                               // #10
            RedundantFeaturePairs      = ctx.RedundantPairs,                // #13
            // ── MLP magnitude regressor (QrfMagHiddenDim > 0) ─────────────────
            QrfMlpHiddenDim            = hp.QrfMagHiddenDim,
            QrfMlpW1                   = ctx.MlpW1,
            QrfMlpB1                   = ctx.MlpB1,
            QrfMlpW2                   = ctx.MlpW2,
            QrfMlpB2                   = SafeQrf(ctx.MlpB2),
            // ── A+ hardening fields ───────────────────────────────────────────
            CalibrationLoss            = SafeQrf(ctx.MurphyCalLoss),
            RefinementLoss             = SafeQrf(ctx.MurphyRefLoss),
            ReliabilityBinConfidence   = ctx.ReliabilityBinConf,
            ReliabilityBinAccuracy     = ctx.ReliabilityBinAcc,
            ReliabilityBinCounts       = ctx.ReliabilityBinCnts,
            QrfCalibrationResidualMean      = SafeQrf(ctx.CalResidualMean),
            QrfCalibrationResidualStd       = SafeQrf(ctx.CalResidualStd),
            QrfCalibrationResidualThreshold = SafeQrf(ctx.CalResidualThreshold, 1.0),
            FeatureVariances           = ctx.FeatureVariances,
            PredictionStabilityScore   = SafeQrf(ctx.PredictionStability),
            QrfDriftArtifact           = ctx.DriftArtifact,
            QrfWarmStartArtifact       = ctx.WarmStartArtifact,
        };

        // ── Sanitize all arrays + run parity audit before serialization ───────
        SanitizeQrfSnapshotArrays(snapshot);

        var auditResult = RunQrfAudit(snapshot, ctx.TestSet);
        snapshot.QrfAuditArtifact = auditResult.Artifact;
        if (auditResult.Findings.Length > 0)
            _logger.LogWarning("QRF audit findings: {Findings}", string.Join("; ", auditResult.Findings));

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
}
