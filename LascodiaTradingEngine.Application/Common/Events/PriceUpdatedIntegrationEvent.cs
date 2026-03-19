using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

public record PriceUpdatedIntegrationEvent : IntegrationEvent
{
    public string  Symbol    { get; init; } = string.Empty;
    public decimal Bid       { get; init; }
    public decimal Ask       { get; init; }
    public DateTime Timestamp { get; init; }
}
