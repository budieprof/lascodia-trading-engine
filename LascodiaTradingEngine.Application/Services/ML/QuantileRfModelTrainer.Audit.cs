using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Caching.Memory;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class QuantileRfModelTrainer
{
    private readonly record struct QrfAuditResult(
        string[] Findings,
        QrfAuditArtifact Artifact);

    private static QrfAuditResult RunQrfAudit(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSamples)
    {
        var findings = new List<string>();

        if (snapshot.GbmTreesJson is not { Length: > 0 })
        {
            findings.Add("Snapshot has no GbmTreesJson - audit skipped.");
            return new QrfAuditResult(
                [.. findings],
                new QrfAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        if (auditSamples.Count == 0)
        {
            findings.Add("No audit samples available.");
            return new QrfAuditResult(
                [.. findings],
                new QrfAuditArtifact
                {
                    SnapshotContractValid = true,
                    AuditedSampleCount = 0,
                    Findings = [.. findings],
                });
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var engine = new QrfInferenceEngine(cache);
        if (!engine.CanHandle(snapshot))
        {
            findings.Add("QrfInferenceEngine refused the snapshot.");
            return new QrfAuditResult(
                [.. findings],
                new QrfAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        double maxRawParityError = 0.0;
        double sumRawParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        int thresholdMismatchCount = 0;
        int auditedCount = 0;

        foreach (var sample in auditSamples.Take(32))
        {
            try
            {
                float[] features = sample.Features;

                var inference = engine.RunInference(
                    features,
                    features.Length,
                    snapshot,
                    [],
                    modelId: 0L,
                    mcDropoutSamples: 0,
                    mcDropoutSeed: 0);

                if (inference is null)
                {
                    findings.Add($"QrfInferenceEngine returned null for audit sample {auditedCount}.");
                    continue;
                }

                double engineRaw = inference.Value.Probability;

                // Compute trainer-side raw probability by traversing the trees and
                // applying Platt + isotonic calibration from the snapshot
                double trainerRaw = ComputeTrainerRawProbQrf(snapshot, features);

                double rawDelta = Math.Abs(trainerRaw - engineRaw);
                maxRawParityError = Math.Max(maxRawParityError, rawDelta);
                sumRawParityError += rawDelta;

                double trainerCalib = InferenceHelpers.ApplyDeployedCalibration(trainerRaw, snapshot);
                double engineCalib = InferenceHelpers.ApplyDeployedCalibration(engineRaw, snapshot);
                double deployedDelta = Math.Abs(trainerCalib - engineCalib);
                maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, deployedDelta);

                if ((trainerCalib >= snapshot.OptimalThreshold) != (engineCalib >= snapshot.OptimalThreshold))
                    thresholdMismatchCount++;

                auditedCount++;
            }
            catch (Exception ex)
            {
                findings.Add($"Audit sample {auditedCount} failed: {ex.Message}");
            }
        }

        if (auditedCount == 0)
            findings.Add("QRF audit did not evaluate any samples.");
        if (maxRawParityError > 1e-9)
            findings.Add($"QRF raw-probability parity drift {maxRawParityError:G} exceeded 1e-9.");
        if (maxDeployedCalibrationDelta > 1e-9)
            findings.Add($"QRF deployed-calibration drift {maxDeployedCalibrationDelta:G} exceeded 1e-9.");
        if (thresholdMismatchCount > 0)
            findings.Add($"QRF threshold decision mismatches detected: {thresholdMismatchCount}.");

        var artifact = new QrfAuditArtifact
        {
            SnapshotContractValid = findings.Count == 0,
            AuditedSampleCount = auditedCount,
            MaxRawParityError = maxRawParityError,
            MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdMismatchCount,
            Findings = [.. findings.Distinct()],
        };

        return new QrfAuditResult(artifact.Findings, artifact);
    }

    /// <summary>
    /// Reconstructs the QRF forest probability from the snapshot's serialised GbmTreesJson.
    /// This is the trainer-side raw probability (before Platt calibration) used for parity audit.
    /// </summary>
    private static double ComputeTrainerRawProbQrf(ModelSnapshot snapshot, float[] features)
    {
        if (snapshot.GbmTreesJson is not { Length: > 0 }) return 0.5;

        List<GbmTree>? trees;
        try
        {
            trees = System.Text.Json.JsonSerializer.Deserialize<List<GbmTree>>(
                snapshot.GbmTreesJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return 0.5;
        }

        if (trees is not { Count: > 0 }) return 0.5;

        // Standardise features using snapshot means/stds
        var stdFeatures = new float[features.Length];
        for (int j = 0; j < features.Length; j++)
        {
            float mean = j < snapshot.Means.Length ? snapshot.Means[j] : 0f;
            float std = j < snapshot.Stds.Length ? snapshot.Stds[j] : 1f;
            stdFeatures[j] = std > 1e-7f ? (features[j] - mean) / std : 0f;
        }

        double sum = 0.0;
        int validTrees = 0;
        foreach (var tree in trees)
        {
            if (tree.Nodes is not { Count: > 0 }) { sum += 0.5; validTrees++; continue; }
            int nodeIdx = 0;
            double leafVal = 0.5;
            while (nodeIdx >= 0 && nodeIdx < tree.Nodes.Count)
            {
                var node = tree.Nodes[nodeIdx];
                if (node.IsLeaf || node.SplitFeature < 0 || node.SplitFeature >= stdFeatures.Length)
                {
                    leafVal = node.LeafValue;
                    break;
                }
                nodeIdx = stdFeatures[node.SplitFeature] <= (float)node.SplitThreshold
                    ? node.LeftChild
                    : node.RightChild;
            }
            sum += double.IsFinite(leafVal) ? leafVal : 0.5;
            validTrees++;
        }

        return validTrees > 0 ? sum / validTrees : 0.5;
    }
}
