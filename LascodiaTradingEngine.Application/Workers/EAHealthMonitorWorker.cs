using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors EA instance heartbeats and transitions stale instances to <see cref="EAInstanceStatus.Disconnected"/>.
/// Runs every 30 seconds. If no heartbeat is received from an instance for 60 seconds, it is marked
/// as disconnected so that <see cref="StrategyWorker"/> stops evaluating strategies on those symbols.
/// </summary>
public class EAHealthMonitorWorker : BackgroundService
{
    private const int PollIntervalSeconds = 30;
    private const int HeartbeatTimeoutSeconds = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EAHealthMonitorWorker> _logger;
    private readonly TradingMetrics _metrics;

    public EAHealthMonitorWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EAHealthMonitorWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _metrics      = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EAHealthMonitorWorker starting (poll={Poll}s, heartbeatTimeout={Timeout}s)",
            PollIntervalSeconds, HeartbeatTimeoutSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckHeartbeatsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EAHealthMonitorWorker: error during heartbeat check");
                _metrics.WorkerErrors.Add(1,
                    new KeyValuePair<string, object?>("worker", "EAHealthMonitor"),
                    new KeyValuePair<string, object?>("reason", "unhandled"));
            }

            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("EAHealthMonitorWorker stopped");
    }

    private async Task CheckHeartbeatsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var db = writeContext.GetDbContext();

        var heartbeatCutoff = DateTime.UtcNow.AddSeconds(-HeartbeatTimeoutSeconds);

        // Find active instances whose heartbeat is stale
        var staleInstances = await db.Set<EAInstance>()
            .Where(e => e.Status == EAInstanceStatus.Active
                     && !e.IsDeleted
                     && e.LastHeartbeat < heartbeatCutoff)
            .ToListAsync(ct);

        if (staleInstances.Count == 0)
            return;

        foreach (var instance in staleInstances)
        {
            instance.Status = EAInstanceStatus.Disconnected;

            _logger.LogWarning(
                "EAHealthMonitorWorker: instance {InstanceId} heartbeat stale ({Age:F0}s > {Threshold}s) — marking Disconnected. Symbols: {Symbols}",
                instance.InstanceId,
                (DateTime.UtcNow - instance.LastHeartbeat).TotalSeconds,
                HeartbeatTimeoutSeconds,
                instance.Symbols);

            _metrics.WorkerErrors.Add(1,
                new KeyValuePair<string, object?>("worker", "EAHealthMonitor"),
                new KeyValuePair<string, object?>("reason", "instance_disconnected"));
        }

        await writeContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "EAHealthMonitorWorker: transitioned {Count} stale EA instance(s) to Disconnected",
            staleInstances.Count);
    }
}
