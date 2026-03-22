using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class BreakoutScalperEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public BreakoutScalperEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.BreakoutScalper;

    public int MinRequiredCandles(Strategy strategy)
    {
        int lookbackBars = 20;
        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            if (doc.RootElement.TryGetProperty("LookbackBars", out var lb) && lb.TryGetInt32(out var lbVal))
                lookbackBars = lbVal;
        }
        catch { /* use default */ }

        return Math.Max(lookbackBars, _options.AtrPeriodForSlTp) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int     lookbackBars       = 20;
        decimal breakoutMultiplier = 1.5m;

        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("LookbackBars",        out var lb) && lb.TryGetInt32(out var lbVal))         lookbackBars       = lbVal;
            if (root.TryGetProperty("BreakoutMultiplier",  out var bm) && bm.TryGetDecimal(out var bmVal))       breakoutMultiplier = bmVal;
        }
        catch (JsonException) { }

        lookbackBars       = Math.Clamp(lookbackBars, 2, 500);
        breakoutMultiplier = Math.Clamp(breakoutMultiplier, 0.1m, 10m);

        int requiredCandles = Math.Max(lookbackBars, _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < requiredCandles)
            return Task.FromResult<TradeSignal?>(null);

        int lastIndex = candles.Count - 1;

        // Calculate ATR over lookbackBars
        decimal atr = CalculateAtr(candles, lastIndex, lookbackBars);

        // Find N-bar high and low over the last lookbackBars candles (excluding current)
        decimal nBarHigh = decimal.MinValue;
        decimal nBarLow  = decimal.MaxValue;
        for (int i = lastIndex - lookbackBars; i < lastIndex; i++)
        {
            if (candles[i].High > nBarHigh) nBarHigh = candles[i].High;
            if (candles[i].Low  < nBarLow)  nBarLow  = candles[i].Low;
        }

        decimal threshold = atr * breakoutMultiplier * 0.1m;

        TradeDirection? direction = null;
        decimal entryPrice;

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

        // ATR-based stop-loss and take-profit
        decimal slAtr = CalculateAtr(candles, lastIndex, _options.AtrPeriodForSlTp);
        if (slAtr <= 0) return Task.FromResult<TradeSignal?>(null); // degenerate data
        decimal stopDistance   = slAtr * _options.StopLossAtrMultiplier;
        decimal profitDistance = slAtr * _options.TakeProfitAtrMultiplier;

        decimal? stopLoss;
        decimal? takeProfit;
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

        var now = DateTime.UtcNow;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = _options.DefaultLotSize,
            Confidence       = _options.BreakoutConfidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.BreakoutExpiryMinutes)
        };

        return Task.FromResult<TradeSignal?>(signal);
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sumTr = 0m;
        int     start = endIndex - period + 1;

        for (int i = start; i <= endIndex; i++)
        {
            decimal high = candles[i].High;
            decimal low  = candles[i].Low;
            decimal prevClose = candles[i - 1].Close;

            decimal tr = Math.Max(high - low,
                         Math.Max(Math.Abs(high - prevClose),
                                  Math.Abs(low  - prevClose)));
            sumTr += tr;
        }

        return sumTr / period;
    }
}
