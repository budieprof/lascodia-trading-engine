using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Infrastructure.Services;

/// <summary>
/// Distributed lock implementation using PostgreSQL session-level advisory locks.
///
/// Advisory locks are lightweight (no table rows, no deadlock detection overhead) and
/// scoped to the database connection. They are ideal for serializing worker operations
/// that must not overlap (e.g. model promotion, position close).
///
/// The string lock key is hashed to a 64-bit integer via SHA256 truncation, which is
/// what PostgreSQL's <c>pg_try_advisory_lock(bigint)</c> expects.
/// </summary>
public sealed class PostgresAdvisoryLock : IDistributedLock
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PostgresAdvisoryLock> _logger;

    public PostgresAdvisoryLock(
        IServiceScopeFactory scopeFactory,
        ILogger<PostgresAdvisoryLock> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default)
    {
        return await TryAcquireCoreAsync(lockKey, timeout: null, ct);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
    {
        return await TryAcquireCoreAsync(lockKey, timeout, ct);
    }

    private async Task<IAsyncDisposable?> TryAcquireCoreAsync(string lockKey, TimeSpan? timeout, CancellationToken ct)
    {
        long lockId = HashToLong(lockKey);

        var scope   = _scopeFactory.CreateAsyncScope();
        var writeDb = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var conn    = (DbConnection)writeDb.GetDbContext().Database.GetDbConnection();

        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        // pg_try_advisory_lock returns true if acquired, false if already held.
        // Session-level lock: held until explicitly released or connection closes.
        bool acquired = await TryLockOnceAsync(conn, lockId, ct);

        // If not acquired and timeout is specified, poll with exponential backoff
        if (!acquired && timeout.HasValue && timeout.Value > TimeSpan.Zero)
        {
            var deadline = DateTime.UtcNow + timeout.Value;
            int pollMs = 50;
            const int maxPollMs = 1000;

            while (!acquired && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                await Task.Delay(Math.Min(pollMs, (int)remaining.TotalMilliseconds), ct);
                acquired = await TryLockOnceAsync(conn, lockId, ct);
                pollMs = Math.Min(pollMs * 2, maxPollMs);
            }
        }

        if (!acquired)
        {
            _logger.LogDebug(
                "Advisory lock not acquired for '{Key}' (id={LockId}) — already held",
                lockKey, lockId);
            await scope.DisposeAsync();
            return null;
        }

        _logger.LogDebug("Advisory lock acquired for '{Key}' (id={LockId})", lockKey, lockId);

        return new LockHandle((DbConnection)conn, lockId, lockKey, scope, _logger);
    }

    private static async Task<bool> TryLockOnceAsync(DbConnection conn, long lockId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT pg_try_advisory_lock({lockId})";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    /// <summary>
    /// Deterministically maps a string key to a 64-bit integer for PostgreSQL advisory locks.
    /// </summary>
    internal static long HashToLong(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly DbConnection  _conn;
        private readonly long          _lockId;
        private readonly string        _lockKey;
        private readonly AsyncServiceScope _scope;
        private readonly ILogger       _logger;
        private bool _released;

        public LockHandle(
            DbConnection conn,
            long lockId,
            string lockKey,
            AsyncServiceScope scope,
            ILogger logger)
        {
            _conn    = conn;
            _lockId  = lockId;
            _lockKey = lockKey;
            _scope   = scope;
            _logger  = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            try
            {
                if (_conn.State == ConnectionState.Open)
                {
                    await using var cmd = _conn.CreateCommand();
                    cmd.CommandText = $"SELECT pg_advisory_unlock({_lockId})";
                    await cmd.ExecuteScalarAsync();
                }

                _logger.LogDebug("Advisory lock released for '{Key}' (id={LockId})", _lockKey, _lockId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to release advisory lock '{Key}' (id={LockId}) — will release on connection close",
                    _lockKey, _lockId);
            }
            finally
            {
                await _scope.DisposeAsync();
            }
        }
    }
}
