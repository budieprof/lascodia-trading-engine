using System.Text.Json;

namespace LascodiaTradingEngine.Application.Services.ML;

internal sealed class FtSerializedLayerWeights
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

internal static class FtTransformerAdditionalLayerCodec
{
    internal static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = false, MaxDepth = 128 };

    internal static byte[] SerializeBinary(
        IReadOnlyList<FtSerializedLayerWeights> layers,
        int embedDim,
        int ffnDim,
        int numHeads,
        int seqLen,
        bool hasPositionalBias)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(layers.Count);
        bw.Write(numHeads);
        bw.Write(hasPositionalBias ? seqLen * seqLen : 0);

        foreach (var layer in layers)
        {
            WriteBinaryMatrix(bw, layer.Wq!, embedDim, embedDim);
            WriteBinaryMatrix(bw, layer.Wk!, embedDim, embedDim);
            WriteBinaryMatrix(bw, layer.Wv!, embedDim, embedDim);
            WriteBinaryMatrix(bw, layer.Wo!, embedDim, embedDim);
            WriteBinaryVector(bw, layer.Gamma1!, embedDim);
            WriteBinaryVector(bw, layer.Beta1!, embedDim);
            WriteBinaryMatrix(bw, layer.Wff1!, embedDim, ffnDim);
            WriteBinaryVector(bw, layer.Bff1!, ffnDim);
            WriteBinaryMatrix(bw, layer.Wff2!, ffnDim, embedDim);
            WriteBinaryVector(bw, layer.Bff2!, embedDim);
            WriteBinaryVector(bw, layer.Gamma2!, embedDim);
            WriteBinaryVector(bw, layer.Beta2!, embedDim);
            if (hasPositionalBias && layer.PosBias is not null)
                WriteBinaryMatrix(bw, layer.PosBias, numHeads, seqLen * seqLen);
        }

        bw.Flush();
        byte[] payload = ms.ToArray();
        uint crc = ComputeCrc32(payload);
        var result = new byte[payload.Length + 4];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        BitConverter.TryWriteBytes(result.AsSpan(payload.Length), crc);
        return result;
    }

    internal static List<FtSerializedLayerWeights> DeserializeBinary(byte[] data, int embedDim, int ffnDim)
    {
        if (data.Length < 4)
            throw new InvalidOperationException("Binary blob too short.");

        int payloadLength = data.Length - 4;
        uint storedCrc = BitConverter.ToUInt32(data, payloadLength);
        uint computedCrc = ComputeCrc32(data.AsSpan(0, payloadLength));
        if (storedCrc != computedCrc)
            throw new InvalidOperationException("CRC32 mismatch.");

        var layers = new List<FtSerializedLayerWeights>();
        using var ms = new MemoryStream(data, 0, payloadLength);
        using var br = new BinaryReader(ms);
        int layerCount = br.ReadInt32();
        int numHeads = br.ReadInt32();
        int seqSq = br.ReadInt32();

        for (int i = 0; i < layerCount; i++)
        {
            var payload = new FtSerializedLayerWeights
            {
                Wq = ReadBinaryMatrix(br, embedDim, embedDim),
                Wk = ReadBinaryMatrix(br, embedDim, embedDim),
                Wv = ReadBinaryMatrix(br, embedDim, embedDim),
                Wo = ReadBinaryMatrix(br, embedDim, embedDim),
                Gamma1 = ReadBinaryVector(br, embedDim),
                Beta1 = ReadBinaryVector(br, embedDim),
                Wff1 = ReadBinaryMatrix(br, embedDim, ffnDim),
                Bff1 = ReadBinaryVector(br, ffnDim),
                Wff2 = ReadBinaryMatrix(br, ffnDim, embedDim),
                Bff2 = ReadBinaryVector(br, embedDim),
                Gamma2 = ReadBinaryVector(br, embedDim),
                Beta2 = ReadBinaryVector(br, embedDim),
            };

            if (seqSq > 0 && numHeads > 0)
                payload.PosBias = ReadBinaryMatrix(br, numHeads, seqSq);

            layers.Add(payload);
        }

        return layers;
    }

    private static void WriteBinaryMatrix(BinaryWriter bw, double[][] matrix, int rows, int cols)
    {
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
                bw.Write(matrix[row][col]);
    }

    private static void WriteBinaryVector(BinaryWriter bw, double[] vector, int length)
    {
        for (int i = 0; i < length; i++)
            bw.Write(vector[i]);
    }

    private static double[][] ReadBinaryMatrix(BinaryReader br, int rows, int cols)
    {
        var matrix = new double[rows][];
        for (int row = 0; row < rows; row++)
        {
            matrix[row] = new double[cols];
            for (int col = 0; col < cols; col++)
                matrix[row][col] = br.ReadDouble();
        }
        return matrix;
    }

    private static double[] ReadBinaryVector(BinaryReader br, int length)
    {
        var vector = new double[length];
        for (int i = 0; i < length; i++)
            vector[i] = br.ReadDouble();
        return vector;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        const uint Polynomial = 0xEDB88320u;
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
        }
        return ~crc;
    }
}
