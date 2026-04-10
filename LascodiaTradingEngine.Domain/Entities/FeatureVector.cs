using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persisted feature vector for a single candle bar, used by both ML training and live scoring
/// to guarantee training-serving parity. Computed by the FeaturePreComputationWorker after
/// each candle close and backfilled by the FeatureStoreBackfillWorker for historical data.
/// </summary>
public class FeatureVector : Entity<long>
{
    /// <summary>FK to the source candle.</summary>
    public long CandleId { get; set; }

    /// <summary>Currency pair symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Timeframe of the source candle.</summary>
    public Timeframe Timeframe { get; set; }

    /// <summary>Timestamp of the bar this feature vector represents.</summary>
    public DateTime BarTimestamp { get; set; }

    /// <summary>
    /// Serialised float[] feature values. Stored as byte[] for compact storage.
    /// Deserialise via Buffer.BlockCopy or BitConverter.
    /// </summary>
    public byte[] Features { get; set; } = Array.Empty<byte>();

    /// <summary>Feature schema version (legacy integer). Kept for backward compatibility.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>JSON array of feature names in the same order as the Features array.</summary>
    public string FeatureNamesJson { get; set; } = "[]";

    /// <summary>When this feature vector was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Deterministic content hash of the feature schema (feature names + lookback + channel count + version tag).
    /// Used for schema drift detection. Null for vectors created before versioned feature store.
    /// NOTE: Requires migration to add this column.
    /// </summary>
    public string? SchemaHash { get; set; }

    /// <summary>
    /// Number of features in this vector. Stored redundantly for fast validation without deserializing Features.
    /// NOTE: Requires migration to add this column.
    /// </summary>
    public int FeatureCount { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}

/// <summary>
/// Audit record tracking which candle range was used to compute a batch of feature vectors.
/// Enables reproducibility auditing for ML training runs.
/// Write-only at runtime; queried only for offline audit.
/// NOTE: Requires migration to create this table.
/// </summary>
public class FeatureVectorLineage : Entity<long>
{
    /// <summary>Currency pair symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Timeframe of the source candles.</summary>
    public Timeframe Timeframe { get; set; }

    /// <summary>Deterministic schema hash at computation time.</summary>
    public string SchemaHash { get; set; } = string.Empty;

    /// <summary>Timestamp of the oldest candle used in computation.</summary>
    public DateTime OldestCandleUsed { get; set; }

    /// <summary>Timestamp of the newest candle used in computation.</summary>
    public DateTime NewestCandleUsed { get; set; }

    /// <summary>Number of candles consumed to produce the feature vectors.</summary>
    public int CandleCount { get; set; }

    /// <summary>Number of features per vector at computation time.</summary>
    public int FeatureCount { get; set; }

    /// <summary>When this lineage record was created.</summary>
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
