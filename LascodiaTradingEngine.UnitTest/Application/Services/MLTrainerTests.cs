using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class MLTrainerTests
{
    private static TrainingHyperparams DefaultHp() => new(
        K: 3, LearningRate: 0.01, L2Lambda: 0.001, MaxEpochs: 50,
        EarlyStoppingPatience: 5, MinAccuracyToPromote: 0.50, MinExpectedValue: -0.10,
        MaxBrierScore: 0.30, MinSharpeRatio: -1.0, MinSamples: 50,
        ShadowRequiredTrades: 30, ShadowExpiryDays: 14, WalkForwardFolds: 3,
        EmbargoBarCount: 10, TrainingTimeoutMinutes: 30, TemporalDecayLambda: 1.0,
        DriftWindowDays: 14, DriftMinPredictions: 30, DriftAccuracyThreshold: 0.50,
        MaxWalkForwardStdDev: 0.15, LabelSmoothing: 0.0, MinFeatureImportance: 0.0,
        EnableRegimeSpecificModels: false, FeatureSampleRatio: 1.0, MaxEce: 0,
        UseTripleBarrier: false, TripleBarrierProfitAtrMult: 2.0,
        TripleBarrierStopAtrMult: 1.0, TripleBarrierHorizonBars: 24,
        NoiseSigma: 0, FpCostWeight: 1.0, NclLambda: 0, FracDiffD: 0,
        MaxFoldDrawdown: 1.0, MinFoldCurveSharpe: -999, PolyLearnerFraction: 0,
        PurgeHorizonBars: 0, NoiseCorrectionThreshold: 0.4, MaxLearnerCorrelation: 0.95,
        SwaStartEpoch: 0, SwaFrequency: 1, MixupAlpha: 0.0,
        EnableGreedyEnsembleSelection: false, MaxGradNorm: 0.0,
        AtrLabelSensitivity: 0.0, ShadowMinZScore: 1.645,
        L1Lambda: 0.0, MagnitudeQuantileTau: 0.0, MagLossWeight: 0.0,
        DensityRatioWindowDays: 0, BarsPerDay: 24,
        DurbinWatsonThreshold: 0.0, AdaptiveLrDecayFactor: 0.0,
        OobPruningEnabled: false, MutualInfoRedundancyThreshold: 0.0,
        MinSharpeTrendSlope: -99.0, FitTemperatureScale: false,
        MinBrierSkillScore: -1.0, RecalibrationDecayLambda: 0.0,
        MaxEnsembleDiversity: 1.0, UseSymmetricCE: false,
        SymmetricCeAlpha: 0.0, DiversityLambda: 0.0,
        UseAdaptiveLabelSmoothing: false, AgeDecayLambda: 0.0,
        UseCovariateShiftWeights: false, MaxBadFoldFraction: 0.5,
        MinQualityRetentionRatio: 0.0, MultiTaskMagnitudeWeight: 0.3,
        CurriculumEasyFraction: 0.3, SelfDistillTemp: 3.0,
        FgsmEpsilon: 0.01, MinF1Score: 0.10, UseClassWeights: true);

    private static TrainingHyperparams TcnTestHp() => DefaultHp() with
    {
        MaxEpochs = 12,
        EarlyStoppingPatience = 3,
        WalkForwardFolds = 1,
        EmbargoBarCount = 0,
        TcnFilters = 8,
        TcnNumBlocks = 2,
        TcnUseAttentionPooling = false,
    };

    private static List<TrainingSample> GenerateSamples(int count, int featureCount = 33)
    {
        var rng = new Random(42);
        var samples = new List<TrainingSample>();
        for (int i = 0; i < count; i++)
        {
            var features = new float[featureCount];
            for (int j = 0; j < featureCount; j++)
                features[j] = (float)(rng.NextDouble() * 2 - 1);
            int direction = features[0] > 0 ? 1 : 0;
            float magnitude = Math.Abs(features[0]) * 0.5f;
            samples.Add(new TrainingSample(features, direction, magnitude));
        }
        return samples;
    }

    private static double PredictAdaBoostRawProbability(ModelSnapshot snapshot, float[] features)
    {
        var trees = JsonSerializer.Deserialize<List<GbmTree>>(snapshot.GbmTreesJson!);
        Assert.NotNull(trees);
        Assert.NotEmpty(trees);
        Assert.NotEmpty(snapshot.Weights);

        double score = 0.0;
        double[] alphas = snapshot.Weights[0];
        int count = Math.Min(trees.Count, alphas.Length);
        for (int i = 0; i < count; i++)
            score += alphas[i] * PredictAdaBoostTree(trees[i], features);

        return Math.Clamp(MLFeatureHelper.Sigmoid(2.0 * score), 1e-7, 1.0 - 1e-7);
    }

    private static double PredictAdaBoostTree(GbmTree tree, float[] features)
    {
        if (tree.Nodes is not { Count: > 0 })
            return 0.0;

        int nodeIndex = 0;
        while (true)
        {
            var node = tree.Nodes[nodeIndex];
            if (node.IsLeaf)
                return node.LeafValue;

            if (node.SplitFeature < 0 || node.SplitFeature >= features.Length)
                return 0.0;

            int nextIndex = features[node.SplitFeature] <= node.SplitThreshold
                ? node.LeftChild
                : node.RightChild;
            if (nextIndex < 0 || nextIndex >= tree.Nodes.Count)
                return 0.0;

            nodeIndex = nextIndex;
        }
    }

    private static List<TrainingSample> GenerateTcnSamples(int count, int featureCount = 33, int lookback = 30, int channels = 9)
    {
        var rng = new Random(42);
        var samples = new List<TrainingSample>();
        for (int i = 0; i < count; i++)
        {
            var features = new float[featureCount];
            for (int j = 0; j < featureCount; j++)
                features[j] = (float)(rng.NextDouble() * 2 - 1);

            var seq = new float[lookback][];
            for (int t = 0; t < lookback; t++)
            {
                seq[t] = new float[channels];
                for (int c = 0; c < channels; c++)
                    seq[t][c] = (float)(rng.NextDouble() * 2 - 1);
            }

            int direction = features[0] > 0 ? 1 : 0;
            float magnitude = Math.Abs(features[0]) * 0.5f;
            samples.Add(new TrainingSample(features, direction, magnitude, seq));
        }
        return samples;
    }

    private static List<TrainingSample> GenerateExclusiveFeatureSamples(int count, int featureCount = 33)
    {
        var samples = new List<TrainingSample>(count);
        int exclusiveSpan = Math.Min(8, featureCount);
        for (int i = 0; i < count; i++)
        {
            var features = new float[featureCount];
            int activeFeature = i % exclusiveSpan;
            features[activeFeature] = 1.0f + (activeFeature * 0.05f);
            if (activeFeature + exclusiveSpan < featureCount)
                features[activeFeature + exclusiveSpan] = (i % 3 == 0 ? 0.25f : -0.25f);

            int direction = activeFeature % 2 == 0 ? 1 : 0;
            float magnitude = 0.25f + activeFeature * 0.02f;
            samples.Add(new TrainingSample(features, direction, magnitude));
        }
        return samples;
    }

    private static List<TrainingSample> GenerateSparseSignalSamples(int count, int featureCount = 12)
    {
        var rng = new Random(17);
        var samples = new List<TrainingSample>(count);
        for (int i = 0; i < count; i++)
        {
            var features = new float[featureCount];
            float driver = (float)(rng.NextDouble() * 2.0 - 1.0);
            features[0] = driver;
            features[1] = driver * 0.7f + (float)(rng.NextDouble() * 0.1 - 0.05);
            for (int j = 2; j < featureCount; j++)
                features[j] = (float)(rng.NextDouble() * 0.02 - 0.01);

            int direction = driver >= 0 ? 1 : 0;
            float magnitude = Math.Abs(driver) * 0.5f;
            samples.Add(new TrainingSample(features, direction, magnitude));
        }

        return samples;
    }

    // ──────────────────────────────────────────────
    // GbmModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_ReturnsValidResult()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure — GBM uses tree-based storage, not weight arrays
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.False(string.IsNullOrEmpty(snap.GbmTreesJson), "GBM model should have GbmTreesJson");

        // Architecture-specific
        Assert.True(snap.Type.Contains("GBM", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_CancellationRespected()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_WarmStart_ReusesParentStandardization()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var baselineSamples = GenerateSamples(220);
        var baseline = await trainer.TrainAsync(baselineSamples, DefaultHp());
        var baselineSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(baseline.ModelBytes)!;

        var shiftedSamples = GenerateSamples(220)
            .Select(s =>
            {
                var shifted = (float[])s.Features.Clone();
                for (int i = 0; i < shifted.Length; i++)
                    shifted[i] += 5f;
                return s with { Features = shifted };
            })
            .ToList();

        var warmStarted = await trainer.TrainAsync(shiftedSamples, DefaultHp(), baselineSnapshot);
        var warmSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(warmStarted.ModelBytes)!;

        Assert.Equal(baselineSnapshot.GenerationNumber + 1, warmSnapshot.GenerationNumber);
        Assert.Equal(baselineSnapshot.Means, warmSnapshot.Means);
        Assert.Equal(baselineSnapshot.Stds, warmSnapshot.Stds);
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_PersistsFeaturePipelineDescriptors_ForBundledLayouts()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var samples = GenerateExclusiveFeatureSamples(240);

        var result = await trainer.TrainAsync(samples, DefaultHp());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.NotNull(snapshot.FeaturePipelineDescriptors);
        Assert.NotEmpty(snapshot.FeaturePipelineDescriptors);
        Assert.Contains(snapshot.FeaturePipelineDescriptors, descriptor =>
            string.Equals(descriptor.Operation, "GROUP_SUM_IN_PLACE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_ConformalOutputsStayInProbabilitySpace()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with { EmbargoBarCount = 0 };
        var result = await trainer.TrainAsync(GenerateSamples(360), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.InRange(snapshot.ConformalQHat, 0.0, 1.0);
        Assert.InRange(snapshot.ConformalQHatBuy, 0.0, 1.0);
        Assert.InRange(snapshot.ConformalQHatSell, 0.0, 1.0);
        Assert.True(snapshot.ConformalQHat > 0.0 && snapshot.ConformalQHat < 1.0);
        Assert.True(snapshot.ConformalQHatBuy > 0.0 && snapshot.ConformalQHatBuy < 1.0);
        Assert.True(snapshot.ConformalQHatSell > 0.0 && snapshot.ConformalQHatSell < 1.0);
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_PersistsMetaLabelMlpParameters()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with { GbmMetaLabelHiddenDim = 4, EmbargoBarCount = 0 };

        var result = await trainer.TrainAsync(GenerateSamples(360), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.Equal(4, snapshot.MetaLabelHiddenDim);
        Assert.NotEmpty(snapshot.MetaLabelWeights);
        Assert.NotEmpty(snapshot.MetaLabelHiddenWeights);
        Assert.NotEmpty(snapshot.MetaLabelHiddenBiases);
        Assert.Equal(snapshot.MetaLabelHiddenDim, snapshot.MetaLabelWeights.Length);
        Assert.Equal(snapshot.MetaLabelHiddenDim, snapshot.MetaLabelHiddenBiases.Length);
        Assert.True(snapshot.MetaLabelHiddenWeights.Length >= snapshot.MetaLabelHiddenDim);
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_PersistsSnapshotContractMetadata_And_PassesValidation()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var result = await trainer.TrainAsync(GenerateExclusiveFeatureSamples(240), DefaultHp() with { EmbargoBarCount = 0 });
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.NotEmpty(snapshot.RawFeatureIndices);
        Assert.NotEmpty(snapshot.FeatureSchemaFingerprint);
        Assert.NotEmpty(snapshot.PreprocessingFingerprint);
        Assert.NotEmpty(snapshot.TrainerFingerprint);
        Assert.True(snapshot.TrainingRandomSeed > 0);
        Assert.NotNull(snapshot.TrainingSplitSummary);
        Assert.NotNull(snapshot.GbmCalibrationArtifact);
        Assert.Equal(snapshot.Features.Length, snapshot.RawFeatureIndices.Length);
        Assert.True(snapshot.TrainingSplitSummary!.CalibrationFitCount > 0);
        Assert.True(snapshot.TrainingSplitSummary.CalibrationDiagnosticsCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.TrainingSplitSummary.AdaptiveHeadSplitMode));

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));
    }

    [Fact(Timeout = 30000)]
    public async Task Gbm_TrainAsync_PruningFallback_DoesNotPersist_SubTenActiveMask()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            MinFeatureImportance = 0.20,
            WalkForwardFolds = 2,
        };

        var result = await trainer.TrainAsync(GenerateSparseSignalSamples(240), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        int activeCount = snapshot.ActiveFeatureMask.Count(active => active);
        Assert.True(snapshot.PrunedFeatureCount == 0 || activeCount >= 10,
            $"Pruned GBM snapshots must either disable pruning or keep at least 10 active features, got {activeCount}.");
    }

    [Fact]
    public void GbmSnapshotSupport_Rejects_PerTreeLearningRateMismatch()
    {
        var features = new[] { "F0", "F1" };
        var rawIndices = new[] { 0, 1 };
        var mask = new[] { true, true };
        var treesJson = JsonSerializer.Serialize(new List<GbmTree>
        {
            new() { Nodes = [new GbmNode { IsLeaf = true, LeafValue = 0.25 }] }
        });

        var snapshot = new ModelSnapshot
        {
            Type = "GBM",
            Version = "3.2",
            Features = features,
            RawFeatureIndices = rawIndices,
            Means = [0f, 0f],
            Stds = [1f, 1f],
            ActiveFeatureMask = mask,
            PrunedFeatureCount = 0,
            BaseLearnersK = 1,
            GbmTreesJson = treesJson,
            GbmPerTreeLearningRates = [0.1, 0.2],
            OptimalThreshold = 0.5,
            ConformalQHat = 0.1,
            ConformalQHatBuy = 0.1,
            ConformalQHatSell = 0.1,
            ConditionalCalibrationRoutingThreshold = 0.5,
            FeatureSchemaFingerprint = GbmSnapshotSupport.ComputeFeatureSchemaFingerprint(features, features.Length),
            PreprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(features.Length, rawIndices, [], mask),
            TrainerFingerprint = "unit-test",
            TrainingSplitSummary = new TrainingSplitSummary
            {
                TrainCount = 1,
                CalibrationCount = 1,
                TestCount = 1,
                AdaptiveHeadSplitMode = "SHARED_CALIBRATION",
            },
        };

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Contains("GbmPerTreeLearningRates", StringComparison.Ordinal));
    }

    [Fact(Timeout = 60000)]
    public async Task Gbm_TrainAsync_Persists_DeployedCalibrationArtifact()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            FitTemperatureScale = true,
        };

        var result = await trainer.TrainAsync(GenerateSamples(420), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.NotNull(snapshot.TrainingSplitSummary);
        Assert.NotNull(snapshot.GbmCalibrationArtifact);

        var split = snapshot.TrainingSplitSummary!;
        var artifact = snapshot.GbmCalibrationArtifact!;

        Assert.True(artifact.SelectedGlobalCalibration is "PLATT" or "TEMPERATURE");
        Assert.Equal(artifact.TemperatureSelected, snapshot.TemperatureScale > 0.0);
        Assert.True(artifact.GlobalPlattNll >= 0.0);
        Assert.True(artifact.TemperatureNll >= 0.0);
        Assert.True(artifact.DiagnosticsSelectedGlobalNll >= 0.0);
        Assert.True(artifact.DiagnosticsSelectedStackNll >= 0.0);
        Assert.Equal(split.CalibrationFitCount, artifact.FitSampleCount);
        Assert.Equal(
            split.CalibrationDiagnosticsCount > 0 ? split.CalibrationDiagnosticsCount : split.CalibrationFitCount,
            artifact.DiagnosticsSampleCount);
        Assert.Equal(split.ConformalCount, artifact.ConformalSampleCount);
        Assert.Equal(split.MetaLabelCount, artifact.MetaLabelSampleCount);
        Assert.Equal(split.AbstentionCount, artifact.AbstentionSampleCount);
        Assert.Equal(split.AdaptiveHeadSplitMode, artifact.AdaptiveHeadMode, ignoreCase: true);
        Assert.Equal(split.AdaptiveHeadCrossFitFoldCount, artifact.AdaptiveHeadCrossFitFoldCount);
        Assert.Equal(split.CalibrationFitCount, artifact.IsotonicSampleCount);
        Assert.Equal(snapshot.IsotonicBreakpoints.Length / 2, artifact.IsotonicBreakpointCount);
        Assert.Equal(snapshot.IsotonicBreakpoints.Length >= 4, artifact.IsotonicAccepted);
        Assert.InRange(snapshot.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);
        Assert.Equal(snapshot.ConditionalCalibrationRoutingThreshold, artifact.ConditionalRoutingThreshold, 8);
        Assert.Equal(
            InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattABuy, snapshot.PlattBBuy),
            artifact.BuyBranchAccepted);
        Assert.Equal(
            InferenceHelpers.HasMeaningfulConditionalCalibration(snapshot.PlattASell, snapshot.PlattBSell),
            artifact.SellBranchAccepted);
        Assert.InRange(InferenceHelpers.ApplyDeployedCalibration(0.73, snapshot), 0.0, 1.0);
    }

    [Fact(Timeout = 60000)]
    public async Task Gbm_TrainAsync_PersistsAdaptiveHeadSplitMetadata_AndTrainingSeed()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            FitTemperatureScale = true,
        };

        var result = await trainer.TrainAsync(GenerateSamples(420), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.True(snapshot.TrainingRandomSeed > 0);
        Assert.NotNull(snapshot.TrainingSplitSummary);
        var split = snapshot.TrainingSplitSummary!;

        Assert.True(split.CalibrationFitCount > 0);
        Assert.True(split.CalibrationDiagnosticsCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(split.AdaptiveHeadSplitMode));
        Assert.InRange(snapshot.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);

        if (split.CalibrationDiagnosticsCount > 0 &&
            split.CalibrationFitCount > 0 &&
            split.CalibrationDiagnosticsStartIndex >= split.CalibrationFitStartIndex + split.CalibrationFitCount)
        {
            Assert.Equal(split.CalibrationCount, split.CalibrationFitCount + split.CalibrationDiagnosticsCount);
        }

        if (string.Equals(split.AdaptiveHeadSplitMode, "DISJOINT", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(split.CalibrationDiagnosticsCount, split.ConformalCount + split.MetaLabelCount + split.AbstentionCount);
            Assert.True(split.ConformalStartIndex <= split.MetaLabelStartIndex);
            Assert.True(split.MetaLabelStartIndex <= split.AbstentionStartIndex);
        }
        else if (string.Equals(split.AdaptiveHeadSplitMode, "CONFORMAL_DISJOINT_SHARED_ADAPTIVE", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(split.CalibrationDiagnosticsCount, split.ConformalCount + split.MetaLabelCount);
            Assert.Equal(split.MetaLabelCount, split.AbstentionCount);
            Assert.Equal(split.MetaLabelStartIndex, split.AbstentionStartIndex);
        }
        else
        {
            Assert.Equal("SHARED_FALLBACK", split.AdaptiveHeadSplitMode);
            Assert.Equal(split.CalibrationDiagnosticsCount, split.ConformalCount);
            Assert.Equal(split.CalibrationDiagnosticsCount, split.MetaLabelCount);
            Assert.Equal(split.CalibrationDiagnosticsCount, split.AbstentionCount);
        }

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));
    }

    [Fact(Timeout = 60000)]
    public async Task Gbm_TrainAsync_Uses_TrainOnlyStandardizationStatistics()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            GbmConceptDriftGate = false,
        };

        var samples = GenerateSamples(240);
        int trainCount = (int)(samples.Count * 0.70);
        for (int i = 0; i < samples.Count; i++)
            samples[i].Features[0] = i < trainCount ? 0f : 1000f;

        var result = await trainer.TrainAsync(samples, hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.InRange(snapshot.Means[0], -1.0, 1.0);
        Assert.True(snapshot.Means[0] < 10.0, $"Expected train-only mean, got {snapshot.Means[0]:F4}.");
    }

    [Fact(Timeout = 60000)]
    public async Task Gbm_TrainAsync_Persists_AuditArtifact_And_Parity()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            FitTemperatureScale = true,
        };

        var result = await trainer.TrainAsync(GenerateExclusiveFeatureSamples(320), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.NotNull(snapshot.GbmSelectionMetrics);
        Assert.NotNull(snapshot.GbmCalibrationMetrics);
        Assert.NotNull(snapshot.GbmTestMetrics);
        Assert.NotNull(snapshot.GbmAuditArtifact);
        Assert.True(double.IsFinite(snapshot.GbmTrainInferenceParityMaxError));
        Assert.InRange(snapshot.GbmTrainInferenceParityMaxError, 0.0, 1e-6);
        Assert.True(snapshot.GbmAuditArtifact!.SnapshotContractValid);
        Assert.Equal(0, snapshot.GbmAuditArtifact.ThresholdDecisionMismatchCount);
        Assert.Empty(snapshot.GbmAuditArtifact.Findings);
    }

    [Fact(Timeout = 60000)]
    public async Task Gbm_TrainAsync_RawAndDeployedParity_HoldForSerializedSnapshot()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            FitTemperatureScale = true,
        };

        var samples = GenerateExclusiveFeatureSamples(320);
        var result = await trainer.TrainAsync(samples, hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        int featureCount = snapshot.Features.Length;
        float[] projectedRaw = MLSignalScorer.ProjectRawFeaturesForSnapshot(samples[0].Features, snapshot);
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(projectedRaw, snapshot.Means, snapshot.Stds, featureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snapshot);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snapshot.ActiveFeatureMask, featureCount);

        double? expectedRaw = GbmModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(inferenceFeatures, snapshot);
        Assert.NotNull(expectedRaw);

        var engine = new GbmInferenceEngine(new MemoryCache(new MemoryCacheOptions()));
        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snapshot, new List<Candle>(), modelId: 17L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.Equal(expectedRaw.Value, inference.Value.Probability, 10);

        double expectedDeployed = InferenceHelpers.ApplyDeployedCalibration(expectedRaw.Value, snapshot);
        double engineDeployed = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, snapshot);

        Assert.Equal(expectedDeployed, engineDeployed, 10);
        Assert.InRange(expectedDeployed, 0.0, 1.0);
    }

    [Fact(Timeout = 30000)]
    public async Task GbmSnapshotSupport_AssessWarmStartCompatibility_RejectsTrainerFingerprintMismatch()
    {
        var trainer = new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>());
        var result = await trainer.TrainAsync(GenerateSamples(240), DefaultHp() with { EmbargoBarCount = 0 });
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        var compatibility = GbmSnapshotSupport.AssessWarmStartCompatibility(
            snapshot,
            snapshot.FeatureSchemaFingerprint,
            snapshot.PreprocessingFingerprint,
            "mismatched-trainer",
            snapshot.Features.Length);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains(
            compatibility.Issues,
            issue => issue.Contains("Trainer fingerprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GbmSnapshotSupport_Rejects_InvalidTreeStructure()
    {
        var features = new[] { "F0", "F1" };
        var rawIndices = new[] { 0, 1 };
        var mask = new[] { true, true };
        var treesJson = JsonSerializer.Serialize(new List<GbmTree>
        {
            new()
            {
                Nodes =
                [
                    new GbmNode { IsLeaf = false, SplitFeature = 2, SplitThreshold = 0.25, LeftChild = 1, RightChild = 3, SplitGain = 0.1 },
                    new GbmNode { IsLeaf = true, LeafValue = 0.4 },
                ]
            }
        });

        var snapshot = new ModelSnapshot
        {
            Type = "GBM",
            Version = "3.2",
            Features = features,
            RawFeatureIndices = rawIndices,
            Means = [0f, 0f],
            Stds = [1f, 1f],
            ActiveFeatureMask = mask,
            PrunedFeatureCount = 0,
            BaseLearnersK = 1,
            GbmTreesJson = treesJson,
            GbmPerTreeLearningRates = [0.1],
            OptimalThreshold = 0.5,
            ConformalQHat = 0.1,
            ConformalQHatBuy = 0.1,
            ConformalQHatSell = 0.1,
            ConditionalCalibrationRoutingThreshold = 0.5,
            FeatureSchemaFingerprint = GbmSnapshotSupport.ComputeFeatureSchemaFingerprint(features, features.Length),
            PreprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(features.Length, rawIndices, [], mask),
            TrainerFingerprint = "unit-test",
            TrainingRandomSeed = 7,
            TrainingSplitSummary = new TrainingSplitSummary
            {
                TrainCount = 1,
                SelectionCount = 1,
                CalibrationCount = 1,
                CalibrationFitCount = 1,
                CalibrationDiagnosticsCount = 1,
                ConformalCount = 1,
                MetaLabelCount = 1,
                AbstentionCount = 1,
                TestCount = 1,
                AdaptiveHeadSplitMode = "SHARED_FALLBACK",
            },
        };

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("tree[0]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GbmSnapshotSupport_Rejects_CyclicTreeStructure()
    {
        var features = new[] { "F0", "F1" };
        var rawIndices = new[] { 0, 1 };
        var mask = new[] { true, true };
        var treesJson = JsonSerializer.Serialize(new List<GbmTree>
        {
            new()
            {
                Nodes =
                [
                    new GbmNode { IsLeaf = false, SplitFeature = 0, SplitThreshold = 0.25, LeftChild = 1, RightChild = 1, SplitGain = 0.1 },
                    new GbmNode { IsLeaf = false, SplitFeature = 1, SplitThreshold = 0.10, LeftChild = 0, RightChild = 0, SplitGain = 0.2 },
                ]
            }
        });

        var snapshot = new ModelSnapshot
        {
            Type = "GBM",
            Version = "3.2",
            Features = features,
            RawFeatureIndices = rawIndices,
            Means = [0f, 0f],
            Stds = [1f, 1f],
            ActiveFeatureMask = mask,
            PrunedFeatureCount = 0,
            BaseLearnersK = 1,
            GbmTreesJson = treesJson,
            GbmPerTreeLearningRates = [0.1],
            OptimalThreshold = 0.5,
            ConformalQHat = 0.1,
            ConformalQHatBuy = 0.1,
            ConformalQHatSell = 0.1,
            ConditionalCalibrationRoutingThreshold = 0.5,
            FeatureSchemaFingerprint = GbmSnapshotSupport.ComputeFeatureSchemaFingerprint(features, features.Length),
            PreprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(features.Length, rawIndices, [], mask),
            TrainerFingerprint = "unit-test",
            TrainingRandomSeed = 17,
            TrainingSplitSummary = new TrainingSplitSummary
            {
                TrainCount = 1,
                SelectionCount = 1,
                CalibrationCount = 1,
                CalibrationFitCount = 1,
                CalibrationDiagnosticsCount = 1,
                ConformalCount = 1,
                MetaLabelCount = 1,
                AbstentionCount = 1,
                TestCount = 1,
                AdaptiveHeadSplitMode = "SHARED_FALLBACK",
            },
        };

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("cycle", StringComparison.OrdinalIgnoreCase) ||
                     issue.Contains("multiple parents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GbmSnapshotSupport_Rejects_CalibrationArtifactMismatch()
    {
        var features = new[] { "F0", "F1" };
        var rawIndices = new[] { 0, 1 };
        var mask = new[] { true, true };
        var split = new TrainingSplitSummary
        {
            TrainCount = 1,
            SelectionCount = 1,
            CalibrationCount = 2,
            CalibrationFitCount = 1,
            CalibrationDiagnosticsCount = 1,
            ConformalCount = 1,
            MetaLabelCount = 1,
            AbstentionCount = 1,
            TestCount = 1,
            AdaptiveHeadSplitMode = "SHARED_FALLBACK",
        };
        var treesJson = JsonSerializer.Serialize(new List<GbmTree>
        {
            new() { Nodes = [new GbmNode { IsLeaf = true, LeafValue = 0.25 }] }
        });

        var snapshot = new ModelSnapshot
        {
            Type = "GBM",
            Version = "3.2",
            Features = features,
            RawFeatureIndices = rawIndices,
            Means = [0f, 0f],
            Stds = [1f, 1f],
            ActiveFeatureMask = mask,
            PrunedFeatureCount = 0,
            BaseLearnersK = 1,
            GbmTreesJson = treesJson,
            GbmPerTreeLearningRates = [0.1],
            OptimalThreshold = 0.5,
            ConformalQHat = 0.1,
            ConformalQHatBuy = 0.1,
            ConformalQHatSell = 0.1,
            ConditionalCalibrationRoutingThreshold = 0.5,
            FeatureSchemaFingerprint = GbmSnapshotSupport.ComputeFeatureSchemaFingerprint(features, features.Length),
            PreprocessingFingerprint = GbmSnapshotSupport.ComputePreprocessingFingerprint(features.Length, rawIndices, [], mask),
            TrainerFingerprint = "unit-test",
            TrainingRandomSeed = 11,
            TrainingSplitSummary = split,
            GbmCalibrationArtifact = new GbmCalibrationArtifact
            {
                SelectedGlobalCalibration = "PLATT",
                CalibrationSelectionStrategy = "FIT_ON_FIT_EVAL_ON_DIAGNOSTICS",
                GlobalPlattNll = 0.0,
                TemperatureNll = 0.0,
                TemperatureSelected = false,
                FitSampleCount = 2,
                DiagnosticsSampleCount = 1,
                DiagnosticsSelectedGlobalNll = 0.0,
                DiagnosticsSelectedStackNll = 0.0,
                ConformalSampleCount = 1,
                MetaLabelSampleCount = 1,
                AbstentionSampleCount = 1,
                AdaptiveHeadMode = split.AdaptiveHeadSplitMode,
                AdaptiveHeadCrossFitFoldCount = split.AdaptiveHeadCrossFitFoldCount,
                ConditionalRoutingThreshold = 0.5,
                IsotonicSampleCount = 1,
                IsotonicBreakpointCount = 0,
                PreIsotonicNll = 0.0,
                PostIsotonicNll = 0.0,
            },
        };

        var validation = GbmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("GbmCalibrationArtifact.FitSampleCount", StringComparison.OrdinalIgnoreCase));
    }

    // ──────────────────────────────────────────────
    // ElmModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_ReturnsValidResult()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
        Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

        // Architecture-specific
        Assert.True(snap.Type.Contains("elm", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_CancellationRespected()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_DoesNotMutateCallerSamples_WhenWinsorizationEnabled()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var samples = GenerateSamples(200);
        var originalFeatures = samples.Select(sample => (float[])sample.Features.Clone()).ToArray();

        await trainer.TrainAsync(samples, DefaultHp() with { ElmWinsorizePercentile = 0.05 });

        for (int i = 0; i < samples.Count; i++)
            Assert.Equal(originalFeatures[i], samples[i].Features);
    }

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_Persists_Replay_Metadata_And_Cv_Diagnostics()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var hp = DefaultHp() with { ElmWinsorizePercentile = 0.05, EmbargoBarCount = 0 };

        var result = await trainer.TrainAsync(GenerateSamples(260), hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        Assert.NotEmpty(snapshot.ElmWinsorizeLowerBounds);
        Assert.Equal(snapshot.Features.Length, snapshot.ElmWinsorizeLowerBounds.Length);
        Assert.Equal(snapshot.Features.Length, snapshot.ElmWinsorizeUpperBounds.Length);
        Assert.NotEmpty(snapshot.MetaLabelTopFeatureIndices);
        Assert.All(snapshot.MetaLabelTopFeatureIndices, featureIndex =>
            Assert.InRange(featureIndex, 0, snapshot.Features.Length - 1));
        Assert.NotEmpty(snapshot.FeatureSchemaFingerprint);
        Assert.NotEmpty(snapshot.PreprocessingFingerprint);
        Assert.NotEmpty(snapshot.TrainerFingerprint);
        Assert.NotNull(result.CvResult.FoldMetrics);
        Assert.NotEmpty(result.CvResult.FoldMetrics!);
        Assert.NotNull(result.CvResult.OofResiduals);
        Assert.NotEmpty(result.CvResult.OofResiduals!);
    }

    [Fact]
    public async Task ElmSnapshotSupport_AssessWarmStartCompatibility_RejectsTrainerFingerprintMismatch()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var result = await trainer.TrainAsync(GenerateSamples(220), DefaultHp());
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        var compatibility = ElmSnapshotSupport.AssessWarmStartCompatibility(
            snapshot,
            snapshot.FeatureSchemaFingerprint,
            snapshot.PreprocessingFingerprint,
            "unit-test-mismatch",
            snapshot.Features.Length,
            snapshot.ElmHiddenDim);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains(
            compatibility.Issues,
            issue => issue.Contains("Trainer fingerprint mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ElmSnapshotSupport_ValidateSnapshot_Rejects_Misaligned_Learner_Metadata()
    {
        var snapshot = new ModelSnapshot
        {
            Type = "elm",
            Features = ["F0", "F1"],
            Means = [0f, 0f],
            Stds = [1f, 1f],
            Weights = [[0.1], [0.2]],
            Biases = [0.0, 0.0],
            ElmInputWeights = [[1.0, 2.0]],
            ElmInputBiases = [[0.0], [0.0]],
            FeatureSubsetIndices = [[0, 1]],
            ElmHiddenDim = 1,
            BaseLearnersK = 2,
            FeatureSchemaFingerprint = ElmSnapshotSupport.ComputeFeatureSchemaFingerprint(["F0", "F1"], 2),
            PreprocessingFingerprint = ElmSnapshotSupport.ComputePreprocessingFingerprint(2, 0.0, false),
            TrainerFingerprint = "unit-test",
        };

        var validation = ElmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue =>
            issue.Contains("ElmInputWeights length does not match learner count", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Issues, issue =>
            issue.Contains("FeatureSubsetIndices length does not match learner count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 30000)]
    public async Task Elm_TrainAsync_Snapshot_Replays_EndToEnd_Under_Strict_Validation()
    {
        var trainer = new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>());
        var hp = DefaultHp() with
        {
            ElmWinsorizePercentile = 0.05,
            FracDiffD = 0.25,
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
        };

        var samples = GenerateSamples(280);
        var result = await trainer.TrainAsync(samples, hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;
        var validation = ElmSnapshotSupport.ValidateSnapshot(snapshot, allowLegacy: false);

        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));

        var replayed = ElmFeaturePipelineHelper.ReplaySnapshotPreprocessing(samples.Take(48).ToList(), snapshot);

        Assert.Equal(48, replayed.Count);
        Assert.All(replayed, sample => Assert.Equal(snapshot.Features.Length, sample.Features.Length));
        Assert.All(replayed, sample => Assert.All(sample.Features, value => Assert.True(float.IsFinite(value))));

        var engine = new ElmInferenceEngine();
        var inference = engine.RunInference(
            replayed[^1].Features,
            snapshot.Features.Length,
            snapshot,
            new List<Candle>(),
            modelId: 1L,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(double.IsFinite(inference.Value.EnsembleStd));
    }

    // ──────────────────────────────────────────────
    // TcnModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_ReturnsValidResult()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = GenerateTcnSamples(160);

        var result = await trainer.TrainAsync(samples, TcnTestHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.Empty(snap.Weights);
        Assert.Empty(snap.Biases);

        // Architecture-specific
        Assert.True(snap.Type.Contains("TCN", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(snap.ConvWeightsJson);
        Assert.Equal(MLFeatureHelper.SequenceChannelCount, snap.TcnChannelNames.Length);
        Assert.Equal(MLFeatureHelper.SequenceChannelCount, snap.TcnActiveChannelMask.Length);
        Assert.Equal(MLFeatureHelper.SequenceChannelCount, snap.TcnChannelImportanceScores.Length);
        Assert.Equal(MLFeatureHelper.FeatureCount, snap.FeatureImportanceScores.Length);
        Assert.NotNull(snap.TcnCalibrationArtifact);
        Assert.NotNull(snap.TcnWarmStartArtifact);
        Assert.NotNull(snap.TcnAuditArtifact);
        Assert.False(string.IsNullOrWhiteSpace(snap.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.TrainerFingerprint));
        Assert.True(snap.TrainingRandomSeed > 0);
        Assert.True(double.IsFinite(snap.TcnTrainInferenceParityMaxError));
        Assert.True(snap.TcnTrainInferenceParityMaxError <= 1e-6);
        Assert.Equal(snap.CalSamples, snap.TcnCalibrationArtifact!.CalibrationSampleCount);
        Assert.Equal(snap.TestSamples, snap.TcnCalibrationArtifact.DiagnosticsSampleCount);
        Assert.True(snap.TcnWarmStartArtifact!.ReuseRatio >= 0.0);
        Assert.True(snap.TcnAuditArtifact!.AuditedSampleCount > 0);
        Assert.Equal(0, snap.TcnAuditArtifact.ThresholdDecisionMismatchCount);
        Assert.True(snap.MagWeights.Length > 0, "TCN snapshot should persist model-native magnitude head weights");
        Assert.InRange(snap.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);
        Assert.True(double.IsFinite(snap.RecalibrationStabilityA));
        Assert.True(double.IsFinite(snap.RecalibrationStabilityB));
        Assert.True(double.IsFinite(snap.AccuracyDecayRate));
        Assert.True(double.IsFinite(snap.F1DecayRate));
        Assert.True(double.IsFinite(result.CvResult.RecalibrationStabilityA));
        Assert.True(double.IsFinite(result.CvResult.RecalibrationStabilityB));
        Assert.True(double.IsFinite(result.CvResult.AccuracyDecayRate));
        Assert.True(double.IsFinite(result.CvResult.F1DecayRate));

        var tcnWeights = JsonSerializer.Deserialize<TcnModelTrainer.TcnSnapshotWeights>(snap.ConvWeightsJson!);
        var validation = TcnSnapshotSupport.ValidateSnapshot(snap, tcnWeights);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_Rejects_Unimplemented_Advanced_Config()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = GenerateTcnSamples(160);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            trainer.TrainAsync(samples, TcnTestHp() with { TcnUseGating = true }));
    }

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_Throws_When_Embargo_Collapses_Calibration_Split()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = GenerateTcnSamples(120);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            trainer.TrainAsync(samples, TcnTestHp() with { EmbargoBarCount = 20 }));
    }

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_CancellationRespected()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = GenerateTcnSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // AdaBoostModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task AdaBoost_TrainAsync_ReturnsValidResult()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(300);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure — AdaBoost uses tree-based stumps, not weight arrays
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.False(string.IsNullOrEmpty(snap.GbmTreesJson), "AdaBoost model should have GbmTreesJson (stump storage)");

        // Architecture-specific
        Assert.True(snap.Type.Contains("AdaBoost", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task AdaBoost_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task AdaBoost_TrainAsync_CancellationRespected()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    [Fact(Timeout = 60000)]
    public async Task AdaBoost_TrainAsync_PersistsCalibrationAndFingerprintMetadata()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(240, featureCount: 12);
        var hp = DefaultHp() with
        {
            K = 12,
            WalkForwardFolds = 2,
            EmbargoBarCount = 0,
            FitTemperatureScale = true,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);

        Assert.NotNull(snap);
        Assert.Equal(0, snap.GenerationNumber);
        Assert.False(string.IsNullOrWhiteSpace(snap.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.TrainerFingerprint));
        Assert.True(snap.TrainingRandomSeed > 0);
        Assert.Equal(snap.Features.Length, snap.FeatureImportanceScores.Length);
        Assert.Equal(snap.Features.Length, snap.ActiveFeatureMask.Length);
        Assert.Contains(snap.ActiveFeatureMask, v => v);
        Assert.Equal(snap.TrainSamples, snap.TrainSamplesAtLastCalibration);
        Assert.NotNull(snap.TrainingSplitSummary);
        Assert.Equal(snap.CalSamples, snap.TrainingSplitSummary!.SelectionCount);
        Assert.Equal(snap.CalSamples, snap.TrainingSplitSummary.CalibrationCount);
        Assert.Equal(snap.TestSamples, snap.TrainingSplitSummary.TestCount);
        Assert.InRange(snap.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);
        Assert.InRange(snap.OptimalThreshold, 0.01, 0.99);
        Assert.InRange(snap.ConformalQHatBuy, 0.0, 1.0);
        Assert.InRange(snap.ConformalQHatSell, 0.0, 1.0);
        Assert.NotEmpty(snap.MetaLabelTopFeatureIndices);
        Assert.True(snap.MetaLabelTopFeatureIndices.Length <= 5);
        Assert.Equal(snap.MetaLabelTopFeatureIndices.Length, snap.MetaLabelTopFeatureIndices.Distinct().Count());
        Assert.NotNull(snap.AdaBoostSelectionMetrics);
        Assert.NotNull(snap.AdaBoostCalibrationMetrics);
        Assert.NotNull(snap.AdaBoostTestMetrics);
        Assert.NotNull(snap.AdaBoostCalibrationArtifact);
        Assert.NotNull(snap.AdaBoostAuditArtifact);
        Assert.True(snap.AdaBoostAuditArtifact!.SnapshotContractValid);
        Assert.Empty(snap.AdaBoostAuditArtifact.Findings);
        Assert.Equal(0, snap.AdaBoostAuditArtifact.ThresholdDecisionMismatchCount);
        Assert.Equal(snap.TrainingSplitSummary.SelectionThresholdCount, snap.AdaBoostCalibrationArtifact!.ThresholdSelectionSampleCount);
        Assert.Equal(snap.TrainingSplitSummary.MetaLabelCount, snap.AdaBoostCalibrationArtifact.MetaLabelSampleCount);
        Assert.Equal(snap.TrainingSplitSummary.AbstentionCount, snap.AdaBoostCalibrationArtifact.AbstentionSampleCount);
        var validation = AdaBoostSnapshotSupport.ValidateSnapshot(snap, allowLegacy: false);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));
    }

    [Fact(Timeout = 60000)]
    public async Task AdaBoost_TrainAsync_AggressivePruning_DoesNotPersistInvalidMask()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSparseSignalSamples(320, featureCount: 12);
        var hp = DefaultHp() with
        {
            K = 12,
            WalkForwardFolds = 2,
            EmbargoBarCount = 0,
            MinFeatureImportance = 10.0,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);

        Assert.NotNull(snap);
        Assert.Equal(snap.Features.Length, snap.ActiveFeatureMask.Length);
        Assert.All(snap.ActiveFeatureMask, value => Assert.True(value));
        Assert.Equal(0, snap.PrunedFeatureCount);
    }

    [Fact(Timeout = 60000)]
    public async Task AdaBoost_TrainAsync_RejectsIncompatibleWarmStartSnapshot()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(220, featureCount: 12);
        var hp = DefaultHp() with
        {
            K = 10,
            WalkForwardFolds = 2,
            EmbargoBarCount = 0,
        };

        var baseline = await trainer.TrainAsync(samples, hp);
        var warmStart = JsonSerializer.Deserialize<ModelSnapshot>(baseline.ModelBytes)!;
        warmStart.FeatureSchemaFingerprint = "mismatch";
        warmStart.GenerationNumber = 7;

        var retrained = await trainer.TrainAsync(samples, hp, warmStart);
        var retrainedSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(retrained.ModelBytes);

        Assert.NotNull(retrainedSnapshot);
        Assert.Equal(0, retrainedSnapshot.GenerationNumber);
    }

    [Fact(Timeout = 30000)]
    public async Task AdaBoost_TrainAsync_InconsistentFeatureLengths_Throws()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = new List<TrainingSample>
        {
            new([1f, 2f, 3f], 1, 0.5f),
            new([1f, 2f], 0, 0.5f),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(samples, DefaultHp()));
        Assert.Contains("consistent feature counts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 60000)]
    public async Task AdaBoost_SnapshotSupport_NormalizesLegacySnapshot()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(240, featureCount: 12);
        var hp = DefaultHp() with
        {
            K = 10,
            WalkForwardFolds = 2,
            EmbargoBarCount = 0,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var legacy = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;
        legacy.ActiveFeatureMask = [];
        legacy.FeatureSchemaFingerprint = string.Empty;
        legacy.PreprocessingFingerprint = string.Empty;
        legacy.TrainerFingerprint = string.Empty;
        legacy.TrainingRandomSeed = 0;
        legacy.TrainingSplitSummary = null;
        legacy.AdaBoostSelectionMetrics = null;
        legacy.AdaBoostCalibrationMetrics = null;
        legacy.AdaBoostTestMetrics = null;
        legacy.AdaBoostCalibrationArtifact = null;
        legacy.AdaBoostAuditArtifact = null;
        legacy.ConformalQHatBuy = 0.0;
        legacy.ConformalQHatSell = 0.0;

        var normalized = AdaBoostSnapshotSupport.NormalizeSnapshotCopy(legacy);

        Assert.Equal(normalized.Features.Length, normalized.ActiveFeatureMask.Length);
        Assert.All(normalized.ActiveFeatureMask, value => Assert.True(value));
        Assert.False(string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.TrainerFingerprint));
        Assert.True(normalized.TrainingRandomSeed > 0);
        Assert.InRange(normalized.ConformalQHatBuy, 0.0, 1.0);
        Assert.InRange(normalized.ConformalQHatSell, 0.0, 1.0);

        var legacyValidation = AdaBoostSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: true);
        Assert.True(legacyValidation.IsValid, string.Join("; ", legacyValidation.Issues));

        var strictValidation = AdaBoostSnapshotSupport.ValidateSnapshot(normalized, allowLegacy: false);
        Assert.False(strictValidation.IsValid);
        Assert.Contains(strictValidation.Issues, issue => issue.Contains("TrainingSplitSummary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 60000)]
    public async Task AdaBoost_InferenceEngine_MatchesSerializedSnapshot()
    {
        var trainer = new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>());
        var samples = GenerateSamples(240, featureCount: 12);
        var hp = DefaultHp() with
        {
            K = 10,
            WalkForwardFolds = 2,
            EmbargoBarCount = 0,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var engine = new AdaBoostInferenceEngine(cache);

        Assert.True(engine.CanHandle(snapshot));

        var inference = engine.RunInference(
            samples[0].Features,
            samples[0].Features.Length,
            snapshot,
            [],
            modelId: 7L,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(inference);
        double expectedRawProbability = PredictAdaBoostRawProbability(snapshot, samples[0].Features);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(Math.Abs(expectedRawProbability - inference.Value.Probability) <= 1e-9);
        Assert.NotNull(snapshot.AdaBoostAuditArtifact);
        Assert.Equal(0, snapshot.AdaBoostAuditArtifact!.ThresholdDecisionMismatchCount);
        Assert.Empty(snapshot.AdaBoostAuditArtifact.Findings);
    }

    // ──────────────────────────────────────────────
    // RocketModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Rocket_TrainAsync_ReturnsValidResult()
    {
        var trainer = new RocketModelTrainer(Mock.Of<ILogger<RocketModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
        Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

        // Architecture-specific
        Assert.True(snap.Type.Contains("ROCKET", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Rocket_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new RocketModelTrainer(Mock.Of<ILogger<RocketModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Rocket_TrainAsync_CancellationRespected()
    {
        var trainer = new RocketModelTrainer(Mock.Of<ILogger<RocketModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // SmoteModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Smote_TrainAsync_ReturnsValidResult()
    {
        var trainer = new SmoteModelTrainer(Mock.Of<ILogger<SmoteModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
        Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

        // Architecture-specific
        Assert.True(snap.Type.Contains("SMOTE", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Smote_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new SmoteModelTrainer(Mock.Of<ILogger<SmoteModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Smote_TrainAsync_CancellationRespected()
    {
        var trainer = new SmoteModelTrainer(Mock.Of<ILogger<SmoteModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // QuantileRfModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task QuantileRf_TrainAsync_ReturnsValidResult()
    {
        var trainer = new QuantileRfModelTrainer(Mock.Of<ILogger<QuantileRfModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        // Output structure — QuantileRF uses tree-based storage, not weight arrays
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.False(string.IsNullOrEmpty(snap.GbmTreesJson), "QuantileRF model should have GbmTreesJson (tree storage)");

        // Architecture-specific
        Assert.True(snap.Type.Contains("quantilerf", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task QuantileRf_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new QuantileRfModelTrainer(Mock.Of<ILogger<QuantileRfModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task QuantileRf_TrainAsync_CancellationRespected()
    {
        var trainer = new QuantileRfModelTrainer(Mock.Of<ILogger<QuantileRfModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // FtTransformerModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 180000)]
    public async Task FtTransformer_TrainAsync_ReturnsValidResult()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        // FtTransformer (TorchSharp self-attention) is compute-heavy on CPU.
        // Reduce epochs and folds to keep execution within timeout.
        var samples = GenerateSamples(220);
        var hp = DefaultHp() with { MaxEpochs = 5, WalkForwardFolds = 2, EarlyStoppingPatience = 3, EmbargoBarCount = 0 };

        // On some platforms the TorchSharp diagonal op can fail with a known
        // libtorch compatibility issue. Accept both a valid result and the error.
        try
        {
            var result = await trainer.TrainAsync(samples, hp);

            Assert.NotNull(result);
            Assert.NotNull(result.ModelBytes);
            Assert.NotEmpty(result.ModelBytes);
            Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
            Assert.True(result.CvResult.FoldCount >= 1);

            // Output structure
            var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
            Assert.NotNull(snap);
            Assert.NotNull(snap.Weights);
            Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
            Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

            // Architecture-specific
            Assert.True(snap.Type.Contains("FTTRANSFORMER", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("8.0", snap.Version);
            Assert.Equal(snap.Features.Length, snap.Means.Length);
            Assert.Equal(snap.Features.Length, snap.Stds.Length);
            Assert.Equal(snap.Features.Length, snap.ActiveFeatureMask.Length);
            Assert.False(string.IsNullOrWhiteSpace(snap.FeatureSchemaFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(snap.PreprocessingFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(snap.TrainerFingerprint));
            Assert.True(snap.TrainingRandomSeed > 0);
            Assert.Equal(samples[0].Features.Length, snap.FtTransformerRawFeatureCount);
            Assert.NotNull(snap.TrainingSplitSummary);
            Assert.True(snap.TrainingSplitSummary!.SelectionCount >= 20);
            Assert.True(snap.TrainingSplitSummary.SelectionPruningCount >= 10);
            Assert.True(snap.TrainingSplitSummary.SelectionThresholdCount >= 5);
            Assert.True(snap.TrainingSplitSummary.SelectionKellyCount >= 5);
            Assert.True(snap.TrainingSplitSummary!.CalibrationCount >= 30);
            Assert.True(snap.TrainingSplitSummary.CalibrationFitCount >= 10);
            Assert.True(snap.TrainingSplitSummary.CalibrationDiagnosticsCount >= 10);
            Assert.True(snap.TrainingSplitSummary.ConformalCount >= 10);
            Assert.Equal(0, snap.TrainingSplitSummary.MetaLabelCount);
            Assert.Equal(0, snap.TrainingSplitSummary.AbstentionCount);
            Assert.True(snap.TrainingSplitSummary.TestCount >= 20);
            Assert.InRange(snap.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);
            Assert.NotNull(snap.FtTransformerSelectionMetrics);
            Assert.NotNull(snap.FtTransformerCalibrationMetrics);
            Assert.NotNull(snap.FtTransformerTestMetrics);
            Assert.NotNull(snap.FtTransformerCalibrationArtifact);
            Assert.NotNull(snap.FtTransformerWarmStartArtifact);
            Assert.NotNull(snap.FtTransformerAuditArtifact);
            Assert.Equal(snap.TrainingSplitSummary.AdaptiveHeadSplitMode, snap.FtTransformerCalibrationArtifact!.AdaptiveHeadMode, ignoreCase: true);
            Assert.Equal(snap.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount, snap.FtTransformerCalibrationArtifact.AdaptiveHeadCrossFitFoldCount);
            Assert.Equal(snap.TrainingSplitSummary.SelectionThresholdCount, snap.FtTransformerCalibrationArtifact.ThresholdSelectionSampleCount);
            Assert.True(snap.FtTransformerCalibrationArtifact.KellySelectionSampleCount > 0);
            Assert.Equal(snap.TrainingSplitSummary.ConformalCount, snap.FtTransformerCalibrationArtifact.ConformalSampleCount);
            Assert.Equal(
                snap.TrainingSplitSummary.CalibrationFitCount + snap.TrainingSplitSummary.CalibrationDiagnosticsCount,
                snap.FtTransformerCalibrationArtifact.RefitSampleCount);
            Assert.Equal(
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount,
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidates.Length);
            Assert.Equal(
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount,
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidateNlls.Length);
            Assert.Equal(
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidateCount,
                snap.FtTransformerCalibrationArtifact.RoutingThresholdCandidateEces.Length);
            Assert.Equal(
                snap.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount,
                snap.FtTransformerCalibrationArtifact.SelectedStackCrossFitFoldNlls.Length);
            Assert.Equal(
                snap.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount,
                snap.FtTransformerCalibrationArtifact.SelectedStackCrossFitFoldEces.Length);
            Assert.Equal(
                snap.FtTransformerCalibrationArtifact.IsotonicAccepted ? snap.IsotonicBreakpoints.Length / 2 : 0,
                snap.FtTransformerCalibrationArtifact.IsotonicBreakpointCount);
            Assert.True(snap.FtTransformerTrainInferenceParityMaxError <= 1e-6);
            Assert.Equal(0, snap.FtTransformerAuditArtifact!.ThresholdDecisionMismatchCount);
            if (snap.RawFeatureIndices.Length > 0)
            {
                Assert.Equal(snap.Features.Length, snap.RawFeatureIndices.Length);
                Assert.All(snap.ActiveFeatureMask, value => Assert.True(value));
            }

            // Metric sanity
            Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
            Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
            when (ex.Message.Contains("diagonal dimensions"))
        {
            // Known TorchSharp libtorch issue on this platform — skip gracefully.
        }
    }

    [Fact(Timeout = 30000)]
    public async Task FtTransformer_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task FtTransformer_TrainAsync_CancellationRespected()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task FtTransformer_TrainAsync_MixedFeatureLengths_Throws()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var samples = GenerateSamples(120);
        samples[17] = new TrainingSample(new float[samples[0].Features.Length - 1], 1, 0.2f);

        await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task FtTransformer_TrainAsync_LargeEmbargo_ThrowsInsteadOfProducingDegenerateSnapshot()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var samples = GenerateSamples(140);
        var hp = DefaultHp() with
        {
            MaxEpochs = 6,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            EmbargoBarCount = 20,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(samples, hp));
    }

    [Fact(Timeout = 30000)]
    public async Task FtTransformer_TrainAsync_InvalidHyperparams_Throws()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var samples = GenerateSamples(120);
        var hp = DefaultHp() with
        {
            MiniBatchSize = 0,
            FtDropoutRate = 1.0,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(samples, hp));
    }

    [Fact]
    public void FtTransformer_ProjectFeaturesByRawIndex_SelectsConfiguredOrder()
    {
        float[] projected = MLSignalScorer.ProjectFeaturesByRawIndex([1f, 2f, 3f, 4f], [3, 1]);

        Assert.Equal([4f, 2f], projected);
    }

    [Fact]
    public void FtTransformer_ProjectFeaturesByRawIndex_DuplicateIndicesThrow()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MLSignalScorer.ProjectFeaturesByRawIndex([1f, 2f, 3f, 4f], [1, 1]));
    }

    [Fact]
    public void FtTransformer_ProjectRawFeaturesForSnapshot_RawFeatureCountMismatch_Throws()
    {
        var snapshot = CreateSimpleFtTransformerSnapshot([2, 0]);

        Assert.Throws<InvalidOperationException>(() =>
            MLSignalScorer.ProjectRawFeaturesForSnapshot([1f, 2f], snapshot));
    }

    [Fact]
    public void FtTransformerInferenceEngine_PrunedSnapshot_UsesProjectedFeatureOrder()
    {
        var prunedSnapshot = CreateSimpleFtTransformerSnapshot([2, 0]);
        var compressedSnapshot = CreateSimpleFtTransformerSnapshot();
        var engine = new FtTransformerInferenceEngine();

        float[] rawFeatures = [1f, 2f, 3f];
        float[] projectedRaw = MLSignalScorer.ProjectFeaturesByRawIndex(rawFeatures, prunedSnapshot.RawFeatureIndices);
        float[] prunedFeatures = MLSignalScorer.StandardiseFeatures(
            projectedRaw, prunedSnapshot.Means, prunedSnapshot.Stds, prunedSnapshot.Features.Length);
        MLSignalScorer.ApplyFeatureMask(prunedFeatures, prunedSnapshot.ActiveFeatureMask, prunedSnapshot.Features.Length);

        float[] compressedFeatures = MLSignalScorer.StandardiseFeatures(
            [3f, 1f], compressedSnapshot.Means, compressedSnapshot.Stds, compressedSnapshot.Features.Length);
        MLSignalScorer.ApplyFeatureMask(compressedFeatures, compressedSnapshot.ActiveFeatureMask, compressedSnapshot.Features.Length);

        var prunedInference = engine.RunInference(
            prunedFeatures, prunedSnapshot.Features.Length, prunedSnapshot,
            new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);
        var compressedInference = engine.RunInference(
            compressedFeatures, compressedSnapshot.Features.Length, compressedSnapshot,
            new List<Candle>(), modelId: 2L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(prunedInference);
        Assert.NotNull(compressedInference);
        Assert.Equal(compressedInference.Value.Probability, prunedInference.Value.Probability, 8);
    }

    [Fact]
    public void FtTransformerInferenceEngine_InvalidFeatureCount_Throws()
    {
        var snapshot = CreateSimpleFtTransformerSnapshot();
        var engine = new FtTransformerInferenceEngine();

        Assert.Throws<InvalidOperationException>(() =>
            engine.RunInference(
                [1f],
                snapshot.Features.Length,
                snapshot,
                new List<Candle>(),
                modelId: 1L,
                mcDropoutSamples: 0,
                mcDropoutSeed: 0));
    }

    [Fact]
    public void FtTransformerInferenceEngine_InvalidSnapshotShape_Throws()
    {
        var snapshot = CreateSimpleFtTransformerSnapshot();
        snapshot.FtTransformerOutputWeights = [1.0];
        var engine = new FtTransformerInferenceEngine();

        Assert.Throws<InvalidOperationException>(() =>
            engine.RunInference(
                [3f, 1f],
                snapshot.Features.Length,
                snapshot,
                new List<Candle>(),
                modelId: 1L,
                mcDropoutSamples: 0,
                mcDropoutSeed: 0));
    }

    [Fact]
    public void FtTransformerInferenceEngine_GoldenSnapshot_StaysWithinRuntimeBudget()
    {
        var engine = new FtTransformerInferenceEngine();
        var snapshot = CreateSimpleFtTransformerSnapshot();
        var features = new float[] { 3f, 1f };
        var candles = new List<Candle>();

        for (int i = 0; i < 10; i++)
            engine.RunInference(features, snapshot.Features.Length, snapshot, candles, modelId: 7L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 200; i++)
        {
            var inference = engine.RunInference(
                features, snapshot.Features.Length, snapshot, candles, modelId: 7L, mcDropoutSamples: 0, mcDropoutSeed: 0);
            Assert.NotNull(inference);
        }

        sw.Stop();
        long totalAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        double avgMs = sw.Elapsed.TotalMilliseconds / 200.0;

        Assert.True(avgMs < 5.0, $"Average FT-Transformer inference time {avgMs:F3}ms exceeded the 5ms budget.");
        Assert.True(totalAlloc < 12_000_000, $"FT-Transformer inference allocated {totalAlloc} bytes over 200 runs.");
    }

    [Fact(Timeout = 90000)]
    public async Task FtTransformer_TrainAsync_RawAndDeployedParity_HoldForSerializedSnapshot()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var hp = DefaultHp() with
        {
            EmbargoBarCount = 0,
            WalkForwardFolds = 2,
            MaxEpochs = 5,
            EarlyStoppingPatience = 3,
            FitTemperatureScale = true,
        };

        var samples = GenerateExclusiveFeatureSamples(260);
        var result = await trainer.TrainAsync(samples, hp);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes)!;

        var replayed = ElmFeaturePipelineHelper.ReplaySnapshotPreprocessing([samples[0]], snapshot);
        int featureCount = snapshot.Features.Length;
        float[] inferenceFeatures = replayed[0].Features;

        double? expectedRaw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(inferenceFeatures, snapshot);
        Assert.NotNull(expectedRaw);

        var engine = new FtTransformerInferenceEngine();
        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snapshot, new List<Candle>(), modelId: 31L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.Equal(expectedRaw.Value, inference.Value.Probability, 10);

        double expectedDeployed = InferenceHelpers.ApplyDeployedCalibration(expectedRaw.Value, snapshot);
        double engineDeployed = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, snapshot);
        Assert.Equal(expectedDeployed, engineDeployed, 10);
    }

    [Fact]
    public void FtTransformer_PreparedFeaturesForPrunedSnapshot_MatchCompressedSnapshot()
    {
        var prunedSnapshot = CreateSimpleFtTransformerSnapshot([2, 0], rawFeatureCount: 3);
        var compressedSnapshot = CreateSimpleFtTransformerSnapshot();
        float[] rawFeatures = [1f, 2f, 3f];

        float[] prunedPrepared = ElmFeaturePipelineHelper
            .ReplaySnapshotPreprocessing([new TrainingSample(rawFeatures, 1, 0f)], prunedSnapshot)[0]
            .Features;
        float[] compressedPrepared = ElmFeaturePipelineHelper
            .ReplaySnapshotPreprocessing([new TrainingSample([3f, 1f], 1, 0f)], compressedSnapshot)[0]
            .Features;

        Assert.Equal(compressedPrepared, prunedPrepared);
    }

    [Fact(Timeout = 120000)]
    public async Task FtTransformer_TrainAsync_TestLabelPoisoning_DoesNotChangeSelectionArtifacts()
    {
        var trainer = new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>());
        var cleanSamples = GenerateSamples(260, featureCount: 12);
        int poisonFrom = (int)(cleanSamples.Count * 0.80);
        var poisonedSamples = cleanSamples
            .Select((sample, index) => index >= poisonFrom
                ? sample with { Direction = sample.Direction > 0 ? 0 : 1 }
                : sample)
            .ToList();
        var hp = DefaultHp() with
        {
            MaxEpochs = 6,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            EmbargoBarCount = 0,
        };

        var cleanResult = await trainer.TrainAsync(cleanSamples, hp);
        var poisonedResult = await trainer.TrainAsync(poisonedSamples, hp);

        var cleanSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(cleanResult.ModelBytes);
        var poisonedSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(poisonedResult.ModelBytes);

        Assert.NotNull(cleanSnapshot);
        Assert.NotNull(poisonedSnapshot);
        Assert.Equal(cleanSnapshot.OptimalThreshold, poisonedSnapshot.OptimalThreshold, 8);
        Assert.Equal(cleanSnapshot.ConditionalCalibrationRoutingThreshold, poisonedSnapshot.ConditionalCalibrationRoutingThreshold, 8);
        Assert.Equal(cleanSnapshot.ActiveFeatureMask, poisonedSnapshot.ActiveFeatureMask);
        Assert.Equal(cleanSnapshot.RawFeatureIndices, poisonedSnapshot.RawFeatureIndices);
        Assert.Equal(
            cleanSnapshot.FtTransformerCalibrationArtifact!.SelectedGlobalCalibration,
            poisonedSnapshot.FtTransformerCalibrationArtifact!.SelectedGlobalCalibration,
            ignoreCase: true);
        Assert.Equal(
            cleanSnapshot.FtTransformerCalibrationArtifact.ConditionalRoutingThreshold,
            poisonedSnapshot.FtTransformerCalibrationArtifact.ConditionalRoutingThreshold,
            8);
        Assert.Equal(
            cleanSnapshot.TrainingSplitSummary!.SelectionThresholdCount,
            poisonedSnapshot.TrainingSplitSummary!.SelectionThresholdCount);
        Assert.Equal(
            cleanSnapshot.TrainingSplitSummary.SelectionKellyCount,
            poisonedSnapshot.TrainingSplitSummary.SelectionKellyCount);
    }

    [Fact]
    public void FtTransformerSnapshotSupport_AssessWarmStartCompatibility_RejectsTrainerFingerprintMismatch()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());

        var compatibility = FtTransformerSnapshotSupport.AssessWarmStartCompatibility(
            snapshot,
            snapshot.FeatureSchemaFingerprint,
            snapshot.PreprocessingFingerprint,
            "mismatched-trainer",
            snapshot.Features.Length,
            snapshot.FtTransformerEmbedDim,
            snapshot.FtTransformerNumHeads,
            snapshot.FtTransformerFfnDim,
            snapshot.FtTransformerNumLayers);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains(
            compatibility.Issues,
            issue => issue.Contains("Trainer fingerprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FtTransformerSnapshotSupport_AssessWarmStartCompatibility_RejectsPreprocessingFingerprintMismatch()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());

        var compatibility = FtTransformerSnapshotSupport.AssessWarmStartCompatibility(
            snapshot,
            snapshot.FeatureSchemaFingerprint,
            "mismatched-preprocessing",
            snapshot.TrainerFingerprint,
            snapshot.Features.Length,
            snapshot.FtTransformerEmbedDim,
            snapshot.FtTransformerNumHeads,
            snapshot.FtTransformerFfnDim,
            snapshot.FtTransformerNumLayers);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains(
            compatibility.Issues,
            issue => issue.Contains("Preprocessing fingerprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FtTransformerSnapshotSupport_AssessWarmStartCompatibility_RejectsLayerCountMismatch()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());

        var compatibility = FtTransformerSnapshotSupport.AssessWarmStartCompatibility(
            snapshot,
            snapshot.FeatureSchemaFingerprint,
            snapshot.PreprocessingFingerprint,
            snapshot.TrainerFingerprint,
            snapshot.Features.Length,
            snapshot.FtTransformerEmbedDim,
            snapshot.FtTransformerNumHeads,
            snapshot.FtTransformerFfnDim,
            snapshot.FtTransformerNumLayers + 1);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains(
            compatibility.Issues,
            issue => issue.Contains("Layer count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FtTransformerSnapshot_AuditGradient_OutputHeadMatchesFiniteDifference()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());
        float[] features = [3f, 1f];
        double label = 1.0;
        double eps = 1e-5;

        double[] analytic = FtTransformerModelTrainer.ComputeOutputWeightGradientFromSnapshotForAudit(features, snapshot, label)!;
        Assert.NotNull(analytic);

        double NumericalLoss(ModelSnapshot snap)
        {
            double raw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(features, snap)!.Value;
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            return -(label * Math.Log(raw) + (1.0 - label) * Math.Log(1.0 - raw));
        }

        var plus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        plus.FtTransformerOutputWeights![0] += eps;
        var minus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        minus.FtTransformerOutputWeights![0] -= eps;

        double finiteDiff = (NumericalLoss(plus) - NumericalLoss(minus)) / (2.0 * eps);
        Assert.Equal(finiteDiff, analytic[0], 4);
    }

    [Fact]
    public void FtTransformerSnapshot_AuditGradient_PositionalBiasMatchesFiniteDifference()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());
        snapshot.FtTransformerPosBias = [new double[(snapshot.Features.Length + 1) * (snapshot.Features.Length + 1)]];

        float[] features = [3f, 1f];
        double label = 1.0;
        double eps = 1e-5;
        int offset = 1;

        double? analytic = FtTransformerModelTrainer.ComputePosBiasGradientFromSnapshotForAudit(
            features, snapshot, label, layerIndex: 0, headIndex: 0, offset: offset);
        Assert.NotNull(analytic);

        double NumericalLoss(ModelSnapshot snap)
        {
            double raw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(features, snap)!.Value;
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            return -(label * Math.Log(raw) + (1.0 - label) * Math.Log(1.0 - raw));
        }

        var plus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        plus.FtTransformerPosBias![0][offset] += eps;
        var minus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        minus.FtTransformerPosBias![0][offset] -= eps;

        double finiteDiff = (NumericalLoss(plus) - NumericalLoss(minus)) / (2.0 * eps);
        Assert.Equal(finiteDiff, analytic.Value, 4);
    }

    [Fact]
    public void FtTransformerSnapshot_AuditGradient_FinalLayerNormGammaMatchesFiniteDifference()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());
        float[] features = [3f, 1f];
        double label = 1.0;
        double eps = 1e-5;
        int index = 0;

        double? analytic = FtTransformerModelTrainer.ComputeFinalLayerNormGammaGradientFromSnapshotForAudit(
            features, snapshot, label, index);
        Assert.NotNull(analytic);

        double NumericalLoss(ModelSnapshot snap)
        {
            double raw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(features, snap)!.Value;
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            return -(label * Math.Log(raw) + (1.0 - label) * Math.Log(1.0 - raw));
        }

        var plus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        plus.FtTransformerGammaFinal![index] += eps;
        var minus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        minus.FtTransformerGammaFinal![index] -= eps;

        double finiteDiff = (NumericalLoss(plus) - NumericalLoss(minus)) / (2.0 * eps);
        Assert.Equal(finiteDiff, analytic.Value, 4);
    }

    [Fact]
    public void FtTransformerSnapshot_AuditGradient_AttentionValueProjectionMatchesFiniteDifference()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());
        float[] features = [3f, 1f];
        double label = 1.0;
        double eps = 1e-5;
        int row = 0;
        int col = 0;

        double? analytic = FtTransformerModelTrainer.ComputeAttentionValueWeightGradientFromSnapshotForAudit(
            features, snapshot, label, layerIndex: 0, rowIndex: row, columnIndex: col);
        Assert.NotNull(analytic);

        double NumericalLoss(ModelSnapshot snap)
        {
            double raw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(features, snap)!.Value;
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            return -(label * Math.Log(raw) + (1.0 - label) * Math.Log(1.0 - raw));
        }

        var plus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        plus.FtTransformerWv![row][col] += eps;
        var minus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        minus.FtTransformerWv![row][col] -= eps;

        double finiteDiff = (NumericalLoss(plus) - NumericalLoss(minus)) / (2.0 * eps);
        Assert.Equal(finiteDiff, analytic.Value, 4);
    }

    [Fact]
    public void FtTransformerSnapshot_AuditGradient_FfnFirstLayerMatchesFiniteDifference()
    {
        var snapshot = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(CreateSimpleFtTransformerSnapshot());
        snapshot.FtTransformerWff1 =
        [
            [0.25, -0.10],
            [0.15, 0.30],
        ];
        snapshot.FtTransformerBff1 = [0.05, -0.02];
        snapshot.FtTransformerWff2 =
        [
            [0.20, 0.10],
            [-0.15, 0.25],
        ];
        snapshot.FtTransformerBff2 = [0.01, -0.03];

        float[] features = [3f, 1f];
        double label = 1.0;
        double eps = 1e-5;
        int row = 0;
        int col = 0;

        double? analytic = FtTransformerModelTrainer.ComputeFfnFirstLayerWeightGradientFromSnapshotForAudit(
            features, snapshot, label, layerIndex: 0, rowIndex: row, columnIndex: col);
        Assert.NotNull(analytic);

        double NumericalLoss(ModelSnapshot snap)
        {
            double raw = FtTransformerModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(features, snap)!.Value;
            raw = Math.Clamp(raw, 1e-7, 1.0 - 1e-7);
            return -(label * Math.Log(raw) + (1.0 - label) * Math.Log(1.0 - raw));
        }

        var plus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        plus.FtTransformerWff1![row][col] += eps;
        var minus = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        minus.FtTransformerWff1![row][col] -= eps;

        double finiteDiff = (NumericalLoss(plus) - NumericalLoss(minus)) / (2.0 * eps);
        Assert.Equal(finiteDiff, analytic.Value, 4);
    }

    [Fact]
    public void FtTransformerSnapshotSupport_NormalizeSnapshotCopy_UpgradesLegacyVersion5Snapshot()
    {
        var snapshot = CreateSimpleFtTransformerSnapshot();
        snapshot.Version = "5.0";
        snapshot.FtTransformerRawFeatureCount = 0;
        snapshot.ActiveFeatureMask = [];
        snapshot.FeatureSchemaFingerprint = string.Empty;
        snapshot.PreprocessingFingerprint = string.Empty;
        snapshot.TrainerFingerprint = string.Empty;
        snapshot.TrainingSplitSummary = new TrainingSplitSummary
        {
            TrainStartIndex = 0,
            TrainCount = 40,
            SelectionStartIndex = 40,
            SelectionCount = 10,
            CalibrationStartIndex = 50,
            CalibrationCount = 12,
            CalibrationDiagnosticsStartIndex = 0,
            CalibrationDiagnosticsCount = 0,
            ConformalStartIndex = 0,
            ConformalCount = 0,
        };

        var normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);

        Assert.Equal("8.0", normalized.Version);
        Assert.Equal(normalized.Features.Length, normalized.ActiveFeatureMask.Length);
        Assert.True(normalized.ActiveFeatureMask.All(active => active));
        Assert.True(normalized.FtTransformerRawFeatureCount >= normalized.Features.Length);
        Assert.False(string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.TrainerFingerprint));
        Assert.Equal(
            normalized.TrainingSplitSummary!.CalibrationCount,
            normalized.TrainingSplitSummary.CalibrationDiagnosticsCount);
        Assert.Equal(
            normalized.TrainingSplitSummary.CalibrationDiagnosticsCount,
            normalized.TrainingSplitSummary.ConformalCount);
    }

    [Fact]
    public void FtTransformerSnapshotSupport_ValidateSnapshot_RejectsUnsupportedVersion()
    {
        var snapshot = CreateSimpleFtTransformerSnapshot();
        snapshot.Version = "4.0";

        var validation = FtTransformerSnapshotSupport.ValidateSnapshot(snapshot);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("Unsupported FT-Transformer snapshot version", StringComparison.OrdinalIgnoreCase));
    }

    // ──────────────────────────────────────────────
    // TabNetModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task TabNet_TrainAsync_ReturnsValidResult()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
        Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

        // Architecture-specific
        Assert.True(snap.Type.Contains("TABNET", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task TabNet_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task TabNet_TrainAsync_CancellationRespected()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_PolySnapshotAndScorePath_RemainUsable()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(320, featureCount: 12);
        var hp = DefaultHp() with
        {
            MaxEpochs = 10,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            PolyLearnerFraction = 1.0,
            MinFeatureImportance = 4.0,
            TabNetAttentionDim = 4,
            TabNetUseSparsemax = false,
        };

        var result = await trainer.TrainAsync(samples, hp);

        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.Equal("3.0", snap.Version);
        Assert.False(snap.TabNetUseSparsemax);
        Assert.Equal(samples[0].Features.Length, snap.TabNetRawFeatureCount);
        Assert.NotNull(snap.TabNetPolyTopFeatureIndices);
        bool polyAccepted = snap.TabNetPolyTopFeatureIndices!.Length > 1;
        if (polyAccepted)
        {
            Assert.True(snap.Features.Length > samples[0].Features.Length);
            Assert.NotNull(snap.FeaturePipelineDescriptors);
            Assert.NotEmpty(snap.FeaturePipelineDescriptors);
            Assert.Equal(
                snap.Features.Length - snap.TabNetRawFeatureCount,
                snap.FeaturePipelineDescriptors.Sum(d => d.OutputCount));
        }
        else
        {
            // Poly expansion was correctly rejected by the calibrated gate
            Assert.Equal(samples[0].Features.Length, snap.Features.Length);
        }
        Assert.False(string.IsNullOrWhiteSpace(snap.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.TrainerFingerprint));
        Assert.NotNull(snap.TabNetPruningDecision);
        Assert.Equal(snap.Features.Length, snap.ActiveFeatureMask.Length);
        Assert.Contains(snap.ActiveFeatureMask, v => v);
        Assert.Equal(snap.PrunedFeatureCount, snap.ActiveFeatureMask.Count(v => !v));
        Assert.NotNull(snap.TrainingSplitSummary);
        Assert.True(snap.TrainingSplitSummary!.SelectionCount > 0);
        Assert.NotNull(snap.TabNetAttentionFcWeights);
        Assert.Equal(4, snap.TabNetAttentionFcWeights![0][0].Length);

        int featureCount = snap.Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snap.Means, snap.Stds, featureCount);
        var beforeTransforms = (float[])inferenceFeatures.Clone();

        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);

        int rawFeatureCount = snap.TabNetRawFeatureCount;
        if (polyAccepted && snap.TabNetPolyTopFeatureIndices!.Length >= 2)
        {
            int leftIdx = snap.TabNetPolyTopFeatureIndices[0];
            int rightIdx = snap.TabNetPolyTopFeatureIndices[1];
            Assert.Equal(beforeTransforms[leftIdx] * beforeTransforms[rightIdx], inferenceFeatures[rawFeatureCount], 5);
        }

        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snap.ActiveFeatureMask, featureCount);

        var engine = new TabNetInferenceEngine();
        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snap, new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
    }

    [Fact]
    public void TabNetInferenceEngine_UsesStepZeroProjection_AndHonorsSparsemaxFlag()
    {
        var engine = new TabNetInferenceEngine();
        var features = new float[] { 1f, 0f };

        var softmaxNoProjection = engine.RunInference(
            features, features.Length, CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: false),
            new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);
        var softmaxWithProjection = engine.RunInference(
            features, features.Length, CreateSimpleTabNetSnapshot(useInitialProjection: true, useSparsemax: false),
            new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);
        var sparsemaxNoProjection = engine.RunInference(
            features, features.Length, CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true),
            new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(softmaxNoProjection);
        Assert.NotNull(softmaxWithProjection);
        Assert.NotNull(sparsemaxNoProjection);

        Assert.True(
            softmaxNoProjection.Value.Probability > softmaxWithProjection.Value.Probability + 0.10,
            "Step-0 initial projection should materially change the deployed probability.");
        Assert.True(
            sparsemaxNoProjection.Value.Probability > softmaxNoProjection.Value.Probability + 0.05,
            "Sparsemax and softmax should not collapse to the same deployed attention path.");
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_RawAndDeployedParity_HoldForSerializedSnapshot()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(220);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);

        int featureCount = snap.Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snap.Means, snap.Stds, featureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snap.ActiveFeatureMask, featureCount);

        double? expectedRaw = TabNetModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(inferenceFeatures, snap);
        Assert.NotNull(expectedRaw);

        var engine = new TabNetInferenceEngine();
        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snap, new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.Equal(expectedRaw.Value, inference.Value.Probability, 8);

        double deployed = InferenceHelpers.ApplyDeployedCalibration(inference.Value.Probability, snap);
        Assert.InRange(deployed, 0.0, 1.0);
        Assert.True(snap.TabNetAuditArtifact is not null);
        Assert.True(snap.TabNetAuditArtifact!.MaxRawParityError <= 1e-6);
        Assert.True(snap.TabNetAuditArtifact.MaxTransformReplayShift >= 0.0);
        Assert.Equal(0, snap.TabNetAuditArtifact.ThresholdDecisionMismatchCount);
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_Persists_DeployedCalibrationArtifact()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(220);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            FitTemperatureScale = true,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.TabNetCalibrationArtifact);

        var artifact = snap.TabNetCalibrationArtifact!;
        Assert.NotNull(snap.TrainingSplitSummary);
        Assert.True(artifact.SelectedGlobalCalibration is "PLATT" or "TEMPERATURE");
        Assert.Equal(artifact.TemperatureSelected, snap.TemperatureScale > 0.0);
        Assert.True(artifact.GlobalPlattNll >= 0.0);
        Assert.True(artifact.PreIsotonicNll >= 0.0);
        Assert.True(artifact.PostIsotonicNll <= artifact.PreIsotonicNll + 1e-6);
        Assert.Equal(snap.TrainingSplitSummary!.CalibrationFitCount, artifact.FitSampleCount);
        Assert.Equal(snap.TrainingSplitSummary.CalibrationDiagnosticsCount, artifact.DiagnosticsSampleCount);
        Assert.False(string.IsNullOrWhiteSpace(artifact.AdaptiveHeadMode));
        Assert.Equal(artifact.IsotonicAccepted ? snap.IsotonicBreakpoints.Length / 2 : 0, artifact.IsotonicBreakpointCount);
        Assert.InRange(snap.ConditionalCalibrationRoutingThreshold, 0.01, 0.99);
        Assert.Equal(
            InferenceHelpers.HasMeaningfulConditionalCalibration(snap.PlattABuy, snap.PlattBBuy),
            artifact.BuyBranchAccepted);
        Assert.Equal(
            InferenceHelpers.HasMeaningfulConditionalCalibration(snap.PlattASell, snap.PlattBSell),
            artifact.SellBranchAccepted);
        Assert.InRange(InferenceHelpers.ApplyDeployedCalibration(0.73, snap), 0.0, 1.0);
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_GluDisabled_AuditDiagnosticsRemainValid()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(240);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            TabNetUseGlu = false,
        };

        var result = await trainer.TrainAsync(samples, hp);

        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.False(snap.TabNetUseGlu);
        Assert.NotNull(snap.TabNetPerStepSparsity);
        Assert.Equal(snap.BaseLearnersK, snap.TabNetPerStepSparsity!.Length);
        Assert.NotNull(snap.TabNetBnDriftByLayer);
        Assert.True(snap.TabNetBnDriftByLayer!.Length > 0);
        Assert.True(snap.TabNetAttentionEntropyThreshold > 0.0);
        Assert.True(snap.TabNetUncertaintyThreshold > 0.0);
        Assert.NotNull(snap.TabNetAuditFindings);
        Assert.True(snap.TabNetTrainInferenceParityMaxError <= 1e-6);
        Assert.DoesNotContain(
            snap.TabNetAuditFindings!,
            finding => finding.Contains("Missing TabNet", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(snap.TabNetAuditArtifact);
        Assert.True(snap.TabNetCalibrationResidualThreshold > 0.0);

        int featureCount = snap.Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snap.Means, snap.Stds, featureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snap.ActiveFeatureMask, featureCount);

        var engine = new TabNetInferenceEngine();
        Assert.True(engine.CanHandle(snap));

        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snap, new List<Candle>(), modelId: 1L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(double.IsFinite(inference.Value.EnsembleStd));
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_WithUnsupervisedPretraining_RemainsInferenceCompatible()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(260);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            TabNetPretrainEpochs = 2,
            TabNetPretrainMaskFraction = 0.35,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);

        Assert.NotNull(snap);
        Assert.Equal("3.0", snap.Version);
        Assert.NotNull(snap.TabNetAuditArtifact);
        Assert.True(snap.TabNetTrainInferenceParityMaxError <= 1e-6);
        Assert.NotNull(snap.TabNetWarmStartArtifact);
        Assert.True(snap.TabNetWarmStartArtifact!.Attempted >= 0);

        int featureCount = snap.Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snap.Means, snap.Stds, featureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snap.ActiveFeatureMask, featureCount);

        double? expectedRaw = TabNetModelTrainer.ComputeRawProbabilityFromSnapshotForAudit(inferenceFeatures, snap);
        Assert.NotNull(expectedRaw);

        var engine = new TabNetInferenceEngine();
        var inference = engine.RunInference(
            inferenceFeatures, featureCount, snap, new List<Candle>(), modelId: 11L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.Equal(expectedRaw.Value, inference.Value.Probability, 8);
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_PersistsSelectionAndCalibrationSplitMetadata()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(260);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);

        Assert.NotNull(snap);
        Assert.NotNull(snap.TrainingSplitSummary);
        Assert.InRange(snap.OptimalThreshold, 0.0, 1.0);

        var split = snap.TrainingSplitSummary!;
        Assert.True(split.TrainCount >= hp.MinSamples);
        Assert.True(split.SelectionCount >= 1);
        Assert.True(split.CalibrationCount >= 1);
        Assert.True(split.TestCount >= 1);
        Assert.True(split.RawSelectionCount >= split.SelectionCount);
        Assert.True(split.RawCalibrationCount >= split.CalibrationCount);
        Assert.True(split.RawTestCount >= split.TestCount);
        Assert.True(split.CalibrationFitCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(split.AdaptiveHeadSplitMode));
        Assert.True(split.ConformalCount > 0);
        Assert.True(split.MetaLabelCount > 0);
        Assert.True(split.AbstentionCount > 0);
        Assert.NotNull(snap.TabNetSelectionMetrics);
        Assert.NotNull(snap.TabNetCalibrationMetrics);
        Assert.NotNull(snap.TabNetTestMetrics);
        Assert.NotNull(snap.TabNetCalibrationArtifact);
        Assert.NotNull(snap.TabNetDriftArtifact);
        Assert.Equal(split.SelectionCount, snap.TabNetSelectionMetrics!.SampleCount);
        Assert.Equal(split.CalibrationDiagnosticsCount, snap.TabNetCalibrationMetrics!.SampleCount);
        Assert.Equal(split.TestCount, snap.TabNetTestMetrics!.SampleCount);
        Assert.Equal(split.TrainCount, snap.TabNetDriftArtifact!.SampleCount);
        Assert.Equal(snap.Features.Length, snap.TabNetDriftArtifact.FeatureCount);
        Assert.InRange(snap.ConformalQHat, 0.0, 1.0);
        Assert.InRange(snap.ConformalQHatBuy, 0.0, 1.0);
        Assert.InRange(snap.ConformalQHatSell, 0.0, 1.0);
        Assert.Equal(split.CalibrationFitCount, snap.TabNetCalibrationArtifact!.FitSampleCount);
        Assert.Equal(split.CalibrationDiagnosticsCount, snap.TabNetCalibrationArtifact.DiagnosticsSampleCount);
        Assert.Equal(split.AdaptiveHeadCrossFitFoldCount, snap.TabNetCalibrationArtifact.AdaptiveHeadCrossFitFoldCount);
        Assert.Equal(split.AdaptiveHeadSplitMode, snap.TabNetCalibrationArtifact.AdaptiveHeadMode, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(snap.TabNetCalibrationArtifact.CalibrationSelectionStrategy));
        if (split.CalibrationDiagnosticsCount > 0 &&
            split.CalibrationDiagnosticsStartIndex >= split.CalibrationFitStartIndex + split.CalibrationFitCount)
            Assert.Equal(split.CalibrationCount, split.CalibrationFitCount + split.CalibrationDiagnosticsCount);
        if (string.Equals(split.AdaptiveHeadSplitMode, "CROSSFIT_SHARED_DIAGNOSTICS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(split.AdaptiveHeadCrossFitFoldCount >= 2);
            Assert.Equal(split.AdaptiveHeadCrossFitFoldCount, split.AdaptiveHeadCrossFitFoldStartIndices.Length);
            Assert.Equal(split.AdaptiveHeadCrossFitFoldCount, split.AdaptiveHeadCrossFitFoldCounts.Length);
            Assert.Equal(split.AdaptiveHeadCrossFitFoldCount, split.AdaptiveHeadCrossFitFoldHashes.Length);
            Assert.Equal(split.CalibrationDiagnosticsCount, split.AdaptiveHeadCrossFitFoldCounts.Sum());
        }
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_TestLabelPoisoning_DoesNotChangeSelectionArtifacts()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var cleanSamples = GenerateSamples(260, featureCount: 12);
        int poisonFrom = (int)(cleanSamples.Count * 0.80);
        var poisonedSamples = cleanSamples
            .Select((sample, index) => index >= poisonFrom
                ? sample with { Direction = sample.Direction > 0 ? 0 : 1 }
                : sample)
            .ToList();
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            MinFeatureImportance = 4.0,
        };

        var cleanResult = await trainer.TrainAsync(cleanSamples, hp);
        var poisonedResult = await trainer.TrainAsync(poisonedSamples, hp);

        var cleanSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(cleanResult.ModelBytes);
        var poisonedSnapshot = JsonSerializer.Deserialize<ModelSnapshot>(poisonedResult.ModelBytes);

        Assert.NotNull(cleanSnapshot);
        Assert.NotNull(poisonedSnapshot);
        Assert.Equal(cleanSnapshot.OptimalThreshold, poisonedSnapshot.OptimalThreshold, 8);
        Assert.Equal(cleanSnapshot.ActiveFeatureMask, poisonedSnapshot.ActiveFeatureMask);
        Assert.Equal(
            cleanSnapshot.TabNetPruningDecision!.PrunedFeatureCount,
            poisonedSnapshot.TabNetPruningDecision!.PrunedFeatureCount);
    }

    [Fact(Timeout = 120000)]
    public async Task TabNet_TrainAsync_ConcurrentRuns_KeepRunSpecificArtifactsIsolated()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var hpA = DefaultHp() with
        {
            MaxEpochs = 6,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            TabNetUseSparsemax = false,
            TabNetUseGlu = true,
        };
        var hpB = hpA with
        {
            TabNetUseSparsemax = true,
            TabNetUseGlu = false,
        };

        var taskA = trainer.TrainAsync(GenerateSamples(220, featureCount: 12), hpA);
        var taskB = trainer.TrainAsync(GenerateSamples(220, featureCount: 12), hpB);

        var results = await Task.WhenAll(taskA, taskB);

        var snapshotA = JsonSerializer.Deserialize<ModelSnapshot>(results[0].ModelBytes);
        var snapshotB = JsonSerializer.Deserialize<ModelSnapshot>(results[1].ModelBytes);

        Assert.NotNull(snapshotA);
        Assert.NotNull(snapshotB);
        Assert.False(snapshotA.TabNetUseSparsemax);
        Assert.True(snapshotB.TabNetUseSparsemax);
        Assert.True(snapshotA.TabNetUseGlu);
        Assert.False(snapshotB.TabNetUseGlu);
        Assert.NotNull(snapshotA.TrainingSplitSummary);
        Assert.NotNull(snapshotB.TrainingSplitSummary);
        Assert.NotNull(snapshotA.TabNetWarmStartArtifact);
        Assert.NotNull(snapshotB.TabNetWarmStartArtifact);
    }

    [Fact(Timeout = 60000)]
    public async Task TabNet_TrainAsync_LargeEmbargo_ThrowsInsteadOfProducingDegenerateSnapshot()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(140);
        var hp = DefaultHp() with
        {
            MaxEpochs = 6,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            EmbargoBarCount = 20,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => trainer.TrainAsync(samples, hp));
    }

    [Fact]
    public void TabNetSnapshotSupport_Rejects_AllFalseMask_AndBadPrunedCount()
    {
        var invalidMask = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        invalidMask.ActiveFeatureMask = [false, false];
        invalidMask.PrunedFeatureCount = 2;

        var invalidMaskValidation = TabNetSnapshotSupport.ValidateSnapshot(invalidMask, allowLegacyV2: false);

        Assert.False(invalidMaskValidation.IsValid);
        Assert.Contains(
            invalidMaskValidation.Issues,
            issue => issue.Contains("prune every feature", StringComparison.OrdinalIgnoreCase));

        var invalidPrunedCount = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        invalidPrunedCount.ActiveFeatureMask = [true, false];
        invalidPrunedCount.PrunedFeatureCount = 0;

        var invalidPrunedCountValidation = TabNetSnapshotSupport.ValidateSnapshot(invalidPrunedCount, allowLegacyV2: false);

        Assert.False(invalidPrunedCountValidation.IsValid);
        Assert.Contains(
            invalidPrunedCountValidation.Issues,
            issue => issue.Contains("PrunedFeatureCount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TabNetSnapshotSupport_UpgradesLegacyFields_AndRejectsInvalidPolyLayouts()
    {
        var upgraded = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        upgraded.TabNetOutputHeadWeights = null;
        upgraded.TabNetOutputWeight = 0.75;
        upgraded.TabNetOutputHeadBias = 0.0;
        upgraded.Biases = [0.25];
        upgraded.TabNetPerStepAttention = [new[] { 0.9, 0.1 }];
        upgraded.TabNetPerStepSparsity = null;
        upgraded.TabNetAuditFindings = null;

        TabNetSnapshotSupport.UpgradeSnapshotInPlace(upgraded);
        var upgradedValidation = TabNetSnapshotSupport.ValidateSnapshot(upgraded, allowLegacyV2: false);

        Assert.True(upgradedValidation.IsValid, string.Join("; ", upgradedValidation.Issues));
        Assert.NotNull(upgraded.TabNetOutputHeadWeights);
        Assert.Single(upgraded.TabNetOutputHeadWeights!);
        Assert.Equal(0.75, upgraded.TabNetOutputHeadWeights![0], 12);
        Assert.Equal(0.25, upgraded.TabNetOutputHeadBias, 12);
        Assert.NotNull(upgraded.TabNetPerStepSparsity);
        Assert.Single(upgraded.TabNetPerStepSparsity!);
        Assert.Equal(1.0, upgraded.TabNetPerStepSparsity![0], 12);
        Assert.NotNull(upgraded.TabNetAuditFindings);
        Assert.Empty(upgraded.TabNetAuditFindings!);

        var invalidPoly = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        invalidPoly.TabNetRawFeatureCount = 2;
        invalidPoly.TabNetPolyTopFeatureIndices = [0, 1];
        invalidPoly.FeaturePipelineTransforms = [TabNetSnapshotSupport.PolyInteractionsTransform];

        var invalidValidation = TabNetSnapshotSupport.ValidateSnapshot(invalidPoly, allowLegacyV2: false);

        Assert.False(invalidValidation.IsValid);
        Assert.Contains(
            invalidValidation.Issues,
            issue =>
                issue.Contains("Polynomial pipeline replay metadata", StringComparison.OrdinalIgnoreCase) ||
                issue.Contains("Feature pipeline descriptors do not reconcile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TabNetSnapshotSupport_NormalizeSnapshotCopy_DoesNotMutateSource_AndBackfillsFingerprints()
    {
        var legacy = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        legacy.FeatureSchemaFingerprint = string.Empty;
        legacy.PreprocessingFingerprint = string.Empty;
        legacy.FeaturePipelineDescriptors = [];
        legacy.TabNetPolyTopFeatureIndices = [0, 1];
        legacy.ConformalQHat = 0.2;
        legacy.ConformalQHatBuy = double.NaN;
        legacy.ConformalQHatSell = 0.0;

        var normalized = TabNetSnapshotSupport.NormalizeSnapshotCopy(legacy);

        Assert.NotSame(legacy, normalized);
        Assert.Empty(legacy.FeaturePipelineDescriptors);
        Assert.NotEmpty(normalized.FeaturePipelineDescriptors);
        Assert.True(string.IsNullOrWhiteSpace(legacy.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint));
        Assert.NotEqual(legacy.FeaturePipelineDescriptors.Length, normalized.FeaturePipelineDescriptors.Length);
        Assert.Equal(normalized.ConformalQHat, normalized.ConformalQHatBuy, 12);
        Assert.Equal(normalized.ConformalQHat, normalized.ConformalQHatSell, 12);
    }

    [Fact]
    public void TabNetSnapshotSupport_Rejects_InvalidCrossFitFoldMetadata()
    {
        var snapshot = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        snapshot.TrainingSplitSummary!.AdaptiveHeadSplitMode = "CROSSFIT_SHARED_DIAGNOSTICS";
        snapshot.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount = 2;
        snapshot.TrainingSplitSummary.AdaptiveHeadCrossFitFoldStartIndices = [3];
        snapshot.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCounts = [1];
        snapshot.TrainingSplitSummary.AdaptiveHeadCrossFitFoldHashes = ["fold-0"];
        snapshot.TabNetCalibrationArtifact = new TabNetCalibrationArtifact
        {
            FitSampleCount = snapshot.TrainingSplitSummary.CalibrationFitCount,
            DiagnosticsSampleCount = snapshot.TrainingSplitSummary.CalibrationDiagnosticsCount,
            ConformalSampleCount = snapshot.TrainingSplitSummary.ConformalCount,
            MetaLabelSampleCount = snapshot.TrainingSplitSummary.MetaLabelCount,
            AbstentionSampleCount = snapshot.TrainingSplitSummary.AbstentionCount,
            AdaptiveHeadMode = snapshot.TrainingSplitSummary.AdaptiveHeadSplitMode,
            AdaptiveHeadCrossFitFoldCount = snapshot.TrainingSplitSummary.AdaptiveHeadCrossFitFoldCount,
            ConditionalRoutingThreshold = snapshot.ConditionalCalibrationRoutingThreshold,
        };

        var validation = TabNetSnapshotSupport.ValidateSnapshot(snapshot, allowLegacyV2: false);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("cross-fit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TabNetInferenceEngine_GoldenSparsemaxSnapshot_ProbabilityStable()
    {
        var engine = new TabNetInferenceEngine();
        var snapshot = CreateGoldenTabNetSnapshot();
        var features = new float[] { 1f, 0f };

        var inference = engine.RunInference(
            features, features.Length, snapshot, new List<Candle>(), modelId: 7L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.Equal(0.7310571040514672, inference.Value.Probability, 8);
        Assert.InRange(inference.Value.EnsembleStd, 0.0, 1.0);
    }

    [Fact]
    public void TabNet_InferenceEngine_GoldenSnapshot_StaysWithinRuntimeBudget()
    {
        var engine = new TabNetInferenceEngine();
        var snapshot = CreateGoldenTabNetSnapshot();
        var features = new float[] { 1f, 0f };
        var candles = new List<Candle>();

        for (int i = 0; i < 10; i++)
            engine.RunInference(features, features.Length, snapshot, candles, modelId: 7L, mcDropoutSamples: 0, mcDropoutSeed: 0);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 200; i++)
        {
            var inference = engine.RunInference(
                features, features.Length, snapshot, candles, modelId: 7L, mcDropoutSamples: 0, mcDropoutSeed: 0);
            Assert.NotNull(inference);
        }

        sw.Stop();
        long totalAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        double avgMs = sw.Elapsed.TotalMilliseconds / 200.0;

        Assert.True(avgMs < 2.0, $"Average TabNet inference time {avgMs:F3}ms exceeded the 2ms budget.");
        Assert.True(totalAlloc < 6_500_000, $"TabNet inference allocated {totalAlloc} bytes over 200 runs.");
    }

    private static ModelSnapshot CreateSimpleTabNetSnapshot(bool useInitialProjection, bool useSparsemax)
    {
        return new ModelSnapshot
        {
            Type = "TABNET",
            Version = "3.0",
            Features = ["F0", "F1"],
            Means = [0f, 0f],
            Stds = [1f, 1f],
            TrainingSplitSummary = new TrainingSplitSummary
            {
                RawTrainCount = 2,
                RawSelectionCount = 1,
                RawCalibrationCount = 1,
                RawTestCount = 1,
                TrainStartIndex = 0,
                TrainCount = 2,
                SelectionStartIndex = 2,
                SelectionCount = 1,
                CalibrationStartIndex = 3,
                CalibrationCount = 1,
                CalibrationFitStartIndex = 3,
                CalibrationFitCount = 1,
                CalibrationDiagnosticsStartIndex = 3,
                CalibrationDiagnosticsCount = 1,
                ConformalStartIndex = 3,
                ConformalCount = 1,
                MetaLabelStartIndex = 3,
                MetaLabelCount = 1,
                AbstentionStartIndex = 3,
                AbstentionCount = 1,
                AdaptiveHeadSplitMode = "SHARED_FALLBACK",
                TestStartIndex = 4,
                TestCount = 1,
            },
            OptimalThreshold = 0.5,
            ConditionalCalibrationRoutingThreshold = 0.5,
            TabNetSelectionMetrics = new TabNetMetricSummary { SplitName = "SELECTION", SampleCount = 1, Threshold = 0.5 },
            TabNetCalibrationMetrics = new TabNetMetricSummary { SplitName = "CALIBRATION_DIAGNOSTICS", SampleCount = 1, Threshold = 0.5 },
            TabNetTestMetrics = new TabNetMetricSummary { SplitName = "TEST", SampleCount = 1, Threshold = 0.5 },
            TabNetDriftArtifact = new TabNetDriftArtifact { SampleCount = 2, FeatureCount = 2, GateAction = "PASS" },
            BaseLearnersK = 1,
            TabNetRawFeatureCount = 2,
            TabNetUseSparsemax = useSparsemax,
            TabNetHiddenDim = 1,
            TabNetRelaxationGamma = 1.5,
            TabNetSharedWeights = [new[] { new[] { 1.0, 1.0 } }],
            TabNetSharedBiases = [new[] { 0.0 }],
            TabNetSharedGateWeights = [new[] { new[] { 0.0, 0.0 } }],
            TabNetSharedGateBiases = [new[] { 10.0 }],
            TabNetStepFcWeights = [Array.Empty<double[][]>()],
            TabNetStepFcBiases = [Array.Empty<double[]>()],
            TabNetStepGateWeights = [Array.Empty<double[][]>()],
            TabNetStepGateBiases = [Array.Empty<double[]>()],
            TabNetAttentionFcWeights = [new[] { new[] { 0.0 }, new[] { 0.0 } }],
            TabNetAttentionFcBiases = [new[] { 0.0, 0.0 }],
            TabNetBnGammas = [new[] { 1.0, 1.0 }, new[] { 1.0 }],
            TabNetBnBetas = [new[] { 0.0, 0.0 }, new[] { 0.0 }],
            TabNetBnRunningMeans = [new[] { 0.0, 0.0 }, new[] { 0.0 }],
            TabNetBnRunningVars = [new[] { 1.0, 1.0 }, new[] { 1.0 }],
            TabNetOutputHeadWeights = [1.0],
            TabNetOutputHeadBias = 0.0,
            TabNetInitialBnFcW = useInitialProjection
                ? [new[] { 0.0, 0.0 }, new[] { 1.0, 0.0 }]
                : null,
            TabNetInitialBnFcB = useInitialProjection ? [0.0, 0.0] : null,
        };
    }

    private static ModelSnapshot CreateSimpleFtTransformerSnapshot(int[]? rawFeatureIndices = null, int? rawFeatureCount = null)
    {
        var features = rawFeatureIndices is { Length: > 0 } indices
            ? indices.Select(i => $"F{i}").ToArray()
            : ["F0", "F1"];
        int resolvedRawFeatureCount = rawFeatureCount ?? (rawFeatureIndices is { Length: > 0 } projectedIndices
            ? projectedIndices.Max() + 1
            : features.Length);

        return new ModelSnapshot
        {
            Type = "FTTRANSFORMER",
            Version = "8.0",
            Features = features,
            RawFeatureIndices = rawFeatureIndices ?? [],
            FtTransformerRawFeatureCount = resolvedRawFeatureCount,
            Means = [0f, 0f],
            Stds = [1f, 1f],
            ActiveFeatureMask = [true, true],
            ConditionalCalibrationRoutingThreshold = 0.5,
            FtTransformerEmbedDim = 2,
            FtTransformerNumHeads = 1,
            FtTransformerFfnDim = 2,
            FtTransformerNumLayers = 1,
            FtTransformerEmbedWeights =
            [
                [1.0, 0.5],
                [-0.5, 1.0],
            ],
            FtTransformerEmbedBiases =
            [
                [0.0, 0.0],
                [0.0, 0.0],
            ],
            FtTransformerClsToken = [0.2, -0.1],
            FtTransformerWq =
            [
                [0.0, 0.0],
                [0.0, 0.0],
            ],
            FtTransformerWk =
            [
                [0.0, 0.0],
                [0.0, 0.0],
            ],
            FtTransformerWv =
            [
                [1.0, 0.0],
                [0.0, 1.0],
            ],
            FtTransformerWo =
            [
                [1.0, 0.0],
                [0.0, 1.0],
            ],
            FtTransformerGamma1 = [1.0, 1.0],
            FtTransformerBeta1 = [0.0, 0.0],
            FtTransformerWff1 =
            [
                [0.0, 0.0],
                [0.0, 0.0],
            ],
            FtTransformerBff1 = [0.0, 0.0],
            FtTransformerWff2 =
            [
                [0.0, 0.0],
                [0.0, 0.0],
            ],
            FtTransformerBff2 = [0.0, 0.0],
            FtTransformerGamma2 = [1.0, 1.0],
            FtTransformerBeta2 = [0.0, 0.0],
            FtTransformerGammaFinal = [1.0, 1.0],
            FtTransformerBetaFinal = [0.0, 0.0],
            FtTransformerOutputWeights = [1.0, -0.5],
            FtTransformerOutputBias = 0.1,
        };
    }

    private static ModelSnapshot CreateGoldenTabNetSnapshot()
    {
        return new ModelSnapshot
        {
            Type = "TABNET",
            Version = "3.0",
            Features = ["F0", "F1"],
            Means = [0f, 0f],
            Stds = [1f, 1f],
            TrainingSplitSummary = new TrainingSplitSummary
            {
                RawTrainCount = 2,
                RawSelectionCount = 1,
                RawCalibrationCount = 1,
                RawTestCount = 1,
                TrainStartIndex = 0,
                TrainCount = 2,
                SelectionStartIndex = 2,
                SelectionCount = 1,
                CalibrationStartIndex = 3,
                CalibrationCount = 1,
                CalibrationFitStartIndex = 3,
                CalibrationFitCount = 1,
                CalibrationDiagnosticsStartIndex = 3,
                CalibrationDiagnosticsCount = 1,
                ConformalStartIndex = 3,
                ConformalCount = 1,
                MetaLabelStartIndex = 3,
                MetaLabelCount = 1,
                AbstentionStartIndex = 3,
                AbstentionCount = 1,
                AdaptiveHeadSplitMode = "SHARED_FALLBACK",
                TestStartIndex = 4,
                TestCount = 1,
            },
            OptimalThreshold = 0.5,
            ConditionalCalibrationRoutingThreshold = 0.5,
            TabNetSelectionMetrics = new TabNetMetricSummary { SplitName = "SELECTION", SampleCount = 1, Threshold = 0.5 },
            TabNetCalibrationMetrics = new TabNetMetricSummary { SplitName = "CALIBRATION_DIAGNOSTICS", SampleCount = 1, Threshold = 0.5 },
            TabNetTestMetrics = new TabNetMetricSummary { SplitName = "TEST", SampleCount = 1, Threshold = 0.5 },
            TabNetDriftArtifact = new TabNetDriftArtifact { SampleCount = 2, FeatureCount = 2, GateAction = "PASS" },
            BaseLearnersK = 1,
            TabNetRawFeatureCount = 2,
            TabNetUseSparsemax = true,
            TabNetHiddenDim = 1,
            TabNetRelaxationGamma = 1.5,
            TabNetUseGlu = true,
            TabNetSharedWeights = [new[] { new[] { 1.0, 0.0 } }],
            TabNetSharedBiases = [new[] { 0.0 }],
            TabNetSharedGateWeights = [new[] { new[] { 0.0, 0.0 } }],
            TabNetSharedGateBiases = [new[] { 50.0 }],
            TabNetStepFcWeights = [Array.Empty<double[][]>()],
            TabNetStepFcBiases = [Array.Empty<double[]>()],
            TabNetStepGateWeights = [Array.Empty<double[][]>()],
            TabNetStepGateBiases = [Array.Empty<double[]>()],
            TabNetAttentionFcWeights = [new[] { new[] { 0.0 }, new[] { 0.0 } }],
            TabNetAttentionFcBiases = [new[] { 0.0, 0.0 }],
            TabNetBnGammas = [new[] { 1.0, 1.0 }, new[] { 1.0 }],
            TabNetBnBetas = [new[] { 0.0, 0.0 }, new[] { 0.0 }],
            TabNetBnRunningMeans = [new[] { 0.0, 0.0 }, new[] { 0.0 }],
            TabNetBnRunningVars = [new[] { 1.0, 1.0 }, new[] { 1.0 }],
            TabNetOutputHeadWeights = [1.0],
            TabNetOutputHeadBias = 0.0,
            TabNetAttentionEntropyThreshold = 1.0,
            TabNetUncertaintyThreshold = 1.0,
        };
    }

    // ──────────────────────────────────────────────
    // SvgpModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task Svgp_TrainAsync_ReturnsValidResult()
    {
        var trainer = new SvgpModelTrainer(Mock.Of<ILogger<SvgpModelTrainer>>());
        var samples = GenerateSamples(1000);
        var hp = DefaultHp() with { SvgpInducingM = 20, MaxEpochs = 20, WalkForwardFolds = 2 };

        // SVGP uses TorchSharp for GPU-accelerated variational inference.
        // On some platforms (macOS ARM) the libtorch diagonal op can fail
        // with "diagonal dimensions cannot be identical 0, 0" — a known
        // TorchSharp/libtorch compatibility issue. Accept both a valid result
        // and the platform-specific ExternalException.
        try
        {
            var result = await trainer.TrainAsync(samples, hp);

            Assert.NotNull(result);
            Assert.NotNull(result.ModelBytes);
            Assert.NotEmpty(result.ModelBytes);
            Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
            Assert.True(result.CvResult.FoldCount >= 1);

            // Output structure
            var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
            Assert.NotNull(snap);
            Assert.NotNull(snap.Weights);
            Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
            Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

            // Architecture-specific
            Assert.True(snap.Type.Contains("svgp", StringComparison.OrdinalIgnoreCase));

            // Metric sanity
            Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
            Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
            when (ex.Message.Contains("diagonal dimensions"))
        {
            // Known TorchSharp libtorch issue on this platform — skip gracefully.
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Svgp_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new SvgpModelTrainer(Mock.Of<ILogger<SvgpModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Svgp_TrainAsync_CancellationRespected()
    {
        var trainer = new SvgpModelTrainer(Mock.Of<ILogger<SvgpModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // DannModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Dann_TrainAsync_ReturnsValidResult()
    {
        var trainer = new DannModelTrainer(Mock.Of<ILogger<DannModelTrainer>>());
        var samples = GenerateSamples(200);

        var result = await trainer.TrainAsync(samples, DefaultHp());

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
        Assert.True(result.CvResult.FoldCount >= 1);

        // Output structure
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);
        Assert.NotNull(snap.Weights);
        Assert.True(snap.Weights.Length > 0, "Model should have at least one learner");
        Assert.True(snap.Biases.Length > 0, "Model should have bias terms");

        // Architecture-specific
        Assert.True(snap.Type.Contains("DANN", StringComparison.OrdinalIgnoreCase));

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
    }

    [Fact(Timeout = 30000)]
    public async Task Dann_TrainAsync_EmptySamples_Throws()
    {
        var trainer = new DannModelTrainer(Mock.Of<ILogger<DannModelTrainer>>());
        var samples = new List<TrainingSample>();

        await Assert.ThrowsAnyAsync<Exception>(() => trainer.TrainAsync(samples, DefaultHp()));
    }

    [Fact(Timeout = 30000)]
    public async Task Dann_TrainAsync_CancellationRespected()
    {
        var trainer = new DannModelTrainer(Mock.Of<ILogger<DannModelTrainer>>());
        var samples = GenerateSamples(200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trainer.TrainAsync(samples, DefaultHp(), ct: cts.Token));
    }

    // ──────────────────────────────────────────────
    // TabNet pruning dimensionality reduction
    // ──────────────────────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task TabNet_Pruning_MaintainsFixedWidthDeploymentContract()
    {
        var trainer = new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>());
        var samples = GenerateSamples(260, featureCount: 12);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            MinFeatureImportance = 4.0,
        };

        var result = await trainer.TrainAsync(samples, hp);
        var snap = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snap);

        Assert.NotNull(snap.TabNetPruningDecision);
        Assert.True(snap.TabNetPruningDecision!.PrunedFeatureCount > 0);
        Assert.Equal(snap.Features.Length, snap.ActiveFeatureMask.Length);
        Assert.Contains(snap.ActiveFeatureMask, v => v);
        Assert.Equal(snap.PrunedFeatureCount, snap.ActiveFeatureMask.Count(v => !v));

        var validation = TabNetSnapshotSupport.ValidateSnapshot(snap, allowLegacyV2: false);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));

        if (snap.TabNetAttentionFcWeights is { Length: > 0 })
            Assert.Equal(snap.Features.Length, snap.TabNetAttentionFcWeights[0].Length);
        if (snap.TabNetBnGammas is { Length: > 0 })
            Assert.Equal(snap.Features.Length, snap.TabNetBnGammas[0].Length);

        bool sawNonZeroEffectiveInput = false;
        var engine = new TabNetInferenceEngine();
        for (int i = 0; i < Math.Min(5, samples.Count); i++)
        {
            int featureCount = snap.Features.Length;
            float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
                samples[i].Features, snap.Means, snap.Stds, featureCount);
            InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);
            MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snap.ActiveFeatureMask, featureCount);

            if (inferenceFeatures.Any(v => Math.Abs(v) > 1e-6f))
                sawNonZeroEffectiveInput = true;

            var inference = engine.RunInference(
                inferenceFeatures, featureCount, snap, new List<Candle>(), modelId: 99L, mcDropoutSamples: 0, mcDropoutSeed: 0);
            Assert.NotNull(inference);
            Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        }

        Assert.True(sawNonZeroEffectiveInput, "Deployed masking should not collapse every audited inference vector to all-zero inputs.");
    }

    // ─── Helper: create a trainer by string identifier ───────────────────────

    private static IMLModelTrainer CreateTrainer(string trainerId) => trainerId switch
    {
        "BaggedLogistic" => new BaggedLogisticTrainer(Mock.Of<ILogger<BaggedLogisticTrainer>>()),
        "Gbm"           => new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>()),
        "Elm"           => new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>()),
        "AdaBoost"      => new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>()),
        "Dann"          => new DannModelTrainer(Mock.Of<ILogger<DannModelTrainer>>()),
        "TabNet"        => new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>()),
        "Tcn"           => new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>()),
        "FtTransformer" => new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>()),
        "Rocket"        => new RocketModelTrainer(Mock.Of<ILogger<RocketModelTrainer>>()),
        "Smote"         => new SmoteModelTrainer(Mock.Of<ILogger<SmoteModelTrainer>>()),
        "QuantileRf"    => new QuantileRfModelTrainer(Mock.Of<ILogger<QuantileRfModelTrainer>>()),
        "Svgp"          => new SvgpModelTrainer(Mock.Of<ILogger<SvgpModelTrainer>>()),
        _ => throw new ArgumentException($"Unknown trainer: {trainerId}")
    };

    // ─── Property-based edge-case tests ──────────────────────────────────────

    [Theory]
    [InlineData("BaggedLogistic")]
    [InlineData("Gbm")]
    [InlineData("Elm")]
    [InlineData("TabNet")]
    [InlineData("AdaBoost")]
    public async Task Trainer_ExtremeEmbargo_ThrowsOrDegrades(string trainerId)
    {
        // Embargo = 30% of samples should collapse the 4-way split
        var trainer = CreateTrainer(trainerId);
        var samples = GenerateSamples(100);
        var hp = DefaultHp() with { EmbargoBarCount = 30 };

        // Should either throw (insufficient samples) or return degraded metrics
        try
        {
            var result = await trainer.TrainAsync(samples, hp);
            // If it doesn't throw, at least verify it produced something
            Assert.NotNull(result);
        }
        catch (InvalidOperationException)
        {
            // Expected — embargo too large for sample count
        }
    }

    [Theory]
    [InlineData("AdaBoost")]
    [InlineData("Gbm")]
    [InlineData("Elm")]
    [InlineData("Dann")]
    [InlineData("TabNet")]
    [InlineData("FtTransformer")]
    [InlineData("Rocket")]
    [InlineData("Smote")]
    [InlineData("Svgp")]
    public async Task Trainer_ExtremeClassImbalance_Rejects(string trainerId)
    {
        var trainer = CreateTrainer(trainerId);
        // All samples have Direction > 0 (100% Buy)
        var samples = GenerateSamples(200);
        for (int i = 0; i < samples.Count; i++)
            samples[i] = samples[i] with { Direction = 1 };
        var hp = DefaultHp();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => trainer.TrainAsync(samples, hp));
    }

    [Theory]
    [InlineData("AdaBoost")]
    [InlineData("Gbm")]
    [InlineData("Elm")]
    public async Task Trainer_NaNFeatures_HandledGracefully(string trainerId)
    {
        var trainer = CreateTrainer(trainerId);
        var samples = GenerateSamples(200);
        // Inject NaN into first sample's first feature
        samples[0] = samples[0] with { Features = samples[0].Features.ToArray() };
        samples[0].Features[0] = float.NaN;
        var hp = DefaultHp();

        // Should either throw validation error or handle gracefully (not produce NaN metrics)
        try
        {
            var result = await trainer.TrainAsync(samples, hp);
            Assert.True(double.IsFinite(result.FinalMetrics.Accuracy),
                "NaN feature leaked into final metrics");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Expected — validation caught the NaN
        }
    }

    [Theory]
    [InlineData("AdaBoost")]
    [InlineData("Gbm")]
    [InlineData("Elm")]
    public async Task Trainer_MismatchedWarmStart_FallsBackToColdStart(string trainerId)
    {
        var trainer = CreateTrainer(trainerId);
        var samples = GenerateSamples(300);
        var hp = DefaultHp();

        // Create a fake warm-start snapshot with wrong feature count
        var fakeWarmStart = new ModelSnapshot
        {
            Type = trainerId switch
            {
                "AdaBoost" => "AdaBoost",
                "Gbm" => "GBM",
                "Elm" => "elm",
                _ => trainerId
            },
            Features = new string[5], // Wrong feature count (should be 33)
            Means = new float[5],
            Stds = new float[5],
        };

        // Should not throw — should fall back to cold start
        var result = await trainer.TrainAsync(samples, hp, fakeWarmStart);
        Assert.NotNull(result);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);
    }

    [Theory]
    [InlineData("AdaBoost")]
    [InlineData("Gbm")]
    public async Task Trainer_HighlyNonStationaryData_DriftGateRejects(string trainerId)
    {
        var trainer = CreateTrainer(trainerId);
        // Create strongly trended (non-stationary) features
        var samples = new List<TrainingSample>(300);
        var rng = new Random(42);
        for (int i = 0; i < 300; i++)
        {
            var features = new float[33];
            for (int j = 0; j < 33; j++)
                features[j] = (float)(i * 0.1 + rng.NextDouble() * 0.01); // strong upward trend
            samples.Add(new TrainingSample(
                Features: features,
                Direction: rng.Next(2) == 0 ? -1 : 1,
                Magnitude: (float)(rng.NextDouble() * 0.5)));
        }
        var hp = DefaultHp();

        // Should throw due to drift gate REJECT (>50% features non-stationary)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => trainer.TrainAsync(samples, hp));
    }
}
