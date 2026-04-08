using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Collects and publishes health snapshots from all background workers.
/// Exposes consolidated view via /health/workers endpoint.
/// </summary>
public interface IWorkerHealthMonitor
{
    /// <summary>Records a successful worker cycle with its duration in milliseconds.</summary>
    void RecordCycleSuccess(string workerName, long durationMs);

    /// <summary>Records a failed worker cycle with the error message.</summary>
    void RecordCycleFailure(string workerName, string errorMessage);

    /// <summary>Records the current backlog depth (e.g. pending items) for the worker.</summary>
    void RecordBacklogDepth(string workerName, int depth);

    /// <summary>
    /// Records lightweight liveness for long-running workers that do not have a natural cycle boundary.
    /// </summary>
    void RecordWorkerHeartbeat(string workerName);

    /// <summary>Returns a snapshot of health status for all monitored workers.</summary>
    IReadOnlyList<WorkerHealthSnapshot> GetCurrentSnapshots();

    /// <summary>Persists current health snapshots to the database for historical tracking.</summary>
    Task PersistSnapshotsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records static metadata about a worker for observability queries.
    /// </summary>
    void RecordWorkerMetadata(string workerName, string? purpose, TimeSpan expectedInterval);

    /// <summary>Records that a worker has stopped execution (normal shutdown or crash).</summary>
    void RecordWorkerStopped(string workerName, string? errorMessage = null);
}
