using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>PositionWorker</c> when a position is closed (Stop Loss, Take Profit,
/// or manual closure). Downstream consumers use this event to back-fill
/// <c>MLModelPredictionLog</c> outcome fields and update account equity figures
/// without polling the Positions table.
/// </summary>
public record PositionClosedIntegrationEvent : IntegrationEvent
{
    /// <summary>The closed position's database Id.</summary>
    public long            PositionId      { get; init; }

    /// <summary>The trade signal that spawned this position, if any.</summary>
    public long?           TradeSignalId   { get; init; }

    /// <summary>The strategy that generated the signal, if any.</summary>
    public long?           StrategyId      { get; init; }

    /// <summary>The currency pair of the closed position.</summary>
    public string          Symbol          { get; init; } = string.Empty;

    /// <summary>Long or Short direction of the position.</summary>
    public PositionDirection Direction      { get; init; }

    /// <summary>Volume-weighted average entry price of the position.</summary>
    public decimal         EntryPrice      { get; init; }

    /// <summary>Price at which the position was closed.</summary>
    public decimal         ClosePrice      { get; init; }

    /// <summary>Realised P&amp;L in account currency.</summary>
    public decimal         RealisedPnL     { get; init; }

    /// <summary>Actual pip movement from entry to close (positive = favourable).</summary>
    public decimal         ActualMagnitudePips { get; init; }

    /// <summary>True if the position closed with a profit.</summary>
    public bool            WasProfitable   { get; init; }

    /// <summary>Reason the position was closed: StopLoss, TakeProfit, or Manual.</summary>
    public string          CloseReason     { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the position was closed.</summary>
    public DateTime        ClosedAt        { get; init; }
}
