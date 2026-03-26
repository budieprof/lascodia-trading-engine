using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Trend-riding strategy using Average Directional Index (ADX) and Directional Movement
/// (+DI / -DI). Designed for trending, high-volatility, and crisis regimes where strong
/// directional moves persist.
///
/// Entry logic:
/// 1. ADX must be above a minimum threshold (default 25) — confirms a strong trend exists.
/// 2. +DI crosses above -DI → Buy (uptrend strengthening).
///    -DI crosses above +DI → Sell (downtrend strengthening).
/// 3. ADX must be rising (current > previous) — trend is gaining strength, not fading.
///
/// Hardening layers (all configurable, safe defaults):
///   • Candle ordering sanity check — rejects out-of-order data.
///   • Gap detection — rejects abnormally large open-vs-previous-close gaps.
///   • Spread safety guard — rejects entries when bid-ask spread is abnormally wide.
///   • Volume confirmation — minimum tick volume on the signal bar.
///   • DI confirmation bars — DI must remain on the crossed side for N bars.
///   • RSI exhaustion gate — prevents buying overbought / selling oversold.
///   • Trend MA alignment — rejects signals against the macro trend.
///   • Slippage buffer — shifts entry price by ATR fraction for realistic fills.
///   • Swing-based stop-loss — optional structural SL from swing pivot instead of pure ATR.
///   • Minimum risk-reward ratio — rejects signals with inadequate TP/SL ratio.
///   • Dynamic confidence scoring — ADX strength bonus above threshold.
///   • Confidence-based lot sizing — optional scaling between min/max lot by confidence score.
///
/// This evaluator intentionally avoids ranging markets (ADX &lt; 25) where directional
/// movement signals produce whipsaws.
/// </summary>
public class MomentumTrendEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<MomentumTrendEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "MomentumTrend");

    public MomentumTrendEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<MomentumTrendEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.MomentumTrend;

    public int MinRequiredCandles(Strategy strategy)
    {
        var (adxPeriod, _) = ParseParameters(strategy.ParametersJson);

        bool rsiEnabled = _options.MomentumTrendMaxRsiForBuy > 0 || _options.MomentumTrendMinRsiForSell > 0;
        int rsiRequirement = rsiEnabled ? _options.MomentumTrendRsiPeriod * 2 : 0;
        int trendRequirement = _options.MomentumTrendTrendMaPeriod > 0 ? _options.MomentumTrendTrendMaPeriod : 0;
        int confirmRequirement = _options.MomentumTrendConfirmationBars;

        // ADX needs 2×period for smoothing; we need current + previous bar for cross detection
        int baseCandles = Math.Max(
            Math.Max(adxPeriod * 2 + 1, _options.AtrPeriodForSlTp),
            Math.Max(rsiRequirement, trendRequirement));

        return baseCandles + 1 + confirmRequirement;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var (adxPeriod, adxThreshold) = ParseParameters(strategy.ParametersJson);
        int lastIdx = candles.Count - 1;

        // ── 1. Candle ordering sanity check ────────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        // ── 2. ATR (Wilder) — used by all subsequent filters ───────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 3. Gap detection ───────────────────────────────────────────────────
        if (_options.MomentumTrendMaxGapAtrFraction > 0 && lastIdx >= 1)
        {
            decimal gap          = Math.Abs(candles[lastIdx].Open - candles[lastIdx - 1].Close);
            decimal gapThreshold = atr * _options.MomentumTrendMaxGapAtrFraction;
            if (gap > gapThreshold)
            {
                LogRejection(strategy, "Gap",
                    $"Price gap {gap:F6} > {_options.MomentumTrendMaxGapAtrFraction:F1}× ATR ({gapThreshold:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 4. Spread safety guard ─────────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.MomentumTrendMaxSpreadAtrFraction > 0 && spread > atr * _options.MomentumTrendMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.MomentumTrendMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. Volume confirmation ─────────────────────────────────────────────
        if (_options.MomentumTrendMinVolume > 0)
        {
            decimal signalBarVolume = candles[lastIdx].Volume;
            if (signalBarVolume < _options.MomentumTrendMinVolume)
            {
                LogRejection(strategy, "Volume",
                    $"Signal bar volume {signalBarVolume:F0} < minimum {_options.MomentumTrendMinVolume:F0}");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 6. Compute +DI, -DI, ADX for current and previous bars ─────────────
        var (adxCurr, pdiCurr, mdiCurr) = IndicatorCalculator.AdxWithDI(candles, lastIdx, adxPeriod);
        var (adxPrev, pdiPrev, mdiPrev) = IndicatorCalculator.AdxWithDI(candles, lastIdx - 1, adxPeriod);

        // ── 7. ADX must be above threshold and rising ──────────────────────────
        if (adxCurr < adxThreshold)
        {
            LogRejection(strategy, "ADXThreshold",
                $"ADX {adxCurr:F2} < threshold {adxThreshold:F2} — ranging market");
            return Task.FromResult<TradeSignal?>(null);
        }
        if (adxCurr <= adxPrev)
        {
            LogRejection(strategy, "ADXFading",
                $"ADX {adxCurr:F2} <= previous {adxPrev:F2} — trend not strengthening");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 8. DI crossover detection ──────────────────────────────────────────
        TradeDirection? direction = null;
        decimal entryPrice;

        // +DI crosses above -DI → Buy
        if (pdiPrev <= mdiPrev && pdiCurr > mdiCurr)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        // -DI crosses above +DI → Sell
        else if (mdiPrev <= pdiPrev && mdiCurr > pdiCurr)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        bool isBullish = direction == TradeDirection.Buy;

        // ── 9. DI confirmation bars ────────────────────────────────────────────
        if (_options.MomentumTrendConfirmationBars > 0)
        {
            int confirmStart = lastIdx - _options.MomentumTrendConfirmationBars + 1;
            for (int i = confirmStart; i <= lastIdx; i++)
            {
                var (_, pdi, mdi) = IndicatorCalculator.AdxWithDI(candles, i, adxPeriod);
                bool held = isBullish ? pdi > mdi : mdi > pdi;
                if (!held)
                {
                    LogRejection(strategy, "DIConfirmation",
                        $"Bar {lastIdx - i} of {_options.MomentumTrendConfirmationBars} DI not on crossed side — DI cross did not hold");
                    return Task.FromResult<TradeSignal?>(null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 10. RSI exhaustion gate ────────────────────────────────────────────
        bool rsiEnabled = _options.MomentumTrendMaxRsiForBuy > 0 || _options.MomentumTrendMinRsiForSell > 0;
        if (rsiEnabled)
        {
            decimal rsiValue = IndicatorCalculator.Rsi(candles, lastIdx, _options.MomentumTrendRsiPeriod);
            if (isBullish && _options.MomentumTrendMaxRsiForBuy > 0
                && rsiValue > _options.MomentumTrendMaxRsiForBuy)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} > max {_options.MomentumTrendMaxRsiForBuy:F2} — overbought buy rejected");
                return Task.FromResult<TradeSignal?>(null);
            }
            if (!isBullish && _options.MomentumTrendMinRsiForSell > 0
                && rsiValue < _options.MomentumTrendMinRsiForSell)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} < min {_options.MomentumTrendMinRsiForSell:F2} — oversold sell rejected");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 11. Trend MA alignment ─────────────────────────────────────────────
        if (_options.MomentumTrendTrendMaPeriod > 0)
        {
            decimal trendEma     = IndicatorCalculator.Ema(candles, lastIdx, _options.MomentumTrendTrendMaPeriod);
            decimal currentClose = candles[lastIdx].Close;
            bool trendAligned    = isBullish
                ? currentClose > trendEma
                : currentClose < trendEma;
            if (!trendAligned)
            {
                LogRejection(strategy, "TrendMA",
                    $"{direction} rejected — close {currentClose:F5} not aligned with {_options.MomentumTrendTrendMaPeriod}-bar EMA ({trendEma:F5})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 12. Slippage buffer ────────────────────────────────────────────────
        if (_options.MomentumTrendSlippageAtrFraction > 0)
        {
            decimal slippageOffset = atr * _options.MomentumTrendSlippageAtrFraction;
            entryPrice += isBullish ? slippageOffset : -slippageOffset;
        }

        // ── 13. Stop-loss calculation ──────────────────────────────────────────
        decimal stopDistance;
        if (_options.MomentumTrendSwingSlEnabled)
        {
            decimal swingPoint = isBullish
                ? IndicatorCalculator.FindSwingLow(candles, lastIdx, _options.MomentumTrendSwingSlLookbackBars)
                : IndicatorCalculator.FindSwingHigh(candles, lastIdx, _options.MomentumTrendSwingSlLookbackBars);

            decimal swingBuffer  = atr * _options.MomentumTrendSwingSlBufferAtrFraction;
            decimal rawSwingStop = isBullish
                ? entryPrice - (swingPoint - swingBuffer)
                : swingPoint + swingBuffer - entryPrice;

            decimal minStop = atr * _options.MomentumTrendSwingSlMinAtrMultiplier;
            decimal maxStop = atr * _options.MomentumTrendSwingSlMaxAtrMultiplier;
            stopDistance = Math.Clamp(rawSwingStop, minStop, maxStop);
        }
        else
        {
            stopDistance = atr * _options.StopLossAtrMultiplier;
        }

        // ── 14. Take-profit calculation ────────────────────────────────────────
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

        decimal stopLoss, takeProfit;
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

        // ── 15. Minimum risk-reward ratio ──────────────────────────────────────
        if (_options.MomentumTrendMinRiskRewardRatio > 0 && stopDistance > 0)
        {
            decimal riskReward = profitDistance / stopDistance;
            if (riskReward < _options.MomentumTrendMinRiskRewardRatio)
            {
                LogRejection(strategy, "RiskReward",
                    $"R:R {riskReward:F2} < minimum {_options.MomentumTrendMinRiskRewardRatio:F2} (SL={stopDistance:F6}, TP={profitDistance:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 16. Dynamic confidence scoring ─────────────────────────────────────
        // Confidence scales with how far ADX exceeds the threshold (stronger trend = higher confidence)
        decimal adxBonus = Math.Min(
            (adxCurr - adxThreshold) / 50m * _options.MomentumTrendConfidenceAdxBoostMax,
            _options.MomentumTrendConfidenceAdxBoostMax);
        decimal confidence = Math.Clamp(_options.MomentumTrendConfidence + adxBonus, 0m, 1m);

        // ── 17. Lot sizing ─────────────────────────────────────────────────────
        decimal lotSize = _options.DefaultLotSize;
        if (_options.MomentumTrendConfidenceLotSizing && _options.MomentumTrendMaxLotSize > _options.MomentumTrendMinLotSize)
        {
            lotSize = _options.MomentumTrendMinLotSize
                + confidence * (_options.MomentumTrendMaxLotSize - _options.MomentumTrendMinLotSize);
            lotSize = Math.Clamp(lotSize, _options.MomentumTrendMinLotSize, _options.MomentumTrendMaxLotSize);
        }

        // ── 18. Emit signal ────────────────────────────────────────────────────
        // Use the last closed candle's timestamp so backtests get simulated
        // timestamps rather than wallclock time.
        var now = candles[lastIdx].Timestamp;
        var signal = new TradeSignal
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
            ExpiresAt        = now.AddMinutes(_options.MomentumTrendExpiryMinutes)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag);

        return Task.FromResult<TradeSignal?>(signal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Rejection diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));

        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "MomentumTrend signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private static (int AdxPeriod, decimal AdxThreshold) ParseParameters(string? json)
    {
        int     adxPeriod    = 14;
        decimal adxThreshold = 25m;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("AdxPeriod",    out var ap) && ap.TryGetInt32(out var apv))   adxPeriod    = apv;
            if (root.TryGetProperty("AdxThreshold", out var at) && at.TryGetDecimal(out var atv)) adxThreshold = atv;
        }
        catch (JsonException) { }

        return (Math.Clamp(adxPeriod, 2, 200), Math.Clamp(adxThreshold, 10m, 80m));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsCandleOrderValid(IReadOnlyList<Candle> candles)
    {
        int check = Math.Min(candles.Count - 1, 3);
        for (int i = candles.Count - check; i < candles.Count; i++)
        {
            if (candles[i].Timestamp <= candles[i - 1].Timestamp)
                return false;
        }
        return true;
    }
}
