using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records the execution quality metrics for a single filled order, capturing the
/// difference between the requested and actual fill price (slippage) and the time
/// taken from order submission to broker confirmation.
/// </summary>
/// <remarks>
/// These records are the primary data source for the execution quality analysis feature,
/// which identifies patterns of poor fills (high slippage, slow fills) by trading session,
/// symbol, or strategy. Persistent high slippage on a particular session or symbol may
/// indicate that trading should be restricted to higher-liquidity windows.
/// </remarks>
public class ExecutionQualityLog : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Order"/> whose fill this log records.</summary>
    public long    OrderId        { get; set; }

    /// <summary>
    /// Optional foreign key to the <see cref="Strategy"/> that generated the order,
    /// enabling per-strategy execution quality analysis. Null for manual orders.
    /// </summary>
    public long?   StrategyId     { get; set; }

    /// <summary>The currency pair on which the order was executed (e.g. "EURUSD").</summary>
    public string  Symbol         { get; set; } = string.Empty;

    /// <summary>
    /// The trading session active when the order was submitted.
    /// Used to correlate execution quality with session liquidity:
    /// London and New York overlap typically offer the tightest spreads and least slippage.
    /// </summary>
    public TradingSession  Session        { get; set; } = TradingSession.London;

    /// <summary>
    /// The price at which the order was intended to be filled — the signal's entry price
    /// or the limit/stop trigger level.
    /// </summary>
    public decimal RequestedPrice { get; set; }

    /// <summary>The actual price at which the broker confirmed the order was filled.</summary>
    public decimal FilledPrice    { get; set; }

    /// <summary>
    /// Signed slippage in pips: positive = filled at a worse price than requested (adverse);
    /// negative = filled at a better price (price improvement).
    /// Calculated as: (FilledPrice − RequestedPrice) × pip_factor × direction_sign.
    /// </summary>
    public decimal SlippagePips   { get; set; }

    /// <summary>
    /// Time elapsed in milliseconds from when the order was submitted to the broker until
    /// the fill confirmation was received. High values indicate latency issues in the
    /// broker API connection or order routing layer.
    /// </summary>
    public long    SubmitToFillMs { get; set; }

    /// <summary>
    /// <c>true</c> if the order was only partially filled (i.e. <c>FilledQuantity</c>
    /// was less than the requested lot size). Partial fills can occur during low-liquidity
    /// periods and may require the remainder to be resubmitted.
    /// </summary>
    public bool    WasPartialFill { get; set; }

    /// <summary>
    /// The fraction of the requested lot size that was actually filled (0.0–1.0).
    /// A fill rate of 1.0 means full fill; 0.5 means half the requested size was filled.
    /// </summary>
    public decimal FillRate       { get; set; }

    /// <summary>UTC timestamp when this execution quality record was created.</summary>
    public DateTime RecordedAt    { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted      { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The order whose fill quality is recorded here.</summary>
    public virtual Order Order { get; set; } = null!;

    /// <summary>The strategy that generated the order (nullable for manual orders).</summary>
    public virtual Strategy? Strategy { get; set; }
}
