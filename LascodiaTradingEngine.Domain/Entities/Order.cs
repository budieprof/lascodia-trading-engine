using Lascodia.Trading.Engine.SharedDomain.Common;
using Lascodia.Trading.Engine.SharedDomain.Filters;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a single order instruction submitted to a broker.
/// An order may originate from a <see cref="TradeSignal"/> (automated) or be created
/// manually through the API. It tracks the full lifecycle from pending through to fill or
/// rejection, including optional trailing-stop configuration.
/// </summary>
/// <remarks>
/// Paper orders (<see cref="IsPaper"/> = <c>true</c>) are simulated locally and never
/// forwarded to a live broker, making them safe for testing strategies in a real-time
/// market environment without financial risk.
/// </remarks>
public class Order : Entity<long>
{
    /// <summary>
    /// Optional foreign key to the <see cref="TradeSignal"/> that triggered this order.
    /// Null for manually placed orders that bypass the signal pipeline.
    /// </summary>
    public long?   TradeSignalId   { get; set; }

    /// <summary>The instrument symbol this order targets (e.g. "EURUSD").</summary>
    public string  Symbol          { get; set; } = string.Empty;

    /// <summary>
    /// The trading session (London, NewYork, Tokyo, Sydney) active when the order was created.
    /// Used by the execution quality analyser to correlate slippage with session liquidity.
    /// </summary>
    public TradingSession Session   { get; set; } = TradingSession.London;

    /// <summary>Foreign key to the <see cref="TradingAccount"/> this order is placed on.</summary>
    public long  TradingAccountId { get; set; }

    /// <summary>Foreign key to the <see cref="Strategy"/> this order belongs to.</summary>
    public long  StrategyId      { get; set; }

    /// <summary>Whether this is a buy or sell order.</summary>
    public OrderType  OrderType       { get; set; } = OrderType.Buy;

    /// <summary>
    /// Determines how the order is executed at the broker:
    /// <c>Market</c> fills immediately at the best available price;
    /// <c>Limit</c>/<c>Stop</c> wait for a specific price level.
    /// </summary>
    public ExecutionType ExecutionType { get; set; } = ExecutionType.Market;

    /// <summary>Requested lot size for this order.</summary>
    public decimal Quantity        { get; set; }

    /// <summary>
    /// Requested entry price. For <c>Market</c> orders this is 0 (filled at market);
    /// for limit/stop orders it is the trigger price.
    /// </summary>
    public decimal Price           { get; set; }

    /// <summary>
    /// Optional stop-loss price. When set, the broker is instructed to close the position
    /// at this price if the market moves against the order.
    /// </summary>
    public decimal? StopLoss       { get; set; }

    /// <summary>
    /// Optional take-profit price. When set, the broker is instructed to close the
    /// position at this price when the market moves in the favourable direction.
    /// </summary>
    public decimal? TakeProfit     { get; set; }

    /// <summary>
    /// Actual price at which the order was filled by the broker.
    /// Null until the order reaches <see cref="OrderStatus.Filled"/> or partially filled state.
    /// Compared against <see cref="Price"/> to compute slippage in <see cref="ExecutionQualityLog"/>.
    /// </summary>
    public decimal? FilledPrice    { get; set; }

    /// <summary>
    /// Actual lot quantity filled. May be less than <see cref="Quantity"/> for partial fills.
    /// </summary>
    public decimal? FilledQuantity { get; set; }

    /// <summary>Current order lifecycle state (Pending, Submitted, Filled, Rejected, Cancelled, etc.).</summary>
    public OrderStatus Status       { get; set; } = OrderStatus.Pending;

    /// <summary>
    /// The broker's own identifier for this order, returned after submission.
    /// Used to query order status, modify, or cancel via the broker API.
    /// </summary>
    public string? BrokerOrderId   { get; set; }

    /// <summary>
    /// Human-readable reason if the broker or risk checker rejected this order.
    /// Populated by the order execution pipeline on failure.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>
    /// Optional free-text notes from a human operator or automated system.
    /// Not required — the <c>[NotRequired]</c> attribute prevents FluentValidation from
    /// raising an error when this field is absent.
    /// </summary>
    [NotRequired]
    public string? Notes           { get; set; }

    /// <summary>
    /// When <c>true</c>, this is a paper/simulated order. It is processed locally by
    /// the paper-trading engine and never sent to the live broker.
    /// </summary>
    public bool    IsPaper         { get; set; }

    // ── Trailing stop configuration ──────────────────────────────────────────

    /// <summary>Whether a trailing stop is active for this order.</summary>
    public bool    TrailingStopEnabled  { get; set; }

    /// <summary>
    /// The trailing stop algorithm to apply: fixed-pip distance, percentage, or ATR-based.
    /// Null when <see cref="TrailingStopEnabled"/> is <c>false</c>.
    /// </summary>
    public TrailingStopType? TrailingStopType { get; set; }

    /// <summary>
    /// The trailing distance expressed in the unit appropriate for <see cref="TrailingStopType"/>
    /// (pips, percentage, or ATR multiplier). Null when trailing stop is disabled.
    /// </summary>
    public decimal? TrailingStopValue   { get; set; }

    /// <summary>
    /// The most favourable price seen since the order was opened.
    /// The trailing stop level is calculated relative to this value.
    /// Updated continuously by the position monitoring worker.
    /// </summary>
    public decimal? HighestFavourablePrice { get; set; }

    // ── Timestamps ───────────────────────────────────────────────────────────

    /// <summary>UTC timestamp when this order record was created in the system.</summary>
    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the broker confirmed the order was filled. Null if not yet filled.</summary>
    public DateTime? FilledAt  { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool      IsDeleted { get; set; }

    /// <summary>Optimistic concurrency token — auto-incremented by PostgreSQL on every update.</summary>
    public uint      RowVersion { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The trade signal this order was generated from (nullable for manual orders).</summary>
    public virtual TradeSignal? TradeSignal { get; set; }

    /// <summary>The strategy this order belongs to.</summary>
    public virtual Strategy Strategy { get; set; } = null!;

    /// <summary>The trading account on which this order was placed.</summary>
    public virtual TradingAccount TradingAccount { get; set; } = null!;

    /// <summary>Execution quality log entry measuring slippage and fill latency for this order. Null until the order is filled.</summary>
    public virtual ExecutionQualityLog? ExecutionQualityLog { get; set; }

    /// <summary>Scale-in/scale-out child orders linked to this parent order.</summary>
    public virtual ICollection<PositionScaleOrder> PositionScaleOrders { get; set; } = new List<PositionScaleOrder>();
}
