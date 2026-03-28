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
/// Inference engine for QRF (Quantile Random Forest) models.
/// Deserialises GbmTree JSON, traverses each tree to a leaf, and aggregates
/// leaf-fraction probabilities. Caches parsed trees in <see cref="IMemoryCache"/>.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class QrfInferenceEngine : IModelInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private const string TreesCacheKeyPrefix = "MLQrfTrees:";
    private static readonly TimeSpan TreesCacheDuration = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;

    public QrfInferenceEngine(IMemoryCache cache) => _cache = cache;

    public bool CanHandle(ModelSnapshot snapshot) =>
        snapshot.Type == "quantilerf"
        && snapshot.GbmTreesJson is { Length: > 0 };

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var (avg, std) = QrfForestProb(features, snapshot, modelId);
        // MC-Dropout is not supported for QRF models
        return new InferenceResult(avg, std);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // QRF tree-forest inference
    // ═══════════════════════════════════════════════════════════════════════════

    private (double AvgProb, double StdProb) QrfForestProb(
        float[] features, ModelSnapshot snap, long modelId)
    {
        var treesCacheKey = $"{TreesCacheKeyPrefix}{modelId}";
        List<GbmTree>? trees;
        if (modelId > 0 && _cache.TryGetValue<List<GbmTree>>(treesCacheKey, out var cachedTrees))
        {
            trees = cachedTrees;
        }
        else
        {
            try
            {
                trees = JsonSerializer.Deserialize<List<GbmTree>>(snap.GbmTreesJson!, JsonOptions);
            }
            catch
            {
                return (0.5, 0.0);
            }

            if (modelId > 0 && trees is { Count: > 0 })
                _cache.Set(treesCacheKey, trees, TreesCacheDuration);
        }

        if (trees is not { Count: > 0 }) return (0.5, 0.0);

        int T = trees.Count;
        var probs = new double[T];

        for (int t = 0; t < T; t++)
        {
            var nodes = trees[t].Nodes;
            if (nodes is not { Count: > 0 }) { probs[t] = 0.5; continue; }

            int nodeIdx = 0;
            double leafVal = 0.5;
            while (nodeIdx >= 0 && nodeIdx < nodes.Count)
            {
                var node = nodes[nodeIdx];
                if (node.IsLeaf || node.SplitFeature < 0 || node.SplitFeature >= features.Length)
                {
                    leafVal = node.LeafValue;
                    break;
                }
                nodeIdx = features[node.SplitFeature] <= (float)node.SplitThreshold
                    ? node.LeftChild
                    : node.RightChild;
            }
            probs[t] = double.IsFinite(leafVal) ? leafVal : 0.5;
        }

        double avg = InferenceHelpers.AggregateProbs(probs, T, snap.MetaWeights, snap.MetaBias,
            snap.EnsembleSelectionWeights,
            snap.LearnerAccuracyWeights is { Length: > 0 } ? snap.LearnerAccuracyWeights : null,
            snap.LearnerCalAccuracies);

        double variance = 0.0;
        for (int t = 0; t < T; t++) { double d = probs[t] - avg; variance += d * d; }
        double std = T > 1 ? Math.Sqrt(variance / (T - 1)) : 0.0;

        return (avg, std);
    }
}
