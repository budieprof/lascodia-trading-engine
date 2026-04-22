using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Runtime projection of raw feature vectors through a trained CPC encoder. Dispatches on
/// <see cref="MLCpcEncoder.EncoderType"/>:
/// <list type="bullet">
///   <item><see cref="CpcEncoderType.Linear"/> — mirrors <c>CpcPretrainer.Encode</c>'s
///   <c>ReLU(W_e · x)</c> exactly.</item>
///   <item><see cref="CpcEncoderType.Tcn"/> — re-runs the 2-layer dilated causal TCN forward
///   pass from <see cref="CpcTcnPretrainer"/> over the full input sequence and returns the
///   last timestep's output.</item>
/// </list>
/// Both branches use cached deserialised weights per encoder id to keep inference hot paths
/// off the JSON parser.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(ICpcEncoderProjection))]
public sealed class CpcEncoderProjection : ICpcEncoderProjection
{
    private readonly ConcurrentDictionary<long, LinearWeights> _linearCache = new();
    private readonly ConcurrentDictionary<long, TcnWeights>    _tcnCache    = new();

    public float[] ProjectLatest(MLCpcEncoder encoder, float[][] sequence)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Length == 0)
            return new float[encoder.EmbeddingDim];

        return encoder.EncoderType switch
        {
            CpcEncoderType.Tcn    => TcnForwardLatest(encoder, sequence),
            CpcEncoderType.Linear => LinearForward(encoder, sequence[^1]),
            _ => throw new InvalidOperationException(
                $"Unknown CPC encoder type '{encoder.EncoderType}' on encoder {encoder.Id}."),
        };
    }

    public float[][] ProjectSequence(MLCpcEncoder encoder, float[][] sequence)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(sequence);

        return encoder.EncoderType switch
        {
            CpcEncoderType.Tcn    => TcnForwardAll(encoder, sequence),
            CpcEncoderType.Linear => LinearForwardAll(encoder, sequence),
            _ => throw new InvalidOperationException(
                $"Unknown CPC encoder type '{encoder.EncoderType}' on encoder {encoder.Id}."),
        };
    }

    // ── Linear branch (unchanged from pre-TCN behaviour) ─────────────────────

    private float[] LinearForward(MLCpcEncoder encoder, float[] x)
    {
        var (We, E, F) = LoadLinear(encoder);
        var z = new float[E];
        for (int r = 0; r < E; r++)
        {
            double sum = 0.0;
            int limit = Math.Min(F, x.Length);
            for (int c = 0; c < limit; c++)
                sum += We[r, c] * x[c];
            if (sum < 0.0) sum = 0.0;
            z[r] = (float)sum;
        }
        return z;
    }

    private float[][] LinearForwardAll(MLCpcEncoder encoder, float[][] sequence)
    {
        var result = new float[sequence.Length][];
        for (int i = 0; i < sequence.Length; i++)
        {
            var row = sequence[i] ?? throw new ArgumentException(
                $"Sequence row {i} is null.", nameof(sequence));
            result[i] = LinearForward(encoder, row);
        }
        return result;
    }

    private LinearWeights LoadLinear(MLCpcEncoder encoder)
    {
        if (encoder.Id > 0 && _linearCache.TryGetValue(encoder.Id, out var cached))
            return cached;

        if (encoder.EncoderBytes is null || encoder.EncoderBytes.Length == 0)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} has no serialised weights.");

        using var doc = JsonDocument.Parse(encoder.EncoderBytes);
        if (!doc.RootElement.TryGetProperty("We", out var weElement) || weElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} Linear payload is missing a 'We' array.");

        int E = encoder.EmbeddingDim;
        if (E <= 0)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} has non-positive EmbeddingDim={E}.");

        int length = weElement.GetArrayLength();
        if (length == 0 || length % E != 0)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} 'We' length {length} is not divisible by EmbeddingDim={E}.");

        int F = length / E;
        var We = new double[E, F];
        int idx = 0;
        foreach (var v in weElement.EnumerateArray())
        {
            int r = idx / F;
            int c = idx % F;
            We[r, c] = v.GetDouble();
            idx++;
        }

        var weights = new LinearWeights(We, E, F);
        if (encoder.Id > 0)
            _linearCache[encoder.Id] = weights;
        return weights;
    }

    // ── TCN branch ───────────────────────────────────────────────────────────

    private float[] TcnForwardLatest(MLCpcEncoder encoder, float[][] sequence)
    {
        var w = LoadTcn(encoder);
        var output = TcnForwardAllInternal(w, sequence);
        return output[^1];
    }

    private float[][] TcnForwardAll(MLCpcEncoder encoder, float[][] sequence)
    {
        var w = LoadTcn(encoder);
        return TcnForwardAllInternal(w, sequence);
    }

    /// <summary>
    /// Forward pass of the 2-layer dilated causal TCN + residual used at training time, matching
    /// <see cref="CpcTcnPretrainer.ForwardSequenceOutput"/> exactly so the per-trainer parity
    /// audits continue to hold at 1e-6.
    /// </summary>
    private static float[][] TcnForwardAllInternal(TcnWeights w, float[][] sequence)
    {
        int T = sequence.Length;
        int E = w.E, F = w.F, K = w.K;
        const int D1 = 1, D2 = 2;

        // Layer 1.
        var z1 = new double[T][];
        for (int t = 0; t < T; t++)
        {
            var a1 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * D1;
                if (srcT < 0) continue;
                var x = sequence[srcT];
                int limit = Math.Min(F, x.Length);
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < limit; c++)
                        s += w.W1[r, c, k] * x[c];
                    a1[r] += s;
                }
            }
            var z = new double[E];
            for (int r = 0; r < E; r++) z[r] = a1[r] > 0.0 ? a1[r] : 0.0;
            z1[t] = z;
        }

        // Layer 2 + residual.
        var output = new float[T][];
        for (int t = 0; t < T; t++)
        {
            var a2 = new double[E];
            for (int k = 0; k < K; k++)
            {
                int srcT = t - k * D2;
                if (srcT < 0) continue;
                var z1Row = z1[srcT];
                for (int r = 0; r < E; r++)
                {
                    double s = 0.0;
                    for (int c = 0; c < E; c++)
                        s += w.W2[r, c, k] * z1Row[c];
                    a2[r] += s;
                }
            }

            var x = sequence[t];
            int limitR = Math.Min(F, x.Length);
            var z = new float[E];
            for (int r = 0; r < E; r++)
            {
                double outer = a2[r] > 0.0 ? a2[r] : 0.0;
                double zr = 0.0;
                for (int c = 0; c < limitR; c++)
                    zr += w.Wr[r, c] * x[c];
                z[r] = (float)(outer + zr);
            }
            output[t] = z;
        }

        return output;
    }

    private TcnWeights LoadTcn(MLCpcEncoder encoder)
    {
        if (encoder.Id > 0 && _tcnCache.TryGetValue(encoder.Id, out var cached))
            return cached;

        if (encoder.EncoderBytes is null || encoder.EncoderBytes.Length == 0)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} has no serialised weights.");

        using var doc = JsonDocument.Parse(encoder.EncoderBytes);
        var root = doc.RootElement;

        int E = root.GetProperty("E").GetInt32();
        int F = root.GetProperty("F").GetInt32();
        int K = root.GetProperty("K").GetInt32();
        if (E != encoder.EmbeddingDim)
            throw new InvalidOperationException(
                $"MLCpcEncoder {encoder.Id} TCN payload E={E} does not match entity EmbeddingDim={encoder.EmbeddingDim}.");

        var W1 = Read3(root.GetProperty("W1"), E, F, K, "W1");
        var W2 = Read3(root.GetProperty("W2"), E, E, K, "W2");
        var Wr = Read2(root.GetProperty("Wr"), E, F, "Wr");

        var weights = new TcnWeights(W1, W2, Wr, E, F, K);
        if (encoder.Id > 0)
            _tcnCache[encoder.Id] = weights;
        return weights;

        static double[,,] Read3(JsonElement arr, int d0, int d1, int d2, string name)
        {
            if (arr.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"TCN payload '{name}' is not an array.");
            int expected = d0 * d1 * d2;
            if (arr.GetArrayLength() != expected)
                throw new InvalidOperationException(
                    $"TCN payload '{name}' has length {arr.GetArrayLength()}, expected {expected} ({d0}×{d1}×{d2}).");
            var result = new double[d0, d1, d2];
            int i = 0;
            foreach (var v in arr.EnumerateArray())
            {
                int p = i / (d1 * d2);
                int q = (i / d2) % d1;
                int r = i % d2;
                result[p, q, r] = v.GetDouble();
                i++;
            }
            return result;
        }

        static double[,] Read2(JsonElement arr, int d0, int d1, string name)
        {
            if (arr.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"TCN payload '{name}' is not an array.");
            int expected = d0 * d1;
            if (arr.GetArrayLength() != expected)
                throw new InvalidOperationException(
                    $"TCN payload '{name}' has length {arr.GetArrayLength()}, expected {expected} ({d0}×{d1}).");
            var result = new double[d0, d1];
            int i = 0;
            foreach (var v in arr.EnumerateArray())
            {
                result[i / d1, i % d1] = v.GetDouble();
                i++;
            }
            return result;
        }
    }

    private sealed record LinearWeights(double[,] We, int E, int F);
    private sealed record TcnWeights(double[,,] W1, double[,,] W2, double[,] Wr, int E, int F, int K);
}
