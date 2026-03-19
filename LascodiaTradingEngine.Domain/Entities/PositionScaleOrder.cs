using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents a scheduled scale-in or scale-out child order associated with an open
/// <see cref="Position"/>, used to dynamically increase or reduce exposure as the trade
/// moves in the favourable direction.
/// </summary>
/// <remarks>
/// Scale-in (<see cref="ScaleType.ScaleIn"/>) adds to a winning position at predefined
/// pip intervals, compounding exposure while the trade is profitable.
/// Scale-out (<see cref="ScaleType.ScaleOut"/>) partially closes a position at specific
/// profit levels, locking in gains progressively.
///
/// The position monitoring worker evaluates pending scale orders on every price cycle
/// and triggers the associated <see cref="Order"/> when <see cref="TriggerPips"/> is reached.
/// </remarks>
public class PositionScaleOrder : Entity<long>
{
    /// <summary>Foreign key to the parent <see cref="Position"/> this scale order modifies.</summary>
    public long    PositionId       { get; set; }

    /// <summary>Foreign key to the <see cref="Order"/> that executes this scale step.</summary>
    public long    OrderId          { get; set; }

    /// <summary>
    /// Whether this step adds to (<c>ScaleIn</c>) or reduces (<c>ScaleOut</c>) the position.
    /// </summary>
    public ScaleType  ScaleType        { get; set; } = ScaleType.ScaleIn;

    /// <summary>
    /// Sequence number of this scale step (1, 2, 3 …).
    /// Determines the order in which scale steps are evaluated and executed.
    /// </summary>
    public int     ScaleStep        { get; set; }

    /// <summary>
    /// Favourable price movement in pips from the average entry price required to trigger
    /// this scale step. e.g. 20 means the position must be 20 pips in profit before this
    /// scale order fires.
    /// </summary>
    public decimal TriggerPips      { get; set; }

    /// <summary>
    /// Lot size of the child order to be placed when the trigger is reached.
    /// For scale-in orders, this is added to the position. For scale-out, it is subtracted.
    /// </summary>
    public decimal LotSize          { get; set; }

    /// <summary>
    /// Optional take-profit price for this specific scale-out order.
    /// When set, the child order closes at this level; otherwise it inherits the parent's TP.
    /// Null for scale-in orders.
    /// </summary>
    public decimal? TakeProfitPrice { get; set; }

    /// <summary>Current state of this scale order: Pending, Triggered, Filled, or Cancelled.</summary>
    public ScaleOrderStatus  Status           { get; set; } = ScaleOrderStatus.Pending;

    /// <summary>UTC timestamp when this scale order record was created.</summary>
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted        { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The parent position this scale order modifies.</summary>
    public virtual Position Position { get; set; } = null!;

    /// <summary>The order that is placed when this scale step is triggered.</summary>
    public virtual Order Order { get; set; } = null!;
}
