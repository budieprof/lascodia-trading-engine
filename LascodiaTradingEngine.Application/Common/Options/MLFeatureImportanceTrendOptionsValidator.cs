using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureImportanceTrendOptions"/> at startup.</summary>
public sealed class MLFeatureImportanceTrendOptionsValidator : IValidateOptions<MLFeatureImportanceTrendOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureImportanceTrendOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureImportanceTrendOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLFeatureImportanceTrendOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.GenerationsToCheck is < 2 or > 64)
            errors.Add("MLFeatureImportanceTrendOptions.GenerationsToCheck must be between 2 and 64.");
        if (options.MinGenerations is < 2 or > 64)
            errors.Add("MLFeatureImportanceTrendOptions.MinGenerations must be between 2 and 64.");
        if (options.MinGenerations > options.GenerationsToCheck)
            errors.Add("MLFeatureImportanceTrendOptions.MinGenerations cannot exceed GenerationsToCheck.");
        if (!double.IsFinite(options.ImportanceDecayThreshold) || options.ImportanceDecayThreshold is < 0.0 or > 1.0)
            errors.Add("MLFeatureImportanceTrendOptions.ImportanceDecayThreshold must be a finite value between 0.0 and 1.0.");
        if (!double.IsFinite(options.MonotonicTolerance) || options.MonotonicTolerance is < 0.0 or > 1.0)
            errors.Add("MLFeatureImportanceTrendOptions.MonotonicTolerance must be a finite value between 0.0 and 1.0.");
        if (!double.IsFinite(options.MinRelativeDrop) || options.MinRelativeDrop is < 0.0 or > 1.0)
            errors.Add("MLFeatureImportanceTrendOptions.MinRelativeDrop must be a finite value between 0.0 and 1.0.");
        if (options.MaxPairsPerCycle is < 1 or > 100_000)
            errors.Add("MLFeatureImportanceTrendOptions.MaxPairsPerCycle must be between 1 and 100000.");
        if (options.MaxFeaturesInAlert is < 1 or > 100)
            errors.Add("MLFeatureImportanceTrendOptions.MaxFeaturesInAlert must be between 1 and 100.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureImportanceTrendOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureImportanceTrendOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.AlertCooldownSeconds is < 1 or > 604_800)
            errors.Add("MLFeatureImportanceTrendOptions.AlertCooldownSeconds must be between 1 and 604800.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 128)
            errors.Add("MLFeatureImportanceTrendOptions.AlertDestination is required and must be at most 128 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
