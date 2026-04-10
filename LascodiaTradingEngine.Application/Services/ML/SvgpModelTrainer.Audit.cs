using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SvgpModelTrainer
{
    private readonly record struct SvgpAuditResult(
        string[] Findings,
        SvgpAuditArtifact Artifact);

    private static SvgpAuditResult RunSvgpAudit(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSamples)
    {
        var findings = new List<string>();

        if (snapshot.SvgpInducingPoints is not { Length: > 0 })
        {
            findings.Add("Snapshot has no SvgpInducingPoints - audit skipped.");
            return new SvgpAuditResult(
                [.. findings],
                new SvgpAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        if (auditSamples.Count == 0)
        {
            findings.Add("No audit samples available.");
            return new SvgpAuditResult(
                [.. findings],
                new SvgpAuditArtifact
                {
                    SnapshotContractValid = true,
                    AuditedSampleCount = 0,
                    Findings = [.. findings],
                });
        }

        var engine = new SvgpInferenceEngine();
        if (!engine.CanHandle(snapshot))
        {
            findings.Add("SvgpInferenceEngine refused the snapshot.");
            return new SvgpAuditResult(
                [.. findings],
                new SvgpAuditArtifact
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
                    findings.Add($"SvgpInferenceEngine returned null for audit sample {auditedCount}.");
                    continue;
                }

                double engineRaw = inference.Value.Probability;

                // Compute trainer-side raw probability using pure-C# ARD prediction
                double trainerRaw = ComputeTrainerRawProbSvgp(snapshot, features);

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
            findings.Add("SVGP audit did not evaluate any samples.");
        if (maxRawParityError > 1e-9)
            findings.Add($"SVGP raw-probability parity drift {maxRawParityError:G} exceeded 1e-9.");
        if (maxDeployedCalibrationDelta > 1e-9)
            findings.Add($"SVGP deployed-calibration drift {maxDeployedCalibrationDelta:G} exceeded 1e-9.");
        if (thresholdMismatchCount > 0)
            findings.Add($"SVGP threshold decision mismatches detected: {thresholdMismatchCount}.");

        var artifact = new SvgpAuditArtifact
        {
            SnapshotContractValid = findings.Count == 0,
            AuditedSampleCount = auditedCount,
            MaxRawParityError = maxRawParityError,
            MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdMismatchCount,
            Findings = [.. findings.Distinct()],
        };

        return new SvgpAuditResult(artifact.Findings, artifact);
    }

    /// <summary>
    /// Reconstructs the SVGP raw probability from the snapshot's alpha vector and
    /// inducing points using the ARD RBF kernel. This is the trainer-side raw
    /// probability (before Platt calibration) used for parity audit.
    /// </summary>
    private static double ComputeTrainerRawProbSvgp(ModelSnapshot snapshot, float[] features)
    {
        if (snapshot.SvgpInducingPoints is not { Length: > 0 } Z) return 0.5;
        if (snapshot.Weights is not { Length: > 0 }) return 0.5;
        if (snapshot.SvgpArdLengthScales is not { Length: > 0 } ls) return 0.5;

        double[] alpha = snapshot.Weights[0];
        double sf2 = snapshot.SvgpSignalVariance;
        int M = Z.Length;
        int F = ls.Length;

        // Standardise features using snapshot means/stds
        var stdFeatures = new float[features.Length];
        for (int j = 0; j < features.Length; j++)
        {
            float mean = j < snapshot.Means.Length ? snapshot.Means[j] : 0f;
            float std = j < snapshot.Stds.Length ? snapshot.Stds[j] : 1f;
            stdFeatures[j] = std > 1e-7f ? (features[j] - mean) / std : 0f;
        }

        double rawMean = 0;
        for (int m = 0; m < M && m < alpha.Length; m++)
        {
            double sq = 0;
            for (int d = 0; d < F; d++)
            {
                double xd = d < stdFeatures.Length ? stdFeatures[d] : 0.0;
                double zd = d < Z[m].Length ? Z[m][d] : 0.0;
                double lengthScale = ls[d] > 1e-8 ? ls[d] : 1.0;
                double diff = (xd - zd) / lengthScale;
                sq += diff * diff;
            }
            rawMean += alpha[m] * sf2 * Math.Exp(-0.5 * sq);
        }

        return MLFeatureHelper.Sigmoid(rawMean);
    }
}
