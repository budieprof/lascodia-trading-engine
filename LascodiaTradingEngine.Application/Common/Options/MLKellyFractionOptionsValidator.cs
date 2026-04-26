using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLKellyFractionOptions"/> at startup.</summary>
public sealed class MLKellyFractionOptionsValidator : IValidateOptions<MLKellyFractionOptions>
{
    public ValidateOptionsResult Validate(string? name, MLKellyFractionOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLKellyFractionOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("MLKellyFractionOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (options.WindowDays is < 1 or > 3650)
            errors.Add("MLKellyFractionOptions.WindowDays must be between 1 and 3650.");
        if (options.MinUsableSamples is < 2 or > 1_000_000)
            errors.Add("MLKellyFractionOptions.MinUsableSamples must be between 2 and 1000000.");
        if (options.MinWins is < 1 or > 1_000_000)
            errors.Add("MLKellyFractionOptions.MinWins must be between 1 and 1000000.");
        if (options.MinLosses is < 1 or > 1_000_000)
            errors.Add("MLKellyFractionOptions.MinLosses must be between 1 and 1000000.");
        if (!double.IsFinite(options.MaxAbsKelly) || options.MaxAbsKelly is < 0.001 or > 1.0)
            errors.Add("MLKellyFractionOptions.MaxAbsKelly must be finite and between 0.001 and 1.0.");
        if (!double.IsFinite(options.PriorTrades) || options.PriorTrades is < 0.0 or > 1_000.0)
            errors.Add("MLKellyFractionOptions.PriorTrades must be finite and between 0 and 1000.");
        if (!double.IsFinite(options.WinRateLowerBoundZ) || options.WinRateLowerBoundZ is < 0.0 or > 3.0)
            errors.Add("MLKellyFractionOptions.WinRateLowerBoundZ must be finite and between 0 and 3.");
        if (!double.IsFinite(options.OutlierPercentile) || options.OutlierPercentile is < 0.50 or > 1.0)
            errors.Add("MLKellyFractionOptions.OutlierPercentile must be finite and between 0.50 and 1.0.");
        if (!double.IsFinite(options.MaxOutcomeMagnitude) || options.MaxOutcomeMagnitude is < 0.001 or > 1_000_000.0)
            errors.Add("MLKellyFractionOptions.MaxOutcomeMagnitude must be finite and between 0.001 and 1000000.");
        if (options.MaxModelsPerCycle is < 1 or > 250_000)
            errors.Add("MLKellyFractionOptions.MaxModelsPerCycle must be between 1 and 250000.");
        if (options.MaxPredictionLogsPerModel is < 10 or > 1_000_000)
            errors.Add("MLKellyFractionOptions.MaxPredictionLogsPerModel must be between 10 and 1000000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLKellyFractionOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLKellyFractionOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.MinWins > options.MinUsableSamples)
            errors.Add("MLKellyFractionOptions.MinWins must be <= MinUsableSamples.");
        if (options.MinLosses > options.MinUsableSamples)
            errors.Add("MLKellyFractionOptions.MinLosses must be <= MinUsableSamples.");
        if (options.MinUsableSamples > options.MaxPredictionLogsPerModel)
            errors.Add("MLKellyFractionOptions.MinUsableSamples must be <= MaxPredictionLogsPerModel.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
