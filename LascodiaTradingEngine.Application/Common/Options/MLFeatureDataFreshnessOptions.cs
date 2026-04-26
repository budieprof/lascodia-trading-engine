using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ML feature-source freshness monitoring.</summary>
public sealed class MLFeatureDataFreshnessOptions : ConfigurationOption<MLFeatureDataFreshnessOptions>
{
    /// <summary>Whether the feature-data freshness worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first freshness scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often feature data sources are evaluated for staleness.</summary>
    public int PollIntervalSeconds { get; set; } = 1_800;

    /// <summary>Maximum accepted age for the latest COT report.</summary>
    public int MaxCotAgeDays { get; set; } = 10;

    /// <summary>Maximum accepted age for the latest sentiment snapshot.</summary>
    public int MaxSentimentAgeHours { get; set; } = 24;

    /// <summary>Expected-bar-duration multiplier used to flag stale closed candles.</summary>
    public double CandleStaleMultiplier { get; set; } = 3.0;

    /// <summary>Maximum active symbol/timeframe pairs evaluated in a single cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 5_000;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between notifications for the same feature freshness alert.</summary>
    public int AlertCooldownSeconds { get; set; } = AlertCooldownDefaults.Default_MLMonitoring;

    /// <summary>Logical destination label included in feature freshness alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
