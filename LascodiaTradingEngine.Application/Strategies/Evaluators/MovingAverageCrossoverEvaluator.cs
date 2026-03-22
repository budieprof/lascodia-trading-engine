using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class MovingAverageCrossoverEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public MovingAverageCrossoverEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.MovingAverageCrossover;

    public int MinRequiredCandles(Strategy strategy)
    {
        int fastPeriod = 9, slowPeriod = 21, maPeriod = 50;
        ParseParameters(strategy.ParametersJson, ref fastPeriod, ref slowPeriod, ref maPeriod);
        // Need the max of all periods + 1 for crossover detection, plus ATR period for SL/TP
        return Math.Max(Math.Max(slowPeriod, maPeriod), _options.AtrPeriodForSlTp) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int fastPeriod = 9, slowPeriod = 21, maPeriod = 50;
        ParseParameters(strategy.ParametersJson, ref fastPeriod, ref slowPeriod, ref maPeriod);

        int requiredCandles = Math.Max(Math.Max(slowPeriod, maPeriod), _options.AtrPeriodForSlTp) + 1;
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

        // ATR-based stop-loss and take-profit
        decimal atr = CalculateAtr(candles, candles.Count - 1, _options.AtrPeriodForSlTp);
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
            Confidence       = _options.MaCrossoverConfidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.MaCrossoverExpiryMinutes)
        };

        return Task.FromResult<TradeSignal?>(signal);
    }

    private static void ParseParameters(string? json, ref int fastPeriod, ref int slowPeriod, ref int maPeriod)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("FastPeriod", out var fp) && fp.TryGetInt32(out var fpVal)) fastPeriod = fpVal;
            if (root.TryGetProperty("SlowPeriod", out var sp) && sp.TryGetInt32(out var spVal)) slowPeriod = spVal;
            if (root.TryGetProperty("MaPeriod",   out var mp) && mp.TryGetInt32(out var mpVal)) maPeriod   = mpVal;
        }
        catch { /* use defaults */ }

        fastPeriod = Math.Clamp(fastPeriod, 2, 500);
        slowPeriod = Math.Clamp(slowPeriod, 2, 500);
        maPeriod   = Math.Clamp(maPeriod, 0, 500); // 0 disables the long MA filter
        if (fastPeriod >= slowPeriod) fastPeriod = Math.Max(1, slowPeriod - 1);
    }

    private static decimal Sma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sum = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sumTr = 0m;
        int start = endIndex - period + 1;
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
