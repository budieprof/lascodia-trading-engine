using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published after a stress test run completes. Consumed by the alert system
/// when margin call risk is detected.
/// </summary>
public record StressTestCompletedIntegrationEvent : IntegrationEvent
{
    public long     SequenceNumber         { get; init; } = EventSequence.Next();
    public long     StressTestResultId     { get; init; }
    public long     TradingAccountId       { get; init; }
    public string   ScenarioName           { get; init; } = string.Empty;
    public decimal  StressedPnl            { get; init; }
    public decimal  StressedPnlPct         { get; init; }
    public bool     WouldTriggerMarginCall { get; init; }
    public DateTime ExecutedAt             { get; init; }
}
