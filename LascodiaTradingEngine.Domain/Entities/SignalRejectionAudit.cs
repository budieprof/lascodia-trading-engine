using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Append-only audit row for every signal suppression or rejection that happens
/// anywhere in the StrategyWorker → SignalOrderBridgeWorker → /order/from-signal
/// pipeline. One row per decision point.
/// </summary>
/// <remarks>
/// <para>
/// Previously the engine had ~20 suppression paths scattered across workers, each
/// emitting its own log line and (usually) a <c>DecisionLog</c> row. Answering
/// "why didn't signal #123 fire?" meant joining log files against the generic
/// <c>DecisionLog</c> table on <c>EntityType</c> / <c>DecisionType</c> / timestamp
/// — painful for operators and brittle for dashboards.
/// </para>
/// <para>
/// This table collapses that into a single purpose-built stream. It is intentionally
/// narrow: <see cref="Stage"/> (which filter rejected), <see cref="Reason"/> (the
/// short code that identifies the specific rule), and optional context. No
/// <c>IsDeleted</c> — this is an immutable audit log; retention is enforced by a
/// separate housekeeping worker, not by soft-delete.
/// </para>
/// <para>
/// Produced by <c>ISignalRejectionAuditor</c>. Consumed by <c>CalibrationSnapshotWorker</c>
/// for monthly rejection-distribution snapshots and by operator dashboards for
/// ad-hoc queries.
/// </para>
/// </remarks>
public class SignalRejectionAudit : Entity<long>
{
    /// <summary>
    /// FK to the <c>TradeSignal</c> that was rejected. Null for rejections that
    /// happen before a signal is created (e.g. pre-fetch timeouts, regime
    /// coherence suppression, conflict-resolution suppression of losing candidates).
    /// </summary>
    public long?    TradeSignalId { get; set; }

    /// <summary>
    /// FK to the owning <c>Strategy</c>. Zero for tick-level rejections that
    /// apply to the whole symbol (e.g. regime coherence, stale EA heartbeat).
    /// </summary>
    public long     StrategyId    { get; set; }

    /// <summary>Currency pair symbol (max 10 chars). Always populated.</summary>
    public string   Symbol        { get; set; } = string.Empty;

    /// <summary>
    /// Broad pipeline stage at which the rejection occurred. Short code from a
    /// fixed vocabulary: <c>Prefetch</c>, <c>Regime</c>, <c>News</c>, <c>Evaluator</c>,
    /// <c>MTF</c>, <c>Correlation</c>, <c>Hawkes</c>, <c>MLScoring</c>,
    /// <c>Abstention</c>, <c>ConflictResolution</c>, <c>Tier1</c>, <c>Tier2</c>,
    /// <c>MLModelStale</c>, <c>PaperRouting</c>. Max 32 chars.
    /// </summary>
    public string   Stage         { get; set; } = string.Empty;

    /// <summary>
    /// Specific rejection reason within the <see cref="Stage"/>. Short machine-
    /// readable code used for dashboard facets and aggregation, e.g.
    /// <c>regime_coherence_timeout</c>, <c>ml_model_stale</c>,
    /// <c>opposing_directions</c>. Max 64 chars.
    /// </summary>
    public string   Reason        { get; set; } = string.Empty;

    /// <summary>
    /// Optional human-readable detail — the same text that would go in a log
    /// line. Primarily for operators looking at the raw row; machines should
    /// prefer <see cref="Reason"/>.
    /// </summary>
    public string?  Detail        { get; set; }

    /// <summary>
    /// The worker or service that wrote this row. Examples:
    /// <c>StrategyWorker</c>, <c>SignalOrderBridgeWorker</c>. Max 50 chars.
    /// </summary>
    public string   Source        { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the rejection was recorded. Set at write time, never updated.</summary>
    public DateTime RejectedAt    { get; set; } = DateTime.UtcNow;

    // Intentionally no IsDeleted — this is an immutable audit stream.
}
