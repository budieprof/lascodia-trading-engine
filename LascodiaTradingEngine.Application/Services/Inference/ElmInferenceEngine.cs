using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for ELM (Extreme Learning Machine) models.
/// Each learner has random fixed input→hidden weights with a configurable activation
/// (Sigmoid/Tanh/ReLU) and an analytically-solved output layer.
/// Learner outputs are aggregated via stacking meta-learner or uniform averaging.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class ElmInferenceEngine : IModelInferenceEngine
{
    private const double DropoutRate = 0.1;

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "elm"
        && snapshot.ElmInputWeights is { Length: > 0 }
        && snapshot.ElmHiddenDim > 0;

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        if (snapshot.Weights.Length == 0 || snapshot.ElmInputWeights is null)
            return null;

        int K = snapshot.Weights.Length;
        int hiddenDim = snapshot.ElmHiddenDim;
        var probs = new double[K];

        for (int k = 0; k < K; k++)
        {
            probs[k] = ElmLearnerProb(
                features, snapshot.Weights[k], snapshot.Biases[k],
                snapshot.ElmInputWeights[k],
                snapshot.ElmInputBiases is { Length: > 0 } && k < snapshot.ElmInputBiases.Length
                    ? snapshot.ElmInputBiases[k] : [],
                featureCount, hiddenDim,
                snapshot.FeatureSubsetIndices?.Length > k ? snapshot.FeatureSubsetIndices[k] : null,
                GetActivation(snapshot.LearnerActivations, k));
        }

        double avg = InferenceHelpers.AggregateProbs(
            probs, K, snapshot.MetaWeights, snapshot.MetaBias,
            snapshot.EnsembleSelectionWeights.Length > 0 ? snapshot.EnsembleSelectionWeights : null,
            snapshot.LearnerCalAccuracies.Length > 0 ? snapshot.LearnerCalAccuracies : null);

        double variance = 0;
        for (int k = 0; k < K; k++) { double d = probs[k] - avg; variance += d * d; }
        double std = K > 1 ? Math.Sqrt(variance / (K - 1)) : 0.0;

        decimal? mcMean = null, mcVar = null;
        if (mcDropoutSamples > 0)
            (mcMean, mcVar) = ComputeMcDropout(features, snapshot, featureCount, mcDropoutSamples, mcDropoutSeed);

        return new InferenceResult(avg, std, mcMean, mcVar);
    }

    private static double ElmLearnerProb(
        float[] features, double[] wOut, double bias,
        double[] wIn, double[] bIn,
        int featureCount, int hiddenSize, int[]? subset,
        ElmActivation activation)
    {
        int subLen = subset?.Length > 0 ? subset.Length : featureCount;
        double score = bias;

        for (int h = 0; h < hiddenSize; h++)
        {
            double z = h < bIn.Length ? bIn[h] : 0.0;
            int rowOff = h * subLen;

            if (subset is { Length: > 0 })
            {
                for (int si = 0; si < subLen && rowOff + si < wIn.Length; si++)
                {
                    int fi = subset[si];
                    if (fi < features.Length)
                        z += wIn[rowOff + si] * features[fi];
                }
            }
            else
            {
                int len = Math.Min(subLen, features.Length);
                for (int si = 0; si < len && rowOff + si < wIn.Length; si++)
                    z += wIn[rowOff + si] * features[si];
            }

            double hAct = Activate(z, activation);
            if (h < wOut.Length)
                score += wOut[h] * hAct;
        }

        return MLFeatureHelper.Sigmoid(score);
    }

    private static double Activate(double z, ElmActivation activation) => activation switch
    {
        ElmActivation.Sigmoid => MLFeatureHelper.Sigmoid(z),
        ElmActivation.Tanh    => Math.Tanh(z),
        ElmActivation.Relu    => Math.Max(0.0, z),
        _                     => MLFeatureHelper.Sigmoid(z),
    };

    private static ElmActivation GetActivation(int[]? learnerActivations, int k)
    {
        if (learnerActivations is null || k >= learnerActivations.Length)
            return ElmActivation.Sigmoid;
        return (ElmActivation)learnerActivations[k];
    }

    private static (decimal Mean, decimal Variance) ComputeMcDropout(
        float[] features, ModelSnapshot snap, int featureCount, int numSamples, int seed)
    {
        var rng = new Random(seed);
        int K = snap.Weights.Length;
        int hiddenDim = snap.ElmHiddenDim;
        var samples = new double[numSamples];

        for (int s = 0; s < numSamples; s++)
        {
            var masked = new float[featureCount];
            double scale = 1.0 / (1.0 - DropoutRate);
            for (int j = 0; j < featureCount; j++)
                masked[j] = rng.NextDouble() >= DropoutRate ? (float)(features[j] * scale) : 0f;

            var probs = new double[K];
            for (int k = 0; k < K; k++)
            {
                probs[k] = ElmLearnerProb(
                    masked, snap.Weights[k], snap.Biases[k],
                    snap.ElmInputWeights![k],
                    snap.ElmInputBiases is { Length: > 0 } && k < snap.ElmInputBiases.Length
                        ? snap.ElmInputBiases[k] : [],
                    featureCount, hiddenDim,
                    snap.FeatureSubsetIndices?.Length > k ? snap.FeatureSubsetIndices[k] : null,
                    GetActivation(snap.LearnerActivations, k));
            }

            samples[s] = InferenceHelpers.AggregateProbs(
                probs, K, snap.MetaWeights, snap.MetaBias, null, null);
        }

        double mean = samples.Average();
        double var2 = 0;
        for (int s = 0; s < numSamples; s++) { double d = samples[s] - mean; var2 += d * d; }
        var2 /= numSamples > 1 ? numSamples - 1 : 1;

        return ((decimal)mean, (decimal)var2);
    }
}
