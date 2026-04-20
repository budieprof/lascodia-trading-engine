using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records an unresolved or operator-visible failure encountered while generating,
/// screening, persisting, or replaying a strategy candidate.
/// </summary>
/// <remarks>
/// <para>
/// Strategy generation can accept candidates before all persistence side effects have
/// succeeded. When a candidate cannot be repaired inline, this entity keeps enough
/// durable context to diagnose the failure, retry related recovery work, and avoid
/// losing visibility after the worker process restarts.
/// </para>
/// <para>
/// The failure store deduplicates active failures by <see cref="CandidateId"/> and
/// <see cref="FailureStage"/> so repeated retries do not flood the table with copies
/// of the same unresolved issue. Operator reporting is tracked separately from
/// resolution: <see cref="IsReported"/> means the issue has been surfaced, while
/// <see cref="ResolvedAtUtc"/> means later replay or persistence succeeded for the
/// candidate.
/// </para>
/// </remarks>
public class StrategyGenerationFailure : Entity<long>
{
    /// <summary>
    /// Stable candidate identifier used to correlate the failure with retry,
    /// reconciliation, and resolution paths.
    /// </summary>
    public string CandidateId { get; set; } = string.Empty;

    /// <summary>
    /// Optional generation cycle that produced the failed candidate. Null is allowed for
    /// best-effort screening audit failures that are not tied to a persisted cycle run.
    /// </summary>
    public string? CycleId { get; set; }

    /// <summary>
    /// Hash or normalized identity for the candidate's strategy type, symbol, timeframe,
    /// and parameters. Used for diagnostics and duplicate-candidate analysis.
    /// </summary>
    public string CandidateHash { get; set; } = string.Empty;

    /// <summary>The strategy archetype or template family of the failed candidate.</summary>
    public StrategyType StrategyType { get; set; }

    /// <summary>The traded instrument for the failed candidate (for example, "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The candle timeframe for the failed candidate.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.H1;

    /// <summary>
    /// JSON snapshot of the normalized strategy parameters for the failed candidate.
    /// This allows operators to reproduce or inspect the candidate even if templates
    /// later change.
    /// </summary>
    public string ParametersJson { get; set; } = string.Empty;

    /// <summary>
    /// Pipeline stage or screening gate where the failure occurred. Active failures are
    /// deduplicated by this value together with <see cref="CandidateId"/>.
    /// </summary>
    public string FailureStage { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable reason describing why the stage failed, such as a rejected gate,
    /// persistence exception category, replay failure, or validation issue.
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Optional structured diagnostic payload with additional context, command data,
    /// exception metadata, or audit-trail details.
    /// </summary>
    public string? DetailsJson { get; set; }

    /// <summary>
    /// <c>true</c> once the unresolved failure has been surfaced to operators or logs.
    /// Reporting does not imply recovery; use <see cref="ResolvedAtUtc"/> for that.
    /// </summary>
    public bool IsReported { get; set; }

    /// <summary>UTC timestamp when this failure row was first recorded.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when the candidate failure was resolved by a later successful
    /// persistence or replay path. Null means the failure is still unresolved.
    /// </summary>
    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
