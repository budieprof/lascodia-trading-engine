using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    private readonly record struct TcnAuditResult(
        string[] Findings,
        double MaxParityError,
        TcnAuditArtifact Artifact);

    private TcnAuditResult RunTcnModelAudit(
        ModelSnapshot snapshot,
        TcnWeights model,
        List<TrainingSample> auditSamples,
        in TcnCalibrationArtifacts calibration,
        int filters,
        bool useAttentionPool)
    {
        TcnSnapshotWeights? snapshotWeights = null;
        try
        {
            snapshotWeights = JsonSerializer.Deserialize<TcnSnapshotWeights>(
                snapshot.ConvWeightsJson ?? string.Empty,
                JsonOptions);
        }
        catch (Exception ex)
        {
            var failedArtifact = new TcnAuditArtifact
            {
                SnapshotContractValid = false,
                AuditedSampleCount = 0,
                ActiveChannelCount = TcnSnapshotSupport.ResolveActiveChannelMask(snapshot).Count(active => active),
                RawFeatureCount = snapshot.Features.Length,
                RecordedEce = snapshot.Ece,
                FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
                PreprocessingFingerprint = snapshot.PreprocessingFingerprint,
                Findings = [$"Failed to deserialize persisted TCN weights: {ex.Message}"],
            };
            return new TcnAuditResult(failedArtifact.Findings, 0.0, failedArtifact);
        }

        var validation = TcnSnapshotSupport.ValidateSnapshot(snapshot, snapshotWeights);
        int activeChannelCount = TcnSnapshotSupport.ResolveActiveChannelMask(snapshot).Count(active => active);

        if (auditSamples.Count == 0)
        {
            var emptyArtifact = new TcnAuditArtifact
            {
                SnapshotContractValid = validation.IsValid,
                AuditedSampleCount = 0,
                ActiveChannelCount = activeChannelCount,
                RawFeatureCount = snapshot.Features.Length,
                RecordedEce = snapshot.Ece,
                FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
                PreprocessingFingerprint = snapshot.PreprocessingFingerprint,
                Findings = validation.IsValid ? [] : validation.Issues,
            };
            return new TcnAuditResult(emptyArtifact.Findings, 0.0, emptyArtifact);
        }

        var findings = new List<string>();
        if (!validation.IsValid)
            findings.AddRange(validation.Issues);

        double maxParityError = 0.0;
        double sumParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        int thresholdDecisionMismatchCount = 0;
        double decisionThreshold = CalibrationThreshold(calibration);

        for (int ai = 0; ai < auditSamples.Count; ai++)
        {
            try
            {
                var sample = auditSamples[ai];
                if (sample.SequenceFeatures is not { Length: > 0 })
                {
                    findings.Add($"Audit sample {ai} is missing standardized sequence features.");
                    continue;
                }

                double trainerRaw = Math.Clamp(TcnProb(sample, model, filters, useAttentionPool), 1e-7, 1 - 1e-7);
                var inference = TcnInferenceEngine.RunStandardizedSequenceForwardPass(snapshot, sample.SequenceFeatures);
                if (inference is null)
                {
                    findings.Add($"Audit sample {ai} returned null TCN inference.");
                    continue;
                }

                double inferenceRaw = inference.Value.Probability;
                double rawParityError = Math.Abs(trainerRaw - inferenceRaw);
                double trainerDeployed = InferenceHelpers.ApplyDeployedCalibration(trainerRaw, snapshot);
                double inferenceDeployed = InferenceHelpers.ApplyDeployedCalibration(inferenceRaw, snapshot);
                double deployedDelta = Math.Abs(trainerDeployed - inferenceDeployed);

                maxParityError = Math.Max(maxParityError, rawParityError);
                sumParityError += rawParityError;
                maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, deployedDelta);
                if ((trainerDeployed >= decisionThreshold) != (inferenceDeployed >= decisionThreshold))
                    thresholdDecisionMismatchCount++;
            }
            catch (Exception ex)
            {
                findings.Add($"Audit sample {ai} failed: {ex.Message}");
            }
        }

        if (maxParityError > 1e-6)
            findings.Add($"Train/inference raw-prob parity max error={maxParityError:E3}");
        if (thresholdDecisionMismatchCount > 0)
            findings.Add($"Thresholded TCN decision parity mismatches observed on {thresholdDecisionMismatchCount} audited samples.");

        var artifact = new TcnAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            AuditedSampleCount = auditSamples.Count,
            ActiveChannelCount = activeChannelCount,
            RawFeatureCount = snapshot.Features.Length,
            MaxRawParityError = maxParityError,
            MeanRawParityError = auditSamples.Count > 0 ? sumParityError / auditSamples.Count : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdDecisionMismatchCount,
            RecordedEce = snapshot.Ece,
            FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
            PreprocessingFingerprint = snapshot.PreprocessingFingerprint,
            Findings = findings.ToArray(),
        };

        return new TcnAuditResult(findings.ToArray(), maxParityError, artifact);
    }
}
