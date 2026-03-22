using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Mean-reversion strategy for ranging and low-volatility regimes.
/// Buys when price touches/crosses below the lower Bollinger Band and reverses back,
/// sells when price touches/crosses above the upper band and reverses back.
/// Includes a Bollinger Band squeeze detector — signals are suppressed when bandwidth
/// is contracting (imminent breakout), preventing entries right before a range break.
/// </summary>
public class BollingerBandReversionEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;

    public BollingerBandReversionEvaluator(StrategyEvaluatorOptions options)
    {
        _options = options;
    }

    public StrategyType StrategyType => StrategyType.BollingerBandReversion;

    public int MinRequiredCandles(Strategy strategy)
    {
        int period = 20;
        ParseParameters(strategy.ParametersJson, ref period, out _, out _);
        return Math.Max(period, _options.AtrPeriodForSlTp) + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        int     period         = 20;
        decimal stdDevMultiple = 2.0m;
        decimal squeezeThreshold = 0.5m;
        ParseParameters(strategy.ParametersJson, ref period, out stdDevMultiple, out squeezeThreshold);

        period           = Math.Clamp(period, 2, 500);
        stdDevMultiple   = Math.Clamp(stdDevMultiple, 0.5m, 5m);
        squeezeThreshold = Math.Clamp(squeezeThreshold, 0m, 1m);

        int required = Math.Max(period, _options.AtrPeriodForSlTp) + 1;
        if (candles.Count < required)
            return Task.FromResult<TradeSignal?>(null);

        int last = candles.Count - 1;
        int prev = last - 1;

        // Current bar Bollinger Bands
        decimal sma    = Sma(candles, last, period);
        decimal stdDev = StdDev(candles, last, period, sma);
        decimal upper  = sma + stdDevMultiple * stdDev;
        decimal lower  = sma - stdDevMultiple * stdDev;

        // Previous bar Bollinger Bands (for crossover detection)
        decimal prevSma    = Sma(candles, prev, period);
        decimal prevStdDev = StdDev(candles, prev, period, prevSma);
        decimal prevUpper  = prevSma + stdDevMultiple * prevStdDev;
        decimal prevLower  = prevSma - stdDevMultiple * prevStdDev;

        // Squeeze detection: bandwidth = (upper - lower) / sma
        // If current bandwidth < squeezeThreshold × previous bandwidth, bands are contracting
        decimal bandwidth     = sma > 0 ? (upper - lower) / sma : 0;
        decimal prevBandwidth = prevSma > 0 ? (prevUpper - prevLower) / prevSma : 0;
        if (prevBandwidth > 0 && bandwidth < squeezeThreshold * prevBandwidth)
            return Task.FromResult<TradeSignal?>(null); // Squeeze — don't trade into an imminent breakout

        // Bands collapsed (zero volatility) — no meaningful reversion signal
        decimal bandWidth = upper - lower;
        if (bandWidth <= 0)
            return Task.FromResult<TradeSignal?>(null);

        decimal currentClose = candles[last].Close;
        decimal prevClose    = candles[prev].Close;

        TradeDirection? direction = null;
        decimal entryPrice;
        decimal confidence;

        // Buy: previous close was at or below lower band, current close is back above it
        if (prevClose <= prevLower && currentClose > lower)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
            // Confidence scales with how deep the touch was
            decimal depth = (lower - prevClose) / bandWidth;
            confidence = Math.Clamp(_options.BollingerConfidence + depth * 0.2m, 0m, 1m);
        }
        // Sell: previous close was at or above upper band, current close is back below it
        else if (prevClose >= prevUpper && currentClose < upper)
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
            decimal depth = (prevClose - upper) / bandWidth;
            confidence = Math.Clamp(_options.BollingerConfidence + depth * 0.2m, 0m, 1m);
        }
        else
        {
            return Task.FromResult<TradeSignal?>(null);
        }

        // ATR-based SL/TP
        decimal atr           = CalculateAtr(candles, last, _options.AtrPeriodForSlTp);
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
            ExpiresAt        = now.AddMinutes(_options.BollingerExpiryMinutes)
        });
    }

    private static void ParseParameters(string? json, ref int period, out decimal stdDevMultiple, out decimal squeezeThreshold)
    {
        stdDevMultiple   = 2.0m;
        squeezeThreshold = 0.5m;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("Period",           out var p)  && p.TryGetInt32(out var pv))     period           = pv;
            if (root.TryGetProperty("StdDevMultiple",   out var sd) && sd.TryGetDecimal(out var sdv)) stdDevMultiple   = sdv;
            if (root.TryGetProperty("SqueezeThreshold", out var sq) && sq.TryGetDecimal(out var sqv)) squeezeThreshold = sqv;
        }
        catch { /* defaults */ }
    }

    private static decimal Sma(IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        decimal sum = 0;
        for (int i = endIndex - period + 1; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }

    private static decimal StdDev(IReadOnlyList<Candle> candles, int endIndex, int period, decimal mean)
    {
        decimal sumSqDiff = 0;
        for (int i = endIndex - period + 1; i <= endIndex; i++)
        {
            decimal diff = candles[i].Close - mean;
            sumSqDiff += diff * diff;
        }
        return (decimal)Math.Sqrt((double)(sumSqDiff / period));
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
