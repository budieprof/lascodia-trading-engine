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
}
