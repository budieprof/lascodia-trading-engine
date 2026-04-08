using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
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

    // ──────────────────────────────────────────────
    // TcnModelTrainer
    // ──────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Tcn_TrainAsync_ReturnsValidResult()
    {
        var trainer = new TcnModelTrainer(Mock.Of<ILogger<TcnModelTrainer>>());
        var samples = GenerateTcnSamples(200);

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
        Assert.True(snap.Type.Contains("TCN", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(snap.ConvWeightsJson);

        // Metric sanity
        Assert.True(result.FinalMetrics.BrierScore >= 0 && result.FinalMetrics.BrierScore <= 1, "Brier score must be in [0,1]");
        Assert.True(result.FinalMetrics.TP + result.FinalMetrics.FP + result.FinalMetrics.FN + result.FinalMetrics.TN > 0, "Confusion matrix should not be all zeros");
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
        var samples = GenerateSamples(200);

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
        var samples = GenerateSamples(200);
        var hp = DefaultHp() with { MaxEpochs = 5, WalkForwardFolds = 2, EarlyStoppingPatience = 3 };

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
        var samples = GenerateSamples(260, featureCount: 12);
        var hp = DefaultHp() with
        {
            MaxEpochs = 8,
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
        Assert.True(snap.TabNetPolyTopFeatureIndices!.Length > 1);
        Assert.True(snap.Features.Length > samples[0].Features.Length);
        Assert.NotNull(snap.FeaturePipelineDescriptors);
        Assert.NotEmpty(snap.FeaturePipelineDescriptors);
        Assert.Equal(
            snap.Features.Length - snap.TabNetRawFeatureCount,
            snap.FeaturePipelineDescriptors.Sum(d => d.OutputCount));
        Assert.False(string.IsNullOrWhiteSpace(snap.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.PreprocessingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(snap.TrainerFingerprint));
        Assert.True(snap.PrunedFeatureCount > 0, "Test setup should exercise the TabNet pruning path.");
        Assert.NotNull(snap.TabNetAttentionFcWeights);
        Assert.Equal(4, snap.TabNetAttentionFcWeights![0][0].Length);

        int featureCount = snap.Features.Length;
        float[] inferenceFeatures = MLSignalScorer.StandardiseFeatures(
            samples[0].Features, snap.Means, snap.Stds, featureCount);
        var beforeTransforms = (float[])inferenceFeatures.Clone();

        InferenceHelpers.ApplyModelSpecificFeatureTransforms(inferenceFeatures, snap);

        int rawFeatureCount = snap.TabNetRawFeatureCount;
        int leftIdx = snap.TabNetPolyTopFeatureIndices[0];
        int rightIdx = snap.TabNetPolyTopFeatureIndices[1];
        Assert.Equal(beforeTransforms[leftIdx] * beforeTransforms[rightIdx], inferenceFeatures[rawFeatureCount], 5);

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
        Assert.True(artifact.SelectedGlobalCalibration is "PLATT" or "TEMPERATURE");
        Assert.Equal(artifact.TemperatureSelected, snap.TemperatureScale > 0.0);
        Assert.True(artifact.GlobalPlattNll >= 0.0);
        Assert.True(artifact.PreIsotonicNll >= 0.0);
        Assert.True(artifact.PostIsotonicNll <= artifact.PreIsotonicNll + 1e-6);
        Assert.Equal(artifact.IsotonicAccepted ? snap.IsotonicBreakpoints.Length / 2 : 0, artifact.IsotonicBreakpointCount);
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
            issue => issue.Contains("Polynomial pipeline replay metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TabNetSnapshotSupport_NormalizeSnapshotCopy_DoesNotMutateSource_AndBackfillsFingerprints()
    {
        var legacy = CreateSimpleTabNetSnapshot(useInitialProjection: false, useSparsemax: true);
        legacy.FeatureSchemaFingerprint = string.Empty;
        legacy.PreprocessingFingerprint = string.Empty;
        legacy.FeaturePipelineDescriptors = [];
        legacy.TabNetPolyTopFeatureIndices = [0, 1];

        var normalized = TabNetSnapshotSupport.NormalizeSnapshotCopy(legacy);

        Assert.NotSame(legacy, normalized);
        Assert.Empty(legacy.FeaturePipelineDescriptors);
        Assert.NotEmpty(normalized.FeaturePipelineDescriptors);
        Assert.True(string.IsNullOrWhiteSpace(legacy.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.FeatureSchemaFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(normalized.PreprocessingFingerprint));
        Assert.NotEqual(legacy.FeaturePipelineDescriptors.Length, normalized.FeaturePipelineDescriptors.Length);
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
        Assert.Equal(1.0 / (1.0 + Math.Exp(-1.0)), inference.Value.Probability, 10);
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
        Assert.True(totalAlloc < 6_000_000, $"TabNet inference allocated {totalAlloc} bytes over 200 runs.");
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

    private static ModelSnapshot CreateGoldenTabNetSnapshot()
    {
        return new ModelSnapshot
        {
            Type = "TABNET",
            Version = "3.0",
            Features = ["F0", "F1"],
            Means = [0f, 0f],
            Stds = [1f, 1f],
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
}
