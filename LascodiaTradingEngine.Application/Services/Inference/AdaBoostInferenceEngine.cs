using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for AdaBoost models.
/// Deserialises decision stumps from <see cref="ModelSnapshot.GbmTreesJson"/> and
/// alpha weights from <see cref="ModelSnapshot.Weights"/>[0], then computes the
/// weighted stump vote: sigmoid(2 × Σ alpha_k × stump_k(x)).
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class AdaBoostInferenceEngine : IModelInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string StumpsCacheKeyPrefix = "MLAdaBoostStumps:";
    private static readonly TimeSpan StumpsCacheDuration = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;

    public AdaBoostInferenceEngine(IMemoryCache cache) => _cache = cache;

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "AdaBoost"
        && snapshot.GbmTreesJson is { Length: > 0 }
        && snapshot.Weights is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var stumps = GetOrParseStumps(snapshot, modelId);
        if (stumps is not { Count: > 0 })
            return null;

        double[] alphas = snapshot.Weights[0];
        if (alphas.Length == 0)
            return null;

        int count = Math.Min(stumps.Count, alphas.Length);
        double score = 0;
        var perStumpProbs = new double[count];

        for (int k = 0; k < count; k++)
        {
            double stumpVal = PredictStump(stumps[k], features);
            score += alphas[k] * stumpVal;
            perStumpProbs[k] = MLFeatureHelper.Sigmoid(2 * alphas[k] * stumpVal);
        }

        double rawProb = MLFeatureHelper.Sigmoid(2 * score);

        double variance = 0;
        for (int k = 0; k < count; k++)
        {
            double d = perStumpProbs[k] - rawProb;
            variance += d * d;
        }
        double std = count > 1 ? Math.Sqrt(variance / (count - 1)) : 0.0;

        return new InferenceResult(rawProb, std);
    }

    private static double PredictStump(GbmTree stump, float[] features)
    {
        if (stump.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        while (true)
        {
            var node = stump.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < 0 || node.SplitFeature >= features.Length) return 0;
            int nextIdx = features[node.SplitFeature] <= node.SplitThreshold
                ? node.LeftChild
                : node.RightChild;
            if (nextIdx < 0 || nextIdx >= stump.Nodes.Count) return 0;
            nodeIdx = nextIdx;
        }
    }

    private List<GbmTree>? GetOrParseStumps(ModelSnapshot snap, long modelId)
    {
        var cacheKey = $"{StumpsCacheKeyPrefix}{modelId}";
        if (modelId > 0 && _cache.TryGetValue<List<GbmTree>>(cacheKey, out var cached))
            return cached;

        List<GbmTree>? stumps;
        try
        {
            stumps = JsonSerializer.Deserialize<List<GbmTree>>(snap.GbmTreesJson!, JsonOptions);
        }
        catch
        {
            return null;
        }

        if (modelId > 0 && stumps is { Count: > 0 })
            _cache.Set(cacheKey, stumps, StumpsCacheDuration);

        return stumps;
    }
}
