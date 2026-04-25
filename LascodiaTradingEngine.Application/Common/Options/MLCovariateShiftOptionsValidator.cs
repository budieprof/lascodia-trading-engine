using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLCovariateShiftOptions"/> at startup.</summary>
public sealed class MLCovariateShiftOptionsValidator : IValidateOptions<MLCovariateShiftOptions>
{
    public ValidateOptionsResult Validate(string? name, MLCovariateShiftOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLCovariateShiftOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 86_400)
            errors.Add("MLCovariateShiftOptions.PollIntervalSeconds must be between 60 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLCovariateShiftOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.WindowDays is < 1 or > 3_650)
            errors.Add("MLCovariateShiftOptions.WindowDays must be between 1 and 3650.");
        if (!double.IsFinite(options.PsiThreshold) || options.PsiThreshold is < 0.01 or > 5.0)
            errors.Add("MLCovariateShiftOptions.PsiThreshold must be a finite value between 0.01 and 5.0.");
        if (!double.IsFinite(options.PerFeaturePsiThreshold) || options.PerFeaturePsiThreshold is < 0.01 or > 5.0)
            errors.Add("MLCovariateShiftOptions.PerFeaturePsiThreshold must be a finite value between 0.01 and 5.0.");
        if (!double.IsFinite(options.MultivariateThreshold) || options.MultivariateThreshold is < 1.01 or > 100.0)
            errors.Add("MLCovariateShiftOptions.MultivariateThreshold must be a finite value between 1.01 and 100.0.");
        if (options.MinCandles is < 20 or > 100_000)
            errors.Add("MLCovariateShiftOptions.MinCandles must be between 20 and 100000.");
        if (options.TrainingDays is < 1 or > 3_650)
            errors.Add("MLCovariateShiftOptions.TrainingDays must be between 1 and 3650.");
        if (options.MaxModelsPerCycle is < 1 or > 10_000)
            errors.Add("MLCovariateShiftOptions.MaxModelsPerCycle must be between 1 and 10000.");
        if (options.MaxQueuedRetrains is < 1 or > 100_000)
            errors.Add("MLCovariateShiftOptions.MaxQueuedRetrains must be between 1 and 100000.");
        if (options.RetrainCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLCovariateShiftOptions.RetrainCooldownSeconds must be between 0 and 2592000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLCovariateShiftOptions.LockTimeoutSeconds must be between 0 and 300.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
