using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// ONNX Runtime inference engine for GPU-accelerated or optimised CPU model scoring.
/// Wraps trained models exported to ONNX format and provides a standardised inference
/// API that <c>MLSignalScorer</c> can use as an alternative to the pure-C# inference path.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Model loading:</b> ONNX models are loaded from byte arrays (stored in
///         <c>ModelSnapshot.OnnxModelBytes</c>) and cached by model ID.</item>
///   <item><b>Execution providers:</b> uses CUDA EP when available, falls back to CPU EP.
///         Detected automatically at startup.</item>
///   <item><b>Thread safety:</b> each <see cref="InferenceSession"/> is thread-safe for
///         concurrent inference. Sessions are pooled per model ID.</item>
///   <item><b>Latency:</b> ONNX Runtime inference is typically 2-10x faster than pure-C#
///         for models with >100 parameters due to vectorised BLAS operations.</item>
/// </list>
/// </remarks>
public interface IOnnxInferenceEngine
{
    /// <summary>
    /// Runs inference on a pre-loaded ONNX model and returns the output probabilities.
    /// </summary>
    /// <param name="modelId">The MLModel.Id used to look up the cached session.</param>
    /// <param name="features">Input feature vector (length must match model's input shape).</param>
    /// <returns>
    /// (buyProbability, magnitude) tuple. Returns (0.5, 0) if the model is not loaded.
    /// </returns>
    (double BuyProbability, double Magnitude) Infer(long modelId, float[] features);

    /// <summary>
    /// Loads an ONNX model from raw bytes and caches it for future inference calls.
    /// </summary>
    bool LoadModel(long modelId, byte[] onnxBytes, bool preferGpu = true);

    /// <summary>Unloads a cached model, freeing GPU/CPU memory.</summary>
    void UnloadModel(long modelId);

    /// <summary>Returns whether GPU (CUDA) execution provider is available.</summary>
    bool IsGpuAvailable { get; }

    /// <summary>Returns inference latency statistics for a model.</summary>
    OnnxModelStats? GetStats(long modelId);
}

/// <summary>Inference latency statistics for a cached ONNX model.</summary>
public sealed record OnnxModelStats(
    long   ModelId,
    string ExecutionProvider,
    int    InferenceCount,
    double AvgLatencyMs,
    double P95LatencyMs,
    double P99LatencyMs);

public sealed class OnnxInferenceEngine : IOnnxInferenceEngine, IDisposable
{
    private readonly ConcurrentDictionary<long, CachedModel> _models = new();
    private readonly ILogger<OnnxInferenceEngine> _logger;
    private readonly bool _gpuAvailable;

    public OnnxInferenceEngine(ILogger<OnnxInferenceEngine> logger)
    {
        _logger = logger;
        _gpuAvailable = DetectGpuAvailability();
        _logger.LogInformation("OnnxInferenceEngine: GPU={Gpu}, available providers=[{Providers}]",
            _gpuAvailable, string.Join(", ", GetAvailableProviders()));
    }

    public bool IsGpuAvailable => _gpuAvailable;

    public bool LoadModel(long modelId, byte[] onnxBytes, bool preferGpu = true)
    {
        try
        {
            // Dispose existing session if reloading
            if (_models.TryRemove(modelId, out var old))
                old.Dispose();

            var options = new SessionOptions();
            string ep;

            if (preferGpu && _gpuAvailable)
            {
                options.AppendExecutionProvider_CUDA();
                ep = "CUDA";
            }
            else
            {
                options.AppendExecutionProvider_CPU();
                ep = "CPU";
            }

            // Enable graph optimizations for faster inference
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            var session = new InferenceSession(onnxBytes, options);

            var model = new CachedModel
            {
                ModelId           = modelId,
                Session           = session,
                ExecutionProvider = ep,
                LoadedAt          = DateTime.UtcNow,
                InputName         = session.InputMetadata.Keys.FirstOrDefault() ?? "input",
                OutputNames       = session.OutputMetadata.Keys.ToArray(),
            };

            _models[modelId] = model;

            _logger.LogInformation(
                "OnnxInferenceEngine: loaded model {Id} ({Size:F1}KB, EP={EP}, inputs=[{In}], outputs=[{Out}])",
                modelId, onnxBytes.Length / 1024.0, ep,
                string.Join(",", session.InputMetadata.Keys),
                string.Join(",", session.OutputMetadata.Keys));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnnxInferenceEngine: failed to load model {Id}", modelId);
            return false;
        }
    }

    public (double BuyProbability, double Magnitude) Infer(long modelId, float[] features)
    {
        if (!_models.TryGetValue(modelId, out var model))
            return (0.5, 0.0);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Create input tensor [1, F]
            var tensor = new DenseTensor<float>(features, [1, features.Length]);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(model.InputName, tensor)
            };

            // Run inference
            using var results = model.Session.Run(inputs);

            double buyProb   = 0.5;
            double magnitude = 0.0;

            // Parse outputs — model may have 1 output (probability) or 2 (probability + magnitude)
            var outputList = results.ToList();
            if (outputList.Count >= 1)
            {
                var probTensor = outputList[0].AsTensor<float>();
                if (probTensor.Length == 1)
                {
                    // Single output: sigmoid probability of Buy
                    buyProb = probTensor[0];
                }
                else if (probTensor.Length >= 2)
                {
                    // Two-class softmax: [P(Sell), P(Buy)]
                    float pSell = probTensor[0];
                    float pBuy  = probTensor[1];
                    buyProb = pBuy / (pSell + pBuy + 1e-8f);
                }
            }

            if (outputList.Count >= 2)
            {
                var magTensor = outputList[1].AsTensor<float>();
                magnitude = magTensor.Length > 0 ? magTensor[0] : 0.0;
            }

            sw.Stop();
            model.RecordLatency(sw.Elapsed.TotalMilliseconds);

            return (Math.Clamp(buyProb, 0.0, 1.0), Math.Abs(magnitude));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "OnnxInferenceEngine: inference failed for model {Id}", modelId);
            return (0.5, 0.0);
        }
    }

    public void UnloadModel(long modelId)
    {
        if (_models.TryRemove(modelId, out var model))
        {
            model.Dispose();
            _logger.LogInformation("OnnxInferenceEngine: unloaded model {Id}", modelId);
        }
    }

    public OnnxModelStats? GetStats(long modelId)
    {
        if (!_models.TryGetValue(modelId, out var model))
            return null;

        return new OnnxModelStats(
            modelId,
            model.ExecutionProvider,
            model.InferenceCount,
            model.AvgLatencyMs,
            model.P95LatencyMs,
            model.P99LatencyMs);
    }

    public void Dispose()
    {
        foreach (var kvp in _models)
            kvp.Value.Dispose();
        _models.Clear();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool DetectGpuAvailability()
    {
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            return providers.Contains("CUDAExecutionProvider");
        }
        catch
        {
            return false;
        }
    }

    private static string[] GetAvailableProviders()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders();
        }
        catch
        {
            return ["CPUExecutionProvider"];
        }
    }

    // ── Cached model state ──────────────────────────────────────────────────

    private sealed class CachedModel : IDisposable
    {
        public long              ModelId           { get; init; }
        public InferenceSession  Session           { get; init; } = null!;
        public string            ExecutionProvider { get; init; } = "CPU";
        public DateTime          LoadedAt          { get; init; }
        public string            InputName         { get; init; } = "input";
        public string[]          OutputNames       { get; init; } = [];

        private readonly List<double> _latencies = [];
        private readonly object _lock = new();

        public int InferenceCount
        {
            get { lock (_lock) return _latencies.Count; }
        }

        public double AvgLatencyMs
        {
            get { lock (_lock) return _latencies.Count > 0 ? _latencies.Average() : 0; }
        }

        public double P95LatencyMs => GetPercentile(0.95);
        public double P99LatencyMs => GetPercentile(0.99);

        public void RecordLatency(double ms)
        {
            lock (_lock)
            {
                _latencies.Add(ms);
                if (_latencies.Count > 1000)
                    _latencies.RemoveAt(0);
            }
        }

        private double GetPercentile(double p)
        {
            lock (_lock)
            {
                if (_latencies.Count == 0) return 0;
                var sorted = _latencies.ToList();
                sorted.Sort();
                int idx = (int)(sorted.Count * p);
                return sorted[Math.Min(idx, sorted.Count - 1)];
            }
        }

        public void Dispose() => Session?.Dispose();
    }
}

/// <summary>
/// Helper for building ONNX model graphs from trained weight matrices.
/// Used by trainers to export their models to ONNX format for GPU-accelerated inference.
/// </summary>
/// <remarks>
/// Full graph construction requires the <c>Google.Protobuf</c> and <c>Onnx</c> NuGet packages
/// to build the ONNX protobuf programmatically. This placeholder provides the API contract.
/// </remarks>
public static class OnnxModelBuilder
{
    /// <summary>
    /// Creates an ONNX model byte array for a simple logistic ensemble.
    /// Placeholder — returns empty until Onnx protobuf NuGet is added.
    /// </summary>
    public static byte[] BuildEnsembleOnnx(double[][] weights, double[] biases, int featureCount)
    {
        // TODO: Add Google.Protobuf + Onnx NuGet, then:
        //   1. Create ModelProto with OpsetImport version 13+
        //   2. Add input: float[1, featureCount]
        //   3. For each learner: MatMul(input, weights[k]) + bias[k] → sigmoid
        //   4. Average across learners → output[1, 2]
        //   5. Serialize to bytes via ModelProto.ToByteArray()
        return [];
    }
}
