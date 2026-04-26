using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for live Kelly-cap computation from resolved ML outcomes.</summary>
public sealed class MLKellyFractionOptions : ConfigurationOption<MLKellyFractionOptions>
{
    /// <summary>Whether the worker is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Additional startup delay after the shared worker startup sequencer.</summary>
    public int InitialDelaySeconds { get; set; }

    /// <summary>How often the worker recomputes live Kelly caps.</summary>
    public int PollIntervalSeconds { get; set; } = 86_400;

    /// <summary>Look-back window for resolved champion predictions.</summary>
    public int WindowDays { get; set; } = 60;

    /// <summary>Minimum comparable economic outcomes required before a reliable Kelly row is emitted.</summary>
    public int MinUsableSamples { get; set; } = 30;

    /// <summary>Minimum winning outcomes required in the selected comparable population.</summary>
    public int MinWins { get; set; } = 5;

    /// <summary>Minimum losing outcomes required in the selected comparable population.</summary>
    public int MinLosses { get; set; } = 5;

    /// <summary>Absolute cap applied to raw, conservative, and half-Kelly values.</summary>
    public double MaxAbsKelly { get; set; } = 0.25;

    /// <summary>Bayesian prior trade count used for win-rate shrinkage.</summary>
    public double PriorTrades { get; set; } = 20.0;

    /// <summary>Z-score subtracted from the posterior win-rate estimate.</summary>
    public double WinRateLowerBoundZ { get; set; } = 1.0;

    /// <summary>Percentile cap applied to outcome magnitudes before payoff-ratio estimation.</summary>
    public double OutlierPercentile { get; set; } = 0.95;

    /// <summary>Absolute maximum outcome magnitude allowed after percentile capping.</summary>
    public double MaxOutcomeMagnitude { get; set; } = 10.0;

    /// <summary>Maximum models processed in one cycle.</summary>
    public int MaxModelsPerCycle { get; set; } = 10_000;

    /// <summary>Maximum recent prediction logs loaded for a single model.</summary>
    public int MaxPredictionLogsPerModel { get; set; } = 50_000;

    /// <summary>When true, insufficient-sample cycles deploy a neutral zero Kelly cap.</summary>
    public bool WriteNeutralCapOnInsufficientSamples { get; set; }

    /// <summary>Timeout for acquiring the singleton cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Relational database command timeout applied to each cycle.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;
}
