using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for cross-detector ML drift agreement monitoring.</summary>
public class MLDriftAgreementOptions : ConfigurationOption<MLDriftAgreementOptions>
{
    /// <summary>Whether the worker should evaluate drift-detector agreement.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first cycle runs.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>How often detector agreement should be evaluated.</summary>
    public int PollIntervalSeconds { get; set; } = 21_600;

    /// <summary>Maximum random delay added after each poll interval.</summary>
    public int PollJitterSeconds { get; set; } = 300;

    /// <summary>Window for counting recent CUSUM alerts.</summary>
    public int CusumAlertWindowHours { get; set; } = 24;

    /// <summary>Window for counting recent drift-triggered training runs.</summary>
    public int ShiftRunWindowHours { get; set; } = 48;

    /// <summary>Minimum number of active detectors required for a consensus alert.</summary>
    public int ConsensusThreshold { get; set; } = 4;

    /// <summary>Maximum active model pairs evaluated in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 1_000;

    /// <summary>Minimum seconds between dispatch attempts for the same alert key.</summary>
    public int AlertCooldownSeconds { get; set; } = 21_600;

    /// <summary>Logical destination label included in alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";

    /// <summary>Timeout for acquiring the singleton cycle lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Relational database command timeout for cycle queries.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 60;
}
