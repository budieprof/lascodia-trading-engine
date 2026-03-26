using Lascodia.Trading.Engine.SharedApplication.Common.Models;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using LascodiaTradingEngine.Domain.Enums;

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

    // ── Breakout Scalper filters ────────────────────────────────────────

    /// <summary>
    /// Minimum ADX value required for a breakout signal to fire.
    /// ADX below this threshold indicates a ranging market where breakouts are prone to failing.
    /// Defaults to 0 (disabled). Typical production value: 20–25.
    /// </summary>
    public decimal BreakoutMinAdx { get; set; } = 0m;

    /// <summary>ADX period for the breakout trend-strength filter. Defaults to 14.</summary>
    public int BreakoutAdxPeriod { get; set; } = 14;

    /// <summary>
    /// Maximum spread as a fraction of ATR allowed for a breakout signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal BreakoutMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the breakout to fire.
    /// Breakouts without volume participation are often false. Defaults to 0 (disabled).
    /// </summary>
    public decimal BreakoutMinVolume { get; set; } = 0m;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Signals that cannot achieve this R:R are rejected. Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal BreakoutMinRiskRewardRatio { get; set; } = 1.0m;

    /// <summary>
    /// Maximum gap (|Open - PreviousClose|) as a fraction of ATR allowed on the signal bar.
    /// Large gaps distort the N-bar range and ATR calculations.
    /// Defaults to 2.0 (200% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal BreakoutMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries are shifted up, sell entries are shifted down.
    /// Defaults to 0 (disabled). Typical value: 0.05–0.15.
    /// </summary>
    public decimal BreakoutSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// Maximum confidence boost from breakout depth — how far price has exceeded the N-bar
    /// high/low level, normalised by ATR. A deep breach signals stronger conviction.
    /// Defaults to 0.2 (up to +20% above base <see cref="BreakoutConfidence"/>).
    /// </summary>
    public decimal BreakoutConfidenceBreachBoostMax { get; set; } = 0.2m;

    /// <summary>
    /// Period for the trend-alignment EMA filter. When > 0, bullish breakouts are only
    /// allowed when close > EMA(period), and bearish when close &lt; EMA(period).
    /// Breakouts against the macro trend have significantly lower win rates.
    /// Defaults to 0 (disabled). Typical value: 100–200.
    /// </summary>
    public int BreakoutTrendMaPeriod { get; set; } = 0;

    /// <summary>RSI period for the breakout overbought/oversold filter. Defaults to 14.</summary>
    public int BreakoutRsiPeriod { get; set; } = 14;

    /// <summary>
    /// Maximum RSI allowed for a bullish breakout signal. Prevents buying into an exhausted,
    /// overbought move. Defaults to 0 (disabled). Typical value: 70.
    /// </summary>
    public decimal BreakoutMaxRsiForBuy { get; set; } = 0m;

    /// <summary>
    /// Minimum RSI allowed for a bearish breakout signal. Prevents selling into an exhausted,
    /// oversold move. Defaults to 0 (disabled). Typical value: 30.
    /// </summary>
    public decimal BreakoutMinRsiForSell { get; set; } = 0m;

    /// <summary>
    /// Number of consecutive closed bars where price must remain on the breakout side of the
    /// N-bar level before the signal fires. Eliminates snap-back false breakouts that poke
    /// above the level and immediately reverse. Defaults to 0 (fires on the breakout bar).
    /// Typical value: 1–2.
    /// </summary>
    public int BreakoutConfirmationBars { get; set; } = 0;

    /// <summary>Default confidence for MA crossover signals. Defaults to 0.70.</summary>
    public decimal MaCrossoverConfidence { get; set; } = 0.70m;

    /// <summary>Signal expiry in minutes for MA crossover. Defaults to 60.</summary>
    public int MaCrossoverExpiryMinutes { get; set; } = 60;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Accounts for expected slippage between signal generation and fill.
    /// Buy entries are shifted up, sell entries are shifted down.
    /// Defaults to 0 (disabled). Typical value: 0.05–0.15.
    /// </summary>
    public decimal MaCrossoverSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// Minimum crossover magnitude as a fraction of ATR required to trigger a signal.
    /// Filters out noise crossovers where the fast and slow MAs barely diverge.
    /// Defaults to 0.1 (10% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverMinMagnitudeAtrFraction { get; set; } = 0.1m;

    /// <summary>
    /// Minimum ADX value required for an MA crossover signal to fire.
    /// ADX below this threshold indicates a ranging/choppy market where crossovers whipsaw.
    /// Defaults to 20. Set to 0 to disable the ADX filter.
    /// </summary>
    public decimal MaCrossoverMinAdx { get; set; } = 20m;

    /// <summary>
    /// ADX period for the MA crossover trend-strength filter. Defaults to 14.
    /// </summary>
    public int MaCrossoverAdxPeriod { get; set; } = 14;

    /// <summary>
    /// Maximum number of crossovers (fast crossing slow in either direction) within the
    /// lookback window before the signal is suppressed as choppy. Defaults to 3.
    /// Set to 0 to disable whipsaw detection.
    /// </summary>
    public int MaCrossoverMaxRecentCrossovers { get; set; } = 3;

    /// <summary>
    /// Number of bars to look back when counting recent crossovers for whipsaw detection.
    /// Defaults to 20.
    /// </summary>
    public int MaCrossoverWhipsawLookbackBars { get; set; } = 20;

    /// <summary>
    /// Number of consecutive bars after the crossover during which the fast MA must
    /// remain on the crossed side of the slow MA. If the MAs revert within this window,
    /// the signal is rejected. Defaults to 0 (disabled — signal fires on the cross bar).
    /// Typical value: 1–3.
    /// </summary>
    public int MaCrossoverConfirmationBars { get; set; } = 0;

    /// <summary>
    /// Maximum spread as a fraction of ATR allowed for an MA crossover signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the MA crossover to fire.
    /// Filters out signals during thin/illiquid markets. Defaults to 0 (disabled).
    /// </summary>
    public decimal MaCrossoverMinVolume { get; set; } = 0m;

    // ── MA Crossover confidence weights ──────────────────────────────────

    /// <summary>
    /// Confidence weight for crossover magnitude factor.
    /// Larger MA separation after cross = stronger momentum. Defaults to 0.35.
    /// </summary>
    public decimal MaCrossoverWeightMagnitude { get; set; } = 0.35m;

    /// <summary>
    /// Confidence weight for trend alignment factor.
    /// Price distance above/below trend MA normalised by ATR. Defaults to 0.30.
    /// </summary>
    public decimal MaCrossoverWeightTrend { get; set; } = 0.30m;

    /// <summary>
    /// Confidence weight for whipsaw penalty factor.
    /// Fewer recent crossovers = cleaner trend = higher score. Defaults to 0.20.
    /// </summary>
    public decimal MaCrossoverWeightWhipsaw { get; set; } = 0.20m;

    /// <summary>
    /// Confidence weight for candle body-to-wick ratio factor.
    /// Strong body with small wicks shows conviction. Defaults to 0.10.
    /// </summary>
    public decimal MaCrossoverWeightCandleBody { get; set; } = 0.10m;

    /// <summary>
    /// Confidence weight for candle pattern confirmation factor (engulfing, pin bar).
    /// Confirmatory patterns boost confidence; absence is neutral. Defaults to 0.15.
    /// </summary>
    public decimal MaCrossoverWeightCandlePattern { get; set; } = 0.15m;

    // ── MA Crossover gap detection ────────────────────────────────────

    /// <summary>
    /// Maximum gap (|Open - PreviousClose|) as a fraction of ATR allowed on the signal bar.
    /// Large gaps distort ATR and crossover magnitude calculations.
    /// Defaults to 2.0 (200% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverMaxGapAtrFraction { get; set; } = 2.0m;

    // ── MA Crossover deadband ───────────────────────────────────────────

    /// <summary>
    /// Minimum previous-bar MA separation (|FastMA - SlowMA|) as a fraction of ATR
    /// required before a crossover is recognised. Prevents noise-triggered crosses
    /// where the MAs are nearly equal and tiny fluctuations flip the relationship.
    /// Defaults to 0.02 (2% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverDeadbandAtrFraction { get; set; } = 0.02m;

    // ── MA Crossover minimum risk-reward ratio ──────────────────────────

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance) after all
    /// SL/TP adjustments (ATR, dynamic, swing). If the final R:R falls below this
    /// threshold the signal is rejected. Prevents swing overrides from creating
    /// inverted or poor risk-reward setups. Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverMinRiskRewardRatio { get; set; } = 1.0m;

    // ── MA Crossover RSI confirmation ──────────────────────────────────

    /// <summary>
    /// RSI period for the MA crossover momentum filter. Defaults to 14.
    /// </summary>
    public int MaCrossoverRsiPeriod { get; set; } = 14;

    /// <summary>
    /// Maximum RSI allowed for a bullish crossover signal. Prevents buying into
    /// overbought conditions. Defaults to 0 (disabled). Typical value: 70.
    /// </summary>
    public decimal MaCrossoverMaxRsiForBuy { get; set; } = 0m;

    /// <summary>
    /// Minimum RSI allowed for a bearish crossover signal. Prevents selling into
    /// oversold conditions. Defaults to 0 (disabled). Typical value: 30.
    /// </summary>
    public decimal MaCrossoverMinRsiForSell { get; set; } = 0m;

    // ── MA Crossover dynamic SL/TP ─────────────────────────────────────

    /// <summary>
    /// When enabled, SL and TP ATR multipliers are scaled by ADX strength.
    /// Stronger trends get tighter stops and wider targets. Defaults to false.
    /// </summary>
    public bool MaCrossoverDynamicSlTp { get; set; } = false;

    /// <summary>
    /// ADX value considered a "strong trend". At this level, SL/TP scales reach
    /// their full strong-trend values. Defaults to 40.
    /// </summary>
    public decimal MaCrossoverStrongAdxThreshold { get; set; } = 40m;

    /// <summary>
    /// SL multiplier scale factor in a strong trend (ADX at threshold).
    /// Values below 1.0 tighten the stop. Defaults to 0.8 (20% tighter).
    /// </summary>
    public decimal MaCrossoverStrongTrendSlScale { get; set; } = 0.8m;

    /// <summary>
    /// TP multiplier scale factor in a strong trend (ADX at threshold).
    /// Values above 1.0 widen the target. Defaults to 1.3 (30% wider).
    /// </summary>
    public decimal MaCrossoverStrongTrendTpScale { get; set; } = 1.3m;

    // ── MA Crossover swing-structure SL/TP ─────────────────────────────

    /// <summary>
    /// ATR fraction used as a buffer beyond swing levels for SL and TP placement.
    /// SL is pushed slightly past the swing to avoid stop-hunting; TP is pushed
    /// slightly past structure to account for spread/slippage.
    /// Defaults to 0.1 (10% of ATR). Set to 0 to disable the buffer.
    /// </summary>
    public decimal MaCrossoverSwingBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>
    /// When enabled, stop-loss is placed at the nearest swing low (buy) or swing high (sell)
    /// within a lookback window, clamped by ATR min/max bounds. Defaults to false.
    /// </summary>
    public bool MaCrossoverSwingSlEnabled { get; set; } = false;

    /// <summary>
    /// Number of bars to search for the swing low/high. Defaults to 10.
    /// </summary>
    public int MaCrossoverSwingSlLookbackBars { get; set; } = 10;

    /// <summary>
    /// Minimum SL distance as an ATR multiplier when using swing-structure SL.
    /// Prevents stops that are too tight. Defaults to 0.8.
    /// </summary>
    public decimal MaCrossoverSwingSlMinAtrMultiplier { get; set; } = 0.8m;

    /// <summary>
    /// Maximum SL distance as an ATR multiplier when using swing-structure SL.
    /// Prevents stops that are too wide. Defaults to 2.5.
    /// </summary>
    public decimal MaCrossoverSwingSlMaxAtrMultiplier { get; set; } = 2.5m;

    // ── MA Crossover RSI momentum confidence ───────────────────────────

    /// <summary>
    /// Confidence weight for RSI momentum alignment factor.
    /// Rewards RSI near the ideal zone (55-60 for buys, 40-45 for sells) and penalises
    /// overextended or counter-momentum RSI. Defaults to 0.10. Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverWeightRsiMomentum { get; set; } = 0.10m;

    // ── MA Crossover confidence-based lot sizing ───────────────────────

    /// <summary>
    /// When enabled, lot size is scaled quadratically between <see cref="MaCrossoverMinLotSize"/>
    /// and <see cref="MaCrossoverMaxLotSize"/> based on the signal's confidence score.
    /// Defaults to false (uses <see cref="DefaultLotSize"/>).
    /// </summary>
    public bool MaCrossoverConfidenceLotSizing { get; set; } = false;

    /// <summary>
    /// Minimum lot size when confidence-based sizing is enabled. Maps to confidence 0.1.
    /// Defaults to 0.01.
    /// </summary>
    public decimal MaCrossoverMinLotSize { get; set; } = 0.01m;

    /// <summary>
    /// Maximum lot size when confidence-based sizing is enabled. Maps to confidence 1.0.
    /// Defaults to 0.10.
    /// </summary>
    public decimal MaCrossoverMaxLotSize { get; set; } = 0.10m;

    // ── MA Crossover swing-structure take-profit ───────────────────────

    /// <summary>
    /// When enabled, take-profit targets the nearest resistance (buys) or support (sells)
    /// within a lookback window, clamped by ATR min/max bounds. Defaults to false.
    /// </summary>
    public bool MaCrossoverSwingTpEnabled { get; set; } = false;

    /// <summary>
    /// Number of bars to search for the swing high (buy TP) or swing low (sell TP).
    /// Defaults to 20 (wider than SL lookback to find meaningful structure).
    /// </summary>
    public int MaCrossoverSwingTpLookbackBars { get; set; } = 20;

    /// <summary>
    /// Minimum TP distance as an ATR multiplier when using swing-structure TP.
    /// Prevents targets that are too close. Defaults to 1.5.
    /// </summary>
    public decimal MaCrossoverSwingTpMinAtrMultiplier { get; set; } = 1.5m;

    /// <summary>
    /// Maximum TP distance as an ATR multiplier when using swing-structure TP.
    /// Prevents targets that are unrealistically far. Defaults to 4.0.
    /// </summary>
    public decimal MaCrossoverSwingTpMaxAtrMultiplier { get; set; } = 4.0m;

    // ── MA Crossover ADX confidence weight ─────────────────────────────

    /// <summary>
    /// Confidence weight for ADX trend-strength factor.
    /// Stronger trends (higher ADX) boost confidence with diminishing returns.
    /// Defaults to 0.10. Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverWeightAdxStrength { get; set; } = 0.10m;

    // ── MA Crossover volume confidence weight ──────────────────────────

    /// <summary>
    /// Confidence weight for volume-relative factor.
    /// Signal bar volume compared to recent average — higher relative volume = higher confidence.
    /// Defaults to 0.10. Set to 0 to disable.
    /// </summary>
    public decimal MaCrossoverWeightVolume { get; set; } = 0.10m;

    /// <summary>
    /// Number of bars used to compute average volume for the volume confidence factor.
    /// Defaults to 20.
    /// </summary>
    public int MaCrossoverVolumeLookbackBars { get; set; } = 20;

    /// <summary>Signal expiry in minutes for RSI reversion. Defaults to 30.</summary>
    public int RsiReversionExpiryMinutes { get; set; } = 30;

    /// <summary>Default confidence for RSI reversion signals. Defaults to 0.65.</summary>
    public decimal RsiReversionConfidence { get; set; } = 0.65m;

    // ── RSI Reversion filters ─────────────────────────────────────────────

    /// <summary>
    /// Maximum spread (Ask − Bid) as a fraction of ATR allowed for an RSI reversion signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal RsiReversionMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Maximum gap (|Open − PreviousClose|) as a fraction of ATR on the signal bar.
    /// Large gaps distort RSI and ATR calculations. Defaults to 2.0. Set to 0 to disable.
    /// </summary>
    public decimal RsiReversionMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the signal to fire.
    /// Filters out signals in thin/illiquid markets. Defaults to 0 (disabled).
    /// </summary>
    public decimal RsiReversionMinVolume { get; set; } = 0m;

    /// <summary>
    /// When true, requires a pin bar or engulfing candle on the signal bar to confirm reversal.
    /// Increases signal precision at the cost of fewer entries. Defaults to false.
    /// </summary>
    public bool RsiReversionRequireCandleConfirmation { get; set; } = false;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries are shifted up, sell entries are shifted down. Defaults to 0 (disabled).
    /// </summary>
    public decimal RsiReversionSlippageAtrFraction { get; set; } = 0m;

    // ── RSI Reversion RSI divergence filter ────────────────────────────────

    /// <summary>
    /// When enabled, requires RSI to form a divergence with price at the oversold/overbought zone.
    /// Bullish: price makes a lower low but RSI makes a higher low (stronger reversal setup).
    /// Defaults to false.
    /// </summary>
    public bool RsiReversionRequireDivergence { get; set; } = false;

    /// <summary>
    /// Number of bars to look back for a prior RSI swing to compare for divergence.
    /// Defaults to 20.
    /// </summary>
    public int RsiReversionDivergenceLookbackBars { get; set; } = 20;

    // ── RSI Reversion stop-loss ────────────────────────────────────────────

    /// <summary>
    /// When true, stop-loss is placed at the nearest swing low (buy) or swing high (sell)
    /// within the lookback window, with an ATR buffer, clamped by min/max multipliers.
    /// Defaults to false (uses ATR multiplier directly).
    /// </summary>
    public bool RsiReversionSwingSlEnabled { get; set; } = false;

    /// <summary>Number of bars to search for the swing point for SL placement. Defaults to 10.</summary>
    public int RsiReversionSwingSlLookbackBars { get; set; } = 10;

    /// <summary>ATR fraction buffer beyond the swing point for SL placement. Defaults to 0.1.</summary>
    public decimal RsiReversionSwingSlBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>Minimum SL distance as ATR multiplier when using swing SL. Defaults to 0.5.</summary>
    public decimal RsiReversionSwingSlMinAtrMultiplier { get; set; } = 0.5m;

    /// <summary>Maximum SL distance as ATR multiplier when using swing SL. Defaults to 2.0.</summary>
    public decimal RsiReversionSwingSlMaxAtrMultiplier { get; set; } = 2.0m;

    // ── RSI Reversion take-profit ──────────────────────────────────────────

    /// <summary>
    /// When true, take-profit targets the RSI midline (50) equivalent price level —
    /// estimated as the SMA of the RSI period. Natural mean-reversion destination.
    /// Minimum TP is floored at half the default ATR TP. Defaults to false.
    /// </summary>
    public bool RsiReversionMidlineTpEnabled { get; set; } = false;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal RsiReversionMinRiskRewardRatio { get; set; } = 1.0m;

    // ── RSI Reversion confidence weights ───────────────────────────────────

    /// <summary>
    /// Confidence weight for RSI depth (how far RSI penetrated oversold/overbought zone).
    /// Deeper penetration = stronger exhaustion signal. Defaults to 0.40.
    /// </summary>
    public decimal RsiReversionWeightDepth { get; set; } = 0.40m;

    /// <summary>
    /// Confidence weight for reversal candle pattern (engulfing, pin bar) on the signal bar.
    /// Defaults to 0 (disabled). Set when your data source reliably produces meaningful OHLC patterns.
    /// </summary>
    public decimal RsiReversionWeightCandle { get; set; } = 0m;

    /// <summary>
    /// Confidence weight for volume relative to the recent average.
    /// Defaults to 0.15. Set to 0 to exclude volume from the confidence calculation.
    /// </summary>
    public decimal RsiReversionWeightVolume { get; set; } = 0.15m;

    /// <summary>Number of bars for average volume calculation in the RSI reversion confidence factor. Defaults to 20.</summary>
    public int RsiReversionVolumeLookbackBars { get; set; } = 20;

    /// <summary>
    /// Confidence weight for RSI recovery speed (how quickly RSI crossed back through the threshold).
    /// A sharp bounce signals stronger conviction. Defaults to 0.20.
    /// </summary>
    public decimal RsiReversionWeightRecoverySpeed { get; set; } = 0.20m;

    /// <summary>
    /// Controls how much the multi-factor score shifts the final confidence above or below the
    /// base <see cref="RsiReversionConfidence"/>. Final confidence = base + (score − 0.5) × sensitivity.
    /// Maximum swing = ±(sensitivity / 2). Defaults to 0.30 (±0.15 around base).
    /// </summary>
    public decimal RsiReversionConfidenceSensitivity { get; set; } = 0.30m;

    // ── RSI Reversion lot sizing ───────────────────────────────────────────

    /// <summary>
    /// When true, lot size is scaled between min and max based on confidence.
    /// Defaults to false (uses DefaultLotSize).
    /// </summary>
    public bool RsiReversionConfidenceLotSizing { get; set; } = false;

    /// <summary>Minimum lot size for confidence-based lot sizing. Defaults to 0.01.</summary>
    public decimal RsiReversionMinLotSize { get; set; } = 0.01m;

    /// <summary>Maximum lot size for confidence-based lot sizing. Defaults to 0.10.</summary>
    public decimal RsiReversionMaxLotSize { get; set; } = 0.10m;

    // ── Bollinger Band Reversion ─────────────────────────────────────────

    /// <summary>Default confidence for Bollinger Band reversion signals. Defaults to 0.65.</summary>
    public decimal BollingerConfidence { get; set; } = 0.65m;

    /// <summary>Signal expiry in minutes for Bollinger Band reversion. Defaults to 45.</summary>
    public int BollingerExpiryMinutes { get; set; } = 45;

    /// <summary>
    /// Number of bars to look back when detecting a bandwidth squeeze.
    /// 1 (default) compares current bandwidth to the immediately previous bar — the original behaviour.
    /// Values &gt; 1 compare current bandwidth to N bars ago, catching gradual multi-bar squeezes
    /// that are invisible to a one-bar comparison (e.g., 3–5 bars of steady contraction).
    /// </summary>
    public int BollingerSqueezeLookbackBars { get; set; } = 1;

    /// <summary>
    /// Maximum spread (Ask − Bid) as a fraction of ATR allowed for a Bollinger Band signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal BollingerMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Maximum gap (|Open − PreviousClose|) as a fraction of ATR on the signal bar.
    /// Large gaps distort band calculations and entry quality. Defaults to 2.0. Set to 0 to disable.
    /// </summary>
    public decimal BollingerMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the signal to fire.
    /// Filters out signals in thin/illiquid markets. Defaults to 0 (disabled).
    /// </summary>
    public decimal BollingerMinVolume { get; set; } = 0m;

    /// <summary>
    /// Maximum RSI for a buy signal. Prevents buying when momentum is already overbought
    /// even when price touches the lower band. Defaults to 0 (disabled). Typical value: 65.
    /// </summary>
    public decimal BollingerMaxRsiForBuy { get; set; } = 0m;

    /// <summary>
    /// Minimum RSI for a sell signal. Prevents selling when momentum is already oversold
    /// even when price touches the upper band. Defaults to 0 (disabled). Typical value: 35.
    /// </summary>
    public decimal BollingerMinRsiForSell { get; set; } = 0m;

    /// <summary>RSI period for the Bollinger Band RSI filter and confidence factor. Defaults to 14.</summary>
    public int BollingerRsiPeriod { get; set; } = 14;

    /// <summary>
    /// Minimum Bollinger Band width as a fraction of ATR.
    /// Prevents entries when the bands are too narrow for a meaningful reversion trade.
    /// Defaults to 0.5. Set to 0 to disable.
    /// </summary>
    public decimal BollingerMinBandwidthAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal BollingerMinRiskRewardRatio { get; set; } = 1.0m;

    /// <summary>
    /// When true, requires a pin bar or engulfing candle on the signal bar to confirm reversal.
    /// Increases signal precision at the cost of fewer entries. Defaults to false.
    /// </summary>
    public bool BollingerRequireCandleConfirmation { get; set; } = false;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries are shifted up, sell entries are shifted down. Defaults to 0 (disabled).
    /// </summary>
    public decimal BollingerSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// When true, stop-loss is placed at the nearest swing low (buy) or swing high (sell)
    /// within the lookback window, with an ATR buffer, clamped by min/max multipliers.
    /// Defaults to false (uses ATR multiplier directly).
    /// </summary>
    public bool BollingerSwingSlEnabled { get; set; } = false;

    /// <summary>Number of bars to search for the swing point for SL placement. Defaults to 10.</summary>
    public int BollingerSwingSlLookbackBars { get; set; } = 10;

    /// <summary>ATR fraction buffer beyond the swing point for SL placement. Defaults to 0.1.</summary>
    public decimal BollingerSwingSlBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>Minimum SL distance as ATR multiplier when using swing SL. Defaults to 0.5.</summary>
    public decimal BollingerSwingSlMinAtrMultiplier { get; set; } = 0.5m;

    /// <summary>Maximum SL distance as ATR multiplier when using swing SL. Defaults to 2.0.</summary>
    public decimal BollingerSwingSlMaxAtrMultiplier { get; set; } = 2.0m;

    /// <summary>
    /// When true, take-profit targets the Bollinger Band middle line (SMA) — the natural
    /// mean-reversion destination. Minimum TP is floored at half the default ATR TP.
    /// Defaults to false (uses ATR multiplier directly).
    /// </summary>
    public bool BollingerMidBandTpEnabled { get; set; } = false;

    // ── Bollinger Band confidence weights ─────────────────────────────────

    /// <summary>
    /// Confidence weight for band-touch depth (how far price breached the band, normalised by bandwidth).
    /// Deeper touch = stronger exhaustion signal. Defaults to 0.40.
    /// </summary>
    public decimal BollingerWeightDepth { get; set; } = 0.40m;

    /// <summary>
    /// Confidence weight for reversal candle pattern (engulfing, pin bar) on the signal bar.
    /// Defaults to 0 (disabled) because plain candles always score 0.5 (neutral), making
    /// a non-zero weight a constant drag toward the midpoint rather than a real signal.
    /// Set this when your data source reliably produces meaningful OHLC patterns.
    /// </summary>
    public decimal BollingerWeightCandle { get; set; } = 0m;

    /// <summary>
    /// Confidence weight for RSI alignment (oversold for buy, overbought for sell).
    /// Defaults to 0.20. Set to 0 to exclude RSI from the confidence calculation.
    /// </summary>
    public decimal BollingerWeightRsi { get; set; } = 0.20m;

    /// <summary>
    /// Confidence weight for volume relative to the recent average.
    /// Defaults to 0.15. Set to 0 to exclude volume from the confidence calculation.
    /// </summary>
    public decimal BollingerWeightVolume { get; set; } = 0.15m;

    /// <summary>Number of bars for average volume calculation in the Bollinger confidence factor. Defaults to 20.</summary>
    public int BollingerVolumeLookbackBars { get; set; } = 20;

    /// <summary>
    /// Controls how much the multi-factor score shifts the final confidence above or below the
    /// base <see cref="BollingerConfidence"/>. Final confidence = BollingerConfidence + (score − 0.5) × sensitivity.
    /// Maximum swing = ±(sensitivity / 2). Defaults to 0.30 (±0.15 around base).
    /// </summary>
    public decimal BollingerConfidenceSensitivity { get; set; } = 0.30m;

    // ── Bollinger Band lot sizing ─────────────────────────────────────────

    /// <summary>
    /// When true, lot size is scaled between min and max based on confidence.
    /// Defaults to false (uses DefaultLotSize).
    /// </summary>
    public bool BollingerConfidenceLotSizing { get; set; } = false;

    /// <summary>Minimum lot size for confidence-based lot sizing. Defaults to 0.01.</summary>
    public decimal BollingerMinLotSize { get; set; } = 0.01m;

    /// <summary>Maximum lot size for confidence-based lot sizing. Defaults to 0.10.</summary>
    public decimal BollingerMaxLotSize { get; set; } = 0.10m;

    // ── MACD Divergence ────────────────────────────────────────────────────

    /// <summary>Default confidence for MACD divergence signals. Defaults to 0.72.</summary>
    public decimal MacdDivergenceConfidence { get; set; } = 0.72m;

    /// <summary>Signal expiry in minutes for MACD divergence. Defaults to 60.</summary>
    public int MacdDivergenceExpiryMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum ADX value required for a MACD divergence signal to fire.
    /// Very low ADX indicates a ranging market where divergence is unreliable.
    /// Defaults to 15. Set to 0 to disable.
    /// </summary>
    public decimal MacdDivergenceMinAdx { get; set; } = 15m;

    /// <summary>ADX period for the MACD divergence trend filter. Defaults to 14.</summary>
    public int MacdDivergenceAdxPeriod { get; set; } = 14;

    /// <summary>
    /// Maximum spread as a fraction of ATR allowed for a MACD divergence signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal MacdDivergenceMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the MACD divergence signal to fire.
    /// Filters out signals during thin/illiquid markets. Defaults to 0 (disabled).
    /// </summary>
    public decimal MacdDivergenceMinVolume { get; set; } = 0m;

    /// <summary>
    /// Maximum RSI for a bullish divergence signal. Prevents buying into overbought conditions
    /// even when divergence is detected. Defaults to 0 (disabled). Typical value: 70.
    /// </summary>
    public decimal MacdDivergenceMaxRsiForBuy { get; set; } = 0m;

    /// <summary>
    /// Minimum RSI for a bearish divergence signal. Prevents selling into oversold conditions.
    /// Defaults to 0 (disabled). Typical value: 30.
    /// </summary>
    public decimal MacdDivergenceMinRsiForSell { get; set; } = 0m;

    /// <summary>RSI period for the MACD divergence momentum filter. Defaults to 14.</summary>
    public int MacdDivergenceRsiPeriod { get; set; } = 14;

    /// <summary>
    /// Requires the MACD histogram to be turning in the signal direction on the current bar.
    /// Bullish: histogram[last] > histogram[last-1]. Bearish: histogram[last] &lt; histogram[last-1].
    /// Defaults to true.
    /// </summary>
    public bool MacdDivergenceRequireHistogramTurn { get; set; } = true;

    /// <summary>
    /// Maximum gap (|Open - PreviousClose|) as a fraction of ATR on the signal bar.
    /// Large gaps distort MACD calculations. Defaults to 2.0. Set to 0 to disable.
    /// </summary>
    public decimal MacdDivergenceMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries shifted up, sell entries shifted down. Defaults to 0 (disabled).
    /// </summary>
    public decimal MacdDivergenceSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal MacdDivergenceMinRiskRewardRatio { get; set; } = 1.0m;

    /// <summary>
    /// When enabled, stop-loss is placed at the divergence swing point (the prior
    /// swing low for buys, swing high for sells) with an ATR buffer, clamped by bounds.
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceSwingSlEnabled { get; set; } = false;

    /// <summary>
    /// ATR fraction buffer beyond the swing point for SL placement. Defaults to 0.1.
    /// </summary>
    public decimal MacdDivergenceSwingSlBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>
    /// Minimum SL distance as ATR multiplier when using swing SL. Defaults to 0.8.
    /// </summary>
    public decimal MacdDivergenceSwingSlMinAtrMultiplier { get; set; } = 0.8m;

    /// <summary>
    /// Maximum SL distance as ATR multiplier when using swing SL. Defaults to 2.5.
    /// </summary>
    public decimal MacdDivergenceSwingSlMaxAtrMultiplier { get; set; } = 2.5m;

    /// <summary>
    /// When enabled, take-profit targets the nearest swing high (buys) or swing low (sells)
    /// within a lookback window, clamped by ATR min/max bounds. Defaults to false.
    /// </summary>
    public bool MacdDivergenceSwingTpEnabled { get; set; } = false;

    /// <summary>
    /// Number of bars to search for the swing high (buy TP) or swing low (sell TP).
    /// Defaults to 20 (wider than SL lookback to find meaningful structure).
    /// </summary>
    public int MacdDivergenceSwingTpLookbackBars { get; set; } = 20;

    /// <summary>
    /// ATR fraction buffer beyond the swing point for TP placement. Defaults to 0.1.
    /// </summary>
    public decimal MacdDivergenceSwingTpBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>
    /// Minimum TP distance as ATR multiplier when using swing TP. Defaults to 1.5.
    /// </summary>
    public decimal MacdDivergenceSwingTpMinAtrMultiplier { get; set; } = 1.5m;

    /// <summary>
    /// Maximum TP distance as ATR multiplier when using swing TP. Defaults to 4.0.
    /// </summary>
    public decimal MacdDivergenceSwingTpMaxAtrMultiplier { get; set; } = 4.0m;

    // ── MACD Divergence dynamic SL/TP ────────────────────────────────────

    /// <summary>
    /// When enabled, SL and TP ATR multipliers are scaled by ADX strength.
    /// Stronger trends get tighter stops and wider targets (reversals need room).
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceDynamicSlTp { get; set; } = false;

    /// <summary>
    /// ADX value considered a "strong trend" for dynamic SL/TP scaling. Defaults to 40.
    /// </summary>
    public decimal MacdDivergenceStrongAdxThreshold { get; set; } = 40m;

    /// <summary>
    /// SL multiplier scale factor in a strong trend. Values above 1.0 widen the stop
    /// (divergence trades counter-trend, so need more room). Defaults to 1.2.
    /// </summary>
    public decimal MacdDivergenceStrongTrendSlScale { get; set; } = 1.2m;

    /// <summary>
    /// TP multiplier scale factor in a strong trend. Values above 1.0 widen the target.
    /// Defaults to 1.4 (stronger trend = bigger reversal potential).
    /// </summary>
    public decimal MacdDivergenceStrongTrendTpScale { get; set; } = 1.4m;

    // ── MACD Divergence pivot detection ──────────────────────────────────

    /// <summary>
    /// Number of bars on each side required to confirm a swing pivot point for
    /// divergence detection. Higher values produce fewer but more reliable pivots.
    /// Defaults to 2. Set to 1 for the legacy single-bar check.
    /// </summary>
    public int MacdDivergencePivotRadius { get; set; } = 2;

    /// <summary>
    /// When enabled, also detects hidden divergence (trend continuation).
    /// Hidden bullish: price makes a higher low, histogram makes a lower low.
    /// Hidden bearish: price makes a lower high, histogram makes a higher high.
    /// Defaults to true.
    /// </summary>
    public bool MacdDivergenceDetectHidden { get; set; } = true;

    /// <summary>
    /// Confidence bonus for classic divergence over zero-line crossovers. Defaults to 0.10.
    /// </summary>
    public decimal MacdDivergenceClassicBonus { get; set; } = 0.10m;

    /// <summary>
    /// Confidence bonus for hidden divergence over zero-line crossovers.
    /// Typically lower than classic because hidden is continuation, not reversal.
    /// Defaults to 0.05.
    /// </summary>
    public decimal MacdDivergenceHiddenBonus { get; set; } = 0.05m;

    /// <summary>
    /// When enabled, lot size is scaled between min and max based on confidence.
    /// Defaults to false (uses DefaultLotSize).
    /// </summary>
    public bool MacdDivergenceConfidenceLotSizing { get; set; } = false;

    /// <summary>Minimum lot size for confidence-based sizing. Defaults to 0.01.</summary>
    public decimal MacdDivergenceMinLotSize { get; set; } = 0.01m;

    /// <summary>Maximum lot size for confidence-based sizing. Defaults to 0.10.</summary>
    public decimal MacdDivergenceMaxLotSize { get; set; } = 0.10m;

    // ── MACD Divergence confidence weights ───────────────────────────────

    /// <summary>
    /// Confidence weight for divergence magnitude (price separation between swing points
    /// normalised by ATR). Larger divergence = stronger exhaustion signal. Defaults to 0.30.
    /// </summary>
    public decimal MacdDivergenceWeightMagnitude { get; set; } = 0.30m;

    /// <summary>
    /// Confidence weight for histogram turning strength (delta between current and previous
    /// histogram bar normalised by ATR). Defaults to 0.25.
    /// </summary>
    public decimal MacdDivergenceWeightHistogramTurn { get; set; } = 0.25m;

    /// <summary>
    /// Confidence weight for ADX trend-strength factor. Defaults to 0.15.
    /// </summary>
    public decimal MacdDivergenceWeightAdx { get; set; } = 0.15m;

    /// <summary>
    /// Confidence weight for candle pattern confirmation (engulfing, pin bar).
    /// Defaults to 0 — opt in explicitly. ScoreCandlePatterns returns 0.5 for
    /// neutral candles, which anchors confidence toward the midpoint and dilutes
    /// stronger factors unless the candle pattern is genuinely informative.
    /// </summary>
    public decimal MacdDivergenceWeightCandlePattern { get; set; } = 0m;

    /// <summary>
    /// Confidence weight for RSI alignment factor. RSI in the expected zone for
    /// the signal direction boosts confidence. Defaults to 0.10.
    /// </summary>
    public decimal MacdDivergenceWeightRsi { get; set; } = 0.10m;

    /// <summary>
    /// Confidence weight for volume relative to recent average. Defaults to 0.05.
    /// </summary>
    public decimal MacdDivergenceWeightVolume { get; set; } = 0.05m;

    /// <summary>
    /// Number of bars for average volume calculation in MACD divergence. Defaults to 20.
    /// </summary>
    public int MacdDivergenceVolumeLookbackBars { get; set; } = 20;

    // ── MACD Divergence: line vs histogram divergence ─────────────────────

    /// <summary>
    /// When enabled, divergence is detected on the MACD line (peaks/troughs) in addition
    /// to the histogram. MACD line divergence is less sensitive but more reliable.
    /// Defaults to false (histogram only).
    /// </summary>
    public bool MacdDivergenceUseMacdLine { get; set; } = false;

    /// <summary>
    /// Confidence bonus for divergence detected on the MACD line rather than the histogram.
    /// MACD line divergence is less sensitive but more reliable, so it deserves a small reward.
    /// Defaults to 0.05. Set to 0 to treat both sources equally.
    /// </summary>
    public decimal MacdDivergenceLineSourceBonus { get; set; } = 0.05m;

    // ── MACD Divergence: zero-line crossover strengthening ────────────────

    /// <summary>
    /// Minimum ADX required for a zero-line crossover (non-divergence) signal.
    /// Crossovers are weaker than divergence signals, so a higher ADX gate reduces noise.
    /// Defaults to 20. Set to 0 to use the same gate as divergence signals.
    /// </summary>
    public decimal MacdDivergenceCrossoverMinAdx { get; set; } = 20m;

    /// <summary>
    /// Confidence penalty applied to zero-line crossover signals (subtracted from score).
    /// Crossovers lack the exhaustion confirmation of divergence. Defaults to 0.10.
    /// </summary>
    public decimal MacdDivergenceCrossoverConfidencePenalty { get; set; } = 0.10m;

    // ── MACD Divergence: divergence age decay ─────────────────────────────

    /// <summary>
    /// Confidence weight for divergence age decay factor. A swing point 3 bars ago is
    /// more actionable than one 25 bars ago. The score decays linearly as
    /// (1 - barDistance / lookback). Defaults to 0.10. Set to 0 to disable.
    /// </summary>
    public decimal MacdDivergenceWeightAge { get; set; } = 0.10m;

    // ── MACD Divergence: histogram zero-cross validation ──────────────────

    /// <summary>
    /// When enabled, requires the histogram to cross zero at least once between the
    /// swing point and the current bar. This ensures a full MACD oscillation cycle,
    /// filtering out weak "same-wave" divergences. Defaults to true.
    /// </summary>
    public bool MacdDivergenceRequireHistogramZeroCross { get; set; } = true;

    // ── MACD Divergence: current bar pivot validation ─────────────────────

    /// <summary>
    /// When enabled, the current bar must form a developing pivot (bars to the left
    /// satisfy the pivot condition) for divergence detection. Reduces premature signals
    /// but delays entry until the pivot is partially confirmed. Defaults to false.
    /// </summary>
    public bool MacdDivergenceRequireCurrentBarPivot { get; set; } = false;

    // ── MACD Divergence: market regime filtering ──────────────────────────

    /// <summary>
    /// When enabled, the evaluator queries the market regime detector and skips
    /// signal generation in unfavourable regimes (e.g. Ranging). Defaults to false.
    /// </summary>
    public bool MacdDivergenceRegimeFilterEnabled { get; set; } = false;

    /// <summary>
    /// Market regimes that are allowed for MACD divergence signals.
    /// If the current regime is not in this list, the signal is skipped.
    /// Defaults to Trending, HighVolatility, Breakout.
    /// </summary>
    public HashSet<MarketRegimeEnum> MacdDivergenceAllowedRegimes { get; set; } =
        [MarketRegimeEnum.Trending, MarketRegimeEnum.HighVolatility, MarketRegimeEnum.Breakout];

    /// <summary>
    /// Confidence penalty applied when the market regime is allowed but not ideal.
    /// For example, HighVolatility may be allowed but less reliable than Trending.
    /// Keyed by regime. Regimes not in this dictionary receive no penalty.
    /// </summary>
    public Dictionary<MarketRegimeEnum, decimal> MacdDivergenceRegimeConfidencePenalty { get; set; } = new()
    {
        { MarketRegimeEnum.HighVolatility, 0.05m },
        { MarketRegimeEnum.Breakout, 0.03m }
    };

    // ── MACD Divergence: signal cooldown ──────────────────────────────────

    /// <summary>
    /// Minimum number of bars that must pass after a signal is generated before
    /// the evaluator can fire again for the same strategy. Prevents duplicate
    /// signals from the same divergence structure. Defaults to 0 (disabled).
    /// </summary>
    public int MacdDivergenceCooldownBars { get; set; } = 0;

    // ── MACD Divergence: multi-timeframe confirmation ─────────────────────

    /// <summary>
    /// When enabled, the evaluator queries the multi-timeframe filter and
    /// applies its confirmation strength as a confidence modifier (not a hard gate).
    /// The hard gate remains in StrategyWorker. This is an evaluator-level
    /// soft penalty so the signal's confidence reflects MTF alignment. Defaults to false.
    /// </summary>
    public bool MacdDivergenceMtfConfidenceEnabled { get; set; } = false;

    /// <summary>
    /// Weight of the multi-timeframe confidence modifier within the evaluator.
    /// Applied as: confidence *= lerp(1.0, mtfStrength, weight).
    /// Defaults to 0.15.
    /// </summary>
    public decimal MacdDivergenceMtfConfidenceWeight { get; set; } = 0.15m;

    // ── MACD Divergence: triple divergence confidence boost ───────────────

    /// <summary>
    /// When enabled, the evaluator scans for a second confirming swing point
    /// (triple divergence: 3 swing points all diverging in the same direction).
    /// If found, confidence is boosted by the configured amount. Defaults to false.
    /// </summary>
    public bool MacdDivergenceTripleDivergenceEnabled { get; set; } = false;

    /// <summary>
    /// Confidence bonus applied when triple divergence is detected.
    /// Defaults to 0.10.
    /// </summary>
    public decimal MacdDivergenceTripleDivergenceBonus { get; set; } = 0.10m;

    // ── MACD Divergence: MACD line zero-cross requirement ─────────────────

    /// <summary>
    /// When enabled, MACD line divergence detection (not histogram) requires
    /// a zero-cross between the two swing points. Previously hardcoded to false.
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceRequireMacdLineZeroCross { get; set; } = false;

    // ── MACD Divergence: Indicator pivot validation ────────────────────────

    /// <summary>
    /// When enabled, divergence detection requires the indicator (histogram or MACD line)
    /// to also form a pivot at the swing index, not just the price. This eliminates false
    /// signals where the indicator value at the price pivot is noise rather than a true extremum.
    /// Defaults to false (off by default for backward compatibility).
    /// </summary>
    public bool MacdDivergenceRequireIndicatorPivot { get; set; } = false;

    // ── MACD Divergence: Signal-line crossover confirmation ────────────────

    /// <summary>
    /// When enabled, divergence signals require the MACD line to cross the signal line
    /// in the trade direction within the last N bars (see MacdDivergenceSignalCrossLookback).
    /// This is a classic entry timing filter that reduces premature entries.
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceRequireSignalLineCross { get; set; } = false;

    /// <summary>
    /// Number of bars to look back for a MACD/signal line crossover confirmation.
    /// Only used when MacdDivergenceRequireSignalLineCross is true.
    /// Defaults to 3.
    /// </summary>
    public int MacdDivergenceSignalCrossLookback { get; set; } = 3;

    // ── MACD Divergence: Minimum indicator divergence magnitude ────────────

    /// <summary>
    /// Minimum absolute difference between indicator values at the two divergence swing
    /// points, expressed as a fraction of ATR. Filters out noise where the indicator
    /// delta is negligible (e.g., -0.00002 vs -0.00001). Set to 0 to disable.
    /// Defaults to 0.05 (5% of ATR).
    /// </summary>
    public decimal MacdDivergenceMinIndicatorDeltaAtrFraction { get; set; } = 0.05m;

    // ── MACD Divergence: Hidden divergence trend context ──────────────────

    /// <summary>
    /// When enabled, hidden divergence (continuation) signals are only generated in
    /// the direction of the prevailing trend, determined by a slow EMA. A bullish hidden
    /// divergence requires price above the trend EMA; bearish requires price below.
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceHiddenRequireTrendAlignment { get; set; } = false;

    /// <summary>
    /// EMA period used for trend direction validation on hidden divergence signals.
    /// Only used when MacdDivergenceHiddenRequireTrendAlignment is true.
    /// Defaults to 200.
    /// </summary>
    public int MacdDivergenceHiddenTrendEmaPeriod { get; set; } = 200;

    // ── MACD Divergence: Zero-line crossover multi-bar momentum ───────────

    /// <summary>
    /// Number of consecutive bars the histogram must accelerate in the signal direction
    /// for a zero-line crossover fallback signal. Reduces whipsaws on noisy crosses.
    /// Set to 1 for no additional check (original behaviour). Defaults to 2.
    /// </summary>
    public int MacdDivergenceCrossoverMomentumBars { get; set; } = 2;

    // ── MACD Divergence: Candlestick pattern hard gate ────────────────────

    /// <summary>
    /// When enabled, divergence signals require a minimum candlestick pattern score
    /// (from IndicatorCalculator.ScoreCandlePatterns) to pass. This promotes entries
    /// that coincide with reversal candle formations (engulfing, hammer, etc.).
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergenceRequireCandlePatternConfirmation { get; set; } = false;

    /// <summary>
    /// Minimum candle pattern score (0..1) required when the hard gate is enabled.
    /// Scores above 0.5 indicate bullish patterns for buys or bearish patterns for sells.
    /// Defaults to 0.55.
    /// </summary>
    public decimal MacdDivergenceMinCandlePatternScore { get; set; } = 0.55m;

    // ── MACD Divergence: Divergence slope in confidence ───────────────────

    /// <summary>
    /// Weight for the divergence slope factor in composite confidence scoring.
    /// Measures how steeply the indicator diverges from price over the swing distance,
    /// normalised by ATR and bar count. Steeper divergence → stronger signal.
    /// Set to 0 to disable. Defaults to 0.10.
    /// </summary>
    public decimal MacdDivergenceWeightSlope { get; set; } = 0.10m;

    // ── MACD Divergence: Partial take-profit ──────────────────────────────

    /// <summary>
    /// When enabled, the evaluator sets a partial take-profit level on the signal
    /// at the swing-structure target (closer target), allowing downstream workers
    /// to scale out a portion of the position at that level while trailing the rest.
    /// Defaults to false.
    /// </summary>
    public bool MacdDivergencePartialTpEnabled { get; set; } = false;

    /// <summary>
    /// Percentage of the position to close at the partial take-profit level (0..1).
    /// Defaults to 0.50 (50%).
    /// </summary>
    public decimal MacdDivergencePartialClosePercent { get; set; } = 0.50m;

    /// <summary>
    /// ATR multiplier for the partial take-profit distance from entry.
    /// Used as fallback when no swing-structure target is found.
    /// Defaults to 1.0 (1x ATR).
    /// </summary>
    public decimal MacdDivergencePartialTpAtrMultiplier { get; set; } = 1.0m;

    // ── MACD Divergence: Cooldown persistence ─────────────────────────────

    /// <summary>
    /// When enabled, the evaluator seeds the in-memory cooldown dictionary from
    /// the database (most recent signal per strategy) on first access, making the
    /// cooldown survive application restarts. Defaults to false.
    /// </summary>
    public bool MacdDivergenceCooldownPersistenceEnabled { get; set; } = false;

    // ── Session Breakout ───────────────────────────────────────────────────

    /// <summary>Default confidence for session breakout signals. Defaults to 0.68.</summary>
    public decimal SessionBreakoutConfidence { get; set; } = 0.68m;

    /// <summary>Signal expiry in minutes for session breakout. Defaults to 30.</summary>
    public int SessionBreakoutExpiryMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum spread as a fraction of ATR allowed for a session breakout signal.
    /// Prevents entries when spread is abnormally wide (news, low liquidity).
    /// Defaults to 0.5 (50% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal SessionBreakoutMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum tick volume on the signal bar required for the session breakout to fire.
    /// Breakouts without volume participation are often false. Defaults to 0 (disabled).
    /// </summary>
    public decimal SessionBreakoutMinVolume { get; set; } = 0m;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Signals that cannot achieve this R:R are rejected. Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal SessionBreakoutMinRiskRewardRatio { get; set; } = 1.0m;

    /// <summary>
    /// Maximum gap (|Open - PreviousClose|) as a fraction of ATR allowed on the signal bar.
    /// Overnight gaps distort the session range. Defaults to 2.0 (200% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal SessionBreakoutMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Number of consecutive closed bars where price must remain on the breakout side of
    /// the session level before the signal fires. Eliminates snap-back false breakouts.
    /// Defaults to 1. Set to 0 to fire on the breakout bar itself.
    /// </summary>
    public int SessionBreakoutConfirmationBars { get; set; } = 1;

    /// <summary>
    /// Minimum ADX value required for a session breakout signal to fire.
    /// Low ADX indicates a ranging market where breakouts are prone to failing.
    /// Defaults to 0 (disabled). Typical production value: 20–25.
    /// </summary>
    public decimal SessionBreakoutMinAdx { get; set; } = 0m;

    /// <summary>ADX period for the session breakout trend-strength filter. Defaults to 14.</summary>
    public int SessionBreakoutAdxPeriod { get; set; } = 14;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries are shifted up, sell entries are shifted down.
    /// Defaults to 0 (disabled). Typical value: 0.05–0.15.
    /// </summary>
    public decimal SessionBreakoutSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// Maximum confidence boost from breakout depth — how far price has exceeded the session
    /// range level, normalised by range size. Defaults to 0.15 (up to +15% above base).
    /// </summary>
    public decimal SessionBreakoutConfidenceBreachBoostMax { get; set; } = 0.15m;

    /// <summary>
    /// Minimum range size as a fraction of ATR. Ranges narrower than this are considered
    /// too tight and likely to produce false breakouts. Defaults to 0.3 (30% of ATR). Set to 0 to disable.
    /// </summary>
    public decimal SessionBreakoutMinRangeSizeAtrFraction { get; set; } = 0.3m;

    // ── Momentum Trend ─────────────────────────────────────────────────────

    /// <summary>Default confidence for momentum trend signals. Defaults to 0.70.</summary>
    public decimal MomentumTrendConfidence { get; set; } = 0.70m;

    /// <summary>Signal expiry in minutes for momentum trend. Defaults to 90.</summary>
    public int MomentumTrendExpiryMinutes { get; set; } = 90;

    /// <summary>
    /// Maximum gap (|Open - PreviousClose|) as a fraction of ATR allowed on the signal bar.
    /// Large gaps distort ADX/DI calculations. Defaults to 2.0. Set to 0 to disable.
    /// </summary>
    public decimal MomentumTrendMaxGapAtrFraction { get; set; } = 2.0m;

    /// <summary>
    /// Maximum spread as a fraction of ATR allowed for a momentum trend signal.
    /// Prevents entries when spread is abnormally wide. Defaults to 0.5. Set to 0 to disable.
    /// </summary>
    public decimal MomentumTrendMaxSpreadAtrFraction { get; set; } = 0.5m;

    /// <summary>
    /// Minimum tick volume on the signal bar. Trend signals without volume are
    /// unreliable. Defaults to 0 (disabled).
    /// </summary>
    public decimal MomentumTrendMinVolume { get; set; } = 0m;

    /// <summary>
    /// Maximum RSI for a bullish momentum trend signal. Prevents buying into an
    /// overbought condition even when DI cross fires. Defaults to 0 (disabled). Typical: 75.
    /// </summary>
    public decimal MomentumTrendMaxRsiForBuy { get; set; } = 0m;

    /// <summary>
    /// Minimum RSI for a bearish momentum trend signal. Prevents selling into an
    /// oversold condition even when DI cross fires. Defaults to 0 (disabled). Typical: 25.
    /// </summary>
    public decimal MomentumTrendMinRsiForSell { get; set; } = 0m;

    /// <summary>RSI period for the momentum trend overbought/oversold filter. Defaults to 14.</summary>
    public int MomentumTrendRsiPeriod { get; set; } = 14;

    /// <summary>
    /// Period for the trend-alignment EMA filter. When > 0, bullish signals require
    /// close > EMA(period) and bearish require close &lt; EMA(period).
    /// Defaults to 0 (disabled). Typical value: 50–200.
    /// </summary>
    public int MomentumTrendTrendMaPeriod { get; set; } = 0;

    /// <summary>
    /// Number of consecutive bars where DI must remain on the crossed side before
    /// the signal fires. Eliminates whipsaw DI crosses that immediately revert.
    /// Defaults to 0 (fires on the cross bar). Typical value: 1–2.
    /// </summary>
    public int MomentumTrendConfirmationBars { get; set; } = 0;

    /// <summary>
    /// Slippage buffer as a fraction of ATR added to the entry price.
    /// Buy entries shift up, sell entries shift down.
    /// Defaults to 0 (disabled). Typical value: 0.05–0.15.
    /// </summary>
    public decimal MomentumTrendSlippageAtrFraction { get; set; } = 0m;

    /// <summary>
    /// Minimum acceptable risk-reward ratio (TP distance / SL distance).
    /// Signals that cannot achieve this are rejected. Defaults to 1.0. Set to 0 to disable.
    /// </summary>
    public decimal MomentumTrendMinRiskRewardRatio { get; set; } = 1.0m;

    /// <summary>
    /// Maximum confidence boost from ADX strength — how far ADX exceeds the threshold,
    /// normalised to [0, ConfidenceAdxBoostMax]. Defaults to 0.20.
    /// </summary>
    public decimal MomentumTrendConfidenceAdxBoostMax { get; set; } = 0.20m;

    /// <summary>
    /// When true, use swing-based stop-loss (structural pivot) instead of pure ATR SL.
    /// Defaults to false.
    /// </summary>
    public bool MomentumTrendSwingSlEnabled { get; set; } = false;

    /// <summary>Number of bars to look back for swing pivot SL. Defaults to 10.</summary>
    public int MomentumTrendSwingSlLookbackBars { get; set; } = 10;

    /// <summary>ATR fraction buffer added beyond the swing point for SL. Defaults to 0.1.</summary>
    public decimal MomentumTrendSwingSlBufferAtrFraction { get; set; } = 0.1m;

    /// <summary>Minimum SL distance as ATR multiplier when using swing SL. Defaults to 0.5.</summary>
    public decimal MomentumTrendSwingSlMinAtrMultiplier { get; set; } = 0.5m;

    /// <summary>Maximum SL distance as ATR multiplier when using swing SL. Defaults to 3.0.</summary>
    public decimal MomentumTrendSwingSlMaxAtrMultiplier { get; set; } = 3.0m;

    /// <summary>
    /// When true, scale lot size between MomentumTrendMinLotSize and MomentumTrendMaxLotSize
    /// based on signal confidence. Defaults to false.
    /// </summary>
    public bool MomentumTrendConfidenceLotSizing { get; set; } = false;

    /// <summary>Minimum lot size for confidence-based scaling. Defaults to 0.01.</summary>
    public decimal MomentumTrendMinLotSize { get; set; } = 0.01m;

    /// <summary>Maximum lot size for confidence-based scaling. Defaults to 0.10.</summary>
    public decimal MomentumTrendMaxLotSize { get; set; } = 0.10m;

    // ── Stop-loss / take-profit ATR multipliers ────────────────────────────

    // ── Post-evaluator confidence modifiers ────────────────────────────────

    /// <summary>
    /// Weight (0.0–1.0) for session-quality confidence adjustment.
    /// The signal's confidence is adjusted by: confidence *= lerp(1.0, sessionQuality, weight).
    /// A weight of 0 disables the modifier. Defaults to 0.15.
    /// </summary>
    public decimal SessionConfidenceWeight { get; set; } = 0.15m;

    /// <summary>
    /// Weight (0.0–1.0) for multi-timeframe confirmation confidence adjustment.
    /// The signal's confidence is adjusted by: confidence *= lerp(1.0, mtfStrength, weight).
    /// A weight of 0 disables the modifier. Only applies when <see cref="RequireMultiTimeframeConfirmation"/>
    /// is false (when MTF is a hard gate, the signal already passed). Defaults to 0.20.
    /// </summary>
    public decimal MultiTimeframeConfidenceWeight { get; set; } = 0.20m;

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
    public List<TradingSession> AllowedSessions { get; set; } = [TradingSession.London, TradingSession.LondonNYOverlap, TradingSession.NewYork];

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

    /// <summary>
    /// Minimum seconds between signals for the same strategy.
    /// Prevents signal flooding when a strategy evaluator fires on every tick.
    /// Defaults to 60. Set to 0 to disable.
    /// </summary>
    public int SignalCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Market regimes that block signal generation globally.
    /// If the latest regime snapshot for a symbol/timeframe matches a blocked regime,
    /// the strategy is skipped. Defaults to [Crisis]. Empty list disables the filter.
    /// </summary>
    public List<MarketRegimeEnum> BlockedRegimes { get; set; } = [MarketRegimeEnum.Crisis];

    /// <summary>
    /// Maximum number of strategies to evaluate concurrently per price tick.
    /// Higher values improve throughput when many strategies share the same symbol,
    /// at the cost of increased DB connection usage. Defaults to 4. Set to 1 to disable parallelism.
    /// </summary>
    public int MaxParallelStrategies { get; set; } = 4;

    /// <summary>
    /// Maximum number of expired signals to process per sweep cycle.
    /// Prevents memory spikes after downtime. Defaults to 500.
    /// </summary>
    public int ExpirySweepBatchSize { get; set; } = 500;

    /// <summary>
    /// Maximum age in seconds for a price tick event before it is dropped as stale.
    /// Prevents strategy evaluation on outdated prices from event bus backlog.
    /// Defaults to 10. Set to 0 to disable.
    /// </summary>
    public int MaxTickAgeSeconds { get; set; } = 10;

    /// <summary>
    /// Number of consecutive evaluation failures before a strategy is temporarily
    /// disabled. Prevents log flooding from persistently broken evaluators.
    /// After <see cref="CircuitBreakerRecoverySeconds"/>, the circuit enters half-open
    /// state and allows one probe attempt. Defaults to 5. Set to 0 to disable.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>
    /// Seconds after which an open circuit breaker transitions to half-open,
    /// allowing a single probe evaluation. If the probe succeeds, the circuit
    /// resets. If it fails, the timer restarts. Defaults to 300 (5 minutes).
    /// </summary>
    public int CircuitBreakerRecoverySeconds { get; set; } = 300;

    /// <summary>
    /// Maximum age in seconds of an EA instance's last heartbeat before the instance
    /// is considered stale. If no EA with a fresh heartbeat owns a symbol, the tick
    /// is dropped as DATA_UNAVAILABLE. Defaults to 60.
    /// </summary>
    public int MaxEAHeartbeatAgeSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout in seconds for individual mediator command calls (e.g. CreateTradeSignalCommand).
    /// Prevents a hung handler from blocking the evaluation pipeline indefinitely.
    /// Defaults to 30.
    /// </summary>
    public int MediatorTimeoutSeconds { get; set; } = 30;
}
