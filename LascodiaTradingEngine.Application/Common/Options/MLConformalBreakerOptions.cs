using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the conformal coverage circuit breaker.</summary>
public class MLConformalBreakerOptions : ConfigurationOption<MLConformalBreakerOptions>
{
    /// <summary>Delay after application startup before the first breaker cycle runs.</summary>
    public int InitialDelayMinutes { get; set; } = 35;

    /// <summary>How often the breaker evaluates active models.</summary>
    public int PollIntervalHours { get; set; } = 24;

    /// <summary>Maximum number of recent resolved prediction logs evaluated per model.</summary>
    public int MaxLogs { get; set; } = 200;

    /// <summary>Minimum usable resolved conformal logs required before a model can trip.</summary>
    public int MinLogs { get; set; } = 30;

    /// <summary>Consecutive uncovered outcomes required to trip the breaker.</summary>
    public int ConsecutiveUncoveredTrigger { get; set; } = 8;

    /// <summary>
    /// Allowed coverage shortfall below the calibration target before sustained low coverage trips.
    /// For example, target 0.90 and tolerance 0.05 trips at empirical coverage below 0.85.
    /// </summary>
    public double CoverageTolerance { get; set; } = 0.05;

    /// <summary>Maximum suspension length in bars.</summary>
    public int MaxSuspensionBars { get; set; } = 96;

    /// <summary>Maximum active models evaluated per database batch.</summary>
    public int ModelBatchSize { get; set; } = 250;

    /// <summary>Maximum active models evaluated in one worker cycle.</summary>
    public int MaxCycleModels { get; set; } = 10_000;

    /// <summary>Maximum acceptable age of a conformal calibration.</summary>
    public int MaxCalibrationAgeDays { get; set; } = 30;

    /// <summary>Require the selected calibration to have been computed after model activation.</summary>
    public bool RequireCalibrationAfterModelActivation { get; set; } = true;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized workers.</summary>
    public int PollJitterSeconds { get; set; } = 300;

    /// <summary>Timeout for acquiring the singleton breaker-cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Allowed absolute difference between served and current conformal thresholds before recording drift telemetry.</summary>
    public double ThresholdMismatchEpsilon { get; set; } = 0.000001;

    /// <summary>
    /// Require the Wilson confidence interval's upper bound to remain below the coverage floor
    /// before sustained low coverage trips. This makes the trip conservative rather than firing
    /// on noisy low empirical coverage alone.
    /// </summary>
    public bool UseWilsonCoverageFloor { get; set; } = true;

    /// <summary>Confidence level for Wilson lower-bound coverage tests.</summary>
    public double WilsonConfidenceLevel { get; set; } = 0.95;

    /// <summary>One-sided binomial p-value threshold for low-coverage trips.</summary>
    public double StatisticalAlpha { get; set; } = 0.01;

    /// <summary>Number of resolved legacy prediction logs backfilled per cycle.</summary>
    public int BackfillBatchSize { get; set; } = 500;

    /// <summary>Polling interval for conformal coverage backfill.</summary>
    public int BackfillPollIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Hard cap on the number of trip alerts dispatched in a single cycle. Resolves are not
    /// counted (operators always want to know about resolved suspensions). Defaults high
    /// enough not to interfere with normal-volume trips; set lower to throttle a
    /// thundering-herd regime shift.
    /// </summary>
    public int MaxAlertsPerCycle { get; set; } = 50;

    /// <summary>
    /// Number of consecutive trip cycles per <c>(model, symbol, timeframe)</c> before the
    /// worker raises a chronic-tripper escalation alert. The standard trip alarm fires every
    /// time the breaker opens; the chronic alarm fires once when the streak crosses the
    /// threshold and auto-resolves on the first recovery cycle. Use this signal to drive a
    /// model-retirement workflow rather than letting the breaker re-trip indefinitely.
    /// </summary>
    public int ChronicTripThreshold { get; set; } = 4;
}
