using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Per-cycle, per-(model, regime) audit row for <c>MLCalibrationMonitorWorker</c> decisions.
/// Captures the ECE measurement, all three failure signals, the bootstrap-derived stderr that
/// gates the trend test, and a JSON blob with per-bin reliability data so reliability diagrams
/// can be reconstructed without rerunning the cycle.
/// </summary>
public class MLCalibrationLog : Entity<long>
{
    /// <summary>The model this row evaluates.</summary>
    public long MLModelId { get; set; }

    /// <summary>Currency pair for quick filter on dashboards.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Candle timeframe for quick filter on dashboards.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// <c>null</c> for the global, regime-pooled row; otherwise the regime whose samples were
    /// evaluated in this row. The worker emits one global row plus one row per matched regime.
    /// </summary>
    public MarketRegime? Regime { get; set; }

    /// <summary>UTC timestamp when this row was decided.</summary>
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Stable result bucket: <c>evaluated</c>, <c>skipped_data</c>, <c>skipped_unstable</c>,
    /// <c>skipped_lock</c>, <c>auto_resolved</c>, <c>alert_warning</c>, <c>alert_critical</c>,
    /// <c>retrain_queued</c>, <c>error</c>.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Stable machine-readable reason within <see cref="Outcome"/>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Number of resolved-prediction samples that contributed to the ECE measurement.</summary>
    public int ResolvedSampleCount { get; set; }

    /// <summary>Expected calibration error measured this cycle.</summary>
    public double CurrentEce { get; set; }

    /// <summary>Prior cycle's ECE for this scope, when available.</summary>
    public double? PreviousEce { get; set; }

    /// <summary>Training-time baseline ECE from the model snapshot, when available.</summary>
    public double? BaselineEce { get; set; }

    /// <summary>Current minus previous ECE.</summary>
    public double TrendDelta { get; set; }

    /// <summary>Current minus baseline ECE.</summary>
    public double BaselineDelta { get; set; }

    /// <summary>Direction-correct rate observed across the resolved sample.</summary>
    public double Accuracy { get; set; }

    /// <summary>Mean predicted-class confidence across the resolved sample.</summary>
    public double MeanConfidence { get; set; }

    /// <summary>
    /// Bootstrap-derived stderr of the ECE point estimate. Used to gate trend signal: a
    /// Warning fires only when |TrendDelta| exceeds <c>RegressionGuardK × EceStderr</c> in
    /// addition to the raw <c>DegradationDelta</c> threshold.
    /// </summary>
    public double EceStderr { get; set; }

    /// <summary>True if absolute ECE exceeded the configured ceiling.</summary>
    public bool ThresholdExceeded { get; set; }

    /// <summary>True if cycle-over-cycle delta exceeded the degradation threshold AND survived the stderr gate.</summary>
    public bool TrendExceeded { get; set; }

    /// <summary>True if delta versus training-time baseline exceeded the degradation threshold.</summary>
    public bool BaselineExceeded { get; set; }

    /// <summary>Final state assigned: <c>none</c>, <c>warning</c>, or <c>critical</c>.</summary>
    public string AlertState { get; set; } = "none";

    /// <summary>Most recent prediction-log <c>OutcomeRecordedAt</c> seen by this evaluation.
    /// Persisted so cross-restart stale-data short-circuit survives without in-memory state.</summary>
    public DateTime? NewestOutcomeAt { get; set; }

    /// <summary>
    /// Versioned JSON payload. Always includes per-bin reliability data
    /// (<c>bins[]: { count, accuracy, meanConfidence }</c>) so reliability diagrams can be
    /// reconstructed downstream. Other diagnostic fields included as needed.
    /// </summary>
    public string DiagnosticsJson { get; set; } = "{}";

    /// <summary>Soft-delete flag, filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
