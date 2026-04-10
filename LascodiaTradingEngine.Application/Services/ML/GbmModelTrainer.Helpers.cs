using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>Serialisable node in a GBM regression tree.</summary>
public sealed class GbmNode
{
    public bool   IsLeaf         { get; set; }
    public double LeafValue      { get; set; }
    public int    SplitFeature   { get; set; }
    public double SplitThreshold { get; set; }
    public int    LeftChild      { get; set; } = -1;
    public int    RightChild     { get; set; } = -1;
    /// <summary>Split gain at this node (for gain-weighted importance).</summary>
    public double SplitGain      { get; set; }
}

/// <summary>Serialisable regression tree used by <see cref="GbmModelTrainer"/>.</summary>
public sealed class GbmTree
{
    public List<GbmNode>? Nodes { get; set; }
}

public sealed partial class GbmModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  PREDICTION HELPERS (Item 6: loop guard)
    // ═══════════════════════════════════════════════════════════════════════

    private static double Predict(GbmTree tree, float[] features)
    {
        if (tree.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        int maxIter = tree.Nodes.Count + 1; // Item 6: loop guard
        int iter = 0;
        while (nodeIdx >= 0 && nodeIdx < tree.Nodes.Count && iter++ < maxIter)
        {
            var node = tree.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < features.Length && features[node.SplitFeature] <= node.SplitThreshold)
                nodeIdx = node.LeftChild;
            else
                nodeIdx = node.RightChild;
        }
        return 0;
    }

    private static double GetTreeLearningRate(int treeIndex, double defaultLearningRate, IReadOnlyList<double>? perTreeLearningRates)
    {
        if (perTreeLearningRates is null || treeIndex < 0 || treeIndex >= perTreeLearningRates.Count)
            return defaultLearningRate;

        double treeLearningRate = perTreeLearningRates[treeIndex];
        return double.IsFinite(treeLearningRate) && treeLearningRate > 0.0
            ? treeLearningRate
            : defaultLearningRate;
    }

    private static double GbmScore(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double score = baseLogOdds;
        for (int ti = 0; ti < trees.Count; ti++)
            score += GetTreeLearningRate(ti, lr, perTreeLearningRates) * Predict(trees[ti], features);
        return score;
    }

    private static double GbmProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, IReadOnlyList<double>? perTreeLearningRates = null)
        => Sigmoid(GbmScore(features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates));

    private static double GbmCalibProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds, double lr,
        int featureCount, ModelSnapshot? calibrationSnapshot = null,
        IReadOnlyList<double>? perTreeLearningRates = null)
    {
        double rawP = Math.Clamp(
            GbmProb(features, trees, baseLogOdds, lr, featureCount, perTreeLearningRates),
            1e-7, 1.0 - 1e-7);

        if (calibrationSnapshot is null)
            return rawP;

        return InferenceHelpers.ApplyDeployedCalibration(rawP, calibrationSnapshot);
    }

    private static double GbmCalibProb(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds,
        double lr, double plattA, double plattB, int featureCount)
        => GbmCalibProb(
            features, trees, baseLogOdds, lr, featureCount,
            CreateCalibrationSnapshot(new GbmCalibrationState(
                plattA,
                plattB,
                0.0,
                0.0,
                0.0,
                0.0,
                0.0,
                0.5,
                [])));

    internal static double? ComputeRawProbabilityFromSnapshotForAudit(float[] features, ModelSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Type, ModelType, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(snapshot.GbmTreesJson))
        {
            return null;
        }

        ModelSnapshot normalized = GbmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        List<GbmTree>? trees;
        try
        {
            trees = JsonSerializer.Deserialize<List<GbmTree>>(normalized.GbmTreesJson!, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (trees is not { Count: > 0 })
            return null;

        int featureCount = normalized.Features.Length > 0
            ? normalized.Features.Length
            : features.Length;
        double learningRate = normalized.GbmLearningRate > 0.0
            ? normalized.GbmLearningRate
            : 0.1;
        return GbmProb(features, trees, normalized.GbmBaseLogOdds, learningRate, featureCount, normalized.GbmPerTreeLearningRates);
    }

    private static double ComputeEnsembleStd(
        float[] features, IReadOnlyList<GbmTree> trees, double baseLogOdds,
        double lr, IReadOnlyList<double>? perTreeLearningRates = null)
    {
        if (trees.Count <= 1)
            return 0.0;

        double score = baseLogOdds;
        var treeProbs = new double[trees.Count];
        for (int ti = 0; ti < trees.Count; ti++)
        {
            double treeLearningRate = GetTreeLearningRate(ti, lr, perTreeLearningRates);
            double leafValue = Predict(trees[ti], features);
            score += treeLearningRate * leafValue;
            treeProbs[ti] = Sigmoid(baseLogOdds + treeLearningRate * leafValue);
        }

        double rawProb = Sigmoid(score);
        double variance = 0.0;
        for (int ti = 0; ti < trees.Count; ti++)
        {
            double diff = treeProbs[ti] - rawProb;
            variance += diff * diff;
        }

        return Math.Sqrt(variance / (trees.Count - 1));
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
    private static double Logit(double p) => Math.Log(p / (1.0 - p));

    private static (float[] Means, float[] Stds) ComputeStandardizationFromSamples(
        IReadOnlyList<TrainingSample> samples)
    {
        var rawFeatures = new List<float[]>(samples.Count);
        foreach (var sample in samples)
            rawFeatures.Add(sample.Features);
        return MLFeatureHelper.ComputeStandardization(rawFeatures);
    }

    private static List<TrainingSample> StandardizeSamples(
        IReadOnlyList<TrainingSample> samples,
        float[] means,
        float[] stds)
    {
        if (samples.Count == 0)
            return [];

        var standardized = new List<TrainingSample>(samples.Count);
        foreach (var sample in samples)
        {
            standardized.Add(sample with
            {
                Features = MLFeatureHelper.Standardize(sample.Features, means, stds),
            });
        }

        return standardized;
    }

    private static FeatureTransformDescriptor CloneFeatureTransformDescriptor(FeatureTransformDescriptor descriptor)
    {
        return new FeatureTransformDescriptor
        {
            Kind = descriptor.Kind,
            Version = descriptor.Version,
            Operation = descriptor.Operation,
            InputFeatureCount = descriptor.InputFeatureCount,
            OutputStartIndex = descriptor.OutputStartIndex,
            OutputCount = descriptor.OutputCount,
            SourceIndexGroups = descriptor.SourceIndexGroups
                .Select(group => (int[])group.Clone())
                .ToArray(),
        };
    }

    private static List<TrainingSample> ApplyFeatureTransforms(
        List<TrainingSample> samples, IReadOnlyList<FeatureTransformDescriptor> descriptors)
    {
        if (samples.Count == 0 || descriptors.Count == 0)
            return samples;

        return samples.Select(sample =>
        {
            var features = (float[])sample.Features.Clone();
            foreach (var descriptor in descriptors)
            {
                FeaturePipelineTransformSupport.TryApplyInPlace(features, descriptor);
            }
            return sample with { Features = features };
        }).ToList();
    }

    private static string[] BuildSnapshotFeatureNames(int featureCount)
    {
        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    private static bool[] BuildAllTrueMask(int featureCount)
    {
        var mask = new bool[featureCount];
        Array.Fill(mask, true);
        return mask;
    }

    private static int ComputeTrainingRandomSeed(
        string featureSchemaFingerprint,
        string trainerFingerprint,
        int sampleCount)
    {
        string payload = $"gbm-seed-v1|{featureSchemaFingerprint}|{trainerFingerprint}|{sampleCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        int seed = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return seed == 0 ? 1 : seed;
    }

    private static Random CreateSeededRandom(int baseSeed, int salt)
    {
        int seed = baseSeed != 0
            ? unchecked((baseSeed * 16777619) ^ salt)
            : salt;
        if (seed == 0)
            seed = 1;
        return new Random(seed);
    }

    private static List<TrainingSample> ApplyFeatureMask(List<TrainingSample> samples, bool[] mask)
    {
        if (samples.Count == 0 || mask.Length == 0)
            return samples;

        return samples.Select(sample =>
        {
            var features = (float[])sample.Features.Clone();
            for (int j = 0; j < features.Length && j < mask.Length; j++)
            {
                if (!mask[j])
                    features[j] = 0f;
            }
            return sample with { Features = features };
        }).ToList();
    }

    private static int SanitizeTrees(List<GbmTree> trees)
    {
        int count = 0;
        foreach (var tree in trees)
        {
            if (tree.Nodes is null) continue;
            bool needsSanitize = false;
            foreach (var node in tree.Nodes)
                if (!double.IsFinite(node.LeafValue) || !double.IsFinite(node.SplitThreshold)) { needsSanitize = true; break; }
            if (needsSanitize)
            {
                foreach (var node in tree.Nodes)
                {
                    if (!double.IsFinite(node.LeafValue)) node.LeafValue = 0;
                    if (!double.IsFinite(node.SplitThreshold)) node.SplitThreshold = 0;
                    node.IsLeaf = true;
                }
                count++;
            }
        }
        return count;
    }

    /// <summary>Item 45: Remove placeholder/unreachable nodes to reduce serialization size.</summary>
    private static void CompactTreeNodes(List<GbmTree> trees)
    {
        foreach (var tree in trees)
        {
            if (tree.Nodes is null || tree.Nodes.Count <= 1) continue;

            // Mark reachable nodes via BFS from root
            var reachable = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                if (idx < 0 || idx >= tree.Nodes.Count) continue;
                var node = tree.Nodes[idx];
                if (node.IsLeaf) continue;
                if (node.LeftChild >= 0 && node.LeftChild < tree.Nodes.Count && reachable.Add(node.LeftChild))
                    queue.Enqueue(node.LeftChild);
                if (node.RightChild >= 0 && node.RightChild < tree.Nodes.Count && reachable.Add(node.RightChild))
                    queue.Enqueue(node.RightChild);
            }

            if (reachable.Count == tree.Nodes.Count) continue; // all reachable

            // Remap indices
            var indexMap = new Dictionary<int, int>();
            var compacted = new List<GbmNode>();
            var sortedReachable = reachable.OrderBy(x => x).ToList();
            for (int i = 0; i < sortedReachable.Count; i++)
                indexMap[sortedReachable[i]] = i;

            foreach (int oldIdx in sortedReachable)
            {
                var node = tree.Nodes[oldIdx];
                var newNode = new GbmNode
                {
                    IsLeaf = node.IsLeaf, LeafValue = node.LeafValue,
                    SplitFeature = node.SplitFeature, SplitThreshold = node.SplitThreshold,
                    SplitGain = node.SplitGain,
                    LeftChild = node.IsLeaf ? -1 : (indexMap.TryGetValue(node.LeftChild, out int lc) ? lc : -1),
                    RightChild = node.IsLeaf ? -1 : (indexMap.TryGetValue(node.RightChild, out int rc) ? rc : -1),
                };
                compacted.Add(newNode);
            }
            tree.Nodes = compacted;
        }
    }

    private void CheckTimeoutBudget(Stopwatch sw, int timeoutMinutes, string phase)
    {
        if (timeoutMinutes <= 0) return;
        if (sw.Elapsed.TotalMinutes > timeoutMinutes)
        {
            _logger.LogWarning("GBM training timeout exceeded ({Elapsed:F1}m > {Budget}m) at {Phase}",
                sw.Elapsed.TotalMinutes, timeoutMinutes, phase);
            throw new OperationCanceledException(
                $"GBM training exceeded {timeoutMinutes}m budget at {phase}");
        }
    }
}
