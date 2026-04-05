namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class FtTransformerModelTrainer
{
    /// <summary>Per-block weights for one transformer layer.</summary>
    private sealed class TransformerLayer
    {
        public double[][] Wq;     // [EmbedDim][EmbedDim]
        public double[][] Wk;     // [EmbedDim][EmbedDim]
        public double[][] Wv;     // [EmbedDim][EmbedDim]
        public double[][] Wo;     // [EmbedDim][EmbedDim]
        public double[] Gamma1;   // [EmbedDim]
        public double[] Beta1;    // [EmbedDim]
        public double[][] Wff1;   // [EmbedDim][FfnDim]
        public double[]   Bff1;   // [FfnDim]
        public double[][] Wff2;   // [FfnDim][EmbedDim]
        public double[]   Bff2;   // [EmbedDim]
        public double[] Gamma2;   // [EmbedDim]
        public double[] Beta2;    // [EmbedDim]
        public double[][]? PosBias;  // [NumHeads][S*S] optional per-head positional bias

        public TransformerLayer(int embedDim, int ffnDim)
        {
            Wq   = new double[embedDim][];
            Wk   = new double[embedDim][];
            Wv   = new double[embedDim][];
            Wo   = new double[embedDim][];
            Gamma1 = new double[embedDim];
            Beta1  = new double[embedDim];
            Wff1 = new double[embedDim][];
            Bff1 = new double[ffnDim];
            Wff2 = new double[ffnDim][];
            Bff2 = new double[embedDim];
            Gamma2 = new double[embedDim];
            Beta2  = new double[embedDim];
        }
    }

    /// <summary>Bundles all trained transformer parameters across all layers.</summary>
    private sealed class TransformerModel
    {
        // Per-feature embedding
        public double[][] We;     // [F][EmbedDim]
        public double[][] Be;     // [F][EmbedDim]

        // Learnable [CLS] token embedding
        public double[] ClsToken; // [EmbedDim]

        // Stacked transformer layers
        public TransformerLayer[] Layers;

        // Final LayerNorm (pre-norm architecture requires LN on the output)
        public double[] GammaFinal; // [EmbedDim]
        public double[] BetaFinal;  // [EmbedDim]

        // Classifier head
        public double[]   WOut;   // [EmbedDim]
        public double     BOut;   // scalar

        // Architecture dims
        public int F;          // number of features (excludes [CLS])
        public int SeqLen;     // F + 1 ([CLS] + features)
        public int EmbedDim;
        public int NumHeads;
        public int HeadDim;
        public int FfnDim;
        public int NumLayers;

        public bool UsePositionalBias;

        // Convenience accessors for layer 0 (backward compatibility with snapshot serialisation)
        public double[][] Wq   { get => Layers[0].Wq;   set => Layers[0].Wq   = value; }
        public double[][] Wk   { get => Layers[0].Wk;   set => Layers[0].Wk   = value; }
        public double[][] Wv   { get => Layers[0].Wv;   set => Layers[0].Wv   = value; }
        public double[][] Wo   { get => Layers[0].Wo;   set => Layers[0].Wo   = value; }
        public double[]   Gamma1 { get => Layers[0].Gamma1; set => Layers[0].Gamma1 = value; }
        public double[]   Beta1  { get => Layers[0].Beta1;  set => Layers[0].Beta1  = value; }
        public double[][] Wff1 { get => Layers[0].Wff1; set => Layers[0].Wff1 = value; }
        public double[]   Bff1 { get => Layers[0].Bff1; set => Layers[0].Bff1 = value; }
        public double[][] Wff2 { get => Layers[0].Wff2; set => Layers[0].Wff2 = value; }
        public double[]   Bff2 { get => Layers[0].Bff2; set => Layers[0].Bff2 = value; }
        public double[]   Gamma2 { get => Layers[0].Gamma2; set => Layers[0].Gamma2 = value; }
        public double[]   Beta2  { get => Layers[0].Beta2;  set => Layers[0].Beta2  = value; }

        public TransformerModel(int f, int embedDim, int numHeads, int ffnDim, int numLayers = 1)
        {
            F         = f;
            SeqLen    = f + 1; // [CLS] + F feature tokens
            EmbedDim  = embedDim;
            NumHeads  = numHeads;
            HeadDim   = embedDim / numHeads;
            FfnDim    = ffnDim;
            NumLayers = numLayers;

            We = new double[f][];
            Be = new double[f][];
            ClsToken = new double[embedDim];

            Layers = new TransformerLayer[numLayers];
            for (int l = 0; l < numLayers; l++)
                Layers[l] = new TransformerLayer(embedDim, ffnDim);

            GammaFinal = new double[embedDim];
            BetaFinal  = new double[embedDim];

            WOut = new double[embedDim];
            BOut = 0.0;
        }
    }

    /// <summary>
    /// Pre-allocated buffers for forward pass to eliminate GC pressure during
    /// repeated inference (Platt fitting, ECE, permutation importance, etc.).
    /// Supports multi-layer transformers by sharing Q/K/V/AttnOut/Res/Ffn buffers across layers.
    /// </summary>
    private sealed class InferenceBuffers
    {
        public readonly double[][] E;      // [S][EmbedDim] - embeddings / inter-layer input (S = F+1 with [CLS])
        public readonly double[][] Q;      // [S][EmbedDim] - queries
        public readonly double[][] K;      // [S][EmbedDim] - keys
        public readonly double[][] V;      // [S][EmbedDim] - values
        public readonly double[][] AttnOut;// [S][EmbedDim] - attention output
        public readonly double[][] Scores; // [NumHeads][S*S] - attention scores per head
        public readonly double[][] AttnW;  // [NumHeads][S*S] - attention weights per head
        public readonly double[][] LnIn;   // [S][EmbedDim] - pre-norm LN output
        public readonly double[][] Res1;   // [S][EmbedDim] - after attention + residual
        public readonly double[][] LnIn2;  // [S][EmbedDim] - pre-norm LN2 output
        public readonly double[][] FfnH;   // [S][FfnDim]   - FFN hidden
        public readonly double[][] FfnOut; // [S][EmbedDim] - FFN output
        public readonly double[][] Res2;   // [S][EmbedDim] - after FFN + residual
        public readonly double[]   FinalLn;// [EmbedDim]    - final LN output for [CLS]

        /// <param name="f">Number of features (SeqLen = f+1 with [CLS] token).</param>
        public InferenceBuffers(int f, int embedDim, int numHeads, int ffnDim)
        {
            int s = f + 1; // [CLS] + F feature tokens
            E      = Alloc2D(s, embedDim);
            Q      = Alloc2D(s, embedDim);
            K      = Alloc2D(s, embedDim);
            V      = Alloc2D(s, embedDim);
            AttnOut= Alloc2D(s, embedDim);
            Scores = Alloc2D(numHeads, s * s);
            AttnW  = Alloc2D(numHeads, s * s);
            LnIn   = Alloc2D(s, embedDim);
            Res1   = Alloc2D(s, embedDim);
            LnIn2  = Alloc2D(s, embedDim);
            FfnH   = Alloc2D(s, ffnDim);
            FfnOut = Alloc2D(s, embedDim);
            Res2   = Alloc2D(s, embedDim);
            FinalLn= new double[embedDim];
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }

    }

    /// <summary>Serialised per-layer weights for additional layers (1..N-1).</summary>
    private sealed class SerializedLayerWeights
    {
        public double[][]? Wq { get; set; }
        public double[][]? Wk { get; set; }
        public double[][]? Wv { get; set; }
        public double[][]? Wo { get; set; }
        public double[]? Gamma1 { get; set; }
        public double[]? Beta1 { get; set; }
        public double[][]? Wff1 { get; set; }
        public double[]? Bff1 { get; set; }
        public double[][]? Wff2 { get; set; }
        public double[]? Bff2 { get; set; }
        public double[]? Gamma2 { get; set; }
        public double[]? Beta2 { get; set; }
        public double[][]? PosBias { get; set; }
    }

    /// <summary>Per-layer cached intermediates for backprop during training.</summary>
    private sealed class LayerForwardCache
    {
        public readonly double[][] Input;      // [S][EmbedDim] - input to this layer (snapshot)
        public readonly double[][] LnIn;       // [S][EmbedDim] - LN1 output (pre-norm before attention)
        public readonly double[][] Q;          // [S][EmbedDim]
        public readonly double[][] K;          // [S][EmbedDim]
        public readonly double[][] V;          // [S][EmbedDim]
        public readonly double[][] AttnOut;    // [S][EmbedDim]
        public readonly double[][][] HeadScores;    // [NumHeads][S][S]
        public readonly double[][][] HeadAttnW;    // [NumHeads][S][S] — post-dropout
        public readonly double[][][] PreDropAttnW; // [NumHeads][S][S] — pre-dropout softmax (for backward)
        public readonly double[][] Res1;       // [S][EmbedDim]
        public readonly double[][] LnIn2;      // [S][EmbedDim] - LN2 output (pre-norm before FFN)
        public readonly double[][] FfnH;       // [S][FfnDim]
        public readonly double[][] FfnHPreAct; // [S][FfnDim]
        public readonly double[][] FfnOut;     // [S][EmbedDim]
        public readonly double[][] Res2;       // [S][EmbedDim]

        public readonly double[] Ln1Mean;      // [S]
        public readonly double[] Ln1InvStd;    // [S]
        public readonly double[][] Ln1Norm;    // [S][EmbedDim]
        public readonly double[] Ln2Mean;      // [S]
        public readonly double[] Ln2InvStd;    // [S]
        public readonly double[][] Ln2Norm;    // [S][EmbedDim]

        public readonly bool[][] AttnDropMask; // [NumHeads][S*S]
        public readonly bool[][] FfnDropMask;  // [S][FfnDim]

        // Final LN cache (only used for last layer, position 0)
        public double FinalLnMean;
        public double FinalLnInvStd;
        public readonly double[] FinalLnNorm;  // [EmbedDim]
        public readonly double[] FinalLnOut;   // [EmbedDim]

        public LayerForwardCache(int s, int embedDim, int numHeads, int ffnDim)
        {
            Input    = Alloc2D(s, embedDim);
            LnIn     = Alloc2D(s, embedDim);
            Q        = Alloc2D(s, embedDim);
            K        = Alloc2D(s, embedDim);
            V        = Alloc2D(s, embedDim);
            AttnOut  = Alloc2D(s, embedDim);
            HeadScores    = new double[numHeads][][];
            HeadAttnW     = new double[numHeads][][];
            PreDropAttnW  = new double[numHeads][][];
            for (int h = 0; h < numHeads; h++)
            {
                HeadScores[h]    = Alloc2D(s, s);
                HeadAttnW[h]     = Alloc2D(s, s);
                PreDropAttnW[h]  = Alloc2D(s, s);
            }
            Res1       = Alloc2D(s, embedDim);
            LnIn2      = Alloc2D(s, embedDim);
            FfnH       = Alloc2D(s, ffnDim);
            FfnHPreAct = Alloc2D(s, ffnDim);
            FfnOut     = Alloc2D(s, embedDim);
            Res2       = Alloc2D(s, embedDim);

            Ln1Mean   = new double[s];
            Ln1InvStd = new double[s];
            Ln1Norm   = Alloc2D(s, embedDim);
            Ln2Mean   = new double[s];
            Ln2InvStd = new double[s];
            Ln2Norm   = Alloc2D(s, embedDim);

            AttnDropMask = new bool[numHeads][];
            for (int h = 0; h < numHeads; h++)
                AttnDropMask[h] = new bool[s * s];
            FfnDropMask = new bool[s][];
            for (int i = 0; i < s; i++)
                FfnDropMask[i] = new bool[ffnDim];

            FinalLnNorm = new double[embedDim];
            FinalLnOut  = new double[embedDim];
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }
    }

    private sealed class ForwardBuffers
    {
        public readonly double[][] E;        // [S][EmbedDim] — embedding / inter-layer carrier (S = F+1)
        public readonly double[]   FinalLn;  // [EmbedDim] — final LN output for [CLS]
        public readonly LayerForwardCache[] LayerCaches;

        public readonly int F;
        public readonly int S;        // SeqLen = F + 1
        public readonly int EmbedDim;
        public readonly int NumHeads;
        public readonly int HeadDim;
        public readonly int FfnDim;
        public readonly int NumLayers;

        public ForwardBuffers(int f, int embedDim, int numHeads, int ffnDim, int numLayers = 1)
        {
            F = f; S = f + 1; EmbedDim = embedDim; NumHeads = numHeads;
            HeadDim = embedDim / numHeads; FfnDim = ffnDim;
            NumLayers = numLayers;

            E      = Alloc2D(f + 1, embedDim);
            FinalLn = new double[embedDim];

            LayerCaches = new LayerForwardCache[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerCaches[l] = new LayerForwardCache(f + 1, embedDim, numHeads, ffnDim);
        }

        private static double[][] Alloc2D(int rows, int cols)
        {
            var arr = new double[rows][];
            for (int i = 0; i < rows; i++) arr[i] = new double[cols];
            return arr;
        }

    }

    // ── Per-layer gradient accumulator ────────────────────────────────────────

    private sealed class LayerGrad
    {
        public double[][] dWq, dWk, dWv, dWo;
        public double[] dGamma1, dBeta1, dGamma2, dBeta2;
        public double[][] dWff1;
        public double[] dBff1;
        public double[][] dWff2;
        public double[] dBff2;
        public double[][]? dPosBias;

        public LayerGrad(int D, int Ff)
        {
            dWq = Alloc2D(D, D); dWk = Alloc2D(D, D);
            dWv = Alloc2D(D, D); dWo = Alloc2D(D, D);
            dGamma1 = new double[D]; dBeta1 = new double[D];
            dGamma2 = new double[D]; dBeta2 = new double[D];
            dWff1 = Alloc2D(D, Ff); dBff1 = new double[Ff];
            dWff2 = Alloc2D(Ff, D); dBff2 = new double[D];
        }

        public void Zero()
        {
            Zero2D(dWq); Zero2D(dWk); Zero2D(dWv); Zero2D(dWo);
            Array.Clear(dGamma1); Array.Clear(dBeta1);
            Array.Clear(dGamma2); Array.Clear(dBeta2);
            Zero2D(dWff1); Array.Clear(dBff1);
            Zero2D(dWff2); Array.Clear(dBff2);
            if (dPosBias is not null) Zero2D(dPosBias);
        }

        private static double[][] Alloc2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
        private static void Zero2D(double[][] a) { for (int i = 0; i < a.Length; i++) Array.Clear(a[i]); }
    }

    private sealed class TransformerGrad
    {
        public double[][] dWe, dBe;
        public double[] dClsToken;
        public LayerGrad[] LayerGrads;
        public double[] dWOut;
        public double dBOut;
        public double[] dGammaFinal, dBetaFinal;

        // Scratch buffers for backward pass (shared across layers, sized S = F+1)
        public double[] Scratch1;     // [D]
        public double[] ScratchD;     // [D]
        public double[][] dInput, dLnIn2, dRes1;
        public double[][] dRes1FromLn2, dE, dAttnOut;
        public double[][] dQ, dK, dV;
        public double[][] dLnIn, dInputFromLn1;
        public double[] dAttnWeightPerCol;

        public TransformerGrad(int F, int D, int Ff, int numLayers)
        {
            int S = F + 1;
            dWe = Alloc2D(F, D); dBe = Alloc2D(F, D);
            dClsToken = new double[D];
            dWOut = new double[D];
            dGammaFinal = new double[D];
            dBetaFinal = new double[D];

            LayerGrads = new LayerGrad[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerGrads[l] = new LayerGrad(D, Ff);

            Scratch1 = new double[D];
            ScratchD = new double[D];
            dInput       = Alloc2D(S, D); dLnIn2       = Alloc2D(S, D);
            dRes1        = Alloc2D(S, D); dRes1FromLn2 = Alloc2D(S, D);
            dE           = Alloc2D(S, D); dAttnOut     = Alloc2D(S, D);
            dQ           = Alloc2D(S, D); dK           = Alloc2D(S, D);
            dV           = Alloc2D(S, D); dLnIn        = Alloc2D(S, D);
            dInputFromLn1 = Alloc2D(S, D);
            dAttnWeightPerCol = new double[S];
        }

        public void Zero()
        {
            Zero2D(dWe); Zero2D(dBe);
            Array.Clear(dClsToken);
            foreach (var lg in LayerGrads) lg.Zero();
            Array.Clear(dWOut);
            dBOut = 0;
            Array.Clear(dGammaFinal);
            Array.Clear(dBetaFinal);
        }

        public void Scale(double s)
        {
            Scale2D(dWe, s); Scale2D(dBe, s);
            Scale1D(dClsToken, s);
            foreach (var lg in LayerGrads)
            {
                Scale2D(lg.dWq, s); Scale2D(lg.dWk, s); Scale2D(lg.dWv, s); Scale2D(lg.dWo, s);
                Scale1D(lg.dGamma1, s); Scale1D(lg.dBeta1, s);
                Scale1D(lg.dGamma2, s); Scale1D(lg.dBeta2, s);
                Scale2D(lg.dWff1, s); Scale1D(lg.dBff1, s);
                Scale2D(lg.dWff2, s); Scale1D(lg.dBff2, s);
            }
            Scale1D(dWOut, s);
            dBOut *= s;
            Scale1D(dGammaFinal, s);
            Scale1D(dBetaFinal, s);
        }

        public void ClipNorm(double maxNorm)
        {
            double normSq = dBOut * dBOut;
            normSq += NormSq2D(dWe) + NormSq2D(dBe);
            normSq += NormSq1D(dClsToken);
            foreach (var lg in LayerGrads)
            {
                normSq += NormSq2D(lg.dWq) + NormSq2D(lg.dWk) + NormSq2D(lg.dWv) + NormSq2D(lg.dWo);
                normSq += NormSq1D(lg.dGamma1) + NormSq1D(lg.dBeta1);
                normSq += NormSq1D(lg.dGamma2) + NormSq1D(lg.dBeta2);
                normSq += NormSq2D(lg.dWff1) + NormSq1D(lg.dBff1);
                normSq += NormSq2D(lg.dWff2) + NormSq1D(lg.dBff2);
            }
            normSq += NormSq1D(dWOut);
            normSq += NormSq1D(dGammaFinal) + NormSq1D(dBetaFinal);

            double norm = Math.Sqrt(normSq);
            if (norm > maxNorm)
                Scale(maxNorm / norm);
        }

        private static double[][] Alloc2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
        private static void Zero2D(double[][] a) { for (int i = 0; i < a.Length; i++) Array.Clear(a[i]); }
        private static void Scale2D(double[][] a, double s) { for (int i = 0; i < a.Length; i++) for (int j = 0; j < a[i].Length; j++) a[i][j] *= s; }
        private static void Scale1D(double[] a, double s) { for (int i = 0; i < a.Length; i++) a[i] *= s; }
        private static double NormSq2D(double[][] a) { double s = 0; for (int i = 0; i < a.Length; i++) for (int j = 0; j < a[i].Length; j++) s += a[i][j] * a[i][j]; return s; }
        private static double NormSq1D(double[] a) { double s = 0; for (int i = 0; i < a.Length; i++) s += a[i] * a[i]; return s; }
    }

    // ── Per-layer Adam state ────────────────────────────────────────────────

    private sealed class LayerAdamState
    {
        public double[][] mWq, vWq, mWk, vWk, mWv, vWv, mWo, vWo;
        public double[] mGamma1, vGamma1, mBeta1, vBeta1;
        public double[] mGamma2, vGamma2, mBeta2, vBeta2;
        public double[][] mWff1, vWff1;
        public double[] mBff1, vBff1;
        public double[][] mWff2, vWff2;
        public double[] mBff2, vBff2;
        public double[][]? mPosBias, vPosBias;

        public LayerAdamState(int D, int Ff)
        {
            mWq = Z2D(D, D); vWq = Z2D(D, D); mWk = Z2D(D, D); vWk = Z2D(D, D);
            mWv = Z2D(D, D); vWv = Z2D(D, D); mWo = Z2D(D, D); vWo = Z2D(D, D);
            mGamma1 = new double[D]; vGamma1 = new double[D];
            mBeta1  = new double[D]; vBeta1  = new double[D];
            mGamma2 = new double[D]; vGamma2 = new double[D];
            mBeta2  = new double[D]; vBeta2  = new double[D];
            mWff1 = Z2D(D, Ff); vWff1 = Z2D(D, Ff);
            mBff1 = new double[Ff]; vBff1 = new double[Ff];
            mWff2 = Z2D(Ff, D); vWff2 = Z2D(Ff, D);
            mBff2 = new double[D]; vBff2 = new double[D];
        }

        private static double[][] Z2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }
    }

    private sealed class AdamState
    {
        public double[][] mWe, vWe, mBe, vBe;
        public double[] mClsToken, vClsToken;
        public LayerAdamState[] LayerStates;
        public double[] mGammaFinal, vGammaFinal, mBetaFinal, vBetaFinal;
        public double[] mWOut, vWOut;
        public double mBOut, vBOut;
        public int Step;
        public double Beta1t = 1.0, Beta2t = 1.0;

        public AdamState(int F, int D, int Ff, int numLayers)
        {
            mWe = Z2D(F, D); vWe = Z2D(F, D); mBe = Z2D(F, D); vBe = Z2D(F, D);
            mClsToken = new double[D]; vClsToken = new double[D];
            LayerStates = new LayerAdamState[numLayers];
            for (int l = 0; l < numLayers; l++)
                LayerStates[l] = new LayerAdamState(D, Ff);
            mGammaFinal = new double[D]; vGammaFinal = new double[D];
            mBetaFinal  = new double[D]; vBetaFinal  = new double[D];
            mWOut = new double[D]; vWOut = new double[D];
        }

        private static double[][] Z2D(int r, int c)
        {
            var a = new double[r][]; for (int i = 0; i < r; i++) a[i] = new double[c]; return a;
        }

    }
}
