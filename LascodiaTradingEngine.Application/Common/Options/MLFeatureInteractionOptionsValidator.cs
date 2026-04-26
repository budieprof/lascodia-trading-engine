using LascodiaTradingEngine.Application.MLModels.Shared;
using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureInteractionOptions"/> at startup.</summary>
public sealed class MLFeatureInteractionOptionsValidator : IValidateOptions<MLFeatureInteractionOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureInteractionOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureInteractionOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLFeatureInteractionOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.TopK is < 1 or > 20)
            errors.Add("MLFeatureInteractionOptions.TopK must be between 1 and 20.");
        if (options.IncludedTopN is < 0 or > 20)
            errors.Add("MLFeatureInteractionOptions.IncludedTopN must be between 0 and 20.");
        if (options.IncludedTopN > options.TopK)
            errors.Add("MLFeatureInteractionOptions.IncludedTopN cannot exceed TopK.");
        if (options.MinSamples is < 50 or > 100_000)
            errors.Add("MLFeatureInteractionOptions.MinSamples must be between 50 and 100000.");
        if (options.MaxLogsPerModel is < 100 or > 100_000)
            errors.Add("MLFeatureInteractionOptions.MaxLogsPerModel must be between 100 and 100000.");
        if (options.MaxFeatures is < 2 or > MLFeatureHelper.MaxAllowedFeatureCount)
            errors.Add($"MLFeatureInteractionOptions.MaxFeatures must be between 2 and {MLFeatureHelper.MaxAllowedFeatureCount}.");
        if (options.MaxModelsPerCycle is < 1 or > 10_000)
            errors.Add("MLFeatureInteractionOptions.MaxModelsPerCycle must be between 1 and 10000.");
        if (!double.IsFinite(options.MinEffectSize) || options.MinEffectSize is < 0.0 or > 1.0)
            errors.Add("MLFeatureInteractionOptions.MinEffectSize must be a finite value between 0.0 and 1.0.");
        if (!double.IsFinite(options.MaxQValue) || options.MaxQValue is < 0.0 or > 1.0)
            errors.Add("MLFeatureInteractionOptions.MaxQValue must be a finite value between 0.0 and 1.0.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureInteractionOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureInteractionOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
