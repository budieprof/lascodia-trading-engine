using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>RegisterEACommandHandler</c> after a new EA instance is registered.
/// Downstream consumers can react to new broker adapter connections.
/// </summary>
public record EAInstanceRegisteredIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long     SequenceNumber    { get; init; } = EventSequence.Next();

    /// <summary>Database Id of the EAInstance record.</summary>
    public long     EAInstanceId      { get; init; }

    /// <summary>Client-assigned unique instance identifier.</summary>
    public string   InstanceId        { get; init; } = string.Empty;

    /// <summary>The trading account this instance is connected to.</summary>
    public long     TradingAccountId  { get; init; }

    /// <summary>Comma-separated symbols this instance is responsible for.</summary>
    public string   Symbols           { get; init; } = string.Empty;

    /// <summary>Whether this instance is the coordinator.</summary>
    public bool     IsCoordinator     { get; init; }

    /// <summary>UTC timestamp when the instance registered.</summary>
    public DateTime RegisteredAt      { get; init; }
}
