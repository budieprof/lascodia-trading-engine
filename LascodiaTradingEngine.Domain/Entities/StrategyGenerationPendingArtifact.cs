using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Stores deferred post-persistence work for a generated strategy candidate.
/// </summary>
/// <remarks>
/// <para>
/// Strategy generation persists the strategy row first, then performs follow-up side
/// effects such as writing the creation audit entry and publishing candidate-created or
/// auto-promoted integration events. If any of those side effects cannot be completed
/// inline, this entity keeps a durable replay item so the worker can finish the work
/// after a crash, transient event-bus issue, or database failure.
/// </para>
/// <para>
/// The pending-artifact store keeps one active row per <see cref="CandidateId"/> and
/// treats the table as a replay backlog snapshot. Rows are soft-deleted when all work is
/// complete, while corrupt or terminally unreplayable rows are quarantined by setting
/// <see cref="QuarantinedAtUtc"/> and <see cref="TerminalFailureReason"/>.
/// </para>
/// </remarks>
public class StrategyGenerationPendingArtifact : Entity<long>
{
    /// <summary>
    /// Database identifier of the strategy row whose post-persist side effects still
    /// need to be completed or reconciled.
    /// </summary>
    public long StrategyId { get; set; }

    /// <summary>
    /// Stable generation candidate identifier. This is unique among active pending
    /// artifacts and is used for replay lookup, deduplication, and failure resolution.
    /// </summary>
    public string CandidateId { get; set; } = string.Empty;

    /// <summary>
    /// Optional generation cycle that produced the strategy candidate.
    /// </summary>
    public string? CycleId { get; set; }

    /// <summary>
    /// Serialized candidate snapshot used to reconstruct enough screening context for
    /// audit logging and integration-event replay. The canonical strategy row is still
    /// reloaded by <see cref="StrategyId"/> during replay.
    /// </summary>
    public string CandidatePayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> when the strategy creation decision-log/audit entry still needs to
    /// be written or verified.
    /// </summary>
    public bool NeedsCreationAudit { get; set; }

    /// <summary>
    /// <c>true</c> when the strategy-candidate-created integration event still needs to
    /// be staged and published.
    /// </summary>
    public bool NeedsCreatedEvent { get; set; }

    /// <summary>
    /// <c>true</c> when an elite candidate still needs its auto-promoted event and
    /// associated metrics update replayed.
    /// </summary>
    public bool NeedsAutoPromoteEvent { get; set; }

    /// <summary>
    /// Number of replay attempts made against this artifact. A single attempt may clear
    /// multiple pending side effects.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent replay attempt or quarantine action.
    /// </summary>
    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>
    /// Last replay error or waiting-status message. Cleared when replay progress succeeds.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// UTC timestamp when the strategy creation audit entry was logged or confirmed to exist.
    /// </summary>
    public DateTime? CreationAuditLoggedAtUtc { get; set; }

    /// <summary>
    /// Integration-event identifier for the candidate-created event, once it has been staged.
    /// </summary>
    public Guid? CandidateCreatedEventId { get; set; }

    /// <summary>
    /// UTC timestamp when the candidate-created event was confirmed published by the event log.
    /// </summary>
    public DateTime? CandidateCreatedEventDispatchedAtUtc { get; set; }

    /// <summary>
    /// Integration-event identifier for the auto-promoted event, once it has been staged.
    /// </summary>
    public Guid? AutoPromotedEventId { get; set; }

    /// <summary>
    /// UTC timestamp when the auto-promoted event was confirmed published by the event log.
    /// </summary>
    public DateTime? AutoPromotedEventDispatchedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when this artifact was quarantined and excluded from normal replay.
    /// Null means the artifact is still eligible for replay if not soft-deleted.
    /// </summary>
    public DateTime? QuarantinedAtUtc { get; set; }

    /// <summary>
    /// Terminal reason explaining why the artifact was quarantined, typically corrupt
    /// serialized payload data or another unrecoverable replay condition.
    /// </summary>
    public string? TerminalFailureReason { get; set; }

    /// <summary>Soft-delete flag. Completed artifacts are removed from the active replay backlog by setting this flag.</summary>
    public bool IsDeleted { get; set; }
}
