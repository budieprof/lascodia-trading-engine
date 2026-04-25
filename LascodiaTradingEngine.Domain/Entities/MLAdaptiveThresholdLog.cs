using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Audit trail for <c>MLAdaptiveThresholdWorker</c> decisions. One row per evaluated
/// (model, scope) pair per cycle, regardless of whether the threshold actually changed.
/// Lets operators answer "why did model X's threshold move on Tuesday?" without having
/// to grep through application logs.
/// </summary>
public class MLAdaptiveThresholdLog : Entity<long>
{
    /// <summary>The model whose threshold was evaluated.</summary>
    public long MLModelId { get; set; }

    /// <summary>Currency pair for quick filtering on dashboards.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Candle timeframe for quick filtering on dashboards.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// <c>null</c> for the global threshold row; otherwise the regime whose threshold is being
    /// updated within the same cycle.
    /// </summary>
    public MarketRegime? Regime { get; set; }

    /// <summary>UTC timestamp at which the worker took the decision recorded by this row.</summary>
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Stable result bucket: <c>updated</c>, <c>skipped_drift</c>, <c>skipped_data</c>,
    /// <c>skipped_regression</c>, <c>skipped_stationarity</c>, <c>skipped_concurrency</c>,
    /// <c>error</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Stable machine-readable reason inside <see cref="Outcome"/>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Threshold that was active before this cycle.</summary>
    public double PreviousThreshold { get; set; }

    /// <summary>Threshold proposed by the EV sweep (pre-EMA-blend).</summary>
    public double OptimalThreshold { get; set; }

    /// <summary>Threshold actually written back to the snapshot after EMA-blend + clamp.</summary>
    public double NewThreshold { get; set; }

    /// <summary>Absolute drift from <see cref="PreviousThreshold"/> to <see cref="NewThreshold"/>.</summary>
    public double Drift { get; set; }

    /// <summary>Walk-forward holdout EV at the candidate threshold.</summary>
    public double HoldoutEvAtNewThreshold { get; set; }

    /// <summary>Walk-forward holdout EV at the previous threshold (regression baseline).</summary>
    public double HoldoutEvAtPreviousThreshold { get; set; }

    /// <summary>Mean P&amp;L (in pips) per trade observed at the new threshold on the holdout.</summary>
    public double HoldoutMeanPnlPips { get; set; }

    /// <summary>Number of weighted samples (post-decay) used in the sweep slice.</summary>
    public int SweepSampleSize { get; set; }

    /// <summary>Number of weighted samples used in the holdout slice.</summary>
    public int HoldoutSampleSize { get; set; }

    /// <summary>PSI between the older and newer halves of the prediction window.</summary>
    public double StationarityPsi { get; set; }

    /// <summary>
    /// Most recent prediction-log <c>OutcomeRecordedAt</c> seen by this evaluation cycle.
    /// Persisted so the stale-data short-circuit survives process restarts and shared
    /// across replicas instead of relying on in-memory state.
    /// </summary>
    public DateTime? NewestOutcomeAt { get; set; }

    /// <summary>Versioned JSON payload for non-indexed diagnostic context.</summary>
    public string DiagnosticsJson { get; set; } = "{}";

    /// <summary>Soft-delete flag, filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
