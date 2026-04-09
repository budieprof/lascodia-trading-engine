using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class FtTransformerModelTrainer
{
    private readonly record struct FtTransformerAuditResult(
        string[] Findings,
        double MaxParityError,
        FtTransformerAuditArtifact Artifact);

    private FtTransformerAuditResult RunFtTransformerModelAudit(
        ModelSnapshot snapshot,
        TransformerModel model,
        List<TrainingSample> rawAuditSamples,
        double decisionThreshold)
    {
        var normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = FtTransformerSnapshotSupport.ValidateNormalizedSnapshot(normalized);
        int featureCount = normalized.Features.Length;
        int rawFeatureCount = normalized.FtTransformerRawFeatureCount > 0
            ? normalized.FtTransformerRawFeatureCount
            : normalized.RawFeatureIndices.Length > 0
                ? normalized.RawFeatureIndices.Max() + 1
                : featureCount;
        int embedDim = normalized.FtTransformerEmbedDim;
        int numHeads = normalized.FtTransformerNumHeads > 0 ? normalized.FtTransformerNumHeads : 1;
        int ffnDim = normalized.FtTransformerFfnDim > 0 ? normalized.FtTransformerFfnDim : embedDim * 4;
        int activeFeatureCount = normalized.ActiveFeatureMask.Length > 0
            ? normalized.ActiveFeatureMask.Count(active => active)
            : featureCount;

        int auditCount = Math.Min(32, rawAuditSamples.Count);
        if (auditCount == 0)
        {
            var emptyArtifact = new FtTransformerAuditArtifact
            {
                SnapshotContractValid = validation.IsValid,
                AuditedSampleCount = 0,
                ActiveFeatureCount = activeFeatureCount,
                RawFeatureCount = rawFeatureCount,
                FeatureSchemaFingerprint = normalized.FeatureSchemaFingerprint,
                PreprocessingFingerprint = normalized.PreprocessingFingerprint,
                Findings = validation.IsValid ? [] : validation.Issues,
            };
            return new FtTransformerAuditResult(emptyArtifact.Findings, 0.0, emptyArtifact);
        }

        int auditStride = Math.Max(1, rawAuditSamples.Count / auditCount);
        var findings = new List<string>();
        if (!validation.IsValid)
            findings.AddRange(validation.Issues);

        double maxParityError = 0.0;
        double sumParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        int thresholdDecisionMismatchCount = 0;

        var trainerBuf = new InferenceBuffers(featureCount, embedDim, numHeads, ffnDim);
        var engine = new FtTransformerInferenceEngine();

        for (int ai = 0; ai < auditCount; ai++)
        {
            int sampleIndex = Math.Min(rawAuditSamples.Count - 1, ai * auditStride);
            try
            {
                float[] rawProjected = MLSignalScorer.ProjectRawFeaturesForSnapshot(
                    rawAuditSamples[sampleIndex].Features, normalized);
                float[] features = MLSignalScorer.StandardiseFeatures(
                    rawProjected, normalized.Means, normalized.Stds, featureCount);
                InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, normalized);
                MLSignalScorer.ApplyFeatureMask(features, normalized.ActiveFeatureMask, featureCount);

                double trainerRaw = ForwardPass(features, model, featureCount, trainerBuf);
                var inference = engine.RunInference(
                    features, featureCount, normalized, new List<Candle>(), 0L, 0, 0);
                if (inference is null)
                {
                    findings.Add($"Audit sample {sampleIndex} returned null FT inference.");
                    continue;
                }

                double rawParityError = Math.Abs(trainerRaw - inference.Value.Probability);
                double trainerDeployed = InferenceHelpers.ApplyDeployedCalibration(
                    trainerRaw,
                    normalized.PlattA,
                    normalized.PlattB,
                    normalized.TemperatureScale,
                    normalized.PlattABuy,
                    normalized.PlattBBuy,
                    normalized.PlattASell,
                    normalized.PlattBSell,
                    normalized.ConditionalCalibrationRoutingThreshold,
                    normalized.IsotonicBreakpoints,
                    applyAgeDecay: false);
                double inferenceDeployed = InferenceHelpers.ApplyDeployedCalibration(
                    inference.Value.Probability,
                    normalized.PlattA,
                    normalized.PlattB,
                    normalized.TemperatureScale,
                    normalized.PlattABuy,
                    normalized.PlattBBuy,
                    normalized.PlattASell,
                    normalized.PlattBSell,
                    normalized.ConditionalCalibrationRoutingThreshold,
                    normalized.IsotonicBreakpoints,
                    applyAgeDecay: false);
                double deployedDelta = Math.Abs(trainerDeployed - inferenceDeployed);

                maxParityError = Math.Max(maxParityError, rawParityError);
                sumParityError += rawParityError;
                maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, deployedDelta);
                if ((trainerDeployed >= decisionThreshold) != (inferenceDeployed >= decisionThreshold))
                    thresholdDecisionMismatchCount++;
            }
            catch (Exception ex)
            {
                findings.Add($"Audit sample {sampleIndex} failed: {ex.Message}");
            }
        }

        if (maxParityError > 1e-6)
            findings.Add($"Train/inference raw-prob parity max error={maxParityError:E3}");
        if (thresholdDecisionMismatchCount > 0)
            findings.Add($"Thresholded FT decision parity mismatches observed on {thresholdDecisionMismatchCount} audited samples.");

        var artifact = new FtTransformerAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            AuditedSampleCount = auditCount,
            ActiveFeatureCount = activeFeatureCount,
            RawFeatureCount = rawFeatureCount,
            MaxRawParityError = maxParityError,
            MeanRawParityError = auditCount > 0 ? sumParityError / auditCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            ThresholdDecisionMismatchCount = thresholdDecisionMismatchCount,
            RecordedEce = normalized.Ece,
            FeatureSchemaFingerprint = normalized.FeatureSchemaFingerprint,
            PreprocessingFingerprint = normalized.PreprocessingFingerprint,
            Findings = findings.ToArray(),
        };

        return new FtTransformerAuditResult(findings.ToArray(), maxParityError, artifact);
    }
}
