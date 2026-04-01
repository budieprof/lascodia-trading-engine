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

    /// <summary>Feature schema version. Vectors with mismatched versions are invalidated.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>JSON array of feature names in the same order as the Features array.</summary>
    public string FeatureNamesJson { get; set; } = "[]";

    /// <summary>When this feature vector was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
