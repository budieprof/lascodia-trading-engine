using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Attributes;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Mean-reversion strategy for ranging and low-volatility regimes.
///
/// Entry logic:
///   Buy  — previous close was at or below the lower band and has since closed back above it.
///   Sell — previous close was at or above the upper band and has since closed back below it.
///
/// Hardening layers (all configurable, safe defaults):
///   • Bollinger Band squeeze suppression — avoids entering before an imminent breakout.
///     Supports single-bar (BollingerSqueezeLookbackBars=1) or multi-bar comparison.
///   • Minimum bandwidth gate — prevents entries when bands are too narrow for a meaningful reversion.
///   • Spread filter — rejects entries when bid-ask spread is abnormally wide.
///   • Gap filter — rejects entries when the signal bar opened with an abnormally large gap.
///   • Volume filter — minimum tick volume on the signal bar.
///   • RSI filter — optional hard gate on RSI overbought/oversold at entry.
///   • Candle confirmation — optional pin-bar / engulfing gate.
///   • Slippage buffer — shifts entry price by ATR fraction to account for fill slippage.
///   • Swing-based stop-loss — optional structural SL from swing low/high instead of pure ATR.
///   • Mid-band take-profit — optional TP targeting the SMA (natural mean-reversion destination).
///   • Minimum risk-reward ratio — rejects signals with inadequate TP/SL ratio.
///   • Multi-factor confidence — weighted score from depth, candle pattern, RSI alignment, volume.
///   • Confidence-based lot sizing — optional scaling between min/max lot by confidence score.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class BollingerBandReversionEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public BollingerBandReversionEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.BollingerBandReversion;

    public int MinRequiredCandles(Strategy strategy)
    {
        int period = 20;
        ParseParameters(strategy.ParametersJson, ref period, out _, out _);

        // RSI Wilder smoothing needs 2×period for proper warmup
        bool rsiActive = _options.BollingerWeightRsi    > 0
                      || _options.BollingerMaxRsiForBuy  > 0
                      || _options.BollingerMinRsiForSell > 0;
        int rsiRequired = rsiActive ? _options.BollingerRsiPeriod * 2 + 1 : 0;

        // Multi-bar squeeze compares the band N-1 bars before prev — needs that many extra bars
        int squeezeExtra = Math.Max(0, _options.BollingerSqueezeLookbackBars - 1);

        return Math.Max(Math.Max(period + squeezeExtra, _options.AtrPeriodForSlTp), rsiRequired) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        // ── 1. Parse & clamp parameters ──────────────────────────────────────
        int     period           = 20;
        decimal stdDevMultiple   = 2.0m;
        decimal squeezeThreshold = 0.5m;
        ParseParameters(strategy.ParametersJson, ref period, out stdDevMultiple, out squeezeThreshold);

        period           = Math.Clamp(period, 2, 500);
        stdDevMultiple   = Math.Clamp(stdDevMultiple, 0.5m, 5m);
        squeezeThreshold = Math.Clamp(squeezeThreshold, 0m, 1m);

        int required = Math.Max(period + Math.Max(0, _options.BollingerSqueezeLookbackBars - 1),
                                _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < required)
            return Task.FromResult<TradeSignal?>(null);

        int last = candles.Count - 1;
        int prev = last - 1;

        // ── 2. ATR — computed once, used by all subsequent filters ───────────
        decimal atr = IndicatorCalculator.Atr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);

        // ── 3. Spread filter ─────────────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.BollingerMaxSpreadAtrFraction > 0 && spread > atr * _options.BollingerMaxSpreadAtrFraction)
            return Task.FromResult<TradeSignal?>(null);

        // ── 4. Gap filter ────────────────────────────────────────────────────
        if (_options.BollingerMaxGapAtrFraction > 0)
        {
            decimal gap = Math.Abs(candles[last].Open - candles[prev].Close);
            if (gap > atr * _options.BollingerMaxGapAtrFraction)
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. Volume filter ─────────────────────────────────────────────────
        if (_options.BollingerMinVolume > 0 && candles[last].Volume < _options.BollingerMinVolume)
            return Task.FromResult<TradeSignal?>(null);

        // ── 6. Bollinger Band calculations (current bar) ─────────────────────
        decimal sma    = IndicatorCalculator.Sma(candles, last, period);
        decimal stdDev = IndicatorCalculator.StdDev(candles, last, period, sma);
        decimal upper  = sma + stdDevMultiple * stdDev;
        decimal lower  = sma - stdDevMultiple * stdDev;

        // Previous bar bands (where the band touch actually occurred)
        decimal prevSma    = IndicatorCalculator.Sma(candles, prev, period);
        decimal prevStdDev = IndicatorCalculator.StdDev(candles, prev, period, prevSma);
        decimal prevUpper  = prevSma + stdDevMultiple * prevStdDev;
        decimal prevLower  = prevSma - stdDevMultiple * prevStdDev;

        // ── 7. Squeeze detection ─────────────────────────────────────────────
        // Bandwidth = (upper − lower) / sma = 2 × stdDevMultiple × stdDev / sma.
        // BollingerSqueezeLookbackBars=1 compares current bandwidth to the previous bar
        // (original behaviour). Values >1 compare to N bars ago, catching gradual multi-bar
        // squeezes invisible to a single-bar comparison (e.g., 3–5 bars of slow contraction).
        const decimal smaEpsilon = 0.000001m;
        decimal bandwidth = sma > smaEpsilon ? (upper - lower) / sma : 0m;

        decimal prevBandwidth = prevSma > smaEpsilon ? (prevUpper - prevLower) / prevSma : 0m;
        decimal referenceBandwidth = prevBandwidth;

        int squeezeLookback = Math.Max(1, _options.BollingerSqueezeLookbackBars);
        if (squeezeLookback > 1)
        {
            // Compare to N bars ago (baseIdx = prev - (squeezeLookback - 1))
            int baseIdx = prev - (squeezeLookback - 1);
            if (baseIdx >= period - 1)
            {
                decimal baseSma    = IndicatorCalculator.Sma(candles, baseIdx, period);
                decimal baseStdDev = IndicatorCalculator.StdDev(candles, baseIdx, period, baseSma);
                referenceBandwidth = baseSma > smaEpsilon
                    ? 2m * stdDevMultiple * baseStdDev / baseSma
                    : 0m;
            }
        }

        if (referenceBandwidth > 0 && bandwidth < squeezeThreshold * referenceBandwidth)
            return Task.FromResult<TradeSignal?>(null);

        // ── 8. Band validity & minimum-width checks ──────────────────────────
        decimal bandWidth = upper - lower;
        if (bandWidth <= 0)
            return Task.FromResult<TradeSignal?>(null);

        // Reject entries when bands are too narrow for a meaningful reversion move
        if (_options.BollingerMinBandwidthAtrFraction > 0 && bandWidth < atr * _options.BollingerMinBandwidthAtrFraction)
            return Task.FromResult<TradeSignal?>(null);

        // ── 9. Signal detection ──────────────────────────────────────────────
        decimal currentClose = candles[last].Close;
        decimal prevClose    = candles[prev].Close;

        TradeDirection? direction = null;
        decimal entryPrice;
        decimal depth; // normalised depth of band touch [0..∞), used for confidence

        // Buy: previous close was at or below the lower band; current close is back above it
        if (prevClose <= prevLower && currentClose > lower)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
            depth      = prevSma > smaEpsilon ? (prevLower - prevClose) / bandWidth : 0m;
        }
        // Sell: previous close was at or above the upper band; current close is back below it
        else if (prevClose >= prevUpper && currentClose < upper)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
            depth      = prevSma > smaEpsilon ? (prevClose - prevUpper) / bandWidth : 0m;
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        bool isBullish = direction == TradeDirection.Buy;
        depth = Math.Max(depth, 0m);

        // ── 10. RSI filter ────────────────────────────────────────────────────
        // Compute RSI once if any RSI-dependent option is active
        bool needsRsi = (_options.BollingerWeightRsi    > 0)
                     || (isBullish  && _options.BollingerMaxRsiForBuy  > 0)
                     || (!isBullish && _options.BollingerMinRsiForSell > 0);
        decimal rsi = needsRsi
            ? IndicatorCalculator.Rsi(candles, last, _options.BollingerRsiPeriod)
            : 50m;

        if (isBullish  && _options.BollingerMaxRsiForBuy  > 0 && rsi > _options.BollingerMaxRsiForBuy)
            return Task.FromResult<TradeSignal?>(null);
        if (!isBullish && _options.BollingerMinRsiForSell > 0 && rsi < _options.BollingerMinRsiForSell)
            return Task.FromResult<TradeSignal?>(null);

        // ── 11. Candle pattern confirmation ───────────────────────────────────
        // ScoreCandlePatterns returns [0..1] where 0.5 is neutral, >0.5 means confirming pattern.
        decimal candleScore = IndicatorCalculator.ScoreCandlePatterns(candles, last, isBullish);
        if (_options.BollingerRequireCandleConfirmation && candleScore <= 0.5m)
            return Task.FromResult<TradeSignal?>(null);

        // ── 12. Slippage buffer ───────────────────────────────────────────────
        decimal slippage = atr * _options.BollingerSlippageAtrFraction;
        if (isBullish) entryPrice += slippage;
        else           entryPrice -= slippage;

        // ── 13. Stop-loss calculation ─────────────────────────────────────────
        decimal stopDistance;
        if (_options.BollingerSwingSlEnabled)
        {
            decimal swingPoint = isBullish
                ? IndicatorCalculator.FindSwingLow( candles, last, _options.BollingerSwingSlLookbackBars)
                : IndicatorCalculator.FindSwingHigh(candles, last, _options.BollingerSwingSlLookbackBars);

            decimal swingBuffer  = atr * _options.BollingerSwingSlBufferAtrFraction;
            decimal rawSwingStop = isBullish
                ? entryPrice - (swingPoint - swingBuffer)
                : swingPoint + swingBuffer - entryPrice;
            // rawSwingStop can be ≤ 0 when the swing point lies beyond entry (extreme gap or data
            // anomaly). Math.Clamp to minStop guarantees a strictly positive stop distance.
            decimal minStop = atr * _options.BollingerSwingSlMinAtrMultiplier;
            decimal maxStop = atr * _options.BollingerSwingSlMaxAtrMultiplier;
            stopDistance = Math.Clamp(rawSwingStop, minStop, maxStop);
        }
        else
        {
            stopDistance = atr * _options.StopLossAtrMultiplier;
        }

        // ── 14. Take-profit calculation ───────────────────────────────────────
        decimal profitDistance;
        if (_options.BollingerMidBandTpEnabled)
        {
            // The SMA (middle band) is the natural mean-reversion destination.
            // For a buy at the lower band, SMA will normally be above entry, so midBandDist > 0.
            // If slippage has shifted entry past the SMA, midBandDist goes non-positive —
            // Math.Max floors profitDistance at half the standard ATR TP in that case.
            decimal midBandDist = isBullish ? sma - entryPrice : entryPrice - sma;
            decimal minTp       = atr * _options.TakeProfitAtrMultiplier * 0.5m;
            profitDistance      = Math.Max(midBandDist, minTp);
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

        // ── 15. Risk-reward validation ────────────────────────────────────────
        if (_options.BollingerMinRiskRewardRatio > 0 && stopDistance > 0)
        {
            decimal rrRatio = profitDistance / stopDistance;
            if (rrRatio < _options.BollingerMinRiskRewardRatio)
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 16. Multi-factor confidence ───────────────────────────────────────
        // Each factor is normalised to [0, 1] where 0.5 is neutral.
        // Weighted average is shifted around the base confidence by ±(sensitivity/2).

        // Depth factor: how far price breached the band (×2 → caps at 1.0 on a 50% breach)
        decimal depthFactor = Math.Clamp(depth * 2m, 0m, 1m);

        // Candle pattern factor: [0..1] from ScoreCandlePatterns (0.5 for a neutral/doji candle)
        decimal candleFactor = candleScore;

        // RSI alignment factor calibrated to the [30, 70] range relevant for BB reversion:
        //   Buy:  RSI=30→1.0, RSI=50→0.5, RSI=70→0.0  (deeper oversold = stronger setup)
        //   Sell: RSI=70→1.0, RSI=50→0.5, RSI=30→0.0  (deeper overbought = stronger setup)
        decimal rsiFactor = isBullish
            ? Math.Clamp((70m - rsi) / 40m, 0m, 1m)
            : Math.Clamp((rsi - 30m) / 40m, 0m, 1m);

        // Volume factor: compares signal bar volume to recent average (1.0 = 2× average)
        decimal volumeFactor = 0.5m;
        if (_options.BollingerWeightVolume > 0 && _options.BollingerVolumeLookbackBars > 0)
        {
            int volLookback = Math.Min(_options.BollingerVolumeLookbackBars, last);
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

        decimal totalWeight = _options.BollingerWeightDepth  + _options.BollingerWeightCandle
                            + _options.BollingerWeightRsi    + _options.BollingerWeightVolume;
        decimal confidence;
        if (totalWeight > 0)
        {
            decimal weightedScore =
                depthFactor  * _options.BollingerWeightDepth  +
                candleFactor * _options.BollingerWeightCandle +
                rsiFactor    * _options.BollingerWeightRsi    +
                volumeFactor * _options.BollingerWeightVolume;

            decimal normalisedScore = weightedScore / totalWeight;
            confidence = Math.Clamp(
                _options.BollingerConfidence + (normalisedScore - 0.5m) * _options.BollingerConfidenceSensitivity,
                0m, 1m);
        }
        else
        {
            // No weights configured — fall back to legacy depth-only scoring
            confidence = Math.Clamp(_options.BollingerConfidence + depth * 0.2m, 0m, 1m);
        }

        // ── 17. Lot sizing ────────────────────────────────────────────────────
        decimal lotSize = _options.DefaultLotSize;
        if (_options.BollingerConfidenceLotSizing && _options.BollingerMaxLotSize > _options.BollingerMinLotSize)
        {
            lotSize = _options.BollingerMinLotSize
                + confidence * (_options.BollingerMaxLotSize - _options.BollingerMinLotSize);
            lotSize = Math.Clamp(lotSize, _options.BollingerMinLotSize, _options.BollingerMaxLotSize);
        }

        // ── 18. Emit signal ───────────────────────────────────────────────────
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
            ExpiresAt        = now.AddMinutes(_options.BollingerExpiryMinutes)
        });
    }

    private static void ParseParameters(string? json, ref int period, out decimal stdDevMultiple, out decimal squeezeThreshold)
    {
        stdDevMultiple   = 2.0m;
        squeezeThreshold = 0.5m;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("Period",           out var p)  && p.TryGetInt32(out var pv))     period           = pv;
            if (root.TryGetProperty("StdDevMultiple",   out var sd) && sd.TryGetDecimal(out var sdv)) stdDevMultiple   = sdv;
            if (root.TryGetProperty("SqueezeThreshold", out var sq) && sq.TryGetDecimal(out var sqv)) squeezeThreshold = sqv;
        }
        catch { /* use defaults */ }
    }
}
