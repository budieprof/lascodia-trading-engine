using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records lag-1 autocorrelation staleness per feature for a model (Rec #180).
/// A high autocorrelation indicates the feature is not updating meaningfully.
/// </summary>
public class MLFeatureStalenessLog : Entity<long>
{
    public long      MLModelId    { get; set; }
    public string    Symbol       { get; set; } = string.Empty;
    public Timeframe Timeframe    { get; set; } = Timeframe.H1;
    public string    FeatureName  { get; set; } = string.Empty;
    /// <summary>Lag-1 autocorrelation of the feature values; near 1.0 indicates staleness.</summary>
    public double    Lag1Autocorr { get; set; }
    public bool      IsStale      { get; set; }
    public DateTime  ComputedAt   { get; set; } = DateTime.UtcNow;
    public bool      IsDeleted    { get; set; }

    public virtual MLModel MLModel { get; set; } = null!;
}
