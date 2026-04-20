namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Lightweight distributed lock for serializing concurrent worker operations.
/// Lock keys are scoped (e.g. by symbol + timeframe) so unrelated operations
/// run in parallel.
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Attempts to acquire a lock for the given key. Returns a disposable handle
    /// if successful, or <c>null</c> if the lock is already held.
    /// The lock is automatically released when the handle is disposed.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, CancellationToken ct = default);

    /// <summary>
    /// Attempts to acquire a lock for the given key, waiting up to <paramref name="timeout"/>.
    /// Returns a disposable handle if successful, or <c>null</c> if the timeout elapsed.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout, CancellationToken ct = default)
        => TryAcquireAsync(lockKey, ct);

    /// <summary>
    /// Struct-keyed overload for high-frequency callers that don't want to
    /// allocate a fresh string per acquisition (e.g. per-tick parallel
    /// strategy evaluation). The backing implementation uses the 64-bit key
    /// directly — PostgreSQL advisory locks natively take a <c>bigint</c> — so
    /// the string-to-hash roundtrip is skipped.
    /// <para>
    /// Callers must namespace their lock IDs so they do not collide with the
    /// SHA256-derived IDs used by the string overload. The recommended pattern
    /// is to OR a well-known 48-bit prefix into the lower half of the entity
    /// ID, e.g. <c>((long)0x5374_5261_7400 &lt;&lt; 16) | strategyId</c>.
    /// </para>
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(long lockId, TimeSpan timeout, CancellationToken ct = default)
        => TryAcquireAsync($"lock:{lockId:X}", timeout, ct);
}
