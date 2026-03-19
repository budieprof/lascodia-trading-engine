using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class MovingAverageCrossoverEvaluator : IStrategyEvaluator
{
    public StrategyType StrategyType => StrategyType.MovingAverageCrossover;

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        // Parse parameters
        int  fastPeriod = 9;
        int  slowPeriod = 21;
        int  maPeriod   = 50;

        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("FastPeriod", out var fp) && fp.TryGetInt32(out var fpVal)) fastPeriod = fpVal;
            if (root.TryGetProperty("SlowPeriod", out var sp) && sp.TryGetInt32(out var spVal)) slowPeriod = spVal;
            if (root.TryGetProperty("MaPeriod",   out var mp) && mp.TryGetInt32(out var mpVal)) maPeriod   = mpVal;
        }
        catch { /* use defaults */ }

        int requiredCandles = slowPeriod + 1;
        if (candles.Count < requiredCandles)
            return Task.FromResult<TradeSignal?>(null);

        // Calculate SMAs for current and previous bar
        decimal currentFast = Sma(candles, candles.Count - 1, fastPeriod);
        decimal currentSlow = Sma(candles, candles.Count - 1, slowPeriod);
        decimal prevFast    = Sma(candles, candles.Count - 2, fastPeriod);
        decimal prevSlow    = Sma(candles, candles.Count - 2, slowPeriod);

        // Check long MA condition
        decimal currentClose = candles[candles.Count - 1].Close;
        decimal? longMa = maPeriod > 0 && candles.Count >= maPeriod
            ? Sma(candles, candles.Count - 1, maPeriod)
            : (decimal?)null;

        bool longMaBullish = longMa == null || currentClose > longMa.Value;
        bool longMaBearish = longMa == null || currentClose < longMa.Value;

        // Detect crossover
        bool bullishCross = prevFast <= prevSlow && currentFast > currentSlow;
        bool bearishCross = prevFast >= prevSlow && currentFast < currentSlow;

        TradeDirection? direction = null;
        decimal entryPrice;

        if (bullishCross && longMaBullish)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        else if (bearishCross && longMaBearish)
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
            Confidence       = 0.7m,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddHours(1)
        };

        return Task.FromResult<TradeSignal?>(signal);
    }

    private static decimal Sma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sum = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }
}
