using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the <c>CalibrationSnapshotWorker</c>.</summary>
public class CalibrationSnapshotOptions : ConfigurationOption<CalibrationSnapshotOptions>
{
    /// <summary>Delay after application startup before the first cycle runs.</summary>
    public int InitialDelayMinutes { get; set; } = 2;

    /// <summary>How often the worker rolls up the previous month(s) into snapshots.</summary>
    public int PollIntervalHours { get; set; } = 24;

    /// <summary>
    /// Number of months back from the current month to consider when filling missing
    /// snapshots. Each month is processed only if it does not already have rows.
    /// </summary>
    public int BackfillMonths { get; set; } = 6;

    /// <summary>
    /// Maximum random delay (in seconds) added to <see cref="PollIntervalHours"/> after each
    /// cycle. Prevents two replicas that started at the same moment from polling in lockstep
    /// and racing on the cycle / unique-index backstop. Default 600s (10 minutes).
    /// </summary>
    public int PollJitterSeconds { get; set; } = 600;

    /// <summary>
    /// When the cycle throws, the next poll interval grows by
    /// <c>2^min(consecutiveFailures, FailureBackoffCapShift)</c>. Default 5 → 32× the base
    /// interval at most. Set to 0 to disable backoff.
    /// </summary>
    public int FailureBackoffCapShift { get; set; } = 5;

    /// <summary>
    /// When <c>true</c>, the worker acquires a singleton distributed lock at the start of
    /// each cycle so only one replica drives backfill + per-month writes. Default <c>true</c>
    /// — without it, multiple replicas redundantly load + race on the unique-index backstop.
    /// </summary>
    public bool UseCycleLock { get; set; } = true;

    /// <summary>
    /// Maximum seconds to wait for the cycle-level distributed lock when
    /// <see cref="UseCycleLock"/> is true. Default 0 — try once and skip the cycle if
    /// another replica holds the lock; the next jittered poll re-attempts.
    /// </summary>
    public int CycleLockTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Number of consecutive cycles in which every attempted month fails before the worker
    /// raises an aggregate <c>SystemicMLDegradation</c> alert. Distinct from per-month
    /// failures; this fires when the snapshot pipeline as a whole is broken.
    /// </summary>
    public int FleetSystemicConsecutiveFailureCycles { get; set; } = 3;

    /// <summary>
    /// Hours since the most recent <c>CalibrationSnapshot</c> row before a staleness alert
    /// fires. Defaults to 1 month + 1 week of slack to accommodate poll-interval drift.
    /// </summary>
    public int StalenessAlertHours { get; set; } = 24 * 38;
}
