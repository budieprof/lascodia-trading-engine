using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>EAHealthMonitorWorker</c> when an EA instance is transitioned to
/// <see cref="Domain.Enums.EAInstanceStatus.Disconnected"/> due to a stale heartbeat.
/// Downstream consumers can react to broker adapter disconnections (e.g. pause strategy
/// evaluation, trigger alerts, update dashboards).
/// </summary>
public record EAInstanceDisconnectedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>Database Id of the EAInstance record.</summary>
    public long EAInstanceId { get; init; }

    /// <summary>Client-assigned unique instance identifier.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>The trading account this instance was connected to.</summary>
    public long TradingAccountId { get; init; }

    /// <summary>Comma-separated symbols that are now orphaned.</summary>
    public string OrphanedSymbols { get; init; } = string.Empty;

    /// <summary>Symbols that were successfully reassigned to standby instances.</summary>
    public string ReassignedSymbols { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the disconnect was detected.</summary>
    public DateTime DetectedAt { get; init; }
}
