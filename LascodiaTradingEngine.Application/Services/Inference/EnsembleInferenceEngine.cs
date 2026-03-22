using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for bagged ensemble models (logistic / MLP / poly learners).
/// Handles standard forward pass and MC-Dropout uncertainty estimation.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class EnsembleInferenceEngine : IModelInferenceEngine
{
    private const double DropoutRate = 0.1;

    /// <summary>
    /// Only handles bagged ensemble architectures (BaggedLogistic, SMOTE) that use the
    /// standard logistic/MLP/poly learner forward pass. All other architectures have
    /// dedicated inference engines and are explicitly excluded.
    /// </summary>
    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Weights is { Length: > 0 }
        && snapshot.Type is not ("TCN" or "quantilerf" or "GBM" or "AdaBoost"
            or "TABNET" or "svgp" or "elm" or "DANN" or "ROCKET" or "FTTRANSFORMER");

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        if (snapshot.Weights.Length == 0) return null;

        var (rawProb, ensembleStd) = EnsembleProb(
            features, snapshot.Weights, snapshot.Biases, featureCount,
            snapshot.FeatureSubsetIndices, snapshot.MetaWeights, snapshot.MetaBias,
            snapshot.PolyLearnerStartIndex,
            snapshot.EnsembleSelectionWeights.Length > 0 ? snapshot.EnsembleSelectionWeights : null,
            snapshot.LearnerCalAccuracies.Length > 0 ? snapshot.LearnerCalAccuracies : null,
            snapshot.MlpHiddenWeights, snapshot.MlpHiddenBiases, snapshot.MlpHiddenDim);

        decimal? mcMean = null, mcVar = null;
        if (mcDropoutSamples > 0)
        {
            (mcMean, mcVar) = ComputeMcDropout(
                features, snapshot, featureCount, mcDropoutSamples, mcDropoutSeed);
        }

        return new InferenceResult(rawProb, ensembleStd, mcMean, mcVar);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Ensemble forward pass
    // ═══════════════════════════════════════════════════════════════════════════

    internal static (double AvgProb, double StdProb) EnsembleProb(
        float[]    features,
        double[][] weights,
        double[]   biases,
        int        featureCount,
        int[][]?   subsets                  = null,
        double[]?  metaWeights              = null,
        double     metaBias                 = 0.0,
        int        polyLearnerStartIndex    = int.MaxValue,
        double[]?  gesWeights               = null,
        double[]?  learnerCalAccuracies     = null,
        double[][]? mlpHiddenW              = null,
        double[][]? mlpHiddenB              = null,
        int         mlpHiddenDim            = 0)
    {
        if (weights.Length == 0) return (0.5, 0.0);

        bool useMlp = mlpHiddenDim > 0 && mlpHiddenW is not null && mlpHiddenB is not null;
        var probs = new double[weights.Length];
        for (int k = 0; k < weights.Length; k++)
        {
            // ── MLP forward pass ──────────────────────────────────────────────
            if (useMlp && k < mlpHiddenW!.Length && mlpHiddenW[k] is not null &&
                k < mlpHiddenB!.Length && mlpHiddenB[k] is not null)
            {
                var hW = mlpHiddenW[k];
                var hB = mlpHiddenB[k];
                int[] subset = subsets?.Length > k && subsets[k] is { Length: > 0 } s ? s : [];
                int subLen = subset.Length > 0 ? subset.Length : featureCount;
                if (subset.Length == 0)
                {
                    subset = new int[featureCount];
                    for (int j = 0; j < featureCount; j++) subset[j] = j;
                    subLen = featureCount;
                }

                double z = biases[k];
                for (int h = 0; h < mlpHiddenDim; h++)
                {
                    double act = hB[h];
                    int rowOff = h * subLen;
                    for (int si = 0; si < subLen && rowOff + si < hW.Length; si++)
                        act += hW[rowOff + si] * features[subset[si]];
                    double hidden = Math.Max(0.0, act); // ReLU
                    if (h < weights[k].Length)
                        z += weights[k][h] * hidden;
                }
                probs[k] = MLFeatureHelper.Sigmoid(z);
                continue;
            }

            // ── Linear logistic forward pass ──────────────────────────────────
            double zLin = biases[k];

            float[] effectiveFeatures = features;
            if (k >= polyLearnerStartIndex && featureCount >= 5)
            {
                var aug = new float[featureCount + 10];
                Array.Copy(features, aug, featureCount);
                int idx = featureCount;
                for (int a = 0; a < 5; a++)
                    for (int b = a + 1; b < 5; b++)
                        aug[idx++] = features[a] * features[b];
                effectiveFeatures = aug;
            }

            if (subsets?.Length > k && subsets[k] is { Length: > 0 } subset2)
            {
                foreach (int j in subset2)
                {
                    if (j < effectiveFeatures.Length && j < weights[k].Length)
                        zLin += weights[k][j] * effectiveFeatures[j];
                }
            }
            else
            {
                int wLen = Math.Min(weights[k].Length, effectiveFeatures.Length);
                for (int j = 0; j < wLen; j++)
                    zLin += weights[k][j] * effectiveFeatures[j];
            }
            probs[k] = MLFeatureHelper.Sigmoid(zLin);
        }

        double avg = InferenceHelpers.AggregateProbs(probs, weights.Length, metaWeights, metaBias, gesWeights, learnerCalAccuracies);

        // Sample std (N-1)
        int N = probs.Length;
        double variance = 0.0;
        for (int k = 0; k < N; k++) { double d = probs[k] - avg; variance += d * d; }
        double std = N > 1 ? Math.Sqrt(variance / (N - 1)) : 0.0;

        return (avg, std);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MC-Dropout: stochastic forward passes with random feature dropout
    // ═══════════════════════════════════════════════════════════════════════════

    private static (decimal Mean, decimal Variance) ComputeMcDropout(
        float[] features, ModelSnapshot snap, int featureCount, int numSamples, int seed)
    {
        var rng = new Random(seed);

        var samples = new double[numSamples];
        for (int s = 0; s < numSamples; s++)
        {
            var maskedFeatures = new float[featureCount];
            double scale = 1.0 / (1.0 - DropoutRate);
            for (int j = 0; j < featureCount; j++)
                maskedFeatures[j] = rng.NextDouble() >= DropoutRate
                    ? (float)(features[j] * scale)
                    : 0f;

            var (prob, _) = EnsembleProb(maskedFeatures, snap.Weights, snap.Biases, featureCount,
                snap.FeatureSubsetIndices, snap.MetaWeights, snap.MetaBias,
                snap.PolyLearnerStartIndex, null, null,
                snap.MlpHiddenWeights, snap.MlpHiddenBiases, snap.MlpHiddenDim);
            samples[s] = prob;
        }

        double mean = samples.Average();
        double variance = 0.0;
        for (int s = 0; s < numSamples; s++)
        {
            double d = samples[s] - mean;
            variance += d * d;
        }
        variance /= numSamples > 1 ? numSamples - 1 : 1;

        return ((decimal)mean, (decimal)variance);
    }
}
