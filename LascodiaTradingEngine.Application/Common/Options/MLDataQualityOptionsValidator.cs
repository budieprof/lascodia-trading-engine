using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDataQualityOptions"/> at startup.</summary>
public sealed class MLDataQualityOptionsValidator : IValidateOptions<MLDataQualityOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDataQualityOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLDataQualityOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 30 or > 86_400)
            errors.Add("MLDataQualityOptions.PollIntervalSeconds must be between 30 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLDataQualityOptions.PollJitterSeconds must be between 0 and 86400.");
        if (!double.IsFinite(options.GapMultiplier) || options.GapMultiplier is < 1.0 or > 100.0)
            errors.Add("MLDataQualityOptions.GapMultiplier must be a finite value between 1.0 and 100.0.");
        if (!double.IsFinite(options.SpikeSigmas) || options.SpikeSigmas is < 1.0 or > 20.0)
            errors.Add("MLDataQualityOptions.SpikeSigmas must be a finite value between 1.0 and 20.0.");
        if (options.SpikeLookbackBars is < 3 or > 10_000)
            errors.Add("MLDataQualityOptions.SpikeLookbackBars must be between 3 and 10000.");
        if (options.MinSpikeBaselineBars is < 3 or > 10_000)
            errors.Add("MLDataQualityOptions.MinSpikeBaselineBars must be between 3 and 10000.");
        if (options.MinSpikeBaselineBars > options.SpikeLookbackBars)
            errors.Add("MLDataQualityOptions.MinSpikeBaselineBars cannot exceed SpikeLookbackBars.");
        if (options.LivePriceStalenessSeconds is < 1 or > 86_400)
            errors.Add("MLDataQualityOptions.LivePriceStalenessSeconds must be between 1 and 86400.");
        if (options.FutureTimestampToleranceSeconds is < 0 or > 3_600)
            errors.Add("MLDataQualityOptions.FutureTimestampToleranceSeconds must be between 0 and 3600.");
        if (options.MaxPairsPerCycle is < 1 or > 10_000)
            errors.Add("MLDataQualityOptions.MaxPairsPerCycle must be between 1 and 10000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDataQualityOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.AlertCooldownSeconds is < 0 or > 86_400)
            errors.Add("MLDataQualityOptions.AlertCooldownSeconds must be between 0 and 86400.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDataQualityOptions.AlertDestination is required and must be at most 100 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
