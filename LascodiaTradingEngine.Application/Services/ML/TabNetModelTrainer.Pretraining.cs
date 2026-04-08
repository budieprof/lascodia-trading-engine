using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  UNSUPERVISED PRE-TRAINING
    //  Encoder-decoder: mask random features, reconstruct via decoder FC.
    //  Full backpropagation through encoder (shared + step + attention FC).
    // ═══════════════════════════════════════════════════════════════════════

    private TabNetWeights RunUnsupervisedPretraining(
        List<TrainingSample> samples, int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax,
        bool useGlu, double lr, int epochs, double maskFraction, double bnMomentum, CancellationToken ct)
    {
        var w = InitializeWeights(F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, false);

        var rng = new Random(TrainerSeed);
        var decoderW = XavierMatrix(rng, F, hiddenDim);
        var decoderB = new double[F];

        // Decoder gradient accumulators (matched to encoder accumulation pattern)
        var decoderWGrad = new double[F][];
        for (int j = 0; j < F; j++) decoderWGrad[j] = new double[hiddenDim];
        var decoderBGrad = new double[F];

        var priorBuf = new double[F];
        var attnBuf  = new double[F];

        // Adam state + gradient accumulator for encoder
        var adam = InitializeAdamState(w);
        var grad = InitializeGradAccumulator(w);
        var bwd  = BackwardBuffers.Allocate(F, hiddenDim);
        int batchCount = 0;

        for (int ep = 0; ep < epochs && !ct.IsCancellationRequested; ep++)
        {
            double cosLr = lr * 0.5 * (1.0 + Math.Cos(Math.PI * ep / epochs));
            double epochLoss = 0;

            foreach (var sample in samples)
            {
                var mask = new bool[F];
                int masked = 0;
                for (int j = 0; j < F; j++)
                {
                    mask[j] = rng.NextDouble() < maskFraction;
                    if (mask[j]) masked++;
                }
                if (masked == 0) continue;

                var maskedFeatures = new float[F];
                for (int j = 0; j < F; j++)
                    maskedFeatures[j] = mask[j] ? 0f : sample.Features[j];

                var fwd = ForwardPass(maskedFeatures, w, priorBuf, attnBuf, true, 0, rng);

                // Decode: reconstruct from aggregated hidden
                var recon = new double[F];
                for (int j = 0; j < F; j++)
                {
                    recon[j] = decoderB[j];
                    for (int k = 0; k < hiddenDim; k++)
                        recon[j] += decoderW[j][k] * fwd.AggregatedH[k];
                }

                // Compute ∂L/∂AggregatedH from reconstruction MSE on masked features
                // Accumulate decoder gradients (applied at batch boundary, matching encoder)
                var dAggH = new double[hiddenDim];
                for (int j = 0; j < F; j++)
                {
                    if (!mask[j]) continue;
                    double err = recon[j] - sample.Features[j];
                    epochLoss += err * err;
                    double dRecon = 2.0 * err / masked;

                    decoderBGrad[j] += dRecon;
                    for (int k = 0; k < hiddenDim; k++)
                    {
                        dAggH[k] += dRecon * decoderW[j][k];
                        decoderWGrad[j][k] += dRecon * fwd.AggregatedH[k];
                    }
                }

                // Backprop through encoder: dAggH → per-step ReLU → layers → attention
                BackpropPretrainEncoder(grad, w, fwd, maskedFeatures, dAggH, hiddenDim, F, bwd);

                batchCount++;
                if (batchCount >= DefaultBatchSize)
                {
                    double invBatch = 1.0 / batchCount;
                    ScaleGradients(grad, invBatch);
                    ClipGradients(grad, 1.0);
                    AdamUpdate(w, grad, adam, cosLr);
                    ZeroGradients(grad);

                    // Apply accumulated decoder gradients at same batch boundary
                    for (int j = 0; j < F; j++)
                    {
                        decoderB[j] -= cosLr * decoderBGrad[j] * invBatch;
                        decoderB[j] = Math.Clamp(decoderB[j], -MaxWeightVal, MaxWeightVal);
                        for (int k = 0; k < hiddenDim; k++)
                        {
                            decoderW[j][k] -= cosLr * decoderWGrad[j][k] * invBatch;
                            decoderW[j][k] = Math.Clamp(decoderW[j][k], -MaxWeightVal, MaxWeightVal);
                        }
                        decoderBGrad[j] = 0;
                        Array.Clear(decoderWGrad[j]);
                    }

                    batchCount = 0;
                }
            }

            if (batchCount > 0)
            {
                double invBatch = 1.0 / batchCount;
                ScaleGradients(grad, invBatch);
                ClipGradients(grad, 1.0);
                AdamUpdate(w, grad, adam, cosLr);
                ZeroGradients(grad);

                // Flush remaining decoder gradients
                for (int j = 0; j < F; j++)
                {
                    decoderB[j] -= cosLr * decoderBGrad[j] * invBatch;
                    decoderB[j] = Math.Clamp(decoderB[j], -MaxWeightVal, MaxWeightVal);
                    for (int k = 0; k < hiddenDim; k++)
                    {
                        decoderW[j][k] -= cosLr * decoderWGrad[j][k] * invBatch;
                        decoderW[j][k] = Math.Clamp(decoderW[j][k], -MaxWeightVal, MaxWeightVal);
                    }
                    decoderBGrad[j] = 0;
                    Array.Clear(decoderWGrad[j]);
                }

                batchCount = 0;
            }

            // Update BN running stats every 2 epochs for fresher statistics
            if (ep % 2 == 1)
            {
                var (epochMeans, epochVars) = ComputeEpochBatchStats(
                    w, samples, 128, DefaultMinCalibrationSamples, rng);
                ApplyBnRunningStatsEma(w, epochMeans, epochVars, bnMomentum);
            }
        }

        return w;
    }

    private static void BackpropPretrainEncoder(
        TabNetWeights grad, TabNetWeights w, ForwardResult fwd,
        float[] features, double[] dAggH, int H, int F, BackwardBuffers bwd)
    {
        var dFutureStep = bwd.DFutureStep;
        Array.Clear(dFutureStep, 0, H);

        for (int s = w.NSteps - 1; s >= 0; s--)
        {
            var dH = new double[H];
            for (int j = 0; j < H; j++)
                dH[j] = dFutureStep[j] + (fwd.StepH[s][j] > 0 ? dAggH[j] : 0.0);

            // Step-specific layers backward
            double[] dInput = dH;
            for (int l = w.StepLayers - 1; l >= 0; l--)
            {
                double[] pre  = fwd.StepStepPre[s][l];
                double[] gate = fwd.StepStepGate[s][l];
                double[] xn   = fwd.StepStepXNorm[s][l];
                double[] fcIn = fwd.StepStepFcIn[s][l];
                int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;

                double[] dResidual = null!;
                if (l > 0)
                {
                    dResidual = new double[H];
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * SqrtHalfResidualScale;
                        dInput[j]    = dInput[j] * SqrtHalfResidualScale;
                    }
                }

                var dBnOut  = new double[H];
                var dGateIn = new double[H];
                if (w.UseGlu)
                {
                    for (int j = 0; j < H; j++)
                    {
                        dBnOut[j]  = dInput[j] * gate[j];
                        dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                    }
                }
                else
                {
                    Array.Copy(dInput, dBnOut, H);
                }

                // Full BN backward
                double meanDy = 0, meanDyXn = 0;
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnStIdx][j] += dBnOut[j] * xn[j];
                    grad.BnBeta[bnStIdx][j]  += dBnOut[j];
                    meanDy   += dBnOut[j];
                    meanDyXn += dBnOut[j] * xn[j];
                }
                meanDy /= H; meanDyXn /= H;

                var dPreFc = new double[H];
                for (int j = 0; j < H; j++)
                {
                    double var_ = w.BnVar[bnStIdx].Length > j ? w.BnVar[bnStIdx][j] : 1.0;
                    double invStd = Math.Min(1.0 / Math.Sqrt(Math.Max(0, var_) + BnEpsilon), MaxInvStd);
                    dPreFc[j] = w.BnGamma[bnStIdx][j] * invStd * (dBnOut[j] - meanDy - xn[j] * meanDyXn);
                }

                var dNext = new double[H];
                for (int i = 0; i < H; i++)
                {
                    for (int j = 0; j < H && j < w.StepW[s][l][i].Length; j++)
                    {
                        double inp = j < fcIn.Length ? fcIn[j] : 0;
                        grad.StepW[s][l][i][j]  += dPreFc[i] * inp;
                        dNext[j] += dPreFc[i] * w.StepW[s][l][i][j];
                        if (w.UseGlu)
                        {
                            grad.StepGW[s][l][i][j] += dGateIn[i] * inp;
                            dNext[j] += dGateIn[i] * w.StepGW[s][l][i][j];
                        }
                    }
                    grad.StepB[s][l][i]  += dPreFc[i];
                    if (w.UseGlu)
                        grad.StepGB[s][l][i] += dGateIn[i];
                }

                if (l > 0) for (int j = 0; j < H; j++) dNext[j] += dResidual[j];
                dInput = dNext;
            }

            // Shared layers backward
            for (int l = w.SharedLayers - 1; l >= 0; l--)
            {
                double[] pre  = fwd.StepSharedPre[s][l];
                double[] gate = fwd.StepSharedGate[s][l];
                double[] xn   = fwd.StepSharedXNorm[s][l];
                double[] fcIn = fwd.StepSharedFcIn[s][l];
                int bnSIdx = w.NSteps + l;
                int inDim = l == 0 ? F : H;

                double[] dResidual = null!;
                if (l > 0)
                {
                    dResidual = new double[H];
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * SqrtHalfResidualScale;
                        dInput[j]    = dInput[j] * SqrtHalfResidualScale;
                    }
                }

                var dBnOut  = new double[H];
                var dGateIn = new double[H];
                if (w.UseGlu)
                {
                    for (int j = 0; j < H; j++)
                    {
                        dBnOut[j]  = dInput[j] * gate[j];
                        dGateIn[j] = dInput[j] * pre[j] * gate[j] * (1 - gate[j]);
                    }
                }
                else
                {
                    Array.Copy(dInput, dBnOut, H);
                }

                double meanDy = 0, meanDyXn = 0;
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnSIdx][j] += dBnOut[j] * xn[j];
                    grad.BnBeta[bnSIdx][j]  += dBnOut[j];
                    meanDy   += dBnOut[j];
                    meanDyXn += dBnOut[j] * xn[j];
                }
                meanDy /= H; meanDyXn /= H;

                var dPreFc = new double[H];
                for (int j = 0; j < H; j++)
                {
                    double var_ = w.BnVar[bnSIdx].Length > j ? w.BnVar[bnSIdx][j] : 1.0;
                    double invStd = Math.Min(1.0 / Math.Sqrt(Math.Max(0, var_) + BnEpsilon), MaxInvStd);
                    dPreFc[j] = w.BnGamma[bnSIdx][j] * invStd * (dBnOut[j] - meanDy - xn[j] * meanDyXn);
                }

                var dNext = new double[inDim];
                for (int i = 0; i < H; i++)
                {
                    for (int j = 0; j < inDim && j < w.SharedW[l][i].Length; j++)
                    {
                        double inp = j < fcIn.Length ? fcIn[j] : 0;
                        grad.SharedW[l][i][j]  += dPreFc[i] * inp;
                        dNext[j] += dPreFc[i] * w.SharedW[l][i][j];
                        if (w.UseGlu)
                        {
                            grad.SharedGW[l][i][j] += dGateIn[i] * inp;
                            dNext[j] += dGateIn[i] * w.SharedGW[l][i][j];
                        }
                    }
                    grad.SharedB[l][i]  += dPreFc[i];
                    if (w.UseGlu)
                        grad.SharedGB[l][i] += dGateIn[i];
                }

                if (l > 0)
                    for (int j = 0; j < Math.Min(dNext.Length, H); j++)
                        dNext[j] += dResidual[j];

                dInput = l == 0 ? dNext : dNext[..H];
            }

            // Attention FC backward (including step 0)
            var attn = fwd.StepAttn[s];
            var dAttn = new double[F];
            for (int j = 0; j < F && j < dInput.Length; j++)
                dAttn[j] = dInput[j] * features[j];

            var dAttnLogits = new double[F];
            ComputeAttentionLogitGradients(attn, dAttn, F, w.UseSparsemax, dAttnLogits);

            var dAttnBnOut = new double[F];
            var dAttnInput = new double[F];
            double attnMeanDy = 0.0, attnMeanDyXn = 0.0;
            for (int j = 0; j < F; j++)
            {
                double prior = s < fwd.StepPriorScales.Length && j < fwd.StepPriorScales[s].Length
                    ? fwd.StepPriorScales[s][j]
                    : 1.0;
                dAttnBnOut[j] = dAttnLogits[j] * prior;

                double xn = s < fwd.StepAttnXNorm.Length && j < fwd.StepAttnXNorm[s].Length
                    ? fwd.StepAttnXNorm[s][j]
                    : 0.0;
                grad.BnGamma[s][j] += dAttnBnOut[j] * xn;
                grad.BnBeta[s][j]  += dAttnBnOut[j];
                attnMeanDy += dAttnBnOut[j];
                attnMeanDyXn += dAttnBnOut[j] * xn;
            }

            attnMeanDy /= F;
            attnMeanDyXn /= F;

            for (int j = 0; j < F; j++)
            {
                double bnVar = w.BnVar[s].Length > j ? w.BnVar[s][j] : 1.0;
                double invStd = Math.Min(1.0 / Math.Sqrt(bnVar + BnEpsilon), MaxInvStd);
                double xn = s < fwd.StepAttnXNorm.Length && j < fwd.StepAttnXNorm[s].Length
                    ? fwd.StepAttnXNorm[s][j]
                    : 0.0;
                dAttnInput[j] = w.BnGamma[s][j] * invStd * (dAttnBnOut[j] - attnMeanDy - xn * attnMeanDyXn);
            }

            if (s > 0)
            {
                var dPrevStep = new double[H];
                double[] hPrevStep = s - 1 < fwd.StepH.Length ? fwd.StepH[s - 1] : new double[H];
                for (int j = 0; j < F; j++)
                {
                    double dFcJ = dAttnInput[j];
                    if (Math.Abs(dFcJ) < 1e-20) continue;
                    int attnDim = Math.Min(H, w.AttnFcW[s][j].Length);
                    for (int k = 0; k < attnDim; k++)
                    {
                        double hPrevK = k < hPrevStep.Length ? hPrevStep[k] : 0.0;
                        grad.AttnFcW[s][j][k] += dFcJ * hPrevK;
                        dPrevStep[k] += dFcJ * w.AttnFcW[s][j][k];
                    }
                    grad.AttnFcB[s][j] += dFcJ;
                }

                Array.Copy(dPrevStep, dFutureStep, H);
            }
            else if (w.InitialBnFcW.Length > 0)
            {
                for (int j = 0; j < F; j++)
                {
                    double dFcJ = dAttnInput[j];
                    if (Math.Abs(dFcJ) < 1e-20) continue;
                    for (int k = 0; k < F; k++)
                        grad.InitialBnFcW[j][k] += dFcJ * features[k];
                    grad.InitialBnFcB[j] += dFcJ;
                }

                Array.Clear(dFutureStep, 0, H);
            }
            else
            {
                Array.Clear(dFutureStep, 0, H);
            }
        }
    }
}
