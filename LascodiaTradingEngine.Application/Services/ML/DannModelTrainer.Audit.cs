using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class DannModelTrainer
{
    private readonly record struct DannAuditResult(
        string[] Findings,
        DannAuditArtifact Artifact);

    private static DannAuditResult RunDannAudit(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSamples)
    {
        var findings = new List<string>();

        if (snapshot.DannWeights is not { Length: > 0 })
        {
            findings.Add("Snapshot has no DannWeights - audit skipped.");
            return new DannAuditResult(
                [.. findings],
                new DannAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        if (auditSamples.Count == 0)
        {
            findings.Add("No audit samples available.");
            return new DannAuditResult(
                [.. findings],
                new DannAuditArtifact
                {
                    SnapshotContractValid = true,
                    AuditedSampleCount = 0,
                    Findings = [.. findings],
                });
        }

        var engine = new DannInferenceEngine();
        if (!engine.CanHandle(snapshot))
        {
            findings.Add("DannInferenceEngine refused the snapshot.");
            return new DannAuditResult(
                [.. findings],
                new DannAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        int featureCount = snapshot.Features.Length;
        bool[] activeMask = snapshot.ActiveFeatureMask;
        int activeF = activeMask.Length > 0 ? activeMask.Count(static m => m) : featureCount;

        double maxRawParityError = 0.0;
        double sumRawParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        int thresholdMismatchCount = 0;
        int auditedCount = 0;

        foreach (var sample in auditSamples.Take(32))
        {
            try
            {
                // Apply active feature mask to match what inference engine expects
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
                    findings.Add($"DannInferenceEngine returned null for audit sample {auditedCount}.");
                    continue;
                }

                double engineRaw = inference.Value.Probability;

                // Compute trainer-side raw probability using the snapshot weights
                // (ForwardCls is private to the trainer but we reconstruct via the same DannWeights)
                double trainerRaw = ComputeTrainerRawProb(snapshot, features);

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
            findings.Add("DANN audit did not evaluate any samples.");
        if (maxRawParityError > 1e-9)
            findings.Add($"DANN raw-probability parity drift {maxRawParityError:G} exceeded 1e-9.");
        if (maxDeployedCalibrationDelta > 1e-9)
            findings.Add($"DANN deployed-calibration drift {maxDeployedCalibrationDelta:G} exceeded 1e-9.");
        if (thresholdMismatchCount > 0)
            findings.Add($"DANN threshold decision mismatches detected: {thresholdMismatchCount}.");

        var artifact = new DannAuditArtifact
        {
            SnapshotContractValid = findings.Count == 0,
            AuditedSampleCount = auditedCount,
            MaxRawParityError = maxRawParityError,
            MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdMismatchCount,
            Findings = [.. findings.Distinct()],
        };

        return new DannAuditResult(artifact.Findings, artifact);
    }

    /// <summary>
    /// Reconstructs the DANN forward pass from snapshot DannWeights to compute
    /// the trainer-side raw probability for parity audit.
    /// </summary>
    private static double ComputeTrainerRawProb(ModelSnapshot snapshot, float[] features)
    {
        var dw = snapshot.DannWeights!;
        int featureCount = features.Length;

        // Detect featDim from packed layout
        int featDim = 0;
        for (int r = 0; r < dw.Length; r++)
        {
            if (dw[r].Length != featureCount + 1) { featDim = r; break; }
        }
        if (featDim <= 0 || 2 * featDim >= dw.Length) return 0.5;

        // Layer 1: F -> featDim (ReLU)
        var h1 = new double[featDim];
        for (int j = 0; j < featDim; j++)
        {
            double pre = dw[j][featureCount];
            for (int fi = 0; fi < featureCount && fi < features.Length; fi++)
                pre += dw[j][fi] * features[fi];
            h1[j] = Math.Max(0.0, pre);
        }

        // Layer 2: featDim -> featDim (ReLU)
        var h2 = new double[featDim];
        for (int j = 0; j < featDim; j++)
        {
            int row = featDim + j;
            double pre = dw[row][featDim];
            for (int k = 0; k < featDim; k++)
                pre += dw[row][k] * h1[k];
            h2[j] = Math.Max(0.0, pre);
        }

        // Label classifier: featDim -> 1 (sigmoid)
        int clsRow = 2 * featDim;
        double logit = dw[clsRow][featDim];
        for (int j = 0; j < featDim; j++)
            logit += dw[clsRow][j] * h2[j];

        return 1.0 / (1.0 + Math.Exp(-logit));
    }
}
