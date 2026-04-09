using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class ElmFeaturePipelineHelper
{
    internal readonly record struct PreparedSamples(
        List<TrainingSample> Samples,
        float[] Means,
        float[] Stds,
        float[] WinsorizeLowerBounds,
        float[] WinsorizeUpperBounds);

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
}
