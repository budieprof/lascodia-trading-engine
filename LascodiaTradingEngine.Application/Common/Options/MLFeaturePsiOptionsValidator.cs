using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeaturePsiOptions"/> at startup.</summary>
public sealed class MLFeaturePsiOptionsValidator : IValidateOptions<MLFeaturePsiOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeaturePsiOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeaturePsiOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLFeaturePsiOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.CandleWindowDays is < 1 or > 3_650)
            errors.Add("MLFeaturePsiOptions.CandleWindowDays must be between 1 and 3650.");
        if (options.MinFeatureSamples is < 20 or > 100_000)
            errors.Add("MLFeaturePsiOptions.MinFeatureSamples must be between 20 and 100000.");
        if (!double.IsFinite(options.PsiAlertThreshold) || options.PsiAlertThreshold is < 0.01 or > 5.0)
            errors.Add("MLFeaturePsiOptions.PsiAlertThreshold must be a finite value between 0.01 and 5.0.");
        if (!double.IsFinite(options.PsiRetrainThreshold) || options.PsiRetrainThreshold is < 0.01 or > 5.0)
            errors.Add("MLFeaturePsiOptions.PsiRetrainThreshold must be a finite value between 0.01 and 5.0.");
        if (options.PsiRetrainThreshold < options.PsiAlertThreshold)
            errors.Add("MLFeaturePsiOptions.PsiRetrainThreshold cannot be less than PsiAlertThreshold.");
        if (!double.IsFinite(options.RetrainMajorityFraction) || options.RetrainMajorityFraction is < 0.0 or > 1.0)
            errors.Add("MLFeaturePsiOptions.RetrainMajorityFraction must be a finite value between 0.0 and 1.0.");
        if (options.MaxModelsPerCycle is < 1 or > 10_000)
            errors.Add("MLFeaturePsiOptions.MaxModelsPerCycle must be between 1 and 10000.");
        if (options.MaxFeaturesInAlert is < 1 or > 100)
            errors.Add("MLFeaturePsiOptions.MaxFeaturesInAlert must be between 1 and 100.");
        if (options.TrainingWindowDays is < 1 or > 3_650)
            errors.Add("MLFeaturePsiOptions.TrainingWindowDays must be between 1 and 3650.");
        if (options.RetrainCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLFeaturePsiOptions.RetrainCooldownSeconds must be between 0 and 2592000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeaturePsiOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeaturePsiOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.AlertCooldownSeconds is < 1 or > 604_800)
            errors.Add("MLFeaturePsiOptions.AlertCooldownSeconds must be between 1 and 604800.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 128)
            errors.Add("MLFeaturePsiOptions.AlertDestination is required and must be at most 128 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
