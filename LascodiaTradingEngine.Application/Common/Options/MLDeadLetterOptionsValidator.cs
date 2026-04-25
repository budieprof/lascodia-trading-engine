using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDeadLetterOptions"/> at startup.</summary>
public sealed class MLDeadLetterOptionsValidator : IValidateOptions<MLDeadLetterOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDeadLetterOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLDeadLetterOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 2_592_000)
            errors.Add("MLDeadLetterOptions.PollIntervalSeconds must be between 60 and 2592000.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLDeadLetterOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.RetryAfterDays is < 1 or > 3_650)
            errors.Add("MLDeadLetterOptions.RetryAfterDays must be between 1 and 3650.");
        if (options.MaxRetries is < 0 or > 100)
            errors.Add("MLDeadLetterOptions.MaxRetries must be between 0 and 100.");
        if (options.MaxRunsPerCycle is < 1 or > 10_000)
            errors.Add("MLDeadLetterOptions.MaxRunsPerCycle must be between 1 and 10000.");
        if (options.MaxRequeuesPerCycle is < 0 or > 10_000)
            errors.Add("MLDeadLetterOptions.MaxRequeuesPerCycle must be between 0 and 10000.");
        if (options.MaxRequeuesPerCycle > options.MaxRunsPerCycle)
            errors.Add("MLDeadLetterOptions.MaxRequeuesPerCycle cannot exceed MaxRunsPerCycle.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDeadLetterOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.AlertCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLDeadLetterOptions.AlertCooldownSeconds must be between 0 and 2592000.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDeadLetterOptions.AlertDestination is required and must be at most 100 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
