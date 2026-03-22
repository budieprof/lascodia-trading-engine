using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

/// <summary>
/// Default signal parameters shared across strategy evaluators.
/// Bound from the <c>StrategyEvaluatorOptions</c> section in appsettings.json.
/// </summary>
public class StrategyEvaluatorOptions : ConfigurationOption<StrategyEvaluatorOptions>
{
    /// <summary>Default lot size for generated trade signals. Defaults to 0.01.</summary>
    public decimal DefaultLotSize { get; set; } = 0.01m;

    /// <summary>Default confidence for breakout scalper signals. Defaults to 0.65.</summary>
    public decimal BreakoutConfidence { get; set; } = 0.65m;

    /// <summary>Signal expiry in minutes for breakout scalper. Defaults to 15.</summary>
    public int BreakoutExpiryMinutes { get; set; } = 15;

    /// <summary>Default confidence for MA crossover signals. Defaults to 0.70.</summary>
    public decimal MaCrossoverConfidence { get; set; } = 0.70m;

    /// <summary>Signal expiry in minutes for MA crossover. Defaults to 60.</summary>
    public int MaCrossoverExpiryMinutes { get; set; } = 60;

    /// <summary>Signal expiry in minutes for RSI reversion. Defaults to 30.</summary>
    public int RsiReversionExpiryMinutes { get; set; } = 30;

    // ── Bollinger Band Reversion ─────────────────────────────────────────

    /// <summary>Default confidence for Bollinger Band reversion signals. Defaults to 0.65.</summary>
    public decimal BollingerConfidence { get; set; } = 0.65m;

    /// <summary>Signal expiry in minutes for Bollinger Band reversion. Defaults to 45.</summary>
    public int BollingerExpiryMinutes { get; set; } = 45;

    // ── MACD Divergence ────────────────────────────────────────────────────

    /// <summary>Default confidence for MACD divergence signals. Defaults to 0.72.</summary>
    public decimal MacdDivergenceConfidence { get; set; } = 0.72m;

    /// <summary>Signal expiry in minutes for MACD divergence. Defaults to 60.</summary>
    public int MacdDivergenceExpiryMinutes { get; set; } = 60;

    // ── Session Breakout ───────────────────────────────────────────────────

    /// <summary>Default confidence for session breakout signals. Defaults to 0.68.</summary>
    public decimal SessionBreakoutConfidence { get; set; } = 0.68m;

    /// <summary>Signal expiry in minutes for session breakout. Defaults to 30.</summary>
    public int SessionBreakoutExpiryMinutes { get; set; } = 30;

    // ── Momentum Trend ─────────────────────────────────────────────────────

    /// <summary>Default confidence for momentum trend signals. Defaults to 0.70.</summary>
    public decimal MomentumTrendConfidence { get; set; } = 0.70m;

    /// <summary>Signal expiry in minutes for momentum trend. Defaults to 90.</summary>
    public int MomentumTrendExpiryMinutes { get; set; } = 90;

    // ── Stop-loss / take-profit ATR multipliers ────────────────────────────

    /// <summary>ATR multiplier for stop-loss distance. Defaults to 1.5.</summary>
    public decimal StopLossAtrMultiplier { get; set; } = 1.5m;

    /// <summary>ATR multiplier for take-profit distance. Defaults to 2.0.</summary>
    public decimal TakeProfitAtrMultiplier { get; set; } = 2.0m;

    /// <summary>ATR period used for SL/TP calculations. Defaults to 14.</summary>
    public int AtrPeriodForSlTp { get; set; } = 14;

    // ── Signal filter settings ─────────────────────────────────────────────

    /// <summary>Minutes before a high-impact news event to blackout trading. Defaults to 30.</summary>
    public int NewsBlackoutMinutesBefore { get; set; } = 30;

    /// <summary>Minutes after a high-impact news event to blackout trading. Defaults to 15.</summary>
    public int NewsBlackoutMinutesAfter { get; set; } = 15;

    /// <summary>
    /// Trading sessions allowed for signal generation.
    /// Defaults to London, LondonNYOverlap, NewYork. Empty list disables the filter.
    /// </summary>
    public List<string> AllowedSessions { get; set; } = ["London", "LondonNYOverlap", "NewYork"];

    /// <summary>
    /// Maximum open positions in a correlated currency group before new signals are blocked.
    /// Defaults to 3. Set to 0 to disable correlation checks.
    /// </summary>
    public int MaxCorrelatedPositions { get; set; } = 3;

    /// <summary>
    /// Whether to require multi-timeframe confirmation before emitting a signal.
    /// Defaults to true.
    /// </summary>
    public bool RequireMultiTimeframeConfirmation { get; set; } = true;

    /// <summary>
    /// Number of recent signal timestamps to feed the Hawkes burst filter.
    /// Defaults to 20. Set to 0 to disable.
    /// </summary>
    public int HawkesRecentSignalCount { get; set; } = 20;

    /// <summary>
    /// Minimum ML abstention score to allow a trade. Signals below this are suppressed.
    /// Defaults to 0.4. Set to 0 to disable.
    /// </summary>
    public decimal MinAbstentionScore { get; set; } = 0.4m;
}
