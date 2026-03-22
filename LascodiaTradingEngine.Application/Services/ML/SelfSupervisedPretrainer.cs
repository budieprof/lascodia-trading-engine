using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Masked-candle autoencoder pre-trainer. Randomly masks 20% of input feature slots
/// and trains an encoder-decoder to reconstruct them. The encoder weights are returned
/// as a <see cref="PretrainingSnapshot"/> for warm-starting supervised learners.
/// </summary>
/// <remarks>
/// Architecture:
///   Input (F features) → Encoder (F → hiddenDim via ReLU dense layer)
///                      → Bottleneck (hiddenDim)
///                      → Decoder  (hiddenDim → F via linear layer)
/// Loss: MSE on masked features only (reconstructive masked modelling, analogous to BERT).
/// Training: Adam-style gradient descent with weight decay.
/// On completion, the encoder weight matrix and reconstruction loss are returned.
/// </remarks>
[RegisterService]
public class SelfSupervisedPretrainer : ISelfSupervisedPretrainer
{
    private readonly ILogger<SelfSupervisedPretrainer> _logger;

    public SelfSupervisedPretrainer(ILogger<SelfSupervisedPretrainer> logger)
    {
        _logger = logger;
    }

    public async Task<PretrainingSnapshot> PretrainAsync(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        double               maskFraction = 0.20,
        CancellationToken    ct           = default)
    {
        await Task.Yield(); // allow cancellation check before CPU-bound work

        int F         = samples[0].Features.Length;
        int hiddenDim = Math.Max(8, F / 2);
        int epochs    = Math.Min(hp.MaxEpochs, 50);
        int n         = samples.Count;

        var rng = new Random(42);

        // ── Initialise encoder weights W_enc [hiddenDim × F] ─────────────────
        float[][] wEnc = InitWeights(hiddenDim, F, rng);
        float[]   bEnc = new float[hiddenDim];

        // ── Initialise decoder weights W_dec [F × hiddenDim] ─────────────────
        float[][] wDec = InitWeights(F, hiddenDim, rng);
        float[]   bDec = new float[F];

        // Adam state
        float[][] mEnc = ZeroLike(wEnc), vEnc = ZeroLike(wEnc);
        float[][] mDec = ZeroLike(wDec), vDec = ZeroLike(wDec);

        double learningRate  = hp.LearningRate;
        double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;
        int    step  = 0;

        var lossCurve = new List<double>();

        for (int epoch = 0; epoch < epochs && !ct.IsCancellationRequested; epoch++)
        {
            double epochLoss = 0;

            // Shuffle
            var idx = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToArray();

            foreach (int i in idx)
            {
                var x = samples[i].Features;

                // Create mask: 1 = masked (reconstruct), 0 = visible (input kept)
                bool[] mask = new bool[F];
                int maskCount = Math.Max(1, (int)(F * maskFraction));
                var maskIndices = Enumerable.Range(0, F).OrderBy(_ => rng.Next()).Take(maskCount).ToHashSet();
                for (int fi = 0; fi < F; fi++) mask[fi] = maskIndices.Contains(fi);

                // Masked input: set masked positions to 0
                float[] xMasked = (float[])x.Clone();
                for (int fi = 0; fi < F; fi++) if (mask[fi]) xMasked[fi] = 0f;

                // Forward: encoder → ReLU → decoder
                float[] hidden  = DenseReLU(xMasked, wEnc, bEnc, hiddenDim, F);
                float[] recon   = DenseLinear(hidden, wDec, bDec, F, hiddenDim);

                // Loss: MSE on masked positions only
                double loss = 0;
                float[] dRecon = new float[F];
                for (int fi = 0; fi < F; fi++)
                {
                    if (!mask[fi]) continue;
                    float err  = recon[fi] - x[fi];
                    loss       += err * err;
                    dRecon[fi]  = 2f * err / maskCount;
                }
                epochLoss += loss / maskCount;

                // Backward: decoder gradients
                step++;
                BackwardStep(dRecon, hidden, wDec, mDec, vDec, learningRate, beta1, beta2, eps, step);

                // Backward through decoder to hidden: dHidden = W_dec^T × dRecon (linear)
                float[] dHidden = new float[hiddenDim];
                for (int h = 0; h < hiddenDim; h++)
                    for (int fi = 0; fi < F; fi++)
                        dHidden[h] += wDec[fi][h] * dRecon[fi];

                // ReLU backward
                for (int h = 0; h < hiddenDim; h++)
                    if (hidden[h] <= 0) dHidden[h] = 0;

                BackwardStep(dHidden, xMasked, wEnc, mEnc, vEnc, learningRate, beta1, beta2, eps, step);
            }

            double avgLoss = epochLoss / n;
            lossCurve.Add(avgLoss);

            if (epoch % 10 == 0)
                _logger.LogDebug("Pre-training epoch {Epoch}/{Total}: loss={Loss:F6}", epoch, epochs, avgLoss);
        }

        // ── Feature importance by reconstruction difficulty ───────────────────
        float[] importance = ComputeFeatureImportance(samples, wEnc, bEnc, wDec, bDec, hiddenDim, F, rng);

        double finalLoss = lossCurve.Count > 0 ? lossCurve[^1] : double.MaxValue;

        _logger.LogInformation(
            "Self-supervised pre-training complete: {N} samples, {Epochs} epochs, final loss={Loss:F6}",
            n, epochs, finalLoss);

        return new PretrainingSnapshot(
            EncoderWeights:                     wEnc,
            HiddenDim:                          hiddenDim,
            ReconstructionLoss:                 finalLoss,
            FeatureImportanceByReconstruction:  importance,
            TrainedAt:                          DateTime.UtcNow);
    }

    // ── Neural network primitives ─────────────────────────────────────────────

    private static float[] DenseReLU(float[] x, float[][] w, float[] b, int outDim, int inDim)
    {
        var out_ = new float[outDim];
        for (int o = 0; o < outDim; o++)
        {
            float sum = b[o];
            for (int i = 0; i < inDim; i++) sum += w[o][i] * x[i];
            out_[o] = Math.Max(0f, sum);
        }
        return out_;
    }

    private static float[] DenseLinear(float[] x, float[][] w, float[] b, int outDim, int inDim)
    {
        var out_ = new float[outDim];
        for (int o = 0; o < outDim; o++)
        {
            float sum = b[o];
            for (int i = 0; i < inDim; i++) sum += w[o][i] * x[i];
            out_[o] = sum;
        }
        return out_;
    }

    private static void BackwardStep(
        float[] dOut, float[] input, float[][] w,
        float[][] m, float[][] v,
        double lr, double beta1, double beta2, double eps, int t)
    {
        double bc1 = 1 - Math.Pow(beta1, t);
        double bc2 = 1 - Math.Pow(beta2, t);

        for (int o = 0; o < w.Length; o++)
            for (int i = 0; i < w[o].Length; i++)
            {
                float g = dOut[o] * input[i];
                m[o][i] = (float)(beta1 * m[o][i] + (1 - beta1) * g);
                v[o][i] = (float)(beta2 * v[o][i] + (1 - beta2) * g * g);
                double mHat = m[o][i] / bc1;
                double vHat = v[o][i] / bc2;
                w[o][i] -= (float)(lr * mHat / (Math.Sqrt(vHat) + eps));
            }
    }

    private static float[][] InitWeights(int outDim, int inDim, Random rng)
    {
        double scale = Math.Sqrt(2.0 / inDim);
        var w = new float[outDim][];
        for (int o = 0; o < outDim; o++)
        {
            w[o] = new float[inDim];
            for (int i = 0; i < inDim; i++)
                w[o][i] = (float)((rng.NextDouble() * 2 - 1) * scale);
        }
        return w;
    }

    private static float[][] ZeroLike(float[][] w)
    {
        var z = new float[w.Length][];
        for (int i = 0; i < w.Length; i++) z[i] = new float[w[i].Length];
        return z;
    }

    private static float[] ComputeFeatureImportance(
        List<TrainingSample> samples, float[][] wEnc, float[] bEnc,
        float[][] wDec, float[] bDec, int hiddenDim, int F, Random rng)
    {
        float[] importance = new float[F];
        int subset = Math.Min(200, samples.Count);

        for (int fi = 0; fi < F; fi++)
        {
            double sumMse = 0;
            int count = 0;
            for (int si = 0; si < subset; si++)
            {
                var x = samples[si].Features;
                float[] xMasked = (float[])x.Clone();
                xMasked[fi] = 0f; // mask only feature fi

                float[] hidden = DenseReLU(xMasked, wEnc, bEnc, hiddenDim, F);
                float[] recon  = DenseLinear(hidden, wDec, bDec, F, hiddenDim);

                float err = recon[fi] - x[fi];
                sumMse += err * err;
                count++;
            }
            importance[fi] = count > 0 ? (float)(sumMse / count) : 0f;
        }
        return importance;
    }
}
