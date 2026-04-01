using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when portfolio VaR exceeds the configured limit. Consumed by the risk
/// monitor to gate new positions and by the alert system for high-severity notifications.
/// </summary>
public record VaRBreachIntegrationEvent : IntegrationEvent
{
    public long     SequenceNumber   { get; init; } = EventSequence.Next();
    public long     TradingAccountId { get; init; }
    public decimal  PortfolioVaR95   { get; init; }
    public decimal  VaRLimitPct      { get; init; }
    public decimal  AccountEquity    { get; init; }
    public DateTime DetectedAt       { get; init; }
}
