using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLCorrelatedSignalConflictOptions"/> at startup.</summary>
public class MLCorrelatedSignalConflictOptionsValidator : IValidateOptions<MLCorrelatedSignalConflictOptions>
{
    public ValidateOptionsResult Validate(string? name, MLCorrelatedSignalConflictOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLCorrelatedSignalConflictOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (o.PollIntervalSeconds is < 30 or > 86_400)
            errors.Add("MLCorrelatedSignalConflictOptions.PollIntervalSeconds must be between 30 and 86400.");
        if (o.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLCorrelatedSignalConflictOptions.PollJitterSeconds must be between 0 and 86400.");
        if (o.WindowMinutes is < 1 or > 1_440)
            errors.Add("MLCorrelatedSignalConflictOptions.WindowMinutes must be between 1 and 1440.");
        if (o.MaxSignalsPerCycle is < 1 or > 10_000)
            errors.Add("MLCorrelatedSignalConflictOptions.MaxSignalsPerCycle must be between 1 and 10000.");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLCorrelatedSignalConflictOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (o.AlertCooldownSeconds is < 60 or > 86_400)
            errors.Add("MLCorrelatedSignalConflictOptions.AlertCooldownSeconds must be between 60 and 86400.");
        if (string.IsNullOrWhiteSpace(o.AlertDestination) || o.AlertDestination.Length > 100)
            errors.Add("MLCorrelatedSignalConflictOptions.AlertDestination is required and must be at most 100 characters.");
        if (o.PairMapJson.Length > 10_000)
            errors.Add("MLCorrelatedSignalConflictOptions.PairMapJson must be at most 10000 characters.");

        if (!string.IsNullOrWhiteSpace(o.PairMapJson))
        {
            try
            {
                using var document = JsonDocument.Parse(o.PairMapJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    errors.Add("MLCorrelatedSignalConflictOptions.PairMapJson must be a JSON object.");
            }
            catch (JsonException)
            {
                errors.Add("MLCorrelatedSignalConflictOptions.PairMapJson must be valid JSON.");
            }
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
