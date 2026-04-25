using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ML direction-streak monitoring.</summary>
public class MLDirectionStreakOptions : ConfigurationOption<MLDirectionStreakOptions>
{
    /// <summary>Whether the worker should evaluate recent model prediction directions.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first cycle runs.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>How often active models should be evaluated.</summary>
    public int PollIntervalSeconds { get; set; } = 3_600;

    /// <summary>Maximum random delay added after each poll interval.</summary>
    public int PollJitterSeconds { get; set; } = 120;

    /// <summary>Number of most-recent predictions evaluated per model.</summary>
    public int WindowSize { get; set; } = 30;

    /// <summary>Maximum tolerated fraction of predictions in one direction.</summary>
    public double MaxSameDirectionFraction { get; set; } = 0.85;

    /// <summary>Minimum binary entropy tolerated before a direction stream is considered collapsed.</summary>
    public double EntropyThreshold { get; set; } = 0.50;

    /// <summary>Runs-test z-score threshold. Values at or below this indicate too few runs.</summary>
    public double RunsZScoreThreshold { get; set; } = -2.0;

    /// <summary>Maximum tolerated longest consecutive run as a fraction of the window.</summary>
    public double LongestRunFraction { get; set; } = 0.60;

    /// <summary>Minimum failed statistical checks required to alert.</summary>
    public int MinFailedTestsToAlert { get; set; } = 2;

    /// <summary>Minimum failed statistical checks required to queue an auto-retrain.</summary>
    public int MinFailedTestsToRetrain { get; set; } = 3;

    /// <summary>Whether severe direction streaks should queue an ML retraining run.</summary>
    public bool AutoQueueRetrain { get; set; } = true;

    /// <summary>Historical lookback window for auto-retraining runs.</summary>
    public int RetrainLookbackDays { get; set; } = 365;

    /// <summary>Maximum active models whose prediction windows are scanned in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 1_000;

    /// <summary>Maximum severe detections allowed to queue retraining in one cycle.</summary>
    public int MaxRetrainsPerCycle { get; set; } = 25;

    /// <summary>Minimum seconds between dispatch attempts for the same direction-streak alert.</summary>
    public int AlertCooldownSeconds { get; set; } = 3_600;

    /// <summary>Timeout for acquiring the singleton cycle lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Logical destination label included in alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
