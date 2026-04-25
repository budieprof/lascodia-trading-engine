using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for initial live conformal calibration persistence.</summary>
public class MLConformalCalibrationOptions : ConfigurationOption<MLConformalCalibrationOptions>
{
    /// <summary>Delay after application startup before the first calibration cycle runs.</summary>
    public int InitialDelayMinutes { get; set; } = 20;

    /// <summary>How often the worker scans active models for missing usable calibration rows.</summary>
    public int PollIntervalMinutes { get; set; } = 30;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized workers.</summary>
    public int PollJitterSeconds { get; set; } = 300;

    /// <summary>Maximum recent resolved prediction logs considered per model.</summary>
    public int MaxLogs { get; set; } = 500;

    /// <summary>Minimum usable resolved prediction logs required before writing a calibration.</summary>
    public int MinLogs { get; set; } = 50;

    /// <summary>Maximum age of resolved prediction logs used as calibration evidence.</summary>
    public int MaxLogAgeDays { get; set; } = 30;

    /// <summary>Maximum age of an existing calibration row before it must be refreshed.</summary>
    public int MaxCalibrationAgeDays { get; set; } = 30;

    /// <summary>Target marginal conformal coverage level.</summary>
    public double TargetCoverage { get; set; } = 0.90;

    /// <summary>Maximum active models evaluated per database batch.</summary>
    public int ModelBatchSize { get; set; } = 100;

    /// <summary>Maximum active models evaluated in one worker cycle.</summary>
    public int MaxCycleModels { get; set; } = 10_000;

    /// <summary>Timeout for acquiring the singleton calibration-cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>When true, calibration evidence must be resolved at or after model activation.</summary>
    public bool RequirePostActivationLogs { get; set; } = true;
}
