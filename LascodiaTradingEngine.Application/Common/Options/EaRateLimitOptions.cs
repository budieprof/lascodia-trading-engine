using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Per-EA-instance rate limits to prevent resource exhaustion from misbehaving or compromised EAs.</summary>
public class EaRateLimitOptions : ConfigurationOption<EaRateLimitOptions>
{
    /// <summary>Max tick batch requests per second per EA instance.</summary>
    public int TickBatchMaxPerSecond { get; set; } = 10;

    /// <summary>Max candle requests per second per EA instance.</summary>
    public int CandleMaxPerSecond { get; set; } = 2;

    /// <summary>Max heartbeat requests per 5 seconds per EA instance.</summary>
    public int HeartbeatMaxPer5Seconds { get; set; } = 1;

    /// <summary>Max snapshot requests per 30 seconds per EA instance.</summary>
    public int SnapshotMaxPer30Seconds { get; set; } = 1;

    /// <summary>Burst allowance multiplier above steady-state rate.</summary>
    public decimal BurstMultiplier { get; set; } = 2.0m;
}
