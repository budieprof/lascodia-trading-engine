using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLConformalBreakerOptions"/> at startup.</summary>
public class MLConformalBreakerOptionsValidator : IValidateOptions<MLConformalBreakerOptions>
{
    public ValidateOptionsResult Validate(string? name, MLConformalBreakerOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelayMinutes < 0)
            errors.Add("MLConformalBreakerOptions.InitialDelayMinutes must be >= 0.");
        if (o.PollIntervalHours < 1)
            errors.Add("MLConformalBreakerOptions.PollIntervalHours must be >= 1.");
        if (o.MinLogs < 10)
            errors.Add("MLConformalBreakerOptions.MinLogs must be >= 10.");
        if (o.MaxLogs < o.MinLogs)
            errors.Add("MLConformalBreakerOptions.MaxLogs must be >= MinLogs.");
        if (o.ConsecutiveUncoveredTrigger < 1)
            errors.Add("MLConformalBreakerOptions.ConsecutiveUncoveredTrigger must be >= 1.");
        if (o.CoverageTolerance is < 0.0 or > 0.5)
            errors.Add("MLConformalBreakerOptions.CoverageTolerance must be between 0.0 and 0.5.");
        if (o.MaxSuspensionBars < 1)
            errors.Add("MLConformalBreakerOptions.MaxSuspensionBars must be >= 1.");
        if (o.WilsonConfidenceLevel is <= 0.5 or >= 1.0)
            errors.Add("MLConformalBreakerOptions.WilsonConfidenceLevel must be > 0.5 and < 1.0.");
        if (o.StatisticalAlpha is <= 0.0 or >= 0.5)
            errors.Add("MLConformalBreakerOptions.StatisticalAlpha must be > 0.0 and < 0.5.");
        if (o.BackfillBatchSize < 1)
            errors.Add("MLConformalBreakerOptions.BackfillBatchSize must be >= 1.");
        if (o.BackfillPollIntervalMinutes < 1)
            errors.Add("MLConformalBreakerOptions.BackfillPollIntervalMinutes must be >= 1.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
