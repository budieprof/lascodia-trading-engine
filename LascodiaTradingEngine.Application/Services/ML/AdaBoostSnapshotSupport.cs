using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class AdaBoostSnapshotSupport
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

    internal static bool IsAdaBoost(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, "AdaBoost", StringComparison.OrdinalIgnoreCase);

    internal static ModelSnapshot NormalizeSnapshotCopy(ModelSnapshot snapshot)
    {
        byte[] cloneBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, CloneJsonOptions);
        var clone = JsonSerializer.Deserialize<ModelSnapshot>(cloneBytes, CloneJsonOptions) ?? new ModelSnapshot();
        UpgradeSnapshotInPlace(clone);
        return clone;
    }

    internal static string[] ResolveFeatureNames(int featureCount)
    {
        var names = new string[featureCount];
        for (int i = 0; i < featureCount; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames)
    {
        if (featureNames.Length == 0)
            return string.Empty;

        var builder = new StringBuilder("adaboost-feature-schema|");
        builder.Append(featureNames.Length).Append('|');
        for (int i = 0; i < featureNames.Length; i++)
            builder.Append(featureNames[i]).Append('|');
        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(int featureCount, bool[]? activeMask)
    {
        if (featureCount <= 0)
            return string.Empty;

        var builder = new StringBuilder("adaboost-preprocessing|");
        builder.Append(featureCount).Append('|');
        if (activeMask is { Length: > 0 })
        {
            for (int i = 0; i < activeMask.Length; i++)
                builder.Append(activeMask[i] ? '1' : '0');
        }
        else
        {
            builder.Append("identity");
        }

        return ComputeSha256(builder.ToString());
    }

    internal static string ComputePreprocessingFingerprint(ModelSnapshot snapshot)
    {
        int featureCount = ResolveFeatureCount(snapshot);
        return ComputePreprocessingFingerprint(featureCount, snapshot.ActiveFeatureMask);
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int featureCount,
        int numRounds,
        int treeDepth)
    {
        string payload = JsonSerializer.Serialize(new
        {
            featureCount,
            numRounds,
            treeDepth,
            hp.LabelSmoothing,
            hp.TemporalDecayLambda,
            hp.AgeDecayLambda,
            hp.ThresholdSearchStepBps,
            hp.ThresholdSearchMin,
            hp.ThresholdSearchMax,
            hp.BarsPerDay,
            hp.AdaBoostAlphaShrinkage,
            hp.AdaBoostMaxTreeDepth,
            hp.AdaBoostWarmStartRoundsFraction,
            hp.UseSammeR,
            hp.UseJointDepth2Search,
            hp.MagnitudeQuantileTau,
            hp.UseAdaptiveLabelSmoothing,
            hp.FitTemperatureScale,
            hp.ConformalCoverage,
            hp.DensityRatioWindowDays,
            hp.UseCovariateShiftWeights,
            hp.LearningRate,
            hp.L2Lambda,
            hp.MaxEpochs,
            hp.EarlyStoppingPatience,
        }, CloneJsonOptions);

        return ComputeSha256(payload);
    }

    internal static CompatibilityResult AssessWarmStartCompatibility(
        ModelSnapshot snapshot,
        string[] expectedFeatureNames,
        string expectedFeatureSchemaFingerprint,
        string expectedPreprocessingFingerprint,
        string expectedTrainerFingerprint,
        int expectedFeatureCount)
    {
        if (!IsAdaBoost(snapshot))
            return new CompatibilityResult(false, ["Warm-start snapshot is not an AdaBoost snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var validation = ValidateSnapshot(normalized, allowLegacy: true);
        var issues = new List<string>(validation.Issues);

        if (normalized.Features.Length != expectedFeatureCount)
            issues.Add("Warm-start feature count does not match the current feature count.");
        if (normalized.Means.Length != expectedFeatureCount || normalized.Stds.Length != expectedFeatureCount)
            issues.Add("Warm-start standardization vectors do not match the current feature count.");
        if (normalized.ActiveFeatureMask.Length > 0 && normalized.ActiveFeatureMask.Length != expectedFeatureCount)
            issues.Add("Warm-start ActiveFeatureMask does not match the current feature count.");

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

        if (expectedFeatureNames.Length > 0 &&
            (normalized.Features.Length != expectedFeatureNames.Length ||
             !normalized.Features.SequenceEqual(expectedFeatureNames)))
        {
            issues.Add("Warm-start feature names do not match the current feature layout.");
        }

        return new CompatibilityResult(issues.Count == 0, [.. issues.Distinct()]);
    }

    internal static void UpgradeSnapshotInPlace(ModelSnapshot snapshot)
    {
        if (!IsAdaBoost(snapshot))
            return;

        snapshot.Features ??= [];
        snapshot.Means ??= [];
        snapshot.Stds ??= [];
        snapshot.Weights ??= [];
        snapshot.ActiveFeatureMask ??= [];
        snapshot.FeatureImportance ??= [];
        snapshot.FeatureImportanceScores ??= [];
        snapshot.FeatureQuantileBreakpoints ??= [];
        snapshot.IsotonicBreakpoints ??= [];
        snapshot.JackknifeResiduals ??= [];
        snapshot.MetaLabelWeights ??= [];
        snapshot.MetaLabelHiddenWeights ??= [];
        snapshot.MetaLabelHiddenBiases ??= [];
        snapshot.MetaLabelTopFeatureIndices ??= [];
        snapshot.AbstentionWeights ??= [];
        snapshot.MagWeights ??= [];
        snapshot.MagQ90Weights ??= [];
        snapshot.RedundantFeaturePairs ??= [];

        int featureCount = ResolveFeatureCount(snapshot);
        if (snapshot.Features.Length == 0 && featureCount > 0)
            snapshot.Features = ResolveFeatureNames(featureCount);

        if (snapshot.ActiveFeatureMask.Length == 0 && featureCount > 0)
        {
            snapshot.ActiveFeatureMask = new bool[featureCount];
            Array.Fill(snapshot.ActiveFeatureMask, true);
            snapshot.PrunedFeatureCount = 0;
        }
        else if (snapshot.ActiveFeatureMask.Length > 0 && snapshot.ActiveFeatureMask.All(static active => active))
        {
            snapshot.PrunedFeatureCount = 0;
        }
        else if (snapshot.ActiveFeatureMask.Length > 0)
        {
            snapshot.PrunedFeatureCount = snapshot.ActiveFeatureMask.Count(static active => !active);
        }

        if (!double.IsFinite(snapshot.OptimalThreshold) || snapshot.OptimalThreshold < 0.0 || snapshot.OptimalThreshold > 1.0)
            snapshot.OptimalThreshold = 0.5;
        if (!double.IsFinite(snapshot.MetaLabelThreshold) || snapshot.MetaLabelThreshold < 0.0 || snapshot.MetaLabelThreshold > 1.0)
            snapshot.MetaLabelThreshold = 0.5;
        if (!double.IsFinite(snapshot.AbstentionThreshold) || snapshot.AbstentionThreshold < 0.0 || snapshot.AbstentionThreshold > 1.0)
            snapshot.AbstentionThreshold = 0.5;
        if (!double.IsFinite(snapshot.ConditionalCalibrationRoutingThreshold) ||
            snapshot.ConditionalCalibrationRoutingThreshold <= 0.0 ||
            snapshot.ConditionalCalibrationRoutingThreshold >= 1.0)
        {
            snapshot.ConditionalCalibrationRoutingThreshold = 0.5;
        }

        if (!double.IsFinite(snapshot.TemperatureScale) || snapshot.TemperatureScale < 0.0)
            snapshot.TemperatureScale = 0.0;
        if (!double.IsFinite(snapshot.DurbinWatsonStatistic) || snapshot.DurbinWatsonStatistic < 0.0)
            snapshot.DurbinWatsonStatistic = 2.0;
        if (!double.IsFinite(snapshot.Ece) || snapshot.Ece < 0.0)
            snapshot.Ece = 1.0;

        snapshot.ConformalQHat = SanitizeProbability(snapshot.ConformalQHat, 0.5);
        snapshot.ConformalQHatBuy = SanitizeProbability(snapshot.ConformalQHatBuy, snapshot.ConformalQHat);
        snapshot.ConformalQHatSell = SanitizeProbability(snapshot.ConformalQHatSell, snapshot.ConformalQHat);

        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) && snapshot.Features.Length > 0)
            snapshot.FeatureSchemaFingerprint = ComputeFeatureSchemaFingerprint(snapshot.Features);
        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint) && featureCount > 0)
            snapshot.PreprocessingFingerprint = ComputePreprocessingFingerprint(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint) && featureCount > 0)
        {
            snapshot.TrainerFingerprint = ComputeSha256(string.Join("|",
                "adaboost-trainer-legacy",
                snapshot.Version,
                featureCount.ToString(CultureInfo.InvariantCulture),
                snapshot.BaseLearnersK.ToString(CultureInfo.InvariantCulture)));
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

            if (split.SelectionPruningCount <= 0 && split.SelectionCount > 0)
            {
                split.SelectionPruningStartIndex = split.SelectionStartIndex;
                split.SelectionPruningCount = split.SelectionCount;
            }

            if (split.SelectionThresholdCount <= 0 && split.SelectionCount > 0)
            {
                split.SelectionThresholdStartIndex = split.SelectionStartIndex;
                split.SelectionThresholdCount = split.SelectionCount;
            }

            if (split.SelectionKellyCount <= 0 && split.SelectionThresholdCount > 0)
            {
                split.SelectionKellyStartIndex = split.SelectionThresholdStartIndex;
                split.SelectionKellyCount = split.SelectionThresholdCount;
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

            if (split.MetaLabelCount <= 0 && snapshot.MetaLabelWeights.Length > 0 && split.CalibrationDiagnosticsCount > 0)
            {
                split.MetaLabelStartIndex = split.CalibrationDiagnosticsStartIndex;
                split.MetaLabelCount = split.CalibrationDiagnosticsCount;
            }

            if (split.AbstentionCount <= 0 && snapshot.AbstentionWeights.Length > 0)
            {
                split.AbstentionStartIndex = split.MetaLabelCount > 0 ? split.MetaLabelStartIndex : split.CalibrationDiagnosticsStartIndex;
                split.AbstentionCount = split.MetaLabelCount > 0 ? split.MetaLabelCount : split.CalibrationDiagnosticsCount;
            }

            if (string.IsNullOrWhiteSpace(split.AdaptiveHeadSplitMode))
                split.AdaptiveHeadSplitMode = "SHARED";

            snapshot.AdaBoostCalibrationArtifact ??= new AdaBoostCalibrationArtifact
            {
                SelectedGlobalCalibration = snapshot.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT",
                CalibrationSelectionStrategy = "FIT_AND_EVAL_ON_SHARED_CALIBRATION",
                TemperatureSelected = snapshot.TemperatureScale > 0.0,
                FitSampleCount = split.CalibrationFitCount,
                DiagnosticsSampleCount = split.CalibrationDiagnosticsCount > 0
                    ? split.CalibrationDiagnosticsCount
                    : split.CalibrationFitCount,
                ThresholdSelectionSampleCount = split.SelectionThresholdCount,
                KellySelectionSampleCount = split.SelectionThresholdCount,
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
                IsotonicAccepted = snapshot.IsotonicBreakpoints.Length >= 4,
            };
        }

        if (snapshot.AdaBoostCalibrationArtifact is { } artifact)
        {
            if (string.IsNullOrWhiteSpace(artifact.SelectedGlobalCalibration))
                artifact.SelectedGlobalCalibration = snapshot.TemperatureScale > 0.0 ? "TEMPERATURE" : "PLATT";
            if (string.IsNullOrWhiteSpace(artifact.CalibrationSelectionStrategy))
                artifact.CalibrationSelectionStrategy = "FIT_AND_EVAL_ON_SHARED_CALIBRATION";
            if (string.IsNullOrWhiteSpace(artifact.AdaptiveHeadMode))
                artifact.AdaptiveHeadMode = snapshot.TrainingSplitSummary?.AdaptiveHeadSplitMode ?? "SHARED";
            if (!double.IsFinite(artifact.ConditionalRoutingThreshold) ||
                artifact.ConditionalRoutingThreshold <= 0.0 ||
                artifact.ConditionalRoutingThreshold >= 1.0)
            {
                artifact.ConditionalRoutingThreshold = snapshot.ConditionalCalibrationRoutingThreshold;
            }
            artifact.TemperatureSelected = artifact.TemperatureSelected || snapshot.TemperatureScale > 0.0;
            if (artifact.IsotonicBreakpointCount <= 0 && snapshot.IsotonicBreakpoints.Length >= 4)
                artifact.IsotonicBreakpointCount = snapshot.IsotonicBreakpoints.Length / 2;
            artifact.IsotonicAccepted = artifact.IsotonicAccepted || snapshot.IsotonicBreakpoints.Length >= 4;
        }

        if (snapshot.AdaBoostAuditArtifact is { } audit)
        {
            audit.Findings ??= [];
            if (string.IsNullOrWhiteSpace(audit.FeatureSchemaFingerprint))
                audit.FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint;
            if (string.IsNullOrWhiteSpace(audit.PreprocessingFingerprint))
                audit.PreprocessingFingerprint = snapshot.PreprocessingFingerprint;
            if (!double.IsFinite(audit.RecordedEce) || audit.RecordedEce < 0.0)
                audit.RecordedEce = snapshot.Ece;
        }
    }

    internal static ValidationResult ValidateSnapshot(ModelSnapshot snapshot, bool allowLegacy = true)
    {
        if (!IsAdaBoost(snapshot))
            return new ValidationResult(false, ["Snapshot is not an AdaBoost snapshot."]);

        var normalized = NormalizeSnapshotCopy(snapshot);
        var issues = new List<string>();

        int featureCount = normalized.Features.Length;
        if (featureCount == 0)
            issues.Add("Features are missing.");
        if (normalized.Means.Length != featureCount)
            issues.Add("Means length does not match feature count.");
        if (normalized.Stds.Length != featureCount)
            issues.Add("Stds length does not match feature count.");
        if (normalized.ActiveFeatureMask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match feature count.");
        else if (!normalized.ActiveFeatureMask.Any(static active => active))
            issues.Add("ActiveFeatureMask cannot prune every feature.");

        int expectedPrunedCount = normalized.ActiveFeatureMask.Count(static active => !active);
        if (normalized.PrunedFeatureCount != expectedPrunedCount)
            issues.Add("PrunedFeatureCount does not match ActiveFeatureMask.");

        if (normalized.FeatureImportance.Length > 0 && normalized.FeatureImportance.Length != featureCount)
            issues.Add("FeatureImportance length does not match feature count.");
        if (normalized.FeatureImportanceScores.Length > 0 && normalized.FeatureImportanceScores.Length != featureCount)
            issues.Add("FeatureImportanceScores length does not match feature count.");
        if (normalized.MagWeights.Length > 0 && normalized.MagWeights.Length != featureCount)
            issues.Add("MagWeights length does not match feature count.");
        if (normalized.MagQ90Weights.Length > 0 && normalized.MagQ90Weights.Length != featureCount)
            issues.Add("MagQ90Weights length does not match feature count.");
        if (normalized.FeatureQuantileBreakpoints.Length > 0 &&
            normalized.FeatureQuantileBreakpoints.Length != featureCount)
        {
            issues.Add("FeatureQuantileBreakpoints length does not match feature count.");
        }

        if (!double.IsFinite(normalized.OptimalThreshold) || normalized.OptimalThreshold < 0.0 || normalized.OptimalThreshold > 1.0)
            issues.Add("OptimalThreshold must be a finite probability in [0, 1].");
        if (!double.IsFinite(normalized.MetaLabelThreshold) || normalized.MetaLabelThreshold < 0.0 || normalized.MetaLabelThreshold > 1.0)
            issues.Add("MetaLabelThreshold must be a finite probability in [0, 1].");
        if (!double.IsFinite(normalized.AbstentionThreshold) || normalized.AbstentionThreshold < 0.0 || normalized.AbstentionThreshold > 1.0)
            issues.Add("AbstentionThreshold must be a finite probability in [0, 1].");
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
        if (!double.IsFinite(normalized.TemperatureScale) || normalized.TemperatureScale < 0.0)
            issues.Add("TemperatureScale must be finite and non-negative.");

        if (normalized.AbstentionWeights.Length > 0 &&
            normalized.AbstentionWeights.Length is not (1 or 3 or 5))
        {
            issues.Add("AbstentionWeights must have length 1, 3, or 5 when present.");
        }

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

        if (string.IsNullOrWhiteSpace(normalized.GbmTreesJson))
        {
            issues.Add("GbmTreesJson is missing.");
        }
        else
        {
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
                    if (normalized.Weights is not { Length: > 0 } || normalized.Weights[0].Length != trees.Count)
                        issues.Add("Weights[0] must contain one alpha per serialized tree.");
                    else if (normalized.Weights[0].Any(alpha => !double.IsFinite(alpha)))
                        issues.Add("Weights[0] contains a non-finite alpha value.");

                    for (int treeIndex = 0; treeIndex < trees.Count; treeIndex++)
                        ValidateTree(trees[treeIndex], treeIndex, featureCount, issues);
                }
            }
            catch (JsonException)
            {
                issues.Add("GbmTreesJson is not valid AdaBoost tree JSON.");
            }
        }

        if (!string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint))
        {
            string computedFeatureSchema = ComputeFeatureSchemaFingerprint(normalized.Features);
            if (!string.Equals(normalized.FeatureSchemaFingerprint, computedFeatureSchema, StringComparison.Ordinal))
                issues.Add("FeatureSchemaFingerprint does not match the serialized feature schema.");
        }

        if (!string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint))
        {
            string computedPreprocessing = ComputePreprocessingFingerprint(normalized);
            if (!string.Equals(normalized.PreprocessingFingerprint, computedPreprocessing, StringComparison.Ordinal))
                issues.Add("PreprocessingFingerprint does not match the serialized preprocessing layout.");
        }

        if (normalized.AdaBoostSelectionMetrics is { } selectionMetrics)
            ValidateMetricSummary(selectionMetrics, "AdaBoostSelectionMetrics", issues);
        if (normalized.AdaBoostCalibrationMetrics is { } calibrationMetrics)
            ValidateMetricSummary(calibrationMetrics, "AdaBoostCalibrationMetrics", issues);
        if (normalized.AdaBoostTestMetrics is { } testMetrics)
            ValidateMetricSummary(testMetrics, "AdaBoostTestMetrics", issues);

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
                if (split.TrainCount <= 0)
                    issues.Add("TrainingSplitSummary.TrainCount must be positive.");
                if (split.SelectionCount <= 0)
                    issues.Add("TrainingSplitSummary.SelectionCount must be positive.");
                if (split.SelectionPruningCount <= 0)
                    issues.Add("TrainingSplitSummary.SelectionPruningCount must be positive.");
                if (split.SelectionThresholdCount <= 0)
                    issues.Add("TrainingSplitSummary.SelectionThresholdCount must be positive.");
                if (split.CalibrationCount <= 0)
                    issues.Add("TrainingSplitSummary.CalibrationCount must be positive.");
                if (split.CalibrationFitCount <= 0)
                    issues.Add("TrainingSplitSummary.CalibrationFitCount must be positive.");
                if (split.CalibrationDiagnosticsCount <= 0)
                    issues.Add("TrainingSplitSummary.CalibrationDiagnosticsCount must be positive.");
                if (split.ConformalCount <= 0)
                    issues.Add("TrainingSplitSummary.ConformalCount must be positive.");
                if (normalized.MetaLabelWeights.Length > 0 && split.MetaLabelCount <= 0)
                    issues.Add("TrainingSplitSummary.MetaLabelCount must be positive when meta-label weights are present.");
                if (normalized.AbstentionWeights.Length > 0 && split.AbstentionCount <= 0)
                    issues.Add("TrainingSplitSummary.AbstentionCount must be positive when abstention weights are present.");
                if (split.TestCount <= 0)
                    issues.Add("TrainingSplitSummary.TestCount must be positive.");
            }

            if (normalized.AdaBoostSelectionMetrics is null)
                issues.Add("AdaBoostSelectionMetrics is missing.");
            if (normalized.AdaBoostCalibrationMetrics is null)
                issues.Add("AdaBoostCalibrationMetrics is missing.");
            if (normalized.AdaBoostTestMetrics is null)
                issues.Add("AdaBoostTestMetrics is missing.");

            if (normalized.AdaBoostCalibrationArtifact is null)
            {
                issues.Add("AdaBoostCalibrationArtifact is missing.");
            }
            else if (normalized.TrainingSplitSummary is not null)
            {
                if (normalized.AdaBoostCalibrationArtifact.ThresholdSelectionSampleCount > 0 &&
                    normalized.AdaBoostCalibrationArtifact.ThresholdSelectionSampleCount !=
                    normalized.TrainingSplitSummary.SelectionThresholdCount)
                {
                    issues.Add("AdaBoostCalibrationArtifact threshold-selection sample count does not match TrainingSplitSummary.");
                }

                if (normalized.AdaBoostCalibrationArtifact.MetaLabelSampleCount > 0 &&
                    normalized.AdaBoostCalibrationArtifact.MetaLabelSampleCount !=
                    normalized.TrainingSplitSummary.MetaLabelCount)
                {
                    issues.Add("AdaBoostCalibrationArtifact meta-label sample count does not match TrainingSplitSummary.");
                }

                if (normalized.AdaBoostCalibrationArtifact.AbstentionSampleCount > 0 &&
                    normalized.AdaBoostCalibrationArtifact.AbstentionSampleCount !=
                    normalized.TrainingSplitSummary.AbstentionCount)
                {
                    issues.Add("AdaBoostCalibrationArtifact abstention sample count does not match TrainingSplitSummary.");
                }
            }

            if (normalized.AdaBoostAuditArtifact is null)
            {
                issues.Add("AdaBoostAuditArtifact is missing.");
            }
            else
            {
                if (!normalized.AdaBoostAuditArtifact.SnapshotContractValid)
                    issues.Add("AdaBoostAuditArtifact reported an invalid snapshot contract.");
            }
        }

        return new ValidationResult(issues.Count == 0, [.. issues.Distinct()]);
    }

    private static int ResolveFeatureCount(ModelSnapshot snapshot)
    {
        return new[]
        {
            snapshot.Features?.Length ?? 0,
            snapshot.Means?.Length ?? 0,
            snapshot.Stds?.Length ?? 0,
            snapshot.ActiveFeatureMask?.Length ?? 0,
            snapshot.FeatureImportance?.Length ?? 0,
            snapshot.FeatureImportanceScores?.Length ?? 0,
        }.Max();
    }

    private static void ValidateMetricSummary(
        AdaBoostMetricSummary summary,
        string prefix,
        List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(summary.SplitName))
            issues.Add($"{prefix}.SplitName is missing.");
        if (summary.SampleCount <= 0)
            issues.Add($"{prefix}.SampleCount must be positive.");
        if (!double.IsFinite(summary.Threshold) || summary.Threshold < 0.0 || summary.Threshold > 1.0)
            issues.Add($"{prefix}.Threshold must be a finite probability in [0, 1].");
        if (!double.IsFinite(summary.Ece) || summary.Ece < 0.0)
            issues.Add($"{prefix}.Ece must be finite and non-negative.");
    }

    private static void ValidateTree(
        GbmTree tree,
        int treeIndex,
        int featureCount,
        List<string> issues)
    {
        if (tree.Nodes is not { Count: > 0 })
        {
            issues.Add($"Tree {treeIndex} does not contain any nodes.");
            return;
        }

        for (int nodeIndex = 0; nodeIndex < tree.Nodes.Count; nodeIndex++)
        {
            var node = tree.Nodes[nodeIndex];
            if (!double.IsFinite(node.LeafValue))
            {
                issues.Add($"Tree {treeIndex} node {nodeIndex} contains a non-finite leaf value.");
                continue;
            }

            if (node.IsLeaf)
                continue;

            if (node.SplitFeature < 0 || node.SplitFeature >= featureCount)
                issues.Add($"Tree {treeIndex} node {nodeIndex} references out-of-range feature {node.SplitFeature}.");
            if (!double.IsFinite(node.SplitThreshold))
                issues.Add($"Tree {treeIndex} node {nodeIndex} contains a non-finite split threshold.");
            if (node.LeftChild < 0 || node.LeftChild >= tree.Nodes.Count)
                issues.Add($"Tree {treeIndex} node {nodeIndex} has an invalid left child index.");
            if (node.RightChild < 0 || node.RightChild >= tree.Nodes.Count)
                issues.Add($"Tree {treeIndex} node {nodeIndex} has an invalid right child index.");
            if (node.LeftChild == nodeIndex || node.RightChild == nodeIndex)
                issues.Add($"Tree {treeIndex} node {nodeIndex} self-references a child node.");
        }
    }

    private static double SanitizeProbability(double value, double fallback)
    {
        if (!double.IsFinite(value) || value <= 0.0 || value >= 1.0)
            return fallback;
        return value;
    }

    private static string ComputeSha256(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
