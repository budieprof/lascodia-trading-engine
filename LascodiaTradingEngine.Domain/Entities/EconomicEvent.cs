using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a scheduled macroeconomic news release or central bank event that may
/// cause abnormal volatility in currency markets.
/// </summary>
/// <remarks>
/// The news filter evaluates upcoming events before allowing new signals to be approved.
/// High-impact events (e.g. Non-Farm Payrolls, FOMC rate decisions) can trigger a trading
/// halt window — no new orders are submitted in the configurable minutes before and after
/// the event to avoid being caught in the volatility spike.
///
/// Events are populated from an external economic calendar API (e.g. ForexFactory, Investing.com)
/// or entered manually, as indicated by <see cref="Source"/>.
/// </remarks>
public class EconomicEvent : Entity<long>
{
    /// <summary>
    /// Full name of the economic release or event.
    /// e.g. "US Non-Farm Payrolls", "ECB Interest Rate Decision", "UK CPI YoY".
    /// </summary>
    public string  Title        { get; set; } = string.Empty;

    /// <summary>
    /// The three-letter currency code most directly affected by this event.
    /// e.g. "USD" for US data, "EUR" for ECB events.
    /// The news filter compares this against the currencies of active strategies.
    /// </summary>
    public string  Currency     { get; set; } = string.Empty;

    /// <summary>
    /// Expected market impact level: <c>Low</c>, <c>Medium</c>, or <c>High</c>.
    /// Only <c>High</c>-impact events trigger trading halts by default, though this
    /// is configurable via <see cref="EngineConfig"/>.
    /// </summary>
    public EconomicImpact  Impact       { get; set; } = EconomicImpact.Low;

    /// <summary>UTC time at which this economic release is scheduled to occur.</summary>
    public DateTime ScheduledAt { get; set; }

    /// <summary>
    /// Consensus analyst forecast for this release (raw string from the data provider,
    /// e.g. "200K" for NFP). Null if no forecast is available.
    /// </summary>
    public string? Forecast     { get; set; }

    /// <summary>
    /// The actual released figure from the previous period (used as reference context).
    /// Null until populated by the data provider. e.g. "187K".
    /// </summary>
    public string? Previous     { get; set; }

    /// <summary>
    /// The actual figure announced at release time.
    /// Populated post-release by the data sync worker. Null before the event fires.
    /// </summary>
    public string? Actual       { get; set; }

    /// <summary>
    /// How this event record was created: via an automated API feed or entered manually.
    /// </summary>
    public EconomicEventSource  Source       { get; set; } = EconomicEventSource.Manual;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted    { get; set; }
}
