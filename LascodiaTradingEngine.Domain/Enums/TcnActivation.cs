namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Activation function for Temporal Convolutional Network (TCN) blocks.
/// <list type="bullet">
///   <item><see cref="Relu"/> — standard ReLU max(0, z). Default, well-suited for most temporal patterns.</item>
///   <item><see cref="Gelu"/> — Gaussian Error Linear Unit z·Φ(z). Smoother gradients, better for deep networks.</item>
///   <item><see cref="Swish"/> — Swish/SiLU z·σ(z). Self-gated, smooth, non-monotonic — marginal gains in some TCN benchmarks.</item>
/// </list>
/// </summary>
public enum TcnActivation
{
    /// <summary>Rectified Linear Unit max(0, z). Default TCN activation.</summary>
    Relu = 0,

    /// <summary>Gaussian Error Linear Unit z × Φ(z). Smoother gradients near zero.</summary>
    Gelu = 1,

    /// <summary>Swish/SiLU z × σ(z). Self-gated activation with smooth gradients and non-monotonic shape.</summary>
    Swish = 2,
}
