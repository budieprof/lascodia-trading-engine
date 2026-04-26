using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureStalenessOptions"/> at startup.</summary>
public sealed class MLFeatureStalenessOptionsValidator : IValidateOptions<MLFeatureStalenessOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureStalenessOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureStalenessOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLFeatureStalenessOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.MinSamples is < 20 or > 100_000)
            errors.Add("MLFeatureStalenessOptions.MinSamples must be between 20 and 100000.");
        if (options.MaxRowsPerModel is < 50 or > 100_000)
            errors.Add("MLFeatureStalenessOptions.MaxRowsPerModel must be between 50 and 100000.");
        if (options.MaxRowsPerModel < options.MinSamples)
            errors.Add("MLFeatureStalenessOptions.MaxRowsPerModel cannot be less than MinSamples.");
        if (options.MaxCandlesPerModel is < MLFeatureHelper.LookbackWindow + 2 or > 100_000)
            errors.Add($"MLFeatureStalenessOptions.MaxCandlesPerModel must be between {MLFeatureHelper.LookbackWindow + 2} and 100000.");
        if (options.MaxCandlesPerModel < options.MinSamples + MLFeatureHelper.LookbackWindow + 1)
            errors.Add("MLFeatureStalenessOptions.MaxCandlesPerModel must cover MinSamples plus the feature lookback window.");
        if (options.MaxFeatures is < 1 or > MLFeatureHelper.MaxAllowedFeatureCount)
            errors.Add($"MLFeatureStalenessOptions.MaxFeatures must be between 1 and {MLFeatureHelper.MaxAllowedFeatureCount}.");
        if (options.MaxModelsPerCycle is < 1 or > 100_000)
            errors.Add("MLFeatureStalenessOptions.MaxModelsPerCycle must be between 1 and 100000.");
        if (!double.IsFinite(options.AbsAutocorrThreshold) || options.AbsAutocorrThreshold is < 0.50 or > 0.9999)
            errors.Add("MLFeatureStalenessOptions.AbsAutocorrThreshold must be a finite value between 0.50 and 0.9999.");
        if (!double.IsFinite(options.ConstantVarianceEpsilon) || options.ConstantVarianceEpsilon is < 1.0e-12 or > 1.0)
            errors.Add("MLFeatureStalenessOptions.ConstantVarianceEpsilon must be a finite value between 1e-12 and 1.0.");
        if (!double.IsFinite(options.MaxStaleFeatureFraction) || options.MaxStaleFeatureFraction is < 0.0 or > 1.0)
            errors.Add("MLFeatureStalenessOptions.MaxStaleFeatureFraction must be a finite value between 0.0 and 1.0.");
        if (options.RetentionDays is < 0 or > 3_650)
            errors.Add("MLFeatureStalenessOptions.RetentionDays must be between 0 and 3650.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureStalenessOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureStalenessOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
