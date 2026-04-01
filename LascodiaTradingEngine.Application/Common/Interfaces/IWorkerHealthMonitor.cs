using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Collects and publishes health snapshots from all background workers.
/// Exposes consolidated view via /health/workers endpoint.
/// </summary>
public interface IWorkerHealthMonitor
{
    void RecordCycleSuccess(string workerName, long durationMs);
    void RecordCycleFailure(string workerName, string errorMessage);
    void RecordBacklogDepth(string workerName, int depth);
    IReadOnlyList<WorkerHealthSnapshot> GetCurrentSnapshots();
    Task PersistSnapshotsAsync(CancellationToken cancellationToken);
}
