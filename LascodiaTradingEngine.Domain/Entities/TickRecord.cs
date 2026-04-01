using Lascodia.Trading.Engine.SharedDomain.Common;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Persisted tick record for microstructure analysis, TCA benchmarking, and realistic
/// backtesting with actual spread dynamics. Stored in the hot tier for 30 days,
/// then migrated to warm (Parquet) storage by the DataRetentionWorker.
/// </summary>
public class TickRecord : Entity<long>
{
    /// <summary>Currency pair symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Bid price at this tick.</summary>
    public decimal Bid { get; set; }

    /// <summary>Ask price at this tick.</summary>
    public decimal Ask { get; set; }

    /// <summary>Computed mid-price: (Bid + Ask) / 2.</summary>
    public decimal Mid { get; set; }

    /// <summary>Spread in points at this tick.</summary>
    public decimal SpreadPoints { get; set; }

    /// <summary>Tick volume (if available from broker).</summary>
    public long TickVolume { get; set; }

    /// <summary>EA instance that streamed this tick.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Timestamp from broker/EA when tick occurred.</summary>
    public DateTime TickTimestamp { get; set; }

    /// <summary>Server receipt timestamp for latency measurement.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
