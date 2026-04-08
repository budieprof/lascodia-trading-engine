using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    private readonly record struct TabNetArchitectureChoice(
        int NSteps,
        int HiddenDim,
        int AttentionDim,
        double Gamma,
        double DropoutRate,
        double SparsityCoeff);

    private readonly record struct TabNetAuditResult(
        string[] Findings,
        double MaxParityError,
        TabNetAuditArtifact Artifact);

    private TabNetArchitectureChoice SelectArchitectureConfig(
        List<TrainingSample> trainSet,
        List<TrainingSample> calSet,
        TrainingHyperparams hp,
        int featureCount,
        int baselineSteps,
        int baselineHiddenDim,
        int baselineAttentionDim,
        int sharedLayers,
        int stepLayers,
        double baselineGamma,
        bool useSparsemax,
        bool useGlu,
        double baselineDropout,
        double baselineSparsity,
        CancellationToken ct)
    {
        var baseline = new TabNetArchitectureChoice(
            baselineSteps, baselineHiddenDim, baselineAttentionDim,
            baselineGamma, baselineDropout, baselineSparsity);
        _lastAutoTuneTrace = [];

        if (hp.MaxEpochs < 12 || trainSet.Count < hp.MinSamples * 2 || calSet.Count < MinCalibrationSamples)
            return baseline;

        int tuneEpochs = Math.Max(6, Math.Min(12, hp.MaxEpochs / 3));
        double tuneLr = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        var tuneTrain = trainSet.Take(Math.Max(hp.MinSamples, (int)(trainSet.Count * 0.60))).ToList();
        var tuneCal = calSet.Take(Math.Max(MinCalibrationSamples, Math.Min(calSet.Count, 120))).ToList();
        if (tuneTrain.Count < hp.MinSamples || tuneCal.Count < MinCalibrationSamples)
            return baseline;

        var candidates = new List<TabNetArchitectureChoice>
        {
            baseline,
            baseline with
            {
                HiddenDim = Math.Max(8, baselineHiddenDim / 2),
                AttentionDim = Math.Max(1, Math.Min(baselineAttentionDim, Math.Max(8, baselineHiddenDim / 2))),
                Gamma = Math.Max(1.1, baselineGamma - 0.15),
                DropoutRate = Math.Min(0.30, baselineDropout + 0.05),
            },
            baseline with
            {
                HiddenDim = Math.Max(8, (int)Math.Round(baselineHiddenDim * 1.25)),
                AttentionDim = Math.Max(1, (int)Math.Round(Math.Min(baselineHiddenDim * 1.25, Math.Max(baselineAttentionDim, baselineHiddenDim)))),
                Gamma = Math.Min(1.9, baselineGamma + 0.10),
                SparsityCoeff = Math.Max(1e-5, baselineSparsity * 0.75),
            },
            baseline with
            {
                NSteps = Math.Max(2, baselineSteps - 1),
                HiddenDim = Math.Max(8, baselineHiddenDim),
                AttentionDim = Math.Max(1, Math.Min(baselineAttentionDim, baselineHiddenDim)),
                DropoutRate = Math.Min(0.30, baselineDropout + 0.03),
            },
            baseline with
            {
                NSteps = Math.Min(5, baselineSteps + 1),
                HiddenDim = Math.Max(8, baselineHiddenDim),
                AttentionDim = Math.Max(1, Math.Min(baselineHiddenDim, Math.Max(2, baselineAttentionDim))),
                Gamma = Math.Min(1.9, baselineGamma + 0.05),
            },
            baseline with
            {
                AttentionDim = Math.Max(1, Math.Max(4, baselineAttentionDim / 2)),
                SparsityCoeff = Math.Min(1e-3, Math.Max(1e-5, baselineSparsity * 1.25)),
            }
        };

        var distinctCandidates = candidates
            .Distinct()
            .ToList();
        var trace = new List<TabNetAutoTuneTraceEntry>(distinctCandidates.Count);

        double bestScore = double.NegativeInfinity;
        var bestChoice = baseline;

        foreach (var candidate in distinctCandidates)
        {
            ct.ThrowIfCancellationRequested();

            var tunedWeights = FitTabNet(
                tuneTrain,
                featureCount,
                candidate.NSteps,
                candidate.HiddenDim,
                Math.Clamp(candidate.AttentionDim, 1, candidate.HiddenDim),
                sharedLayers,
                stepLayers,
                candidate.Gamma,
                useSparsemax,
                useGlu,
                tuneLr,
                candidate.SparsityCoeff,
                tuneEpochs,
                hp.LabelSmoothing,
                null,
                null,
                null,
                hp.TemporalDecayLambda,
                hp.L2Lambda,
                Math.Max(3, hp.EarlyStoppingPatience / 2),
                hp.MagLossWeight,
                hp.MaxGradNorm,
                candidate.DropoutRate,
                hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98,
                hp.TabNetGhostBatchSize > 0 ? hp.TabNetGhostBatchSize : 128,
                0,
                ct);

            var tuneCalibration = FitTabNetCalibrationStack(tuneCal, tunedWeights, hp.FitTemperatureScale);
            var tuneMetrics = EvaluateTabNet(tuneCal, tunedWeights, tuneCalibration.FinalSnapshot, [], 0.0, featureCount);
            double tuneEce = ComputeEce(tuneCal, tunedWeights, tuneCalibration.FinalSnapshot);
            var cvHp = hp with
            {
                WalkForwardFolds = Math.Clamp(Math.Min(hp.WalkForwardFolds, 2), 1, 2),
                MaxEpochs = tuneEpochs,
                EarlyStoppingPatience = Math.Max(3, hp.EarlyStoppingPatience / 2),
                MaxBadFoldFraction = 1.0,
                MaxFoldDrawdown = 1.0,
                MinFoldCurveSharpe = -99.0,
                MinSharpeTrendSlope = -99.0,
            };
            var (tuneCv, _) = RunWalkForwardCV(
                tuneTrain, cvHp, featureCount, candidate.NSteps, candidate.HiddenDim,
                Math.Clamp(candidate.AttentionDim, 1, candidate.HiddenDim),
                sharedLayers, stepLayers, candidate.Gamma, useSparsemax, useGlu, tuneLr,
                candidate.SparsityCoeff, tuneEpochs, hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98, ct);

            double score =
                0.45 * tuneMetrics.Accuracy +
                0.10 * tuneMetrics.F1 +
                0.10 * Math.Tanh(tuneMetrics.ExpectedValue) +
                0.05 * Math.Tanh(tuneMetrics.SharpeRatio / 2.0) -
                0.18 * tuneMetrics.BrierScore -
                0.14 * tuneEce +
                0.25 * tuneCv.AvgAccuracy +
                0.06 * tuneCv.AvgF1 +
                0.05 * Math.Tanh(tuneCv.AvgEV) +
                0.03 * Math.Tanh(tuneCv.AvgSharpe / 2.0) -
                0.05 * tuneCv.StdAccuracy;

            trace.Add(new TabNetAutoTuneTraceEntry
            {
                Steps = candidate.NSteps,
                HiddenDim = candidate.HiddenDim,
                AttentionDim = Math.Clamp(candidate.AttentionDim, 1, candidate.HiddenDim),
                Gamma = candidate.Gamma,
                DropoutRate = candidate.DropoutRate,
                SparsityCoeff = candidate.SparsityCoeff,
                Score = score,
                CvAccuracy = tuneCv.AvgAccuracy,
                CvF1 = tuneCv.AvgF1,
                CvExpectedValue = tuneCv.AvgEV,
                CvSharpe = tuneCv.AvgSharpe,
                CvStdAccuracy = tuneCv.StdAccuracy,
                HoldoutAccuracy = tuneMetrics.Accuracy,
                HoldoutF1 = tuneMetrics.F1,
                HoldoutExpectedValue = tuneMetrics.ExpectedValue,
                HoldoutSharpe = tuneMetrics.SharpeRatio,
                HoldoutBrier = tuneMetrics.BrierScore,
                HoldoutEce = tuneEce,
                Selected = false,
            });

            if (score > bestScore)
            {
                bestScore = score;
                bestChoice = candidate;
            }
        }

        for (int i = 0; i < trace.Count; i++)
            trace[i].Selected = trace[i].Steps == bestChoice.NSteps &&
                                trace[i].HiddenDim == bestChoice.HiddenDim &&
                                trace[i].AttentionDim == bestChoice.AttentionDim &&
                                Math.Abs(trace[i].Gamma - bestChoice.Gamma) <= 1e-9 &&
                                Math.Abs(trace[i].DropoutRate - bestChoice.DropoutRate) <= 1e-9 &&
                                Math.Abs(trace[i].SparsityCoeff - bestChoice.SparsityCoeff) <= 1e-9;
        _lastAutoTuneTrace = trace
            .OrderByDescending(t => t.Score)
            .ToArray();

        if (!bestChoice.Equals(baseline))
        {
            _logger.LogInformation(
                "TabNet auto-tune selected steps={Steps} hidden={Hidden} attn={Attn} gamma={Gamma:F2} dropout={Dropout:F2} sparsity={Sparsity:G3}",
                bestChoice.NSteps, bestChoice.HiddenDim, bestChoice.AttentionDim,
                bestChoice.Gamma, bestChoice.DropoutRate, bestChoice.SparsityCoeff);
        }

        return bestChoice;
    }

    private static double ComputePruningCompositeScore(
        EvalMetrics metrics, double ece, int prunedCount, int totalFeatureCount, double[]? featureStabilityScores)
    {
        double sparsityGain = totalFeatureCount > 0 ? (double)prunedCount / totalFeatureCount : 0.0;
        double instabilityPenalty = 0.0;
        if (featureStabilityScores is { Length: > 0 })
        {
            instabilityPenalty = featureStabilityScores
                .Where(double.IsFinite)
                .DefaultIfEmpty(0.0)
                .Average();
        }

        return
            metrics.Accuracy +
            0.10 * metrics.F1 +
            0.08 * Math.Tanh(metrics.ExpectedValue) +
            0.05 * Math.Tanh(metrics.SharpeRatio / 2.0) -
            0.22 * metrics.BrierScore -
            0.18 * ece +
            0.15 * sparsityGain -
            0.05 * instabilityPenalty;
    }

    private TabNetAuditResult RunTabNetModelAudit(
        ModelSnapshot snapshot,
        TabNetWeights weights,
        IReadOnlyList<TrainingSample> rawAuditSamples)
    {
        var validation = TabNetSnapshotSupport.ValidateNormalizedSnapshot(snapshot, allowLegacyV2: false);
        if (!validation.IsValid)
            throw new InvalidOperationException($"TabNet snapshot audit failed contract validation: {string.Join("; ", validation.Issues)}");

        var findings = new List<string>();
        var engine = new TabNetInferenceEngine();
        int featureCount = snapshot.Features.Length > 0 ? snapshot.Features.Length : snapshot.Means.Length;
        double maxParityError = 0.0;
        double sumParityError = 0.0;
        double maxCalibratedDelta = 0.0;
        double maxUncertaintyObserved = 0.0;

        int auditCount = Math.Min(rawAuditSamples.Count, 24);
        for (int i = 0; i < auditCount; i++)
        {
            float[] features = MLSignalScorer.StandardiseFeatures(rawAuditSamples[i].Features, snapshot.Means, snapshot.Stds, featureCount);
            InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, snapshot);
            MLSignalScorer.ApplyFeatureMask(features, snapshot.ActiveFeatureMask, featureCount);

            double expected = TabNetRawProb(features, weights);
            var inference = engine.RunInference(
                features, featureCount, snapshot, new List<LascodiaTradingEngine.Domain.Entities.Candle>(), 0L, 0, 0);
            if (inference is null)
            {
                findings.Add($"Inference engine returned null during TabNet audit sample {i}.");
                continue;
            }

            double rawDelta = Math.Abs(expected - inference.Value.Probability);
            maxParityError = Math.Max(maxParityError, rawDelta);
            sumParityError += rawDelta;
            maxUncertaintyObserved = Math.Max(maxUncertaintyObserved, inference.Value.EnsembleStd);

            double expectedCalibrated = InferenceHelpers.ApplyDeployedCalibration(expected, snapshot);
            double observedCalibrated = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, snapshot);
            maxCalibratedDelta = Math.Max(maxCalibratedDelta, Math.Abs(expectedCalibrated - observedCalibrated));
        }

        if (maxParityError > 1e-6)
            findings.Add($"Train/inference raw-prob parity max error={maxParityError:E3}");
        if (snapshot.Ece > 0.10)
            findings.Add($"High calibration error ECE={snapshot.Ece:F3}");
        if (snapshot.TabNetUncertaintyThreshold <= 0.0)
            findings.Add("Missing TabNet uncertainty threshold.");
        if (snapshot.TabNetAttentionEntropyThreshold <= 0.0)
            findings.Add("Missing TabNet attention-entropy threshold.");

        var artifact = new TabNetAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            AuditedSampleCount = auditCount,
            MaxRawParityError = maxParityError,
            MeanRawParityError = auditCount > 0 ? sumParityError / auditCount : 0.0,
            MaxDeployedCalibrationDelta = maxCalibratedDelta,
            MaxUncertaintyObserved = maxUncertaintyObserved,
            RecordedEce = snapshot.Ece,
            FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
            PreprocessingFingerprint = snapshot.PreprocessingFingerprint,
            Findings = findings.ToArray(),
        };

        return new TabNetAuditResult(findings.ToArray(), maxParityError, artifact);
    }
}
