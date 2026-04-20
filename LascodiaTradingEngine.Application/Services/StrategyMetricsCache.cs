using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Snapshot of the latest per-strategy performance signals used by the tick-time
/// conflict resolver and health gate. A missing <see cref="HealthStatus"/> means
/// no snapshot has been recorded yet (newly-activated strategy) — treat as healthy.
/// </summary>
public readonly record struct StrategyMetricsSnapshot(
    decimal Sharpe,
    StrategyHealthStatus? HealthStatus);

/// <summary>
/// Process-lifetime cache for the per-tick pre-fetch of strategy performance
/// metrics (Sharpe + health status from <c>StrategyPerformanceSnapshot</c>).
///
/// <para>
/// The cache eliminates two per-tick grouped queries against the snapshot table
/// per symbol (one for health-critical filtering, one for Sharpe ranking) and
/// replaces them with a single batch refresh bounded by
/// <c>StrategyEvaluatorOptions.StrategyMetricsCacheTtlSeconds</c>. Freshness is
/// guaranteed across meaningful state changes via event-driven invalidation —
/// the owning worker (<see cref="Workers.StrategyWorker"/>) calls
/// <see cref="Invalidate"/> whenever a <c>BacktestCompletedIntegrationEvent</c>
/// or <c>StrategyActivatedIntegrationEvent</c> fires for a strategy so the next
/// tick reloads that strategy's snapshot from the DB.
/// </para>
///
/// <para>
/// Thread-safety: all state is in <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// and concurrent <see cref="GetManyAsync"/> callers race harmlessly on refill —
/// the last writer wins on the cache entry, but either writer's data is valid.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
public sealed class StrategyMetricsCache
{
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<long, CachedEntry> _entries = new();

    public StrategyMetricsCache(TradingMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the latest metrics snapshot for each requested strategy. Entries
    /// older than <paramref name="ttlSeconds"/> (or missing) are refreshed from
    /// the DB in a single grouped query under the caller's cancellation token.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, StrategyMetricsSnapshot>> GetManyAsync(
        DbContext ctx,
        IReadOnlyList<long> strategyIds,
        int ttlSeconds,
        CancellationToken ct)
    {
        if (strategyIds.Count == 0)
            return new Dictionary<long, StrategyMetricsSnapshot>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var ttl = TimeSpan.FromSeconds(Math.Max(1, ttlSeconds));
        var result = new Dictionary<long, StrategyMetricsSnapshot>(strategyIds.Count);
        var toRefresh = new List<long>();

        foreach (var id in strategyIds)
        {
            if (_entries.TryGetValue(id, out var entry) && (now - entry.CachedAt) < ttl)
            {
                result[id] = entry.Snapshot;
                _metrics.StrategyMetricsCacheHits.Add(1);
            }
            else
            {
                toRefresh.Add(id);
            }
        }

        if (toRefresh.Count > 0)
        {
            _metrics.StrategyMetricsCacheMisses.Add(toRefresh.Count);

            // Single batch query for all strategies that need refresh.
            var refreshed = await ctx.Set<Domain.Entities.StrategyPerformanceSnapshot>()
                .Where(x => toRefresh.Contains(x.StrategyId) && !x.IsDeleted)
                .GroupBy(x => x.StrategyId)
                .Select(g => new
                {
                    StrategyId   = g.Key,
                    SharpeRatio  = g.OrderByDescending(x => x.EvaluatedAt).First().SharpeRatio,
                    HealthStatus = g.OrderByDescending(x => x.EvaluatedAt).First().HealthStatus
                })
                .ToListAsync(ct);

            var refreshedSet = new HashSet<long>(refreshed.Count);
            foreach (var row in refreshed)
            {
                var snap = new StrategyMetricsSnapshot(row.SharpeRatio, row.HealthStatus);
                _entries[row.StrategyId] = new CachedEntry(snap, now);
                result[row.StrategyId] = snap;
                refreshedSet.Add(row.StrategyId);
            }

            // Strategies with no snapshot yet: cache a sentinel so we don't re-query
            // every tick for freshly-activated strategies that haven't been scored.
            foreach (var id in toRefresh)
            {
                if (!refreshedSet.Contains(id))
                {
                    var snap = new StrategyMetricsSnapshot(0m, null);
                    _entries[id] = new CachedEntry(snap, now);
                    result[id] = snap;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Drops the cached entry for <paramref name="strategyId"/> so the next
    /// <see cref="GetManyAsync"/> for it refreshes from the DB. Called by the
    /// owning worker on <c>BacktestCompletedIntegrationEvent</c> /
    /// <c>StrategyActivatedIntegrationEvent</c>.
    /// </summary>
    public void Invalidate(long strategyId, string trigger)
    {
        if (_entries.TryRemove(strategyId, out _))
        {
            _metrics.StrategyMetricsCacheInvalidations.Add(1,
                new KeyValuePair<string, object?>("trigger", trigger));
        }
    }

    /// <summary>Test hook: clear the cache. Never called from production paths.</summary>
    internal void ClearForTests() => _entries.Clear();

    private readonly record struct CachedEntry(StrategyMetricsSnapshot Snapshot, DateTime CachedAt);
}
