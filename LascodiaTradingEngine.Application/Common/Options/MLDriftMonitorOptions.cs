using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration defaults for live ML drift monitoring.</summary>
public class MLDriftMonitorOptions : ConfigurationOption<MLDriftMonitorOptions>
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
    public int DriftWindowDays { get; set; } = 14;
    public int DriftMinPredictions { get; set; } = 30;
    public double DriftAccuracyThreshold { get; set; } = 0.50;
    public int TrainingDataWindowDays { get; set; } = 365;
    public double MaxBrierScore { get; set; } = 0.30;
    public double MaxEnsembleDisagreement { get; set; } = 0.35;
    public double RelativeDegradationRatio { get; set; } = 0.85;
    public int ConsecutiveFailuresBeforeRetrain { get; set; } = 3;
    public double SharpeDegradationRatio { get; set; } = 0.60;
    public int MinClosedTradesForSharpe { get; set; } = 20;
    public int MaxQueueDepth { get; set; } = int.MaxValue;
    public int MaxModelsPerCycle { get; set; } = 1_024;
    public int LockTimeoutSeconds { get; set; } = 5;
    public int DbCommandTimeoutSeconds { get; set; } = 60;
    public int MinTimeBetweenRetrainsHours { get; set; } = 0;
    public int AlertCooldownSeconds { get; set; } = 21_600;
    public string AlertDestination { get; set; } = "ml-ops";
    public int DriftFlagTtlHours { get; set; } = 24;
}
