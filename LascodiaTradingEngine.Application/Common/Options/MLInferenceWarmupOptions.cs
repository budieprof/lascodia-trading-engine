using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for startup ML inference warm-up.</summary>
public sealed class MLInferenceWarmupOptions : ConfigurationOption<MLInferenceWarmupOptions>
{
    /// <summary>Whether startup inference warm-up should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional delay after the worker startup phase before warm-up begins.</summary>
    public int StartupDelaySeconds { get; set; } = 5;

    /// <summary>Per-model timeout for feature construction and inference warm-up.</summary>
    public int ModelTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum snapshot-backed models warmed during one startup pass.</summary>
    public int MaxModelsPerStartup { get; set; } = 10_000;

    /// <summary>Number of model timeouts tolerated before aborting the remaining pass. Zero disables aborting.</summary>
    public int MaxTimeoutsBeforeAbort { get; set; } = 1;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the warm-up pass.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
