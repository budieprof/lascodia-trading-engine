using System.Text.Json;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

public class CpcEncoderProjectionTest
{
    [Fact]
    public void ProjectLatest_Reproduces_Forward_Pass_From_CpcPretrainer()
    {
        // Build a small encoder whose We matrix we control end-to-end.
        const int E = 4;
        const int F = 3;
        double[,] We =
        {
            { 1.0, 0.0, 0.0 },
            { 0.0, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 },
            { -1.0, -1.0, -1.0 }  // will be zero under ReLU for non-negative inputs
        };

        var encoder = CreateEncoder(E, F, We);
        var projection = new CpcEncoderProjection();

        float[] input = { 0.7f, -0.3f, 0.4f };
        var seq = new[] { new float[] { 0.1f, 0.2f, 0.3f }, input };

        var result = projection.ProjectLatest(encoder, seq);

        // Expected: z = ReLU(We · x).
        //  row 0 = 0.7          (>0 → 0.7)
        //  row 1 = -0.3         (ReLU → 0)
        //  row 2 = 0.4
        //  row 3 = -0.8         (ReLU → 0)
        Assert.Equal(E, result.Length);
        Assert.Equal(0.7f, result[0], precision: 5);
        Assert.Equal(0.0f, result[1], precision: 5);
        Assert.Equal(0.4f, result[2], precision: 5);
        Assert.Equal(0.0f, result[3], precision: 5);
    }

    [Fact]
    public void ProjectLatest_Returns_Zero_Vector_For_Empty_Sequence()
    {
        const int E = 8;
        const int F = 6;
        var encoder = CreateEncoder(E, F, new double[E, F]);
        var projection = new CpcEncoderProjection();

        var result = projection.ProjectLatest(encoder, Array.Empty<float[]>());
        Assert.Equal(E, result.Length);
        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ProjectSequence_Produces_Embedding_Per_Row()
    {
        const int E = 3;
        const int F = 2;
        double[,] We = { { 1.0, 0.0 }, { 0.0, 1.0 }, { 1.0, 1.0 } };
        var encoder = CreateEncoder(E, F, We);
        var projection = new CpcEncoderProjection();

        var seq = new[]
        {
            new float[] { 0.5f, 0.0f },
            new float[] { 0.0f, 0.5f },
            new float[] { 0.3f, 0.7f },
        };

        var result = projection.ProjectSequence(encoder, seq);
        Assert.Equal(3, result.Length);
        Assert.Equal(0.5f, result[0][0], precision: 5);
        Assert.Equal(0.5f, result[1][1], precision: 5);
        Assert.Equal(1.0f, result[2][2], precision: 5); // 0.3 + 0.7
    }

    [Fact]
    public void ProjectLatest_Throws_On_Missing_Weights()
    {
        var encoder = new MLCpcEncoder
        {
            Id = 1,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = 4,
            PredictionSteps = 1,
            EncoderBytes = null,
            IsActive = true,
        };
        var projection = new CpcEncoderProjection();

        Assert.Throws<InvalidOperationException>(() =>
            projection.ProjectLatest(encoder, new[] { new float[] { 1f, 1f, 1f } }));
    }

    [Fact]
    public void ProjectLatest_Caches_Per_Encoder_Id()
    {
        const int E = 2;
        const int F = 2;
        double[,] We = { { 1.0, 0.0 }, { 0.0, 1.0 } };
        var encoder = CreateEncoder(E, F, We, id: 42);
        var projection = new CpcEncoderProjection();

        var r1 = projection.ProjectLatest(encoder, new[] { new float[] { 1f, 1f } });
        var r2 = projection.ProjectLatest(encoder, new[] { new float[] { 1f, 1f } });
        // Second call hits the weight cache; output must be identical.
        Assert.Equal(r1, r2);
    }

    private static MLCpcEncoder CreateEncoder(int E, int F, double[,] We, long id = 1)
    {
        var flat = new double[E * F];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                flat[r * F + c] = We[r, c];

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            We = flat,
            Wp = new[] { new double[E * E] }
        });

        return new MLCpcEncoder
        {
            Id = id,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            EmbeddingDim = E,
            PredictionSteps = 1,
            EncoderBytes = bytes,
            TrainedAt = DateTime.UtcNow,
            IsActive = true,
        };
    }
}
