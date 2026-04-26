using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for live isotonic recalibration of active ML model snapshots.</summary>
public sealed class MLIsotonicRecalibrationOptions : ConfigurationOption<MLIsotonicRecalibrationOptions>
{
    /// <summary>Whether live isotonic recalibration is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional startup delay after the shared worker startup sequencer.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often the worker scans active models for recalibration opportunities.</summary>
    public int PollIntervalSeconds { get; set; } = 28_800;

    /// <summary>Look-back window for resolved prediction logs.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>Minimum resolved prediction logs required before fitting PAVA.</summary>
    public int MinResolved { get; set; } = 50;

    /// <summary>Maximum active models scanned in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 10_000;

    /// <summary>Maximum recent resolved prediction logs loaded per model.</summary>
    public int MaxPredictionLogsPerModel { get; set; } = 50_000;

    /// <summary>Minimum PAVA segments required before a fitted curve is considered useful.</summary>
    public int MinPavaSegments { get; set; } = 2;

    /// <summary>Maximum persisted isotonic breakpoint segments to prevent oversized snapshots.</summary>
    public int MaxBreakpoints { get; set; } = 1_000;

    /// <summary>Minimum ECE improvement required before persisting a new curve.</summary>
    public double MinimumEceImprovement { get; set; }

    /// <summary>Timeout for acquiring the singleton recalibration-cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Relational database command timeout applied to each recalibration cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
