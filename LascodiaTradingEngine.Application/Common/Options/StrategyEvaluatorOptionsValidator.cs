using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Validates <see cref="StrategyEvaluatorOptions"/> at startup to fail fast on misconfiguration.
/// Registered via <c>services.AddOptionsWithValidateOnStart</c> so invalid values are caught
/// before the engine processes any ticks — not at 3am during a position.
/// </summary>
public class StrategyEvaluatorOptionsValidator : IValidateOptions<StrategyEvaluatorOptions>
{
    public ValidateOptionsResult Validate(string? name, StrategyEvaluatorOptions o)
    {
        var errors = new List<string>();

        // Core parameters
        if (o.DefaultLotSize <= 0)
            errors.Add("DefaultLotSize must be > 0");
        if (o.AtrPeriodForSlTp < 2)
            errors.Add("AtrPeriodForSlTp must be >= 2");
        if (o.StopLossAtrMultiplier <= 0)
            errors.Add("StopLossAtrMultiplier must be > 0");
        if (o.TakeProfitAtrMultiplier <= 0)
            errors.Add("TakeProfitAtrMultiplier must be > 0");

        // Worker pipeline
        if (o.MaxParallelStrategies < 1)
            errors.Add("MaxParallelStrategies must be >= 1");
        if (o.MaxTickAgeSeconds < 0)
            errors.Add("MaxTickAgeSeconds must be >= 0");
        if (o.SignalCooldownSeconds < 0)
            errors.Add("SignalCooldownSeconds must be >= 0");
        if (o.MaxConsecutiveFailures < 0)
            errors.Add("MaxConsecutiveFailures must be >= 0");
        if (o.CircuitBreakerRecoverySeconds < 0)
            errors.Add("CircuitBreakerRecoverySeconds must be >= 0");
        if (o.ExpirySweepBatchSize < 1)
            errors.Add("ExpirySweepBatchSize must be >= 1");

        // Breakout evaluator
        if (o.BreakoutConfidence is < 0 or > 1)
            errors.Add("BreakoutConfidence must be between 0 and 1");
        if (o.BreakoutExpiryMinutes < 1)
            errors.Add("BreakoutExpiryMinutes must be >= 1");

        // MA crossover evaluator
        if (o.MaCrossoverConfidence is < 0 or > 1)
            errors.Add("MaCrossoverConfidence must be between 0 and 1");
        if (o.MaCrossoverExpiryMinutes < 1)
            errors.Add("MaCrossoverExpiryMinutes must be >= 1");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
