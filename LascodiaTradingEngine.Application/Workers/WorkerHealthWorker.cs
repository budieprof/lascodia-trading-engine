using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Interfaces;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Periodically persists worker health snapshots from the in-memory monitor to the database.
/// Alerts when any worker's last success exceeds 2x its configured interval.
/// </summary>
public class WorkerHealthWorker : BackgroundService
{
    private readonly ILogger<WorkerHealthWorker> _logger;
    private readonly IWorkerHealthMonitor _healthMonitor;
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);
    private int _consecutiveFailures;

    public WorkerHealthWorker(
        ILogger<WorkerHealthWorker> logger,
        IWorkerHealthMonitor healthMonitor)
    {
        _logger        = logger;
        _healthMonitor = healthMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkerHealthWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _healthMonitor.PersistSnapshotsAsync(stoppingToken);
                CheckForStaleWorkers();
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _logger.LogError(ex, "WorkerHealthWorker error (failure #{Count})", _consecutiveFailures);
            }

            await Task.Delay(PersistInterval, stoppingToken);
        }
    }

    private void CheckForStaleWorkers()
    {
        var snapshots = _healthMonitor.GetCurrentSnapshots();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.LastSuccessAt.HasValue && snapshot.ConfiguredIntervalSeconds > 0)
            {
                var staleness = DateTime.UtcNow - snapshot.LastSuccessAt.Value;
                var threshold = TimeSpan.FromSeconds(snapshot.ConfiguredIntervalSeconds * 2);

                if (staleness > threshold)
                {
                    _logger.LogWarning(
                        "Worker {Name} is stale: last success {Ago:F0}s ago (threshold={Threshold:F0}s)",
                        snapshot.WorkerName, staleness.TotalSeconds, threshold.TotalSeconds);
                }
            }

            if (snapshot.ConsecutiveFailures >= 3)
            {
                _logger.LogWarning(
                    "Worker {Name} has {Failures} consecutive failures. Last error: {Error}",
                    snapshot.WorkerName, snapshot.ConsecutiveFailures, snapshot.LastErrorMessage);
            }
        }
    }
}
