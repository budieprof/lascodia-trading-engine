using System.Buffers;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  FORWARD PASS — True TabNet architecture
    //  Returns raw probability, per-step aggregated output, and cached
    //  intermediates for backpropagation.
    // ═══════════════════════════════════════════════════════════════════════

    private static ForwardResult ForwardPass(
        float[] features, TabNetWeights w, double[] priorScalesBuf, double[] attnLogitsBuf,
        bool training, double dropoutRate, Random? rng,
        double[][]? epochBatchMeans = null, double[][]? epochBatchVars = null,
        ForwardResult? result = null)
    {
        int F = w.F, nSteps = w.NSteps, H = w.HiddenDim;

        var fwd = result ?? ForwardResult.Allocate(nSteps, F, H, w.SharedLayers, w.StepLayers);
        fwd.Reset(H);

        Array.Fill(priorScalesBuf, 1.0, 0, F);

        var hPrev = fwd.HPrev; // pooled [H] buffer
        var attnInput = fwd.AttnInput; // pooled [F] buffer

        for (int s = 0; s < nSteps; s++)
        {
            // Save per-step prior scales before they are modified by this step's attention
            Array.Copy(priorScalesBuf, 0, fwd.StepPriorScales[s], 0, F);

            // ── 1. Attentive Transformer: FC → BN → Sparsemax ────────
            if (s == 0)
            {
                // Step-0: learnable initial BN FC projection for symmetry with steps > 0
                if (w.InitialBnFcW.Length > 0 && w.InitialBnFcW[0].Length == F)
                {
                    for (int j = 0; j < F; j++)
                    {
                        double val = w.InitialBnFcB[j];
                        for (int k = 0; k < F; k++)
                            val += w.InitialBnFcW[j][k] * features[k];
                        attnInput[j] = val;
                    }
                }
                else
                {
                    for (int j = 0; j < F; j++)
                        attnInput[j] = features[j];
                }
            }
            else
            {
                for (int j = 0; j < F; j++)
                {
                    double val = 0;
                    for (int k = 0; k < H && k < w.AttnFcW[s][j].Length; k++)
                        val += w.AttnFcW[s][j][k] * hPrev[k];
                    attnInput[j] = val + w.AttnFcB[s][j];
                }
            }

            // BN on attention input — use batch stats during training
            int bnIdx = s;
            double[]? attnBatchMean = training && epochBatchMeans is not null && bnIdx < epochBatchMeans.Length ? epochBatchMeans[bnIdx] : null;
            double[]? attnBatchVar  = training && epochBatchVars  is not null && bnIdx < epochBatchVars.Length  ? epochBatchVars[bnIdx]  : null;
            double[] activeMean = attnBatchMean ?? w.BnMean[bnIdx];
            double[] activeVar  = attnBatchVar  ?? w.BnVar[bnIdx];
            var (bnAttnOutput, attnXNorm) = ApplyBatchNormWithXNorm(attnInput, F, w.BnGamma[bnIdx], w.BnBeta[bnIdx],
                activeMean, activeVar);

            Array.Copy(attnXNorm, fwd.StepAttnXNorm[s], F);

            // Apply prior scales
            for (int j = 0; j < F; j++)
                attnLogitsBuf[j] = priorScalesBuf[j] * bnAttnOutput[j];

            Array.Copy(attnLogitsBuf, 0, fwd.StepAttnPre[s], 0, F);

            // Sparsemax or Softmax
            double[] attn = w.UseSparsemax
                ? Sparsemax(attnLogitsBuf, F)
                : SoftmaxArr(attnLogitsBuf, F);

            Array.Copy(attn, fwd.StepAttn[s], F);

            // ── 2. Prior scale update with configurable γ ────────────
            for (int j = 0; j < F; j++)
                priorScalesBuf[j] = Math.Max(1e-6, priorScalesBuf[j] * (w.Gamma - attn[j]));

            // ── 3. Mask input ────────────────────────────────────────
            for (int j = 0; j < F; j++)
                fwd.StepMasked[s][j] = features[j] * attn[j];

            // ── 4. Feature Transformer: shared FC→BN→GLU blocks ──────
            double[] h = fwd.StepMasked[s];
            int inputDim = F;
            var gluBuf = fwd.GluBuf;
            int gluFlip = 0; // alternates between GluOutA/GluOutB to avoid aliasing

            for (int l = 0; l < w.SharedLayers; l++)
            {
                int bnSharedIdx = w.NSteps + l;
                double[]? bm = training && epochBatchMeans is not null && bnSharedIdx < epochBatchMeans.Length ? epochBatchMeans[bnSharedIdx] : null;
                double[]? bv = training && epochBatchVars  is not null && bnSharedIdx < epochBatchVars.Length  ? epochBatchVars[bnSharedIdx]  : null;

                if (gluBuf is not null)
                {
                    var outBuf = (gluFlip & 1) == 0 ? fwd.GluOutA : fwd.GluOutB;
                    gluFlip++;

                    FcBnGluPooled(h, inputDim, H,
                        w.SharedW[l], w.SharedB[l], w.SharedGW[l], w.SharedGB[l],
                        w.BnGamma[bnSharedIdx], w.BnBeta[bnSharedIdx],
                        w.BnMean[bnSharedIdx], w.BnVar[bnSharedIdx], bm, bv,
                        training, dropoutRate, rng, w.UseGlu, gluBuf,
                        outBuf, fwd.StepSharedPre[s][l], fwd.StepSharedGate[s][l],
                        fwd.StepSharedXNorm[s][l], fwd.StepSharedFcIn[s][l],
                        s < fwd.StepSharedDropMask.Length && l < fwd.StepSharedDropMask[s].Length
                            ? fwd.StepSharedDropMask[s][l] : null);

                    if (l > 0 && h.Length == H)
                        for (int j = 0; j < H; j++)
                            outBuf[j] = (outBuf[j] + h[j]) * SqrtHalfResidualScale;

                    h = outBuf;
                }
                else
                {
                    var (hNew, pre, gate, xn, fcIn) = FcBnGlu(h, inputDim, H,
                        w.SharedW[l], w.SharedB[l], w.SharedGW[l], w.SharedGB[l],
                        w.BnGamma[bnSharedIdx], w.BnBeta[bnSharedIdx],
                        w.BnMean[bnSharedIdx], w.BnVar[bnSharedIdx], bm, bv,
                        training, dropoutRate, rng, w.UseGlu);

                    Array.Copy(pre, fwd.StepSharedPre[s][l], H);
                    Array.Copy(gate, fwd.StepSharedGate[s][l], H);
                    Array.Copy(xn, fwd.StepSharedXNorm[s][l], H);
                    Array.Copy(fcIn, 0, fwd.StepSharedFcIn[s][l], 0, Math.Min(fcIn.Length, fwd.StepSharedFcIn[s][l].Length));

                    if (l > 0 && h.Length == H)
                        for (int j = 0; j < H; j++)
                            hNew[j] = (hNew[j] + h[j]) * SqrtHalfResidualScale;

                    h = hNew;
                }

                inputDim = H;
            }

            // ── 5. Step-specific FC→BN→GLU blocks ────────────────────
            for (int l = 0; l < w.StepLayers; l++)
            {
                int bnStepIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                double[]? bm = training && epochBatchMeans is not null && bnStepIdx < epochBatchMeans.Length ? epochBatchMeans[bnStepIdx] : null;
                double[]? bv = training && epochBatchVars  is not null && bnStepIdx < epochBatchVars.Length  ? epochBatchVars[bnStepIdx]  : null;

                if (gluBuf is not null)
                {
                    var outBuf = (gluFlip & 1) == 0 ? fwd.GluOutA : fwd.GluOutB;
                    gluFlip++;

                    FcBnGluPooled(h, H, H,
                        w.StepW[s][l], w.StepB[s][l], w.StepGW[s][l], w.StepGB[s][l],
                        w.BnGamma[bnStepIdx], w.BnBeta[bnStepIdx],
                        w.BnMean[bnStepIdx], w.BnVar[bnStepIdx], bm, bv,
                        training, dropoutRate, rng, w.UseGlu, gluBuf,
                        outBuf, fwd.StepStepPre[s][l], fwd.StepStepGate[s][l],
                        fwd.StepStepXNorm[s][l], fwd.StepStepFcIn[s][l],
                        s < fwd.StepStepDropMask.Length && l < fwd.StepStepDropMask[s].Length
                            ? fwd.StepStepDropMask[s][l] : null);

                    if (l > 0)
                        for (int j = 0; j < H; j++)
                            outBuf[j] = (outBuf[j] + h[j]) * SqrtHalfResidualScale;

                    h = outBuf;
                }
                else
                {
                    var (hNew, pre, gate, xn, fcIn) = FcBnGlu(h, H, H,
                        w.StepW[s][l], w.StepB[s][l], w.StepGW[s][l], w.StepGB[s][l],
                        w.BnGamma[bnStepIdx], w.BnBeta[bnStepIdx],
                        w.BnMean[bnStepIdx], w.BnVar[bnStepIdx], bm, bv,
                        training, dropoutRate, rng, w.UseGlu);

                    Array.Copy(pre, fwd.StepStepPre[s][l], H);
                    Array.Copy(gate, fwd.StepStepGate[s][l], H);
                    Array.Copy(xn, fwd.StepStepXNorm[s][l], H);
                    Array.Copy(fcIn, 0, fwd.StepStepFcIn[s][l], 0, Math.Min(fcIn.Length, fwd.StepStepFcIn[s][l].Length));

                    if (l > 0)
                        for (int j = 0; j < H; j++)
                            hNew[j] = (hNew[j] + h[j]) * SqrtHalfResidualScale;

                    h = hNew;
                }
            }

            // ── 6. ReLU gate and aggregate ───────────────────────────
            Array.Copy(h, fwd.StepH[s], H);
            for (int j = 0; j < H; j++)
                fwd.AggregatedH[j] += Math.Max(h[j], 0.0);

            // Update hPrev in-place (pooled buffer)
            Array.Copy(h, hPrev, H);
        }

        // ── 7. Output head: FC → sigmoid (SIMD dot product) ──────────
        double logit = w.OutputB + SimdDot(w.OutputW, fwd.AggregatedH, H);

        fwd.Prob = Sigmoid(logit);
        Array.Copy(priorScalesBuf, fwd.PriorScales, F);

        return fwd;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FC → BN → GLU BLOCK
    // ═══════════════════════════════════════════════════════════════════════

    private static (double[] Output, double[] PreGlu, double[] GateSigmoid, double[] XNorm, double[] FcInput) FcBnGlu(
        double[] input, int inDim, int outDim,
        double[][] fcW, double[] fcB, double[][] gateW, double[] gateB,
        double[] bnGamma, double[] bnBeta, double[] bnMean, double[] bnVar,
        double[]? batchMean, double[]? batchVar,
        bool training, double dropoutRate, Random? rng, bool useGlu)
    {
        // Cache the actual FC input for correct backward gradients through residual connections
        var fcInput = new double[inDim];
        Array.Copy(input, fcInput, inDim);

        // Linear transform
        var linear = new double[outDim];
        for (int i = 0; i < outDim; i++)
        {
            double val = fcB[i];
            for (int j = 0; j < inDim && j < fcW[i].Length; j++)
                val += fcW[i][j] * input[j];
            linear[i] = val;
        }

        // BN — use batch stats during training, running stats at inference
        double[] activeMean = training && batchMean is not null ? batchMean : bnMean;
        double[] activeVar  = training && batchVar  is not null ? batchVar  : bnVar;
        var (bnOutput, xNorm) = ApplyBatchNormWithXNorm(linear, outDim, bnGamma, bnBeta, activeMean, activeVar);

        var gate = new double[outDim];
        if (useGlu)
        {
            for (int i = 0; i < outDim; i++)
            {
                double val = gateB[i];
                for (int j = 0; j < inDim && j < gateW[i].Length; j++)
                    val += gateW[i][j] * input[j];
                gate[i] = Sigmoid(val);
            }
        }
        else
        {
            Array.Fill(gate, 1.0);
        }

        // GLU: linear ⊙ sigmoid(gate)
        var output = new double[outDim];
        for (int i = 0; i < outDim; i++)
            output[i] = bnOutput[i] * gate[i];

        // Dropout (training only)
        if (training && dropoutRate > 0 && rng is not null)
        {
            double scale = 1.0 / (1.0 - dropoutRate);
            for (int i = 0; i < outDim; i++)
                if (rng.NextDouble() < dropoutRate) output[i] = 0;
                else output[i] *= scale;
        }

        return (output, bnOutput, gate, xNorm, fcInput);
    }

    /// <summary>
    /// Pooled FC→BN→GLU that writes results into pre-allocated destination arrays,
    /// eliminating 5 heap allocations per call during the training hot loop.
    /// Optionally populates a dropout mask for exact backward pass.
    /// </summary>
    private static void FcBnGluPooled(
        double[] input, int inDim, int outDim,
        double[][] fcW, double[] fcB, double[][] gateW, double[] gateB,
        double[] bnGamma, double[] bnBeta, double[] bnMean, double[] bnVar,
        double[]? batchMean, double[]? batchVar,
        bool training, double dropoutRate, Random? rng, bool useGlu,
        FcBnGluBuffers buf,
        double[] dstOutput, double[] dstPre, double[] dstGate, double[] dstXNorm, double[] dstFcIn,
        bool[]? dropMask = null)
    {
        // Cache FC input
        Array.Copy(input, 0, dstFcIn, 0, Math.Min(inDim, dstFcIn.Length));

        // Linear + optional gate transforms (SIMD-accelerated inner products)
        int dotLen = Math.Min(inDim, fcW.Length > 0 ? fcW[0].Length : inDim);
        for (int i = 0; i < outDim; i++)
        {
            int rowLen = Math.Min(dotLen, fcW[i].Length);
            buf.Linear[i] = fcB[i] + SimdDot(fcW[i], input, rowLen);

            if (useGlu)
            {
                rowLen = Math.Min(dotLen, gateW[i].Length);
                dstGate[i] = Sigmoid(gateB[i] + SimdDot(gateW[i], input, rowLen));
            }
            else
            {
                dstGate[i] = 1.0;
            }
        }

        // BN
        double[] activeMean = training && batchMean is not null ? batchMean : bnMean;
        double[] activeVar  = training && batchVar  is not null ? batchVar  : bnVar;
        for (int i = 0; i < outDim && i < bnGamma.Length; i++)
        {
            double m = activeMean.Length > i ? activeMean[i] : 0.0;
            double v = activeVar.Length > i ? activeVar[i] : 1.0;
            dstXNorm[i] = (buf.Linear[i] - m) / Math.Sqrt(v + BnEpsilon);
            dstPre[i]   = bnGamma[i] * dstXNorm[i] + bnBeta[i];
        }

        // GLU + dropout with mask caching
        if (training && dropoutRate > 0 && rng is not null)
        {
            double scale = 1.0 / (1.0 - dropoutRate);
            for (int i = 0; i < outDim; i++)
            {
                bool kept = rng.NextDouble() >= dropoutRate;
                if (dropMask is not null) dropMask[i] = kept;
                double glu = dstPre[i] * dstGate[i];
                dstOutput[i] = kept ? glu * scale : 0;
            }
        }
        else
        {
            for (int i = 0; i < outDim; i++)
                dstOutput[i] = dstPre[i] * dstGate[i];
            if (dropMask is not null)
                Array.Fill(dropMask, true, 0, outDim);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BATCH NORMALIZATION
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] ApplyBatchNorm(
        double[] input, int len, double[] gamma, double[] beta,
        double[] mean, double[] var_)
    {
        var output = new double[len];
        for (int i = 0; i < len && i < gamma.Length; i++)
        {
            double m = mean.Length > i ? mean[i] : 0.0;
            double v = var_.Length > i ? var_[i] : 1.0;
            double xn = (input[i] - m) / Math.Sqrt(v + BnEpsilon);
            output[i] = gamma[i] * xn + beta[i];
        }
        return output;
    }

    private static (double[] Output, double[] XNorm) ApplyBatchNormWithXNorm(
        double[] input, int len, double[] gamma, double[] beta,
        double[] mean, double[] var_)
    {
        var output = new double[len];
        var xNorm  = new double[len];
        for (int i = 0; i < len && i < gamma.Length; i++)
        {
            double m = mean.Length > i ? mean[i] : 0.0;
            double v = var_.Length > i ? var_[i] : 1.0;
            xNorm[i]  = (input[i] - m) / Math.Sqrt(v + BnEpsilon);
            output[i] = gamma[i] * xNorm[i] + beta[i];
        }
        return (output, xNorm);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SPARSEMAX (Martins & Astudillo 2016)
    // ═══════════════════════════════════════════════════════════════════════

    private static double[] Sparsemax(double[] z, int len)
    {
        var sorted = new double[len];
        for (int i = 0; i < len; i++) sorted[i] = z[i];
        Array.Sort(sorted);
        Array.Reverse(sorted);

        double cumSum = 0;
        int k = 0;
        for (int i = 0; i < len; i++)
        {
            cumSum += sorted[i];
            if (sorted[i] > (cumSum - 1.0) / (i + 1))
                k = i + 1;
            else
                break;
        }

        cumSum = 0;
        for (int i = 0; i < k; i++) cumSum += sorted[i];
        double tau = (cumSum - 1.0) / k;

        var output = new double[len];
        double sum = 0;
        for (int i = 0; i < len; i++)
        {
            output[i] = Math.Max(0, z[i] - tau);
            sum += output[i];
        }

        // Guard: if all entries zeroed (e.g. identical inputs), fall back to uniform
        if (sum < Eps)
            for (int i = 0; i < len; i++) output[i] = 1.0 / len;

        return output;
    }

    private static double[] SoftmaxArr(double[] x, int len)
    {
        double max = double.MinValue;
        for (int i = 0; i < len; i++) if (x[i] > max) max = x[i];
        var e = new double[len]; double sum = 0;
        for (int i = 0; i < len; i++) { e[i] = Math.Exp(x[i] - max); sum += e[i]; }
        sum += Eps;
        for (int i = 0; i < len; i++) e[i] /= sum;
        return e;
    }
}
