using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="CorrelationMatrixOptions"/> at startup.</summary>
public class CorrelationMatrixOptionsValidator : IValidateOptions<CorrelationMatrixOptions>
{
    public ValidateOptionsResult Validate(string? name, CorrelationMatrixOptions o)
    {
        var errors = new List<string>();

        if (o.PollIntervalHours is < 1 or > 168)
            errors.Add("CorrelationMatrixOptions.PollIntervalHours must be between 1 and 168.");
        if (o.LookbackDays is < 5 or > 365)
            errors.Add("CorrelationMatrixOptions.LookbackDays must be between 5 and 365.");
        if (o.MinClosesPerSymbol is < 2 or > 366)
            errors.Add("CorrelationMatrixOptions.MinClosesPerSymbol must be between 2 and 366.");
        if (o.MinOverlapPoints is < 2 or > 366)
            errors.Add("CorrelationMatrixOptions.MinOverlapPoints must be between 2 and 366.");
        if (o.MinOverlapPoints > o.MinClosesPerSymbol - 1)
            errors.Add("CorrelationMatrixOptions.MinOverlapPoints must be <= MinClosesPerSymbol - 1.");
        if (!double.IsFinite(o.DecayHalfLife) || o.DecayHalfLife is <= 0.0 or > 365.0)
            errors.Add("CorrelationMatrixOptions.DecayHalfLife must be a finite value greater than 0 and at most 365.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
