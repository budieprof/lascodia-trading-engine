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

    /// <summary>
    /// Margin level percentage below which new trades are rejected.
    /// Protects against margin call by keeping a safety buffer. Defaults to 150%.
    /// </summary>
    public decimal MinMarginLevelPct { get; set; } = 150m;

    /// <summary>
    /// Slippage buffer multiplier applied to margin and risk calculations.
    /// 1.02 means a 2% buffer above the calculated requirement. Defaults to 1.02 (2%).
    /// </summary>
    public decimal SlippageBufferMultiplier { get; set; } = 1.02m;

    /// <summary>
    /// Maximum allowed spread in pips. Signals are rejected when the live spread exceeds this.
    /// Zero means no spread check is applied. Defaults to 0 (disabled).
    /// </summary>
    public decimal MaxSpreadPips { get; set; }

    /// <summary>
    /// Number of hours before the next known market close (Friday close or holiday)
    /// during which the weekend gap risk multiplier is applied.
    /// Defaults to 4 hours (trade placed within 4 hours of Friday market close).
    /// </summary>
    public int WeekendGapWindowHours { get; set; } = 4;

    /// <summary>
    /// Known market holiday dates (format: "MM-dd") when the gap risk multiplier should
    /// be applied. Common holidays: Christmas (12-25), New Year (01-01).
    /// The multiplier applies on the trading day before and on the holiday itself.
    /// </summary>
    public List<string> MarketHolidays { get; set; } = ["12-25", "01-01"];

    /// <summary>
    /// Multiplier applied to the broker's stop-out level to derive a safety floor for the
    /// margin level check. For example, if the broker stops out at 50% and this is 2.0,
    /// the effective floor is 100%. The engine uses the higher of <see cref="MinMarginLevelPct"/>
    /// and (broker stop-out × this multiplier). Defaults to 2.0.
    /// </summary>
    public decimal StopOutBufferMultiplier { get; set; } = 2.0m;

    /// <summary>
    /// Minutes after the last losing trade before the consecutive loss gate automatically resets.
    /// Prevents a permanent deadlock where the gate blocks all trading and no winning trade can
    /// break the streak. Zero means the gate never auto-resets (only a winning trade resets it).
    /// Defaults to 60 minutes.
    /// </summary>
    public int ConsecutiveLossCooldownMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum |correlation| between two symbols for them to be considered correlated.
    /// Used by Tier 2 of CountCorrelatedPositions when CorrelationMatrixWorker data is available.
    /// Defaults to 0.75.
    /// </summary>
    public decimal CorrelationThreshold { get; set; } = 0.75m;

    /// <summary>
    /// Drawdown percentage threshold at which the engine enters Reduced recovery mode
    /// (lot sizes halved). Defaults to 10%.
    /// </summary>
    public decimal ReducedDrawdownPct { get; set; } = 10m;

    /// <summary>
    /// Drawdown percentage threshold at which the engine enters Halted recovery mode
    /// (all trading paused). Must be greater than <see cref="ReducedDrawdownPct"/>.
    /// Defaults to 20%.
    /// </summary>
    public decimal HaltedDrawdownPct { get; set; } = 20m;
}
