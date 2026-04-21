namespace LascodiaTradingEngine.Domain.Enums;

/// <summary>
/// Encoder architecture a persisted <see cref="Entities.MLCpcEncoder"/> row was trained with.
/// The value discriminates the <c>EncoderBytes</c> payload shape — different architectures
/// persist different weight layouts and are fed through different forward-pass math at
/// projection time, so the runtime must know which implementation to use without guessing.
/// </summary>
public enum CpcEncoderType
{
    /// <summary>
    /// Single-step linear encoder: <c>z = ReLU(W_e · x)</c>. Lightweight, fastest to train,
    /// and parity-trivial to audit. Weights payload: <c>{ "We": [E*F], "Wp": [[E*E]] }</c>.
    /// </summary>
    Linear = 0,

    /// <summary>
    /// Two-layer dilated causal Temporal Convolutional Network (TCN) with a residual projection:
    /// <c>z[t] = ReLU(conv2(ReLU(conv1(x))))[t] + residual(x[t])</c>. Captures a short window of
    /// past context (receptive field ≈ 7 steps at dilations 1 and 2 with kernel 3). Weights payload:
    /// <c>{ "Type": "tcn", "E", "F", "K", "W1", "W2", "Wr", "Wp" }</c>.
    /// </summary>
    Tcn = 1,
}
