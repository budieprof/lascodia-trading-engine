using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when a symbol's ownership is transferred from one EA instance to another
/// (e.g. during instance failover, deregistration, or manual reassignment).
/// </summary>
public record SymbolReassignedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>The instrument symbol that was reassigned.</summary>
    public required string Symbol { get; init; }

    /// <summary>The EA instance that previously owned this symbol.</summary>
    public required string PreviousInstanceId { get; init; }

    /// <summary>The EA instance that now owns this symbol (null if unassigned).</summary>
    public required string? NewInstanceId { get; init; }

    /// <summary>Human-readable reason for the reassignment.</summary>
    public required string Reason { get; init; }
}
