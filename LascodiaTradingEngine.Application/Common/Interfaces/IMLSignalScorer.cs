using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Comprehensive ML scoring output for a trade signal, including predicted direction,
/// magnitude, confidence, uncertainty estimates, and explainability artifacts.
/// </summary>
public record MLScoreResult(
    TradeDirection? PredictedDirection,
    decimal?        PredictedMagnitudePips,
    decimal?        ConfidenceScore,
    long?           MLModelId,
    /// <summary>
    /// Standard deviation of individual ensemble learner probabilities.
    /// 0 = full agreement; 0.5 = maximum disagreement.
    /// Logged to <c>MLModelPredictionLog.EnsembleDisagreement</c> for drift monitoring.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        EnsembleDisagreement = null,
    /// <summary>
    /// JSON array of the top-3 SHAP feature contributions for this prediction.
    /// Format: [{"Feature":"Rsi","Value":0.042},{"Feature":"AtrNorm","Value":-0.031}, ...]
    /// Computed as φ_j = w̄_j × x_j (ensemble-averaged linear SHAP).
    /// Stored in <c>MLModelPredictionLog.ContributionsJson</c> for per-trade explainability.
    /// Null when no active model scored the signal.
    /// </summary>
    string?         ContributionsJson = null,
    /// <summary>
    /// Half-Kelly optimal fraction of capital to risk on this signal.
    /// f* = max(0, 2p − 1) × 0.5, where p is the Platt-calibrated probability.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        KellyFraction = null,
    /// <summary>
    /// Conformal prediction set at 90% marginal coverage.
    /// "Buy" — model is confident in the Buy direction.
    /// "Sell" — model is confident in the Sell direction.
    /// "Ambiguous" — both directions are plausible given the uncertainty (widen spread / skip).
    /// Null when no conformal threshold is available in the snapshot.
    /// </summary>
    string?         ConformalSet = null,
    /// <summary>
    /// Meta-label model confidence that the primary prediction is correct.
    /// Values below the snapshot's MetaLabelThreshold indicate the primary signal
    /// should be filtered out. Null when meta-labeling is not available.
    /// </summary>
    decimal?        MetaLabelScore = null,
    /// <summary>
    /// Jackknife+ prediction interval as a formatted string "±X.Xpips@90%".
    /// Provides a per-sample adaptive uncertainty band around the magnitude prediction.
    /// Null when Jackknife+ residuals are not available in the snapshot.
    /// </summary>
    string?         JackknifeInterval = null,
    /// <summary>
    /// Abstention gate score P(tradeable environment) from the abstention classifier.
    /// When this value is below <c>ModelSnapshot.AbstentionThreshold</c>, the signal
    /// should be suppressed (no trade). Null when the abstention gate is not available.
    /// </summary>
    decimal?        AbstentionScore = null,
    /// <summary>
    /// Integer encoding of the conformal prediction set size for direct use as a filter:
    ///   0 = "None" — conformal not available or prediction set is empty (skip trade).
    ///   1 = "Buy" or "Sell" — confident single-class prediction (proceed).
    ///   2 = "Ambiguous" — both labels are plausible (widen spread / skip trade).
    /// Null when no conformal threshold is available in the snapshot.
    /// </summary>
    int?            ConformalSetSize = null,
    /// <summary>Conformal calibration record active when this prediction was served.</summary>
    long?           MLConformalCalibrationId = null,
    /// <summary>Prediction-time conformal threshold used to create the served prediction set.</summary>
    double?         ConformalThresholdUsed = null,
    /// <summary>Prediction-time conformal target coverage.</summary>
    double?         ConformalTargetCoverageUsed = null,
    /// <summary>JSON array containing the labels in the served conformal prediction set.</summary>
    string?         ConformalPredictionSetJson = null,
    /// <summary>
    /// Binary prediction entropy H = −p·log₂(p) − (1−p)·log₂(1−p), normalised to [0, 1].
    /// 0.0 = certain prediction (p→0 or p→1); 1.0 = maximum uncertainty (p = 0.5).
    /// <c>StrategyWorker</c> can skip signals with H above a threshold to filter low-conviction trades.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        EntropyScore = null,

    // ── Rec #10: Heteroscedastic magnitude ────────────────────────────────────
    /// <summary>
    /// ±1σ uncertainty bound around <see cref="PredictedMagnitudePips"/> in pips.
    /// Derived from the heteroscedastic output head (models both μ and σ²).
    /// Null when heteroscedastic prediction is disabled for this model.
    /// </summary>
    decimal?        MagnitudeUncertaintyPips = null,

    // ── Rec #11: Monte Carlo Dropout ─────────────────────────────────────────
    /// <summary>
    /// Variance of predicted probability across N MC-Dropout stochastic forward passes.
    /// High variance = high epistemic uncertainty. Null when MC-Dropout is disabled.
    /// </summary>
    decimal?        McDropoutVariance = null,

    /// <summary>
    /// Mean predicted probability across N MC-Dropout stochastic forward passes.
    /// Null when MC-Dropout is disabled.
    /// </summary>
    decimal?        McDropoutMean = null,

    // ── Rec #8: Counterfactual explanations ───────────────────────────────────
    /// <summary>
    /// JSON object describing the minimal feature perturbation to flip this prediction.
    /// E.g. {"RSI":"+12.3","ATR_norm":"-0.08"}. Null when not computed.
    /// </summary>
    string?         CounterfactualJson = null,

    // ── Rec #19: Approximate SHAP ─────────────────────────────────────────────
    /// <summary>
    /// JSON array of approximate SHAP values for all features, computed as
    /// <c>featureImportance[j] × standardisedFeature[j]</c>. This is a fast linear
    /// approximation — not true permutation or kernel SHAP — so values may
    /// understate non-linear interaction effects. Format: [0.042, -0.031, ...].
    /// For top-feature attribution with non-linear weighting, see <see cref="ContributionsJson"/>.
    /// </summary>
    string?         ShapValuesJson = null,

    // ── Rec #21: Quantile regression ─────────────────────────────────────────
    /// <summary>10th-percentile magnitude estimate in pips (downside / pessimistic).</summary>
    decimal?        MagnitudeP10Pips = null,
    /// <summary>90th-percentile magnitude estimate in pips (upside / optimistic).</summary>
    decimal?        MagnitudeP90Pips = null,

    // ── Rec #23: OOD detection ────────────────────────────────────────────────
    /// <summary>
    /// Mahalanobis distance from the feature vector to the training distribution.
    /// Scores > 3.0 indicate out-of-distribution input; signal should be suppressed.
    /// </summary>
    double?         OodMahalanobisScore = null,
    /// <summary><c>true</c> when the input is flagged as out-of-distribution.</summary>
    bool            IsOod = false,

    // ── Rec #24: Regime-gated routing ─────────────────────────────────────────
    /// <summary>
    /// Routing decision string e.g. "Regime:Trending", "Global", "Fallback".
    /// </summary>
    string?         RegimeRoutingDecision = null,

    // ── Rec #35: MinT reconciled probability ─────────────────────────────────
    /// <summary>
    /// MinT-reconciled Buy probability blending H1, H4, D1 model outputs.
    /// Null when fewer than 2 timeframe models are available.
    /// </summary>
    decimal?        MinTReconciledProbability = null,

    // ── Rec #38: Survival analysis ────────────────────────────────────────────
    /// <summary>
    /// Estimated bars until the profit target is reached (Cox survival model).
    /// Null when no survival model is active for this symbol/timeframe.
    /// </summary>
    double?         EstimatedTimeToTargetBars = null,

    /// <summary>Instantaneous hazard rate from the Cox model. Higher = sooner arrival.</summary>
    double?         SurvivalHazardRate = null,

    /// <summary>
    /// Raw Buy-class probability emitted by the base inference engine before calibration.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        RawProbability = null,

    /// <summary>
    /// Base model's calibrated Buy-class probability before any post-calibration
    /// stacking/meta blending is applied.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        CalibratedProbability = null,

    /// <summary>
    /// Effective calibrated Buy-class probability that drove the live trade decision
    /// after all scorer-side blending, including stacking meta when active.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        ServedCalibratedProbability = null,

    /// <summary>
    /// Effective Buy-threshold applied at scoring time after regime/adaptive overrides.
    /// Null when no active model scored the signal.
    /// </summary>
    decimal?        DecisionThresholdUsed = null,

    // ── Improvement #1: Ensemble scoring committee ─────────────────────────
    /// <summary>
    /// JSON array of model IDs that contributed to this prediction as committee members.
    /// Format: <c>[42, 87, 103]</c>. Null when single-model scoring was used.
    /// </summary>
    string?         CommitteeModelIdsJson = null,
    /// <summary>
    /// Standard deviation of committee member probabilities (0.0–0.5).
    /// Higher values indicate disagreement among committee members.
    /// Null when single-model scoring was used.
    /// </summary>
    decimal?        CommitteeDisagreement = null,
    /// <summary>
    /// Raw feature vector supplied to the model before standardisation/masking, serialized
    /// as JSON for post-hoc diagnostics such as feature-interaction discovery.
    /// </summary>
    string?         RawFeaturesJson = null,
    /// <summary>
    /// Role the served model played at scoring time. During signal-level A/B tests this
    /// distinguishes challenger-served predictions from the normal champion path.
    /// </summary>
    ModelRole       ModelRole = ModelRole.Champion);

public interface IMLSignalScorer
{
    /// <summary>
    /// Scores a rule-based signal using the active ML model for the symbol/timeframe.
    /// Returns null fields if no active model exists (signal proceeds unscored).
    /// </summary>
    Task<MLScoreResult> ScoreAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken);

    /// <summary>
    /// Scores a batch of signals. Group-by-model-id batching gives inference engines
    /// (ONNX runtime, TorchSharp bagged logistic) the opportunity to run a single
    /// forward pass over all same-model inputs — typically 2–5× throughput vs N
    /// sequential <see cref="ScoreAsync"/> calls on an active-market tick.
    ///
    /// <para>Default implementation falls back to per-signal scoring so callers can
    /// start using the batch API without breaking existing scorers. Performance
    /// implementations should override.</para>
    /// </summary>
    async Task<IReadOnlyList<MLScoreResult>> ScoreBatchAsync(
        IReadOnlyList<(TradeSignal Signal, IReadOnlyList<Candle> Candles)> batch,
        CancellationToken cancellationToken)
    {
        var results = new MLScoreResult[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = await ScoreAsync(batch[i].Signal, batch[i].Candles, cancellationToken);
        }
        return results;
    }
}
