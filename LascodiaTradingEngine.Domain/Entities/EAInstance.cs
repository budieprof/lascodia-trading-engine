using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Tracks a registered MQL5 Expert Advisor instance connected to the engine.
/// Each EA instance runs on a specific MT5 chart and is responsible for one or more symbols.
/// Only one instance may be designated as the coordinator at any time.
/// </summary>
/// <remarks>
/// The engine uses heartbeat timestamps to detect stale connections. If an instance
/// misses heartbeats beyond the configured threshold, the <c>EAHealthMonitorWorker</c>
/// transitions it to <see cref="EAInstanceStatus.Disconnected"/>.
/// </remarks>
public class EAInstance : Entity<long>
{
    /// <summary>
    /// Client-assigned unique identifier for this EA instance (typically a GUID
    /// generated on first attach inside the MQL5 EA).
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// The trading account number on the MT5 broker side.
    /// Used to correlate with <see cref="TradingAccount"/> records.
    /// </summary>
    public long TradingAccountId { get; set; }

    /// <summary>
    /// Comma-separated list of symbols this EA instance is responsible for
    /// (e.g. "EURUSD,GBPUSD,USDJPY"). Used for symbol-overlap validation on registration.
    /// </summary>
    public string Symbols { get; set; } = string.Empty;

    /// <summary>
    /// The primary chart symbol the EA is attached to in MT5.
    /// </summary>
    public string ChartSymbol { get; set; } = string.Empty;

    /// <summary>
    /// The chart timeframe the EA is attached to (e.g. "H1", "M15").
    /// </summary>
    public string ChartTimeframe { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, this instance acts as the coordinator responsible for
    /// account-level operations (e.g. reconciliation, session broadcasts).
    /// Only one coordinator per trading account is allowed.
    /// </summary>
    public bool IsCoordinator { get; set; }

    /// <summary>Current lifecycle status of this EA instance.</summary>
    public EAInstanceStatus Status { get; set; } = EAInstanceStatus.Active;

    /// <summary>UTC timestamp of the last heartbeat received from this instance.</summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version string of the EA binary (e.g. "1.0.0"). Used for compatibility checks
    /// and to determine whether the EA needs an upgrade prompt.
    /// </summary>
    public string EAVersion { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this instance first registered with the engine.</summary>
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when this instance was deregistered. Null while active.</summary>
    public DateTime? DeregisteredAt { get; set; }

    /// <summary>
    /// Highest position-delta sequence number successfully processed from this instance.
    /// Used for idempotency — duplicate or out-of-order deltas are rejected.
    /// </summary>
    public long? LastProcessedDeltaSequence { get; set; }

    /// <summary>
    /// Highest position-snapshot sequence number successfully processed from this instance.
    /// Used for idempotency — duplicate or out-of-order snapshots are rejected.
    /// </summary>
    public long? LastProcessedPositionSnapshotSequence { get; set; }

    /// <summary>
    /// Highest order-snapshot sequence number successfully processed from this instance.
    /// Used for idempotency — duplicate or out-of-order snapshots are rejected.
    /// </summary>
    public long? LastProcessedOrderSnapshotSequence { get; set; }

    /// <summary>
    /// Highest deal-snapshot sequence number successfully processed from this instance.
    /// Used for idempotency — duplicate or out-of-order snapshots are rejected.
    /// </summary>
    public long? LastProcessedDealSnapshotSequence { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Optimistic concurrency token — auto-incremented by PostgreSQL on every update.</summary>
    public uint RowVersion { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The trading account this EA instance is connected to.</summary>
    public virtual TradingAccount TradingAccount { get; set; } = null!;
}
