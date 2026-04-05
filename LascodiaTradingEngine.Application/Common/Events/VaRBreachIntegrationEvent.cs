using Lascodia.Trading.Engine.EventBus.Events;

namespace LascodiaTradingEngine.Application.Common.Events;

/// <summary>
/// Published when portfolio VaR exceeds the configured limit. Consumed by the risk
/// monitor to gate new positions and by the alert system for high-severity notifications.
/// </summary>
public record VaRBreachIntegrationEvent : IntegrationEvent
{
    /// <summary>Monotonic sequence number for ordering detection.</summary>
    public long     SequenceNumber   { get; init; } = EventSequence.Next();

    /// <summary>The trading account whose VaR limit was breached.</summary>
    public long     TradingAccountId { get; init; }

    /// <summary>Current portfolio VaR at the 95% confidence level.</summary>
    public decimal  PortfolioVaR95   { get; init; }

    /// <summary>Configured maximum VaR limit as a percentage of equity.</summary>
    public decimal  VaRLimitPct      { get; init; }

    /// <summary>Account equity at the time of detection.</summary>
    public decimal  AccountEquity    { get; init; }

    /// <summary>UTC timestamp when the breach was detected.</summary>
    public DateTime DetectedAt       { get; init; }
}
