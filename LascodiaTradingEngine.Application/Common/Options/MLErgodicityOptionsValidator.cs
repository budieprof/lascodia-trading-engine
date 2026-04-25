using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLErgodicityOptions"/> at startup.</summary>
public class MLErgodicityOptionsValidator : IValidateOptions<MLErgodicityOptions>
{
    public ValidateOptionsResult Validate(string? name, MLErgodicityOptions o)
    {
        var errors = new List<string>();

        if (o.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLErgodicityOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (o.PollIntervalHours is < 1 or > 168)
            errors.Add("MLErgodicityOptions.PollIntervalHours must be between 1 and 168.");
        if (o.WindowDays is < 1 or > 365)
            errors.Add("MLErgodicityOptions.WindowDays must be between 1 and 365.");
        if (o.MinSamples is < 2 or > 10_000)
            errors.Add("MLErgodicityOptions.MinSamples must be between 2 and 10000.");
        if (o.MaxLogsPerModel is < 2 or > 10_000)
            errors.Add("MLErgodicityOptions.MaxLogsPerModel must be between 2 and 10000.");
        if (o.MaxLogsPerModel < o.MinSamples)
            errors.Add("MLErgodicityOptions.MaxLogsPerModel must be >= MinSamples.");
        if (o.ModelBatchSize is < 1 or > 10_000)
            errors.Add("MLErgodicityOptions.ModelBatchSize must be between 1 and 10000.");
        if (o.MaxCycleModels is < 1 or > 250_000)
            errors.Add("MLErgodicityOptions.MaxCycleModels must be between 1 and 250000.");
        if (o.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLErgodicityOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (o.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLErgodicityOptions.DbCommandTimeoutSeconds must be between 1 and 600.");
        if (!double.IsFinite(o.MaxKellyAbs) || o.MaxKellyAbs is <= 0.0 or > 100.0)
            errors.Add("MLErgodicityOptions.MaxKellyAbs must be a finite value between 0 and 100.");
        if (!double.IsFinite(o.ReturnPipScale) || o.ReturnPipScale is <= 0.0 or > 100_000.0)
            errors.Add("MLErgodicityOptions.ReturnPipScale must be a finite value between 0 and 100000.");
        if (!double.IsFinite(o.MaxReturnAbs) || o.MaxReturnAbs is <= 0.0 or >= 1.0)
            errors.Add("MLErgodicityOptions.MaxReturnAbs must be a finite value greater than 0 and less than 1.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
