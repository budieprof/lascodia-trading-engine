using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  GPU-ACCELERATED TABNET TRAINING (TorchSharp)
    //
    //  Mirrors the CPU FitTabNet logic using TorchSharp autograd for
    //  automatic differentiation. The trained weights are extracted back
    //  into TabNetWeights so all downstream code (calibration, evaluation,
    //  audit, snapshot) works unchanged on CPU.
    //
    //  Falls back to CPU FitTabNet when CUDA is unavailable or the dataset
    //  is too small to benefit from GPU overhead.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimum training samples for GPU path. Set conservatively low because even small
    /// batches benefit from GPU parallelism across the TabNet decision steps and FC layers.
    /// The real overhead is one-time device init, not per-batch transfer.
    /// </summary>
    private const int GpuMinSamples = 64;

    private static bool IsGpuAvailable()
    {
        try { return torch.cuda.is_available(); }
        catch { return false; }
    }

    /// <summary>
    /// GPU-accelerated TabNet fitting. Mirrors the CPU FitTabNet contract exactly:
    /// same inputs, same TabNetWeights output, same training semantics.
    /// </summary>
    private TabNetWeights FitTabNetGpu(
        List<TrainingSample> trainSet,
        int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers,
        double gamma, bool useSparsemax, bool useGlu,
        double baseLr, double sparsityCoeff, int maxEpochs,
        double labelSmoothing,
        ModelSnapshot? warmStart, TabNetWeights? pretrainedInit,
        double[]? densityWeights, double temporalDecayLambda,
        double l2Lambda, int patience, double magLossWeight,
        double maxGradNorm, double dropoutRate, double bnMomentum,
        int ghostBatchSize, int warmupEpochs, TabNetRunContext runContext, CancellationToken ct)
    {
        var device = torch.CUDA;
        int n = trainSet.Count;
        bool useMagHead = magLossWeight > 0.0;

        _logger.LogInformation(
            "TabNet GPU training: n={N} F={F} steps={S} hidden={H} device={Dev}",
            n, F, nSteps, hiddenDim, "CUDA");

        // ── 1. Prepare data tensors ──────────────────────────────────────────
        var (xTrain, yTrain, magTrain, sampleWeights) = PrepareTensors(
            trainSet, F, densityWeights, temporalDecayLambda, device);

        // ── 2. Validation split (last 10%) ───────────────────────────────────
        int nFit = (int)(n * 0.90);
        int nVal = n - nFit;

        // ── 3. Allocate TabNet parameters on GPU ─────────────────────────────
        var rng = new Random(TrainerSeed);
        var parms = new TabNetGpuParams(F, nSteps, hiddenDim, attentionDim,
            sharedLayers, stepLayers, useGlu, useMagHead, device, rng);

        // Load warm-start weights if compatible
        if (pretrainedInit is not null)
            LoadPretrainedWeightsToGpu(pretrainedInit, parms, device);

        // ── 4. Optimizer + LR schedule ───────────────────────────────────────
        using var optimizer = optim.AdamW(
            parms.AllParameters(),
            lr: baseLr,
            beta1: AdamBeta1, beta2: AdamBeta2, eps: AdamEpsilon,
            weight_decay: l2Lambda);

        // ── 5. Training loop ─────────────────────────────────────────────────
        double bestValLoss = double.MaxValue;
        int bestEpoch = 0;
        int earlyCount = 0;
        float[][]? bestWeightSnapshot = null;

        var indices = Enumerable.Range(0, nFit).ToArray();
        int batchSize = Math.Max(DefaultBatchSize, 1);

        try
        {
            for (int ep = 0; ep < maxEpochs && !ct.IsCancellationRequested; ep++)
            {
                // Cosine LR with optional warmup
                double cosLr;
                if (warmupEpochs > 0 && ep < warmupEpochs)
                    cosLr = baseLr * (0.1 + 0.9 * ep / warmupEpochs);
                else
                {
                    int decayEp = ep - warmupEpochs;
                    int decayTotal = maxEpochs - warmupEpochs;
                    cosLr = decayTotal > 0
                        ? baseLr * 0.5 * (1.0 + Math.Cos(Math.PI * decayEp / decayTotal))
                        : baseLr;
                }

                // Update optimizer LR
                foreach (var pg in optimizer.ParamGroups)
                    pg.LearningRate = cosLr;

                // Shuffle
                var epochRng = new Random(TrainerSeed + ep);
                for (int i = indices.Length - 1; i > 0; i--)
                {
                    int k = epochRng.Next(i + 1);
                    (indices[k], indices[i]) = (indices[i], indices[k]);
                }

                double epochLoss = 0;

                // Mini-batch loop
                for (int bStart = 0; bStart < nFit; bStart += batchSize)
                {
                    int bEnd = Math.Min(bStart + batchSize, nFit);
                    int bsz = bEnd - bStart;

                    // Gather batch indices
                    var batchIdx = new long[bsz];
                    for (int i = 0; i < bsz; i++) batchIdx[i] = indices[bStart + i];
                    using var idxT = torch.tensor(batchIdx, device: device);

                    using var xB = xTrain.index_select(0, idxT);
                    using var yB = yTrain.index_select(0, idxT);
                    using var wB = sampleWeights.index_select(0, idxT);

                    optimizer.zero_grad();

                    // Forward pass
                    var (prob, aggH, totalSparsity) = TabNetForwardGpu(
                        xB, parms, nSteps, gamma, useSparsemax, useGlu,
                        training: true, dropoutRate, bnMomentum);

                    // Smoothed labels
                    using var ySmooth = labelSmoothing > 0
                        ? yB * (float)(1.0 - labelSmoothing) + (float)(0.5 * labelSmoothing)
                        : yB.alias();

                    // BCE loss (weighted)
                    using var probClamped = prob.clamp((float)ProbClampMin, (float)(1.0 - ProbClampMin));
                    using var bce = -(ySmooth * probClamped.log() + (1f - ySmooth) * (1f - probClamped).log());
                    using var weightedBce = (bce * wB).mean();

                    // Sparsity regularisation
                    using var sparsityLoss = totalSparsity * (float)sparsityCoeff;

                    // Total loss
                    using var loss = weightedBce + sparsityLoss;

                    // Magnitude head
                    if (useMagHead)
                    {
                        using var magB = magTrain.index_select(0, idxT);
                        using var magPred = torch.mm(aggH, parms.MagW.t()) + parms.MagB;
                        using var magErr = magPred.squeeze(1) - magB;
                        using var absErr = magErr.abs();
                        using var huber = torch.where(
                            absErr <= (float)runContext.HuberDelta,
                            magErr.pow(2f) * 0.5f,
                            absErr * (float)runContext.HuberDelta - (float)(0.5 * runContext.HuberDelta * runContext.HuberDelta)
                        ).mean() * (float)magLossWeight;
                        using var totalLoss = loss + huber;
                        totalLoss.backward();
                    }
                    else
                    {
                        loss.backward();
                    }

                    // Gradient clipping
                    if (maxGradNorm > 0)
                        torch.nn.utils.clip_grad_norm_(parms.AllParameters(), maxGradNorm);

                    optimizer.step();

                    epochLoss += loss.item<float>() * bsz;

                    // Dispose forward pass outputs not covered by using declarations
                    prob.Dispose();
                    aggH.Dispose();
                    totalSparsity.Dispose();
                }

                // ── Validation (every 5 epochs) ─────────────────────────────
                if (nVal >= runContext.MinCalibrationSamples && ep % 5 == 4)
                {
                    using (no_grad())
                    {
                        using var xVal = xTrain.narrow(0, nFit, nVal);
                        using var yVal = yTrain.narrow(0, nFit, nVal);
                        var (vProb, vAggH, vSparsity) = TabNetForwardGpu(
                            xVal, parms, nSteps, gamma, useSparsemax, useGlu,
                            training: false, 0, bnMomentum);
                        vAggH.Dispose(); vSparsity.Dispose();
                        using var vProbC = vProb.clamp((float)ProbClampMin, (float)(1.0 - ProbClampMin));
                        vProb.Dispose();
                        using var vBce = -(yVal * vProbC.log() + (1f - yVal) * (1f - vProbC).log()).mean();
                        double valLoss = vBce.item<float>();

                        if (valLoss < bestValLoss - EarlyStopMinDelta)
                        {
                            bestValLoss = valLoss;
                            bestEpoch = ep;
                            bestWeightSnapshot = SnapshotGpuParams(parms);
                            earlyCount = 0;
                        }
                        else if (++earlyCount >= Math.Max(3, patience / 5))
                        {
                            _logger.LogDebug("TabNet GPU early stopping at epoch {E} (best at {Best})", ep, bestEpoch);
                            break;
                        }
                    }
                }

                if (ep % 10 == 9)
                    _logger.LogDebug("TabNet GPU epoch {Ep}: loss={Loss:F4} lr={Lr:F5}",
                        ep, epochLoss / nFit, cosLr);
            }
        }
        finally
        {
            // Dispose data tensors
            xTrain.Dispose();
            yTrain.Dispose();
            magTrain.Dispose();
            sampleWeights.Dispose();
        }

        // ── 6. Extract trained weights → TabNetWeights ───────────────────────
        if (bestWeightSnapshot is not null)
            RestoreGpuParams(parms, bestWeightSnapshot);

        var weights = ExtractGpuWeights(parms, F, nSteps, hiddenDim, attentionDim,
            sharedLayers, stepLayers, gamma, useSparsemax, useGlu, useMagHead);

        parms.Dispose();

        _logger.LogInformation("TabNet GPU training complete (best epoch {Best})", bestEpoch);

        // Track warm-start report (no warm-start reuse tracking in GPU path)
        runContext.WarmStartLoadReport = new TabNetSnapshotSupport.WarmStartLoadReport(0, 0, 0, 0, 0);

        return weights;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DATA PREPARATION
    // ═══════════════════════════════════════════════════════════════════════

    private static (Tensor X, Tensor Y, Tensor Mag, Tensor Weights) PrepareTensors(
        List<TrainingSample> samples, int F,
        double[]? densityWeights, double temporalDecayLambda,
        Device device)
    {
        int n = samples.Count;
        var xArr = new float[n * F];
        var yArr = new float[n];
        var magArr = new float[n];
        var wArr = new float[n];

        var tempWeights = ComputeTemporalWeights(n, temporalDecayLambda);

        for (int i = 0; i < n; i++)
        {
            var s = samples[i];
            for (int j = 0; j < F && j < s.Features.Length; j++)
                xArr[i * F + j] = s.Features[j];
            yArr[i] = s.Direction > 0 ? 1f : 0f;
            magArr[i] = s.Magnitude;
            double w = tempWeights[i];
            if (densityWeights is not null && i < densityWeights.Length)
                w *= densityWeights[i];
            wArr[i] = (float)w;
        }

        return (
            torch.tensor(xArr, device: device).reshape(n, F),
            torch.tensor(yArr, device: device),
            torch.tensor(magArr, device: device),
            torch.tensor(wArr, device: device)
        );
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GPU FORWARD PASS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full TabNet forward pass on GPU tensors. Returns (probabilities [B], aggregatedH [B,H], sparsityLoss scalar).
    /// </summary>
    private static (Tensor Prob, Tensor AggH, Tensor SparsityLoss) TabNetForwardGpu(
        Tensor x, TabNetGpuParams p, int nSteps, double gamma,
        bool useSparsemax, bool useGlu, bool training, double dropoutRate, double bnMomentum)
    {
        int B = (int)x.shape[0];
        int F = (int)x.shape[1];
        int H = p.HiddenDim;

        var priorScales = torch.ones(new long[] { B, F }, device: x.device);
        var aggH = torch.zeros(new long[] { B, H }, device: x.device);
        var sparsityLoss = torch.tensor(0f, device: x.device);
        Tensor hPrev = torch.zeros(new long[] { B, H }, device: x.device);

        for (int s = 0; s < nSteps; s++)
        {
            // ── Attentive Transformer ────────────────────────────────────
            Tensor attnInput;
            if (s == 0)
            {
                // Step-0: learnable initial projection
                attnInput = torch.mm(x, p.InitialBnFcW.t()) + p.InitialBnFcB;
            }
            else
            {
                attnInput = torch.mm(hPrev, p.AttnFcW[s].t()) + p.AttnFcB[s];
            }

            // BatchNorm on attention input
            var bnAttn = GpuBatchNorm(attnInput, p.BnGamma[s], p.BnBeta[s],
                p.BnRunMean[s], p.BnRunVar[s], training, bnMomentum);

            // Apply prior scales
            using var scaledLogits = bnAttn * priorScales;

            // Sparsemax or Softmax
            Tensor attn;
            if (useSparsemax)
                attn = GpuSparsemax(scaledLogits);
            else
                attn = functional.softmax(scaledLogits, dim: 1);

            // Sparsity regularisation: -sum(attn * log(attn + eps))
            using var attnSparsity = -(attn * (attn + (float)Eps).log()).sum() / (B * nSteps);
            var newSparsityLoss = sparsityLoss + attnSparsity;
            sparsityLoss.Dispose();
            sparsityLoss = newSparsityLoss;

            // Update prior scales: prior *= (gamma - attn)
            using var gammaMinusAttn = ((float)gamma - attn).clamp_min(1e-6f);
            var newPrior = priorScales * gammaMinusAttn;
            priorScales.copy_(newPrior);
            newPrior.Dispose();

            // ── Feature masking ──────────────────────────────────────────
            using var masked = x * attn;

            // ── Shared Feature Transformer ───────────────────────────────
            Tensor h = masked;
            int inputDim = F;
            for (int l = 0; l < p.SharedLayers; l++)
            {
                int bnIdx = nSteps + l;
                var hNew = GpuFcBnGlu(h, inputDim, H,
                    p.SharedW[l], p.SharedB[l],
                    useGlu ? p.SharedGW[l] : null, useGlu ? p.SharedGB[l] : null,
                    p.BnGamma[bnIdx], p.BnBeta[bnIdx],
                    p.BnRunMean[bnIdx], p.BnRunVar[bnIdx],
                    training, bnMomentum, dropoutRate, useGlu);
                if (l > 0)
                {
                    // Residual with sqrt(0.5) scaling
                    var residual = (hNew + h) * (float)SqrtHalfResidualScale;
                    hNew.Dispose();
                    h.Dispose();
                    h = residual;
                }
                else
                {
                    if (!ReferenceEquals(h, masked)) h.Dispose();
                    h = hNew;
                }
                inputDim = H;
            }

            // ── Step-specific Feature Transformer ────────────────────────
            for (int l = 0; l < p.StepLayers; l++)
            {
                int bnIdx = nSteps + p.SharedLayers + s * p.StepLayers + l;
                var hNew = GpuFcBnGlu(h, H, H,
                    p.StepW[s][l], p.StepB[s][l],
                    useGlu ? p.StepGW[s][l] : null, useGlu ? p.StepGB[s][l] : null,
                    p.BnGamma[bnIdx], p.BnBeta[bnIdx],
                    p.BnRunMean[bnIdx], p.BnRunVar[bnIdx],
                    training, bnMomentum, dropoutRate, useGlu);
                if (l > 0)
                {
                    var residual = (hNew + h) * (float)SqrtHalfResidualScale;
                    hNew.Dispose();
                    h.Dispose();
                    h = residual;
                }
                else
                {
                    h.Dispose();
                    h = hNew;
                }
            }

            // ReLU gate and aggregate
            using var reluH = functional.relu(h);
            var newAgg = aggH + reluH;
            aggH.Dispose();
            aggH = newAgg;

            // Update hPrev for next step (clone to decouple storage from h before disposing h)
            var newHPrev = h.detach().clone();
            hPrev.Dispose();
            hPrev = newHPrev;

            // Cleanup
            attnInput.Dispose();
            bnAttn.Dispose();
            attn.Dispose();
            h.Dispose();
        }

        hPrev.Dispose();
        priorScales.Dispose();

        // ── Output head ──────────────────────────────────────────────────
        using var logit = torch.mm(aggH, p.OutputW.t()) + p.OutputB;
        var prob = torch.sigmoid(logit).squeeze(1); // [B]

        return (prob, aggH, sparsityLoss);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GPU BUILDING BLOCKS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>FC → BN → GLU block on GPU tensors.</summary>
    private static Tensor GpuFcBnGlu(
        Tensor input, int inDim, int outDim,
        Parameter fcW, Parameter fcB,
        Parameter? gateW, Parameter? gateB,
        Parameter bnGamma, Parameter bnBeta,
        Tensor bnRunMean, Tensor bnRunVar,
        bool training, double bnMomentum, double dropoutRate, bool useGlu)
    {
        // FC: [B, inDim] @ [outDim, inDim]^T + bias
        using var linear = torch.mm(input, fcW.t()) + fcB;

        // BatchNorm
        var bnOut = GpuBatchNorm(linear, bnGamma, bnBeta, bnRunMean, bnRunVar, training, bnMomentum);

        Tensor output;
        if (useGlu && gateW is not null && gateB is not null)
        {
            // GLU: output = BN(FC(x)) * sigmoid(GateFC(x))
            using var gateLinear = torch.mm(input, gateW.t()) + gateB;
            using var gateSigmoid = torch.sigmoid(gateLinear);
            output = bnOut * gateSigmoid;
            bnOut.Dispose();
        }
        else
        {
            output = bnOut;
        }

        // Dropout
        if (training && dropoutRate > 0)
        {
            var dropped = functional.dropout(output, dropoutRate, training);
            output.Dispose();
            output = dropped;
        }

        return output;
    }

    /// <summary>Manual batch norm compatible with ghost BN semantics.</summary>
    private static Tensor GpuBatchNorm(
        Tensor input, Parameter gamma, Parameter beta,
        Tensor runMean, Tensor runVar,
        bool training, double momentum)
    {
        if (training)
        {
            using var mean = input.mean(new long[] { 0 });
            using var variance = input.var(new long[] { 0 }, unbiased: false);

            // Update running stats (EMA)
            using (no_grad())
            {
                runMean.mul_((float)(1.0 - momentum)).add_(mean * (float)momentum);
                runVar.mul_((float)(1.0 - momentum)).add_(variance * (float)momentum);
            }

            using var xNorm = (input - mean) / (variance + (float)BnEpsilon).sqrt();
            return xNorm * gamma + beta;
        }
        else
        {
            using var xNorm = (input - runMean) / (runVar + (float)BnEpsilon).sqrt();
            return xNorm * gamma + beta;
        }
    }

    /// <summary>
    /// GPU sparsemax (Martins &amp; Astudillo 2016). Differentiable through autograd
    /// since all ops (sort, cumsum, clamp) have defined gradients.
    /// Falls back to softmax when all entries are zeroed.
    /// </summary>
    private static Tensor GpuSparsemax(Tensor z)
    {
        int F = (int)z.shape[1];
        var sortResult = z.sort(dim: 1, descending: true);
        using var sorted = sortResult.Values;
        sortResult.Indices.Dispose();
        using var cumSum = sorted.cumsum(dim: 1);
        using var range = torch.arange(1, F + 1, dtype: z.dtype, device: z.device).unsqueeze(0);
        using var test = sorted - (cumSum - 1f) / range;
        using var kMask = test.gt(torch.tensor(0f, device: z.device));
        using var kFloat = kMask.to_type(ScalarType.Float32).sum(dim: 1, keepdim: true);
        using var kLong = kFloat.to_type(ScalarType.Int64).clamp_min(1);
        using var cumSumAtK = cumSum.gather(1, kLong - 1);
        using var tau = (cumSumAtK - 1f) / kFloat.clamp_min(1f);
        var output = (z - tau).clamp_min(0f);

        // Fallback: if all entries zero, use softmax (smooth, gradient-friendly)
        using var sum = output.sum(dim: 1, keepdim: true);
        using var zeroMask = sum.lt(torch.tensor((float)Eps, device: z.device));
        bool hasZeros = zeroMask.any().item<bool>();
        if (hasZeros)
        {
            using var softmaxFallback = functional.softmax(z, dim: 1);
            var merged = torch.where(zeroMask, softmaxFallback, output);
            output.Dispose();
            return merged;
        }

        return output;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GPU PARAMETER CONTAINER
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds all TabNet trainable parameters as TorchSharp Parameter objects on the target device.
    /// Mirrors the structure of TabNetWeights but in GPU-resident tensor form.
    /// </summary>
    private sealed class TabNetGpuParams : IDisposable
    {
        public readonly int HiddenDim;
        public readonly int AttentionDim;
        public readonly int SharedLayers;
        public readonly int StepLayers;
        public readonly int NSteps;
        public readonly int TotalBnLayers;

        // Initial BN FC
        public readonly Parameter InitialBnFcW;
        public readonly Parameter InitialBnFcB;

        // Shared feature transformer
        public readonly Parameter[] SharedW;
        public readonly Parameter[] SharedB;
        public readonly Parameter[] SharedGW;
        public readonly Parameter[] SharedGB;

        // Step-specific feature transformer
        public readonly Parameter[][] StepW;
        public readonly Parameter[][] StepB;
        public readonly Parameter[][] StepGW;
        public readonly Parameter[][] StepGB;

        // Attention FC
        public readonly Parameter[] AttnFcW;
        public readonly Parameter[] AttnFcB;

        // BN parameters
        public readonly Parameter[] BnGamma;
        public readonly Parameter[] BnBeta;
        public readonly Tensor[] BnRunMean;
        public readonly Tensor[] BnRunVar;

        // Output head
        public readonly Parameter OutputW;
        public readonly Parameter OutputB;

        // Magnitude head
        public readonly Parameter MagW;
        public readonly Parameter MagB;

        public TabNetGpuParams(int F, int nSteps, int hiddenDim, int attentionDim,
            int sharedLayers, int stepLayers, bool useGlu, bool useMagHead,
            Device device, Random rng)
        {
            HiddenDim = hiddenDim;
            AttentionDim = attentionDim;
            SharedLayers = sharedLayers;
            StepLayers = stepLayers;
            NSteps = nSteps;
            TotalBnLayers = nSteps + sharedLayers + nSteps * stepLayers;

            float xavierScale(int fanIn, int fanOut) => (float)Math.Sqrt(2.0 / (fanIn + fanOut));

            Tensor xavierInit(int rows, int cols)
            {
                float scale = xavierScale(rows, cols);
                return torch.randn(rows, cols, device: device) * scale;
            }

            // Initial BN FC (F→F)
            InitialBnFcW = new Parameter(xavierInit(F, F));
            InitialBnFcB = new Parameter(torch.zeros(F, device: device));

            // Shared layers
            SharedW = new Parameter[sharedLayers];
            SharedB = new Parameter[sharedLayers];
            SharedGW = new Parameter[sharedLayers];
            SharedGB = new Parameter[sharedLayers];
            for (int l = 0; l < sharedLayers; l++)
            {
                int inDim = l == 0 ? F : hiddenDim;
                SharedW[l] = new Parameter(xavierInit(hiddenDim, inDim));
                SharedB[l] = new Parameter(torch.zeros(hiddenDim, device: device));
                SharedGW[l] = useGlu
                    ? new Parameter(xavierInit(hiddenDim, inDim))
                    : new Parameter(torch.zeros(hiddenDim, inDim, device: device), requires_grad: false);
                SharedGB[l] = useGlu
                    ? new Parameter(torch.zeros(hiddenDim, device: device))
                    : new Parameter(torch.zeros(hiddenDim, device: device), requires_grad: false);
            }

            // Step layers
            StepW = new Parameter[nSteps][];
            StepB = new Parameter[nSteps][];
            StepGW = new Parameter[nSteps][];
            StepGB = new Parameter[nSteps][];
            for (int s = 0; s < nSteps; s++)
            {
                StepW[s] = new Parameter[stepLayers];
                StepB[s] = new Parameter[stepLayers];
                StepGW[s] = new Parameter[stepLayers];
                StepGB[s] = new Parameter[stepLayers];
                for (int l = 0; l < stepLayers; l++)
                {
                    StepW[s][l] = new Parameter(xavierInit(hiddenDim, hiddenDim));
                    StepB[s][l] = new Parameter(torch.zeros(hiddenDim, device: device));
                    StepGW[s][l] = useGlu
                        ? new Parameter(xavierInit(hiddenDim, hiddenDim))
                        : new Parameter(torch.zeros(hiddenDim, hiddenDim, device: device), requires_grad: false);
                    StepGB[s][l] = useGlu
                        ? new Parameter(torch.zeros(hiddenDim, device: device))
                        : new Parameter(torch.zeros(hiddenDim, device: device), requires_grad: false);
                }
            }

            // Attention FC
            AttnFcW = new Parameter[nSteps];
            AttnFcB = new Parameter[nSteps];
            for (int s = 0; s < nSteps; s++)
            {
                AttnFcW[s] = new Parameter(xavierInit(F, attentionDim));
                AttnFcB[s] = new Parameter(torch.zeros(F, device: device));
            }

            // BN params
            BnGamma = new Parameter[TotalBnLayers];
            BnBeta = new Parameter[TotalBnLayers];
            BnRunMean = new Tensor[TotalBnLayers];
            BnRunVar = new Tensor[TotalBnLayers];
            for (int b = 0; b < TotalBnLayers; b++)
            {
                int dim = b < nSteps ? F : hiddenDim;
                BnGamma[b] = new Parameter(torch.ones(dim, device: device));
                BnBeta[b] = new Parameter(torch.zeros(dim, device: device));
                BnRunMean[b] = torch.zeros(dim, device: device);
                BnRunVar[b] = torch.ones(dim, device: device);
            }

            // Output head
            OutputW = new Parameter(xavierInit(1, hiddenDim));
            OutputB = new Parameter(torch.zeros(1, device: device));

            // Magnitude head
            MagW = useMagHead
                ? new Parameter(xavierInit(1, hiddenDim))
                : new Parameter(torch.zeros(1, hiddenDim, device: device), requires_grad: false);
            MagB = useMagHead
                ? new Parameter(torch.zeros(1, device: device))
                : new Parameter(torch.zeros(1, device: device), requires_grad: false);
        }

        public IEnumerable<Parameter> AllParameters()
        {
            yield return InitialBnFcW;
            yield return InitialBnFcB;
            for (int l = 0; l < SharedLayers; l++)
            {
                yield return SharedW[l]; yield return SharedB[l];
                yield return SharedGW[l]; yield return SharedGB[l];
            }
            for (int s = 0; s < NSteps; s++)
            {
                for (int l = 0; l < StepLayers; l++)
                {
                    yield return StepW[s][l]; yield return StepB[s][l];
                    yield return StepGW[s][l]; yield return StepGB[s][l];
                }
                yield return AttnFcW[s]; yield return AttnFcB[s];
            }
            for (int b = 0; b < TotalBnLayers; b++)
            {
                yield return BnGamma[b]; yield return BnBeta[b];
            }
            yield return OutputW; yield return OutputB;
            yield return MagW; yield return MagB;
        }

        public void Dispose()
        {
            foreach (var p in AllParameters()) p.Dispose();
            for (int b = 0; b < TotalBnLayers; b++)
            {
                BnRunMean[b].Dispose();
                BnRunVar[b].Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT SNAPSHOT / RESTORE (GPU ↔ CPU)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Snapshots all GPU parameter data to CPU float arrays for early-stopping restore.</summary>
    private static float[][] SnapshotGpuParams(TabNetGpuParams p)
    {
        using (no_grad())
        {
            var snaps = new List<float[]>();
            foreach (var param in p.AllParameters())
                snaps.Add(param.cpu().data<float>().ToArray());
            return snaps.ToArray();
        }
    }

    /// <summary>Restores GPU parameters from a CPU snapshot.</summary>
    private static void RestoreGpuParams(TabNetGpuParams p, float[][] snapshot)
    {
        using (no_grad())
        {
            int i = 0;
            foreach (var param in p.AllParameters())
            {
                using var t = torch.tensor(snapshot[i++], device: param.device).reshape(param.shape);
                param.copy_(t);
            }
        }
    }

    /// <summary>Loads CPU TabNetWeights into GPU parameters (warm-start).</summary>
    private static void LoadPretrainedWeightsToGpu(TabNetWeights src, TabNetGpuParams dst, Device device)
    {
        using (no_grad())
        {
            void Load2D(Parameter param, double[][] data, int rows, int cols)
            {
                int r = Math.Min(rows, data.Length);
                var arr = new float[rows * cols];
                for (int i = 0; i < r && i < data.Length; i++)
                    for (int j = 0; j < cols && j < data[i].Length; j++)
                        arr[i * cols + j] = (float)data[i][j];
                using var t = torch.tensor(arr, device: device).reshape(rows, cols);
                param.copy_(t);
            }

            void Load1D(Parameter param, double[] data, int dim)
            {
                var arr = new float[dim];
                for (int j = 0; j < dim && j < data.Length; j++)
                    arr[j] = (float)data[j];
                using var t = torch.tensor(arr, device: device);
                param.copy_(t);
            }

            if (src.InitialBnFcW.Length > 0)
            {
                Load2D(dst.InitialBnFcW, src.InitialBnFcW, src.F, src.F);
                Load1D(dst.InitialBnFcB, src.InitialBnFcB, src.F);
            }

            for (int l = 0; l < Math.Min(src.SharedLayers, dst.SharedLayers); l++)
            {
                int inDim = l == 0 ? src.F : src.HiddenDim;
                Load2D(dst.SharedW[l], src.SharedW[l], src.HiddenDim, inDim);
                Load1D(dst.SharedB[l], src.SharedB[l], src.HiddenDim);
                if (src.UseGlu)
                {
                    Load2D(dst.SharedGW[l], src.SharedGW[l], src.HiddenDim, inDim);
                    Load1D(dst.SharedGB[l], src.SharedGB[l], src.HiddenDim);
                }
            }

            for (int s = 0; s < Math.Min(src.NSteps, dst.NSteps); s++)
            {
                for (int l = 0; l < Math.Min(src.StepLayers, dst.StepLayers); l++)
                {
                    Load2D(dst.StepW[s][l], src.StepW[s][l], src.HiddenDim, src.HiddenDim);
                    Load1D(dst.StepB[s][l], src.StepB[s][l], src.HiddenDim);
                    if (src.UseGlu)
                    {
                        Load2D(dst.StepGW[s][l], src.StepGW[s][l], src.HiddenDim, src.HiddenDim);
                        Load1D(dst.StepGB[s][l], src.StepGB[s][l], src.HiddenDim);
                    }
                }
                Load2D(dst.AttnFcW[s], src.AttnFcW[s], src.F, src.AttentionDim);
                Load1D(dst.AttnFcB[s], src.AttnFcB[s], src.F);
            }

            for (int b = 0; b < Math.Min(src.TotalBnLayers, dst.TotalBnLayers); b++)
            {
                int dim = b < src.NSteps ? src.F : src.HiddenDim;
                Load1D(dst.BnGamma[b], src.BnGamma[b], dim);
                Load1D(dst.BnBeta[b], src.BnBeta[b], dim);
                // BnRunMean/BnRunVar are plain Tensors (not Parameters), load directly
                {
                    var meanArr = new float[dim];
                    for (int j = 0; j < dim && j < src.BnMean[b].Length; j++)
                        meanArr[j] = (float)src.BnMean[b][j];
                    dst.BnRunMean[b].copy_(torch.tensor(meanArr, device: dst.BnRunMean[b].device));

                    var varArr = new float[dim];
                    for (int j = 0; j < dim && j < src.BnVar[b].Length; j++)
                        varArr[j] = (float)src.BnVar[b][j];
                    dst.BnRunVar[b].copy_(torch.tensor(varArr, device: dst.BnRunVar[b].device));
                }
            }
        }
    }

    /// <summary>Extracts GPU parameters → CPU TabNetWeights for downstream pipeline.</summary>
    private static TabNetWeights ExtractGpuWeights(
        TabNetGpuParams p, int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax, bool useGlu,
        bool useMagHead)
    {
        using (no_grad())
        {
            double[][] Extract2D(Parameter param, int rows, int cols)
            {
                var flat = param.cpu().data<float>().ToArray();
                var result = new double[rows][];
                for (int i = 0; i < rows; i++)
                {
                    result[i] = new double[cols];
                    for (int j = 0; j < cols; j++)
                        result[i][j] = flat[i * cols + j];
                }
                return result;
            }

            double[] Extract1D(Parameter param, int dim)
            {
                var flat = param.cpu().data<float>().ToArray();
                var result = new double[dim];
                for (int j = 0; j < dim && j < flat.Length; j++)
                    result[j] = flat[j];
                return result;
            }

            double[] Extract1DTensor(Tensor t, int dim)
            {
                var flat = t.cpu().data<float>().ToArray();
                var result = new double[dim];
                for (int j = 0; j < dim && j < flat.Length; j++)
                    result[j] = flat[j];
                return result;
            }

            int totalBn = nSteps + sharedLayers + nSteps * stepLayers;

            var w = new TabNetWeights
            {
                NSteps = nSteps,
                F = F,
                HiddenDim = hiddenDim,
                AttentionDim = attentionDim,
                SharedLayers = sharedLayers,
                StepLayers = stepLayers,
                Gamma = gamma,
                UseSparsemax = useSparsemax,
                UseGlu = useGlu,
                TotalBnLayers = totalBn,

                InitialBnFcW = Extract2D(p.InitialBnFcW, F, F),
                InitialBnFcB = Extract1D(p.InitialBnFcB, F),

                SharedW = new double[sharedLayers][][],
                SharedB = new double[sharedLayers][],
                SharedGW = new double[sharedLayers][][],
                SharedGB = new double[sharedLayers][],

                StepW = new double[nSteps][][][],
                StepB = new double[nSteps][][],
                StepGW = new double[nSteps][][][],
                StepGB = new double[nSteps][][],

                AttnFcW = new double[nSteps][][],
                AttnFcB = new double[nSteps][],

                BnGamma = new double[totalBn][],
                BnBeta = new double[totalBn][],
                BnMean = new double[totalBn][],
                BnVar = new double[totalBn][],

                OutputW = Extract1D(p.OutputW, hiddenDim),
                OutputB = Extract1D(p.OutputB, 1)[0],

                MagW = useMagHead ? Extract1D(p.MagW, hiddenDim) : [],
                MagB = useMagHead ? Extract1D(p.MagB, 1)[0] : 0.0,
            };

            for (int l = 0; l < sharedLayers; l++)
            {
                int inDim = l == 0 ? F : hiddenDim;
                w.SharedW[l] = Extract2D(p.SharedW[l], hiddenDim, inDim);
                w.SharedB[l] = Extract1D(p.SharedB[l], hiddenDim);
                w.SharedGW[l] = Extract2D(p.SharedGW[l], hiddenDim, inDim);
                w.SharedGB[l] = Extract1D(p.SharedGB[l], hiddenDim);
            }

            for (int s = 0; s < nSteps; s++)
            {
                w.StepW[s] = new double[stepLayers][][];
                w.StepB[s] = new double[stepLayers][];
                w.StepGW[s] = new double[stepLayers][][];
                w.StepGB[s] = new double[stepLayers][];
                for (int l = 0; l < stepLayers; l++)
                {
                    w.StepW[s][l] = Extract2D(p.StepW[s][l], hiddenDim, hiddenDim);
                    w.StepB[s][l] = Extract1D(p.StepB[s][l], hiddenDim);
                    w.StepGW[s][l] = Extract2D(p.StepGW[s][l], hiddenDim, hiddenDim);
                    w.StepGB[s][l] = Extract1D(p.StepGB[s][l], hiddenDim);
                }
                w.AttnFcW[s] = Extract2D(p.AttnFcW[s], F, attentionDim);
                w.AttnFcB[s] = Extract1D(p.AttnFcB[s], F);
            }

            for (int b = 0; b < totalBn; b++)
            {
                int dim = b < nSteps ? F : hiddenDim;
                w.BnGamma[b] = Extract1D(p.BnGamma[b], dim);
                w.BnBeta[b] = Extract1D(p.BnBeta[b], dim);
                w.BnMean[b] = Extract1DTensor(p.BnRunMean[b], dim);
                w.BnVar[b] = Extract1DTensor(p.BnRunVar[b], dim);
            }

            return w;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GPU UNSUPERVISED PRE-TRAINING
    //  Masked autoencoder: mask random features, forward through encoder,
    //  reconstruct via linear decoder, MSE loss on masked features only.
    //  Autograd handles all gradients (encoder + decoder).
    // ═══════════════════════════════════════════════════════════════════════

    private TabNetWeights RunUnsupervisedPretrainingGpu(
        List<TrainingSample> samples, int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax,
        bool useGlu, double lr, int epochs, double maskFraction, double bnMomentum,
        CancellationToken ct)
    {
        var device = torch.CUDA;
        int n = samples.Count;

        _logger.LogInformation("TabNet GPU pre-training: n={N} F={F} epochs={E} mask={M:P0}",
            n, F, epochs, maskFraction);

        // ── Prepare data tensor ──────────────────────────────────────────
        var xArr = new float[n * F];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < F && j < samples[i].Features.Length; j++)
                xArr[i * F + j] = samples[i].Features[j];
        using var xAll = torch.tensor(xArr, device: device).reshape(n, F);

        // ── Allocate encoder params ──────────────────────────────────────
        var rng = new Random(TrainerSeed);
        var parms = new TabNetGpuParams(F, nSteps, hiddenDim, attentionDim,
            sharedLayers, stepLayers, useGlu, false, device, rng);

        // ── Decoder: linear F ← hiddenDim ────────────────────────────────
        float decScale = (float)Math.Sqrt(2.0 / (F + hiddenDim));
        using var decoderW = new Parameter(torch.randn(new long[] { F, hiddenDim }, device: device) * decScale);
        using var decoderB = new Parameter(torch.zeros(F, device: device));

        // ── Optimizer over encoder + decoder ─────────────────────────────
        var allParams = parms.AllParameters().Concat(new[] { decoderW, decoderB });
        using var optimizer = optim.Adam(allParams, lr: lr);

        int batchSize = Math.Max(DefaultBatchSize, 1);
        var indices = Enumerable.Range(0, n).ToArray();

        for (int ep = 0; ep < epochs && !ct.IsCancellationRequested; ep++)
        {
            double cosLr = lr * 0.5 * (1.0 + Math.Cos(Math.PI * ep / epochs));
            foreach (var pg in optimizer.ParamGroups)
                pg.LearningRate = cosLr;

            // Shuffle
            var epochRng = new Random(TrainerSeed + ep);
            for (int i = indices.Length - 1; i > 0; i--)
            {
                int k = epochRng.Next(i + 1);
                (indices[k], indices[i]) = (indices[i], indices[k]);
            }

            double epochLoss = 0;

            for (int bStart = 0; bStart < n; bStart += batchSize)
            {
                int bEnd = Math.Min(bStart + batchSize, n);
                int bsz = bEnd - bStart;

                // Gather batch
                var batchIdx = new long[bsz];
                for (int i = 0; i < bsz; i++) batchIdx[i] = indices[bStart + i];
                using var idxT = torch.tensor(batchIdx, device: device);
                using var xB = xAll.index_select(0, idxT);

                // Generate random mask: 1 = masked (to reconstruct), 0 = visible
                using var maskProbs = torch.rand(new long[] { bsz, F }, device: device);
                using var mask = maskProbs.lt(torch.tensor((float)maskFraction, device: device))
                    .to_type(ScalarType.Float32);

                // Skip if no features masked in entire batch
                using var maskSum = mask.sum();
                if (maskSum.item<float>() < 1f) continue;

                // Masked input: zero out masked features
                using var maskedInput = xB * (1f - mask);

                optimizer.zero_grad();

                // Forward through encoder
                var (prob, aggH, sparsity) = TabNetForwardGpu(
                    maskedInput, parms, nSteps, gamma, useSparsemax, useGlu,
                    training: true, 0, bnMomentum);
                prob.Dispose();
                sparsity.Dispose();

                // Decode: reconstruct all features from aggregated hidden
                using var recon = torch.mm(aggH, decoderW.t()) + decoderB;
                aggH.Dispose();

                // MSE loss on masked features only
                using var reconErr = (recon - xB).pow(2f) * mask;
                using var loss = reconErr.sum() / mask.sum().clamp_min(1f);

                loss.backward();

                // Gradient clipping
                torch.nn.utils.clip_grad_norm_(parms.AllParameters(), 1.0);

                optimizer.step();
                epochLoss += loss.item<float>() * bsz;
            }

            // Update BN running stats every 2 epochs
            if (ep % 2 == 1)
            {
                using (no_grad())
                {
                    // Forward a subsample through encoder to update BN running stats
                    int statN = Math.Min(n, 256);
                    using var xStat = xAll.narrow(0, 0, statN);
                    var (sProb, sAgg, sSp) = TabNetForwardGpu(
                        xStat, parms, nSteps, gamma, useSparsemax, useGlu,
                        training: true, 0, bnMomentum);
                    sProb.Dispose(); sAgg.Dispose(); sSp.Dispose();
                }
            }
        }

        // Extract weights back to CPU
        var weights = ExtractGpuWeights(parms, F, nSteps, hiddenDim, attentionDim,
            sharedLayers, stepLayers, gamma, useSparsemax, useGlu, false);
        parms.Dispose();

        _logger.LogInformation("TabNet GPU pre-training complete ({Epochs} epochs)", epochs);
        return weights;
    }
}
