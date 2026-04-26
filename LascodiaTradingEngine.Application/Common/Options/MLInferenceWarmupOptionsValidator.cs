using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLInferenceWarmupOptions"/> at startup.</summary>
public sealed class MLInferenceWarmupOptionsValidator : IValidateOptions<MLInferenceWarmupOptions>
{
    public ValidateOptionsResult Validate(string? name, MLInferenceWarmupOptions options)
    {
        var errors = new List<string>();

        if (options.StartupDelaySeconds is < 0 or > 300)
            errors.Add("MLInferenceWarmupOptions.StartupDelaySeconds must be between 0 and 300.");
        if (options.ModelTimeoutSeconds is < 1 or > 600)
            errors.Add("MLInferenceWarmupOptions.ModelTimeoutSeconds must be between 1 and 600.");
        if (options.MaxModelsPerStartup is < 1 or > 250_000)
            errors.Add("MLInferenceWarmupOptions.MaxModelsPerStartup must be between 1 and 250000.");
        if (options.MaxTimeoutsBeforeAbort is < 0 or > 100)
            errors.Add("MLInferenceWarmupOptions.MaxTimeoutsBeforeAbort must be between 0 and 100.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLInferenceWarmupOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLInferenceWarmupOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
