using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Trains a Contrastive Predictive Coding (CPC) encoder on candle sequences using
/// a noise-contrastive estimation loss (InfoNCE) (Rec #49). Implementations differ in
/// encoder architecture — pick one via <see cref="Kind"/> at DI resolution time.
/// </summary>
public interface ICpcPretrainer
{
    /// <summary>
    /// Encoder architecture this implementation produces. The
    /// <c>CpcPretrainerWorker</c> resolves <see cref="IEnumerable{ICpcPretrainer}"/> and
    /// selects by <c>Kind == MLCpcOptions.EncoderType</c>.
    /// </summary>
    CpcEncoderType Kind { get; }

    /// <summary>
    /// Trains the CPC encoder and returns the fitted entity for persistence. The
    /// implementation is expected to set <see cref="MLCpcEncoder.EncoderType"/> to match
    /// <see cref="Kind"/>, <see cref="MLCpcEncoder.IsActive"/> to <c>true</c>, and serialise
    /// weights into <see cref="MLCpcEncoder.EncoderBytes"/> in its own architecture-specific
    /// payload shape (see <see cref="CpcEncoderType"/>).
    /// </summary>
    Task<MLCpcEncoder> TrainAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<float[][]> sequences,
        int embeddingDim,
        int predictionSteps,
        CancellationToken cancellationToken);
}
