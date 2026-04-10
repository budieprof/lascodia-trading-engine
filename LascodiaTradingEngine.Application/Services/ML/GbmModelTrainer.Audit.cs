using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class GbmModelTrainer
{
    private readonly record struct GbmAuditResult(
        string[] Findings,
        double MaxParityError,
        GbmAuditArtifact Artifact);

    private static GbmMetricSummary CreateGbmMetricSummary(
        string splitName,
        EvalMetrics metrics,
        double ece,
        double threshold,
        int sampleCount)
    {
        return new GbmMetricSummary
        {
            SplitName = splitName,
            SampleCount = sampleCount,
            Threshold = threshold,
            Accuracy = metrics.Accuracy,
            Precision = metrics.Precision,
            Recall = metrics.Recall,
            F1 = metrics.F1,
            ExpectedValue = metrics.ExpectedValue,
            BrierScore = metrics.BrierScore,
            WeightedAccuracy = metrics.WeightedAccuracy,
            SharpeRatio = metrics.SharpeRatio,
            Ece = ece,
        };
    }

    private static GbmAuditResult RunGbmModelAudit(
        ModelSnapshot snapshot,
        IReadOnlyList<TrainingSample> rawAuditSamples)
    {
        var normalized = GbmSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = GbmSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: false);
        int featureCount = normalized.Features.Length;
        int rawFeatureCount = normalized.RawFeatureIndices.Length > 0
            ? normalized.RawFeatureIndices.Max() + 1
            : featureCount;
        int activeFeatureCount = normalized.ActiveFeatureMask is { Length: > 0 } mask
            ? mask.Count(active => active)
            : featureCount;

        var findings = new List<string>();
        if (!validation.IsValid)
            findings.AddRange(validation.Issues);
        if (normalized.GbmSelectionMetrics is null)
            findings.Add("Missing GBM selection metrics artifact.");
        if (normalized.GbmCalibrationMetrics is null)
            findings.Add("Missing GBM calibration metrics artifact.");
        if (normalized.GbmTestMetrics is null)
            findings.Add("Missing GBM test metrics artifact.");

        int auditCount = Math.Min(24, rawAuditSamples.Count);
        if (auditCount == 0)
        {
            var emptyArtifact = new GbmAuditArtifact
            {
                SnapshotContractValid = validation.IsValid,
                AuditedSampleCount = 0,
                ActiveFeatureCount = activeFeatureCount,
                RawFeatureCount = rawFeatureCount,
                RecordedEce = normalized.Ece,
                FeatureSchemaFingerprint = normalized.FeatureSchemaFingerprint,
                PreprocessingFingerprint = normalized.PreprocessingFingerprint,
                Findings = findings.ToArray(),
            };
            return new GbmAuditResult(emptyArtifact.Findings, 0.0, emptyArtifact);
        }

        var engine = new GbmInferenceEngine(new MemoryCache(new MemoryCacheOptions()));
        int auditStride = Math.Max(1, rawAuditSamples.Count / auditCount);
        double maxParityError = 0.0;
        double sumParityError = 0.0;
        double maxDeployedCalibrationDelta = 0.0;
        double maxTransformReplayShift = 0.0;
        double maxMaskApplicationShift = 0.0;
        int thresholdDecisionMismatchCount = 0;

        for (int auditIndex = 0; auditIndex < auditCount; auditIndex++)
        {
            int sampleIndex = Math.Min(rawAuditSamples.Count - 1, auditIndex * auditStride);
            try
            {
                float[] rawProjected = MLSignalScorer.ProjectRawFeaturesForSnapshot(
                    rawAuditSamples[sampleIndex].Features,
                    normalized);
                float[] features = MLSignalScorer.StandardiseFeatures(
                    rawProjected,
                    normalized.Means,
                    normalized.Stds,
                    featureCount);
                var beforeTransforms = (float[])features.Clone();
                InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, normalized);
                var beforeMask = (float[])features.Clone();
                MLSignalScorer.ApplyFeatureMask(features, normalized.ActiveFeatureMask, featureCount);

                double transformShift = 0.0;
                double maskShift = 0.0;
                for (int featureIndex = 0; featureIndex < featureCount; featureIndex++)
                {
                    transformShift += Math.Abs(beforeTransforms[featureIndex] - beforeMask[featureIndex]);
                    maskShift += Math.Abs(beforeMask[featureIndex] - features[featureIndex]);
                }

                double? trainerRaw = ComputeRawProbabilityFromSnapshotForAudit(features, normalized);
                var inference = engine.RunInference(
                    features,
                    featureCount,
                    normalized,
                    new List<Candle>(),
                    0L,
                    0,
                    0);
                if (!trainerRaw.HasValue || inference is null)
                {
                    findings.Add($"Audit sample {sampleIndex} could not be scored through both GBM paths.");
                    continue;
                }

                double rawParityError = Math.Abs(trainerRaw.Value - inference.Value.Probability);
                double trainerDeployed = InferenceHelpers.ApplyDeployedCalibration(trainerRaw.Value, normalized);
                double inferenceDeployed = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, normalized);

                maxParityError = Math.Max(maxParityError, rawParityError);
                sumParityError += rawParityError;
                maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, Math.Abs(trainerDeployed - inferenceDeployed));
                maxTransformReplayShift = Math.Max(maxTransformReplayShift, transformShift);
                maxMaskApplicationShift = Math.Max(maxMaskApplicationShift, maskShift);
                if ((trainerDeployed >= normalized.OptimalThreshold) != (inferenceDeployed >= normalized.OptimalThreshold))
                    thresholdDecisionMismatchCount++;
            }
            catch (Exception ex)
            {
                findings.Add($"Audit sample {sampleIndex} failed: {ex.Message}");
            }
        }

        if (maxParityError > 1e-6)
            findings.Add($"Train/inference GBM raw-prob parity max error={maxParityError:E3}");
        if (thresholdDecisionMismatchCount > 0)
        {
            findings.Add(
                $"Thresholded GBM decision parity mismatches observed on {thresholdDecisionMismatchCount} audited samples.");
        }

        var artifact = new GbmAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            AuditedSampleCount = auditCount,
            ActiveFeatureCount = activeFeatureCount,
            RawFeatureCount = rawFeatureCount,
            MaxRawParityError = maxParityError,
            MeanRawParityError = auditCount > 0 ? sumParityError / auditCount : 0.0,
            MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta,
            MaxTransformReplayShift = maxTransformReplayShift,
            MaxMaskApplicationShift = maxMaskApplicationShift,
            ThresholdDecisionMismatchCount = thresholdDecisionMismatchCount,
            RecordedEce = normalized.Ece,
            FeatureSchemaFingerprint = normalized.FeatureSchemaFingerprint,
            PreprocessingFingerprint = normalized.PreprocessingFingerprint,
            Findings = findings.ToArray(),
        };

        return new GbmAuditResult(findings.ToArray(), maxParityError, artifact);
    }
}
