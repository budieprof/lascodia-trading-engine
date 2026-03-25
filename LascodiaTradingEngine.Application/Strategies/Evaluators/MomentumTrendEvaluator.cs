using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

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
/// This evaluator intentionally avoids ranging markets (ADX &lt; 25) where directional
/// movement signals produce whipsaws.
/// </summary>
public class MomentumTrendEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public MomentumTrendEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.MomentumTrend;

    public int MinRequiredCandles(Strategy strategy)
    {
        int adxPeriod = 14;
        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            if (doc.RootElement.TryGetProperty("AdxPeriod", out var p) && p.TryGetInt32(out var pv))
                adxPeriod = pv;
        }
        catch { /* default */ }

        // ADX needs 2×period for smoothing + 1, plus ATR for SL/TP
        return Math.Max(adxPeriod * 2 + 1, _options.AtrPeriodForSlTp) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int     adxPeriod    = 14;
        decimal adxThreshold = 25m;

        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("AdxPeriod",    out var ap) && ap.TryGetInt32(out var apv))     adxPeriod    = apv;
            if (root.TryGetProperty("AdxThreshold", out var at) && at.TryGetDecimal(out var atv))   adxThreshold = atv;
        }
        catch { /* defaults */ }

        adxPeriod    = Math.Clamp(adxPeriod, 2, 200);
        adxThreshold = Math.Clamp(adxThreshold, 10m, 80m);

        int required = Math.Max(adxPeriod * 2 + 1, _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < required)
            return Task.FromResult<TradeSignal?>(null);

        int last = candles.Count - 1;

        // Compute +DI, -DI, ADX for current and previous bars
        var (adxCurr, pdiCurr, mdiCurr) = IndicatorCalculator.AdxWithDI(candles, last, adxPeriod);
        var (adxPrev, pdiPrev, mdiPrev) = IndicatorCalculator.AdxWithDI(candles, last - 1, adxPeriod);

        // ADX must be above threshold and rising
        if (adxCurr < adxThreshold || adxCurr <= adxPrev)
            return Task.FromResult<TradeSignal?>(null);

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

        // ATR-based SL/TP
        decimal atr           = IndicatorCalculator.Atr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

        decimal? stopLoss, takeProfit;
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

        // Confidence scales with ADX strength (stronger trend = higher confidence)
        decimal adxBonus = Math.Min((adxCurr - adxThreshold) / 50m, 0.20m);
        decimal confidence = Math.Clamp(_options.MomentumTrendConfidence + adxBonus, 0m, 1m);

        var now = DateTime.UtcNow;
        return Task.FromResult<TradeSignal?>(new TradeSignal
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
            ExpiresAt        = now.AddMinutes(_options.MomentumTrendExpiryMinutes)
        });
    }
}
