namespace LascodiaTradingEngine.Application.Services.ML;

public sealed partial class TcnModelTrainer
{
    // ── Private weight initialisation (Architecture partial) ───────────────

    /// <summary>
    /// Initialises a weight array with Gaussian noise using the Box-Muller transform.
    /// Duplicated here because the main file's <c>InitWeights</c> is private and
    /// inaccessible from a separate partial file compilation unit.
    /// </summary>
    private static double[] InitArchWeights(int count, Random rng, double scale)
    {
        var w = new double[count];
        for (int i = 0; i < count; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            w[i] = scale * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return w;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Item 1 — Gated TCN variant
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialises gated TCN conv weights. Each block has two sets of conv weights:
    /// convW[b] for the filter path (tanh) and gateW[b] for the gate path (sigmoid).
    /// Output = tanh(filter_conv) ⊙ sigmoid(gate_conv).
    /// </summary>
    internal static (double[][] GateW, double[][] GateB) InitGateWeights(
        int numBlocks, int[] blockInC, int filters, int kernelSize, Random rng)
    {
        var gateW = new double[numBlocks][];
        var gateB = new double[numBlocks][];
        for (int b = 0; b < numBlocks; b++)
        {
            int inC = blockInC[b];
            gateW[b] = InitArchWeights(filters * inC * kernelSize, rng, Math.Sqrt(2.0 / (inC * kernelSize)));
            gateB[b] = new double[filters];
        }
        return (gateW, gateB);
    }

    /// <summary>
    /// Forward pass for a single gated TCN block at timestep t.
    /// Returns the gated output: tanh(filterConv) * sigmoid(gateConv).
    /// </summary>
    internal static void GatedBlockForward(
        double[] filterConvW, double[] filterConvB,
        double[] gateConvW, double[] gateConvB,
        double[][] blockInput, int t, int inC, int filters, int kernelSize, int dilation,
        double[] filterOutput, double[] gateOutput, double[] combinedOutput)
    {
        for (int o = 0; o < filters; o++)
        {
            double filterSum = filterConvB[o];
            double gateSum = gateConvB[o];
            for (int k = 0; k < kernelSize; k++)
            {
                int srcT = t - k * dilation;
                if (srcT < 0) continue;
                for (int c = 0; c < inC; c++)
                {
                    int wIdx = (o * inC + c) * kernelSize + k;
                    filterSum += filterConvW[wIdx] * blockInput[srcT][c];
                    gateSum += gateConvW[wIdx] * blockInput[srcT][c];
                }
            }
            filterOutput[o] = Math.Tanh(filterSum);
            gateOutput[o] = 1.0 / (1.0 + Math.Exp(-gateSum)); // sigmoid
            combinedOutput[o] = filterOutput[o] * gateOutput[o];
        }
    }

    /// <summary>
    /// Backward pass for gated block. Computes gradients for both filter and gate conv weights.
    /// Uses the actual weight values (not gradient accumulators) for the chain rule through
    /// <paramref name="dBlockInput"/>.
    /// </summary>
    internal static void GatedBlockBackward(
        double[] dOutput, double[] filterOutput, double[] gateOutput,
        double[] filterConvW, double[] gateConvW,
        double[] dFilterConvW, double[] dFilterConvB,
        double[] dGateConvW, double[] dGateConvB,
        double[][] blockInput, int t, int inC, int filters, int kernelSize, int dilation,
        double[][] dBlockInput)
    {
        for (int o = 0; o < filters; o++)
        {
            // d/d(filter) = dOutput * gate * (1 - tanh²(filter))
            double dFilter = dOutput[o] * gateOutput[o] * (1.0 - filterOutput[o] * filterOutput[o]);
            // d/d(gate) = dOutput * filter * gate * (1 - gate)
            double dGate = dOutput[o] * filterOutput[o] * gateOutput[o] * (1.0 - gateOutput[o]);

            dFilterConvB[o] += dFilter;
            dGateConvB[o] += dGate;

            for (int k = 0; k < kernelSize; k++)
            {
                int srcT = t - k * dilation;
                if (srcT < 0) continue;
                for (int c = 0; c < inC; c++)
                {
                    int wIdx = (o * inC + c) * kernelSize + k;
                    dFilterConvW[wIdx] += dFilter * blockInput[srcT][c];
                    dGateConvW[wIdx] += dGate * blockInput[srcT][c];
                    dBlockInput[srcT][c] += dFilter * filterConvW[wIdx] + dGate * gateConvW[wIdx];
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Item 2 — Late-splitting multi-task heads
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates separate conv weights for the magnitude branch (blocks from splitBlock onward).
    /// The direction branch reuses the main convW/convB arrays.
    /// </summary>
    internal static (double[][] MagConvW, double[][] MagConvB, double[]?[] MagResW) InitLateSplitWeights(
        int splitBlock, int numBlocks, int[] blockInC, int filters, int kernelSize, Random rng)
    {
        int splitCount = numBlocks - splitBlock;
        var magConvW = new double[splitCount][];
        var magConvB = new double[splitCount][];
        var magResW = new double[]?[splitCount];
        for (int i = 0; i < splitCount; i++)
        {
            int b = splitBlock + i;
            int inC = blockInC[b];
            magConvW[i] = InitArchWeights(filters * inC * kernelSize, rng, Math.Sqrt(2.0 / (inC * kernelSize)));
            magConvB[i] = new double[filters];
            if (inC != filters)
                magResW[i] = InitArchWeights(filters * inC, rng, Math.Sqrt(2.0 / inC));
        }
        return (magConvW, magConvB, magResW);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Item 3 — Configurable kernel sizes per block
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses comma-separated kernel sizes (e.g. "3,3,5,5") into an int array.
    /// Returns null if empty/invalid, falling back to uniform kernel size.
    /// </summary>
    internal static int[]? ParseKernelSizes(string? kernelSizesStr, int numBlocks)
    {
        if (string.IsNullOrWhiteSpace(kernelSizesStr)) return null;
        var parts = kernelSizesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != numBlocks) return null;
        var sizes = new int[numBlocks];
        for (int i = 0; i < numBlocks; i++)
        {
            if (!int.TryParse(parts[i], out int k) || k < 2) return null;
            sizes[i] = k;
        }
        return sizes;
    }

    /// <summary>
    /// Computes receptive field for variable kernel sizes: 1 + Σ(kernelSize[b]-1) × dilation[b].
    /// </summary>
    internal static int ComputeReceptiveField(int[] kernelSizes, int[] dilations)
    {
        int rf = 1;
        for (int b = 0; b < kernelSizes.Length; b++)
            rf += (kernelSizes[b] - 1) * dilations[b];
        return rf;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Item 4 — Depthwise separable convolutions
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Depthwise separable convolution: depthwise (per-channel) conv followed by 1×1 pointwise conv.
    /// Reduces parameters from (filters × inC × K) to (inC × K + filters × inC).
    /// </summary>
    internal static (double[] DepthwiseW, double[] DepthwiseB, double[] PointwiseW, double[] PointwiseB)
        InitDepthwiseSeparableWeights(int inC, int filters, int kernelSize, Random rng)
    {
        // Depthwise: [inC × kernelSize] — one filter per input channel
        var dwW = InitArchWeights(inC * kernelSize, rng, Math.Sqrt(2.0 / kernelSize));
        var dwB = new double[inC];
        // Pointwise: [filters × inC] — 1×1 conv to mix channels
        var pwW = InitArchWeights(filters * inC, rng, Math.Sqrt(2.0 / inC));
        var pwB = new double[filters];
        return (dwW, dwB, pwW, pwB);
    }

    /// <summary>
    /// Forward pass for depthwise separable conv at timestep t.
    /// Step 1: depthwise conv (per-channel with causal dilation)
    /// Step 2: pointwise 1×1 conv (channel mixing)
    /// </summary>
    internal static void DepthwiseSeparableForward(
        double[] dwW, double[] dwB, double[] pwW, double[] pwB,
        double[][] blockInput, int t, int inC, int filters, int kernelSize, int dilation,
        double[] depthwiseOutput, double[] output)
    {
        // Depthwise conv: each input channel gets its own kernel
        for (int c = 0; c < inC; c++)
        {
            double sum = dwB[c];
            for (int k = 0; k < kernelSize; k++)
            {
                int srcT = t - k * dilation;
                if (srcT < 0) continue;
                sum += dwW[c * kernelSize + k] * blockInput[srcT][c];
            }
            depthwiseOutput[c] = sum;
        }
        // Pointwise 1×1 conv: mix channels
        for (int o = 0; o < filters; o++)
        {
            double sum = pwB[o];
            int off = o * inC;
            for (int c = 0; c < inC; c++)
                sum += pwW[off + c] * depthwiseOutput[c];
            output[o] = sum;
        }
    }

    /// <summary>
    /// Backward pass for depthwise separable conv at timestep t.
    /// </summary>
    internal static void DepthwiseSeparableBackward(
        double[] dOutput, double[] depthwiseOutput,
        double[] dwW, double[] pwW,
        double[] dDwW, double[] dDwB, double[] dPwW, double[] dPwB,
        double[][] blockInput, int t, int inC, int filters, int kernelSize, int dilation,
        double[][] dBlockInput)
    {
        // Backward through pointwise
        var dDepthwise = new double[inC];
        for (int o = 0; o < filters; o++)
        {
            dPwB[o] += dOutput[o];
            int off = o * inC;
            for (int c = 0; c < inC; c++)
            {
                dPwW[off + c] += dOutput[o] * depthwiseOutput[c];
                dDepthwise[c] += dOutput[o] * pwW[off + c];
            }
        }
        // Backward through depthwise
        for (int c = 0; c < inC; c++)
        {
            dDwB[c] += dDepthwise[c];
            for (int k = 0; k < kernelSize; k++)
            {
                int srcT = t - k * dilation;
                if (srcT < 0) continue;
                dDwW[c * kernelSize + k] += dDepthwise[c] * blockInput[srcT][c];
                dBlockInput[srcT][c] += dDepthwise[c] * dwW[c * kernelSize + k];
            }
        }
    }
}
