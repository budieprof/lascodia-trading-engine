using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Mean-reversion strategy based on RSI oversold/overbought crossback.
///
/// Entry logic:
///   Buy  — RSI exits the oversold zone (prevRSI ≤ oversold, currentRSI > oversold).
///   Sell — RSI exits the overbought zone (prevRSI ≥ overbought, currentRSI &lt; overbought).
///
/// Hardening layers (all configurable, safe defaults):
///   • Spread filter — rejects entries when bid-ask spread is abnormally wide.
///   • Gap filter — rejects entries when the signal bar opened with an abnormally large gap.
///   • Volume filter — minimum tick volume on the signal bar.
///   • Candle confirmation — optional pin-bar / engulfing gate.
///   • RSI divergence filter — optional requirement for price-RSI divergence at entry.
///   • Slippage buffer — shifts entry price by ATR fraction to account for fill slippage.
///   • Swing-based stop-loss — optional structural SL from swing low/high instead of pure ATR.
///   • Midline take-profit — optional TP targeting the SMA (RSI 50 equivalent price level).
///   • Minimum risk-reward ratio — rejects signals with inadequate TP/SL ratio.
///   • Multi-factor confidence — weighted score from RSI depth, candle pattern, volume, recovery speed.
///   • Confidence-based lot sizing — optional scaling between min/max lot by confidence score.
/// </summary>
public class RSIReversionEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public RSIReversionEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.RSIReversion;

    public int MinRequiredCandles(Strategy strategy)
    {
        int period = 14;
        ParseParameters(strategy.ParametersJson, ref period, out _, out _);

        // Wilder RSI needs 2×period for proper warmup; ATR also needs its own window
        int rsiRequired = period * 2 + 1;
        int divergenceExtra = _options.RsiReversionRequireDivergence
            ? _options.RsiReversionDivergenceLookbackBars
            : 0;

        return Math.Max(Math.Max(rsiRequired + divergenceExtra, _options.AtrPeriodForSlTp), period + 1) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        // ── 1. Parse & clamp parameters ──────────────────────────────────────
        int     period     = 14;
        decimal oversold   = 30m;
        decimal overbought = 70m;
        ParseParameters(strategy.ParametersJson, ref period, out oversold, out overbought);

        period     = Math.Clamp(period, 2, 500);
        oversold   = Math.Clamp(oversold, 1m, 49m);
        overbought = Math.Clamp(overbought, 51m, 99m);

        int required = Math.Max(Math.Max(period * 2 + 1, _options.AtrPeriodForSlTp), period + 1) + 1;
        if (candles.Count < required)
            return Task.FromResult<TradeSignal?>(null);

        int last = candles.Count - 1;
        int prev = last - 1;

        // ── 2. ATR — computed once, used by all subsequent filters ───────────
        decimal atr = IndicatorCalculator.Atr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);

        // ── 3. Spread filter ─────────────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.RsiReversionMaxSpreadAtrFraction > 0 && spread > atr * _options.RsiReversionMaxSpreadAtrFraction)
            return Task.FromResult<TradeSignal?>(null);

        // ── 4. Gap filter ────────────────────────────────────────────────────
        if (_options.RsiReversionMaxGapAtrFraction > 0)
        {
            decimal gap = Math.Abs(candles[last].Open - candles[prev].Close);
            if (gap > atr * _options.RsiReversionMaxGapAtrFraction)
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. Volume filter ─────────────────────────────────────────────────
        if (_options.RsiReversionMinVolume > 0 && candles[last].Volume < _options.RsiReversionMinVolume)
            return Task.FromResult<TradeSignal?>(null);

        // ── 6. RSI calculation (Wilder smoothing for production accuracy) ────
        decimal currentRsi = IndicatorCalculator.Rsi(candles, last, period);
        decimal prevRsi    = IndicatorCalculator.Rsi(candles, prev, period);

        // ── 7. Signal detection ──────────────────────────────────────────────
        TradeDirection? direction = null;
        decimal entryPrice;
        decimal rsiDepth; // how far RSI penetrated the zone, normalised

        if (prevRsi <= oversold && currentRsi > oversold)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
            // Depth: how deep into oversold prevRsi was (e.g., RSI=10 with oversold=30 → depth=(30-10)/30=0.67)
            rsiDepth = (oversold - prevRsi) / oversold;
        }
        else if (prevRsi >= overbought && currentRsi < overbought)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
            // Depth: how deep into overbought prevRsi was (e.g., RSI=90 with overbought=70 → depth=(90-70)/30=0.67)
            rsiDepth = (prevRsi - overbought) / (100m - overbought);
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        bool isBullish = direction == TradeDirection.Buy;
        rsiDepth = Math.Clamp(rsiDepth, 0m, 1m);

        // ── 8. RSI divergence filter ─────────────────────────────────────────
        if (_options.RsiReversionRequireDivergence)
        {
            if (!HasRsiDivergence(candles, last, period, isBullish, _options.RsiReversionDivergenceLookbackBars))
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 9. Candle pattern confirmation ───────────────────────────────────
        decimal candleScore = IndicatorCalculator.ScoreCandlePatterns(candles, last, isBullish);
        if (_options.RsiReversionRequireCandleConfirmation && candleScore <= 0.5m)
            return Task.FromResult<TradeSignal?>(null);

        // ── 10. Slippage buffer ──────────────────────────────────────────────
        decimal slippage = atr * _options.RsiReversionSlippageAtrFraction;
        if (isBullish) entryPrice += slippage;
        else           entryPrice -= slippage;

        // ── 11. Stop-loss calculation ────────────────────────────────────────
        decimal stopDistance;
        if (_options.RsiReversionSwingSlEnabled)
        {
            decimal swingPoint = isBullish
                ? IndicatorCalculator.FindSwingLow( candles, last, _options.RsiReversionSwingSlLookbackBars)
                : IndicatorCalculator.FindSwingHigh(candles, last, _options.RsiReversionSwingSlLookbackBars);

            decimal swingBuffer  = atr * _options.RsiReversionSwingSlBufferAtrFraction;
            decimal rawSwingStop = isBullish
                ? entryPrice - (swingPoint - swingBuffer)
                : swingPoint + swingBuffer - entryPrice;

            decimal minStop = atr * _options.RsiReversionSwingSlMinAtrMultiplier;
            decimal maxStop = atr * _options.RsiReversionSwingSlMaxAtrMultiplier;
            stopDistance = Math.Clamp(rawSwingStop, minStop, maxStop);
        }
        else
        {
            stopDistance = atr * _options.StopLossAtrMultiplier;
        }

        // ── 12. Take-profit calculation ──────────────────────────────────────
        decimal profitDistance;
        if (_options.RsiReversionMidlineTpEnabled)
        {
            // The SMA of the RSI period approximates the price level where RSI ≈ 50.
            decimal sma = IndicatorCalculator.Sma(candles, last, period);
            decimal midDist = isBullish ? sma - entryPrice : entryPrice - sma;
            decimal minTp   = atr * _options.TakeProfitAtrMultiplier * 0.5m;
            profitDistance   = Math.Max(midDist, minTp);
        }
        else
        {
            profitDistance = atr * _options.TakeProfitAtrMultiplier;
        }

        decimal? stopLoss, takeProfit;
        if (isBullish)
        {
            stopLoss   = entryPrice - stopDistance;
            takeProfit = entryPrice + profitDistance;
        }
        else
        {
            stopLoss   = entryPrice + stopDistance;
            takeProfit = entryPrice - profitDistance;
        }

        // ── 13. Risk-reward validation ───────────────────────────────────────
        if (_options.RsiReversionMinRiskRewardRatio > 0 && stopDistance > 0)
        {
            decimal rrRatio = profitDistance / stopDistance;
            if (rrRatio < _options.RsiReversionMinRiskRewardRatio)
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 14. Multi-factor confidence ──────────────────────────────────────
        // Depth factor: how far RSI penetrated the zone (already normalised 0..1)
        decimal depthFactor = Math.Clamp(rsiDepth * 2m, 0m, 1m);

        // Candle pattern factor: [0..1] from ScoreCandlePatterns (0.5 for neutral)
        decimal candleFactor = candleScore;

        // Volume factor: compares signal bar volume to recent average (1.0 = 2× average)
        decimal volumeFactor = 0.5m;
        if (_options.RsiReversionWeightVolume > 0 && _options.RsiReversionVolumeLookbackBars > 0)
        {
            int volLookback = Math.Min(_options.RsiReversionVolumeLookbackBars, last);
            if (volLookback > 0)
            {
                decimal avgVol = 0m;
                for (int i = last - volLookback; i < last; i++)
                    avgVol += candles[i].Volume;
                avgVol /= volLookback;
                volumeFactor = avgVol > 0
                    ? Math.Clamp(candles[last].Volume / avgVol / 2m, 0m, 1m)
                    : 0.5m;
            }
        }

        // Recovery speed factor: how sharply RSI bounced through the threshold
        // A large delta (currentRsi - prevRsi for buys) normalised by the zone width signals conviction.
        decimal rsiDelta = isBullish ? (currentRsi - prevRsi) : (prevRsi - currentRsi);
        decimal zoneWidth = isBullish ? oversold : (100m - overbought);
        decimal recoveryFactor = zoneWidth > 0
            ? Math.Clamp(rsiDelta / zoneWidth, 0m, 1m)
            : 0.5m;

        decimal totalWeight = _options.RsiReversionWeightDepth + _options.RsiReversionWeightCandle
                            + _options.RsiReversionWeightVolume + _options.RsiReversionWeightRecoverySpeed;
        decimal confidence;
        if (totalWeight > 0)
        {
            decimal weightedScore =
                depthFactor    * _options.RsiReversionWeightDepth +
                candleFactor   * _options.RsiReversionWeightCandle +
                volumeFactor   * _options.RsiReversionWeightVolume +
                recoveryFactor * _options.RsiReversionWeightRecoverySpeed;

            decimal normalisedScore = weightedScore / totalWeight;
            confidence = Math.Clamp(
                _options.RsiReversionConfidence + (normalisedScore - 0.5m) * _options.RsiReversionConfidenceSensitivity,
                0m, 1m);
        }
        else
        {
            // No weights configured — fall back to legacy depth-only scoring
            confidence = Math.Clamp(_options.RsiReversionConfidence + rsiDepth * 0.2m, 0m, 1m);
        }

        // ── 15. Lot sizing ───────────────────────────────────────────────────
        decimal lotSize = _options.DefaultLotSize;
        if (_options.RsiReversionConfidenceLotSizing && _options.RsiReversionMaxLotSize > _options.RsiReversionMinLotSize)
        {
            lotSize = _options.RsiReversionMinLotSize
                + confidence * (_options.RsiReversionMaxLotSize - _options.RsiReversionMinLotSize);
            lotSize = Math.Clamp(lotSize, _options.RsiReversionMinLotSize, _options.RsiReversionMaxLotSize);
        }

        // ── 16. Emit signal ──────────────────────────────────────────────────
        var now = DateTime.UtcNow;
        return Task.FromResult<TradeSignal?>(new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = lotSize,
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.RsiReversionExpiryMinutes)
        });
    }

    /// <summary>
    /// Detects bullish or bearish RSI divergence within the lookback window.
    /// Bullish: price makes a lower low but RSI makes a higher low.
    /// Bearish: price makes a higher high but RSI makes a lower high.
    /// </summary>
    private static bool HasRsiDivergence(
        IReadOnlyList<Candle> candles, int endIndex, int period,
        bool isBullish, int lookbackBars)
    {
        int searchStart = Math.Max(period * 2, endIndex - lookbackBars);
        decimal currentRsi   = IndicatorCalculator.Rsi(candles, endIndex, period);
        decimal currentPrice = isBullish ? candles[endIndex].Low : candles[endIndex].High;

        for (int i = endIndex - 2; i >= searchStart; i--)
        {
            // Look for a prior swing point
            bool isSwing = isBullish
                ? IndicatorCalculator.IsSwingLow(candles, i)
                : IndicatorCalculator.IsSwingHigh(candles, i);

            if (!isSwing) continue;

            decimal priorRsi   = IndicatorCalculator.Rsi(candles, i, period);
            decimal priorPrice = isBullish ? candles[i].Low : candles[i].High;

            if (isBullish)
            {
                // Bullish divergence: lower low in price, higher low in RSI
                if (currentPrice < priorPrice && currentRsi > priorRsi)
                    return true;
            }
            else
            {
                // Bearish divergence: higher high in price, lower high in RSI
                if (currentPrice > priorPrice && currentRsi < priorRsi)
                    return true;
            }
        }

        return false;
    }

    private static void ParseParameters(string? json, ref int period, out decimal oversold, out decimal overbought)
    {
        oversold   = 30m;
        overbought = 70m;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("Period",     out var p)  && p.TryGetInt32(out var pVal))       period     = pVal;
            if (root.TryGetProperty("Oversold",   out var os) && os.TryGetDecimal(out var osVal))   oversold   = osVal;
            if (root.TryGetProperty("Overbought", out var ob) && ob.TryGetDecimal(out var obVal))   overbought = obVal;
        }
        catch { /* use defaults */ }
    }
}
