using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class DannModelTrainer
{

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN MODEL STATE
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete DANN weight state: feature extractor + label classifier + domain classifier.
    /// Inference requires only WFeat, bFeat, wCls, bCls.
    /// </summary>
    private sealed class DannModel
    {
        public int F;
        public int featDim;
        public int domHid;

        // ── Feature extractor Layer 1: F → featDim ────────────────────────────
        public double[][] WFeat;    // [featDim][F]
        public double[]   bFeat;    // [featDim]

        // ── Feature extractor Layer 2: featDim → featDim ──────────────────────
        public double[][] WFeat2;   // [featDim][featDim]
        public double[]   bFeat2;   // [featDim]

        // ── Label classifier: featDim → 1 ─────────────────────────────────────
        public double[] wCls;       // [featDim]
        public double   bCls;

        // ── Domain classifier: featDim → domHid → 1 ───────────────────────────
        public double[][] WDom1;    // [domHid][featDim]
        public double[]   bDom1;    // [domHid]
        public double[]   wDom2;    // [domHid]
        public double     bDom2;

        public DannModel(int f, int featDim, int domHid)
        {
            F            = f;
            this.featDim = featDim;
            this.domHid  = domHid;

            WFeat  = new double[featDim][]; for (int j = 0; j < featDim; j++) WFeat[j]  = new double[f];
            bFeat  = new double[featDim];
            WFeat2 = new double[featDim][]; for (int j = 0; j < featDim; j++) WFeat2[j] = new double[featDim];
            bFeat2 = new double[featDim];
            wCls   = new double[featDim];
            bCls   = 0.0;
            WDom1  = new double[domHid][]; for (int k = 0; k < domHid; k++) WDom1[k] = new double[featDim];
            bDom1  = new double[domHid];
            wDom2  = new double[domHid];
            bDom2  = 0.0;
        }

        /// <summary>Deep-copy for checkpointing best weights during early stopping.</summary>
        public DannModel Clone()
        {
            var c = new DannModel(F, featDim, domHid);
            for (int j = 0; j < featDim; j++) Array.Copy(WFeat[j],  c.WFeat[j],  F);
            Array.Copy(bFeat,  c.bFeat,  featDim);
            for (int j = 0; j < featDim; j++) Array.Copy(WFeat2[j], c.WFeat2[j], featDim);
            Array.Copy(bFeat2, c.bFeat2, featDim);
            Array.Copy(wCls,   c.wCls,   featDim);
            c.bCls = bCls;
            for (int k = 0; k < domHid; k++) Array.Copy(WDom1[k], c.WDom1[k], featDim);
            Array.Copy(bDom1, c.bDom1, domHid);
            Array.Copy(wDom2, c.wDom2, domHid);
            c.bDom2 = bDom2;
            return c;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN PARAMETER HOLDER  (raw Parameters, mirrors SvgpModelTrainer pattern)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Holds raw <see cref="Parameter"/> tensors for the 2-layer DANN architecture.
    /// Feature extractor:  F → featDim (ReLU) → featDim (ReLU)
    /// Label classifier:   featDim → 1
    /// Domain classifier:  featDim → domHid (ReLU) → 1
    /// GRL is applied externally via the detach-trick before calling <see cref="DomainLogit"/>.
    /// </summary>
    private sealed class DannNet : IDisposable
    {
        // ── Feat1: F → featDim ────────────────────────────────────────────────
        public readonly Parameter Feat1W;  // [featDim, F]
        public readonly Parameter Feat1B;  // [featDim]
        // ── Feat2: featDim → featDim ──────────────────────────────────────────
        public readonly Parameter Feat2W;  // [featDim, featDim]
        public readonly Parameter Feat2B;  // [featDim]
        // ── Cls: featDim → 1 ──────────────────────────────────────────────────
        public readonly Parameter ClsW;    // [1, featDim]
        public readonly Parameter ClsB;    // [1]
        // ── Dom1: featDim → domHid, Dom2: domHid → 1 ─────────────────────────
        public readonly Parameter Dom1W;   // [domHid, featDim]
        public readonly Parameter Dom1B;   // [domHid]
        public readonly Parameter Dom2W;   // [1, domHid]
        public readonly Parameter Dom2B;   // [1]

        public readonly int F, FeatDim, DomHid;
        public readonly Device DeviceType;

        /// <summary>
        /// Controls whether dropout is applied in <see cref="Features"/>.
        /// Set to <c>false</c> during validation / evaluation to get deterministic predictions.
        /// </summary>
        public bool Training { get; set; } = true;

        private const float DropoutRate = 0.10f;

        // All parameters, passed directly to torch.optim.Adam
        public Parameter[] AllParams => [Feat1W, Feat1B, Feat2W, Feat2B,
                                          ClsW,   ClsB,   Dom1W,  Dom1B,
                                          Dom2W,  Dom2B];

        public DannNet(int F, int featDim, int domHid, Device device)
        {
            this.F       = F;
            this.FeatDim = featDim;
            this.DomHid  = domHid;
            DeviceType   = device;

            Feat1W = new Parameter(KaimingUniform(featDim, F,       device));
            Feat1B = new Parameter(zeros(featDim,          device: device));
            Feat2W = new Parameter(KaimingUniform(featDim, featDim, device));
            Feat2B = new Parameter(zeros(featDim,          device: device));
            ClsW   = new Parameter(KaimingUniform(1,       featDim, device));
            ClsB   = new Parameter(zeros(1,                device: device));
            Dom1W  = new Parameter(KaimingUniform(domHid,  featDim, device));
            Dom1B  = new Parameter(zeros(domHid,           device: device));
            Dom2W  = new Parameter(KaimingUniform(1,       domHid,  device));
            Dom2B  = new Parameter(zeros(1,                device: device));
        }

        private static Tensor KaimingUniform(int fanOut, int fanIn, Device device)
        {
            float bound = (float)Math.Sqrt(2.0 / fanIn);
            return (torch.rand(fanOut, fanIn, device: device) * 2f - 1f) * bound;
        }

        /// <summary>
        /// Returns hidden representation [B, featDim] after both ReLU layers.
        /// When <see cref="Training"/> is <c>true</c>, applies 10 % dropout after layer-1
        /// to regularise the feature extractor. Dropout is disabled during evaluation.
        /// </summary>
        public Tensor Features(Tensor x)
        {
            var h1 = functional.relu(torch.mm(x, Feat1W.t()) + Feat1B);
            if (Training)
            {
                var h1d = functional.dropout(h1, p: DropoutRate, training: true);
                h1.Dispose();
                h1 = h1d;
            }
            var result = functional.relu(torch.mm(h1, Feat2W.t()) + Feat2B);
            h1.Dispose();
            return result;
        }

        /// <summary>Returns raw classification logit [B, 1].</summary>
        public Tensor ClassifyLogit(Tensor h) => torch.mm(h, ClsW.t()) + ClsB;

        /// <summary>Returns raw domain logit [B, 1]. Pass GRL-transformed features.</summary>
        public Tensor DomainLogit(Tensor hGrl)
        {
            using var hd = functional.relu(torch.mm(hGrl, Dom1W.t()) + Dom1B);
            return torch.mm(hd, Dom2W.t()) + Dom2B;
        }

        /// <summary>Zero out all parameter gradients before each batch.</summary>
        public void ZeroGrad()
        {
            foreach (var p in AllParams) p.grad?.zero_();
        }

        /// <summary>Clip gradient global norm to <paramref name="maxNorm"/>.</summary>
        public void ClipGradNorm(float maxNorm)
        {
            double totalNorm = 0.0;
            foreach (var p in AllParams)
            {
                if (p.grad is { } g)
                    totalNorm += g.pow(2).sum().item<float>();
            }
            double norm = Math.Sqrt(totalNorm);
            if (norm > maxNorm)
            {
                float coeff = (float)(maxNorm / norm);
                foreach (var p in AllParams) p.grad?.mul_(coeff);
            }
        }

        public void Dispose()
        {
            Feat1W.Dispose(); Feat1B.Dispose();
            Feat2W.Dispose(); Feat2B.Dispose();
            ClsW.Dispose();   ClsB.Dispose();
            Dom1W.Dispose();  Dom1B.Dispose();
            Dom2W.Dispose();  Dom2B.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN FITTING  (TorchSharp: 2-layer extractor + GRL + Adam + cosine LR)
    // ═══════════════════════════════════════════════════════════════════════════

    private DannModel FitDann(
        List<TrainingSample> trainSet,
        TrainingHyperparams  hp,
        int                  F,
        int                  featDim,
        int                  domHid,
        double               baseLr,
        int                  maxEpochs,
        double               lamBase,
        double               labelSmoothing,
        ModelSnapshot?       warmStart,
        double[]?            densityWeights,
        CancellationToken    ct)
    {
        int n        = trainSet.Count;
        int batchSz  = Math.Min(DefaultBatch, n);
        int patience = hp.EarlyStoppingPatience > 0 ? hp.EarlyStoppingPatience : 10;

        // Validation split (last 10% of training data)
        int valSize = Math.Max(10, n / 10);
        int trainN  = n - valSize;
        var valSet  = trainSet.GetRange(trainN, valSize);

        // Temporal decay weights for training samples
        double[] tempWeights = ComputeTemporalWeights(trainN, hp.TemporalDecayLambda);

        // Blend with density weights; normalise to mean 1
        double[] sampleWeights = new double[trainN];
        for (int i = 0; i < trainN; i++)
        {
            sampleWeights[i] = tempWeights[i];
            if (densityWeights is not null && i < densityWeights.Length)
                sampleWeights[i] *= densityWeights[i];
            sampleWeights[i] = Math.Max(sampleWeights[i], 1e-8);
        }
        double wSum = sampleWeights.Sum();
        if (wSum > 0) for (int i = 0; i < trainN; i++) sampleWeights[i] = sampleWeights[i] * trainN / wSum;

        // Source/target domain boundary — use BarsPerDay instead of hardcoded 24
        int barsPerDay = hp.BarsPerDay > 0 ? hp.BarsPerDay : 24;
        // When an explicit window is set, the domain boundary is trainN minus that window (older = source,
        // recent = target). When no window is configured, use the last 30% as the target domain so that
        // the GRL always adapts toward recent market conditions rather than splitting arbitrarily at the midpoint.
        int domainBoundary = hp.DensityRatioWindowDays > 0
            ? Math.Max(1, trainN - Math.Min(trainN - 1, hp.DensityRatioWindowDays * barsPerDay))
            : Math.Max(1, trainN - trainN / 3);

        // ── Select compute device (GPU when available, CPU otherwise) ─────────
        var device = torch.cuda.is_available() ? CUDA : CPU;
        _logger.LogInformation("DANN: training on {Device}", device.type);

        // ── Build TorchSharp module ───────────────────────────────────────────
        using var net = new DannNet(F, featDim, domHid, device);

        // Warm-start from parent snapshot if geometry matches
        if (warmStart?.DannWeights is { Length: > 0 } ws)
            TryLoadWarmStartWeights(net, ws, F, featDim, domHid, device, _logger);
        else
            _logger.LogDebug("DANN: cold init (no warm-start).");

        double weightDecay = hp.L2Lambda > 0 ? hp.L2Lambda : 0.0;
        using var opt       = optim.Adam(net.AllParams, lr: baseLr, weight_decay: weightDecay);
        var scheduler = optim.lr_scheduler.CosineAnnealingLR(opt, T_max: maxEpochs, eta_min: baseLr * 0.01);

        var rng     = new Random(42);
        var indices = Enumerable.Range(0, trainN).ToArray();

        double    bestValAcc      = -1.0;
        DannModel? bestModel      = null;
        int        noImproveTicks = 0;

        // Pre-build validation batch tensors once (avoids per-epoch allocation)
        int nVal = valSet.Count;
        var valXArr = new float[nVal * F];
        var valYArr = new int[nVal];
        for (int vi = 0; vi < nVal; vi++)
        {
            Array.Copy(valSet[vi].Features, 0, valXArr, vi * F, F);
            valYArr[vi] = valSet[vi].Direction;
        }

        for (int epoch = 0; epoch < maxEpochs && !ct.IsCancellationRequested; epoch++)
        {
            // Progressive GRL λ: λ(p) = 2/(1+e^{-10p}) − 1,  p ∈ [0,1]
            double p   = (double)epoch / maxEpochs;
            float  lam = (float)(lamBase * (2.0 / (1.0 + Math.Exp(-10.0 * p)) - 1.0));

            // Shuffle training indices
            for (int i = trainN - 1; i > 0; i--)
            {
                int j2 = rng.Next(i + 1);
                (indices[i], indices[j2]) = (indices[j2], indices[i]);
            }

            for (int start = 0; start < trainN && !ct.IsCancellationRequested; start += batchSz)
            {
                int end = Math.Min(start + batchSz, trainN);
                int bsz = end - start;

                // Build batch arrays
                var xArr    = new float[bsz * F];
                var yClsArr = new float[bsz];
                var yDomArr = new float[bsz];
                var wArr    = new float[bsz];
                var srcList = new List<int>(bsz);

                for (int bi = 0; bi < bsz; bi++)
                {
                    int idx    = indices[start + bi];
                    var s      = trainSet[idx];
                    bool isSrc = idx < domainBoundary;
                    Array.Copy(s.Features, 0, xArr, bi * F, F);
                    yClsArr[bi] = s.Direction == 1
                        ? (float)(1.0 - labelSmoothing * 0.5)
                        : (float)(labelSmoothing * 0.5);
                    yDomArr[bi] = isSrc ? 0f : 1f;
                    wArr[bi]    = (float)(idx < sampleWeights.Length ? sampleWeights[idx] : 1.0);
                    if (isSrc) srcList.Add(bi);
                }

                opt.zero_grad();

                using var xT    = torch.tensor(xArr,    device: device).reshape(bsz, F);
                using var yDomT = torch.tensor(yDomArr, device: device).reshape(bsz, 1);
                using var wDomT = torch.tensor(wArr,    device: device).reshape(bsz, 1);

                // Forward: 2-layer feature extraction
                using var h = net.Features(xT);   // [bsz, featDim]

                // ── Classification loss (source samples only) ──────────────────
                Tensor? clsLoss = null;
                if (srcList.Count > 0)
                {
                    using var srcIdxT  = torch.tensor(srcList.ToArray(), dtype: ScalarType.Int64, device: device);
                    using var hSrc     = h.index_select(0, srcIdxT);      // [nSrc, featDim]
                    using var logitSrc = net.ClassifyLogit(hSrc);          // [nSrc, 1]
                    using var predSrc  = torch.sigmoid(logitSrc);

                    var yClsSrc = srcList.Select(bi => yClsArr[bi]).ToArray();
                    var wClsSrc = srcList.Select(bi => wArr[bi]).ToArray();
                    using var yClsSrcT = torch.tensor(yClsSrc, device: device).reshape(srcList.Count, 1);
                    using var wClsSrcT = torch.tensor(wClsSrc, device: device).reshape(srcList.Count, 1);

                    clsLoss = functional.binary_cross_entropy(predSrc, yClsSrcT, weight: wClsSrcT);
                }

                // ── Domain loss with GRL ───────────────────────────────────────
                // h_grl = h.detach()*(1+λ) − h*λ  ⟹  forward=h, ∂/∂h = −λ (reversal)
                using var hGrl    = h.detach() * (1f + lam) - h * lam;
                using var domLogit = net.DomainLogit(hGrl);                // [bsz, 1]
                using var domPred  = torch.sigmoid(domLogit);
                using var domLoss  = functional.binary_cross_entropy(domPred, yDomT, weight: wDomT);

                // Total loss + backward
                Tensor totalLoss = clsLoss is not null ? clsLoss + domLoss : domLoss;
                totalLoss.backward();

                // Gradient norm clipping (manual — avoids torch.nn.utils ambiguity)
                net.ClipGradNorm(5.0f);
                opt.step();

                // Per-parameter weight-magnitude clipping — guards against runaway
                // weights that survive gradient-norm clipping (different failure mode).
                using (no_grad())
                {
                    foreach (var param in net.AllParams)
                        param.clamp_(-WeightClipMagnitude, WeightClipMagnitude);
                }

                clsLoss?.Dispose();
                if (!ReferenceEquals(totalLoss, domLoss)) totalLoss.Dispose();
            }

            // Cosine annealing LR step — updates the optimizer's LR for the next epoch
            scheduler.step();

            // ── Early stopping: batch-validate on full val set ─────────────────
            net.Training = false;
            using (no_grad())
            {
                using var valXT    = torch.tensor(valXArr, device: device).reshape(nVal, F);
                using var valH     = net.Features(valXT);
                using var valLogit = net.ClassifyLogit(valH);
                using var valProb  = torch.sigmoid(valLogit).squeeze(1);
                var probs = valProb.cpu().data<float>().ToArray();
                int correct = 0;
                for (int vi = 0; vi < nVal; vi++)
                    if ((probs[vi] >= 0.5f ? 1 : 0) == valYArr[vi]) correct++;
                double valAcc = (double)correct / nVal;

                if (valAcc > bestValAcc + 1e-4)
                {
                    bestValAcc     = valAcc;
                    bestModel      = ExtractToDannModel(net, F, featDim, domHid);
                    noImproveTicks = 0;
                }
                else if (++noImproveTicks >= patience)
                {
                    break;  // Fixed: patience is now in epochs (1 check per epoch)
                }
            }
            net.Training = true;
        }

        return bestModel ?? ExtractToDannModel(net, F, featDim, domHid);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WEIGHT EXTRACTION: TorchSharp → DannModel (for C# inference pipeline)
    // ═══════════════════════════════════════════════════════════════════════════

    private static DannModel ExtractToDannModel(DannNet net, int F, int featDim, int domHid)
    {
        var m = new DannModel(F, featDim, domHid);
        using (no_grad())
        {
            // Parameters are raw tensors; .cpu().contiguous().data<float>() reads values safely.
            // Feat1W shape: [featDim, F]
            var w1 = net.Feat1W.cpu().contiguous().data<float>().ToArray();
            var b1 = net.Feat1B.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++)
            {
                m.bFeat[j] = b1[j];
                for (int fi = 0; fi < F; fi++) m.WFeat[j][fi] = w1[j * F + fi];
            }

            // Feat2W shape: [featDim, featDim]
            var w2 = net.Feat2W.cpu().contiguous().data<float>().ToArray();
            var b2 = net.Feat2B.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++)
            {
                m.bFeat2[j] = b2[j];
                for (int k = 0; k < featDim; k++) m.WFeat2[j][k] = w2[j * featDim + k];
            }

            // ClsW shape: [1, featDim]
            var wCls = net.ClsW.cpu().contiguous().data<float>().ToArray();
            var bCls = net.ClsB.cpu().contiguous().data<float>().ToArray();
            for (int j = 0; j < featDim; j++) m.wCls[j] = wCls[j];
            m.bCls = bCls[0];

            // Dom1W shape: [domHid, featDim]
            var wd1 = net.Dom1W.cpu().contiguous().data<float>().ToArray();
            var bd1 = net.Dom1B.cpu().contiguous().data<float>().ToArray();
            for (int k = 0; k < domHid; k++)
            {
                m.bDom1[k] = bd1[k];
                for (int j = 0; j < featDim; j++) m.WDom1[k][j] = wd1[k * featDim + j];
            }

            // Dom2W shape: [1, domHid]
            var wd2 = net.Dom2W.cpu().contiguous().data<float>().ToArray();
            var bd2 = net.Dom2B.cpu().contiguous().data<float>().ToArray();
            for (int k = 0; k < domHid; k++) m.wDom2[k] = wd2[k];
            m.bDom2 = bd2[0];
        }
        return m;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WARM-START: load DannWeights snapshot → TorchSharp module
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads weights from a serialised <see cref="ModelSnapshot.DannWeights"/> into <paramref name="net"/>.
    /// Supports both the v4 format (2-layer extractor) and the legacy v3 format (1-layer).
    /// When a geometry mismatch is detected the module is left at default Kaiming init.
    /// </summary>
    private static void TryLoadWarmStartWeights(
        DannNet net, double[][] ws, int F, int featDim, int domHid,
        Device device, ILogger logger)
    {
        // v4 row layout: featDim + featDim + 1 + domHid + 1  (2-layer extractor)
        // v3 row layout: featDim + 1 + domHid + 1            (1-layer extractor)
        int newRows = 2 * featDim + 1 + domHid + 1;
        int oldRows = featDim + 1 + domHid + 1;

        bool isNew = ws.Length == newRows && ws[0].Length == F + 1;
        bool isOld = ws.Length == oldRows && ws[0].Length == F + 1;

        if (!isNew && !isOld)
        {
            logger.LogDebug("DANN warm-start geometry mismatch — keeping Kaiming init.");
            return;
        }

        using (no_grad())
        {
            // ── Feat1W / Feat1B ───────────────────────────────────────────────
            var w1f = new float[featDim * F];
            var b1f = new float[featDim];
            for (int j = 0; j < featDim; j++)
            {
                b1f[j] = (float)ws[j][F];
                for (int fi = 0; fi < F; fi++) w1f[j * F + fi] = (float)ws[j][fi];
            }
            net.Feat1W.copy_(torch.tensor(w1f, device: device).reshape(featDim, F));
            net.Feat1B.copy_(torch.tensor(b1f, device: device));

            // ── Feat2W / Feat2B (v4 only; v3 keeps Kaiming init) ──────────────
            int baseRow;
            if (isNew)
            {
                var w2f = new float[featDim * featDim];
                var b2f = new float[featDim];
                for (int j = 0; j < featDim; j++)
                {
                    b2f[j] = (float)ws[featDim + j][featDim];
                    for (int k = 0; k < featDim; k++) w2f[j * featDim + k] = (float)ws[featDim + j][k];
                }
                net.Feat2W.copy_(torch.tensor(w2f, device: device).reshape(featDim, featDim));
                net.Feat2B.copy_(torch.tensor(b2f, device: device));
                baseRow = 2 * featDim;
            }
            else
            {
                logger.LogDebug("DANN warm-start: upgrading v3→v4 (Feat2 cold-init).");
                baseRow = featDim;
            }

            // ── ClsW / ClsB ───────────────────────────────────────────────────
            var wClsF = new float[featDim];
            var bClsF = new float[1];
            for (int j = 0; j < featDim; j++) wClsF[j] = (float)ws[baseRow][j];
            bClsF[0] = (float)ws[baseRow][featDim];
            net.ClsW.copy_(torch.tensor(wClsF, device: device).reshape(1, featDim));
            net.ClsB.copy_(torch.tensor(bClsF, device: device));

            // ── Dom1W / Dom1B ─────────────────────────────────────────────────
            var wd1f = new float[domHid * featDim];
            var bd1f = new float[domHid];
            for (int k = 0; k < domHid; k++)
            {
                bd1f[k] = (float)ws[baseRow + 1 + k][featDim];
                for (int j = 0; j < featDim; j++) wd1f[k * featDim + j] = (float)ws[baseRow + 1 + k][j];
            }
            net.Dom1W.copy_(torch.tensor(wd1f, device: device).reshape(domHid, featDim));
            net.Dom1B.copy_(torch.tensor(bd1f, device: device));

            // ── Dom2W / Dom2B ─────────────────────────────────────────────────
            var wd2f = new float[domHid];
            var bd2f = new float[1];
            int lastRow = baseRow + 1 + domHid;
            for (int k = 0; k < domHid; k++) wd2f[k] = (float)ws[lastRow][k];
            bd2f[0] = (float)ws[lastRow][domHid];
            net.Dom2W.copy_(torch.tensor(wd2f, device: device).reshape(1, domHid));
            net.Dom2B.copy_(torch.tensor(bd2f, device: device));
        }
        logger.LogDebug("DANN warm-start: weights loaded ({Format}).", isNew ? "v4" : "v3→v4");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INFERENCE FORWARD PASS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns P(Buy|x) ∈ (0,1) using the 2-layer feature extractor + label classifier.
    /// Layer1: h1 = ReLU(WFeat  · x   + bFeat)
    /// Layer2: h2 = ReLU(WFeat2 · h1  + bFeat2)
    /// Output: σ(wCls · h2 + bCls)
    /// </summary>
    private static double ForwardCls(DannModel m, float[] x)
    {
        // ── Layer 1: F → featDim ─────────────────────────────────────────────
        var h1 = new double[m.featDim];
        for (int j = 0; j < m.featDim; j++)
        {
            double pre = m.bFeat[j];
            for (int fi = 0; fi < m.F; fi++) pre += m.WFeat[j][fi] * x[fi];
            h1[j] = Math.Max(0.0, pre);
        }

        // ── Layer 2: featDim → featDim ───────────────────────────────────────
        double logit = m.bCls;
        for (int j = 0; j < m.featDim; j++)
        {
            double pre2 = m.bFeat2[j];
            for (int k = 0; k < m.featDim; k++) pre2 += m.WFeat2[j][k] * h1[k];
            logit += m.wCls[j] * Math.Max(0.0, pre2);
        }
        return Sigmoid(logit);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WALK-FORWARD CROSS-VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    private (WalkForwardResult Result, bool EquityCurveGateFailed) RunWalkForwardCV(
        List<TrainingSample> samples,
        TrainingHyperparams  hp,
        int                  F,
        double               lr,
        int                  epochs,
        double               lamBase,
        CancellationToken    ct)
    {
        int folds    = hp.WalkForwardFolds;
        int embargo  = hp.EmbargoBarCount;
        int foldSize = samples.Count / (folds + 1);
        int featDim  = hp.DannFeatDim is > 0 ? hp.DannFeatDim.Value : DefaultFeatDim;
        int domHid   = hp.DannDomHid  is > 0 ? hp.DannDomHid.Value  : DefaultDomHid;

        if (foldSize < 50)
        {
            _logger.LogWarning("DANN walk-forward fold size too small ({Size}), skipping CV", foldSize);
            return (new WalkForwardResult(0, 0, 0, 0, 0, 0), false);
        }

        var accList    = new List<double>(folds);
        var f1List     = new List<double>(folds);
        var evList     = new List<double>(folds);
        var sharpeList = new List<double>(folds);
        var foldImpList = new List<double[]>(folds);
        int badFolds   = 0;
        int evaluatedFolds = 0;

        for (int fold = 0; fold < folds && !ct.IsCancellationRequested; fold++)
        {
            int testEnd   = (fold + 2) * foldSize;
            int testStart = testEnd - foldSize;
            int purgeExtra = MLFeatureHelper.LookbackWindow - 1;
            int trainEnd  = Math.Max(0, testStart - embargo - purgeExtra);

            if (trainEnd < hp.MinSamples) { continue; }

            var foldTrain = samples[..trainEnd].ToList();
            if (hp.PurgeHorizonBars > 0)
            {
                int purgeFrom = Math.Max(0, testStart - hp.PurgeHorizonBars);
                if (purgeFrom < foldTrain.Count) foldTrain = foldTrain[..purgeFrom];
            }

            var foldTest = samples[testStart..Math.Min(testEnd, samples.Count)];
            if (foldTest.Count < 20) continue;
            evaluatedFolds++;

            var cvHp = hp with
            {
                MaxEpochs             = Math.Max(20, epochs / 3),
                EarlyStoppingPatience = Math.Max(5,  hp.EarlyStoppingPatience / 2),
            };

            var cvModel = FitDann(foldTrain, cvHp, F, featDim, domHid, lr, cvHp.MaxEpochs, lamBase,
                hp.LabelSmoothing, null, null, ct);
            var m = EvaluateModel(foldTest.ToList(), cvModel, new double[F], 0.0, 1.0, 0.0, F);

            // Per-feature stability: single-round permutation importance when the fold test
            // set is large enough for a reliable estimate; fall back to mean |WFeat| otherwise.
            var foldImp = new double[F];
            if (foldTest.Count >= 50)
            {
                var foldRng  = new Random(42);
                var foldList = foldTest.ToList();
                double foldBase = foldList.Average(s =>
                    (ForwardCls(cvModel, s.Features) >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0);
                for (int fi = 0; fi < F && !ct.IsCancellationRequested; fi++)
                {
                    var shuffled = foldList.Select(s => ((float[])s.Features.Clone(), s.Direction)).ToList();
                    for (int i = shuffled.Count - 1; i > 0; i--)
                    {
                        int j2 = foldRng.Next(i + 1);
                        (shuffled[i].Item1[fi], shuffled[j2].Item1[fi]) =
                            (shuffled[j2].Item1[fi], shuffled[i].Item1[fi]);
                    }
                    double shuffleAcc = shuffled.Average(s =>
                        (ForwardCls(cvModel, s.Item1) >= 0.5 ? 1 : 0) == s.Direction ? 1.0 : 0.0);
                    foldImp[fi] = Math.Max(0.0, foldBase - shuffleAcc);
                }
            }
            else
            {
                for (int fi = 0; fi < F; fi++)
                {
                    double s2 = 0;
                    for (int j = 0; j < cvModel.featDim; j++) s2 += Math.Abs(cvModel.WFeat[j][fi]);
                    foldImp[fi] = s2 / cvModel.featDim;
                }
            }

            // Equity-curve gate: reject if max-drawdown is catastrophic
            if (m.SharpeRatio < -2.0 || (m.ExpectedValue < -0.05 && m.Accuracy < 0.40))
            {
                badFolds++;
                _logger.LogDebug("DANN fold {Fold} failed equity gate (sharpe={S:F2} acc={A:P1})",
                    fold, m.SharpeRatio, m.Accuracy);
                continue;
            }

            accList.Add(m.Accuracy);
            f1List.Add(m.F1);
            evList.Add(m.ExpectedValue);
            sharpeList.Add(m.SharpeRatio);
            foldImpList.Add(foldImp);
        }

        if (accList.Count == 0)
        {
            // Mirror the other trainers: a small/hostile CV window should not force
            // an empty snapshot when final training can still proceed.
            _logger.LogWarning("DANN: no usable CV folds were retained — continuing without a CV gate.");
            return (new WalkForwardResult(0, 0, 0, 0, 0, evaluatedFolds), false);
        }

        double avgAcc  = accList.Average();
        double stdAcc  = Std(accList);
        double avgF1   = f1List.Average();
        double avgEV   = evList.Average();
        double avgShrp = sharpeList.Average();

        // Equity curve gate: fraction of bad folds exceeds MaxBadFoldFraction (default 0.5).
        double maxBadFrac = hp.MaxBadFoldFraction is > 0.0 and < 1.0
            ? hp.MaxBadFoldFraction : 0.5;
        bool gateFailed = folds > 0 && (double)badFolds / folds > maxBadFrac;

        // Walk-forward Sharpe trend (linear regression slope)
        double sharpeTrend = ComputeLinearSlope(sharpeList);

        // Feature stability: per-feature coefficient of variation across folds
        double[]? featureStability = null;
        if (foldImpList.Count >= 2)
        {
            featureStability = new double[F];
            for (int fi = 0; fi < F; fi++)
            {
                var vals = foldImpList.Select(imp => imp[fi]).ToList();
                double mean = vals.Average();
                double std2 = Std(vals);
                featureStability[fi] = mean > 1e-9 ? std2 / mean : 0.0;
            }
        }

        // Cross-fold variance gate
        if (stdAcc > hp.MaxWalkForwardStdDev && hp.MaxWalkForwardStdDev < 1.0)
        {
            _logger.LogWarning(
                "DANN walk-forward std={Std:P1} exceeds gate {Gate:P1} — model may be unstable.",
                stdAcc, hp.MaxWalkForwardStdDev);
        }

        return (new WalkForwardResult(avgAcc, stdAcc, avgF1, avgEV, avgShrp,
            accList.Count, SharpeTrend: sharpeTrend, FeatureStabilityScores: featureStability), gateFailed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  META-LABEL + ABSTENTION
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double[] Weights, double Bias) FitMetaLabelModel(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F)
    {
        if (calSet.Count < 10) return ([], 0.0);

        // Features: [p, |p-0.5|, p*(1-p), |logit(p)|]
        // — p and |logit| capture different saturations; p*(1-p) adds orthogonal uncertainty signal.
        const int MetaF = 4;
        var w = new double[MetaF];
        double b = 0.0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[MetaF]; double db = 0.0;
            foreach (var s in calSet)
            {
                double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
                double logit  = Math.Log(pClamp / (1.0 - pClamp));
                double[] feat = [p2, Math.Abs(p2 - 0.5), p2 * (1.0 - p2), Math.Abs(logit)];
                bool correct  = (p2 >= 0.5 ? 1 : 0) == s.Direction;
                double y      = correct ? 1.0 : 0.0;
                double dot    = b;
                for (int fi = 0; fi < MetaF; fi++) dot += w[fi] * feat[fi];
                double pred   = Sigmoid(dot);
                double err    = pred - y;
                db += err;
                for (int fi = 0; fi < MetaF; fi++) dw[fi] += err * feat[fi];
            }
            double lr2 = 0.01 / calSet.Count;
            b -= lr2 * db;
            for (int fi = 0; fi < MetaF; fi++) w[fi] -= lr2 * dw[fi];
        }
        return (w, b);
    }

    private static (double[] Weights, double Bias, double Threshold) FitAbstentionModel(
        List<TrainingSample> calSet, DannModel model, double plattA, double plattB, int F,
        bool f1Sweep = false)
    {
        if (calSet.Count < 10) return ([], 0.0, 0.5);

        // Features: [p, |p-0.5|, p*(1-p), |logit(p)|] — same expanded set as meta-label.
        const int AbstF = 4;
        var w = new double[AbstF];
        double b = 0.0;

        for (int iter = 0; iter < 200; iter++)
        {
            var dw = new double[AbstF]; double db = 0.0;
            foreach (var s in calSet)
            {
                double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
                double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
                double logit  = Math.Log(pClamp / (1.0 - pClamp));
                double[] feat = [p2, Math.Abs(p2 - 0.5), p2 * (1.0 - p2), Math.Abs(logit)];
                bool tradeable = Math.Abs(p2 - 0.5) > 0.10; // >60% or <40% confidence = tradeable
                double y      = tradeable ? 1.0 : 0.0;
                double dot    = b;
                for (int fi = 0; fi < AbstF; fi++) dot += w[fi] * feat[fi];
                double pred   = Sigmoid(dot);
                double err    = pred - y;
                db += err;
                for (int fi = 0; fi < AbstF; fi++) dw[fi] += err * feat[fi];
            }
            double lr2 = 0.01 / calSet.Count;
            b -= lr2 * db;
            for (int fi = 0; fi < AbstF; fi++) w[fi] -= lr2 * dw[fi];
        }

        // Compute abstention scores for all cal samples
        var scoredCal = calSet.Select(s =>
        {
            double p2     = ApplyPlatt(ForwardCls(model, s.Features), plattA, plattB);
            double pClamp = Math.Clamp(p2, 1e-7, 1.0 - 1e-7);
            double logit  = Math.Log(pClamp / (1.0 - pClamp));
            double dot    = b + w[0] * p2 + w[1] * Math.Abs(p2 - 0.5)
                              + w[2] * p2 * (1.0 - p2) + w[3] * Math.Abs(logit);
            return (Score: Sigmoid(dot), Label: s.Direction, Pred: p2 >= 0.5 ? 1 : 0);
        }).OrderBy(x => x.Score).ToArray();

        double thr;
        if (f1Sweep && scoredCal.Length >= 10)
        {
            // Sweep threshold and pick the value that maximises F1 on the cal set.
            double bestF1 = -1.0;
            thr = 0.5;
            for (int ti = 0; ti < scoredCal.Length; ti++)
            {
                double candidate = scoredCal[ti].Score;
                int tp = 0, fp = 0, fn = 0;
                foreach (var (sc, lbl, pred) in scoredCal)
                {
                    if (sc < candidate) continue; // abstain on low-score samples
                    if (pred == 1 && lbl == 1) tp++;
                    else if (pred == 1 && lbl == 0) fp++;
                    else if (pred == 0 && lbl == 1) fn++;
                }
                double prec = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
                double rec  = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
                double f1   = prec + rec > 0 ? 2.0 * prec * rec / (prec + rec) : 0.0;
                if (f1 > bestF1) { bestF1 = f1; thr = candidate; }
            }
        }
        else
        {
            // Default: threshold at the 40th percentile of scores (top 60% pass).
            int tIdx = (int)(scoredCal.Length * 0.40);
            thr = tIdx < scoredCal.Length ? scoredCal[tIdx].Score : 0.5;
        }

        return (w, b, thr);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  WEIGHT SANITISATION
    // ═══════════════════════════════════════════════════════════════════════════

    private static int SanitizeWeights(DannModel model)
    {
        int count = 0;
        count += SanitizeMatrix(model.WFeat);
        count += SanitizeVector(model.bFeat);
        count += SanitizeMatrix(model.WFeat2);
        count += SanitizeVector(model.bFeat2);
        count += SanitizeVector(model.wCls);
        if (!double.IsFinite(model.bCls)) { model.bCls = 0.0; count++; }
        count += SanitizeMatrix(model.WDom1);
        count += SanitizeVector(model.bDom1);
        count += SanitizeVector(model.wDom2);
        if (!double.IsFinite(model.bDom2)) { model.bDom2 = 0.0; count++; }
        return count;
    }

    private static int SanitizeMatrix(double[][] m)
    {
        int count = 0;
        for (int i = 0; i < m.Length; i++) count += SanitizeVector(m[i]);
        return count;
    }

    private static int SanitizeVector(double[] v)
    {
        int count = 0;
        for (int i = 0; i < v.Length; i++)
        {
            if (!double.IsFinite(v[i])) { v[i] = 0.0; count++; }
        }
        return count;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  DANN WEIGHTS SNAPSHOT SERIALISATION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Packs all DANN layer weights into a jagged array for snapshot serialisation (v4 format):
    /// rows 0..featDim-1         = WFeat layer-1   (each row: F weights + bias)
    /// rows featDim..2*featDim-1 = WFeat2 layer-2  (each row: featDim weights + bias)
    /// row  2*featDim            = wCls + bCls appended
    /// rows 2*featDim+1..2*featDim+domHid = WDom1  (each row: featDim weights + bias)
    /// row  2*featDim+domHid+1   = wDom2 + bDom2 appended
    /// </summary>
    private static double[][] ExtractDannWeightsForSnapshot(DannModel model)
    {
        int totalRows = 2 * model.featDim + 1 + model.domHid + 1;
        var result    = new double[totalRows][];

        // Feature extractor layer-1 rows
        for (int j = 0; j < model.featDim; j++)
        {
            result[j] = new double[model.F + 1];
            Array.Copy(model.WFeat[j], result[j], model.F);
            result[j][model.F] = model.bFeat[j];
        }

        // Feature extractor layer-2 rows
        for (int j = 0; j < model.featDim; j++)
        {
            int row = model.featDim + j;
            result[row] = new double[model.featDim + 1];
            Array.Copy(model.WFeat2[j], result[row], model.featDim);
            result[row][model.featDim] = model.bFeat2[j];
        }

        // Label classifier row (wCls + bCls)
        int clsRow = 2 * model.featDim;
        result[clsRow] = new double[model.featDim + 1];
        Array.Copy(model.wCls, result[clsRow], model.featDim);
        result[clsRow][model.featDim] = model.bCls;

        // Domain layer-1 rows
        for (int k = 0; k < model.domHid; k++)
        {
            int row = 2 * model.featDim + 1 + k;
            result[row] = new double[model.featDim + 1];
            Array.Copy(model.WDom1[k], result[row], model.featDim);
            result[row][model.featDim] = model.bDom1[k];
        }

        // Domain layer-2 row (wDom2 + bDom2)
        int lastRow = 2 * model.featDim + 1 + model.domHid;
        result[lastRow] = new double[model.domHid + 1];
        Array.Copy(model.wDom2, result[lastRow], model.domHid);
        result[lastRow][model.domHid] = model.bDom2;

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  MATHS HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private static double Sigmoid(double x) =>
        x >= 0 ? 1.0 / (1.0 + Math.Exp(-x)) : Math.Exp(x) / (1.0 + Math.Exp(x));

    private static double AdamScalar(
        double param, double grad,
        ref double m, ref double v,
        double lr, double bc1, double bc2)
    {
        m = AdamBeta1 * m + (1.0 - AdamBeta1) * grad;
        v = AdamBeta2 * v + (1.0 - AdamBeta2) * grad * grad;
        return param - lr * (m / bc1) / (Math.Sqrt(v / bc2) + AdamEpsilon);
    }
}
