using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services.Inference;

/// <summary>
/// Serialises a trained <see cref="ModelSnapshot"/> to the ONNX exchange format so
/// inference can run through <c>Microsoft.ML.OnnxRuntime</c> instead of TorchSharp
/// or pure-C# matrix math. The runtime is ~5–10× faster on CPU for the model
/// families the engine ships (logistic, GBM, MLP, transformer variants).
///
/// <para>
/// This interface is a scaffold — a production implementation would build an ONNX
/// graph from the snapshot's architecture and weight tensors. Concrete scope:
/// </para>
///
/// <list type="bullet">
///   <item><see cref="LearnerArchitecture.BaggedLogistic"/>: MatMul + Sigmoid with
///         bagged weights concatenated as a tensor; easiest to export.</item>
///   <item><see cref="LearnerArchitecture.Gbm"/>: sequence of tree-ensemble ops via the
///         <c>ai.onnx.ml.TreeEnsembleClassifier</c> operator set.</item>
///   <item><see cref="LearnerArchitecture.TemporalConvNet"/>,
///         <see cref="LearnerArchitecture.FtTransformer"/>,
///         <see cref="LearnerArchitecture.TabNet"/>: TorchSharp's <c>torch.onnx.export</c>
///         can produce ONNX graphs directly from the module.</item>
///   <item><see cref="LearnerArchitecture.Rocket"/>,
///         <see cref="LearnerArchitecture.Elm"/>: export as a linear layer; kernels
///         are deterministic and can be embedded as constants.</item>
/// </list>
///
/// <para>
/// Activation path once an implementation exists:
/// </para>
/// <list type="number">
///   <item>In <c>MLTrainingWorker</c> after a model passes the 9 quality gates,
///         call <see cref="ExportToBytesAsync"/> and persist the ONNX bytes to
///         a new <c>MLModel.OnnxBytes</c> column (migration required).</item>
///   <item>Add an <c>OnnxInferenceEngine</c> that implements
///         <c>IModelInferenceEngine</c> by constructing an
///         <c>InferenceSession</c> from <c>OnnxBytes</c>.</item>
///   <item>Register it ahead of the existing engines so <c>CanHandle</c> picks
///         ONNX first when bytes are available, with transparent fallback to
///         the legacy engine when they aren't.</item>
/// </list>
/// </summary>
public interface IOnnxModelExporter
{
    /// <summary>
    /// Returns <c>true</c> when the exporter knows how to translate the given
    /// snapshot's architecture to ONNX. Future architectures can be added without
    /// disrupting existing ones.
    /// </summary>
    bool CanExport(ModelSnapshot snapshot);

    /// <summary>
    /// Serialises the snapshot to an ONNX-format byte buffer suitable for persisting
    /// alongside the existing <see cref="ModelSnapshot"/> JSON. Throws
    /// <see cref="NotSupportedException"/> when <see cref="CanExport"/> returns false.
    /// </summary>
    Task<byte[]> ExportToBytesAsync(ModelSnapshot snapshot, CancellationToken ct);
}
