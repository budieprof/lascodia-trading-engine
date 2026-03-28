using System.Threading;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Common.WorkerGroups;

/// <summary>
/// Bulkhead isolation for worker thread pools.
/// Prevents ML training/monitoring workers from starving trading-critical workers
/// by limiting how many concurrent CPU-bound tasks each group can run.
///
/// Usage in workers:
///   await WorkerBulkhead.MLTraining.WaitAsync(cancellationToken);
///   try { ... CPU-bound work ... }
///   finally { WorkerBulkhead.MLTraining.Release(); }
///
/// Trading-critical workers (SignalOrderBridge, PositionWorker, etc.) use their
/// own semaphores and are unaffected by ML saturation.
/// </summary>
public static class WorkerBulkhead
{
    // ── Trading-critical: generous allocation (rarely saturated) ─────────
    // SignalOrderBridgeWorker already has its own SemaphoreSlim(16).
    // This is a global backstop for all other trading workers combined.
    public static readonly SemaphoreSlim CoreTrading = new(
        initialCount: 32,
        maxCount: 32);

    // ── ML Training: bounded to prevent thread pool starvation ──────────
    // ML training is CPU-heavy (TCN forward pass, tree construction).
    // Limit to ProcessorCount to leave threads for trading + HTTP.
    public static readonly SemaphoreSlim MLTraining = new(
        initialCount: Math.Max(Environment.ProcessorCount, 4),
        maxCount: Math.Max(Environment.ProcessorCount, 4));

    // ── ML Monitoring: lighter than training but 58 workers compete ─────
    // Most monitoring workers do DB reads + light math. Limit concurrency
    // to prevent connection pool exhaustion from 58 simultaneous queries.
    public static readonly SemaphoreSlim MLMonitoring = new(
        initialCount: Math.Max(Environment.ProcessorCount * 2, 8),
        maxCount: Math.Max(Environment.ProcessorCount * 2, 8));

    // ── Backtesting: isolated from trading, can use remaining capacity ──
    public static readonly SemaphoreSlim Backtesting = new(
        initialCount: Math.Max(Environment.ProcessorCount / 2, 2),
        maxCount: Math.Max(Environment.ProcessorCount / 2, 2));

    /// <summary>
    /// Get the appropriate bulkhead for a worker type.
    /// Returns null if the worker should not be throttled (e.g., alerts).
    /// </summary>
    public static SemaphoreSlim? ForWorkerType(string groupName) => groupName switch
    {
        "CoreTrading" => CoreTrading,
        "MLTraining" => MLTraining,
        "MLMonitoring" => MLMonitoring,
        "Backtesting" => Backtesting,
        _ => null
    };

    /// <summary>
    /// Snapshot of current semaphore availability for diagnostics.
    /// </summary>
    public static (int CoreAvailable, int MLTrainAvailable, int MLMonAvailable, int BacktestAvailable) GetAvailability()
        => (CoreTrading.CurrentCount, MLTraining.CurrentCount, MLMonitoring.CurrentCount, Backtesting.CurrentCount);
}
