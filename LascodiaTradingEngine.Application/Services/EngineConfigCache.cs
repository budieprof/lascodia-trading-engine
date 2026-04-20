using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Process-lifetime cache for <c>EngineConfig</c> raw string values keyed by
/// <c>Key</c>.
///
/// <para>
/// Before this cache the hot tick path issued a fresh <c>EngineConfig</c>
/// query per key lookup — multiple lookups per tick for the backtest
/// qualification gate alone. The values change only via
/// <c>UpsertEngineConfigCommand</c>, which invalidates the cached key
/// atomically with its write so callers observe the new value on the very next
/// tick. A TTL (default 300 s) acts as a safety net for multi-instance
/// deployments where another instance wrote the key — in-process invalidation
/// only covers the local writer.
/// </para>
///
/// <para>
/// The cache stores raw string values; callers do their own parsing. Absent
/// keys are cached as <c>null</c> so we don't re-query every tick for a key
/// that simply hasn't been set. <see cref="InvalidateAll"/> exists for
/// emergencies (operator-driven full-cache flush) but is not called from
/// production paths.
/// </para>
/// </summary>
[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
public sealed class EngineConfigCache
{
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public EngineConfigCache(TradingMetrics metrics, TimeProvider timeProvider)
    {
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the cached raw string value for <paramref name="key"/>, or loads
    /// it from the DB on miss / TTL expiry. Returns <c>null</c> when no row
    /// exists for the key (positively cached so repeated absent lookups do not
    /// re-query). A <paramref name="ttlSeconds"/> &le; 0 bypasses the cache
    /// and always queries.
    /// </summary>
    public async Task<string?> GetRawAsync(
        DbContext ctx,
        string key,
        int ttlSeconds,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (ttlSeconds > 0
            && _entries.TryGetValue(key, out var entry)
            && (now - entry.CachedAt) < TimeSpan.FromSeconds(ttlSeconds))
        {
            _metrics.EngineConfigCacheHits.Add(1);
            return entry.Value;
        }

        _metrics.EngineConfigCacheMisses.Add(1);

        var row = await ctx.Set<Domain.Entities.EngineConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, ct);

        var value = row?.Value;

        if (ttlSeconds > 0)
            _entries[key] = new Entry(value, now);

        return value;
    }

    /// <summary>
    /// Typed helper: read + parse an <see cref="int"/>. Returns
    /// <paramref name="defaultValue"/> on missing key or parse failure.
    /// </summary>
    public async Task<int> GetIntAsync(DbContext ctx, string key, int defaultValue, int ttlSeconds, CancellationToken ct)
    {
        var raw = await GetRawAsync(ctx, key, ttlSeconds, ct);
        return raw is not null && int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    /// <summary>Typed helper: read + parse a <see cref="bool"/>.</summary>
    public async Task<bool> GetBoolAsync(DbContext ctx, string key, bool defaultValue, int ttlSeconds, CancellationToken ct)
    {
        var raw = await GetRawAsync(ctx, key, ttlSeconds, ct);
        return raw is not null && bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    /// <summary>Typed helper: read + parse a <see cref="double"/>.</summary>
    public async Task<double> GetDoubleAsync(DbContext ctx, string key, double defaultValue, int ttlSeconds, CancellationToken ct)
    {
        var raw = await GetRawAsync(ctx, key, ttlSeconds, ct);
        return raw is not null && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : defaultValue;
    }

    /// <summary>
    /// Drops the cached entry for <paramref name="key"/> so the next lookup
    /// refreshes from the DB. Called by <c>UpsertEngineConfigCommandHandler</c>
    /// after a write so in-process callers see the new value immediately.
    /// </summary>
    public void Invalidate(string key)
    {
        _entries.TryRemove(key, out _);
    }

    /// <summary>Drops every cached entry — emergency flush, not a hot path.</summary>
    public void InvalidateAll()
    {
        _entries.Clear();
    }

    /// <summary>Test hook — never called from production paths.</summary>
    internal int ApproximateCount => _entries.Count;

    private readonly record struct Entry(string? Value, DateTime CachedAt);
}
