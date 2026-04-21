using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Shared policy for converting a serialised <see cref="ModelSnapshot"/> into named feature
/// importance scores. Feature monitoring workers use this so "importance" means the same thing
/// across rank-shift, consensus, and future interpretability checks.
/// </summary>
internal static class ModelSnapshotFeatureImportanceExtractor
{
    internal const string SourceTcnChannelScores = "tcn_channel_scores";
    internal const string SourceFeatureImportanceScores = "feature_importance_scores";
    internal const string SourceFeatureImportance = "feature_importance";
    internal const string SourceWeightFallback = "weight_fallback";
    internal const string SourceNone = "none";

    internal static FeatureImportanceExtractionResult Extract(ModelSnapshot snapshot)
    {
        if (string.Equals(snapshot.Type, "TCN", StringComparison.OrdinalIgnoreCase)
            && snapshot.TcnChannelNames.Length > 0
            && snapshot.TcnChannelImportanceScores.Length > 0)
        {
            return BuildImportanceMap(
                snapshot.TcnChannelNames,
                snapshot.TcnChannelImportanceScores,
                SourceTcnChannelScores);
        }

        if (snapshot.FeatureImportanceScores.Length > 0
            && snapshot.Features.Length >= snapshot.FeatureImportanceScores.Length)
        {
            return BuildImportanceMap(
                snapshot.Features,
                snapshot.FeatureImportanceScores,
                SourceFeatureImportanceScores);
        }

        if (snapshot.FeatureImportance.Length > 0
            && snapshot.Features.Length >= snapshot.FeatureImportance.Length)
        {
            return BuildImportanceMap(
                snapshot.Features,
                snapshot.FeatureImportance.Select(v => (double)v),
                SourceFeatureImportance);
        }

        if (snapshot.Weights.Length > 0 && snapshot.Features.Length > 0)
        {
            int featureCount = snapshot.Features.Length;
            var sums = new double[featureCount];
            int contributingLearners = 0;
            int rejected = 0;

            foreach (var learnerWeights in snapshot.Weights)
            {
                if (learnerWeights.Length == 0)
                    continue;

                contributingLearners++;
                for (int i = 0; i < featureCount && i < learnerWeights.Length; i++)
                {
                    double value = learnerWeights[i];
                    if (double.IsFinite(value))
                        sums[i] += Math.Abs(value);
                    else
                        rejected++;
                }
            }

            if (contributingLearners == 0)
                return FeatureImportanceExtractionResult.Empty(SourceNone);

            for (int i = 0; i < sums.Length; i++)
                sums[i] /= contributingLearners;

            var result = BuildImportanceMap(snapshot.Features, sums, SourceWeightFallback);
            return result with { InvalidValueCount = result.InvalidValueCount + rejected };
        }

        return FeatureImportanceExtractionResult.Empty(SourceNone);
    }

    private static FeatureImportanceExtractionResult BuildImportanceMap(
        IReadOnlyList<string> names,
        IEnumerable<double> rawValues,
        string source)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        int index = 0;
        int rejected = 0;

        foreach (double rawValue in rawValues)
        {
            if (index >= names.Count)
                break;

            string name = names[index];
            index++;

            if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(rawValue))
            {
                rejected++;
                continue;
            }

            result[name] = Math.Abs(rawValue);
        }

        return new FeatureImportanceExtractionResult(result, source, rejected);
    }
}

internal sealed record FeatureImportanceExtractionResult(
    IReadOnlyDictionary<string, double> Importance,
    string Source,
    int InvalidValueCount)
{
    internal static FeatureImportanceExtractionResult Empty(string source) =>
        new(new Dictionary<string, double>(StringComparer.Ordinal), source, 0);
}
