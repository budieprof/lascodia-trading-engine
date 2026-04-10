using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class SmoteModelTrainer
{
    private readonly record struct SmoteAuditResult(
        string[] Findings,
        SmoteAuditArtifact Artifact);

    private static SmoteAuditResult RunSmoteAudit(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSet)
    {
        var findings = new List<string>();
        var artifact = new SmoteAuditArtifact { SnapshotContractValid = true };

        if (auditSet.Count == 0)
        {
            findings.Add("No audit samples available.");
            artifact.SnapshotContractValid = false;
            artifact.Findings = [.. findings];
            return new SmoteAuditResult(artifact.Findings, artifact);
        }

        var engine = new EnsembleInferenceEngine();
        if (!engine.CanHandle(snapshot))
        {
            findings.Add("EnsembleInferenceEngine refused the snapshot.");
            artifact.SnapshotContractValid = false;
            artifact.Findings = [.. findings];
            return new SmoteAuditResult(artifact.Findings, artifact);
        }

        double maxRawError = 0, sumRawError = 0, maxCalibDelta = 0;
        int thresholdMismatch = 0, audited = 0;

        foreach (var sample in auditSet.Take(32))
        {
            var inference = engine.RunInference(sample.Features, sample.Features.Length, snapshot, [], 0L, 0, 0);
            if (inference is null) { findings.Add("Inference returned null."); break; }

            double engineCalib = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, snapshot);
            maxCalibDelta = Math.Max(maxCalibDelta, Math.Abs(inference.Value.Probability - engineCalib));
            if ((engineCalib >= snapshot.OptimalThreshold) != (inference.Value.Probability >= 0.5))
                thresholdMismatch++;
            audited++;
        }

        artifact.AuditedSampleCount = audited;
        artifact.MaxRawParityError = maxRawError;
        artifact.MeanRawParityError = audited > 0 ? sumRawError / audited : 0;
        artifact.MaxDeployedCalibrationDelta = maxCalibDelta;
        artifact.ThresholdDecisionMismatchCount = thresholdMismatch;
        artifact.Findings = [.. findings.Distinct()];
        artifact.SnapshotContractValid = artifact.Findings.Length == 0;
        return new SmoteAuditResult(artifact.Findings, artifact);
    }
}
