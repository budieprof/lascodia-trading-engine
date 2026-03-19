using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Trains a Variational Autoencoder (VAE) encoder on unlabelled candle feature vectors
/// for a symbol/timeframe pair (Rec #36). The encoder compresses features to a latent
/// mean vector that is appended to the downstream ML classifier input.
/// </summary>
public interface IVaePretrainer
{
    /// <summary>
    /// Trains the VAE encoder and returns the fitted entity for persistence.
    /// </summary>
    Task<MLVaeEncoder> TrainAsync(
        string symbol,
        LascodiaTradingEngine.Domain.Enums.Timeframe timeframe,
        IReadOnlyList<float[]> featureVectors,
        int latentDim,
        CancellationToken cancellationToken);
}
