using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Monte Carlo permutation test for assessing whether a strategy's performance is
/// statistically significant or could have arisen by chance. Randomly shuffles the
/// association between entry signals and trade outcomes, recomputes the target metric
/// each iteration, and reports the p-value (fraction of random permutations that
/// achieved an equal or better score than the observed strategy).
///
/// <b>Interpretation:</b>
/// <list type="bullet">
///   <item>p &lt; 0.05 — strategy performance is statistically significant at 95% confidence</item>
///   <item>p &gt; 0.10 — insufficient evidence that performance differs from random</item>
/// </list>
///
/// This complements bootstrap CI (which estimates the range of the true metric) with
/// a formal hypothesis test (which asks "could random trading have produced this?").
///
/// <b>Multiple-comparisons correction (Šidák):</b><br/>
/// Because the winner was selected from N candidates during the optimisation search,
/// testing it at the nominal α = 0.05 inflates the false positive rate. The effective
/// family-wise α is 1 − (1 − 0.05)^N, which can be far above 5% for large N. The
/// Šidák correction inverts this: α_corrected = 1 − (1 − α)^(1/N), ensuring the
/// family-wise error rate stays at α regardless of how many candidates were screened.
/// This is tighter than Bonferroni (α/N) because it accounts for independence.
///
/// <b>Known conservative bias:</b> Šidák assumes the N candidates are independent, but
/// TPE/GP candidates are iteratively correlated (each guided by previous results). This
/// overestimates the family-wise correction, making the test conservative (i.e. it may
/// reject truly significant strategies). This is acceptable for a trading engine where
/// false positives are more costly than false negatives.
/// </summary>
internal static class PermutationTestAnalyzer
{
    /// <summary>
    /// Runs a Monte Carlo permutation test with Šidák multiple-comparisons correction.
    /// Shuffles entire trades (reordering the full sequence) to break both the
    /// signal→outcome association AND temporal clustering. This is more conservative
    /// than shuffling only PnL values, which preserves entry timing structure and can
    /// underestimate significance in markets with regime clustering.
    /// </summary>
    /// <param name="trades">Original ordered trade list from the backtest.</param>
    /// <param name="observedScore">The actual health score achieved by the strategy.</param>
    /// <param name="initialBalance">Starting balance for metric computation.</param>
    /// <param name="candidatesEvaluated">
    /// Number of candidates screened during the optimisation search before this winner
    /// was selected. Used for Šidák correction. Pass 1 to disable correction.
    /// </param>
    /// <param name="familyWiseAlpha">Desired family-wise error rate (default 0.05).</param>
    /// <param name="iterations">Number of random permutations (default 1000).</param>
    /// <param name="seed">Random seed for reproducibility.</param>
    /// <returns>
    /// The raw p-value, the Šidák-corrected significance threshold, and whether
    /// the result is significant after correction.
    /// </returns>
    public static (double PValue, double CorrectedAlpha, bool IsSignificant) RunPermutationTest(
        IReadOnlyList<BacktestTrade> trades,
        decimal observedScore,
        decimal initialBalance,
        int candidatesEvaluated = 1,
        double familyWiseAlpha = 0.05,
        int iterations = 1000,
        int seed = 42)
    {
        if (trades.Count < 5)
            return (1.0, familyWiseAlpha, false); // Too few trades for meaningful test

        // Sanitize: NaN/Infinity observed scores cannot be meaningfully compared against
        // permuted scores. Treat as non-significant to prevent undefined comparison results.
        {
            double obsDouble = (double)observedScore;
            if (double.IsNaN(obsDouble) || double.IsInfinity(obsDouble))
                return (1.0, familyWiseAlpha, false);
        }

        // Šidák correction: α_corrected = 1 - (1 - α)^(1/N)
        // This is the per-comparison threshold that keeps the family-wise rate at α
        // when N independent tests are performed (selecting the best of N candidates).
        int n = Math.Max(1, candidatesEvaluated);
        double correctedAlpha = 1.0 - Math.Pow(1.0 - familyWiseAlpha, 1.0 / n);

        var rng         = new Random(seed);
        var tradeArray  = trades.ToArray();
        int nBetter     = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            // Fisher-Yates shuffle of entire trades (breaks temporal order completely).
            // This destroys both signal→outcome association and regime clustering,
            // giving a more accurate null distribution than PnL-only shuffling.
            var shuffled = (BacktestTrade[])tradeArray.Clone();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            decimal permScore = OptimizationHealthScorer.ComputeHealthScoreFromTrades(shuffled, initialBalance);
            if (permScore >= observedScore)
                nBetter++;
        }

        // +1 correction avoids p=0 (conservative adjustment for finite permutations)
        double pValue = (double)(nBetter + 1) / (iterations + 1);
        return (pValue, correctedAlpha, pValue < correctedAlpha);
    }
}
