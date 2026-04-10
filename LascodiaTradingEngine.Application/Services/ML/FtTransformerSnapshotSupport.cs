using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class FtTransformerSnapshotSupport
{
    private const string CurrentSnapshotVersion = "8.0";
    private const string InitialSnapshotVersion = "5.0";

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

    internal static bool IsFtTransformer(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, "FTTRANSFORMER", StringComparison.OrdinalIgnoreCase);

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
        int expectedEmbedDim,
        int expectedNumHeads,
        int expectedFfnDim,
        int expectedNumLayers)
    {
        if (!IsFtTransformer(snapshot))
            return new CompatibilityResult(false, ["Warm-start snapshot is not an FT-Transformer snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var issues = new List<string>();

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

        if (normalized.FtTransformerEmbedWeights is { Length: > 0 } embedWeights &&
            embedWeights.Length != expectedFeatureCount)
        {
            issues.Add("Feature count mismatch.");
        }

        if (normalized.FtTransformerEmbedDim > 0 && normalized.FtTransformerEmbedDim != expectedEmbedDim)
            issues.Add("Embedding dimension mismatch.");
        if (normalized.FtTransformerNumHeads > 0 && normalized.FtTransformerNumHeads != expectedNumHeads)
            issues.Add("Attention head count mismatch.");
        if (normalized.FtTransformerFfnDim > 0 && normalized.FtTransformerFfnDim != expectedFfnDim)
            issues.Add("FFN dimension mismatch.");
        if (normalized.FtTransformerNumLayers > 0 && normalized.FtTransformerNumLayers != expectedNumLayers)
            issues.Add("Layer count mismatch.");

        return new CompatibilityResult(issues.Count == 0, issues.ToArray());
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames, int rawFeatureCount)
    {
        rawFeatureCount = Math.Max(0, Math.Min(rawFeatureCount, featureNames.Length));
        if (rawFeatureCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("fttransformer-feature-schema|");
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
        int rawFeatureCount,
        int[] rawFeatureIndices,
        bool[]? activeMask)
    {
        var builder = new StringBuilder();
        builder.Append("fttransformer-preproc|");
        builder.Append(rawFeatureCount);
        builder.Append('|');
        if (rawFeatureIndices.Length > 0)
        {
            builder.Append("raw-index-map|");
            for (int i = 0; i < rawFeatureIndices.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(rawFeatureIndices[i]);
            }
        }
        else
        {
            builder.Append("identity");
        }

        builder.Append('|');
        if (activeMask is { Length: > 0 })
        {
            for (int i = 0; i < activeMask.Length; i++)
                builder.Append(activeMask[i] ? '1' : '0');
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(ModelSnapshot snapshot)
    {
        int rawFeatureCount = ResolveRawFeatureCount(snapshot, snapshot.Features.Length);
        return ComputePreprocessingFingerprint(rawFeatureCount, snapshot.RawFeatureIndices, snapshot.ActiveFeatureMask);
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int embedDim,
        int numHeads,
        int ffnDim,
        int numLayers)
    {
        string payload = JsonSerializer.Serialize(new
        {
            embedDim,
            numHeads,
            ffnDim,
            numLayers,
            hp.FtDropoutRate,
            hp.FtUsePositionalEncoding,
            hp.LabelSmoothing,
            hp.LearningRate,
            hp.L2Lambda,
            hp.MaxGradNorm,
            hp.MagLossWeight,
            hp.TemporalDecayLambda,
        }, CloneJsonOptions);

        return ComputeSha256(payload);
    }

    internal static void UpgradeSnapshotInPlace(ModelSnapshot snapshot)
    {
        if (!IsFtTransformer(snapshot))
            return;

        string version = ResolveSnapshotVersion(snapshot.Version);

        snapshot.RawFeatureIndices ??= [];
        snapshot.ActiveFeatureMask ??= [];
        snapshot.Features ??= [];
        snapshot.Means ??= [];
        snapshot.Stds ??= [];

        int featureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : snapshot.FtTransformerEmbedWeights?.Length ?? snapshot.Means.Length;
        int rawFeatureCount = ResolveRawFeatureCount(snapshot, featureCount);

        if (snapshot.Features.Length == 0 && featureCount > 0)
        {
            var names = new string[featureCount];
            for (int i = 0; i < featureCount; i++)
                names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
            snapshot.Features = names;
        }

        if (snapshot.ActiveFeatureMask.Length == 0 && featureCount > 0)
        {
            snapshot.ActiveFeatureMask = new bool[featureCount];
            Array.Fill(snapshot.ActiveFeatureMask, true);
        }

        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) && featureCount > 0)
        {
            var rawFeatureNames = new string[rawFeatureCount];
            for (int i = 0; i < rawFeatureCount; i++)
                rawFeatureNames[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
            snapshot.FeatureSchemaFingerprint = ComputeFeatureSchemaFingerprint(rawFeatureNames, rawFeatureCount);
        }

        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint))
            snapshot.PreprocessingFingerprint = ComputePreprocessingFingerprint(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint))
        {
            snapshot.TrainerFingerprint = ComputeSha256(string.Join("|",
                snapshot.Type,
                version,
                snapshot.FtTransformerEmbedDim,
                snapshot.FtTransformerNumHeads,
                snapshot.FtTransformerFfnDim,
                snapshot.FtTransformerNumLayers));
        }

        snapshot.FtTransformerRawFeatureCount = rawFeatureCount;
        if (!double.IsFinite(snapshot.ConditionalCalibrationRoutingThreshold))
            snapshot.ConditionalCalibrationRoutingThreshold = 0.5;

        switch (version)
        {
            case "5.0":
            {
                UpgradeVersion5To6(snapshot, rawFeatureCount);
                version = "6.0";
                goto case "6.0";
            }
            case "6.0":
            {
                UpgradeVersion6To7(snapshot);
                version = "7.0";
                goto case "7.0";
            }
            case "7.0":
            {
                UpgradeVersion7To8(snapshot);
                version = CurrentSnapshotVersion;
                break;
            }
            case CurrentSnapshotVersion:
                UpgradeCurrentSnapshot(snapshot, rawFeatureCount);
                break;
        }

        snapshot.Version = version;
    }

    internal static ValidationResult ValidateSnapshot(ModelSnapshot snapshot)
    {
        if (!IsFtTransformer(snapshot))
            return new ValidationResult(true, []);

        return ValidateNormalizedSnapshot(NormalizeSnapshotCopy(snapshot));
    }

    internal static ValidationResult ValidateNormalizedSnapshot(ModelSnapshot snapshot)
    {
        if (!IsFtTransformer(snapshot))
            return new ValidationResult(true, []);

        var issues = new List<string>();
        int featureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : snapshot.FtTransformerEmbedWeights?.Length ?? snapshot.Means.Length;
        int rawFeatureCount = ResolveRawFeatureCount(snapshot, featureCount);
        int embedDim = snapshot.FtTransformerEmbedDim;
        int numHeads = snapshot.FtTransformerNumHeads > 0 ? snapshot.FtTransformerNumHeads : 1;
        int ffnDim = snapshot.FtTransformerFfnDim > 0 ? snapshot.FtTransformerFfnDim : embedDim * 4;
        int numLayers = snapshot.FtTransformerNumLayers > 0 ? snapshot.FtTransformerNumLayers : 1;
        int seqLen = featureCount + 1;
        int seqSq = seqLen * seqLen;

        if (!string.Equals(snapshot.Version, CurrentSnapshotVersion, StringComparison.Ordinal))
            issues.Add($"Unsupported FT-Transformer snapshot version '{snapshot.Version}'.");

        if (featureCount <= 0)
            issues.Add("Snapshot feature count is zero.");
        if (embedDim <= 0)
            issues.Add("FtTransformerEmbedDim must be positive.");
        if (numHeads <= 0)
            issues.Add("FtTransformerNumHeads must be positive.");
        if (embedDim > 0 && numHeads > 0 && embedDim % numHeads != 0)
            issues.Add("Embedding dimension must be divisible by attention head count.");
        if (ffnDim <= 0)
            issues.Add("FtTransformerFfnDim must be positive.");
        if (numLayers <= 0)
            issues.Add("FtTransformerNumLayers must be positive.");

        if (snapshot.Means.Length != 0 && snapshot.Means.Length != featureCount)
            issues.Add("Means length does not match feature count.");
        if (snapshot.Stds.Length != 0 && snapshot.Stds.Length != featureCount)
            issues.Add("Stds length does not match feature count.");
        if (snapshot.ActiveFeatureMask.Length != 0 && snapshot.ActiveFeatureMask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match feature count.");
        if (snapshot.RawFeatureIndices.Length > 0 && snapshot.RawFeatureIndices.Length != featureCount)
            issues.Add("RawFeatureIndices length does not match feature count.");
        if (snapshot.RawFeatureIndices.Any(index => index < 0))
            issues.Add("RawFeatureIndices contains a negative index.");
        if (rawFeatureCount < featureCount)
            issues.Add("FtTransformerRawFeatureCount cannot be smaller than the serialized feature count.");
        if (snapshot.RawFeatureIndices.Length > 0 && snapshot.RawFeatureIndices.Max() >= rawFeatureCount)
            issues.Add("RawFeatureIndices references a raw feature outside FtTransformerRawFeatureCount.");
        if (snapshot.RawFeatureIndices.Length > 0 &&
            snapshot.RawFeatureIndices.Distinct().Count() != snapshot.RawFeatureIndices.Length)
        {
            issues.Add("RawFeatureIndices contains duplicate indices.");
        }

        string computedSchemaFingerprint = ComputeFeatureSchemaFingerprint(ResolveRawFeatureNames(rawFeatureCount), rawFeatureCount);
        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint))
            issues.Add("FeatureSchemaFingerprint is missing.");
        else if (!string.Equals(snapshot.FeatureSchemaFingerprint, computedSchemaFingerprint, StringComparison.Ordinal))
            issues.Add("FeatureSchemaFingerprint does not match the serialized FT raw feature schema.");

        string computedPreprocessingFingerprint = ComputePreprocessingFingerprint(rawFeatureCount, snapshot.RawFeatureIndices, snapshot.ActiveFeatureMask);
        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint))
            issues.Add("PreprocessingFingerprint is missing.");
        else if (!string.Equals(snapshot.PreprocessingFingerprint, computedPreprocessingFingerprint, StringComparison.Ordinal))
            issues.Add("PreprocessingFingerprint does not match the serialized FT preprocessing layout.");

        if (string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint))
            issues.Add("TrainerFingerprint is missing.");

        ValidateMatrix(snapshot.FtTransformerEmbedWeights, featureCount, embedDim, "Embedding weights", issues);
        ValidateMatrix(snapshot.FtTransformerEmbedBiases, featureCount, embedDim, "Embedding biases", issues);
        ValidateVector(snapshot.FtTransformerClsToken, embedDim, "CLS token", issues);
        ValidateLayer(snapshot.FtTransformerWq, snapshot.FtTransformerWk, snapshot.FtTransformerWv, snapshot.FtTransformerWo,
            snapshot.FtTransformerGamma1, snapshot.FtTransformerBeta1,
            snapshot.FtTransformerGamma2, snapshot.FtTransformerBeta2,
            snapshot.FtTransformerWff1, snapshot.FtTransformerBff1,
            snapshot.FtTransformerWff2, snapshot.FtTransformerBff2,
            snapshot.FtTransformerPosBias,
            embedDim, ffnDim, numHeads, seqSq, "Layer0", issues);
        ValidateVector(snapshot.FtTransformerGammaFinal, embedDim, "Final layernorm gamma", issues);
        ValidateVector(snapshot.FtTransformerBetaFinal, embedDim, "Final layernorm beta", issues);
        ValidateVector(snapshot.FtTransformerOutputWeights, embedDim, "Output weights", issues);
        if (!double.IsFinite(snapshot.FtTransformerOutputBias))
            issues.Add("Output bias is non-finite.");

        if (numLayers > 1)
        {
            var additionalLayers = TryLoadAdditionalLayers(snapshot, embedDim, ffnDim, issues);
            if (additionalLayers is null)
            {
                issues.Add("Additional FT-Transformer layers are missing or malformed.");
            }
            else if (additionalLayers.Count != numLayers - 1)
            {
                issues.Add("Additional FT-Transformer layer count does not match FtTransformerNumLayers.");
            }
            else
            {
                for (int i = 0; i < additionalLayers.Count; i++)
                {
                    var layer = additionalLayers[i];
                    ValidateLayer(layer.Wq, layer.Wk, layer.Wv, layer.Wo,
                        layer.Gamma1, layer.Beta1,
                        layer.Gamma2, layer.Beta2,
                        layer.Wff1, layer.Bff1,
                        layer.Wff2, layer.Bff2,
                        layer.PosBias,
                        embedDim, ffnDim, numHeads, seqSq, $"Layer{i + 1}", issues);
                }
            }
        }

        ValidateSplitSummary(snapshot.TrainingSplitSummary, issues);
        ValidateCalibrationArtifact(snapshot, issues);

        return new ValidationResult(issues.Count == 0, issues.ToArray());
    }

    private static int ResolveRawFeatureCount(ModelSnapshot snapshot, int featureCount)
    {
        if (snapshot.FtTransformerRawFeatureCount > 0)
            return snapshot.FtTransformerRawFeatureCount;
        if (snapshot.RawFeatureIndices.Length > 0)
            return snapshot.RawFeatureIndices.Max() + 1;
        return featureCount;
    }

    private static string[] ResolveRawFeatureNames(int rawFeatureCount)
    {
        var names = new string[rawFeatureCount];
        for (int i = 0; i < rawFeatureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    private static void ValidateSplitSummary(TrainingSplitSummary? split, List<string> issues)
    {
        if (split is null)
            return;

        if (split.TrainCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty train split.");
        if (split.SelectionCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty selection split.");
        if (split.CalibrationCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty calibration split.");
        if (split.CalibrationFitCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty calibration-fit split.");
        if (split.CalibrationDiagnosticsCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty calibration-diagnostics split.");
        if (split.ConformalCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty conformal split.");
        if (split.TestCount <= 0)
            issues.Add("TrainingSplitSummary requires a non-empty test split.");

        if (split.TrainStartIndex < 0 || split.SelectionStartIndex < 0 || split.CalibrationStartIndex < 0 ||
            split.CalibrationFitStartIndex < 0 || split.CalibrationDiagnosticsStartIndex < 0 ||
            split.ConformalStartIndex < 0 || split.TestStartIndex < 0)
        {
            issues.Add("TrainingSplitSummary contains a negative split start index.");
        }

        if (split.SelectionStartIndex < split.TrainStartIndex + split.TrainCount)
            issues.Add("TrainingSplitSummary selection split overlaps the train split.");
        if (split.CalibrationStartIndex < split.SelectionStartIndex + split.SelectionCount)
            issues.Add("TrainingSplitSummary calibration split overlaps the selection split.");
        if (split.TestStartIndex < split.CalibrationStartIndex + split.CalibrationCount)
            issues.Add("TrainingSplitSummary test split overlaps the calibration split.");

        if (split.CalibrationFitStartIndex != split.CalibrationStartIndex)
            issues.Add("TrainingSplitSummary calibration-fit split must start at CalibrationStartIndex.");
        if (split.SelectionPruningCount > 0 || split.SelectionThresholdCount > 0 || split.SelectionKellyCount > 0)
        {
            if (split.SelectionPruningCount <= 0)
                issues.Add("TrainingSplitSummary selection-pruning split must be non-empty when selection sub-splits are present.");
            if (split.SelectionThresholdCount <= 0)
                issues.Add("TrainingSplitSummary selection-threshold split must be non-empty when selection sub-splits are present.");
            if (split.SelectionPruningStartIndex != split.SelectionStartIndex)
                issues.Add("TrainingSplitSummary selection-pruning split must start at SelectionStartIndex.");
            if (split.SelectionThresholdStartIndex != split.SelectionPruningStartIndex + split.SelectionPruningCount)
                issues.Add("TrainingSplitSummary selection-threshold split must immediately follow the selection-pruning split.");
            if (split.SelectionKellyCount > 0)
            {
                if (split.SelectionKellyStartIndex != split.SelectionThresholdStartIndex + split.SelectionThresholdCount)
                    issues.Add("TrainingSplitSummary selection-Kelly split must immediately follow the selection-threshold split.");
                if (split.SelectionPruningCount + split.SelectionThresholdCount + split.SelectionKellyCount != split.SelectionCount)
                    issues.Add("TrainingSplitSummary selection sub-split counts must sum to SelectionCount.");
            }
            else if (split.SelectionPruningCount + split.SelectionThresholdCount != split.SelectionCount)
            {
                issues.Add("TrainingSplitSummary selection sub-split counts must sum to SelectionCount.");
            }
        }

        if (split.CalibrationDiagnosticsStartIndex != split.CalibrationFitStartIndex + split.CalibrationFitCount)
            issues.Add("TrainingSplitSummary calibration-diagnostics split must immediately follow calibration-fit.");

        int calibrationEnd = split.CalibrationStartIndex + split.CalibrationCount;
        bool conformalWithinDiagnostics =
            split.ConformalStartIndex >= split.CalibrationDiagnosticsStartIndex &&
            split.ConformalStartIndex + split.ConformalCount <=
            split.CalibrationDiagnosticsStartIndex + split.CalibrationDiagnosticsCount;
        bool conformalDisjointAfterDiagnostics =
            split.ConformalStartIndex == split.CalibrationDiagnosticsStartIndex + split.CalibrationDiagnosticsCount &&
            split.ConformalStartIndex + split.ConformalCount <= calibrationEnd &&
            split.CalibrationFitCount + split.CalibrationDiagnosticsCount + split.ConformalCount == split.CalibrationCount;

        if (split.ConformalStartIndex < split.CalibrationStartIndex ||
            split.ConformalStartIndex + split.ConformalCount > calibrationEnd)
        {
            issues.Add("TrainingSplitSummary conformal split must lie within the calibration split.");
        }
        else if (!conformalWithinDiagnostics && !conformalDisjointAfterDiagnostics)
        {
            issues.Add("TrainingSplitSummary conformal split must either share or immediately follow the calibration-diagnostics split.");
        }

        if (split.AdaptiveHeadCrossFitFoldCount > 0 ||
            split.AdaptiveHeadCrossFitFoldStartIndices.Length > 0 ||
            split.AdaptiveHeadCrossFitFoldCounts.Length > 0 ||
            split.AdaptiveHeadCrossFitFoldHashes.Length > 0)
        {
            if (!split.AdaptiveHeadSplitMode.Contains("CROSSFIT", StringComparison.OrdinalIgnoreCase))
                issues.Add("TrainingSplitSummary cross-fit metadata requires an AdaptiveHeadSplitMode containing 'CROSSFIT'.");
            if (split.AdaptiveHeadCrossFitFoldCount <= 0)
                issues.Add("TrainingSplitSummary AdaptiveHeadCrossFitFoldCount must be positive when cross-fit metadata is present.");
            if (split.AdaptiveHeadCrossFitFoldStartIndices.Length != split.AdaptiveHeadCrossFitFoldCount ||
                split.AdaptiveHeadCrossFitFoldCounts.Length != split.AdaptiveHeadCrossFitFoldCount ||
                split.AdaptiveHeadCrossFitFoldHashes.Length != split.AdaptiveHeadCrossFitFoldCount)
            {
                issues.Add("TrainingSplitSummary cross-fit metadata array lengths must match AdaptiveHeadCrossFitFoldCount.");
            }
            else
            {
                int foldCountSum = 0;
                for (int i = 0; i < split.AdaptiveHeadCrossFitFoldCount; i++)
                {
                    int foldStart = split.AdaptiveHeadCrossFitFoldStartIndices[i];
                    int foldCount = split.AdaptiveHeadCrossFitFoldCounts[i];
                    if (foldCount <= 0)
                        issues.Add($"TrainingSplitSummary cross-fit fold {i} has a non-positive count.");
                    if (foldStart < split.CalibrationDiagnosticsStartIndex ||
                        foldStart + foldCount > split.CalibrationDiagnosticsStartIndex + split.CalibrationDiagnosticsCount)
                    {
                        issues.Add($"TrainingSplitSummary cross-fit fold {i} falls outside the calibration-diagnostics split.");
                    }
                    foldCountSum += Math.Max(0, foldCount);
                }

                if (foldCountSum != split.CalibrationDiagnosticsCount)
                    issues.Add("TrainingSplitSummary cross-fit fold counts must sum to CalibrationDiagnosticsCount.");
            }
        }
    }

    private static void ValidateCalibrationArtifact(ModelSnapshot snapshot, List<string> issues)
    {
        if (snapshot.FtTransformerCalibrationArtifact is not { } artifact)
            return;

        if (snapshot.TrainingSplitSummary is { } split)
        {
            if (artifact.FitSampleCount > 0 && artifact.FitSampleCount != split.CalibrationFitCount)
                issues.Add("FtTransformerCalibrationArtifact FitSampleCount does not match TrainingSplitSummary.CalibrationFitCount.");
            if (artifact.DiagnosticsSampleCount > 0 && artifact.DiagnosticsSampleCount != split.CalibrationDiagnosticsCount)
                issues.Add("FtTransformerCalibrationArtifact DiagnosticsSampleCount does not match TrainingSplitSummary.CalibrationDiagnosticsCount.");
            if (artifact.ConformalSampleCount > 0 && artifact.ConformalSampleCount != split.ConformalCount)
                issues.Add("FtTransformerCalibrationArtifact ConformalSampleCount does not match TrainingSplitSummary.ConformalCount.");
            if (!string.IsNullOrWhiteSpace(artifact.AdaptiveHeadMode) &&
                !string.Equals(artifact.AdaptiveHeadMode, split.AdaptiveHeadSplitMode, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("FtTransformerCalibrationArtifact AdaptiveHeadMode does not match TrainingSplitSummary.AdaptiveHeadSplitMode.");
            }
            if (artifact.AdaptiveHeadCrossFitFoldCount != split.AdaptiveHeadCrossFitFoldCount)
                issues.Add("FtTransformerCalibrationArtifact AdaptiveHeadCrossFitFoldCount does not match TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount.");
            if (artifact.ThresholdSelectionSampleCount > 0 &&
                split.SelectionThresholdCount > 0 &&
                artifact.ThresholdSelectionSampleCount != split.SelectionThresholdCount)
            {
                issues.Add("FtTransformerCalibrationArtifact ThresholdSelectionSampleCount does not match TrainingSplitSummary.SelectionThresholdCount.");
            }
            if (artifact.KellySelectionSampleCount > 0 &&
                split.SelectionKellyCount > 0 &&
                artifact.KellySelectionSampleCount != split.SelectionKellyCount)
            {
                issues.Add("FtTransformerCalibrationArtifact KellySelectionSampleCount does not match TrainingSplitSummary.SelectionKellyCount.");
            }
        }

        if (artifact.RefitSampleCount > 0 && artifact.RefitSampleCount < artifact.FitSampleCount)
            issues.Add("FtTransformerCalibrationArtifact RefitSampleCount cannot be smaller than FitSampleCount.");
        if (artifact.RoutingThresholdCandidateCount > 0)
        {
            if (artifact.RoutingThresholdCandidates.Length != artifact.RoutingThresholdCandidateCount ||
                artifact.RoutingThresholdCandidateNlls.Length != artifact.RoutingThresholdCandidateCount ||
                artifact.RoutingThresholdCandidateEces.Length != artifact.RoutingThresholdCandidateCount)
            {
                issues.Add("FtTransformerCalibrationArtifact routing-threshold arrays must match RoutingThresholdCandidateCount.");
            }
        }

        int crossFitMetricCount = artifact.SelectedStackCrossFitFoldNlls.Length;
        if (crossFitMetricCount != artifact.SelectedStackCrossFitFoldEces.Length)
            issues.Add("FtTransformerCalibrationArtifact selected-stack cross-fit arrays must have equal lengths.");
        if (artifact.AdaptiveHeadCrossFitFoldCount > 1 && crossFitMetricCount > 0 &&
            crossFitMetricCount != artifact.AdaptiveHeadCrossFitFoldCount)
        {
            issues.Add("FtTransformerCalibrationArtifact selected-stack cross-fit arrays must match AdaptiveHeadCrossFitFoldCount.");
        }

        if (artifact.IsotonicAccepted)
        {
            if (snapshot.IsotonicBreakpoints.Length < 4)
                issues.Add("FtTransformerCalibrationArtifact marked isotonic as accepted but IsotonicBreakpoints is empty.");
            else if (artifact.IsotonicBreakpointCount != snapshot.IsotonicBreakpoints.Length / 2)
                issues.Add("FtTransformerCalibrationArtifact IsotonicBreakpointCount does not match IsotonicBreakpoints.");
        }
        else if (snapshot.IsotonicBreakpoints.Length >= 4)
        {
            issues.Add("IsotonicBreakpoints are present but FtTransformerCalibrationArtifact did not mark isotonic as accepted.");
        }
    }

    private static List<FtSerializedLayerWeights>? TryLoadAdditionalLayers(
        ModelSnapshot snapshot,
        int embedDim,
        int ffnDim,
        List<string> issues)
    {
        if (snapshot.FtTransformerAdditionalLayersBytes is { Length: > 4 } blob)
        {
            try { return FtTransformerAdditionalLayerCodec.DeserializeBinary(blob, embedDim, ffnDim); }
            catch (Exception ex) { issues.Add($"Binary additional layer payload is invalid: {ex.Message}"); }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FtTransformerAdditionalLayersJson))
        {
            try
            {
                var layers = JsonSerializer.Deserialize<List<FtSerializedLayerWeights>>(
                    snapshot.FtTransformerAdditionalLayersJson, FtTransformerAdditionalLayerCodec.JsonOptions);
                return layers ?? [];
            }
            catch (JsonException ex)
            {
                issues.Add($"JSON additional layer payload is invalid: {ex.Message}");
            }
        }

        return null;
    }

    private static void ValidateLayer(
        double[][]? wq,
        double[][]? wk,
        double[][]? wv,
        double[][]? wo,
        double[]? gamma1,
        double[]? beta1,
        double[]? gamma2,
        double[]? beta2,
        double[][]? wff1,
        double[]? bff1,
        double[][]? wff2,
        double[]? bff2,
        double[][]? posBias,
        int embedDim,
        int ffnDim,
        int numHeads,
        int seqSq,
        string layerName,
        List<string> issues)
    {
        ValidateMatrix(wq, embedDim, embedDim, $"{layerName} Wq", issues);
        ValidateMatrix(wk, embedDim, embedDim, $"{layerName} Wk", issues);
        ValidateMatrix(wv, embedDim, embedDim, $"{layerName} Wv", issues);
        ValidateMatrix(wo, embedDim, embedDim, $"{layerName} Wo", issues);
        ValidateVector(gamma1, embedDim, $"{layerName} Gamma1", issues);
        ValidateVector(beta1, embedDim, $"{layerName} Beta1", issues);
        ValidateVector(gamma2, embedDim, $"{layerName} Gamma2", issues);
        ValidateVector(beta2, embedDim, $"{layerName} Beta2", issues);
        ValidateMatrix(wff1, embedDim, ffnDim, $"{layerName} Wff1", issues);
        ValidateVector(bff1, ffnDim, $"{layerName} Bff1", issues);
        ValidateMatrix(wff2, ffnDim, embedDim, $"{layerName} Wff2", issues);
        ValidateVector(bff2, embedDim, $"{layerName} Bff2", issues);

        if (posBias is null)
            return;

        ValidateMatrix(posBias, numHeads, seqSq, $"{layerName} PosBias", issues);
    }

    private static void ValidateMatrix(double[][]? matrix, int expectedRows, int expectedCols, string name, List<string> issues)
    {
        if (matrix is null || matrix.Length != expectedRows)
        {
            issues.Add($"{name} row count is inconsistent.");
            return;
        }

        for (int row = 0; row < matrix.Length; row++)
        {
            if (matrix[row].Length != expectedCols)
            {
                issues.Add($"{name} column count is inconsistent.");
                return;
            }

            if (!matrix[row].All(double.IsFinite))
            {
                issues.Add($"{name} contains a non-finite value.");
                return;
            }
        }
    }

    private static void ValidateVector(double[]? vector, int expectedLength, string name, List<string> issues)
    {
        if (vector is null || vector.Length != expectedLength)
        {
            issues.Add($"{name} length is inconsistent.");
            return;
        }

        if (!vector.All(double.IsFinite))
            issues.Add($"{name} contains a non-finite value.");
    }

    private static string ComputeSha256(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }

    private static string ResolveSnapshotVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? InitialSnapshotVersion : version.Trim();

    private static void UpgradeVersion5To6(ModelSnapshot snapshot, int rawFeatureCount)
    {
        if (!double.IsFinite(snapshot.ConditionalCalibrationRoutingThreshold))
            snapshot.ConditionalCalibrationRoutingThreshold = 0.5;

        snapshot.FtTransformerRawFeatureCount = rawFeatureCount;
        EnsureSelectionWindow(snapshot.TrainingSplitSummary);
    }

    private static void UpgradeVersion6To7(ModelSnapshot snapshot)
        => EnsureCalibrationDiagnosticsWindow(snapshot.TrainingSplitSummary);

    private static void UpgradeVersion7To8(ModelSnapshot snapshot)
    {
        EnsureConformalWindow(snapshot.TrainingSplitSummary);
        snapshot.Version = CurrentSnapshotVersion;
    }

    private static void UpgradeCurrentSnapshot(ModelSnapshot snapshot, int rawFeatureCount)
    {
        snapshot.FtTransformerRawFeatureCount = rawFeatureCount;
        if (!double.IsFinite(snapshot.ConditionalCalibrationRoutingThreshold))
            snapshot.ConditionalCalibrationRoutingThreshold = 0.5;
        EnsureSelectionWindow(snapshot.TrainingSplitSummary);
        EnsureCalibrationDiagnosticsWindow(snapshot.TrainingSplitSummary);
        EnsureConformalWindow(snapshot.TrainingSplitSummary);
    }

    private static void EnsureSelectionWindow(TrainingSplitSummary? split)
    {
        if (split is null)
            return;

        if (split.SelectionCount <= 0 && split.TrainCount > 0)
        {
            split.SelectionStartIndex = split.TrainStartIndex;
            split.SelectionCount = split.TrainCount;
        }
    }

    private static void EnsureCalibrationDiagnosticsWindow(TrainingSplitSummary? split)
    {
        if (split is null)
            return;

        if (split.CalibrationDiagnosticsCount <= 0 && split.CalibrationCount > 0)
        {
            split.CalibrationDiagnosticsStartIndex = split.CalibrationStartIndex;
            split.CalibrationDiagnosticsCount = split.CalibrationCount;
        }
    }

    private static void EnsureConformalWindow(TrainingSplitSummary? split)
    {
        if (split is null)
            return;

        if (split.ConformalCount <= 0 && split.CalibrationDiagnosticsCount > 0)
        {
            split.ConformalStartIndex = split.CalibrationDiagnosticsStartIndex;
            split.ConformalCount = split.CalibrationDiagnosticsCount;
        }
    }
}
