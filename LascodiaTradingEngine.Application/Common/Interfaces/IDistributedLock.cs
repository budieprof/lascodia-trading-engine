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
}
