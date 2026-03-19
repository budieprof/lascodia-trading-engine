using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Generates a counterfactual explanation for an ML prediction: the minimal change
/// to the input feature vector that would flip the prediction direction.
/// </summary>
/// <remarks>
/// Uses constrained gradient ascent (projected gradient descent on the negative loss)
/// starting from the original feature vector. The search terminates when the model
/// assigns probability ≥ 0.5 to the opposite direction, subject to a maximum
/// per-feature perturbation budget (default ±2σ from the training distribution).
/// The resulting delta is formatted as a JSON object for storage in
/// <c>MLModelPredictionLog.CounterfactualJson</c>.
/// </remarks>
public interface ICounterfactualExplainer
{
    /// <summary>
    /// Computes the minimal feature perturbation required to flip the predicted direction.
    /// </summary>
    /// <param name="features">
    /// Normalised feature vector for the prediction (same values used at scoring time).
    /// </param>
    /// <param name="predictedDirection">The direction predicted by the model.</param>
    /// <param name="modelSnapshot">
    /// The serialised model snapshot used to evaluate candidate perturbations.
    /// </param>
    /// <param name="featureNames">
    /// Ordered feature names parallel to <paramref name="features"/>
    /// (from <c>MLFeatureHelper.FeatureNames</c>).
    /// </param>
    /// <param name="maxPerFeatureSigma">
    /// Maximum allowed perturbation per feature in standard-deviation units.
    /// Default 2.0 to prevent unrealistic counterfactuals.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A JSON string e.g. <c>{"RSI":"+12.3","ATR_norm":"-0.08"}</c> showing only
    /// features with non-negligible delta (|Δ| > 0.01σ). Returns <c>null</c> when no
    /// flip is achievable within the perturbation budget.
    /// </returns>
    Task<string?> ExplainAsync(
        float[]           features,
        TradeDirection    predictedDirection,
        ModelSnapshot     modelSnapshot,
        string[]          featureNames,
        double            maxPerFeatureSigma = 2.0,
        CancellationToken ct                = default);
}
