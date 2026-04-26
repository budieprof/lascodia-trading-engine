using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for feature-rank-shift monitoring.</summary>
public sealed class MLFeatureRankShiftOptions : ConfigurationOption<MLFeatureRankShiftOptions>
{
    /// <summary>Whether the feature-rank-shift worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active champions are compared with their previous generation.</summary>
    public int PollIntervalSeconds { get; set; } = 3_600;

    /// <summary>Top-N features from each model included in the rank-union comparison.</summary>
    public int TopFeatures { get; set; } = 10;

    /// <summary>Minimum feature-union size required before Spearman rank correlation is trusted.</summary>
    public int MinUnionFeatures { get; set; } = 3;

    /// <summary>Alert when Spearman rank correlation falls below this threshold.</summary>
    public double RankCorrelationThreshold { get; set; } = 0.50;

    /// <summary>How far back to search for the previous superseded champion.</summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>Maximum active champions evaluated in a single cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 1_000;

    /// <summary>Maximum number of diverging features included in alert payloads.</summary>
    public int MaxDivergingFeaturesInAlert { get; set; } = 5;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between notifications for the same rank-shift alert.</summary>
    public int AlertCooldownSeconds { get; set; } = AlertCooldownDefaults.Default_MLMonitoring;

    /// <summary>Logical destination label included in rank-shift alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
