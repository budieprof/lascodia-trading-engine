using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>OpenPositionCommandHandler</c> after a new position is persisted.
/// Downstream consumers (e.g. <c>PositionWorker</c>, risk monitors) can use this
/// event to react immediately instead of relying solely on polling.
/// </summary>
public record PositionOpenedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long              SequenceNumber    { get; init; } = EventSequence.Next();

    /// <summary>The new position's database Id.</summary>
    public long              PositionId        { get; init; }

    /// <summary>The order that opened this position, if any.</summary>
    public long?             OpenOrderId       { get; init; }

    /// <summary>The currency pair symbol.</summary>
    public string            Symbol            { get; init; } = string.Empty;

    /// <summary>Long or Short.</summary>
    public PositionDirection Direction         { get; init; }

    /// <summary>Volume opened.</summary>
    public decimal           OpenLots          { get; init; }

    /// <summary>Volume-weighted average entry price.</summary>
    public decimal           AverageEntryPrice { get; init; }

    /// <summary>Stop-loss level, if set.</summary>
    public decimal?          StopLoss          { get; init; }

    /// <summary>Take-profit level, if set.</summary>
    public decimal?          TakeProfit        { get; init; }

    /// <summary>Whether this is a paper-trading position.</summary>
    public bool              IsPaper           { get; init; }

    /// <summary>UTC timestamp when the position was opened.</summary>
    public DateTime          OpenedAt          { get; init; }
}
