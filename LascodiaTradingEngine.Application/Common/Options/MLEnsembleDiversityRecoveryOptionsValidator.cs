using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLEnsembleDiversityRecoveryOptions"/> at startup.</summary>
public sealed class MLEnsembleDiversityRecoveryOptionsValidator
    : IValidateOptions<MLEnsembleDiversityRecoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, MLEnsembleDiversityRecoveryOptions options)
    {
        var errors = new List<string>();

        if (options.PollIntervalSeconds is < 60 or > 604_800)
            errors.Add("MLEnsembleDiversityRecoveryOptions.PollIntervalSeconds must be between 60 and 604800.");
        if (options.MaxEnsembleDiversity is < 0.01 or > 0.999 || !double.IsFinite(options.MaxEnsembleDiversity))
            errors.Add("MLEnsembleDiversityRecoveryOptions.MaxEnsembleDiversity must be between 0.01 and 0.999.");
        if (options.MinDisagreementDiversity is < 0.0 or > 1.0 || !double.IsFinite(options.MinDisagreementDiversity))
            errors.Add("MLEnsembleDiversityRecoveryOptions.MinDisagreementDiversity must be between 0.0 and 1.0.");
        if (options.ForcedNclLambda is < 0.0 or > 5.0 || !double.IsFinite(options.ForcedNclLambda))
            errors.Add("MLEnsembleDiversityRecoveryOptions.ForcedNclLambda must be between 0.0 and 5.0.");
        if (options.ForcedDiversityLambda is < 0.0 or > 5.0 || !double.IsFinite(options.ForcedDiversityLambda))
            errors.Add("MLEnsembleDiversityRecoveryOptions.ForcedDiversityLambda must be between 0.0 and 5.0.");
        if (options.TrainingDataWindowDays is < 30 or > 3_650)
            errors.Add("MLEnsembleDiversityRecoveryOptions.TrainingDataWindowDays must be between 30 and 3650.");
        if (options.MaxModelsPerCycle is < 1 or > 10_000)
            errors.Add("MLEnsembleDiversityRecoveryOptions.MaxModelsPerCycle must be between 1 and 10000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLEnsembleDiversityRecoveryOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 5 or > 600)
            errors.Add("MLEnsembleDiversityRecoveryOptions.DbCommandTimeoutSeconds must be between 5 and 600.");
        if (options.MinTimeBetweenRetrainsHours is < 0 or > 720)
            errors.Add("MLEnsembleDiversityRecoveryOptions.MinTimeBetweenRetrainsHours must be between 0 and 720.");
        if (options.MaxQueueDepth is < 1)
            errors.Add("MLEnsembleDiversityRecoveryOptions.MaxQueueDepth must be positive.");
        if (options.RetrainPriority is < 0 or > 10)
            errors.Add("MLEnsembleDiversityRecoveryOptions.RetrainPriority must be between 0 and 10.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
