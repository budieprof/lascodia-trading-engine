using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>LogDecisionCommandHandler</c> after an audit row lands.
/// The admin UI's audit trail page subscribes to this via the realtime relay
/// so the table refreshes without polling. Decision logs are append-only so
/// there's no corresponding <c>Updated</c> event.
/// </summary>
public record AuditDecisionLoggedIntegrationEvent : IntegrationEvent
{
    public long     SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>DB id of the newly-created decision-log row.</summary>
    public long     DecisionLogId  { get; init; }

    /// <summary>The entity type the decision pertains to (e.g. "Order").</summary>
    public string   EntityType     { get; init; } = string.Empty;

    /// <summary>FK-like id of the entity. May be 0 for pipeline-level events.</summary>
    public long     EntityId       { get; init; }

    /// <summary>Decision category: SignalApproved, ConfigUpdated, etc.</summary>
    public string   DecisionType   { get; init; } = string.Empty;

    /// <summary>Outcome label: Approved / Rejected / Updated / etc.</summary>
    public string   Outcome        { get; init; } = string.Empty;

    /// <summary>Short source identifier (command / worker name).</summary>
    public string   Source         { get; init; } = string.Empty;

    /// <summary>UTC create time.</summary>
    public DateTime CreatedAt      { get; init; }
}
