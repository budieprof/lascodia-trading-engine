using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a conformal coverage breaker event (Rec #81) for an <see cref="MLModel"/>.
/// When the empirical coverage of the model's conformal prediction sets falls below the
/// target level for a configurable number of consecutive bars, the model is temporarily
/// suspended from scoring and this log entry is created.
/// </summary>
/// <remarks>
/// Managed by <c>MLConformalBreakerWorker</c>. The model is reinstated automatically
/// once <see cref="ResumeAt"/> is reached, or sooner if coverage recovers.
/// </remarks>
public class MLConformalBreakerLog : Entity<long>
{
    /// <summary>Foreign key to the <see cref="MLModel"/> that was suspended.</summary>
    public long MLModelId { get; set; }

    /// <summary>The currency pair for which coverage monitoring triggered the suspension.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The chart timeframe for which coverage monitoring triggered the suspension.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// Number of consecutive prediction bars on which empirical coverage was below the
    /// target level before this suspension was triggered.
    /// </summary>
    public int ConsecutivePoorCoverageBars { get; set; }

    /// <summary>Number of resolved predictions evaluated in the breaker window.</summary>
    public int SampleCount { get; set; }

    /// <summary>Number of evaluated predictions whose conformal set contained the true outcome.</summary>
    public int CoveredCount { get; set; }

    /// <summary>
    /// The empirical coverage rate at the time of suspension (fraction of true outcomes
    /// that fell inside the model's conformal prediction set).
    /// E.g. 0.82 when the target was 0.90 means 8 percentage points below target.
    /// </summary>
    public double EmpiricalCoverage { get; set; }

    /// <summary>Target conformal coverage used for the breaker decision.</summary>
    public double TargetCoverage { get; set; }

    /// <summary>Conformal threshold used for the breaker decision.</summary>
    public double CoverageThreshold { get; set; }

    /// <summary>Reason the breaker tripped.</summary>
    public MLConformalBreakerTripReason TripReason { get; set; } = MLConformalBreakerTripReason.Unknown;

    /// <summary>Wilson lower confidence bound for empirical conformal coverage, when computed.</summary>
    public double? CoverageLowerBound { get; set; }

    /// <summary>Wilson upper confidence bound for empirical conformal coverage, when computed.</summary>
    public double? CoverageUpperBound { get; set; }

    /// <summary>One-sided binomial lower-tail p-value for observed coverage, when computed.</summary>
    public double? CoveragePValue { get; set; }

    /// <summary>Number of post-suspension resolved predictions used for the latest active-breaker evaluation.</summary>
    public int FreshSampleCount { get; set; }

    /// <summary>UTC timestamp of the newest prediction outcome considered by the latest breaker evaluation.</summary>
    public DateTime? LastEvaluatedOutcomeAt { get; set; }

    /// <summary>
    /// Number of bars for which the model will remain suspended before being eligible
    /// for automatic reinstatement.
    /// </summary>
    public int SuspensionBars { get; set; }

    /// <summary>UTC timestamp when the model was suspended.</summary>
    public DateTime SuspendedAt { get; set; }

    /// <summary>UTC timestamp when the model suspension is scheduled to lift.</summary>
    public DateTime ResumeAt { get; set; }

    /// <summary>
    /// <c>true</c> while the suspension is in effect.
    /// Set to <c>false</c> by <c>MLConformalBreakerWorker</c> once <see cref="ResumeAt"/>
    /// is reached or coverage recovers.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The ML model that was suspended by this breaker event.</summary>
    public virtual MLModel MLModel { get; set; } = null!;
}
