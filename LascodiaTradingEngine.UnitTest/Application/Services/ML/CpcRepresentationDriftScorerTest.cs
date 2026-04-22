using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Moq;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcRepresentationDriftScorerTest
{
    [Fact]
    public void Score_Returns_PriorUnavailable_When_Prior_Is_Null()
    {
        var scorer = new CpcRepresentationDriftScorer(ConstantProjection(sign: 1f));
        var result = scorer.Score(Encoder("new"), prior: null, BuildSequences(count: 10));

        Assert.False(result.Evaluable);
        Assert.Equal("representation_drift_prior_unavailable", result.Reason);
    }

    [Fact]
    public void Score_Returns_Zero_Distance_And_Zero_Psi_When_Prior_And_Candidate_Match()
    {
        var scorer = new CpcRepresentationDriftScorer(ConstantProjection(sign: 1f));
        var result = scorer.Score(Encoder("new"), Encoder("old"), BuildSequences(count: 20));

        Assert.True(result.Evaluable);
        Assert.NotNull(result.CentroidCosineDistance);
        Assert.NotNull(result.MeanPsi);
        Assert.InRange(result.CentroidCosineDistance!.Value, 0.0, 1e-9);
        Assert.InRange(result.MeanPsi!.Value, 0.0, 1e-9);
    }

    [Fact]
    public void Score_Returns_Large_Distance_When_Prior_And_Candidate_Point_Opposite()
    {
        // Projection returns +1s for candidate, -1s for prior. Cosine distance should be ~2.0
        // (orthogonally opposite directions).
        var projection = PolarityByEncoderProjection();
        var scorer = new CpcRepresentationDriftScorer(projection);
        var result = scorer.Score(Encoder("new", marker: 1), Encoder("old", marker: 0), BuildSequences(count: 20));

        Assert.True(result.Evaluable);
        Assert.NotNull(result.CentroidCosineDistance);
        Assert.InRange(result.CentroidCosineDistance!.Value, 1.5, 2.0);
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

    private static IReadOnlyList<float[][]> BuildSequences(int count)
    {
        var list = new List<float[][]>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add([[i, i * 0.1f, i * 0.01f, i * 0.001f]]);
        }
        return list;
    }

    private static ICpcEncoderProjection ConstantProjection(float sign)
    {
        var projection = new Mock<ICpcEncoderProjection>();
        projection
            .Setup(p => p.ProjectLatest(It.IsAny<MLCpcEncoder>(), It.IsAny<float[][]>()))
            .Returns((MLCpcEncoder e, float[][] _) => Enumerable.Repeat(sign, e.EmbeddingDim).ToArray());
        return projection.Object;
    }

    private static ICpcEncoderProjection PolarityByEncoderProjection()
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
