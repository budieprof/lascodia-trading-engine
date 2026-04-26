using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for multi-horizon ML accuracy monitoring.</summary>
public sealed class MLHorizonAccuracyOptions : ConfigurationOption<MLHorizonAccuracyOptions>
{
    /// <summary>Whether the horizon-accuracy worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active ML models are evaluated.</summary>
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>Calendar-day lookback window for resolved prediction outcomes.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>Minimum resolved primary and horizon outcomes required before a row is reliable.</summary>
    public int MinPredictions { get; set; } = 20;

    /// <summary>Maximum tolerated gap between primary direction accuracy and 3-bar accuracy.</summary>
    public double HorizonGapThreshold { get; set; } = 0.10;

    /// <summary>Wilson-score confidence z value used for lower-bound accuracy.</summary>
    public double WilsonZ { get; set; } = 1.96;

    /// <summary>Destination hint embedded in the generated alert payload.</summary>
    public string AlertDestination { get; set; } = "ml-ops";

    /// <summary>Maximum active models processed in one cycle. Oldest or missing rows are prioritized.</summary>
    public int MaxModelsPerCycle { get; set; } = 512;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
