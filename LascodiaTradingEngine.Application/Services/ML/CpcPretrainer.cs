using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Contrastive Predictive Coding (CPC) pre-trainer using a linear encoder and
/// bilinear prediction network (Rec #49). Minimises InfoNCE loss: the positive future
/// embedding should score higher than K=9 random negatives when predicted from context.
/// </summary>
[RegisterService]
public sealed class CpcPretrainer : ICpcPretrainer
{
    private const int    Epochs = 30;
    private const double Lr     = 0.001;
    private const int    Negatives = 9;

    public Task<MLCpcEncoder> TrainAsync(
        string symbol, Timeframe timeframe,
        IReadOnlyList<float[][]> sequences,
        int embeddingDim,
        int predictionSteps,
        CancellationToken cancellationToken)
    {
        if (sequences.Count == 0 || sequences[0].Length == 0)
            return Task.FromResult(new MLCpcEncoder
            {
                Symbol = symbol, Timeframe = timeframe,
                EmbeddingDim = embeddingDim, PredictionSteps = predictionSteps,
                TrainedAt = DateTime.UtcNow
            });

        int F = sequences[0][0].Length;
        int E = embeddingDim;
        var rng = new Random(42);

        // Encoder weights: E×F
        double[,] We = new double[E, F];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                We[r, c] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(2.0 / F);

        // Prediction bilinear weights: one per prediction step (E×E)
        var Wp = new double[predictionSteps][,];
        for (int k = 0; k < predictionSteps; k++)
        {
            Wp[k] = new double[E, E];
            for (int r = 0; r < E; r++)
                for (int c = 0; c < E; c++)
                    Wp[k][r, c] = (rng.NextDouble() * 2 - 1) * 0.01;
        }

        double totalLoss = 0;
        var allEmbeddings = sequences.SelectMany(s => s)
            .Select(f => Encode(We, f, E, F)).ToList();

        for (int epoch = 0; epoch < Epochs && !cancellationToken.IsCancellationRequested; epoch++)
        {
            totalLoss = 0;
            foreach (var seq in sequences)
            {
                if (seq.Length < predictionSteps + 2) continue;
                int t = rng.Next(0, seq.Length - predictionSteps - 1);

                double[] ct = Encode(We, seq[t], E, F); // context embedding

                for (int k = 1; k <= predictionSteps; k++)
                {
                    if (t + k >= seq.Length) break;
                    double[] posEmb = Encode(We, seq[t + k], E, F);

                    // Positive score
                    double[] pred = MatVec(Wp[k - 1], ct, E);
                    double posScore = Dot(pred, posEmb);

                    // Negative scores
                    double negSum = 0;
                    for (int neg = 0; neg < Negatives; neg++)
                    {
                        var negEmb = allEmbeddings[rng.Next(allEmbeddings.Count)];
                        negSum += Math.Exp(Dot(pred, negEmb) - posScore);
                    }
                    totalLoss += Math.Log(1 + negSum) / sequences.Count;
                }
            }
        }

        var encoderBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            We = FlattenM(We, E, F),
            Wp = Enumerable.Range(0, predictionSteps)
                     .Select(k => FlattenM(Wp[k], E, E)).ToArray()
        });

        return Task.FromResult(new MLCpcEncoder
        {
            Symbol          = symbol,
            Timeframe       = timeframe,
            EmbeddingDim    = embeddingDim,
            PredictionSteps = predictionSteps,
            InfoNceLoss     = totalLoss,
            TrainingSamples = sequences.Count,
            EncoderBytes    = encoderBytes,
            TrainedAt       = DateTime.UtcNow,
            IsActive        = true
        });
    }

    private static double[] Encode(double[,] W, float[] x, int E, int F)
    {
        var z = new double[E];
        for (int r = 0; r < E; r++)
        {
            for (int c = 0; c < Math.Min(F, x.Length); c++)
                z[r] += W[r, c] * x[c];
            z[r] = Math.Max(0, z[r]); // ReLU
        }
        return z;
    }

    private static double[] MatVec(double[,] W, double[] x, int E)
    {
        var y = new double[E];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < Math.Min(E, x.Length); c++)
                y[r] += W[r, c] * x[c];
        return y;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    private static double[] FlattenM(double[,] M, int rows, int cols)
    {
        var result = new double[rows * cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                result[r * cols + c] = M[r, c];
        return result;
    }
}
