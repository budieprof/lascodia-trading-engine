namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Multi-objective selection via non-dominated sorting and crowding distance.
/// Used to select candidates from the Pareto front when optimising across competing
/// objectives (e.g. Sharpe vs max drawdown vs win rate).
/// </summary>
internal static class ParetoFrontSelector
{
    /// <summary>
    /// Extracts the Pareto-optimal (non-dominated) subset from a list of candidates.
    /// All objectives are assumed to be maximised — negate for minimisation objectives.
    /// </summary>
    public static List<T> ExtractParetoFront<T>(
        IReadOnlyList<T> candidates,
        params Func<T, double>[] objectives)
    {
        if (candidates.Count == 0 || objectives.Length == 0) return [];

        var front = new List<T>();

        for (int i = 0; i < candidates.Count; i++)
        {
            bool dominated = false;
            for (int j = 0; j < candidates.Count; j++)
            {
                if (i == j) continue;
                if (Dominates(candidates[j], candidates[i], objectives))
                {
                    dominated = true;
                    break;
                }
            }
            if (!dominated) front.Add(candidates[i]);
        }

        return front;
    }

    /// <summary>
    /// Selects <paramref name="count"/> candidates from the Pareto front using crowding
    /// distance to preserve diversity along the front. Candidates at the extremes of
    /// each objective always survive (infinite crowding distance).
    /// </summary>
    public static List<T> SelectByCrowdingDistance<T>(
        IReadOnlyList<T> paretoFront,
        int count,
        params Func<T, double>[] objectives)
    {
        if (paretoFront.Count <= count) return [.. paretoFront];

        var distances = ComputeCrowdingDistances(paretoFront, objectives);

        return paretoFront
            .Zip(distances, (candidate, dist) => (candidate, dist))
            .OrderByDescending(x => x.dist)
            .Take(count)
            .Select(x => x.candidate)
            .ToList();
    }

    /// <summary>
    /// Ranks all candidates by non-dominated sorting (NSGA-II style).
    /// Returns fronts in order: front 0 (Pareto-optimal), front 1 (dominated by front 0 only), etc.
    /// Within each front, candidates are ordered by crowding distance (descending).
    /// </summary>
    public static List<T> RankByNonDominatedSorting<T>(
        IReadOnlyList<T> candidates,
        int maxCount,
        params Func<T, double>[] objectives)
    {
        if (candidates.Count == 0) return [];

        var remaining = Enumerable.Range(0, candidates.Count).ToHashSet();
        var result    = new List<T>();

        while (remaining.Count > 0 && result.Count < maxCount)
        {
            // Extract current Pareto front from remaining candidates
            var currentIndices = remaining.ToList();
            var currentFront   = new List<int>();

            foreach (int i in currentIndices)
            {
                bool dominated = false;
                foreach (int j in currentIndices)
                {
                    if (i == j) continue;
                    if (Dominates(candidates[j], candidates[i], objectives))
                    {
                        dominated = true;
                        break;
                    }
                }
                if (!dominated) currentFront.Add(i);
            }

            // Sort this front by crowding distance
            var frontCandidates = currentFront.Select(i => candidates[i]).ToList();
            var distances       = ComputeCrowdingDistances(frontCandidates, objectives);

            var sorted = frontCandidates
                .Zip(distances, (c, d) => (Candidate: c, Distance: d))
                .OrderByDescending(x => x.Distance)
                .Select(x => x.Candidate);

            foreach (var candidate in sorted)
            {
                if (result.Count >= maxCount) break;
                result.Add(candidate);
            }

            foreach (int idx in currentFront)
                remaining.Remove(idx);
        }

        return result;
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="a"/> dominates <paramref name="b"/>.
    /// NaN/Infinity objectives are sanitized to 0 to prevent unpredictable dominance results
    /// from degenerate backtest metrics.
    /// </summary>
    /// <remarks>
    /// Dominance checking is O(n²) per front extraction. This is acceptable for the typical
    /// candidate pool sizes in optimization (50-200). For pools exceeding 1000 candidates,
    /// consider Kung's O(n log n) algorithm for 2-3 objectives.
    /// </remarks>
    private static bool Dominates<T>(T a, T b, Func<T, double>[] objectives)
    {
        bool atLeastOneBetter = false;
        foreach (var obj in objectives)
        {
            double va = Sanitize(obj(a)), vb = Sanitize(obj(b));
            if (va < vb) return false;      // a is worse in at least one objective
            if (va > vb) atLeastOneBetter = true;
        }
        return atLeastOneBetter;
    }

    /// <summary>Clamps NaN/Infinity to 0 to prevent nonsensical dominance comparisons.</summary>
    private static double Sanitize(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;

    /// <summary>
    /// Crowding distance per NSGA-II: sum of normalised neighbour gaps per objective.
    /// Note: when objectives are highly correlated (e.g. Sharpe and win rate), crowding
    /// distance may double-count the same diversity axis. This is a known NSGA-II limitation;
    /// PCA-based crowding would address it but adds complexity disproportionate to the
    /// typical 3-objective case used here.
    /// </summary>
    private static double[] ComputeCrowdingDistances<T>(IReadOnlyList<T> front, Func<T, double>[] objectives)
    {
        int n = front.Count;
        var distances = new double[n];

        foreach (var obj in objectives)
        {
            // Sort indices by this objective (sanitize to handle NaN/Infinity)
            var sorted = Enumerable.Range(0, n).OrderBy(i => Sanitize(obj(front[i]))).ToArray();

            double objMin = Sanitize(obj(front[sorted[0]]));
            double objMax = Sanitize(obj(front[sorted[n - 1]]));
            double range  = objMax - objMin;

            // Boundary candidates get infinite distance (always preserved)
            distances[sorted[0]]     = double.PositiveInfinity;
            distances[sorted[n - 1]] = double.PositiveInfinity;

            if (range < 1e-12) continue; // All same value for this objective

            for (int i = 1; i < n - 1; i++)
            {
                double gap = obj(front[sorted[i + 1]]) - obj(front[sorted[i - 1]]);
                distances[sorted[i]] += gap / range;
            }
        }

        return distances;
    }
}
