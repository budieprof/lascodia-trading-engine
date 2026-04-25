using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persistent lease record backing <c>LeaseBasedDistributedLock</c>. Replaces the previous
/// session-scoped Postgres advisory-lock primitive, which had no auto-expiry on holder
/// crash and could strand a lock for the connection-pool TTL (typically 5–30 s).
/// </summary>
/// <remarks>
/// <para>
/// Each row represents a held lease. Acquire is an upsert that succeeds only when the
/// existing row is expired or absent; release hard-deletes the row; a background
/// heartbeat extends <see cref="ExpiresAtUtc"/> while the lock is held. If the holder
/// crashes the heartbeat stops, the lease expires after at most one lease duration, and
/// other workers can re-acquire — independent of any database connection state.
/// </para>
/// <para>
/// Hard-deletes (rather than the codebase-default soft-delete) on release are deliberate:
/// keeping tombstone rows around would bloat the table without serving any operational
/// purpose. The <see cref="IsDeleted"/> column is preserved for soft-delete contract
/// compliance only.
/// </para>
/// </remarks>
public class DistributedLockLease : Entity<long>
{
    /// <summary>
    /// Logical lock key (e.g. <c>workers:ml-adwin-drift:cycle</c>). Unique across the table.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Per-acquire identifier. Generated fresh on each successful acquire so heartbeat
    /// and release operations only succeed against the current holder, never a stale one.
    /// </summary>
    public Guid OwnerId { get; set; }

    /// <summary>UTC timestamp at which this lease was first acquired.</summary>
    public DateTime AcquiredAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp at which this lease expires. The lease is considered held while
    /// <c>ExpiresAtUtc &gt; UtcNow</c>; otherwise it is reclaimable. Heartbeat extends
    /// this on a half-lease cadence.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool IsDeleted { get; set; }
}
