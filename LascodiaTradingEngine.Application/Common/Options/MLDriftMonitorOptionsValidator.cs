using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDriftMonitorOptions"/> at startup.</summary>
public sealed class MLDriftMonitorOptionsValidator : IValidateOptions<MLDriftMonitorOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDriftMonitorOptions options)
    {
        var errors = new List<string>();

        if (options.PollIntervalSeconds is < 60 or > 86_400)
            errors.Add("MLDriftMonitorOptions.PollIntervalSeconds must be between 60 and 86400.");
        if (options.DriftWindowDays is < 1 or > 365)
            errors.Add("MLDriftMonitorOptions.DriftWindowDays must be between 1 and 365.");
        if (options.DriftMinPredictions is < 5 or > 5_000)
            errors.Add("MLDriftMonitorOptions.DriftMinPredictions must be between 5 and 5000.");
        if (options.DriftAccuracyThreshold is < 0.0 or > 1.0 || !double.IsFinite(options.DriftAccuracyThreshold))
            errors.Add("MLDriftMonitorOptions.DriftAccuracyThreshold must be between 0.0 and 1.0.");
        if (options.TrainingDataWindowDays is < 30 or > 3_650)
            errors.Add("MLDriftMonitorOptions.TrainingDataWindowDays must be between 30 and 3650.");
        if (options.MaxBrierScore is < 0.0 or > 1.0 || !double.IsFinite(options.MaxBrierScore))
            errors.Add("MLDriftMonitorOptions.MaxBrierScore must be between 0.0 and 1.0.");
        if (options.MaxEnsembleDisagreement is < 0.0 or > 5.0 || !double.IsFinite(options.MaxEnsembleDisagreement))
            errors.Add("MLDriftMonitorOptions.MaxEnsembleDisagreement must be between 0.0 and 5.0.");
        if (options.RelativeDegradationRatio is < 0.01 or > 1.0 || !double.IsFinite(options.RelativeDegradationRatio))
            errors.Add("MLDriftMonitorOptions.RelativeDegradationRatio must be between 0.01 and 1.0.");
        if (options.ConsecutiveFailuresBeforeRetrain is < 1 or > 100)
            errors.Add("MLDriftMonitorOptions.ConsecutiveFailuresBeforeRetrain must be between 1 and 100.");
        if (options.SharpeDegradationRatio is < 0.0 or > 1.0 || !double.IsFinite(options.SharpeDegradationRatio))
            errors.Add("MLDriftMonitorOptions.SharpeDegradationRatio must be between 0.0 and 1.0.");
        if (options.MinClosedTradesForSharpe is < 5 or > 5_000)
            errors.Add("MLDriftMonitorOptions.MinClosedTradesForSharpe must be between 5 and 5000.");
        if (options.MaxQueueDepth is < 1)
            errors.Add("MLDriftMonitorOptions.MaxQueueDepth must be positive.");
        if (options.MaxModelsPerCycle is < 1 or > 10_000)
            errors.Add("MLDriftMonitorOptions.MaxModelsPerCycle must be between 1 and 10000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDriftMonitorOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 5 or > 600)
            errors.Add("MLDriftMonitorOptions.DbCommandTimeoutSeconds must be between 5 and 600.");
        if (options.MinTimeBetweenRetrainsHours is < 0 or > 720)
            errors.Add("MLDriftMonitorOptions.MinTimeBetweenRetrainsHours must be between 0 and 720.");
        if (options.AlertCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLDriftMonitorOptions.AlertCooldownSeconds must be between 0 and 2592000.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDriftMonitorOptions.AlertDestination is required and must be at most 100 characters.");
        if (options.DriftFlagTtlHours is < 1 or > 720)
            errors.Add("MLDriftMonitorOptions.DriftFlagTtlHours must be between 1 and 720.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
