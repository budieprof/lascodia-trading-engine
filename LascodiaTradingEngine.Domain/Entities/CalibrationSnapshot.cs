using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Aggregated rejection distribution across the signal pipeline for a calibration
/// period (a calendar month by default). One row per
/// <c>(PeriodStart, Stage, Reason)</c> tuple written by
/// <c>CalibrationSnapshotWorker</c> so operators can watch gate hit rates shift
/// over time and calibrate thresholds against real traffic rather than guesses.
/// </summary>
/// <remarks>
/// <para>
/// The table is deliberately denormalised: <see cref="Stage"/> and
/// <see cref="Reason"/> mirror the <c>SignalRejectionAudit</c> columns so
/// quarterly reviews do not need a join. <see cref="PeriodStart"/> is the first
/// moment of the period (midnight UTC on the 1st of the month for monthly
/// snapshots); <see cref="PeriodEnd"/> is exclusive so adjacent snapshots do not
/// double-count boundary rows.
/// </para>
/// <para>
/// Snapshots are append-only and immutable: a re-run of the worker for a period
/// already snapshotted should be a no-op (the worker guards against duplicates
/// via the <c>(PeriodStart, PeriodGranularity)</c> + <c>(Stage, Reason)</c>
/// unique index). No soft-delete.
/// </para>
/// </remarks>
public class CalibrationSnapshot : Entity<long>
{
    /// <summary>Inclusive start of the aggregation window (UTC).</summary>
    public DateTime PeriodStart       { get; set; }

    /// <summary>Exclusive end of the aggregation window (UTC).</summary>
    public DateTime PeriodEnd         { get; set; }

    /// <summary>
    /// Short code for the period granularity: <c>Monthly</c>, <c>Weekly</c>,
    /// <c>Daily</c>. Only monthly is written by the default worker cadence;
    /// finer granularities exist so operators can back-fill manually if needed.
    /// </summary>
    public string   PeriodGranularity { get; set; } = "Monthly";

    /// <summary>Mirror of <c>SignalRejectionAudit.Stage</c>. Max 32 chars.</summary>
    public string   Stage             { get; set; } = string.Empty;

    /// <summary>Mirror of <c>SignalRejectionAudit.Reason</c>. Max 64 chars.</summary>
    public string   Reason            { get; set; } = string.Empty;

    /// <summary>Total rejection count for this stage × reason in the period.</summary>
    public long     RejectionCount    { get; set; }

    /// <summary>Number of distinct symbols that hit this stage × reason at least once.</summary>
    public int      DistinctSymbols   { get; set; }

    /// <summary>Number of distinct strategies that hit this stage × reason at least once.</summary>
    public int      DistinctStrategies { get; set; }

    /// <summary>UTC timestamp when the snapshot was computed.</summary>
    public DateTime ComputedAt        { get; set; } = DateTime.UtcNow;
}
