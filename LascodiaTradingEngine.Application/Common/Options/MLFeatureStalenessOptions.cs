using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for feature-staleness monitoring.</summary>
public sealed class MLFeatureStalenessOptions : ConfigurationOption<MLFeatureStalenessOptions>
{
    /// <summary>Whether the feature-staleness worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active model feature vectors are scanned for staleness.</summary>
    public int PollIntervalSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>Minimum usable observations required to evaluate feature staleness.</summary>
    public int MinSamples { get; set; } = 50;

    /// <summary>Maximum recent raw prediction feature rows loaded per model.</summary>
    public int MaxRowsPerModel { get; set; } = 1_000;

    /// <summary>Maximum recent candles loaded for legacy candle-derived fallback rows.</summary>
    public int MaxCandlesPerModel { get; set; } = 300;

    /// <summary>Maximum feature columns evaluated per model.</summary>
    public int MaxFeatures { get; set; } = MLFeatureHelper.FeatureCountV7;

    /// <summary>Maximum active symbol/timeframe champions evaluated in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 256;

    /// <summary>Absolute lag-1 autocorrelation threshold that marks a feature stale.</summary>
    public double AbsAutocorrThreshold { get; set; } = 0.95;

    /// <summary>Variance threshold under which a feature is treated as constant.</summary>
    public double ConstantVarianceEpsilon { get; set; } = 1.0e-9;

    /// <summary>Maximum fraction of evaluated features allowed to be marked stale.</summary>
    public double MaxStaleFeatureFraction { get; set; } = 0.25;

    /// <summary>Days to keep active staleness logs before soft-pruning them. Zero disables pruning.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
