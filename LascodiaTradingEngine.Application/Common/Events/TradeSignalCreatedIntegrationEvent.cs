using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

public record TradeSignalCreatedIntegrationEvent : IntegrationEvent
{
    public long    TradeSignalId { get; init; }
    public long    StrategyId    { get; init; }
    public string  Symbol        { get; init; } = string.Empty;
    public string  Direction     { get; init; } = string.Empty;
    public decimal EntryPrice    { get; init; }
}
