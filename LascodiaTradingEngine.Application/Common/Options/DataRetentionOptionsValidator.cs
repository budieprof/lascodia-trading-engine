using Microsoft.Extensions.Options;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Validates <see cref="DataRetentionOptions"/> at startup.</summary>
public sealed class DataRetentionOptionsValidator : IValidateOptions<DataRetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, DataRetentionOptions options)
    {
        var errors = new List<string>();

        if (options.PredictionLogHotDays is < 1 or > 3_650)
            errors.Add("DataRetentionOptions.PredictionLogHotDays must be between 1 and 3650.");
        if (options.TickRecordHotDays is < 1 or > 3_650)
            errors.Add("DataRetentionOptions.TickRecordHotDays must be between 1 and 3650.");
        if (options.DecisionLogHotDays is < 1 or > 3_650)
            errors.Add("DataRetentionOptions.DecisionLogHotDays must be between 1 and 3650.");
        if (options.PendingModelStrategyTtlDays is < 0 or > 3_650)
            errors.Add("DataRetentionOptions.PendingModelStrategyTtlDays must be between 0 and 3650.");
        if (options.CandleHotDays is < 1 or > 7_300)
            errors.Add("DataRetentionOptions.CandleHotDays must be between 1 and 7300.");
        if (options.IdempotencyKeyTtlHours is < 1 or > 8_760)
            errors.Add("DataRetentionOptions.IdempotencyKeyTtlHours must be between 1 and 8760.");
        if (options.WorkerHealthSnapshotDays is < 1 or > 3_650)
            errors.Add("DataRetentionOptions.WorkerHealthSnapshotDays must be between 1 and 3650.");
        if (options.MarketDataAnomalyDays is < 1 or > 3_650)
            errors.Add("DataRetentionOptions.MarketDataAnomalyDays must be between 1 and 3650.");
        if (options.IntegrationEventLogPublishedDays is < 1 or > 365)
            errors.Add("DataRetentionOptions.IntegrationEventLogPublishedDays must be between 1 and 365.");
        if (options.BatchSize is < 1 or > 100_000)
            errors.Add("DataRetentionOptions.BatchSize must be between 1 and 100000.");
        if (options.InitialDelaySeconds is < 0 or > 86_400)
            errors.Add("DataRetentionOptions.InitialDelaySeconds must be between 0 and 86400.");
        if (options.PollIntervalSeconds is < 1 or > 86_400)
            errors.Add("DataRetentionOptions.PollIntervalSeconds must be between 1 and 86400.");
        if (options.PollJitterSeconds is < 0 or > 86_400)
            errors.Add("DataRetentionOptions.PollJitterSeconds must be between 0 and 86400.");
        if (options.LockTimeoutSeconds is < 0 or > 300)
            errors.Add("DataRetentionOptions.LockTimeoutSeconds must be between 0 and 300.");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
