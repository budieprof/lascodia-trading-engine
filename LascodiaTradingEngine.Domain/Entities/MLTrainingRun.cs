using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Tracks a single ML model training job, from queue through completion or failure.
/// Each run specifies the data window to train on and records the resulting accuracy metrics
/// and a reference to the <see cref="MLModel"/> produced.
/// </summary>
/// <remarks>
/// Runs are queued by API endpoints or scheduled automation. The <c>MLTrainingWorker</c>
/// picks up the next <see cref="RunStatus.Queued"/> run, processes it, and either promotes
/// the resulting model to <c>Active</c> or marks the run as <c>Failed</c> with an error message.
/// </remarks>
public class MLTrainingRun : Entity<long>
{
    /// <summary>The currency pair this training run targets (e.g. "EURUSD").</summary>
    public string   Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe for which a model is being trained (e.g. H1).</summary>
    public Timeframe   Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// How this training run was initiated:
    /// <c>Scheduled</c> by the automated scheduler, or <c>Manual</c> via the API.
    /// </summary>
    public TriggerType   TriggerType        { get; set; } = TriggerType.Scheduled;

    /// <summary>Current processing state: Queued → Running → Completed / Failed.</summary>
    public RunStatus   Status             { get; set; } = RunStatus.Queued;

    /// <summary>
    /// UTC start of the historical candle data window used for training.
    /// The worker loads all closed candles for the symbol/timeframe between
    /// <see cref="FromDate"/> and <see cref="ToDate"/>.
    /// </summary>
    public DateTime FromDate           { get; set; }

    /// <summary>UTC end of the training data window.</summary>
    public DateTime ToDate             { get; set; }

    /// <summary>Number of training samples (feature vectors) extracted from the candle window.</summary>
    public int      TotalSamples       { get; set; }

    /// <summary>
    /// Direction prediction accuracy achieved on the held-out evaluation set, 0.0–1.0.
    /// Populated after training completes. Null while the run is still in progress.
    /// </summary>
    public decimal? DirectionAccuracy  { get; set; }

    /// <summary>
    /// Magnitude RMSE in pips from the evaluation set.
    /// Null for models that do not produce magnitude predictions, or while running.
    /// </summary>
    public decimal? MagnitudeRMSE      { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="MLModel"/> entity created at the end of this run.
    /// Null until training succeeds and the model record is persisted.
    /// </summary>
    public long?    MLModelId          { get; set; }

    /// <summary>
    /// Error message if the run failed. Contains exception details to aid diagnosis.
    /// Null on successful runs.
    /// </summary>
    public string?  ErrorMessage       { get; set; }

    /// <summary>
    /// Number of times this training run has been attempted (including the current attempt).
    /// Incremented by <c>MLTrainingWorker</c> on each failure before re-queuing.
    /// </summary>
    public int      AttemptCount       { get; set; }

    /// <summary>
    /// Maximum number of retry attempts before the run is permanently marked as
    /// <see cref="RunStatus.Failed"/>. Default 3. Set to 1 to disable retries.
    /// </summary>
    public int      MaxAttempts        { get; set; } = 3;

    /// <summary>
    /// UTC timestamp before which the worker must not retry this run.
    /// Computed using exponential back-off on each failure.
    /// <c>null</c> means the run is eligible for immediate pickup.
    /// </summary>
    public DateTime? NextRetryAt       { get; set; }

    /// <summary>UTC timestamp when this run record was created (queued).</summary>
    public DateTime StartedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when training finished (succeeded or failed). Null while running.</summary>
    public DateTime? CompletedAt       { get; set; }

    /// <summary>UTC timestamp set when a worker claims this run (informational).</summary>
    public DateTime? PickedUpAt        { get; set; }

    /// <summary>
    /// Unique identifier of the worker process instance that claimed this run.
    /// Generated as a new Guid per process lifetime so concurrent worker instances
    /// can never claim the same run — each worker owns only the run whose
    /// WorkerInstanceId matches its own static Guid.
    /// </summary>
    public Guid?    WorkerInstanceId   { get; set; }

    /// <summary>Wall-clock time in milliseconds from claim to completion. Null while running.</summary>
    public long?    TrainingDurationMs { get; set; }

    /// <summary>F1 score achieved on the held-out evaluation set. Null while running.</summary>
    public decimal? F1Score            { get; set; }

    /// <summary>
    /// Expected value per signal on the evaluation set: mean(|magnitude| when correct)
    /// minus mean(|magnitude| when wrong). Positive = profitable edge. Null while running.
    /// </summary>
    public decimal? ExpectedValue      { get; set; }

    /// <summary>
    /// Brier score (mean squared error of probability forecasts, 0–1, lower is better).
    /// Measures probability calibration quality. Null while running.
    /// </summary>
    public decimal? BrierScore         { get; set; }

    /// <summary>
    /// Simulated Sharpe ratio on the evaluation set using ATR-normalised signal returns.
    /// Positive values indicate risk-adjusted profitability. Null while running.
    /// </summary>
    public decimal? SharpeRatio        { get; set; }

    /// <summary>
    /// Optional JSON object containing hyperparameter overrides for this run.
    /// Set by <c>TriggerMLHyperparamSearchCommand</c> to explore different training configurations.
    /// When present, <c>MLTrainingWorker</c> uses these values instead of the <c>EngineConfig</c> defaults.
    /// Encoding mirrors <c>HyperparamCandidate</c>: K, LearningRate, L2Lambda, TemporalDecayLambda,
    /// MaxEpochs, EmbargoBarCount, SearchBatchId, CandidateIndex, TotalCandidates.
    /// </summary>
    public string?  HyperparamConfigJson { get; set; }

    /// <summary>
    /// Fraction of training samples labelled Buy (1), in the range 0.0–1.0.
    /// 0.5 = perfectly balanced. Populated after samples are built, before training starts.
    /// Values far from 0.5 (e.g. > 0.65) indicate label imbalance that may bias the model.
    /// </summary>
    public decimal? LabelImbalanceRatio { get; set; }

    /// <summary>
    /// JSON snapshot of training dataset metadata captured before model fitting:
    /// buy/sell counts, candle date range, feature means and standard deviations.
    /// Stored for post-hoc debugging and reproducibility checks.
    /// </summary>
    public string?  TrainingDatasetStatsJson { get; set; }

    // ── Rec #1: TCN / learner architecture ───────────────────────────────────

    /// <summary>
    /// The learner architecture requested for this run. Defaults to
    /// <see cref="LearnerArchitecture.BaggedLogistic"/>. Set to
    /// <see cref="LearnerArchitecture.TemporalConvNet"/> or <see cref="LearnerArchitecture.HybridTcnLogistic"/>
    /// to invoke <c>TcnModelTrainer</c>.
    /// </summary>
    public LearnerArchitecture LearnerArchitecture { get; set; } = LearnerArchitecture.BaggedLogistic;

    // ── Rec #3: Self-supervised pre-training ─────────────────────────────────

    /// <summary>
    /// <c>true</c> when this run is the masked-autoencoder pre-training phase.
    /// Pre-training runs produce embedding weights stored in <see cref="MLModel.ModelBytes"/>
    /// but are not promoted to production. A subsequent supervised fine-tuning run
    /// references the pre-trained weights via <c>MLModel.TransferredFromModelId</c>.
    /// </summary>
    public bool     IsPretrainingRun   { get; set; }

    // ── Rec #6: Model distillation ────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when this run is a knowledge-distillation job targeting a compact
    /// student model. The ensemble teacher model is referenced in
    /// <see cref="MLModel.DistilledFromModelId"/> of the produced model.
    /// </summary>
    public bool     IsDistillationRun  { get; set; }

    // ── Rec #9: Emergency retrain via structural-break detection ──────────────

    /// <summary>
    /// <c>true</c> when this run was triggered by <c>MLStructuralBreakWorker</c>
    /// detecting a Bai-Perron structural break. Emergency runs are queue-priority
    /// and bypass the standard scheduling delay.
    /// </summary>
    public bool     IsEmergencyRetrain { get; set; }

    // ── Rec #14: Curriculum learning ──────────────────────────────────────────

    /// <summary>
    /// The difficulty score of the hardest sample seen in the final curriculum epoch.
    /// Computed as abs(label) / ATR_norm for each sample. Stored for audit purposes.
    /// Null when curriculum learning was not used for this run.
    /// </summary>
    public decimal? CurriculumFinalDifficulty { get; set; }

    // ── Rec #20: Temporal sample weighting ────────────────────────────────────

    /// <summary>
    /// Exponential decay half-life (days) used to weight training samples by recency.
    /// w_i = exp(−ln2 / HalfLife × days_since_sample).
    /// Null when temporal weighting was disabled for this run.
    /// </summary>
    public double?  TemporalDecayHalfLifeDays { get; set; }

    // ── Rec #22: SMOTE oversampling ────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when SMOTE was applied to balance the minority class.
    /// Set when <see cref="ClassImbalanceRatio"/> exceeded the 60/40 threshold.
    /// </summary>
    public bool     SmoteApplied        { get; set; }

    // ── Rec #27: Adversarial augmentation ─────────────────────────────────────

    /// <summary>
    /// <c>true</c> when FGSM adversarial samples were mixed into training.
    /// The ε value is recorded in <see cref="HyperparamConfigJson"/>.
    /// </summary>
    public bool     AdversarialAugmentApplied { get; set; }

    // ── Rec #28: Confident learning ────────────────────────────────────────────

    /// <summary>
    /// Estimated fraction of mislabelled training samples removed by Confident Learning
    /// before training (0–100). Null when Confident Learning was not used.
    /// </summary>
    public double?  LabelNoiseRatePercent { get; set; }

    // ── Rec #29: Weight pruning ────────────────────────────────────────────────

    /// <summary>
    /// Fraction of model weights set to zero by magnitude pruning (0–100).
    /// E.g. 20.0 means the bottom 20 % of weights by absolute value were zeroed.
    /// Null when pruning was not applied.
    /// </summary>
    public double?  SparsityPercent     { get; set; }

    // ── Rec #33: MAML few-shot meta-learning ──────────────────────────────────

    /// <summary>
    /// <c>true</c> when this run is a MAML meta-learning job that optimises a
    /// task-agnostic initialisation across multiple symbol tasks.
    /// </summary>
    public bool     IsMamlRun           { get; set; }

    /// <summary>
    /// Number of inner-loop gradient steps used per task during MAML meta-training.
    /// Null when <see cref="IsMamlRun"/> is <c>false</c>.
    /// </summary>
    public int?     MamlInnerSteps      { get; set; }

    // ── Rec #40: Coreset selection ─────────────────────────────────────────────

    /// <summary>
    /// Fraction of training samples retained after coreset selection (0–1).
    /// E.g. 0.5 means the greedy coreset algorithm kept the 50% most representative samples.
    /// Null when coreset selection was not used for this run.
    /// </summary>
    public double? CoresetSelectionRatio  { get; set; }

    // ── Rec #52: Importance sampling for rare events ───────────────────────────

    /// <summary>
    /// <c>true</c> when importance sampling was applied to up-weight rare high-magnitude
    /// events in the training loss. The sampling ratio is derived from the empirical
    /// magnitude distribution; events beyond the 90th percentile receive weight inversely
    /// proportional to their density.
    /// </summary>
    public bool    RareEventWeightingApplied { get; set; }

    // ── Rec #82: Time-series split CV ────────────────────────────────────────

    /// <summary>Per-fold log-loss scores from time-series expanding-window CV (Rec #82).</summary>
    public string? CvFoldScoresJson        { get; set; }

    // ── Rec #85: NCE loss ────────────────────────────────────────────────────

    /// <summary><c>true</c> when Noise Contrastive Estimation loss was used instead of BCE.</summary>
    public bool    NceLossUsed             { get; set; }

    // ── Rec #70: Mixup augmentation ───────────────────────────────────────────

    /// <summary><c>true</c> when mixup data augmentation was applied during training.</summary>
    public bool    MixupApplied            { get; set; }

    // ── Rec #71: Curriculum learning ─────────────────────────────────────────

    /// <summary><c>true</c> when curriculum learning ordering was applied during training.</summary>
    public bool    CurriculumApplied       { get; set; }

    // ── Improvement #2: Drift-aware trainer selection ──────────────────────

    /// <summary>
    /// Identifies which drift criterion triggered this training run:
    /// <c>"AccuracyDrift"</c>, <c>"CalibrationDrift"</c>, <c>"CovariateShift"</c>,
    /// <c>"SharpeDrift"</c>, <c>"DisagreementDrift"</c>, or <c>"MultiSignal"</c>
    /// when multiple signals fired simultaneously.
    /// Null for manually triggered, scheduled, or tenure-challenge runs.
    /// Used by <see cref="TrainerSelector"/> to bias architecture selection based on
    /// which drift type triggered the retrain.
    /// </summary>
    public string?  DriftTriggerType     { get; set; }

    /// <summary>
    /// JSON object containing the drift metrics that triggered this run.
    /// Format varies by <see cref="DriftTriggerType"/>:
    /// <list type="bullet">
    ///   <item>AccuracyDrift: <c>{"accuracy":0.47,"threshold":0.50}</c></item>
    ///   <item>CovariateShift: <c>{"maxPsi":0.28,"psiFeature":"Rsi","msz":1.7}</c></item>
    ///   <item>SharpeDrift: <c>{"liveSharpe":0.15,"trainSharpe":0.80}</c></item>
    /// </list>
    /// Null when <see cref="DriftTriggerType"/> is null.
    /// </summary>
    public string?  DriftMetadataJson    { get; set; }

    // ── Improvement #8: Abstention-aware trainer ranking ────────────────────

    /// <summary>
    /// Fraction of evaluation-set signals where the model's abstention gate fired
    /// (abstention score below threshold), in the range 0.0–1.0. Higher values
    /// indicate the model frequently declines to trade.
    /// Null while the run is in progress or when abstention is not computed.
    /// </summary>
    public decimal? AbstentionRate       { get; set; }

    /// <summary>
    /// Precision of the abstention gate: among signals the model chose to abstain on,
    /// what fraction would have been losing trades? Range 0.0–1.0, higher is better.
    /// Null while the run is in progress or when abstention is not computed.
    /// </summary>
    public decimal? AbstentionPrecision  { get; set; }

    // ── Improvement #9: Training budget allocation ──────────────────────────

    /// <summary>
    /// Queue priority for this training run. Lower values are processed first.
    /// <list type="bullet">
    ///   <item>0 — Emergency (structural break)</item>
    ///   <item>1 — Drift-triggered (accuracy/covariate degradation)</item>
    ///   <item>2 — Tenure challenge (proactive)</item>
    ///   <item>3 — Manual (operator-initiated)</item>
    ///   <item>5 — Scheduled (routine)</item>
    /// </list>
    /// Defaults to 5 (lowest priority). Used by <c>MLTrainingWorker.ClaimNextRunAsync</c>
    /// when two-lane queue is enabled.
    /// </summary>
    public int      Priority             { get; set; } = 5;

    // ── Data lineage & reproducibility (Improvement 3.4) ────────────────────

    /// <summary>
    /// SHA-256 hash of the sorted training feature matrix bytes. Enables exact dataset
    /// identification for reproducibility audits. Matches the value stored in
    /// <see cref="MLModel.DatasetHash"/> for the produced model.
    /// </summary>
    public string? DatasetHash { get; set; }

    /// <summary>
    /// Inclusive start candle ID used to build the training set. Together with
    /// <see cref="CandleIdRangeEnd"/>, defines the exact candle range for reproduction.
    /// </summary>
    public long? CandleIdRangeStart { get; set; }

    /// <summary>Inclusive end candle ID used to build the training set.</summary>
    public long? CandleIdRangeEnd { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool     IsDeleted          { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The ML model produced by this training run (null until training succeeds).</summary>
    public virtual MLModel? MLModel { get; set; }

}
