using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Represents the portfolio capital allocation weight assigned to a <see cref="Strategy"/>
/// by the portfolio rebalancing engine.
/// </summary>
/// <remarks>
/// The allocation weight determines what fraction of available capital is routed to each
/// active strategy. Weights are recalculated periodically based on rolling risk-adjusted
/// performance (Sharpe ratio), so well-performing strategies receive a larger share of
/// capital while underperforming ones are reduced.
///
/// Weights are normalised to sum to 1.0 across all active strategies after rebalancing.
/// A strategy with <c>Weight = 0</c> effectively receives no new trades until rebalanced.
/// </remarks>
public class StrategyAllocation : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> this allocation belongs to.</summary>
    public long    StrategyId          { get; set; }

    /// <summary>
    /// Normalised capital allocation weight in the range 0.0–1.0.
    /// e.g. 0.3 means 30% of available trading capital is allocated to this strategy.
    /// All active strategies' weights should sum to 1.0 after rebalancing.
    /// </summary>
    public decimal Weight              { get; set; }

    /// <summary>
    /// Rolling Sharpe ratio for this strategy at the time of the last rebalance.
    /// Sharpe = (mean return − risk-free rate) / standard deviation of returns.
    /// Higher values indicate better risk-adjusted performance and typically result
    /// in a higher <see cref="Weight"/> allocation.
    /// </summary>
    public decimal RollingSharpRatio   { get; set; }

    /// <summary>UTC timestamp of the most recent rebalance that produced this allocation.</summary>
    public DateTime LastRebalancedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted           { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this allocation record belongs to.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
