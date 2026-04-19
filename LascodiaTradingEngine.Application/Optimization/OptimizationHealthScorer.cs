using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

using MarketRegime = LascodiaTradingEngine.Domain.Enums.MarketRegime;

/// <summary>
/// Composite health score computation for optimization candidates.
/// Default weights: WinRate 25%, ProfitFactor 20%, Drawdown 20%, Sharpe 15%, SampleSize 20%.
/// Guards against NaN/Infinity from degenerate backtest results.
///
/// This is the SINGLE SOURCE OF TRUTH for the health score formula. All other classes
/// (BootstrapAnalyzer, PermutationTestAnalyzer) must delegate here — never duplicate.
///
/// <para>
/// <b>Regime-aware weighting:</b> the <c>ComputeHealthScore(..., MarketRegime?)</c> overload
/// rebalances the 5-factor weights so that the metrics that matter most in a given regime
/// carry more of the score. Bootstrap/permutation analysers still use the regime-neutral
/// default because they test statistical properties of a trade stream, not live suitability.
/// </para>
/// </summary>
internal static class OptimizationHealthScorer
{
    /// <summary>Computes health score from a <see cref="BacktestResult"/>.</summary>
    internal static decimal ComputeHealthScore(BacktestResult r)
    {
        return ComputeHealthScore(r.WinRate, r.ProfitFactor, r.MaxDrawdownPct, r.SharpeRatio, r.TotalTrades);
    }

    /// <summary>Overload for manual metric values (used by health/feedback workers).</summary>
    internal static decimal ComputeHealthScore(decimal winRate, decimal profitFactor, decimal maxDrawdownPct, decimal sharpeRatio, int totalTrades = 50)
    {
        return ComputeHealthScoreWithWeights(winRate, profitFactor, maxDrawdownPct, sharpeRatio, totalTrades, DefaultWeights);
    }

    /// <summary>
    /// Regime-aware health score. When <paramref name="regime"/> is null, falls back to the
    /// default weights (identical to the regime-neutral overload). When a regime is supplied,
    /// the weight vector is selected from <see cref="RegimeWeights"/>.
    ///
    /// <para>
    /// Why regime-aware: a strategy tuned for Trending markets will naturally show lower win
    /// rate but higher profit factor; scored with the default weights during a Trending
    /// period it may trip the Degrading threshold even while performing exactly as designed.
    /// Rebalancing toward profit factor in Trending and toward drawdown preservation in
    /// Crisis keeps the score interpretable across regimes.
    /// </para>
    /// </summary>
    internal static decimal ComputeHealthScore(
        decimal winRate,
        decimal profitFactor,
        decimal maxDrawdownPct,
        decimal sharpeRatio,
        int totalTrades,
        MarketRegime? regime)
    {
        HealthWeights weights = regime.HasValue ? RegimeWeights[regime.Value] : DefaultWeights;
        return ComputeHealthScoreWithWeights(winRate, profitFactor, maxDrawdownPct, sharpeRatio, totalTrades, weights);
    }

    /// <summary>
    /// Regime-aware health score with caller-supplied weight overrides. Callers (e.g.
    /// <c>StrategyHealthWorker</c> via <c>RegimeHealthWeightsProvider</c>) that need
    /// hot-reloadable, per-environment tuning pass their resolved dictionary here. On
    /// lookup miss, falls back to the static <see cref="RegimeWeights"/> defaults so a
    /// partial override (e.g. only Trending configured) doesn't break the other regimes.
    /// </summary>
    internal static decimal ComputeHealthScore(
        decimal winRate,
        decimal profitFactor,
        decimal maxDrawdownPct,
        decimal sharpeRatio,
        int totalTrades,
        MarketRegime? regime,
        IReadOnlyDictionary<MarketRegime, HealthWeights>? weightOverrides)
    {
        HealthWeights weights;
        if (!regime.HasValue)
        {
            weights = DefaultWeights;
        }
        else if (weightOverrides is not null && weightOverrides.TryGetValue(regime.Value, out var overrideWeights))
        {
            weights = overrideWeights;
        }
        else
        {
            weights = RegimeWeights[regime.Value];
        }
        return ComputeHealthScoreWithWeights(winRate, profitFactor, maxDrawdownPct, sharpeRatio, totalTrades, weights);
    }

    /// <summary>
    /// 5-factor weight vector for the composite health score. The five weights must sum to
    /// <c>1.0</c> so the score stays in [0, 1].
    /// </summary>
    internal readonly record struct HealthWeights(
        decimal WinRate,
        decimal ProfitFactor,
        decimal Drawdown,
        decimal Sharpe,
        decimal SampleSize);

    /// <summary>Default (regime-neutral) weights — matches the historical formula.</summary>
    internal static readonly HealthWeights DefaultWeights = new(
        WinRate:      0.25m,
        ProfitFactor: 0.20m,
        Drawdown:     0.20m,
        Sharpe:       0.15m,
        SampleSize:   0.20m);

    /// <summary>
    /// Per-regime weight vectors. Each row sums to 1.0. Rationale:
    /// <list type="bullet">
    ///   <item><b>Trending</b>: PF is the dominant signal (few big wins, contained losses); WR naturally lower.</item>
    ///   <item><b>Ranging</b>: mean-reversion strategies win often with thin margins — WR weighted highest.</item>
    ///   <item><b>HighVolatility</b>: risk control is paramount — DD weighted heaviest, sample size softer.</item>
    ///   <item><b>LowVolatility</b>: returns are consistent and small — Sharpe is the best discriminator.</item>
    ///   <item><b>Crisis</b>: survival dominates; DD + sample size (avoid single-regime noise) matter most.</item>
    ///   <item><b>Breakout</b>: rewards PF and sample size; WR naturally low on false-breakout noise.</item>
    /// </list>
    /// </summary>
    internal static readonly IReadOnlyDictionary<MarketRegime, HealthWeights> RegimeWeights =
        new Dictionary<MarketRegime, HealthWeights>
        {
            [MarketRegime.Trending]       = new(0.15m, 0.30m, 0.15m, 0.20m, 0.20m),
            [MarketRegime.Ranging]        = new(0.30m, 0.15m, 0.20m, 0.15m, 0.20m),
            [MarketRegime.HighVolatility] = new(0.20m, 0.15m, 0.30m, 0.20m, 0.15m),
            [MarketRegime.LowVolatility]  = new(0.20m, 0.15m, 0.20m, 0.30m, 0.15m),
            [MarketRegime.Crisis]         = new(0.15m, 0.15m, 0.30m, 0.15m, 0.25m),
            [MarketRegime.Breakout]       = new(0.20m, 0.25m, 0.15m, 0.15m, 0.25m),
        };

    private static decimal ComputeHealthScoreWithWeights(
        decimal winRate,
        decimal profitFactor,
        decimal maxDrawdownPct,
        decimal sharpeRatio,
        int totalTrades,
        HealthWeights w)
    {
        return w.WinRate      * Sanitize(winRate)
             + w.ProfitFactor * Math.Min(1m, Sanitize(profitFactor) / 2m)
             + w.Drawdown     * Math.Max(0m, 1m - Sanitize(maxDrawdownPct) / 20m)
             + w.Sharpe       * Math.Min(1m, Math.Max(0m, Sanitize(sharpeRatio)) / 2m)
             + w.SampleSize   * Math.Min(1m, totalTrades / 50m);

        // Clamp NaN/Infinity to 0 to prevent nonsensical health scores
        static decimal Sanitize(decimal v)
        {
            double d = (double)v;
            return double.IsNaN(d) || double.IsInfinity(d) ? 0m : v;
        }
    }

    /// <summary>
    /// Computes health score from a list of trades and initial balance. Used by
    /// BootstrapAnalyzer (resampled trades) and PermutationTestAnalyzer (shuffled trades).
    /// Extracts metrics from the trade sequence then delegates to the canonical formula.
    /// </summary>
    internal static decimal ComputeHealthScoreFromTrades(IReadOnlyList<BacktestTrade> trades, decimal initialBalance)
    {
        if (trades.Count == 0) return 0m;

        int totalTrades  = trades.Count;
        int winningCount = trades.Count(t => t.PnL > 0);
        decimal winRate  = (decimal)winningCount / totalTrades;

        decimal grossWins  = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        decimal grossLoss  = Math.Abs(trades.Where(t => t.PnL < 0).Sum(t => t.PnL));
        decimal profitFactor = grossLoss > 0 ? grossWins / grossLoss : grossWins > 0 ? 10m : 0m;

        // Max drawdown
        decimal equity = initialBalance;
        decimal peak   = equity;
        decimal maxDd  = 0m;
        foreach (var trade in trades)
        {
            equity += trade.PnL;
            if (equity > peak) peak = equity;
            decimal dd = peak > 0 ? (peak - equity) / peak * 100m : 0m;
            if (dd > maxDd) maxDd = dd;
        }

        // Sharpe ratio (annualised, assuming ~252 trading days)
        decimal meanReturn = trades.Average(t => t.PnL);
        double variance    = trades.Sum(t => (double)(t.PnL - meanReturn) * (double)(t.PnL - meanReturn)) / Math.Max(1, totalTrades - 1);
        double stdDev      = Math.Sqrt(variance);
        decimal sharpe     = stdDev > 1e-10 ? (decimal)((double)meanReturn / stdDev * Math.Sqrt(252)) : 0m;

        return ComputeHealthScore(winRate, profitFactor, maxDd, sharpe, totalTrades);
    }

    /// <summary>
    /// Computes health score from a PnL array (no trade objects). Used by
    /// PermutationTestAnalyzer where only PnL values are shuffled.
    /// </summary>
    internal static decimal ComputeHealthScoreFromPnls(decimal[] pnls, decimal initialBalance)
    {
        if (pnls.Length == 0) return 0m;

        int totalTrades  = pnls.Length;
        int winningCount = pnls.Count(p => p > 0);
        decimal winRate  = (decimal)winningCount / totalTrades;

        decimal grossWins = pnls.Where(p => p > 0).Sum();
        decimal grossLoss = Math.Abs(pnls.Where(p => p < 0).Sum());
        decimal profitFactor = grossLoss > 0 ? grossWins / grossLoss : grossWins > 0 ? 10m : 0m;

        // Max drawdown
        decimal equity = initialBalance;
        decimal peak   = equity;
        decimal maxDd  = 0m;
        foreach (decimal pnl in pnls)
        {
            equity += pnl;
            if (equity > peak) peak = equity;
            decimal dd = peak > 0 ? (peak - equity) / peak * 100m : 0m;
            if (dd > maxDd) maxDd = dd;
        }

        // Sharpe ratio (annualised)
        decimal meanReturn = pnls.Sum() / totalTrades;
        double variance    = pnls.Sum(p => (double)(p - meanReturn) * (double)(p - meanReturn)) / Math.Max(1, totalTrades - 1);
        double stdDev      = Math.Sqrt(variance);
        decimal sharpe     = stdDev > 1e-10 ? (decimal)((double)meanReturn / stdDev * Math.Sqrt(252)) : 0m;

        return ComputeHealthScore(winRate, profitFactor, maxDd, sharpe, totalTrades);
    }
}
