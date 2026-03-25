using System.Collections.Concurrent;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Detects MACD histogram divergence from price to identify trend exhaustion
/// and reversal entries. Works well in trending and high-volatility regimes.
///
/// Classic divergence (reversal):
///   Bullish: price makes a lower low but MACD histogram makes a higher low → Buy.
///   Bearish: price makes a higher high but MACD histogram makes a lower high → Sell.
///
/// Hidden divergence (trend continuation):
///   Bullish: price makes a higher low but MACD histogram makes a lower low → Buy.
///   Bearish: price makes a lower high but MACD histogram makes a higher high → Sell.
///
/// Also generates trend-continuation signals on MACD zero-line crossovers
/// when no divergence is present, confirmed by histogram direction.
///
/// Enterprise-grade filters:
///   1.  Candle order validation
///   2.  ADX trend-strength gate
///   3.  Histogram turn confirmation
///   4.  Spread safety guard
///   5.  Volume confirmation
///   6.  RSI momentum filter
///   7.  Gap detection
///   8.  Multi-bar pivot divergence detection (configurable radius)
///   9.  Hidden divergence detection
///  10.  Right-to-left scan (most recent swing prioritised)
///  11.  Dynamic SL/TP scaling by ADX
///  12.  Swing-structure SL placement
///  13.  Swing-structure TP placement
///  14.  Risk-reward validation
///  15.  Composite confidence scoring (7 weighted factors)
///  16.  Confidence-based lot sizing
///  17.  Slippage buffer
///  18.  MACD line divergence detection (configurable line vs histogram)
///  19.  Histogram zero-cross validation between swing points
///  20.  Current bar developing-pivot validation
///  21.  Stricter ADX gate + confidence penalty for zero-line crossovers
///  22.  Divergence age decay in confidence scoring
///  23.  Market regime filtering (skip unfavourable regimes, penalise borderline)
///  24.  Signal cooldown (prevent duplicate signals from same divergence structure)
///  25.  Multi-timeframe confidence modifier (soft penalty within evaluator)
///  26.  Triple divergence detection (3 aligned swings → confidence boost)
///  27.  Configurable MACD line zero-cross requirement
///  28.  Indicator pivot validation (require indicator swing at divergence point)
///  29.  MACD signal-line crossover confirmation for entry timing
///  30.  Minimum indicator divergence magnitude threshold (ATR-normalised)
///  31.  Hidden divergence trend alignment (slow EMA direction gate)
///  32.  Multi-bar histogram momentum check for zero-line crossovers
///  33.  Candlestick pattern hard gate (engulfing/hammer/etc. required)
///  34.  Divergence slope measurement in confidence scoring
///  35.  Partial take-profit with scale-out at swing target
///  36.  Persistent cooldown via DB seeding on cache miss
/// </summary>
public class MACDDivergenceEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<MACDDivergenceEvaluator> _logger;
    private readonly TradingMetrics _metrics;
    private readonly IMarketRegimeDetector _regimeDetector;
    private readonly IMultiTimeframeFilter _mtfFilter;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Tracks the candle timestamp of the last signal generated per strategy,
    /// enabling the cooldown mechanism to prevent duplicate signals from the
    /// same divergence structure.
    /// </summary>
    private readonly ConcurrentDictionary<long, DateTime> _lastSignalTimestamps = new();

    /// <summary>
    /// Tracks which strategy IDs have had their cooldown seeded from the DB.
    /// Prevents repeated DB queries once a strategy's cooldown is initialised.
    /// </summary>
    private readonly ConcurrentDictionary<long, bool> _cooldownSeeded = new();

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "MacdDivergence");

    public MACDDivergenceEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<MACDDivergenceEvaluator> logger,
        TradingMetrics metrics,
        IMarketRegimeDetector regimeDetector,
        IMultiTimeframeFilter mtfFilter,
        IServiceScopeFactory? scopeFactory = null)
    {
        _options         = options;
        _logger          = logger;
        _metrics         = metrics;
        _regimeDetector  = regimeDetector;
        _mtfFilter       = mtfFilter;
        _scopeFactory    = scopeFactory;

        if (_options.MacdDivergenceCooldownBars > 0
            && !_options.MacdDivergenceCooldownPersistenceEnabled)
        {
            _logger.LogWarning(
                "MACD divergence signal cooldown is in-memory only — cooldown state will be lost on application restart. " +
                "Enable MacdDivergenceCooldownPersistenceEnabled for DB-backed persistence");
        }
    }

    public StrategyType StrategyType => StrategyType.MACDDivergence;

    public int MinRequiredCandles(Strategy strategy)
    {
        int slowPeriod = 26, divergenceLookback = 10;
        ParseParameters(strategy.ParametersJson, out _, out slowPeriod, out _, out divergenceLookback);

        int emaWarmup = slowPeriod * 2;
        int adxRequirement = _options.MacdDivergenceMinAdx > 0 ? _options.MacdDivergenceAdxPeriod * 2 : 0;
        bool rsiEnabled = _options.MacdDivergenceMaxRsiForBuy > 0 || _options.MacdDivergenceMinRsiForSell > 0;
        int rsiRequirement = rsiEnabled ? _options.MacdDivergenceRsiPeriod * 2 : 0;
        int volumeRequirement = _options.MacdDivergenceMinVolume > 0 || _options.MacdDivergenceWeightVolume > 0
            ? _options.MacdDivergenceVolumeLookbackBars
            : 0;
        int swingTpRequirement = _options.MacdDivergenceSwingTpEnabled
            ? _options.MacdDivergenceSwingTpLookbackBars
            : 0;
        int trendEmaRequirement = _options.MacdDivergenceHiddenRequireTrendAlignment
            ? _options.MacdDivergenceHiddenTrendEmaPeriod * 2
            : 0;

        return Math.Max(
            Math.Max(emaWarmup + divergenceLookback, _options.AtrPeriodForSlTp),
            Math.Max(Math.Max(adxRequirement, rsiRequirement),
                     Math.Max(Math.Max(volumeRequirement, swingTpRequirement), trendEmaRequirement))) + 2;
    }

    public async Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        ParseParameters(strategy.ParametersJson, out int fastPeriod, out int slowPeriod, out int signalPeriod, out int divergenceLookback);

        // ── 0. Candle order validation ───────────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return null;
        }

        int required = MinRequiredCandles(strategy);
        if (candles.Count < required)
            return null;

        // ── 0a. Market regime filtering ──────────────────────────────────────
        MarketRegimeEnum? detectedRegime = null;
        if (_options.MacdDivergenceRegimeFilterEnabled)
        {
            var snapshot = await _regimeDetector.DetectAsync(
                strategy.Symbol, strategy.Timeframe, candles, cancellationToken);
            detectedRegime = snapshot.Regime;

            if (!_options.MacdDivergenceAllowedRegimes.Contains(snapshot.Regime))
            {
                LogRejection(strategy, "Regime", $"Regime {snapshot.Regime} not in allowed list");
                return null;
            }
        }

        // ── 0b. Signal cooldown (with optional DB persistence) ────────────────
        if (_options.MacdDivergenceCooldownBars > 0)
        {
            // Seed from DB on first access if persistence is enabled
            if (_options.MacdDivergenceCooldownPersistenceEnabled
                && _scopeFactory is not null
                && _cooldownSeeded.TryAdd(strategy.Id, true))
            {
                await SeedCooldownFromDbAsync(strategy.Id, cancellationToken);
            }

            if (_lastSignalTimestamps.TryGetValue(strategy.Id, out var lastSignalTs))
            {
                int barsSinceSignal = 0;
                for (int i = candles.Count - 1; i >= 0; i--)
                {
                    if (candles[i].Timestamp <= lastSignalTs) break;
                    barsSinceSignal++;
                }

                if (barsSinceSignal < _options.MacdDivergenceCooldownBars)
                {
                    _logger.LogDebug(
                        "MACD divergence skipped for {Symbol} (strategy {StrategyId}): cooldown active ({Bars}/{Required} bars)",
                        strategy.Symbol, strategy.Id, barsSinceSignal, _options.MacdDivergenceCooldownBars);
                    return null;
                }
            }
        }

        int last = candles.Count - 1;

        // ── 1. ATR (needed by multiple filters) ─────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, last, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return null;
        }

        // ── 2. Gap detection ─────────────────────────────────────────────────
        if (_options.MacdDivergenceMaxGapAtrFraction > 0)
        {
            decimal gap = Math.Abs(candles[last].Open - candles[last - 1].Close);
            if (gap > atr * _options.MacdDivergenceMaxGapAtrFraction)
            {
                LogRejection(strategy, "Gap",
                    $"Price gap {gap:F6} > {_options.MacdDivergenceMaxGapAtrFraction:F1}× ATR");
                return null;
            }
        }

        // ── 3. Spread safety guard ──────────────────────────────────────────
        if (_options.MacdDivergenceMaxSpreadAtrFraction > 0)
        {
            decimal spread = currentPrice.Ask - currentPrice.Bid;
            if (spread > atr * _options.MacdDivergenceMaxSpreadAtrFraction)
            {
                LogRejection(strategy, "Spread",
                    $"Spread {spread:F6} > {_options.MacdDivergenceMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
                return null;
            }
        }

        // ── 4. Volume filter ─────────────────────────────────────────────────
        if (_options.MacdDivergenceMinVolume > 0 && candles[last].Volume < _options.MacdDivergenceMinVolume)
        {
            LogRejection(strategy, "Volume",
                $"Volume {candles[last].Volume:F0} < minimum {_options.MacdDivergenceMinVolume:F0}");
            return null;
        }

        // ── 5. ADX trend-strength gate ──────────────────────────────────────
        decimal adxValue = 0m;
        if (_options.MacdDivergenceMinAdx > 0 || _options.MacdDivergenceWeightAdx > 0
            || _options.MacdDivergenceDynamicSlTp)
        {
            adxValue = IndicatorCalculator.Adx(candles, last, _options.MacdDivergenceAdxPeriod);
            if (_options.MacdDivergenceMinAdx > 0 && adxValue < _options.MacdDivergenceMinAdx)
            {
                LogRejection(strategy, "ADX",
                    $"ADX {adxValue:F2} < minimum {_options.MacdDivergenceMinAdx:F2} — ranging market");
                return null;
            }
        }

        // ── 6. Compute MACD line, signal line, and histogram ────────────────
        var closes    = IndicatorCalculator.ExtractCloses(candles);
        var fastEma   = IndicatorCalculator.EmaSeries(closes, fastPeriod);
        var slowEma   = IndicatorCalculator.EmaSeries(closes, slowPeriod);
        var macdLine  = new decimal[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            macdLine[i] = fastEma[i] - slowEma[i];

        var signalLine = IndicatorCalculator.EmaSeries(macdLine, signalPeriod);
        var histogram  = new decimal[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            histogram[i] = macdLine[i] - signalLine[i];

        cancellationToken.ThrowIfCancellationRequested();

        // ── 7. Signal detection ─────────────────────────────────────────────
        TradeDirection? direction = null;
        DivergenceType divergenceType = DivergenceType.None;
        DivergenceSource divergenceSource = DivergenceSource.None;
        int swingIndex = -1;
        bool isCrossoverOnly = false;

        int pivotRadius = Math.Max(1, _options.MacdDivergencePivotRadius);
        bool requireCurrentPivot = _options.MacdDivergenceRequireCurrentBarPivot;
        bool requireZeroCross = _options.MacdDivergenceRequireHistogramZeroCross;
        bool requireIndPivot = _options.MacdDivergenceRequireIndicatorPivot;
        decimal minIndDelta = _options.MacdDivergenceMinIndicatorDeltaAtrFraction > 0
            ? atr * _options.MacdDivergenceMinIndicatorDeltaAtrFraction
            : 0m;

        // Classic divergence on histogram (reversal)
        var classicResult = DetectClassicDivergence(
            candles, histogram, last, divergenceLookback, pivotRadius,
            requireCurrentPivot, requireZeroCross, requireIndPivot, minIndDelta);
        if (classicResult.HasValue)
        {
            direction        = classicResult.Value.Direction;
            divergenceType   = DivergenceType.Classic;
            divergenceSource = DivergenceSource.Histogram;
            swingIndex       = classicResult.Value.SwingIndex;
        }

        // Classic divergence on MACD line (if enabled and histogram didn't fire)
        if (!classicResult.HasValue && _options.MacdDivergenceUseMacdLine)
        {
            var lineResult = DetectClassicDivergence(
                candles, macdLine, last, divergenceLookback, pivotRadius,
                requireCurrentPivot, requireZeroCross: _options.MacdDivergenceRequireMacdLineZeroCross,
                requireIndicatorPivot: requireIndPivot, minIndicatorDelta: minIndDelta);
            if (lineResult.HasValue)
            {
                direction        = lineResult.Value.Direction;
                divergenceType   = DivergenceType.Classic;
                divergenceSource = DivergenceSource.MacdLine;
                swingIndex       = lineResult.Value.SwingIndex;
            }
        }

        // Hidden divergence on histogram (trend continuation) — only if classic not found
        if (direction is null && _options.MacdDivergenceDetectHidden)
        {
            var hiddenResult = DetectHiddenDivergence(
                candles, histogram, last, divergenceLookback, pivotRadius,
                requireCurrentPivot, requireZeroCross, requireIndPivot, minIndDelta);
            if (hiddenResult.HasValue)
            {
                direction        = hiddenResult.Value.Direction;
                divergenceType   = DivergenceType.Hidden;
                divergenceSource = DivergenceSource.Histogram;
                swingIndex       = hiddenResult.Value.SwingIndex;
            }
        }

        // Hidden divergence on MACD line (if enabled and histogram didn't fire)
        if (direction is null && _options.MacdDivergenceDetectHidden && _options.MacdDivergenceUseMacdLine)
        {
            var lineResult = DetectHiddenDivergence(
                candles, macdLine, last, divergenceLookback, pivotRadius,
                requireCurrentPivot, requireZeroCross: _options.MacdDivergenceRequireMacdLineZeroCross,
                requireIndicatorPivot: requireIndPivot, minIndicatorDelta: minIndDelta);
            if (lineResult.HasValue)
            {
                direction        = lineResult.Value.Direction;
                divergenceType   = DivergenceType.Hidden;
                divergenceSource = DivergenceSource.MacdLine;
                swingIndex       = lineResult.Value.SwingIndex;
            }
        }

        // ── 7b. Hidden divergence trend alignment gate ─────────────────────
        if (divergenceType == DivergenceType.Hidden && direction.HasValue
            && _options.MacdDivergenceHiddenRequireTrendAlignment)
        {
            var trendEma = IndicatorCalculator.EmaSeries(closes, _options.MacdDivergenceHiddenTrendEmaPeriod);
            decimal currentClose = closes[last];
            decimal emaValue = trendEma[last];
            bool aligned = direction.Value == TradeDirection.Buy
                ? currentClose > emaValue
                : currentClose < emaValue;

            if (!aligned)
            {
                LogRejection(strategy, "HiddenTrendAlignment",
                    $"{direction.Value} not aligned with trend EMA (close={currentClose:F5}, EMA={emaValue:F5})");
                // Reset to allow fallback to zero-line crossover
                direction = null;
                divergenceType = DivergenceType.None;
                divergenceSource = DivergenceSource.None;
                swingIndex = -1;
            }
        }

        // Fallback: MACD zero-line crossover (trend continuation)
        if (direction is null)
        {
            const decimal zeroLineEpsilon = 0.000001m;
            int momentumBars = Math.Max(1, _options.MacdDivergenceCrossoverMomentumBars);
            bool bullishCross = macdLine[last - 1] <= zeroLineEpsilon && macdLine[last] > zeroLineEpsilon
                && HasHistogramMomentum(histogram, last, momentumBars, isBullish: true);
            bool bearishCross = macdLine[last - 1] >= -zeroLineEpsilon && macdLine[last] < -zeroLineEpsilon
                && HasHistogramMomentum(histogram, last, momentumBars, isBullish: false);

            if (bullishCross)
                direction = TradeDirection.Buy;
            else if (bearishCross)
                direction = TradeDirection.Sell;
            else
                return null;

            isCrossoverOnly = true;

            // Stricter ADX gate for crossover-only signals
            if (_options.MacdDivergenceCrossoverMinAdx > 0)
            {
                if (adxValue == 0m)
                    adxValue = IndicatorCalculator.Adx(candles, last, _options.MacdDivergenceAdxPeriod);

                if (adxValue < _options.MacdDivergenceCrossoverMinAdx)
                {
                    LogRejection(strategy, "CrossoverADX",
                        $"ADX {adxValue:F2} < crossover minimum {_options.MacdDivergenceCrossoverMinAdx:F2}");
                    return null;
                }
            }
        }

        // ── 8. Histogram turn confirmation ──────────────────────────────────
        if (_options.MacdDivergenceRequireHistogramTurn)
        {
            bool turning = direction == TradeDirection.Buy
                ? histogram[last] > histogram[last - 1]
                : histogram[last] < histogram[last - 1];

            if (!turning)
            {
                LogRejection(strategy, "HistogramTurn", "Histogram not turning in signal direction");
                return null;
            }
        }

        // ── 8b. Signal-line crossover confirmation ──────────────────────────
        if (_options.MacdDivergenceRequireSignalLineCross && !isCrossoverOnly)
        {
            bool isBull = direction == TradeDirection.Buy;
            if (!HasRecentSignalLineCross(macdLine, signalLine, last,
                    _options.MacdDivergenceSignalCrossLookback, isBull))
            {
                LogRejection(strategy, "SignalLineCross",
                    $"No signal-line crossover within {_options.MacdDivergenceSignalCrossLookback} bars");
                return null;
            }
        }

        // ── 8c. Candlestick pattern hard gate ─────────────────────────────
        if (_options.MacdDivergenceRequireCandlePatternConfirmation)
        {
            decimal patternScore = IndicatorCalculator.ScoreCandlePatterns(
                candles, last, direction == TradeDirection.Buy);
            if (patternScore < _options.MacdDivergenceMinCandlePatternScore)
            {
                _logger.LogDebug(
                    "MACD divergence skipped for {Symbol}: candle pattern score {Score:F3} below minimum {Min}",
                    strategy.Symbol, patternScore, _options.MacdDivergenceMinCandlePatternScore);
                _metrics.EvaluatorRejections.Add(1, EvaluatorTag);
                return null;
            }
        }

        // ── 9. RSI momentum filter ──────────────────────────────────────────
        decimal rsiValue = 50m;
        bool rsiEnabled = _options.MacdDivergenceMaxRsiForBuy > 0 || _options.MacdDivergenceMinRsiForSell > 0
                          || _options.MacdDivergenceWeightRsi > 0;
        if (rsiEnabled)
        {
            rsiValue = IndicatorCalculator.Rsi(candles, last, _options.MacdDivergenceRsiPeriod);

            if (direction == TradeDirection.Buy && _options.MacdDivergenceMaxRsiForBuy > 0
                && rsiValue > _options.MacdDivergenceMaxRsiForBuy)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} > max {_options.MacdDivergenceMaxRsiForBuy:F2} — overbought buy rejected");
                return null;
            }

            if (direction == TradeDirection.Sell && _options.MacdDivergenceMinRsiForSell > 0
                && rsiValue < _options.MacdDivergenceMinRsiForSell)
            {
                LogRejection(strategy, "RSI",
                    $"RSI {rsiValue:F2} < min {_options.MacdDivergenceMinRsiForSell:F2} — oversold sell rejected");
                return null;
            }
        }

        // ── 10. Entry price + slippage buffer ───────────────────────────────
        decimal entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;
        if (_options.MacdDivergenceSlippageAtrFraction > 0)
        {
            decimal slippage = atr * _options.MacdDivergenceSlippageAtrFraction;
            entryPrice += direction == TradeDirection.Buy ? slippage : -slippage;
        }

        // ── 11. SL/TP calculation with dynamic ADX scaling ──────────────────
        decimal slMultiplier = _options.StopLossAtrMultiplier;
        decimal tpMultiplier = _options.TakeProfitAtrMultiplier;

        if (_options.MacdDivergenceDynamicSlTp && adxValue > 0)
        {
            decimal strongAdx = _options.MacdDivergenceStrongAdxThreshold;
            decimal t = strongAdx > 0 ? Math.Clamp(adxValue / strongAdx, 0m, 1m) : 0m;
            slMultiplier *= Lerp(1.0m, _options.MacdDivergenceStrongTrendSlScale, t);
            tpMultiplier *= Lerp(1.0m, _options.MacdDivergenceStrongTrendTpScale, t);
        }

        decimal stopDistance   = atr * slMultiplier;
        decimal profitDistance = atr * tpMultiplier;

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

        // ── 12. Swing-structure SL override ─────────────────────────────────
        bool isDivergence = divergenceType != DivergenceType.None;
        if (_options.MacdDivergenceSwingSlEnabled && isDivergence && swingIndex >= 0)
        {
            decimal buffer = atr * _options.MacdDivergenceSwingSlBufferAtrFraction;
            decimal minSlDist = atr * _options.MacdDivergenceSwingSlMinAtrMultiplier;
            decimal maxSlDist = atr * _options.MacdDivergenceSwingSlMaxAtrMultiplier;

            if (direction == TradeDirection.Buy)
            {
                decimal swingLow = candles[swingIndex].Low;
                decimal candidateSl = swingLow - buffer;
                decimal dist = entryPrice - candidateSl;
                if (dist >= minSlDist && dist <= maxSlDist)
                    stopLoss = candidateSl;
            }
            else
            {
                decimal swingHigh = candles[swingIndex].High;
                decimal candidateSl = swingHigh + buffer;
                decimal dist = candidateSl - entryPrice;
                if (dist >= minSlDist && dist <= maxSlDist)
                    stopLoss = candidateSl;
            }
        }

        // ── 13. Swing-structure TP override ─────────────────────────────────
        if (_options.MacdDivergenceSwingTpEnabled)
        {
            decimal tpBuffer = atr * _options.MacdDivergenceSwingTpBufferAtrFraction;
            decimal minTpDist = atr * _options.MacdDivergenceSwingTpMinAtrMultiplier;
            decimal maxTpDist = atr * _options.MacdDivergenceSwingTpMaxAtrMultiplier;

            if (direction == TradeDirection.Buy)
            {
                decimal swingHigh = IndicatorCalculator.FindSwingHigh(
                    candles, last, _options.MacdDivergenceSwingTpLookbackBars);
                decimal candidateTp = swingHigh - tpBuffer;
                decimal dist = candidateTp - entryPrice;
                if (dist >= minTpDist && dist <= maxTpDist)
                    takeProfit = candidateTp;
            }
            else
            {
                decimal swingLow = IndicatorCalculator.FindSwingLow(
                    candles, last, _options.MacdDivergenceSwingTpLookbackBars);
                decimal candidateTp = swingLow + tpBuffer;
                decimal dist = entryPrice - candidateTp;
                if (dist >= minTpDist && dist <= maxTpDist)
                    takeProfit = candidateTp;
            }
        }

        // ── 13b. Partial take-profit ──────────────────────────────────────
        decimal? partialTakeProfit = null;
        decimal? partialClosePercent = null;
        if (_options.MacdDivergencePartialTpEnabled)
        {
            decimal partialDist = atr * _options.MacdDivergencePartialTpAtrMultiplier;

            // Use swing-structure target as partial TP if it's closer than the full TP
            if (_options.MacdDivergenceSwingTpEnabled)
            {
                decimal tpBuffer = atr * _options.MacdDivergenceSwingTpBufferAtrFraction;
                if (direction == TradeDirection.Buy)
                {
                    decimal swingHigh = IndicatorCalculator.FindSwingHigh(
                        candles, last, _options.MacdDivergenceSwingTpLookbackBars);
                    decimal candidatePartial = swingHigh - tpBuffer;
                    decimal dist = candidatePartial - entryPrice;
                    if (dist > 0 && dist < Math.Abs(takeProfit - entryPrice))
                        partialDist = dist;
                }
                else
                {
                    decimal swingLow = IndicatorCalculator.FindSwingLow(
                        candles, last, _options.MacdDivergenceSwingTpLookbackBars);
                    decimal candidatePartial = swingLow + tpBuffer;
                    decimal dist = entryPrice - candidatePartial;
                    if (dist > 0 && dist < Math.Abs(entryPrice - takeProfit))
                        partialDist = dist;
                }
            }

            decimal partialTp = direction == TradeDirection.Buy
                ? entryPrice + partialDist
                : entryPrice - partialDist;

            // Only set partial TP if it's between entry and full TP
            bool validPartial = direction == TradeDirection.Buy
                ? partialTp > entryPrice && partialTp < takeProfit
                : partialTp < entryPrice && partialTp > takeProfit;

            if (validPartial)
            {
                partialTakeProfit = partialTp;
                partialClosePercent = _options.MacdDivergencePartialClosePercent;
            }
        }

        // ── 14. Risk-reward validation ──────────────────────────────────────
        if (_options.MacdDivergenceMinRiskRewardRatio > 0)
        {
            decimal slDist     = Math.Abs(entryPrice - stopLoss);
            decimal tpDist     = Math.Abs(takeProfit - entryPrice);
            decimal riskReward = slDist > 0 ? tpDist / slDist : 0m;
            if (riskReward < _options.MacdDivergenceMinRiskRewardRatio)
            {
                LogRejection(strategy, "RiskReward",
                    $"R:R {riskReward:F2} < minimum {_options.MacdDivergenceMinRiskRewardRatio:F2} (SL={slDist:F6}, TP={tpDist:F6})");
                return null;
            }
        }

        // ── 15. Composite confidence scoring ────────────────────────────────
        decimal confidence = ComputeConfidence(
            candles, last, atr, histogram, divergenceLookback,
            divergenceType, divergenceSource, swingIndex,
            direction.Value, adxValue, rsiValue);

        // ── 15b. Crossover confidence penalty ────────────────────────────────
        if (isCrossoverOnly && _options.MacdDivergenceCrossoverConfidencePenalty > 0)
            confidence = Math.Clamp(confidence - _options.MacdDivergenceCrossoverConfidencePenalty, 0m, 1m);

        // ── 15c. Triple divergence confidence boost ──────────────────────────
        if (_options.MacdDivergenceTripleDivergenceEnabled && isDivergence && swingIndex >= 0)
        {
            bool hasTriple = HasTripleDivergence(
                candles, histogram,
                swingIndex, divergenceLookback, pivotRadius, direction.Value, divergenceType);
            if (hasTriple)
            {
                confidence = Math.Clamp(confidence + _options.MacdDivergenceTripleDivergenceBonus, 0m, 1m);
                _logger.LogDebug(
                    "MACD triple divergence detected for {Symbol}: +{Bonus} confidence",
                    strategy.Symbol, _options.MacdDivergenceTripleDivergenceBonus);
            }
        }

        // ── 15d. Market regime confidence penalty ────────────────────────────
        if (detectedRegime.HasValue
            && _options.MacdDivergenceRegimeConfidencePenalty.TryGetValue(detectedRegime.Value, out var regimePenalty)
            && regimePenalty > 0)
        {
            confidence = Math.Clamp(confidence - regimePenalty, 0m, 1m);
        }

        // ── 15e. Multi-timeframe confidence modifier ─────────────────────────
        if (_options.MacdDivergenceMtfConfidenceEnabled && _options.MacdDivergenceMtfConfidenceWeight > 0)
        {
            decimal mtfStrength = await _mtfFilter.GetConfirmationStrengthAsync(
                strategy.Symbol, direction.Value.ToString(),
                strategy.Timeframe.ToString(), cancellationToken);
            decimal mtfFactor = 1.0m - _options.MacdDivergenceMtfConfidenceWeight * (1.0m - mtfStrength);
            confidence = Math.Clamp(confidence * mtfFactor, 0.1m, 1.0m);
        }

        // ── 16. Lot sizing ──────────────────────────────────────────────────
        decimal lotSize = _options.DefaultLotSize;
        if (_options.MacdDivergenceConfidenceLotSizing)
        {
            lotSize = Lerp(_options.MacdDivergenceMinLotSize, _options.MacdDivergenceMaxLotSize, confidence);
            lotSize = Math.Round(lotSize, 2);
        }

        _metrics.SignalsGenerated.Add(1, EvaluatorTag,
            new("divergence_type", divergenceType.ToString()),
            new("divergence_source", divergenceSource.ToString()));

        _logger.LogDebug(
            "MACDDivergence signal generated for {Symbol} (strategy {StrategyId}): " +
            "{Direction}, divergence={DivergenceType}/{DivergenceSource}, " +
            "swingAge={SwingAge} bars, confidence={Confidence:F3}, lotSize={LotSize}",
            strategy.Symbol, strategy.Id,
            direction.Value, divergenceType, divergenceSource,
            swingIndex >= 0 ? last - swingIndex : -1, confidence, lotSize);

        // ── Record cooldown timestamp ──────────────────────────────────────
        if (_options.MacdDivergenceCooldownBars > 0)
            _lastSignalTimestamps[strategy.Id] = candles[last].Timestamp;

        var now = candles[last].Timestamp;
        return new TradeSignal
        {
            StrategyId       = strategy.Id,
            Symbol           = strategy.Symbol,
            Direction        = direction.Value,
            EntryPrice       = entryPrice,
            StopLoss         = stopLoss,
            TakeProfit       = takeProfit,
            SuggestedLotSize    = lotSize,
            Confidence          = confidence,
            PartialTakeProfit   = partialTakeProfit,
            PartialClosePercent = partialClosePercent,
            Status              = TradeSignalStatus.Pending,
            GeneratedAt         = now,
            ExpiresAt           = now.AddMinutes(_options.MacdDivergenceExpiryMinutes)
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Divergence type
    // ═════════════════════════════════════════════════════════════════════════

    private enum DivergenceType { None, Classic, Hidden }
    private enum DivergenceSource { None, Histogram, MacdLine }

    // ═════════════════════════════════════════════════════════════════════════
    // Composite confidence
    // ═════════════════════════════════════════════════════════════════════════

    private decimal ComputeConfidence(
        IReadOnlyList<Candle> candles, int lastIdx, decimal atr, decimal[] histogram,
        int divergenceLookback,
        DivergenceType divergenceType, DivergenceSource divergenceSource, int swingIndex,
        TradeDirection direction, decimal adxValue, decimal rsiValue)
    {
        bool isDivergence = divergenceType != DivergenceType.None;
        decimal totalWeight = 0m;
        decimal weightedSum = 0m;

        // Factor 1: Divergence magnitude (price distance between swing and current, normalised by ATR)
        decimal wMagnitude = _options.MacdDivergenceWeightMagnitude;
        if (wMagnitude > 0)
        {
            decimal magnitudeScore;
            if (isDivergence && swingIndex >= 0 && atr > 0)
            {
                decimal priceDist = direction == TradeDirection.Buy
                    ? Math.Abs(candles[swingIndex].Low - candles[lastIdx].Low)
                    : Math.Abs(candles[lastIdx].High - candles[swingIndex].High);
                magnitudeScore = Math.Clamp(priceDist / (atr * 2m), 0m, 1m);
            }
            else
            {
                decimal macdMag = Math.Abs(histogram[lastIdx]);
                magnitudeScore = Math.Clamp(macdMag / atr, 0m, 1m) * 0.5m;
            }
            weightedSum += wMagnitude * magnitudeScore;
            totalWeight += wMagnitude;
        }

        // Factor 2: Histogram turning strength
        decimal wHistTurn = _options.MacdDivergenceWeightHistogramTurn;
        if (wHistTurn > 0)
        {
            decimal histDelta = histogram[lastIdx] - histogram[lastIdx - 1];
            decimal turnScore = direction == TradeDirection.Buy
                ? Math.Clamp(histDelta / atr, 0m, 1m)
                : Math.Clamp(-histDelta / atr, 0m, 1m);
            weightedSum += wHistTurn * turnScore;
            totalWeight += wHistTurn;
        }

        // Factor 3: ADX strength
        decimal wAdx = _options.MacdDivergenceWeightAdx;
        if (wAdx > 0 && adxValue > 0)
        {
            decimal adxScore = Math.Clamp((adxValue - 10m) / 40m, 0m, 1m);
            weightedSum += wAdx * adxScore;
            totalWeight += wAdx;
        }

        // Factor 4: Candle pattern confirmation
        decimal wPattern = _options.MacdDivergenceWeightCandlePattern;
        if (wPattern > 0)
        {
            decimal patternScore = IndicatorCalculator.ScoreCandlePatterns(
                candles, lastIdx, direction == TradeDirection.Buy);
            weightedSum += wPattern * patternScore;
            totalWeight += wPattern;
        }

        // Factor 5: RSI alignment
        decimal wRsi = _options.MacdDivergenceWeightRsi;
        if (wRsi > 0)
        {
            decimal rsiScore;
            if (direction == TradeDirection.Buy)
                rsiScore = Math.Clamp((50m - rsiValue) / 30m + 0.5m, 0m, 1m);
            else
                rsiScore = Math.Clamp((rsiValue - 50m) / 30m + 0.5m, 0m, 1m);
            weightedSum += wRsi * rsiScore;
            totalWeight += wRsi;
        }

        // Factor 6: Volume relative to average
        decimal wVolume = _options.MacdDivergenceWeightVolume;
        if (wVolume > 0 && lastIdx >= _options.MacdDivergenceVolumeLookbackBars)
        {
            decimal avgVol = 0m;
            int volStart = lastIdx - _options.MacdDivergenceVolumeLookbackBars;
            for (int i = volStart; i < lastIdx; i++)
                avgVol += candles[i].Volume;
            avgVol /= _options.MacdDivergenceVolumeLookbackBars;

            decimal volumeScore = avgVol > 0
                ? Math.Clamp(candles[lastIdx].Volume / (avgVol * 2m), 0m, 1m)
                : 0.5m;
            weightedSum += wVolume * volumeScore;
            totalWeight += wVolume;
        }

        // Factor 7: Divergence age decay — recent swing points are more actionable
        decimal wAge = _options.MacdDivergenceWeightAge;
        if (wAge > 0 && isDivergence && swingIndex >= 0 && divergenceLookback > 0)
        {
            int barDistance = lastIdx - swingIndex;
            decimal ageScore = Math.Clamp(1m - (decimal)barDistance / divergenceLookback, 0m, 1m);
            weightedSum += wAge * ageScore;
            totalWeight += wAge;
        }

        // Factor 8: Divergence slope — steeper indicator divergence from price is stronger
        decimal wSlope = _options.MacdDivergenceWeightSlope;
        if (wSlope > 0 && isDivergence && swingIndex >= 0 && atr > 0)
        {
            int barDist = lastIdx - swingIndex;
            if (barDist > 0)
            {
                decimal indicatorDelta = Math.Abs(histogram[lastIdx] - histogram[swingIndex]);
                decimal slopePerBar = indicatorDelta / barDist;
                // Normalise: slope of 1 ATR over the lookback distance scores 1.0
                decimal slopeScore = Math.Clamp(slopePerBar * divergenceLookback / atr, 0m, 1m);
                weightedSum += wSlope * slopeScore;
                totalWeight += wSlope;
            }
        }

        decimal baseConfidence = totalWeight > 0 ? weightedSum / totalWeight : _options.MacdDivergenceConfidence;

        decimal bonus = divergenceType switch
        {
            DivergenceType.Classic => _options.MacdDivergenceClassicBonus,
            DivergenceType.Hidden  => _options.MacdDivergenceHiddenBonus,
            _                      => 0m
        };

        if (divergenceSource == DivergenceSource.MacdLine)
            bonus += _options.MacdDivergenceLineSourceBonus;

        return Math.Clamp(baseConfidence + bonus, 0m, 1m);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Classic divergence detection (reversal)
    // Scans right-to-left to prioritise the most recent swing point.
    // Uses N-bar pivot detection for reliable swing identification.
    // ═════════════════════════════════════════════════════════════════════════

    private static (TradeDirection Direction, int SwingIndex)? DetectClassicDivergence(
        IReadOnlyList<Candle> candles, decimal[] indicator, int currentIndex, int lookback, int pivotRadius,
        bool requireCurrentBarPivot, bool requireZeroCross,
        bool requireIndicatorPivot = false, decimal minIndicatorDelta = 0m)
    {
        int start = currentIndex - lookback;
        if (start < pivotRadius) start = pivotRadius;
        int end = currentIndex - pivotRadius;
        if (end <= start) return null;

        decimal currentLow  = candles[currentIndex].Low;
        decimal currentHigh = candles[currentIndex].High;
        decimal currentInd  = indicator[currentIndex];

        const decimal epsilon = 0.000001m;

        // Bullish classic: price lower low, indicator higher low (scan right-to-left)
        if (currentInd < -epsilon && (!requireCurrentBarPivot || IsLeftPivotLow(candles, currentIndex, pivotRadius)))
        {
            for (int i = end; i >= start; i--)
            {
                if (IsPivotLow(candles, i, pivotRadius)
                    && candles[i].Low > currentLow
                    && indicator[i] < currentInd)
                {
                    if (requireIndicatorPivot && !IsIndicatorPivotLow(indicator, i, pivotRadius))
                        continue;
                    if (minIndicatorDelta > 0 && Math.Abs(currentInd - indicator[i]) < minIndicatorDelta)
                        continue;
                    if (requireZeroCross && !HasZeroCross(indicator, i, currentIndex))
                        continue;
                    return (TradeDirection.Buy, i);
                }
            }
        }

        // Bearish classic: price higher high, indicator lower high (scan right-to-left)
        if (currentInd > epsilon)
        {
            if (requireCurrentBarPivot && !IsLeftPivotHigh(candles, currentIndex, pivotRadius))
                return null;

            for (int i = end; i >= start; i--)
            {
                if (IsPivotHigh(candles, i, pivotRadius)
                    && candles[i].High < currentHigh
                    && indicator[i] > currentInd)
                {
                    if (requireIndicatorPivot && !IsIndicatorPivotHigh(indicator, i, pivotRadius))
                        continue;
                    if (minIndicatorDelta > 0 && Math.Abs(indicator[i] - currentInd) < minIndicatorDelta)
                        continue;
                    if (requireZeroCross && !HasZeroCross(indicator, i, currentIndex))
                        continue;
                    return (TradeDirection.Sell, i);
                }
            }
        }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Hidden divergence detection (trend continuation)
    // Bullish hidden: price higher low, indicator lower low → continuation up.
    // Bearish hidden: price lower high, indicator higher high → continuation down.
    // ═════════════════════════════════════════════════════════════════════════

    private static (TradeDirection Direction, int SwingIndex)? DetectHiddenDivergence(
        IReadOnlyList<Candle> candles, decimal[] indicator, int currentIndex, int lookback, int pivotRadius,
        bool requireCurrentBarPivot, bool requireZeroCross,
        bool requireIndicatorPivot = false, decimal minIndicatorDelta = 0m)
    {
        int start = currentIndex - lookback;
        if (start < pivotRadius) start = pivotRadius;
        int end = currentIndex - pivotRadius;
        if (end <= start) return null;

        decimal currentLow  = candles[currentIndex].Low;
        decimal currentHigh = candles[currentIndex].High;
        decimal currentInd  = indicator[currentIndex];

        const decimal epsilon = 0.000001m;

        // Bullish hidden: price higher low, indicator lower low (scan right-to-left)
        if (currentInd < -epsilon && (!requireCurrentBarPivot || IsLeftPivotLow(candles, currentIndex, pivotRadius)))
        {
            for (int i = end; i >= start; i--)
            {
                if (IsPivotLow(candles, i, pivotRadius)
                    && candles[i].Low < currentLow
                    && indicator[i] > currentInd)
                {
                    if (requireIndicatorPivot && !IsIndicatorPivotLow(indicator, i, pivotRadius))
                        continue;
                    if (minIndicatorDelta > 0 && Math.Abs(indicator[i] - currentInd) < minIndicatorDelta)
                        continue;
                    if (requireZeroCross && !HasZeroCross(indicator, i, currentIndex))
                        continue;
                    return (TradeDirection.Buy, i);
                }
            }
        }

        // Bearish hidden: price lower high, indicator higher high (scan right-to-left)
        if (currentInd > epsilon)
        {
            if (requireCurrentBarPivot && !IsLeftPivotHigh(candles, currentIndex, pivotRadius))
                return null;

            for (int i = end; i >= start; i--)
            {
                if (IsPivotHigh(candles, i, pivotRadius)
                    && candles[i].High > currentHigh
                    && indicator[i] < currentInd)
                {
                    if (requireIndicatorPivot && !IsIndicatorPivotHigh(indicator, i, pivotRadius))
                        continue;
                    if (minIndicatorDelta > 0 && Math.Abs(currentInd - indicator[i]) < minIndicatorDelta)
                        continue;
                    if (requireZeroCross && !HasZeroCross(indicator, i, currentIndex))
                        continue;
                    return (TradeDirection.Sell, i);
                }
            }
        }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Triple divergence detection
    // Scans for a second confirming swing point behind the first one, creating
    // a 3-point divergence pattern (current + swing1 + swing2) which is a
    // significantly stronger signal than standard 2-point divergence.
    // ═════════════════════════════════════════════════════════════════════════

    private static bool HasTripleDivergence(
        IReadOnlyList<Candle> candles, decimal[] indicator,
        int firstSwingIndex, int lookback, int pivotRadius,
        TradeDirection direction, DivergenceType divergenceType)
    {
        int start = firstSwingIndex - lookback;
        if (start < pivotRadius) start = pivotRadius;
        int end = firstSwingIndex - pivotRadius;
        if (end <= start) return false;

        // Scan for a second swing point behind the first that continues the divergence pattern
        for (int i = end; i >= start; i--)
        {
            if (direction == TradeDirection.Buy)
            {
                if (!IsPivotLow(candles, i, pivotRadius)) continue;

                if (divergenceType == DivergenceType.Classic)
                {
                    // Classic bullish triple: price makes successively lower lows,
                    // indicator makes successively higher lows
                    if (candles[i].Low > candles[firstSwingIndex].Low
                        && indicator[i] < indicator[firstSwingIndex])
                        return true;
                }
                else // Hidden
                {
                    // Hidden bullish triple: price makes successively higher lows,
                    // indicator makes successively lower lows
                    if (candles[i].Low < candles[firstSwingIndex].Low
                        && indicator[i] > indicator[firstSwingIndex])
                        return true;
                }
            }
            else
            {
                if (!IsPivotHigh(candles, i, pivotRadius)) continue;

                if (divergenceType == DivergenceType.Classic)
                {
                    // Classic bearish triple: price makes successively higher highs,
                    // indicator makes successively lower highs
                    if (candles[i].High < candles[firstSwingIndex].High
                        && indicator[i] > indicator[firstSwingIndex])
                        return true;
                }
                else // Hidden
                {
                    // Hidden bearish triple: price makes successively lower highs,
                    // indicator makes successively higher highs
                    if (candles[i].High > candles[firstSwingIndex].High
                        && indicator[i] < indicator[firstSwingIndex])
                        return true;
                }
            }
        }

        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Multi-bar pivot detection
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsPivotLow(IReadOnlyList<Candle> candles, int index, int radius)
    {
        if (index < radius || index + radius >= candles.Count)
            return false;

        decimal low = candles[index].Low;
        for (int j = 1; j <= radius; j++)
        {
            if (candles[index - j].Low <= low || candles[index + j].Low <= low)
                return false;
        }
        return true;
    }

    private static bool IsPivotHigh(IReadOnlyList<Candle> candles, int index, int radius)
    {
        if (index < radius || index + radius >= candles.Count)
            return false;

        decimal high = candles[index].High;
        for (int j = 1; j <= radius; j++)
        {
            if (candles[index - j].High >= high || candles[index + j].High >= high)
                return false;
        }
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Developing pivot detection (left-side only — current bar hasn't closed)
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsLeftPivotLow(IReadOnlyList<Candle> candles, int index, int radius)
    {
        if (index < radius)
            return false;

        decimal low = candles[index].Low;
        for (int j = 1; j <= radius; j++)
        {
            if (candles[index - j].Low <= low)
                return false;
        }
        return true;
    }

    private static bool IsLeftPivotHigh(IReadOnlyList<Candle> candles, int index, int radius)
    {
        if (index < radius)
            return false;

        decimal high = candles[index].High;
        for (int j = 1; j <= radius; j++)
        {
            if (candles[index - j].High >= high)
                return false;
        }
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Indicator pivot detection (validates the indicator also forms a swing)
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsIndicatorPivotLow(decimal[] indicator, int index, int radius)
    {
        if (index < radius || index + radius >= indicator.Length)
            return false;

        decimal val = indicator[index];
        for (int j = 1; j <= radius; j++)
        {
            if (indicator[index - j] <= val || indicator[index + j] <= val)
                return false;
        }
        return true;
    }

    private static bool IsIndicatorPivotHigh(decimal[] indicator, int index, int radius)
    {
        if (index < radius || index + radius >= indicator.Length)
            return false;

        decimal val = indicator[index];
        for (int j = 1; j <= radius; j++)
        {
            if (indicator[index - j] >= val || indicator[index + j] >= val)
                return false;
        }
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Histogram zero-cross validation
    // Ensures a full oscillation cycle between the two divergence points.
    // ═════════════════════════════════════════════════════════════════════════

    private static bool HasZeroCross(decimal[] indicator, int fromIndex, int toIndex)
    {
        for (int i = fromIndex + 1; i < toIndex; i++)
        {
            if ((indicator[i - 1] < 0 && indicator[i] > 0) ||
                (indicator[i - 1] > 0 && indicator[i] < 0))
                return true;
        }
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Histogram momentum check (multi-bar acceleration for zero-line crossovers)
    // ═════════════════════════════════════════════════════════════════════════

    private static bool HasHistogramMomentum(decimal[] histogram, int lastIdx, int requiredBars, bool isBullish)
    {
        if (requiredBars <= 1)
            return isBullish ? histogram[lastIdx] > histogram[lastIdx - 1]
                             : histogram[lastIdx] < histogram[lastIdx - 1];

        int barsToCheck = Math.Min(requiredBars, lastIdx);
        for (int i = 0; i < barsToCheck; i++)
        {
            int idx = lastIdx - i;
            if (idx < 1) return false;
            bool accelerating = isBullish
                ? histogram[idx] > histogram[idx - 1]
                : histogram[idx] < histogram[idx - 1];
            if (!accelerating) return false;
        }
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Signal-line crossover confirmation
    // Checks whether the MACD line crossed the signal line in the trade
    // direction within the last N bars.
    // ═════════════════════════════════════════════════════════════════════════

    private static bool HasRecentSignalLineCross(
        decimal[] macdLine, decimal[] signalLine, int lastIdx, int lookback, bool isBullish)
    {
        int start = Math.Max(1, lastIdx - lookback + 1);
        for (int i = lastIdx; i >= start; i--)
        {
            if (isBullish)
            {
                if (macdLine[i] > signalLine[i] && macdLine[i - 1] <= signalLine[i - 1])
                    return true;
            }
            else
            {
                if (macdLine[i] < signalLine[i] && macdLine[i - 1] >= signalLine[i - 1])
                    return true;
            }
        }
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Rejection diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));

        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "MACDDivergence signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
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

    private static decimal Lerp(decimal min, decimal max, decimal t)
        => min + (max - min) * Math.Clamp(t, 0m, 1m);

    private static void ParseParameters(string? json, out int fast, out int slow, out int signal, out int divLookback)
    {
        fast = 12; slow = 26; signal = 9; divLookback = 10;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("FastPeriod",          out var fp) && fp.TryGetInt32(out var fv)) fast        = fv;
            if (root.TryGetProperty("SlowPeriod",          out var sp) && sp.TryGetInt32(out var sv)) slow        = sv;
            if (root.TryGetProperty("SignalPeriod",        out var sg) && sg.TryGetInt32(out var gv)) signal      = gv;
            if (root.TryGetProperty("DivergenceLookback",  out var dl) && dl.TryGetInt32(out var dv)) divLookback = dv;
        }
        catch { /* defaults */ }

        fast        = Math.Clamp(fast, 2, 200);
        slow        = Math.Clamp(slow, 2, 500);
        signal      = Math.Clamp(signal, 2, 100);
        divLookback = Math.Clamp(divLookback, 3, 50);
        if (fast >= slow) fast = Math.Max(1, slow - 1);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Cooldown persistence — seeds the in-memory dictionary from the DB
    // on first access per strategy, so cooldown survives restarts.
    // ═════════════════════════════════════════════════════════════════════════

    private async Task SeedCooldownFromDbAsync(long strategyId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory!.CreateScope();
            var readCtx = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            var dbContext = readCtx.GetDbContext();

            var lastSignal = await dbContext.Set<TradeSignal>()
                .Where(s => s.StrategyId == strategyId && !s.IsDeleted)
                .OrderByDescending(s => s.GeneratedAt)
                .Select(s => (DateTime?)s.GeneratedAt)
                .FirstOrDefaultAsync(ct);

            if (lastSignal.HasValue)
            {
                _lastSignalTimestamps.TryAdd(strategyId, lastSignal.Value);
                _logger.LogDebug(
                    "MACD divergence cooldown seeded from DB for strategy {StrategyId}: last signal at {Timestamp}",
                    strategyId, lastSignal.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to seed cooldown from DB for strategy {StrategyId} — falling back to in-memory only",
                strategyId);
            // Remove seeded flag so it retries next time
            _cooldownSeeded.TryRemove(strategyId, out _);
        }
    }
}
