using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Bootstrap resampling for confidence interval estimation on backtest metrics.
/// Uses a block bootstrap to account for serial correlation in trading returns:
/// consecutive trades are resampled in contiguous blocks rather than individually,
/// preserving the autocorrelation structure and producing wider (more honest) CIs
/// than naive i.i.d. bootstrap.
/// </summary>
internal static class BootstrapAnalyzer
{
    /// <summary>
    /// Computes a block bootstrap confidence interval for a backtest health score.
    /// Block size is chosen as ceil(sqrt(n)), which balances between preserving
    /// serial dependence (larger blocks) and allowing enough resampling variation
    /// (smaller blocks).
    /// </summary>
    /// <param name="trades">Original trade list from the backtest.</param>
    /// <param name="initialBalance">Starting balance for metric computation.</param>
    /// <param name="iterations">Number of bootstrap iterations (default 1000).</param>
    /// <param name="confidenceLevel">Confidence level, e.g. 0.95 for 95% CI.</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    /// <returns>Lower bound, median, and upper bound of the health score distribution.</returns>
    public static (decimal LowerBound, decimal Median, decimal UpperBound) ComputeHealthScoreCI(
        IReadOnlyList<BacktestTrade> trades,
        decimal initialBalance,
        int iterations = 1000,
        double confidenceLevel = 0.95,
        int seed = 42)
    {
        if (trades.Count < 5)
            return (0m, 0m, 0m); // Not enough trades for meaningful resampling

        var rng       = new Random(seed);
        var scores    = new decimal[iterations];
        int n         = trades.Count;

        // For very small samples (n < 10), serial correlation estimation is unreliable
        // so fall back to standard i.i.d. bootstrap. Above that, use block bootstrap
        // with block size = ceil(sqrt(n)) to preserve autocorrelation structure.
        int blockSize = n < 10 ? 1 : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));

        for (int iter = 0; iter < iterations; iter++)
        {
            if (blockSize == 1)
            {
                // Standard i.i.d. bootstrap for small samples
                var resampled = new BacktestTrade[n];
                for (int i = 0; i < n; i++)
                    resampled[i] = trades[rng.Next(n)];
                scores[iter] = OptimizationHealthScorer.ComputeHealthScoreFromTrades(resampled, initialBalance);
            }
            else
            {
                // Block bootstrap: sample contiguous blocks with replacement.
                // Non-wrapping: clamp blockStart so blocks never span the boundary
                // between the last and first trade, which would pair chronologically
                // distant trades and inject artificial correlation into the CI estimate.
                var resampled = new List<BacktestTrade>(n + blockSize);
                int maxStart  = n - blockSize; // Ensures blockStart + blockSize <= n
                while (resampled.Count < n)
                {
                    int blockStart = maxStart > 0 ? rng.Next(maxStart + 1) : 0;
                    for (int j = 0; j < blockSize && resampled.Count < n; j++)
                        resampled.Add(trades[blockStart + j]);
                }
                scores[iter] = OptimizationHealthScorer.ComputeHealthScoreFromTrades(resampled, initialBalance);
            }
        }

        Array.Sort(scores);

        double alpha  = 1.0 - confidenceLevel;
        int lowerIdx  = Math.Max(0, (int)(alpha / 2.0 * iterations));
        int upperIdx  = Math.Min(iterations - 1, (int)((1.0 - alpha / 2.0) * iterations));
        int medianIdx = iterations / 2;

        return (scores[lowerIdx], scores[medianIdx], scores[upperIdx]);
    }
}
