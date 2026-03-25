using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class RSIReversionEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public RSIReversionEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.RSIReversion;

    public int MinRequiredCandles(Strategy strategy)
    {
        int period = 14;
        try
        {
            using var doc = JsonDocument.Parse(strategy.ParametersJson ?? "{}");
            if (doc.RootElement.TryGetProperty("Period", out var p) && p.TryGetInt32(out var pVal))
                period = pVal;
        }
        catch { /* use default */ }

        // Need period+1 for RSI calculation (to get previous RSI too) plus ATR for SL/TP
        return Math.Max(period + 1, _options.AtrPeriodForSlTp) + 1;
    }

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

        period     = Math.Clamp(period, 2, 500);
        oversold   = Math.Clamp(oversold, 1m, 49m);
        overbought = Math.Clamp(overbought, 51m, 99m);

        int requiredCandles = Math.Max(period + 1, _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < requiredCandles)
            return Task.FromResult<TradeSignal?>(null);

        decimal currentRsi = IndicatorCalculator.SimpleRsi(candles, candles.Count - 1, period);
        decimal prevRsi    = IndicatorCalculator.SimpleRsi(candles, candles.Count - 2, period);

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

        // ATR-based stop-loss and take-profit
        decimal atr = IndicatorCalculator.Atr(candles, candles.Count - 1, _options.AtrPeriodForSlTp);
        if (atr <= 0) return Task.FromResult<TradeSignal?>(null);
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

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
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.RsiReversionExpiryMinutes)
        };

        return Task.FromResult<TradeSignal?>(signal);
    }
}
