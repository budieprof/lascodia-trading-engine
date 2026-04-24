using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Services;
using LascodiaTradingEngine.Application.Services.Alerts;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Workers;

/// <summary>
/// Monitors EA instance heartbeats and transitions stale instances to <see cref="EAInstanceStatus.Disconnected"/>.
/// Polls every 10 seconds so the status lag behind actual staleness is &lt;= 10 seconds. Consumers should still
/// prefer heartbeat-fresh queries when gating signal emission to avoid the residual poll-latency window.
/// </summary>
public sealed class EAHealthMonitorWorker : BackgroundService
{
    internal const string WorkerName = nameof(EAHealthMonitorWorker);

    private const int PollIntervalSeconds = 10;
    private const int HeartbeatTimeoutSeconds = 60;
    private const int MaxBackoffSeconds = 60;
    private const string DistributedLockKey = "workers:ea-health-monitor:cycle";
    private const string AllDisconnectedAlertDeduplicationKey = "EAHealthMonitor:NoActiveInstances";

    /// <summary>
    /// Soft-stale threshold used by consumers via IsHeartbeatFresh helpers. Within
    /// this window status is still Active but new-signal emission should pause.
    /// Set to the EA heartbeat interval (30 seconds) so a single missed heartbeat
    /// trips the gate while two misses hit the 60 second hard-disconnect.
    /// </summary>
    public const int HeartbeatSoftStaleSeconds = 30;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(PollIntervalSeconds);
    private static readonly TimeSpan DistributedLockTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDegradationModeManager _degradationManager;
    private readonly IAlertDispatcher _alertDispatcher;
    private readonly ILogger<EAHealthMonitorWorker> _logger;
    private readonly TradingMetrics? _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkerHealthMonitor? _healthMonitor;
    private readonly IDistributedLock? _distributedLock;

    private int _consecutiveFailures;
    private bool _previousNoActiveInstances;
    private bool _missingDistributedLockWarningEmitted;

    public EAHealthMonitorWorker(
        IServiceScopeFactory scopeFactory,
        IDegradationModeManager degradationManager,
        IAlertDispatcher alertDispatcher,
        ILogger<EAHealthMonitorWorker> logger,
        TradingMetrics? metrics = null,
        TimeProvider? timeProvider = null,
        IWorkerHealthMonitor? healthMonitor = null,
        IDistributedLock? distributedLock = null)
    {
        _scopeFactory = scopeFactory;
        _degradationManager = degradationManager;
        _alertDispatcher = alertDispatcher;
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthMonitor = healthMonitor;
        _distributedLock = distributedLock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{Worker} starting (poll={Poll}s, heartbeatTimeout={Timeout}s)",
            WorkerName,
            PollIntervalSeconds,
            HeartbeatTimeoutSeconds);

        _healthMonitor?.RecordWorkerMetadata(
            WorkerName,
            "Marks stale EA instances disconnected, fails over symbols/coordinator ownership within the same trading account, and drives no-active-EA degradation state.",
            PollInterval);

        try
        {
            try
            {
                var initialDelay = WorkerStartupSequencer.GetDelay(WorkerName);
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                long cycleStarted = Stopwatch.GetTimestamp();

                try
                {
                    _healthMonitor?.RecordWorkerHeartbeat(WorkerName);

                    var result = await RunCycleAsync(stoppingToken);
                    long durationMs = (long)Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;

                    _healthMonitor?.RecordBacklogDepth(WorkerName, result.StaleInstanceCount);
                    _healthMonitor?.RecordCycleSuccess(WorkerName, durationMs);
                    _metrics?.WorkerCycleDurationMs.Record(
                        durationMs,
                        new KeyValuePair<string, object?>("worker", WorkerName));

                    if (result.SkippedReason is { Length: > 0 })
                    {
                        _logger.LogDebug(
                            "{Worker}: cycle skipped ({Reason}).",
                            WorkerName,
                            result.SkippedReason);
                    }
                    else
                    {
                        if (result.StaleInstanceCount > 0)
                        {
                            _logger.LogWarning(
                                "{Worker}: disconnected {Count} stale EA instance(s), reassigned {ReassignedCount} symbol(s), and failed over {CoordinatorFailovers} coordinator role(s).",
                                WorkerName,
                                result.StaleInstanceCount,
                                result.ReassignedSymbolCount,
                                result.CoordinatorFailoverCount);
                        }

                        if (result.EnteredNoActiveState)
                        {
                            _logger.LogCritical(
                                "{Worker}: no active EA instances available — engine entered DataUnavailable mode.",
                                WorkerName);
                        }
                        else if (result.RecoveredActiveState)
                        {
                            _logger.LogInformation(
                                "{Worker}: EA connectivity recovered — {ActiveCount} active instance(s) available.",
                                WorkerName,
                                result.ActiveInstanceCount);
                        }
                    }

                    if (_consecutiveFailures > 0)
                    {
                        _healthMonitor?.RecordRecovery(WorkerName, _consecutiveFailures);
                        _logger.LogInformation(
                            "{Worker}: recovered after {Failures} consecutive failure(s).",
                            WorkerName,
                            _consecutiveFailures);
                    }

                    _consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _healthMonitor?.RecordRetry(WorkerName);
                    _healthMonitor?.RecordCycleFailure(WorkerName, ex.Message);
                    _metrics?.WorkerErrors.Add(
                        1,
                        new KeyValuePair<string, object?>("worker", WorkerName),
                        new KeyValuePair<string, object?>("reason", "ea_health_monitor_cycle"));
                    _logger.LogError(ex, "{Worker}: heartbeat-monitor cycle failed.", WorkerName);
                }

                try
                {
                    await Task.Delay(CalculateDelay(_consecutiveFailures), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _healthMonitor?.RecordWorkerStopped(WorkerName);
            _logger.LogInformation("{Worker} stopped", WorkerName);
        }
    }

    internal static TimeSpan CalculateDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return PollInterval;

        double delaySeconds = Math.Min(
            PollIntervalSeconds * Math.Pow(2, consecutiveFailures - 1),
            MaxBackoffSeconds);

        return TimeSpan.FromSeconds(delaySeconds);
    }

    internal async Task<EAHealthMonitorCycleResult> RunCycleAsync(CancellationToken ct)
    {
        if (_distributedLock is null)
        {
            if (!_missingDistributedLockWarningEmitted)
            {
                _logger.LogWarning(
                    "{Worker} running without IDistributedLock; duplicate disconnect failover is possible in multi-instance deployments.",
                    WorkerName);
                _missingDistributedLockWarningEmitted = true;
            }

            return await RunCycleCoreAsync(ct);
        }

        var cycleLock = await _distributedLock.TryAcquireAsync(DistributedLockKey, DistributedLockTimeout, ct);
        if (cycleLock is null)
            return EAHealthMonitorCycleResult.Skipped("lock_busy");

        await using (cycleLock)
        {
            return await RunCycleCoreAsync(ct);
        }
    }

    private async Task<EAHealthMonitorCycleResult> RunCycleCoreAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IIntegrationEventService>();
        var db = writeContext.GetDbContext();

        var now = _timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;
        var heartbeatCutoff = nowUtc.AddSeconds(-HeartbeatTimeoutSeconds);

        var activeInstances = await db.Set<EAInstance>()
            .Where(e => e.Status == EAInstanceStatus.Active && !e.IsDeleted)
            .OrderByDescending(e => e.LastHeartbeat)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var staleInstances = activeInstances
            .Where(e => e.LastHeartbeat < heartbeatCutoff)
            .ToList();

        int coordinatorFailovers = 0;
        int reassignedSymbolCount = 0;
        var staleIds = staleInstances.Select(x => x.Id).ToHashSet();
        var disconnectEvents = new List<EAInstanceDisconnectedIntegrationEvent>(staleInstances.Count);
        var activeSymbolMap = activeInstances.ToDictionary(
            instance => instance.Id,
            instance => new HashSet<string>(ParseSymbols(instance.Symbols), StringComparer.OrdinalIgnoreCase));

        foreach (var instance in staleInstances)
        {
            var originalSymbols = ParseSymbols(instance.Symbols);
            var remainingSymbols = new List<string>(originalSymbols.Count);
            var reassignedSymbols = new List<string>(originalSymbols.Count);

            var standbyCandidates = activeInstances
                .Where(e => e.TradingAccountId == instance.TradingAccountId
                         && e.Id != instance.Id
                         && !staleIds.Contains(e.Id))
                .OrderByDescending(e => e.LastHeartbeat)
                .ThenBy(e => e.Id)
                .ToList();

            if (instance.IsCoordinator)
            {
                var coordinatorCandidate = standbyCandidates.FirstOrDefault(e => e.IsCoordinator)
                    ?? standbyCandidates.FirstOrDefault();

                if (coordinatorCandidate is not null)
                {
                    if (!coordinatorCandidate.IsCoordinator)
                        coordinatorFailovers++;

                    coordinatorCandidate.IsCoordinator = true;
                    instance.IsCoordinator = false;
                }
            }

            foreach (var symbol in originalSymbols)
            {
                var candidate = standbyCandidates.FirstOrDefault(e => !activeSymbolMap[e.Id].Contains(symbol));
                if (candidate is null)
                {
                    remainingSymbols.Add(symbol);
                    continue;
                }

                if (activeSymbolMap[candidate.Id].Add(symbol))
                {
                    reassignedSymbols.Add(symbol);
                    reassignedSymbolCount++;
                }
                else
                {
                    remainingSymbols.Add(symbol);
                }
            }

            instance.Status = EAInstanceStatus.Disconnected;
            instance.Symbols = FormatSymbols(remainingSymbols);

            double heartbeatAgeSeconds = Math.Max(0, (nowUtc - instance.LastHeartbeat).TotalSeconds);
            _metrics?.EaDisconnectedHeartbeatAgeSeconds.Record(heartbeatAgeSeconds);

            _logger.LogWarning(
                "{Worker}: instance {InstanceId} heartbeat stale ({Age:F0}s > {Threshold}s) — marking Disconnected. OriginalSymbols={Symbols}, ReassignedSymbols={ReassignedSymbols}, RemainingSymbols={RemainingSymbols}",
                WorkerName,
                instance.InstanceId,
                heartbeatAgeSeconds,
                HeartbeatTimeoutSeconds,
                FormatSymbols(originalSymbols),
                FormatSymbols(reassignedSymbols),
                instance.Symbols);

            disconnectEvents.Add(new EAInstanceDisconnectedIntegrationEvent
            {
                EAInstanceId = instance.Id,
                InstanceId = instance.InstanceId,
                TradingAccountId = instance.TradingAccountId,
                OrphanedSymbols = FormatSymbols(originalSymbols),
                ReassignedSymbols = FormatSymbols(reassignedSymbols),
                DetectedAt = nowUtc,
            });
        }

        foreach (var candidate in activeInstances.Where(e => !staleIds.Contains(e.Id)))
        {
            candidate.Symbols = FormatSymbols(activeSymbolMap[candidate.Id]);
        }

        foreach (var disconnectEvent in disconnectEvents)
        {
            await eventBus.SaveAndPublish(writeContext, disconnectEvent);
        }

        int activeInstanceCount = activeInstances.Count - staleInstances.Count;
        bool hadNoActiveInstances = activeInstanceCount == 0;
        var availability = await SynchronizeAvailabilityStateAsync(
            writeContext,
            db,
            activeInstanceCount,
            staleInstances.Count,
            nowUtc,
            ct);

        _previousNoActiveInstances = hadNoActiveInstances;

        _metrics?.EaActiveInstanceCount.Record(activeInstanceCount);
        _metrics?.EaStaleInstanceCount.Record(staleInstances.Count);
        if (staleInstances.Count > 0)
            _metrics?.EaInstancesDisconnected.Add(staleInstances.Count);
        if (reassignedSymbolCount > 0)
            _metrics?.EaSymbolsReassigned.Add(reassignedSymbolCount);
        if (coordinatorFailovers > 0)
            _metrics?.EaCoordinatorFailovers.Add(coordinatorFailovers);
        if (availability.EnteredNoActiveState)
        {
            _metrics?.EaAvailabilityTransitions.Add(
                1,
                new KeyValuePair<string, object?>("transition", "enter"));
        }
        else if (availability.RecoveredActiveState)
        {
            _metrics?.EaAvailabilityTransitions.Add(
                1,
                new KeyValuePair<string, object?>("transition", "recover"));
        }

        return new EAHealthMonitorCycleResult(
            staleInstances.Count,
            reassignedSymbolCount,
            coordinatorFailovers,
            activeInstanceCount,
            availability.EnteredNoActiveState,
            availability.RecoveredActiveState,
            null);
    }

    private async Task<EAAvailabilityStateResult> SynchronizeAvailabilityStateAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        int activeInstanceCount,
        int newlyDisconnectedCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        bool noActiveInstances = activeInstanceCount == 0;
        bool wasDataUnavailable = _degradationManager.CurrentMode == DegradationMode.DataUnavailable;

        if (noActiveInstances)
        {
            if (!wasDataUnavailable)
            {
                await _degradationManager.TransitionToAsync(
                    DegradationMode.DataUnavailable,
                    "No active EA instances available — no market data source is currently online.",
                    ct);
            }

            await UpsertNoActiveInstancesAlertAsync(
                writeContext,
                db,
                newlyDisconnectedCount,
                nowUtc,
                ct);

            return new EAAvailabilityStateResult(
                EnteredNoActiveState: !wasDataUnavailable,
                RecoveredActiveState: false);
        }

        bool alertResolved = await ResolveNoActiveInstancesAlertAsync(writeContext, db, nowUtc, ct);

        if (wasDataUnavailable)
        {
            await _degradationManager.TransitionToAsync(
                DegradationMode.Normal,
                $"EA connectivity recovered — {activeInstanceCount} active EA instance(s) available.",
                ct);
        }

        return new EAAvailabilityStateResult(
            EnteredNoActiveState: false,
            RecoveredActiveState: wasDataUnavailable || alertResolved || _previousNoActiveInstances);
    }

    private async Task UpsertNoActiveInstancesAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        int disconnectedCount,
        DateTime nowUtc,
        CancellationToken ct)
    {
        int cooldownSeconds = await AlertCooldownDefaults.GetCooldownAsync(
            db,
            AlertCooldownDefaults.CK_Infrastructure,
            AlertCooldownDefaults.Default_Infrastructure,
            ct);

        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(
                a => !a.IsDeleted
                  && a.IsActive
                  && a.DeduplicationKey == AllDisconnectedAlertDeduplicationKey,
                ct);

        if (alert is null)
        {
            alert = new Alert
            {
                AlertType = AlertType.EADisconnected,
                DeduplicationKey = AllDisconnectedAlertDeduplicationKey,
                IsActive = true,
            };
            db.Set<Alert>().Add(alert);
        }

        alert.Severity = AlertSeverity.Critical;
        alert.CooldownSeconds = cooldownSeconds;
        alert.AutoResolvedAt = null;
        alert.ConditionJson = JsonSerializer.Serialize(new
        {
            Source = WorkerName,
            Event = "NoActiveInstances",
            NewlyDisconnectedCount = disconnectedCount,
            DetectedAt = nowUtc
        });

        await writeContext.SaveChangesAsync(ct);

        if (alert.LastTriggeredAt.HasValue
            && nowUtc - NormalizeUtc(alert.LastTriggeredAt.Value) < TimeSpan.FromSeconds(cooldownSeconds))
        {
            return;
        }

        try
        {
            await _alertDispatcher.DispatchAsync(
                alert,
                "No active EA instances available — engine entering DataUnavailable mode. Market-data-driven execution is paused until at least one instance recovers.",
                ct);
            await writeContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Worker}: failed to dispatch no-active-EA alert.", WorkerName);
        }
    }

    private async Task<bool> ResolveNoActiveInstancesAlertAsync(
        IWriteApplicationDbContext writeContext,
        DbContext db,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var alert = await db.Set<Alert>()
            .FirstOrDefaultAsync(
                a => !a.IsDeleted
                  && a.IsActive
                  && a.DeduplicationKey == AllDisconnectedAlertDeduplicationKey,
                ct);

        if (alert is null)
            return false;

        try
        {
            await _alertDispatcher.TryAutoResolveAsync(alert, conditionStillActive: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "{Worker}: failed to dispatch auto-resolve notification for no-active-EA alert.",
                WorkerName);
        }

        alert.IsActive = false;
        alert.AutoResolvedAt ??= nowUtc;
        await writeContext.SaveChangesAsync(ct);
        return true;
    }

    private static List<string> ParseSymbols(string? symbols)
        => (symbols ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(symbol => symbol.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FormatSymbols(IEnumerable<string> symbols)
        => string.Join(",", symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.Ordinal));

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal readonly record struct EAHealthMonitorCycleResult(
    int StaleInstanceCount,
    int ReassignedSymbolCount,
    int CoordinatorFailoverCount,
    int ActiveInstanceCount,
    bool EnteredNoActiveState,
    bool RecoveredActiveState,
    string? SkippedReason)
{
    public static EAHealthMonitorCycleResult Skipped(string reason)
        => new(
            StaleInstanceCount: 0,
            ReassignedSymbolCount: 0,
            CoordinatorFailoverCount: 0,
            ActiveInstanceCount: 0,
            EnteredNoActiveState: false,
            RecoveredActiveState: false,
            SkippedReason: reason);
}

internal readonly record struct EAAvailabilityStateResult(
    bool EnteredNoActiveState,
    bool RecoveredActiveState);
