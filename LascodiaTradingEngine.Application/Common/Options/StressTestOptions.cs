using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for stress testing scenarios and scheduling.</summary>
public class StressTestOptions : ConfigurationOption<StressTestOptions>
{
    /// <summary>Day of week to run automated weekly stress tests (0=Sunday, 6=Saturday).</summary>
    public int WeeklyRunDayOfWeek { get; set; } = 0;

    /// <summary>Hour of day (UTC) to run weekly stress tests.</summary>
    public int WeeklyRunHourUtc { get; set; } = 6;

    /// <summary>Whether automated weekly stress tests are enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Polling interval in seconds for the StressTestWorker.</summary>
    public int PollIntervalSeconds { get; set; } = 300;
}
