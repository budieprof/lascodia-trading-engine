using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureDataFreshnessOptions"/> at startup.</summary>
public sealed class MLFeatureDataFreshnessOptionsValidator : IValidateOptions<MLFeatureDataFreshnessOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureDataFreshnessOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureDataFreshnessOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 86_400)
            errors.Add("MLFeatureDataFreshnessOptions.PollIntervalSeconds must be between 60 and 86400.");
        if (options.MaxCotAgeDays is < 1 or > 60)
            errors.Add("MLFeatureDataFreshnessOptions.MaxCotAgeDays must be between 1 and 60.");
        if (options.MaxSentimentAgeHours is < 1 or > 168)
            errors.Add("MLFeatureDataFreshnessOptions.MaxSentimentAgeHours must be between 1 and 168.");
        if (!double.IsFinite(options.CandleStaleMultiplier) || options.CandleStaleMultiplier is < 1.0 or > 100.0)
            errors.Add("MLFeatureDataFreshnessOptions.CandleStaleMultiplier must be a finite value between 1.0 and 100.0.");
        if (options.MaxPairsPerCycle is < 1 or > 100_000)
            errors.Add("MLFeatureDataFreshnessOptions.MaxPairsPerCycle must be between 1 and 100000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureDataFreshnessOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureDataFreshnessOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.AlertCooldownSeconds is < 1 or > 604_800)
            errors.Add("MLFeatureDataFreshnessOptions.AlertCooldownSeconds must be between 1 and 604800.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 128)
            errors.Add("MLFeatureDataFreshnessOptions.AlertDestination is required and must be at most 128 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
