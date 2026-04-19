using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Snapshot of multi-level order-book features computed from the latest
/// <see cref="OrderBookSnapshot"/> for a symbol. All values are normalised /
/// clamped so they consume a single feature slot each in the V6 vector.
/// </summary>
public sealed record OrderBookFeatureSnapshot(
    /// <summary>Bid/(Bid+Ask) volume ratio at top of book; 0.5 = balanced, [0,1].</summary>
    decimal BookImbalanceTop1,
    /// <summary>Same ratio aggregated across the top 5 levels per side. Null-filled levels
    /// fall back to BookImbalanceTop1 so a 1-level broker degrades gracefully.</summary>
    decimal BookImbalanceTop5,
    /// <summary>Total liquidity across top N levels, normalised to [0,1] via tanh on
    /// log-volume (volumes have huge cross-symbol variance; tanh+log keeps the model
    /// responsive without exploding on outlier prints).</summary>
    decimal TotalLiquidityNorm,
    /// <summary>Slope of the bid-side depth curve (volume vs distance from top).
    /// Higher = thicker book deeper down (resilient); lower = thin book.</summary>
    decimal BookSlopeBid,
    /// <summary>Slope of the ask-side depth curve, same interpretation.</summary>
    decimal BookSlopeAsk);

public interface IOrderBookFeatureProvider
{
    /// <summary>
    /// Returns the latest order-book features for a symbol at <paramref name="asOf"/>
    /// (uses the most recent <see cref="OrderBookSnapshot"/> at or before that time).
    /// Returns null when no snapshot exists or all snapshots are stale (&gt; 60 s old) —
    /// V6 builder zero-fills in that case.
    /// </summary>
    Task<OrderBookFeatureSnapshot?> GetSnapshotAsync(string symbol, DateTime asOf, CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IOrderBookFeatureProvider))]
public sealed class OrderBookFeatureProvider : IOrderBookFeatureProvider
{
    private readonly IReadApplicationDbContext _readContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OrderBookFeatureProvider> _logger;

    private const int     MaxStalenessSeconds = 60;
    private const int     TopNLevels          = 5;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    public OrderBookFeatureProvider(
        IReadApplicationDbContext readContext, IMemoryCache cache, ILogger<OrderBookFeatureProvider> logger)
    {
        _readContext = readContext;
        _cache       = cache;
        _logger      = logger;
    }

    public async Task<OrderBookFeatureSnapshot?> GetSnapshotAsync(
        string symbol, DateTime asOf, CancellationToken ct)
    {
        string cacheKey = $"OrderBookFeat:{symbol}";
        if (_cache.TryGetValue(cacheKey, out OrderBookFeatureSnapshot? cached))
            return cached;

        var ob = await _readContext.GetDbContext()
            .Set<OrderBookSnapshot>()
            .AsNoTracking()
            .Where(o => o.Symbol == symbol && !o.IsDeleted && o.CapturedAt <= asOf)
            .OrderByDescending(o => o.CapturedAt)
            .FirstOrDefaultAsync(ct);

        if (ob is null) return null;
        if ((asOf - ob.CapturedAt).TotalSeconds > MaxStalenessSeconds) return null;

        // Top-of-book imbalance is always available even when the broker doesn't
        // expose multi-level DOM. Floor at 1 to avoid divide-by-zero on dead-quote rows.
        decimal top1Total = Math.Max(ob.BidVolume + ob.AskVolume, 1m);
        decimal imbTop1   = Math.Clamp(ob.BidVolume / top1Total, 0m, 1m);

        decimal imbTop5 = imbTop1;
        decimal totalLiquidityRaw = ob.BidVolume + ob.AskVolume;
        decimal slopeBid = 0m, slopeAsk = 0m;

        if (!string.IsNullOrWhiteSpace(ob.LevelsJson))
        {
            try
            {
                var depth = JsonSerializer.Deserialize<DepthEnvelope>(ob.LevelsJson);
                if (depth is not null)
                {
                    var bids = (depth.Bids ?? Array.Empty<DepthLevel>()).Take(TopNLevels).ToList();
                    var asks = (depth.Asks ?? Array.Empty<DepthLevel>()).Take(TopNLevels).ToList();
                    decimal bidVol = bids.Sum(l => l.V);
                    decimal askVol = asks.Sum(l => l.V);
                    decimal totalN = Math.Max(bidVol + askVol, 1m);
                    imbTop5            = Math.Clamp(bidVol / totalN, 0m, 1m);
                    totalLiquidityRaw  = bidVol + askVol;
                    slopeBid           = ComputeDepthSlope(bids, ascendingFromBest: false);
                    slopeAsk           = ComputeDepthSlope(asks, ascendingFromBest: true);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex,
                    "OrderBookFeatureProvider: invalid LevelsJson for {Symbol} snapshot {Id} — using top-of-book only",
                    symbol, ob.Id);
            }
        }

        // Normalise total liquidity via tanh(log(1+vol)/8). Calibrated to map typical
        // FX liquidity (few hundred thousand to tens of millions) into roughly (0.3, 0.95).
        decimal liqNorm = (decimal)Math.Tanh(Math.Log((double)Math.Max(1m, totalLiquidityRaw) + 1.0) / 8.0);

        var snapshot = new OrderBookFeatureSnapshot(
            BookImbalanceTop1:   imbTop1,
            BookImbalanceTop5:   imbTop5,
            TotalLiquidityNorm:  Math.Clamp(liqNorm, 0m, 1m),
            BookSlopeBid:        slopeBid,
            BookSlopeAsk:        slopeAsk);

        _cache.Set(cacheKey, snapshot, CacheTtl);
        return snapshot;
    }

    /// <summary>
    /// Slope = mean per-level Δvolume / Δprice as we walk away from best. Positive
    /// indicates volume grows as we move further from the best price — a "thick" book.
    /// Negative or near-zero indicates a "thin" book (most volume at top, falls off fast).
    /// Clamped [-1, 1] for feature stability.
    /// </summary>
    private static decimal ComputeDepthSlope(IReadOnlyList<DepthLevel> levels, bool ascendingFromBest)
    {
        if (levels.Count < 2) return 0m;
        decimal slopeSum = 0m;
        int n = 0;
        for (int i = 1; i < levels.Count; i++)
        {
            decimal dV = levels[i].V - levels[i - 1].V;
            decimal dP = ascendingFromBest
                ? levels[i].P - levels[i - 1].P
                : levels[i - 1].P - levels[i].P;
            if (dP <= 0m) continue;
            slopeSum += dV / dP;
            n++;
        }
        if (n == 0) return 0m;
        decimal mean = slopeSum / n;
        // Normalise into [-1, 1] via tanh so cross-symbol scale doesn't blow up.
        return Math.Clamp((decimal)Math.Tanh((double)mean / 1_000_000.0), -1m, 1m);
    }

    private sealed record DepthEnvelope(DepthLevel[]? Bids, DepthLevel[]? Asks);
    private sealed record DepthLevel(decimal P, decimal V);
}
