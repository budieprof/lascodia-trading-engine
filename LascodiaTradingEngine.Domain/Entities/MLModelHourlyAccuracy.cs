using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores per-UTC-hour direction accuracy for an <see cref="MLModel"/>, providing
/// finer-grained intraday accuracy data than the four-bucket
/// <see cref="MLModelSessionAccuracy"/> (Asian / London / LondonNYOverlap / NewYork).
///
/// One row per (model, hour 0–23) is maintained via an upsert pattern.
/// Computed by <c>MLTimeOfDayAccuracyWorker</c> over a rolling look-back window.
/// </summary>
public class MLModelHourlyAccuracy : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this row belongs to.</summary>
    public long      MLModelId          { get; set; }

    /// <summary>The currency pair this accuracy record covers (e.g. "EURUSD").</summary>
    public string    Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe this record covers.</summary>
    public Timeframe Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>UTC hour (0–23) this row represents.</summary>
    public int       HourUtc            { get; set; }

    /// <summary>Number of resolved predictions in this hour bucket during the look-back window.</summary>
    public int       TotalPredictions   { get; set; }

    /// <summary>Number of those predictions where <c>DirectionCorrect == true</c>.</summary>
    public int       CorrectPredictions { get; set; }

    /// <summary>Direction accuracy for this hour: <c>CorrectPredictions / TotalPredictions</c>.</summary>
    public double    Accuracy           { get; set; }

    /// <summary>Start of the look-back window used to compute these statistics.</summary>
    public DateTime  WindowStart        { get; set; }

    /// <summary>UTC timestamp when this row was last computed.</summary>
    public DateTime  ComputedAt         { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted          { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>The model this accuracy row belongs to.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
