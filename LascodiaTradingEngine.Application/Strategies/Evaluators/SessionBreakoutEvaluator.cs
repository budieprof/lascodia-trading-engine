using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Time-aware breakout strategy that trades the break of a prior session's range
/// during the opening of a new session. Classic examples: Asian range breakout at
/// London open, or London range breakout at New York open.
///
/// Filter pipeline (in order):
/// 1. Candle ordering sanity check
/// 2. Minimum candle count
/// 3. Breakout window time gate
/// 4. Session range discovery (high/low)
/// 5. Range size validation (minimum ATR fraction)
/// 6. ATR calculation + zero guard
/// 7. Gap detection (overnight gap vs ATR)
/// 8. Breakout direction detection (close + price confirmation)
/// 9. Multi-bar confirmation
/// 10. ADX trend-strength filter
/// 11. Volume confirmation
/// 12. Spread safety guard
/// 13. Slippage buffer
/// 14. ATR-based SL/TP
/// 15. Risk-reward ratio validation
/// 16. Dynamic confidence scoring
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class SessionBreakoutEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<SessionBreakoutEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "SessionBreakout");

    public SessionBreakoutEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<SessionBreakoutEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.SessionBreakout;

    public int MinRequiredCandles(Strategy strategy)
    {
        int adxRequirement = _options.SessionBreakoutMinAdx > 0 ? _options.SessionBreakoutAdxPeriod * 2 : 0;
        int confirmRequirement = _options.SessionBreakoutConfirmationBars;
        int baseCandles = Math.Max(
            Math.Max(60, _options.AtrPeriodForSlTp),
            adxRequirement);
        return baseCandles + 1 + confirmRequirement;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParameters(strategy.ParametersJson);

        // ── 1. Candle ordering sanity check ────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 2. Minimum candle count ────────────────────────────────────────
        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        int lastIdx = candles.Count - 1;
        var lastCandle = candles[lastIdx];
        int lastHour = lastCandle.Timestamp.Hour;

        // ── 3. Breakout window time gate ───────────────────────────────────
        if (lastHour < parameters.BreakoutStartHour || lastHour >= parameters.BreakoutEndHour)
            return Task.FromResult<TradeSignal?>(null);

        // ── 4. Session range discovery ─────────────────────────────────────
        decimal rangeHigh = decimal.MinValue;
        decimal rangeLow  = decimal.MaxValue;
        bool    rangeFound = false;

        for (int i = lastIdx; i >= 0; i--)
        {
            int hour = candles[i].Timestamp.Hour;
            bool inRange = parameters.RangeStartHourUtc <= parameters.RangeEndHourUtc
                ? hour >= parameters.RangeStartHourUtc && hour < parameters.RangeEndHourUtc
                : hour >= parameters.RangeStartHourUtc || hour < parameters.RangeEndHourUtc;

            if (inRange)
            {
                if (candles[i].High > rangeHigh) rangeHigh = candles[i].High;
                if (candles[i].Low  < rangeLow)  rangeLow  = candles[i].Low;
                rangeFound = true;
            }
            else if (rangeFound)
            {
                break;
            }
        }

        if (!rangeFound || rangeHigh == decimal.MinValue || rangeLow == decimal.MaxValue)
        {
            LogRejection(strategy, "NoRange", "Could not find session range candles");
            return Task.FromResult<TradeSignal?>(null);
        }

        decimal rangeSize = rangeHigh - rangeLow;
        if (rangeSize <= 0)
        {
            LogRejection(strategy, "DegenerateRange", "Range high equals range low — no tradable range");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. ATR calculation + zero guard ────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 6. Range size validation ───────────────────────────────────────
        if (_options.SessionBreakoutMinRangeSizeAtrFraction > 0 && rangeSize < atr * _options.SessionBreakoutMinRangeSizeAtrFraction)
        {
            LogRejection(strategy, "RangeTooNarrow",
                $"Range size {rangeSize:F6} < {_options.SessionBreakoutMinRangeSizeAtrFraction:P0} of ATR ({atr * _options.SessionBreakoutMinRangeSizeAtrFraction:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 7. Gap detection ───────────────────────────────────────────────
        if (_options.SessionBreakoutMaxGapAtrFraction > 0 && lastIdx >= 1)
        {
            decimal gap          = Math.Abs(candles[lastIdx].Open - candles[lastIdx - 1].Close);
            decimal gapThreshold = atr * _options.SessionBreakoutMaxGapAtrFraction;
            if (gap > gapThreshold)
            {
                LogRejection(strategy, "Gap",
                    $"Price gap {gap:F6} > {_options.SessionBreakoutMaxGapAtrFraction:F1}× ATR ({gapThreshold:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        decimal threshold = atr * parameters.ThresholdMultiplier;

        // ── 8. Breakout direction detection ────────────────────────────────
        TradeDirection? direction = null;
        decimal entryPrice;

        if (lastCandle.Close > rangeHigh + threshold && currentPrice.Ask > rangeHigh + threshold)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        else if (lastCandle.Close < rangeLow - threshold && currentPrice.Bid < rangeLow - threshold)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 9. Multi-bar confirmation ──────────────────────────────────────
        if (_options.SessionBreakoutConfirmationBars > 0)
        {
            int confirmStart = lastIdx - _options.SessionBreakoutConfirmationBars + 1;
            if (confirmStart < 0) confirmStart = 0;
            for (int i = confirmStart; i <= lastIdx; i++)
            {
                bool held = direction == TradeDirection.Buy
                    ? candles[i].Close > rangeHigh
                    : candles[i].Close < rangeLow;
                if (!held)
                {
                    LogRejection(strategy, "Confirmation",
                        $"Bar {lastIdx - i} of {_options.SessionBreakoutConfirmationBars} did not close on breakout side — price snapped back");
                    return Task.FromResult<TradeSignal?>(null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 10. ADX trend-strength filter ──────────────────────────────────
        if (_options.SessionBreakoutMinAdx > 0)
        {
            decimal adxValue = IndicatorCalculator.Adx(candles, lastIdx, _options.SessionBreakoutAdxPeriod);
            if (adxValue < _options.SessionBreakoutMinAdx)
            {
                LogRejection(strategy, "ADX",
                    $"ADX {adxValue:F2} < minimum {_options.SessionBreakoutMinAdx:F2} — ranging market, breakout likely to fail");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 11. Volume confirmation ────────────────────────────────────────
        if (_options.SessionBreakoutMinVolume > 0)
        {
            decimal signalBarVolume = candles[lastIdx].Volume;
            if (signalBarVolume < _options.SessionBreakoutMinVolume)
            {
                LogRejection(strategy, "Volume",
                    $"Signal bar volume {signalBarVolume:F0} < minimum {_options.SessionBreakoutMinVolume:F0}");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 12. Spread safety guard ────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.SessionBreakoutMaxSpreadAtrFraction > 0 && spread > atr * _options.SessionBreakoutMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.SessionBreakoutMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 13. Slippage buffer ────────────────────────────────────────────
        if (_options.SessionBreakoutSlippageAtrFraction > 0)
        {
            decimal slippageOffset = atr * _options.SessionBreakoutSlippageAtrFraction;
            entryPrice += direction == TradeDirection.Buy ? slippageOffset : -slippageOffset;
        }

        // ── 14. ATR-based SL/TP ───────────────────────────────────────────
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

        decimal stopLoss, takeProfit;
        if (direction == TradeDirection.Buy)
        {
            stopLoss   = entryPrice - stopDistance;
            takeProfit = entryPrice + profitDistance;
        }
        else
        {
            stopLoss   = entryPrice + stopDistance;
            takeProfit = entryPrice - profitDistance;
        }

        // ── 15. Risk-reward ratio validation ───────────────────────────────
        if (_options.SessionBreakoutMinRiskRewardRatio > 0)
        {
            decimal slDist     = Math.Abs(entryPrice - stopLoss);
            decimal tpDist     = Math.Abs(takeProfit - entryPrice);
            decimal riskReward = slDist > 0 ? tpDist / slDist : 0m;
            if (riskReward < _options.SessionBreakoutMinRiskRewardRatio)
            {
                LogRejection(strategy, "RiskReward",
                    $"R:R {riskReward:F2} < minimum {_options.SessionBreakoutMinRiskRewardRatio:F2} (SL={slDist:F6}, TP={tpDist:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 16. Dynamic confidence scoring ─────────────────────────────────
        decimal breakoutDistance = direction == TradeDirection.Buy
            ? currentPrice.Ask - rangeHigh
            : rangeLow - currentPrice.Bid;
        decimal breachBoost = rangeSize > 0
            ? Math.Min(breakoutDistance / rangeSize * _options.SessionBreakoutConfidenceBreachBoostMax,
                       _options.SessionBreakoutConfidenceBreachBoostMax)
            : 0m;
        decimal confidence = Math.Clamp(_options.SessionBreakoutConfidence + breachBoost, 0m, 1m);

        var now = candles[lastIdx].Timestamp;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = _options.DefaultLotSize,
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.SessionBreakoutExpiryMinutes)
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
            "SessionBreakout signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private static SessionBreakoutParameters ParseParameters(string? json)
    {
        int     rangeStartHourUtc    = 0;
        int     rangeEndHourUtc      = 8;
        int     breakoutStartHour    = 8;
        int     breakoutEndHour      = 12;
        decimal thresholdMultiplier  = 0.3m;

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("RangeStartHourUtc",  out var rs) && rs.TryGetInt32(out var rsv))   rangeStartHourUtc    = rsv;
            if (root.TryGetProperty("RangeEndHourUtc",    out var re) && re.TryGetInt32(out var rev))   rangeEndHourUtc      = rev;
            if (root.TryGetProperty("BreakoutStartHour",  out var bs) && bs.TryGetInt32(out var bsv))   breakoutStartHour    = bsv;
            if (root.TryGetProperty("BreakoutEndHour",    out var be) && be.TryGetInt32(out var bev))   breakoutEndHour      = bev;
            if (root.TryGetProperty("ThresholdMultiplier", out var tm) && tm.TryGetDecimal(out var tmv)) thresholdMultiplier  = tmv;
        }
        catch (JsonException) { }

        return new SessionBreakoutParameters(
            Math.Clamp(rangeStartHourUtc, 0, 23),
            Math.Clamp(rangeEndHourUtc, 0, 23),
            Math.Clamp(breakoutStartHour, 0, 23),
            Math.Clamp(breakoutEndHour, 0, 24),
            Math.Clamp(thresholdMultiplier, 0m, 5m));
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

    private readonly record struct SessionBreakoutParameters(
        int RangeStartHourUtc,
        int RangeEndHourUtc,
        int BreakoutStartHour,
        int BreakoutEndHour,
        decimal ThresholdMultiplier);
}
