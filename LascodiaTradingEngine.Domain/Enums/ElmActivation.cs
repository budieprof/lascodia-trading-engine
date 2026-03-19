namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Hidden-layer activation function for the Extreme Learning Machine (ELM) trainer.
/// Different activations suit different data characteristics:
/// <list type="bullet">
///   <item><see cref="Sigmoid"/> — classic ELM default; bounded [0,1], compatible with the analytical ridge solve.</item>
///   <item><see cref="Tanh"/> — zero-centered outputs [−1,1]; can improve convergence when features are standardised.</item>
///   <item><see cref="Relu"/> — unbounded positive; avoids vanishing gradients but can produce sparse activations.</item>
/// </list>
/// </summary>
public enum ElmActivation
{
    /// <summary>Logistic sigmoid σ(z) = 1 / (1 + e^(−z)). Default ELM activation.</summary>
    Sigmoid = 0,

    /// <summary>Hyperbolic tangent tanh(z) = (e^z − e^(−z)) / (e^z + e^(−z)). Zero-centered.</summary>
    Tanh = 1,

    /// <summary>Rectified Linear Unit max(0, z). Sparse, unbounded positive activations.</summary>
    Relu = 2,
}
