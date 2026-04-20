using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persists lightweight day-level scheduling state for the strategy-generation worker.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler keeps in-memory counters for once-per-day execution, retry budgeting,
/// consecutive failures, and circuit-breaker backoff. This entity mirrors that state to
/// durable storage so a process restart does not accidentally rerun a completed daily
/// cycle, forget an exhausted retry window, or clear an active circuit breaker.
/// </para>
/// <para>
/// The store maintains one active row per <see cref="WorkerName"/>. This state is
/// intentionally separate from <see cref="StrategyGenerationCycleRun"/> because schedule
/// gating only needs a compact worker-level snapshot, not the full cycle audit history.
/// </para>
/// </remarks>
public class StrategyGenerationScheduleState : Entity<long>
{
    /// <summary>
    /// Logical worker that owns this schedule state. The active row is unique per worker.
    /// </summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>
    /// UTC date on which the worker last completed, skipped for the day, or otherwise
    /// consumed its scheduled daily run slot. Null means no durable run date is known.
    /// </summary>
    public DateTime? LastRunDateUtc { get; set; }

    /// <summary>
    /// UTC timestamp until which automatic scheduled generation is paused by the circuit
    /// breaker. Null means the circuit breaker is not active.
    /// </summary>
    public DateTime? CircuitBreakerUntilUtc { get; set; }

    /// <summary>
    /// Number of consecutive scheduled generation failures. This resets to zero after a
    /// successful run and is compared with the configured failure threshold before opening
    /// the circuit breaker.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Number of retry attempts already used inside the current scheduled run window.
    /// When exhausted, the scheduler marks the day as consumed and waits for the next day.
    /// </summary>
    public int RetriesThisWindow { get; set; }

    /// <summary>
    /// UTC date for the retry window represented by <see cref="RetriesThisWindow"/>.
    /// If this date is not today when loaded, the retry counter is reset.
    /// </summary>
    public DateTime? RetryWindowDateUtc { get; set; }

    /// <summary>
    /// UTC timestamp when this schedule-state row was last persisted.
    /// </summary>
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the active schedule-state lookup.</summary>
    public bool IsDeleted { get; set; }
}
