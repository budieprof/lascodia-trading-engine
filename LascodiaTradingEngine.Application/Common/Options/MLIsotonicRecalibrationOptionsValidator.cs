using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLIsotonicRecalibrationOptions"/> at startup.</summary>
public sealed class MLIsotonicRecalibrationOptionsValidator : IValidateOptions<MLIsotonicRecalibrationOptions>
{
    public ValidateOptionsResult Validate(string? name, MLIsotonicRecalibrationOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLIsotonicRecalibrationOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("MLIsotonicRecalibrationOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (options.WindowDays is < 1 or > 3650)
            errors.Add("MLIsotonicRecalibrationOptions.WindowDays must be between 1 and 3650.");
        if (options.MinResolved is < 10 or > 1_000_000)
            errors.Add("MLIsotonicRecalibrationOptions.MinResolved must be between 10 and 1000000.");
        if (options.MaxModelsPerCycle is < 1 or > 250_000)
            errors.Add("MLIsotonicRecalibrationOptions.MaxModelsPerCycle must be between 1 and 250000.");
        if (options.MaxPredictionLogsPerModel is < 10 or > 1_000_000)
            errors.Add("MLIsotonicRecalibrationOptions.MaxPredictionLogsPerModel must be between 10 and 1000000.");
        if (options.MinPavaSegments is < 2 or > 1_000)
            errors.Add("MLIsotonicRecalibrationOptions.MinPavaSegments must be between 2 and 1000.");
        if (options.MaxBreakpoints is < 2 or > 10_000)
            errors.Add("MLIsotonicRecalibrationOptions.MaxBreakpoints must be between 2 and 10000.");
        if (!double.IsFinite(options.MinimumEceImprovement) || options.MinimumEceImprovement is < 0.0 or > 1.0)
            errors.Add("MLIsotonicRecalibrationOptions.MinimumEceImprovement must be finite and between 0 and 1.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLIsotonicRecalibrationOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLIsotonicRecalibrationOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (options.MinResolved > options.MaxPredictionLogsPerModel)
            errors.Add("MLIsotonicRecalibrationOptions.MinResolved must be <= MaxPredictionLogsPerModel.");
        if (options.MinPavaSegments > options.MaxBreakpoints)
            errors.Add("MLIsotonicRecalibrationOptions.MinPavaSegments must be <= MaxBreakpoints.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
