using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="MLMetricsExportOptions"/> at startup.</summary>
public sealed class MLMetricsExportOptionsValidator : IValidateOptions<MLMetricsExportOptions>
{
    public ValidateOptionsResult Validate(string? name, MLMetricsExportOptions options)
    {
        var errors = new List<string>();

        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("MLMetricsExportOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("MLMetricsExportOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (options.WindowDays is < 1 or > 3_650)
            errors.Add("MLMetricsExportOptions.WindowDays must be between 1 and 3650.");
        if (options.MinResolvedSamples is < 1 or > 1_000_000)
            errors.Add("MLMetricsExportOptions.MinResolvedSamples must be between 1 and 1000000.");
        if (options.MaxModelsPerCycle is < 1 or > 250_000)
            errors.Add("MLMetricsExportOptions.MaxModelsPerCycle must be between 1 and 250000.");
        if (options.MaxPredictionLogsPerModel is < 10 or > 1_000_000)
            errors.Add("MLMetricsExportOptions.MaxPredictionLogsPerModel must be between 10 and 1000000.");
        if (options.MinResolvedSamples > options.MaxPredictionLogsPerModel)
            errors.Add("MLMetricsExportOptions.MinResolvedSamples must be <= MaxPredictionLogsPerModel.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("MLMetricsExportOptions.LockTimeoutSeconds must be between 0 and 300.");
        if (options.DbCommandTimeoutSeconds is < 1 or > 600)
            errors.Add("MLMetricsExportOptions.DbCommandTimeoutSeconds must be between 1 and 600.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
