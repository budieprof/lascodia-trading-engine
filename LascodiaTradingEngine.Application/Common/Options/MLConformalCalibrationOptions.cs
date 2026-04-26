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

    /// <summary>
    /// Bounded in-process concurrency for per-model evaluation. Default 1 preserves
    /// strictly-sequential semantics; bumping fans out to N concurrent (model, log
    /// load, calibration compute, transactional persist) chains, each in its own DI scope.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>
    /// Wall-clock cycle warning threshold in seconds. The cycle-level distributed lock
    /// is held for the duration of one cycle; a long cycle risks the lock expiring and
    /// another replica re-acquiring it before this one finishes. 0 disables the warn.
    /// </summary>
    public int LongCycleWarnSeconds { get; set; } = 300;

    /// <summary>
    /// When true, the worker dispatches a durable stale-calibration alert for any model
    /// that is skipped for <see cref="StaleSkipAlertThreshold"/> consecutive cycles due
    /// to insufficient logs or invalid snapshot — surfacing broken prediction-logging
    /// pipelines or silently-corrupted snapshots that would otherwise stay invisible.
    /// </summary>
    public bool StaleAlertEnabled { get; set; } = true;

    /// <summary>
    /// Number of consecutive insufficient-logs / invalid-snapshot skips before the
    /// stale-calibration alert fires for a model. Default 5 ≈ 2.5h at the default 30m
    /// cycle.
    /// </summary>
    public int StaleSkipAlertThreshold { get; set; } = 5;
}
