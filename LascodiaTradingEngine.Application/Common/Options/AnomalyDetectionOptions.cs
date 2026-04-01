using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for market data anomaly detection thresholds.</summary>
public class AnomalyDetectionOptions : ConfigurationOption<AnomalyDetectionOptions>
{
    /// <summary>Price spike threshold as multiple of ATR (e.g. 5.0 = 5x ATR).</summary>
    public decimal PriceSpikeAtrMultiple { get; set; } = 5.0m;

    /// <summary>Maximum seconds bid/ask can be unchanged during an active session before flagging as stale.</summary>
    public int StaleQuoteMaxSeconds { get; set; } = 30;

    /// <summary>Volume anomaly threshold as multiple of recent average.</summary>
    public decimal VolumeAnomalyMultiple { get; set; } = 10.0m;

    /// <summary>Whether to quarantine anomalous data points (true) or just log and pass through (false).</summary>
    public bool QuarantineAnomalies { get; set; } = true;
}
