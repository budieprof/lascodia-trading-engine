using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Latency SLA targets for the critical trading path. Alerts when P99 breaches for consecutive minutes.</summary>
public class LatencySlaOptions : ConfigurationOption<LatencySlaOptions>
{
    /// <summary>P99 target in ms: tick → signal creation.</summary>
    public int TickToSignalP99Ms { get; set; } = 500;

    /// <summary>P99 target in ms: signal → Tier 1 validation.</summary>
    public int SignalToTier1P99Ms { get; set; } = 200;

    /// <summary>P99 target in ms: Tier 2 risk check.</summary>
    public int Tier2RiskCheckP99Ms { get; set; } = 100;

    /// <summary>P99 target in ms: EA poll → order submission.</summary>
    public int EaPollToSubmitP99Ms { get; set; } = 1000;

    /// <summary>P99 target in ms: total tick-to-fill.</summary>
    public int TotalTickToFillP99Ms { get; set; } = 3000;

    /// <summary>Consecutive minutes of SLA breach before alerting.</summary>
    public int ConsecutiveBreachMinutesBeforeAlert { get; set; } = 5;
}
