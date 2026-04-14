using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for tiered data retention and pruning policies.</summary>
public class DataRetentionOptions : ConfigurationOption<DataRetentionOptions>
{
    /// <summary>Prediction logs hot retention in days.</summary>
    public int PredictionLogHotDays { get; set; } = 90;

    /// <summary>Tick records hot retention in days.</summary>
    public int TickRecordHotDays { get; set; } = 30;

    /// <summary>Decision logs hot retention in days.</summary>
    public int DecisionLogHotDays { get; set; } = 365;

    /// <summary>Candle data hot retention in days.</summary>
    public int CandleHotDays { get; set; } = 730;

    /// <summary>Idempotency key TTL in hours.</summary>
    public int IdempotencyKeyTtlHours { get; set; } = 24;

    /// <summary>Worker health snapshots retention in days.</summary>
    public int WorkerHealthSnapshotDays { get; set; } = 30;

    /// <summary>Market data anomaly records retention in days.</summary>
    public int MarketDataAnomalyDays { get; set; } = 90;

    /// <summary>Integration event log retention in days. Only Published (state=2) events are purged;
    /// failed and in-progress events are kept for the retry worker.</summary>
    public int IntegrationEventLogPublishedDays { get; set; } = 7;

    /// <summary>Batch size for each retention sweep cycle.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Polling interval in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 3600;
}
