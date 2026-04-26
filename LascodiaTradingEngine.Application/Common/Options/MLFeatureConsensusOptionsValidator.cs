using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLFeatureConsensusOptions"/> at startup.</summary>
public sealed class MLFeatureConsensusOptionsValidator : IValidateOptions<MLFeatureConsensusOptions>
{
    public ValidateOptionsResult Validate(string? name, MLFeatureConsensusOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLFeatureConsensusOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (o.PollIntervalSeconds is < 60 or > 86_400)
            errors.Add("MLFeatureConsensusOptions.PollIntervalSeconds must be between 60 and 86400.");
        if (o.MinModelsForConsensus is < 2 or > 1000)
            errors.Add("MLFeatureConsensusOptions.MinModelsForConsensus must be between 2 and 1000.");
        if (o.MinArchitecturesForConsensus is < 1 or > 100)
            errors.Add("MLFeatureConsensusOptions.MinArchitecturesForConsensus must be between 1 and 100.");
        if (o.MinArchitecturesForConsensus > o.MinModelsForConsensus)
            errors.Add("MLFeatureConsensusOptions.MinArchitecturesForConsensus must be <= MinModelsForConsensus.");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLFeatureConsensusOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (o.MinSnapshotSpacingSeconds is < 0 or > 86_400)
            errors.Add("MLFeatureConsensusOptions.MinSnapshotSpacingSeconds must be between 0 and 86400.");
        if (o.MaxModelsPerPair is < 2 or > 5000)
            errors.Add("MLFeatureConsensusOptions.MaxModelsPerPair must be between 2 and 5000.");
        if (o.MaxModelsPerPair < o.MinModelsForConsensus)
            errors.Add("MLFeatureConsensusOptions.MaxModelsPerPair must be >= MinModelsForConsensus.");
        if (o.MaxPairsPerCycle is < 1 or > 100_000)
            errors.Add("MLFeatureConsensusOptions.MaxPairsPerCycle must be between 1 and 100000.");
        if (o.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLFeatureConsensusOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
