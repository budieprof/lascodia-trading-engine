using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class ElmSnapshotSupport
{
    internal readonly record struct ValidationResult(bool IsValid, string[] Issues);

    internal readonly record struct CompatibilityResult(bool IsCompatible, string[] Issues);

    private static readonly JsonSerializerOptions CloneJsonOptions =
        new()
        {
            WriteIndented = false,
            MaxDepth = 128,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
        };

    internal static bool IsElm(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, "elm", StringComparison.OrdinalIgnoreCase);

    internal static ModelSnapshot NormalizeSnapshotCopy(ModelSnapshot snapshot)
    {
        byte[] cloneBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, CloneJsonOptions);
        var clone = JsonSerializer.Deserialize<ModelSnapshot>(cloneBytes, CloneJsonOptions) ?? new ModelSnapshot();
        UpgradeSnapshotInPlace(clone);
        return clone;
    }

    internal static CompatibilityResult AssessWarmStartCompatibility(
        ModelSnapshot snapshot,
        string expectedFeatureSchemaFingerprint,
        string expectedPreprocessingFingerprint,
        string expectedTrainerFingerprint,
        int expectedFeatureCount,
        int expectedHiddenSize)
    {
        if (!IsElm(snapshot))
            return new CompatibilityResult(false, ["Warm-start snapshot is not an ELM snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var validation = ValidateSnapshot(normalized, allowLegacy: true);
        var issues = new List<string>(validation.Issues);

        if (!string.IsNullOrWhiteSpace(expectedFeatureSchemaFingerprint) &&
            !string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint) &&
            !string.Equals(expectedFeatureSchemaFingerprint, normalized.FeatureSchemaFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Feature schema fingerprint mismatch.");
        }

        if (!string.IsNullOrWhiteSpace(expectedPreprocessingFingerprint) &&
            !string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint) &&
            !string.Equals(expectedPreprocessingFingerprint, normalized.PreprocessingFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Preprocessing fingerprint mismatch.");
        }

        if (!string.IsNullOrWhiteSpace(expectedTrainerFingerprint) &&
            !string.IsNullOrWhiteSpace(normalized.TrainerFingerprint) &&
            !string.Equals(expectedTrainerFingerprint, normalized.TrainerFingerprint, StringComparison.Ordinal))
        {
            issues.Add("Trainer fingerprint mismatch.");
        }

        if (normalized.Features.Length != expectedFeatureCount)
            issues.Add("Warm-start feature count does not match the current feature count.");
        if (normalized.Means.Length != expectedFeatureCount || normalized.Stds.Length != expectedFeatureCount)
            issues.Add("Warm-start standardization vectors do not match the current feature count.");
        if (normalized.ActiveFeatureMask.Length > 0 && normalized.ActiveFeatureMask.Length != expectedFeatureCount)
            issues.Add("Warm-start ActiveFeatureMask does not match the current feature count.");
        if (normalized.ElmWinsorizeLowerBounds.Length > 0 && normalized.ElmWinsorizeLowerBounds.Length != expectedFeatureCount)
            issues.Add("Warm-start winsorization lower bounds do not match the current feature count.");
        if (normalized.ElmWinsorizeUpperBounds.Length > 0 && normalized.ElmWinsorizeUpperBounds.Length != expectedFeatureCount)
            issues.Add("Warm-start winsorization upper bounds do not match the current feature count.");
        if (normalized.ElmHiddenDim > 0 && expectedHiddenSize > 0 && normalized.ElmHiddenDim != expectedHiddenSize)
            issues.Add("Warm-start hidden dimension does not match the current hidden size.");

        return new CompatibilityResult(issues.Count == 0, [..issues.Distinct()]);
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames, int rawFeatureCount)
    {
        rawFeatureCount = Math.Max(0, Math.Min(rawFeatureCount, featureNames.Length));
        if (rawFeatureCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("elm-feature-schema|");
        builder.Append(rawFeatureCount);
        builder.Append('|');
        for (int i = 0; i < rawFeatureCount; i++)
        {
            builder.Append(featureNames[i]);
            builder.Append('|');
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(
        int featureCount,
        double fracDiffD,
        bool winsorizationEnabled)
    {
        var builder = new StringBuilder();
        builder.Append("elm-preproc|");
        builder.Append(featureCount);
        builder.Append('|');
        builder.Append(Math.Round(fracDiffD, 6));
        builder.Append('|');
        builder.Append(winsorizationEnabled ? '1' : '0');
        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(ModelSnapshot snapshot)
    {
        int featureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : Math.Max(snapshot.Means.Length, snapshot.Stds.Length);
        bool winsorizationEnabled =
            snapshot.ElmWinsorizeLowerBounds is { Length: > 0 } &&
            snapshot.ElmWinsorizeUpperBounds is { Length: > 0 };
        return ComputePreprocessingFingerprint(featureCount, snapshot.FracDiffD, winsorizationEnabled);
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int featureCount,
        int hiddenSize,
        int learnerCount)
    {
        string payload = JsonSerializer.Serialize(new
        {
            featureCount,
            hiddenSize,
            learnerCount,
            hp.LabelSmoothing,
            hp.TemporalDecayLambda,
            hp.FeatureSampleRatio,
            hp.L2Lambda,
            hp.UseClassWeights,
            hp.ElmActivation,
            hp.ElmDropoutRate,
            hp.ElmHiddenSizeVariation,
            hp.ElmMixActivations,
            hp.ElmUseSmote,
            hp.ElmWinsorizePercentile,
            hp.FracDiffD,
            hp.ElmOuterSeed,
            hp.ElmSubModelLr,
            hp.ElmSubModelMaxEpochs,
            hp.ElmSubModelPatience,
            hp.ElmMagRegressorLr,
            hp.ElmMagRegressorMaxEpochs,
            hp.ElmMagRegressorPatience,
            hp.ElmMagQuadraticTerms,
            hp.FitTemperatureScale,
            hp.MinFeatureImportance,
            hp.ThresholdSearchStepBps,
            hp.MutualInfoRedundancyThreshold,
            hp.AgeDecayLambda,
        }, CloneJsonOptions);

        return ComputeSha256(payload);
    }

    internal static void UpgradeSnapshotInPlace(ModelSnapshot snapshot)
    {
        if (!IsElm(snapshot))
            return;

        snapshot.Features ??= [];
        snapshot.Means ??= [];
        snapshot.Stds ??= [];
        snapshot.ActiveFeatureMask ??= [];
        snapshot.ElmInputWeights ??= [];
        snapshot.ElmInputBiases ??= [];
        snapshot.ElmWinsorizeLowerBounds ??= [];
        snapshot.ElmWinsorizeUpperBounds ??= [];
        snapshot.MetaLabelTopFeatureIndices ??= [];

        int featureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : Math.Max(snapshot.Means.Length, snapshot.Stds.Length);

        if (snapshot.Features.Length == 0 && featureCount > 0)
        {
            var names = new string[featureCount];
            for (int i = 0; i < featureCount; i++)
                names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
            snapshot.Features = names;
        }

        if (snapshot.ElmWinsorizeLowerBounds.Length == 0 && snapshot.ElmWinsorizeUpperBounds.Length > 0)
            snapshot.ElmWinsorizeLowerBounds = new float[snapshot.ElmWinsorizeUpperBounds.Length];
        if (snapshot.ElmWinsorizeUpperBounds.Length == 0 && snapshot.ElmWinsorizeLowerBounds.Length > 0)
            snapshot.ElmWinsorizeUpperBounds = new float[snapshot.ElmWinsorizeLowerBounds.Length];

        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) && featureCount > 0)
            snapshot.FeatureSchemaFingerprint = ComputeFeatureSchemaFingerprint(snapshot.Features, featureCount);
        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint) && featureCount > 0)
            snapshot.PreprocessingFingerprint = ComputePreprocessingFingerprint(snapshot);
    }

    internal static ValidationResult ValidateSnapshot(ModelSnapshot snapshot, bool allowLegacy)
    {
        if (!IsElm(snapshot))
            return new ValidationResult(false, ["Snapshot is not an ELM snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var issues = new List<string>();

        int featureCount = normalized.Features.Length > 0
            ? normalized.Features.Length
            : Math.Max(normalized.Means.Length, normalized.Stds.Length);

        if (featureCount <= 0)
            issues.Add("Feature schema is empty.");
        if (normalized.Means.Length > 0 && normalized.Means.Length != featureCount)
            issues.Add("Means length does not match the serialized feature schema.");
        if (normalized.Stds.Length > 0 && normalized.Stds.Length != featureCount)
            issues.Add("Stds length does not match the serialized feature schema.");
        if (normalized.ActiveFeatureMask.Length > 0 && normalized.ActiveFeatureMask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match the serialized feature schema.");
        if (normalized.ActiveFeatureMask.Length > 0 && !normalized.ActiveFeatureMask.Any(v => v))
            issues.Add("ActiveFeatureMask disables every feature.");
        if (normalized.ElmWinsorizeLowerBounds.Length != normalized.ElmWinsorizeUpperBounds.Length)
            issues.Add("Winsorization bounds are ragged.");
        if (normalized.ElmWinsorizeLowerBounds.Length > 0 && normalized.ElmWinsorizeLowerBounds.Length != featureCount)
            issues.Add("Winsorization bounds length does not match the serialized feature schema.");
        if (normalized.MetaLabelTopFeatureIndices.Any(i => i < 0 || i >= featureCount))
            issues.Add("MetaLabelTopFeatureIndices contain an out-of-range feature index.");

        if (!string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint))
        {
            string computedSchema = ComputeFeatureSchemaFingerprint(normalized.Features, featureCount);
            if (!string.Equals(normalized.FeatureSchemaFingerprint, computedSchema, StringComparison.Ordinal))
                issues.Add("FeatureSchemaFingerprint does not match the serialized feature schema.");
        }

        if (!string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint))
        {
            string computedPreprocessing = ComputePreprocessingFingerprint(normalized);
            if (!string.Equals(normalized.PreprocessingFingerprint, computedPreprocessing, StringComparison.Ordinal))
                issues.Add("PreprocessingFingerprint does not match the serialized ELM preprocessing layout.");
        }

        if (!allowLegacy)
        {
            if (string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint))
                issues.Add("FeatureSchemaFingerprint is missing.");
            if (string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint))
                issues.Add("PreprocessingFingerprint is missing.");
            if (string.IsNullOrWhiteSpace(normalized.TrainerFingerprint))
                issues.Add("TrainerFingerprint is missing.");
        }

        return new ValidationResult(issues.Count == 0, [..issues.Distinct()]);
    }

    private static string ComputeSha256(string payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}
