using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for per-symbol ML degradation mode monitoring.</summary>
public class MLDegradationModeOptions : ConfigurationOption<MLDegradationModeOptions>
{
    /// <summary>Whether the worker should evaluate ML degradation flags.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first cycle runs.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>How often model availability should be evaluated.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum random delay added to each poll interval to avoid synchronized scans.</summary>
    public int PollJitterSeconds { get; set; } = 30;

    /// <summary>Maximum symbols evaluated in a single cycle.</summary>
    public int MaxSymbolsPerCycle { get; set; } = 1_000;

    /// <summary>Minutes a symbol may remain degraded before the critical alert is raised.</summary>
    public int CriticalAfterMinutes { get; set; } = 60;

    /// <summary>Hours a symbol may remain degraded before the escalation alert is raised.</summary>
    public int EscalateAfterHours { get; set; } = 24;

    /// <summary>Minimum seconds between dispatch attempts for the same alert key.</summary>
    public int AlertCooldownSeconds { get; set; } = 900;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Logical destination label included in activation and critical alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";

    /// <summary>Logical destination label included in escalation alert payloads.</summary>
    public string EscalationDestination { get; set; } = "ml-ops-escalation";
}
