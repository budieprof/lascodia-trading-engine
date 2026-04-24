using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Shared trading-day boundary settings used by daily loss, drawdown, and attribution logic.
/// </summary>
public class TradingDayOptions : ConfigurationOption<TradingDayOptions>
{
    /// <summary>
    /// Trading-day rollover minute-of-day in UTC.
    /// 0 = 00:00 UTC, 1320 = 22:00 UTC.
    /// </summary>
    public int RolloverMinuteOfDayUtc { get; set; } = 0;

    /// <summary>
    /// Maximum distance, in minutes, between the configured rollover boundary and a broker
    /// snapshot that may be used as a fallback start-of-day equity baseline.
    /// </summary>
    public int BrokerSnapshotBoundaryToleranceMinutes { get; set; } = 180;
}
