using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>DeregisterEACommandHandler</c> when an EA instance is gracefully
/// deregistered (status set to ShuttingDown). Downstream consumers can react to planned
/// broker adapter shutdowns (e.g. pause strategies, update dashboards).
/// </summary>
public record EAInstanceDeregisteredIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>Database Id of the EAInstance record.</summary>
    public long EAInstanceId { get; init; }

    /// <summary>Client-assigned unique instance identifier.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>The trading account this instance was connected to.</summary>
    public long TradingAccountId { get; init; }

    /// <summary>Comma-separated symbols this instance was responsible for.</summary>
    public string Symbols { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the deregistration occurred.</summary>
    public DateTime DeregisteredAt { get; init; }
}
