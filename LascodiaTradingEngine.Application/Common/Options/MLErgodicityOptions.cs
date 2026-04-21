using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults and bounds for ergodicity economics monitoring.</summary>
public class MLErgodicityOptions : ConfigurationOption<MLErgodicityOptions>
{
    /// <summary>How often the worker computes model ergodicity metrics.</summary>
    public int PollIntervalHours { get; set; } = 24;

    /// <summary>Rolling prediction-outcome history window used for metrics.</summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>Minimum resolved predictions required before a model is evaluated.</summary>
    public int MinSamples { get; set; } = 20;

    /// <summary>Maximum recent resolved prediction logs used per model.</summary>
    public int MaxLogsPerModel { get; set; } = 200;

    /// <summary>Maximum active models loaded per prediction-log query batch.</summary>
    public int ModelBatchSize { get; set; } = 250;

    /// <summary>Maximum active models evaluated in one cycle.</summary>
    public int MaxCycleModels { get; set; } = 10_000;

    /// <summary>Timeout for acquiring the singleton ergodicity-cycle distributed lock.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>Absolute cap applied to persisted Kelly fractions.</summary>
    public double MaxKellyAbs { get; set; } = 2.0;

    /// <summary>Actual pips are divided by this value when converted to a fractional return proxy.</summary>
    public double ReturnPipScale { get; set; } = 100.0;

    /// <summary>Absolute cap applied to per-outcome return proxies before log-growth math.</summary>
    public double MaxReturnAbs { get; set; } = 0.50;
}
