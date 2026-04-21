using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Pure projection of raw OHLCV rows through a trained CPC encoder's linear+ReLU head,
/// producing an E-dim context embedding. Must reproduce <c>CpcPretrainer.Encode</c> exactly
/// so the embedding at training time and at inference time are bit-identical for identical
/// weights and inputs — which is what the per-trainer parity audits require.
/// </summary>
public interface ICpcEncoderProjection
{
    /// <summary>
    /// Projects the latest row of the sequence through the encoder and returns the E-dim
    /// embedding. Used by the inference path where only the current "context" embedding
    /// is needed.
    /// </summary>
    /// <param name="encoder">Active encoder with deserialisable <see cref="MLCpcEncoder.EncoderBytes"/>.</param>
    /// <param name="sequence">
    /// Per-step raw feature vectors (the last row is encoded as the context).
    /// </param>
    float[] ProjectLatest(MLCpcEncoder encoder, float[][] sequence);

    /// <summary>
    /// Projects each row of the sequence through the encoder. Useful when training-sample
    /// construction needs per-step embeddings (not currently required for V7 but provided
    /// for completeness and future features).
    /// </summary>
    float[][] ProjectSequence(MLCpcEncoder encoder, float[][] sequence);
}
