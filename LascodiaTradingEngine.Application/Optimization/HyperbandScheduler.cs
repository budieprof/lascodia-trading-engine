using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Hyperband scheduler for multi-fidelity parameter screening. Runs multiple successive
/// halving brackets simultaneously, each exploring a different aggressiveness level,
/// then pools all survivors. This eliminates the need for operators to guess the right
/// fidelity rung configuration — Hyperband automatically allocates budget across brackets
/// with different n/r tradeoffs.
///
/// <b>Reference:</b> Li, L., Jamieson, K., DeSalvo, G., Rostamizadeh, A., &amp; Talwalkar, A.
/// (2018). "Hyperband: A Novel Bandit-Based Approach to Hyperparameter Optimization."
/// Journal of Machine Learning Research, 18(185), 1-52.
///
/// <b>Adaptation for trading:</b> "Resource" = fidelity (fraction of training candles used
/// for backtest). Higher fidelity = more candles = slower but more accurate evaluation.
/// Low-fidelity evaluations downsample candles by selecting every Nth bar.
///
/// <b>Production hardening:</b>
/// <list type="bullet">
///   <item>Per-bracket cancellation + aggregate timeout propagation</item>
///   <item>Circuit breaker: consecutive backtest failures within a bracket abort that bracket</item>
///   <item>Noise-adaptive promotion thresholds: lower fidelity = looser threshold</item>
///   <item>Graceful degradation: scarce data (few candles) produces fewer brackets naturally</item>
///   <item>Budget cap enforcement: total evaluations across all brackets ≤ configured limit</item>
///   <item>Deduplication: candidates appearing in multiple bracket survivors are merged</item>
///   <item>Per-bracket timing + diagnostic logging</item>
///   <item>Minimum candle guard: bracket skipped if downsampled candles &lt; 30</item>
/// </list>
/// </summary>
internal sealed class HyperbandScheduler
{
    private readonly ILogger _logger;
    private readonly TradingMetrics _metrics;

    internal HyperbandScheduler(ILogger logger, TradingMetrics metrics)
    {
        _logger  = logger;
        _metrics = metrics;
    }

    // ── Data structures ────────────────────────────────────────────────────

    /// <summary>
    /// One successive halving bracket. Each bracket represents a specific tradeoff
    /// between number of initial candidates and starting resource (fidelity).
    /// </summary>
    internal sealed record Bracket(
        int Index,
        int InitialCandidates,
        double[] FidelityRungs,
        int[] CandidatesPerRung);

    /// <summary>Result from executing all Hyperband brackets.</summary>
    internal sealed record HyperbandResult(
        List<ScoredCandidateWithFidelity> Survivors,
        int TotalEvaluations,
        int BracketsExecuted,
        int BracketsSkipped);

    /// <summary>A scored candidate with metadata about the fidelity at which it was evaluated.</summary>
    internal sealed record ScoredCandidateWithFidelity(
        string ParamsJson,
        decimal HealthScore,
        BacktestResult Result,
        double EvaluatedAtFidelity,
        int SourceBracket);

    // ── Bracket computation ────────────────────────────────────────────────

    /// <summary>
    /// Computes Hyperband brackets from the standard formula.
    /// <paramref name="eta"/> is the reduction factor (standard: 3).
    /// <paramref name="maxFidelity"/> is 1.0 (full candles).
    /// <paramref name="minFidelity"/> is the smallest useful fraction (e.g., 30/totalCandles).
    /// <paramref name="budgetPerBracket"/> caps the total evaluations per bracket.
    /// </summary>
    internal static List<Bracket> ComputeBrackets(
        int eta, double maxFidelity, double minFidelity, int budgetPerBracket)
    {
        if (eta < 2) eta = 3;
        if (minFidelity <= 0 || minFidelity >= maxFidelity)
            return [new Bracket(0, budgetPerBracket, [maxFidelity], [budgetPerBracket])];

        int sMax = Math.Max(0, (int)Math.Floor(Math.Log(maxFidelity / minFidelity) / Math.Log(eta)));

        // Cap at 5 brackets — more than that offers diminishing returns and wastes
        // budget on ultra-low-fidelity evaluations where candle downsampling noise
        // dominates the signal.
        sMax = Math.Min(sMax, 5);

        var brackets = new List<Bracket>();

        for (int s = sMax; s >= 0; s--)
        {
            // n = initial candidates for this bracket
            // Standard Hyperband: n = ceil(sMax+1 / (s+1)) * eta^s
            // But we cap by budgetPerBracket to control total spend.
            int n = (int)Math.Ceiling((double)(sMax + 1) / (s + 1) * Math.Pow(eta, s));
            n = Math.Clamp(n, 1, budgetPerBracket);

            // r = starting fidelity for this bracket
            double r = maxFidelity * Math.Pow(eta, -s);
            r = Math.Max(r, minFidelity);

            // Build the rung schedule: fidelity doubles (×eta) at each rung,
            // candidates reduce by factor of eta
            var fidelities = new List<double>();
            var candidateCounts = new List<int>();
            double currentFidelity = r;
            int currentN = n;

            for (int i = 0; i <= s; i++)
            {
                double clampedFidelity = Math.Min(currentFidelity, maxFidelity);
                fidelities.Add(clampedFidelity);
                candidateCounts.Add(currentN);

                currentFidelity *= eta;
                currentN = Math.Max(1, (int)Math.Floor((double)currentN / eta));
            }

            brackets.Add(new Bracket(
                Index: sMax - s,
                InitialCandidates: n,
                FidelityRungs: fidelities.ToArray(),
                CandidatesPerRung: candidateCounts.ToArray()));
        }

        return brackets;
    }

    // ── Bracket execution ──────────────────────────────────────────────────

    /// <summary>
    /// Executes all Hyperband brackets, each with its own successive halving schedule.
    /// Respects the global budget cap and propagates cancellation.
    /// </summary>
    /// <param name="brackets">Bracket schedule from <see cref="ComputeBrackets"/>.</param>
    /// <param name="candidateSource">
    /// Function that produces N candidate parameter sets for a bracket. Called once per
    /// bracket. The scheduler passes the desired count; the source may return fewer if
    /// the parameter space is exhausted (the bracket adapts).
    /// </param>
    /// <param name="trainCandles">Full training candle set. Downsampled per fidelity level.</param>
    /// <param name="strategy">Strategy being optimized (for evaluator dispatch).</param>
    /// <param name="screeningOptions">Transaction cost options for backtests.</param>
    /// <param name="validator">Backtest runner with timeout + caching.</param>
    /// <param name="baselineScore">Current strategy health score. Used for promotion threshold.</param>
    /// <param name="maxParallel">Max concurrent backtest evaluations.</param>
    /// <param name="screeningTimeoutSeconds">Per-backtest timeout in seconds.</param>
    /// <param name="circuitBreakerThreshold">Consecutive failures before aborting a bracket.</param>
    /// <param name="globalBudgetRemaining">
    /// Total evaluations remaining across all brackets. Decremented atomically. When
    /// exhausted, remaining brackets are skipped.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<HyperbandResult> ExecuteAllBracketsAsync(
        List<Bracket> brackets,
        Func<int, int, List<string>> candidateSource,
        List<Candle> trainCandles,
        Strategy strategy,
        BacktestOptions screeningOptions,
        OptimizationValidator validator,
        decimal baselineScore,
        int maxParallel,
        int screeningTimeoutSeconds,
        int circuitBreakerThreshold,
        int globalBudgetRemaining,
        CancellationToken ct)
    {
        var allSurvivors = new List<ScoredCandidateWithFidelity>();
        int totalEvals = 0;
        int bracketsExecuted = 0;
        int bracketsSkipped = 0;

        foreach (var bracket in brackets)
        {
            ct.ThrowIfCancellationRequested();

            if (globalBudgetRemaining <= 0)
            {
                bracketsSkipped++;
                _metrics.HyperbandBracketsSkipped.Add(1);
                _logger.LogDebug(
                    "HyperbandScheduler: skipping bracket {Index} — global budget exhausted",
                    bracket.Index);
                continue;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var (survivors, evals) = await ExecuteSingleBracketAsync(
                    bracket, candidateSource, trainCandles, strategy, screeningOptions,
                    validator, baselineScore, maxParallel, screeningTimeoutSeconds,
                    circuitBreakerThreshold, globalBudgetRemaining, ct);

                totalEvals += evals;
                globalBudgetRemaining -= evals;
                allSurvivors.AddRange(survivors);
                bracketsExecuted++;

                sw.Stop();
                _logger.LogDebug(
                    "HyperbandScheduler: bracket {Index} completed — {Evals} evals, {Survivors} survivors, " +
                    "fidelity {StartF:P0}→{EndF:P0}, {Ms:F0}ms",
                    bracket.Index, evals, survivors.Count,
                    bracket.FidelityRungs[0],
                    bracket.FidelityRungs[^1],
                    sw.Elapsed.TotalMilliseconds);

                _metrics.OptimizationPhaseDurationMs.Record(
                    sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("phase", $"hyperband_bracket_{bracket.Index}"));
                _metrics.HyperbandBracketsExecuted.Add(1);

                // Survival rate: what fraction of initial candidates survived this bracket?
                if (bracket.InitialCandidates > 0)
                    _metrics.HyperbandSurvivalRate.Record(
                        (double)survivors.Count / bracket.InitialCandidates,
                        new KeyValuePair<string, object?>("bracket", bracket.Index));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate shutdown
            }
            catch (Exception ex)
            {
                bracketsSkipped++;
                _metrics.HyperbandBracketsSkipped.Add(1);
                _logger.LogWarning(ex,
                    "HyperbandScheduler: bracket {Index} failed (non-fatal) — continuing with remaining brackets",
                    bracket.Index);
            }
        }

        return new HyperbandResult(allSurvivors, totalEvals, bracketsExecuted, bracketsSkipped);
    }

    /// <summary>
    /// Executes one successive halving bracket. Candidates are progressively screened
    /// through increasing fidelity levels, with the bottom (1 - 1/eta) fraction pruned
    /// at each rung.
    /// </summary>
    private async Task<(List<ScoredCandidateWithFidelity> Survivors, int Evaluations)> ExecuteSingleBracketAsync(
        Bracket bracket,
        Func<int, int, List<string>> candidateSource,
        List<Candle> trainCandles,
        Strategy strategy,
        BacktestOptions screeningOptions,
        OptimizationValidator validator,
        decimal baselineScore,
        int maxParallel,
        int screeningTimeoutSeconds,
        int circuitBreakerThreshold,
        int bracketBudgetRemaining,
        CancellationToken ct)
    {
        // Source candidates for this bracket
        var activeCandidates = candidateSource(bracket.InitialCandidates, bracket.Index);
        if (activeCandidates.Count == 0)
            return ([], 0);

        int totalEvals = 0;
        var lastRungScores = new List<ScoredCandidateWithFidelity>();

        for (int rung = 0; rung < bracket.FidelityRungs.Length; rung++)
        {
            ct.ThrowIfCancellationRequested();

            // Budget guard: if remaining budget can't cover this rung, stop early
            if (bracketBudgetRemaining <= 0)
            {
                _logger.LogDebug(
                    "HyperbandScheduler: bracket {Bracket} stopping at rung {Rung} — budget exhausted",
                    bracket.Index, rung);
                break;
            }

            // Cap active candidates to remaining budget
            if (activeCandidates.Count > bracketBudgetRemaining)
                activeCandidates = activeCandidates.Take(bracketBudgetRemaining).ToList();

            double fidelity = bracket.FidelityRungs[rung];
            int targetSurvivors = rung < bracket.FidelityRungs.Length - 1
                ? bracket.CandidatesPerRung[rung + 1]
                : activeCandidates.Count; // Last rung: keep all

            // Downsample training candles to this fidelity level
            var downsampledCandles = DownsampleCandles(trainCandles, fidelity);
            if (downsampledCandles.Count < 30)
            {
                _logger.LogDebug(
                    "HyperbandScheduler: bracket {Bracket} rung {Rung} skipped — " +
                    "only {Count} candles at fidelity {Fidelity:P0} (need ≥30)",
                    bracket.Index, rung, downsampledCandles.Count, fidelity);
                break;
            }

            // Scale timeout proportionally to fidelity
            int rungTimeout = Math.Max(5, (int)(screeningTimeoutSeconds * fidelity));

            // Evaluate all active candidates at this fidelity level.
            // Only successful evaluations are added to `scores`; failures increment the
            // circuit breaker counter but don't produce a scored entry. This keeps
            // totalEvals accurate for budget tracking (we only "spend" evals that ran).
            var scores = new ConcurrentBag<(int Index, decimal Score, BacktestResult Result)>();
            int consecutiveFailures = 0;
            int rungEvalsAttempted = 0;

            await Parallel.ForEachAsync(
                Enumerable.Range(0, activeCandidates.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallel,
                    CancellationToken = ct,
                },
                async (idx, pCt) =>
                {
                    if (Volatile.Read(ref consecutiveFailures) >= circuitBreakerThreshold)
                        return;

                    Interlocked.Increment(ref rungEvalsAttempted);
                    try
                    {
                        var result = await validator.RunWithTimeoutAsync(
                            strategy, activeCandidates[idx], downsampledCandles,
                            screeningOptions, rungTimeout, pCt);
                        var score = OptimizationHealthScorer.ComputeHealthScore(result);
                        scores.Add((idx, score, result));
                        Interlocked.Exchange(ref consecutiveFailures, 0);
                    }
                    catch (OperationCanceledException) when (pCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (!pCt.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref consecutiveFailures);
                    }
                    catch
                    {
                        Interlocked.Increment(ref consecutiveFailures);
                    }
                });

            int rungEvals = Volatile.Read(ref rungEvalsAttempted);
            totalEvals += rungEvals;
            bracketBudgetRemaining -= rungEvals;

            // Circuit breaker tripped — abort this bracket
            if (consecutiveFailures >= circuitBreakerThreshold)
            {
                _logger.LogWarning(
                    "HyperbandScheduler: circuit breaker tripped in bracket {Bracket} rung {Rung} " +
                    "after {Failures} consecutive failures — aborting bracket",
                    bracket.Index, rung, consecutiveFailures);
                _metrics.OptimizationCircuitBreakerTrips.Add(1);
                break;
            }

            // Noise-adaptive promotion: lower fidelity rungs use a looser threshold
            // because downsampled candle evaluations are noisier. At fidelity 0.11 (9x
            // downsample), tolerance is ~0.80; at fidelity 1.0, tolerance is ~0.95.
            double noiseTolerance = 0.75 + 0.20 * fidelity;

            // Promotion: keep candidates that either beat the baseline (with noise tolerance)
            // or are in the top targetSurvivors by score
            var validScores = scores
                .Where(s => s.Score >= 0m) // Exclude failed evaluations
                .OrderByDescending(s => s.Score)
                .ToList();

            if (validScores.Count == 0) break; // All candidates failed

            // Save the scores at this rung for the final result
            lastRungScores = validScores
                .Take(targetSurvivors)
                .Select(s => new ScoredCandidateWithFidelity(
                    activeCandidates[s.Index], s.Score, s.Result, fidelity, bracket.Index))
                .ToList();

            if (rung < bracket.FidelityRungs.Length - 1)
            {
                // Promote: keep candidates above baseline threshold OR top N
                var aboveBaseline = validScores
                    .Where(s => s.Score >= baselineScore * (decimal)noiseTolerance)
                    .ToList();

                var promoted = aboveBaseline.Count >= targetSurvivors
                    ? aboveBaseline.Take(targetSurvivors).ToList()
                    : validScores.Take(targetSurvivors).ToList();

                int pruned = activeCandidates.Count - promoted.Count;
                activeCandidates = promoted.Select(p => activeCandidates[p.Index]).ToList();

                if (activeCandidates.Count == 0)
                {
                    _logger.LogDebug(
                        "HyperbandScheduler: bracket {Bracket} rung {Rung} pruned all candidates — " +
                        "aborting bracket",
                        bracket.Index, rung);
                    break;
                }

                _logger.LogDebug(
                    "HyperbandScheduler: bracket {Bracket} rung {Rung}/{TotalRungs} " +
                    "(fidelity={Fidelity:P0}) — pruned {Pruned}/{Total}, {Remaining} survive",
                    bracket.Index, rung + 1, bracket.FidelityRungs.Length,
                    fidelity, pruned, pruned + promoted.Count, promoted.Count);
            }
        }

        return (lastRungScores, totalEvals);
    }

    // ── Survivor pooling ───────────────────────────────────────────────────

    /// <summary>
    /// Deduplicates and ranks survivors from all brackets. When the same candidate
    /// appears in multiple brackets (evaluated at different fidelities), the highest-
    /// fidelity evaluation is kept. Returns the top N candidates.
    /// </summary>
    internal static List<ScoredCandidateWithFidelity> PoolSurvivors(
        List<ScoredCandidateWithFidelity> allSurvivors, int maxCount)
    {
        if (allSurvivors.Count == 0) return [];

        // Deduplicate by ParamsJson, keeping the highest-fidelity evaluation.
        // When fidelity is equal (same candidate evaluated at same level in different
        // brackets), keep the higher score to avoid penalizing variance.
        var deduped = allSurvivors
            .GroupBy(s => CanonicalParameterJson.Normalize(s.ParamsJson))
            .Select(g => g.OrderByDescending(s => s.EvaluatedAtFidelity)
                          .ThenByDescending(s => s.HealthScore)
                          .First())
            .OrderByDescending(s => s.HealthScore)
            .Take(maxCount)
            .ToList();

        return deduped;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Downsamples candles to the given fidelity level by selecting every Nth bar.
    /// Preserves temporal ordering. At fidelity 1.0, returns the original list.
    /// At fidelity 0.25, returns every 4th candle.
    /// </summary>
    internal static List<Candle> DownsampleCandles(List<Candle> candles, double fidelity)
    {
        if (fidelity >= 1.0) return candles;

        int step = Math.Max(1, (int)Math.Round(1.0 / fidelity));
        return candles.Where((_, i) => i % step == 0).ToList();
    }

    /// <summary>
    /// Computes the minimum useful fidelity given the total candle count.
    /// Ensures at least <paramref name="minUsableCandles"/> candles at the lowest rung.
    /// </summary>
    internal static double ComputeMinFidelity(int totalCandles, int minUsableCandles = 30)
    {
        if (totalCandles <= minUsableCandles) return 1.0;
        return Math.Max(0.01, (double)minUsableCandles / totalCandles);
    }

    /// <summary>
    /// Estimates total evaluations across all brackets for budget planning.
    /// Used by the dry-run simulator and config validation.
    /// </summary>
    internal static int EstimateTotalEvaluations(List<Bracket> brackets)
    {
        int total = 0;
        foreach (var bracket in brackets)
        {
            for (int rung = 0; rung < bracket.CandidatesPerRung.Length; rung++)
                total += bracket.CandidatesPerRung[rung];
        }
        return total;
    }
}
