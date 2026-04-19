using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a periodic health evaluation snapshot for a <see cref="Strategy"/>,
/// capturing key performance metrics over a rolling window of recent closed trades.
/// </summary>
/// <remarks>
/// The <c>StrategyHealthWorker</c> evaluates all active strategies on a configurable
/// schedule, computes the metrics below over a rolling trade window, derives a composite
/// <see cref="HealthScore"/>, and persists a new snapshot. The health score drives
/// automatic strategy management:
/// <list type="bullet">
///   <item><description><c>Healthy</c> (score ≥ 0.6) — strategy continues running normally.</description></item>
///   <item><description><c>Degraded</c> (0.3 ≤ score &lt; 0.6) — a warning is raised and optimisation may be triggered.</description></item>
///   <item><description><c>Critical</c> (score &lt; 0.3) — strategy is automatically paused and an <see cref="OptimizationRun"/> is queued.</description></item>
/// </list>
/// </remarks>
public class StrategyPerformanceSnapshot : Entity<long>
{
    /// <summary>Foreign key to the <see cref="Strategy"/> this snapshot covers.</summary>
    public long    StrategyId     { get; set; }

    /// <summary>Total number of closed trades in the evaluation window.</summary>
    public int     WindowTrades   { get; set; }

    /// <summary>Number of closed trades that ended in a profit within the window.</summary>
    public int     WinningTrades  { get; set; }

    /// <summary>Number of closed trades that ended in a loss within the window.</summary>
    public int     LosingTrades   { get; set; }

    /// <summary>
    /// Win rate = WinningTrades / WindowTrades, in the range 0.0–1.0.
    /// e.g. 0.55 means 55% of trades in the window were profitable.
    /// </summary>
    public decimal WinRate        { get; set; }

    /// <summary>
    /// Profit factor = gross profit / gross loss across all trades in the window.
    /// Values &gt; 1.0 indicate a net-profitable system; &gt; 2.0 is considered good.
    /// Values ≤ 1.0 mean losses exceeded gains.
    /// </summary>
    public decimal ProfitFactor   { get; set; }

    /// <summary>
    /// Sharpe ratio = (average trade return − risk-free rate) / standard deviation of returns.
    /// Higher values indicate better risk-adjusted performance.
    /// Annualised if the window is long enough; otherwise rolling.
    /// </summary>
    public decimal SharpeRatio    { get; set; }

    /// <summary>
    /// Maximum observed equity drawdown as a percentage of peak equity within the window.
    /// Used in the health score formula with an inverse weighting — lower drawdown → higher score.
    /// </summary>
    public decimal MaxDrawdownPct { get; set; }

    /// <summary>Net P&amp;L in account currency across all trades in the evaluation window.</summary>
    public decimal TotalPnL       { get; set; }

    /// <summary>
    /// Composite health score in the range 0.0–1.0, computed as:
    /// 0.4 × WinRate + 0.3 × min(1, ProfitFactor/2) + 0.3 × max(0, 1 − MaxDrawdownPct/20).
    /// </summary>
    public decimal HealthScore    { get; set; }

    /// <summary>
    /// Categorical health status derived from <see cref="HealthScore"/>:
    /// Healthy (≥ 0.6), Degraded (0.3–0.6), or Critical (&lt; 0.3).
    /// </summary>
    public StrategyHealthStatus  HealthStatus   { get; set; } = StrategyHealthStatus.Healthy;

    /// <summary>UTC timestamp when this snapshot was computed by the health worker.</summary>
    public DateTime EvaluatedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Market regime at the time this snapshot was captured. Lets the promotion gate
    /// verify "strategy posts positive Sharpe in ≥ N distinct regimes" without having
    /// to re-join MarketRegimeSnapshot by timestamp after the fact. Nullable so
    /// pre-regime-aware snapshots stay queryable under the new gate.
    /// </summary>
    public MarketRegime? MarketRegime { get; set; }

    /// <summary>Soft-delete flag. Filtered out by the global EF Core query filter.</summary>
    public bool    IsDeleted      { get; set; }

    // ── Navigation properties ────────────────────────────────────────────────

    /// <summary>The strategy this performance snapshot was evaluated for.</summary>
    public virtual Strategy Strategy { get; set; } = null!;
}
