using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using Timeframe = LascodiaTradingEngine.Domain.Enums.Timeframe;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Process-lifetime cache for the most-recent <c>MarketRegimeSnapshot.Regime</c>
/// per <c>(Symbol, Timeframe)</c>.
///
/// <para>
/// The tick path previously re-queried the regime table once per distinct
/// timeframe PER TICK, for every symbol that received a price event. Regime
/// detection runs on minute-to-hour cadences, so that per-tick grouped query
/// was almost always returning the same value it returned last tick. This
/// cache collapses those queries to one per (Symbol, Timeframe, TTL-window)
/// with event-invalidation on regime change — <c>RegimeDetectionWorker</c>
/// calls <see cref="Invalidate"/> whenever a snapshot's Regime differs from
/// the prior one.
/// </para>
///
/// <para>
/// On miss or TTL expiry, <see cref="GetAsync"/> performs a single
/// <c>OrderByDescending(DetectedAt).Select(r => r.Regime)</c> query and caches
/// the result. An explicit null sentinel represents "no snapshot exists"
/// (freshly-added symbol) so repeated missing lookups don't re-query.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
public sealed class MarketRegimeCache
{
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string Symbol, Timeframe Timeframe), Entry> _entries = new();

    public MarketRegimeCache(TradingMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the cached regime for <c>(symbol, timeframe)</c> if within
    /// <paramref name="ttlSeconds"/>; otherwise queries the DB and caches.
    /// Returns <c>null</c> when no snapshot exists for the pair (distinct
    /// from "unknown" — we positively cache the absence).
    /// </summary>
    public async Task<MarketRegimeEnum?> GetAsync(
        DbContext ctx,
        string symbol,
        Timeframe timeframe,
        int ttlSeconds,
        CancellationToken ct)
    {
        var key = (symbol, timeframe);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (ttlSeconds > 0
            && _entries.TryGetValue(key, out var entry)
            && (now - entry.CachedAt) < TimeSpan.FromSeconds(ttlSeconds))
        {
            _metrics.MarketRegimeCacheHits.Add(1);
            return entry.Regime;
        }

        _metrics.MarketRegimeCacheMisses.Add(1);

        // Read the most recent snapshot for this pair. Returning a nullable
        // regime lets us distinguish "cached absence" from "regime = default(0)".
        var rows = await ctx.Set<Domain.Entities.MarketRegimeSnapshot>()
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && !x.IsDeleted)
            .OrderByDescending(x => x.DetectedAt)
            .Select(x => (MarketRegimeEnum?)x.Regime)
            .Take(1)
            .ToListAsync(ct);

        var regime = rows.Count > 0 ? rows[0] : null;

        if (ttlSeconds > 0)
            _entries[key] = new Entry(regime, now);

        return regime;
    }

    /// <summary>
    /// Drops the cached entry for <paramref name="symbol"/> ×
    /// <paramref name="timeframe"/>, forcing a DB refresh on next read. Called
    /// by <c>RegimeDetectionWorker</c> when a snapshot's regime differs from
    /// the prior one.
    /// </summary>
    public void Invalidate(string symbol, Timeframe timeframe)
    {
        _entries.TryRemove((symbol, timeframe), out _);
    }

    /// <summary>
    /// Drops every cached entry for <paramref name="symbol"/> regardless of
    /// timeframe. Useful when a symbol's entire detection state becomes
    /// suspect (e.g. EA data outage).
    /// </summary>
    public void InvalidateSymbol(string symbol)
    {
        foreach (var k in _entries.Keys)
        {
            if (string.Equals(k.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                _entries.TryRemove(k, out _);
        }
    }

    /// <summary>Test hook: clear the cache. Never called from production paths.</summary>
    internal void ClearForTests() => _entries.Clear();

    private readonly record struct Entry(MarketRegimeEnum? Regime, DateTime CachedAt);
}
