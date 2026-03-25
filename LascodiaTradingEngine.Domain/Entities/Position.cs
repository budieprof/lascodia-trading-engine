using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents an open or closed market position — the aggregated state of one or more
/// filled orders on a single instrument in a single direction.
/// </summary>
/// <remarks>
/// The <c>PositionWorker</c> background service polls open positions every 10 seconds,
/// updates <see cref="CurrentPrice"/> and <see cref="UnrealizedPnL"/> from the live price
/// cache, and triggers automatic stop-loss / take-profit closures when the respective
/// levels are breached.
/// </remarks>
public class Position : Entity<long>
{
    /// <summary>The instrument this position is on (e.g. "EURUSD").</summary>
    public string  Symbol              { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a Long (buy) or Short (sell) position.
    /// Determines the direction of SL/TP comparisons:
    /// <list type="bullet">
    ///   <item><description>Long: SL is below entry, TP is above entry.</description></item>
    ///   <item><description>Short: SL is above entry, TP is below entry.</description></item>
    /// </list>
    /// </summary>
    public PositionDirection  Direction           { get; set; } = PositionDirection.Long;

    /// <summary>
    /// Currently open lot size. Decreases as scale-out orders are filled.
    /// Reaches 0 when the position is fully closed.
    /// </summary>
    public decimal OpenLots            { get; set; }

    /// <summary>
    /// Volume-weighted average entry price across all scale-in orders.
    /// Used as the cost basis for P&amp;L calculations.
    /// </summary>
    public decimal AverageEntryPrice   { get; set; }

    /// <summary>
    /// Most recently observed market price for this symbol (mid-price).
    /// Updated every polling cycle by the <c>PositionWorker</c>.
    /// Null if no live price has been received yet.
    /// </summary>
    public decimal? CurrentPrice       { get; set; }

    /// <summary>
    /// Current unrealised profit or loss in account currency, calculated as:
    /// <c>(CurrentPrice - AverageEntryPrice) × OpenLots × ContractSize</c> (adjusted for direction).
    /// Negative when the position is in drawdown.
    /// </summary>
    public decimal UnrealizedPnL       { get; set; }

    /// <summary>
    /// Cumulative realised P&amp;L from partial closes and scale-out orders.
    /// Finalised when the position is fully closed.
    /// </summary>
    public decimal RealizedPnL         { get; set; }

    /// <summary>
    /// Cumulative swap (rollover) charges in account currency, as reported by the broker.
    /// Updated from EA position snapshots/deltas. Negative = cost, positive = credit.
    /// Included in total P&amp;L on position close.
    /// </summary>
    public decimal Swap                { get; set; }

    /// <summary>
    /// Cumulative commission charges in account currency, as reported by the broker.
    /// Updated from EA position snapshots/deltas.
    /// </summary>
    public decimal Commission          { get; set; }

    /// <summary>
    /// Stop-loss price level. The <c>PositionWorker</c> compares <see cref="CurrentPrice"/>
    /// against this value on every cycle and closes the position when the level is breached.
    /// Null if no stop loss is configured.
    /// </summary>
    public decimal? StopLoss           { get; set; }

    /// <summary>
    /// Take-profit price level. Triggers automatic closure in profit when hit.
    /// Null if no take profit is configured.
    /// </summary>
    public decimal? TakeProfit         { get; set; }

    /// <summary>Current state of the position (Open, Closed, PartiallyFilled, etc.).</summary>
    public PositionStatus  Status              { get; set; } = PositionStatus.Open;

    /// <summary>
    /// When <c>true</c>, this is a simulated paper position — P&amp;L is tracked internally
    /// but no real money is at risk and no broker orders are placed.
    /// </summary>
    public bool    IsPaper             { get; set; }

    /// <summary>
    /// Current trailing stop trigger price, continuously updated as the market moves
    /// in the favourable direction. Null if trailing stop is not active.
    /// </summary>
    public decimal? TrailingStopLevel   { get; set; }

    /// <summary>Whether a trailing stop is active for this position.</summary>
    public bool    TrailingStopEnabled  { get; set; }

    /// <summary>
    /// The trailing stop algorithm in use: fixed-pip distance, percentage of price,
    /// or ATR-based dynamic distance. Null when trailing stop is disabled.
    /// </summary>
    public TrailingStopType? TrailingStopType     { get; set; }

    /// <summary>
    /// The trailing distance expressed in the unit for the chosen <see cref="TrailingStopType"/>.
    /// Null when trailing stop is disabled.
    /// </summary>
    public decimal? TrailingStopValue   { get; set; }

    /// <summary>
    /// Identifier assigned by the broker to this position (if the broker supports position IDs).
    /// Used to correlate engine positions with broker-side state during reconciliation.
    /// </summary>
    public string? BrokerPositionId     { get; set; }

    /// <summary>
    /// The order that initially opened this position. Used for idempotency:
    /// if the event bus retries delivery, this prevents duplicate positions.
    /// </summary>
    public long? OpenOrderId           { get; set; }

    /// <summary>UTC timestamp when this position was opened (first fill).</summary>
    public DateTime OpenedAt           { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the position was fully closed. Null while still open.</summary>
    public DateTime? ClosedAt          { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted           { get; set; }

    /// <summary>Optimistic concurrency token — auto-incremented by PostgreSQL on every update.</summary>
    public uint    RowVersion          { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>
    /// Scale-in and scale-out child orders that modify the size of this position
    /// as it moves in the favourable direction.
    /// </summary>
    public virtual ICollection<PositionScaleOrder> ScaleOrders { get; set; } = new List<PositionScaleOrder>();
}
