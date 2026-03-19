using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a trained machine-learning model that scores trade signals with a
/// predicted direction, magnitude, and confidence before order submission.
/// </summary>
/// <remarks>
/// Each model is scoped to a specific <see cref="Symbol"/> and <see cref="Timeframe"/> pair.
/// Only one model per symbol/timeframe combination may be <see cref="IsActive"/> at a time.
/// The <c>MLTrainingWorker</c> promotes a newly trained model to <c>Active</c> and
/// demotes the previous champion to <see cref="MLModelStatus.Superseded"/>.
/// A shadow evaluation (<see cref="MLShadowEvaluation"/>) may run a challenger model
/// alongside the champion before a full promotion decision is made.
/// </remarks>
public class MLModel : Entity<long>
{
    /// <summary>The currency pair this model was trained on (e.g. "EURUSD").</summary>
    public string  Symbol              { get; set; } = string.Empty;

    /// <summary>The chart timeframe this model was trained on (e.g. H1, M15).</summary>
    public Timeframe  Timeframe           { get; set; } = Timeframe.H1;

    /// <summary>
    /// Semantic version string for this model (e.g. "1.0.0", "1.2400.0").
    /// The second segment typically encodes the training sample count for traceability.
    /// </summary>
    public string  ModelVersion        { get; set; } = string.Empty;

    /// <summary>
    /// Absolute file path to the serialised model file on disk (JSON for the built-in
    /// statistical model, or a .mlnet binary for ML.NET-based models).
    /// Loaded by <c>IMLSignalScorer</c> at startup and cached in memory.
    /// </summary>
    public string  FilePath            { get; set; } = string.Empty;

    /// <summary>
    /// Current lifecycle state of this model:
    /// <c>Training</c> → <c>Active</c> → <c>Superseded</c> / <c>Retired</c>.
    /// </summary>
    public MLModelStatus  Status              { get; set; } = MLModelStatus.Training;

    /// <summary>
    /// <c>true</c> when this model is the current champion for its symbol/timeframe
    /// and is actively being used to score new signals. Only one model per pair should
    /// be active at any time.
    /// </summary>
    public bool    IsActive            { get; set; }

    /// <summary>
    /// Fraction of test samples on which the model correctly predicted the next-bar direction
    /// (Buy vs Sell), in the range 0.0–1.0. Computed during training evaluation.
    /// </summary>
    public decimal? DirectionAccuracy  { get; set; }

    /// <summary>
    /// Root-mean-square error of the magnitude prediction in pips.
    /// Lower values indicate the model is better at estimating how far the price will move.
    /// Null if the model does not produce magnitude predictions.
    /// </summary>
    public decimal? MagnitudeRMSE      { get; set; }

    /// <summary>Number of historical candle samples used to train this model.</summary>
    public int     TrainingSamples     { get; set; }

    /// <summary>UTC timestamp when training completed and this record was created.</summary>
    public DateTime TrainedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when this model was promoted to <c>Active</c> status and began
    /// scoring live signals. Null until the model is activated.
    /// </summary>
    public DateTime? ActivatedAt       { get; set; }

    /// <summary>
    /// Serialised model weights stored as UTF-8 JSON bytes.
    /// Preferred over <see cref="FilePath"/> for cloud / containerised deployments
    /// where local disk is not shared across instances.
    /// </summary>
    public byte[]? ModelBytes              { get; set; }

    /// <summary>F1 score achieved on the final held-out evaluation set.</summary>
    public decimal? F1Score                { get; set; }

    /// <summary>
    /// Expected value per signal on the evaluation set (ATR-normalised units).
    /// Positive values indicate the model has a profitable directional edge.
    /// </summary>
    public decimal? ExpectedValue          { get; set; }

    /// <summary>Brier score of the ensemble's probability output (0–1, lower is better).</summary>
    public decimal? BrierScore             { get; set; }

    /// <summary>Simulated Sharpe ratio on the evaluation set using ATR-normalised signal returns.</summary>
    public decimal? SharpeRatio            { get; set; }

    /// <summary>
    /// Platt scaling parameter A. The calibrated probability is
    /// sigmoid(PlattA * logit(rawProb) + PlattB). Default 1.0 means no calibration shift.
    /// </summary>
    public decimal? PlattA                 { get; set; }

    /// <summary>Platt scaling bias parameter B. Default 0.0 means no calibration shift.</summary>
    public decimal? PlattB                 { get; set; }

    /// <summary>Number of walk-forward CV folds used to validate this model.</summary>
    public int      WalkForwardFolds       { get; set; }

    /// <summary>Average direction accuracy across all walk-forward CV folds (0–1).</summary>
    public decimal? WalkForwardAvgAccuracy { get; set; }

    /// <summary>
    /// Standard deviation of direction accuracy across CV folds.
    /// Low values indicate stable performance across time periods.
    /// </summary>
    public decimal? WalkForwardStdDev      { get; set; }

    /// <summary>
    /// Market regime this model was trained on (e.g. "Trending", "Ranging", "Volatile").
    /// <c>null</c> for the global model trained across all regimes.
    /// When non-null, <c>MLSignalScorer</c> routes inference to this model only when the
    /// current detected regime matches, falling back to the global model otherwise.
    /// </summary>
    public string? RegimeScope             { get; set; }

    /// <summary>
    /// When <c>true</c>, this model has been temporarily suppressed by
    /// <c>MLSignalSuppressionWorker</c> due to a hard-floor accuracy breach.
    /// Suppressed models are skipped at scoring time (treated as if no active model exists)
    /// until accuracy recovers or a replacement model is activated, at which point
    /// suppression is automatically lifted.
    /// </summary>
    public bool    IsSuppressed            { get; set; }

    /// <summary>
    /// When <c>true</c>, this previously-superseded model has been temporarily
    /// re-activated by <c>MLSuppressionRollbackWorker</c> to serve as a fallback
    /// champion while the current primary model is suppressed.
    /// <c>MLSignalScorer</c> uses this model when the primary model is unavailable.
    /// Automatically deactivated when the primary model's suppression is lifted or
    /// a new model is promoted.
    /// </summary>
    public bool    IsFallbackChampion      { get; set; }

    /// <summary>
    /// Direction accuracy measured on live production predictions after the model was activated.
    /// Computed from <see cref="MLModelPredictionLog"/> records and written back at model
    /// retirement time by <c>MLTrainingWorker</c> / <c>MLShadowArbiterWorker</c>.
    /// Null until the model is superseded.
    /// </summary>
    public decimal? LiveDirectionAccuracy  { get; set; }

    /// <summary>
    /// Total number of resolved live prediction logs for this model.
    /// Written at retirement time alongside <see cref="LiveDirectionAccuracy"/>.
    /// </summary>
    public int      LiveTotalPredictions   { get; set; }

    /// <summary>
    /// Number of calendar days this model was active (from <see cref="ActivatedAt"/> to retirement).
    /// Written at retirement time.
    /// </summary>
    public int      LiveActiveDays         { get; set; }

    // ── Rec #1: TCN / learner architecture ───────────────────────────────────

    /// <summary>
    /// The neural/statistical architecture used to produce this model.
    /// <see cref="LearnerArchitecture.BaggedLogistic"/> is the default.
    /// TCN and Hybrid variants are written by <c>TcnModelTrainer</c>.
    /// </summary>
    public LearnerArchitecture LearnerArchitecture { get; set; } = LearnerArchitecture.BaggedLogistic;

    // ── Rec #4: Cross-symbol knowledge transfer ───────────────────────────────

    /// <summary>
    /// Optional FK to the donor <see cref="MLModel"/> whose weights warm-started this model
    /// during cross-symbol transfer learning. Null for cold-start models.
    /// </summary>
    public long?   TransferredFromModelId  { get; set; }

    // ── Rec #6: Model distillation ────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when this model was produced by knowledge distillation from a larger
    /// ensemble. Distilled models have lower inference latency at the cost of a small
    /// accuracy reduction. Controlled by <c>MLModelDistillationWorker</c>.
    /// </summary>
    public bool    IsDistilled             { get; set; }

    /// <summary>
    /// Optional FK to the ensemble <see cref="MLModel"/> this model was distilled from.
    /// Null for non-distilled models.
    /// </summary>
    public long?   DistilledFromModelId    { get; set; }

    // ── Rec #17: Online SGD post-trade updates ────────────────────────────────

    /// <summary>
    /// Number of online SGD weight update passes applied to this model after activation.
    /// Each pass corresponds to one resolved trade outcome processed by
    /// <c>MLOnlineLearningWorker</c>. Zero for models that have never been online-updated.
    /// </summary>
    public int     OnlineLearningUpdateCount { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent online SGD weight update.
    /// Null until the first online update is applied.
    /// </summary>
    public DateTime? LastOnlineLearningAt  { get; set; }

    // ── Rec #25: Stacking ensemble meta-learner ───────────────────────────────

    /// <summary>
    /// <c>true</c> when this model is a stacking meta-learner trained on the
    /// out-of-fold predictions of base models.  Meta-learner models are not promoted
    /// to production via the standard shadow evaluation pipeline; instead they are
    /// referenced by <c>MLSignalScorer</c> directly when blending base outputs.
    /// </summary>
    public bool    IsMetaLearner           { get; set; }

    // ── Rec #33: MAML few-shot meta-learning ──────────────────────────────────

    /// <summary>
    /// <c>true</c> when this model stores a MAML meta-initialisation — a set of weights
    /// that can be rapidly fine-tuned to a new symbol in K inner-loop gradient steps.
    /// MAML initialisers are not used for direct inference; they are the starting point
    /// for per-symbol fine-tuning runs triggered by <c>MLMamlMetaLearnerWorker</c>.
    /// </summary>
    public bool    IsMamlInitializer       { get; set; }

    // ── Rec #44: Model soups (weight averaging) ────────────────────────────────

    /// <summary>
    /// <c>true</c> when this model was produced by model-soup weight averaging —
    /// the arithmetic mean of weights from N independently fine-tuned checkpoints
    /// selected by <c>MLModelSoupWorker</c> using greedy interpolation on the
    /// validation accuracy. Soup models occupy a flatter loss basin and generalise better.
    /// </summary>
    public bool    IsSoupModel             { get; set; }

    // ── Rec #45: Recurrent online Platt scaling ────────────────────────────────

    /// <summary>
    /// Exponential-moving-average drift of the Platt calibration parameter A from its
    /// training-time value. Tracked by <c>MLOnlinePlattWorker</c> using a sliding window
    /// of 50 recent outcomes. Positive drift = model becoming over-confident.
    /// Null until at least one online Platt update has been applied.
    /// </summary>
    public double? PlattCalibrationDrift   { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted               { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>Training runs that produced or are associated with this model.</summary>
    public virtual ICollection<MLTrainingRun> TrainingRuns { get; set; } = new List<MLTrainingRun>();

    /// <summary>Trade signals that were scored by this model.</summary>
    public virtual ICollection<TradeSignal> TradeSignals { get; set; } = new List<TradeSignal>();

    /// <summary>Individual per-signal prediction records used to measure live model accuracy.</summary>
    public virtual ICollection<MLModelPredictionLog> PredictionLogs { get; set; } = new List<MLModelPredictionLog>();

    /// <summary>Shadow evaluations in which this model acted as the champion (incumbent).</summary>
    public virtual ICollection<MLShadowEvaluation> ChampionEvaluations { get; set; } = new List<MLShadowEvaluation>();

    /// <summary>Shadow evaluations in which this model acted as the challenger (candidate for promotion).</summary>
    public virtual ICollection<MLShadowEvaluation> ChallengerEvaluations { get; set; } = new List<MLShadowEvaluation>();

    /// <summary>Granger-causality feature audits computed against this model's live predictions.</summary>
    public virtual ICollection<MLCausalFeatureAudit> CausalFeatureAudits { get; set; } = new List<MLCausalFeatureAudit>();

    /// <summary>Conformal calibration records for this model (Rec #16).</summary>
    public virtual ICollection<MLConformalCalibration> ConformalCalibrations { get; set; } = new List<MLConformalCalibration>();

    /// <summary>Pairwise feature interaction scores computed for this model (Rec #34).</summary>
    public virtual ICollection<MLFeatureInteractionAudit> FeatureInteractionAudits { get; set; } = new List<MLFeatureInteractionAudit>();

    // ── Rec #79: Posterior predictive checks ──────────────────────────────────

    /// <summary>
    /// <c>true</c> when the most recent posterior predictive check (Rec #79) found that
    /// the model's actual OOS accuracy fell below the 5th percentile of the simulated
    /// accuracy distribution — indicating unmodelled distributional shift.
    /// </summary>
    public bool    PpcSurprised           { get; set; }

    // ── Rec #80: Rolling OOS equity curve ────────────────────────────────────

    /// <summary>
    /// Most recent OOS max drawdown computed from resolved prediction logs.
    /// Null until first OOS equity curve snapshot is computed.
    /// Updated by <c>MLOosEquityCurveWorker</c>.
    /// </summary>
    public double? LatestOosMaxDrawdown   { get; set; }

    /// <summary>Kyle's lambda (price impact coefficient) from the most recent MLKyleLambdaWorker run.</summary>
    public double LatestKyleLambda { get; set; }

    // ── Rec #112: CVB ensemble ────────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when this model was produced by cross-validation blending
    /// (K=5 expanding-window models averaged with uniform 1/K weights).
    /// </summary>
    public bool    IsCvbEnsemble          { get; set; }
}
