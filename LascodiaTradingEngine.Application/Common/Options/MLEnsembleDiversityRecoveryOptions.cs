using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for ensemble-diversity recovery monitoring.</summary>
public class MLEnsembleDiversityRecoveryOptions : ConfigurationOption<MLEnsembleDiversityRecoveryOptions>
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 21_600;
    public double MaxEnsembleDiversity { get; set; } = 0.75;
    public double MinDisagreementDiversity { get; set; } = 0.05;
    public bool TreatZeroAsMissing { get; set; } = true;
    public double ForcedNclLambda { get; set; } = 0.30;
    public double ForcedDiversityLambda { get; set; } = 0.15;
    public int TrainingDataWindowDays { get; set; } = 365;
    public int MaxModelsPerCycle { get; set; } = 512;
    public int LockTimeoutSeconds { get; set; } = 5;
    public int DbCommandTimeoutSeconds { get; set; } = 60;
    public int MinTimeBetweenRetrainsHours { get; set; } = 12;
    public int MaxQueueDepth { get; set; } = int.MaxValue;
    public int RetrainPriority { get; set; } = 2;
}
