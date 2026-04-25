using LascodiaTradingEngine.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Infrastructure.Services;

/// <summary>
/// Distributed lock implementation backed by a typed <c>DistributedLockLease</c> row with
/// time-bound TTL and a background heartbeat. Replaces the prior session-scoped Postgres
/// advisory-lock primitive (<c>PostgresAdvisoryLock</c>), which had no auto-expiry on
/// holder crash and could strand a lock for the connection-pool TTL.
/// </summary>
/// <remarks>
/// <para>
/// Acquire is a single SQL upsert: insert a fresh lease row, or update on conflict only
/// when the existing row is expired or soft-deleted. Returns 1 row if acquired, 0 if the
/// lock is currently held by an unexpired lease — providing the same fast/non-blocking
/// contention-detection semantics as the advisory lock.
/// </para>
/// <para>
/// Once acquired, the returned <c>LeaseHandle</c> starts a background heartbeat that
/// extends <c>ExpiresAtUtc</c> on a half-lease cadence (default lease 60 s, heartbeat
/// 30 s). On disposal the lease row is hard-deleted. If the holding process crashes the
/// heartbeat stops, the lease expires within at most one lease duration, and another
/// worker can acquire — independent of the database connection state.
/// </para>
/// <para>
/// Provider compatibility: relies on <c>INSERT ... ON CONFLICT (Key) DO UPDATE ... WHERE</c>
/// which is supported by Postgres and by SQLite ≥ 3.24, both of which the rest of the
/// codebase targets. The handle is safe to dispose multiple times and idempotent.
/// </para>
/// </remarks>
public sealed class LeaseBasedDistributedLock : IDistributedLock
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeaseBasedDistributedLock> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lease duration. Should be long enough to safely outlast a heartbeat round-trip plus
    /// transient DB hiccups, short enough that a crashed holder recovers within tolerable
    /// time.
    /// </summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Heartbeat cadence. Conservative half-lease default ensures one missed heartbeat
    /// still leaves headroom before the lease expires.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; }

    public LeaseBasedDistributedLock(
        IServiceScopeFactory scopeFactory,
        ILogger<LeaseBasedDistributedLock> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? heartbeatInterval = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(60);
        HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromTicks(LeaseDuration.Ticks / 2);
    }

    public Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
        => TryAcquireAsync(lockKey, TimeSpan.Zero, ct);

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string lockKey, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lockKey);

        var ownerId = Guid.NewGuid();
        var deadline = _timeProvider.GetUtcNow() + timeout;
        int pollMs = 50;
        const int maxPollMs = 1000;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await TryAcquireOnceAsync(lockKey, ownerId, ct))
            {
                _logger.LogDebug(
                    "LeaseBasedDistributedLock: acquired '{Key}' as {OwnerId} (lease {LeaseSec}s, heartbeat {HbSec}s)",
                    lockKey, ownerId, LeaseDuration.TotalSeconds, HeartbeatInterval.TotalSeconds);

                return new LeaseHandle(this, lockKey, ownerId);
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (timeout <= TimeSpan.Zero || remaining <= TimeSpan.Zero)
            {
                _logger.LogDebug(
                    "LeaseBasedDistributedLock: '{Key}' busy (timeout exhausted)", lockKey);
                return null;
            }

            int sleep = Math.Min(pollMs, (int)Math.Ceiling(remaining.TotalMilliseconds));
            await Task.Delay(TimeSpan.FromMilliseconds(sleep), _timeProvider, ct);
            pollMs = Math.Min(pollMs * 2, maxPollMs);
        }
    }

    private async Task<bool> TryAcquireOnceAsync(string key, Guid ownerId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeDb.GetDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now + LeaseDuration;

        // Upsert with conditional update: only steal an existing row if its lease has
        // expired (or it's been soft-deleted). Returns the affected-row count — 1 on
        // successful acquire/steal, 0 when a live holder still owns the lock.
        // Compatible with both Postgres and SQLite (≥ 3.24).
        // Raw SQL bypasses the C# default for Entity<T>.OutboxId (NOT NULL), so we
        // pass a fresh Guid explicitly. The WHERE clause uses table-qualified columns
        // to reference the existing row on conflict, and excluded.* for the proposed
        // values — both Postgres and SQLite (≥ 3.24) accept this form.
        const string sql = @"
INSERT INTO ""DistributedLockLease"" (""Key"", ""OwnerId"", ""AcquiredAtUtc"", ""ExpiresAtUtc"", ""IsDeleted"", ""OutboxId"")
VALUES ({0}, {1}, {2}, {3}, 0, {4})
ON CONFLICT (""Key"") DO UPDATE
SET ""OwnerId"" = excluded.""OwnerId"",
    ""AcquiredAtUtc"" = excluded.""AcquiredAtUtc"",
    ""ExpiresAtUtc"" = excluded.""ExpiresAtUtc"",
    ""IsDeleted"" = 0
WHERE ""DistributedLockLease"".""ExpiresAtUtc"" < excluded.""AcquiredAtUtc""
   OR ""DistributedLockLease"".""IsDeleted"" = 1";

        try
        {
            int rows = await db.Database.ExecuteSqlRawAsync(sql,
                new object[] { key, ownerId, now, expiresAt, Guid.NewGuid() }, ct);
            return rows > 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "LeaseBasedDistributedLock: acquire failed for '{Key}': {Message}",
                key, ex.Message);
            return false;
        }
    }

    private async Task<bool> TryHeartbeatAsync(string key, Guid ownerId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeDb.GetDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now + LeaseDuration;

        const string sql = @"
UPDATE ""DistributedLockLease""
SET ""ExpiresAtUtc"" = {0}
WHERE ""Key"" = {1} AND ""OwnerId"" = {2} AND ""IsDeleted"" = 0";

        try
        {
            int rows = await db.Database.ExecuteSqlRawAsync(sql,
                new object[] { expiresAt, key, ownerId }, ct);
            return rows > 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "LeaseBasedDistributedLock: heartbeat failed for '{Key}' ({OwnerId})", key, ownerId);
            return false;
        }
    }

    private async Task ReleaseAsync(string key, Guid ownerId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeDb.GetDbContext();

        const string sql = @"
DELETE FROM ""DistributedLockLease"" WHERE ""Key"" = {0} AND ""OwnerId"" = {1}";

        try
        {
            int rows = await db.Database.ExecuteSqlRawAsync(sql, new object[] { key, ownerId });
            _logger.LogDebug(
                "LeaseBasedDistributedLock: released '{Key}' ({OwnerId}); rows={Rows}",
                key, ownerId, rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LeaseBasedDistributedLock: release failed for '{Key}' ({OwnerId}); will expire naturally in {LeaseSec}s",
                key, ownerId, LeaseDuration.TotalSeconds);
        }
    }

    private sealed class LeaseHandle : IAsyncDisposable
    {
        private readonly LeaseBasedDistributedLock _parent;
        private readonly string _key;
        private readonly Guid _ownerId;
        private readonly Timer _heartbeatTimer;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        public LeaseHandle(LeaseBasedDistributedLock parent, string key, Guid ownerId)
        {
            _parent = parent;
            _key = key;
            _ownerId = ownerId;

            _heartbeatTimer = new Timer(
                callback: static state => ((LeaseHandle)state!).FireHeartbeat(),
                state: this,
                dueTime: parent.HeartbeatInterval,
                period: parent.HeartbeatInterval);
        }

        private void FireHeartbeat()
        {
            // Fire-and-forget: any heartbeat failure is logged inside TryHeartbeatAsync and
            // the lease will simply expire if the failures continue. We never throw out of
            // the timer callback — that would crash the timer.
            _ = HeartbeatSafelyAsync();
        }

        private async Task HeartbeatSafelyAsync()
        {
            try
            {
                if (_cts.IsCancellationRequested) return;
                await _parent.TryHeartbeatAsync(_key, _ownerId, _cts.Token);
            }
            catch
            {
                // Already logged. Swallow to keep the timer alive.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _cts.Cancel();
                await _heartbeatTimer.DisposeAsync();
            }
            catch
            {
                // best-effort
            }

            await _parent.ReleaseAsync(_key, _ownerId);
            _cts.Dispose();
        }
    }
}
