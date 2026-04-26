using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using LascodiaTradingEngine.Application.Services.Alerts;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for per-feature PSI monitoring.</summary>
public sealed class MLFeaturePsiOptions : ConfigurationOption<MLFeaturePsiOptions>
{
    /// <summary>Whether the feature PSI worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first PSI scan runs.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often active models are scanned for PSI drift.</summary>
    public int PollIntervalSeconds { get; set; } = 7_200;

    /// <summary>Number of days of closed candles used to build the live feature distribution.</summary>
    public int CandleWindowDays { get; set; } = 14;

    /// <summary>Minimum comparable feature samples required before evaluating a model.</summary>
    public int MinFeatureSamples { get; set; } = 50;

    /// <summary>Per-feature PSI threshold that raises a model-degradation alert.</summary>
    public double PsiAlertThreshold { get; set; } = 0.25;

    /// <summary>Per-feature PSI threshold that contributes to automatic retrain decisions.</summary>
    public double PsiRetrainThreshold { get; set; } = 0.40;

    /// <summary>Fraction of checked features that must exceed the retrain threshold before queuing retrain.</summary>
    public double RetrainMajorityFraction { get; set; } = 0.50;

    /// <summary>Maximum active models evaluated in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 256;

    /// <summary>Maximum number of high-PSI features included in alert and retrain metadata.</summary>
    public int MaxFeaturesInAlert { get; set; } = 5;

    /// <summary>Training window, in days, requested when PSI drift queues a retrain.</summary>
    public int TrainingWindowDays { get; set; } = 365;

    /// <summary>Minimum seconds between PSI-triggered retrains for the same symbol/timeframe.</summary>
    public int RetrainCooldownSeconds { get; set; } = 86_400;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; }

    /// <summary>Relational database command timeout used by the cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum seconds between notifications for the same PSI alert.</summary>
    public int AlertCooldownSeconds { get; set; } = AlertCooldownDefaults.Default_MLMonitoring;

    /// <summary>Logical destination label included in PSI alert payloads.</summary>
    public string AlertDestination { get; set; } = "ml-ops";
}
