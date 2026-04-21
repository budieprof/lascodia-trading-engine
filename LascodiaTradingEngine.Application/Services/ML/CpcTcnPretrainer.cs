using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using System.Text.Json;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Temporal Convolutional Network (TCN) pre-trainer for CPC. Two causal 1-D convolution
/// layers with dilation <c>1</c> and <c>2</c> and kernel <c>K=3</c>, separated by ReLU,
/// plus a 1×1 residual projection from the input. Each output <c>z[t]</c> fuses a
/// 7-timestep receptive field of past context with the current raw row — a strict
/// generalisation of <see cref="CpcPretrainer"/>'s single-step linear encoder.
///
/// <para>
/// Training optimises InfoNCE with analytical SGD. Gradients flow through the context
/// embedding <c>z[t]</c> back into <c>W_1</c>, <c>W_2</c>, <c>W_r</c>; positives and
/// negatives use stop-gradient (their embeddings are cached per epoch and treated as
/// constants in the backward pass). This is the standard MoCo/BYOL-style simplification,
/// keeps each sample's backward cost O(K·(E·F + E·E)), and produces an encoder whose
/// projection at inference time is bit-reproducible.
/// </para>
///
/// <para>
/// Payload shape under <see cref="MLCpcEncoder.EncoderBytes"/>:
/// <c>{ Type, E, F, K, W1 (E·F·K), W2 (E·E·K), Wr (E·F), Wp (steps·E·E) }</c>.
/// </para>
/// </summary>
[RegisterService]
public sealed class CpcTcnPretrainer : ICpcPretrainer
{
    private const int    Epochs       = 30;
    private const double Lr           = 0.001;
    private const int    Negatives    = 9;
    private const int    Kernel       = 3;          // K
    private const int    Dilation1    = 1;
    private const int    Dilation2    = 2;
    private const double GradClipNorm = 5.0;

    /// <inheritdoc />
    public CpcEncoderType Kind => CpcEncoderType.Tcn;

    /// <inheritdoc />
    public Task<MLCpcEncoder> TrainAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<float[][]> sequences,
        int embeddingDim,
        int predictionSteps,
        CancellationToken cancellationToken)
    {
        if (sequences.Count == 0 || sequences[0].Length == 0)
        {
            return Task.FromResult(new MLCpcEncoder
            {
                Symbol = symbol, Timeframe = timeframe,
                EncoderType = CpcEncoderType.Tcn,
                EmbeddingDim = embeddingDim, PredictionSteps = predictionSteps,
                TrainedAt = DateTime.UtcNow
            });
        }

        int F = sequences[0][0].Length;
        int E = embeddingDim;
        int K = Kernel;
        var rng = new Random(42);

        // ── He-style uniform init for ReLU network ─────────────────────────
        double scale1 = Math.Sqrt(2.0 / Math.Max(1, F * K));
        double scale2 = Math.Sqrt(2.0 / Math.Max(1, E * K));
        double scaleR = Math.Sqrt(2.0 / Math.Max(1, F));

        double[,,] W1 = new double[E, F, K];
        double[,,] W2 = new double[E, E, K];
        double[,]  Wr = new double[E, F];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                for (int k = 0; k < K; k++)
                    W1[r, c, k] = (rng.NextDouble() * 2 - 1) * scale1;
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++)
                for (int k = 0; k < K; k++)
                    W2[r, c, k] = (rng.NextDouble() * 2 - 1) * scale2;
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                Wr[r, c] = (rng.NextDouble() * 2 - 1) * scaleR;

        var Wp = new double[predictionSteps][,];
        for (int k = 0; k < predictionSteps; k++)
        {
            Wp[k] = new double[E, E];
            for (int r = 0; r < E; r++)
                for (int c = 0; c < E; c++)
                    Wp[k][r, c] = (rng.NextDouble() * 2 - 1) * 0.01;
        }

        double lastEpochLoss = 0.0;

        for (int epoch = 0; epoch < Epochs && !cancellationToken.IsCancellationRequested; epoch++)
        {
            // Stop-gradient negative/positive pool: encode every sequence once per epoch with
            // the current weights and hold the output as a constant for this epoch's inner loop.
            var epochEmbeddings = sequences.Select(s => ForwardSequenceOutput(W1, W2, Wr, s, E, F, K)).ToList();

            double epochLoss = 0.0;
            int    sampleCount = 0;

            for (int sIdx = 0; sIdx < sequences.Count; sIdx++)
            {
                var seq = sequences[sIdx];
                if (seq.Length < predictionSteps + 2) continue;

                int t = rng.Next(0, seq.Length - predictionSteps - 1);

                // Context — real forward with caches so we can backprop.
                var cache = ForwardSequenceWithCache(W1, W2, Wr, seq, E, F, K);
                var ctEmb = cache.Z[t];

                for (int k = 1; k <= predictionSteps; k++)
                {
                    if (t + k >= seq.Length) break;

                    // Positive and negatives use the cached per-epoch embeddings (stop-gradient).
                    var posEmb = epochEmbeddings[sIdx][t + k];

                    var negIdx   = new (int Seq, int Step)[Negatives];
                    var negEmbs  = new double[Negatives][];
                    var sNeg     = new double[Negatives];

                    var p = MatVec(Wp[k - 1], ctEmb, E);
                    double sPos = Dot(p, posEmb);

                    for (int j = 0; j < Negatives; j++)
                    {
                        int ns = rng.Next(epochEmbeddings.Count);
                        var list = epochEmbeddings[ns];
                        int nt = rng.Next(list.Length);
                        negIdx[j]  = (ns, nt);
                        negEmbs[j] = list[nt];
                        sNeg[j]    = Dot(p, negEmbs[j]);
                    }

                    // log-sum-exp stabilised softmax
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
                    double loss = Math.Log(sumExp) + maxScore - sPos;
                    epochLoss += loss;
                    sampleCount++;

                    double piPos = expPos / sumExp;

                    // dL/dp = (πPos − 1) · pos + Σ πNeg_j · neg_j    — stop-grad on pos/neg
                    var dP = new double[E];
                    for (int r = 0; r < E; r++)
                        dP[r] = (piPos - 1.0) * posEmb[r];
                    for (int j = 0; j < Negatives; j++)
                    {
                        double w = expNeg[j] / sumExp;
                        var ne = negEmbs[j];
                        for (int r = 0; r < E; r++)
                            dP[r] += w * ne[r];
                    }

                    // dL/dWp[k-1][r,c] = dP_r · ctEmb_c
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

                    // Backprop dCt through the TCN at context timestep t, get (dW1, dW2, dWr).
                    var (dW1, dW2, dWr) = BackwardAtTimestep(
                        cache, seq, t, dCt, W1, W2, E, F, K);

                    ClipAndApply(W1, dW1, W2, dW2, Wr, dWr, Wp[k - 1], dWp_k, E, F, K, Lr, GradClipNorm);
                }
            }

            lastEpochLoss = sampleCount > 0 ? epochLoss / sampleCount : 0.0;
        }

        var encoderBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Type = "tcn",
            E, F, K,
            W1 = Flatten3(W1, E, F, K),
            W2 = Flatten3(W2, E, E, K),
            Wr = Flatten2(Wr, E, F),
            Wp = Enumerable.Range(0, predictionSteps)
                     .Select(k => Flatten2(Wp[k], E, E)).ToArray()
        });

        return Task.FromResult(new MLCpcEncoder
        {
            Symbol          = symbol,
            Timeframe       = timeframe,
            EncoderType     = CpcEncoderType.Tcn,
            EmbeddingDim    = embeddingDim,
            PredictionSteps = predictionSteps,
            InfoNceLoss     = lastEpochLoss,
            TrainingSamples = sequences.Count,
            EncoderBytes    = encoderBytes,
            TrainedAt       = DateTime.UtcNow,
            IsActive        = true,
        });
    }

    // ── Forward helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Cheap forward pass that returns only the per-timestep output embeddings — used to
    /// build the stop-gradient negative / positive pool once per epoch.
    /// </summary>
    internal static double[][] ForwardSequenceOutput(
        double[,,] W1, double[,,] W2, double[,] Wr,
        float[][] seq, int E, int F, int K)
    {
        int T = seq.Length;

        // Layer 1 with dilation Dilation1.
        var z1 = new double[T][];
        for (int t = 0; t < T; t++)
        {
            var a1 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * Dilation1;
                if (srcT < 0) continue;
                var x = seq[srcT];
                int limit = Math.Min(F, x.Length);
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < limit; c++)
                        s += W1[r, c, k] * x[c];
                    a1[r] += s;
                }
            }
            var z = new double[E];
            for (int r = 0; r < E; r++) z[r] = a1[r] > 0.0 ? a1[r] : 0.0;
            z1[t] = z;
        }

        // Layer 2 with dilation Dilation2.
        var output = new double[T][];
        for (int t = 0; t < T; t++)
        {
            var a2 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * Dilation2;
                if (srcT < 0) continue;
                var z1Row = z1[srcT];
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < E; c++)
                        s += W2[r, c, k] * z1Row[c];
                    a2[r] += s;
                }
            }

            var z = new double[E];
            for (int r = 0; r < E; r++) z[r] = a2[r] > 0.0 ? a2[r] : 0.0;

            // Residual 1×1 conv from the current raw step.
            var x = seq[t];
            int limitR = Math.Min(F, x.Length);
            for (int r = 0; r < E; r++)
            {
                double zr = 0.0;
                for (int c = 0; c < limitR; c++)
                    zr += Wr[r, c] * x[c];
                z[r] += zr;
            }

            output[t] = z;
        }
        return output;
    }

    /// <summary>
    /// Forward pass that retains the intermediate activations (<c>a1, z1, a2, z2, zr</c>) for
    /// the selected sequence so the backward pass can propagate gradients through ReLU masks
    /// and the two conv layers without recomputing.
    /// </summary>
    internal static TcnForwardCache ForwardSequenceWithCache(
        double[,,] W1, double[,,] W2, double[,] Wr,
        float[][] seq, int E, int F, int K)
    {
        int T = seq.Length;
        var a1 = new double[T][];
        var z1 = new double[T][];
        var a2 = new double[T][];
        var z2 = new double[T][];
        var zr = new double[T][];
        var z  = new double[T][];

        for (int t = 0; t < T; t++)
        {
            var row1 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * Dilation1;
                if (srcT < 0) continue;
                var x = seq[srcT];
                int limit = Math.Min(F, x.Length);
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < limit; c++)
                        s += W1[r, c, k] * x[c];
                    row1[r] += s;
                }
            }
            a1[t] = row1;
            var zt1 = new double[E];
            for (int r = 0; r < E; r++) zt1[r] = row1[r] > 0.0 ? row1[r] : 0.0;
            z1[t] = zt1;
        }

        for (int t = 0; t < T; t++)
        {
            var row2 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * Dilation2;
                if (srcT < 0) continue;
                var z1Row = z1[srcT];
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < E; c++)
                        s += W2[r, c, k] * z1Row[c];
                    row2[r] += s;
                }
            }
            a2[t] = row2;
            var zt2 = new double[E];
            for (int r = 0; r < E; r++) zt2[r] = row2[r] > 0.0 ? row2[r] : 0.0;
            z2[t] = zt2;

            var x = seq[t];
            int limitR = Math.Min(F, x.Length);
            var rowR = new double[E];
            for (int r = 0; r < E; r++)
            {
                double v = 0.0;
                for (int c = 0; c < limitR; c++)
                    v += Wr[r, c] * x[c];
                rowR[r] = v;
            }
            zr[t] = rowR;

            var zt = new double[E];
            for (int r = 0; r < E; r++) zt[r] = zt2[r] + rowR[r];
            z[t] = zt;
        }

        return new TcnForwardCache(a1, z1, a2, z2, zr, z);
    }

    /// <summary>
    /// Backpropagate <paramref name="dCt"/> = dL/dz[tContext] through the cached forward pass
    /// and accumulate gradients on the three weight tensors. Only the ReLU-active positions
    /// and the (dilation-aware) receptive field around <paramref name="tContext"/> contribute.
    /// </summary>
    internal static (double[,,] DW1, double[,,] DW2, double[,] DWr) BackwardAtTimestep(
        TcnForwardCache cache,
        float[][] seq,
        int tContext,
        double[] dCt,
        double[,,] W1, double[,,] W2,
        int E, int F, int K)
    {
        var dW1 = new double[E, F, K];
        var dW2 = new double[E, E, K];
        var dWr = new double[E, F];

        // z[tContext] = z2[tContext] + zr[tContext] → dz2 = dzr = dCt.
        var da2  = new double[E];
        var dz2  = dCt;           // alias — we don't need the separate array
        var dzr  = dCt;

        for (int r = 0; r < E; r++)
            da2[r] = cache.A2[tContext][r] > 0.0 ? dz2[r] : 0.0;

        // dWr[r, c] += dzr[r] * x[tContext, c]
        var xCtx = seq[tContext];
        int limitR = Math.Min(F, xCtx.Length);
        for (int r = 0; r < E; r++)
        {
            double drVal = dzr[r];
            if (drVal == 0.0) continue;
            for (int c = 0; c < limitR; c++)
                dWr[r, c] += drVal * xCtx[c];
        }

        // Layer 2: sum over k of dW2[r, c, k] += da2[r] · z1[tContext − k·d2, c]
        //          and accumulate dz1[tContext − k·d2, c] for use by layer-1 backprop.
        // dz1 lives at up to K distinct upstream timesteps.
        var dz1ByT = new Dictionary<int, double[]>();
        for (int k = 0; k < K; k++)
        {
            int srcT = tContext - k * Dilation2;
            if (srcT < 0) continue;
            var z1Row = cache.Z1[srcT];
            for (int r = 0; r < E; r++)
            {
                double dar = da2[r];
                if (dar == 0.0) continue;
                for (int c = 0; c < E; c++)
                    dW2[r, c, k] += dar * z1Row[c];
            }

            if (!dz1ByT.TryGetValue(srcT, out var acc))
            {
                acc = new double[E];
                dz1ByT[srcT] = acc;
            }
            for (int c = 0; c < E; c++)
            {
                double s = 0.0;
                for (int r = 0; r < E; r++)
                    s += W2[r, c, k] * da2[r];
                acc[c] += s;
            }
        }

        // Layer 1: for each tPrime with nonzero dz1[tPrime], apply ReLU mask and accumulate dW1.
        foreach (var kv in dz1ByT)
        {
            int tPrime = kv.Key;
            var dz1Row = kv.Value;
            var a1Row  = cache.A1[tPrime];
            var da1    = new double[E];
            for (int r = 0; r < E; r++)
                da1[r] = a1Row[r] > 0.0 ? dz1Row[r] : 0.0;

            for (int k = 0; k < K; k++)
            {
                int srcT = tPrime - k * Dilation1;
                if (srcT < 0) continue;
                var x = seq[srcT];
                int limit = Math.Min(F, x.Length);
                for (int r = 0; r < E; r++)
                {
                    double dar = da1[r];
                    if (dar == 0.0) continue;
                    for (int c = 0; c < limit; c++)
                        dW1[r, c, k] += dar * x[c];
                }
            }
        }

        return (dW1, dW2, dWr);
    }

    /// <summary>
    /// Jointly clip the four gradient tensors at L2 norm <paramref name="clip"/>, then
    /// apply an SGD step at learning rate <paramref name="lr"/>: <c>W ← W − lr · dW</c>.
    /// </summary>
    private static void ClipAndApply(
        double[,,] W1, double[,,] dW1,
        double[,,] W2, double[,,] dW2,
        double[,]  Wr, double[,]  dWr,
        double[,]  Wp_k, double[,] dWp_k,
        int E, int F, int K, double lr, double clip)
    {
        double sumSq = 0.0;
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                for (int k = 0; k < K; k++) sumSq += dW1[r, c, k] * dW1[r, c, k];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++)
                for (int k = 0; k < K; k++) sumSq += dW2[r, c, k] * dW2[r, c, k];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++) sumSq += dWr[r, c] * dWr[r, c];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++) sumSq += dWp_k[r, c] * dWp_k[r, c];

        double norm  = Math.Sqrt(sumSq);
        double scale = norm > clip ? clip / norm : 1.0;
        double eff   = lr * scale;

        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++)
                for (int k = 0; k < K; k++) W1[r, c, k] -= eff * dW1[r, c, k];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++)
                for (int k = 0; k < K; k++) W2[r, c, k] -= eff * dW2[r, c, k];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < F; c++) Wr[r, c] -= eff * dWr[r, c];
        for (int r = 0; r < E; r++)
            for (int c = 0; c < E; c++) Wp_k[r, c] -= eff * dWp_k[r, c];
    }

    // ── Small math helpers ───────────────────────────────────────────────────

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

    private static double[] Flatten3(double[,,] M, int D0, int D1, int D2)
    {
        var result = new double[D0 * D1 * D2];
        for (int i = 0; i < D0; i++)
            for (int j = 0; j < D1; j++)
                for (int k = 0; k < D2; k++)
                    result[(i * D1 + j) * D2 + k] = M[i, j, k];
        return result;
    }

    private static double[] Flatten2(double[,] M, int D0, int D1)
    {
        var result = new double[D0 * D1];
        for (int i = 0; i < D0; i++)
            for (int j = 0; j < D1; j++)
                result[i * D1 + j] = M[i, j];
        return result;
    }

    /// <summary>
    /// Cached forward-pass intermediates used by <see cref="BackwardAtTimestep"/>.
    /// All fields are indexed by timestep; <c>A2[t]</c> and <c>Z1[t]</c> are retained so the
    /// backward pass can ReLU-mask correctly and re-use layer-1 outputs without recomputation.
    /// </summary>
    internal sealed record TcnForwardCache(
        double[][] A1,
        double[][] Z1,
        double[][] A2,
        double[][] Z2,
        double[][] Zr,
        double[][] Z);
}
