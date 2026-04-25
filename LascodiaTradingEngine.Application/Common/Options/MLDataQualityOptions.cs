using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ML market-data quality monitoring.</summary>
public class MLDataQualityOptions : ConfigurationOption<MLDataQualityOptions>
{
    /// <summary>Whether the data-quality worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; } = 45;

    /// <summary>How often active model feeds are scanned for data-quality issues.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized scans.</summary>
    public int PollJitterSeconds { get; set; } = 30;

    /// <summary>Expected-bar-duration multiplier used to detect missing closed candles.</summary>
    public double GapMultiplier { get; set; } = 2.5;

    /// <summary>Z-score threshold used to flag anomalous latest close prices.</summary>
    public double SpikeSigmas { get; set; } = 4.0;

    /// <summary>Number of prior closed candles used as the spike-detection baseline.</summary>
    public int SpikeLookbackBars { get; set; } = 50;

    /// <summary>Minimum valid prior closes required before spike detection is trusted.</summary>
    public int MinSpikeBaselineBars { get; set; } = 20;

    /// <summary>Maximum allowed age of the persisted live price snapshot.</summary>
    public int LivePriceStalenessSeconds { get; set; } = 300;

    /// <summary>Allowed future timestamp skew before a candle or live price is considered invalid.</summary>
    public int FutureTimestampToleranceSeconds { get; set; } = 60;

    /// <summary>Maximum active symbol/timeframe pairs evaluated in a single cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 1_000;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Minimum seconds between notifications for the same data-quality condition.</summary>
    public int AlertCooldownSeconds { get; set; } = 1_800;

    /// <summary>Logical destination label included in data-quality alert payloads.</summary>
    public string AlertDestination { get; set; } = "market-data";
}
