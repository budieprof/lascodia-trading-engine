using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// <see cref="IOnnxModelExporter"/> for bagged-logistic ensembles.
///
/// <para>
/// Ensemble shape: K bagged logistic regressors, each a weight vector of length F + scalar
/// bias, producing <c>sigmoid(w·x + b)</c>. The ensemble probability is the arithmetic mean
/// across learners. ONNX graph structure:
/// </para>
///
/// <list type="number">
///   <item>Input <c>x</c>: float[1, F]</item>
///   <item><c>W</c>: initializer float[F, K] (columns are learner weight vectors)</item>
///   <item><c>B</c>: initializer float[K] (learner biases)</item>
///   <item><c>logits</c> = MatMul(x, W) + B → float[1, K]</item>
///   <item><c>probs</c> = Sigmoid(logits) → float[1, K]</item>
///   <item><c>rawProb</c> = ReduceMean(probs, axis=1) → float[1, 1]</item>
/// </list>
///
/// <para>
/// Platt scaling is intentionally <b>not</b> embedded in the graph so that
/// <c>MLSignalScorer</c>'s existing C# Platt step continues to apply uniformly
/// across legacy and ONNX paths. This keeps calibration drift monitoring unchanged.
/// </para>
///
/// <para>
/// <b>Status:</b> dependency-gapped. The <c>Microsoft.ML.OnnxRuntime</c> package only
/// supplies the inference runtime — it does not build graphs. Emitting valid ONNX bytes
/// requires <c>Google.Protobuf</c> plus onnx.proto-generated <c>ModelProto</c> types
/// (e.g. via the <c>Onnx</c> NuGet package) to programmatically construct the graph.
/// </para>
///
/// <para>
/// <see cref="ExportToBytesAsync"/> currently throws <see cref="NotSupportedException"/>.
/// The <c>MLTrainingWorker</c> export hook wraps the call in a try/catch so absence of the
/// runtime dependency is a transparent no-op: no <c>OnnxBytes</c> get persisted, and
/// <c>MLSignalScorer</c> transparently falls back to the legacy ensemble inference path.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class BaggedLogisticOnnxExporter : IOnnxModelExporter
{
    /// <inheritdoc />
    public bool CanExport(ModelSnapshot snapshot)
    {
        if (snapshot.Weights is not { Length: > 0 }) return false;
        if (snapshot.Biases is not { Length: > 0 })  return false;
        if (snapshot.Weights.Length != snapshot.Biases.Length) return false;

        var type = snapshot.Type;
        return string.Equals(type, "ENSEMBLE",        StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "BAGGED_LOGISTIC", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "LOGISTIC",        StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<byte[]> ExportToBytesAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        throw new NotSupportedException(
            "ONNX graph construction requires Google.Protobuf + onnx.proto generated types. " +
            "Add the Onnx NuGet package, then replace this body with a ModelProto build that " +
            "emits MatMul(x, W) + B → Sigmoid → ReduceMean. See XML docs for the target graph " +
            "shape. Until then MLSignalScorer transparently falls back to the legacy engine.");
    }
}
