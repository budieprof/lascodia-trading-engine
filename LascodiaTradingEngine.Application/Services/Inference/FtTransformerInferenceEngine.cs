using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Inference engine for FT-Transformer (Feature Tokenizer + Transformer) models.
/// Embeds each feature via per-feature linear projections, prepends a learnable [CLS] token,
/// runs N pre-norm transformer blocks (multi-head self-attention + FFN with GELU),
/// and reads the final [CLS] representation through a linear head → sigmoid.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(IModelInferenceEngine))]
public sealed class FtTransformerInferenceEngine : IModelInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public bool CanHandle(ModelSnapshot snapshot)
    {
        if (!FtTransformerSnapshotSupport.IsFtTransformer(snapshot))
            return false;

        var normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = FtTransformerSnapshotSupport.ValidateNormalizedSnapshot(normalized);
        return validation.IsValid;
    }

    public InferenceResult? RunInference(
        float[] features, int featureCount, ModelSnapshot snapshot,
        List<Candle> candleWindow, long modelId,
        int mcDropoutSamples, int mcDropoutSeed)
    {
        var normalized = FtTransformerSnapshotSupport.NormalizeSnapshotCopy(snapshot);
        var validation = FtTransformerSnapshotSupport.ValidateNormalizedSnapshot(normalized);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Invalid FT-Transformer snapshot: {string.Join("; ", validation.Issues)}");

        int F  = normalized.FtTransformerEmbedWeights!.Length;
        int D  = normalized.FtTransformerEmbedDim;
        int H  = normalized.FtTransformerNumHeads > 0 ? normalized.FtTransformerNumHeads : 1;
        int Dh = D / H;
        int Ff = normalized.FtTransformerFfnDim > 0 ? normalized.FtTransformerFfnDim : D * 4;
        int S  = F + 1; // [CLS] + F feature tokens
        int numLayers = normalized.FtTransformerNumLayers > 0 ? normalized.FtTransformerNumLayers : 1;

        if (featureCount != F)
        {
            throw new InvalidOperationException(
                $"FT-Transformer featureCount {featureCount} does not match snapshot feature count {F}.");
        }

        if (features.Length != F)
        {
            throw new InvalidOperationException(
                $"FT-Transformer input feature length {features.Length} does not match snapshot feature count {F}.");
        }

        // Allocate embedding matrix
        var E = AllocMatrix(S, D);

        // 1. Place [CLS] token at position 0
        Array.Copy(normalized.FtTransformerClsToken!, E[0], D);

        // 2. Feature embedding: e_f = We[f] * x_f + Be[f]
        var We = normalized.FtTransformerEmbedWeights!;
        var Be = normalized.FtTransformerEmbedBiases!;
        for (int f = 0; f < F; f++)
        {
            double xf = features[f];
            for (int d = 0; d < D; d++)
            {
                E[f + 1][d] = We[f][d] * xf + Be[f][d];
            }
        }

        // 3. Process layer 0 using snapshot's top-level Wq/Wk/Wv/Wo/Gamma/Beta fields
        ProcessLayer(E, S, D, H, Dh, Ff,
            normalized.FtTransformerWq, normalized.FtTransformerWk,
            normalized.FtTransformerWv, normalized.FtTransformerWo,
            normalized.FtTransformerGamma1, normalized.FtTransformerBeta1,
            normalized.FtTransformerGamma2, normalized.FtTransformerBeta2,
            normalized.FtTransformerWff1, normalized.FtTransformerBff1,
            normalized.FtTransformerWff2, normalized.FtTransformerBff2,
            normalized.FtTransformerPosBias);

        // 4. Process additional layers (prefer binary, fall back to JSON)
        if (numLayers > 1)
        {
            List<SerializedLayerWeights>? additionalLayers = null;

            if (normalized.FtTransformerAdditionalLayersBytes is { Length: > 4 } blob)
            {
                try { additionalLayers = DeserializeAdditionalLayers(blob, D, Ff); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Invalid FT-Transformer binary additional-layer payload.", ex);
                }
            }

            if (additionalLayers is null && !string.IsNullOrEmpty(normalized.FtTransformerAdditionalLayersJson))
            {
                try
                {
                    var arr = JsonSerializer.Deserialize<SerializedLayerWeights[]>(
                        normalized.FtTransformerAdditionalLayersJson, JsonOptions);
                    if (arr is not null) additionalLayers = new List<SerializedLayerWeights>(arr);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Invalid FT-Transformer JSON additional-layer payload.", ex);
                }
            }

            if (additionalLayers is null || additionalLayers.Count != numLayers - 1)
            {
                throw new InvalidOperationException(
                    $"FT-Transformer expected {numLayers - 1} additional layers but found {additionalLayers?.Count ?? 0}.");
            }

            for (int l = 0; l < additionalLayers.Count; l++)
            {
                var layer = additionalLayers[l];
                ProcessLayer(E, S, D, H, Dh, Ff,
                    layer.Wq, layer.Wk, layer.Wv, layer.Wo,
                    layer.Gamma1, layer.Beta1,
                    layer.Gamma2, layer.Beta2,
                    layer.Wff1, layer.Bff1,
                    layer.Wff2, layer.Bff2,
                    layer.PosBias);
            }
        }

        // 5. Final LayerNorm on [CLS] position
        var finalLn = new double[D];
        LayerNorm(E[0], normalized.FtTransformerGammaFinal, normalized.FtTransformerBetaFinal, finalLn, D);

        // 6. Classification head
        double logit = normalized.FtTransformerOutputBias;
        if (normalized.FtTransformerOutputWeights is { Length: > 0 } wOut)
            for (int d = 0; d < D; d++)
                logit += wOut[d] * finalLn[d];

        double rawProb = MLFeatureHelper.Sigmoid(logit);

        return new InferenceResult(rawProb, 0.0);
    }

    private static void ProcessLayer(
        double[][] E, int S, int D, int H, int Dh, int Ff,
        double[][]? Wq, double[][]? Wk, double[][]? Wv, double[][]? Wo,
        double[]? gamma1, double[]? beta1,
        double[]? gamma2, double[]? beta2,
        double[][]? Wff1, double[]? Bff1,
        double[][]? Wff2, double[]? Bff2,
        double[][]? posBias = null)
    {
        if (Wq is null || Wk is null || Wv is null || Wo is null) return;

        // Pre-norm LN1
        var lnIn = AllocMatrix(S, D);
        for (int i = 0; i < S; i++)
            LayerNorm(E[i], gamma1, beta1, lnIn[i], D);

        // Q, K, V projections
        var Q = MatMul(lnIn, Wq, S, D, D);
        var K = MatMul(lnIn, Wk, S, D, D);
        var V = MatMul(lnIn, Wv, S, D, D);

        // Multi-head attention
        var attnOut = AllocMatrix(S, D);
        double sqrtDh = Math.Sqrt(Dh);
        for (int h = 0; h < H; h++)
        {
            int hOff = h * Dh;
            bool hasBias = posBias is not null && h < posBias.Length && posBias[h] is { Length: > 0 };

            // Scores
            var scores = new double[S * S];
            for (int r = 0; r < S; r++)
                for (int c = 0; c < S; c++)
                {
                    double dot = 0;
                    for (int d = 0; d < Dh; d++)
                        dot += Q[r][hOff + d] * K[c][hOff + d];
                    int idx = r * S + c;
                    scores[idx] = dot / sqrtDh;
                    if (hasBias && idx < posBias![h].Length)
                        scores[idx] += posBias[h][idx];
                }

            // Softmax per row
            for (int r = 0; r < S; r++)
            {
                int rowOff = r * S;
                double max = double.MinValue;
                for (int c = 0; c < S; c++)
                    if (scores[rowOff + c] > max) max = scores[rowOff + c];
                double sum = 0;
                for (int c = 0; c < S; c++)
                {
                    scores[rowOff + c] = Math.Exp(scores[rowOff + c] - max);
                    sum += scores[rowOff + c];
                }
                sum += 1e-10;
                for (int c = 0; c < S; c++)
                    scores[rowOff + c] /= sum;
            }

            // Weighted V
            for (int r = 0; r < S; r++)
            {
                int rowOff = r * S;
                for (int d = 0; d < Dh; d++)
                {
                    double s = 0;
                    for (int c = 0; c < S; c++)
                        s += scores[rowOff + c] * V[c][hOff + d];
                    attnOut[r][hOff + d] = s;
                }
            }
        }

        // Output projection + residual
        var res1 = AllocMatrix(S, D);
        for (int i = 0; i < S; i++)
            for (int d = 0; d < D; d++)
            {
                double s = 0;
                for (int k = 0; k < D && k < Wo.Length; k++)
                    s += attnOut[i][k] * (d < Wo[k].Length ? Wo[k][d] : 0.0);
                res1[i][d] = s + E[i][d];
            }

        // Pre-norm LN2
        var lnIn2 = AllocMatrix(S, D);
        for (int i = 0; i < S; i++)
            LayerNorm(res1[i], gamma2, beta2, lnIn2[i], D);

        // FFN: Linear → GELU → Linear + residual
        for (int i = 0; i < S; i++)
        {
            var ffnH = new double[Ff];
            if (Wff1 is not null)
                for (int fh = 0; fh < Ff; fh++)
                {
                    double s = Bff1 is not null && fh < Bff1.Length ? Bff1[fh] : 0.0;
                    for (int d = 0; d < D && d < Wff1.Length; d++)
                        s += lnIn2[i][d] * (fh < Wff1[d].Length ? Wff1[d][fh] : 0.0);
                    ffnH[fh] = GELU(s);
                }

            for (int d = 0; d < D; d++)
            {
                double s = Bff2 is not null && d < Bff2.Length ? Bff2[d] : 0.0;
                if (Wff2 is not null)
                    for (int fh = 0; fh < Ff && fh < Wff2.Length; fh++)
                        s += ffnH[fh] * (d < Wff2[fh].Length ? Wff2[fh][d] : 0.0);
                E[i][d] = s + res1[i][d];
            }
        }
    }

    private static void LayerNorm(double[] x, double[]? gamma, double[]? beta, double[] y, int D)
    {
        double mean = 0;
        for (int d = 0; d < D; d++) mean += x[d];
        mean /= D;

        double variance = 0;
        for (int d = 0; d < D; d++) { double diff = x[d] - mean; variance += diff * diff; }
        double invStd = 1.0 / Math.Sqrt(variance / D + 1e-8);

        for (int d = 0; d < D; d++)
        {
            double g = gamma is not null && d < gamma.Length ? gamma[d] : 1.0;
            double b = beta is not null && d < beta.Length ? beta[d] : 0.0;
            y[d] = g * (x[d] - mean) * invStd + b;
        }
    }

    private static double GELU(double x)
    {
        const double sqrt2OverPi = 0.7978845608028654;
        double inner = sqrt2OverPi * (x + 0.044715 * x * x * x);
        return 0.5 * x * (1.0 + Math.Tanh(inner));
    }

    private static double[][] MatMul(double[][] A, double[][] B, int M, int K, int N)
    {
        var C = AllocMatrix(M, N);
        for (int i = 0; i < M; i++)
            for (int j = 0; j < N; j++)
            {
                double s = 0;
                for (int k = 0; k < K && k < B.Length; k++)
                    s += A[i][k] * (j < B[k].Length ? B[k][j] : 0.0);
                C[i][j] = s;
            }
        return C;
    }

    private static double[][] AllocMatrix(int rows, int cols)
    {
        var m = new double[rows][];
        for (int i = 0; i < rows; i++) m[i] = new double[cols];
        return m;
    }

    /// <summary>
    /// Serialised transformer layer weights for layers 1..N-1 (layer 0 uses top-level snapshot fields).
    /// Must match the structure written by <c>FtTransformerModelTrainer</c>.
    /// </summary>
    private sealed class SerializedLayerWeights
    {
        public double[][]? Wq     { get; set; }
        public double[][]? Wk     { get; set; }
        public double[][]? Wv     { get; set; }
        public double[][]? Wo     { get; set; }
        public double[]?   Gamma1 { get; set; }
        public double[]?   Beta1  { get; set; }
        public double[]?   Gamma2 { get; set; }
        public double[]?   Beta2  { get; set; }
        public double[][]? Wff1   { get; set; }
        public double[]?   Bff1   { get; set; }
        public double[][]? Wff2   { get; set; }
        public double[]?   Bff2   { get; set; }
        public double[][]? PosBias { get; set; }
    }

    // ── Binary layer deserialisation (matches FtTransformerModelTrainer serialiser) ──

    private static List<SerializedLayerWeights> DeserializeAdditionalLayers(byte[] data, int D, int Ff)
    {
        // Verify CRC32 trailer
        if (data.Length < 4) throw new InvalidOperationException("Binary blob too short for CRC trailer.");
        int payloadLen = data.Length - 4;
        uint storedCrc = BitConverter.ToUInt32(data, payloadLen);
        uint computedCrc = ComputeCrc32(data.AsSpan(0, payloadLen));
        if (storedCrc != computedCrc)
            throw new InvalidOperationException($"CRC32 mismatch: stored={storedCrc:X8} computed={computedCrc:X8}");

        var result = new List<SerializedLayerWeights>();
        using var ms = new MemoryStream(data, 0, payloadLen);
        using var br = new BinaryReader(ms);

        int numLayers = br.ReadInt32();
        int numHeads  = br.ReadInt32();
        int seqSq     = br.ReadInt32(); // S*S for PosBias (0 if no positional bias)

        for (int l = 0; l < numLayers; l++)
        {
            var lw = new SerializedLayerWeights
            {
                Wq     = ReadMatrix(br, D, D),
                Wk     = ReadMatrix(br, D, D),
                Wv     = ReadMatrix(br, D, D),
                Wo     = ReadMatrix(br, D, D),
                Gamma1 = ReadVector(br, D),
                Beta1  = ReadVector(br, D),
                Wff1   = ReadMatrix(br, D, Ff),
                Bff1   = ReadVector(br, Ff),
                Wff2   = ReadMatrix(br, Ff, D),
                Bff2   = ReadVector(br, D),
                Gamma2 = ReadVector(br, D),
                Beta2  = ReadVector(br, D),
            };
            if (seqSq > 0 && numHeads > 0)
                lw.PosBias = ReadMatrix(br, numHeads, seqSq);
            result.Add(lw);
        }
        return result;
    }

    private static double[][] ReadMatrix(BinaryReader br, int rows, int cols)
    {
        var m = new double[rows][];
        for (int r = 0; r < rows; r++)
        {
            m[r] = new double[cols];
            for (int c = 0; c < cols; c++)
                m[r][c] = br.ReadDouble();
        }
        return m;
    }

    private static double[] ReadVector(BinaryReader br, int len)
    {
        var v = new double[len];
        for (int i = 0; i < len; i++)
            v[i] = br.ReadDouble();
        return v;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
        }
        return ~crc;
    }
}
