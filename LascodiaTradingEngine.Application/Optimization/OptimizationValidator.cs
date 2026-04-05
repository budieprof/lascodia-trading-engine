using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Post-selection validation helpers for optimization candidates: walk-forward stability,
/// parameter sensitivity analysis, transaction cost stress testing, and temporal signal
/// correlation checking.
/// </summary>
internal sealed class OptimizationValidator
{
    private readonly IBacktestEngine _backtestEngine;
    private decimal _initialBalance = 10_000m;
    private ConcurrentDictionary<string, BacktestResult>? _cache;

    internal OptimizationValidator(IBacktestEngine backtestEngine) => _backtestEngine = backtestEngine;

    /// <summary>Sets the initial balance used for all backtest runs within this validator.</summary>
    internal void SetInitialBalance(decimal balance) => _initialBalance = balance;

    /// <summary>
    /// Enables per-run backtest result caching. Same (paramsJson, candle set) combinations
    /// are evaluated multiple times across phases (coarse → seed → fine). The cache prevents
    /// redundant computation. Call <see cref="ClearCache"/> at run end.
    /// </summary>
    internal void EnableCache() => _cache = new ConcurrentDictionary<string, BacktestResult>();

    /// <summary>Clears the backtest result cache. Call at the end of each optimization run.</summary>
    internal void ClearCache() => _cache = null;

    /// <summary>
    /// Anchored walk-forward stability check on out-of-sample data. Splits the OOS candles
    /// into anchored IS/OOS windows where the first portion of each fold is used for mini
    /// parameter selection (best of baseline vs winner) and the remainder is tested OOS.
    /// This is stronger than fixed-param walk-forward because it validates that the
    /// parameters remain competitive even when a fold-local re-selection is allowed.
    /// If the min score across windows is below the configured ratio of the max, the
    /// parameters are unstable. Window count adapts from 2-8 based on data size.
    /// </summary>
    internal async Task<(decimal AvgScore, bool IsStable)> WalkForwardValidateAsync(
        Strategy strategy, string paramsJson, List<Candle> oosCandles,
        BacktestOptions options, int timeoutSecs, CancellationToken ct,
        double minMaxRatio = 0.50)
    {
        // Timeframe-aware minimum: M1 needs more candles per window to be meaningful
        int minCandlesPerWindow = GetMinCandlesPerWindow(strategy.Timeframe);
        int windowCount = Math.Clamp(oosCandles.Count / minCandlesPerWindow, 2, 8);
        int chunkSize = oosCandles.Count / windowCount;
        if (chunkSize < minCandlesPerWindow) return (0m, true);

        // Each fold is split: first 60% for mini IS selection, last 40% for OOS test.
        // The IS portion picks the better of the original strategy params vs the winner
        // params, then the chosen params are tested on the OOS portion. This ensures
        // the winner params are locally competitive, not just globally best.
        const double foldIsSplit = 0.60;

        var scores = new List<decimal>();
        for (int i = 0; i < windowCount; i++)
        {
            var chunk = oosCandles.Skip(i * chunkSize).Take(chunkSize).ToList();
            if (chunk.Count < 15) continue;

            int foldIsCount  = Math.Max(10, (int)(chunk.Count * foldIsSplit));
            int foldOosCount = chunk.Count - foldIsCount;
            if (foldOosCount < 10) // Not enough OOS data for a meaningful test
            {
                // Fall back to fixed-param evaluation on the full chunk
                try
                {
                    var result = await RunWithTimeoutAsync(strategy, paramsJson, chunk, options, timeoutSecs, ct);
                    scores.Add(OptimizationHealthScorer.ComputeHealthScore(result));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                catch { continue; }
                continue;
            }

            var foldIs  = chunk.GetRange(0, foldIsCount);
            var foldOos = chunk.GetRange(foldIsCount, foldOosCount);

            try
            {
                // Mini IS selection: test winner params on fold IS
                var winnerIsResult = await RunWithTimeoutAsync(strategy, paramsJson, foldIs, options, timeoutSecs, ct);
                decimal winnerIsScore = OptimizationHealthScorer.ComputeHealthScore(winnerIsResult);

                // Also test baseline (current strategy params) on fold IS
                var baselineIsResult = await RunWithTimeoutAsync(strategy, strategy.ParametersJson, foldIs, options, timeoutSecs, ct);
                decimal baselineIsScore = OptimizationHealthScorer.ComputeHealthScore(baselineIsResult);

                // Pick whichever performs better on this fold's IS, then test OOS
                string selectedParams = winnerIsScore >= baselineIsScore ? paramsJson : strategy.ParametersJson;
                var oosResult = await RunWithTimeoutAsync(strategy, selectedParams, foldOos, options, timeoutSecs, ct);
                scores.Add(OptimizationHealthScorer.ComputeHealthScore(oosResult));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch { continue; }
        }

        // Require at least half the folds to succeed; otherwise we can't judge stability
        int minSuccessfulFolds = Math.Max(2, windowCount / 2);
        if (scores.Count < minSuccessfulFolds)
            return (0m, false); // Insufficient data = unstable, not "pass by default"

        decimal avg = scores.Sum() / scores.Count;
        decimal min = scores.Min();
        decimal max = scores.Max();

        bool stable = max == 0m || (min / max) >= (decimal)minMaxRatio;
        return (avg, stable);
    }

    /// <summary>
    /// Perturbs each numeric parameter by ±perturbPct and checks if the health score
    /// drops by more than 20%. Parameters on a cliff edge are dangerous.
    /// Also runs joint random perturbations to detect interaction effects between
    /// parameters that individually look stable but jointly shift the strategy off a cliff.
    /// Perturbations are evaluated in parallel for efficiency.
    /// </summary>
    internal async Task<(bool IsRobust, string Report)> SensitivityCheckAsync(
        Strategy strategy, string winnerParamsJson, List<Candle> candles,
        BacktestOptions options, int timeoutSecs, decimal baseScore,
        double perturbPct, CancellationToken ct,
        double degradationTolerance = 0.20, int maxParallel = 4)
    {
        Dictionary<string, JsonElement>? baseParams;
        try { baseParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(winnerParamsJson); }
        catch { return (false, "unparseable winner params — cannot verify sensitivity"); }
        if (baseParams is null || baseParams.Count == 0) return (true, "no params to perturb");

        var perturbations = new List<(string Label, string Json)>();

        // Univariate perturbations: perturb each parameter independently.
        // For near-zero parameters (|baseVal| < 1e-10), use additive perturbation
        // based on the parameter's known range; multiplicative perturbation would
        // produce negligible shifts for values near zero.
        foreach (var (key, val) in baseParams)
        {
            if (!val.TryGetDouble(out double baseVal)) continue;

            foreach (double dir in new[] { -perturbPct, perturbPct })
            {
                double newVal;
                if (Math.Abs(baseVal) < 1e-10)
                {
                    // Additive perturbation: shift by perturbPct of a reasonable scale.
                    // Use 1.0 as default scale if no range context available.
                    newVal = baseVal + dir;
                }
                else
                {
                    newVal = baseVal * (1.0 + dir);
                }

                var perturbed = new Dictionary<string, object>();
                foreach (var kv in baseParams)
                {
                    if (kv.Key == key)
                        perturbed[kv.Key] = newVal;
                    else if (kv.Value.TryGetDouble(out double d))
                        perturbed[kv.Key] = d;
                    else
                        perturbed[kv.Key] = kv.Value.Clone(); // Preserve non-numeric params (booleans, strings)
                }
                perturbations.Add(($"{key}={baseVal:F4}→{newVal:F4}", JsonSerializer.Serialize(perturbed)));
            }
        }

        // Joint perturbations: randomly perturb ALL numeric parameters simultaneously
        // to detect interaction effects invisible to univariate sweeps.
        // Seed with strategy ID only (not DayOfYear) for deterministic results within a run.
        var numericKeys = baseParams
            .Where(kv => kv.Value.TryGetDouble(out _))
            .Select(kv => kv.Key)
            .ToList();

        if (numericKeys.Count >= 2)
        {
            var rng = new Random(strategy.Id.GetHashCode());
            const int jointSamples = 8;
            for (int j = 0; j < jointSamples; j++)
            {
                var perturbed = new Dictionary<string, object>();
                var label = new List<string>();
                foreach (var (key, val) in baseParams)
                {
                    if (!numericKeys.Contains(key) || !val.TryGetDouble(out double baseVal))
                    {
                        // Preserve non-numeric params (booleans, strings, etc.)
                        if (val.TryGetDouble(out double nonPerturbVal))
                            perturbed[key] = nonPerturbVal;
                        else
                            perturbed[key] = val.Clone();
                        continue;
                    }

                    double newVal;
                    if (Math.Abs(baseVal) < 1e-10)
                    {
                        double additive = (rng.NextDouble() * 2.0 - 1.0) * perturbPct;
                        newVal = baseVal + additive;
                    }
                    else
                    {
                        double factor = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * perturbPct;
                        newVal = baseVal * factor;
                    }

                    perturbed[key] = newVal;
                    label.Add($"{key}={baseVal:F4}→{newVal:F4}");
                }
                perturbations.Add(($"joint[{j}]: {string.Join(", ", label)}", JsonSerializer.Serialize(perturbed)));
            }
        }

        var issues = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(perturbations, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallel,
            CancellationToken = ct
        }, async (p, pCt) =>
        {
            try
            {
                var result = await RunWithTimeoutAsync(strategy, p.Json, candles, options, timeoutSecs, pCt);
                decimal score = OptimizationHealthScorer.ComputeHealthScore(result);
                if (baseScore > 0 && (baseScore - score) / baseScore > (decimal)degradationTolerance)
                    issues.Add($"{p.Label}: -{(baseScore - score) / baseScore:P0}");
            }
            catch (OperationCanceledException) when (!pCt.IsCancellationRequested)
            {
                issues.Add($"{p.Label}: backtest timed out");
            }
            catch (Exception ex)
            {
                issues.Add($"{p.Label}: backtest failed ({ex.GetType().Name})");
            }
        });

        return (issues.IsEmpty, issues.IsEmpty ? $"all perturbations within {degradationTolerance:P0} tolerance" : string.Join("; ", issues));
    }

    /// <summary>
    /// Tests the winner under pessimistic transaction costs (2x spread, commission, slippage).
    /// If the score drops below the approval threshold, the strategy is cost-fragile.
    /// </summary>
    internal async Task<(bool IsRobust, decimal PessimisticScore)> CostSensitivitySweepAsync(
        Strategy strategy, string paramsJson, List<Candle> candles,
        BacktestOptions baseOptions, decimal approvalThreshold,
        int timeoutSecs, CancellationToken ct,
        double costMultiplier = 2.0)
    {
        decimal multiplier = (decimal)costMultiplier;
        var pessOptions = new BacktestOptions
        {
            SpreadPriceUnits   = baseOptions.SpreadPriceUnits * multiplier,
            CommissionPerLot   = baseOptions.CommissionPerLot * multiplier,
            SlippagePriceUnits = baseOptions.SlippagePriceUnits * multiplier,
            ContractSize       = baseOptions.ContractSize,
        };

        try
        {
            var result = await RunWithTimeoutAsync(strategy, paramsJson, candles, pessOptions, timeoutSecs, ct);
            decimal score = OptimizationHealthScorer.ComputeHealthScore(result);
            return (score >= approvalThreshold, score);
        }
        catch
        {
            return (false, 0m);
        }
    }

    /// <summary>
    /// Runs the winner on recent candles to extract trade entry timestamps, then compares
    /// with recent signals from other active strategies on the same symbol. High temporal
    /// overlap means the "new" strategy offers no diversification benefit.
    /// The overlap window adapts to the strategy's timeframe — an M1 strategy and an H4
    /// strategy have very different signal cadences, so a fixed 1-hour window would be
    /// too tight for D1 or too loose for M1.
    /// </summary>
    internal async Task<(bool IsSafe, double MaxOverlap)> TemporalSignalCorrelationCheckAsync(
        Strategy strategy, string winnerParamsJson, List<Candle> recentCandles,
        BacktestOptions options, int timeoutSecs, DbContext db,
        double maxOverlapThreshold, Timeframe timeframe, CancellationToken ct)
    {
        BacktestResult winnerResult;
        try
        {
            winnerResult = await RunWithTimeoutAsync(strategy, winnerParamsJson, recentCandles, options, timeoutSecs, ct);
        }
        catch { return (true, 0.0); }

        if (winnerResult.TotalTrades < 5) return (true, 0.0);

        var winnerEntryTimes = winnerResult.Trades.Select(t => t.EntryTime).ToList();

        var recentSignalGroups = await db.Set<TradeSignal>()
            .Where(s => s.StrategyId != strategy.Id
                     && s.Strategy!.Symbol == strategy.Symbol
                     && s.Strategy.Status == StrategyStatus.Active
                     && s.GeneratedAt >= DateTime.UtcNow.AddDays(-30)
                     && !s.IsDeleted)
            .GroupBy(s => s.StrategyId)
            .Select(g => g.Select(s => s.GeneratedAt).ToList())
            .ToListAsync(ct);

        // Adaptive overlap window: signals within this many hours of each other
        // are considered "the same signal". Scales with timeframe so that D1
        // strategies use a 24h window while M1 uses 0.25h (15 min).
        double overlapWindowHours = GetOverlapWindowHours(timeframe);

        double maxOverlap = 0;
        foreach (var otherEntries in recentSignalGroups)
        {
            int overlapping = winnerEntryTimes.Count(entry =>
                otherEntries.Any(e => Math.Abs((entry - e).TotalHours) < overlapWindowHours));
            double ratio = (double)overlapping / winnerEntryTimes.Count;
            maxOverlap = Math.Max(maxOverlap, ratio);
        }

        return (maxOverlap < maxOverlapThreshold, maxOverlap);
    }

    /// <summary>
    /// Returns the minimum candles required per walk-forward window, scaled by timeframe.
    /// M1 needs far more candles than D1 to represent a meaningful evaluation period.
    /// </summary>
    private static int GetMinCandlesPerWindow(Timeframe tf) => tf switch
    {
        Timeframe.M1  => 200,  // 200 minutes ≈ 3.3 hours
        Timeframe.M5  => 100,  // 500 minutes ≈ 8.3 hours
        Timeframe.M15 => 40,   // 600 minutes ≈ 10 hours
        Timeframe.H1  => 20,   // 20 hours
        Timeframe.H4  => 10,   // 40 hours ≈ 1.7 days
        Timeframe.D1  => 5,    // 5 trading days
        _             => 20,
    };

    /// <summary>Maps a timeframe to an appropriate temporal overlap window in hours.</summary>
    private static double GetOverlapWindowHours(Timeframe tf) => tf switch
    {
        Timeframe.M1  => 0.25,  // 15 minutes
        Timeframe.M5  => 0.5,   // 30 minutes
        Timeframe.M15 => 1.0,   // 1 hour
        Timeframe.H1  => 2.0,   // 2 hours
        Timeframe.H4  => 8.0,   // 8 hours
        Timeframe.D1  => 24.0,  // 24 hours
        _             => 1.0,
    };

    /// <summary>
    /// Temporal chunked evaluation: evaluates a candidate's parameters on K non-overlapping
    /// time segments with embargo gaps between chunks. Each chunk runs the fixed parameters
    /// (no re-training), measuring consistency across time periods.
    /// </summary>
    /// <summary>
    /// Evaluates a candidate over temporal folds and returns the mean score together with
    /// the coefficient of variation across successful folds. Returning CV per-call keeps
    /// approval gating candidate-specific and avoids cross-talk between parallel evaluations.
    /// </summary>
    internal async Task<(decimal MeanScore, BacktestResult LastResult, double CvCoefficientOfVariation)> TemporalChunkedEvaluateAsync(
        Strategy strategy, string paramsJson, List<Candle> trainCandles,
        BacktestOptions options, int timeoutSecs, int kFolds, int embargoPerFold,
        int minTrades, CancellationToken ct)
    {
        int foldSize = trainCandles.Count / kFolds;
        if (foldSize < 30)
        {
            var result = await RunWithTimeoutAsync(strategy, paramsJson, trainCandles, options, timeoutSecs, ct);
            return (OptimizationHealthScorer.ComputeHealthScore(result), result, 0.0);
        }

        var scores = new List<decimal>();
        BacktestResult? lastResult = null;

        for (int k = 0; k < kFolds; k++)
        {
            int testStart = k * foldSize + embargoPerFold;
            int testEnd   = Math.Min((k + 1) * foldSize - embargoPerFold, trainCandles.Count);
            if (testEnd - testStart < 20) continue;

            var testFold = trainCandles.GetRange(testStart, testEnd - testStart);
            try
            {
                var result = await RunWithTimeoutAsync(strategy, paramsJson, testFold, options, timeoutSecs, ct);
                if (result.TotalTrades >= minTrades)
                {
                    scores.Add(OptimizationHealthScorer.ComputeHealthScore(result));
                    lastResult = result;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch { continue; }
        }

        if (scores.Count == 0 || lastResult is null)
            throw new InvalidOperationException("All CV folds failed or produced too few trades.");

        decimal mean = scores.Sum() / scores.Count;

        // Compute coefficient of variation for cross-fold consistency check
        double cvCoefficientOfVariation;
        if (scores.Count >= 2 && mean > 0)
        {
            double variance = scores.Sum(s => (double)(s - mean) * (double)(s - mean)) / (scores.Count - 1);
            cvCoefficientOfVariation = Math.Sqrt(variance) / (double)mean;
        }
        else
        {
            cvCoefficientOfVariation = 0;
        }

        return (mean, lastResult, cvCoefficientOfVariation);
    }

    /// <summary>
    /// Combinatorial Purged Cross-Validation (CPCV) — generates all C(N, K) train/test
    /// split combinations from N temporal folds, with purge + embargo between each IS/OOS
    /// boundary. Returns a distribution of health scores rather than a single point estimate,
    /// providing a more statistically robust view of strategy performance.
    ///
    /// Reference: de Prado, M. L. (2018). Advances in Financial Machine Learning, Ch. 12.
    ///
    /// For computational tractability, when the number of combinations exceeds maxCombinations,
    /// a deterministic random subset is sampled.
    /// </summary>
    internal async Task<(decimal MeanScore, decimal StdScore, double CvCoefficient, int CombinationsEvaluated, IReadOnlyList<decimal> Scores)> CpcvEvaluateAsync(
        Strategy strategy, string paramsJson, List<Candle> candles,
        BacktestOptions options, int timeoutSecs,
        int nFolds, int testFoldCount, int embargoCandles,
        int minTrades, int maxCombinations, int seed,
        CancellationToken ct, int maxParallelism = 4)
    {
        if (nFolds < 3 || testFoldCount < 1 || testFoldCount >= nFolds)
            throw new ArgumentException($"CPCV requires nFolds >= 3 and 1 <= testFoldCount < nFolds (got {nFolds}, {testFoldCount})");

        int foldSize = candles.Count / nFolds;
        if (foldSize < 20)
        {
            var result = await RunWithTimeoutAsync(strategy, paramsJson, candles, options, timeoutSecs, ct);
            var score = OptimizationHealthScorer.ComputeHealthScore(result);
            return (score, 0m, 0.0, 1, [score]);
        }

        // Generate all C(nFolds, testFoldCount) combinations of test fold indices
        var allCombinations = GenerateCombinations(nFolds, testFoldCount);
        var rng = new Random(seed);

        // Sample if too many combinations
        IList<int[]> selectedCombinations;
        if (allCombinations.Count <= maxCombinations)
        {
            selectedCombinations = allCombinations;
        }
        else
        {
            // Fisher-Yates shuffle on indices, take first maxCombinations
            var indices = Enumerable.Range(0, allCombinations.Count).ToArray();
            for (int i = indices.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            selectedCombinations = indices.Take(maxCombinations).Select(i => allCombinations[i]).ToList();
        }

        // Pre-build all fold splits (CPU-only, no IO) so the parallel phase is pure evaluation
        var foldSplits = new List<(List<Candle> Train, List<Candle> Test)>();
        foreach (var testFoldIndices in selectedCombinations)
        {
            var testFoldSet = new HashSet<int>(testFoldIndices);
            var trainList = new List<Candle>();
            var testList = new List<Candle>();

            for (int fold = 0; fold < nFolds; fold++)
            {
                int start = fold * foldSize;
                int end = (fold == nFolds - 1) ? candles.Count : (fold + 1) * foldSize;

                if (testFoldSet.Contains(fold))
                {
                    testList.AddRange(candles.GetRange(start, end - start));
                }
                else
                {
                    bool adjacentToTest = testFoldSet.Contains(fold - 1) || testFoldSet.Contains(fold + 1);

                    if (adjacentToTest && embargoCandles > 0)
                    {
                        int purgeStart = testFoldSet.Contains(fold - 1) ? embargoCandles : 0;
                        int purgeEnd = testFoldSet.Contains(fold + 1) ? embargoCandles : 0;
                        int safeStart = start + purgeStart;
                        int safeEnd = end - purgeEnd;
                        if (safeStart < safeEnd)
                            trainList.AddRange(candles.GetRange(safeStart, safeEnd - safeStart));
                    }
                    else
                    {
                        trainList.AddRange(candles.GetRange(start, end - start));
                    }
                }
            }

            if (trainList.Count >= 30 && testList.Count >= 10)
                foldSplits.Add((trainList, testList));
        }

        // Evaluate all combinations in parallel — they are independent
        var scores = new ConcurrentBag<decimal>();
        await Parallel.ForEachAsync(foldSplits, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxParallelism),
            CancellationToken = ct,
        }, async (split, pCt) =>
        {
            try
            {
                var testResult = await RunWithTimeoutAsync(strategy, paramsJson, split.Test, options, timeoutSecs, pCt);
                if (testResult.TotalTrades >= minTrades)
                    scores.Add(OptimizationHealthScorer.ComputeHealthScore(testResult));
            }
            catch (OperationCanceledException) when (!pCt.IsCancellationRequested) { /* timeout */ }
            catch { /* skip failed combination */ }
        });

        var scoreList = scores.ToList();
        if (scoreList.Count < 2)
        {
            decimal singleScore = scoreList.Count == 1 ? scoreList[0] : 0m;
            return (singleScore, 0m, 0.0, scoreList.Count, scoreList);
        }

        decimal mean = scoreList.Sum() / scoreList.Count;
        double variance = scoreList.Sum(s => (double)(s - mean) * (double)(s - mean)) / (scoreList.Count - 1);
        decimal std = (decimal)Math.Sqrt(variance);
        double cv = mean > 0 ? Math.Sqrt(variance) / (double)mean : 0;

        return (mean, std, cv, scoreList.Count, scoreList);
    }

    /// <summary>Generates all C(n, k) combinations of k indices from [0..n-1].</summary>
    private static List<int[]> GenerateCombinations(int n, int k)
    {
        var results = new List<int[]>();
        var current = new int[k];
        GenerateCombinationsRecurse(results, current, 0, n, 0);
        return results;
    }

    private static void GenerateCombinationsRecurse(List<int[]> results, int[] current, int start, int n, int depth)
    {
        if (depth == current.Length)
        {
            results.Add((int[])current.Clone());
            return;
        }
        for (int i = start; i <= n - (current.Length - depth); i++)
        {
            current[depth] = i;
            GenerateCombinationsRecurse(results, current, i + 1, n, depth + 1);
        }
    }

    /// <summary>
    /// Portfolio-level impact check: computes Pearson correlation between the winner's
    /// OOS trade PnL series and the recent performance snapshots of each other active
    /// strategy — including strategies on correlated instruments (cross-asset).
    ///
    /// Same-symbol strategies are always checked. Cross-asset strategies are checked when
    /// the instruments share a common currency (e.g. EURUSD and GBPUSD both have USD,
    /// AUDUSD and NZDUSD both have AUD/USD exposure). This catches hidden portfolio-level
    /// correlation that per-symbol checks would miss.
    /// </summary>
    internal async Task<(bool IsSafe, double MaxCorrelation)> PortfolioCorrelationCheckAsync(
        Strategy strategy, BacktestResult oosResult, DbContext db,
        double maxCorrelation, CancellationToken ct)
    {
        if (oosResult.Trades is null || oosResult.Trades.Count < 10) return (true, 0.0);

        // Build a daily PnL series from the winner's OOS trades
        var winnerDailyPnl = oosResult.Trades
            .GroupBy(t => t.ExitTime.Date)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.PnL));

        if (winnerDailyPnl.Count < 5) return (true, 0.0);

        // Find correlated symbols: instruments sharing a common currency with
        // this strategy's symbol. E.g. if optimizing EURUSD, also check GBPUSD,
        // USDCHF, USDJPY (all share USD). This catches cross-asset exposure overlap.
        var thisPair = await db.Set<CurrencyPair>()
            .Where(p => p.Symbol == strategy.Symbol && !p.IsDeleted)
            .Select(p => new { p.BaseCurrency, p.QuoteCurrency })
            .FirstOrDefaultAsync(ct);

        var correlatedSymbols = new HashSet<string> { strategy.Symbol };
        if (thisPair is not null)
        {
            var relatedSymbols = await db.Set<CurrencyPair>()
                .Where(p => !p.IsDeleted && p.Symbol != strategy.Symbol
                         && (p.BaseCurrency == thisPair.BaseCurrency
                          || p.BaseCurrency == thisPair.QuoteCurrency
                          || p.QuoteCurrency == thisPair.BaseCurrency
                          || p.QuoteCurrency == thisPair.QuoteCurrency))
                .Select(p => p.Symbol)
                .ToListAsync(ct);

            foreach (var sym in relatedSymbols)
                correlatedSymbols.Add(sym);
        }

        // Load recent performance snapshots from all active strategies on same or correlated symbols
        var otherSnapshots = await db.Set<StrategyPerformanceSnapshot>()
            .Where(s => s.StrategyId != strategy.Id
                     && s.Strategy.Status == StrategyStatus.Active
                     && correlatedSymbols.Contains(s.Strategy.Symbol)
                     && s.EvaluatedAt >= DateTime.UtcNow.AddDays(-60)
                     && !s.IsDeleted)
            .GroupBy(s => s.StrategyId)
            .Select(g => g.OrderBy(s => s.EvaluatedAt)
                          .Select(s => new { Date = s.EvaluatedAt.Date, s.TotalPnL })
                          .ToList())
            .ToListAsync(ct);

        double highestCorrelation = 0;
        foreach (var snapshots in otherSnapshots)
        {
            var otherDaily = snapshots
                .GroupBy(s => s.Date)
                .ToDictionary(g => g.Key, g => (double)g.Last().TotalPnL);

            var commonDates = winnerDailyPnl.Keys.Intersect(otherDaily.Keys).OrderBy(d => d).ToList();
            if (commonDates.Count < 5) continue;

            var xs = commonDates.Select(d => winnerDailyPnl[d]).ToArray();
            var ys = commonDates.Select(d => otherDaily[d]).ToArray();
            // Only flag positive correlation (amplifying risk). Negative correlation
            // is desirable — it indicates hedging and improves portfolio diversification.
            double corr = Math.Max(0, PearsonCorrelation(xs, ys));
            highestCorrelation = Math.Max(highestCorrelation, corr);
        }

        return (highestCorrelation < maxCorrelation, highestCorrelation);
    }

    /// <summary>Computes Pearson correlation coefficient between two arrays.</summary>
    private static double PearsonCorrelation(double[] x, double[] y)
    {
        int n = x.Length;
        if (n < 2) return 0;

        double meanX = x.Average(), meanY = y.Average();
        double cov = 0, varX = 0, varY = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX, dy = y[i] - meanY;
            cov  += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }

        double denom = Math.Sqrt(varX * varY);
        return denom > 1e-12 ? cov / denom : 0;
    }

    /// <summary>
    /// Pre-flight data quality validation. Checks for duplicate timestamps, excessive gaps,
    /// and staleness. Returns (isValid, issues) — if not valid, optimization should not proceed.
    /// </summary>
    /// <param name="holidayDates">
    /// Optional set of known holiday/bank-holiday dates from EconomicEvent table.
    /// When provided, the staleness check extends its tolerance to cover multi-day
    /// holiday closures (e.g. Christmas, New Year) instead of using the fixed 65h window.
    /// Pass null to fall back to weekend-only tolerance.
    /// </param>
    internal static (bool IsValid, string Issues) ValidateCandleQuality(
        List<Candle> candles, Timeframe timeframe, int maxGapMultiplier = 5,
        decimal outlierBarRangeThreshold = 0.10m,
        HashSet<DateTime>? holidayDates = null)
    {
        if (candles.Count < 2) return (false, "fewer than 2 candles");

        var issues = new List<string>();

        // Check for duplicate timestamps
        var timestamps = candles.Select(c => c.Timestamp).ToList();
        int duplicates = timestamps.Count - timestamps.Distinct().Count();
        if (duplicates > 0)
            issues.Add($"{duplicates} duplicate timestamp(s)");

        // Check for excessive gaps (more than maxGapMultiplier × expected bar duration)
        double expectedBarMinutes = timeframe switch
        {
            Timeframe.M1  => 1,
            Timeframe.M5  => 5,
            Timeframe.M15 => 15,
            Timeframe.H1  => 60,
            Timeframe.H4  => 240,
            Timeframe.D1  => 1440,
            _             => 60,
        };

        double maxGapMinutes = expectedBarMinutes * maxGapMultiplier;
        int largeGaps = 0;
        for (int i = 1; i < candles.Count; i++)
        {
            double gap = (candles[i].Timestamp - candles[i - 1].Timestamp).TotalMinutes;

            // Allow weekend gaps (up to ~3 days) and holiday gaps.
            // If holiday dates are provided, count consecutive non-trading days from
            // the previous candle forward; extend the allowed gap accordingly.
            double allowedGapMinutes = 4320; // 3 days default (weekend)
            if (holidayDates is not null && gap > maxGapMinutes)
            {
                var gapStart = candles[i - 1].Timestamp.Date.AddDays(1);
                int consecutiveClosedDays = 0;
                for (var d = gapStart; d <= candles[i].Timestamp.Date; d = d.AddDays(1))
                {
                    bool isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                    if (isWeekend || holidayDates.Contains(d))
                        consecutiveClosedDays++;
                    else
                        break;
                }
                // Allow (closed days + 1 buffer day) × 24h × 60min
                allowedGapMinutes = Math.Max(allowedGapMinutes, (consecutiveClosedDays + 1) * 1440.0);
            }

            if (gap > maxGapMinutes && gap < allowedGapMinutes)
                continue; // Acceptable gap (weekend or holiday)
            if (gap > maxGapMinutes)
                largeGaps++;
        }
        if (largeGaps > candles.Count * 0.05) // More than 5% gaps
            issues.Add($"{largeGaps} gaps exceeding {maxGapMultiplier}x expected bar duration");

        // Check OHLC invariants: High must be >= Low, prices must be positive
        int invalidOhlc = 0;
        int negativePrices = 0;
        int outlierCandles = 0;
        int frozenBars = 0;

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];

            if (c.High < c.Low)
                invalidOhlc++;

            if (c.Open <= 0 || c.High <= 0 || c.Low <= 0 || c.Close <= 0)
                negativePrices++;

            // Frozen price detection: consecutive bars with identical OHLC indicate a stalled
            // data feed or illiquid instrument. These produce zero-range bars that generate no
            // signals, making backtest results unreliable.
            if (i > 0)
            {
                var p = candles[i - 1];
                if (c.Open == p.Open && c.High == p.High && c.Low == p.Low && c.Close == p.Close)
                    frozenBars++;
            }

            // Outlier detection: configurable threshold (default 10%, higher for volatile assets like crypto)
            if (c.High > 0 && c.Low > 0)
            {
                decimal barRange = (c.High - c.Low) / c.Low;
                if (barRange > outlierBarRangeThreshold)
                    outlierCandles++;
            }
        }

        if (invalidOhlc > 0)
            issues.Add($"{invalidOhlc} candle(s) with High < Low (corrupted OHLC data)");

        if (negativePrices > 0)
            issues.Add($"{negativePrices} candle(s) with non-positive prices");

        if (outlierCandles > candles.Count * 0.02) // More than 2% outliers
            issues.Add($"{outlierCandles} candle(s) with >{outlierBarRangeThreshold:P0} single-bar range (possible data errors)");

        if (frozenBars > candles.Count * 0.02) // More than 2% frozen
            issues.Add($"{frozenBars} consecutive identical OHLC bar(s) (frozen/stale prices — data feed stall or illiquid instrument)");

        // Check staleness — most recent candle should be within reasonable window.
        // Holiday-aware: if holidays fall between the most recent candle and now,
        // extend tolerance by the number of consecutive closed days.
        double stalenessHours = expectedBarMinutes / 60.0 * 10; // 10 bars worth
        var mostRecent = candles[^1].Timestamp;
        double hoursStale = (DateTime.UtcNow - mostRecent).TotalHours;
        double maxStaleHours = Math.Max(stalenessHours, 65); // 65h covers Friday close → Monday open

        if (holidayDates is not null && hoursStale > maxStaleHours)
        {
            // Count consecutive non-trading days from the most recent candle forward
            int closedDays = 0;
            for (var d = mostRecent.Date.AddDays(1); d <= DateTime.UtcNow.Date; d = d.AddDays(1))
            {
                bool isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if (isWeekend || holidayDates.Contains(d))
                    closedDays++;
                else
                    break;
            }
            // Extend tolerance: each closed day adds 24h + buffer
            maxStaleHours = Math.Max(maxStaleHours, (closedDays + 1) * 24.0 + 17.0); // +17h for session open delay
        }

        if (hoursStale > maxStaleHours)
            issues.Add($"most recent candle is {hoursStale:F0}h old (stale data)");

        return (issues.Count == 0, issues.Count == 0 ? "ok" : string.Join("; ", issues));
    }

    /// <summary>
    /// Imputes minor candle gaps (1-2 missing bars) by inserting synthetic candles with
    /// OHLC = previous close and zero volume. For gaps on known holidays or weekends,
    /// no imputation is needed (they're natural market closures). Returns the candle list
    /// with gaps filled and a count of imputed bars for logging.
    /// </summary>
    internal static (List<Candle> Candles, int ImputedCount) ImputeMinorGaps(
        List<Candle> candles, Timeframe timeframe, int maxImputeBars = 2,
        HashSet<DateTime>? holidayDates = null)
    {
        if (candles.Count < 2) return (candles, 0);

        double expectedBarMinutes = timeframe switch
        {
            Timeframe.M1  => 1,
            Timeframe.M5  => 5,
            Timeframe.M15 => 15,
            Timeframe.H1  => 60,
            Timeframe.H4  => 240,
            Timeframe.D1  => 1440,
            _             => 60,
        };

        var result = new List<Candle>(candles.Count + 10); // Pre-size with small headroom
        result.Add(candles[0]);
        int imputed = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            double gapMinutes = (candles[i].Timestamp - candles[i - 1].Timestamp).TotalMinutes;
            int missingBars = (int)(gapMinutes / expectedBarMinutes) - 1;

            if (missingBars > 0 && missingBars <= maxImputeBars)
            {
                // Check if the gap spans only weekends/holidays — skip imputation
                bool isNaturalClosure = true;
                var gapStart = candles[i - 1].Timestamp;
                for (int j = 1; j <= missingBars; j++)
                {
                    var barTime = gapStart.AddMinutes(expectedBarMinutes * j);
                    bool isWeekend = barTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                    bool isHoliday = holidayDates?.Contains(barTime.Date) == true;
                    if (!isWeekend && !isHoliday)
                    {
                        isNaturalClosure = false;
                        break;
                    }
                }

                if (!isNaturalClosure)
                {
                    var prevClose = candles[i - 1].Close;
                    for (int j = 1; j <= missingBars; j++)
                    {
                        result.Add(new Candle
                        {
                            Symbol    = candles[i - 1].Symbol,
                            Timeframe = timeframe,
                            Timestamp = gapStart.AddMinutes(expectedBarMinutes * j),
                            Open      = prevClose,
                            High      = prevClose,
                            Low       = prevClose,
                            Close     = prevClose,
                            Volume    = 0,
                            IsClosed  = true,
                        });
                        imputed++;
                    }
                }
            }

            result.Add(candles[i]);
        }

        return (result, imputed);
    }

    /// <summary>Runs a backtest with timeout and transaction costs.</summary>
    internal async Task<BacktestResult> RunWithTimeoutAsync(
        Strategy strategy, string paramsJson, List<Candle> candles,
        BacktestOptions options, int timeoutSecs, CancellationToken ct)
    {
        // Check cache: same params + same candle set = same result.
        // Hash includes count, boundary timestamps, and sampled intermediate timestamps
        // to avoid collisions between lists with identical boundaries but different data.
        string? cacheKey = null;
        if (_cache is not null)
        {
            var hash = new HashCode();
            hash.Add(candles.Count);
            if (candles.Count > 0)
            {
                hash.Add(candles[0].Timestamp.Ticks);
                hash.Add(candles[^1].Timestamp.Ticks);
                // Sample up to 8 evenly-spaced intermediate timestamps for collision resistance
                int step = Math.Max(1, candles.Count / 8);
                for (int i = step; i < candles.Count - 1; i += step)
                    hash.Add(candles[i].Timestamp.Ticks);
            }
            hash.Add(options.SpreadPriceUnits);
            hash.Add(options.CommissionPerLot);
            hash.Add(options.SlippagePriceUnits);
            cacheKey = $"{paramsJson}:{hash.ToHashCode()}";

            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var candidate = CloneStrategy(strategy);
        candidate.ParametersJson = paramsJson;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));
        var result = await _backtestEngine.RunAsync(candidate, candles, _initialBalance, cts.Token, options);

        if (cacheKey is not null)
            _cache!.TryAdd(cacheKey, result);

        return result;
    }

    internal static Strategy CloneStrategy(Strategy source) => new()
    {
        Id                      = source.Id,
        Name                    = source.Name,
        Description             = source.Description,
        StrategyType            = source.StrategyType,
        Symbol                  = source.Symbol,
        Timeframe               = source.Timeframe,
        ParametersJson          = source.ParametersJson,
        Status                  = source.Status,
        RiskProfileId           = source.RiskProfileId,
        CreatedAt               = source.CreatedAt,
        LifecycleStage          = source.LifecycleStage,
        LifecycleStageEnteredAt = source.LifecycleStageEnteredAt,
        EstimatedCapacityLots   = source.EstimatedCapacityLots,
        IsDeleted               = source.IsDeleted
    };
}
