using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when the emergency kill switch is triggered. Consumed by all workers
/// to immediately halt processing and by the alert system for critical notifications.
/// </summary>
public record EmergencyFlattenIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long     SequenceNumber        { get; init; } = EventSequence.Next();

    /// <summary>The trading account that triggered the emergency flatten.</summary>
    public long     TriggeredByAccountId  { get; init; }

    /// <summary>Human-readable reason for the emergency flatten.</summary>
    public string   Reason                { get; init; } = string.Empty;

    /// <summary>Number of pending orders cancelled.</summary>
    public int      OrdersCancelled       { get; init; }

    /// <summary>Number of open positions queued for closure.</summary>
    public int      PositionsQueued       { get; init; }

    /// <summary>Number of active strategies paused.</summary>
    public int      StrategiesPaused      { get; init; }

    /// <summary>UTC timestamp when the flatten was triggered.</summary>
    public DateTime TriggeredAt           { get; init; }
}
