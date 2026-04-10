using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Pre-computes and persists feature vectors per (symbol, timeframe, bar) after each candle close.
/// Both training and scoring read from the store to guarantee training/serving parity.
/// Stores feature schema version to detect schema drift.
/// </summary>
public record StoredFeatureVector(
    long CandleId,
    string Symbol,
    Timeframe Timeframe,
    DateTime BarTimestamp,
    double[] Features,
    int SchemaVersion,
    string[] FeatureNames)
{
    /// <summary>
    /// Deterministic content hash of the feature schema. Null for legacy vectors
    /// that predate the versioned feature store.
    /// </summary>
    public string? SchemaHash { get; init; }

    /// <summary>When this feature vector was computed.</summary>
    public DateTime ComputedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface IFeatureStore
{
    // ── Original API (backward compatible) ───────────────────────────────────

    /// <summary>Persists a single feature vector to the store.</summary>
    Task PersistAsync(
        StoredFeatureVector vector,
        CancellationToken cancellationToken);

    /// <summary>Persists a batch of feature vectors to the store.</summary>
    Task PersistBatchAsync(
        IReadOnlyList<StoredFeatureVector> vectors,
        CancellationToken cancellationToken);

    /// <summary>Retrieves a single feature vector by symbol, timeframe, and bar timestamp.</summary>
    Task<StoredFeatureVector?> GetAsync(
        string symbol,
        Timeframe timeframe,
        DateTime barTimestamp,
        CancellationToken cancellationToken);

    /// <summary>Retrieves all feature vectors for a symbol/timeframe within the given date range.</summary>
    Task<IReadOnlyList<StoredFeatureVector>> GetRangeAsync(
        string symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken);

    /// <summary>Current feature schema version (legacy integer). Incremented when feature definitions change.</summary>
    int CurrentSchemaVersion { get; }

    // ── Versioned feature store extensions ────────────────────────────────────

    /// <summary>
    /// Computes a deterministic hash of the current feature schema:
    /// feature names, lookback window, channel count, and computation version tag.
    /// Changes whenever the feature computation logic changes.
    /// </summary>
    string CurrentSchemaHash { get; }

    /// <summary>
    /// Returns the feature vector for a symbol/timeframe/bar as it would have been computed
    /// at the specified point in time. Ensures no look-ahead bias by excluding data
    /// that arrived after the requested timestamp.
    /// </summary>
    /// <param name="symbol">Currency pair symbol.</param>
    /// <param name="timeframe">Candle timeframe.</param>
    /// <param name="barTimestamp">The bar this feature vector represents.</param>
    /// <param name="asOfUtc">Point-in-time cutoff. Only vectors computed at or before this time are considered.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest feature vector computed at or before asOfUtc, or null if none exists.</returns>
    Task<StoredFeatureVector?> GetPointInTimeAsync(
        string symbol,
        Timeframe timeframe,
        DateTime barTimestamp,
        DateTime asOfUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves a contiguous range of feature vectors for training.
    /// Returns cache hits matching the required schema and a list of missing bar timestamps
    /// that need recomputation from candles.
    /// </summary>
    /// <param name="symbol">Currency pair symbol.</param>
    /// <param name="timeframe">Candle timeframe.</param>
    /// <param name="from">Start of date range (inclusive).</param>
    /// <param name="to">End of date range (inclusive).</param>
    /// <param name="requiredSchemaHash">Only vectors with this schema hash are returned as cached.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of cached vectors and missing bar timestamps that require recomputation.</returns>
    Task<(List<StoredFeatureVector> Cached, List<DateTime> Missing)> GetBatchAsync(
        string symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        string requiredSchemaHash,
        CancellationToken ct = default);

    /// <summary>
    /// Records the computation lineage for a feature vector batch:
    /// which candles were used, what schema version, what parameters (lookback, etc.).
    /// Enables reproducibility auditing.
    /// </summary>
    /// <param name="symbol">Currency pair symbol.</param>
    /// <param name="timeframe">Candle timeframe.</param>
    /// <param name="schemaHash">Deterministic schema hash at computation time.</param>
    /// <param name="oldestCandleUsed">Timestamp of the oldest candle consumed.</param>
    /// <param name="newestCandleUsed">Timestamp of the newest candle consumed.</param>
    /// <param name="candleCount">Number of candles consumed.</param>
    /// <param name="featureCount">Number of features per vector.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordLineageAsync(
        string symbol,
        Timeframe timeframe,
        string schemaHash,
        DateTime oldestCandleUsed,
        DateTime newestCandleUsed,
        int candleCount,
        int featureCount,
        CancellationToken ct = default);

    /// <summary>
    /// Evicts cached feature vectors older than the retention period
    /// or belonging to deprecated schema versions (any schema hash that does not match
    /// <paramref name="currentSchemaHash"/>). Called periodically by a background worker.
    /// </summary>
    /// <param name="maxAge">Feature vectors older than this are evicted regardless of schema.</param>
    /// <param name="currentSchemaHash">The current schema hash. Vectors with a different hash are evicted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of feature vectors evicted.</returns>
    Task<int> EvictStaleAsync(
        TimeSpan maxAge,
        string currentSchemaHash,
        CancellationToken ct = default);
}
