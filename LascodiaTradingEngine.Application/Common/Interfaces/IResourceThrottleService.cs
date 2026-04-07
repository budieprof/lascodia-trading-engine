namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Shared resource throttle for CPU-intensive operations (backtests, ML training, optimization).
/// Prevents concurrent CPU-bound workers from overwhelming the host by limiting total concurrent
/// slots to a configurable maximum (default: <c>Environment.ProcessorCount - 2</c>).
/// </summary>
public interface IResourceThrottleService
{
    /// <summary>
    /// Attempts to acquire a CPU slot. Returns a disposable handle if successful,
    /// or <c>null</c> if all slots are occupied. The slot is released when the handle is disposed.
    /// </summary>
    Task<IDisposable?> TryAcquireCpuSlotAsync(string workerName, CancellationToken ct = default);

    /// <summary>Current number of slots in use.</summary>
    int ActiveSlots { get; }

    /// <summary>Maximum configured slots.</summary>
    int MaxSlots { get; }
}
