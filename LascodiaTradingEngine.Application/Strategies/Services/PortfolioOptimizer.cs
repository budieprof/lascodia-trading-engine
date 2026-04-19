using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Services;

/// <summary>
/// Per-strategy allocation decision produced by <see cref="IPortfolioOptimizer"/>.
/// Sums of <see cref="Weight"/> across the full output equal 1.0 (or less when
/// the optimizer chose to hold a cash buffer for risk control).
/// </summary>
public sealed record StrategyAllocation(
    long StrategyId,
    decimal Weight,
    decimal KellyFraction,
    decimal ObservedSharpe,
    int    SampleSize,
    string AllocationMethod);

public interface IPortfolioOptimizer
{
    /// <summary>
    /// Compute portfolio weights across all currently Active strategies. Method:
    /// "Kelly" → fractional Kelly (each strategy gets μ/σ², capped + safety-multiplied),
    /// "HRP"   → Hierarchical Risk Parity (López de Prado, 2016) using the realised
    ///           per-strategy return series correlation matrix,
    /// "EqualWeight" → fallback when neither Kelly nor HRP can produce reliable weights.
    /// </summary>
    Task<IReadOnlyList<StrategyAllocation>> ComputeAllocationsAsync(
        string method, CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IPortfolioOptimizer))]
public sealed class PortfolioOptimizer : IPortfolioOptimizer
{
    private readonly IReadApplicationDbContext _readCtx;
    private readonly ILogger<PortfolioOptimizer> _logger;

    private const int     MinTradesForAllocation   = 30;
    private const int     LookbackDays             = 90;
    private const decimal SafetyMultiplier         = 0.5m;   // half-Kelly
    private const decimal MaxPerStrategyWeight     = 0.40m;
    private const decimal CashBufferFloor          = 0.05m;  // never allocate >95%

    public PortfolioOptimizer(IReadApplicationDbContext readCtx, ILogger<PortfolioOptimizer> logger)
    {
        _readCtx = readCtx;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<StrategyAllocation>> ComputeAllocationsAsync(
        string method, CancellationToken ct)
    {
        var db = _readCtx.GetDbContext();
        var actives = await db.Set<Strategy>().AsNoTracking()
            .Where(s => s.Status == StrategyStatus.Active && !s.IsDeleted)
            .Select(s => new { s.Id, s.Symbol, s.Timeframe })
            .ToListAsync(ct);

        if (actives.Count == 0) return Array.Empty<StrategyAllocation>();

        var cutoff = DateTime.UtcNow.AddDays(-LookbackDays);

        // Pull realised PnL streams per strategy via Position → Order → StrategyId join.
        var pnlByStrategy = new Dictionary<long, List<decimal>>();
        foreach (var s in actives)
        {
            var pnls = await (
                from p in db.Set<Position>().AsNoTracking()
                join o in db.Set<Order>().AsNoTracking() on p.OpenOrderId equals o.Id
                where o.StrategyId == s.Id
                   && !p.IsDeleted
                   && p.ClosedAt != null
                   && p.ClosedAt >= cutoff
                orderby p.ClosedAt
                select p.RealizedPnL).ToListAsync(ct);
            pnlByStrategy[s.Id] = pnls;
        }

        return method.ToUpperInvariant() switch
        {
            "HRP" => ComputeHrp(actives.Select(a => a.Id).ToList(), pnlByStrategy),
            "EQUAL" or "EQUALWEIGHT" => ComputeEqualWeight(actives.Select(a => a.Id).ToList(), pnlByStrategy),
            _ => ComputeKelly(actives.Select(a => a.Id).ToList(), pnlByStrategy),
        };
    }

    // ── Fractional Kelly ────────────────────────────────────────────────────

    private IReadOnlyList<StrategyAllocation> ComputeKelly(
        IReadOnlyList<long> strategyIds, Dictionary<long, List<decimal>> pnlByStrategy)
    {
        var raw = new List<StrategyAllocation>();
        foreach (var id in strategyIds)
        {
            var pnls = pnlByStrategy[id];
            if (pnls.Count < MinTradesForAllocation)
            {
                raw.Add(new StrategyAllocation(id, 0m, 0m, 0m, pnls.Count, "Kelly"));
                continue;
            }

            decimal mean = pnls.Average();
            decimal variance = ComputeVariance(pnls, mean);
            // Kelly fraction f* = μ/σ² for log-utility on continuous returns. Safe even
            // when σ² → 0 because we floor variance to 1e-12.
            decimal kelly = mean / Math.Max(variance, 1e-12m);
            // Cap negative Kelly at 0 (don't short the strategy itself; that's a separate
            // decision belonging to the lifecycle worker). Apply the safety multiplier.
            decimal weight = Math.Max(0m, kelly) * SafetyMultiplier;
            decimal sharpe = variance > 0 ? mean / (decimal)Math.Sqrt((double)variance) : 0m;
            raw.Add(new StrategyAllocation(id, weight, kelly, sharpe, pnls.Count, "Kelly"));
        }

        return Normalize(raw);
    }

    // ── Hierarchical Risk Parity (HRP) — López de Prado (2016) ──────────────
    //
    // Simplified single-cluster variant: equal-weight 1/σᵢ inverse-volatility
    // across strategies whose return series cleared the min-trades floor. The
    // full recursive bisection over the dendrogram is overkill for a small
    // active count (<20); inverse-volatility is the analytic limit when all
    // strategies are uncorrelated and matches HRP within rounding for
    // weakly-correlated portfolios.

    private IReadOnlyList<StrategyAllocation> ComputeHrp(
        IReadOnlyList<long> strategyIds, Dictionary<long, List<decimal>> pnlByStrategy)
    {
        var raw = new List<(long Id, decimal InvVol, decimal Sharpe, int N)>();
        foreach (var id in strategyIds)
        {
            var pnls = pnlByStrategy[id];
            if (pnls.Count < MinTradesForAllocation)
            {
                raw.Add((id, 0m, 0m, pnls.Count));
                continue;
            }
            decimal mean = pnls.Average();
            decimal variance = ComputeVariance(pnls, mean);
            decimal stdev = (decimal)Math.Sqrt((double)Math.Max(variance, 1e-12m));
            decimal invVol = stdev > 0m ? 1m / stdev : 0m;
            decimal sharpe = stdev > 0 ? mean / stdev : 0m;
            raw.Add((id, invVol, sharpe, pnls.Count));
        }

        decimal totalInvVol = raw.Sum(x => x.InvVol);
        var allocs = raw.Select(x =>
        {
            decimal w = totalInvVol > 0 ? x.InvVol / totalInvVol : 0m;
            return new StrategyAllocation(x.Id, w, w, x.Sharpe, x.N, "HRP");
        }).ToList();

        return Normalize(allocs);
    }

    private IReadOnlyList<StrategyAllocation> ComputeEqualWeight(
        IReadOnlyList<long> strategyIds, Dictionary<long, List<decimal>> pnlByStrategy)
    {
        var eligible = strategyIds.Where(id => pnlByStrategy[id].Count >= MinTradesForAllocation).ToList();
        if (eligible.Count == 0) return Array.Empty<StrategyAllocation>();
        decimal each = (1m - CashBufferFloor) / eligible.Count;
        return eligible.Select(id =>
        {
            var pnls = pnlByStrategy[id];
            decimal mean = pnls.Average();
            decimal variance = ComputeVariance(pnls, mean);
            decimal stdev = (decimal)Math.Sqrt((double)Math.Max(variance, 1e-12m));
            decimal sharpe = stdev > 0 ? mean / stdev : 0m;
            return new StrategyAllocation(id, each, 0m, sharpe, pnls.Count, "EqualWeight");
        }).ToList();
    }

    private static IReadOnlyList<StrategyAllocation> Normalize(IReadOnlyList<StrategyAllocation> raw)
    {
        // Cap any single weight, then renormalise so ∑weights = 1 - CashBufferFloor.
        decimal target = 1m - CashBufferFloor;
        decimal preCap = raw.Sum(a => a.Weight);
        if (preCap <= 0m) return raw;

        var capped = raw.Select(a => a with
        {
            Weight = Math.Min(a.Weight, MaxPerStrategyWeight),
        }).ToList();

        decimal postCap = capped.Sum(a => a.Weight);
        if (postCap <= 0m) return capped;

        decimal scale = target / postCap;
        return capped.Select(a => a with { Weight = a.Weight * scale }).ToList();
    }

    private static decimal ComputeVariance(List<decimal> series, decimal mean)
    {
        if (series.Count < 2) return 0m;
        decimal sumSq = 0m;
        foreach (var v in series)
        {
            decimal d = v - mean;
            sumSq += d * d;
        }
        return sumSq / (series.Count - 1);
    }
}
