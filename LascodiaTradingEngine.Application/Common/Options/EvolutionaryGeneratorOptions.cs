using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for <c>EvolutionaryGeneratorWorker</c>.</summary>
public class EvolutionaryGeneratorOptions : ConfigurationOption<EvolutionaryGeneratorOptions>
{
    /// <summary>When false, the worker loops but proposes no offspring.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker generates a new evolutionary cohort.</summary>
    public int PollIntervalSeconds { get; set; } = 24 * 60 * 60;

    /// <summary>
    /// Maximum random delay (in seconds) added to <see cref="PollIntervalSeconds"/> after each
    /// cycle. Prevents replicas from waking in lockstep and racing on the cycle-level
    /// distributed lock.
    /// </summary>
    public int PollJitterSeconds { get; set; } = 600;

    /// <summary>Maximum number of offspring proposed per cycle.</summary>
    public int MaxOffspringPerCycle { get; set; } = 12;

    /// <summary>Maximum seconds to wait for the cycle-level distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// When the cycle throws, the next wake interval grows by
    /// <c>2^min(consecutiveFailures, FailureBackoffCapShift)</c>. Default 5 → 32× the base
    /// rate at most. Set to 0 to disable backoff.
    /// </summary>
    public int FailureBackoffCapShift { get; set; } = 5;

    /// <summary>
    /// Number of consecutive cycles that propose candidates but produce zero inserts before
    /// the worker raises an aggregate <c>SystemicMLDegradation</c> alert. Indicates that
    /// parent eligibility regressed, idempotency keys collide, or persistence is failing
    /// fleet-wide. Auto-resolves on the first cycle that inserts at least one candidate.
    /// </summary>
    public int FleetSystemicConsecutiveZeroInsertCycles { get; set; } = 3;

    /// <summary>
    /// Hours since the worker last inserted a draft candidate before a staleness alert
    /// fires. Default 168h (one week) — evolutionary cohorts run daily by default, so a
    /// week of zero output means something has gone wrong at the proposal or persistence
    /// layer. Auto-resolves when fresh inserts arrive.
    /// </summary>
    public int StalenessAlertHours { get; set; } = 24 * 7;
}
