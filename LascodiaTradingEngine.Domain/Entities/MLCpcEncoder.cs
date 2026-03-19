using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores a Contrastive Predictive Coding (CPC) encoder trained on unlabelled candle
/// sequences for a symbol/timeframe (Rec #49). CPC learns temporal context representations
/// by predicting future embeddings from context embeddings, producing transferable features
/// without requiring labelled data.
/// </summary>
public class MLCpcEncoder : Entity<long>
{
    public string    Symbol          { get; set; } = string.Empty;
    public Timeframe Timeframe       { get; set; } = Timeframe.H1;
    /// <summary>Dimensionality of the learned context embedding.</summary>
    public int       EmbeddingDim    { get; set; }
    /// <summary>Number of future steps predicted during CPC pre-training.</summary>
    public int       PredictionSteps { get; set; }
    /// <summary>Contrastive loss (InfoNCE) achieved on the validation set (lower is better).</summary>
    public double    InfoNceLoss     { get; set; }
    /// <summary>Number of candle sequences used for pre-training.</summary>
    public int       TrainingSamples { get; set; }
    /// <summary>Serialised encoder weights as UTF-8 JSON bytes.</summary>
    public byte[]?   EncoderBytes    { get; set; }
    public DateTime  TrainedAt       { get; set; } = DateTime.UtcNow;
    public bool      IsActive        { get; set; }
    public bool      IsDeleted       { get; set; }
}
