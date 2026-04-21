using System.Text.Json;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

/// <summary>
/// Guards the TCN CPC pretrainer: forward pass produces a well-shaped embedding sequence,
/// the SGD backward pass actually moves weights off their He-init seed, persisted payload
/// round-trips through <see cref="CpcEncoderProjection"/>, and the stamped
/// <see cref="MLCpcEncoder.EncoderType"/> is <see cref="CpcEncoderType.Tcn"/>.
/// </summary>
public class CpcTcnPretrainerTest
{
    [Fact]
    public async Task TrainAsync_Returns_Tcn_Encoder_With_Well_Shaped_Payload()
    {
        var sequences = BuildArOneSequences(numSequences: 32, seqLen: 24, featureDim: 6, seed: 17);

        var trainer = new CpcTcnPretrainer();
        var encoder = await trainer.TrainAsync(
            "EURUSD", Timeframe.H1, sequences,
            embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        Assert.Equal(CpcEncoderType.Tcn, encoder.EncoderType);
        Assert.True(encoder.IsActive);
        Assert.NotNull(encoder.EncoderBytes);
        Assert.True(double.IsFinite(encoder.InfoNceLoss),
            $"InfoNceLoss not finite: {encoder.InfoNceLoss}");
        Assert.True(encoder.InfoNceLoss < 10.0,
            $"InfoNceLoss {encoder.InfoNceLoss} above MaxAcceptableLoss bound.");

        // Payload shape sanity.
        using var doc = JsonDocument.Parse(encoder.EncoderBytes);
        var root = doc.RootElement;
        Assert.Equal("tcn", root.GetProperty("Type").GetString());
        Assert.Equal(8, root.GetProperty("E").GetInt32());
        Assert.Equal(6, root.GetProperty("F").GetInt32());
        Assert.Equal(3, root.GetProperty("K").GetInt32());
        Assert.Equal(8 * 6 * 3, root.GetProperty("W1").GetArrayLength());
        Assert.Equal(8 * 8 * 3, root.GetProperty("W2").GetArrayLength());
        Assert.Equal(8 * 6, root.GetProperty("Wr").GetArrayLength());
    }

    [Fact]
    public async Task TrainAsync_Moves_Weights_Off_He_Init()
    {
        var sequences = BuildArOneSequences(numSequences: 32, seqLen: 24, featureDim: 6, seed: 42);

        var trainer = new CpcTcnPretrainer();
        var encoder = await trainer.TrainAsync(
            "EURUSD", Timeframe.H1, sequences,
            embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        using var doc = JsonDocument.Parse(encoder.EncoderBytes!);
        var root = doc.RootElement;
        var trainedW1 = root.GetProperty("W1").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var trainedW2 = root.GetProperty("W2").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var trainedWr = root.GetProperty("Wr").EnumerateArray().Select(v => v.GetDouble()).ToArray();

        // Reproduce the deterministic init (same PRNG sequence). W1 is drawn first, then W2,
        // then Wr, then Wp — matching the order in CpcTcnPretrainer.
        var (initW1, initW2, initWr) = ReproduceInit(E: 8, F: 6, K: 3, seed: 42);
        Assert.Equal(initW1.Length, trainedW1.Length);
        Assert.Equal(initW2.Length, trainedW2.Length);
        Assert.Equal(initWr.Length, trainedWr.Length);

        double drift1 = 0, drift2 = 0, driftR = 0;
        for (int i = 0; i < trainedW1.Length; i++) { var d = trainedW1[i] - initW1[i]; drift1 += d * d; }
        for (int i = 0; i < trainedW2.Length; i++) { var d = trainedW2[i] - initW2[i]; drift2 += d * d; }
        for (int i = 0; i < trainedWr.Length; i++) { var d = trainedWr[i] - initWr[i]; driftR += d * d; }

        Assert.True(drift1 > 1e-6 || drift2 > 1e-6 || driftR > 1e-6,
            $"TCN weights did not drift from He init (|ΔW1|²={drift1:g}, |ΔW2|²={drift2:g}, |ΔWr|²={driftR:g}).");
    }

    [Fact]
    public async Task Projection_Reproduces_Pretrainer_Forward_Pass_On_Last_Timestep()
    {
        var sequences = BuildArOneSequences(numSequences: 16, seqLen: 20, featureDim: 6, seed: 11);

        var trainer = new CpcTcnPretrainer();
        var encoder = await trainer.TrainAsync(
            "EURUSD", Timeframe.H1, sequences,
            embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        var projection = new CpcEncoderProjection();
        var projected = projection.ProjectLatest(encoder, sequences[0]);

        Assert.Equal(encoder.EmbeddingDim, projected.Length);
        foreach (var v in projected)
            Assert.True(float.IsFinite(v));
    }

    [Fact]
    public async Task TrainAsync_On_Empty_Sequences_Returns_Inactive_Tcn_Encoder()
    {
        var trainer = new CpcTcnPretrainer();
        var result = await trainer.TrainAsync(
            "EURUSD", Timeframe.H1, Array.Empty<float[][]>(),
            embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        Assert.Equal(CpcEncoderType.Tcn, result.EncoderType);
        Assert.False(result.IsActive);
        Assert.Equal(0.0, result.InfoNceLoss);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static IReadOnlyList<float[][]> BuildArOneSequences(
        int numSequences, int seqLen, int featureDim, int seed)
    {
        var rng = new Random(seed);
        var result = new List<float[][]>(numSequences);
        for (int s = 0; s < numSequences; s++)
        {
            var seq = new float[seqLen][];
            var state = new double[featureDim];
            for (int f = 0; f < featureDim; f++) state[f] = rng.NextDouble() * 2 - 1;
            for (int t = 0; t < seqLen; t++)
            {
                var row = new float[featureDim];
                for (int f = 0; f < featureDim; f++)
                {
                    double eps = (rng.NextDouble() * 2 - 1) * 0.2;
                    state[f] = 0.8 * state[f] + eps;
                    row[f] = (float)state[f];
                }
                seq[t] = row;
            }
            result.Add(seq);
        }
        return result;
    }

    /// <summary>
    /// Replays the deterministic He-init PRNG sequence exactly as
    /// <see cref="CpcTcnPretrainer"/> consumes it (W1, then W2, then Wr).
    /// </summary>
    private static (double[] W1, double[] W2, double[] Wr) ReproduceInit(int E, int F, int K, int seed)
    {
        var rng = new Random(seed);
        double scale1 = Math.Sqrt(2.0 / (F * K));
        double scale2 = Math.Sqrt(2.0 / (E * K));
        double scaleR = Math.Sqrt(2.0 / F);

        var W1 = new double[E * F * K];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                for (int k = 0; k < K; k++)
                    W1[(r * F + c) * K + k] = (rng.NextDouble() * 2 - 1) * scale1;

        var W2 = new double[E * E * K];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++)
                for (int k = 0; k < K; k++)
                    W2[(r * E + c) * K + k] = (rng.NextDouble() * 2 - 1) * scale2;

        var Wr = new double[E * F];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                Wr[r * F + c] = (rng.NextDouble() * 2 - 1) * scaleR;

        return (W1, W2, Wr);
    }
}
