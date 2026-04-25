using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDirectionStreakOptions"/> at startup.</summary>
public sealed class MLDirectionStreakOptionsValidator : IValidateOptions<MLDirectionStreakOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDirectionStreakOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLDirectionStreakOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 30 or > 86_400)
            errors.Add("MLDirectionStreakOptions.PollIntervalSeconds must be between 30 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLDirectionStreakOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.WindowSize is < 10 or > 500)
            errors.Add("MLDirectionStreakOptions.WindowSize must be between 10 and 500.");
        if (options.MaxSameDirectionFraction is < 0.55 or > 0.99 || !double.IsFinite(options.MaxSameDirectionFraction))
            errors.Add("MLDirectionStreakOptions.MaxSameDirectionFraction must be between 0.55 and 0.99.");
        if (options.EntropyThreshold is < 0.0 or > 1.0 || !double.IsFinite(options.EntropyThreshold))
            errors.Add("MLDirectionStreakOptions.EntropyThreshold must be between 0.0 and 1.0.");
        if (options.RunsZScoreThreshold is < -10.0 or > 0.0 || !double.IsFinite(options.RunsZScoreThreshold))
            errors.Add("MLDirectionStreakOptions.RunsZScoreThreshold must be between -10.0 and 0.0.");
        if (options.LongestRunFraction is < 0.10 or > 1.0 || !double.IsFinite(options.LongestRunFraction))
            errors.Add("MLDirectionStreakOptions.LongestRunFraction must be between 0.10 and 1.0.");
        if (options.MinFailedTestsToAlert is < 1 or > 4)
            errors.Add("MLDirectionStreakOptions.MinFailedTestsToAlert must be between 1 and 4.");
        if (options.MinFailedTestsToRetrain is < 1 or > 4)
            errors.Add("MLDirectionStreakOptions.MinFailedTestsToRetrain must be between 1 and 4.");
        if (options.MinFailedTestsToRetrain < options.MinFailedTestsToAlert)
            errors.Add("MLDirectionStreakOptions.MinFailedTestsToRetrain must be greater than or equal to MinFailedTestsToAlert.");
        if (options.RetrainLookbackDays is < 30 or > 3_650)
            errors.Add("MLDirectionStreakOptions.RetrainLookbackDays must be between 30 and 3650.");
        if (options.MaxModelsPerCycle is < 1 or > 100_000)
            errors.Add("MLDirectionStreakOptions.MaxModelsPerCycle must be between 1 and 100000.");
        if (options.MaxRetrainsPerCycle is < 0 or > 1_000)
            errors.Add("MLDirectionStreakOptions.MaxRetrainsPerCycle must be between 0 and 1000.");
        if (options.AlertCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLDirectionStreakOptions.AlertCooldownSeconds must be between 0 and 2592000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDirectionStreakOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDirectionStreakOptions.AlertDestination is required and must be at most 100 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
