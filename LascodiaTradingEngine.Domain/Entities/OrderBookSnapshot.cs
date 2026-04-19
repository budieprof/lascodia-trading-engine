using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Point-in-time snapshot of the top-of-book order book for a symbol, streamed from EA instances.
/// Used for liquidity assessment before order submission and microstructure analysis.
/// </summary>
public class OrderBookSnapshot : Entity<long>
{
    /// <summary>Currency pair symbol (e.g. "EURUSD").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Best bid price at snapshot time.</summary>
    public decimal BidPrice { get; set; }

    /// <summary>Best ask price at snapshot time.</summary>
    public decimal AskPrice { get; set; }

    /// <summary>Volume available at best bid.</summary>
    public decimal BidVolume { get; set; }

    /// <summary>Volume available at best ask.</summary>
    public decimal AskVolume { get; set; }

    /// <summary>Computed spread in points at snapshot time.</summary>
    public decimal SpreadPoints { get; set; }

    /// <summary>
    /// JSON-serialised array of depth levels beyond the top-of-book, ordered from
    /// best to worst price on each side. Shape:
    /// <c>{"bids":[{"p":1.0998,"v":2_500_000},...],"asks":[{"p":1.1002,"v":3_100_000},...]}</c>.
    /// Null when the broker doesn't expose <c>MarketBookGet</c> (most retail brokers
    /// only return top-of-book, in which case the existing Bid/Ask/BidVolume/AskVolume
    /// columns are the full picture). Tier-1 ECN-style retail brokers (ICMarkets,
    /// Pepperstone, FP Markets) typically return 5–10 levels per side here.
    /// </summary>
    public string? LevelsJson { get; set; }

    /// <summary>EA instance that provided this snapshot.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Timestamp from the EA when the snapshot was captured.</summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
