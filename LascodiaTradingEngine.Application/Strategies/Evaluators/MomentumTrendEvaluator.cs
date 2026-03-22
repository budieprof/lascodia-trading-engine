using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
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
        var (adxCurr, pdiCurr, mdiCurr) = ComputeAdx(candles, last, adxPeriod);
        var (adxPrev, pdiPrev, mdiPrev) = ComputeAdx(candles, last - 1, adxPeriod);

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

    // ── ADX calculation ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes ADX, +DI, and -DI at the given bar index using Wilder's smoothing.
    /// </summary>
    private static (decimal Adx, decimal PlusDI, decimal MinusDI) ComputeAdx(
        IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        int start = endIndex - period * 2;
        if (start < 1) start = 1;

        decimal smoothedPlusDM  = 0;
        decimal smoothedMinusDM = 0;
        decimal smoothedTR      = 0;

        // Initial sums over first `period` bars
        int initEnd = Math.Min(start + period, endIndex + 1);
        for (int i = start; i < initEnd; i++)
        {
            var (tr, pdm, mdm) = TrueRangeAndDM(candles, i);
            smoothedTR      += tr;
            smoothedPlusDM  += pdm;
            smoothedMinusDM += mdm;
        }

        // Wilder's smoothing for remaining bars
        for (int i = initEnd; i <= endIndex; i++)
        {
            var (tr, pdm, mdm) = TrueRangeAndDM(candles, i);
            smoothedTR      = smoothedTR      - smoothedTR      / period + tr;
            smoothedPlusDM  = smoothedPlusDM  - smoothedPlusDM  / period + pdm;
            smoothedMinusDM = smoothedMinusDM - smoothedMinusDM / period + mdm;
        }

        decimal plusDI  = smoothedTR > 0 ? (smoothedPlusDM  / smoothedTR) * 100 : 0;
        decimal minusDI = smoothedTR > 0 ? (smoothedMinusDM / smoothedTR) * 100 : 0;

        decimal diSum  = plusDI + minusDI;
        decimal dx     = diSum > 0 ? Math.Abs(plusDI - minusDI) / diSum * 100 : 0;

        // Simple ADX approximation (average DX over last period)
        // For a more accurate ADX we'd need to track running DX values,
        // but this single-point approximation is sufficient for crossover detection.
        decimal adx = dx; // Approximation — actual Wilder ADX would smooth DX

        // Improve approximation: compute DX at a few recent points and average
        if (endIndex - start >= period)
        {
            decimal dxSum = dx;
            int dxCount = 1;
            for (int offset = 1; offset <= Math.Min(period - 1, 4); offset++)
            {
                int idx = endIndex - offset;
                if (idx <= start) break;
                var (adxI, _, _) = ComputeAdxSingle(candles, idx, period, start);
                dxSum += adxI;
                dxCount++;
            }
            adx = dxSum / dxCount;
        }

        return (adx, plusDI, minusDI);
    }

    private static (decimal Dx, decimal PlusDI, decimal MinusDI) ComputeAdxSingle(
        IReadOnlyList<Candle> candles, int endIndex, int period, int start)
    {
        decimal sTR = 0, sPDM = 0, sMDM = 0;
        int initEnd = Math.Min(start + period, endIndex + 1);
        for (int i = start; i < initEnd; i++)
        {
            var (tr, pdm, mdm) = TrueRangeAndDM(candles, i);
            sTR += tr; sPDM += pdm; sMDM += mdm;
        }
        for (int i = initEnd; i <= endIndex; i++)
        {
            var (tr, pdm, mdm) = TrueRangeAndDM(candles, i);
            sTR  = sTR  - sTR  / period + tr;
            sPDM = sPDM - sPDM / period + pdm;
            sMDM = sMDM - sMDM / period + mdm;
        }
        decimal pdi = sTR > 0 ? sPDM / sTR * 100 : 0;
        decimal mdi = sTR > 0 ? sMDM / sTR * 100 : 0;
        decimal sum = pdi + mdi;
        decimal dx  = sum > 0 ? Math.Abs(pdi - mdi) / sum * 100 : 0;
        return (dx, pdi, mdi);
    }

    private static (decimal TR, decimal PlusDM, decimal MinusDM) TrueRangeAndDM(
        IReadOnlyList<Candle> candles, int i)
    {
        decimal high      = candles[i].High;
        decimal low       = candles[i].Low;
        decimal prevHigh  = candles[i - 1].High;
        decimal prevLow   = candles[i - 1].Low;
        decimal prevClose = candles[i - 1].Close;

        decimal tr = Math.Max(high - low,
                     Math.Max(Math.Abs(high - prevClose),
                              Math.Abs(low  - prevClose)));

        decimal upMove   = high - prevHigh;
        decimal downMove = prevLow - low;

        decimal plusDM  = upMove > downMove && upMove > 0 ? upMove : 0;
        decimal minusDM = downMove > upMove && downMove > 0 ? downMove : 0;

        return (tr, plusDM, minusDM);
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
