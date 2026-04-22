using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcEncoderGateEvaluatorTest
{
    [Fact]
    public async Task EvaluateAsync_Rejects_When_Projection_Smoke_Test_Is_Invalid()
    {
        await using var db = CreateDbContext();
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns([float.NaN]);

        var evaluator = CreateGateEvaluator(projection.Object);

        var result = await evaluator.EvaluateAsync(
            db,
            CreateRequest(CreateEncoder(), PriorInfoNceLoss: null),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("projection_invalid", result.Reason);
        Assert.Null(result.ValidationInfoNceLoss);
    }

    [Fact]
    public async Task EvaluateAsync_Accepts_When_Validation_Gates_Pass_And_No_Prior_Exists()
    {
        await using var db = CreateDbContext();
        var evaluator = CreateGateEvaluator(CreateProjection());

        var result = await evaluator.EvaluateAsync(
            db,
            CreateRequest(CreateEncoder(), PriorInfoNceLoss: null),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal("accepted", result.Reason);
        Assert.NotNull(result.ValidationScore);
        Assert.True(double.IsFinite(result.ValidationInfoNceLoss!.Value));
        Assert.Equal("downstream_probe_disabled", result.DownstreamProbe.Reason);
        Assert.True(result.Diagnostics.ContainsKey("ValidationMeanEmbeddingL2Norm"));
    }

    [Fact]
    public async Task EvaluateAsync_Rejects_When_Candidate_Does_Not_Improve_On_Prior()
    {
        await using var db = CreateDbContext();
        var evaluator = CreateGateEvaluator(CreateProjection());

        var result = await evaluator.EvaluateAsync(
            db,
            CreateRequest(CreateEncoder(), PriorInfoNceLoss: 0.0001),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("no_improvement", result.Reason);
        Assert.True(result.ValidationInfoNceLoss > 0.0001);
    }

    [Fact]
    public async Task EvaluateAsync_Rejects_When_Projected_Embedding_Shape_Is_Malformed()
    {
        await using var db = CreateDbContext();
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] _) => Enumerable.Repeat(0.1f, encoder.EmbeddingDim).ToArray());
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder _, float[][] sequence) =>
                sequence.Select(_ => new[] { 0.1f }).ToArray());
        var evaluator = CreateGateEvaluator(projection.Object);

        var result = await evaluator.EvaluateAsync(
            db,
            CreateRequest(CreateEncoder(), PriorInfoNceLoss: null),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("validation_loss_out_of_bounds", result.Reason);
        Assert.Null(result.Diagnostics["ValidationMeanEmbeddingL2Norm"]);
    }

    [Fact]
    public async Task DownstreamProbe_Rejects_When_Samples_Are_Insufficient()
    {
        await using var db = CreateDbContext();
        var evaluator = CreateProbeEvaluator(CreateDirectionalProjection());

        var result = await evaluator.EvaluateAsync(
            db,
            CreateProbeRequest(minSamples: 50),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("downstream_probe_insufficient_samples", result.Reason);
    }

    [Fact]
    public async Task DownstreamProbe_Rejects_When_Labels_Are_Insufficient()
    {
        await using var db = CreateDbContext();
        var evaluator = CreateProbeEvaluator(CreateDirectionalProjection());

        var result = await evaluator.EvaluateAsync(
            db,
            CreateProbeRequest(allPositive: true),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("downstream_probe_insufficient_labels", result.Reason);
    }

    [Fact]
    public async Task DownstreamProbe_Rejects_When_Balanced_Accuracy_Is_Below_Floor()
    {
        await using var db = CreateDbContext();
        var evaluator = CreateProbeEvaluator(CreateConstantProjection());

        var result = await evaluator.EvaluateAsync(
            db,
            CreateProbeRequest(minBalancedAccuracy: 0.75),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("downstream_probe_below_floor", result.Reason);
        Assert.Equal(0.5, result.CandidateBalancedAccuracy);
    }

    [Fact]
    public async Task DownstreamProbe_Rejects_When_Candidate_Has_No_Lift_Over_Prior()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(CreateEncoder(id: 42));
        await db.SaveChangesAsync();

        var evaluator = CreateProbeEvaluator(CreatePriorAwareProjection(priorId: 42));

        var result = await evaluator.EvaluateAsync(
            db,
            CreateProbeRequest(priorEncoderId: 42, minBalancedAccuracy: 0.40, minProbeImprovement: 0.01),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("downstream_probe_no_lift", result.Reason);
        Assert.Equal(0.5, result.CandidateBalancedAccuracy);
        Assert.Equal(1.0, result.PriorBalancedAccuracy);
    }

    private static CpcDownstreamProbeEvaluator CreateProbeEvaluator(ICpcEncoderProjection projection)
        => new(new CpcDownstreamProbeRunner(projection));

    [Fact]
    public async Task EvaluateAsync_Rejects_When_RepresentationDrift_Below_Minimum_Centroid_Distance()
    {
        await using var db = CreateDbContext();
        // Prior and candidate both project to constant +1s; centroid cosine distance = 0 < floor.
        db.Set<MLCpcEncoder>().Add(CreateEncoder(id: 77));
        await db.SaveChangesAsync();

        var projection = CreateConstantProjection();
        var evaluator = CreateGateEvaluator(projection);

        var request = CreateRequestWithGateOverrides(
            priorEncoderId: 77,
            priorLoss: 1000.0,
            enableRepresentationDrift: true,
            minCentroidDistance: 0.1,
            encoder: CreateEncoder(id: 78));
        var result = await evaluator.EvaluateAsync(db, request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("representation_drift_insufficient", result.Reason);
        Assert.NotNull(result.RepresentationDrift.CentroidCosineDistance);
        Assert.InRange(result.RepresentationDrift.CentroidCosineDistance!.Value, 0.0, 1e-6);
    }

    [Fact]
    public async Task EvaluateAsync_Rejects_When_AdversarialAuc_Exceeds_Ceiling()
    {
        await using var db = CreateDbContext();
        db.Set<MLCpcEncoder>().Add(CreateEncoder(id: 88, encoderBytes: [0]));
        await db.SaveChangesAsync();

        var evaluator = CreateGateEvaluator(PolarityProjection());

        var candidate = CreateEncoder(id: 89, encoderBytes: [1]);
        var request = CreateRequestWithGateOverrides(
            priorEncoderId: 88,
            priorLoss: 1000.0,
            enableAdversarial: true,
            maxAdversarialAuc: 0.80,
            minAdversarialSamples: 5,
            encoder: candidate);
        var result = await evaluator.EvaluateAsync(db, request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("adversarial_validation_failed", result.Reason);
        Assert.NotNull(result.AdversarialValidation.Auc);
        Assert.True(result.AdversarialValidation.Auc!.Value > 0.80);
    }

    [Fact]
    public async Task EvaluateAsync_Rejects_When_ArchitectureSwitch_Regresses_Downstream_Accuracy()
    {
        await using var db = CreateDbContext();
        // Active Linear encoder already present; candidate is Tcn and the projection makes Linear
        // the "good" predictor and Tcn the "bad" predictor → the cross-architecture regression
        // exceeds the configured tolerance.
        db.Set<MLCpcEncoder>().Add(new MLCpcEncoder
        {
            Id = 99, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear, EmbeddingDim = 16,
            PredictionSteps = 3, EncoderBytes = [2], // informative in the prior-aware projection
            InfoNceLoss = 1.0, TrainedAt = DateTime.UtcNow.AddDays(-2),
            IsActive = true
        });
        await db.SaveChangesAsync();

        var evaluator = CreateGateEvaluator(CreatePriorAwareProjection(priorId: 99));
        var candidate = new MLCpcEncoder
        {
            Id = 100, Symbol = "EURUSD", Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Tcn, EmbeddingDim = 16,
            PredictionSteps = 3, EncoderBytes = [3], // uninformative in the prior-aware projection
            InfoNceLoss = 1.0, TrainedAt = DateTime.UtcNow,
            IsActive = true
        };

        var request = CreateArchitectureSwitchRequest(candidate);
        var result = await evaluator.EvaluateAsync(db, request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("architecture_switch_regression", result.Reason);
        Assert.True(result.ArchitectureSwitch.Evaluated);
        Assert.NotNull(result.ArchitectureSwitch.CandidateBalancedAccuracy);
        Assert.NotNull(result.ArchitectureSwitch.CrossArchPriorBalancedAccuracy);
    }

    private static CpcEncoderGateRequest CreateRequestWithGateOverrides(
        long? priorEncoderId,
        double? priorLoss,
        MLCpcEncoder encoder,
        bool enableRepresentationDrift = false,
        double minCentroidDistance = 0.0,
        double maxRepresentationMeanPsi = double.PositiveInfinity,
        bool enableAdversarial = false,
        double maxAdversarialAuc = 1.0,
        int minAdversarialSamples = 10)
    {
        var sequences = BuildDirectionalSequences(allPositive: false);
        return new CpcEncoderGateRequest(
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            Regime: null,
            PriorEncoderId: priorEncoderId,
            PriorInfoNceLoss: priorLoss,
            Encoder: encoder,
            TrainingSequences: sequences.Take(12).ToArray(),
            ValidationSequences: sequences.Skip(12).ToArray(),
            Options: new CpcEncoderGateOptions(
                EmbeddingBlockSize: encoder.EmbeddingDim,
                PredictionSteps: 3,
                MaxValidationLoss: 1000.0,
                MinValidationEmbeddingL2Norm: 0.0,
                MinValidationEmbeddingVariance: 0.0,
                EnableDownstreamProbeGate: false,
                MinDownstreamProbeSamples: 10,
                MinDownstreamProbeBalancedAccuracy: 0.50,
                MinDownstreamProbeImprovement: 0.01,
                MinImprovement: 0.0,
                EnableRepresentationDriftGate: enableRepresentationDrift,
                MinCentroidCosineDistance: minCentroidDistance,
                MaxRepresentationMeanPsi: maxRepresentationMeanPsi,
                EnableArchitectureSwitchGate: false,
                MaxArchitectureSwitchAccuracyRegression: 1.0,
                EnableAdversarialValidationGate: enableAdversarial,
                MaxAdversarialValidationAuc: maxAdversarialAuc,
                MinAdversarialValidationSamples: minAdversarialSamples));
    }

    private static CpcEncoderGateRequest CreateArchitectureSwitchRequest(MLCpcEncoder encoder)
    {
        var sequences = BuildDirectionalSequences(allPositive: false);
        return new CpcEncoderGateRequest(
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            Regime: null,
            PriorEncoderId: null,
            PriorInfoNceLoss: null,
            Encoder: encoder,
            TrainingSequences: sequences.Take(12).ToArray(),
            ValidationSequences: sequences.Skip(12).ToArray(),
            Options: new CpcEncoderGateOptions(
                EmbeddingBlockSize: encoder.EmbeddingDim,
                PredictionSteps: 3,
                MaxValidationLoss: 1000.0,
                MinValidationEmbeddingL2Norm: 0.0,
                MinValidationEmbeddingVariance: 0.0,
                EnableDownstreamProbeGate: false,
                MinDownstreamProbeSamples: 5,
                MinDownstreamProbeBalancedAccuracy: 0.0,
                MinDownstreamProbeImprovement: 0.0,
                MinImprovement: 0.0,
                EnableRepresentationDriftGate: false,
                MinCentroidCosineDistance: 0.0,
                MaxRepresentationMeanPsi: double.PositiveInfinity,
                EnableArchitectureSwitchGate: true,
                MaxArchitectureSwitchAccuracyRegression: 0.05,
                EnableAdversarialValidationGate: false,
                MaxAdversarialValidationAuc: 1.0,
                MinAdversarialValidationSamples: 10));
    }

    private static MLCpcEncoder CreateEncoder(long id, byte[]? encoderBytes)
        => new()
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            PredictionSteps = 3,
            EncoderBytes = encoderBytes ?? [1, 2, 3],
            InfoNceLoss = 1.0,
            TrainedAt = DateTime.UtcNow,
            IsActive = true,
        };

    private static ICpcEncoderProjection PolarityProjection()
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) =>
            {
                float sign = e.EncoderBytes is { Length: > 0 } && e.EncoderBytes[0] == 1 ? 1.0f : -1.0f;
                return Enumerable.Repeat(sign, e.EmbeddingDim).ToArray();
            });
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] seq) =>
            {
                float sign = e.EncoderBytes is { Length: > 0 } && e.EncoderBytes[0] == 1 ? 1.0f : -1.0f;
                return seq.Select(_ => Enumerable.Repeat(sign, e.EmbeddingDim).ToArray()).ToArray();
            });
        return projection.Object;
    }

    private static CpcEncoderGateRequest CreateRequest(
        MLCpcEncoder encoder,
        double? PriorInfoNceLoss)
    {
        var sequences = BuildSequences();
        return new CpcEncoderGateRequest(
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            Regime: null,
            PriorEncoderId: null,
            PriorInfoNceLoss: PriorInfoNceLoss,
            Encoder: encoder,
            TrainingSequences: sequences.Take(12).ToArray(),
            ValidationSequences: sequences.Skip(12).ToArray(),
            Options: new CpcEncoderGateOptions(
                EmbeddingBlockSize: 16,
                PredictionSteps: 3,
                MaxValidationLoss: 1000.0,
                MinValidationEmbeddingL2Norm: 0.001,
                MinValidationEmbeddingVariance: 0.0000001,
                EnableDownstreamProbeGate: false,
                MinDownstreamProbeSamples: 10,
                MinDownstreamProbeBalancedAccuracy: 0.50,
                MinDownstreamProbeImprovement: 0.01,
                MinImprovement: 0.02,
                EnableRepresentationDriftGate: false,
                MinCentroidCosineDistance: 0.0,
                MaxRepresentationMeanPsi: double.PositiveInfinity,
                EnableArchitectureSwitchGate: false,
                MaxArchitectureSwitchAccuracyRegression: 1.0,
                EnableAdversarialValidationGate: false,
                MaxAdversarialValidationAuc: 1.0,
                MinAdversarialValidationSamples: 10));
    }

    private static MLCpcEncoder CreateEncoder(long id = 0)
        => new()
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 16,
            PredictionSteps = 3,
            EncoderBytes = [1, 2, 3],
            InfoNceLoss = 1.0,
            TrainedAt = DateTime.UtcNow,
            IsActive = true,
        };

    private static CpcEncoderGateEvaluator CreateGateEvaluator(ICpcEncoderProjection projection)
    {
        var runner = new CpcDownstreamProbeRunner(projection);
        return new CpcEncoderGateEvaluator(
            projection,
            new CpcContrastiveValidationScorer(projection),
            new CpcDownstreamProbeEvaluator(runner),
            runner,
            new CpcRepresentationDriftScorer(projection),
            new CpcAdversarialValidationScorer(projection));
    }

    private static ICpcEncoderProjection CreateProjection()
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] sequence) => ProjectSequence(encoder, sequence)[^1]);
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] sequence) => ProjectSequence(encoder, sequence));
        return projection.Object;
    }

    private static float[][] ProjectSequence(MLCpcEncoder encoder, float[][] sequence)
    {
        var projected = new float[sequence.Length][];
        for (int row = 0; row < sequence.Length; row++)
        {
            projected[row] = new float[encoder.EmbeddingDim];
            for (int dim = 0; dim < encoder.EmbeddingDim; dim++)
            {
                projected[row][dim] =
                    (float)((row + 1) * 0.01 + (dim + 1) * 0.001 + sequence[row][0] * 0.1 + sequence[row][3] * 10.0);
            }
        }

        return projected;
    }

    private static IReadOnlyList<float[][]> BuildSequences()
    {
        var sequences = new List<float[][]>();
        for (int seq = 0; seq < 24; seq++)
        {
            var rows = new float[12][];
            for (int row = 0; row < rows.Length; row++)
            {
                var direction = (seq + row) % 2 == 0 ? 1f : -1f;
                rows[row] =
                [
                    seq * 0.01f + row * 0.001f,
                    row * 0.002f,
                    row * 0.003f,
                    direction * (0.0001f + seq * 0.00001f),
                    0.5f + row * 0.01f,
                    0.25f + seq * 0.01f
                ];
            }

            sequences.Add(rows);
        }

        return sequences;
    }

    private static CpcEncoderGateRequest CreateProbeRequest(
        long? priorEncoderId = null,
        int minSamples = 10,
        double minBalancedAccuracy = 0.50,
        double minProbeImprovement = 0.01,
        bool allPositive = false)
    {
        var sequences = BuildDirectionalSequences(allPositive);
        return new CpcEncoderGateRequest(
            Symbol: "EURUSD",
            Timeframe: Timeframe.H1,
            Regime: null,
            PriorEncoderId: priorEncoderId,
            PriorInfoNceLoss: null,
            Encoder: CreateEncoder(),
            TrainingSequences: sequences.Take(12).ToArray(),
            ValidationSequences: sequences.Skip(12).ToArray(),
            Options: new CpcEncoderGateOptions(
                EmbeddingBlockSize: 16,
                PredictionSteps: 3,
                MaxValidationLoss: 1000.0,
                MinValidationEmbeddingL2Norm: 0.0,
                MinValidationEmbeddingVariance: 0.0,
                EnableDownstreamProbeGate: true,
                MinDownstreamProbeSamples: minSamples,
                MinDownstreamProbeBalancedAccuracy: minBalancedAccuracy,
                MinDownstreamProbeImprovement: minProbeImprovement,
                MinImprovement: 0.02,
                EnableRepresentationDriftGate: false,
                MinCentroidCosineDistance: 0.0,
                MaxRepresentationMeanPsi: double.PositiveInfinity,
                EnableArchitectureSwitchGate: false,
                MaxArchitectureSwitchAccuracyRegression: 1.0,
                EnableAdversarialValidationGate: false,
                MaxAdversarialValidationAuc: 1.0,
                MinAdversarialValidationSamples: 10));
    }

    private static IReadOnlyList<float[][]> BuildDirectionalSequences(bool allPositive)
    {
        var sequences = new List<float[][]>();
        for (int seq = 0; seq < 24; seq++)
        {
            var positive = allPositive || seq % 2 == 0;
            var sign = positive ? 1f : -1f;
            var rows = new float[12][];
            for (int row = 0; row < rows.Length; row++)
            {
                rows[row] =
                [
                    sign,
                    row * 0.01f,
                    seq * 0.01f,
                    row >= 5 && row <= 7 ? sign * 0.001f : 0.0f,
                    1.0f,
                    0.5f
                ];
            }

            sequences.Add(rows);
        }

        return sequences;
    }

    private static ICpcEncoderProjection CreateDirectionalProjection()
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] _) => Enumerable.Repeat(0.1f, encoder.EmbeddingDim).ToArray());
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] sequence) =>
                ProjectDirectionalSequence(encoder, sequence, informative: true));
        return projection.Object;
    }

    private static ICpcEncoderProjection CreateConstantProjection()
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] _) => Enumerable.Repeat(1.0f, encoder.EmbeddingDim).ToArray());
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] sequence) =>
                ProjectDirectionalSequence(encoder, sequence, informative: false));
        return projection.Object;
    }

    private static ICpcEncoderProjection CreatePriorAwareProjection(long priorId)
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] _) => Enumerable.Repeat(1.0f, encoder.EmbeddingDim).ToArray());
        projection
            .Setup(p => p.ProjectSequence(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder encoder, float[][] sequence) =>
                ProjectDirectionalSequence(encoder, sequence, informative: encoder.Id == priorId));
        return projection.Object;
    }

    private static float[][] ProjectDirectionalSequence(
        MLCpcEncoder encoder,
        float[][] sequence,
        bool informative)
    {
        var projected = new float[sequence.Length][];
        var sign = sequence[0][0];
        for (int row = 0; row < sequence.Length; row++)
        {
            projected[row] = new float[encoder.EmbeddingDim];
            projected[row][0] = informative ? sign : 1.0f;
            for (int dim = 1; dim < encoder.EmbeddingDim; dim++)
                projected[row][dim] = dim * 0.001f;
        }

        return projected;
    }

    private static WriteApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }
}
