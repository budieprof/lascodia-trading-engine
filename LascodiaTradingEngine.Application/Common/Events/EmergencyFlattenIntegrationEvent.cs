using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when the emergency kill switch is triggered. Consumed by all workers
/// to immediately halt processing and by the alert system for critical notifications.
/// </summary>
public record EmergencyFlattenIntegrationEvent : IntegrationEvent
{
    public long     SequenceNumber        { get; init; } = EventSequence.Next();
    public long     TriggeredByAccountId  { get; init; }
    public string   Reason                { get; init; } = string.Empty;
    public int      OrdersCancelled       { get; init; }
    public int      PositionsQueued       { get; init; }
    public int      StrategiesPaused      { get; init; }
    public DateTime TriggeredAt           { get; init; }
}
