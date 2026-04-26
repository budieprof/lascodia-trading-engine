using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.MLModels.Shared;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for feature-interaction discovery.</summary>
public sealed class MLFeatureInteractionOptions : ConfigurationOption<MLFeatureInteractionOptions>
{
    /// <summary>Whether the feature-interaction worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first interaction scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active models are scanned for pairwise feature interactions.</summary>
    public int PollIntervalSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>Maximum interaction candidates persisted per model.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Number of top-ranked candidates marked replayable for future training.</summary>
    public int IncludedTopN { get; set; } = 3;

    /// <summary>Minimum resolved prediction rows required before a model is evaluated.</summary>
    public int MinSamples { get; set; } = 100;

    /// <summary>Maximum recent prediction logs loaded per model.</summary>
    public int MaxLogsPerModel { get; set; } = 1_000;

    /// <summary>Maximum base features considered when testing pairwise products.</summary>
    public int MaxFeatures { get; set; } = MLFeatureHelper.FeatureCountV7;

    /// <summary>Maximum active models evaluated in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 256;

    /// <summary>Minimum incremental R-squared required before persisting a candidate.</summary>
    public double MinEffectSize { get; set; } = 0.001;

    /// <summary>Maximum Benjamini-Hochberg adjusted p-value allowed for a candidate.</summary>
    public double MaxQValue { get; set; } = 0.20;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
