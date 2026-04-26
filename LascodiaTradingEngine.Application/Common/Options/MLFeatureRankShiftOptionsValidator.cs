using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureRankShiftOptions"/> at startup.</summary>
public sealed class MLFeatureRankShiftOptionsValidator : IValidateOptions<MLFeatureRankShiftOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureRankShiftOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureRankShiftOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLFeatureRankShiftOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.TopFeatures is < 3 or > 1_000)
            errors.Add("MLFeatureRankShiftOptions.TopFeatures must be between 3 and 1000.");
        if (options.MinUnionFeatures is < 3 or > 2_000)
            errors.Add("MLFeatureRankShiftOptions.MinUnionFeatures must be between 3 and 2000.");
        if (options.MinUnionFeatures > options.TopFeatures * 2)
            errors.Add("MLFeatureRankShiftOptions.MinUnionFeatures cannot exceed TopFeatures * 2.");
        if (!double.IsFinite(options.RankCorrelationThreshold) || options.RankCorrelationThreshold is < -1.0 or > 1.0)
            errors.Add("MLFeatureRankShiftOptions.RankCorrelationThreshold must be a finite value between -1.0 and 1.0.");
        if (options.LookbackDays is < 1 or > 3_650)
            errors.Add("MLFeatureRankShiftOptions.LookbackDays must be between 1 and 3650.");
        if (options.MaxModelsPerCycle is < 1 or > 100_000)
            errors.Add("MLFeatureRankShiftOptions.MaxModelsPerCycle must be between 1 and 100000.");
        if (options.MaxDivergingFeaturesInAlert is < 1 or > 100)
            errors.Add("MLFeatureRankShiftOptions.MaxDivergingFeaturesInAlert must be between 1 and 100.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureRankShiftOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureRankShiftOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.AlertCooldownSeconds is < 1 or > 604_800)
            errors.Add("MLFeatureRankShiftOptions.AlertCooldownSeconds must be between 1 and 604800.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 128)
            errors.Add("MLFeatureRankShiftOptions.AlertDestination is required and must be at most 128 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
