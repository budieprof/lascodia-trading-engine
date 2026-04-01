using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the feature store and backfill worker.</summary>
public class FeatureStoreOptions : ConfigurationOption<FeatureStoreOptions>
{
    /// <summary>Batch size for backfill processing.</summary>
    public int BackfillBatchSize { get; set; } = 500;

    /// <summary>Polling interval in seconds for the backfill worker.</summary>
    public int BackfillPollIntervalSeconds { get; set; } = 3600;

    /// <summary>Maximum candles to process per backfill run.</summary>
    public int MaxCandlesPerRun { get; set; } = 10000;
}
