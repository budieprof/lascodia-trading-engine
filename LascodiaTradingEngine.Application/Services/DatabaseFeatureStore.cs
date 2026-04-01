using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Database-backed feature store that persists feature vectors for each candle bar.
/// Both training and scoring read from this store to guarantee training-serving parity.
/// Includes an in-memory LRU cache (capacity 10,000) for hot-path scoring reads.
/// Registered as Singleton — uses IServiceScopeFactory to avoid captive DbContext.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IFeatureStore))]
public class DatabaseFeatureStore : IFeatureStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseFeatureStore> _logger;

    /// <summary>Current feature schema version. Increment when feature engineering changes.</summary>
    public int CurrentSchemaVersion => 1;

    /// <summary>LRU cache keyed by "Symbol|Timeframe|Timestamp". Bounded to 10,000 entries.</summary>
    private readonly ConcurrentDictionary<string, StoredFeatureVector> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private const int MaxCacheSize = 10_000;

    public DatabaseFeatureStore(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseFeatureStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    public async Task PersistAsync(StoredFeatureVector vector, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var entity = ToEntity(vector);
        await ctx.Set<FeatureVector>().AddAsync(entity, cancellationToken);
        await ctx.SaveChangesAsync(cancellationToken);

        CacheVector(vector);
    }

    public async Task PersistBatchAsync(IReadOnlyList<StoredFeatureVector> vectors, CancellationToken cancellationToken)
    {
        if (vectors.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var entities = vectors.Select(ToEntity).ToList();
        await ctx.Set<FeatureVector>().AddRangeAsync(entities, cancellationToken);
        await ctx.SaveChangesAsync(cancellationToken);

        foreach (var v in vectors)
            CacheVector(v);
    }

    public async Task<StoredFeatureVector?> GetAsync(
        string symbol, Timeframe timeframe, DateTime barTimestamp,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(symbol, timeframe, barTimestamp);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        var entity = await ctx.Set<FeatureVector>()
            .Where(f => f.Symbol == symbol && f.Timeframe == timeframe
                     && f.BarTimestamp == barTimestamp && f.SchemaVersion == CurrentSchemaVersion
                     && !f.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null) return null;

        var vector = ToStoredVector(entity);
        CacheVector(vector);
        return vector;
    }

    public async Task<IReadOnlyList<StoredFeatureVector>> GetRangeAsync(
        string symbol, Timeframe timeframe, DateTime from, DateTime to,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        var entities = await ctx.Set<FeatureVector>()
            .Where(f => f.Symbol == symbol && f.Timeframe == timeframe
                     && f.BarTimestamp >= from && f.BarTimestamp <= to
                     && f.SchemaVersion == CurrentSchemaVersion
                     && !f.IsDeleted)
            .OrderBy(f => f.BarTimestamp)
            .ToListAsync(cancellationToken);

        return entities.Select(ToStoredVector).ToList();
    }

    // -- Serialization helpers ────────────────────────────────────────────

    private static FeatureVector ToEntity(StoredFeatureVector v)
    {
        var featureBytes = new byte[v.Features.Length * sizeof(double)];
        Buffer.BlockCopy(v.Features, 0, featureBytes, 0, featureBytes.Length);

        return new FeatureVector
        {
            CandleId         = v.CandleId,
            Symbol           = v.Symbol,
            Timeframe        = v.Timeframe,
            BarTimestamp      = v.BarTimestamp,
            Features         = featureBytes,
            SchemaVersion    = v.SchemaVersion,
            FeatureNamesJson = JsonSerializer.Serialize(v.FeatureNames),
            ComputedAt       = DateTime.UtcNow
        };
    }

    private static StoredFeatureVector ToStoredVector(FeatureVector e)
    {
        if (e.Features.Length == 0 || e.Features.Length % sizeof(double) != 0)
            return new StoredFeatureVector(e.CandleId, e.Symbol, e.Timeframe, e.BarTimestamp,
                Array.Empty<double>(), e.SchemaVersion, Array.Empty<string>());

        var features = new double[e.Features.Length / sizeof(double)];
        Buffer.BlockCopy(e.Features, 0, features, 0, e.Features.Length);

        var names = JsonSerializer.Deserialize<string[]>(e.FeatureNamesJson) ?? Array.Empty<string>();

        return new StoredFeatureVector(
            e.CandleId, e.Symbol, e.Timeframe, e.BarTimestamp,
            features, e.SchemaVersion, names);
    }

    // -- LRU cache management ─────────────────────────────────────────────

    private static string CacheKey(string symbol, Timeframe tf, DateTime ts)
        => $"{symbol}|{tf}|{ts:O}";

    private void CacheVector(StoredFeatureVector v)
    {
        var key = CacheKey(v.Symbol, v.Timeframe, v.BarTimestamp);
        _cache[key] = v;
        _cacheOrder.Enqueue(key);

        // Evict oldest entries when cache exceeds capacity
        while (_cache.Count > MaxCacheSize && _cacheOrder.TryDequeue(out var evictKey))
            _cache.TryRemove(evictKey, out _);
    }
}
