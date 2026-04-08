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
        int holdoutStartIndex,
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
        TabNetRunContext runContext,
        CancellationToken ct)
    {
        var baseline = new TabNetArchitectureChoice(
            baselineSteps, baselineHiddenDim, baselineAttentionDim,
            baselineGamma, baselineDropout, baselineSparsity);
        runContext.AutoTuneTrace = [];

        if (hp.MaxEpochs < 12 || trainSet.Count < hp.MinSamples * 2 || calSet.Count < runContext.MinCalibrationSamples)
            return baseline;

        int tuneEpochs = Math.Max(6, Math.Min(12, hp.MaxEpochs / 3));
        double tuneLr = hp.LearningRate > 0 ? hp.LearningRate : 0.02;
        var tuneTrain = trainSet.Take(Math.Max(hp.MinSamples, (int)(trainSet.Count * 0.60))).ToList();
        var tuneCal = calSet.Take(Math.Max(runContext.MinCalibrationSamples, Math.Min(calSet.Count, 120))).ToList();
        if (tuneTrain.Count < hp.MinSamples || tuneCal.Count < runContext.MinCalibrationSamples)
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
            },
            baseline with
            {
                HiddenDim = Math.Max(8, (int)Math.Round(baselineHiddenDim * 0.75)),
                AttentionDim = Math.Max(1, Math.Min(Math.Max(4, baselineAttentionDim), Math.Max(8, (int)Math.Round(baselineHiddenDim * 0.75)))),
                DropoutRate = Math.Min(0.35, baselineDropout + 0.08),
                SparsityCoeff = Math.Min(1e-3, Math.Max(1e-5, baselineSparsity * 1.40)),
            },
            baseline with
            {
                NSteps = Math.Min(6, baselineSteps + 1),
                HiddenDim = Math.Max(8, (int)Math.Round(baselineHiddenDim * 1.10)),
                AttentionDim = Math.Max(1, (int)Math.Round(Math.Min(Math.Max(2, baselineAttentionDim), baselineHiddenDim * 1.10))),
                Gamma = Math.Min(1.95, baselineGamma + 0.12),
                DropoutRate = Math.Max(0.0, baselineDropout - 0.03),
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
                runContext,
                ct);

            var tuneCalibration = FitTabNetCalibrationStack(
                tuneCal,
                null,
                tunedWeights,
                hp.FitTemperatureScale,
                runContext.MinCalibrationSamples,
                runContext.CalibrationEpochs,
                runContext.CalibrationLr);
            double tuneThreshold = ComputeOptimalThreshold(
                tuneCal, tunedWeights, tuneCalibration.FinalSnapshot,
                hp.ThresholdSearchMin, hp.ThresholdSearchMax);
            var tuneMetrics = EvaluateTabNet(
                tuneCal, tunedWeights, tuneCalibration.FinalSnapshot, [], 0.0, featureCount, tuneThreshold);
            double tuneEce = ComputeEce(tuneCal, tunedWeights, tuneCalibration.FinalSnapshot);
            var cvHp = hp with
            {
                WalkForwardFolds = Math.Clamp(hp.WalkForwardFolds, 1, 3),
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
                candidate.SparsityCoeff, tuneEpochs, hp.TabNetMomentumBn > 0 ? hp.TabNetMomentumBn : 0.98, runContext, ct);

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
            string holdoutSliceHash = ComputeStableHash(
                $"tabnet-autotune:{holdoutStartIndex}:{tuneCal.Count}:{tuneTrain.Count}:{featureCount}");

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
                HoldoutThreshold = tuneThreshold,
                TuneTrainSampleCount = tuneTrain.Count,
                TuneHoldoutSampleCount = tuneCal.Count,
                HoldoutStartIndex = holdoutStartIndex,
                HoldoutCount = tuneCal.Count,
                CvFoldCount = tuneCv.FoldCount,
                HoldoutSplitName = "SELECTION",
                HoldoutSliceHash = holdoutSliceHash,
                ScoreBreakdown =
                    $"holdoutAcc=0.45*{tuneMetrics.Accuracy:F4};holdoutF1=0.10*{tuneMetrics.F1:F4};holdoutEv=0.10*tanh({tuneMetrics.ExpectedValue:F4});" +
                    $"holdoutSharpe=0.05*tanh({tuneMetrics.SharpeRatio:F4}/2);holdoutBrier=-0.18*{tuneMetrics.BrierScore:F4};" +
                    $"holdoutEce=-0.14*{tuneEce:F4};cvAcc=0.25*{tuneCv.AvgAccuracy:F4};cvF1=0.06*{tuneCv.AvgF1:F4};" +
                    $"cvEv=0.05*tanh({tuneCv.AvgEV:F4});cvSharpe=0.03*tanh({tuneCv.AvgSharpe:F4}/2);cvStd=-0.05*{tuneCv.StdAccuracy:F4}",
                Selected = false,
            });

            if (score > bestScore)
            {
                bestScore = score;
                bestChoice = candidate;
            }
        }

        double bestHoldoutAccuracy = trace
            .Where(entry => entry.Score == bestScore)
            .Select(entry => entry.HoldoutAccuracy)
            .DefaultIfEmpty(0.0)
            .Max();
        for (int i = 0; i < trace.Count; i++)
        {
            trace[i].Selected = trace[i].Steps == bestChoice.NSteps &&
                                trace[i].HiddenDim == bestChoice.HiddenDim &&
                                trace[i].AttentionDim == bestChoice.AttentionDim &&
                                Math.Abs(trace[i].Gamma - bestChoice.Gamma) <= 1e-9 &&
                                Math.Abs(trace[i].DropoutRate - bestChoice.DropoutRate) <= 1e-9 &&
                                Math.Abs(trace[i].SparsityCoeff - bestChoice.SparsityCoeff) <= 1e-9;
            if (trace[i].Selected)
            {
                trace[i].RejectionReasons = [];
                continue;
            }

            var reasons = new List<string>();
            if (trace[i].Score + 1e-6 < bestScore)
                reasons.Add("Composite score below winning candidate.");
            if (trace[i].HoldoutAccuracy + 1e-6 < bestHoldoutAccuracy)
                reasons.Add("Holdout accuracy below winner.");
            if (trace[i].HoldoutEce > trace.Where(entry => entry.Selected).Select(entry => entry.HoldoutEce).DefaultIfEmpty(trace[i].HoldoutEce).First() + 0.01)
                reasons.Add("Holdout ECE materially worse than winner.");
            if (trace[i].CvStdAccuracy > trace.Where(entry => entry.Selected).Select(entry => entry.CvStdAccuracy).DefaultIfEmpty(trace[i].CvStdAccuracy).First() + 0.01)
                reasons.Add("Cross-validation stability worse than winner.");
            trace[i].RejectionReasons = reasons.ToArray();
        }
        runContext.AutoTuneTrace = trace
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
        int activeFeatureCount = snapshot.ActiveFeatureMask is { Length: > 0 } mask
            ? mask.Count(v => v)
            : featureCount;
        double maxParityError = 0.0;
        double sumParityError = 0.0;
        double maxCalibratedDelta = 0.0;
        double maxUncertaintyObserved = 0.0;
        double maxTransformReplayShift = 0.0;
        double maxMaskApplicationShift = 0.0;
        int thresholdDecisionMismatchCount = 0;
        bool allAuditedInputsCollapsedAfterMask;

        if (snapshot.ActiveFeatureMask is { Length: > 0 } activeMask &&
            snapshot.PrunedFeatureCount != activeMask.Count(v => !v))
        {
            findings.Add("Active feature mask and pruned feature count are inconsistent.");
        }
        if (!double.IsFinite(snapshot.OptimalThreshold) || snapshot.OptimalThreshold <= 0.0 || snapshot.OptimalThreshold >= 1.0)
            findings.Add($"Optimal threshold is out of range: {snapshot.OptimalThreshold:F4}");
        if (snapshot.TabNetSelectionMetrics is null)
            findings.Add("Missing TabNet selection metrics artifact.");
        if (snapshot.TabNetCalibrationMetrics is null)
            findings.Add("Missing TabNet calibration metrics artifact.");
        if (snapshot.TabNetTestMetrics is null)
            findings.Add("Missing TabNet test metrics artifact.");
        if (snapshot.TabNetDriftArtifact is null)
            findings.Add("Missing TabNet drift artifact.");
        if (snapshot.TrainingSplitSummary is { } splitSummary)
        {
            if (splitSummary.SelectionCount <= 0)
                findings.Add("Selection split metadata is missing or empty.");
            if (splitSummary.CalibrationCount <= 0)
                findings.Add("Calibration split metadata is missing or empty.");
            if (splitSummary.TestCount <= 0)
                findings.Add("Test split metadata is missing or empty.");
        }

        // Distribute audit samples evenly across the dataset (not just first-N)
        const int MaxAuditSamples = 24;
        int auditCount = Math.Min(rawAuditSamples.Count, MaxAuditSamples);
        int auditStride = rawAuditSamples.Count > MaxAuditSamples
            ? rawAuditSamples.Count / MaxAuditSamples
            : 1;
        allAuditedInputsCollapsedAfterMask = auditCount > 0;
        for (int ai = 0; ai < auditCount; ai++)
        {
            int i = ai * auditStride;
            if (i >= rawAuditSamples.Count) break;
            float[] features = MLSignalScorer.StandardiseFeatures(rawAuditSamples[i].Features, snapshot.Means, snapshot.Stds, featureCount);
            var beforeTransforms = (float[])features.Clone();
            InferenceHelpers.ApplyModelSpecificFeatureTransforms(features, snapshot);
            var beforeMask = (float[])features.Clone();
            MLSignalScorer.ApplyFeatureMask(features, snapshot.ActiveFeatureMask, featureCount);

            double transformShift = 0.0;
            double maskShift = 0.0;
            double maskedL1 = 0.0;
            for (int j = 0; j < featureCount && j < beforeMask.Length && j < features.Length; j++)
            {
                transformShift += Math.Abs(beforeTransforms[j] - beforeMask[j]);
                maskShift += Math.Abs(beforeMask[j] - features[j]);
                maskedL1 += Math.Abs(features[j]);
            }
            maxTransformReplayShift = Math.Max(maxTransformReplayShift, transformShift);
            maxMaskApplicationShift = Math.Max(maxMaskApplicationShift, maskShift);
            if (maskedL1 > 1e-9)
                allAuditedInputsCollapsedAfterMask = false;

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
            bool expectedDecision = expectedCalibrated >= snapshot.OptimalThreshold;
            bool observedDecision = observedCalibrated >= snapshot.OptimalThreshold;
            if (expectedDecision != observedDecision)
                thresholdDecisionMismatchCount++;
        }

        // Weight array NaN/Inf sanity check
        static int CountNonFinite(double[][] arrays)
        {
            int count = 0;
            foreach (var arr in arrays)
                foreach (double v in arr)
                    if (!double.IsFinite(v)) count++;
            return count;
        }
        static int CountNonFinite1D(double[] arr)
        {
            int c = 0;
            foreach (double v in arr) if (!double.IsFinite(v)) c++;
            return c;
        }
        int nonFiniteWeights = 0;
        if (weights.OutputW is { Length: > 0 }) nonFiniteWeights += CountNonFinite1D(weights.OutputW);
        foreach (var layer in weights.SharedW) nonFiniteWeights += CountNonFinite(layer);
        foreach (var step in weights.StepW) foreach (var layer in step) nonFiniteWeights += CountNonFinite(layer);
        foreach (var step in weights.AttnFcW) nonFiniteWeights += CountNonFinite(step);
        if (nonFiniteWeights > 0)
            findings.Add($"Model contains {nonFiniteWeights} non-finite (NaN/Inf) weight values.");

        if (maxParityError > 1e-6)
            findings.Add($"Train/inference raw-prob parity max error={maxParityError:E3}");
        if (thresholdDecisionMismatchCount > 0)
            findings.Add($"Thresholded decision parity mismatches observed on {thresholdDecisionMismatchCount} audited samples.");
        if (snapshot.Ece > 0.10)
            findings.Add($"High calibration error ECE={snapshot.Ece:F3}");
        if (snapshot.TabNetUncertaintyThreshold <= 0.0)
            findings.Add("Missing TabNet uncertainty threshold.");
        if (snapshot.TabNetAttentionEntropyThreshold <= 0.0)
            findings.Add("Missing TabNet attention-entropy threshold.");
        if (allAuditedInputsCollapsedAfterMask)
            findings.Add("Every audited input collapsed to an all-zero deployed feature vector after masking.");

        var artifact = new TabNetAuditArtifact
        {
            SnapshotContractValid = validation.IsValid,
            AuditedSampleCount = auditCount,
            ActiveFeatureCount = activeFeatureCount,
            MaxRawParityError = maxParityError,
            MeanRawParityError = auditCount > 0 ? sumParityError / auditCount : 0.0,
            MaxDeployedCalibrationDelta = maxCalibratedDelta,
            MaxTransformReplayShift = maxTransformReplayShift,
            MaxMaskApplicationShift = maxMaskApplicationShift,
            ThresholdDecisionMismatchCount = thresholdDecisionMismatchCount,
            MaxUncertaintyObserved = maxUncertaintyObserved,
            RecordedEce = snapshot.Ece,
            FeatureSchemaFingerprint = snapshot.FeatureSchemaFingerprint,
            PreprocessingFingerprint = snapshot.PreprocessingFingerprint,
            Findings = findings.ToArray(),
        };

        return new TabNetAuditResult(findings.ToArray(), maxParityError, artifact);
    }
}
