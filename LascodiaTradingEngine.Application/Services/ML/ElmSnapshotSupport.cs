using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;

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
        var validation = ValidateNormalizedSnapshot(normalized, allowLegacy: true);
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
        snapshot.RawFeatureIndices ??= [];
        snapshot.FeaturePipelineTransforms ??= [];
        snapshot.FeaturePipelineDescriptors ??= [];
        snapshot.ActiveFeatureMask ??= [];
        snapshot.Weights ??= [];
        snapshot.Biases ??= [];
        snapshot.ElmInputWeights ??= [];
        snapshot.ElmInputBiases ??= [];
        snapshot.FeatureSubsetIndices ??= [];
        snapshot.LearnerActivations ??= [];
        snapshot.ElmWinsorizeLowerBounds ??= [];
        snapshot.ElmWinsorizeUpperBounds ??= [];
        snapshot.MetaLabelTopFeatureIndices ??= [];
        snapshot.ElmInverseGram ??= [];
        snapshot.ElmInverseGramDim ??= [];

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

        return ValidateNormalizedSnapshot(NormalizeSnapshotCopy(snapshot), allowLegacy);
    }

    internal static ValidationResult ValidateNormalizedSnapshot(ModelSnapshot normalized, bool allowLegacy)
    {
        var issues = new List<string>();
        var weights = normalized.Weights ?? [];
        var biases = normalized.Biases ?? [];
        var inputWeights = normalized.ElmInputWeights ?? [];
        var inputBiases = normalized.ElmInputBiases ?? [];
        var featureSubsets = normalized.FeatureSubsetIndices ?? [];
        var learnerActivations = normalized.LearnerActivations ?? [];
        var activeFeatureMask = normalized.ActiveFeatureMask ?? [];
        var magWeights = normalized.MagWeights ?? [];

        int featureCount = normalized.Features.Length > 0
            ? normalized.Features.Length
            : Math.Max(normalized.Means.Length, normalized.Stds.Length);

        if (featureCount <= 0)
            issues.Add("Feature schema is empty.");
        if (normalized.Means.Length > 0 && normalized.Means.Length != featureCount)
            issues.Add("Means length does not match the serialized feature schema.");
        if (normalized.Stds.Length > 0 && normalized.Stds.Length != featureCount)
            issues.Add("Stds length does not match the serialized feature schema.");
        if (normalized.RawFeatureIndices.Length > 0 && normalized.RawFeatureIndices.Length != featureCount)
            issues.Add("RawFeatureIndices length does not match the serialized feature schema.");
        if (normalized.RawFeatureIndices.Any(index => index < 0))
            issues.Add("RawFeatureIndices contains a negative index.");
        if (normalized.RawFeatureIndices.Length > 0 &&
            normalized.RawFeatureIndices.Distinct().Count() != normalized.RawFeatureIndices.Length)
        {
            issues.Add("RawFeatureIndices contains duplicate indices.");
        }
        if (activeFeatureMask.Length > 0 && activeFeatureMask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match the serialized feature schema.");
        if (activeFeatureMask.Length > 0 && !activeFeatureMask.Any(v => v))
            issues.Add("ActiveFeatureMask disables every feature.");
        if (normalized.ElmWinsorizeLowerBounds.Length != normalized.ElmWinsorizeUpperBounds.Length)
            issues.Add("Winsorization bounds are ragged.");
        if (normalized.ElmWinsorizeLowerBounds.Length > 0 && normalized.ElmWinsorizeLowerBounds.Length != featureCount)
            issues.Add("Winsorization bounds length does not match the serialized feature schema.");
        if (normalized.MetaLabelTopFeatureIndices.Any(i => i < 0 || i >= featureCount))
            issues.Add("MetaLabelTopFeatureIndices contain an out-of-range feature index.");
        if (normalized.ElmHiddenDim <= 0)
            issues.Add("ElmHiddenDim must be positive.");
        if (weights.Length == 0)
            issues.Add("Weights are missing.");
        if (biases.Length != weights.Length)
            issues.Add("Biases length does not match learner count.");
        if (inputWeights.Length != weights.Length)
            issues.Add("ElmInputWeights length does not match learner count.");
        if (inputBiases.Length > 0 && inputBiases.Length != weights.Length)
            issues.Add("ElmInputBiases length does not match learner count.");
        if (featureSubsets.Length > 0 && featureSubsets.Length != weights.Length)
        {
            issues.Add("FeatureSubsetIndices length does not match learner count.");
        }
        if (learnerActivations.Length > 0 &&
            learnerActivations.Length != 1 &&
            learnerActivations.Length != weights.Length)
        {
            issues.Add("LearnerActivations length must be 1 or match learner count.");
        }
        if (normalized.BaseLearnersK > 0 && normalized.BaseLearnersK != weights.Length)
            issues.Add("BaseLearnersK does not match learner count.");
        if (normalized.PrunedFeatureCount < 0)
            issues.Add("PrunedFeatureCount cannot be negative.");
        if (activeFeatureMask.Length > 0 &&
            normalized.PrunedFeatureCount != activeFeatureMask.Count(active => !active))
        {
            issues.Add("PrunedFeatureCount does not match ActiveFeatureMask.");
        }
        if (magWeights.Length > 0 && magWeights.Length != featureCount)
            issues.Add("MagWeights length does not match the serialized feature schema.");

        ValidateFeaturePipeline(normalized, featureCount, issues);
        ValidateLearnerMetadata(normalized, featureCount, issues);
        ValidateInverseGramMetadata(normalized, issues);

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

    private static void ValidateFeaturePipeline(ModelSnapshot snapshot, int featureCount, List<string> issues)
    {
        int rawFeatureCount = snapshot.RawFeatureIndices.Length > 0
            ? snapshot.RawFeatureIndices.Max() + 1
            : featureCount;

        var descriptors = snapshot.FeaturePipelineDescriptors ?? [];
        foreach (var descriptor in descriptors)
        {
            if (descriptor.InputFeatureCount != rawFeatureCount)
                issues.Add("Feature pipeline descriptor input count does not match raw feature count.");
            if (descriptor.SourceIndexGroups.Length == 0)
                issues.Add("Feature pipeline descriptor must declare at least one source group.");

            if (string.Equals(descriptor.Operation, FeaturePipelineTransformSupport.GroupSumInPlaceOperation, StringComparison.OrdinalIgnoreCase))
            {
                if (descriptor.OutputStartIndex != 0)
                    issues.Add("In-place feature pipeline descriptors must start at index 0.");
                if (descriptor.OutputCount != featureCount)
                    issues.Add("In-place feature pipeline descriptors must preserve the full feature count.");
            }
            else
            {
                if (descriptor.OutputStartIndex < rawFeatureCount)
                    issues.Add("Feature pipeline descriptor output start index overlaps raw features.");
                if (descriptor.OutputCount != descriptor.SourceIndexGroups.Length)
                    issues.Add("Feature pipeline descriptor output count does not match SourceIndexGroups length.");
            }

            foreach (var group in descriptor.SourceIndexGroups)
            {
                if (group.Length == 0)
                {
                    issues.Add("Feature pipeline descriptor source groups cannot be empty.");
                    break;
                }

                foreach (int index in group)
                {
                    if (index < 0 || index >= rawFeatureCount)
                    {
                        issues.Add("Feature pipeline descriptor contains an out-of-range raw feature index.");
                        break;
                    }
                }
            }
        }
    }

    private static void ValidateLearnerMetadata(ModelSnapshot snapshot, int featureCount, List<string> issues)
    {
        var inputWeightMatrix = snapshot.ElmInputWeights ?? [];
        var inputBiasMatrix = snapshot.ElmInputBiases ?? [];
        var featureSubsets = snapshot.FeatureSubsetIndices ?? [];

        for (int k = 0; k < snapshot.Weights.Length; k++)
        {
            if (snapshot.Weights[k] is not { Length: > 0 } outputWeights)
            {
                issues.Add($"Learner {k} output weights are missing.");
                continue;
            }

            if (k >= inputWeightMatrix.Length || inputWeightMatrix[k] is not { Length: > 0 } inputWeights)
            {
                issues.Add($"Learner {k} input weights are missing.");
                continue;
            }

            int[] subset = featureSubsets.Length > k
                ? featureSubsets[k] ?? []
                : [];
            if (subset.Length > 0)
            {
                if (subset.Any(index => index < 0 || index >= featureCount))
                    issues.Add($"Learner {k} feature subset contains an out-of-range feature index.");
                if (subset.Distinct().Count() != subset.Length)
                    issues.Add($"Learner {k} feature subset contains duplicate feature indices.");
            }

            int effectiveInputWidth = subset.Length > 0 ? subset.Length : featureCount;
            if (effectiveInputWidth <= 0)
            {
                issues.Add($"Learner {k} does not have any usable input features.");
                continue;
            }

            if (inputWeights.Length % effectiveInputWidth != 0)
            {
                issues.Add($"Learner {k} input weight matrix length is not divisible by its effective input width.");
                continue;
            }

            int hiddenFromInputs = inputWeights.Length / effectiveInputWidth;
            if (hiddenFromInputs != outputWeights.Length)
                issues.Add($"Learner {k} hidden dimension inferred from ElmInputWeights does not match output weights.");

            if (inputBiasMatrix.Length > k && inputBiasMatrix[k] is { Length: > 0 } inputBiases)
            {
                if (inputBiases.Length != outputWeights.Length)
                    issues.Add($"Learner {k} ElmInputBiases length does not match hidden dimension.");
            }
        }
    }

    private static void ValidateInverseGramMetadata(ModelSnapshot snapshot, List<string> issues)
    {
        if (snapshot.ElmInverseGram is not { Length: > 0 })
            return;

        if (snapshot.ElmInverseGramDim is not { Length: > 0 } inverseGramDims ||
            inverseGramDims.Length != snapshot.ElmInverseGram.Length)
        {
            issues.Add("ElmInverseGramDim length does not match ElmInverseGram length.");
            return;
        }

        if (snapshot.ElmInverseGram.Length != snapshot.Weights.Length)
            issues.Add("ElmInverseGram length does not match learner count.");

        for (int k = 0; k < Math.Min(snapshot.ElmInverseGram.Length, snapshot.Weights.Length); k++)
        {
            int gramDim = inverseGramDims[k];
            if (gramDim <= 0)
            {
                issues.Add($"Learner {k} inverse Gram dimension must be positive.");
                continue;
            }

            if (snapshot.ElmInverseGram[k] is not { Length: > 0 } inverseGram)
            {
                issues.Add($"Learner {k} inverse Gram matrix is missing.");
                continue;
            }

            if (inverseGram.Length != gramDim * gramDim)
            {
                issues.Add($"Learner {k} inverse Gram matrix length does not match ElmInverseGramDim.");
                continue;
            }

            int hiddenSize = snapshot.Weights[k]?.Length ?? 0;
            if (hiddenSize > 0 && gramDim != hiddenSize && gramDim != hiddenSize + 1)
                issues.Add($"Learner {k} inverse Gram dimension is inconsistent with hidden dimension.");
        }
    }

    private static string ComputeSha256(string payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }
}
