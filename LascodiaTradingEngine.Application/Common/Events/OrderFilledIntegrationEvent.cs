using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>OrderExecutionWorker</c> immediately after a broker fill confirmation
/// is received. Downstream consumers use this event to record execution quality metrics
/// and trigger any fill-reactive workflows without polling the Orders table.
/// </summary>
public record OrderFilledIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection. Consumers can discard events with lower sequence than previously seen.</summary>
    public long          SequenceNumber  { get; init; } = EventSequence.Next();

    /// <summary>The filled order's database Id.</summary>
    public long          OrderId         { get; init; }

    /// <summary>The strategy that generated the order, if any. Null for manual orders.</summary>
    public long?         StrategyId      { get; init; }

    /// <summary>The currency pair on which the order was filled.</summary>
    public string        Symbol          { get; init; } = string.Empty;

    /// <summary>The trading session active at fill time, for liquidity correlation.</summary>
    public TradingSession Session        { get; init; }

    /// <summary>The price at which the order was intended to fill.</summary>
    public decimal       RequestedPrice  { get; init; }

    /// <summary>The broker-confirmed fill price.</summary>
    public decimal       FilledPrice     { get; init; }

    /// <summary>Elapsed milliseconds from order submission to fill confirmation.</summary>
    public long          SubmitToFillMs  { get; init; }

    /// <summary>True if the order was only partially filled.</summary>
    public bool          WasPartialFill  { get; init; }

    /// <summary>Fraction of the requested lot size that was actually filled (0.0–1.0).</summary>
    public decimal       FillRate        { get; init; }

    /// <summary>UTC timestamp of the fill confirmation.</summary>
    public DateTime      FilledAt        { get; init; }
}
