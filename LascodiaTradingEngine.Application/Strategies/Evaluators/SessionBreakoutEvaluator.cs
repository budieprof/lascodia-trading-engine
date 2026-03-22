using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Time-aware breakout strategy that trades the break of a prior session's range
/// during the opening of a new session. Classic examples: Asian range breakout at
/// London open, or London range breakout at New York open.
///
/// Logic:
/// 1. Define the "range session" (default: Asian, 00:00–08:00 UTC) and find its High/Low.
/// 2. During the "breakout session" (default: London, 08:00–17:00 UTC), wait for price
///    to break above the range high or below the range low by a configurable ATR threshold.
/// 3. Confirm the breakout bar closes beyond the level (not just a wick).
/// 4. Generate signal with ATR-based SL/TP.
/// </summary>
public class SessionBreakoutEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public SessionBreakoutEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.SessionBreakout;

    public int MinRequiredCandles(Strategy strategy)
    {
        // Need enough candles to cover the range session + some breakout session bars + ATR
        // For H1: Asian = 8 bars, plus a few London bars, plus ATR14 = ~25
        // For M15: Asian = 32 bars, plus some = ~50
        // Use a generous 60 to be safe across timeframes
        return Math.Max(60, _options.AtrPeriodForSlTp + 1);
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int rangeStartHourUtc  = 0;   // Asian session start
        int rangeEndHourUtc    = 8;   // Asian session end
        int breakoutStartHour  = 8;   // London open
        int breakoutEndHour    = 12;  // Only trade breakouts in early London
        decimal thresholdMultiplier = 0.3m; // ATR fraction above/below range for confirmation

        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("RangeStartHourUtc",     out var rs) && rs.TryGetInt32(out var rsv))     rangeStartHourUtc     = rsv;
            if (root.TryGetProperty("RangeEndHourUtc",        out var re) && re.TryGetInt32(out var rev))     rangeEndHourUtc       = rev;
            if (root.TryGetProperty("BreakoutStartHour",      out var bs) && bs.TryGetInt32(out var bsv))     breakoutStartHour     = bsv;
            if (root.TryGetProperty("BreakoutEndHour",        out var be) && be.TryGetInt32(out var bev))     breakoutEndHour       = bev;
            if (root.TryGetProperty("ThresholdMultiplier",    out var tm) && tm.TryGetDecimal(out var tmv))   thresholdMultiplier   = tmv;
        }
        catch { /* defaults */ }

        rangeStartHourUtc   = Math.Clamp(rangeStartHourUtc, 0, 23);
        rangeEndHourUtc     = Math.Clamp(rangeEndHourUtc, 0, 23);
        breakoutStartHour   = Math.Clamp(breakoutStartHour, 0, 23);
        breakoutEndHour     = Math.Clamp(breakoutEndHour, 0, 24);
        thresholdMultiplier = Math.Clamp(thresholdMultiplier, 0m, 5m);

        if (candles.Count < _options.AtrPeriodForSlTp + 1)
            return Task.FromResult<TradeSignal?>(null);

        int last = candles.Count - 1;
        var lastCandle = candles[last];
        int lastHour = lastCandle.Timestamp.Hour;

        // Only evaluate during the breakout window
        if (lastHour < breakoutStartHour || lastHour >= breakoutEndHour)
            return Task.FromResult<TradeSignal?>(null);

        // Find the range session candles (from today or the most recent occurrence)
        decimal rangeHigh = decimal.MinValue;
        decimal rangeLow  = decimal.MaxValue;
        bool    rangeFound = false;

        for (int i = last; i >= 0; i--)
        {
            int hour = candles[i].Timestamp.Hour;
            bool inRange = rangeStartHourUtc <= rangeEndHourUtc
                ? hour >= rangeStartHourUtc && hour < rangeEndHourUtc
                : hour >= rangeStartHourUtc || hour < rangeEndHourUtc; // overnight range

            if (inRange)
            {
                if (candles[i].High > rangeHigh) rangeHigh = candles[i].High;
                if (candles[i].Low  < rangeLow)  rangeLow  = candles[i].Low;
                rangeFound = true;
            }
            else if (rangeFound)
            {
                break; // We've left the range session — stop looking
            }
        }

        if (!rangeFound || rangeHigh == decimal.MinValue || rangeLow == decimal.MaxValue)
            return Task.FromResult<TradeSignal?>(null);

        decimal rangeSize = rangeHigh - rangeLow;
        if (rangeSize <= 0)
            return Task.FromResult<TradeSignal?>(null);

        // ATR for threshold and SL/TP
        decimal atr       = CalculateAtr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);
        decimal threshold = atr * thresholdMultiplier;

        TradeDirection? direction = null;
        decimal entryPrice;

        // Breakout above range high — close must be above, not just a wick
        if (lastCandle.Close > rangeHigh + threshold && currentPrice.Ask > rangeHigh + threshold)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        // Breakout below range low
        else if (lastCandle.Close < rangeLow - threshold && currentPrice.Bid < rangeLow - threshold)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

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

        // Confidence scales with how clean the breakout is relative to range size
        decimal breakoutDistance = direction == TradeDirection.Buy
            ? currentPrice.Ask - rangeHigh
            : rangeLow - currentPrice.Bid;
        decimal confidence = Math.Clamp(
            _options.SessionBreakoutConfidence + (breakoutDistance / rangeSize) * 0.15m,
            0m, 1m);

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
            ExpiresAt        = now.AddMinutes(_options.SessionBreakoutExpiryMinutes)
        });
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sumTr = 0m;
        for (int i = endIndex - period + 1; i <= endIndex; i++)
        {
            decimal prevClose = candles[i - 1].Close;
            decimal tr = Math.Max(candles[i].High - candles[i].Low,
                         Math.Max(Math.Abs(candles[i].High - prevClose),
                                  Math.Abs(candles[i].Low  - prevClose)));
            sumTr += tr;
        }
        return sumTr / period;
    }
}
