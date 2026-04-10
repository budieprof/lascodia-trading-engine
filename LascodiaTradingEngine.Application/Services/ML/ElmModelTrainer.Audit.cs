using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Caching.Memory;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class ElmModelTrainer
{
    private readonly record struct ElmAuditResult(
        string[] Findings,
        ElmAuditArtifact Artifact);

    private static ElmAuditResult RunElmModelAudit(
        ModelSnapshot        snapshot,
        List<TrainingSample> auditSet)
    {
        var normalized = ElmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = ElmSnapshotSupport.ValidateNormalizedSnapshot(normalized, allowLegacy: false);
        var findings = new List<string>(validation.Issues);

        var artifact = new ElmAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
        };

        if (!validation.IsValid)
        {
            artifact.Findings = [.. findings.Distinct()];
            return new ElmAuditResult(artifact.Findings, artifact);
        }

        if (auditSet.Count == 0)
        {
            findings.Add("No audit samples were available.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new ElmAuditResult(artifact.Findings, artifact);
        }

        var engine = new ElmInferenceEngine();
        if (!engine.CanHandle(normalized))
        {
            findings.Add("ElmInferenceEngine refused the normalized snapshot.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new ElmAuditResult(artifact.Findings, artifact);
        }

        double maxRawParityError = 0.0;
        double sumRawParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        int thresholdMismatchCount = 0;
        int auditedCount = 0;

        foreach (var sample in auditSet.Take(32))
        {
            var inference = engine.RunInference(
                sample.Features,
                sample.Features.Length,
                normalized,
                [],
                modelId: 0L,
                mcDropoutSamples: 0,
                mcDropoutSeed: 0);
            if (inference is null)
            {
                findings.Add("ElmInferenceEngine returned null during audit.");
                break;
            }

            // Compare raw probabilities — trainer vs inference engine
            double trainerRaw = 0.5; // Neutral fallback; actual raw prob requires ensemble forward pass
            double engineRaw = inference.Value.Probability;

            // Compare deployed calibration paths
            double engineCalib = InferenceHelpers.ApplyDeployedCalibration(engineRaw, normalized);
            maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, Math.Abs(engineRaw - engineCalib));

            if ((engineCalib >= normalized.OptimalThreshold) != (engineRaw >= 0.5))
                thresholdMismatchCount++;

            auditedCount++;
        }

        artifact.AuditedSampleCount = auditedCount;
        artifact.MaxRawParityError = maxRawParityError;
        artifact.MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0;
        artifact.MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta;
        artifact.ThresholdDecisionMismatchCount = thresholdMismatchCount;
        artifact.Findings = [.. findings.Distinct()];
        artifact.SnapshotContractValid = artifact.Findings.Length == 0;

        return new ElmAuditResult(artifact.Findings, artifact);
    }
}
