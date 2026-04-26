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

    /// <summary>
    /// <see cref="LascodiaTradingEngine.Domain.Entities.Alert.CooldownSeconds"/> applied to
    /// chronic-trip escalation alerts. Used by the alert dispatcher to suppress duplicate
    /// notifications while the chronic condition persists. One hour matches the typical
    /// breaker re-evaluation cadence; deployments with longer poll intervals can raise this
    /// without affecting other alarms.
    /// </summary>
    public int ChronicTripAlertCooldownSeconds { get; set; } = 3600;

    /// <summary>
    /// Half-life (in days) of the exponential time-decay weight applied to each prediction
    /// log when computing empirical coverage. <c>0</c> disables time-decay (all in-window
    /// observations weighted equally). Default 7 days lets recent failures dominate while
    /// keeping older context for trend detection.
    /// </summary>
    public int TimeDecayHalfLifeDays { get; set; } = 7;

    /// <summary>
    /// Number of bootstrap resamples used to estimate the standard error of empirical
    /// coverage. Bootstrap is enabled when this is &gt; 0 and disabled when 0. The stderr
    /// is then used by <see cref="RegressionGuardK"/> for K-sigma trend gating.
    /// </summary>
    public int BootstrapResamples { get; set; } = 200;

    /// <summary>
    /// Multiplier on the bootstrapped coverage stderr. The breaker only trips on the
    /// sustained-low-coverage path when <c>(empirical - target) &lt; -tolerance - K*stderr</c>.
    /// Higher K = more conservative (waits for stronger statistical evidence before
    /// tripping). Set to <c>0</c> to disable stderr gating and revert to plain
    /// empirical-vs-tolerance comparison.
    /// </summary>
    public double RegressionGuardK { get; set; } = 1.0;

    /// <summary>
    /// Number of distinct models that must be in trip-or-refresh state in a single cycle
    /// before the worker raises a fleet-wide <c>SystemicMLDegradation</c> alert. Below this
    /// threshold, individual trip alerts cover the situation; above it, the fleet alert
    /// signals an upstream issue (broken data feed, calibration pipeline regression, etc.).
    /// </summary>
    public int FleetSystemicMinTrippedModels { get; set; } = 5;

    /// <summary>
    /// Fraction of evaluated models that must be in trip-or-refresh state for the fleet-wide
    /// <c>SystemicMLDegradation</c> alert to fire. Combined with
    /// <see cref="FleetSystemicMinTrippedModels"/> via AND.
    /// </summary>
    public double FleetSystemicTripRatioThreshold { get; set; } = 0.25;

    /// <summary>
    /// Hours since the most recent resolved outcome before a model is considered
    /// "stale" (no fresh predictions reaching the worker). A staleness alert fires once
    /// past this threshold; the alert auto-resolves when fresh outcomes arrive. Distinct
    /// from chronic-trip (which is repeated *trips*, not absent data).
    /// </summary>
    public int StalenessHours { get; set; } = 48;

    /// <summary>
    /// When <c>true</c>, the worker reads per-context override keys from <c>EngineConfig</c>
    /// before evaluating each model and uses them in place of the global defaults. The
    /// override hierarchy is checked in this order (first hit wins):
    /// <list type="number">
    ///   <item><c>MLConformal:Override:Model:{id}:{Knob}</c></item>
    ///   <item><c>MLConformal:Override:Symbol:{symbol}:Timeframe:{timeframe}:{Knob}</c></item>
    ///   <item><c>MLConformal:Override:Symbol:{symbol}:{Knob}</c></item>
    ///   <item><c>MLConformal:Override:Timeframe:{timeframe}:{Knob}</c></item>
    /// </list>
    /// Knobs supported: <c>MaxLogs</c>, <c>MinLogs</c>, <c>CoverageTolerance</c>,
    /// <c>ConsecutiveUncoveredTrigger</c>, <c>RegressionGuardK</c>.
    /// </summary>
    public bool OverridesEnabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the audit log entries (per-model breaker state changes) include
    /// a verbose diagnostics JSON blob with per-cycle metadata: time-decay weights,
    /// bootstrap stderr, override-resolved knobs, and overall sample distribution. Useful
    /// in development; can be disabled in production to reduce row size.
    /// </summary>
    public bool VerboseAuditDiagnostics { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, the worker resolves the active <c>MarketRegime</c> for each
    /// prediction-log outcome (by joining with the most recent <c>MarketRegimeSnapshot</c>
    /// before the outcome timestamp) and the evaluator computes per-regime coverage
    /// breakdowns. Diagnostic-only — does not change trip semantics. The worst-regime
    /// information is surfaced in trip alert payloads so operators can immediately see
    /// which regime is driving a coverage failure. Adds one indexed query per cycle
    /// covering all evaluated (symbol, timeframe) tuples.
    /// </summary>
    public bool EnablePerRegimeDecomposition { get; set; } = false;

    /// <summary>
    /// Maximum degree of parallelism used inside each model batch's in-memory evaluation
    /// phase. The evaluation phase is purely CPU-bound (Wilson + p-value + bootstrap
    /// math), so fan-out scales with cores. Set to <c>1</c> for fully sequential
    /// evaluation. Default <c>0</c> resolves to <c>Environment.ProcessorCount</c> at
    /// startup. Clamped to <c>[1, 32]</c>.
    /// </summary>
    public int EvaluationParallelism { get; set; } = 0;
}
