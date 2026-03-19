using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class BreakoutScalperEvaluator : IStrategyEvaluator
{
    public StrategyType StrategyType => StrategyType.BreakoutScalper;

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
        catch { /* use defaults */ }

        // Need lookbackBars candles for ATR + 1 extra for first TR calculation
        if (candles.Count < lookbackBars + 1)
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

        var now = DateTime.UtcNow;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = null,
            TakeProfit       = null,
            SuggestedLotSize = 0.01m,
            Confidence       = 0.65m,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(15)
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
