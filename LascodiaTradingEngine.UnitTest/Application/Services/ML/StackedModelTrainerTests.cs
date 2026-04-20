using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using Microsoft.Extensions.Logging.Abstractions;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class StackedModelTrainerTests
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

    /// <summary>
    /// Generate a labelled dataset where the label is a simple linear function of the last
    /// feature (so the learner clearly has signal). 200 samples × 33 features (V1 schema).
    /// </summary>
    private static List<TrainingSample> MakeLinearlySeparableV1(int count = 200)
    {
        var list = new List<TrainingSample>(count);
        var rng  = new Random(42);
        for (int i = 0; i < count; i++)
        {
            var feats = new float[33];
            for (int j = 0; j < 33; j++)
                feats[j] = (float)(rng.NextDouble() * 2 - 1);

            // Strong signal on feature 0.
            feats[0] = (float)((rng.NextDouble() * 2 - 1) + (i % 2 == 0 ? 1.5 : -1.5));
            int dir = feats[0] > 0 ? 1 : -1;

            list.Add(new TrainingSample(feats, dir, Magnitude: (float)Math.Abs(feats[0]) * 0.01f));
        }
        return list;
    }

    [Fact]
    public async Task Train_ProducesSnapshotWithStackedMetaJson()
    {
        var trainer = new StackedModelTrainer(NullLogger<StackedModelTrainer>.Instance);
        var samples = MakeLinearlySeparableV1(count: 200);

        var hp = DefaultHp();

        var result = await trainer.TrainAsync(samples, hp, warmStart: null, parentModelId: null, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.ModelBytes.Length > 0);

        var snapshotJson = Encoding.UTF8.GetString(result.ModelBytes);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(snapshotJson);
        Assert.NotNull(snapshot);
        Assert.Equal(StackedSnapshotSupport.ModelType, snapshot!.Type);
        Assert.Equal(33, snapshot.ExpectedInputFeatures);
        Assert.Equal(1,  snapshot.FeatureSchemaVersion);
        Assert.NotNull(snapshot.StackedMetaJson);

        var artifact = StackedSnapshotSupport.TryDeserialize(snapshot.StackedMetaJson);
        Assert.NotNull(artifact);
        Assert.NotEmpty(artifact!.SubModels);
        Assert.Equal(artifact.SubModels.Length, artifact.MetaWeights.Length);
    }

    [Fact]
    public async Task Train_AccuracyBeatsChance()
    {
        var trainer = new StackedModelTrainer(NullLogger<StackedModelTrainer>.Instance);
        var samples = MakeLinearlySeparableV1(count: 300);
        var hp = DefaultHp();

        var result = await trainer.TrainAsync(samples, hp, warmStart: null, parentModelId: null, ct: CancellationToken.None);

        // With a strong linear signal, a logistic meta-learner should comfortably beat
        // chance on the held-out slice. 55% is a loose floor — production models aim higher.
        Assert.True(result.FinalMetrics.Accuracy > 0.55,
            $"Expected accuracy > 0.55, got {result.FinalMetrics.Accuracy:P2}");
    }

    [Fact]
    public async Task InferenceEngine_CanHandleTrainedSnapshot()
    {
        var trainer = new StackedModelTrainer(NullLogger<StackedModelTrainer>.Instance);
        var samples = MakeLinearlySeparableV1(count: 200);
        var hp = DefaultHp();
        var result = await trainer.TrainAsync(samples, hp, warmStart: null, parentModelId: null, ct: CancellationToken.None);
        var snapshotJson = Encoding.UTF8.GetString(result.ModelBytes);
        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(snapshotJson)!;

        var engine = new StackedInferenceEngine();
        Assert.True(engine.CanHandle(snapshot));

        // Standardise the first sample using the snapshot's persisted means/stds so the
        // inference engine receives the same shape it would at runtime.
        var raw = samples[0].Features;
        var standardised = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            float mean = snapshot.Means[i];
            float std  = snapshot.Stds[i] < 1e-8f ? 1f : snapshot.Stds[i];
            standardised[i] = (raw[i] - mean) / std;
        }

        var inference = engine.RunInference(
            features: standardised,
            featureCount: standardised.Length,
            snapshot: snapshot,
            candleWindow: [],
            modelId: 1L,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.InRange(inference!.Value.Probability, 0.0, 1.0);
    }

    [Fact]
    public void FeatureFamilies_V5AndV6ActiveSetsMatchSchema()
    {
        // V5 (52) covers through SynthDom; RealDom requires V6 (57).
        var v5 = StackedFeatureFamilies.ActiveFor(52);
        Assert.Contains(v5, f => f.Name == "SynthDom");
        Assert.DoesNotContain(v5, f => f.Name == "RealDom");

        var v6 = StackedFeatureFamilies.ActiveFor(57);
        Assert.Contains(v6, f => f.Name == "SynthDom");
        Assert.Contains(v6, f => f.Name == "RealDom");

        // V1 only has OHLCV.
        var v1 = StackedFeatureFamilies.ActiveFor(33);
        Assert.Single(v1);
        Assert.Equal("Ohlcv", v1[0].Name);
    }
}
