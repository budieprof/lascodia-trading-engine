using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults and bounds for feature-importance consensus monitoring.</summary>
public sealed class MLFeatureConsensusOptions : ConfigurationOption<MLFeatureConsensusOptions>
{
    /// <summary>Whether feature consensus monitoring is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional startup delay after the shared worker startup sequencer.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often the worker recomputes feature consensus snapshots.</summary>
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>Minimum valid model snapshots needed before writing consensus.</summary>
    public int MinModelsForConsensus { get; set; } = 3;

    /// <summary>Minimum distinct learner architectures needed before writing consensus.</summary>
    public int MinArchitecturesForConsensus { get; set; } = 2;

    /// <summary>Timeout for acquiring the singleton feature-consensus cycle lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Minimum time between snapshots for the same symbol/timeframe.</summary>
    public int MinSnapshotSpacingSeconds { get; set; } = 300;

    /// <summary>Maximum models loaded for one symbol/timeframe pair.</summary>
    public int MaxModelsPerPair { get; set; } = 128;

    /// <summary>Maximum symbol/timeframe pairs processed in one cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 1000;

    /// <summary>Relational database command timeout applied to each cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
