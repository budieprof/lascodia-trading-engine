using System.Buffers;
using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  TABNET FITTING — True TabNet with shared+step-specific GLU Feature
    //  Transformer, Attentive Transformer with Sparsemax, Ghost BN, Adam
    //  optimizer with cosine LR, gradient clipping, and early stopping.
    // ═══════════════════════════════════════════════════════════════════════

    private TabNetWeights FitTabNet(
        List<TrainingSample> trainSet,
        int                  F,
        int                  nSteps,
        int                  hiddenDim,
        int                  attentionDim,
        int                  sharedLayers,
        int                  stepLayers,
        double               gamma,
        bool                 useSparsemax,
        bool                 useGlu,
        double               baseLr,
        double               sparsityCoeff,
        int                  maxEpochs,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        TabNetWeights?       pretrainedInit,
        double[]?            densityWeights,
        double               temporalDecayLambda,
        double               l2Lambda,
        int                  patience,
        double               magLossWeight,
        double               maxGradNorm,
        double               dropoutRate,
        double               bnMomentum,
        int                  ghostBatchSize,
        int                  warmupEpochs,
        TabNetRunContext     runContext,
        CancellationToken    ct)
    {
        int n = trainSet.Count;
        bool useMagHead = magLossWeight > 0.0;

        // ── GPU fast-path: use TorchSharp when CUDA is available and dataset large enough ──
        if (n >= GpuMinSamples && IsGpuAvailable())
        {
            try
            {
                return FitTabNetGpu(trainSet, F, nSteps, hiddenDim, attentionDim,
                    sharedLayers, stepLayers, gamma, useSparsemax, useGlu,
                    baseLr, sparsityCoeff, maxEpochs, labelSmoothing,
                    warmStart, pretrainedInit, densityWeights, temporalDecayLambda,
                    l2Lambda, patience, magLossWeight, maxGradNorm,
                    dropoutRate, bnMomentum, ghostBatchSize, warmupEpochs, runContext, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TabNet GPU training failed, falling back to CPU: {Message}", ex.Message);
            }
        }

        var warmStartReport = new TabNetSnapshotSupport.WarmStartLoadReport(0, 0, 0, 0, 0);
        runContext.WarmStartLoadReport = warmStartReport;

        // Temporal decay weights blended with density weights
        var temporalWeights = ComputeTemporalWeights(n, temporalDecayLambda);
        if (densityWeights is { Length: > 0 } && densityWeights.Length == n)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++) { temporalWeights[i] *= densityWeights[i]; sum += temporalWeights[i]; }
            if (sum > Eps) for (int i = 0; i < n; i++) temporalWeights[i] /= sum;
        }

        // ── Initialise weights ─────────────────────────────────────────────
        var w = InitializeWeights(F, nSteps, hiddenDim, attentionDim, sharedLayers, stepLayers,
            gamma, useSparsemax, useGlu, useMagHead);

        // ── Load from pre-trained or warm-start ────────────────────────────
        if (pretrainedInit is not null)
        {
            CopyCompatibleWeights(pretrainedInit, w);
            _logger.LogInformation("TabNet: loaded pre-trained encoder weights");
        }
        else if (warmStart?.Type == ModelType && warmStart.Version == ModelVersion)
        {
            warmStartReport = LoadWarmStartWeights(warmStart, w);
            _logger.LogInformation(
                "TabNet warm-start: gen={Gen} reused={Reused}/{Attempted} resized={Resized} skipped={Skipped} rejected={Rejected} reuseRatio={ReuseRatio:P1}",
                warmStart.GenerationNumber, warmStartReport.Reused, warmStartReport.Attempted,
                warmStartReport.Resized, warmStartReport.Skipped, warmStartReport.Rejected, warmStartReport.ReuseRatio);
        }
        else if (warmStart?.Type == ModelType)
        {
            _logger.LogInformation("TabNet warm-start: version mismatch ({V}→{V2}), starting fresh.",
                warmStart.Version, ModelVersion);
        }
        runContext.WarmStartLoadReport = warmStartReport;

        // ── Adam state ─────────────────────────────────────────────────────
        var adam = InitializeAdamState(w);

        // Warm-start Adam second moments from loaded weight magnitudes
        if (warmStart is not null && (pretrainedInit is not null || warmStart.Type == ModelType))
            InitializeAdamSecondMoment(adam, w);

        // ── Validation split for early stopping (last 10% of train) ───────
        int valSize  = Math.Max(20, n / 10);
        var valSet   = trainSet[^valSize..];
        var fitSet   = trainSet[..^valSize];
        int nFit     = fitSet.Count;

        double bestValLoss = double.MaxValue;
        int    earlyCount  = 0;
        int    bestEpoch   = 0;
        TabNetWeights bestW = CloneWeights(w);

        // ── Training indices for per-epoch shuffling ──────────────────────
        var indices = new int[nFit];
        for (int i = 0; i < nFit; i++) indices[i] = i;

        // ── Mini-batch gradient accumulators ──────────────────────────────
        var grad = InitializeGradAccumulator(w);
        int batchCount = 0;

        // ── Pooled scratch buffers ────────────────────────────────────────
        var pool = ArrayPool<double>.Shared;
        double[] priorScales = pool.Rent(F);
        double[] attnLogits  = pool.Rent(F);

        // Pre-allocate ForwardResult and BackwardBuffers once, reuse across all samples/epochs
        var fwdPool = ForwardResult.Allocate(nSteps, F, hiddenDim, sharedLayers, stepLayers);
        var bwdPool = BackwardBuffers.Allocate(F, hiddenDim);

        try
        {
            for (int ep = 0; ep < maxEpochs && !ct.IsCancellationRequested; ep++)
            {
                double cosLr;
                if (warmupEpochs > 0 && ep < warmupEpochs)
                {
                    // Linear warmup: ramp from baseLr/10 to baseLr over warmupEpochs
                    cosLr = baseLr * (0.1 + 0.9 * ep / warmupEpochs);
                }
                else
                {
                    // Cosine decay from baseLr after warmup phase
                    int decayEp = ep - warmupEpochs;
                    int decayTotal = maxEpochs - warmupEpochs;
                    cosLr = decayTotal > 0
                        ? baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * decayEp / decayTotal))
                        : baseLr;
                }

                // Shuffle training indices each epoch
                var epochRng = new Random(42 + ep);
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int k = epochRng.Next(i + 1);
                    (indices[k], indices[i]) = (indices[i], indices[k]);
                }

                // ── Per-mini-batch ghost BN stats ─────────────────────────
                // Instead of one epoch-wide pre-pass, recompute ghost-batch
                // stats from each mini-batch's samples for fresher statistics.
                double[][] miniBatchMeans = null!;
                double[][] miniBatchVars  = null!;
                bool needBnRefresh = true;

                double epochTrainLoss = 0;

                for (int ii = 0; ii < nFit; ii++)
                {
                    // Refresh ghost-batch BN stats at each mini-batch boundary
                    if (needBnRefresh)
                    {
                        int mbStart = ii;
                        int mbEnd   = Math.Min(ii + DefaultBatchSize, nFit);
                        int mbCount = Math.Min(mbEnd - mbStart, ghostBatchSize);
                        if (mbCount >= runContext.MinCalibrationSamples)
                        {
                            var mbIndices = new int[mbCount];
                            for (int mi = 0; mi < mbCount; mi++)
                                mbIndices[mi] = indices[mbStart + mi];
                            (miniBatchMeans, miniBatchVars) = ComputeMiniBatchBnStats(
                                w, fitSet, mbCount, runContext.MinCalibrationSamples, mbIndices);
                        }
                        else if (miniBatchMeans is null)
                        {
                            // Fallback: compute from the full ghost batch for the first mini-batch
                            (miniBatchMeans, miniBatchVars) = ComputeEpochBatchStats(
                                w, fitSet, ghostBatchSize, runContext.MinCalibrationSamples, epochRng);
                        }
                        needBnRefresh = false;
                    }

                    int idx = indices[ii];
                    var sample = fitSet[idx];
                    double sampleWt = temporalWeights.Length > idx ? temporalWeights[idx] : 1.0 / nFit;

                    int rawY = sample.Direction > 0 ? 1 : 0;
                    double y = labelSmoothing > 0
                        ? rawY * (1 - labelSmoothing) + 0.5 * labelSmoothing
                        : rawY;

                    // ── Forward pass (reuses pooled result, mini-batch BN stats) ──
                    var fwd = ForwardPass(sample.Features, w, priorScales, attnLogits, training: true,
                        dropoutRate, epochRng, miniBatchMeans, miniBatchVars, fwdPool);

                    double errCE = fwd.Prob - y;

                    // Track training loss
                    epochTrainLoss -= rawY * Math.Log(fwd.Prob + Eps)
                                    + (1 - rawY) * Math.Log(1 - fwd.Prob + Eps);

                    // ── Magnitude head Huber gradient ─────────────────────
                    double huberGrad = 0.0;
                    if (useMagHead)
                    {
                        double magPred = w.MagB;
                        for (int j = 0; j < w.HiddenDim && j < w.MagW.Length; j++)
                            magPred += w.MagW[j] * fwd.AggregatedH[j];
                        double magErr = magPred - sample.Magnitude;
                        huberGrad = Math.Abs(magErr) <= runContext.HuberDelta
                            ? magErr
                            : runContext.HuberDelta * Math.Sign(magErr);
                    }

                    // ── Backward pass (full BN backward, pooled buffers) ──
                    AccumulateGradients(grad, w, fwd, sample.Features, errCE, sampleWt,
                        huberGrad, magLossWeight, sparsityCoeff, useMagHead, miniBatchVars, dropoutRate, bwdPool);

                    batchCount++;

                    // ── Apply AdamW update at batch boundaries ────────────
                    if (batchCount >= DefaultBatchSize || ii == nFit - 1)
                    {
                        double invBatch = 1.0 / batchCount;
                        ScaleGradients(grad, invBatch);

                        if (maxGradNorm > 0)
                            ClipGradients(grad, maxGradNorm);

                        int nanFixCount = AdamUpdate(w, grad, adam, cosLr, l2Lambda);
                        if (nanFixCount > 0)
                            _logger.LogWarning("TabNet Adam step {T}: replaced {Count} non-finite gradient/parameter values",
                                adam.T, nanFixCount);
                        if (miniBatchMeans is not null && miniBatchVars is not null)
                            ApplyBnRunningStatsEma(w, miniBatchMeans, miniBatchVars, bnMomentum);
                        ZeroGradients(grad);
                        batchCount = 0;
                        needBnRefresh = true; // refresh BN stats for next mini-batch
                    }
                }

                // ── Log training loss periodically ───────────────────────
                if (ep % 10 == 9)
                    _logger.LogDebug("TabNet epoch {Ep}: trainLoss={Loss:F4} cosLr={Lr:F5}",
                        ep, epochTrainLoss / nFit, cosLr);

                // ── Early stopping (inference mode: running stats, no batch stats) ──
                if (valSet.Count >= runContext.MinCalibrationSamples && ep % 5 == 4)
                {
                    double valLoss = 0;
                    foreach (var vs in valSet)
                    {
                        var vfwd = ForwardPass(vs.Features, w, priorScales, attnLogits,
                            training: false, 0, null);
                        int vy = vs.Direction > 0 ? 1 : 0;
                        valLoss -= vy * Math.Log(vfwd.Prob + Eps)
                                 + (1 - vy) * Math.Log(1 - vfwd.Prob + Eps);
                    }
                    valLoss /= valSet.Count;

                    if (valLoss < bestValLoss - EarlyStopMinDelta)
                    {
                        bestValLoss = valLoss;
                        bestEpoch   = ep;
                        bestW       = CloneWeights(w);
                        earlyCount  = 0;
                    }
                    else if (++earlyCount >= Math.Max(3, patience / 5))
                    {
                        _logger.LogDebug("TabNet early stopping at epoch {E} (best at {Best})", ep, bestEpoch);
                        break;
                    }
                }
            }
        }
        finally
        {
            pool.Return(priorScales);
            pool.Return(attnLogits);
        }

        if (bestEpoch > 0)
            return bestW;

        if (warmStart is not null)
            w.OutputB = double.IsFinite(w.OutputB) ? w.OutputB : 0.0;

        return w;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BACKWARD PASS — Gradient accumulation with full BN backward
    // ═══════════════════════════════════════════════════════════════════════

    private static void AccumulateGradients(
        TabNetWeights grad, TabNetWeights w, ForwardResult fwd,
        float[] features, double errCE, double sampleWt,
        double huberGrad, double magLossWeight,
        double sparsityCoeff, bool useMagHead,
        double[][]? epochBatchVars, double dropoutRate, BackwardBuffers bwd)
    {
        int H = w.HiddenDim, F = w.F;
        bwd.Clear();

        // ── Output head gradients (L2 removed — handled by AdamW decoupled decay) ──
        double dLogit = sampleWt * errCE;
        for (int j = 0; j < H; j++)
            grad.OutputW[j] += dLogit * fwd.AggregatedH[j];
        grad.OutputB += dLogit;

        // ── Magnitude head gradients ─────────────────────────────────
        if (useMagHead && w.MagW.Length > 0)
        {
            double scaledHuber = sampleWt * magLossWeight * huberGrad;
            for (int j = 0; j < H && j < w.MagW.Length; j++)
                grad.MagW[j] += scaledHuber * fwd.AggregatedH[j];
            grad.MagB += scaledHuber;
        }

        // ── Per-step backward ────────────────────────────────────────
        var dAggH = bwd.DAggH;
        for (int j = 0; j < H; j++)
            dAggH[j] = dLogit * w.OutputW[j];

        if (useMagHead && w.MagW.Length > 0)
        {
            double scaledHuber = sampleWt * magLossWeight * huberGrad;
            for (int j = 0; j < H && j < w.MagW.Length; j++)
                dAggH[j] += scaledHuber * w.MagW[j];
        }

        var dFutureStep = bwd.DFutureStep;
        Array.Clear(dFutureStep, 0, H);

        for (int s = w.NSteps - 1; s >= 0; s--)
        {
            // ReLU gradient
            var dH = bwd.DH;
            Array.Clear(dH, 0, H);
            for (int j = 0; j < H; j++)
                dH[j] = dFutureStep[j] + (fwd.StepH[s][j] > 0 ? dAggH[j] : 0.0);

            // ── Backward through step-specific layers ────────────
            var dInput = bwd.DInput;
            Array.Copy(dH, dInput, H);

            for (int l = w.StepLayers - 1; l >= 0; l--)
            {
                double[] pre   = fwd.StepStepPre[s][l];
                double[] gate  = fwd.StepStepGate[s][l];
                double[] xNorm = fwd.StepStepXNorm[s][l];
                double[] fcIn  = fwd.StepStepFcIn[s][l];

                var dResidual = bwd.DResidual;
                if (l > 0)
                {
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * SqrtHalfResidualScale;
                        dInput[j]    = dInput[j] * SqrtHalfResidualScale;
                    }
                }

                // Apply cached dropout mask: zero dropped units, scale kept units
                if (dropoutRate > 0 && s < fwd.StepStepDropMask.Length && l < fwd.StepStepDropMask[s].Length)
                {
                    bool[] mask = fwd.StepStepDropMask[s][l];
                    double scale = 1.0 / (1.0 - dropoutRate);
                    for (int j = 0; j < H && j < mask.Length; j++)
                        dInput[j] = mask[j] ? dInput[j] * scale : 0;
                }

                var dBnOut  = bwd.DBnOut;
                var dGateIn = bwd.DGateIn;
                Array.Clear(dBnOut, 0, H);
                Array.Clear(dGateIn, 0, H);
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

                // Full BN backward: ∂L/∂x = γ/σ * (∂L/∂y - mean(∂L/∂y) - x̂ * mean(∂L/∂y * x̂))
                int bnStIdx = w.NSteps + w.SharedLayers + s * w.StepLayers + l;
                var dPreFc = bwd.DPreFc;
                Array.Clear(dPreFc, 0, H);

                double meanDy = 0, meanDyXn = 0;
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnStIdx][j] += dBnOut[j] * xNorm[j];
                    grad.BnBeta[bnStIdx][j]  += dBnOut[j];
                    meanDy   += dBnOut[j];
                    meanDyXn += dBnOut[j] * xNorm[j];
                }
                meanDy /= H; meanDyXn /= H;

                for (int j = 0; j < H; j++)
                {
                    double var_ = epochBatchVars is not null && bnStIdx < epochBatchVars.Length && j < epochBatchVars[bnStIdx].Length
                        ? epochBatchVars[bnStIdx][j]
                        : (w.BnVar[bnStIdx].Length > j ? w.BnVar[bnStIdx][j] : 1.0);
                    double invStd = Math.Min(1.0 / Math.Sqrt(var_ + BnEpsilon), MaxInvStd);
                    dPreFc[j] = w.BnGamma[bnStIdx][j] * invStd * (dBnOut[j] - meanDy - xNorm[j] * meanDyXn);
                }

                // FC backward using cached FC input (SIMD-accelerated)
                var dNextInput = bwd.DNextInputH;
                Array.Clear(dNextInput, 0, H);
                for (int i = 0; i < H; i++)
                {
                    int rowLen = Math.Min(H, w.StepW[s][l][i].Length);
                    SimdMulAdd(grad.StepW[s][l][i],  fcIn, dPreFc[i],  rowLen);
                    SimdMulAdd(dNextInput, w.StepW[s][l][i],  dPreFc[i],  rowLen);
                    grad.StepB[s][l][i]  += dPreFc[i];
                    if (w.UseGlu)
                    {
                        SimdMulAdd(grad.StepGW[s][l][i], fcIn, dGateIn[i], rowLen);
                        SimdMulAdd(dNextInput, w.StepGW[s][l][i], dGateIn[i], rowLen);
                        grad.StepGB[s][l][i] += dGateIn[i];
                    }
                }

                if (l > 0)
                    for (int j = 0; j < H; j++) dNextInput[j] += dResidual[j];

                Array.Copy(dNextInput, dInput, H);
            }

            // ── Backward through shared layers ───────────────────
            for (int l = w.SharedLayers - 1; l >= 0; l--)
            {
                double[] pre   = fwd.StepSharedPre[s][l];
                double[] gate  = fwd.StepSharedGate[s][l];
                double[] xNorm = fwd.StepSharedXNorm[s][l];
                double[] fcIn  = fwd.StepSharedFcIn[s][l];

                var dResidual = bwd.DResidual;
                if (l > 0)
                {
                    for (int j = 0; j < H; j++)
                    {
                        dResidual[j] = dInput[j] * SqrtHalfResidualScale;
                        dInput[j]    = dInput[j] * SqrtHalfResidualScale;
                    }
                }

                // Apply cached dropout mask
                if (dropoutRate > 0 && s < fwd.StepSharedDropMask.Length && l < fwd.StepSharedDropMask[s].Length)
                {
                    bool[] mask = fwd.StepSharedDropMask[s][l];
                    double scale = 1.0 / (1.0 - dropoutRate);
                    for (int j = 0; j < H && j < mask.Length; j++)
                        dInput[j] = mask[j] ? dInput[j] * scale : 0;
                }

                var dBnOut  = bwd.DBnOut;
                var dGateIn = bwd.DGateIn;
                Array.Clear(dBnOut, 0, H);
                Array.Clear(dGateIn, 0, H);
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
                int bnSIdx = w.NSteps + l;
                int inDim = l == 0 ? F : H;
                var dPreFc = bwd.DPreFc;
                Array.Clear(dPreFc, 0, H);

                double meanDy = 0, meanDyXn = 0;
                for (int j = 0; j < H; j++)
                {
                    grad.BnGamma[bnSIdx][j] += dBnOut[j] * xNorm[j];
                    grad.BnBeta[bnSIdx][j]  += dBnOut[j];
                    meanDy   += dBnOut[j];
                    meanDyXn += dBnOut[j] * xNorm[j];
                }
                meanDy /= H; meanDyXn /= H;

                for (int j = 0; j < H; j++)
                {
                    double var_ = epochBatchVars is not null && bnSIdx < epochBatchVars.Length && j < epochBatchVars[bnSIdx].Length
                        ? epochBatchVars[bnSIdx][j]
                        : (w.BnVar[bnSIdx].Length > j ? w.BnVar[bnSIdx][j] : 1.0);
                    double invStd = Math.Min(1.0 / Math.Sqrt(var_ + BnEpsilon), MaxInvStd);
                    dPreFc[j] = w.BnGamma[bnSIdx][j] * invStd * (dBnOut[j] - meanDy - xNorm[j] * meanDyXn);
                }

                // FC backward using cached FC input (SIMD-accelerated)
                int nextDim = inDim;
                var dNextInput = l == 0 ? bwd.DNextInputF : bwd.DNextInputH;
                Array.Clear(dNextInput, 0, nextDim);
                for (int i = 0; i < H; i++)
                {
                    int rowLen = Math.Min(inDim, w.SharedW[l][i].Length);
                    int fcInLen = Math.Min(rowLen, fcIn.Length);
                    SimdMulAdd(grad.SharedW[l][i],  fcIn, dPreFc[i],  fcInLen);
                    int propLen = Math.Min(rowLen, nextDim);
                    SimdMulAdd(dNextInput, w.SharedW[l][i],  dPreFc[i],  propLen);
                    grad.SharedB[l][i]  += dPreFc[i];
                    if (w.UseGlu)
                    {
                        SimdMulAdd(grad.SharedGW[l][i], fcIn, dGateIn[i], fcInLen);
                        SimdMulAdd(dNextInput, w.SharedGW[l][i], dGateIn[i], propLen);
                        grad.SharedGB[l][i] += dGateIn[i];
                    }
                }

                if (l > 0)
                    for (int j = 0; j < Math.Min(nextDim, H); j++)
                        dNextInput[j] += dResidual[j];

                if (l == 0)
                {
                    // dNextInput is bwd.DNextInputF (length F) — preserve full F-dim gradient
                    // for the attention path below, even when F > H
                    Array.Copy(dNextInput, dInput, Math.Min(F, H));
                }
                else
                    Array.Copy(dNextInput, dInput, H);
            }

            // ── Task-loss gradient through attention + sparsity ──
            // Use the F-length buffer directly to avoid truncation when F > H
            var dSharedOut = bwd.DNextInputF;
            var attn = fwd.StepAttn[s];
            var dAttn = bwd.DAttn;
            Array.Clear(dAttn, 0, F);
            for (int j = 0; j < F; j++)
                dAttn[j] = dSharedOut[j] * features[j];

            if (sparsityCoeff > 0)
            {
                for (int j = 0; j < F; j++)
                {
                    double entropyGrad = -(Math.Log(attn[j] + Eps) + 1.0);
                    dAttn[j] += sampleWt * sparsityCoeff * entropyGrad / w.NSteps;
                }
            }

            var dAttnLogits = bwd.DAttnLogits;
            ComputeAttentionLogitGradients(attn, dAttn, F, w.UseSparsemax, dAttnLogits);

            int bnIdx = s;
            var dAttnBnOut = bwd.DAttnBnOut;
            var dAttnInput = bwd.DAttnInput;
            Array.Clear(dAttnBnOut, 0, F);
            Array.Clear(dAttnInput, 0, F);

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
                grad.BnGamma[bnIdx][j] += dAttnBnOut[j] * xn;
                grad.BnBeta[bnIdx][j]  += dAttnBnOut[j];
                attnMeanDy += dAttnBnOut[j];
                attnMeanDyXn += dAttnBnOut[j] * xn;
            }

            attnMeanDy /= F;
            attnMeanDyXn /= F;

            for (int j = 0; j < F; j++)
            {
                double var_ = epochBatchVars is not null && bnIdx < epochBatchVars.Length && j < epochBatchVars[bnIdx].Length
                    ? epochBatchVars[bnIdx][j]
                    : (w.BnVar[bnIdx].Length > j ? w.BnVar[bnIdx][j] : 1.0);
                double invStd = Math.Min(1.0 / Math.Sqrt(var_ + BnEpsilon), MaxInvStd);
                double xn = s < fwd.StepAttnXNorm.Length && j < fwd.StepAttnXNorm[s].Length
                    ? fwd.StepAttnXNorm[s][j]
                    : 0.0;
                dAttnInput[j] = w.BnGamma[bnIdx][j] * invStd * (dAttnBnOut[j] - attnMeanDy - xn * attnMeanDyXn);
            }

            // Propagate into attention FC weights
            var dPrevStep = bwd.DPrevStep;
            Array.Clear(dPrevStep, 0, H);
            if (s > 0)
            {
                double[] hPrevStep = s > 0 && s - 1 < fwd.StepH.Length ? fwd.StepH[s - 1] : new double[H];
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
            else
            {
                for (int j = 0; j < F; j++)
                {
                    double dFcJ = dAttnInput[j];
                    if (Math.Abs(dFcJ) < 1e-20) continue;
                    if (w.InitialBnFcW.Length > 0 && w.InitialBnFcW[0].Length == F)
                    {
                        for (int k = 0; k < F; k++)
                            grad.InitialBnFcW[j][k] += dFcJ * features[k];
                        grad.InitialBnFcB[j] += dFcJ;
                    }
                }

                Array.Clear(dFutureStep, 0, H);
            }
        }
    }

    private static void ComputeAttentionLogitGradients(
        double[] attn,
        double[] dAttn,
        int featureCount,
        bool useSparsemax,
        double[] dst)
    {
        Array.Clear(dst, 0, featureCount);

        if (useSparsemax)
        {
            int supportCount = 0;
            double supportMean = 0.0;
            for (int j = 0; j < featureCount; j++)
            {
                if (attn[j] <= Eps)
                    continue;
                supportMean += dAttn[j];
                supportCount++;
            }

            if (supportCount <= 0)
                return;

            supportMean /= supportCount;
            for (int j = 0; j < featureCount; j++)
                dst[j] = attn[j] > Eps ? dAttn[j] - supportMean : 0.0;

            return;
        }

        double weightedMean = 0.0;
        for (int j = 0; j < featureCount; j++)
            weightedMean += attn[j] * dAttn[j];

        for (int j = 0; j < featureCount; j++)
            dst[j] = attn[j] * (dAttn[j] - weightedMean);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ADAM OPTIMIZER UPDATE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Counter for non-finite gradient/parameter values replaced during Adam updates.</summary>
    /// <summary>
    /// AdamW update: decoupled weight decay is applied directly to weights
    /// before the Adam step, keeping it independent of the adaptive learning rate.
    /// Biases and BN params are NOT decayed (standard practice).
    /// </summary>
    private static int AdamUpdate(TabNetWeights w, TabNetWeights grad, AdamState adam, double lr, double wd = 0)
    {
        int nanFixCount = 0;
        adam.T++;
        double bc1 = 1.0 - Math.Pow(AdamBeta1, adam.T);
        double bc2 = 1.0 - Math.Pow(AdamBeta2, adam.T);

        // Initial BN FC (weights decayed, biases not)
        if (w.InitialBnFcW.Length > 0)
        {
            nanFixCount += AdamWStep2D(w.InitialBnFcW, adam.MInitialBnFcW, adam.VInitialBnFcW, grad.InitialBnFcW, lr, bc1, bc2, wd);
            nanFixCount += AdamStepArr(w.InitialBnFcB, adam.MInitialBnFcB, adam.VInitialBnFcB, grad.InitialBnFcB, lr, bc1, bc2);
        }

        // Output head (weights decayed, bias not)
        nanFixCount += AdamStep(ref w.OutputB, ref adam.MOutputB, ref adam.VOutputB, grad.OutputB, lr, bc1, bc2);
        nanFixCount += AdamWStepArr(w.OutputW, adam.MOutputW, adam.VOutputW, grad.OutputW, lr, bc1, bc2, wd);

        // Magnitude head
        if (w.MagW.Length > 0)
        {
            nanFixCount += AdamStep(ref w.MagB, ref adam.MMagB, ref adam.VMagB, grad.MagB, lr, bc1, bc2);
            nanFixCount += AdamWStepArr(w.MagW, adam.MMagW, adam.VMagW, grad.MagW, lr, bc1, bc2, wd);
        }

        // Shared layers (FC + gate weights decayed, biases not)
        for (int l = 0; l < w.SharedLayers; l++)
        {
            nanFixCount += AdamWStep2D(w.SharedW[l], adam.MSharedW[l], adam.VSharedW[l], grad.SharedW[l], lr, bc1, bc2, wd);
            nanFixCount += AdamStepArr(w.SharedB[l], adam.MSharedB[l], adam.VSharedB[l], grad.SharedB[l], lr, bc1, bc2);
            nanFixCount += AdamWStep2D(w.SharedGW[l], adam.MSharedGW[l], adam.VSharedGW[l], grad.SharedGW[l], lr, bc1, bc2, wd);
            nanFixCount += AdamStepArr(w.SharedGB[l], adam.MSharedGB[l], adam.VSharedGB[l], grad.SharedGB[l], lr, bc1, bc2);
        }

        // Step-specific layers
        for (int s = 0; s < w.NSteps; s++)
        {
            for (int l = 0; l < w.StepLayers; l++)
            {
                nanFixCount += AdamWStep2D(w.StepW[s][l], adam.MStepW[s][l], adam.VStepW[s][l], grad.StepW[s][l], lr, bc1, bc2, wd);
                nanFixCount += AdamStepArr(w.StepB[s][l], adam.MStepB[s][l], adam.VStepB[s][l], grad.StepB[s][l], lr, bc1, bc2);
                nanFixCount += AdamWStep2D(w.StepGW[s][l], adam.MStepGW[s][l], adam.VStepGW[s][l], grad.StepGW[s][l], lr, bc1, bc2, wd);
                nanFixCount += AdamStepArr(w.StepGB[s][l], adam.MStepGB[s][l], adam.VStepGB[s][l], grad.StepGB[s][l], lr, bc1, bc2);
            }

            nanFixCount += AdamWStep2D(w.AttnFcW[s], adam.MAttnFcW[s], adam.VAttnFcW[s], grad.AttnFcW[s], lr, bc1, bc2, wd);
            nanFixCount += AdamStepArr(w.AttnFcB[s], adam.MAttnFcB[s], adam.VAttnFcB[s], grad.AttnFcB[s], lr, bc1, bc2);
        }

        // BN params — never weight-decayed
        for (int b = 0; b < w.TotalBnLayers; b++)
        {
            nanFixCount += AdamStepArr(w.BnGamma[b], adam.MBnGamma[b], adam.VBnGamma[b], grad.BnGamma[b], lr, bc1, bc2);
            nanFixCount += AdamStepArr(w.BnBeta[b], adam.MBnBeta[b], adam.VBnBeta[b], grad.BnBeta[b], lr, bc1, bc2);
        }

        return nanFixCount;
    }

    private static int AdamStep(ref double param, ref double m, ref double v, double g, double lr, double bc1, double bc2)
    {
        int nanFixCount = 0;
        if (!double.IsFinite(g)) { g = 0; nanFixCount++; }
        m = AdamBeta1 * m + (1 - AdamBeta1) * g;
        v = AdamBeta2 * v + (1 - AdamBeta2) * g * g;
        param -= lr * (m / bc1) / (Math.Sqrt(v / bc2) + AdamEpsilon);
        if (!double.IsFinite(param)) { param = 0; nanFixCount++; }
        else param = Math.Clamp(param, -MaxWeightVal, MaxWeightVal);
        return nanFixCount;
    }

    private static int AdamStepArr(double[] param, double[] m, double[] v, double[] g, double lr, double bc1, double bc2)
    {
        int nanFixCount = 0;
        for (int j = 0; j < param.Length; j++)
        {
            double gj = g[j];
            if (!double.IsFinite(gj)) { gj = 0; nanFixCount++; }
            m[j] = AdamBeta1 * m[j] + (1 - AdamBeta1) * gj;
            v[j] = AdamBeta2 * v[j] + (1 - AdamBeta2) * gj * gj;
            param[j] -= lr * (m[j] / bc1) / (Math.Sqrt(v[j] / bc2) + AdamEpsilon);
            if (!double.IsFinite(param[j])) { param[j] = 0; nanFixCount++; }
            else param[j] = Math.Clamp(param[j], -MaxWeightVal, MaxWeightVal);
        }
        return nanFixCount;
    }

    /// <summary>AdamW array step: decoupled weight decay before Adam update.</summary>
    private static int AdamWStepArr(double[] param, double[] m, double[] v, double[] g, double lr, double bc1, double bc2, double wd)
    {
        if (wd > 0)
            for (int j = 0; j < param.Length; j++)
                param[j] *= (1.0 - lr * wd);

        return AdamStepArr(param, m, v, g, lr, bc1, bc2);
    }

    private static int AdamWStep2D(double[][] param, double[][] m, double[][] v, double[][] g, double lr, double bc1, double bc2, double wd)
    {
        int nanFixCount = 0;
        for (int i = 0; i < param.Length; i++)
            nanFixCount += AdamWStepArr(param[i], m[i], v[i], g[i], lr, bc1, bc2, wd);
        return nanFixCount;
    }

    private static int AdamStep2D(double[][] param, double[][] m, double[][] v, double[][] g, double lr, double bc1, double bc2)
    {
        int nanFixCount = 0;
        for (int i = 0; i < param.Length; i++)
            nanFixCount += AdamStepArr(param[i], m[i], v[i], g[i], lr, bc1, bc2);
        return nanFixCount;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GRADIENT UTILITIES
    // ═══════════════════════════════════════════════════════════════════════

    private static void ScaleGradients(TabNetWeights grad, double scale)
    {
        if (grad.InitialBnFcW.Length > 0) { Scale2D(grad.InitialBnFcW, scale); ScaleArr(grad.InitialBnFcB, scale); }
        ScaleArr(grad.OutputW, scale);
        grad.OutputB *= scale;
        if (grad.MagW.Length > 0) { ScaleArr(grad.MagW, scale); grad.MagB *= scale; }
        foreach (var l in grad.SharedW) Scale2D(l, scale);
        foreach (var l in grad.SharedB) ScaleArr(l, scale);
        foreach (var l in grad.SharedGW) Scale2D(l, scale);
        foreach (var l in grad.SharedGB) ScaleArr(l, scale);
        foreach (var s in grad.StepW) foreach (var l in s) Scale2D(l, scale);
        foreach (var s in grad.StepB) foreach (var l in s) ScaleArr(l, scale);
        foreach (var s in grad.StepGW) foreach (var l in s) Scale2D(l, scale);
        foreach (var s in grad.StepGB) foreach (var l in s) ScaleArr(l, scale);
        foreach (var s in grad.AttnFcW) Scale2D(s, scale);
        foreach (var s in grad.AttnFcB) ScaleArr(s, scale);
        foreach (var b in grad.BnGamma) ScaleArr(b, scale);
        foreach (var b in grad.BnBeta) ScaleArr(b, scale);
    }

    private static void ClipGradients(TabNetWeights grad, double maxNorm)
    {
        double sqNorm = 0;
        if (grad.InitialBnFcW.Length > 0) { sqNorm += SqNorm2D(grad.InitialBnFcW) + SqNormArr(grad.InitialBnFcB); }
        sqNorm += SqNormArr(grad.OutputW) + grad.OutputB * grad.OutputB;
        if (grad.MagW.Length > 0) sqNorm += SqNormArr(grad.MagW) + grad.MagB * grad.MagB;
        foreach (var l in grad.SharedW) sqNorm += SqNorm2D(l);
        foreach (var l in grad.SharedB) sqNorm += SqNormArr(l);
        foreach (var l in grad.SharedGW) sqNorm += SqNorm2D(l);
        foreach (var l in grad.SharedGB) sqNorm += SqNormArr(l);
        foreach (var s in grad.StepW) foreach (var l in s) sqNorm += SqNorm2D(l);
        foreach (var s in grad.StepB) foreach (var l in s) sqNorm += SqNormArr(l);
        foreach (var s in grad.StepGW) foreach (var l in s) sqNorm += SqNorm2D(l);
        foreach (var s in grad.StepGB) foreach (var l in s) sqNorm += SqNormArr(l);
        foreach (var s in grad.AttnFcW) sqNorm += SqNorm2D(s);
        foreach (var s in grad.AttnFcB) sqNorm += SqNormArr(s);

        double norm = Math.Sqrt(sqNorm);
        if (norm > maxNorm)
            ScaleGradients(grad, maxNorm / norm);
    }

    private static void ZeroGradients(TabNetWeights grad)
    {
        if (grad.InitialBnFcW.Length > 0) { foreach (var r in grad.InitialBnFcW) Array.Clear(r); Array.Clear(grad.InitialBnFcB); }
        Array.Clear(grad.OutputW); grad.OutputB = 0;
        if (grad.MagW.Length > 0) { Array.Clear(grad.MagW); grad.MagB = 0; }
        foreach (var l in grad.SharedW) foreach (var r in l) Array.Clear(r);
        foreach (var l in grad.SharedB) Array.Clear(l);
        foreach (var l in grad.SharedGW) foreach (var r in l) Array.Clear(r);
        foreach (var l in grad.SharedGB) Array.Clear(l);
        foreach (var s in grad.StepW) foreach (var l in s) foreach (var r in l) Array.Clear(r);
        foreach (var s in grad.StepB) foreach (var l in s) Array.Clear(l);
        foreach (var s in grad.StepGW) foreach (var l in s) foreach (var r in l) Array.Clear(r);
        foreach (var s in grad.StepGB) foreach (var l in s) Array.Clear(l);
        foreach (var s in grad.AttnFcW) foreach (var r in s) Array.Clear(r);
        foreach (var s in grad.AttnFcB) Array.Clear(s);
        foreach (var b in grad.BnGamma) Array.Clear(b);
        foreach (var b in grad.BnBeta) Array.Clear(b);
    }

    private static double SqNormArr(double[] arr) { double s = 0; foreach (double v in arr) s += v * v; return s; }
    private static double SqNorm2D(double[][] arr) { double s = 0; foreach (var r in arr) s += SqNormArr(r); return s; }
    private static void ScaleArr(double[] arr, double s) { for (int i = 0; i < arr.Length; i++) arr[i] *= s; }
    private static void Scale2D(double[][] arr, double s) { foreach (var r in arr) ScaleArr(r, s); }
}
