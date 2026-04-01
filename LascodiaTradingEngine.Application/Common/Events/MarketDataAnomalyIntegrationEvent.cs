using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when a market data anomaly is detected and quarantined.
/// Consumed by the alert system and by workers that should pause evaluation
/// on the affected symbol until the anomaly clears.
/// </summary>
public record MarketDataAnomalyIntegrationEvent : IntegrationEvent
{
    public long                  SequenceNumber { get; init; } = EventSequence.Next();
    public string                Symbol         { get; init; } = string.Empty;
    public MarketDataAnomalyType AnomalyType    { get; init; }
    public string                InstanceId     { get; init; } = string.Empty;
    public string                Description    { get; init; } = string.Empty;
    public bool                  WasQuarantined { get; init; }
    public DateTime              DetectedAt     { get; init; }
}
