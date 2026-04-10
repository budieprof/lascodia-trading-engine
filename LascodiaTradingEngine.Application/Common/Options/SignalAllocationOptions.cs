using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>Configuration for multi-account signal allocation.</summary>
public class SignalAllocationOptions : ConfigurationOption<SignalAllocationOptions>
{
    /// <summary>Default allocation method: ProRataEquity, EqualRisk, or KellyOptimal.</summary>
    public string DefaultMethod { get; set; } = "ProRataEquity";

    /// <summary>Whether multi-account allocation is enabled. When false, signals are consumed individually.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Minimum account equity to be eligible for allocation.</summary>
    public decimal MinAccountEquity { get; set; } = 100m;

    /// <summary>Risk percentage per trade for the EqualRisk allocation method (default 1%).</summary>
    public decimal EqualRiskPercentage { get; set; } = 0.01m;
}
