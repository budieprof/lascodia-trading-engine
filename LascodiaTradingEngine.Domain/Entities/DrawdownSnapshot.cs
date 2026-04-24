using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a point-in-time snapshot of the account's equity drawdown level and the
/// current recovery mode active in the risk engine.
/// </summary>
/// <remarks>
/// The drawdown monitor worker captures these snapshots periodically to build a historical
/// equity drawdown timeline. When <see cref="DrawdownPct"/> exceeds the thresholds defined
/// in the active <see cref="RiskProfile"/>, the risk engine transitions to a recovery mode
/// that reduces lot sizes and tightens risk limits until equity recovers.
///
/// Snapshots are never deleted (<see cref="IsDeleted"/> will typically remain <c>false</c>)
/// as they form the authoritative equity curve history used in performance reporting.
/// </remarks>
public class DrawdownSnapshot : Entity<long>
{
    /// <summary>
    /// Account equity at the moment this snapshot was captured, in account currency.
    /// Equity = cash balance + sum of all unrealised P&amp;L on open positions.
    /// </summary>
    public decimal  CurrentEquity { get; set; }

    /// <summary>
    /// Highest equity value recorded up to this point (running peak).
    /// The drawdown percentage is calculated relative to this peak value.
    /// </summary>
    public decimal  PeakEquity    { get; set; }

    /// <summary>
    /// Current drawdown as a percentage of <see cref="PeakEquity"/>.
    /// Calculated as: (PeakEquity − CurrentEquity) / PeakEquity × 100.
    /// A value of 5.0 means the account is 5% below its peak equity.
    /// </summary>
    public decimal  DrawdownPct   { get; set; }

    /// <summary>
    /// The risk engine operating mode at this point in time:
    /// <c>Normal</c> — standard trading parameters apply;
    /// <c>Reduced</c> — reduced lot sizes and tighter risk limits are in effect;
    /// <c>Halted</c> — all automated trading is halted pending manual review.
    /// </summary>
    public RecoveryMode   RecoveryMode  { get; set; } = RecoveryMode.Normal;

    /// <summary>UTC timestamp when this drawdown snapshot was recorded.</summary>
    public DateTime RecordedAt    { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool     IsDeleted     { get; set; }
}
