using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDriftAgreementOptions"/> at startup.</summary>
public sealed class MLDriftAgreementOptionsValidator : IValidateOptions<MLDriftAgreementOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDriftAgreementOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLDriftAgreementOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 60 or > 86_400)
            errors.Add("MLDriftAgreementOptions.PollIntervalSeconds must be between 60 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLDriftAgreementOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.CusumAlertWindowHours is < 1 or > 720)
            errors.Add("MLDriftAgreementOptions.CusumAlertWindowHours must be between 1 and 720.");
        if (options.ShiftRunWindowHours is < 1 or > 720)
            errors.Add("MLDriftAgreementOptions.ShiftRunWindowHours must be between 1 and 720.");
        if (options.ConsensusThreshold is < 2 or > 5)
            errors.Add("MLDriftAgreementOptions.ConsensusThreshold must be between 2 and 5.");
        if (options.MaxModelsPerCycle is < 1 or > 100_000)
            errors.Add("MLDriftAgreementOptions.MaxModelsPerCycle must be between 1 and 100000.");
        if (options.AlertCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLDriftAgreementOptions.AlertCooldownSeconds must be between 0 and 2592000.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDriftAgreementOptions.AlertDestination is required and must be at most 100 characters.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDriftAgreementOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 5 or > 600)
            errors.Add("MLDriftAgreementOptions.DbCommandTimeoutSeconds must be between 5 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
