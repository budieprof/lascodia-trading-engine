using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Typed on-disk representation of a stacked meta-learner: one logistic sub-model per
/// feature-family block, plus a logistic meta-combiner over sub-model Buy-probabilities.
/// Serialised as JSON into <see cref="ModelSnapshot.StackedMetaJson"/> and reconstructed
/// by <c>StackedInferenceEngine</c>.
/// </summary>
/// <param name="FeatureSchemaVersion">Schema version (5 = V5 52 features, 6 = V6 57 features).</param>
/// <param name="ExpectedInputFeatures">Raw feature-vector length this stack was trained on.</param>
/// <param name="SubModels">One entry per active feature family, in family-enum order.</param>
/// <param name="MetaWeights">Length = <see cref="SubModels"/> count. Weights over Buy-probabilities.</param>
/// <param name="MetaBias">Meta-combiner bias term.</param>
/// <param name="FeatureMeans">Global standardisation means (length = ExpectedInputFeatures).</param>
/// <param name="FeatureStds">Global standardisation stds (length = ExpectedInputFeatures).</param>
public sealed record StackedMetaLearnerArtifact(
    int FeatureSchemaVersion,
    int ExpectedInputFeatures,
    StackedSubModel[] SubModels,
    double[] MetaWeights,
    double MetaBias,
    double[] FeatureMeans,
    double[] FeatureStds);

/// <summary>
/// A logistic sub-model trained on one feature-family block.
/// </summary>
/// <param name="FamilyName">Human-readable family identifier.</param>
/// <param name="FeatureIndices">Raw indices into the V5/V6 feature vector this sub-model consumes.</param>
/// <param name="Weights">Logistic regression weights, length = FeatureIndices length.</param>
/// <param name="Bias">Logistic regression bias term.</param>
public sealed record StackedSubModel(
    string FamilyName,
    int[] FeatureIndices,
    double[] Weights,
    double Bias);

/// <summary>
/// Feature-family blocks for the V5/V6 feature schema. Ranges are fixed by feature-schema
/// design; changing them is a breaking change that invalidates all Stacked snapshots.
/// See <c>MLFeatureHelper</c> for the canonical feature order.
/// </summary>
public static class StackedFeatureFamilies
{
    public readonly record struct Family(string Name, int StartInclusive, int EndExclusive, int MinSchemaVersion);

    public static readonly Family Ohlcv        = new("Ohlcv",        0,  33, 1);
    public static readonly Family Macro        = new("Macro",        33, 37, 2);
    public static readonly Family Calendar     = new("Calendar",     37, 43, 3);
    public static readonly Family Microstruct  = new("Microstruct",  43, 48, 4);
    public static readonly Family SynthDom     = new("SynthDom",     48, 52, 5);
    public static readonly Family RealDom      = new("RealDom",      52, 57, 6);

    /// <summary>
    /// Return the families active for a given feature count. A family is active when the
    /// feature count is at least <c>EndExclusive</c> — the schema is additive, so a V5 vector
    /// (52 features) includes all families through SynthDom but not RealDom.
    /// </summary>
    public static IReadOnlyList<Family> ActiveFor(int expectedInputFeatures)
    {
        var all = new[] { Ohlcv, Macro, Calendar, Microstruct, SynthDom, RealDom };
        var active = new List<Family>(all.Length);
        foreach (var f in all)
        {
            if (expectedInputFeatures >= f.EndExclusive)
                active.Add(f);
        }
        return active;
    }

    public static int[] IndicesFor(Family family)
    {
        int len = family.EndExclusive - family.StartInclusive;
        var idx = new int[len];
        for (int i = 0; i < len; i++) idx[i] = family.StartInclusive + i;
        return idx;
    }
}

/// <summary>
/// Shared helpers for Stacked snapshot serialisation and inference-time reconstruction.
/// </summary>
public static class StackedSnapshotSupport
{
    public const string ModelType = "stacked";
    public const string ModelVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        MaxDepth = 16,
    };

    public static bool IsStacked(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, ModelType, StringComparison.OrdinalIgnoreCase);

    public static string Serialize(StackedMetaLearnerArtifact artifact) =>
        JsonSerializer.Serialize(artifact, JsonOpts);

    public static StackedMetaLearnerArtifact? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<StackedMetaLearnerArtifact>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));
}
