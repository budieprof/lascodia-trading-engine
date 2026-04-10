using System.Security.Cryptography;
using System.Text;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using static TorchSharp.torch;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    // ── Prediction helpers ─────────────────────────────────────────────────────

    private static double PredictScore(float[] features, List<GbmTree> stumps, List<double> alphas)
    {
        double score = 0;
        int    count = Math.Min(stumps.Count, alphas.Count);
        for (int k = 0; k < count; k++)
            score += alphas[k] * PredictStump(stumps[k], features);
        return score;
    }

    private static double PredictRawProb(float[] features, List<GbmTree> stumps, List<double> alphas)
        => Math.Clamp(MLFeatureHelper.Sigmoid(2 * PredictScore(features, stumps, alphas)), 1e-7, 1.0 - 1e-7);

    private static double ComputeEnsembleStd(float[] features, List<GbmTree> stumps, List<double> alphas)
    {
        int count = Math.Min(stumps.Count, alphas.Count);
        if (count <= 1)
            return 0.0;

        double score = 0.0;
        var perStumpProbs = new double[count];
        for (int k = 0; k < count; k++)
        {
            double stumpVal = PredictStump(stumps[k], features);
            score += alphas[k] * stumpVal;
            perStumpProbs[k] = MLFeatureHelper.Sigmoid(2 * alphas[k] * stumpVal);
        }

        double rawProb = Math.Clamp(MLFeatureHelper.Sigmoid(2 * score), 1e-7, 1.0 - 1e-7);
        double variance = 0.0;
        for (int k = 0; k < count; k++)
        {
            double diff = perStumpProbs[k] - rawProb;
            variance += diff * diff;
        }

        return Math.Sqrt(variance / (count - 1));
    }

    /// <summary>
    /// Traverses a base-learner tree of arbitrary depth and returns the leaf value.
    /// For SAMME stumps the leaf value is ±1; for SAMME.R it is ½·logit(p_leaf).
    /// Handles depth-1 (stump) and depth-2 trees transparently.
    /// Returns 0 on any structural anomaly (null nodes, out-of-bounds feature index).
    /// </summary>
    private static double PredictStump(GbmTree stump, float[] features)
    {
        if (stump.Nodes is not { Count: > 0 }) return 0;
        int nodeIdx = 0;
        while (true)
        {
            var node = stump.Nodes[nodeIdx];
            if (node.IsLeaf) return node.LeafValue;
            if (node.SplitFeature < 0 || node.SplitFeature >= features.Length) return 0;
            bool goLeft  = features[node.SplitFeature] <= node.SplitThreshold;
            int  nextIdx = goLeft ? node.LeftChild : node.RightChild;
            if (nextIdx < 0 || nextIdx >= stump.Nodes.Count) return 0;
            nodeIdx = nextIdx;
        }
    }

    /// <summary>
    /// Full AdaBoost probability pipeline:
    /// raw margin → deployed global calibration (temperature or Platt) →
    /// optional class-conditional Platt → optional isotonic correction.
    /// </summary>
    private static double PredictProb(
        float[]       features,
        List<GbmTree> stumps,
        List<double>  alphas,
        double        plattA,
        double        plattB,
        double        temperatureScale,
        double[]?     isotonicBp = null,
        double        decisionThreshold = 0.5,
        double        plattABuy  = double.NaN,
        double        plattBBuy  = double.NaN,
        double        plattASell = double.NaN,
        double        plattBSell = double.NaN,
        double        routingThreshold = DefaultConditionalRoutingThreshold)
    {
        _ = decisionThreshold;
        double rawProb = PredictRawProb(features, stumps, alphas);
        return PredictProbFromRaw(
            rawProb, plattA, plattB, temperatureScale, isotonicBp,
            plattABuy, plattBBuy, plattASell, plattBSell, routingThreshold);
    }

    private static double PredictProbFromRaw(
        double    rawProb,
        double    plattA,
        double    plattB,
        double    temperatureScale,
        double[]? isotonicBp = null,
        double    plattABuy  = double.NaN,
        double    plattBBuy  = double.NaN,
        double    plattASell = double.NaN,
        double    plattBSell = double.NaN,
        double    routingThreshold = DefaultConditionalRoutingThreshold)
    {
        return InferenceHelpers.ApplyDeployedCalibration(
            rawProb,
            plattA,
            plattB,
            temperatureScale,
            plattABuy,
            plattBBuy,
            plattASell,
            plattBSell,
            routingThreshold,
            isotonicBp,
            ageDecayLambda: 0.0,
            trainedAtUtc: default,
            applyAgeDecay: false);
    }

    private static double PredictPreIsotonicProbFromRaw(
        double rawProb,
        double plattA,
        double plattB,
        double temperatureScale,
        double plattABuy  = double.NaN,
        double plattBBuy  = double.NaN,
        double plattASell = double.NaN,
        double plattBSell = double.NaN,
        double routingThreshold = DefaultConditionalRoutingThreshold)
    {
        return PredictProbFromRaw(
            rawProb,
            plattA,
            plattB,
            temperatureScale,
            isotonicBp: null,
            plattABuy,
            plattBBuy,
            plattASell,
            plattBSell,
            routingThreshold);
    }

    // ── Feature pruning helpers ────────────────────────────────────────────────

    private static bool[] BuildFeatureMask(float[] importance, double threshold, int F)
    {
        var mask = new bool[F];
        if (threshold <= 0.0 || F == 0) { Array.Fill(mask, true); return mask; }
        double minImp = threshold / F;
        for (int j = 0; j < F; j++) mask[j] = importance[j] >= minImp;
        return mask;
    }

    private static List<TrainingSample> ApplyMask(List<TrainingSample> samples, bool[] mask)
    {
        var result = new List<TrainingSample>(samples.Count);
        foreach (var s in samples)
        {
            var f = (float[])s.Features.Clone();
            for (int j = 0; j < f.Length && j < mask.Length; j++)
                if (!mask[j]) f[j] = 0f;
            result.Add(s with { Features = f });
        }
        return result;
    }

    // ── Stationarity gate (lag-1 Pearson correlation as ADF proxy) ────────────

    private static int CountNonStationaryFeatures(List<TrainingSample> samples, int F)
    {
        int nonStat = 0;
        int n       = samples.Count;
        if (n < 3) return 0;

        for (int fi = 0; fi < F; fi++)
        {
            // |ρ₁| > 0.97 is a conservative proxy for a unit root (I(1) process)
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            int    nc   = n - 1;
            for (int i = 0; i < nc; i++)
            {
                double x = samples[i].Features[fi];
                double y = samples[i + 1].Features[fi];
                sumX  += x; sumY  += y;
                sumXY += x * y;
                sumX2 += x * x; sumY2 += y * y;
            }
            double varX  = sumX2 - sumX * sumX / nc;
            double varY  = sumY2 - sumY * sumY / nc;
            double denom = Math.Sqrt(Math.Max(0, varX * varY));
            double rho   = denom > 1e-12 ? (sumXY - sumX * sumY / nc) / denom : 0;
            if (Math.Abs(rho) > 0.97) nonStat++;
        }
        return nonStat;
    }

    // ── Deterministic seeding helpers ─────────────────────────────────────────

    private static int ComputeTrainingRandomSeed(
        string featureSchemaFingerprint,
        string trainerFingerprint,
        int sampleCount)
    {
        string payload = $"adaboost-seed-v1|{featureSchemaFingerprint}|{trainerFingerprint}|{sampleCount}";
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

    private static void TrySeedTorch(int trainingRandomSeed)
    {
        long seed = trainingRandomSeed > 0 ? trainingRandomSeed : 1;
        try
        {
            manual_seed(seed);
        }
        catch
        {
            // Best-effort deterministic seeding. Failure should not block training.
        }
    }

    // ── Pruned-tree feature validation ────────────────────────────────────────

    private static bool TreeUsesOnlyActiveFeatures(GbmTree tree, bool[] activeMask)
    {
        if (tree.Nodes is not { Count: > 0 })
            return false;

        foreach (var node in tree.Nodes)
        {
            if (node.IsLeaf)
                continue;

            if (node.SplitFeature < 0 || node.SplitFeature >= activeMask.Length || !activeMask[node.SplitFeature])
                return false;
        }

        return true;
    }

    private static bool IsPrunedModelAcceptable(
        EvalMetrics baseMetrics,
        EvalMetrics prunedMetrics,
        double      baseEce,
        double      prunedEce,
        double      baseBrierSkillScore,
        double      prunedBrierSkillScore)
    {
        const double accuracyTolerance = 0.01;
        const double evTolerance = 0.005;
        const double sharpeTolerance = 0.05;
        const double brierTolerance = 0.005;
        const double eceTolerance = 0.01;
        const double bssTolerance = 0.02;

        if (prunedMetrics.Accuracy + accuracyTolerance < baseMetrics.Accuracy)
            return false;
        if (prunedMetrics.ExpectedValue + evTolerance < baseMetrics.ExpectedValue)
            return false;
        if (prunedMetrics.SharpeRatio + sharpeTolerance < baseMetrics.SharpeRatio)
            return false;
        if (prunedMetrics.BrierScore > baseMetrics.BrierScore + brierTolerance)
            return false;
        if (prunedEce > baseEce + eceTolerance)
            return false;
        if (prunedBrierSkillScore + bssTolerance < baseBrierSkillScore)
            return false;

        return true;
    }

    // ── Snapshot array sanitization ───────────────────────────────────────────

    private static void SanitizeSnapshotArrays(ModelSnapshot snapshot)
    {
        SanitizeFloatArr(snapshot.Means);
        SanitizeFloatArr(snapshot.Stds);
        SanitizeDoubleArr(snapshot.MagWeights);
        SanitizeDoubleArr(snapshot.IsotonicBreakpoints);
        SanitizeDoubleArr(snapshot.MetaLabelWeights);
        SanitizeDoubleArr(snapshot.AbstentionWeights);
        SanitizeDoubleArr(snapshot.MagQ90Weights);
        SanitizeDoubleArr(snapshot.JackknifeResiduals);
        SanitizeDoubleArr(snapshot.FeatureImportanceScores);
        SanitizeDoubleArr(snapshot.FeatureVariances);
        SanitizeDoubleArr(snapshot.ReliabilityBinConfidence);
        SanitizeDoubleArr(snapshot.ReliabilityBinAccuracy);
        SanitizeFloatArr(snapshot.FeatureImportance);
        if (snapshot.Weights is not null)
            foreach (var w in snapshot.Weights) SanitizeDoubleArr(w);
        if (snapshot.FeatureQuantileBreakpoints is not null)
            foreach (var bp in snapshot.FeatureQuantileBreakpoints) SanitizeDoubleArr(bp);
    }

    private static void SanitizeDoubleArr(double[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++)
            if (!double.IsFinite(arr[i])) arr[i] = 0.0;
    }

    private static void SanitizeFloatArr(float[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++)
            if (!float.IsFinite(arr[i])) arr[i] = 0f;
    }

    private static void SanitizeIntArr(int[]? arr)
    {
        if (arr is null) return;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] < 0) arr[i] = 0;
    }

    private static double SafeDouble(double v, double fallback = 0.0)
        => double.IsFinite(v) ? v : fallback;
}
