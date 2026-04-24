using Microsoft.Extensions.Diagnostics.HealthChecks;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Health check that reports the aggregate health of all registered background workers.
/// Returns Unhealthy if any worker has stopped or has >10 consecutive failures.
/// Returns Degraded if any worker has >3 consecutive failures.
/// </summary>
public class WorkerHealthCheck : IHealthCheck
{
    private readonly IWorkerHealthMonitor _monitor;

    public WorkerHealthCheck(IWorkerHealthMonitor monitor)
    {
        _monitor = monitor;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshots = _monitor.GetCurrentSnapshots();

        if (snapshots.Count == 0)
            return Task.FromResult(HealthCheckResult.Degraded("No worker health snapshots available"));

        var critical = snapshots
            .Where(s => (!s.IsRunning && !s.IsCompleted) || s.ConsecutiveFailures > 10)
            .Select(s => s.WorkerName)
            .ToList();

        if (critical.Count > 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"{critical.Count} workers unhealthy: {string.Join(", ", critical.Take(10))}"));

        var degraded = snapshots
            .Where(s => s.ConsecutiveFailures > 3)
            .Select(s => s.WorkerName)
            .ToList();

        if (degraded.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{degraded.Count} workers degraded: {string.Join(", ", degraded.Take(10))}"));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{snapshots.Count} workers healthy"));
    }
}
