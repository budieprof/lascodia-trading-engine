using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for ROCKET (RandOm Convolutional KErnel Transform) models.
/// Applies pre-generated random 1-D dilated convolutions to the feature vector
/// (treated as a time series), extracts max-pool + PPV features per kernel,
/// standardises with stored means/stds, and applies a ridge-regression head.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class RocketInferenceEngine : IModelInferenceEngine
{
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "ROCKET"
        && snapshot.RocketKernelWeights is { Length: > 0 }
        && snapshot.Weights is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var kernelWeights = snapshot.RocketKernelWeights!;
        int numKernels = kernelWeights.Length;

        var dilations = snapshot.RocketKernelDilations;
        var paddings  = snapshot.RocketKernelPaddings;
        var lengths   = snapshot.RocketKernelLengths;

        if (dilations is null || paddings is null || lengths is null)
            return null;

        // Extract ROCKET features: 2 per kernel (max-pool + PPV)
        var rocketFeatures = new double[2 * numKernels];
        int F = featureCount;

        for (int k = 0; k < numKernels; k++)
        {
            double[] w   = kernelWeights[k];
            int      len = k < lengths.Length ? lengths[k] : w.Length;
            int      dil = k < dilations.Length ? dilations[k] : 1;
            bool     pad = k < paddings.Length && paddings[k];

            int padding   = pad ? (len - 1) * dil / 2 : 0;
            int outputLen = F + 2 * padding - (len - 1) * dil;

            double maxVal  = double.MinValue;
            int    ppvPos  = 0;
            int    posCount = 0;

            for (int pos = 0; pos < outputLen; pos++)
            {
                double dot = 0;
                for (int j = 0; j < len; j++)
                {
                    int srcIdx = pos + j * dil - padding;
                    double xVal = (srcIdx >= 0 && srcIdx < F) ? features[srcIdx] : 0;
                    dot += w[j] * xVal;
                }
                if (dot > maxVal) maxVal = dot;
                if (dot > 0) ppvPos++;
                posCount++;
            }

            rocketFeatures[k]              = maxVal == double.MinValue ? 0 : maxVal;
            rocketFeatures[numKernels + k] = posCount > 0 ? (double)ppvPos / posCount : 0;
        }

        // Standardise using training-time statistics
        if (snapshot.RocketFeatureMeans is { Length: > 0 } means &&
            snapshot.RocketFeatureStds is { Length: > 0 } stds)
        {
            int dim = Math.Min(rocketFeatures.Length, Math.Min(means.Length, stds.Length));
            for (int j = 0; j < dim; j++)
            {
                double s = stds[j] > 1e-8 ? stds[j] : 1.0;
                rocketFeatures[j] = (rocketFeatures[j] - means[j]) / s;
            }
        }

        // Ridge regression head: logit = w · rocketFeatures + bias
        double[] headW = snapshot.Weights[0];
        double headBias = snapshot.Biases.Length > 0 ? snapshot.Biases[0] : 0.0;
        int headDim = Math.Min(headW.Length, rocketFeatures.Length);

        double logit = headBias;
        for (int j = 0; j < headDim; j++)
            logit += headW[j] * rocketFeatures[j];

        double rawProb = MLFeatureHelper.Sigmoid(logit);

        return new InferenceResult(rawProb, 0.0);
    }
}
