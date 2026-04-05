using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published after a stress test run completes. Consumed by the alert system
/// when margin call risk is detected.
/// </summary>
public record StressTestCompletedIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long     SequenceNumber         { get; init; } = EventSequence.Next();

    /// <summary>Database Id of the stress test result record.</summary>
    public long     StressTestResultId     { get; init; }

    /// <summary>The trading account tested.</summary>
    public long     TradingAccountId       { get; init; }

    /// <summary>Name of the stress scenario applied (e.g. "2008 GFC", "Flash Crash").</summary>
    public string   ScenarioName           { get; init; } = string.Empty;

    /// <summary>Estimated P&amp;L impact in account currency under the stress scenario.</summary>
    public decimal  StressedPnl            { get; init; }

    /// <summary>Stressed P&amp;L as a percentage of account equity.</summary>
    public decimal  StressedPnlPct         { get; init; }

    /// <summary>Whether the scenario would trigger a margin call.</summary>
    public bool     WouldTriggerMarginCall { get; init; }

    /// <summary>UTC timestamp when the stress test completed.</summary>
    public DateTime ExecutedAt             { get; init; }
}
