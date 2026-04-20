using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the latest lag-1 autocorrelation staleness result for one input feature
/// of a specific ML model.
/// </summary>
/// <remarks>
/// <para>
/// Feature staleness detection checks whether a feature is effectively repeating its
/// previous value from candle to candle. A feature whose lag-1 autocorrelation is very
/// close to <c>+1</c> or <c>-1</c> may be carrying little fresh information in the
/// current market regime and can be a candidate for suppression during retraining.
/// </para>
/// <para>
/// <c>MLFeatureStalenessWorker</c> computes this value from recent candle-derived
/// feature vectors on a weekly cadence and upserts one active row per
/// <c>(MLModelId, FeatureName)</c>. The row therefore represents the current feature
/// freshness state, not an append-only history entry.
/// </para>
/// </remarks>
public class MLFeatureStalenessLog : Entity<long>
{
    /// <summary>Foreign key to the ML model whose feature vector was analysed.</summary>
    public long      MLModelId    { get; set; }

    /// <summary>The traded instrument this feature staleness result applies to (for example, "EURUSD").</summary>
    public string    Symbol       { get; set; } = string.Empty;

    /// <summary>The candle timeframe used to build the feature series.</summary>
    public Timeframe Timeframe    { get; set; } = Timeframe.H1;

    /// <summary>
    /// Human-readable feature name from the model feature vector. Together with
    /// <see cref="MLModelId"/>, this identifies the active staleness row.
    /// </summary>
    public string    FeatureName  { get; set; } = string.Empty;

    /// <summary>
    /// Lag-1 Pearson autocorrelation of the feature values, where values near
    /// <c>+1.0</c> or <c>-1.0</c> indicate that consecutive observations are highly
    /// predictable from the previous candle.
    /// </summary>
    public double    Lag1Autocorr { get; set; }

    /// <summary>
    /// <c>true</c> when <see cref="Lag1Autocorr"/> exceeds the worker's absolute-value
    /// staleness threshold after multiple-feature correction.
    /// </summary>
    public bool      IsStale      { get; set; }

    /// <summary>UTC timestamp when the staleness score was last computed for this feature.</summary>
    public DateTime  ComputedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted    { get; set; }

    // Navigation properties

    /// <summary>The ML model whose feature produced this staleness record.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
