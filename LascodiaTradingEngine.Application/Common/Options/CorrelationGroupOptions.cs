using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configurable currency pair correlation groups used by portfolio risk checks.
/// Bound from the <c>CorrelationGroupOptions</c> section in appsettings.json.
/// </summary>
public class CorrelationGroupOptions : ConfigurationOption<CorrelationGroupOptions>
{
    /// <summary>
    /// Array of correlation groups. Each group is an array of symbol strings that are
    /// considered correlated. Defaults to standard forex correlation groups if empty.
    /// </summary>
    public string[][] Groups { get; set; } =
    [
        ["EURUSD", "GBPUSD", "AUDUSD", "NZDUSD"],
        ["USDCHF", "USDJPY", "USDCAD"],
        ["EURJPY", "GBPJPY", "AUDJPY"],
    ];
}
