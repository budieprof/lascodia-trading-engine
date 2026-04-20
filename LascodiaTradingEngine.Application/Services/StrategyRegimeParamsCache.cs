using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Process-lifetime cache for <c>StrategyRegimeParams.ParametersJson</c> keyed by
/// <c>(StrategyId, Regime)</c>.
///
/// <para>
/// The original per-tick pattern queried this table once per strategy inside the
/// parallel evaluation loop — a hot, short-lived, and in steady-state nearly-static
/// lookup. Regime-conditional parameter rows change only when the
/// <c>OptimizationWorker</c> promotes a new set for a specific regime, so a short
/// TTL (default 120s, configurable via
/// <c>StrategyEvaluatorOptions.RegimeParamsCacheTtlSeconds</c>) trades fractional
/// freshness for a large reduction in per-tick query count.
/// </para>
///
/// <para>
/// Missing rows are cached as explicit nulls to avoid re-querying the DB every
/// tick for strategies that legitimately have no regime override for a given
/// regime. <see cref="Invalidate"/> is exposed so callers that know a regime-param
/// row has just been written (e.g. optimisation promotion) can force the next
/// tick to reload.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
public sealed class StrategyRegimeParamsCache
{
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(long StrategyId, MarketRegimeEnum Regime), CachedEntry> _entries = new();

    public StrategyRegimeParamsCache(TradingMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the cached <c>ParametersJson</c> for the given (strategy, regime)
    /// or <c>null</c> when no row exists. Refreshes from the DB on miss or after
    /// TTL expiry. A TTL of zero or negative bypasses the cache entirely.
    /// </summary>
    public async Task<string?> GetAsync(
        DbContext ctx,
        long strategyId,
        MarketRegimeEnum regime,
        int ttlSeconds,
        CancellationToken ct)
    {
        var key = (strategyId, regime);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (ttlSeconds > 0
            && _entries.TryGetValue(key, out var entry)
            && (now - entry.CachedAt) < TimeSpan.FromSeconds(ttlSeconds))
        {
            _metrics.RegimeParamsCacheHits.Add(1);
            return entry.ParametersJson;
        }

        _metrics.RegimeParamsCacheMisses.Add(1);

        var paramsJson = await ctx.Set<Domain.Entities.StrategyRegimeParams>()
            .AsNoTracking()
            .Where(p => p.StrategyId == strategyId && p.Regime == regime && !p.IsDeleted)
            .Select(p => p.ParametersJson)
            .FirstOrDefaultAsync(ct);

        if (ttlSeconds > 0)
            _entries[key] = new CachedEntry(paramsJson, now);

        return paramsJson;
    }

    /// <summary>
    /// Drops the cached entry for <paramref name="strategyId"/> × <paramref name="regime"/>,
    /// forcing a DB refresh on the next lookup. Useful when the OptimizationWorker
    /// has just persisted a new regime-params row.
    /// </summary>
    public void Invalidate(long strategyId, MarketRegimeEnum regime)
    {
        _entries.TryRemove((strategyId, regime), out _);
    }

    /// <summary>Drops all cached entries for a strategy (all regimes).</summary>
    public void InvalidateAll(long strategyId)
    {
        foreach (var k in _entries.Keys)
        {
            if (k.StrategyId == strategyId)
                _entries.TryRemove(k, out _);
        }
    }

    /// <summary>Test hook: clear the cache. Never called from production paths.</summary>
    internal void ClearForTests() => _entries.Clear();

    private readonly record struct CachedEntry(string? ParametersJson, DateTime CachedAt);
}
