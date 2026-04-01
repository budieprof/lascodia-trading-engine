using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Singleton state machine managing engine degradation mode transitions.
/// Uses ReaderWriterLockSlim for thread-safe reads (hot path) with infrequent writes.
/// Auto-degrades when subsystem heartbeats go stale beyond configured thresholds.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class DegradationModeManager : IDegradationModeManager
{
    private readonly ILogger<DegradationModeManager> _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private DegradationMode _currentMode = DegradationMode.Normal;

    /// <summary>Subsystem heartbeat timestamps.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

    /// <summary>Staleness thresholds per subsystem (seconds).</summary>
    private readonly ConcurrentDictionary<string, int> _thresholds = new()
    {
        ["MLScorer"] = 120,       // 2 minutes without ML heartbeat -> MLDegraded
        ["EventBus"] = 60,        // 1 minute without event bus -> EventBusDegraded
        ["ReadDb"] = 30,          // 30 seconds without read DB -> ReadDbDegraded
    };

    // Well-known subsystem names
    public const string SubsystemMLScorer = "MLScorer";
    public const string SubsystemEventBus = "EventBus";
    public const string SubsystemReadDb   = "ReadDb";

    public DegradationModeManager(ILogger<DegradationModeManager> logger)
    {
        _logger = logger;
    }

    public DegradationMode CurrentMode
    {
        get
        {
            _lock.EnterReadLock();
            try { return _currentMode; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public Task TransitionToAsync(DegradationMode newMode, string reason, CancellationToken cancellationToken)
    {
        _lock.EnterWriteLock();
        try
        {
            var oldMode = _currentMode;
            if (oldMode == newMode) return Task.CompletedTask;

            _currentMode = newMode;
            _logger.LogWarning(
                "DegradationMode transition: {Old} -> {New}. Reason: {Reason}",
                oldMode, newMode, reason);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public void RecordSubsystemHeartbeat(string subsystemName)
    {
        _heartbeats[subsystemName] = DateTime.UtcNow;

        // If we were degraded due to this subsystem and it's now healthy, attempt recovery
        var mode = CurrentMode;
        if (mode != DegradationMode.Normal && mode != DegradationMode.EmergencyHalt)
        {
            bool allHealthy = _thresholds.Keys.All(IsSubsystemOperational);
            if (allHealthy)
            {
                _ = TransitionToAsync(DegradationMode.Normal, $"All subsystems recovered (triggered by {subsystemName} heartbeat)", CancellationToken.None);
            }
        }
    }

    public bool IsSubsystemOperational(string subsystemName)
    {
        if (!_heartbeats.TryGetValue(subsystemName, out var lastHeartbeat))
            return true; // No heartbeat recorded yet -- assume operational (initial startup)

        if (!_thresholds.TryGetValue(subsystemName, out var thresholdSeconds))
            return true; // No threshold configured -- assume operational

        return (DateTime.UtcNow - lastHeartbeat).TotalSeconds <= thresholdSeconds;
    }
}
