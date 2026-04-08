using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TabNetModelTrainer
{
    private sealed class TabNetRunContext
    {
        public required double HuberDelta { get; init; }
        public required int CalibrationEpochs { get; init; }
        public required double CalibrationLr { get; init; }
        public required int MinCalibrationSamples { get; init; }
        public TabNetSnapshotSupport.WarmStartLoadReport WarmStartLoadReport { get; set; } =
            new(0, 0, 0, 0, 0);
        public TabNetAutoTuneTraceEntry[] AutoTuneTrace { get; set; } = [];
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  INTERNAL WEIGHT CONTAINER
    //  Encapsulates all TabNet architecture weights to avoid parameter explosion.
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class TabNetWeights
    {
        // Architecture dimensions
        public int NSteps;
        public int F;
        public int HiddenDim;
        public int AttentionDim;
        public int SharedLayers;
        public int StepLayers;
        public double Gamma;
        public bool UseSparsemax;
        public bool UseGlu;

        // Initial BN FC for step-0 attention symmetry (features → F)
        public double[][] InitialBnFcW = [];  // [F][F]
        public double[]   InitialBnFcB = [];  // [F]

        // Shared Feature Transformer: [layer] → weights/biases
        public double[][][] SharedW  = [];   // [layer][outDim][inDim]
        public double[][]   SharedB  = [];   // [layer][outDim]
        public double[][][] SharedGW = [];   // GLU gate weights
        public double[][]   SharedGB = [];   // GLU gate biases

        // Step-specific Feature Transformer: [step][layer] → weights/biases
        public double[][][][] StepW  = [];   // [step][layer][outDim][inDim]
        public double[][][]   StepB  = [];   // [step][layer][outDim]
        public double[][][][] StepGW = [];   // [step][layer][outDim][inDim]
        public double[][][]   StepGB = [];   // [step][layer][outDim]

        // Attentive Transformer: FC per step
        public double[][][] AttnFcW = [];    // [step][attDim][inDim]
        public double[][]   AttnFcB = [];    // [step][attDim]

        // Batch Normalization parameters: indexed linearly
        // Layout: [attn_bn_0, ..., attn_bn_{nSteps-1}, shared_bn_0, ..., step_0_bn_0, ...]
        public double[][] BnGamma = [];      // [bnIdx][dim]
        public double[][] BnBeta  = [];      // [bnIdx][dim]
        public double[][] BnMean  = [];      // running mean [bnIdx][dim]
        public double[][] BnVar   = [];      // running var  [bnIdx][dim]

        // Output head
        public double[] OutputW = [];        // [hiddenDim]
        public double   OutputB;

        // Magnitude head
        public double[] MagW = [];           // [hiddenDim]
        public double   MagB;

        public int TotalBnLayers;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ADAM MOMENT CONTAINER
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class AdamState
    {
        // Initial BN FC moments
        public double[][] MInitialBnFcW = [], VInitialBnFcW = [];
        public double[]   MInitialBnFcB = [], VInitialBnFcB = [];

        // Mirrors TabNetWeights structure with m (first moment) and v (second moment)
        public double[][][] MSharedW = [], VSharedW = [];
        public double[][]   MSharedB = [], VSharedB = [];
        public double[][][] MSharedGW = [], VSharedGW = [];
        public double[][]   MSharedGB = [], VSharedGB = [];

        public double[][][][] MStepW = [], VStepW = [];
        public double[][][]   MStepB = [], VStepB = [];
        public double[][][][] MStepGW = [], VStepGW = [];
        public double[][][]   MStepGB = [], VStepGB = [];

        public double[][][] MAttnFcW = [], VAttnFcW = [];
        public double[][]   MAttnFcB = [], VAttnFcB = [];

        public double[][] MBnGamma = [], VBnGamma = [];
        public double[][] MBnBeta = [], VBnBeta = [];

        public double[] MOutputW = [], VOutputW = [];
        public double MOutputB, VOutputB;

        public double[] MMagW = [], VMagW = [];
        public double MMagB, VMagB;

        public int T; // Adam step counter
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FORWARD-PASS RESULT (pooled to avoid per-sample GC pressure)
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class ForwardResult
    {
        public double   Prob;
        public double[] AggregatedH  = [];     // [hiddenDim] — sum of per-step ReLU outputs
        public double[][] StepH      = [];     // [step][hiddenDim] — per-step output before ReLU
        public double[][] StepAttn   = [];     // [step][F] — attention masks
        public double[][] StepMasked = [];     // [step][F] — masked input
        public double[][][] StepSharedPre   = [];  // [step][layer][hiddenDim] — BN output
        public double[][][] StepSharedGate  = [];  // [step][layer][hiddenDim] — gate sigmoid
        public double[][][] StepSharedXNorm = [];  // [step][layer][hiddenDim] — BN xNorm
        public double[][][] StepSharedFcIn  = [];  // [step][layer][inDim] — FC input
        public double[][][] StepStepPre     = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepGate    = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepXNorm   = [];  // [step][layer][hiddenDim]
        public double[][][] StepStepFcIn    = [];  // [step][layer][inDim]
        public double[][] StepAttnPre = [];    // [step][F] — pre-attention logits
        public double[]   PriorScales = [];    // [F] — final prior scales (after all steps)
        public double[][] StepPriorScales = []; // [step][F] — per-step prior scales (before attention)
        public double[][] StepAttnXNorm = [];   // [step][F] — attention BN xNorm (for backward)

        // Pooled scratch buffers to avoid per-call allocations in ForwardPass
        public double[] HPrev = [];            // [hiddenDim]
        public double[] AttnInput = [];        // [F]

        // Pooled FcBnGlu buffers to avoid 5 allocations per FC→BN→GLU call
        public FcBnGluBuffers? GluBuf;
        public double[] GluOutA = [];          // [hiddenDim] — alternating scratch buffers for pooled GLU
        public double[] GluOutB = [];          // [hiddenDim] — prevents aliasing when h == previous output

        // Dropout masks cached for backward: true = kept (not dropped), false = dropped
        public bool[][][] StepSharedDropMask = []; // [step][layer][hiddenDim]
        public bool[][][] StepStepDropMask   = []; // [step][layer][hiddenDim]

        public static ForwardResult Allocate(int nSteps, int F, int H, int sharedLayers, int stepLayers)
        {
            var r = new ForwardResult
            {
                AggregatedH  = new double[H],
                StepH        = AllocJagged(nSteps, H),
                StepAttn     = AllocJagged(nSteps, F),
                StepMasked   = AllocJagged(nSteps, F),
                StepAttnPre  = AllocJagged(nSteps, F),
                PriorScales  = new double[F],
                StepPriorScales = AllocJagged(nSteps, F),
                StepAttnXNorm   = AllocJagged(nSteps, F),
                HPrev        = new double[H],
                AttnInput    = new double[F],
                GluBuf       = FcBnGluBuffers.Allocate(H, Math.Max(F, H)),
                GluOutA      = new double[H],
                GluOutB      = new double[H],
                StepSharedPre   = Alloc3(nSteps, sharedLayers, H),
                StepSharedGate  = Alloc3(nSteps, sharedLayers, H),
                StepSharedXNorm = Alloc3(nSteps, sharedLayers, H),
                StepSharedFcIn  = new double[nSteps][][],
                StepStepPre     = Alloc3(nSteps, stepLayers, H),
                StepStepGate    = Alloc3(nSteps, stepLayers, H),
                StepStepXNorm   = Alloc3(nSteps, stepLayers, H),
                StepStepFcIn    = Alloc3(nSteps, stepLayers, H),
                StepSharedDropMask = AllocBool3(nSteps, sharedLayers, H),
                StepStepDropMask   = AllocBool3(nSteps, stepLayers, H),
            };
            for (int s = 0; s < nSteps; s++)
            {
                r.StepSharedFcIn[s] = new double[sharedLayers][];
                for (int l = 0; l < sharedLayers; l++)
                    r.StepSharedFcIn[s][l] = new double[l == 0 ? F : H];
            }
            return r;
        }

        public void Reset(int H)
        {
            Prob = 0;
            Array.Clear(AggregatedH);
            Array.Clear(HPrev);

            // Clear all step-level arrays to prevent stale data leaking across samples
            for (int s = 0; s < StepH.Length; s++)
            {
                Array.Clear(StepH[s]);
                Array.Clear(StepAttn[s]);
                Array.Clear(StepMasked[s]);
                Array.Clear(StepAttnPre[s]);
                Array.Clear(StepPriorScales[s]);
                Array.Clear(StepAttnXNorm[s]);

                for (int l = 0; l < StepSharedPre[s].Length; l++)
                {
                    Array.Clear(StepSharedPre[s][l]);
                    Array.Clear(StepSharedGate[s][l]);
                    Array.Clear(StepSharedXNorm[s][l]);
                    Array.Clear(StepSharedFcIn[s][l]);
                }
                for (int l = 0; l < StepStepPre[s].Length; l++)
                {
                    Array.Clear(StepStepPre[s][l]);
                    Array.Clear(StepStepGate[s][l]);
                    Array.Clear(StepStepXNorm[s][l]);
                    Array.Clear(StepStepFcIn[s][l]);
                }
            }
            Array.Clear(PriorScales);
            Array.Clear(AttnInput);
        }

        private static double[][] AllocJagged(int d1, int d2)
        {
            var a = new double[d1][];
            for (int i = 0; i < d1; i++) a[i] = new double[d2];
            return a;
        }

        private static double[][][] Alloc3(int d1, int d2, int d3)
        {
            var a = new double[d1][][];
            for (int i = 0; i < d1; i++)
            {
                a[i] = new double[d2][];
                for (int j = 0; j < d2; j++) a[i][j] = new double[d3];
            }
            return a;
        }

        private static bool[][][] AllocBool3(int d1, int d2, int d3)
        {
            var a = new bool[d1][][];
            for (int i = 0; i < d1; i++)
            {
                a[i] = new bool[d2][];
                for (int j = 0; j < d2; j++)
                {
                    a[i][j] = new bool[d3];
                    Array.Fill(a[i][j], true); // default = kept (no dropout)
                }
            }
            return a;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BACKWARD-PASS BUFFERS (pooled to avoid per-sample GC pressure)
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class BackwardBuffers
    {
        public double[] DAggH     = [];
        public double[] DH        = [];
        public double[] DInput    = [];
        public double[] DResidual = [];
        public double[] DBnOut    = [];
        public double[] DGateIn   = [];
        public double[] DPreFc    = [];
        public double[] DNextInputH = []; // [H] for step/shared backward
        public double[] DNextInputF = []; // [F] for shared layer 0 backward
        public double[] DAttn     = [];
        public double[] DAttnLogits = [];
        public double[] DAttnBnOut = [];
        public double[] DAttnInput = [];
        public double[] DFutureStep = [];
        public double[] DPrevStep = [];

        public static BackwardBuffers Allocate(int F, int H)
        {
            return new BackwardBuffers
            {
                DAggH       = new double[H],
                DH          = new double[H],
                DInput      = new double[H],
                DResidual   = new double[H],
                DBnOut      = new double[H],
                DGateIn     = new double[H],
                DPreFc      = new double[H],
                DNextInputH = new double[H],
                DNextInputF = new double[F],
                DAttn       = new double[F],
                DAttnLogits = new double[F],
                DAttnBnOut  = new double[F],
                DAttnInput  = new double[F],
                DFutureStep = new double[H],
                DPrevStep   = new double[H],
            };
        }

        public void Clear()
        {
            Array.Clear(DAggH);
            Array.Clear(DH);
            Array.Clear(DInput);
            Array.Clear(DResidual);
            Array.Clear(DBnOut);
            Array.Clear(DGateIn);
            Array.Clear(DPreFc);
            Array.Clear(DNextInputH);
            Array.Clear(DNextInputF);
            Array.Clear(DAttn);
            Array.Clear(DAttnLogits);
            Array.Clear(DAttnBnOut);
            Array.Clear(DAttnInput);
            Array.Clear(DFutureStep);
            Array.Clear(DPrevStep);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FC→BN→GLU BUFFERS (pooled to avoid 5 allocations per call)
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class FcBnGluBuffers
    {
        public double[] Linear = [];
        public double[] Gate   = [];
        public double[] Output = [];
        public double[] FcInput = []; // cached input clone for backward

        public static FcBnGluBuffers Allocate(int outDim, int maxInDim)
        {
            return new FcBnGluBuffers
            {
                Linear  = new double[outDim],
                Gate    = new double[outDim],
                Output  = new double[outDim],
                FcInput = new double[maxInDim],
            };
        }

        public void Clear(int outDim, int inDim)
        {
            Array.Clear(Linear, 0, outDim);
            Array.Clear(Gate, 0, outDim);
            Array.Clear(Output, 0, outDim);
            Array.Clear(FcInput, 0, inDim);
        }
    }
}
