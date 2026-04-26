using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for Hawkes process kernel fitting.</summary>
public sealed class MLHawkesProcessOptions : ConfigurationOption<MLHawkesProcessOptions>
{
    /// <summary>Whether the Hawkes kernel fitting worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first fit cycle runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active symbol/timeframe signal streams are refitted.</summary>
    public int PollIntervalSeconds { get; set; } = 24 * 60 * 60;

    /// <summary>Number of recent calendar days included in each calibration window.</summary>
    public int CalibrationWindowDays { get; set; } = 30;

    /// <summary>Minimum signal timestamps required before a kernel is considered fit-worthy.</summary>
    public int MinimumFitSamples { get; set; } = 20;

    /// <summary>Maximum symbol/timeframe streams processed in one worker cycle.</summary>
    public int MaxPairsPerCycle { get; set; } = 128;

    /// <summary>Maximum recent signal timestamps loaded per symbol/timeframe stream.</summary>
    public int MaxSignalsPerPair { get; set; } = 5_000;

    /// <summary>Upper bound for alpha / beta so fitted kernels remain stationary.</summary>
    public double MaximumBranchingRatio { get; set; } = 0.95;

    /// <summary>Coordinate-search sweeps per optimisation starting point.</summary>
    public int OptimisationSweeps { get; set; } = 40;

    /// <summary>Maximum optimisation starting points evaluated per fit.</summary>
    public int MaxOptimisationStarts { get; set; } = 24;

    /// <summary>Intensity multiplier above baseline mu that suppresses clustered signals.</summary>
    public double SuppressMultiplier { get; set; } = 2.0;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
