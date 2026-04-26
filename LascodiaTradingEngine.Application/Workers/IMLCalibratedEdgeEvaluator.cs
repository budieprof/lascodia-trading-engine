using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Pure-functional live-edge math + signal/severity classification, extracted from
/// <see cref="MLCalibratedEdgeWorker"/> as an injectable collaborator so the math
/// can be unit-tested without spinning up the worker. The default implementation is
/// <see cref="MLCalibratedEdgeEvaluator"/>.
/// </summary>
public interface IMLCalibratedEdgeEvaluator
{
    /// <summary>
    /// Computes the per-window live-edge summary (mean EV, win rate, mean probability
    /// gap, mean magnitude, bootstrap-derived EV stderr). Pure function — no DI, no DB.
    /// <paramref name="bootstrapResamples"/> = 0 short-circuits the stderr computation
    /// (returns 0). <paramref name="modelId"/> is mixed into the FNV-1a deterministic
    /// seed so two models with identical sample boundaries don't share an RNG sequence.
    /// </summary>
    LiveEdgeSummary ComputeSummary(
        IReadOnlyList<CalibratedEdgeSample> samples,
        int bootstrapResamples,
        long modelId);

    /// <summary>
    /// Classifies the alert state. Critical when <c>EV + K·stderr ≤ 0</c> (the
    /// K-sigma significance gate prevents a single noisy window from tripping
    /// Critical on a model that's actually break-even). Warning when EV is below
    /// the configured warning floor. Otherwise None.
    /// </summary>
    MLCalibratedEdgeAlertState ResolveAlertState(
        double expectedValuePips,
        double evStderr,
        double warnExpectedValuePips,
        double regressionGuardK);

    /// <summary>
    /// Maps the alert state to an <see cref="AlertSeverity"/> using the realised EV
    /// against the warning floor as the graduation criterion.
    /// </summary>
    AlertSeverity DetermineSeverity(
        MLCalibratedEdgeAlertState alertState,
        LiveEdgeSummary summary,
        double warnExpectedValuePips);

    /// <summary>
    /// True when the prediction log carries enough information (an exact served or
    /// calibrated probability, OR an exact logged threshold) to reconstruct the
    /// served edge honestly. Confidence-only legacy logs return false.
    /// </summary>
    bool IsEdgeInformative(MLModelPredictionLog log);

    /// <summary>
    /// Reduces a prediction log to the evaluator-input shape. Returns false when the
    /// log doesn't carry the information needed (no actual direction or magnitude).
    /// </summary>
    bool TryCreateSample(MLModelPredictionLog log, out CalibratedEdgeSample sample);
}
