using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults and bounds for EWMA live-accuracy monitoring.</summary>
public class MLEwmaAccuracyOptions : ConfigurationOption<MLEwmaAccuracyOptions>
{
    /// <summary>Whether EWMA accuracy monitoring is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional startup delay after the shared worker startup sequencer.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often the worker checks for newly resolved model outcomes.</summary>
    public int PollIntervalSeconds { get; set; } = 600;

    /// <summary>EWMA smoothing factor in the range (0, 1].</summary>
    public double Alpha { get; set; } = 0.05;

    /// <summary>Minimum resolved outcomes before alerting is active.</summary>
    public int MinPredictions { get; set; } = 20;

    /// <summary>Warning floor for EWMA accuracy.</summary>
    public double WarnThreshold { get; set; } = 0.50;

    /// <summary>Critical floor for EWMA accuracy.</summary>
    public double CriticalThreshold { get; set; } = 0.48;

    /// <summary>Operator destination identifier persisted into alert diagnostics.</summary>
    public string AlertDestination { get; set; } = "ml-ops";

    /// <summary>Maximum active models scanned in a single cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 10_000;

    /// <summary>Maximum newly resolved prediction logs loaded per model query batch.</summary>
    public int PredictionLogBatchSize { get; set; } = 1_000;

    /// <summary>Timeout for acquiring the singleton EWMA-cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Relational database command timeout applied to each EWMA cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
