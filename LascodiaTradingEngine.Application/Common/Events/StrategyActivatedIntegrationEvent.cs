using Lascodia.Trading.Engine.EventBus.Events;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published by <c>ActivateStrategyCommandHandler</c> when a strategy's status transitions
/// to <c>Active</c>. The primary consumer automatically queues a <c>BacktestRun</c> so
/// that every newly deployed strategy has an initial performance baseline before it
/// begins live signal generation.
/// </summary>
public record StrategyActivatedIntegrationEvent : IntegrationEvent
{
    /// <summary>The activated strategy's database Id.</summary>
    public long       StrategyId   { get; init; }

    /// <summary>Human-readable strategy name.</summary>
    public string     Name         { get; init; } = string.Empty;

    /// <summary>The currency pair the strategy trades.</summary>
    public string     Symbol       { get; init; } = string.Empty;

    /// <summary>The chart timeframe the strategy operates on.</summary>
    public Timeframe  Timeframe    { get; init; }

    /// <summary>UTC timestamp when the strategy was activated.</summary>
    public DateTime   ActivatedAt  { get; init; }
}
