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
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "elm"
        && snapshot.Weights is { Length: > 0 }
        && snapshot.ElmInputWeights is { Length: > 0 }
        && snapshot.Biases is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        if (snapshot.Weights is not { Length: > 0 } || snapshot.ElmInputWeights is null)
            return null;

        var biases = snapshot.Biases;
        if (biases is null)
            return null;

        int K = Math.Min(
            snapshot.Weights.Length,
            Math.Min(biases.Length, snapshot.ElmInputWeights.Length));
        if (K <= 0)
            return null;

        var probs = new double[K];
        var validLearners = new List<int>(K);

        for (int k = 0; k < K; k++)
        {
            if (snapshot.Weights[k] is not { Length: > 0 } wOut ||
                snapshot.ElmInputWeights[k] is not { Length: > 0 } wIn)
            {
                continue;
            }

            var subset = snapshot.FeatureSubsetIndices?.Length > k ? snapshot.FeatureSubsetIndices[k] : null;
            var inputBiases = snapshot.ElmInputBiases is { Length: > 0 } && k < snapshot.ElmInputBiases.Length
                ? snapshot.ElmInputBiases[k]
                : [];
            int learnerHidden = ResolveLearnerHiddenSize(
                wOut,
                wIn,
                inputBiases,
                subset,
                featureCount,
                snapshot.ElmHiddenDim);

            probs[validLearners.Count] = ClampProbabilityOrNeutral(ElmLearnerProb(
                features, wOut, biases[k],
                wIn,
                inputBiases ?? [],
                featureCount, learnerHidden,
                subset,
                GetActivation(snapshot.LearnerActivations, k)));
            validLearners.Add(k);
        }

        int validCount = validLearners.Count;
        if (validCount == 0)
            return null;

        double avg = ClampProbabilityOrNeutral(InferenceHelpers.AggregateProbs(
            probs, validCount,
            SelectLearnerValues(snapshot.MetaWeights, validLearners), snapshot.MetaBias,
            SelectLearnerValues(snapshot.EnsembleSelectionWeights, validLearners),
            SelectLearnerValues(snapshot.LearnerAccuracyWeights, validLearners),
            SelectLearnerValues(snapshot.LearnerCalAccuracies, validLearners)));

        double meanProb = 0.0;
        for (int k = 0; k < validCount; k++)
            meanProb += probs[k];
        meanProb /= validCount;

        double variance = 0;
        for (int k = 0; k < validCount; k++) { double d = probs[k] - meanProb; variance += d * d; }
        double std = validCount > 1 && double.IsFinite(variance) ? Math.Sqrt(variance / (validCount - 1)) : 0.0;

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
                    if (fi >= 0 && fi < features.Length)
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
        if (learnerActivations is null || learnerActivations.Length == 0)
            return ElmActivation.Sigmoid;
        return (ElmActivation)(k < learnerActivations.Length
            ? learnerActivations[k]
            : learnerActivations[0]);
    }

    private static (decimal Mean, decimal Variance) ComputeMcDropout(
        float[] features, ModelSnapshot snap, int featureCount, int numSamples, int seed)
    {
        var rng = new Random(seed);
        var biases = snap.Biases;
        if (biases is null)
            return (0m, 0m);

        int K = Math.Min(
            snap.Weights.Length,
            Math.Min(biases.Length, snap.ElmInputWeights?.Length ?? 0));
        if (K <= 0)
            return (0m, 0m);
        var samples = new double[numSamples];
        double dropoutRate = snap.ElmDropoutRate.HasValue
            ? Math.Clamp(snap.ElmDropoutRate.Value, 0.0, 0.5)
            : 0.1;

        for (int s = 0; s < numSamples; s++)
        {
            var probs = new double[K];
            var validLearners = new List<int>(K);
            for (int k = 0; k < K; k++)
            {
                if (snap.Weights[k] is not { Length: > 0 } wOut ||
                    snap.ElmInputWeights![k] is not { Length: > 0 } wIn)
                {
                    continue;
                }

                var subset = snap.FeatureSubsetIndices?.Length > k ? snap.FeatureSubsetIndices[k] : null;
                var inputBiases = snap.ElmInputBiases is { Length: > 0 } && k < snap.ElmInputBiases.Length
                    ? snap.ElmInputBiases[k]
                    : [];
                int learnerHidden = ResolveLearnerHiddenSize(
                    wOut,
                    wIn,
                    inputBiases,
                    subset,
                    featureCount,
                    snap.ElmHiddenDim);
                probs[validLearners.Count] = ClampProbabilityOrNeutral(ElmLearnerProbWithHiddenDropout(
                    features, wOut, biases[k],
                    wIn,
                    inputBiases ?? [],
                    featureCount, learnerHidden,
                    subset,
                    GetActivation(snap.LearnerActivations, k),
                    rng,
                    dropoutRate));
                validLearners.Add(k);
            }

            int validCount = validLearners.Count;
            if (validCount == 0)
            {
                samples[s] = 0.5;
                continue;
            }

            samples[s] = ClampProbabilityOrNeutral(InferenceHelpers.AggregateProbs(
                probs, validCount,
                SelectLearnerValues(snap.MetaWeights, validLearners), snap.MetaBias,
                SelectLearnerValues(snap.EnsembleSelectionWeights, validLearners),
                SelectLearnerValues(snap.LearnerAccuracyWeights, validLearners),
                SelectLearnerValues(snap.LearnerCalAccuracies, validLearners)));
        }

        double mean = samples.Average();
        double var2 = 0;
        for (int s = 0; s < numSamples; s++) { double d = samples[s] - mean; var2 += d * d; }
        var2 /= numSamples > 1 ? numSamples - 1 : 1;

        if (!double.IsFinite(mean))
            mean = 0.5;
        if (!double.IsFinite(var2) || var2 < 0.0)
            var2 = 0.0;

        return ((decimal)mean, (decimal)var2);
    }

    /// <summary>
    /// ELM learner forward pass with MC dropout applied to hidden units (matching training).
    /// </summary>
    private static double ElmLearnerProbWithHiddenDropout(
        float[] features, double[] wOut, double bias,
        double[] wIn, double[] bIn,
        int featureCount, int hiddenSize, int[]? subset,
        ElmActivation activation, Random rng, double dropoutRate)
    {
        int subLen = subset?.Length > 0 ? subset.Length : featureCount;
        double score = bias;
        double scale = dropoutRate > 0.0 ? 1.0 / (1.0 - dropoutRate) : 1.0;

        for (int h = 0; h < hiddenSize; h++)
        {
            if (dropoutRate > 0.0 && rng.NextDouble() < dropoutRate) continue;

            double z = h < bIn.Length ? bIn[h] : 0.0;
            int rowOff = h * subLen;

            if (subset is { Length: > 0 })
            {
                for (int si = 0; si < subLen && rowOff + si < wIn.Length; si++)
                {
                    int fi = subset[si];
                    if (fi >= 0 && fi < features.Length)
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
                score += wOut[h] * hAct * scale;
        }

        return MLFeatureHelper.Sigmoid(score);
    }

    private static int ResolveLearnerHiddenSize(
        double[] wOut,
        double[] wIn,
        double[]? bIn,
        int[]? subset,
        int featureCount,
        int fallbackHiddenDim)
    {
        int subsetLen = subset?.Length > 0 ? subset.Length : featureCount;
        int hiddenFromWeights = wOut.Length;
        int hiddenFromBiases = bIn?.Length ?? 0;
        int hiddenFromInputs = subsetLen > 0 ? wIn.Length / subsetLen : 0;

        int resolved = hiddenFromWeights;
        if (hiddenFromBiases > 0)
            resolved = Math.Min(resolved, hiddenFromBiases);
        if (hiddenFromInputs > 0)
            resolved = Math.Min(resolved, hiddenFromInputs);

        if (resolved > 0)
            return resolved;

        return fallbackHiddenDim > 0 ? fallbackHiddenDim : hiddenFromWeights;
    }

    private static double[]? SelectLearnerValues(double[]? values, List<int> learnerIndices)
    {
        if (values is not { Length: > 0 } || learnerIndices.Count == 0)
            return null;

        var selected = new double[learnerIndices.Count];
        for (int i = 0; i < learnerIndices.Count; i++)
        {
            int sourceIndex = learnerIndices[i];
            if (sourceIndex < 0 || sourceIndex >= values.Length)
                return null;
            selected[i] = values[sourceIndex];
        }

        return selected;
    }

    private static double ClampProbabilityOrNeutral(double probability)
    {
        if (!double.IsFinite(probability))
            return 0.5;

        return Math.Clamp(probability, 0.0, 1.0);
    }
}
