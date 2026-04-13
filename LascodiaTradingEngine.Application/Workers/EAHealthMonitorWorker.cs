using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
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
    private readonly IDegradationModeManager _degradationManager;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly ILogger<EAHealthMonitorWorker> _logger;
    private readonly TradingMetrics _metrics;

    /// <summary>Tracks whether we were in an all-disconnected state on the previous cycle
    /// to avoid repeated transitions and alert spam.</summary>
    private bool _previousAllDisconnected;

    public EAHealthMonitorWorker(
        IServiceScopeFactory scopeFactory,
        IDegradationModeManager degradationManager,
        IAlertDispatcher alertDispatcher,
        ILogger<EAHealthMonitorWorker> logger,
        TradingMetrics metrics)
    {
        _scopeFactory        = scopeFactory;
        _degradationManager  = degradationManager;
        _alertDispatcher     = alertDispatcher;
        _logger              = logger;
        _metrics             = metrics;
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

        // Load active standby instances for potential symbol reassignment
        var activeInstances = await db.Set<EAInstance>()
            .Where(e => e.Status == EAInstanceStatus.Active
                     && !e.IsDeleted
                     && !staleInstances.Select(s => s.Id).Contains(e.Id))
            .ToListAsync(ct);

        var reassignedSymbols = new List<(string Symbol, string FromInstanceId, string ToInstanceId)>();

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

            // Auto-reassign orphaned symbols to active standby instances
            var orphanedSymbols = (instance.Symbols ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var sym in orphanedSymbols)
            {
                // Find an active instance that doesn't already own this symbol
                var candidate = activeInstances.FirstOrDefault(s =>
                    s.Symbols is null || !s.Symbols.Split(',').Contains(sym, StringComparer.OrdinalIgnoreCase));

                if (candidate is not null)
                {
                    // Assign symbol to standby instance
                    candidate.Symbols = string.IsNullOrWhiteSpace(candidate.Symbols)
                        ? sym
                        : $"{candidate.Symbols},{sym}";

                    reassignedSymbols.Add((sym, instance.InstanceId, candidate.InstanceId));

                    _logger.LogWarning(
                        "EAHealthMonitor: auto-assigned orphaned symbol {Symbol} from disconnected {DisconnectedId} to standby {StandbyId}",
                        sym, instance.InstanceId, candidate.InstanceId);
                }
                else
                {
                    _logger.LogWarning(
                        "EAHealthMonitor: no standby instance available for orphaned symbol {Symbol} — manual intervention required",
                        sym);
                }
            }
        }

        var eventBus = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();

        // Publish disconnect events for each stale instance before saving
        foreach (var instance in staleInstances)
        {
            var instanceReassigned = reassignedSymbols
                .Where(r => r.FromInstanceId == instance.InstanceId)
                .Select(r => r.Symbol)
                .ToList();

            await eventBus.SaveAndPublish(writeContext, new EAInstanceDisconnectedIntegrationEvent
            {
                EAInstanceId     = instance.Id,
                InstanceId       = instance.InstanceId,
                TradingAccountId = instance.TradingAccountId,
                OrphanedSymbols  = instance.Symbols ?? string.Empty,
                ReassignedSymbols = string.Join(",", instanceReassigned),
                DetectedAt       = DateTime.UtcNow,
            });
        }

        _logger.LogInformation(
            "EAHealthMonitorWorker: transitioned {Count} stale EA instance(s) to Disconnected, reassigned {ReassignedCount} symbol(s)",
            staleInstances.Count, reassignedSymbols.Count);

        foreach (var (symbol, fromId, toId) in reassignedSymbols)
        {
            _logger.LogInformation(
                "EAHealthMonitor: symbol {Symbol} reassigned from {FromInstance} to {ToInstance}",
                symbol, fromId, toId);
        }

        // ── All-disconnect detection: if every EA instance is now disconnected,
        // transition the engine to DataUnavailable mode and dispatch a critical alert.
        var remainingActiveCount = await db.Set<EAInstance>()
            .CountAsync(e => e.Status == EAInstanceStatus.Active && !e.IsDeleted, ct);

        if (remainingActiveCount == 0 && !_previousAllDisconnected)
        {
            _previousAllDisconnected = true;
            _logger.LogCritical(
                "EAHealthMonitorWorker: ALL EA instances disconnected — entering DataUnavailable mode. " +
                "Signal generation will be blocked until at least one instance reconnects.");

            await _degradationManager.TransitionToAsync(
                DegradationMode.DataUnavailable,
                "All EA instances disconnected — no market data source available",
                ct);

            // Dispatch critical alert for all-disconnect
            try
            {
                var allDisconnectAlert = new Alert
                {
                    AlertType     = AlertType.EADisconnected,
                    Severity      = AlertSeverity.Critical,
                    IsActive      = true,
                    ConditionJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Source = "EAHealthMonitorWorker",
                        Event  = "AllInstancesDisconnected",
                        DisconnectedCount = staleInstances.Count,
                        DetectedAt = DateTime.UtcNow
                    })
                };
                db.Set<Alert>().Add(allDisconnectAlert);
                await db.SaveChangesAsync(ct);

                await _alertDispatcher.DispatchAsync(
                    allDisconnectAlert,
                    "ALL EA instances disconnected — engine entering DataUnavailable mode. " +
                    "No market data source available. Signal generation is blocked.",
                    ct);
            }
            catch (Exception alertEx)
            {
                _logger.LogError(alertEx,
                    "EAHealthMonitor: failed to dispatch all-disconnect alert");
            }
        }
        else if (remainingActiveCount > 0 && _previousAllDisconnected)
        {
            // At least one instance recovered — clear the all-disconnect flag.
            // DegradationModeManager auto-recovers via subsystem heartbeats,
            // so no explicit transition back to Normal is needed here.
            _previousAllDisconnected = false;
            _logger.LogInformation(
                "EAHealthMonitorWorker: EA instance(s) recovered — {ActiveCount} active instance(s) available",
                remainingActiveCount);
        }
    }
}
