using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class GbmSnapshotSupport
{
    internal readonly record struct ValidationResult(bool IsValid, string[] Issues);

    internal readonly record struct CompatibilityResult(bool IsCompatible, string[] Issues);

    private const int MaxGbmTreeJsonBytes = 5 * 1024 * 1024;
    private const int MaxFeaturePipelineDescriptorCount = 128;
    private const int MaxFeaturePipelineSourceGroupCount = 16_384;
    private const int MaxPartialDependencePairs = 4_096;
    private const int MaxTreeDepth = 64;

    private static readonly JsonSerializerOptions CloneJsonOptions =
        new()
        {
            WriteIndented = false,
            MaxDepth = 128,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
        };

    internal static bool IsGbm(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, "GBM", StringComparison.OrdinalIgnoreCase);

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
        int expectedFeatureCount)
    {
        if (!IsGbm(snapshot))
            return new CompatibilityResult(false, ["Warm-start snapshot is not a GBM snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var validation = ValidateSnapshot(normalized, allowLegacy: true);
        var issues = new List<string>(validation.Issues);

        if (!string.IsNullOrWhiteSpace(expectedFeatureSchemaFingerprint) &&
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
        if (normalized.RawFeatureIndices.Length > 0 && normalized.RawFeatureIndices.Length != expectedFeatureCount)
            issues.Add("Warm-start RawFeatureIndices do not match the current feature count.");
        if (normalized.ActiveFeatureMask.Length > 0 && normalized.ActiveFeatureMask.Length != expectedFeatureCount)
            issues.Add("Warm-start ActiveFeatureMask does not match the current feature count.");

        return new CompatibilityResult(issues.Count == 0, [..issues.Distinct()]);
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames, int rawFeatureCount)
    {
        rawFeatureCount = Math.Max(0, Math.Min(rawFeatureCount, featureNames.Length));
        if (rawFeatureCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("gbm-feature-schema|");
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
        FeatureTransformDescriptor[] descriptors,
        bool[]? activeMask)
    {
        var builder = new StringBuilder();
        builder.Append("gbm-preproc|");
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
        foreach (var descriptor in descriptors.OrderBy(d => d.OutputStartIndex).ThenBy(d => d.Kind, StringComparer.Ordinal))
        {
            builder.Append(descriptor.Kind).Append('|');
            builder.Append(descriptor.Version).Append('|');
            builder.Append(descriptor.Operation).Append('|');
            builder.Append(descriptor.InputFeatureCount).Append('|');
            builder.Append(descriptor.OutputStartIndex).Append('|');
            builder.Append(descriptor.OutputCount).Append('|');
            foreach (var group in descriptor.SourceIndexGroups)
            {
                builder.Append('[');
                for (int i = 0; i < group.Length; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    builder.Append(group[i]);
                }
                builder.Append(']');
            }
            builder.Append('|');
        }

        if (activeMask is { Length: > 0 })
        {
            for (int i = 0; i < activeMask.Length; i++)
                builder.Append(activeMask[i] ? '1' : '0');
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(ModelSnapshot snapshot)
    {
        int rawFeatureCount = snapshot.RawFeatureIndices.Length > 0
            ? snapshot.RawFeatureIndices.Max() + 1
            : snapshot.Features.Length;
        return ComputePreprocessingFingerprint(
            rawFeatureCount,
            snapshot.RawFeatureIndices,
            snapshot.FeaturePipelineDescriptors ?? [],
            snapshot.ActiveFeatureMask);
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int featureCount,
        int numRounds,
        int maxDepth,
        double learningRate)
    {
        string payload = JsonSerializer.Serialize(new
        {
            featureCount,
            numRounds,
            maxDepth,
            learningRate,
            hp.LabelSmoothing,
            hp.TemporalDecayLambda,
            hp.FeatureSampleRatio,
            hp.L2Lambda,
            hp.UseClassWeights,
            hp.GbmRowSubsampleRatio,
            hp.GbmMinSamplesLeaf,
            hp.GbmMinSplitGain,
            hp.GbmMinSplitGainDecayPerDepth,
            hp.GbmShrinkageAnnealing,
            hp.GbmDartDropRate,
            hp.GbmUseHistogramSplits,
            hp.GbmHistogramBins,
            hp.GbmUseLeafWiseGrowth,
            hp.GbmMaxLeaves,
            hp.GbmValCheckFrequency,
            hp.EarlyStoppingPatience,
            hp.GbmInteractionConstraints,
            hp.GbmMetaLabelHiddenDim,
            hp.GbmUseSeparateAbstention,
            hp.AgeDecayLambda,
            hp.ThresholdSearchStepBps,
            hp.BarsPerDay,
        }, CloneJsonOptions);

        return ComputeSha256(payload);
    }

    internal static void UpgradeSnapshotInPlace(ModelSnapshot snapshot)
    {
        if (!IsGbm(snapshot))
            return;

        snapshot.Features ??= [];
        snapshot.Means ??= [];
        snapshot.Stds ??= [];
        snapshot.RawFeatureIndices ??= [];
        snapshot.FeaturePipelineTransforms ??= [];
        snapshot.FeaturePipelineDescriptors ??= [];
        snapshot.ActiveFeatureMask ??= [];
        snapshot.FeatureImportance ??= [];
        snapshot.FeatureImportanceScores ??= [];
        snapshot.GainWeightedImportance ??= [];
        snapshot.MetaLabelWeights ??= [];
        snapshot.MetaLabelHiddenWeights ??= [];
        snapshot.MetaLabelHiddenBiases ??= [];
        snapshot.MetaLabelTopFeatureIndices ??= [];
        snapshot.AbstentionWeights ??= [];
        snapshot.JackknifeResiduals ??= [];
        snapshot.VennAbersMultiP ??= [];
        snapshot.GbmPerTreeLearningRates ??= [];
        snapshot.RedundantFeaturePairs ??= [];
        snapshot.RedundantFeatureDropIndices ??= [];

        int featureCount = snapshot.Features.Length;
        if (featureCount == 0)
            featureCount = Math.Max(snapshot.Means.Length, snapshot.Stds.Length);
        if (featureCount == 0)
            featureCount = Math.Max(snapshot.ActiveFeatureMask.Length, snapshot.FeatureImportance.Length);

        if (snapshot.Features.Length == 0 && featureCount > 0)
        {
            var names = new string[featureCount];
            for (int i = 0; i < featureCount; i++)
                names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
            snapshot.Features = names;
        }

        if (snapshot.RawFeatureIndices.Length == 0 && featureCount > 0)
            snapshot.RawFeatureIndices = Enumerable.Range(0, featureCount).ToArray();

        if (snapshot.ActiveFeatureMask.Length == 0 && featureCount > 0)
        {
            snapshot.ActiveFeatureMask = new bool[featureCount];
            Array.Fill(snapshot.ActiveFeatureMask, true);
            snapshot.PrunedFeatureCount = 0;
        }
        else if (snapshot.ActiveFeatureMask.Length > 0 && snapshot.ActiveFeatureMask.All(active => active))
        {
            snapshot.PrunedFeatureCount = 0;
        }

        if (!double.IsFinite(snapshot.OptimalThreshold) || snapshot.OptimalThreshold < 0.0 || snapshot.OptimalThreshold > 1.0)
            snapshot.OptimalThreshold = 0.5;
        if (!double.IsFinite(snapshot.AbstentionThreshold) || snapshot.AbstentionThreshold < 0.0 || snapshot.AbstentionThreshold > 1.0)
            snapshot.AbstentionThreshold = 0.5;
        if (!double.IsFinite(snapshot.AbstentionThresholdBuy) || snapshot.AbstentionThresholdBuy < 0.0 || snapshot.AbstentionThresholdBuy > 1.0)
            snapshot.AbstentionThresholdBuy = snapshot.AbstentionThreshold;
        if (!double.IsFinite(snapshot.AbstentionThresholdSell) || snapshot.AbstentionThresholdSell < 0.0 || snapshot.AbstentionThresholdSell > 1.0)
            snapshot.AbstentionThresholdSell = snapshot.AbstentionThreshold;
        if (!double.IsFinite(snapshot.ConditionalCalibrationRoutingThreshold) ||
            snapshot.ConditionalCalibrationRoutingThreshold <= 0.0 ||
            snapshot.ConditionalCalibrationRoutingThreshold >= 1.0)
        {
            snapshot.ConditionalCalibrationRoutingThreshold = 0.5;
        }

        snapshot.ConformalQHat = SanitizeProbability(snapshot.ConformalQHat, 0.5);
        snapshot.ConformalQHatBuy = SanitizeProbability(snapshot.ConformalQHatBuy, snapshot.ConformalQHat);
        snapshot.ConformalQHatSell = SanitizeProbability(snapshot.ConformalQHatSell, snapshot.ConformalQHat);
        snapshot.DurbinWatsonStatistic = double.IsFinite(snapshot.DurbinWatsonStatistic)
            ? snapshot.DurbinWatsonStatistic
            : 2.0;

        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) && snapshot.Features.Length > 0)
            snapshot.FeatureSchemaFingerprint = ComputeFeatureSchemaFingerprint(snapshot.Features, snapshot.Features.Length);
        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint) && snapshot.Features.Length > 0)
            snapshot.PreprocessingFingerprint = ComputePreprocessingFingerprint(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint) && snapshot.Features.Length > 0)
        {
            snapshot.TrainerFingerprint = ComputeSha256(string.Join("|",
                "gbm-trainer-legacy",
                snapshot.Version,
                snapshot.Features.Length.ToString(CultureInfo.InvariantCulture),
                snapshot.BaseLearnersK.ToString(CultureInfo.InvariantCulture),
                snapshot.GbmLearningRate.ToString("R", CultureInfo.InvariantCulture)));
        }

        if (snapshot.TrainingRandomSeed <= 0)
            snapshot.TrainingRandomSeed = 1;

        if (snapshot.TrainingSplitSummary is { } split)
        {
            split.AdaptiveHeadCrossFitFoldStartIndices ??= [];
            split.AdaptiveHeadCrossFitFoldCounts ??= [];
            split.AdaptiveHeadCrossFitFoldHashes ??= [];

            if (split.SelectionCount <= 0 && split.CalibrationCount > 0)
            {
                split.SelectionStartIndex = split.CalibrationStartIndex;
                split.SelectionCount = split.CalibrationCount;
            }

            if (split.CalibrationFitCount <= 0 && split.CalibrationCount > 0)
            {
                split.CalibrationFitStartIndex = split.CalibrationStartIndex;
                split.CalibrationFitCount = split.CalibrationCount;
            }

            if (split.CalibrationDiagnosticsCount <= 0 && split.CalibrationCount > 0)
            {
                split.CalibrationDiagnosticsStartIndex = split.CalibrationStartIndex;
                split.CalibrationDiagnosticsCount = split.CalibrationCount;
            }

            if (split.ConformalCount <= 0 && split.CalibrationDiagnosticsCount > 0)
            {
                split.ConformalStartIndex = split.CalibrationDiagnosticsStartIndex;
                split.ConformalCount = split.CalibrationDiagnosticsCount;
            }

            if (split.MetaLabelCount <= 0 && split.CalibrationDiagnosticsCount > 0)
            {
                split.MetaLabelStartIndex = split.CalibrationDiagnosticsStartIndex;
                split.MetaLabelCount = split.CalibrationDiagnosticsCount;
            }

            if (split.AbstentionCount <= 0 && split.MetaLabelCount > 0)
            {
                split.AbstentionStartIndex = split.MetaLabelStartIndex;
                split.AbstentionCount = split.MetaLabelCount;
            }

            if (string.IsNullOrWhiteSpace(split.AdaptiveHeadSplitMode))
                split.AdaptiveHeadSplitMode = "SHARED_FALLBACK";

            snapshot.GbmCalibrationArtifact ??= new GbmCalibrationArtifact
            {
                SelectedGlobalCalibration = snapshot.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
                CalibrationSelectionStrategy = split.CalibrationDiagnosticsCount > 0
                    ? "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS"
                    : "FIT_AND_EVAL_ON_FIT",
                GlobalPlattNll = 0.0,
                TemperatureNll = 0.0,
                TemperatureSelected = snapshot.TemperatureScale > 0.0,
                FitSampleCount = split.CalibrationFitCount,
                DiagnosticsSampleCount = split.CalibrationDiagnosticsCount > 0
                    ? split.CalibrationDiagnosticsCount
                    : split.CalibrationFitCount,
                DiagnosticsSelectedGlobalNll = 0.0,
                DiagnosticsSelectedStackNll = 0.0,
                ConformalSampleCount = split.ConformalCount,
                MetaLabelSampleCount = split.MetaLabelCount,
                AbstentionSampleCount = split.AbstentionCount,
                AdaptiveHeadMode = split.AdaptiveHeadSplitMode,
                AdaptiveHeadCrossFitFoldCount = split.AdaptiveHeadCrossFitFoldCount,
                ConditionalRoutingThreshold = snapshot.ConditionalCalibrationRoutingThreshold,
                BuyBranchAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattABuy, snapshot.PlattBBuy),
                SellBranchAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattASell, snapshot.PlattBSell),
                IsotonicSampleCount = split.CalibrationFitCount,
                IsotonicBreakpointCount = snapshot.IsotonicBreakpoints.Length / 2,
                PreIsotonicNll = 0.0,
                PostIsotonicNll = 0.0,
                IsotonicAccepted = snapshot.IsotonicBreakpoints.Length >= 4,
            };
        }

        if (snapshot.GbmCalibrationArtifact is { } artifact)
        {
            if (string.IsNullOrWhiteSpace(artifact.SelectedGlobalCalibration))
                artifact.SelectedGlobalCalibration = snapshot.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT";
            if (string.IsNullOrWhiteSpace(artifact.CalibrationSelectionStrategy))
                artifact.CalibrationSelectionStrategy = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS";
            if (string.IsNullOrWhiteSpace(artifact.AdaptiveHeadMode))
                artifact.AdaptiveHeadMode = snapshot.TrainingSplitSummary?.AdaptiveHeadSplitMode ?? "SHARED_FALLBACK";
            if (!double.IsFinite(artifact.ConditionalRoutingThreshold))
                artifact.ConditionalRoutingThreshold = snapshot.ConditionalCalibrationRoutingThreshold;
            artifact.TemperatureSelected = artifact.TemperatureSelected || snapshot.TemperatureScale > 0.0;
            if (artifact.IsotonicBreakpointCount <= 0 && snapshot.IsotonicBreakpoints.Length >= 4)
                artifact.IsotonicBreakpointCount = snapshot.IsotonicBreakpoints.Length / 2;
            artifact.IsotonicAccepted = artifact.IsotonicAccepted || snapshot.IsotonicBreakpoints.Length >= 4;
        }
    }

    internal static ValidationResult ValidateSnapshot(ModelSnapshot snapshot, bool allowLegacy = true)
    {
        if (!IsGbm(snapshot))
            return new ValidationResult(false, ["Snapshot is not a GBM snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var issues = new List<string>();

        int featureCount = normalized.Features.Length;
        if (featureCount == 0)
            issues.Add("Features are missing.");
        if (normalized.Means.Length != featureCount)
            issues.Add("Means length does not match feature count.");
        if (normalized.Stds.Length != featureCount)
            issues.Add("Stds length does not match feature count.");
        if (normalized.RawFeatureIndices.Length != featureCount)
            issues.Add("RawFeatureIndices length does not match feature count.");
        if (normalized.RawFeatureIndices.Any(index => index < 0))
            issues.Add("RawFeatureIndices contains a negative index.");
        if (normalized.RawFeatureIndices.Length > 0 &&
            normalized.RawFeatureIndices.Distinct().Count() != normalized.RawFeatureIndices.Length)
        {
            issues.Add("RawFeatureIndices contains duplicate indices.");
        }

        if (normalized.ActiveFeatureMask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match feature count.");
        else if (!normalized.ActiveFeatureMask.Any(active => active))
            issues.Add("ActiveFeatureMask cannot prune every feature.");

        int expectedPrunedCount = normalized.ActiveFeatureMask.Count(active => !active);
        if (normalized.PrunedFeatureCount != expectedPrunedCount)
            issues.Add("PrunedFeatureCount does not match ActiveFeatureMask.");

        if (normalized.FeatureImportance.Length > 0 && normalized.FeatureImportance.Length != featureCount)
            issues.Add("FeatureImportance length does not match feature count.");
        if (normalized.FeatureImportanceScores.Length > 0 && normalized.FeatureImportanceScores.Length != featureCount)
            issues.Add("FeatureImportanceScores length does not match feature count.");
        if (normalized.GainWeightedImportance.Length > 0 && normalized.GainWeightedImportance.Length != featureCount)
            issues.Add("GainWeightedImportance length does not match feature count.");
        if (normalized.MagWeights.Length > 0 && normalized.MagWeights.Length != featureCount)
            issues.Add("MagWeights length does not match feature count.");

        if (!double.IsFinite(normalized.OptimalThreshold) || normalized.OptimalThreshold < 0.0 || normalized.OptimalThreshold > 1.0)
            issues.Add("OptimalThreshold must be a finite probability in [0, 1].");
        if (!double.IsFinite(normalized.ConditionalCalibrationRoutingThreshold) ||
            normalized.ConditionalCalibrationRoutingThreshold <= 0.0 ||
            normalized.ConditionalCalibrationRoutingThreshold >= 1.0)
        {
            issues.Add("ConditionalCalibrationRoutingThreshold must be a finite probability in (0, 1).");
        }
        if (!double.IsFinite(normalized.ConformalQHat) || normalized.ConformalQHat <= 0.0 || normalized.ConformalQHat >= 1.0)
            issues.Add("ConformalQHat must be a finite probability in (0, 1).");
        if (!double.IsFinite(normalized.ConformalQHatBuy) || normalized.ConformalQHatBuy <= 0.0 || normalized.ConformalQHatBuy >= 1.0)
            issues.Add("ConformalQHatBuy must be a finite probability in (0, 1).");
        if (!double.IsFinite(normalized.ConformalQHatSell) || normalized.ConformalQHatSell <= 0.0 || normalized.ConformalQHatSell >= 1.0)
            issues.Add("ConformalQHatSell must be a finite probability in (0, 1).");
        if (!double.IsFinite(normalized.DurbinWatsonStatistic) || normalized.DurbinWatsonStatistic < 0.0)
            issues.Add("DurbinWatsonStatistic must be finite and non-negative.");

        if (normalized.AbstentionWeights.Length > 0 && normalized.AbstentionWeights.Length != 3)
            issues.Add("AbstentionWeights must have length 3 when present.");

        if (normalized.MetaLabelHiddenDim < 0)
            issues.Add("MetaLabelHiddenDim cannot be negative.");
        if (normalized.MetaLabelHiddenDim > 0)
        {
            if (normalized.MetaLabelWeights.Length != normalized.MetaLabelHiddenDim)
                issues.Add("MetaLabelWeights length must match MetaLabelHiddenDim for MLP snapshots.");
            if (normalized.MetaLabelHiddenBiases.Length != normalized.MetaLabelHiddenDim)
                issues.Add("MetaLabelHiddenBiases length must match MetaLabelHiddenDim.");
            if (normalized.MetaLabelHiddenWeights.Length == 0 ||
                normalized.MetaLabelHiddenWeights.Length % normalized.MetaLabelHiddenDim != 0)
            {
                issues.Add("MetaLabelHiddenWeights must contain a full hidden-layer matrix.");
            }
        }
        else if (normalized.MetaLabelHiddenWeights.Length > 0 || normalized.MetaLabelHiddenBiases.Length > 0)
        {
            issues.Add("MetaLabelHidden* arrays must be empty for linear meta-label snapshots.");
        }

        if (normalized.MetaLabelTopFeatureIndices.Any(index => index < 0 || index >= featureCount))
            issues.Add("MetaLabelTopFeatureIndices contains an out-of-range feature index.");

        if (normalized.JackknifeResiduals.Any(value => !double.IsFinite(value) || value < 0.0))
            issues.Add("JackknifeResiduals must be finite and non-negative.");
        for (int i = 1; i < normalized.JackknifeResiduals.Length; i++)
        {
            if (normalized.JackknifeResiduals[i] + 1e-12 < normalized.JackknifeResiduals[i - 1])
            {
                issues.Add("JackknifeResiduals must be sorted in ascending order.");
                break;
            }
        }

        if (normalized.VennAbersMultiP.Any(bounds => bounds is null || bounds.Length != 2 ||
                                                     !double.IsFinite(bounds[0]) || !double.IsFinite(bounds[1])))
        {
            issues.Add("VennAbersMultiP must contain [lower, upper] finite probability pairs.");
        }

        int rawFeatureCount = normalized.RawFeatureIndices.Length > 0
            ? normalized.RawFeatureIndices.Max() + 1
            : featureCount;
        if (normalized.FeaturePipelineDescriptors.Length > MaxFeaturePipelineDescriptorCount)
        {
            issues.Add($"FeaturePipelineDescriptors cannot contain more than {MaxFeaturePipelineDescriptorCount} descriptors.");
        }

        int totalSourceGroups = 0;
        foreach (var descriptor in normalized.FeaturePipelineDescriptors)
        {
            if (descriptor.InputFeatureCount != rawFeatureCount)
                issues.Add("Feature pipeline descriptor input count does not match raw feature count.");
            if (descriptor.SourceIndexGroups.Length == 0)
                issues.Add("Feature pipeline descriptor must declare at least one source group.");

            totalSourceGroups += descriptor.SourceIndexGroups.Length;

            if (string.Equals(descriptor.Operation, FeaturePipelineTransformSupport.GroupSumInPlaceOperation, StringComparison.OrdinalIgnoreCase))
            {
                if (descriptor.OutputStartIndex != 0)
                    issues.Add("In-place feature pipeline descriptors must start at index 0.");
                if (descriptor.OutputCount != rawFeatureCount)
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
                if (group.Length < 2)
                {
                    issues.Add("Feature pipeline descriptor source groups must contain at least two indices.");
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
        if (totalSourceGroups > MaxFeaturePipelineSourceGroupCount)
        {
            issues.Add($"Feature pipeline descriptors cannot declare more than {MaxFeaturePipelineSourceGroupCount} source groups in total.");
        }

        if (normalized.PartialDependenceData.Length > 0)
        {
            if (normalized.MetaLabelTopFeatureIndices.Length > 0 &&
                normalized.PartialDependenceData.Length != normalized.MetaLabelTopFeatureIndices.Length)
            {
                issues.Add("PartialDependenceData length must match MetaLabelTopFeatureIndices when both are present.");
            }

            int totalPairs = 0;
            for (int i = 0; i < normalized.PartialDependenceData.Length; i++)
            {
                var series = normalized.PartialDependenceData[i];
                if (series is null || series.Length == 0)
                    continue;
                if (series.Length % 2 != 0)
                    issues.Add("PartialDependenceData series must contain [x, y] pairs.");
                if (series.Any(value => !double.IsFinite(value)))
                    issues.Add("PartialDependenceData must contain only finite values.");
                totalPairs += series.Length / 2;
            }

            if (totalPairs > MaxPartialDependencePairs)
                issues.Add($"PartialDependenceData cannot exceed {MaxPartialDependencePairs} [x,y] pairs.");
        }

        if (string.IsNullOrWhiteSpace(normalized.GbmTreesJson))
        {
            issues.Add("GbmTreesJson is missing.");
        }
        else
        {
            int treeJsonBytes = Encoding.UTF8.GetByteCount(normalized.GbmTreesJson);
            if (treeJsonBytes > MaxGbmTreeJsonBytes)
                issues.Add($"GbmTreesJson exceeds the {MaxGbmTreeJsonBytes}-byte safety limit.");

            try
            {
                var trees = JsonSerializer.Deserialize<List<GbmTree>>(normalized.GbmTreesJson, CloneJsonOptions);
                if (trees is not { Count: > 0 })
                {
                    issues.Add("GbmTreesJson does not contain any trees.");
                }
                else
                {
                    if (normalized.BaseLearnersK > 0 && normalized.BaseLearnersK != trees.Count)
                        issues.Add("BaseLearnersK does not match the serialized tree count.");
                    if (normalized.GbmPerTreeLearningRates.Length > 0 &&
                        normalized.GbmPerTreeLearningRates.Length != trees.Count)
                    {
                        issues.Add("GbmPerTreeLearningRates length does not match the serialized tree count.");
                    }

                    for (int treeIndex = 0; treeIndex < trees.Count; treeIndex++)
                        ValidateTree(trees[treeIndex], treeIndex, featureCount, issues);
                }
            }
            catch (JsonException)
            {
                issues.Add("GbmTreesJson is not valid GBM tree JSON.");
            }
        }

        if (!string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint))
        {
            string computedFeatureSchema = ComputeFeatureSchemaFingerprint(normalized.Features, featureCount);
            if (!string.Equals(normalized.FeatureSchemaFingerprint, computedFeatureSchema, StringComparison.Ordinal))
                issues.Add("FeatureSchemaFingerprint does not match the serialized feature schema.");
        }

        if (!string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint))
        {
            string computedPreprocessing = ComputePreprocessingFingerprint(normalized);
            if (!string.Equals(normalized.PreprocessingFingerprint, computedPreprocessing, StringComparison.Ordinal))
                issues.Add("PreprocessingFingerprint does not match the serialized preprocessing layout.");
        }

        if (!allowLegacy)
        {
            if (string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint))
                issues.Add("FeatureSchemaFingerprint is missing.");
            if (string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint))
                issues.Add("PreprocessingFingerprint is missing.");
            if (string.IsNullOrWhiteSpace(normalized.TrainerFingerprint))
                issues.Add("TrainerFingerprint is missing.");
            if (normalized.TrainingRandomSeed <= 0)
                issues.Add("TrainingRandomSeed must be positive.");
            if (normalized.TrainingSplitSummary is null)
            {
                issues.Add("TrainingSplitSummary is missing.");
            }
            else
            {
                var split = normalized.TrainingSplitSummary;
                var crossFitFoldStarts = split.AdaptiveHeadCrossFitFoldStartIndices ?? [];
                var crossFitFoldCounts = split.AdaptiveHeadCrossFitFoldCounts ?? [];
                var crossFitFoldHashes = split.AdaptiveHeadCrossFitFoldHashes ?? [];

                if (split.TrainCount <= 0)
                    issues.Add("TrainingSplitSummary.TrainCount must be positive.");
                if (split.SelectionCount <= 0)
                    issues.Add("TrainingSplitSummary.SelectionCount must be positive.");
                if (split.CalibrationCount <= 0)
                    issues.Add("TrainingSplitSummary.CalibrationCount must be positive.");
                if (split.TestCount <= 0)
                    issues.Add("TrainingSplitSummary.TestCount must be positive.");
                if (split.CalibrationFitCount > split.CalibrationCount)
                    issues.Add("TrainingSplitSummary.CalibrationFitCount cannot exceed CalibrationCount.");
                if (split.CalibrationDiagnosticsCount > split.CalibrationCount)
                    issues.Add("TrainingSplitSummary.CalibrationDiagnosticsCount cannot exceed CalibrationCount.");

                bool diagnosticsDisjointFromFit =
                    split.CalibrationDiagnosticsCount > 0 &&
                    split.CalibrationFitCount > 0 &&
                    split.CalibrationDiagnosticsStartIndex >= split.CalibrationFitStartIndex + split.CalibrationFitCount;
                if (split.CalibrationFitCount > 0 &&
                    split.CalibrationDiagnosticsCount > 0 &&
                    diagnosticsDisjointFromFit &&
                    split.CalibrationFitCount + split.CalibrationDiagnosticsCount != split.CalibrationCount)
                {
                    issues.Add("TrainingSplitSummary calibration subsets do not reconcile to CalibrationCount.");
                }

                if (string.IsNullOrWhiteSpace(split.AdaptiveHeadSplitMode))
                    issues.Add("TrainingSplitSummary.AdaptiveHeadSplitMode is missing.");
                if (split.ConformalCount < 0 || split.MetaLabelCount < 0 || split.AbstentionCount < 0)
                    issues.Add("TrainingSplitSummary adaptive-head counts cannot be negative.");
                if (split.AdaptiveHeadCrossFitFoldCount < 0)
                    issues.Add("TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount cannot be negative.");
                if (crossFitFoldStarts.Length != split.AdaptiveHeadCrossFitFoldCount ||
                    crossFitFoldCounts.Length != split.AdaptiveHeadCrossFitFoldCount ||
                    crossFitFoldHashes.Length != split.AdaptiveHeadCrossFitFoldCount)
                {
                    issues.Add("TrainingSplitSummary adaptive-head cross-fit arrays must match AdaptiveHeadCrossFitFoldCount.");
                }

                if (string.Equals(split.AdaptiveHeadSplitMode, "DISJOINT", StringComparison.OrdinalIgnoreCase) &&
                    split.CalibrationDiagnosticsCount > 0 &&
                    split.ConformalCount + split.MetaLabelCount + split.AbstentionCount != split.CalibrationDiagnosticsCount)
                {
                    issues.Add("TrainingSplitSummary DISJOINT adaptive-head slices do not reconcile to CalibrationDiagnosticsCount.");
                }

                if (string.Equals(split.AdaptiveHeadSplitMode, "CONFORMAL_DISJOINT_SHARED_ADAPTIVE", StringComparison.OrdinalIgnoreCase) &&
                    split.CalibrationDiagnosticsCount > 0 &&
                    (split.ConformalCount + split.MetaLabelCount != split.CalibrationDiagnosticsCount ||
                     split.MetaLabelCount != split.AbstentionCount ||
                     split.MetaLabelStartIndex != split.AbstentionStartIndex))
                {
                    issues.Add("TrainingSplitSummary shared adaptive-head slices are inconsistent.");
                }

                if (string.Equals(split.AdaptiveHeadSplitMode, "SHARED_FALLBACK", StringComparison.OrdinalIgnoreCase) &&
                    split.CalibrationDiagnosticsCount > 0 &&
                    (split.ConformalCount != split.CalibrationDiagnosticsCount ||
                     split.MetaLabelCount != split.CalibrationDiagnosticsCount ||
                     split.AbstentionCount != split.CalibrationDiagnosticsCount ||
                     split.ConformalStartIndex != split.CalibrationDiagnosticsStartIndex ||
                     split.MetaLabelStartIndex != split.CalibrationDiagnosticsStartIndex ||
                     split.AbstentionStartIndex != split.CalibrationDiagnosticsStartIndex))
                {
                    issues.Add("TrainingSplitSummary SHARED_FALLBACK adaptive-head slices must match CalibrationDiagnostics.");
                }
            }

            if (normalized.GbmCalibrationArtifact is null)
            {
                issues.Add("GbmCalibrationArtifact is missing.");
            }
            else
            {
                ValidateCalibrationArtifact(normalized, normalized.GbmCalibrationArtifact, issues);
            }
        }

        return new ValidationResult(issues.Count == 0, [..issues.Distinct()]);
    }

    private static void ValidateCalibrationArtifact(
        ModelSnapshot snapshot,
        GbmCalibrationArtifact artifact,
        ICollection<string> issues)
    {
        if (string.IsNullOrWhiteSpace(artifact.SelectedGlobalCalibration))
            issues.Add("GbmCalibrationArtifact.SelectedGlobalCalibration is missing.");
        if (string.IsNullOrWhiteSpace(artifact.CalibrationSelectionStrategy))
            issues.Add("GbmCalibrationArtifact.CalibrationSelectionStrategy is missing.");
        if (string.IsNullOrWhiteSpace(artifact.AdaptiveHeadMode))
            issues.Add("GbmCalibrationArtifact.AdaptiveHeadMode is missing.");
        if (!double.IsFinite(artifact.GlobalPlattNll) || artifact.GlobalPlattNll < 0.0)
            issues.Add("GbmCalibrationArtifact.GlobalPlattNll must be finite and non-negative.");
        if (!double.IsFinite(artifact.TemperatureNll) || artifact.TemperatureNll < 0.0)
            issues.Add("GbmCalibrationArtifact.TemperatureNll must be finite and non-negative.");
        if (!double.IsFinite(artifact.DiagnosticsSelectedGlobalNll) || artifact.DiagnosticsSelectedGlobalNll < 0.0)
            issues.Add("GbmCalibrationArtifact.DiagnosticsSelectedGlobalNll must be finite and non-negative.");
        if (!double.IsFinite(artifact.DiagnosticsSelectedStackNll) || artifact.DiagnosticsSelectedStackNll < 0.0)
            issues.Add("GbmCalibrationArtifact.DiagnosticsSelectedStackNll must be finite and non-negative.");
        if (!double.IsFinite(artifact.PreIsotonicNll) || artifact.PreIsotonicNll < 0.0)
            issues.Add("GbmCalibrationArtifact.PreIsotonicNll must be finite and non-negative.");
        if (!double.IsFinite(artifact.PostIsotonicNll) || artifact.PostIsotonicNll < 0.0)
            issues.Add("GbmCalibrationArtifact.PostIsotonicNll must be finite and non-negative.");

        if (snapshot.TrainingSplitSummary is { } split)
        {
            int expectedDiagnosticsCount = split.CalibrationDiagnosticsCount > 0
                ? split.CalibrationDiagnosticsCount
                : split.CalibrationFitCount;

            if (artifact.FitSampleCount != split.CalibrationFitCount)
                issues.Add("GbmCalibrationArtifact.FitSampleCount does not match TrainingSplitSummary.CalibrationFitCount.");
            if (artifact.DiagnosticsSampleCount != expectedDiagnosticsCount)
                issues.Add("GbmCalibrationArtifact.DiagnosticsSampleCount does not match the calibration evaluation slice.");
            if (artifact.ConformalSampleCount != split.ConformalCount)
                issues.Add("GbmCalibrationArtifact.ConformalSampleCount does not match TrainingSplitSummary.ConformalCount.");
            if (artifact.MetaLabelSampleCount != split.MetaLabelCount)
                issues.Add("GbmCalibrationArtifact.MetaLabelSampleCount does not match TrainingSplitSummary.MetaLabelCount.");
            if (artifact.AbstentionSampleCount != split.AbstentionCount)
                issues.Add("GbmCalibrationArtifact.AbstentionSampleCount does not match TrainingSplitSummary.AbstentionCount.");
            if (!string.Equals(artifact.AdaptiveHeadMode, split.AdaptiveHeadSplitMode, StringComparison.OrdinalIgnoreCase))
                issues.Add("GbmCalibrationArtifact.AdaptiveHeadMode does not match TrainingSplitSummary.AdaptiveHeadSplitMode.");
            if (artifact.AdaptiveHeadCrossFitFoldCount != split.AdaptiveHeadCrossFitFoldCount)
                issues.Add("GbmCalibrationArtifact.AdaptiveHeadCrossFitFoldCount does not match TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount.");
            if (artifact.IsotonicSampleCount != split.CalibrationFitCount)
                issues.Add("GbmCalibrationArtifact.IsotonicSampleCount does not match TrainingSplitSummary.CalibrationFitCount.");
        }

        if (!double.IsFinite(artifact.ConditionalRoutingThreshold) ||
            Math.Abs(artifact.ConditionalRoutingThreshold - snapshot.ConditionalCalibrationRoutingThreshold) > 1e-9)
        {
            issues.Add("GbmCalibrationArtifact.ConditionalRoutingThreshold does not match snapshot routing threshold.");
        }

        bool temperatureSelected = snapshot.TemperatureScale > 0.0;
        if (artifact.TemperatureSelected != temperatureSelected)
            issues.Add("GbmCalibrationArtifact.TemperatureSelected does not match snapshot TemperatureScale.");
        if (temperatureSelected &&
            !string.Equals(artifact.SelectedGlobalCalibration, "TEMPERATURE", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("GbmCalibrationArtifact.SelectedGlobalCalibration must be TEMPERATURE when TemperatureScale > 0.");
        }
        if (!temperatureSelected &&
            !string.Equals(artifact.SelectedGlobalCalibration, "PLATT", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("GbmCalibrationArtifact.SelectedGlobalCalibration must be PLATT when TemperatureScale == 0.");
        }

        bool buyAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattABuy, snapshot.PlattBBuy);
        bool sellAccepted = InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattASell, snapshot.PlattBSell);
        if (artifact.BuyBranchAccepted != buyAccepted)
            issues.Add("GbmCalibrationArtifact.BuyBranchAccepted does not match the serialized buy conditional calibrator.");
        if (artifact.SellBranchAccepted != sellAccepted)
            issues.Add("GbmCalibrationArtifact.SellBranchAccepted does not match the serialized sell conditional calibrator.");

        int expectedBreakpointCount = snapshot.IsotonicBreakpoints.Length / 2;
        if (artifact.IsotonicBreakpointCount != expectedBreakpointCount)
            issues.Add("GbmCalibrationArtifact.IsotonicBreakpointCount does not match serialized isotonic breakpoints.");
        bool isotonicAccepted = snapshot.IsotonicBreakpoints.Length >= 4;
        if (artifact.IsotonicAccepted != isotonicAccepted)
            issues.Add("GbmCalibrationArtifact.IsotonicAccepted does not match serialized isotonic calibration state.");
        if (artifact.IsotonicAccepted && artifact.PostIsotonicNll > artifact.PreIsotonicNll + 1e-6)
            issues.Add("GbmCalibrationArtifact accepted isotonic calibration despite worse diagnostics NLL.");
    }

    private static void ValidateTree(GbmTree tree, int treeIndex, int featureCount, ICollection<string> issues)
    {
        if (tree.Nodes is not { Count: > 0 } nodes)
        {
            issues.Add($"GbmTreesJson tree[{treeIndex}] does not contain any nodes.");
            return;
        }

        var state = new byte[nodes.Count];
        var indegree = new int[nodes.Count];

        void Visit(int nodeIndex, int depth)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Count)
                return;

            if (depth > MaxTreeDepth)
            {
                issues.Add($"GbmTreesJson tree[{treeIndex}] exceeds the maximum supported depth of {MaxTreeDepth}.");
                return;
            }

            if (state[nodeIndex] == 1)
            {
                issues.Add($"GbmTreesJson tree[{treeIndex}] contains a cycle involving node[{nodeIndex}].");
                return;
            }
            if (state[nodeIndex] == 2)
                return;

            state[nodeIndex] = 1;

            var node = nodes[nodeIndex];

            if (!double.IsFinite(node.LeafValue))
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] has a non-finite leaf value.");
            if (!double.IsFinite(node.SplitThreshold))
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] has a non-finite split threshold.");
            if (!double.IsFinite(node.SplitGain))
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] has a non-finite split gain.");

            if (node.IsLeaf)
            {
                if (node.LeftChild >= 0 || node.RightChild >= 0)
                    issues.Add($"GbmTreesJson tree[{treeIndex}] leaf node[{nodeIndex}] cannot reference children.");
                state[nodeIndex] = 2;
                return;
            }

            if (node.SplitFeature < 0 || node.SplitFeature >= featureCount)
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] has an out-of-range split feature.");
            if (node.LeftChild < 0 || node.LeftChild >= nodes.Count || node.RightChild < 0 || node.RightChild >= nodes.Count)
            {
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] references an out-of-range child.");
                state[nodeIndex] = 2;
                return;
            }

            if (node.LeftChild == nodeIndex || node.RightChild == nodeIndex || node.LeftChild == node.RightChild)
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{nodeIndex}] has an invalid child layout.");

            indegree[node.LeftChild]++;
            if (indegree[node.LeftChild] > 1)
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{node.LeftChild}] has multiple parents.");

            indegree[node.RightChild]++;
            if (indegree[node.RightChild] > 1)
                issues.Add($"GbmTreesJson tree[{treeIndex}] node[{node.RightChild}] has multiple parents.");

            Visit(node.LeftChild, depth + 1);
            Visit(node.RightChild, depth + 1);

            state[nodeIndex] = 2;
        }

        Visit(0, 1);

        if (indegree[0] != 0)
            issues.Add($"GbmTreesJson tree[{treeIndex}] root node must not have any parents.");

        if (state.Any(value => value == 0))
            issues.Add($"GbmTreesJson tree[{treeIndex}] contains unreachable nodes after root traversal.");
    }

    private static double SanitizeProbability(double value, double fallback)
    {
        if (!double.IsFinite(value) || value <= 0.0 || value >= 1.0)
            return Math.Clamp(fallback, 1e-6, 1.0 - 1e-6);
        return value;
    }

    private static string ComputeSha256(string payload)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
