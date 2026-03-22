using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Configurable thresholds for the risk checker.
/// Bound from the <c>RiskCheckerOptions</c> section in appsettings.json.
/// </summary>
public class RiskCheckerOptions : ConfigurationOption<RiskCheckerOptions>
{
    /// <summary>
    /// Minimum signal confidence required when the ML model disagrees with the strategy direction.
    /// Signals below this confidence with ML disagreement are rejected. Defaults to 0.70 (70%).
    /// </summary>
    public decimal MLDisagreementMinConfidence { get; set; } = 0.70m;
}
