using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Composite health score computation for optimization candidates.
/// Weights: WinRate 25%, ProfitFactor 20%, Drawdown 20%, Sharpe 15%, SampleSize 20%.
/// Guards against NaN/Infinity from degenerate backtest results.
///
/// This is the SINGLE SOURCE OF TRUTH for the health score formula. All other classes
/// (BootstrapAnalyzer, PermutationTestAnalyzer) must delegate here — never duplicate.
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
        return 0.25m * Sanitize(winRate)
             + 0.20m * Math.Min(1m, Sanitize(profitFactor) / 2m)
             + 0.20m * Math.Max(0m, 1m - Sanitize(maxDrawdownPct) / 20m)
             + 0.15m * Math.Min(1m, Math.Max(0m, Sanitize(sharpeRatio)) / 2m)
             + 0.20m * Math.Min(1m, totalTrades / 50m);

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
