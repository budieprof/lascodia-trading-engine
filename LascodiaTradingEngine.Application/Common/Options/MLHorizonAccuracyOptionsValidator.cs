using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLHorizonAccuracyOptions"/> at startup.</summary>
public sealed class MLHorizonAccuracyOptionsValidator : IValidateOptions<MLHorizonAccuracyOptions>
{
    public ValidateOptionsResult Validate(string? name, MLHorizonAccuracyOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLHorizonAccuracyOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("MLHorizonAccuracyOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (options.WindowDays is < 1 or > 3_650)
            errors.Add("MLHorizonAccuracyOptions.WindowDays must be between 1 and 3650.");
        if (options.MinPredictions is < 1 or > 1_000_000)
            errors.Add("MLHorizonAccuracyOptions.MinPredictions must be between 1 and 1000000.");
        if (!double.IsFinite(options.HorizonGapThreshold) || options.HorizonGapThreshold is < 0.0 or > 1.0)
            errors.Add("MLHorizonAccuracyOptions.HorizonGapThreshold must be a finite value between 0.0 and 1.0.");
        if (!double.IsFinite(options.WilsonZ) || options.WilsonZ is < 0.0 or > 5.0)
            errors.Add("MLHorizonAccuracyOptions.WilsonZ must be a finite value between 0.0 and 5.0.");
        if (options.AlertDestination?.Length > 100)
            errors.Add("MLHorizonAccuracyOptions.AlertDestination cannot exceed 100 characters.");
        if (options.MaxModelsPerCycle is < 1 or > 100_000)
            errors.Add("MLHorizonAccuracyOptions.MaxModelsPerCycle must be between 1 and 100000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLHorizonAccuracyOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLHorizonAccuracyOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
