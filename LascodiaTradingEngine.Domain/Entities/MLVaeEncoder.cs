using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a trained Variational Autoencoder (VAE) encoder for a symbol/timeframe,
/// used to compress raw feature vectors into a lower-dimensional latent representation
/// (Rec #36). The latent mean vector is appended to the feature set fed to the
/// downstream classifier, capturing non-linear structure not visible to the linear model.
/// </summary>
public class MLVaeEncoder : Entity<long>
{
    public string    Symbol          { get; set; } = string.Empty;
    public Timeframe Timeframe       { get; set; } = Timeframe.H1;
    /// <summary>Dimensionality of the latent space (number of μ outputs).</summary>
    public int       LatentDim       { get; set; }
    /// <summary>Number of input features the encoder was trained on.</summary>
    public int       InputDim        { get; set; }
    /// <summary>Number of training candles used to fit the VAE.</summary>
    public int       TrainingSamples { get; set; }
    /// <summary>Final reconstruction loss (ELBO) on the validation set.</summary>
    public double    ReconstructionLoss { get; set; }
    /// <summary>Serialised encoder weights (JSON UTF-8): EncoderWeightsJson containing W1, b1, W_mu, b_mu, W_logvar, b_logvar.</summary>
    public byte[]?   EncoderBytes    { get; set; }
    public DateTime  TrainedAt       { get; set; } = DateTime.UtcNow;
    public bool      IsActive        { get; set; }
    public bool      IsDeleted       { get; set; }
}
