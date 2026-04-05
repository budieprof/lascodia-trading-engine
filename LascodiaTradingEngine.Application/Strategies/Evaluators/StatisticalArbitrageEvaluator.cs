using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Statistical arbitrage strategy that trades mean-reversion of the spread between
/// a primary symbol and a correlated symbol. The spread is computed via OLS hedge
/// ratio, and entries are triggered when the z-score breaches configurable thresholds.
///
/// Filter pipeline (in order):
/// 1. Minimum candle count
/// 2. Correlated pair candle retrieval
/// 3. OLS hedge ratio computation
/// 4. Spread z-score calculation
/// 5. Z-score entry threshold gate
/// 6. ATR calculation + zero guard
/// 7. ATR-based SL/TP
/// 8. Dynamic confidence scoring
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class StatisticalArbitrageEvaluator : IStrategyEvaluator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<StatisticalArbitrageEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "StatisticalArbitrage");

    public StatisticalArbitrageEvaluator(
        IServiceScopeFactory scopeFactory,
        StrategyEvaluatorOptions options,
        ILogger<StatisticalArbitrageEvaluator> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _options      = options;
        _logger       = logger;
        _metrics      = metrics;
    }

    public StrategyType StrategyType => StrategyType.StatisticalArbitrage;

    public int MinRequiredCandles(Strategy strategy)
    {
        var p = ParseParameters(strategy.ParametersJson);
        return p.LookbackPeriod + p.AtrPeriod + 1;
    }

    public async Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var p = ParseParameters(strategy.ParametersJson);

        // ── 1. Minimum candle count ────────────────────────────────────────
        if (candles.Count < MinRequiredCandles(strategy))
            return null;

        int lastIdx = candles.Count - 1;

        // ── 2. Correlated pair candle retrieval ────────────────────────────
        IReadOnlyList<Candle> correlatedCandles;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<ICrossPairCandleProvider>();
            var crossPairs = await provider.GetCrossPairCandlesAsync(
                strategy.Symbol, strategy.Timeframe, candles[lastIdx].Timestamp, p.LookbackPeriod, cancellationToken);

            if (!crossPairs.TryGetValue(p.CorrelatedSymbol, out var correlated) || correlated.Count == 0)
            {
                LogRejection(strategy, "NoCrossPair",
                    $"Correlated symbol {p.CorrelatedSymbol} not found or empty");
                return null;
            }

            correlatedCandles = correlated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve correlated candles for {Symbol} (strategy {StrategyId})",
                strategy.Symbol, strategy.Id);
            return null;
        }

        // ── 3. Match candle counts and extract close arrays ────────────────
        int matchCount = Math.Min(
            Math.Min(p.LookbackPeriod, candles.Count),
            correlatedCandles.Count);

        if (matchCount < 10)
        {
            LogRejection(strategy, "InsufficientData",
                $"Matched candle count {matchCount} < 10");
            return null;
        }

        // Verify temporal alignment: last candle of each series should be within 1 bar duration
        var primaryLast = candles[candles.Count - 1].Timestamp;
        var correlatedLast = correlatedCandles[correlatedCandles.Count - 1].Timestamp;
        var maxGap = strategy.Timeframe switch
        {
            Timeframe.M1  => TimeSpan.FromMinutes(2),
            Timeframe.M5  => TimeSpan.FromMinutes(10),
            Timeframe.M15 => TimeSpan.FromMinutes(30),
            Timeframe.H1  => TimeSpan.FromHours(2),
            Timeframe.H4  => TimeSpan.FromHours(8),
            Timeframe.D1  => TimeSpan.FromDays(2),
            _             => TimeSpan.FromHours(2),
        };
        if (Math.Abs((primaryLast - correlatedLast).Ticks) > maxGap.Ticks)
        {
            LogRejection(strategy, "TimestampMisalignment",
                $"Candle timestamp misalignment {primaryLast} vs {correlatedLast}");
            return null;
        }

        var primaryCloses = new decimal[matchCount];
        var correlatedCloses = new decimal[matchCount];
        for (int i = 0; i < matchCount; i++)
        {
            primaryCloses[i] = candles[candles.Count - matchCount + i].Close;
            correlatedCloses[i] = correlatedCandles[correlatedCandles.Count - matchCount + i].Close;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 4. OLS hedge ratio ─────────────────────────────────────────────
        var (alpha, beta) = IndicatorCalculator.OlsHedgeRatio(primaryCloses, correlatedCloses);
        if (beta == 0m)
        {
            LogRejection(strategy, "DegenerateOLS", "Beta is zero — no cointegration detected");
            return null;
        }

        // ── 5. Spread z-score ──────────────────────────────────────────────
        var spread = new decimal[matchCount];
        decimal sumSpread = 0m;
        for (int i = 0; i < matchCount; i++)
        {
            spread[i] = primaryCloses[i] - beta * correlatedCloses[i];
            sumSpread += spread[i];
        }

        decimal mean = sumSpread / matchCount;

        decimal sumSqDev = 0m;
        for (int i = 0; i < matchCount; i++)
        {
            decimal dev = spread[i] - mean;
            sumSqDev += dev * dev;
        }

        decimal stdDev = (decimal)Math.Sqrt((double)(sumSqDev / matchCount));
        if (stdDev < 0.0000001m)
        {
            LogRejection(strategy, "DegenerateSpread", "Spread standard deviation is near zero");
            return null;
        }

        decimal zScore = (spread[matchCount - 1] - mean) / stdDev;

        // ── 6. Z-score entry threshold gate ────────────────────────────────
        if (Math.Abs(zScore) < p.ZScoreEntry)
            return null;

        // ── 7. ATR calculation + zero guard ────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, p.AtrPeriod);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return null;
        }

        // ── 8. Direction, entry, SL/TP ─────────────────────────────────────
        // z > entry → spread is above mean → primary overvalued → sell primary
        // z < -entry → spread is below mean → primary undervalued → buy primary
        TradeDirection direction = zScore > p.ZScoreEntry
            ? TradeDirection.Sell
            : TradeDirection.Buy;

        decimal entryPrice = direction == TradeDirection.Buy
            ? currentPrice.Ask
            : currentPrice.Bid;

        decimal stopDistance = atr * p.StopLossAtrMultiplier;
        decimal profitDistance = atr * p.TakeProfitAtrMultiplier;

        decimal stopLoss, takeProfit;
        if (direction == TradeDirection.Buy)
        {
            stopLoss = entryPrice - stopDistance;
            takeProfit = entryPrice + profitDistance;
        }
        else
        {
            stopLoss = entryPrice + stopDistance;
            takeProfit = entryPrice - profitDistance;
        }

        // ── 9. Dynamic confidence scoring ──────────────────────────────────
        decimal confidence = Math.Clamp(
            0.5m * Math.Abs(zScore) / p.ZScoreEntry,
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
            ExpiresAt        = now.AddMinutes(30)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag);

        return signal;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Rejection diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));

        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "StatisticalArbitrage signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameter parsing
    // ═════════════════════════════════════════════════════════════════════════

    private static StatArbParams ParseParameters(string? json)
    {
        string  correlatedSymbol       = "GBPUSD";
        int     lookbackPeriod         = 60;
        decimal zScoreEntry            = 2.0m;
        decimal zScoreExit             = 0.5m;
        decimal stopLossAtrMultiplier  = 2.0m;
        decimal takeProfitAtrMultiplier = 3.0m;
        int     atrPeriod              = 14;

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("CorrelatedSymbol",        out var cs) && cs.GetString() is { } csv)     correlatedSymbol       = csv;
            if (root.TryGetProperty("LookbackPeriod",          out var lp) && lp.TryGetInt32(out var lpv))   lookbackPeriod         = lpv;
            if (root.TryGetProperty("ZScoreEntry",             out var ze) && ze.TryGetDecimal(out var zev))  zScoreEntry            = zev;
            if (root.TryGetProperty("ZScoreExit",              out var zx) && zx.TryGetDecimal(out var zxv))  zScoreExit             = zxv;
            if (root.TryGetProperty("StopLossAtrMultiplier",   out var sl) && sl.TryGetDecimal(out var slv))  stopLossAtrMultiplier  = slv;
            if (root.TryGetProperty("TakeProfitAtrMultiplier", out var tp) && tp.TryGetDecimal(out var tpv))  takeProfitAtrMultiplier = tpv;
            if (root.TryGetProperty("AtrPeriod",               out var ap) && ap.TryGetInt32(out var apv))   atrPeriod              = apv;
        }
        catch (JsonException) { }

        return new StatArbParams(
            correlatedSymbol,
            Math.Clamp(lookbackPeriod, 10, 500),
            Math.Clamp(zScoreEntry, 0.5m, 5.0m),
            Math.Clamp(zScoreExit, 0.0m, zScoreEntry),
            Math.Clamp(stopLossAtrMultiplier, 0.5m, 10m),
            Math.Clamp(takeProfitAtrMultiplier, 0.5m, 10m),
            Math.Clamp(atrPeriod, 5, 50));
    }

    private sealed record StatArbParams(
        string CorrelatedSymbol,
        int LookbackPeriod,
        decimal ZScoreEntry,
        decimal ZScoreExit,
        decimal StopLossAtrMultiplier,
        decimal TakeProfitAtrMultiplier,
        int AtrPeriod);
}
