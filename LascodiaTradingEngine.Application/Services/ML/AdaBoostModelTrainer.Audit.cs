using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Caching.Memory;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class AdaBoostModelTrainer
{
    private readonly record struct AdaBoostAuditResult(
        string[] Findings,
        AdaBoostAuditArtifact Artifact);

    private static AdaBoostMetricSummary CreateAdaBoostMetricSummary(
        string splitName,
        EvalMetrics metrics,
        double ece,
        double threshold,
        int sampleCount)
    {
        return new AdaBoostMetricSummary
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

    private static AdaBoostAuditResult CreateAdaBoostAuditArtifact(
        ModelSnapshot snapshot,
        List<TrainingSample> auditSet)
    {
        var normalized = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = AdaBoostSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: true);
        var findings = new List<string>(validation.Issues);
        int rawFeatureCount = normalized.Features.Length;
        int activeFeatureCount = normalized.ActiveFeatureMask.Length > 0
            ? normalized.ActiveFeatureMask.Count(static active => active)
            : rawFeatureCount;

        var artifact = new AdaBoostAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            ActiveFeatureCount = activeFeatureCount,
            RawFeatureCount = rawFeatureCount,
            RecordedEce = normalized.Ece,
            FeatureSchemaFingerprint = normalized.FeatureSchemaFingerprint,
            PreprocessingFingerprint = normalized.PreprocessingFingerprint,
        };

        if (!validation.IsValid)
        {
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new AdaBoostAuditResult(artifact.Findings, artifact);
        }

        if (auditSet.Count == 0)
        {
            findings.Add("No audit samples were available.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new AdaBoostAuditResult(artifact.Findings, artifact);
        }

        List<GbmTree>? stumps;
        try
        {
            stumps = JsonSerializer.Deserialize<List<GbmTree>>(normalized.GbmTreesJson!, JsonOptions);
        }
        catch (JsonException)
        {
            findings.Add("Failed to deserialize GbmTreesJson during AdaBoost audit.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new AdaBoostAuditResult(artifact.Findings, artifact);
        }

        if (stumps is not { Count: > 0 } || normalized.Weights is not { Length: > 0 })
        {
            findings.Add("AdaBoost audit could not reconstruct the stump ensemble.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new AdaBoostAuditResult(artifact.Findings, artifact);
        }

        var alphas = normalized.Weights[0].ToList();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var engine = new AdaBoostInferenceEngine(cache);
        if (!engine.CanHandle(normalized))
        {
            findings.Add("AdaBoostInferenceEngine refused the normalized snapshot.");
            artifact.Findings = [.. findings.Distinct()];
            artifact.SnapshotContractValid = false;
            return new AdaBoostAuditResult(artifact.Findings, artifact);
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
                findings.Add("AdaBoostInferenceEngine returned null during audit.");
                break;
            }

            double trainerRaw = PredictRawProb(sample.Features, stumps, alphas);
            double engineRaw = inference.Value.Probability;
            double rawDelta = Math.Abs(trainerRaw - engineRaw);
            maxRawParityError = Math.Max(maxRawParityError, rawDelta);
            sumRawParityError += rawDelta;

            double trainerCalib = PredictProb(
                sample.Features,
                stumps,
                alphas,
                normalized.PlattA,
                normalized.PlattB,
                normalized.TemperatureScale,
                normalized.IsotonicBreakpoints,
                normalized.OptimalThreshold,
                normalized.PlattABuy,
                normalized.PlattBBuy,
                normalized.PlattASell,
                normalized.PlattBSell,
                normalized.ConditionalCalibrationRoutingThreshold);
            double engineCalib = InferenceHelpers.ApplyDeployedCalibration(engineRaw, normalized);
            maxDeployedCalibrationDelta = Math.Max(maxDeployedCalibrationDelta, Math.Abs(trainerCalib - engineCalib));
            if ((trainerCalib >= normalized.OptimalThreshold) != (engineCalib >= normalized.OptimalThreshold))
                thresholdMismatchCount++;

            auditedCount++;
        }

        if (auditedCount == 0)
            findings.Add("AdaBoost audit did not evaluate any samples.");
        if (maxRawParityError > 1e-9)
            findings.Add($"AdaBoost raw-probability parity drift {maxRawParityError:G} exceeded 1e-9.");
        if (maxDeployedCalibrationDelta > 1e-9)
            findings.Add($"AdaBoost deployed-calibration drift {maxDeployedCalibrationDelta:G} exceeded 1e-9.");
        if (thresholdMismatchCount > 0)
            findings.Add($"AdaBoost threshold decision mismatches detected: {thresholdMismatchCount}.");

        artifact.AuditedSampleCount = auditedCount;
        artifact.MaxRawParityError = maxRawParityError;
        artifact.MeanRawParityError = auditedCount > 0 ? sumRawParityError / auditedCount : 0.0;
        artifact.MaxDeployedCalibrationDelta = maxDeployedCalibrationDelta;
        artifact.ThresholdDecisionMismatchCount = thresholdMismatchCount;
        artifact.Findings = [.. findings.Distinct()];
        artifact.SnapshotContractValid = artifact.Findings.Length == 0;

        return new AdaBoostAuditResult(artifact.Findings, artifact);
    }
}
