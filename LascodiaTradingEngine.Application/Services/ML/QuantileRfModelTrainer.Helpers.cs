using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class QuantileRfModelTrainer
{

    // ── Per-tree leaf-fraction probability ───────────────────────────────────

    /// <summary>
    /// #30: Iterative (non-recursive) traversal to the matching leaf. Returns the
    /// fraction of positive (Buy) training samples: LeafPosCount / LeafTotalCount.
    /// Returns 0.5 for degenerate leaves with no samples or out-of-bounds indices.
    /// </summary>
    private static double GetLeafProb(List<TreeNode> nodes, int nodeIndex, float[] features)
    {
        int idx = nodeIndex;
        while (idx >= 0 && idx < nodes.Count)
        {
            var node = nodes[idx];
            if (node.SplitFeat < 0 || node.SplitFeat >= features.Length)
                return node.LeafTotalCount > 0
                    ? (double)node.LeafPosCount / node.LeafTotalCount
                    : 0.5;
            idx = features[node.SplitFeat] <= node.SplitThresh
                ? node.LeftChild
                : node.RightChild;
        }
        return 0.5;
    }

    // ── Raw QRF probability (leaf-fraction, no Platt) ─────────────────────────

    /// <summary>
    /// Average leaf-fraction probability across all trees (uncalibrated).
    /// #47: The <paramref name="trainSet"/> parameter is retained for call-site
    /// compatibility across trainers but is not used — leaf counts are pre-computed
    /// during tree construction and stored in the nodes directly.
    /// </summary>
    private static double PredictRawProb(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)
    {
        if (allTrees.Count == 0) return 0.5;
        double sum = 0.0;
        foreach (var tree in allTrees)
            sum += GetLeafProb(tree, 0, features);
        double prob = sum / allTrees.Count;
        return double.IsFinite(prob) ? prob : 0.5;
    }

    // ── Calibrated probability (Platt + optional isotonic) ────────────────────

    /// <summary>
    /// Produces a calibrated probability: raw QRF leaf-fraction → Platt sigmoid →
    /// optional isotonic PAVA correction. Used for threshold decisions and evaluation.
    /// </summary>
    private static double PredictProb(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet,
        double               plattA,
        double               plattB,
        double[]?            isotonicBp = null)
    {
        double raw = PredictRawProb(features, allTrees, trainSet);
        raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
        double logit  = MLFeatureHelper.Logit(raw);
        double calibP = MLFeatureHelper.Sigmoid(plattA * logit + plattB);
        if (isotonicBp is { Length: >= 4 })
            calibP = ApplyIsotonicCalibration(calibP, isotonicBp);
        return calibP;
    }

    // ── NaN/Inf node sanitization ─────────────────────────────────────────────

    private static int SanitizeTreeNodes(List<List<TreeNode>> allTrees)
    {
        int count = 0;
        foreach (var treeNodes in allTrees)
        {
            foreach (var node in treeNodes)
            {
                if (!double.IsFinite(node.LeafDirection))
                {
                    node.LeafDirection = 0.5;
                    count++;
                }
                if (!double.IsFinite(node.SplitThresh))
                {
                    // Convert to leaf — a split with a non-finite threshold is unusable
                    node.SplitFeat   = -1;
                    node.SplitThresh = 0.0;
                    count++;
                }
            }
        }
        return count;
    }

    // ── Density-ratio importance weights (logistic discriminator) ─────────────

    private static double[] ComputeDensityRatioWeights(
        List<TrainingSample> trainSet, int F, int windowDays)
    {
        int recentCount = Math.Min(trainSet.Count / 3, windowDays * 24);
        if (recentCount < 20) return [.. Enumerable.Repeat(1.0 / trainSet.Count, trainSet.Count)];

        int cutoff = trainSet.Count - recentCount;
        var w      = new double[F];
        double b   = 0;

        // Logistic discriminator: recent = 1, historical = 0
        for (int epoch = 0; epoch < DensityRatioEpochs; epoch++)
        {
            for (int i = 0; i < trainSet.Count; i++)
            {
                double label = i >= cutoff ? 1.0 : 0.0;
                double z     = b;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    z += w[j] * trainSet[i].Features[j];
                double p   = MLFeatureHelper.Sigmoid(z);
                double err = p - label;
                b -= DensityRatioLr * err;
                for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                    w[j] -= DensityRatioLr * err * trainSet[i].Features[j];
            }
        }

        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            double z = b;
            for (int j = 0; j < F && j < trainSet[i].Features.Length; j++)
                z += w[j] * trainSet[i].Features[j];
            double p   = Math.Clamp(MLFeatureHelper.Sigmoid(z), 0.01, 0.99);
            weights[i] = p / (1 - p); // importance ratio
        }

        double wSum = weights.Sum();
        if (wSum > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= wSum;

        return weights;
    }

    // ── Covariate shift weights (parent model novelty scoring) ────────────────

    private static double[] ComputeCovariateShiftWeights(
        List<TrainingSample> trainSet, double[][] parentBp, int F)
    {
        var weights = new double[trainSet.Count];
        for (int i = 0; i < trainSet.Count; i++)
        {
            int outsideCount = 0, checkedCount = 0;
            for (int j = 0; j < F && j < parentBp.Length; j++)
            {
                double[] bp = parentBp[j];
                if (bp.Length < 2) continue;
                checkedCount++;
                double v = trainSet[i].Features[j];
                if (v < bp[0] || v > bp[^1]) outsideCount++;
            }
            double noveltyFraction = checkedCount > 0 ? (double)outsideCount / checkedCount : 0;
            weights[i] = 1.0 + noveltyFraction; // up-weight novel samples
        }

        double mean = weights.Average();
        if (mean > 1e-15)
            for (int i = 0; i < weights.Length; i++) weights[i] /= mean;

        return weights;
    }

    // ── Feature mask + apply ──────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        if (threshold <= 0.0 || F == 0)
        {
            var all = new bool[F];
            Array.Fill(all, true);
            return all;
        }
        double minImportance = threshold / F;
        var mask = new bool[F];
        for (int j = 0; j < F; j++)
            mask[j] = importance[j] >= minImportance;
        return mask;
    }

    /// <summary>
    /// #34: Lightweight mask — skips clone when mask is all-true (no features pruned).
    /// When features are pruned, zeroes masked features in a cloned array.
    /// </summary>
    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        // Fast path: no-op when all features are active
        bool allActive = true;
        foreach (bool m in mask) if (!m) { allActive = false; break; }
        if (allActive) return samples;

        return [.. samples.Select(s =>
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            return s with { Features = f };
        })];
    }

    // ── Weighted bootstrap sampling ───────────────────────────────────────────

    private static int SampleWeighted(Random rng, double[] cumWeights)
    {
        double r   = rng.NextDouble() * cumWeights[^1];
        int    idx = Array.BinarySearch(cumWeights, r);
        return idx < 0 ? Math.Min(~idx, cumWeights.Length - 1) : idx;
    }

    // ── Standard deviation helper ─────────────────────────────────────────────

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double sumSq = 0;
        foreach (double v in values) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    // ── Per-tree probability helpers (#2, #5, #7, #10) ───────────────────────

    /// <summary>Returns an array of T per-tree leaf-fraction probabilities for <paramref name="features"/>.</summary>
    private static double[] GetTreeProbs(
        float[]              features,
        List<List<TreeNode>> allTrees,
        List<TrainingSample> trainSet)   // trainSet retained for call-site compatibility
    {
        int T     = allTrees.Count;
        var probs = new double[T];
        for (int t = 0; t < T; t++)
            probs[t] = GetLeafProb(allTrees[t], 0, features);
        return probs;
    }
}
