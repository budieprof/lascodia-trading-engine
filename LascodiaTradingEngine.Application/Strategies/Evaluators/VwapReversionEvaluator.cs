using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Mean-reversion strategy that trades price deviations from session VWAP.
/// Entries fire when price moves beyond a configurable ATR threshold from VWAP,
/// with ADX and volume confirmation filters.
///
/// Filter pipeline (in order):
/// 1. Minimum candle count
/// 2. D1 timeframe rejection
/// 3. Session time gate
/// 4. Session start index discovery
/// 5. VWAP computation
/// 6. ATR calculation + zero guard
/// 7. Deviation threshold gate
/// 8. ADX trend filter (reject strong trends)
/// 9. Volume confirmation
/// 10. ATR-based SL/TP (with VWAP target cap)
/// 11. Dynamic confidence scoring
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class VwapReversionEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<VwapReversionEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "VwapReversion");

    public VwapReversionEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<VwapReversionEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.VwapReversion;

    public int MinRequiredCandles(Strategy strategy)
    {
        var p = ParseParameters(strategy.ParametersJson);
        return p.AtrPeriod * 2 + 1;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var p = ParseParameters(strategy.ParametersJson);

        // ── 1. Minimum candle count ────────────────────────────────────────
        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        int lastIdx = candles.Count - 1;

        // ── 2. D1 timeframe rejection ──────────────────────────────────────
        if (strategy.Timeframe == Timeframe.D1)
            return Task.FromResult<TradeSignal?>(null);

        // ── 3. Session time gate ───────────────────────────────────────────
        int lastHour = candles[lastIdx].Timestamp.Hour;
        bool sessionSpansMidnight = p.SessionStartHour > p.SessionEndHour;

        if (sessionSpansMidnight)
        {
            // Session like 20:00-04:00: valid hours are >= start OR < end
            if (lastHour < p.SessionStartHour && lastHour >= p.SessionEndHour)
                return Task.FromResult<TradeSignal?>(null);
        }
        else
        {
            if (lastHour < p.SessionStartHour || lastHour >= p.SessionEndHour)
                return Task.FromResult<TradeSignal?>(null);
        }

        // ── 4. Session start index discovery ───────────────────────────────
        int sessionStartIdx = -1;

        for (int i = lastIdx; i >= 0; i--)
        {
            var hour = candles[i].Timestamp.Hour;

            if (sessionSpansMidnight)
            {
                // Session like 20:00-04:00: break when we exit the valid range
                bool inSession = hour >= p.SessionStartHour || hour < p.SessionEndHour;
                if (!inSession)
                {
                    sessionStartIdx = i + 1;
                    break;
                }
            }
            else
            {
                // Normal session like 08:00-16:00
                if (candles[i].Timestamp.Date != candles[lastIdx].Timestamp.Date
                    || hour < p.SessionStartHour)
                {
                    sessionStartIdx = i + 1;
                    break;
                }
            }

            if (i == 0) sessionStartIdx = 0;
        }

        if (sessionStartIdx < 0 || sessionStartIdx > lastIdx || lastIdx - sessionStartIdx < 5)
        {
            LogRejection(strategy, "NoSessionStart", "Could not locate session start index or too few bars");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. VWAP computation ────────────────────────────────────────────
        decimal vwap = IndicatorCalculator.Vwap(candles, lastIdx, sessionStartIdx);
        if (vwap == 0m)
        {
            LogRejection(strategy, "DegenerateVWAP", "VWAP is zero — no volume in session");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 6. ATR calculation + zero guard ────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, p.AtrPeriod);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 7. Deviation threshold gate ────────────────────────────────────
        decimal midPrice = (currentPrice.Bid + currentPrice.Ask) / 2m;
        decimal deviation = (midPrice - vwap) / atr;

        if (Math.Abs(deviation) < p.EntryAtrThreshold)
            return Task.FromResult<TradeSignal?>(null);

        cancellationToken.ThrowIfCancellationRequested();

        // ── 8. ADX trend filter (reject strong trends) ─────────────────────
        decimal adx = IndicatorCalculator.Adx(candles, lastIdx, 14);
        if (adx > p.MaxAdx)
        {
            LogRejection(strategy, "ADX",
                $"ADX {adx:F2} > maximum {p.MaxAdx:F2} — trending market, reversion unlikely");
            return Task.FromResult<TradeSignal?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 9. Volume confirmation ─────────────────────────────────────────
        int volLookback = Math.Min(20, lastIdx);
        if (volLookback > 0)
        {
            decimal sumVol = 0m;
            for (int i = lastIdx - volLookback; i < lastIdx; i++)
                sumVol += candles[i].Volume;

            decimal avgVol = sumVol / volLookback;
            if (avgVol > 0 && candles[lastIdx].Volume < avgVol * p.MinVolumeRatio)
            {
                LogRejection(strategy, "Volume",
                    $"Current volume {candles[lastIdx].Volume:F0} < {p.MinVolumeRatio:F1}x avg ({avgVol:F0})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 10. Direction, entry, SL/TP ────────────────────────────────────
        // deviation > 0 → price above VWAP → sell (expect reversion down)
        // deviation < 0 → price below VWAP → buy (expect reversion up)
        TradeDirection direction = deviation > 0
            ? TradeDirection.Sell
            : TradeDirection.Buy;

        decimal entryPrice = direction == TradeDirection.Buy
            ? currentPrice.Ask
            : currentPrice.Bid;

        decimal stopDistance = atr * p.StopLossAtrMultiplier;
        decimal atrTpDistance = atr * p.TakeProfitAtrMultiplier;
        decimal vwapTpDistance = Math.Abs(entryPrice - vwap);

        // TP is the closer of ATR-based target or VWAP level
        decimal profitDistance = Math.Min(atrTpDistance, vwapTpDistance);
        if (profitDistance <= 0) profitDistance = atrTpDistance;

        decimal stopLoss, takeProfit;
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

        // ── 11. Dynamic confidence scoring ─────────────────────────────────
        decimal adxFactor = p.MaxAdx > 0 ? 1.0m - adx / p.MaxAdx : 1.0m;
        decimal confidence = Math.Clamp(
            0.6m * Math.Abs(deviation) / p.EntryAtrThreshold * adxFactor,
            0m, 1m);

        var now = DateTime.UtcNow;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = _options.DefaultLotSize,
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(20)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag);

        return Task.FromResult<TradeSignal?>(signal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Rejection diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));

        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "VwapReversion signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private static VwapParams ParseParameters(string? json)
    {
        int     sessionStartHour       = 8;
        int     sessionEndHour         = 16;
        decimal entryAtrThreshold      = 1.5m;
        decimal stopLossAtrMultiplier  = 2.0m;
        decimal takeProfitAtrMultiplier = 1.0m;
        int     atrPeriod              = 14;
        decimal maxAdx                 = 40m;
        decimal minVolumeRatio         = 1.2m;

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("SessionStartHour",       out var ss) && ss.TryGetInt32(out var ssv))    sessionStartHour       = ssv;
            if (root.TryGetProperty("SessionEndHour",         out var se) && se.TryGetInt32(out var sev))    sessionEndHour         = sev;
            if (root.TryGetProperty("EntryAtrThreshold",      out var ea) && ea.TryGetDecimal(out var eav))  entryAtrThreshold      = eav;
            if (root.TryGetProperty("StopLossAtrMultiplier",  out var sl) && sl.TryGetDecimal(out var slv))  stopLossAtrMultiplier  = slv;
            if (root.TryGetProperty("TakeProfitAtrMultiplier", out var tp) && tp.TryGetDecimal(out var tpv)) takeProfitAtrMultiplier = tpv;
            if (root.TryGetProperty("AtrPeriod",              out var ap) && ap.TryGetInt32(out var apv))    atrPeriod              = apv;
            if (root.TryGetProperty("MaxAdx",                 out var ma) && ma.TryGetDecimal(out var mav))  maxAdx                 = mav;
            if (root.TryGetProperty("MinVolumeRatio",         out var mv) && mv.TryGetDecimal(out var mvv))  minVolumeRatio         = mvv;
        }
        catch (JsonException) { }

        return new VwapParams(
            Math.Clamp(sessionStartHour, 0, 23),
            Math.Clamp(sessionEndHour, 1, 24),
            Math.Clamp(entryAtrThreshold, 0.5m, 5.0m),
            Math.Clamp(stopLossAtrMultiplier, 0.5m, 10m),
            Math.Clamp(takeProfitAtrMultiplier, 0.5m, 10m),
            Math.Clamp(atrPeriod, 5, 50),
            Math.Clamp(maxAdx, 10m, 100m),
            Math.Clamp(minVolumeRatio, 0.5m, 5.0m));
    }

    private sealed record VwapParams(
        int SessionStartHour,
        int SessionEndHour,
        decimal EntryAtrThreshold,
        decimal StopLossAtrMultiplier,
        decimal TakeProfitAtrMultiplier,
        int AtrPeriod,
        decimal MaxAdx,
        decimal MinVolumeRatio);
}
