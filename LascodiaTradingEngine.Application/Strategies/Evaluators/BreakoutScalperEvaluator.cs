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
/// Evaluates breakout scalping signals by detecting price breaks above/below a lookback-period
/// high/low channel with optional ADX and RSI confirmation. Produces short-duration trade
/// signals with ATR-based stop-loss and take-profit levels.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class BreakoutScalperEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<BreakoutScalperEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "BreakoutScalper");

    public BreakoutScalperEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<BreakoutScalperEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.BreakoutScalper;

    public int MinRequiredCandles(Strategy strategy)
    {
        var (lookbackBars, _) = ParseParameters(strategy.ParametersJson);
        int adxRequirement    = _options.BreakoutMinAdx > 0 ? _options.BreakoutAdxPeriod * 2 : 0;
        bool rsiEnabled       = _options.BreakoutMaxRsiForBuy > 0 || _options.BreakoutMinRsiForSell > 0;
        int rsiRequirement    = rsiEnabled ? _options.BreakoutRsiPeriod * 2 : 0;
        int trendRequirement  = _options.BreakoutTrendMaPeriod > 0 ? _options.BreakoutTrendMaPeriod : 0;
        int confirmRequirement = _options.BreakoutConfirmationBars;
        int baseCandles = Math.Max(
            Math.Max(lookbackBars, _options.AtrPeriodForSlTp),
            Math.Max(adxRequirement, Math.Max(rsiRequirement, trendRequirement)));
        return baseCandles + 1 + confirmRequirement;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var (lookbackBars, breakoutMultiplier) = ParseParameters(strategy.ParametersJson);
        int lastIdx = candles.Count - 1;

        // ── 1. Candle ordering sanity check ──────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        // ── 2. ATR (Wilder) — used by all subsequent filters ─────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 3. Gap detection — reject if prior bar gapped abnormally ─────────
        // Large gaps distort the N-bar range and inflate apparent breakouts.
        if (_options.BreakoutMaxGapAtrFraction > 0 && lastIdx >= 1)
        {
            decimal gap          = Math.Abs(candles[lastIdx].Open - candles[lastIdx - 1].Close);
            decimal gapThreshold = atr * _options.BreakoutMaxGapAtrFraction;
            if (gap > gapThreshold)
            {
                LogRejection(strategy, "Gap",
                    $"Price gap {gap:F6} > {_options.BreakoutMaxGapAtrFraction:F1}× ATR ({gapThreshold:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 4. N-bar high/low and breakout detection ─────────────────────────
        decimal nBarHigh = decimal.MinValue;
        decimal nBarLow  = decimal.MaxValue;
        for (int i = lastIdx - lookbackBars; i < lastIdx; i++)
        {
            if (candles[i].High > nBarHigh) nBarHigh = candles[i].High;
            if (candles[i].Low  < nBarLow)  nBarLow  = candles[i].Low;
        }

        // Confirmation threshold: price must clear the level by a fraction of ATR
        // to avoid triggering on a single pip poke above the range boundary.
        decimal threshold = atr * breakoutMultiplier * 0.1m;

        TradeDirection direction;
        decimal        entryPrice;

        if (currentPrice.Ask > nBarHigh + threshold)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        else if (currentPrice.Bid < nBarLow - threshold)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. Confirmation bars ──────────────────────────────────────────────
        // Each of the last N closed bars must have closed on the breakout side of
        // the level. A breakout that snapped back within those bars is rejected.
        if (_options.BreakoutConfirmationBars > 0)
        {
            int confirmStart = lastIdx - _options.BreakoutConfirmationBars + 1;
            for (int i = confirmStart; i <= lastIdx; i++)
            {
                bool held = direction == TradeDirection.Buy
                    ? candles[i].Close > nBarHigh
                    : candles[i].Close < nBarLow;
                if (!held)
                {
                    LogRejection(strategy, "Confirmation",
                        $"Bar {lastIdx - i} of {_options.BreakoutConfirmationBars} did not close on breakout side — price snapped back");
                    return Task.FromResult<TradeSignal?>(null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 6. ADX trend-strength filter ──────────────────────────────────────
        // Breakouts in ranging markets (low ADX) frequently fail and reverse.
        if (_options.BreakoutMinAdx > 0)
        {
            decimal adxValue = IndicatorCalculator.Adx(candles, lastIdx, _options.BreakoutAdxPeriod);
            if (adxValue < _options.BreakoutMinAdx)
            {
                LogRejection(strategy, "ADX",
                    $"ADX {adxValue:F2} < minimum {_options.BreakoutMinAdx:F2} — ranging market, breakout likely to fail");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 7. Trend MA alignment ─────────────────────────────────────────────
        // Breakouts against the macro trend have significantly lower win rates.
        if (_options.BreakoutTrendMaPeriod > 0)
        {
            decimal trendEma    = IndicatorCalculator.Ema(candles, lastIdx, _options.BreakoutTrendMaPeriod);
            decimal currentClose = candles[lastIdx].Close;
            bool trendAligned   = direction == TradeDirection.Buy
                ? currentClose > trendEma
                : currentClose < trendEma;
            if (!trendAligned)
            {
                LogRejection(strategy, "TrendMA",
                    $"{direction} rejected — close {currentClose:F5} not aligned with {_options.BreakoutTrendMaPeriod}-bar EMA ({trendEma:F5})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 8. RSI overbought/oversold gate ───────────────────────────────────
        // Prevents entering a breakout when momentum is already exhausted.
        bool rsiEnabled = _options.BreakoutMaxRsiForBuy > 0 || _options.BreakoutMinRsiForSell > 0;
        if (rsiEnabled)
        {
            decimal rsiValue = IndicatorCalculator.Rsi(candles, lastIdx, _options.BreakoutRsiPeriod);
            if (direction == TradeDirection.Buy && _options.BreakoutMaxRsiForBuy > 0
                && rsiValue > _options.BreakoutMaxRsiForBuy)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} > max {_options.BreakoutMaxRsiForBuy:F2} — overbought buy rejected");
                return Task.FromResult<TradeSignal?>(null);
            }
            if (direction == TradeDirection.Sell && _options.BreakoutMinRsiForSell > 0
                && rsiValue < _options.BreakoutMinRsiForSell)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} < min {_options.BreakoutMinRsiForSell:F2} — oversold sell rejected");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 10. Volume confirmation ───────────────────────────────────────────
        // A genuine breakout should be accompanied by elevated volume.
        if (_options.BreakoutMinVolume > 0)
        {
            decimal signalBarVolume = candles[lastIdx].Volume;
            if (signalBarVolume < _options.BreakoutMinVolume)
            {
                LogRejection(strategy, "Volume",
                    $"Signal bar volume {signalBarVolume:F0} < minimum {_options.BreakoutMinVolume:F0}");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 11. Spread safety guard ───────────────────────────────────────────
        // Wide spreads (during news or thin markets) make scalper entries unprofitable.
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.BreakoutMaxSpreadAtrFraction > 0 && spread > atr * _options.BreakoutMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.BreakoutMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 12. Slippage buffer ───────────────────────────────────────────────
        if (_options.BreakoutSlippageAtrFraction > 0)
        {
            decimal slippageOffset = atr * _options.BreakoutSlippageAtrFraction;
            entryPrice += direction == TradeDirection.Buy ? slippageOffset : -slippageOffset;
        }

        // ── 13. ATR-based stop-loss and take-profit ───────────────────────────
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

        // ── 14. Minimum risk-reward ratio ─────────────────────────────────────
        if (_options.BreakoutMinRiskRewardRatio > 0)
        {
            decimal slDist     = Math.Abs(entryPrice - stopLoss);
            decimal tpDist     = Math.Abs(takeProfit - entryPrice);
            decimal riskReward = slDist > 0 ? tpDist / slDist : 0m;
            if (riskReward < _options.BreakoutMinRiskRewardRatio)
            {
                LogRejection(strategy, "RiskReward",
                    $"R:R {riskReward:F2} < minimum {_options.BreakoutMinRiskRewardRatio:F2} (SL={slDist:F6}, TP={tpDist:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 15. Dynamic confidence scoring ───────────────────────────────────
        // Confidence scales with how far price has breached the level.
        // A shallow poke at the boundary gets base confidence; a deep thrust
        // beyond the range gets base + up to BreakoutConfidenceBreachBoostMax.
        decimal breachDistance = direction == TradeDirection.Buy
            ? currentPrice.Ask - nBarHigh
            : nBarLow - currentPrice.Bid;
        decimal breachBoost = atr > 0
            ? Math.Min(breachDistance / atr * _options.BreakoutConfidenceBreachBoostMax,
                       _options.BreakoutConfidenceBreachBoostMax)
            : 0m;
        decimal confidence = Math.Clamp(_options.BreakoutConfidence + breachBoost, 0m, 1m);

        // Use wallclock time so GeneratedAt/ExpiresAt are accurate in live mode.
        // On higher timeframes the candle open can lag by hours, causing born-expired signals.
        var now = DateTime.UtcNow;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = _options.DefaultLotSize,
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.BreakoutExpiryMinutes)
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
            "BreakoutScalper signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private static (int LookbackBars, decimal BreakoutMultiplier) ParseParameters(string? json)
    {
        int     lookbackBars       = 20;
        decimal breakoutMultiplier = 1.5m;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("LookbackBars",       out var lb) && lb.TryGetInt32(out var lbVal))   lookbackBars       = lbVal;
            if (root.TryGetProperty("BreakoutMultiplier", out var bm) && bm.TryGetDecimal(out var bmVal)) breakoutMultiplier = bmVal;
        }
        catch (JsonException) { }

        return (Math.Clamp(lookbackBars, 2, 500), Math.Clamp(breakoutMultiplier, 0.1m, 10m));
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
