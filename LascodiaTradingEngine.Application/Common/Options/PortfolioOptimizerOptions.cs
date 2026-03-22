using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Portfolio optimization parameters for position sizing.
/// Bound from the <c>PortfolioOptimizerOptions</c> section in appsettings.json.
/// </summary>
public class PortfolioOptimizerOptions : ConfigurationOption<PortfolioOptimizerOptions>
{
    /// <summary>Maximum Kelly fraction across all positions. Defaults to 0.25 (25%).</summary>
    public double MaxPortfolioKelly { get; set; } = 0.25;

    /// <summary>Maximum Kelly fraction per individual trade. Defaults to 0.05 (5%).</summary>
    public double MaxPerTradeKelly { get; set; } = 0.05;

    /// <summary>Maximum allocation per correlation group. Defaults to 0.10 (10%).</summary>
    public double CorrelationGroupCap { get; set; } = 0.10;

    /// <summary>Minimum lot size for a trade to be placed. Defaults to 0.01.</summary>
    public double MinLotSize { get; set; } = 0.01;
}
