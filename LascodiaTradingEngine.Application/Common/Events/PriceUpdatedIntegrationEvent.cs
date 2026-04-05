using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by tick ingestion when live prices are updated.
/// The primary consumer is <c>StrategyWorker</c>, which evaluates strategies on each price change.
/// </summary>
public record PriceUpdatedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long    SequenceNumber { get; init; } = EventSequence.Next();

    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public string  Symbol    { get; init; } = string.Empty;

    /// <summary>Latest bid price.</summary>
    public decimal Bid       { get; init; }

    /// <summary>Latest ask price.</summary>
    public decimal Ask       { get; init; }

    /// <summary>UTC timestamp of the price tick.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Bid-ask spread in broker points. Null if not provided by the EA.</summary>
    public int? SpreadPoints { get; init; }

    /// <summary>Tick volume at the time of capture. Null if not provided by the EA.</summary>
    public long? TickVolume { get; init; }
}
