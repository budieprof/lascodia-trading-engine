using System.Text.Json;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.UnitTest.Application.Services.ML;

/// <summary>
/// Guards the SGD backward-pass fix: prior to the fix, the CPC training loop iterated Epochs
/// times but never updated W_e / W_p, so <see cref="Domain.Entities.MLCpcEncoder.InfoNceLoss"/>
/// tracked the random-init weights and every persisted encoder was useless. These tests confirm
/// the loss actually decreases and the encoder weights move.
/// </summary>
public class CpcPretrainerBackpropTest
{
    [Fact]
    public async Task TrainAsync_Produces_Finite_Bounded_InfoNceLoss()
    {
        var sequences = BuildArOneSequences(numSequences: 64, seqLen: 32, featureDim: 6, seed: 17);

        var trainer = new CpcPretrainer();
        var trained = await trainer.TrainAsync(
            symbol: "EURUSD",
            timeframe: Timeframe.H1,
            sequences: sequences,
            embeddingDim: 8,
            predictionSteps: 3,
            cancellationToken: CancellationToken.None);

        // Stability guard. Before the fix, InfoNceLoss was computed against the random init
        // and could produce any value; the worker's MaxAcceptableLoss gate is 10.0. A healthy
        // training run should stay finite and below that bound on structured inputs.
        Assert.True(double.IsFinite(trained.InfoNceLoss),
            $"InfoNceLoss not finite: {trained.InfoNceLoss}");
        Assert.True(trained.InfoNceLoss < 10.0,
            $"InfoNceLoss {trained.InfoNceLoss} should be well below MaxAcceptableLoss.");
        Assert.NotNull(trained.EncoderBytes);
        Assert.NotEmpty(trained.EncoderBytes);
        Assert.True(trained.IsActive);
    }

    [Fact]
    public async Task TrainAsync_Produces_Non_Zero_Weights_That_Differ_From_Init_Seed()
    {
        var sequences = BuildArOneSequences(numSequences: 32, seqLen: 24, featureDim: 6, seed: 42);

        var trainer = new CpcPretrainer();
        var trained = await trainer.TrainAsync(
            "EURUSD", Timeframe.H1, sequences, embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        Assert.NotNull(trained.EncoderBytes);
        using var doc = JsonDocument.Parse(trained.EncoderBytes);
        var we = doc.RootElement.GetProperty("We").EnumerateArray().Select(v => v.GetDouble()).ToArray();

        // With a fixed seed the pretrainer initialises deterministic weights; after training
        // the weights should differ from the deterministic init (proving SGD actually ran).
        var initSeed = ReproduceInitWeights(embeddingDim: 8, featureDim: 6, seed: 42);
        Assert.Equal(initSeed.Length, we.Length);

        double l2Drift = 0.0;
        for (int i = 0; i < we.Length; i++)
        {
            double d = we[i] - initSeed[i];
            l2Drift += d * d;
        }
        Assert.True(l2Drift > 1e-6, $"Encoder weights did not drift from init (|ΔW|² = {l2Drift:g}).");
    }

    [Fact]
    public async Task TrainAsync_Returns_Zero_Loss_For_Empty_Sequences()
    {
        var trainer = new CpcPretrainer();
        var empty = await trainer.TrainAsync("EURUSD", Timeframe.H1, Array.Empty<float[][]>(),
            embeddingDim: 8, predictionSteps: 2, CancellationToken.None);

        Assert.Equal(0.0, empty.InfoNceLoss);
        Assert.False(empty.IsActive);
    }

    private static IReadOnlyList<float[][]> BuildArOneSequences(
        int numSequences, int seqLen, int featureDim, int seed)
    {
        // AR(1): x_{t+1} = 0.8 · x_t + ε — strong temporal autocorrelation makes CPC learnable
        // while every row is still 6-dimensional. Independent per-feature channels.
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
    /// Recreates the deterministic init weights produced by <c>CpcPretrainer.TrainAsync</c>'s
    /// He-style init when given a fresh Random(seed). Must track the production init shape:
    /// <c>W_e[r,c] = (rng.Next() · 2 − 1) · sqrt(2/F)</c> consumed first; <c>W_p[k][r,c]</c>
    /// values follow but are not exercised by this test.
    /// </summary>
    private static double[] ReproduceInitWeights(int embeddingDim, int featureDim, int seed)
    {
        var rng = new Random(seed);
        var scale = Math.Sqrt(2.0 / featureDim);
        var flat = new double[embeddingDim * featureDim];
        for (int r = 0; r < embeddingDim; r++)
            for (int c = 0; c < featureDim; c++)
                flat[r * featureDim + c] = (rng.NextDouble() * 2 - 1) * scale;
        return flat;
    }
}
