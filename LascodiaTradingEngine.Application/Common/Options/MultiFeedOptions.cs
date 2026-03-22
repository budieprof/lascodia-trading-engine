using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configuration for multi-feed market data management.
/// Bound from the <c>MultiFeedOptions</c> section in appsettings.json.
/// </summary>
public class MultiFeedOptions : ConfigurationOption<MultiFeedOptions>
{
    /// <summary>Seconds before a feed is considered stale. Defaults to 30.</summary>
    public int StalenessThresholdSeconds { get; set; } = 30;

    /// <summary>Milliseconds window for cross-validating ticks across feeds. Defaults to 500.</summary>
    public int CrossValidationWindowMs { get; set; } = 500;

    /// <summary>Maximum allowed mid-price divergence in pips between feeds. Defaults to 3.0.</summary>
    public double MaxMidPriceDivergencePips { get; set; } = 3.0;
}
