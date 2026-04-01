using Lascodia.Trading.Engine.SharedDomain.Common;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Domain.Entities;

/// <summary>
/// Records a detected anomaly in incoming market data from EA instances.
/// Anomalous data points are quarantined and the last-known-good price is used
/// until the anomaly clears. Triggers alerts for manual review.
/// </summary>
public class MarketDataAnomaly : Entity<long>
{
    /// <summary>Type of anomaly detected.</summary>
    public MarketDataAnomalyType AnomalyType { get; set; }

    /// <summary>Currency pair symbol where the anomaly was detected.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>EA instance that sent the anomalous data.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The anomalous value (price, volume, timestamp, etc.).</summary>
    public decimal? AnomalousValue { get; set; }

    /// <summary>The expected/reference value for comparison.</summary>
    public decimal? ExpectedValue { get; set; }

    /// <summary>Deviation magnitude (e.g. ATR multiple for price spikes).</summary>
    public decimal? DeviationMagnitude { get; set; }

    /// <summary>Last known good bid price before the anomaly.</summary>
    public decimal? LastGoodBid { get; set; }

    /// <summary>Last known good ask price before the anomaly.</summary>
    public decimal? LastGoodAsk { get; set; }

    /// <summary>Human-readable description of the anomaly.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether the anomaly has been reviewed by a human.</summary>
    public bool IsReviewed { get; set; }

    /// <summary>Whether the anomalous data point was discarded (true) or accepted after review (false).</summary>
    public bool WasQuarantined { get; set; } = true;

    /// <summary>When the anomaly was detected.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the anomaly was resolved (cleared or reviewed).</summary>
    public DateTime? ResolvedAt { get; set; }

    public bool IsDeleted { get; set; }
    public uint RowVersion { get; set; }
}
