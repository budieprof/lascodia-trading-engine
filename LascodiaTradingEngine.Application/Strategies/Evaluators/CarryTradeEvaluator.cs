using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services.ML;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Carry-style evaluator: captures persistent long-horizon drift in a pair, which in
/// real-world FX tracks the interest-rate differential between base and quote currencies.
///
/// <para>
/// <b>Note on swap data:</b> The engine does not currently ingest true per-pair swap rates
/// from the broker. Rather than block this archetype on that work, the evaluator reuses the
/// existing <see cref="MacroFeatureCalculator.ComputePairCarryProxy"/> — a 90-bar drift
/// divided by rolling volatility and clamped to [-3,3]. Empirically this tracks IRD-driven
/// persistent trends well enough for a first-generation carry strategy. When broker swap
/// rates are later wired into <c>CurrencyPair</c> (or a new <c>SwapRate</c> entity), this
/// evaluator can be extended to weight the proxy by the signed IRD without changing its
/// public contract.
/// </para>
///
/// <para>
/// The strategy is deliberately distinct from <see cref="MomentumTrendEvaluator"/>: it
/// requires a multi-month drift signal (90 bars ≈ 4–5 months on H4 data) before firing,
/// and rejects when drift is within a neutral band. This makes it genuinely orthogonal to
/// short-horizon trend-following rather than a scaled-up duplicate.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class CarryTradeEvaluator : IStrategyEvaluator
{
    private const int    CarryProxyLookback = 91; // MacroFeatureCalculator requires >= 91 closes
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<CarryTradeEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "CarryTrade");

    public CarryTradeEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<CarryTradeEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.CarryTrade;

    public int MinRequiredCandles(Strategy strategy)
        => Math.Max(CarryProxyLookback + _options.AtrPeriodForSlTp, 100);

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParameters(strategy.ParametersJson);

        // ── 1. Sanity ──────────────────────────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        int lastIdx = candles.Count - 1;

        // ── 2. Compute carry proxy (90-bar drift / volatility) ─────────────
        var closes = new double[CarryProxyLookback];
        int start = lastIdx - CarryProxyLookback + 1;
        for (int i = 0; i < CarryProxyLookback; i++)
            closes[i] = (double)candles[start + i].Close;

        double carry = MacroFeatureCalculator.ComputePairCarryProxy(closes);
        if (double.IsNaN(carry))
        {
            LogRejection(strategy, "ProxyNaN", "Carry proxy returned NaN (invalid closes window)");
            return Task.FromResult<TradeSignal?>(null);
        }

        if (Math.Abs(carry) < parameters.MinCarryStrength)
        {
            LogRejection(strategy, "InsufficientCarry",
                $"|carry| {Math.Abs(carry):F3} < threshold {parameters.MinCarryStrength:F3} — drift too weak to fire");
            return Task.FromResult<TradeSignal?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 3. ATR ─────────────────────────────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 4. Direction decision ──────────────────────────────────────────
        // Positive drift = base strengthening vs quote → buy the pair.
        TradeDirection direction = carry > 0 ? TradeDirection.Buy : TradeDirection.Sell;
        decimal entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;

        // ── 5. Spread guard ────────────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.CarryTradeMaxSpreadAtrFraction > 0 && spread > atr * _options.CarryTradeMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.CarryTradeMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 6. SL/TP — carry positions use wider ATR multiples to accommodate the long horizon ─
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier * parameters.HorizonMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier * parameters.HorizonMultiplier;

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

        // ── 7. Confidence scales with carry magnitude ──────────────────────
        decimal carryBoost = Math.Min(
            (decimal)((Math.Abs(carry) - parameters.MinCarryStrength)
                      * (double)_options.CarryTradeConfidenceStrengthScale),
            _options.CarryTradeConfidenceStrengthMax);
        decimal confidence = Math.Clamp(_options.CarryTradeConfidence + carryBoost, 0m, 1m);

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
            ExpiresAt        = now.AddMinutes(_options.CarryTradeExpiryMinutes)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag);
        return Task.FromResult<TradeSignal?>(signal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "CarryTrade signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameters
    // ═════════════════════════════════════════════════════════════════════════

    private static CarryTradeParameters ParseParameters(string? json)
    {
        double  minCarryStrength  = 0.8;   // drift must exceed ±0.8 ATR-normalised units
        decimal horizonMultiplier = 2.0m;  // SL/TP multiples vs baseline

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("MinCarryStrength",  out var mcs) && mcs.TryGetDouble(out var mcsv))  minCarryStrength  = mcsv;
            if (root.TryGetProperty("HorizonMultiplier", out var hm) && hm.TryGetDecimal(out var hmv))    horizonMultiplier = hmv;
        }
        catch (JsonException) { }

        return new CarryTradeParameters(
            Math.Clamp(minCarryStrength, 0.0, 3.0),
            Math.Clamp(horizonMultiplier, 0.5m, 5.0m));
    }

    private static bool IsCandleOrderValid(IReadOnlyList<Candle> candles)
    {
        int check = Math.Min(candles.Count - 1, 3);
        for (int i = candles.Count - check; i < candles.Count; i++)
        {
            if (candles[i].Timestamp <= candles[i - 1].Timestamp)
                return false;
        }
        return true;
    }

    private readonly record struct CarryTradeParameters(
        double MinCarryStrength,
        decimal HorizonMultiplier);
}
