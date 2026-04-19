using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class ElmFeaturePipelineHelper
{
    internal readonly record struct PreparedSamples(
        List<TrainingSample> Samples,
        float[] Means,
        float[] Stds,
        float[] WinsorizeLowerBounds,
        float[] WinsorizeUpperBounds);

    internal readonly record struct SnapshotPreparedFeatures(
        float[] RawFeatures,
        float[] Features,
        int FeatureCount,
        float[] Means,
        float[] Stds);

    internal static PreparedSamples PrepareTrainingSamples(
        IReadOnlyList<TrainingSample> samples,
        int featureCount,
        int trainSampleCountForStats,
        double winsorizePercentile,
        double fracDiffD)
    {
        var cloned = CloneSamples(samples);
        trainSampleCountForStats = Math.Clamp(trainSampleCountForStats, 0, cloned.Count);

        var (winsorLowerBounds, winsorUpperBounds) = ComputeWinsorizationBounds(
            cloned,
            featureCount,
            trainSampleCountForStats,
            winsorizePercentile);

        if (winsorLowerBounds.Length > 0)
        {
            for (int i = 0; i < cloned.Count; i++)
            {
                var features = (float[])cloned[i].Features.Clone();
                ApplyWinsorizationInPlace(features, winsorLowerBounds, winsorUpperBounds);
                cloned[i] = cloned[i] with { Features = features };
            }
        }

        var rawTrainFeatures = new List<float[]>(trainSampleCountForStats);
        for (int i = 0; i < trainSampleCountForStats && i < cloned.Count; i++)
            rawTrainFeatures.Add(cloned[i].Features);

        var (means, stds) = MLFeatureHelper.ComputeStandardization(rawTrainFeatures);

        var standardised = new List<TrainingSample>(cloned.Count);
        for (int i = 0; i < cloned.Count; i++)
        {
            standardised.Add(cloned[i] with
            {
                Features = MLFeatureHelper.Standardize(cloned[i].Features, means, stds),
            });
        }

        if (fracDiffD > 0.0)
            standardised = MLFeatureHelper.ApplyFractionalDifferencing(standardised, featureCount, fracDiffD);

        return new PreparedSamples(standardised, means, stds, winsorLowerBounds, winsorUpperBounds);
    }

    internal static List<TrainingSample> CloneSamples(IReadOnlyList<TrainingSample> samples)
    {
        var cloned = new List<TrainingSample>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
            cloned.Add(samples[i] with { Features = (float[])samples[i].Features.Clone() });
        return cloned;
    }

    internal static (float[] LowerBounds, float[] UpperBounds) ComputeWinsorizationBounds(
        IReadOnlyList<TrainingSample> samples,
        int featureCount,
        int trainSampleCount,
        double winsorizePercentile)
    {
        if (winsorizePercentile <= 0.0 || trainSampleCount <= 0 || featureCount <= 0)
            return ([], []);

        trainSampleCount = Math.Clamp(trainSampleCount, 0, samples.Count);
        if (trainSampleCount == 0)
            return ([], []);

        var lower = new float[featureCount];
        var upper = new float[featureCount];

        for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
        {
            var values = new float[trainSampleCount];
            for (int sampleIndex = 0; sampleIndex < trainSampleCount; sampleIndex++)
                values[sampleIndex] = samples[sampleIndex].Features[featureIndex];

            Array.Sort(values);
            int loIdx = Math.Clamp((int)(winsorizePercentile * trainSampleCount), 0, trainSampleCount - 1);
            int hiIdx = Math.Clamp((int)((1.0 - winsorizePercentile) * trainSampleCount), 0, trainSampleCount - 1);
            lower[featureIndex] = values[loIdx];
            upper[featureIndex] = values[hiIdx];
        }

        return (lower, upper);
    }

    internal static void ApplyWinsorizationInPlace(float[] features, float[] lowerBounds, float[] upperBounds)
    {
        int width = Math.Min(features.Length, Math.Min(lowerBounds.Length, upperBounds.Length));
        for (int featureIndex = 0; featureIndex < width; featureIndex++)
            features[featureIndex] = Math.Clamp(features[featureIndex], lowerBounds[featureIndex], upperBounds[featureIndex]);
    }

    internal static float[] CloneAndWinsorize(float[] features, ModelSnapshot snapshot)
    {
        var cloned = (float[])features.Clone();
        if (snapshot.ElmWinsorizeLowerBounds is { Length: > 0 } lowerBounds &&
            snapshot.ElmWinsorizeUpperBounds is { Length: > 0 } upperBounds)
        {
            ApplyWinsorizationInPlace(cloned, lowerBounds, upperBounds);
        }

        return cloned;
    }

    internal static float[] StandardiseFeatures(float[] rawFeatures, float[] means, float[] stds, int featureCount)
    {
        float[] features = new float[featureCount];
        for (int j = 0; j < featureCount && j < rawFeatures.Length; j++)
        {
            if (!float.IsFinite(rawFeatures[j]))
            {
                features[j] = 0f;
                continue;
            }

            float std = j < stds.Length && stds[j] > 1e-8f ? stds[j] : 1f;
            float mean = j < means.Length ? means[j] : 0f;
            features[j] = (rawFeatures[j] - mean) / std;
        }

        return features;
    }

    internal static float[] ProjectFeaturesByRawIndex(float[] features, int[] rawFeatureIndices)
    {
        if (rawFeatureIndices.Length == 0)
            return features;

        if (rawFeatureIndices.Distinct().Count() != rawFeatureIndices.Length)
            throw new InvalidOperationException("RawFeatureIndices contains duplicate indices.");

        var projected = new float[rawFeatureIndices.Length];
        for (int i = 0; i < rawFeatureIndices.Length; i++)
        {
            int rawIndex = rawFeatureIndices[i];
            if (rawIndex < 0 || rawIndex >= features.Length)
            {
                throw new InvalidOperationException(
                    $"RawFeatureIndices[{i}]={rawIndex} is outside the available feature range 0..{features.Length - 1}.");
            }

            projected[i] = features[rawIndex];
        }

        return projected;
    }

    internal static float[] ProjectRawFeaturesForSnapshot(float[] rawFeatures, ModelSnapshot snapshot)
    {
        if (string.Equals(snapshot.Type, "FTTRANSFORMER", StringComparison.OrdinalIgnoreCase) &&
            snapshot.FtTransformerRawFeatureCount > 0 &&
            rawFeatures.Length != snapshot.FtTransformerRawFeatureCount)
        {
            throw new InvalidOperationException(
                $"FT-Transformer raw feature length {rawFeatures.Length} does not match snapshot raw feature count {snapshot.FtTransformerRawFeatureCount}.");
        }

        if (snapshot.RawFeatureIndices.Length == 0)
            return rawFeatures;

        float[] projected = ProjectFeaturesByRawIndex(rawFeatures, snapshot.RawFeatureIndices);
        int expectedFeatureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : projected.Length;
        if (projected.Length != expectedFeatureCount)
        {
            throw new InvalidOperationException(
                $"Projected feature count {projected.Length} does not match snapshot schema count {expectedFeatureCount}.");
        }

        return projected;
    }

    internal static (float[] Means, float[] Stds) ResolveStandardisationStats(
        ModelSnapshot snapshot,
        string? currentRegime,
        int featureCount)
    {
        float[] means = snapshot.Means;
        float[] stds = snapshot.Stds;
        if (currentRegime is not null &&
            snapshot.RegimeMeans.TryGetValue(currentRegime, out var regimeMeans) &&
            snapshot.RegimeStds.TryGetValue(currentRegime, out var regimeStds) &&
            regimeMeans.Length == featureCount &&
            regimeStds.Length == featureCount)
        {
            means = regimeMeans;
            stds = regimeStds;
        }

        return (means, stds);
    }

    internal static void ApplyFeatureMask(float[] features, bool[] mask, int featureCount)
    {
        if (mask.Length == 0)
            return;

        if (mask.Length != featureCount)
        {
            throw new InvalidOperationException(
                $"ActiveFeatureMask length {mask.Length} does not match feature count {featureCount}.");
        }

        if (!mask.Any(v => v))
            throw new InvalidOperationException("ActiveFeatureMask cannot disable every feature.");

        for (int j = 0; j < featureCount; j++)
        {
            if (!mask[j])
                features[j] = 0f;
        }
    }

    internal static SnapshotPreparedFeatures PrepareSnapshotFeatures(
        float[] rawFeatures,
        ModelSnapshot snapshot,
        string? currentRegime = null,
        float[]? meansOverride = null,
        float[]? stdsOverride = null,
        bool applyTransforms = true,
        bool applyMask = true)
    {
        float[] projectedRawFeatures = ProjectRawFeaturesForSnapshot(rawFeatures, snapshot);
        float[] replayedRawFeatures = CloneAndWinsorize(projectedRawFeatures, snapshot);
        // Route featureCount through the snapshot's own resolver so legacy V2/V3 models
        // (saved before ExpectedInputFeatures/FeatureSchemaVersion were persisted) still
        // validate against their own ActiveFeatureMask length. Previously this fell back
        // to replayedRawFeatures.Length, which could be 33 for a V2 model — causing
        // ApplyFeatureMask to throw "length 37 does not match feature count 33".
        int resolved = snapshot.ResolveExpectedInputFeatures();
        int featureCount = resolved > 0
            ? resolved
            : (snapshot.Features.Length > 0 ? snapshot.Features.Length : replayedRawFeatures.Length);

        (float[] means, float[] stds) = meansOverride is { Length: > 0 } && stdsOverride is { Length: > 0 }
            ? (meansOverride, stdsOverride)
            : ResolveStandardisationStats(snapshot, currentRegime, featureCount);

        float[] features = StandardiseFeatures(replayedRawFeatures, means, stds, featureCount);

        if (applyTransforms)
            InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, snapshot);
        if (applyMask)
            ApplyFeatureMask(features, snapshot.ActiveFeatureMask, featureCount);

        return new SnapshotPreparedFeatures(replayedRawFeatures, features, featureCount, means, stds);
    }

    internal static List<TrainingSample> ReplaySnapshotPreprocessing(
        IReadOnlyList<TrainingSample> samples,
        ModelSnapshot snapshot,
        string? currentRegime = null)
    {
        var replayed = new List<TrainingSample>(samples.Count);
        if (samples.Count == 0)
            return replayed;

        int featureCount = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var prepared = PrepareSnapshotFeatures(
                samples[i].Features,
                snapshot,
                currentRegime,
                applyTransforms: false,
                applyMask: false);
            featureCount = prepared.FeatureCount;
            replayed.Add(samples[i] with { Features = prepared.Features });
        }

        if (snapshot.FracDiffD > 0.0 && featureCount > 0)
            replayed = MLFeatureHelper.ApplyFractionalDifferencing(replayed, featureCount, snapshot.FracDiffD);

        for (int i = 0; i < replayed.Count; i++)
        {
            float[] features = (float[])replayed[i].Features.Clone();
            InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, snapshot);
            ApplyFeatureMask(features, snapshot.ActiveFeatureMask, featureCount);
            replayed[i] = replayed[i] with { Features = features };
        }

        return replayed;
    }
}
