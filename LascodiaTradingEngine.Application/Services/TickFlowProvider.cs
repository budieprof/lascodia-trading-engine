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

        // Tick-volume imbalance: signed count of mid-price moves up vs down, weighted
        // by tick volume. Closest proxy to aggressor-side we have without real DOM.
        long upVol = 0L, downVol = 0L;
        for (int i = ticks.Count - 1; i > 0; i--)
        {
            int dir = Math.Sign(ticks[i - 1].Mid - ticks[i].Mid);
            long vol = Math.Max(1L, ticks[i - 1].TickVolume);
            if (dir > 0) upVol += vol;
            else if (dir < 0) downVol += vol;
        }
        long totalVol = upVol + downVol;
        decimal tickVolumeImbalance = totalVol > 0
            ? Math.Clamp((decimal)(upVol - downVol) / totalVol, -1m, 1m)
            : 0m;

        var snapshot = new TickFlowSnapshot(
            tickDelta, currentSpread, spreadMean, spreadStdDev,
            SpreadPercentileRank: spreadPercentile,
            SpreadRelVolatility:  spreadRelVol,
            TickVolumeImbalance:  tickVolumeImbalance);

        _cache.Set(cacheKey, snapshot, CacheTtl);

        return snapshot;
    }
}
