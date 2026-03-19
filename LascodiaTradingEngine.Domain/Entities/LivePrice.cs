using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persisted snapshot of the latest bid/ask price for a currency pair.
/// Maintains exactly one row per symbol — upserted (insert or update) on every incoming tick
/// so downstream components can query the current market price from the database without
/// subscribing to the live price stream.
/// </summary>
/// <remarks>
/// This entity acts as a database-backed price cache for components that cannot access
/// the in-memory <c>ILivePriceCache</c> singleton (e.g. API endpoints serving client
/// requests). For high-frequency access patterns (strategy evaluation, position monitoring)
/// the in-memory cache is preferred to avoid database round-trips.
/// </remarks>
public class LivePrice : Entity<long>
{
    /// <summary>The instrument symbol this price belongs to (e.g. "EURUSD").</summary>
    public string   Symbol    { get; set; } = string.Empty;

    /// <summary>
    /// The current bid price — the price at which the broker will buy the base currency
    /// (i.e. the price a trader can sell at). Used for Short entry and Long SL/TP evaluation.
    /// </summary>
    public decimal  Bid       { get; set; }

    /// <summary>
    /// The current ask price — the price at which the broker will sell the base currency
    /// (i.e. the price a trader can buy at). Used for Long entry and Short SL/TP evaluation.
    /// The spread = Ask − Bid.
    /// </summary>
    public decimal  Ask       { get; set; }

    /// <summary>UTC timestamp of the tick that produced this bid/ask snapshot.</summary>
    public DateTime Timestamp { get; set; }
}
