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
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long                  SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>Affected currency pair symbol.</summary>
    public string                Symbol         { get; init; } = string.Empty;

    /// <summary>Type of anomaly detected (e.g. PriceSpike, StaleQuote, InvertedSpread).</summary>
    public MarketDataAnomalyType AnomalyType    { get; init; }

    /// <summary>EA instance that provided the anomalous data.</summary>
    public string                InstanceId     { get; init; } = string.Empty;

    /// <summary>Human-readable anomaly description.</summary>
    public string                Description    { get; init; } = string.Empty;

    /// <summary>Whether the anomalous data point was quarantined (replaced with last-known-good).</summary>
    public bool                  WasQuarantined { get; init; }

    /// <summary>UTC timestamp when the anomaly was detected.</summary>
    public DateTime              DetectedAt     { get; init; }
}
