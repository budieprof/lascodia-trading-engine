using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Singleton state machine managing engine degradation mode transitions.
/// Uses ReaderWriterLockSlim for thread-safe reads (hot path) with infrequent writes.
/// Auto-degrades when subsystem heartbeats go stale beyond configured thresholds.
/// Thresholds are configurable via EngineConfig and mode is persisted across restarts.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class DegradationModeManager : IDegradationModeManager
{
    private readonly ILogger<DegradationModeManager> _logger;
    private readonly TradingMetrics _metrics;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile DegradationMode _currentMode = DegradationMode.Normal;

    /// <summary>Subsystem heartbeat timestamps.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

    /// <summary>
    /// Staleness thresholds per subsystem (seconds). Defaults are used until
    /// <see cref="UpdateThresholdsFromConfig"/> is called with values from EngineConfig.
    /// </summary>
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

    // EngineConfig keys
    private const string CK_MLScorerTimeout   = "Degradation:MLScorerTimeoutSeconds";
    private const string CK_EventBusTimeout   = "Degradation:EventBusTimeoutSeconds";
    private const string CK_ReadDbTimeout     = "Degradation:ReadDbTimeoutSeconds";
    private const string CK_ActiveMode        = "Degradation:ActiveMode";

    public DegradationModeManager(ILogger<DegradationModeManager> logger, TradingMetrics metrics)
    {
        _logger  = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Updates thresholds from EngineConfig values. Called by WorkerHealthWorker on each cycle.
    /// Also restores persisted mode on first call (startup recovery).
    /// </summary>
    public void UpdateThresholdsFromConfig(
        int? mlScorerTimeout = null,
        int? eventBusTimeout = null,
        int? readDbTimeout = null,
        string? persistedMode = null)
    {
        if (mlScorerTimeout.HasValue) _thresholds[SubsystemMLScorer] = mlScorerTimeout.Value;
        if (eventBusTimeout.HasValue) _thresholds[SubsystemEventBus] = eventBusTimeout.Value;
        if (readDbTimeout.HasValue)   _thresholds[SubsystemReadDb]   = readDbTimeout.Value;

        // Restore persisted mode on startup (only if current mode is still Normal)
        if (persistedMode is not null
            && _currentMode == DegradationMode.Normal
            && Enum.TryParse<DegradationMode>(persistedMode, ignoreCase: true, out var restored)
            && restored != DegradationMode.Normal)
        {
            _currentMode = restored;
            _logger.LogWarning("DegradationModeManager: restored persisted mode {Mode} from EngineConfig", restored);
        }
    }

    public DegradationMode CurrentMode => _currentMode;

    public async Task TransitionToAsync(DegradationMode newMode, string reason, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var oldMode = _currentMode;
            if (oldMode == newMode) return;

            _currentMode = newMode;
            _logger.LogWarning(
                "DegradationMode transition: {Old} -> {New}. Reason: {Reason}",
                oldMode, newMode, reason);
            _metrics.DegradationTransitions.Add(1,
                new("from_mode", oldMode.ToString()),
                new("to_mode", newMode.ToString()));
        }
        finally
        {
            _lock.Release();
        }
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
