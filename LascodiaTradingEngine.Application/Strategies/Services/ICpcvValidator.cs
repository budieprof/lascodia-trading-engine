using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Combinatorial Purged Cross-Validation (CPCV) — deferred subsystem.
///
/// <para>
/// Currently a scaffold. The full implementation is a ~1000-line subsystem that
/// replaces / augments <c>WalkForwardRunClaimer</c>. Real CPCV is the single
/// highest-leverage change for reducing backtest overfitting, and deserves its
/// own focused work-stream rather than being stuffed into a batch commit.
/// </para>
///
/// <para>
/// <b>Design notes (for the dedicated work-stream):</b>
/// <list type="number">
/// <item><description>Split the training window into N contiguous groups (suggested N=12 for
///   12-month data). Choose test group size K=2.</description></item>
/// <item><description>Enumerate C(N, K) test-group combinations. For each combination: train
///   on the remaining groups (with a 1-group embargo before + after the test
///   groups to eliminate label leakage from overlapping triple-barrier horizons),
///   test on the K groups, record the resulting Sharpe.</description></item>
/// <item><description>Output is a distribution of C(N,K) Sharpes rather than a single point.
///   Deflated Sharpe and PBO become directly computable from this distribution.</description></item>
/// <item><description>For N=12, K=2, that's 66 paths per strategy candidate. Each path is
///   a full train + test cycle — expect ~20-100× the compute of single walk-forward.
///   Mitigation: only run CPCV on candidates that have already passed the cheap
///   gates (single-path Sharpe, TCA-adjusted EV, drawdown limit).</description></item>
/// <item><description>Integration point: <c>StrategyGenerationCycleRunner</c> calls this
///   validator on the top-k candidates from the primary-screening pool.
///   Candidates whose CPCV 25th-percentile Sharpe is below threshold are
///   discarded before promotion.</description></item>
/// <item><description>Storage: add a <c>CpcvRun</c> entity parallel to <c>WalkForwardRun</c>
///   with columns for the full Sharpe distribution (JSON array), derived DSR,
///   and PBO. <c>PromotionGateValidator</c> then reads CpcvRun instead of the
///   current single-path BacktestRun for DSR/PBO evaluation.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>References:</b>
/// López de Prado, M. (2018). <i>Advances in Financial Machine Learning</i>, Ch. 7 (Cross-Validation), Ch. 14 (Backtest Statistics).
/// </para>
/// </summary>
public interface ICpcvValidator
{
    /// <summary>
    /// Run CPCV on the given strategy + backtest window. Returns the full Sharpe
    /// distribution across all C(N,K) paths. Caller computes DSR / PBO from the
    /// distribution via <c>PromotionGateValidator</c> helpers.
    /// </summary>
    Task<CpcvResult> ValidateAsync(
        long strategyId,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken ct);
}

/// <summary>
/// CPCV run output. <see cref="SharpeDistribution"/> holds the C(N,K) path Sharpes;
/// summary statistics are derived for quick consumption by gates and dashboards.
/// </summary>
public sealed record CpcvResult(
    long StrategyId,
    DateTime FromDate,
    DateTime ToDate,
    int NGroups,
    int KTestGroups,
    IReadOnlyList<double> SharpeDistribution,
    double MedianSharpe,
    double P25Sharpe,
    double P75Sharpe,
    double DeflatedSharpe,
    double ProbabilityOfOverfitting);
