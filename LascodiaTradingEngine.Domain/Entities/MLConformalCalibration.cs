using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores the calibration set nonconformity scores used by split conformal prediction
/// to produce statistically guaranteed coverage sets (Rec #16).
/// One row per model: the <see cref="NonConformityScoresJson"/> array is the sorted
/// calibration residuals from which the coverage threshold is derived at inference time.
/// </summary>
/// <remarks>
/// Split conformal prediction splits the labelled data into a training set and a
/// calibration set. For each calibration sample, the nonconformity score is
/// α_i = 1 − ŷ_{y_i} (1 minus the predicted probability of the true class).
/// The 90 % coverage threshold τ is the ⌈(n+1)(1−α)/n⌉-th quantile of these scores.
/// At inference time, the prediction set includes all classes whose predicted probability
/// exceeds 1 − τ.  When both classes exceed the threshold the set is "Ambiguous".
/// </remarks>
public class MLConformalCalibration : Entity<long>
{
    /// <summary>FK to the <see cref="MLModel"/> this calibration belongs to.</summary>
    public long     MLModelId            { get; set; }

    /// <summary>The currency pair (e.g. "EURUSD").</summary>
    public string   Symbol               { get; set; } = string.Empty;

    /// <summary>The chart timeframe.</summary>
    public Timeframe Timeframe           { get; set; } = Timeframe.H1;

    /// <summary>
    /// JSON-serialised double[] of sorted nonconformity scores from the calibration set.
    /// Used at inference time to compute the coverage threshold τ.
    /// </summary>
    public string   NonConformityScoresJson { get; set; } = "[]";

    /// <summary>
    /// Number of calibration samples used to build the score distribution.
    /// More samples → tighter, more reliable coverage threshold.
    /// </summary>
    public int      CalibrationSamples   { get; set; }

    /// <summary>
    /// Target marginal coverage level (0–1). Default 0.90 means the prediction set
    /// contains the true label at least 90 % of the time over calibration samples.
    /// </summary>
    public double   TargetCoverage       { get; set; } = 0.90;

    /// <summary>
    /// The coverage threshold τ derived from <see cref="NonConformityScoresJson"/>.
    /// Pre-computed and stored for fast lookup at inference time without re-sorting.
    /// </summary>
    public double   CoverageThreshold    { get; set; }

    /// <summary>
    /// Empirical coverage measured on a held-out test set after calibration.
    /// Should be ≥ <see cref="TargetCoverage"/> if the calibration is valid.
    /// Null until post-calibration evaluation is run.
    /// </summary>
    public double?  EmpiricalCoverage    { get; set; }

    /// <summary>Fraction of inference calls that returned an "Ambiguous" set on the test set.</summary>
    public double?  AmbiguousRate        { get; set; }

    /// <summary>UTC timestamp when this calibration was computed.</summary>
    public DateTime CalibratedAt         { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag.</summary>
    public bool     IsDeleted            { get; set; }

    public virtual MLModel MLModel { get; set; } = null!;
}
