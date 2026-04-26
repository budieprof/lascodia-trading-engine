using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLAdwinDriftOptions"/> at startup.</summary>
public class MLAdwinDriftOptionsValidator : IValidateOptions<MLAdwinDriftOptions>
{
    public ValidateOptionsResult Validate(string? name, MLAdwinDriftOptions o)
    {
        var errors = new List<string>();

        if (o.PollIntervalSeconds is < 60 or > 7 * 24 * 60 * 60)
            errors.Add("MLAdwinDriftOptions.PollIntervalSeconds must be in [60, 604800].");
        if (o.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLAdwinDriftOptions.PollJitterSeconds must be in [0, 86400].");
        if (o.WindowSize is < 5 or > 5_000)
            errors.Add("MLAdwinDriftOptions.WindowSize must be in [5, 5000].");
        if (o.MinResolvedPredictions is < 5 or > 5_000)
            errors.Add("MLAdwinDriftOptions.MinResolvedPredictions must be in [5, 5000].");
        if (!double.IsFinite(o.Delta) || o.Delta is <= 0.0 or > 0.25)
            errors.Add("MLAdwinDriftOptions.Delta must be a finite value in (0, 0.25].");
        if (o.LookbackDays is < 1 or > 3650)
            errors.Add("MLAdwinDriftOptions.LookbackDays must be in [1, 3650].");
        if (o.FlagTtlHours is < 1 or > 24 * 30)
            errors.Add("MLAdwinDriftOptions.FlagTtlHours must be in [1, 720].");
        if (o.MaxModelsPerCycle is < 1 or > 4096)
            errors.Add("MLAdwinDriftOptions.MaxModelsPerCycle must be in [1, 4096].");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLAdwinDriftOptions.LockTimeoutSeconds must be in [0, 300].");
        if (o.MinTimeBetweenRetrainsHours is < 0 or > 24 * 30)
            errors.Add("MLAdwinDriftOptions.MinTimeBetweenRetrainsHours must be in [0, 720].");
        if (o.DbCommandTimeoutSeconds is < 5 or > 600)
            errors.Add("MLAdwinDriftOptions.DbCommandTimeoutSeconds must be in [5, 600].");
        if (o.SaveBatchSize is < 1 or > 256)
            errors.Add("MLAdwinDriftOptions.SaveBatchSize must be in [1, 256].");
        if (o.FailureBackoffCapShift is < 0 or > 16)
            errors.Add("MLAdwinDriftOptions.FailureBackoffCapShift must be in [0, 16].");
        if (o.FleetSystemicDriftThreshold < 1)
            errors.Add("MLAdwinDriftOptions.FleetSystemicDriftThreshold must be >= 1.");
        if (o.StalenessAlertHours < 1)
            errors.Add("MLAdwinDriftOptions.StalenessAlertHours must be >= 1.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
