using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="EvolutionaryGeneratorOptions"/> at startup.</summary>
public class EvolutionaryGeneratorOptionsValidator : IValidateOptions<EvolutionaryGeneratorOptions>
{
    public ValidateOptionsResult Validate(string? name, EvolutionaryGeneratorOptions o)
    {
        var errors = new List<string>();

        if (o.PollIntervalSeconds is < 60 or > 7 * 24 * 60 * 60)
            errors.Add("EvolutionaryGeneratorOptions.PollIntervalSeconds must be in [60, 604800].");
        if (o.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("EvolutionaryGeneratorOptions.PollJitterSeconds must be in [0, 86400].");
        if (o.MaxOffspringPerCycle is < 0 or > 1_000)
            errors.Add("EvolutionaryGeneratorOptions.MaxOffspringPerCycle must be in [0, 1000].");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("EvolutionaryGeneratorOptions.LockTimeoutSeconds must be in [0, 300].");
        if (o.FailureBackoffCapShift is < 0 or > 16)
            errors.Add("EvolutionaryGeneratorOptions.FailureBackoffCapShift must be in [0, 16].");
        if (o.FleetSystemicConsecutiveZeroInsertCycles < 1)
            errors.Add("EvolutionaryGeneratorOptions.FleetSystemicConsecutiveZeroInsertCycles must be >= 1.");
        if (o.StalenessAlertHours < 1)
            errors.Add("EvolutionaryGeneratorOptions.StalenessAlertHours must be >= 1.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
