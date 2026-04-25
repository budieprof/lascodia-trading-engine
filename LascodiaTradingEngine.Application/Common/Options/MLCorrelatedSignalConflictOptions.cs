using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for correlated ML signal-conflict monitoring.</summary>
public class MLCorrelatedSignalConflictOptions : ConfigurationOption<MLCorrelatedSignalConflictOptions>
{
    /// <summary>Delay after application startup before the first conflict scan runs.</summary>
    public int InitialDelaySeconds { get; set; } = 45;

    /// <summary>How often the worker scans approved ML signals for correlated conflicts.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized scans.</summary>
    public int PollJitterSeconds { get; set; } = 30;

    /// <summary>Signal lookback window, in minutes.</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Correlated-pair map JSON, e.g. {"EURUSD":["GBPUSD"]}.</summary>
    public string PairMapJson { get; set; } = "{}";

    /// <summary>Logical alert destination embedded in the alert payload for downstream routing.</summary>
    public string AlertDestination { get; set; } = "ml-ops";

    /// <summary>Whether not-yet-ordered approved signals in a detected conflict should be rejected.</summary>
    public bool RejectConflictingApprovedSignals { get; set; } = true;

    /// <summary>Maximum approved signals evaluated in one scan.</summary>
    public int MaxSignalsPerCycle { get; set; } = 1_000;

    /// <summary>Timeout for acquiring the singleton correlated-signal-conflict distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Cooldown applied to pair-specific conflict alerts.</summary>
    public int AlertCooldownSeconds { get; set; } = 1_800;
}
