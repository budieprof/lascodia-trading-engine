using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Trains a Contrastive Predictive Coding (CPC) encoder on candle sequences using
/// a noise-contrastive estimation loss (InfoNCE) (Rec #49).
/// </summary>
public interface ICpcPretrainer
{
    /// <summary>
    /// Trains the CPC encoder and returns the fitted entity for persistence.
    /// </summary>
    Task<MLCpcEncoder> TrainAsync(
        string symbol,
        LascodiaTradingEngine.Domain.Enums.Timeframe timeframe,
        IReadOnlyList<float[][]> sequences,
        int embeddingDim,
        int predictionSteps,
        CancellationToken cancellationToken);
}
