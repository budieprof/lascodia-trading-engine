using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists a correlated failure detection event raised when a significant fraction
/// of active ML models degrade simultaneously — indicative of a systemic market
/// structure shift (e.g. central bank intervention, liquidity shock) rather than
/// isolated per-symbol model degradation.
/// </summary>
/// <remarks>
/// Produced by <c>MLCorrelatedFailureWorker</c>. When the failure ratio exceeds the
/// configured alarm threshold, the worker may activate a system-wide training pause
/// (via <c>MLTraining:SystemicPauseActive</c>) to prevent wasting compute on models
/// that will immediately degrade again. The pause is lifted when the failure ratio
/// drops below the recovery threshold.
/// </remarks>
public class MLCorrelatedFailureLog : Entity<long>
{
    /// <summary>UTC timestamp when the correlated failure was detected.</summary>
    public DateTime DetectedAt          { get; set; } = DateTime.UtcNow;

    /// <summary>Number of active models that are currently degraded (below drift accuracy threshold).</summary>
    public int      FailingModelCount   { get; set; }

    /// <summary>Total number of active models evaluated.</summary>
    public int      TotalModelCount     { get; set; }

    /// <summary>Ratio of failing to total models (0.0–1.0).</summary>
    public double   FailureRatio        { get; set; }

    /// <summary>
    /// JSON array of affected symbol strings: <c>["EURUSD","GBPUSD","USDJPY"]</c>.
    /// </summary>
    public string   SymbolsAffectedJson { get; set; } = "[]";

    /// <summary>
    /// <c>true</c> when this detection event triggered a systemic training pause.
    /// <c>false</c> when the event was logged but did not trigger a pause (e.g. already paused).
    /// </summary>
    public bool     PauseActivated      { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool     IsDeleted           { get; set; }
}
