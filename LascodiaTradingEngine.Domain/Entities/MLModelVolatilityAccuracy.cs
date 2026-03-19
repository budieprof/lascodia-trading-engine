using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores per-ATR-quantile direction accuracy for an <see cref="MLModel"/>, enabling
/// <c>MLSignalScorer</c> and the strategy risk layer to suppress or down-weight signals
/// in volatility regimes where the model historically underperforms.
/// </summary>
/// <remarks>
/// Computed by <c>MLVolatilityAccuracyWorker</c> over a rolling look-back window.
/// One row per (model, volatility bucket) is maintained via an upsert pattern.
/// The three buckets — Low, Medium, High — are derived from tertile ATR quantiles
/// computed across the prediction window, so bucket thresholds adapt to the
/// prevailing volatility environment rather than being fixed in advance.
/// </remarks>
public class MLModelVolatilityAccuracy : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this row belongs to.</summary>
    public long             MLModelId          { get; set; }

    /// <summary>The currency pair this accuracy record covers (e.g. "EURUSD").</summary>
    public string           Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe this record covers.</summary>
    public Timeframe        Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// Volatility bucket: "Low" (bottom tertile ATR), "Medium" (middle tertile),
    /// or "High" (top tertile) relative to the rolling window.
    /// </summary>
    public string           VolatilityBucket   { get; set; } = string.Empty;

    /// <summary>Number of resolved prediction logs in this bucket during the look-back window.</summary>
    public int              TotalPredictions   { get; set; }

    /// <summary>Number of those predictions where <c>DirectionCorrect == true</c>.</summary>
    public int              CorrectPredictions { get; set; }

    /// <summary>Direction accuracy for this bucket: <c>CorrectPredictions / TotalPredictions</c>.</summary>
    public double           Accuracy           { get; set; }

    /// <summary>ATR threshold separating this bucket from the one below it.</summary>
    public decimal          AtrThresholdLow    { get; set; }

    /// <summary>ATR threshold separating this bucket from the one above it.</summary>
    public decimal          AtrThresholdHigh   { get; set; }

    /// <summary>Start of the look-back window used to compute these statistics.</summary>
    public DateTime         WindowStart        { get; set; }

    /// <summary>UTC timestamp when this row was last computed.</summary>
    public DateTime         ComputedAt         { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool             IsDeleted          { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>The model this accuracy row belongs to.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
