using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

public class MovingAverageCrossoverEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<MovingAverageCrossoverEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "MaCrossover");

    public MovingAverageCrossoverEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<MovingAverageCrossoverEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.MovingAverageCrossover;

    public int MinRequiredCandles(Strategy strategy)
    {
        var p = ParseParameters(strategy.ParametersJson);
        int maxIndicatorPeriod = Math.Max(Math.Max(p.SlowPeriod, p.TrendMaPeriod), _options.AtrPeriodForSlTp);
        int adxRequirement = _options.MaCrossoverMinAdx > 0 ? _options.MaCrossoverAdxPeriod * 2 : 0;
        int whipsawRequirement = _options.MaCrossoverMaxRecentCrossovers > 0 ? _options.MaCrossoverWhipsawLookbackBars : 0;
        bool rsiEnabled = _options.MaCrossoverMaxRsiForBuy > 0 || _options.MaCrossoverMinRsiForSell > 0;
        int rsiRequirement = rsiEnabled ? _options.MaCrossoverRsiPeriod * 2 : 0;
        int confirmRequirement = Math.Max(0, _options.MaCrossoverConfirmationBars);
        return Math.Max(Math.Max(Math.Max(maxIndicatorPeriod, adxRequirement),
            Math.Max(whipsawRequirement, rsiRequirement)), 0) + 2 + confirmRequirement;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var p = ParseParameters(strategy.ParametersJson);
        int lastIdx = candles.Count - 1;

        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        // ── 1. Compute MAs ─────────────────────────────────────────────────────
        int confirmBars = Math.Max(0, _options.MaCrossoverConfirmationBars);
        int crossIdx = lastIdx - confirmBars;
        if (crossIdx < 1)
            return Task.FromResult<TradeSignal?>(null);

        // MAs at the crossover detection point (single-pass per period)
        var (prevFast, crossFast) = CalculateMaPair(candles, crossIdx, p.FastPeriod, p.Type);
        var (prevSlow, crossSlow) = CalculateMaPair(candles, crossIdx, p.SlowPeriod, p.Type);

        // MAs at the current bar (for magnitude, confidence, and downstream filters)
        decimal currentFast, currentSlow;
        if (confirmBars == 0)
        {
            currentFast = crossFast;
            currentSlow = crossSlow;
        }
        else
        {
            currentFast = CalculateMa(candles, lastIdx, p.FastPeriod, p.Type);
            currentSlow = CalculateMa(candles, lastIdx, p.SlowPeriod, p.Type);
        }

        // ── 2. ATR — Wilder smoothing (needed by deadband, filters, SL/TP) ──
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 3. Detect crossover (with deadband) ──────────────────────────────
        // A deadband prevents noise-triggered crosses when the MAs are nearly equal.
        // The previous-bar separation must exceed a fraction of ATR before a cross
        // is recognised.
        decimal deadband = atr * _options.MaCrossoverDeadbandAtrFraction;

        decimal prevSeparation = prevFast - prevSlow; // positive = fast above slow
        bool prevWellBelow = prevSeparation <= -deadband;
        bool prevWellAbove = prevSeparation >= deadband;
        bool bullishCross = prevWellBelow && crossFast > crossSlow;
        bool bearishCross = prevWellAbove && crossFast < crossSlow;

        if (!bullishCross && !bearishCross)
            return Task.FromResult<TradeSignal?>(null);

        // ── 3a. Multi-bar confirmation ─────────────────────────────────────────
        // The fast MA must remain on the crossed side of the slow MA for N
        // consecutive bars after the crossover to confirm the signal.
        if (confirmBars > 0)
        {
            // Quick-check the current bar before looping through intermediates
            if ((bullishCross && currentFast <= currentSlow) || (bearishCross && currentFast >= currentSlow))
            {
                LogRejection(strategy, "Confirmation",
                    $"MAs reverted at current bar — fast/slow separation not maintained for {confirmBars} bars");
                return Task.FromResult<TradeSignal?>(null);
            }

            for (int i = crossIdx + 1; i < lastIdx; i++)
            {
                decimal f = CalculateMa(candles, i, p.FastPeriod, p.Type);
                decimal s = CalculateMa(candles, i, p.SlowPeriod, p.Type);
                if ((bullishCross && f <= s) || (bearishCross && f >= s))
                {
                    LogRejection(strategy, "Confirmation",
                        $"MAs reverted at bar {i - crossIdx}/{confirmBars} — crossover not confirmed");
                    return Task.FromResult<TradeSignal?>(null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 3b. Gap detection — reject signals right after large price gaps ──
        if (_options.MaCrossoverMaxGapAtrFraction > 0 && lastIdx >= 1)
        {
            decimal gap = Math.Abs(candles[lastIdx].Open - candles[lastIdx - 1].Close);
            decimal gapThreshold = atr * _options.MaCrossoverMaxGapAtrFraction;
            if (gap > gapThreshold)
            {
                LogRejection(strategy, "Gap",
                    $"Price gap {gap:F6} > {_options.MaCrossoverMaxGapAtrFraction:F1}× ATR ({gapThreshold:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 4. Crossover magnitude filter ────────────────────────────────────
        if (_options.MaCrossoverMinMagnitudeAtrFraction > 0)
        {
            decimal crossoverMagnitude = Math.Abs(currentFast - currentSlow);
            decimal threshold = atr * _options.MaCrossoverMinMagnitudeAtrFraction;
            if (crossoverMagnitude < threshold)
            {
                LogRejection(strategy, "Magnitude",
                    $"Crossover magnitude {crossoverMagnitude:F6} < threshold {threshold:F6} ({_options.MaCrossoverMinMagnitudeAtrFraction:P0} of ATR)");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 5. Trend MA filter ───────────────────────────────────────────────
        decimal currentClose = candles[lastIdx].Close;
        decimal? trendMa = p.TrendMaPeriod > 0 && candles.Count >= p.TrendMaPeriod
            ? CalculateMa(candles, lastIdx, p.TrendMaPeriod, p.Type)
            : null;

        bool trendBullish = trendMa == null || currentClose > trendMa.Value;
        bool trendBearish = trendMa == null || currentClose < trendMa.Value;

        if (bullishCross && !trendBullish)
        {
            LogRejection(strategy, "TrendMA",
                $"Bullish cross rejected — price {currentClose:F5} below trend MA {trendMa!.Value:F5}");
            return Task.FromResult<TradeSignal?>(null);
        }
        if (bearishCross && !trendBearish)
        {
            LogRejection(strategy, "TrendMA",
                $"Bearish cross rejected — price {currentClose:F5} above trend MA {trendMa!.Value:F5}");
            return Task.FromResult<TradeSignal?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 6. ADX trend-strength filter ─────────────────────────────────────
        decimal adxValue = 0m;
        if (_options.MaCrossoverMinAdx > 0 || _options.MaCrossoverDynamicSlTp)
        {
            adxValue = IndicatorCalculator.Adx(candles, lastIdx, _options.MaCrossoverAdxPeriod);
            if (_options.MaCrossoverMinAdx > 0 && adxValue < _options.MaCrossoverMinAdx)
            {
                LogRejection(strategy, "ADX",
                    $"ADX {adxValue:F2} < minimum {_options.MaCrossoverMinAdx:F2} — ranging market");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 7. Whipsaw / chop detection ──────────────────────────────────────
        int recentCrossovers = 0;
        if (_options.MaCrossoverMaxRecentCrossovers > 0)
        {
            recentCrossovers = CountRecentCrossovers(
                candles, lastIdx, p.FastPeriod, p.SlowPeriod, p.Type,
                _options.MaCrossoverWhipsawLookbackBars, cancellationToken);

            if (recentCrossovers > _options.MaCrossoverMaxRecentCrossovers)
            {
                LogRejection(strategy, "Whipsaw",
                    $"{recentCrossovers} crossovers in last {_options.MaCrossoverWhipsawLookbackBars} bars > max {_options.MaCrossoverMaxRecentCrossovers}");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 8. Volume confirmation ───────────────────────────────────────────
        if (_options.MaCrossoverMinVolume > 0)
        {
            decimal signalBarVolume = candles[lastIdx].Volume;
            if (signalBarVolume < _options.MaCrossoverMinVolume)
            {
                LogRejection(strategy, "Volume",
                    $"Signal bar volume {signalBarVolume:F0} < minimum {_options.MaCrossoverMinVolume:F0}");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 9. RSI momentum confirmation ───────────────────────────────────
        bool rsiFilterEnabled = _options.MaCrossoverMaxRsiForBuy > 0 || _options.MaCrossoverMinRsiForSell > 0;
        bool rsiConfidenceEnabled = _options.MaCrossoverWeightRsiMomentum > 0;
        decimal rsiValue = (rsiFilterEnabled || rsiConfidenceEnabled)
            ? IndicatorCalculator.Rsi(candles, lastIdx, _options.MaCrossoverRsiPeriod)
            : 50m;

        if (rsiFilterEnabled)
        {
            if (bullishCross && _options.MaCrossoverMaxRsiForBuy > 0 && rsiValue > _options.MaCrossoverMaxRsiForBuy)
            {
                LogRejection(strategy, "RSI",
                    $"Bullish cross rejected — RSI {rsiValue:F2} > max {_options.MaCrossoverMaxRsiForBuy:F2} (overbought)");
                return Task.FromResult<TradeSignal?>(null);
            }
            if (bearishCross && _options.MaCrossoverMinRsiForSell > 0 && rsiValue < _options.MaCrossoverMinRsiForSell)
            {
                LogRejection(strategy, "RSI",
                    $"Bearish cross rejected — RSI {rsiValue:F2} < min {_options.MaCrossoverMinRsiForSell:F2} (oversold)");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 10. Spread safety guard ──────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.MaCrossoverMaxSpreadAtrFraction > 0 && spread > atr * _options.MaCrossoverMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.MaCrossoverMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 11. Direction and entry price ────────────────────────────────────
        TradeDirection direction;
        decimal entryPrice;
        if (bullishCross)
        {
            direction  = TradeDirection.Buy;
            entryPrice = currentPrice.Ask;
        }
        else
        {
            direction  = TradeDirection.Sell;
            entryPrice = currentPrice.Bid;
        }

        // ── 11b. Slippage buffer ────────────────────────────────────────────
        if (_options.MaCrossoverSlippageAtrFraction > 0)
        {
            decimal slippageOffset = atr * _options.MaCrossoverSlippageAtrFraction;
            entryPrice += direction == TradeDirection.Buy ? slippageOffset : -slippageOffset;
        }

        // ── 12. ATR-based stop-loss and take-profit ──────────────────────────
        decimal slMultiplier = _options.StopLossAtrMultiplier;
        decimal tpMultiplier = _options.TakeProfitAtrMultiplier;

        if (_options.MaCrossoverDynamicSlTp && adxValue > 0)
        {
            // Linearly interpolate between 1.0 (at MinAdx) and the strong-trend scale
            // (at StrongAdxThreshold). ADX beyond the threshold clamps to the strong scale.
            decimal minAdx = Math.Max(_options.MaCrossoverMinAdx, 15m);
            decimal strongAdx = _options.MaCrossoverStrongAdxThreshold;
            decimal t = strongAdx > minAdx
                ? Math.Clamp((adxValue - minAdx) / (strongAdx - minAdx), 0m, 1m)
                : 0m;

            slMultiplier *= 1m + t * (_options.MaCrossoverStrongTrendSlScale - 1m);
            tpMultiplier *= 1m + t * (_options.MaCrossoverStrongTrendTpScale - 1m);
        }

        decimal stopDistance   = atr * slMultiplier;
        decimal profitDistance = atr * tpMultiplier;

        decimal stopLoss;
        decimal takeProfit;
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

        // ── 13. Swing-structure stop-loss override ─────────────────────────
        decimal swingBuffer = atr * _options.MaCrossoverSwingBufferAtrFraction;
        if (_options.MaCrossoverSwingSlEnabled)
        {
            decimal swingLevel = direction == TradeDirection.Buy
                ? IndicatorCalculator.FindSwingLow(candles, lastIdx, _options.MaCrossoverSwingSlLookbackBars) - swingBuffer
                : IndicatorCalculator.FindSwingHigh(candles, lastIdx, _options.MaCrossoverSwingSlLookbackBars) + swingBuffer;

            decimal minSlDistance = atr * _options.MaCrossoverSwingSlMinAtrMultiplier;
            decimal maxSlDistance = atr * _options.MaCrossoverSwingSlMaxAtrMultiplier;
            decimal swingDistance = Math.Clamp(Math.Abs(entryPrice - swingLevel), minSlDistance, maxSlDistance);

            stopLoss = direction == TradeDirection.Buy
                ? entryPrice - swingDistance
                : entryPrice + swingDistance;
        }

        // ── 13b. Swing-structure take-profit override ──────────────────────
        if (_options.MaCrossoverSwingTpEnabled)
        {
            // Target the nearest resistance (buys) or support (sells) with a buffer
            decimal tpSwingLevel = direction == TradeDirection.Buy
                ? IndicatorCalculator.FindSwingHigh(candles, lastIdx, _options.MaCrossoverSwingTpLookbackBars) + swingBuffer
                : IndicatorCalculator.FindSwingLow(candles, lastIdx, _options.MaCrossoverSwingTpLookbackBars) - swingBuffer;

            decimal minTpDistance = atr * _options.MaCrossoverSwingTpMinAtrMultiplier;
            decimal maxTpDistance = atr * _options.MaCrossoverSwingTpMaxAtrMultiplier;
            decimal swingTpDistance = Math.Clamp(Math.Abs(tpSwingLevel - entryPrice), minTpDistance, maxTpDistance);

            takeProfit = direction == TradeDirection.Buy
                ? entryPrice + swingTpDistance
                : entryPrice - swingTpDistance;
        }

        // ── 13c. Post-swing risk-reward validation ─────────────────────────
        if (_options.MaCrossoverMinRiskRewardRatio > 0)
        {
            decimal finalSlDistance = Math.Abs(entryPrice - stopLoss);
            decimal finalTpDistance = Math.Abs(takeProfit - entryPrice);
            decimal riskReward = finalSlDistance > 0 ? finalTpDistance / finalSlDistance : 0m;
            if (riskReward < _options.MaCrossoverMinRiskRewardRatio)
            {
                LogRejection(strategy, "RiskReward",
                    $"R:R {riskReward:F2} < minimum {_options.MaCrossoverMinRiskRewardRatio:F2} (SL={finalSlDistance:F6}, TP={finalTpDistance:F6})");
                return Task.FromResult<TradeSignal?>(null);
            }
        }

        // ── 14. Dynamic confidence scoring ───────────────────────────────────
        decimal confidence = ComputeConfidence(
            currentFast, currentSlow, atr,
            trendMa, currentClose,
            candles, lastIdx,
            recentCrossovers, bullishCross, rsiValue, adxValue,
            p.WeightOverrides);

        // Use the last candle's close time so backtests get simulated timestamps
        // instead of wallclock time. In live mode the candle timestamp is effectively "now".
        var now = candles[lastIdx].Timestamp;
        var signal = new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize = CalculateLotSize(confidence),
            Confidence       = confidence,
            Status           = TradeSignalStatus.Pending,
            GeneratedAt      = now,
            ExpiresAt        = now.AddMinutes(_options.MaCrossoverExpiryMinutes)
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
            "MaCrossover signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private enum MaType { Sma, Ema, Vwma }

    private record struct ConfidenceWeightOverrides(
        decimal? Magnitude, decimal? Trend, decimal? Whipsaw, decimal? CandleBody,
        decimal? CandlePattern, decimal? RsiMomentum, decimal? AdxStrength, decimal? Volume);

    private record struct MaCrossoverParams(
        int FastPeriod, int SlowPeriod, int TrendMaPeriod, MaType Type,
        ConfidenceWeightOverrides WeightOverrides);

    private MaCrossoverParams ParseParameters(string? json)
    {
        int fastPeriod = 9, slowPeriod = 21, trendMaPeriod = 50;
        var maType = MaType.Ema; // EMA by default — less lag than SMA
        var weights = new ConfidenceWeightOverrides();

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("FastPeriod",   out var fp) && fp.TryGetInt32(out var fpVal))  fastPeriod    = fpVal;
            if (root.TryGetProperty("SlowPeriod",   out var sp) && sp.TryGetInt32(out var spVal))  slowPeriod    = spVal;
            if (root.TryGetProperty("MaPeriod",     out var mp) && mp.TryGetInt32(out var mpVal))  trendMaPeriod = mpVal;

            // Backwards-compatible: "UseEma": true/false or "MaType": "Sma"|"Ema"|"Vwma"
            if (root.TryGetProperty("MaType", out var mt) && mt.ValueKind == JsonValueKind.String)
            {
                var mtStr = mt.GetString();
                if (string.Equals(mtStr, "Sma",  StringComparison.OrdinalIgnoreCase)) maType = MaType.Sma;
                else if (string.Equals(mtStr, "Vwma", StringComparison.OrdinalIgnoreCase)) maType = MaType.Vwma;
                else maType = MaType.Ema;
            }
            else if (root.TryGetProperty("UseEma", out var ue))
            {
                maType = ue.ValueKind == JsonValueKind.True ? MaType.Ema : MaType.Sma;
            }

            // Per-strategy confidence weight overrides (optional — null falls back to global)
            if (root.TryGetProperty("ConfidenceWeights", out var cw) && cw.ValueKind == JsonValueKind.Object)
            {
                weights = new ConfidenceWeightOverrides(
                    Magnitude:     TryGetDecimal(cw, "Magnitude"),
                    Trend:         TryGetDecimal(cw, "Trend"),
                    Whipsaw:       TryGetDecimal(cw, "Whipsaw"),
                    CandleBody:    TryGetDecimal(cw, "CandleBody"),
                    CandlePattern: TryGetDecimal(cw, "CandlePattern"),
                    RsiMomentum:   TryGetDecimal(cw, "RsiMomentum"),
                    AdxStrength:   TryGetDecimal(cw, "AdxStrength"),
                    Volume:        TryGetDecimal(cw, "Volume"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MaCrossover: failed to parse ParametersJson — using defaults");
        }

        fastPeriod    = Math.Clamp(fastPeriod, 2, 500);
        slowPeriod    = Math.Clamp(slowPeriod, 2, 500);
        trendMaPeriod = Math.Clamp(trendMaPeriod, 0, 500); // 0 disables the trend MA filter
        if (fastPeriod >= slowPeriod) fastPeriod = Math.Max(1, slowPeriod - 1);

        return new MaCrossoverParams(fastPeriod, slowPeriod, trendMaPeriod, maType, weights);
    }

    private static decimal? TryGetDecimal(JsonElement parent, string property)
    {
        if (parent.TryGetProperty(property, out var el) && el.TryGetDecimal(out var val))
            return Math.Max(0m, val);
        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Moving average dispatch (delegates to IndicatorCalculator)
    // ═════════════════════════════════════════════════════════════════════════

    private static decimal CalculateMa(IReadOnlyList<Candle> candles, int endIndex, int period, MaType type)
        => type switch
        {
            MaType.Ema  => IndicatorCalculator.Ema(candles, endIndex, period),
            MaType.Vwma => IndicatorCalculator.Vwma(candles, endIndex, period),
            _           => IndicatorCalculator.Sma(candles, endIndex, period),
        };

    // ═════════════════════════════════════════════════════════════════════════
    // Whipsaw detection — O(n) incremental MA differencing
    // ═════════════════════════════════════════════════════════════════════════

    private static int CountRecentCrossovers(
        IReadOnlyList<Candle> candles, int endIndex,
        int fastPeriod, int slowPeriod, MaType type, int lookbackBars,
        CancellationToken cancellationToken = default)
    {
        int startBar = Math.Max(endIndex - lookbackBars, slowPeriod);

        // EMA has an O(n) incremental path; SMA has an O(n) sliding-window path.
        // VWMA uses the generic per-bar CalculateMa path (still O(n×period) but VWMA
        // is uncommon in whipsaw detection and the lookback window is small).
        return type switch
        {
            MaType.Ema => CountRecentCrossoversEma(candles, startBar, endIndex, fastPeriod, slowPeriod, cancellationToken),
            MaType.Sma => CountRecentCrossoversSma(candles, startBar, endIndex, fastPeriod, slowPeriod),
            _          => CountRecentCrossoversGeneric(candles, startBar, endIndex, fastPeriod, slowPeriod, type),
        };
    }

    /// <summary>
    /// Generic crossover counter for MA types without a dedicated O(n) path (e.g. VWMA).
    /// </summary>
    private static int CountRecentCrossoversGeneric(
        IReadOnlyList<Candle> candles, int startBar, int endIndex,
        int fastPeriod, int slowPeriod, MaType type)
    {
        int crossovers = 0;
        decimal prevFast = CalculateMa(candles, startBar - 1, fastPeriod, type);
        decimal prevSlow = CalculateMa(candles, startBar - 1, slowPeriod, type);

        for (int i = startBar; i <= endIndex; i++)
        {
            decimal curFast = CalculateMa(candles, i, fastPeriod, type);
            decimal curSlow = CalculateMa(candles, i, slowPeriod, type);

            bool crossed = (prevFast <= prevSlow && curFast > curSlow)
                        || (prevFast >= prevSlow && curFast < curSlow);
            if (crossed) crossovers++;

            prevFast = curFast;
            prevSlow = curSlow;
        }

        return crossovers;
    }

    /// <summary>
    /// O(n) SMA crossover counting using a sliding window — no per-bar MA recalculation.
    /// </summary>
    private static int CountRecentCrossoversSma(
        IReadOnlyList<Candle> candles, int startBar, int endIndex,
        int fastPeriod, int slowPeriod)
    {
        // Compute initial SMA values at startBar-1
        decimal fastSum = 0, slowSum = 0;
        for (int i = startBar - fastPeriod; i < startBar; i++)
            fastSum += candles[i].Close;
        for (int i = startBar - slowPeriod; i < startBar; i++)
            slowSum += candles[i].Close;

        decimal prevFast = fastSum / fastPeriod;
        decimal prevSlow = slowSum / slowPeriod;
        int crossovers = 0;

        for (int i = startBar; i <= endIndex; i++)
        {
            // Slide window: add new bar, remove oldest bar
            fastSum += candles[i].Close - candles[i - fastPeriod].Close;
            slowSum += candles[i].Close - candles[i - slowPeriod].Close;
            decimal curFast = fastSum / fastPeriod;
            decimal curSlow = slowSum / slowPeriod;

            bool crossed = (prevFast <= prevSlow && curFast > curSlow)
                        || (prevFast >= prevSlow && curFast < curSlow);
            if (crossed) crossovers++;

            prevFast = curFast;
            prevSlow = curSlow;
        }

        return crossovers;
    }

    /// <summary>
    /// O(n) EMA crossover counting — seeds via <see cref="Ema"/> to guarantee identical
    /// values at startBar-1, then updates incrementally through the lookback window.
    /// </summary>
    private static int CountRecentCrossoversEma(
        IReadOnlyList<Candle> candles, int startBar, int endIndex,
        int fastPeriod, int slowPeriod, CancellationToken cancellationToken)
    {
        // Seed at startBar-1 using the same Ema() function used in crossover detection
        decimal prevFast = IndicatorCalculator.Ema(candles, startBar - 1, fastPeriod);
        decimal prevSlow = IndicatorCalculator.Ema(candles, startBar - 1, slowPeriod);

        decimal fastMul = 2.0m / (fastPeriod + 1);
        decimal slowMul = 2.0m / (slowPeriod + 1);
        int crossovers = 0;

        for (int i = startBar; i <= endIndex; i++)
        {
            decimal c = candles[i].Close;
            decimal curFast = (c - prevFast) * fastMul + prevFast;
            decimal curSlow = (c - prevSlow) * slowMul + prevSlow;

            bool crossed = (prevFast <= prevSlow && curFast > curSlow)
                        || (prevFast >= prevSlow && curFast < curSlow);
            if (crossed) crossovers++;

            prevFast = curFast;
            prevSlow = curSlow;

            if ((i & 511) == 0)
                cancellationToken.ThrowIfCancellationRequested();
        }

        return crossovers;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dynamic confidence — weighted multi-factor scoring
    // ═════════════════════════════════════════════════════════════════════════

    private decimal ComputeConfidence(
        decimal currentFast, decimal currentSlow, decimal atr,
        decimal? trendMa, decimal currentClose,
        IReadOnlyList<Candle> candles, int lastIdx,
        int recentCrossovers, bool isBullish, decimal rsi, decimal adx,
        ConfidenceWeightOverrides overrides)
    {
        decimal baseConfidence = _options.MaCrossoverConfidence;

        // Factor 1: Crossover magnitude relative to ATR (0..1)
        decimal magnitude = Math.Abs(currentFast - currentSlow);
        decimal magScore = atr > 0 ? Math.Min(1.0m, magnitude / atr) : 0.5m;

        // Factor 2: Trend proximity — closer to trend MA = better entry (0..1)
        decimal trendScore;
        if (trendMa.HasValue && atr > 0)
        {
            decimal trendDistance = Math.Abs(currentClose - trendMa.Value);
            trendScore = Math.Max(0m, 1.0m - Math.Min(1.0m, trendDistance / (atr * 2m)));
        }
        else
        {
            trendScore = 0.5m;
        }

        // Factor 3: Whipsaw penalty (0..1)
        decimal whipsawScore;
        if (_options.MaCrossoverMaxRecentCrossovers > 0)
        {
            int maxAllowed = _options.MaCrossoverMaxRecentCrossovers;
            whipsawScore = Math.Max(0m, 1.0m - ((decimal)recentCrossovers / (maxAllowed + 1)));
        }
        else
        {
            whipsawScore = 0.5m;
        }

        // Factor 4: Direction-aware bar body-to-wick ratio on signal bar (0..1)
        // A strong body aligned with the signal direction scores high; a counter-direction
        // body is penalised even if it has a good body-to-wick ratio.
        decimal barRange = candles[lastIdx].High - candles[lastIdx].Low;
        decimal bodyScore;
        if (barRange > 0)
        {
            decimal rawBody = candles[lastIdx].Close - candles[lastIdx].Open; // positive = bullish bar
            decimal bodyRatio = Math.Abs(rawBody) / barRange;
            bool bodyAligned = isBullish ? rawBody > 0 : rawBody < 0;
            bodyScore = bodyAligned ? bodyRatio : bodyRatio * 0.3m;
        }
        else
        {
            bodyScore = 0.5m;
        }

        // Factor 5: Candle pattern confirmation (0..1)
        decimal patternScore = IndicatorCalculator.ScoreCandlePatterns(candles, lastIdx, isBullish);

        // Factor 6: RSI momentum alignment (0..1)
        // Triangular window: peaks at ideal RSI (57.5 buy / 42.5 sell), zero at extremes.
        // Rewards momentum that confirms direction without being overextended.
        decimal rsiScore;
        if (_options.MaCrossoverWeightRsiMomentum > 0)
        {
            decimal idealRsi = isBullish ? 57.5m : 42.5m;
            const decimal halfWidth = 22.5m;
            rsiScore = Math.Max(0m, 1.0m - Math.Abs(rsi - idealRsi) / halfWidth);
        }
        else
        {
            rsiScore = 0.5m;
        }

        // Factor 7: ADX trend strength (0..1)
        // Normalized with diminishing returns: ADX 20 → 0.0, ADX 40+ → 1.0 (log-like curve)
        decimal adxScore;
        if (_options.MaCrossoverWeightAdxStrength > 0 && adx > 0)
        {
            decimal strongAdx = _options.MaCrossoverStrongAdxThreshold;
            decimal minAdx = Math.Max(_options.MaCrossoverMinAdx, 15m);
            decimal raw = strongAdx > minAdx
                ? Math.Clamp((adx - minAdx) / (strongAdx - minAdx), 0m, 1m)
                : 0m;
            // Square root for diminishing returns — early ADX gains matter more
            adxScore = (decimal)Math.Sqrt((double)raw);
        }
        else
        {
            adxScore = 0.5m;
        }

        // Factor 8: Volume relative to average (0..1)
        decimal volumeScore;
        if (_options.MaCrossoverWeightVolume > 0 && _options.MaCrossoverVolumeLookbackBars > 0)
        {
            int volLookback = _options.MaCrossoverVolumeLookbackBars;
            int volStart = Math.Max(0, lastIdx - volLookback);
            decimal volSum = 0m;
            int volCount = 0;
            for (int i = volStart; i < lastIdx; i++)
            {
                volSum += candles[i].Volume;
                volCount++;
            }
            decimal avgVolume = volCount > 0 ? volSum / volCount : 0m;
            decimal signalVolume = candles[lastIdx].Volume;
            // Ratio capped at 1.0: 1× average = 0.5, 2× average = 1.0, 0× = 0.0
            volumeScore = avgVolume > 0
                ? Math.Min(1.0m, signalVolume / (avgVolume * 2m))
                : 0.5m;
        }
        else
        {
            volumeScore = 0.5m;
        }

        // Weighted composite — per-strategy overrides fall back to global options
        decimal wMag     = overrides.Magnitude     ?? _options.MaCrossoverWeightMagnitude;
        decimal wTrend   = overrides.Trend         ?? _options.MaCrossoverWeightTrend;
        decimal wWhip    = overrides.Whipsaw       ?? _options.MaCrossoverWeightWhipsaw;
        decimal wBody    = overrides.CandleBody    ?? _options.MaCrossoverWeightCandleBody;
        decimal wPattern = overrides.CandlePattern ?? _options.MaCrossoverWeightCandlePattern;
        decimal wRsi     = overrides.RsiMomentum   ?? _options.MaCrossoverWeightRsiMomentum;
        decimal wAdx     = overrides.AdxStrength   ?? _options.MaCrossoverWeightAdxStrength;
        decimal wVol     = overrides.Volume        ?? _options.MaCrossoverWeightVolume;
        decimal wTotal   = wMag + wTrend + wWhip + wBody + wPattern + wRsi + wAdx + wVol;
        // Normalise in case weights don't sum to 1
        if (wTotal <= 0) wTotal = 1m;

        decimal weightedAvg = (magScore * wMag + trendScore * wTrend
                            + whipsawScore * wWhip + bodyScore * wBody
                            + patternScore * wPattern + rsiScore * wRsi
                            + adxScore * wAdx + volumeScore * wVol) / wTotal;

        // Map [0..1] weighted average to [base-0.2 .. base+0.2]
        decimal confidence = baseConfidence + (weightedAvg - 0.5m) * 0.4m;
        return Math.Clamp(confidence, 0.1m, 1.0m);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Single-pass MA pair — returns (previous, current) in one walk
    // ═════════════════════════════════════════════════════════════════════════

    private static (decimal Previous, decimal Current) CalculateMaPair(
        IReadOnlyList<Candle> candles, int endIndex, int period, MaType type)
        => type switch
        {
            MaType.Ema  => IndicatorCalculator.EmaPair(candles, endIndex, period),
            MaType.Sma  => SmaPair(candles, endIndex, period),
            _           => (IndicatorCalculator.Vwma(candles, endIndex - 1, period), IndicatorCalculator.Vwma(candles, endIndex, period)),
        };

    private static (decimal Previous, decimal Current) SmaPair(
        IReadOnlyList<Candle> candles, int endIndex, int period)
    {
        // SMA at endIndex-1: average of [endIndex-period .. endIndex-1]
        decimal sum = 0;
        for (int i = endIndex - period; i <= endIndex - 1; i++)
            sum += candles[i].Close;
        decimal prev = sum / period;

        // Slide to endIndex: remove oldest, add newest
        sum += candles[endIndex].Close - candles[endIndex - period].Close;
        return (prev, sum / period);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsCandleOrderValid(IReadOnlyList<Candle> candles)
    {
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].Timestamp <= candles[i - 1].Timestamp)
                return false;
        }
        return true;
    }

    private decimal CalculateLotSize(decimal confidence)
    {
        if (!_options.MaCrossoverConfidenceLotSizing)
            return _options.DefaultLotSize;

        decimal minLot = _options.MaCrossoverMinLotSize;
        decimal maxLot = _options.MaCrossoverMaxLotSize;
        // Quadratic (convex) interpolation: early confidence gains map to smaller lot
        // increases, while high confidence maps to proportionally larger lots.
        // This reflects diminishing marginal value of low confidence scores.
        decimal t = Math.Clamp((confidence - 0.1m) / 0.9m, 0m, 1m);
        return minLot + t * t * (maxLot - minLot);
    }
}
