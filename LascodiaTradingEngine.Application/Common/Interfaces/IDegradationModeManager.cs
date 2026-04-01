using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Centralized engine degradation state machine. Workers query CurrentMode to adjust
/// behavior (e.g., skip ML scoring in MLDegraded, halt trading in EmergencyHalt).
/// Thread-safe for concurrent reads from multiple workers.
/// </summary>
public interface IDegradationModeManager
{
    /// <summary>Current engine degradation mode. Workers check this at the top of each cycle.</summary>
    DegradationMode CurrentMode { get; }

    /// <summary>Transitions to a new degradation mode. Logs the transition and publishes an alert.</summary>
    Task TransitionToAsync(DegradationMode newMode, string reason, CancellationToken cancellationToken);

    /// <summary>Records a heartbeat from a subsystem. If heartbeats go stale, the manager auto-degrades.</summary>
    void RecordSubsystemHeartbeat(string subsystemName);

    /// <summary>Checks if a specific subsystem is operational based on heartbeat freshness.</summary>
    bool IsSubsystemOperational(string subsystemName);
}
