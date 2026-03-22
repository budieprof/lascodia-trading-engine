using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Result returned by an inference engine after running a model forward pass.
/// </summary>
/// <param name="Probability">Raw Buy-class probability before calibration (0..1).</param>
/// <param name="EnsembleStd">Inter-learner standard deviation (0 for single-output models like TCN).</param>
/// <param name="McDropoutMean">Mean probability across MC-Dropout samples. Null when MC-Dropout is disabled.</param>
/// <param name="McDropoutVariance">Variance across MC-Dropout samples. Null when MC-Dropout is disabled.</param>
public readonly record struct InferenceResult(
    double   Probability,
    double   EnsembleStd,
    decimal? McDropoutMean     = null,
    decimal? McDropoutVariance = null);

/// <summary>
/// Encapsulates model-type-specific inference: forward pass + MC-Dropout uncertainty estimation.
/// Implementations are resolved at runtime based on <see cref="CanHandle"/>.
/// </summary>
public interface IModelInferenceEngine
{
    /// <summary>
    /// Returns <c>true</c> when this engine can run inference for the given snapshot.
    /// Exactly one engine should return true for any valid snapshot.
    /// </summary>
    bool CanHandle(ModelSnapshot snapshot);

    /// <summary>
    /// Runs the model forward pass and optional MC-Dropout uncertainty estimation.
    /// Returns null when the snapshot weights are invalid or inference cannot proceed.
    /// </summary>
    InferenceResult? RunInference(
        float[]        features,
        int            featureCount,
        ModelSnapshot  snapshot,
        List<Candle>   candleWindow,
        long           modelId,
        int            mcDropoutSamples,
        int            mcDropoutSeed);
}
