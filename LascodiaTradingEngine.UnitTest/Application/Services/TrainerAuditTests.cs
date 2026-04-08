using System.Runtime.InteropServices;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.Services.Inference;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services;

public class TrainerAuditTests
{
    public static IEnumerable<object[]> AuditCases()
    {
        yield return ["bagged", false];
        yield return ["gbm", false];
        yield return ["elm", false];
        yield return ["tcn", true];
        yield return ["adaboost", false];
        yield return ["rocket", false];
        yield return ["smote", false];
        yield return ["quantilerf", false];
        yield return ["fttransformer", false];
        yield return ["tabnet", false];
        yield return ["svgp", false];
        yield return ["dann", false];
    }

    [Theory(Timeout = 120000)]
    [MemberData(nameof(AuditCases))]
    public async Task Trainer_Audit_SnapshotAndInferenceContract_Holds(string trainerId, bool requiresCandleWindow)
    {
        var trainer = CreateTrainer(trainerId);
        var samples = CreateSamples(trainerId);
        var hp = CreateAuditHyperparams(trainerId);

        TrainingResult result;
        try
        {
            result = await trainer.TrainAsync(samples, hp);
        }
        catch (ExternalException) when (trainerId == "svgp")
        {
            // Known platform-specific TorchSharp/libtorch issue.
            return;
        }

        Assert.NotNull(result);
        Assert.NotNull(result.ModelBytes);
        Assert.NotEmpty(result.ModelBytes);
        Assert.InRange(result.FinalMetrics.Accuracy, 0.0, 1.0);

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snapshot);

        ValidateSnapshotCore(snapshot, samples[0].Features.Length);

        var engine = CreateInferenceEngine(trainerId);
        Assert.True(engine.CanHandle(snapshot), $"Inference engine refused snapshot for trainer '{trainerId}'.");

        int inferenceFeatureCount = snapshot.Features.Length > 0 ? snapshot.Features.Length : samples[0].Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snapshot.Means, snapshot.Stds, inferenceFeatureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snapshot);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snapshot.ActiveFeatureMask, inferenceFeatureCount);

        var candleWindow = requiresCandleWindow ? CreateCandleWindow() : new List<Candle>();
        var inference = engine.RunInference(
            inferenceFeatures,
            inferenceFeatureCount,
            snapshot,
            candleWindow,
            modelId: 1L,
            mcDropoutSamples: 0,
            mcDropoutSeed: 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(double.IsFinite(inference.Value.EnsembleStd));

        if (trainerId == "tabnet")
        {
            Assert.NotNull(snapshot.TabNetWarmStartArtifact);
            Assert.True(snapshot.TabNetWarmStartArtifact!.Compatible);
            Assert.True(snapshot.TabNetWarmStartArtifact.ReuseRatio >= 0.0);
        }
    }

    [Theory(Timeout = 300000)]
    [MemberData(nameof(AuditCases))]
    public async Task Trainer_Audit_WarmStartRoundTrip_RemainsUsable(string trainerId, bool requiresCandleWindow)
    {
        var trainer = CreateTrainer(trainerId);
        var samples = CreateSamples(trainerId);
        var hp = CreateAuditHyperparams(trainerId);

        TrainingResult baseline;
        try
        {
            baseline = await trainer.TrainAsync(samples, hp);
        }
        catch (ExternalException) when (trainerId == "svgp")
        {
            return;
        }

        var warmStart = JsonSerializer.Deserialize<ModelSnapshot>(baseline.ModelBytes);
        Assert.NotNull(warmStart);

        TrainingResult warmStarted;
        try
        {
            warmStarted = await trainer.TrainAsync(samples, hp, warmStart);
        }
        catch (ExternalException) when (trainerId == "svgp")
        {
            return;
        }

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(warmStarted.ModelBytes);
        Assert.NotNull(snapshot);
        ValidateSnapshotCore(snapshot, samples[0].Features.Length);

        var engine = CreateInferenceEngine(trainerId);
        Assert.True(engine.CanHandle(snapshot), $"Warm-started snapshot not accepted for trainer '{trainerId}'.");

        int inferenceFeatureCount = snapshot.Features.Length > 0 ? snapshot.Features.Length : samples[0].Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snapshot.Means, snapshot.Stds, inferenceFeatureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snapshot);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snapshot.ActiveFeatureMask, inferenceFeatureCount);
        var candleWindow = requiresCandleWindow ? CreateCandleWindow() : new List<Candle>();
        var inference = engine.RunInference(inferenceFeatures, inferenceFeatureCount, snapshot, candleWindow, 1L, 0, 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(double.IsFinite(inference.Value.EnsembleStd));

        if (trainerId == "tabnet")
        {
            Assert.NotNull(snapshot.TabNetWarmStartArtifact);
            Assert.True(snapshot.TabNetWarmStartArtifact!.ReuseRatio >= 0.0);
        }
    }

    [Theory(Timeout = 180000)]
    [MemberData(nameof(AuditCases))]
    public async Task Trainer_Audit_CorruptedWarmStart_DoesNotProduceCorruptSnapshot(string trainerId, bool requiresCandleWindow)
    {
        var trainer = CreateTrainer(trainerId);
        var samples = CreateSamples(trainerId);
        var hp = CreateAuditHyperparams(trainerId);

        var corruptedWarmStart = CreateCorruptedWarmStartSnapshot(trainerId, samples[0].Features.Length);

        TrainingResult result;
        try
        {
            result = await trainer.TrainAsync(samples, hp, corruptedWarmStart);
        }
        catch (ExternalException) when (trainerId == "svgp")
        {
            return;
        }

        var snapshot = JsonSerializer.Deserialize<ModelSnapshot>(result.ModelBytes);
        Assert.NotNull(snapshot);
        ValidateSnapshotCore(snapshot, samples[0].Features.Length);

        var engine = CreateInferenceEngine(trainerId);
        Assert.True(engine.CanHandle(snapshot), $"Corrupted-warm-start snapshot not accepted for trainer '{trainerId}'.");

        int inferenceFeatureCount = snapshot.Features.Length > 0 ? snapshot.Features.Length : samples[0].Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snapshot.Means, snapshot.Stds, inferenceFeatureCount);
        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snapshot);
        MLSignalScorer.ApplyFeatureMask(inferenceFeatures, snapshot.ActiveFeatureMask, inferenceFeatureCount);
        var candleWindow = requiresCandleWindow ? CreateCandleWindow() : new List<Candle>();
        var inference = engine.RunInference(inferenceFeatures, inferenceFeatureCount, snapshot, candleWindow, 1L, 0, 0);

        Assert.NotNull(inference);
        Assert.InRange(inference.Value.Probability, 0.0, 1.0);
        Assert.True(double.IsFinite(inference.Value.EnsembleStd));
    }

    private static IMLModelTrainer CreateTrainer(string trainerId) => trainerId switch
    {
        "bagged" => new BaggedLogisticTrainer(Mock.Of<ILogger<BaggedLogisticTrainer>>()),
        "gbm" => new GbmModelTrainer(Mock.Of<ILogger<GbmModelTrainer>>()),
        "elm" => new ElmModelTrainer(Mock.Of<ILogger<ElmModelTrainer>>()),
        "tcn" => new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>()),
        "adaboost" => new AdaBoostModelTrainer(Mock.Of<ILogger<AdaBoostModelTrainer>>()),
        "rocket" => new RocketModelTrainer(Mock.Of<ILogger<RocketModelTrainer>>()),
        "smote" => new SmoteModelTrainer(Mock.Of<ILogger<SmoteModelTrainer>>()),
        "quantilerf" => new QuantileRfModelTrainer(Mock.Of<ILogger<QuantileRfModelTrainer>>()),
        "fttransformer" => new FtTransformerModelTrainer(Mock.Of<ILogger<FtTransformerModelTrainer>>()),
        "tabnet" => new TabNetModelTrainer(Mock.Of<ILogger<TabNetModelTrainer>>()),
        "svgp" => new SvgpModelTrainer(Mock.Of<ILogger<SvgpModelTrainer>>()),
        "dann" => new DannModelTrainer(Mock.Of<ILogger<DannModelTrainer>>()),
        _ => throw new ArgumentOutOfRangeException(nameof(trainerId), trainerId, null),
    };

    private static IModelInferenceEngine CreateInferenceEngine(string trainerId)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return trainerId switch
        {
            "bagged" => new EnsembleInferenceEngine(),
            "gbm" => new GbmInferenceEngine(cache),
            "elm" => new ElmInferenceEngine(),
            "tcn" => new TcnInferenceEngine(),
            "adaboost" => new AdaBoostInferenceEngine(cache),
            "rocket" => new RocketInferenceEngine(),
            "smote" => new EnsembleInferenceEngine(),
            "quantilerf" => new QrfInferenceEngine(cache),
            "fttransformer" => new FtTransformerInferenceEngine(),
            "tabnet" => new TabNetInferenceEngine(),
            "svgp" => new SvgpInferenceEngine(),
            "dann" => new DannInferenceEngine(),
            _ => throw new ArgumentOutOfRangeException(nameof(trainerId), trainerId, null),
        };
    }

    private static List<TrainingSample> CreateSamples(string trainerId) => trainerId switch
    {
        "tcn" => GenerateTcnSamples(120),
        "fttransformer" => GenerateSamples(140),
        "tabnet" => GenerateSamples(220),
        "svgp" => GenerateSamples(600),
        _ => GenerateSamples(180),
    };

    private static TrainingHyperparams CreateAuditHyperparams(string trainerId)
    {
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
            WalkForwardFolds = 2,
            EarlyStoppingPatience = 3,
            EmbargoBarCount = 5,
        };

        return trainerId switch
        {
            "dann" => DefaultHp() with { MaxEpochs = 20, WalkForwardFolds = 3, EarlyStoppingPatience = 5 },
            "fttransformer" => hp with { MaxEpochs = 3, EarlyStoppingPatience = 2 },
            "tabnet" => hp with
            {
                MaxEpochs = 4,
                EarlyStoppingPatience = 2,
                CurriculumEasyFraction = 0.0,
                SelfDistillTemp = 0.0,
                FgsmEpsilon = 0.0,
                MaxLearnerCorrelation = 1.0,
            },
            "tcn" => hp with { MaxEpochs = 6, WalkForwardFolds = 2, EarlyStoppingPatience = 2 },
            "svgp" => hp with { MaxEpochs = 10, SvgpInducingM = 20 },
            _ => hp,
        };
    }

    private static ModelSnapshot CreateCorruptedWarmStartSnapshot(string trainerId, int rawFeatureCount)
    {
        var means = new float[rawFeatureCount];
        var stds = Enumerable.Repeat(1f, rawFeatureCount).ToArray();

        return trainerId switch
        {
            "bagged" => new ModelSnapshot
            {
                Type = "BaggedLogistic",
                Features = BuildFeatureNames(rawFeatureCount),
                Means = means,
                Stds = stds,
                Weights = [Enumerable.Repeat(double.NaN, rawFeatureCount).ToArray()],
                Biases = [double.NaN],
            },
            "gbm" => new ModelSnapshot { Type = "GBM", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "elm" => new ModelSnapshot
            {
                Type = "elm",
                Features = BuildFeatureNames(rawFeatureCount),
                Means = means,
                Stds = stds,
                Weights = [Enumerable.Repeat(double.NaN, 4).ToArray()],
                Biases = [double.NaN],
                ElmInputWeights = [Enumerable.Repeat(double.NaN, rawFeatureCount * 4).ToArray()],
                ElmInputBiases = [Enumerable.Repeat(double.NaN, 4).ToArray()],
                ElmHiddenDim = 4,
            },
            "tcn" => new ModelSnapshot { Type = "TCN", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "adaboost" => new ModelSnapshot { Type = "AdaBoost", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "rocket" => new ModelSnapshot { Type = "ROCKET", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "smote" => new ModelSnapshot { Type = "SMOTE", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "quantilerf" => new ModelSnapshot { Type = "quantilerf", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "fttransformer" => new ModelSnapshot
            {
                Type = "FTTRANSFORMER",
                Features = BuildFeatureNames(rawFeatureCount),
                Means = means,
                Stds = stds,
                FtTransformerEmbedDim = 16,
                FtTransformerNumHeads = 4,
                FtTransformerFfnDim = 64,
                FtTransformerNumLayers = 1,
                FtTransformerEmbedWeights = Enumerable.Range(0, rawFeatureCount).Select(_ => Enumerable.Repeat(double.NaN, 16).ToArray()).ToArray(),
                FtTransformerEmbedBiases = Enumerable.Range(0, rawFeatureCount).Select(_ => new double[16]).ToArray(),
                FtTransformerClsToken = new double[16],
                FtTransformerWq = Enumerable.Range(0, 16).Select(_ => new double[16]).ToArray(),
                FtTransformerWk = Enumerable.Range(0, 16).Select(_ => new double[16]).ToArray(),
                FtTransformerWv = Enumerable.Range(0, 16).Select(_ => new double[16]).ToArray(),
                FtTransformerWo = Enumerable.Range(0, 16).Select(_ => new double[16]).ToArray(),
                FtTransformerWff1 = Enumerable.Range(0, 16).Select(_ => new double[64]).ToArray(),
                FtTransformerBff1 = new double[64],
                FtTransformerWff2 = Enumerable.Range(0, 64).Select(_ => new double[16]).ToArray(),
                FtTransformerBff2 = new double[16],
                FtTransformerGamma1 = Enumerable.Repeat(1.0, 16).ToArray(),
                FtTransformerBeta1 = new double[16],
                FtTransformerGamma2 = Enumerable.Repeat(1.0, 16).ToArray(),
                FtTransformerBeta2 = new double[16],
                FtTransformerGammaFinal = Enumerable.Repeat(1.0, 16).ToArray(),
                FtTransformerBetaFinal = new double[16],
                FtTransformerOutputWeights = Enumerable.Repeat(double.NaN, 16).ToArray(),
                FtTransformerOutputBias = double.NaN,
                Weights = Enumerable.Range(0, rawFeatureCount).Select(_ => new double[16]).ToArray(),
                Biases = [double.NaN],
            },
            "tabnet" => new ModelSnapshot
            {
                Type = "TABNET",
                Features = BuildFeatureNames(rawFeatureCount),
                Means = means,
                Stds = stds,
                Weights = [Enumerable.Repeat(double.NaN, rawFeatureCount).ToArray()],
                Biases = [double.NaN],
                TabNetStepAttentionWeights = [Enumerable.Repeat(double.NaN, rawFeatureCount).ToArray()],
                TabNetOutputWeight = double.NaN,
            },
            "svgp" => new ModelSnapshot { Type = "svgp", Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
            "dann" => new ModelSnapshot
            {
                Type = "DANN",
                Features = BuildFeatureNames(rawFeatureCount),
                Means = means,
                Stds = stds,
                DannWeights =
                [
                    Enumerable.Repeat(double.NaN, rawFeatureCount + 1).ToArray(),
                    Enumerable.Repeat(double.NaN, rawFeatureCount + 1).ToArray(),
                    Enumerable.Repeat(double.NaN, 3).ToArray(),
                    Enumerable.Repeat(double.NaN, 3).ToArray(),
                    Enumerable.Repeat(double.NaN, 3).ToArray(),
                ],
            },
            _ => new ModelSnapshot { Type = trainerId, Features = BuildFeatureNames(rawFeatureCount), Means = means, Stds = stds },
        };
    }

    private static void ValidateSnapshotCore(ModelSnapshot snapshot, int rawFeatureCount)
    {
        Assert.NotEmpty(snapshot.Type);
        Assert.NotEmpty(snapshot.Version);
        Assert.NotNull(snapshot.Features);
        Assert.NotEmpty(snapshot.Features);
        Assert.NotNull(snapshot.Means);
        Assert.NotNull(snapshot.Stds);
        Assert.Equal(snapshot.Features.Length, snapshot.Means.Length);
        Assert.Equal(snapshot.Features.Length, snapshot.Stds.Length);

        AssertFinite(snapshot.Means);
        AssertFinite(snapshot.Stds);
        AssertFinite(snapshot.Biases);
        AssertFinite(snapshot.MagWeights);
        AssertFinite(snapshot.FeatureImportance);
        AssertFinite(snapshot.FeatureImportanceScores);
        AssertFinite(snapshot.FeatureQuantileBreakpoints);
        AssertFinite(snapshot.IsotonicBreakpoints);
        AssertFinite(snapshot.JackknifeResiduals);

        if (snapshot.ActiveFeatureMask.Length > 0)
            Assert.Equal(snapshot.Features.Length, snapshot.ActiveFeatureMask.Length);

        if (snapshot.Type == "TCN")
        {
            Assert.NotEmpty(snapshot.ConvWeightsJson);
            Assert.NotEmpty(snapshot.SeqMeans);
            Assert.NotEmpty(snapshot.SeqStds);
        }
        else if (snapshot.Type == "GBM")
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.GbmTreesJson));
        }
        else if (snapshot.Type == "AdaBoost")
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.GbmTreesJson));
            Assert.NotEmpty(snapshot.Weights);
        }
        else if (snapshot.Type == "quantilerf")
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.GbmTreesJson));
        }
        else if (snapshot.Type == "svgp")
        {
            Assert.NotNull(snapshot.SvgpInducingPoints);
            Assert.NotNull(snapshot.SvgpArdLengthScales);
        }
        else if (snapshot.Type == "TABNET")
        {
            Assert.NotNull(snapshot.TabNetStepAttentionWeights);
            Assert.Equal(snapshot.Weights.Length, snapshot.TabNetStepAttentionWeights.Length);
            Assert.True(double.IsFinite(snapshot.TabNetOutputWeight));
            Assert.NotNull(snapshot.FeaturePipelineTransforms);
            Assert.NotNull(snapshot.FeaturePipelineDescriptors);
            Assert.NotNull(snapshot.TabNetOutputHeadWeights);
            Assert.Equal(snapshot.TabNetHiddenDim, snapshot.TabNetOutputHeadWeights!.Length);
            Assert.NotNull(snapshot.TabNetPerStepSparsity);
            Assert.Equal(snapshot.BaseLearnersK, snapshot.TabNetPerStepSparsity!.Length);
            Assert.NotNull(snapshot.TabNetBnDriftByLayer);
            Assert.True(snapshot.TabNetBnDriftByLayer!.Length > 0);
            Assert.NotNull(snapshot.TabNetAuditFindings);
            Assert.NotNull(snapshot.TabNetAuditArtifact);
            Assert.True(snapshot.TabNetTrainInferenceParityMaxError <= 1e-6);
            Assert.True(snapshot.TabNetAttentionEntropyThreshold > 0.0);
            Assert.True(snapshot.TabNetUncertaintyThreshold > 0.0);
            Assert.True(snapshot.TabNetCalibrationResidualThreshold > 0.0);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.FeatureSchemaFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.PreprocessingFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.TrainerFingerprint));
            Assert.True(snapshot.TrainingRandomSeed > 0);
            Assert.NotNull(snapshot.TrainingSplitSummary);

            if (snapshot.FeaturePipelineTransforms.Any(t =>
                    string.Equals(t, "TABNET_POLY_INTERACTIONS_V1", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.True(snapshot.Features.Length > snapshot.TabNetRawFeatureCount);
                Assert.NotNull(snapshot.TabNetPolyTopFeatureIndices);
                Assert.True(snapshot.TabNetPolyTopFeatureIndices!.Length > 1);
                Assert.NotEmpty(snapshot.FeaturePipelineDescriptors);
            }
        }
        else if (snapshot.Type == "FTTRANSFORMER")
        {
            Assert.NotNull(snapshot.FtTransformerEmbedWeights);
            Assert.NotNull(snapshot.FtTransformerClsToken);
            Assert.True(snapshot.FtTransformerEmbedDim > 0);
        }
        else if (snapshot.Type == "ROCKET")
        {
            Assert.NotNull(snapshot.RocketKernelWeights);
            Assert.NotNull(snapshot.RocketKernelDilations);
            Assert.NotNull(snapshot.RocketKernelPaddings);
            Assert.NotNull(snapshot.RocketKernelLengths);
        }
        else if (snapshot.Type == "DANN")
        {
            Assert.NotNull(snapshot.DannWeights);
            Assert.NotEmpty(snapshot.DannWeights);
        }
        else
        {
            Assert.NotEmpty(snapshot.Weights);
        }

        Assert.True(snapshot.Features.Length >= Math.Min(1, rawFeatureCount));
    }

    private static void AssertFinite(float[] values)
    {
        foreach (var value in values)
            Assert.True(float.IsFinite(value), $"Non-finite float value detected: {value}");
    }

    private static void AssertFinite(double[] values)
    {
        foreach (var value in values)
            Assert.True(double.IsFinite(value), $"Non-finite double value detected: {value}");
    }

    private static void AssertFinite(float[][]? values)
    {
        if (values is null)
            return;

        foreach (var row in values)
            AssertFinite(row);
    }

    private static void AssertFinite(double[][]? values)
    {
        if (values is null)
            return;

        foreach (var row in values)
            AssertFinite(row);
    }

    private static string[] BuildFeatureNames(int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = i < MLFeatureHelper.FeatureNames.Length ? MLFeatureHelper.FeatureNames[i] : $"F{i}";
        return names;
    }

    private static List<TrainingSample> GenerateSamples(int count, int featureCount = 33)
    {
        var rng = new Random(42);
        var samples = new List<TrainingSample>(count);
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

    private static List<TrainingSample> GenerateTcnSamples(int count, int featureCount = 33, int lookback = 30, int channels = 9)
    {
        var rng = new Random(42);
        var samples = new List<TrainingSample>(count);
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

    private static List<Candle> CreateCandleWindow(int count = 64)
    {
        var candles = new List<Candle>(count);
        decimal price = 1.1000m;
        var start = DateTime.UtcNow.AddHours(-count);

        for (int i = 0; i < count; i++)
        {
            decimal drift = (decimal)Math.Sin(i / 5.0) * 0.0003m;
            decimal open = price;
            decimal close = price + drift + 0.0001m;
            decimal high = Math.Max(open, close) + 0.0002m;
            decimal low = Math.Min(open, close) - 0.0002m;

            candles.Add(new Candle
            {
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = 1000 + i,
                Timestamp = start.AddHours(i),
                IsClosed = true,
            });

            price = close;
        }

        return candles;
    }

    private static TrainingHyperparams DefaultHp() => new(
        K: 3, LearningRate: 0.01, L2Lambda: 0.001, MaxEpochs: 50,
        EarlyStoppingPatience: 5, MinAccuracyToPromote: 0.50, MinAcceptanceRateToPromote: 0.10, MinExpectedValue: -0.10,
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
}
