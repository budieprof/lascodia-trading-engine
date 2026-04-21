using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records an individual ML model prediction made at signal-scoring time, along with
/// the eventual actual outcome once the trade closes. Used to measure and track the
/// real-world accuracy of deployed models in production.
/// </summary>
/// <remarks>
/// A prediction log is created for every signal scored by an <see cref="MLModel"/>.
/// The predicted fields are populated immediately at scoring time; the actual outcome
/// fields (<see cref="ActualDirection"/>, <see cref="ActualMagnitudePips"/>,
/// <see cref="WasProfitable"/>, <see cref="DirectionCorrect"/>) are back-filled by the
/// outcome-recorder worker once the associated trade is closed.
///
/// These records are the foundation of the live model performance dashboard and the
/// shadow evaluation system (<see cref="MLShadowEvaluation"/>), which compares champion
/// and challenger models on real trading outcomes before promoting a new model.
/// </remarks>
public class MLModelPredictionLog : Entity<long>
{
    /// <summary>Foreign key to the <see cref="TradeSignal"/> this prediction was made for.</summary>
    public long    TradeSignalId         { get; set; }

    /// <summary>Foreign key to the <see cref="MLModel"/> that made this prediction.</summary>
    public long    MLModelId             { get; set; }

    /// <summary>
    /// Whether the model was acting as the <c>Champion</c> (active production model)
    /// or <c>Challenger</c> (candidate under shadow evaluation) at prediction time.
    /// </summary>
    public ModelRole  ModelRole             { get; set; } = ModelRole.Champion;

    /// <summary>The currency pair this prediction covers (e.g. "EURUSD").</summary>
    public string  Symbol                { get; set; } = string.Empty;

    /// <summary>The chart timeframe on which the model's features were computed.</summary>
    public Timeframe  Timeframe           { get; set; } = Timeframe.H1;

    /// <summary>The direction (<c>Buy</c> or <c>Sell</c>) predicted by the model.</summary>
    public TradeDirection  PredictedDirection    { get; set; } = TradeDirection.Buy;

    /// <summary>
    /// The price movement magnitude predicted by the model in pips.
    /// Used in conjunction with <see cref="ConfidenceScore"/> to filter low-conviction signals.
    /// </summary>
    public decimal PredictedMagnitudePips { get; set; }

    /// <summary>
    /// Model's confidence in this prediction, in the range 0.0–1.0.
    /// Derived from the model's output probability distribution.
    /// </summary>
    public decimal ConfidenceScore       { get; set; }

    /// <summary>
    /// Raw Buy-class probability emitted by the base inference engine before any
    /// Platt, temperature, isotonic, or age-decay calibration is applied.
    /// Null for legacy logs created before exact probability persistence was added.
    /// </summary>
    public decimal? RawProbability       { get; set; }

    /// <summary>
    /// Base model's calibrated Buy-class probability after its own calibration stack
    /// (temperature / Platt / isotonic / age decay) but before any scorer-side
    /// stacking/meta blend is applied.
    /// Null for legacy logs created before exact probability persistence was added.
    /// </summary>
    public decimal? CalibratedProbability { get; set; }

    /// <summary>
    /// Effective calibrated Buy-class probability that actually drove the served
    /// production decision after scorer-side blending (for example, stacking meta).
    /// When null, consumers should fall back to <see cref="CalibratedProbability"/>
    /// for backward compatibility with older rows.
    /// </summary>
    public decimal? ServedCalibratedProbability { get; set; }

    /// <summary>
    /// Effective decision threshold used at scoring time after applying regime,
    /// adaptive, and optimal-threshold precedence.
    /// Null for legacy logs created before threshold persistence was added.
    /// </summary>
    public decimal? DecisionThresholdUsed { get; set; }

    /// <summary>
    /// The actual direction the market moved after the signal was acted upon.
    /// Populated by the outcome-recorder worker once the trade closes.
    /// Null while the trade is still open.
    /// </summary>
    public TradeDirection? ActualDirection       { get; set; }

    /// <summary>
    /// The actual price movement achieved from entry to close, in pips.
    /// Populated after the trade closes. Negative values indicate adverse movement.
    /// Null while the trade is still open.
    /// </summary>
    public decimal? ActualMagnitudePips  { get; set; }

    /// <summary>
    /// <c>true</c> if the associated trade closed with a positive P&amp;L (profit).
    /// Null until the trade is closed and outcome is recorded.
    /// </summary>
    public bool?   WasProfitable         { get; set; }

    /// <summary>
    /// <c>true</c> if <see cref="PredictedDirection"/> matched <see cref="ActualDirection"/>.
    /// The primary metric used to evaluate model direction accuracy in live trading.
    /// Null until the trade is closed and outcome is recorded.
    /// </summary>
    public bool?   DirectionCorrect      { get; set; }

    /// <summary>
    /// Whether the predicted direction was still correct at the 3-bar forward horizon
    /// (price at +3 closed candles moved in the predicted direction vs the prediction bar).
    /// Null until resolved by <c>MLMultiHorizonOutcomeWorker</c>.
    /// </summary>
    public bool?   HorizonCorrect3       { get; set; }

    /// <summary>
    /// Whether the predicted direction was correct at the 6-bar forward horizon.
    /// Null until resolved by <c>MLMultiHorizonOutcomeWorker</c>.
    /// </summary>
    public bool?   HorizonCorrect6       { get; set; }

    /// <summary>
    /// Whether the predicted direction was correct at the 12-bar forward horizon.
    /// Null until resolved by <c>MLMultiHorizonOutcomeWorker</c>.
    /// </summary>
    public bool?   HorizonCorrect12      { get; set; }

    /// <summary>UTC timestamp when this prediction was recorded (at signal scoring time).</summary>
    public DateTime PredictedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the actual trade outcome was back-filled. Null while open.</summary>
    public DateTime? OutcomeRecordedAt   { get; set; }

    /// <summary>
    /// Identifies which worker resolved the outcome.
    /// "NextBarCandle" = resolved by <c>MLPredictionOutcomeWorker</c> using the first closed
    /// candle after the prediction. Null when the outcome has not yet been resolved.
    /// </summary>
    public string? ResolutionSource      { get; set; }

    /// <summary>
    /// Standard deviation of individual ensemble learner probabilities at prediction time.
    /// 0.0 = all learners agree; 0.5 = maximum disagreement.
    /// Rising values indicate that model certainty is degrading over time.
    /// Null when the signal was not scored by an ML model.
    /// </summary>
    public decimal? EnsembleDisagreement { get; set; }

    /// <summary>
    /// JSON array of the top-5 SHAP feature contributions for this prediction.
    /// Format: [{"Feature":"Rsi","Value":0.042},{"Feature":"AtrNorm","Value":-0.031}, ...]
    /// Computed as φ_j = w̄_j × x_j (ensemble-averaged linear SHAP).
    /// Covers all trainer architectures — falls back to importance×feature for tree models.
    /// Null for predictions made before SHAP attribution was introduced.
    /// </summary>
    public string? ContributionsJson     { get; set; }

    /// <summary>
    /// Wall-clock time taken by <c>IMLSignalScorer.ScoreAsync</c> to produce this prediction,
    /// in milliseconds. Populated by the signal-creation path and used by
    /// <c>MLInferenceLatencyWorker</c> to compute rolling P50/P95/P99 percentiles.
    /// Null for predictions made before latency instrumentation was introduced.
    /// </summary>
    public int?    LatencyMs             { get; set; }

    // ── Rec #8: Counterfactual explanations ──────────────────────────────────

    /// <summary>
    /// JSON object describing the minimal feature change required to flip this prediction.
    /// Format: {"RSI":"+12.3","ATR_norm":"-0.08"} — only features that must change are included.
    /// Computed by <c>CounterfactualExplainer</c> via constrained gradient ascent in feature space.
    /// Null for predictions made before counterfactual explanations were introduced.
    /// </summary>
    public string? CounterfactualJson    { get; set; }

    // ── Rec #10: Heteroscedastic magnitude ────────────────────────────────────

    /// <summary>
    /// ±1σ uncertainty bound around <see cref="PredictedMagnitudePips"/> in pips,
    /// derived from the heteroscedastic output head that models both μ and σ² of the
    /// return distribution. E.g. 3.2 means predicted magnitude ± 3.2 pips at 68% coverage.
    /// Null when heteroscedastic magnitude prediction is not enabled for this model.
    /// </summary>
    public decimal? MagnitudeUncertaintyPips { get; set; }

    // ── Rec #11: Monte Carlo Dropout ─────────────────────────────────────────

    /// <summary>
    /// Variance of the MC-Dropout probability distribution computed from N stochastic
    /// forward passes with dropout active at inference time.
    /// High variance = high epistemic (model) uncertainty — complements conformal coverage.
    /// Null when MC-Dropout was not used for this prediction.
    /// </summary>
    public decimal? McDropoutVariance    { get; set; }

    /// <summary>
    /// Mean predicted probability from the N MC-Dropout forward passes.
    /// May differ slightly from <see cref="ConfidenceScore"/> due to Jensen's inequality
    /// (E[sigmoid(z)] ≠ sigmoid(E[z])). Null when MC-Dropout was not used.
    /// </summary>
    public decimal? McDropoutMean        { get; set; }

    // ── Rec #16: Conformal Prediction ─────────────────────────────────────────

    /// <summary>
    /// The nonconformity score (1 − ŷ_{true class}) for this prediction,
    /// computed once the true outcome is known. Used post-hoc to audit whether
    /// the coverage guarantee was met.
    /// </summary>
    public double?  ConformalNonConformityScore { get; set; }

    /// <summary>
    /// Foreign key to the conformal calibration record whose threshold/prediction-set
    /// semantics were active when this prediction was served. Null for legacy rows.
    /// </summary>
    public long? MLConformalCalibrationId { get; set; }

    /// <summary>
    /// Prediction-time conformal nonconformity threshold used to form the served
    /// prediction set. This is the canonical threshold for later coverage auditing.
    /// </summary>
    public double? ConformalThresholdUsed { get; set; }

    /// <summary>
    /// Prediction-time target coverage level, e.g. 0.90 for 90% marginal coverage.
    /// </summary>
    public double? ConformalTargetCoverageUsed { get; set; }

    /// <summary>
    /// JSON array of class labels included in the served conformal prediction set.
    /// Examples: <c>["Buy"]</c>, <c>["Sell"]</c>, <c>["Buy","Sell"]</c>, or <c>[]</c>.
    /// </summary>
    public string? ConformalPredictionSetJson { get; set; }

    /// <summary>
    /// Canonical realised conformal coverage result. Set when the true outcome is known:
    /// <c>true</c> when the served prediction set contained the actual direction.
    /// </summary>
    public bool? WasConformalCovered { get; set; }

    // ── Rec #19: Approximate SHAP feature attribution ─────────────────────────

    /// <summary>
    /// JSON array of approximate SHAP values for all features, computed as
    /// <c>featureImportance[j] × standardisedFeature[j]</c>. This is a fast linear
    /// approximation — not true permutation or kernel SHAP — so values may
    /// understate non-linear interaction effects.
    /// Format: [0.042, -0.031, 0.007, ...] — one value per feature, matching FeatureNames order.
    /// For top-feature attribution, see <see cref="ContributionsJson"/>.
    /// </summary>
    public string?  ShapValuesJson       { get; set; }

    /// <summary>
    /// JSON array of the raw feature vector supplied to the model at prediction time,
    /// after schema dispatch and replayable interaction-feature appends, but before
    /// standardisation, model-specific transforms, and feature masking. This gives
    /// post-hoc diagnostics workers an exact train/inference parity input instead of
    /// inferring interactions from SHAP proxy values.
    /// </summary>
    public string?  RawFeaturesJson      { get; set; }

    // ── Rec #21: Quantile regression ──────────────────────────────────────────

    /// <summary>
    /// 10th-percentile magnitude prediction in pips (downside estimate).
    /// From the quantile regression head trained with pinball loss at τ=0.1.
    /// Null when quantile regression is not enabled for this model.
    /// </summary>
    public decimal? MagnitudeP10Pips     { get; set; }

    /// <summary>
    /// 90th-percentile magnitude prediction in pips (upside estimate).
    /// From the quantile regression head trained with pinball loss at τ=0.9.
    /// Null when quantile regression is not enabled for this model.
    /// </summary>
    public decimal? MagnitudeP90Pips     { get; set; }

    // ── Rec #23: OOD detection ────────────────────────────────────────────────

    /// <summary>
    /// Mahalanobis distance from the incoming feature vector to the training distribution.
    /// Scores above 3σ are flagged as out-of-distribution.
    /// Null when OOD detection is not enabled for this model.
    /// </summary>
    public double?  OodMahalanobisScore  { get; set; }

    /// <summary>
    /// <c>true</c> when <see cref="OodMahalanobisScore"/> exceeded the 3σ threshold,
    /// indicating the model should not be trusted for this prediction.
    /// </summary>
    public bool     IsOod                { get; set; }

    // ── Rec #24: Regime-gated routing ─────────────────────────────────────────

    /// <summary>
    /// The routing decision made at inference time.
    /// "Global" = global model used. "Regime:Trending" = regime-specific model used.
    /// "Fallback" = fell back to champion after regime model unavailable.
    /// Null for predictions made before regime routing was introduced.
    /// </summary>
    public string?  RegimeRoutingDecision { get; set; }

    // ── Rec #38: Survival analysis / time-to-target estimate ──────────────────

    /// <summary>
    /// Estimated number of bars until the profit target is reached, as predicted by the
    /// Cox proportional-hazards survival model (<see cref="MLSurvivalModel"/>).
    /// Null when no survival model is active for this symbol/timeframe.
    /// </summary>
    public double? EstimatedTimeToTargetBars { get; set; }

    /// <summary>
    /// Instantaneous hazard rate λ(t=0) from the Cox survival model at prediction time.
    /// Higher values indicate the target is likely to be reached sooner.
    /// Null when no survival model is active.
    /// </summary>
    public double? SurvivalHazardRate        { get; set; }

    // ── Improvement #1: Ensemble scoring committee ────────────────────────

    /// <summary>
    /// JSON array of model IDs that contributed to this prediction as committee members.
    /// Format: <c>[42, 87, 103]</c>. Null when single-model scoring was used.
    /// </summary>
    public string? CommitteeModelIdsJson  { get; set; }

    /// <summary>
    /// Standard deviation of committee member probabilities (0.0–0.5).
    /// Higher values indicate disagreement among committee members.
    /// Null when single-model scoring was used.
    /// </summary>
    public decimal? CommitteeDisagreement { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted             { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The trade signal for which this prediction was made.</summary>
    public virtual TradeSignal TradeSignal { get; set; } = null!;

    /// <summary>The ML model that produced this prediction.</summary>
    public virtual MLModel MLModel { get; set; } = null!;

    /// <summary>Conformal calibration active when this prediction was served, if recorded.</summary>
    public virtual MLConformalCalibration? MLConformalCalibration { get; set; }
}
