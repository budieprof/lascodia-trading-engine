using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// One row per active strategy per portfolio-optimization cycle. Records the
/// computed allocation weight (Kelly-fractional or HRP) along with the inputs
/// that produced it, so position sizing reads a stable per-strategy fraction
/// and the historical allocation trail is queryable.
///
/// <para>
/// Index design favours the read pattern: position sizing reads "latest
/// snapshot for strategy X" → indexed on <c>(StrategyId, ComputedAt DESC)</c>.
/// </para>
/// </summary>
public class PortfolioWeightSnapshot : Entity<long>
{
    public long StrategyId { get; set; }
    public virtual Strategy? Strategy { get; set; }

    /// <summary>
    /// Allocation method used to compute <see cref="Weight"/>: "Kelly", "HRP",
    /// or "EqualWeight" (fallback). Stored as string for forward compatibility.
    /// </summary>
    public string AllocationMethod { get; set; } = "Kelly";

    /// <summary>
    /// Fractional weight in [0, 1]. Sum across one cycle's snapshots equals 1.0
    /// (or less when an explicit cash buffer is held). Position sizing multiplies
    /// this by the configured global risk budget to derive the per-trade lot size.
    /// </summary>
    public decimal Weight { get; set; }

    /// <summary>
    /// Kelly-fractional input — observed mean return / variance ratio at compute
    /// time. Stored for audit / dashboard inspection; the worker may apply a
    /// safety multiplier (default 0.5) before persisting <see cref="Weight"/>.
    /// </summary>
    public decimal KellyFraction { get; set; }

    /// <summary>Strategy's observed Sharpe ratio used in the allocation calculation.</summary>
    public decimal ObservedSharpe { get; set; }

    /// <summary>Number of trades the calculation was based on (sample size signal).</summary>
    public int SampleSize { get; set; }

    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted  { get; set; }
    public uint RowVersion { get; set; }
}
