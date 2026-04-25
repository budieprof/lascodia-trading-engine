using Microsoft.Extensions.Options;
using LascodiaTradingEngine.Application.Workers;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLCorrelatedFailureOptions"/> at startup.</summary>
public class MLCorrelatedFailureOptionsValidator : IValidateOptions<MLCorrelatedFailureOptions>
{
    public ValidateOptionsResult Validate(string? name, MLCorrelatedFailureOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLCorrelatedFailureOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (o.PollIntervalSeconds is < 30 or > 86_400)
            errors.Add("MLCorrelatedFailureOptions.PollIntervalSeconds must be between 30 and 86400.");
        if (o.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("MLCorrelatedFailureOptions.PollJitterSeconds must be between 0 and 86400.");
        if (!double.IsFinite(o.AlarmRatio) || o.AlarmRatio is < 0.01 or > 1.0)
            errors.Add("MLCorrelatedFailureOptions.AlarmRatio must be a finite value between 0.01 and 1.0.");
        if (!double.IsFinite(o.RecoveryRatio) || o.RecoveryRatio is < 0.0 or > 1.0)
            errors.Add("MLCorrelatedFailureOptions.RecoveryRatio must be a finite value between 0.0 and 1.0.");
        if (o.RecoveryRatio >= o.AlarmRatio)
            errors.Add("MLCorrelatedFailureOptions.RecoveryRatio must be lower than AlarmRatio.");
        if (o.MinModelsForAlarm < 2)
            errors.Add("MLCorrelatedFailureOptions.MinModelsForAlarm must be >= 2.");
        if (o.StateChangeCooldownMinutes is < 0 or > 1_440)
            errors.Add("MLCorrelatedFailureOptions.StateChangeCooldownMinutes must be between 0 and 1440.");
        if (o.ModelStatsBatchSize is < 1 or > 10_000)
            errors.Add("MLCorrelatedFailureOptions.ModelStatsBatchSize must be between 1 and 10000.");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLCorrelatedFailureOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (!Enum.TryParse<MLCorrelatedFailureMetric>(o.FailureMetric, ignoreCase: true, out _))
            errors.Add("MLCorrelatedFailureOptions.FailureMetric must be DirectionAccuracy, Profitability, or Composite.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
