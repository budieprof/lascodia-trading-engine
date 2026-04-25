using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLEwmaAccuracyOptions"/> at startup.</summary>
public sealed class MLEwmaAccuracyOptionsValidator : IValidateOptions<MLEwmaAccuracyOptions>
{
    public ValidateOptionsResult Validate(string? name, MLEwmaAccuracyOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLEwmaAccuracyOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (o.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("MLEwmaAccuracyOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (!double.IsFinite(o.Alpha) || o.Alpha is <= 0.0 or > 1.0)
            errors.Add("MLEwmaAccuracyOptions.Alpha must be finite and in the range (0, 1].");
        if (o.MinPredictions is < 1 or > 1_000_000)
            errors.Add("MLEwmaAccuracyOptions.MinPredictions must be between 1 and 1000000.");
        if (!double.IsFinite(o.WarnThreshold) || o.WarnThreshold is < 0.0 or > 1.0)
            errors.Add("MLEwmaAccuracyOptions.WarnThreshold must be finite and between 0 and 1.");
        if (!double.IsFinite(o.CriticalThreshold) || o.CriticalThreshold is < 0.0 or > 1.0)
            errors.Add("MLEwmaAccuracyOptions.CriticalThreshold must be finite and between 0 and 1.");
        if (o.CriticalThreshold > o.WarnThreshold)
            errors.Add("MLEwmaAccuracyOptions.CriticalThreshold must be <= WarnThreshold.");
        if (string.IsNullOrWhiteSpace(o.AlertDestination) || o.AlertDestination.Length > 128)
            errors.Add("MLEwmaAccuracyOptions.AlertDestination must be 1-128 non-whitespace characters.");
        if (o.MaxModelsPerCycle is < 1 or > 250_000)
            errors.Add("MLEwmaAccuracyOptions.MaxModelsPerCycle must be between 1 and 250000.");
        if (o.PredictionLogBatchSize is < 1 or > 10_000)
            errors.Add("MLEwmaAccuracyOptions.PredictionLogBatchSize must be between 1 and 10000.");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLEwmaAccuracyOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (o.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLEwmaAccuracyOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
