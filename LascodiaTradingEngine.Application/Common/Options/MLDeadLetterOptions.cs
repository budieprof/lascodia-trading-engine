using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ML training dead-letter recovery.</summary>
public class MLDeadLetterOptions : ConfigurationOption<MLDeadLetterOptions>
{
    /// <summary>Whether the dead-letter recovery worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; } = 90;

    /// <summary>How often failed training runs are scanned for dead-letter recovery.</summary>
    public int PollIntervalSeconds { get; set; } = 604_800;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized scans.</summary>
    public int PollJitterSeconds { get; set; } = 600;

    /// <summary>Minimum age of a failed run before it is eligible for dead-letter recovery.</summary>
    public int RetryAfterDays { get; set; } = 7;

    /// <summary>Maximum dead-letter recovery attempts per symbol/timeframe before operator alerting.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Maximum failed runs loaded in a single recovery cycle.</summary>
    public int MaxRunsPerCycle { get; set; } = 1_000;

    /// <summary>Maximum failed runs requeued in a single recovery cycle.</summary>
    public int MaxRequeuesPerCycle { get; set; } = 100;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Minimum seconds between retry-cap alert notifications for the same pair.</summary>
    public int AlertCooldownSeconds { get; set; } = 86_400;

    /// <summary>Logical destination label included in dead-letter alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
