using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>CreateOrderCommandHandler</c> after a new order is persisted.
/// Downstream consumers (e.g. <c>OrderExecutionWorker</c>) can use this event to
/// react immediately instead of relying solely on polling.
/// </summary>
public record OrderCreatedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long          SequenceNumber    { get; init; } = EventSequence.Next();

    /// <summary>The new order's database Id.</summary>
    public long          OrderId           { get; init; }

    /// <summary>The trade signal that triggered this order, if any.</summary>
    public long?         TradeSignalId     { get; init; }

    /// <summary>The strategy that generated this order.</summary>
    public long          StrategyId        { get; init; }

    /// <summary>The trading account the order belongs to.</summary>
    public long          TradingAccountId  { get; init; }

    /// <summary>The currency pair symbol.</summary>
    public string        Symbol            { get; init; } = string.Empty;

    /// <summary>Buy or Sell.</summary>
    public OrderType     OrderType         { get; init; }

    /// <summary>Market, Limit, Stop, or StopLimit.</summary>
    public ExecutionType ExecutionType     { get; init; }

    /// <summary>Requested lot size.</summary>
    public decimal       Quantity          { get; init; }

    /// <summary>Requested price (0 for Market orders).</summary>
    public decimal       Price             { get; init; }

    /// <summary>Whether this is a paper-trading order.</summary>
    public bool          IsPaper           { get; init; }

    /// <summary>UTC timestamp when the order was created.</summary>
    public DateTime      CreatedAt         { get; init; }
}
