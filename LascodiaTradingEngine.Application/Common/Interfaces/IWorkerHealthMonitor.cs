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

    /// <summary>Returns a snapshot of health status for all monitored workers.</summary>
    IReadOnlyList<WorkerHealthSnapshot> GetCurrentSnapshots();

    /// <summary>Persists current health snapshots to the database for historical tracking.</summary>
    Task PersistSnapshotsAsync(CancellationToken cancellationToken);
}
