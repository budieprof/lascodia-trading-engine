using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLHawkesProcessOptions"/> at startup.</summary>
public sealed class MLHawkesProcessOptionsValidator : IValidateOptions<MLHawkesProcessOptions>
{
    public ValidateOptionsResult Validate(string? name, MLHawkesProcessOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLHawkesProcessOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 300 or > 604_800)
            errors.Add("MLHawkesProcessOptions.PollIntervalSeconds must be between 300 and 604800.");
        if (options.CalibrationWindowDays is < 1 or > 365)
            errors.Add("MLHawkesProcessOptions.CalibrationWindowDays must be between 1 and 365.");
        if (options.MinimumFitSamples is < 3 or > 1_000_000)
            errors.Add("MLHawkesProcessOptions.MinimumFitSamples must be between 3 and 1000000.");
        if (options.MaxPairsPerCycle is < 1 or > 100_000)
            errors.Add("MLHawkesProcessOptions.MaxPairsPerCycle must be between 1 and 100000.");
        if (options.MaxSignalsPerPair is < 3 or > 1_000_000)
            errors.Add("MLHawkesProcessOptions.MaxSignalsPerPair must be between 3 and 1000000.");
        if (options.MaxSignalsPerPair < options.MinimumFitSamples)
            errors.Add("MLHawkesProcessOptions.MaxSignalsPerPair cannot be less than MinimumFitSamples.");
        if (!double.IsFinite(options.MaximumBranchingRatio) || options.MaximumBranchingRatio is < 0.05 or > 0.999)
            errors.Add("MLHawkesProcessOptions.MaximumBranchingRatio must be a finite value between 0.05 and 0.999.");
        if (options.OptimisationSweeps is < 10 or > 500)
            errors.Add("MLHawkesProcessOptions.OptimisationSweeps must be between 10 and 500.");
        if (options.MaxOptimisationStarts is < 1 or > 100)
            errors.Add("MLHawkesProcessOptions.MaxOptimisationStarts must be between 1 and 100.");
        if (!double.IsFinite(options.SuppressMultiplier) || options.SuppressMultiplier is < 1.01 or > 100.0)
            errors.Add("MLHawkesProcessOptions.SuppressMultiplier must be a finite value between 1.01 and 100.0.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLHawkesProcessOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLHawkesProcessOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
