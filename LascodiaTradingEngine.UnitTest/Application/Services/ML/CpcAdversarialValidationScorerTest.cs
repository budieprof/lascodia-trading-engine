using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcAdversarialValidationScorerTest
{
    [Fact]
    public void Score_Returns_PriorUnavailable_When_Prior_Is_Null()
    {
        var scorer = new CpcAdversarialValidationScorer(ConstantProjection(sign: 1f));
        var result = scorer.Score(Encoder("new"), prior: null, Sequences(count: 50), minSamplesPerClass: 10);

        Assert.False(result.Evaluated);
        Assert.Equal("adversarial_validation_prior_unavailable", result.Reason);
    }

    [Fact]
    public void Score_Returns_InsufficientSamples_When_Min_Is_Not_Met()
    {
        var scorer = new CpcAdversarialValidationScorer(PolarityProjection());
        var result = scorer.Score(Encoder("new", marker: 1), Encoder("old", marker: 0), Sequences(count: 5), minSamplesPerClass: 50);

        Assert.False(result.Evaluated);
        Assert.Equal("adversarial_validation_insufficient_samples", result.Reason);
    }

    [Fact]
    public void Score_Returns_Near_Half_Auc_When_Embeddings_Are_Identical()
    {
        // Same projection for both encoders; AUC should reflect inseparability — i.e. near 0.5.
        var scorer = new CpcAdversarialValidationScorer(ConstantProjection(sign: 1f));
        var result = scorer.Score(Encoder("new"), Encoder("old"), Sequences(count: 50), minSamplesPerClass: 10);

        Assert.True(result.Evaluated);
        Assert.NotNull(result.Auc);
        Assert.InRange(result.Auc!.Value, 0.49, 0.51);
    }

    [Fact]
    public void Score_Returns_Max_Auc_When_Embeddings_Are_Fully_Separable()
    {
        // Candidate projects to +1s; prior projects to -1s → trivially separable → AUC = 1.0.
        var scorer = new CpcAdversarialValidationScorer(PolarityProjection());
        var result = scorer.Score(Encoder("new", marker: 1), Encoder("old", marker: 0), Sequences(count: 50), minSamplesPerClass: 10);

        Assert.True(result.Evaluated);
        Assert.NotNull(result.Auc);
        Assert.InRange(result.Auc!.Value, 0.99, 1.0);
    }

    private static MLCpcEncoder Encoder(string label, byte marker = 0)
        => new()
        {
            Id = label == "new" ? 2 : 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EncoderType = CpcEncoderType.Linear,
            EmbeddingDim = 4,
            PredictionSteps = 2,
            EncoderBytes = [marker],
            TrainedAt = DateTime.UtcNow,
            IsActive = true,
        };

    private static IReadOnlyList<float[][]> Sequences(int count)
        => Enumerable.Range(0, count).Select(i => new[] { new[] { (float)i } }).ToArray();

    private static ICpcEncoderProjection ConstantProjection(float sign)
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) => Enumerable.Repeat(sign, e.EmbeddingDim).ToArray());
        return projection.Object;
    }

    private static ICpcEncoderProjection PolarityProjection()
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) =>
            {
                var sign = e.EncoderBytes is { Length: > 0 } && e.EncoderBytes[0] == 1 ? 1.0f : -1.0f;
                return Enumerable.Repeat(sign, e.EmbeddingDim).ToArray();
            });
        return projection.Object;
    }
}
