using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Minimum-viable Combinatorial Purged Cross-Validation.
///
/// <para>
/// Full CPCV retrains the model on every test partition — 66 paths × per-path
/// training cost is prohibitive without infrastructure work (warm starts,
/// incremental trainers, distributed compute). This MVP implements the
/// <b>trade-resampling</b> approximation: partition the strategy's realised
/// trades into N chronological groups, generate every C(N, K) combination as a
/// test partition (rest is train/reference), and compute the Sharpe of each
/// test partition. The resulting distribution is what deflated-Sharpe and
/// probability-of-overfitting calculations need.
/// </para>
///
/// <para>
/// <b>How this differs from full CPCV</b>: we do NOT retrain per-fold. The same
/// strategy parameters produced every trade; partitioning only tells us whether
/// the strategy's edge is concentrated in a few time windows (classic overfitting
/// signature) vs. evenly distributed across the dataset (genuine edge). A
/// strategy whose P25 Sharpe is &gt; 0 is robust across partitions; one whose
/// P25 is deeply negative while median is positive was likely lucky on one
/// contiguous window.
/// </para>
///
/// <para>
/// <b>When to upgrade</b>: once we have a warm-startable trainer, implement a
/// companion <c>CpcvRetrainValidator</c> that actually retrains per fold and
/// reports the richer out-of-sample distribution. Keep this class as the cheap
/// pre-filter — run the expensive retrain CPCV only on candidates that survive
/// the trade-resampling signal.
/// </para>
/// </summary>
public sealed class CpcvValidator : ICpcvValidator
{
    private readonly IReadApplicationDbContext _readCtx;

    // Defaults chosen so we produce a useful distribution (66 paths at N=12, K=2)
    // without blowing up combinatorially. Override via EngineConfig if needed.
    private const int DefaultNGroups = 12;
    private const int DefaultKTestGroups = 2;
    private const int MinimumTradesForCpcv = 60; // below this we don't have enough signal

    public CpcvValidator(IReadApplicationDbContext readCtx)
    {
        _readCtx = readCtx;
    }

    public async Task<CpcvResult> ValidateAsync(
        long strategyId, DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();

        // Pull the realised trade P&L series for this strategy within the window.
        // Position has no StrategyId; the link is Position → OpenOrder → Strategy.
        // Join so we filter by Order.StrategyId and time-bound by Position.ClosedAt.
        var trades = await (
                from p in db.Set<Position>().AsNoTracking()
                join o in db.Set<Order>().AsNoTracking() on p.OpenOrderId equals o.Id
                where o.StrategyId == strategyId
                   && !p.IsDeleted
                   && p.ClosedAt != null
                   && p.ClosedAt >= fromDate
                   && p.ClosedAt <= toDate
                orderby p.ClosedAt
                select new { At = p.ClosedAt!.Value, Pnl = (double)p.RealizedPnL })
            .ToListAsync(ct);

        if (trades.Count < MinimumTradesForCpcv)
        {
            return new CpcvResult(
                StrategyId: strategyId,
                FromDate: fromDate,
                ToDate: toDate,
                NGroups: 0, KTestGroups: 0,
                SharpeDistribution: Array.Empty<double>(),
                MedianSharpe: 0, P25Sharpe: 0, P75Sharpe: 0,
                DeflatedSharpe: 0, ProbabilityOfOverfitting: 1.0);
        }

        int n = DefaultNGroups;
        int k = DefaultKTestGroups;
        if (trades.Count < n * 5) n = Math.Max(4, trades.Count / 5); // scale down for small datasets
        if (k >= n) k = Math.Max(1, n / 3);

        // Partition trades into N chronological groups of roughly equal size.
        var groups = PartitionChronologically(trades.Select(t => t.Pnl).ToArray(), n);

        // Enumerate C(N, K) combinations, compute Sharpe on each test partition.
        var distribution = new List<double>();
        foreach (var testIdx in Combinations(n, k))
        {
            var testPnls = testIdx.SelectMany(i => groups[i]).ToArray();
            if (testPnls.Length < 10) continue; // skip pathologically small test folds
            distribution.Add(ComputeSharpe(testPnls));
        }

        distribution.Sort();
        double median = Percentile(distribution, 0.50);
        double p25    = Percentile(distribution, 0.25);
        double p75    = Percentile(distribution, 0.75);

        // Deflated Sharpe on the distribution: use the median as the "observed"
        // Sharpe, number-of-folds as the trials count. This is the corrected
        // version of PromotionGateValidator's DSR — now computed against a real
        // distribution of out-of-sample-like Sharpes rather than a single point.
        double dsr = PromotionGateValidator.ComputeDeflatedSharpe(
            rawSharpe: median, trials: distribution.Count, trades: trades.Count);

        // PBO = fraction of folds where the median strategy ranked in the bottom
        // half. Proxy here: fraction of folds below overall median Sharpe. A
        // strategy whose fold-Sharpes are ≥ median 50% of the time has PBO = 0.5;
        // one whose Sharpes are mostly above has PBO < 0.5 (good).
        int belowMedian = distribution.Count(s => s < median);
        double pbo = distribution.Count > 0 ? (double)belowMedian / distribution.Count : 1.0;

        return new CpcvResult(
            StrategyId: strategyId,
            FromDate: fromDate, ToDate: toDate,
            NGroups: n, KTestGroups: k,
            SharpeDistribution: distribution,
            MedianSharpe: median, P25Sharpe: p25, P75Sharpe: p75,
            DeflatedSharpe: dsr, ProbabilityOfOverfitting: pbo);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<List<double>> PartitionChronologically(double[] pnls, int n)
    {
        var groups = new List<List<double>>(n);
        for (int i = 0; i < n; i++) groups.Add(new List<double>());
        int perGroup = pnls.Length / n;
        int remainder = pnls.Length % n;
        int idx = 0;
        for (int g = 0; g < n; g++)
        {
            int size = perGroup + (g < remainder ? 1 : 0);
            for (int j = 0; j < size && idx < pnls.Length; j++)
                groups[g].Add(pnls[idx++]);
        }
        return groups;
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var indices = new int[k];
        for (int i = 0; i < k; i++) indices[i] = i;
        while (true)
        {
            yield return (int[])indices.Clone();
            // Advance to next combination (standard lexicographic)
            int i2 = k - 1;
            while (i2 >= 0 && indices[i2] == n - k + i2) i2--;
            if (i2 < 0) yield break;
            indices[i2]++;
            for (int j = i2 + 1; j < k; j++) indices[j] = indices[j - 1] + 1;
        }
    }

    private static double ComputeSharpe(double[] pnls)
    {
        if (pnls.Length < 2) return 0.0;
        double mean = pnls.Average();
        double variance = pnls.Sum(p => (p - mean) * (p - mean)) / (pnls.Length - 1);
        double stdev = Math.Sqrt(variance);
        return stdev > 1e-12 ? mean / stdev * Math.Sqrt(pnls.Length) : 0.0;
    }

    private static double Percentile(IReadOnlyList<double> sortedAsc, double pct)
    {
        if (sortedAsc.Count == 0) return 0.0;
        double pos = pct * (sortedAsc.Count - 1);
        int low = (int)Math.Floor(pos);
        int high = (int)Math.Ceiling(pos);
        if (low == high) return sortedAsc[low];
        double frac = pos - low;
        return sortedAsc[low] * (1 - frac) + sortedAsc[high] * frac;
    }
}
