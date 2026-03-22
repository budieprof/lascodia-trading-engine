using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for DANN (Domain-Adversarial Neural Network) models.
/// Reconstructs the feature extractor (2-layer FC with ReLU) and label classifier
/// from <see cref="ModelSnapshot.DannWeights"/>.
/// The domain classifier is not used at inference time.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class DannInferenceEngine : IModelInferenceEngine
{
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "DANN"
        && snapshot.DannWeights is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var dw = snapshot.DannWeights!;

        // Reconstruct dimensions from the packed DannWeights layout:
        // [0..featDim-1]    = feature extractor layer 1 rows (each has F+1 elements: W + bias)
        // [featDim..2*featDim-1] = feature extractor layer 2 rows (each has featDim+1)
        // [2*featDim]       = label classifier row (featDim+1)
        // [2*featDim+1..]   = domain classifier rows (not used at inference)
        //
        // Detect featDim: layer 1 rows have (F+1) elements, layer 2 rows have (featDim+1).
        // We know F = featureCount, so we find the first row where length != F+1.
        int featDim = 0;
        for (int r = 0; r < dw.Length; r++)
        {
            if (dw[r].Length != featureCount + 1)
            {
                featDim = r;
                break;
            }
        }

        if (featDim <= 0 || 2 * featDim >= dw.Length)
            return null;

        // Layer 1: F → featDim (ReLU)
        var h1 = new double[featDim];
        for (int j = 0; j < featDim; j++)
        {
            double pre = dw[j][featureCount]; // bias at end
            for (int fi = 0; fi < featureCount && fi < features.Length; fi++)
                pre += dw[j][fi] * features[fi];
            h1[j] = Math.Max(0.0, pre); // ReLU
        }

        // Layer 2: featDim → featDim (ReLU)
        var h2 = new double[featDim];
        for (int j = 0; j < featDim; j++)
        {
            int row = featDim + j;
            double pre = dw[row][featDim]; // bias at end
            for (int k = 0; k < featDim; k++)
                pre += dw[row][k] * h1[k];
            h2[j] = Math.Max(0.0, pre); // ReLU
        }

        // Label classifier: featDim → 1 (sigmoid)
        int clsRow = 2 * featDim;
        double logit = dw[clsRow][featDim]; // bias at end
        for (int j = 0; j < featDim; j++)
            logit += dw[clsRow][j] * h2[j];

        double rawProb = MLFeatureHelper.Sigmoid(logit);

        return new InferenceResult(rawProb, 0.0);
    }
}
