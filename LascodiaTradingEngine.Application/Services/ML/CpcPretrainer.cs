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
///
/// <para>
/// Forward pass per step: <c>z = ReLU(W_e · x)</c>, <c>p = W_p[k-1] · z_t</c>,
/// <c>s+ = p · z_{t+k}</c>, <c>s-_j = p · z_{neg_j}</c>. Standard InfoNCE loss
/// <c>L = log(exp(s+) + Σ_j exp(s-_j)) − s+</c> is minimised by SGD with analytical
/// gradients through <c>W_p</c> and <c>W_e</c> (accumulated over context, positive and
/// negative contributions). Gradients are clipped at L2-norm 5.0 for stability.
/// </para>
/// </summary>
[RegisterService]
public sealed class CpcPretrainer : ICpcPretrainer
{
    private const int    Epochs        = 30;
    private const double Lr            = 0.001;
    private const int    Negatives     = 9;
    private const double GradClipNorm  = 5.0;

    /// <inheritdoc />
    public CpcEncoderType Kind => CpcEncoderType.Linear;

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

        // Encoder weights: E×F (He init — matches the existing CpcPretrainer initialisation
        // so tests that freeze the seed are not disturbed by the backprop addition).
        double[,] We = new double[E, F];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                We[r, c] = (rng.NextDouble() * 2 - 1) * Math.Sqrt(2.0 / F);

        // Prediction bilinear weights: one per prediction step (E×E).
        var Wp = new double[predictionSteps][,];
        for (int k = 0; k < predictionSteps; k++)
        {
            Wp[k] = new double[E, E];
            for (int r = 0; r < E; r++)
                for (int c = 0; c < E; c++)
                    Wp[k][r, c] = (rng.NextDouble() * 2 - 1) * 0.01;
        }

        double lastEpochLoss = 0.0;

        // Flattened view of all per-step feature rows — used as the negative pool. Resampled
        // each epoch so negatives reflect the currently-trained encoder (simple memory-bank
        // style: stale within an epoch, refreshed between epochs).
        var allRows = sequences.SelectMany(s => s).ToList();

        for (int epoch = 0; epoch < Epochs && !cancellationToken.IsCancellationRequested; epoch++)
        {
            // Refresh cached negative embeddings at the start of each epoch using current W_e.
            var allEmbeddings = allRows.Select(f => Encode(We, f, E, F)).ToList();

            double epochLoss = 0.0;
            int    sampleCount = 0;

            foreach (var seq in sequences)
            {
                if (seq.Length < predictionSteps + 2) continue;
                int t = rng.Next(0, seq.Length - predictionSteps - 1);
                var xt = seq[t];

                // Forward: context embedding c_t
                var ctEmb = Encode(We, xt, E, F);

                for (int k = 1; k <= predictionSteps; k++)
                {
                    if (t + k >= seq.Length) break;
                    var xPos = seq[t + k];
                    var posEmb = Encode(We, xPos, E, F);

                    // Predicted future: p = Wp[k-1] · c_t
                    var p = MatVec(Wp[k - 1], ctEmb, E);

                    // Positive score
                    double sPos = Dot(p, posEmb);

                    // Negative scores + indices (need indices to backprop through W_e).
                    var negIdx   = new int[Negatives];
                    var negEmbs  = new double[Negatives][];
                    var negRows  = new float[Negatives][];
                    var sNeg     = new double[Negatives];
                    for (int j = 0; j < Negatives; j++)
                    {
                        int idx = rng.Next(allEmbeddings.Count);
                        negIdx[j]  = idx;
                        negEmbs[j] = allEmbeddings[idx];
                        negRows[j] = allRows[idx];
                        sNeg[j]    = Dot(p, negEmbs[j]);
                    }

                    // Softmax probabilities with log-sum-exp for numerical stability.
                    double maxScore = sPos;
                    for (int j = 0; j < Negatives; j++) if (sNeg[j] > maxScore) maxScore = sNeg[j];
                    double expPos = Math.Exp(sPos - maxScore);
                    double sumExp = expPos;
                    var expNeg = new double[Negatives];
                    for (int j = 0; j < Negatives; j++)
                    {
                        expNeg[j] = Math.Exp(sNeg[j] - maxScore);
                        sumExp   += expNeg[j];
                    }
                    // L = log(sum_exp) + maxScore − sPos
                    double loss = Math.Log(sumExp) + maxScore - sPos;
                    epochLoss += loss;
                    sampleCount++;

                    double piPos = expPos / sumExp;
                    // Negative-score gradients (softmax weights).
                    var piNeg = new double[Negatives];
                    for (int j = 0; j < Negatives; j++) piNeg[j] = expNeg[j] / sumExp;

                    // dL/dp = (πPos − 1) · pos + Σ_j πNeg_j · negEmb_j
                    var dP = new double[E];
                    for (int r = 0; r < E; r++)
                        dP[r] = (piPos - 1.0) * posEmb[r];
                    for (int j = 0; j < Negatives; j++)
                    {
                        double w = piNeg[j];
                        var ne = negEmbs[j];
                        for (int r = 0; r < E; r++)
                            dP[r] += w * ne[r];
                    }

                    // dL/dW_p[k-1][r,c] = dP_r · c_t_c (outer product)
                    var dWp_k = new double[E, E];
                    for (int r = 0; r < E; r++)
                    {
                        double dr = dP[r];
                        for (int c = 0; c < E; c++)
                            dWp_k[r, c] = dr * ctEmb[c];
                    }

                    // dL/dc_t = Wp[k-1]^T · dP
                    var dCt = new double[E];
                    for (int c = 0; c < E; c++)
                    {
                        double sum = 0.0;
                        for (int r = 0; r < E; r++)
                            sum += Wp[k - 1][r, c] * dP[r];
                        dCt[c] = sum;
                    }

                    // dL/dpos = (πPos − 1) · p
                    var dPos = new double[E];
                    for (int r = 0; r < E; r++)
                        dPos[r] = (piPos - 1.0) * p[r];

                    // dL/dneg_j = πNeg_j · p
                    var dNegArr = new double[Negatives][];
                    for (int j = 0; j < Negatives; j++)
                    {
                        var dN = new double[E];
                        double w = piNeg[j];
                        for (int r = 0; r < E; r++) dN[r] = w * p[r];
                        dNegArr[j] = dN;
                    }

                    // Backprop through ReLU + W_e — three contributions (ctx, pos, negs) all
                    // accumulate into a single dW_e. Mask gradient by post-ReLU positivity
                    // (z > 0 ⇒ pass-through, z == 0 ⇒ zero).
                    var dWe = new double[E, F];
                    AccumulateWeGrad(dWe, ctEmb, xt, dCt, E, F);
                    AccumulateWeGrad(dWe, posEmb, xPos, dPos, E, F);
                    for (int j = 0; j < Negatives; j++)
                        AccumulateWeGrad(dWe, negEmbs[j], negRows[j], dNegArr[j], E, F);

                    ClipAndApply(We, dWe, Wp[k - 1], dWp_k, E, F, Lr, GradClipNorm);
                }
            }

            lastEpochLoss = sampleCount > 0 ? epochLoss / sampleCount : 0.0;
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
            EncoderType     = CpcEncoderType.Linear,
            EmbeddingDim    = embeddingDim,
            PredictionSteps = predictionSteps,
            InfoNceLoss     = lastEpochLoss,
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

    /// <summary>
    /// Adds the contribution of one embedding to <paramref name="dWe"/>: for each row r where
    /// the post-ReLU activation is positive, the gradient <c>dL/dW_e[r, c] += dEmb_r · x_c</c>
    /// (ReLU-masked, using input row length limit).
    /// </summary>
    private static void AccumulateWeGrad(
        double[,] dWe,
        double[] postRelu,
        float[] x,
        double[] dEmb,
        int E, int F)
    {
        int limit = Math.Min(F, x.Length);
        for (int r = 0; r < E; r++)
        {
            if (postRelu[r] <= 0.0) continue; // ReLU gate
            double dr = dEmb[r];
            if (dr == 0.0) continue;
            for (int c = 0; c < limit; c++)
                dWe[r, c] += dr * x[c];
        }
    }

    /// <summary>
    /// Computes the joint L2 norm of <paramref name="dWe"/> and <paramref name="dWp_k"/>,
    /// rescales both if above <paramref name="clip"/>, then applies a step of SGD:
    /// <c>W ← W − lr · dW</c>.
    /// </summary>
    private static void ClipAndApply(
        double[,] We, double[,] dWe,
        double[,] Wp_k, double[,] dWp_k,
        int E, int F, double lr, double clip)
    {
        double sumSq = 0.0;
        for (int r = 0; r < E; r++)
        for (int c = 0; c < F; c++) sumSq += dWe[r, c] * dWe[r, c];
        for (int r = 0; r < E; r++)
        for (int c = 0; c < E; c++) sumSq += dWp_k[r, c] * dWp_k[r, c];

        double norm = Math.Sqrt(sumSq);
        double scale = norm > clip ? clip / norm : 1.0;

        double eff = lr * scale;
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                We[r, c] -= eff * dWe[r, c];

        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++)
                Wp_k[r, c] -= eff * dWp_k[r, c];
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
