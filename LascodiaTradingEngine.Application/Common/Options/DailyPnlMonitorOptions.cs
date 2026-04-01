using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for the daily P&amp;L monitor worker that triggers emergency flatten on excessive losses.</summary>
public class DailyPnlMonitorOptions : ConfigurationOption<DailyPnlMonitorOptions>
{
    /// <summary>Polling interval in seconds. Default: 30.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Whether to dispatch EmergencyFlattenCommand when daily loss limit is breached. Default: true.</summary>
    public bool EmergencyFlattenEnabled { get; set; } = true;
}
