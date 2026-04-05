using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the SpreadProfileWorker.</summary>
public class SpreadProfileOptions : ConfigurationOption<SpreadProfileOptions>
{
    /// <summary>Whether the spread profile aggregation worker is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often (in hours) the worker runs aggregation. Defaults to 24 (daily).</summary>
    public int WorkerIntervalHours { get; set; } = 24;

    /// <summary>Number of days of tick data to aggregate. Defaults to 30.</summary>
    public int AggregationDays { get; set; } = 30;
}
