using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults and bounds for correlated ML failure monitoring.</summary>
public class MLCorrelatedFailureOptions : ConfigurationOption<MLCorrelatedFailureOptions>
{
    /// <summary>How often the worker evaluates correlated model failure.</summary>
    public int PollIntervalSeconds { get; set; } = 600;

    /// <summary>Fraction of evaluated models that must fail before activating systemic pause.</summary>
    public double AlarmRatio { get; set; } = 0.40;

    /// <summary>Fraction below which systemic pause can be lifted.</summary>
    public double RecoveryRatio { get; set; } = 0.20;

    /// <summary>Minimum evaluated model count before systemic alarm logic can run.</summary>
    public int MinModelsForAlarm { get; set; } = 3;

    /// <summary>Minimum minutes between pause state changes.</summary>
    public int StateChangeCooldownMinutes { get; set; } = 60;

    /// <summary>Maximum model ids included in one prediction-statistics query.</summary>
    public int ModelStatsBatchSize { get; set; } = 1_000;

    /// <summary>Model health metric used for correlated failure classification.</summary>
    public string FailureMetric { get; set; } = "DirectionAccuracy";
}
