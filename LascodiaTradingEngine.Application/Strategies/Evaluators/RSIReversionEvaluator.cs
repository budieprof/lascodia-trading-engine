using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class RSIReversionEvaluator : IStrategyEvaluator
{
    public StrategyType StrategyType => StrategyType.RSIReversion;

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int     period     = 14;
        decimal oversold   = 30m;
        decimal overbought = 70m;

        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("Period",     out var p)  && p.TryGetInt32(out var pVal))       period     = pVal;
            if (root.TryGetProperty("Oversold",   out var os) && os.TryGetDecimal(out var osVal))   oversold   = osVal;
            if (root.TryGetProperty("Overbought", out var ob) && ob.TryGetDecimal(out var obVal))   overbought = obVal;
        }
        catch { /* use defaults */ }

        int requiredCandles = period + 1;
        if (candles.Count < requiredCandles)
            return Task.FromResult<TradeSignal?>(null);

        decimal currentRsi = CalculateRsi(candles, candles.Count - 1, period);
        decimal prevRsi    = CalculateRsi(candles, candles.Count - 2, period);

        TradeDirection? direction = null;
        decimal entryPrice;
        decimal confidence;

        if (prevRsi <= oversold && currentRsi > oversold)
        {
            // Exits oversold — Buy signal
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
            confidence = Math.Min(1.0m, (oversold - prevRsi) / oversold);
        }
        else if (prevRsi >= overbought && currentRsi < overbought)
        {
            // Exits overbought — Sell signal
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
            confidence = Math.Min(1.0m, (prevRsi - overbought) / (100m - overbought));
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
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(30)
        };

        return Task.FromResult<TradeSignal?>(signal);
    }

    /// <summary>
    /// Calculates RSI using Wilder's smoothing method at the given end index.
    /// </summary>
    private static decimal CalculateRsi(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        // We need `period` price changes, so `period + 1` closing prices ending at endIndex
        int startIndex = endIndex - period;

        decimal avgGain = 0m;
        decimal avgLoss = 0m;

        // Initial average (simple average of first `period` changes)
        for (int i = startIndex + 1; i <= startIndex + period; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            if (change > 0) avgGain += change;
            else            avgLoss -= change;
        }
        avgGain /= period;
        avgLoss /= period;

        if (avgLoss == 0m) return 100m;

        decimal rs  = avgGain / avgLoss;
        decimal rsi = 100m - (100m / (1m + rs));
        return rsi;
    }
}
