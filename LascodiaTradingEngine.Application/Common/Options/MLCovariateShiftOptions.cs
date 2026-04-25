using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ML covariate-shift monitoring.</summary>
public class MLCovariateShiftOptions : ConfigurationOption<MLCovariateShiftOptions>
{
    /// <summary>Whether the covariate-shift worker should run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after application startup before the first scan runs.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>How often active models are scanned for feature-distribution shift.</summary>
    public int PollIntervalSeconds { get; set; } = 3_600;

    /// <summary>Maximum random delay added after each poll interval to avoid synchronized scans.</summary>
    public int PollJitterSeconds { get; set; } = 120;

    /// <summary>Recent closed-candle window used to compare live feature distributions.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>Importance-weighted PSI threshold that triggers retraining.</summary>
    public double PsiThreshold { get; set; } = 0.20;

    /// <summary>Individual feature PSI threshold persisted into drift diagnostics.</summary>
    public double PerFeaturePsiThreshold { get; set; } = 0.25;

    /// <summary>Mean squared z-score threshold for joint multivariate shift.</summary>
    public double MultivariateThreshold { get; set; } = 1.50;

    /// <summary>Minimum feature samples required for a reliable PSI estimate.</summary>
    public int MinCandles { get; set; } = 100;

    /// <summary>Training-data lookback window for auto-degrading retraining runs.</summary>
    public int TrainingDays { get; set; } = 365;

    /// <summary>Maximum active models evaluated in a single cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 1_000;

    /// <summary>Maximum queued/running ML training backlog before this worker stops adding runs.</summary>
    public int MaxQueuedRetrains { get; set; } = 100;

    /// <summary>Cooldown before another covariate-shift retrain can be queued for the same symbol/timeframe.</summary>
    public int RetrainCooldownSeconds { get; set; } = 86_400;

    /// <summary>Timeout for acquiring the singleton distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;
}
