using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT INITIALIZATION (Xavier/Glorot)
    // ═══════════════════════════════════════════════════════════════════════

    private static TabNetWeights InitializeWeights(
        int F, int nSteps, int hiddenDim, int attentionDim,
        int sharedLayers, int stepLayers, double gamma, bool useSparsemax, bool useMagHead)
    {
        var rng = new Random(42);
        int totalBn = nSteps + sharedLayers + nSteps * stepLayers;

        var w = new TabNetWeights
        {
            NSteps       = nSteps,
            F            = F,
            HiddenDim    = hiddenDim,
            AttentionDim = attentionDim,
            SharedLayers = sharedLayers,
            StepLayers   = stepLayers,
            Gamma        = gamma,
            UseSparsemax = useSparsemax,
            TotalBnLayers = totalBn,

            // Initial BN FC for step-0 attention symmetry
            InitialBnFcW = XavierMatrix(rng, F, F),
            InitialBnFcB = new double[F],

            SharedW  = InitFcLayers(rng, sharedLayers, hiddenDim, F, hiddenDim),
            SharedB  = InitBiasLayers(sharedLayers, hiddenDim),
            SharedGW = InitFcLayers(rng, sharedLayers, hiddenDim, F, hiddenDim),
            SharedGB = InitBiasLayers(sharedLayers, hiddenDim),

            StepW  = new double[nSteps][][][],
            StepB  = new double[nSteps][][],
            StepGW = new double[nSteps][][][],
            StepGB = new double[nSteps][][],

            AttnFcW = new double[nSteps][][],
            AttnFcB = new double[nSteps][],

            BnGamma = new double[totalBn][],
            BnBeta  = new double[totalBn][],
            BnMean  = new double[totalBn][],
            BnVar   = new double[totalBn][],

            OutputW = XavierVec(rng, hiddenDim, hiddenDim, 1),
            OutputB = 0.0,

            MagW = useMagHead ? XavierVec(rng, hiddenDim, hiddenDim, 1) : [],
            MagB = 0.0,
        };

        for (int s = 0; s < nSteps; s++)
        {
            w.StepW[s]  = InitFcLayers(rng, stepLayers, hiddenDim, hiddenDim, hiddenDim);
            w.StepB[s]  = InitBiasLayers(stepLayers, hiddenDim);
            w.StepGW[s] = InitFcLayers(rng, stepLayers, hiddenDim, hiddenDim, hiddenDim);
            w.StepGB[s] = InitBiasLayers(stepLayers, hiddenDim);

            w.AttnFcW[s] = XavierMatrix(rng, F, hiddenDim);
            w.AttnFcB[s] = new double[F];
        }

        for (int b = 0; b < totalBn; b++)
        {
            int dim = b < nSteps ? F : hiddenDim;
            w.BnGamma[b] = Enumerable.Repeat(1.0, dim).ToArray();
            w.BnBeta[b]  = new double[dim];
            w.BnMean[b]  = new double[dim];
            w.BnVar[b]   = Enumerable.Repeat(1.0, dim).ToArray();
        }

        return w;
    }

    private static double[][][] InitFcLayers(Random rng, int numLayers, int outDim, int firstInDim, int subsequentInDim)
    {
        var layers = new double[numLayers][][];
        for (int l = 0; l < numLayers; l++)
        {
            int inDim = l == 0 ? firstInDim : subsequentInDim;
            layers[l] = XavierMatrix(rng, outDim, inDim);
        }
        return layers;
    }

    private static double[][] InitBiasLayers(int numLayers, int dim)
    {
        var layers = new double[numLayers][];
        for (int l = 0; l < numLayers; l++) layers[l] = new double[dim];
        return layers;
    }

    private static TabNetWeights InitializeGradAccumulator(TabNetWeights w)
    {
        return InitializeWeights(w.F, w.NSteps, w.HiddenDim, w.AttentionDim,
            w.SharedLayers, w.StepLayers, w.Gamma, w.UseSparsemax, w.MagW.Length > 0);
    }

    private static AdamState InitializeAdamState(TabNetWeights w)
    {
        var a = new AdamState
        {
            MInitialBnFcW = ZeroDim2(w.InitialBnFcW), VInitialBnFcW = ZeroDim2(w.InitialBnFcW),
            MInitialBnFcB = new double[w.InitialBnFcB.Length], VInitialBnFcB = new double[w.InitialBnFcB.Length],

            MSharedW  = ZeroDim3(w.SharedW),  VSharedW  = ZeroDim3(w.SharedW),
            MSharedB  = ZeroDim2(w.SharedB),   VSharedB  = ZeroDim2(w.SharedB),
            MSharedGW = ZeroDim3(w.SharedGW),  VSharedGW = ZeroDim3(w.SharedGW),
            MSharedGB = ZeroDim2(w.SharedGB),   VSharedGB = ZeroDim2(w.SharedGB),

            MStepW  = ZeroDim4(w.StepW),   VStepW  = ZeroDim4(w.StepW),
            MStepB  = ZeroDim3(w.StepB),    VStepB  = ZeroDim3(w.StepB),
            MStepGW = ZeroDim4(w.StepGW),   VStepGW = ZeroDim4(w.StepGW),
            MStepGB = ZeroDim3(w.StepGB),    VStepGB = ZeroDim3(w.StepGB),

            MAttnFcW = ZeroDim3(w.AttnFcW), VAttnFcW = ZeroDim3(w.AttnFcW),
            MAttnFcB = ZeroDim2(w.AttnFcB),  VAttnFcB = ZeroDim2(w.AttnFcB),

            MBnGamma = ZeroDim2(w.BnGamma), VBnGamma = ZeroDim2(w.BnGamma),
            MBnBeta  = ZeroDim2(w.BnBeta),   VBnBeta  = ZeroDim2(w.BnBeta),

            MOutputW = new double[w.OutputW.Length], VOutputW = new double[w.OutputW.Length],
            MMagW    = new double[w.MagW.Length],     VMagW    = new double[w.MagW.Length],
        };
        return a;
    }

    private static void InitializeAdamSecondMoment(AdamState adam, TabNetWeights w)
    {
        // Warm-start Adam's second moment from loaded weight magnitudes to reduce initial instability
        const double MinV = 1e-4;
        static void WarmV(double[] param, double[] v) { for (int i = 0; i < param.Length; i++) v[i] = Math.Max(param[i] * param[i], MinV); }
        static void WarmV2D(double[][] param, double[][] v) { for (int i = 0; i < param.Length; i++) WarmV(param[i], v[i]); }

        if (w.InitialBnFcW.Length > 0) WarmV2D(w.InitialBnFcW, adam.VInitialBnFcW);
        WarmV(w.OutputW, adam.VOutputW);
        if (w.MagW.Length > 0) WarmV(w.MagW, adam.VMagW);

        for (int l = 0; l < w.SharedLayers; l++)
        {
            WarmV2D(w.SharedW[l], adam.VSharedW[l]);
            WarmV(w.SharedB[l], adam.VSharedB[l]);
            WarmV2D(w.SharedGW[l], adam.VSharedGW[l]);
            WarmV(w.SharedGB[l], adam.VSharedGB[l]);
        }

        for (int s = 0; s < w.NSteps; s++)
        {
            for (int l = 0; l < w.StepLayers; l++)
            {
                WarmV2D(w.StepW[s][l], adam.VStepW[s][l]);
                WarmV(w.StepB[s][l], adam.VStepB[s][l]);
                WarmV2D(w.StepGW[s][l], adam.VStepGW[s][l]);
                WarmV(w.StepGB[s][l], adam.VStepGB[s][l]);
            }
            WarmV2D(w.AttnFcW[s], adam.VAttnFcW[s]);
            WarmV(w.AttnFcB[s], adam.VAttnFcB[s]);
        }

        adam.T = 10; // Pretend a few steps have passed
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WEIGHT CLONING
    // ═══════════════════════════════════════════════════════════════════════

    private static TabNetWeights CloneWeights(TabNetWeights src)
    {
        return new TabNetWeights
        {
            NSteps = src.NSteps, F = src.F, HiddenDim = src.HiddenDim,
            AttentionDim = src.AttentionDim, SharedLayers = src.SharedLayers,
            StepLayers = src.StepLayers, Gamma = src.Gamma,
            UseSparsemax = src.UseSparsemax, TotalBnLayers = src.TotalBnLayers,

            InitialBnFcW = DeepClone2(src.InitialBnFcW),
            InitialBnFcB = (double[])src.InitialBnFcB.Clone(),

            SharedW  = DeepClone3(src.SharedW),  SharedB  = DeepClone2(src.SharedB),
            SharedGW = DeepClone3(src.SharedGW), SharedGB = DeepClone2(src.SharedGB),
            StepW    = DeepClone4(src.StepW),     StepB    = DeepClone3(src.StepB),
            StepGW   = DeepClone4(src.StepGW),    StepGB   = DeepClone3(src.StepGB),
            AttnFcW  = DeepClone3(src.AttnFcW),   AttnFcB  = DeepClone2(src.AttnFcB),
            BnGamma  = DeepClone2(src.BnGamma),   BnBeta   = DeepClone2(src.BnBeta),
            BnMean   = DeepClone2(src.BnMean),    BnVar    = DeepClone2(src.BnVar),
            OutputW  = (double[])src.OutputW.Clone(), OutputB = src.OutputB,
            MagW     = (double[])src.MagW.Clone(),    MagB    = src.MagB,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  COMPATIBLE WEIGHT COPY (with partial dimension adaptation)
    // ═══════════════════════════════════════════════════════════════════════

    private static void CopyCompatibleWeights(TabNetWeights src, TabNetWeights dst)
    {
        // Shared layers — copy overlapping region even if dimensions differ
        int minShared = Math.Min(src.SharedLayers, dst.SharedLayers);
        int minH = Math.Min(src.HiddenDim, dst.HiddenDim);
        for (int l = 0; l < minShared; l++)
        {
            CopyMatrixPartial(src.SharedW[l], dst.SharedW[l], minH);
            CopyArrayPartial(src.SharedB[l], dst.SharedB[l], minH);
            CopyMatrixPartial(src.SharedGW[l], dst.SharedGW[l], minH);
            CopyArrayPartial(src.SharedGB[l], dst.SharedGB[l], minH);
        }

        int minSteps = Math.Min(src.NSteps, dst.NSteps);
        int minStepL = Math.Min(src.StepLayers, dst.StepLayers);
        for (int s = 0; s < minSteps; s++)
        {
            for (int l = 0; l < minStepL; l++)
            {
                CopyMatrixPartial(src.StepW[s][l], dst.StepW[s][l], minH);
                CopyArrayPartial(src.StepB[s][l], dst.StepB[s][l], minH);
                CopyMatrixPartial(src.StepGW[s][l], dst.StepGW[s][l], minH);
                CopyArrayPartial(src.StepGB[s][l], dst.StepGB[s][l], minH);
            }
        }

        // Initial BN FC
        if (src.InitialBnFcW.Length > 0 && dst.InitialBnFcW.Length > 0)
        {
            int minF = Math.Min(src.F, dst.F);
            CopyMatrixPartial(src.InitialBnFcW, dst.InitialBnFcW, minF);
            CopyArrayPartial(src.InitialBnFcB, dst.InitialBnFcB, minF);
        }
    }

    private static void CopyMatrixPartial(double[][] src, double[][] dst, int maxDim)
    {
        int rows = Math.Min(Math.Min(src.Length, dst.Length), maxDim);
        for (int i = 0; i < rows; i++)
        {
            int cols = Math.Min(Math.Min(src[i].Length, dst[i].Length), maxDim);
            Array.Copy(src[i], dst[i], cols);
        }
    }

    private static void CopyArrayPartial(double[] src, double[] dst, int maxLen)
    {
        int len = Math.Min(Math.Min(src.Length, dst.Length), maxLen);
        Array.Copy(src, dst, len);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  WARM-START LOADING FROM MODEL SNAPSHOT
    // ═══════════════════════════════════════════════════════════════════════

    private void LoadWarmStartWeights(ModelSnapshot snapshot, TabNetWeights w)
    {
        try
        {
            if (snapshot.TabNetSharedWeights is { } sw && sw.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyMatrix(sw[l], w.SharedW[l]);

            if (snapshot.TabNetSharedBiases is { } sb && sb.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyArray(sb[l], w.SharedB[l]);

            if (snapshot.TabNetSharedGateWeights is { } sgw && sgw.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyMatrix(sgw[l], w.SharedGW[l]);

            if (snapshot.TabNetSharedGateBiases is { } sgb && sgb.Length == w.SharedLayers)
                for (int l = 0; l < w.SharedLayers; l++)
                    CopyArray(sgb[l], w.SharedGB[l]);

            if (snapshot.TabNetStepFcWeights is { } sfcw && sfcw.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sfcw[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyMatrix(sfcw[s][l], w.StepW[s][l]);

            if (snapshot.TabNetStepFcBiases is { } sfcb && sfcb.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sfcb[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyArray(sfcb[s][l], w.StepB[s][l]);

            if (snapshot.TabNetStepGateWeights is { } sgwS && sgwS.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sgwS[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyMatrix(sgwS[s][l], w.StepGW[s][l]);

            if (snapshot.TabNetStepGateBiases is { } sgbS && sgbS.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    if (sgbS[s].Length == w.StepLayers)
                        for (int l = 0; l < w.StepLayers; l++)
                            CopyArray(sgbS[s][l], w.StepGB[s][l]);

            if (snapshot.TabNetAttentionFcWeights is { } afw && afw.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    CopyMatrix(afw[s], w.AttnFcW[s]);

            if (snapshot.TabNetAttentionFcBiases is { } afb && afb.Length == w.NSteps)
                for (int s = 0; s < w.NSteps; s++)
                    CopyArray(afb[s], w.AttnFcB[s]);

            if (snapshot.TabNetBnGammas is { } bng && bng.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bng[b], w.BnGamma[b]);

            if (snapshot.TabNetBnBetas is { } bnb && bnb.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnb[b], w.BnBeta[b]);

            if (snapshot.TabNetBnRunningMeans is { } bnm && bnm.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnm[b], w.BnMean[b]);

            if (snapshot.TabNetBnRunningVars is { } bnv && bnv.Length == w.TotalBnLayers)
                for (int b = 0; b < w.TotalBnLayers; b++)
                    CopyArray(bnv[b], w.BnVar[b]);

            if (snapshot.TabNetOutputHeadWeights is { } ohw && ohw.Length == w.HiddenDim)
                Array.Copy(ohw, w.OutputW, w.HiddenDim);

            w.OutputB = snapshot.TabNetOutputHeadBias;

            if (snapshot.MagWeights is { Length: > 0 } mw && mw.Length == w.HiddenDim)
                Array.Copy(mw, w.MagW, w.HiddenDim);

            // Load initial BN FC weights
            if (snapshot.TabNetInitialBnFcW is { } ibfw && ibfw.Length == w.F)
                CopyMatrix(ibfw, w.InitialBnFcW);
            if (snapshot.TabNetInitialBnFcB is { } ibfb && ibfb.Length == w.F)
                Array.Copy(ibfb, w.InitialBnFcB, w.F);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TabNet warm-start: failed to load v3 weights, starting fresh.");
        }
    }
}
