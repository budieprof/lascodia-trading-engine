using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the <c>MLAdwinDriftWorker</c>.</summary>
public class MLAdwinDriftOptions : ConfigurationOption<MLAdwinDriftOptions>
{
    /// <summary>When false, the worker loops but skips evaluation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker evaluates active models for drift.</summary>
    public int PollIntervalSeconds { get; set; } = 24 * 60 * 60;

    /// <summary>
    /// Maximum random delay (in seconds) added to <see cref="PollIntervalSeconds"/> after each
    /// cycle. Prevents replicas from waking in lockstep and racing on the cycle-level lock.
    /// Default 600s (10 minutes).
    /// </summary>
    public int PollJitterSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum number of recent resolved outcomes per model the ADWIN scanner considers.
    /// Larger windows are more statistically powerful but slower; smaller windows react
    /// faster to drift but accept more noise.
    /// </summary>
    public int WindowSize { get; set; } = 100;

    /// <summary>
    /// Minimum resolved predictions a model must have within the lookback window before
    /// drift evaluation runs. Models below this floor are skipped as dormant.
    /// </summary>
    public int MinResolvedPredictions { get; set; } = 30;

    /// <summary>
    /// ADWIN confidence parameter. Smaller values = more conservative (fewer false drift
    /// detections). Default <c>0.002</c> matches the canonical Bifet/Gavalda ADWIN paper.
    /// </summary>
    public double Delta { get; set; } = 0.002;

    /// <summary>How many days back to consider when loading recent prediction outcomes.</summary>
    public int LookbackDays { get; set; } = 180;

    /// <summary>How long a drift flag remains active after detection before being eligible for clear.</summary>
    public int FlagTtlHours { get; set; } = 48;

    /// <summary>Maximum number of models evaluated in a single cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 256;

    /// <summary>Maximum seconds to wait for the cycle-level distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Cooldown between auto-degrading retrains for the same pair. Prevents re-queueing
    /// while the previous fix is still propagating through the SPRT shadow tournament.
    /// </summary>
    public int MinTimeBetweenRetrainsHours { get; set; } = 12;

    /// <summary>
    /// When <c>true</c>, the per-cycle audit row stores the gzip'd outcome series as a blob.
    /// Useful for offline drift forensics; slightly larger DB rows.
    /// </summary>
    public bool SnapshotOutcomeSeries { get; set; } = true;

    /// <summary>Per-cycle DB command timeout (applied on relational providers only).</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Number of audit/flag mutations committed per <c>SaveChangesAsync</c>.</summary>
    public int SaveBatchSize { get; set; } = 32;

    /// <summary>
    /// When the cycle throws, the next wake interval grows by
    /// <c>2^min(consecutiveFailures, FailureBackoffCapShift)</c>. Default 5 = 32× the base
    /// rate at most. Set to 0 to disable backoff.
    /// </summary>
    public int FailureBackoffCapShift { get; set; } = 5;

    /// <summary>
    /// Number of distinct models that must drift in a single cycle before the worker raises
    /// an aggregate <c>SystemicMLDegradation</c> alert. Defaults to 5 — below that, the
    /// per-model drift alerts are sufficient signal; above, the fleet-wide alert points to
    /// an upstream cause (data feed, regime shift, calibration regression).
    /// </summary>
    public int FleetSystemicDriftThreshold { get; set; } = 5;

    /// <summary>
    /// Hours since the worker last successfully wrote any <c>MLAdwinDriftLog</c> row before
    /// a staleness alert fires. Default 36h = three poll cycles at the default 12-hour
    /// cadence. Auto-resolves when fresh audit rows arrive.
    /// </summary>
    public int StalenessAlertHours { get; set; } = 36;

    /// <summary>
    /// When <c>true</c>, the worker reads per-context override keys from <c>EngineConfig</c>
    /// each cycle and uses them in place of the global defaults. The override hierarchy is
    /// checked in this order (first hit wins):
    /// <list type="number">
    ///   <item><c>MLAdwinDrift:Override:Model:{id}:{Knob}</c></item>
    ///   <item><c>MLAdwinDrift:Override:Symbol:{symbol}:Timeframe:{timeframe}:{Knob}</c></item>
    ///   <item><c>MLAdwinDrift:Override:Symbol:{symbol}:{Knob}</c></item>
    ///   <item><c>MLAdwinDrift:Override:Timeframe:{timeframe}:{Knob}</c></item>
    /// </list>
    /// Knobs supported: <c>Delta</c>, <c>WindowSize</c>, <c>MinResolvedPredictions</c>.
    /// Override-key validation logs typo'd suffixes once per cycle.
    /// </summary>
    public bool OverridesEnabled { get; set; } = true;
}
