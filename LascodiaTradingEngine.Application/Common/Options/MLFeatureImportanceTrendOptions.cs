using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for feature-importance trend monitoring.</summary>
public sealed class MLFeatureImportanceTrendOptions : ConfigurationOption<MLFeatureImportanceTrendOptions>
{
    /// <summary>Whether the feature-importance trend worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first trend scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active model pairs are scanned for decaying feature importance.</summary>
    public int PollIntervalSeconds { get; set; } = 86_400;

    /// <summary>Maximum recent model generations loaded per symbol/timeframe.</summary>
    public int GenerationsToCheck { get; set; } = 4;

    /// <summary>Minimum valid generations required before a trend decision is trusted.</summary>
    public int MinGenerations { get; set; } = 3;

    /// <summary>Latest-generation importance must be at or below this value to alert.</summary>
    public double ImportanceDecayThreshold { get; set; } = 0.005;

    /// <summary>Absolute tolerance allowed while enforcing monotone decrease.</summary>
    public double MonotonicTolerance { get; set; }

    /// <summary>Minimum first-to-latest relative drop required to alert.</summary>
    public double MinRelativeDrop { get; set; } = 0.50;

    /// <summary>Maximum active symbol/timeframe pairs evaluated in one cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 1_000;

    /// <summary>Maximum number of dying features included in alert payloads.</summary>
    public int MaxFeaturesInAlert { get; set; } = 20;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between notifications for the same feature trend alert.</summary>
    public int AlertCooldownSeconds { get; set; } = AlertCooldownDefaults.Default_MLMonitoring;

    /// <summary>Logical destination label included in feature trend alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
