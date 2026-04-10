using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Caching.Memory;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class RocketModelTrainer
{
    private readonly record struct RocketAuditResult(
        string[] Findings,
        RocketAuditArtifact Artifact);

    private static RocketAuditResult RunRocketAudit(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSamples)
    {
        var findings = new List<string>();

        if (snapshot.RocketKernelWeights is not { Length: > 0 })
        {
            findings.Add("Snapshot has no RocketKernelWeights - audit skipped.");
            return new RocketAuditResult(
                [.. findings],
                new RocketAuditArtifact
                {
                    SnapshotContractValid = false,
                    Findings = [.. findings],
                });
        }

        if (auditSamples.Count == 0)
        {
            findings.Add("No audit samples available.");
            return new RocketAuditResult(
                [.. findings],
                new RocketAuditArtifact
                {
                    SnapshotContractValid = true,
                    AuditedSampleCount = 0,
                    Findings = [.. findings],
                });
        }

        var engine = new RocketInferenceEngine();
        if (!engine.CanHandle(snapshot))
        {
            findings.Add("RocketInferenceEngine refused the snapshot.");
            return new RocketAuditResult(
                [.. findings],
                new RocketAuditArtifact
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
                    findings.Add($"RocketInferenceEngine returned null for audit sample {auditedCount}.");
                    continue;
                }

                double engineRaw = inference.Value.Probability;

                // Compute trainer-side raw probability using the same ROCKET pipeline
                double trainerRaw = ComputeTrainerRawProbRocket(snapshot, features);

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
            findings.Add("ROCKET audit did not evaluate any samples.");
        if (maxRawParityError > 1e-9)
            findings.Add($"ROCKET raw-probability parity drift {maxRawParityError:G} exceeded 1e-9.");
        if (maxDeployedCalibrationDelta > 1e-9)
            findings.Add($"ROCKET deployed-calibration drift {maxDeployedCalibrationDelta:G} exceeded 1e-9.");
        if (thresholdMismatchCount > 0)
            findings.Add($"ROCKET threshold decision mismatches detected: {thresholdMismatchCount}.");

        var artifact = new RocketAuditArtifact
        {
            SnapshotContractValid = findings.Count == 0,
            AuditedSampleCount = auditedCount,
            MaxRawParityError = maxRawParityError,
            MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdMismatchCount,
            Findings = [.. findings.Distinct()],
        };

        return new RocketAuditResult(artifact.Findings, artifact);
    }

    /// <summary>
    /// Reconstructs the ROCKET raw probability from the snapshot's kernel weights
    /// and ridge head. This is the trainer-side raw probability (before Platt calibration)
    /// used for parity audit.
    /// </summary>
    private static double ComputeTrainerRawProbRocket(ModelSnapshot snapshot, float[] features)
    {
        if (snapshot.RocketKernelWeights is not { Length: > 0 } kernelWeights) return 0.5;
        if (snapshot.Weights is not { Length: > 0 }) return 0.5;

        int numKernels = kernelWeights.Length;
        var dilations = snapshot.RocketKernelDilations;
        var paddings = snapshot.RocketKernelPaddings;
        var lengths = snapshot.RocketKernelLengths;

        if (dilations is null || paddings is null || lengths is null) return 0.5;

        // Standardise features
        var stdFeatures = new float[features.Length];
        for (int j = 0; j < features.Length; j++)
        {
            float mean = j < snapshot.Means.Length ? snapshot.Means[j] : 0f;
            float std = j < snapshot.Stds.Length ? snapshot.Stds[j] : 1f;
            stdFeatures[j] = std > 1e-7f ? (features[j] - mean) / std : 0f;
        }

        int F = stdFeatures.Length;
        var rocketFeatures = new double[2 * numKernels];

        for (int k = 0; k < numKernels; k++)
        {
            double[] w = kernelWeights[k];
            int len = k < lengths.Length ? lengths[k] : w.Length;
            int dil = k < dilations.Length ? dilations[k] : 1;
            bool pad = k < paddings.Length && paddings[k];

            int padding = pad ? (len - 1) * dil / 2 : 0;
            int outputLen = F + 2 * padding - (len - 1) * dil;

            double maxVal = double.MinValue;
            int ppvPos = 0;
            int posCount = 0;

            for (int pos = 0; pos < outputLen; pos++)
            {
                double dot = 0;
                for (int j = 0; j < len; j++)
                {
                    int srcIdx = pos + j * dil - padding;
                    double xVal = (srcIdx >= 0 && srcIdx < F) ? stdFeatures[srcIdx] : 0;
                    dot += w[j] * xVal;
                }
                if (dot > maxVal) maxVal = dot;
                if (dot > 0) ppvPos++;
                posCount++;
            }

            rocketFeatures[k] = maxVal == double.MinValue ? 0 : maxVal;
            rocketFeatures[numKernels + k] = posCount > 0 ? (double)ppvPos / posCount : 0;
        }

        // Standardise ROCKET features
        if (snapshot.RocketFeatureMeans is { Length: > 0 } rMeans &&
            snapshot.RocketFeatureStds is { Length: > 0 } rStds)
        {
            int dim = Math.Min(rocketFeatures.Length, Math.Min(rMeans.Length, rStds.Length));
            for (int j = 0; j < dim; j++)
            {
                double s = rStds[j] > 1e-8 ? rStds[j] : 1.0;
                rocketFeatures[j] = (rocketFeatures[j] - rMeans[j]) / s;
            }
        }

        // Ridge head
        double[] headW = snapshot.Weights[0];
        double headBias = snapshot.Biases.Length > 0 ? snapshot.Biases[0] : 0.0;
        int headDim = Math.Min(headW.Length, rocketFeatures.Length);

        double logit = headBias;
        for (int j = 0; j < headDim; j++)
            logit += headW[j] * rocketFeatures[j];

        return MLFeatureHelper.Sigmoid(logit);
    }
}
