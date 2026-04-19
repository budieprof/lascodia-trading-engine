using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Default implementation of <see cref="ITickFlowProvider"/> that queries persisted
/// <see cref="TickRecord"/> rows for tick-level microstructure metrics.
/// Results are cached for 1 minute per symbol to avoid repeated DB hits.
/// </summary>
[RegisterService(ServiceLifetime.Scoped, typeof(ITickFlowProvider))]
public sealed class TickFlowProvider : ITickFlowProvider
{
    // Widened from 30 → 128 so the ECDF percentile rank (V4 feature) has enough mass
    // to be statistically meaningful. 128 ticks ≈ 10-30 seconds of live flow at typical
    // FX tick rates — short enough to stay reactive, long enough for stable percentiles.
    private const int TickWindow = 128;
    private const int MinTicksRequired = 5;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private readonly IReadApplicationDbContext _readContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TickFlowProvider> _logger;

    public TickFlowProvider(
        IReadApplicationDbContext readContext,
        IMemoryCache cache,
        ILogger<TickFlowProvider> logger)
    {
        _readContext = readContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<TickFlowSnapshot?> GetSnapshotAsync(string symbol, DateTime asOf, CancellationToken ct)
    {
        string cacheKey = $"TickFlow:{symbol}";

        if (_cache.TryGetValue(cacheKey, out TickFlowSnapshot? cached))
            return cached;

        var ticks = await _readContext.GetDbContext()
            .Set<TickRecord>()
            .Where(t => t.Symbol == symbol && !t.IsDeleted && t.TickTimestamp <= asOf)
            .OrderByDescending(t => t.TickTimestamp)
            .Take(TickWindow)
            .ToListAsync(ct);

        if (ticks.Count < MinTicksRequired)
        {
            _logger.LogDebug("Insufficient ticks for {Symbol}: {Count} < {Min}", symbol, ticks.Count, MinTicksRequired);
            return null;
        }

        // Compute tick delta: iterate oldest → newest (reverse the descending list)
        int directionSum = 0;
        for (int i = ticks.Count - 1; i > 0; i--)
        {
            // ticks[i] is older, ticks[i-1] is newer (descending order)
            directionSum += Math.Sign(ticks[i - 1].Mid - ticks[i].Mid);
        }
        int pairCount = ticks.Count - 1;
        decimal tickDelta = pairCount > 0
            ? Math.Clamp((decimal)directionSum / pairCount, -1m, 1m)
            : 0m;

        // Current spread: latest tick (index 0 in descending list)
        decimal currentSpread = ticks[0].Ask - ticks[0].Bid;

        // Spread mean and standard deviation across all ticks
        decimal sumSpread = 0m;
        for (int i = 0; i < ticks.Count; i++)
            sumSpread += ticks[i].Ask - ticks[i].Bid;
        decimal spreadMean = sumSpread / ticks.Count;

        decimal sumSqDiff = 0m;
        for (int i = 0; i < ticks.Count; i++)
        {
            decimal diff = (ticks[i].Ask - ticks[i].Bid) - spreadMean;
            sumSqDiff += diff * diff;
        }
        decimal spreadStdDev = (decimal)Math.Sqrt((double)(sumSqDiff / ticks.Count));

        // V4 extensions: spread rel-volatility, percentile rank, tick-volume imbalance.
        decimal spreadRelVol = spreadMean > 0m
            ? Math.Clamp(spreadStdDev / spreadMean, 0m, 3m)
            : 0m;

        // ECDF percentile rank: fraction of ticks in the window whose spread is
        // strictly less than the current spread. 0 = current is the tightest seen,
        // 1 = current is the widest seen. Gives the model a cheap regime indicator
        // without burdening it with the raw spread magnitude.
        int strictlyLess = 0;
        for (int i = 0; i < ticks.Count; i++)
        {
            if ((ticks[i].Ask - ticks[i].Bid) < currentSpread) strictlyLess++;
        }
        decimal spreadPercentile = ticks.Count > 0
            ? Math.Clamp((decimal)strictlyLess / ticks.Count, 0m, 1m)
            : 0m;

        // ── Lee-Ready order-flow imbalance (V5 quality upgrade) ──────────
        // Classify each tick as buyer- or seller-initiated using the Lee-Ready
        // (1991) algorithm: a trade above the prior quote midpoint is buyer-
        // initiated, below is seller-initiated, equal-to ("tick test") inherits
        // the previous classification. Bid/Ask isn't a real "trade", so we use
        // the mid as the trade price proxy; this is the canonical retail-data
        // approximation. Output replaces the simpler "sign(Δmid)" version.
        long buyVol = 0L, sellVol = 0L;
        int  prevClass = 0; // -1 sell, +1 buy, 0 unknown
        for (int i = ticks.Count - 2; i >= 0; i--) // newest=0 → oldest; iterate older→newer
        {
            decimal priorMid = (ticks[i + 1].Ask + ticks[i + 1].Bid) / 2m;
            decimal nowMid   = (ticks[i].Ask + ticks[i].Bid) / 2m;
            int sign = nowMid > priorMid ? 1 : nowMid < priorMid ? -1 : prevClass;
            if (sign == 0) continue;
            long vol = Math.Max(1L, ticks[i].TickVolume);
            if (sign > 0) buyVol  += vol;
            else          sellVol += vol;
            prevClass = sign;
        }
        long totalVol = buyVol + sellVol;
        decimal tickVolumeImbalance = totalVol > 0
            ? Math.Clamp((decimal)(buyVol - sellVol) / totalVol, -1m, 1m)
            : 0m;

        // ── V5 microstructure features ──────────────────────────────────
        // Build the tick-return series (oldest → newest) once for Roll, Amihud,
        // VarianceRatio, and EffectiveSpread.
        var midReturns = new List<decimal>(Math.Max(0, ticks.Count - 1));
        var absReturnsPerVol = new List<decimal>(Math.Max(0, ticks.Count - 1));
        decimal effectiveSpreadSum = 0m;
        int effectiveSpreadCount = 0;
        for (int i = ticks.Count - 2; i >= 0; i--)
        {
            decimal priorMid = (ticks[i + 1].Ask + ticks[i + 1].Bid) / 2m;
            decimal nowMid   = (ticks[i].Ask + ticks[i].Bid) / 2m;
            if (priorMid == 0m) continue;
            decimal ret = (nowMid - priorMid) / priorMid;
            midReturns.Add(ret);
            long vol = Math.Max(1L, ticks[i].TickVolume);
            absReturnsPerVol.Add(Math.Abs(ret) / vol);

            // Effective spread: use the trade-side estimate |trade − mid| × 2 / mid.
            // For mid-based ticks the trade price is approximated as the prior-side quote
            // (Bid for sell-initiated, Ask for buy-initiated trades).
            decimal tradePrice = nowMid > priorMid ? ticks[i].Ask : ticks[i].Bid;
            decimal effSpread = priorMid > 0m
                ? 2m * Math.Abs(tradePrice - priorMid) / priorMid
                : 0m;
            effectiveSpreadSum += effSpread;
            effectiveSpreadCount++;
        }
        decimal effectiveSpread = effectiveSpreadCount > 0
            ? Math.Clamp(effectiveSpreadSum / effectiveSpreadCount, 0m, 0.01m) // cap at 1% as sanity floor
            : 0m;
        decimal amihudIlliquidity = absReturnsPerVol.Count > 0
            ? Math.Clamp(absReturnsPerVol.Average(), 0m, 1m)
            : 0m;

        // Roll's spread estimator: 2×√(−Cov(Δp_t, Δp_{t−1})). Bid-ask bounce makes
        // adjacent returns negatively correlated; the magnitude of that covariance
        // recovers the implicit spread. When the autocovariance is positive (trending
        // returns), the estimator is undefined and we return 0 — explicit signal that
        // the symbol is not currently in bid-ask-bounce regime.
        decimal rollSpreadEstimate = 0m;
        if (midReturns.Count >= 3)
        {
            decimal mean = midReturns.Average();
            decimal cov = 0m;
            for (int i = 1; i < midReturns.Count; i++)
                cov += (midReturns[i] - mean) * (midReturns[i - 1] - mean);
            cov /= (midReturns.Count - 1);
            if (cov < 0m) rollSpreadEstimate = 2m * (decimal)Math.Sqrt((double)(-cov));
            rollSpreadEstimate = Math.Clamp(rollSpreadEstimate, 0m, 0.01m);
        }

        // Variance ratio at k=2: Var(2-period returns) / (2 × Var(1-period returns)).
        // Equals 1.0 under random walk; >1 indicates positive autocorr (momentum),
        // <1 indicates negative autocorr (mean reversion). Captures whether tick-level
        // returns have exploitable serial structure.
        decimal varianceRatio = 1m;
        if (midReturns.Count >= 4)
        {
            var twoPeriod = new List<decimal>(midReturns.Count / 2);
            for (int i = 0; i + 1 < midReturns.Count; i += 2)
                twoPeriod.Add(midReturns[i] + midReturns[i + 1]);
            decimal var1 = SampleVariance(midReturns);
            decimal var2 = SampleVariance(twoPeriod);
            if (var1 > 0m)
                varianceRatio = Math.Clamp(var2 / (2m * var1), 0m, 5m);
        }

        var snapshot = new TickFlowSnapshot(
            tickDelta, currentSpread, spreadMean, spreadStdDev,
            SpreadPercentileRank: spreadPercentile,
            SpreadRelVolatility:  spreadRelVol,
            TickVolumeImbalance:  tickVolumeImbalance,
            EffectiveSpread:      effectiveSpread,
            AmihudIlliquidity:    amihudIlliquidity,
            RollSpreadEstimate:   rollSpreadEstimate,
            VarianceRatio:        varianceRatio);

        _cache.Set(cacheKey, snapshot, CacheTtl);

        return snapshot;
    }

    private static decimal SampleVariance(IReadOnlyList<decimal> values)
    {
        if (values.Count < 2) return 0m;
        decimal mean = values.Average();
        decimal sumSq = 0m;
        foreach (var v in values)
        {
            decimal d = v - mean;
            sumSq += d * d;
        }
        return sumSq / (values.Count - 1);
    }
}
