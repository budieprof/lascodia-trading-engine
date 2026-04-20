using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Durable audit record for one strategy-generation cycle, from start through
/// completion/failure and summary-event publication.
/// </summary>
/// <remarks>
/// <para>
/// A cycle run is created as soon as the strategy-generation runner starts so recovery
/// paths have a durable anchor even if the process crashes before screening finishes.
/// The row is then updated as the cycle attaches its screening fingerprint, completes
/// candidate generation, publishes its cycle summary, or records a terminal failure.
/// </para>
/// <para>
/// This entity is intentionally separate from <see cref="StrategyGenerationCheckpoint"/>.
/// Checkpoints hold resumable in-progress screening state, while cycle-run records form
/// the long-lived operational audit trail used by health queries, summary dispatch
/// reconciliation, previous-cycle lookup, and operator diagnostics.
/// </para>
/// </remarks>
public class StrategyGenerationCycleRun : Entity<long>
{
    /// <summary>
    /// Logical worker that produced this cycle run. Used for worker-scoped history and
    /// previous-completed-cycle lookups.
    /// </summary>
    public string WorkerName { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier for this generation attempt. The runner creates this from the
    /// UTC start timestamp plus a GUID suffix so concurrent/manual triggers remain distinct.
    /// </summary>
    public string CycleId { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle status for the cycle. Current values are <c>Running</c>,
    /// <c>Completed</c>, and <c>Failed</c>.
    /// </summary>
    public string Status { get; set; } = "Running";

    /// <summary>
    /// Optional compatibility fingerprint for the screening context used by the cycle.
    /// This is attached once the screening context is known and is useful for correlating
    /// the run with any checkpoint written during the same cycle.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>UTC timestamp when the generation cycle was started.</summary>
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when the cycle reached a terminal status. Null while the cycle is
    /// still <c>Running</c>.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Total elapsed runtime in milliseconds, measured by the runner when it stages or
    /// records completion.
    /// </summary>
    public double? DurationMs { get; set; }

    /// <summary>
    /// Number of primary candidate strategies accepted during this cycle.
    /// </summary>
    public int CandidatesCreated { get; set; }

    /// <summary>
    /// Number of reserve candidate strategies accepted for regime rotation or future
    /// promotion during this cycle.
    /// </summary>
    public int ReserveCandidatesCreated { get; set; }

    /// <summary>
    /// Number of strategy candidates evaluated by the screening pipeline, including
    /// candidates that failed one or more quality gates.
    /// </summary>
    public int CandidatesScreened { get; set; }

    /// <summary>
    /// Number of symbols whose screening loop was processed or resumed during this cycle.
    /// </summary>
    public int SymbolsProcessed { get; set; }

    /// <summary>
    /// Number of symbols skipped because of caps, existing coverage, correlation limits,
    /// missing data, or other generation gates.
    /// </summary>
    public int SymbolsSkipped { get; set; }

    /// <summary>
    /// Number of existing strategies pruned or retired as part of cycle maintenance.
    /// </summary>
    public int StrategiesPruned { get; set; }

    /// <summary>
    /// Number of otherwise valid candidates removed by portfolio-level filters such as
    /// correlation, concentration, or drawdown contribution checks.
    /// </summary>
    public int PortfolioFilterRemoved { get; set; }

    /// <summary>
    /// Integration-event identifier for the cycle-completed summary event, when one has
    /// been staged for publication.
    /// </summary>
    public Guid? SummaryEventId { get; set; }

    /// <summary>
    /// Serialized cycle-completed summary event payload. Stored so failed or interrupted
    /// dispatches can be retried exactly without reconstructing the event from changed state.
    /// </summary>
    public string? SummaryEventPayloadJson { get; set; }

    /// <summary>
    /// Number of attempts made to publish the summary event for this cycle.
    /// </summary>
    public int SummaryEventDispatchAttempts { get; set; }

    /// <summary>
    /// UTC timestamp when the summary event was successfully published. Null when no
    /// summary was staged or when publication is still pending retry.
    /// </summary>
    public DateTime? SummaryEventDispatchedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp of the latest summary-event publication failure.
    /// </summary>
    public DateTime? SummaryEventFailedAtUtc { get; set; }

    /// <summary>
    /// Truncated error message from the latest summary-event publication failure.
    /// </summary>
    public string? SummaryEventFailureMessage { get; set; }

    /// <summary>
    /// Pipeline stage where a terminal cycle failure occurred, such as recovery,
    /// screening, persistence, summary publication, or cleanup.
    /// </summary>
    public string? FailureStage { get; set; }

    /// <summary>
    /// Truncated diagnostic message for a terminal cycle failure.
    /// </summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// UTC timestamp when this audit row was last updated. Used by health hydration and
    /// pending summary-dispatch ordering.
    /// </summary>
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
