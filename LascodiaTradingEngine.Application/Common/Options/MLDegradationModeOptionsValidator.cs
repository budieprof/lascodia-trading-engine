using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLDegradationModeOptions"/> at startup.</summary>
public sealed class MLDegradationModeOptionsValidator : IValidateOptions<MLDegradationModeOptions>
{
    public ValidateOptionsResult Validate(string? name, MLDegradationModeOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLDegradationModeOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 30 or > 86_400)
            errors.Add("MLDegradationModeOptions.PollIntervalSeconds must be between 30 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLDegradationModeOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.MaxSymbolsPerCycle is < 1 or > 100_000)
            errors.Add("MLDegradationModeOptions.MaxSymbolsPerCycle must be between 1 and 100000.");
        if (options.CriticalAfterMinutes is < 1 or > 43_200)
            errors.Add("MLDegradationModeOptions.CriticalAfterMinutes must be between 1 and 43200.");
        if (options.EscalateAfterHours is < 1 or > 720)
            errors.Add("MLDegradationModeOptions.EscalateAfterHours must be between 1 and 720.");
        if (options.AlertCooldownSeconds is < 0 or > 2_592_000)
            errors.Add("MLDegradationModeOptions.AlertCooldownSeconds must be between 0 and 2592000.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLDegradationModeOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (string.IsNullOrWhiteSpace(options.AlertDestination) || options.AlertDestination.Length > 100)
            errors.Add("MLDegradationModeOptions.AlertDestination is required and must be at most 100 characters.");
        if (string.IsNullOrWhiteSpace(options.EscalationDestination) || options.EscalationDestination.Length > 100)
            errors.Add("MLDegradationModeOptions.EscalationDestination is required and must be at most 100 characters.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
