using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.MLModels.Shared;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Database-backed versioned feature store that persists feature vectors for each candle bar.
/// Both training and scoring read from this store to guarantee training-serving parity.
///
/// Production capabilities:
/// - Schema versioning via deterministic content hash of feature names + computation parameters
/// - Point-in-time reconstruction for reproducible backtesting (no look-ahead bias)
/// - Lineage tracking for computation auditing
/// - Batch retrieval with selective recomputation (returns cache hits + missing timestamps)
/// - Stale cache eviction for deprecated schemas and aged vectors
/// - In-memory LRU cache (capacity 10,000) for hot-path scoring reads
///
/// Registered as Singleton — uses IServiceScopeFactory to avoid captive DbContext.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IFeatureStore))]
public class DatabaseFeatureStore : IFeatureStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseFeatureStore> _logger;

    /// <summary>Current feature schema version (legacy integer). Increment when feature engineering changes.</summary>
    public int CurrentSchemaVersion => 1;

    /// <summary>
    /// Deterministic content hash of the current feature schema.
    /// Computed once at startup from MLFeatureHelper constants. Changes when feature
    /// definitions, lookback window, or channel count change.
    /// </summary>
    public string CurrentSchemaHash { get; } = ComputeCurrentSchemaVersion();

    /// <summary>LRU cache keyed by "Symbol|Timeframe|Timestamp". Bounded to 10,000 entries.</summary>
    private readonly ConcurrentDictionary<string, StoredFeatureVector> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();
    private const int MaxCacheSize = 10_000;

    /// <summary>Maximum number of rows to soft-delete per eviction batch to avoid long transactions.</summary>
    private const int EvictionBatchSize = 1_000;

    public DatabaseFeatureStore(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseFeatureStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    // ── Original API (backward compatible) ───────────────────────────────────

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

    // ── Point-in-Time Reconstruction ─────────────────────────────────────────

    public async Task<StoredFeatureVector?> GetPointInTimeAsync(
        string symbol,
        Timeframe timeframe,
        DateTime barTimestamp,
        DateTime asOfUtc,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        // Return the latest computation that was available at asOfUtc.
        // This prevents look-ahead bias: we never return a vector that was
        // computed after the requested point in time.
        var entity = await ctx.Set<FeatureVector>()
            .Where(f => f.Symbol == symbol
                     && f.Timeframe == timeframe
                     && f.BarTimestamp == barTimestamp
                     && f.ComputedAt <= asOfUtc
                     && !f.IsDeleted)
            .OrderByDescending(f => f.ComputedAt)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return null;

        return ToStoredVector(entity);
    }

    // ── Batch Retrieve with Missing Detection ────────────────────────────────

    public async Task<(List<StoredFeatureVector> Cached, List<DateTime> Missing)> GetBatchAsync(
        string symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        string requiredSchemaHash,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>().GetDbContext();

        // Load all feature vectors in the range that match the required schema hash
        var matchingEntities = await readCtx.Set<FeatureVector>()
            .Where(f => f.Symbol == symbol
                     && f.Timeframe == timeframe
                     && f.BarTimestamp >= from
                     && f.BarTimestamp <= to
                     && f.SchemaHash == requiredSchemaHash
                     && !f.IsDeleted)
            .OrderBy(f => f.BarTimestamp)
            .ToListAsync(ct);

        var cached = matchingEntities.Select(ToStoredVector).ToList();
        var cachedTimestamps = new HashSet<DateTime>(cached.Select(v => v.BarTimestamp));

        // Determine which bar timestamps exist in the range but lack feature vectors.
        // We query the candle table to find all closed bars in the range.
        var allBarTimestamps = await readCtx.Set<Candle>()
            .Where(c => c.Symbol == symbol
                     && c.Timeframe == timeframe
                     && c.IsClosed
                     && c.Timestamp >= from
                     && c.Timestamp <= to
                     && !c.IsDeleted)
            .OrderBy(c => c.Timestamp)
            .Select(c => c.Timestamp)
            .ToListAsync(ct);

        var missing = allBarTimestamps
            .Where(ts => !cachedTimestamps.Contains(ts))
            .ToList();

        return (cached, missing);
    }

    // ── Lineage Tracking ─────────────────────────────────────────────────────

    public async Task RecordLineageAsync(
        string symbol,
        Timeframe timeframe,
        string schemaHash,
        DateTime oldestCandleUsed,
        DateTime newestCandleUsed,
        int candleCount,
        int featureCount,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

        var lineage = new FeatureVectorLineage
        {
            Symbol           = symbol,
            Timeframe        = timeframe,
            SchemaHash       = schemaHash,
            OldestCandleUsed = oldestCandleUsed,
            NewestCandleUsed = newestCandleUsed,
            CandleCount      = candleCount,
            FeatureCount     = featureCount,
            RecordedAtUtc    = DateTime.UtcNow
        };

        await ctx.Set<FeatureVectorLineage>().AddAsync(lineage, ct);
        await ctx.SaveChangesAsync(ct);

        _logger.LogDebug(
            "Recorded feature lineage: {Symbol}/{Tf} schema={SchemaHash} candles={Count} range=[{Oldest:O}..{Newest:O}]",
            symbol, timeframe, schemaHash, candleCount, oldestCandleUsed, newestCandleUsed);
    }

    // ── Stale Cache Eviction ─────────────────────────────────────────────────

    public async Task<int> EvictStaleAsync(
        TimeSpan maxAge,
        string currentSchemaHash,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        int totalEvicted = 0;

        // Process in batches to avoid holding long transactions
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<IWriteApplicationDbContext>().GetDbContext();

            // Find vectors that are either:
            // 1. Older than maxAge regardless of schema, OR
            // 2. Belonging to a deprecated schema (SchemaHash != current and SchemaHash is set)
            var staleVectors = await ctx.Set<FeatureVector>()
                .Where(f => !f.IsDeleted
                    && ((f.ComputedAt < cutoff)
                        || (f.SchemaHash != null && f.SchemaHash != currentSchemaHash)))
                .OrderBy(f => f.Id)
                .Take(EvictionBatchSize)
                .ToListAsync(ct);

            if (staleVectors.Count == 0) break;

            foreach (var v in staleVectors)
                v.IsDeleted = true;

            await ctx.SaveChangesAsync(ct);
            totalEvicted += staleVectors.Count;

            _logger.LogDebug("Evicted {Count} stale feature vectors (batch)", staleVectors.Count);

            // If we got fewer than the batch size, we've processed all stale vectors
            if (staleVectors.Count < EvictionBatchSize) break;
        }

        if (totalEvicted > 0)
        {
            // Clear in-memory cache since evicted vectors may be cached
            _cache.Clear();
            while (_cacheOrder.TryDequeue(out _)) { }

            _logger.LogInformation(
                "Evicted {Total} stale feature vectors (maxAge={MaxAge}, currentSchema={Schema})",
                totalEvicted, maxAge, currentSchemaHash);
        }

        return totalEvicted;
    }

    // ── Schema Version Computation ───────────────────────────────────────────

    /// <summary>
    /// Computes a deterministic SHA-256 hash of the current feature schema:
    /// feature names, lookback window, sequence channel count, and a computation version tag.
    /// This hash changes whenever any of these values change, triggering cache invalidation.
    /// </summary>
    public static string ComputeCurrentSchemaVersion()
    {
        var sb = new StringBuilder();

        // Feature names define the schema structure
        sb.AppendJoin('|', MLFeatureHelper.FeatureNames);
        sb.Append(':');
        sb.AppendJoin('|', MLFeatureHelper.SequenceChannelNames);
        sb.Append(':');
        sb.Append(MLFeatureHelper.LookbackWindow);
        sb.Append(':');
        sb.Append(MLFeatureHelper.SequenceChannelCount);
        sb.Append(':');
        sb.Append(MLFeatureHelper.FeatureCount);
        sb.Append(':');
        // Version tag — bump this when computation logic changes but names/counts don't
        sb.Append("v2");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private FeatureVector ToEntity(StoredFeatureVector v)
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
            SchemaHash       = v.SchemaHash ?? CurrentSchemaHash,
            FeatureCount     = v.Features.Length,
            FeatureNamesJson = JsonSerializer.Serialize(v.FeatureNames),
            ComputedAt       = DateTime.UtcNow
        };
    }

    private static StoredFeatureVector ToStoredVector(FeatureVector e)
    {
        if (e.Features.Length == 0 || e.Features.Length % sizeof(double) != 0)
            return new StoredFeatureVector(e.CandleId, e.Symbol, e.Timeframe, e.BarTimestamp,
                Array.Empty<double>(), e.SchemaVersion, Array.Empty<string>())
            {
                SchemaHash = e.SchemaHash,
                ComputedAtUtc = e.ComputedAt
            };

        var features = new double[e.Features.Length / sizeof(double)];
        Buffer.BlockCopy(e.Features, 0, features, 0, e.Features.Length);

        var names = JsonSerializer.Deserialize<string[]>(e.FeatureNamesJson) ?? Array.Empty<string>();

        return new StoredFeatureVector(
            e.CandleId, e.Symbol, e.Timeframe, e.BarTimestamp,
            features, e.SchemaVersion, names)
        {
            SchemaHash = e.SchemaHash,
            ComputedAtUtc = e.ComputedAt
        };
    }

    // ── LRU cache management ─────────────────────────────────────────────────

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
