using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configuration for broker quality tracking.
/// Bound from the <c>BrokerQualityOptions</c> section in appsettings.json.
/// </summary>
public class BrokerQualityOptions : ConfigurationOption<BrokerQualityOptions>
{
    /// <summary>Number of recent fills to keep in the rolling window per broker/symbol/hour. Defaults to 100.</summary>
    public int WindowSize { get; set; } = 100;
}
