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
/// Inference engine for GBM (Gradient Boosting Machine) models.
/// Deserialises the GBM tree ensemble from <see cref="ModelSnapshot.GbmTreesJson"/>,
/// traverses each tree to a leaf, and sums predictions with learning-rate scaling
/// on top of a base log-odds intercept.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class GbmInferenceEngine : IModelInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string TreesCacheKeyPrefix = "MLGbmTrees:";
    private static readonly TimeSpan TreesCacheDuration = TimeSpan.FromMinutes(30);

    private const double DefaultLearningRate = 0.1;

    private readonly IMemoryCache _cache;

    public GbmInferenceEngine(IMemoryCache cache) => _cache = cache;

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "GBM"
        && snapshot.GbmTreesJson is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var trees = GetOrParseTrees(snapshot, modelId);
        if (trees is not { Count: > 0 })
            return null;

        double lr = snapshot.GbmLearningRate > 0
            ? snapshot.GbmLearningRate
            : DefaultLearningRate;

        double score = snapshot.GbmBaseLogOdds;
        var treeProbs = new double[trees.Count];

        for (int t = 0; t < trees.Count; t++)
        {
            double leafVal = Predict(trees[t], features);
            score += lr * leafVal;
            treeProbs[t] = MLFeatureHelper.Sigmoid(snapshot.GbmBaseLogOdds + lr * leafVal);
        }

        double rawProb = MLFeatureHelper.Sigmoid(score);

        double variance = 0;
        for (int t = 0; t < trees.Count; t++)
        {
            double d = treeProbs[t] - rawProb;
            variance += d * d;
        }
        double std = trees.Count > 1 ? Math.Sqrt(variance / (trees.Count - 1)) : 0.0;

        return new InferenceResult(rawProb, std);
    }

    private static double Predict(GbmTree tree, float[] features)
    {
        if (tree.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        while (nodeIdx >= 0 && nodeIdx < tree.Nodes.Count)
        {
            var node = tree.Nodes[nodeIdx];
            if (node.IsLeaf || node.SplitFeature < 0 || node.SplitFeature >= features.Length)
                return node.LeafValue;
            nodeIdx = features[node.SplitFeature] <= (float)node.SplitThreshold
                ? node.LeftChild
                : node.RightChild;
        }
        return 0;
    }

    private List<GbmTree>? GetOrParseTrees(ModelSnapshot snap, long modelId)
    {
        var cacheKey = $"{TreesCacheKeyPrefix}{modelId}";
        if (modelId > 0 && _cache.TryGetValue<List<GbmTree>>(cacheKey, out var cached))
            return cached;

        List<GbmTree>? trees;
        try
        {
            trees = JsonSerializer.Deserialize<List<GbmTree>>(snap.GbmTreesJson!, JsonOptions);
        }
        catch
        {
            return null;
        }

        if (modelId > 0 && trees is { Count: > 0 })
            _cache.Set(cacheKey, trees, TreesCacheDuration);

        return trees;
    }
}
