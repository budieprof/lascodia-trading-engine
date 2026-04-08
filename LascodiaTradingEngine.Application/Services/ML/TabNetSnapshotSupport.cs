using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

internal static class TabNetSnapshotSupport
{
    internal const string PolyInteractionsTransform = "TABNET_POLY_INTERACTIONS_V1";

    internal readonly record struct ValidationResult(bool IsValid, string[] Issues);

    internal readonly record struct WarmStartLoadReport(
        int Attempted,
        int Reused,
        int Resized,
        int Skipped,
        int Rejected)
    {
        public double ReuseRatio => Attempted > 0 ? (double)Reused / Attempted : 0.0;
    }

    internal readonly record struct CompatibilityResult(bool IsCompatible, string[] Issues);

    private static readonly JsonSerializerOptions CloneJsonOptions =
        new()
        {
            WriteIndented = false,
            MaxDepth = 128,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

    internal static bool IsTabNet(ModelSnapshot snapshot) =>
        string.Equals(snapshot.Type, "TABNET", StringComparison.OrdinalIgnoreCase);

    internal static string[] BuildFeaturePipelineTransforms(int[] polyTopIdx) =>
        polyTopIdx.Length > 1 ? [PolyInteractionsTransform] : [];

    internal static FeatureTransformDescriptor[] BuildFeaturePipelineDescriptors(int rawFeatureCount, int[][] productTerms)
    {
        if (productTerms.Length == 0)
            return [];

        return
        [
            new FeatureTransformDescriptor
            {
                Kind = PolyInteractionsTransform,
                Version = "2.0",
                Operation = "PRODUCT",
                InputFeatureCount = rawFeatureCount,
                OutputStartIndex = rawFeatureCount,
                OutputCount = productTerms.Length,
                SourceIndexGroups = productTerms.Select(t => (int[])t.Clone()).ToArray(),
            }
        ];
    }

    internal static bool HasFeaturePipelineTransform(ModelSnapshot snapshot, string transformName) =>
        snapshot.FeaturePipelineTransforms.Any(t =>
            string.Equals(t, transformName, StringComparison.OrdinalIgnoreCase)) ||
        (snapshot.FeaturePipelineDescriptors?.Any(d =>
            string.Equals(d.Kind, transformName, StringComparison.OrdinalIgnoreCase)) ?? false);

    internal static FeatureTransformDescriptor[] ResolveFeaturePipelineDescriptors(ModelSnapshot snapshot)
    {
        if (snapshot.FeaturePipelineDescriptors is { Length: > 0 } descriptors)
            return descriptors;

        if (snapshot.TabNetPolyTopFeatureIndices is not { Length: > 1 } topIdx)
            return [];

        int rawFeatureCount = snapshot.TabNetRawFeatureCount > 0
            ? snapshot.TabNetRawFeatureCount
            : snapshot.Features.Length;
        var productTerms = new List<int[]>();
        for (int a = 0; a < topIdx.Length; a++)
        {
            for (int b = a + 1; b < topIdx.Length; b++)
                productTerms.Add([topIdx[a], topIdx[b]]);
        }

        return BuildFeaturePipelineDescriptors(rawFeatureCount, productTerms.ToArray());
    }

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
        string expectedPreprocessingFingerprint)
    {
        if (!IsTabNet(snapshot))
            return new CompatibilityResult(false, ["Warm-start snapshot is not a TabNet snapshot."]);

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

        return new CompatibilityResult(issues.Count == 0, issues.ToArray());
    }

    internal static string ComputeFeatureSchemaFingerprint(string[] featureNames, int rawFeatureCount)
    {
        rawFeatureCount = Math.Max(0, Math.Min(rawFeatureCount, featureNames.Length));
        if (rawFeatureCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.Append("tabnet-feature-schema|");
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
        FeatureTransformDescriptor[] descriptors,
        bool[]? activeMask)
    {
        var builder = new StringBuilder();
        builder.Append("tabnet-preproc|");
        builder.Append(rawFeatureCount);
        builder.Append('|');
        foreach (var descriptor in descriptors.OrderBy(d => d.OutputStartIndex).ThenBy(d => d.Kind))
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
                    if (i > 0) builder.Append(',');
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
        int rawFeatureCount = snapshot.TabNetRawFeatureCount > 0
            ? snapshot.TabNetRawFeatureCount
            : snapshot.Features.Length;
        return ComputePreprocessingFingerprint(
            rawFeatureCount,
            ResolveFeaturePipelineDescriptors(snapshot),
            null);
    }

    internal static string ComputeTrainerFingerprint(
        TrainingHyperparams hp,
        int nSteps,
        int hiddenDim,
        int attentionDim,
        int sharedLayers,
        int stepLayers,
        double gamma,
        bool useSparsemax,
        bool useGlu,
        double dropoutRate,
        double sparsityCoeff)
    {
        string payload = JsonSerializer.Serialize(new
        {
            nSteps,
            hiddenDim,
            attentionDim,
            sharedLayers,
            stepLayers,
            gamma,
            useSparsemax,
            useGlu,
            dropoutRate,
            sparsityCoeff,
            hp.LabelSmoothing,
            hp.LearningRate,
            hp.L2Lambda,
            hp.MaxGradNorm,
            hp.TabNetMomentumBn,
            hp.TabNetGhostBatchSize,
            hp.TabNetWarmupEpochs,
            hp.MagLossWeight,
            hp.TemporalDecayLambda,
        }, CloneJsonOptions);
        return ComputeSha256(payload);
    }

    internal static void UpgradeSnapshotInPlace(ModelSnapshot snapshot)
    {
        if (!IsTabNet(snapshot))
            return;

        snapshot.FeaturePipelineTransforms ??= [];
        snapshot.FeaturePipelineDescriptors ??= [];

        if (snapshot.TabNetRawFeatureCount <= 0)
        {
            snapshot.TabNetRawFeatureCount = snapshot.Features.Length > 0
                ? snapshot.Features.Length
                : snapshot.Means.Length;
        }

        if ((snapshot.FeaturePipelineTransforms?.Length ?? 0) == 0 &&
            snapshot.TabNetPolyTopFeatureIndices is { Length: > 1 })
        {
            snapshot.FeaturePipelineTransforms = [PolyInteractionsTransform];
        }

        if ((snapshot.FeaturePipelineDescriptors?.Length ?? 0) == 0 &&
            snapshot.TabNetPolyTopFeatureIndices is { Length: > 1 })
        {
            snapshot.FeaturePipelineDescriptors = ResolveFeaturePipelineDescriptors(snapshot);
        }

        if ((snapshot.TabNetOutputHeadWeights?.Length ?? 0) == 0 &&
            snapshot.TabNetHiddenDim > 0)
        {
            var outputWeights = new double[snapshot.TabNetHiddenDim];
            if (double.IsFinite(snapshot.TabNetOutputWeight))
                outputWeights[0] = snapshot.TabNetOutputWeight;
            snapshot.TabNetOutputHeadWeights = outputWeights;
        }

        if (snapshot.Biases is { Length: > 0 } legacyBiases &&
            double.IsFinite(legacyBiases[0]) &&
            (!double.IsFinite(snapshot.TabNetOutputHeadBias) ||
             (Math.Abs(snapshot.TabNetOutputHeadBias) <= 1e-12 &&
              Math.Abs(legacyBiases[0]) > 1e-12)))
        {
            snapshot.TabNetOutputHeadBias = legacyBiases[0];
        }

        if (snapshot.TabNetPerStepAttention is { Length: > 0 } perStep &&
            snapshot.TabNetPerStepSparsity is not { Length: > 0 })
        {
            var sparsity = new double[perStep.Length];
            for (int s = 0; s < perStep.Length; s++)
            {
                if (perStep[s].Length == 0)
                    continue;
                int nonZero = 0;
                for (int j = 0; j < perStep[s].Length; j++)
                    if (perStep[s][j] > 1e-6)
                        nonZero++;
                sparsity[s] = (double)nonZero / perStep[s].Length;
            }
            snapshot.TabNetPerStepSparsity = sparsity;
        }

        if ((snapshot.TabNetAuditFindings?.Length ?? 0) == 0)
            snapshot.TabNetAuditFindings = [];

        if (string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint) &&
            snapshot.Features.Length > 0 &&
            snapshot.TabNetRawFeatureCount > 0)
        {
            snapshot.FeatureSchemaFingerprint = ComputeFeatureSchemaFingerprint(snapshot.Features, snapshot.TabNetRawFeatureCount);
        }

        if (string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint))
            snapshot.PreprocessingFingerprint = ComputePreprocessingFingerprint(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint))
        {
            snapshot.TrainerFingerprint = ComputeSha256(string.Join("|",
                snapshot.Type,
                snapshot.Version,
                snapshot.BaseLearnersK,
                snapshot.TabNetHiddenDim,
                snapshot.TabNetUseSparsemax,
                snapshot.TabNetUseGlu,
                snapshot.TabNetRelaxationGamma));
        }
    }

    internal static ValidationResult ValidateSnapshot(ModelSnapshot snapshot, bool allowLegacyV2 = true)
    {
        if (!IsTabNet(snapshot))
            return new ValidationResult(true, []);

        return ValidateNormalizedSnapshot(NormalizeSnapshotCopy(snapshot), allowLegacyV2);
    }

    internal static ValidationResult ValidateNormalizedSnapshot(ModelSnapshot snapshot, bool allowLegacyV2 = true)
    {
        if (!IsTabNet(snapshot))
            return new ValidationResult(true, []);

        var issues = new List<string>();
        var featurePipelineTransforms = snapshot.FeaturePipelineTransforms ?? [];
        var featurePipelineDescriptors = ResolveFeaturePipelineDescriptors(snapshot);

        foreach (string transform in featurePipelineTransforms)
        {
            if (string.IsNullOrWhiteSpace(transform))
                continue;

            if (!string.Equals(transform, PolyInteractionsTransform, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Unknown feature pipeline transform '{transform}'.");
        }

        foreach (var descriptor in featurePipelineDescriptors)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Kind))
            {
                issues.Add("Feature pipeline descriptor kind is missing.");
                continue;
            }

            if (!string.Equals(descriptor.Kind, PolyInteractionsTransform, StringComparison.OrdinalIgnoreCase))
                issues.Add($"Unknown feature pipeline descriptor '{descriptor.Kind}'.");

            if (!string.Equals(descriptor.Operation, "PRODUCT", StringComparison.OrdinalIgnoreCase))
                issues.Add($"Unsupported descriptor operation '{descriptor.Operation}'.");
        }

        bool isV3 = string.Equals(snapshot.Version, "3.0", StringComparison.OrdinalIgnoreCase) ||
                    snapshot.TabNetSharedWeights is { Length: > 0 };

        if (!isV3)
        {
            if (!allowLegacyV2)
                issues.Add("Legacy TabNet snapshot is not accepted on this path.");
            else if (snapshot.Weights is not { Length: > 0 })
                issues.Add("Legacy TabNet snapshot has no attention weights.");

            return new ValidationResult(issues.Count == 0, issues.ToArray());
        }

        int featureCount = snapshot.Features.Length > 0
            ? snapshot.Features.Length
            : snapshot.Means.Length;
        if (featureCount <= 0)
            issues.Add("Snapshot feature count is zero.");

        if (snapshot.Means.Length != 0 && snapshot.Means.Length != featureCount)
            issues.Add("Means length does not match feature count.");
        if (snapshot.Stds.Length != 0 && snapshot.Stds.Length != featureCount)
            issues.Add("Stds length does not match feature count.");
        if (snapshot.TabNetHiddenDim <= 0)
            issues.Add("TabNetHiddenDim must be positive.");

        int nSteps = snapshot.BaseLearnersK > 0 ? snapshot.BaseLearnersK : 3;
        int sharedLayers = snapshot.TabNetSharedWeights?.Length ?? 0;
        int stepLayers = snapshot.TabNetStepFcWeights is { Length: > 0 } stepW && stepW[0] is not null
            ? stepW[0].Length
            : 0;
        int totalBnLayers = nSteps + sharedLayers + nSteps * stepLayers;

        static bool MatrixShapeMatches(double[][] matrix, int rows, int cols)
        {
            if (matrix.Length != rows)
                return false;

            for (int i = 0; i < rows; i++)
                if (matrix[i].Length != cols)
                    return false;

            return true;
        }

        if (snapshot.TabNetSharedWeights is not { Length: > 0 })
            issues.Add("Shared feature-transformer weights are missing.");
        if (snapshot.TabNetSharedBiases is null || snapshot.TabNetSharedBiases.Length != sharedLayers)
            issues.Add("Shared feature-transformer biases are inconsistent.");
        if (snapshot.TabNetUseGlu &&
            (snapshot.TabNetSharedGateWeights is null || snapshot.TabNetSharedGateWeights.Length != sharedLayers))
            issues.Add("Shared GLU gate weights are inconsistent.");
        if (snapshot.TabNetUseGlu &&
            (snapshot.TabNetSharedGateBiases is null || snapshot.TabNetSharedGateBiases.Length != sharedLayers))
            issues.Add("Shared GLU gate biases are inconsistent.");
        if (snapshot.TabNetStepFcWeights is null || snapshot.TabNetStepFcWeights.Length != nSteps)
            issues.Add("Step-specific FC weights are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetStepFcBiases is null || snapshot.TabNetStepFcBiases.Length != nSteps)
            issues.Add("Step-specific FC biases are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetUseGlu &&
            (snapshot.TabNetStepGateWeights is null || snapshot.TabNetStepGateWeights.Length != nSteps))
            issues.Add("Step-specific GLU gate weights are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetUseGlu &&
            (snapshot.TabNetStepGateBiases is null || snapshot.TabNetStepGateBiases.Length != nSteps))
            issues.Add("Step-specific GLU gate biases are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetAttentionFcWeights is null || snapshot.TabNetAttentionFcWeights.Length != nSteps)
            issues.Add("Attention FC weights are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetAttentionFcBiases is null || snapshot.TabNetAttentionFcBiases.Length != nSteps)
            issues.Add("Attention FC biases are inconsistent with BaseLearnersK.");
        if (snapshot.TabNetBnGammas is null || snapshot.TabNetBnGammas.Length != totalBnLayers)
            issues.Add("BN gamma layout is inconsistent with architecture depth.");
        if (snapshot.TabNetBnBetas is null || snapshot.TabNetBnBetas.Length != totalBnLayers)
            issues.Add("BN beta layout is inconsistent with architecture depth.");
        if (snapshot.TabNetBnRunningMeans is null || snapshot.TabNetBnRunningMeans.Length != totalBnLayers)
            issues.Add("BN running-mean layout is inconsistent with architecture depth.");
        if (snapshot.TabNetBnRunningVars is null || snapshot.TabNetBnRunningVars.Length != totalBnLayers)
            issues.Add("BN running-var layout is inconsistent with architecture depth.");
        if (snapshot.TabNetOutputHeadWeights is not { Length: > 0 })
            issues.Add("Output-head weights are missing.");
        else if (snapshot.TabNetOutputHeadWeights.Length != snapshot.TabNetHiddenDim)
            issues.Add("Output-head weights do not match TabNetHiddenDim.");

        if (snapshot.TabNetRawFeatureCount > 0 && snapshot.TabNetRawFeatureCount > featureCount)
            issues.Add("TabNetRawFeatureCount cannot exceed snapshot feature count.");
        if (snapshot.ActiveFeatureMask is { Length: > 0 } mask && mask.Length != featureCount)
            issues.Add("ActiveFeatureMask length does not match feature count.");
        if (featureCount > snapshot.TabNetRawFeatureCount &&
            !HasFeaturePipelineTransform(snapshot, PolyInteractionsTransform))
        {
            issues.Add("Feature count exceeds raw feature count but no replayable feature transform is declared.");
        }

        if (HasFeaturePipelineTransform(snapshot, PolyInteractionsTransform) &&
            featurePipelineDescriptors.Length == 0 &&
            snapshot.TabNetPolyTopFeatureIndices is not { Length: > 1 })
        {
            issues.Add("Polynomial pipeline replay is enabled but neither typed descriptors nor TabNetPolyTopFeatureIndices are available.");
        }
        else if (featurePipelineDescriptors.Length > 0)
        {
            int descriptorOutputTotal = 0;
            foreach (var descriptor in featurePipelineDescriptors)
            {
                if (descriptor.InputFeatureCount != snapshot.TabNetRawFeatureCount)
                    issues.Add("Feature pipeline descriptor input count does not match TabNetRawFeatureCount.");
                if (descriptor.OutputStartIndex < snapshot.TabNetRawFeatureCount)
                    issues.Add("Feature pipeline descriptor output start index overlaps raw features.");
                if (descriptor.OutputCount != descriptor.SourceIndexGroups.Length)
                    issues.Add("Feature pipeline descriptor output count does not match SourceIndexGroups length.");

                descriptorOutputTotal += descriptor.OutputCount;
                foreach (var group in descriptor.SourceIndexGroups)
                {
                    if (group.Length < 2)
                    {
                        issues.Add("Feature pipeline descriptor source groups must contain at least two indices.");
                        break;
                    }

                    for (int i = 0; i < group.Length; i++)
                    {
                        if (group[i] < 0 || group[i] >= snapshot.TabNetRawFeatureCount)
                        {
                            issues.Add("Feature pipeline descriptor contains an out-of-range raw feature index.");
                            break;
                        }
                    }
                }
            }

            int expectedFeatureCount = snapshot.TabNetRawFeatureCount + descriptorOutputTotal;
            if (featureCount != expectedFeatureCount)
                issues.Add("Feature pipeline descriptors do not reconcile to snapshot feature count.");
        }
        else if (HasFeaturePipelineTransform(snapshot, PolyInteractionsTransform) &&
                 snapshot.TabNetPolyTopFeatureIndices is { Length: > 1 } polyTopIdx)
        {
            int expectedFeatureCount = snapshot.TabNetRawFeatureCount + polyTopIdx.Length * (polyTopIdx.Length - 1) / 2;
            if (featureCount != expectedFeatureCount)
                issues.Add("Polynomial pipeline replay metadata does not match snapshot feature count.");

            for (int i = 0; i < polyTopIdx.Length; i++)
            {
                if (polyTopIdx[i] < 0 || polyTopIdx[i] >= snapshot.TabNetRawFeatureCount)
                {
                    issues.Add("TabNetPolyTopFeatureIndices contains an out-of-range raw feature index.");
                    break;
                }
            }
        }

        if (snapshot.TabNetInitialBnFcW is { Length: > 0 } initialW && featureCount > 0)
        {
            if (!MatrixShapeMatches(initialW, featureCount, featureCount))
                issues.Add("Initial attention projection weights do not match feature count.");
            if (snapshot.TabNetInitialBnFcB is null || snapshot.TabNetInitialBnFcB.Length != featureCount)
                issues.Add("Initial attention projection bias length is inconsistent.");
        }

        for (int l = 0; l < sharedLayers; l++)
        {
            int inDim = l == 0 ? featureCount : snapshot.TabNetHiddenDim;
            if (snapshot.TabNetSharedWeights is { } sharedWeights &&
                l < sharedWeights.Length &&
                !MatrixShapeMatches(sharedWeights[l], snapshot.TabNetHiddenDim, inDim))
            {
                issues.Add($"Shared feature-transformer weight shape is invalid at layer {l}.");
            }

            if (snapshot.TabNetSharedBiases is { } sharedBiases &&
                l < sharedBiases.Length &&
                sharedBiases[l].Length != snapshot.TabNetHiddenDim)
            {
                issues.Add($"Shared feature-transformer bias shape is invalid at layer {l}.");
            }

            if (snapshot.TabNetUseGlu &&
                snapshot.TabNetSharedGateWeights is { } sharedGateWeights &&
                l < sharedGateWeights.Length &&
                !MatrixShapeMatches(sharedGateWeights[l], snapshot.TabNetHiddenDim, inDim))
            {
                issues.Add($"Shared GLU gate weight shape is invalid at layer {l}.");
            }

            if (snapshot.TabNetUseGlu &&
                snapshot.TabNetSharedGateBiases is { } sharedGateBiases &&
                l < sharedGateBiases.Length &&
                sharedGateBiases[l].Length != snapshot.TabNetHiddenDim)
            {
                issues.Add($"Shared GLU gate bias shape is invalid at layer {l}.");
            }
        }

        for (int s = 0; s < nSteps; s++)
        {
            if (snapshot.TabNetStepFcWeights is { } stepWeights)
            {
                if (s >= stepWeights.Length || stepWeights[s].Length != stepLayers)
                {
                    issues.Add($"Step-specific FC weights are inconsistent at step {s}.");
                }
                else
                {
                    for (int l = 0; l < stepLayers; l++)
                    {
                        if (!MatrixShapeMatches(stepWeights[s][l], snapshot.TabNetHiddenDim, snapshot.TabNetHiddenDim))
                        {
                            issues.Add($"Step-specific FC weight shape is invalid at step {s}, layer {l}.");
                            break;
                        }
                    }
                }
            }

            if (snapshot.TabNetStepFcBiases is { } stepBiases)
            {
                if (s >= stepBiases.Length || stepBiases[s].Length != stepLayers)
                {
                    issues.Add($"Step-specific FC biases are inconsistent at step {s}.");
                }
                else
                {
                    for (int l = 0; l < stepLayers; l++)
                    {
                        if (stepBiases[s][l].Length != snapshot.TabNetHiddenDim)
                        {
                            issues.Add($"Step-specific FC bias shape is invalid at step {s}, layer {l}.");
                            break;
                        }
                    }
                }
            }

            if (snapshot.TabNetUseGlu && snapshot.TabNetStepGateWeights is { } stepGateWeights)
            {
                if (s >= stepGateWeights.Length || stepGateWeights[s].Length != stepLayers)
                {
                    issues.Add($"Step-specific GLU gate weights are inconsistent at step {s}.");
                }
                else
                {
                    for (int l = 0; l < stepLayers; l++)
                    {
                        if (!MatrixShapeMatches(stepGateWeights[s][l], snapshot.TabNetHiddenDim, snapshot.TabNetHiddenDim))
                        {
                            issues.Add($"Step-specific GLU gate weight shape is invalid at step {s}, layer {l}.");
                            break;
                        }
                    }
                }
            }

            if (snapshot.TabNetUseGlu && snapshot.TabNetStepGateBiases is { } stepGateBiases)
            {
                if (s >= stepGateBiases.Length || stepGateBiases[s].Length != stepLayers)
                {
                    issues.Add($"Step-specific GLU gate biases are inconsistent at step {s}.");
                }
                else
                {
                    for (int l = 0; l < stepLayers; l++)
                    {
                        if (stepGateBiases[s][l].Length != snapshot.TabNetHiddenDim)
                        {
                            issues.Add($"Step-specific GLU gate bias shape is invalid at step {s}, layer {l}.");
                            break;
                        }
                    }
                }
            }

            if (snapshot.TabNetAttentionFcWeights is { } attentionWeights)
            {
                if (s >= attentionWeights.Length || attentionWeights[s].Length != featureCount)
                {
                    issues.Add($"Attention FC weight shape is invalid at step {s}.");
                }
                else
                {
                    int expectedAttentionDim = attentionWeights[s].Length > 0 ? attentionWeights[s][0].Length : 0;
                    if (expectedAttentionDim <= 0 || expectedAttentionDim > snapshot.TabNetHiddenDim)
                    {
                        issues.Add($"Attention FC inner dimension is invalid at step {s}.");
                    }
                    else
                    {
                        for (int j = 0; j < attentionWeights[s].Length; j++)
                        {
                            if (attentionWeights[s][j].Length != expectedAttentionDim)
                            {
                                issues.Add($"Attention FC weight rows are ragged at step {s}.");
                                break;
                            }
                        }
                    }
                }
            }

            if (snapshot.TabNetAttentionFcBiases is { } attentionBiases &&
                (s >= attentionBiases.Length || attentionBiases[s].Length != featureCount))
            {
                issues.Add($"Attention FC bias shape is invalid at step {s}.");
            }
        }

        for (int b = 0; b < totalBnLayers; b++)
        {
            int expectedDim = b < nSteps ? featureCount : snapshot.TabNetHiddenDim;

            if (snapshot.TabNetBnGammas is { } bnGammas &&
                (b >= bnGammas.Length || bnGammas[b].Length != expectedDim))
                issues.Add($"BN gamma shape is invalid at layer {b}.");
            if (snapshot.TabNetBnBetas is { } bnBetas &&
                (b >= bnBetas.Length || bnBetas[b].Length != expectedDim))
                issues.Add($"BN beta shape is invalid at layer {b}.");
            if (snapshot.TabNetBnRunningMeans is { } bnMeans &&
                (b >= bnMeans.Length || bnMeans[b].Length != expectedDim))
                issues.Add($"BN running-mean shape is invalid at layer {b}.");
            if (snapshot.TabNetBnRunningVars is { } bnVars &&
                (b >= bnVars.Length || bnVars[b].Length != expectedDim))
                issues.Add($"BN running-var shape is invalid at layer {b}.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint))
        {
            string computedSchemaFingerprint = ComputeFeatureSchemaFingerprint(snapshot.Features, snapshot.TabNetRawFeatureCount);
            if (!string.Equals(snapshot.FeatureSchemaFingerprint, computedSchemaFingerprint, StringComparison.Ordinal))
                issues.Add("Feature schema fingerprint does not match snapshot feature metadata.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint))
        {
            string computedPreprocFingerprint = ComputePreprocessingFingerprint(snapshot);
            if (!string.Equals(snapshot.PreprocessingFingerprint, computedPreprocFingerprint, StringComparison.Ordinal))
                issues.Add("Preprocessing fingerprint does not match replayable transform metadata.");
        }

        return new ValidationResult(issues.Count == 0, issues.ToArray());
    }

    private static string ComputeSha256(string payload)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
