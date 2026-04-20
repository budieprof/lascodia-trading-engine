using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for stacked meta-learner snapshots produced by <c>StackedModelTrainer</c>.
/// Assumes features arriving here have already been standardised by the common
/// <c>ElmFeaturePipelineHelper.PrepareSnapshotFeatures</c> pipeline using the snapshot's
/// <see cref="ModelSnapshot.Means"/>/<see cref="ModelSnapshot.Stds"/>.
///
/// <para>
/// Forward pass:
/// <list type="number">
///   <item>For each family sub-model: project standardised features onto the family's raw
///     indices, compute <c>sigmoid(w·x + b)</c> to obtain the family Buy-probability.</item>
///   <item>Feed the K family probabilities into the meta logistic regression to obtain
///     the stacked Buy-probability.</item>
/// </list>
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class StackedInferenceEngine : IModelInferenceEngine
{
    public bool CanHandle(ModelSnapshot snapshot) =>
        StackedSnapshotSupport.IsStacked(snapshot)
        && !string.IsNullOrEmpty(snapshot.StackedMetaJson);

    public InferenceResult? RunInference(
        float[] features,
        int featureCount,
        ModelSnapshot snapshot,
        List<Candle> candleWindow,
        long modelId,
        int mcDropoutSamples,
        int mcDropoutSeed)
    {
        var artifact = StackedSnapshotSupport.TryDeserialize(snapshot.StackedMetaJson);
        if (artifact is null || artifact.SubModels.Length == 0 || artifact.MetaWeights.Length == 0)
            return null;

        if (artifact.SubModels.Length != artifact.MetaWeights.Length)
            return null;

        var subProbs = new double[artifact.SubModels.Length];
        for (int f = 0; f < artifact.SubModels.Length; f++)
        {
            var sub = artifact.SubModels[f];
            if (sub.Weights.Length != sub.FeatureIndices.Length)
                return null;

            double z = sub.Bias;
            for (int j = 0; j < sub.FeatureIndices.Length; j++)
            {
                int idx = sub.FeatureIndices[j];
                if (idx < 0 || idx >= features.Length)
                    return null;
                z += sub.Weights[j] * features[idx];
            }
            subProbs[f] = StackedSnapshotSupport.Sigmoid(z);
        }

        double metaZ = artifact.MetaBias;
        for (int f = 0; f < subProbs.Length; f++)
            metaZ += artifact.MetaWeights[f] * subProbs[f];

        double probability = Math.Clamp(StackedSnapshotSupport.Sigmoid(metaZ), 1e-7, 1.0 - 1e-7);

        double mean = 0;
        for (int f = 0; f < subProbs.Length; f++) mean += subProbs[f];
        mean /= subProbs.Length;
        double variance = 0;
        for (int f = 0; f < subProbs.Length; f++)
        {
            double d = subProbs[f] - mean;
            variance += d * d;
        }
        double std = subProbs.Length > 1 ? Math.Sqrt(variance / (subProbs.Length - 1)) : 0.0;

        return new InferenceResult(probability, std, ModelSpaceValues: subProbs);
    }
}
