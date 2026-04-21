using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the Exponentially-Weighted Moving Average (EWMA) accuracy for an
/// <see cref="MLModel"/>, providing a faster-responding performance signal than
/// the equal-weighted rolling accuracy used by <c>MLRollingAccuracyWorker</c>.
///
/// <b>Rationale:</b> Rolling accuracy weights every prediction in the window equally,
/// meaning a performance inflection can take many observations to become visible.
/// EWMA heavily weights recent predictions, detecting degradation or improvement
/// significantly earlier — often 3–5× faster depending on the smoothing factor α.
///
/// One row per active model is maintained via an upsert pattern.
/// Computed by <c>MLEwmaAccuracyWorker</c> every poll cycle.
/// </summary>
public class MLModelEwmaAccuracy : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> this row belongs to.</summary>
    public long      MLModelId          { get; set; }

    /// <summary>The currency pair this accuracy record covers (e.g. "EURUSD").</summary>
    public string    Symbol             { get; set; } = string.Empty;

    /// <summary>The chart timeframe this record covers.</summary>
    public Timeframe Timeframe          { get; set; } = Timeframe.H1;

    /// <summary>
    /// Current EWMA accuracy value in range [0, 1].
    /// Updated incrementally: <c>ewma = α × latest + (1−α) × previous</c>.
    /// </summary>
    public double    EwmaAccuracy       { get; set; }

    /// <summary>Smoothing factor α used to compute this value (default 0.05).</summary>
    public double    Alpha              { get; set; }

    /// <summary>Total number of resolved predictions incorporated into the EWMA so far.</summary>
    public int       TotalPredictions   { get; set; }

    /// <summary>UTC prediction timestamp of the most recent resolved prediction used in this computation.</summary>
    public DateTime  LastPredictionAt   { get; set; }

    /// <summary>
    /// UTC outcome-resolution timestamp of the most recent prediction used in this computation.
    /// This is the primary incremental watermark because prediction outcomes are back-filled
    /// after the original prediction time.
    /// </summary>
    public DateTime? LastOutcomeRecordedAt { get; set; }

    /// <summary>
    /// Prediction log id paired with <see cref="LastOutcomeRecordedAt"/> to make the
    /// incremental watermark stable when multiple outcomes share the same timestamp.
    /// </summary>
    public long      LastPredictionLogId { get; set; }

    /// <summary>UTC timestamp when this row was last updated.</summary>
    public DateTime  ComputedAt         { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted          { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>The model this EWMA accuracy row belongs to.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
