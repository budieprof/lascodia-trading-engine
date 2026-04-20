using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
/// News-fade strategy: captures the well-documented post-release mean-reversion after
/// high-impact macro events. When a scheduled <see cref="EconomicEvent"/> with
/// <c>Impact = High</c> lands inside the current candle period (or within a configurable
/// window around it) for the symbol's base or quote currency, and the candle exhibits a
/// large intracandle move (close vs open, measured in ATRs), the evaluator takes the
/// <i>opposite</i> direction to the move.
///
/// <para>
/// The logic assumes that minute-grain news prints overshoot within the first 1-5 minutes
/// and partially retrace within ~15-30 minutes. On H4/D1 candles this manifests as a close
/// that has pulled back from the candle's extremes — the fade is implicit in the candle
/// body. The evaluator prefers lower-timeframe strategies (M5/M15) but works on any TF
/// because it relies on candle body × ATR magnitude, not on absolute minute timing.
/// </para>
///
/// <para>
/// DB access follows the CompositeML pattern: inject <see cref="IServiceScopeFactory"/>
/// and open a fresh scope per evaluation to obtain <see cref="IReadApplicationDbContext"/>.
/// Keeps the evaluator registered as Singleton without holding an EF context long-term.
/// </para>
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class NewsFadeEvaluator : IStrategyEvaluator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<NewsFadeEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "NewsFade");

    public NewsFadeEvaluator(
        IServiceScopeFactory scopeFactory,
        StrategyEvaluatorOptions options,
        ILogger<NewsFadeEvaluator> logger,
        TradingMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _options      = options;
        _logger       = logger;
        _metrics      = metrics;
    }

    public StrategyType StrategyType => StrategyType.NewsFade;

    public int MinRequiredCandles(Strategy strategy) => Math.Max(_options.AtrPeriodForSlTp + 2, 20);

    public async Task<TradeSignal?> EvaluateAsync(
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
            return null;
        }

        if (candles.Count < MinRequiredCandles(strategy))
            return null;

        int lastIdx = candles.Count - 1;
        var lastCandle = candles[lastIdx];

        // ── 2. Event lookup ────────────────────────────────────────────────
        var currencies = ExtractCurrencies(strategy.Symbol);
        if (currencies.Count == 0)
            return null;

        var windowStart = lastCandle.Timestamp.AddMinutes(-parameters.MaxMinutesSinceEvent);
        var windowEnd   = lastCandle.Timestamp.AddMinutes(parameters.MaxMinutesSinceEvent);

        EconomicEvent? recentEvent;
        using (var scope = _scopeFactory.CreateScope())
        {
            var readDb = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();
            recentEvent = await readDb.GetDbContext()
                .Set<EconomicEvent>()
                .AsNoTracking()
                .Where(e => !e.IsDeleted
                         && e.Impact == EconomicImpact.High
                         && currencies.Contains(e.Currency)
                         && e.ScheduledAt >= windowStart
                         && e.ScheduledAt <= windowEnd)
                .OrderByDescending(e => e.ScheduledAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (recentEvent is null)
            return null;

        // ── 3. Age gate ────────────────────────────────────────────────────
        // Require the event has been public for at least MinMinutesSinceEvent — avoids
        // trading into the spike itself, which is the part that tends to overshoot.
        var minutesSinceEvent = (lastCandle.Timestamp - recentEvent.ScheduledAt).TotalMinutes;
        if (minutesSinceEvent < parameters.MinMinutesSinceEvent)
        {
            LogRejection(strategy, "TooSoon",
                $"Only {minutesSinceEvent:F1} min since {recentEvent.Title} — awaiting overshoot decay");
            return null;
        }

        if (parameters.FadeOnlyPositiveAge && minutesSinceEvent < 0)
        {
            LogRejection(strategy, "FutureEvent",
                $"Event {recentEvent.Title} scheduled {-minutesSinceEvent:F1} min ahead");
            return null;
        }

        // ── 4. ATR ─────────────────────────────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return null;
        }

        // ── 5. Intracandle move ────────────────────────────────────────────
        decimal move = lastCandle.Close - lastCandle.Open;
        decimal moveAtrs = move / atr;

        if (Math.Abs(moveAtrs) < parameters.MomentumAtrThreshold)
        {
            LogRejection(strategy, "InsufficientMove",
                $"|candle body| {Math.Abs(moveAtrs):F2} ATR < threshold {parameters.MomentumAtrThreshold:F2} ATR — no overshoot to fade");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 6. Fade direction ──────────────────────────────────────────────
        TradeDirection direction = moveAtrs > 0 ? TradeDirection.Sell : TradeDirection.Buy;
        decimal entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;

        // ── 7. Spread guard ────────────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.NewsFadeMaxSpreadAtrFraction > 0 && spread > atr * _options.NewsFadeMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.NewsFadeMaxSpreadAtrFraction:P0} of ATR ({atr:F6}) — post-news widening makes entry uneconomic");
            return null;
        }

        // ── 8. SL/TP ───────────────────────────────────────────────────────
        decimal stopDistance   = atr * _options.StopLossAtrMultiplier;
        decimal profitDistance = atr * _options.TakeProfitAtrMultiplier;

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

        // ── 9. Confidence ──────────────────────────────────────────────────
        decimal overshootBoost = Math.Min(
            (Math.Abs(moveAtrs) - parameters.MomentumAtrThreshold) * _options.NewsFadeConfidenceOvershootScale,
            _options.NewsFadeConfidenceOvershootMax);
        decimal confidence = Math.Clamp(_options.NewsFadeConfidence + overshootBoost, 0m, 1m);

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
            ExpiresAt        = now.AddMinutes(_options.NewsFadeExpiryMinutes)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag,
            new("impact", recentEvent.Impact.ToString()),
            new("currency", recentEvent.Currency));
        return signal;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Currency extraction (mirrors NewsFilter.ExtractCurrencies; keep them in sync)
    // ═════════════════════════════════════════════════════════════════════════

    internal static List<string> ExtractCurrencies(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return [];

        var upper = symbol.ToUpperInvariant();
        if (upper.Length < 6)
            return upper.Length == 3 ? [upper] : [];

        if (upper.All(char.IsLetterOrDigit))
            return [upper[..3], upper[3..6]];

        var list = new List<string>();
        if (upper.Length >= 3) list.Add(upper[..3]);
        if (upper.Length >= 6) list.Add(upper[^3..]);
        return list.Count > 0 ? list : [upper];
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "NewsFade signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameters
    // ═════════════════════════════════════════════════════════════════════════

    private static NewsFadeParameters ParseParameters(string? json)
    {
        int     minMinutesSinceEvent = 3;
        int     maxMinutesSinceEvent = 20;
        decimal momentumAtrThreshold = 0.8m;
        bool    fadeOnlyPositiveAge  = true;

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("MinMinutesSinceEvent", out var mn) && mn.TryGetInt32(out var mnv))   minMinutesSinceEvent = mnv;
            if (root.TryGetProperty("MaxMinutesSinceEvent", out var mx) && mx.TryGetInt32(out var mxv))   maxMinutesSinceEvent = mxv;
            if (root.TryGetProperty("MomentumAtrThreshold", out var mt) && mt.TryGetDecimal(out var mtv)) momentumAtrThreshold = mtv;
            if (root.TryGetProperty("FadeOnlyPositiveAge", out var fa) && fa.ValueKind == JsonValueKind.False) fadeOnlyPositiveAge = false;
        }
        catch (JsonException) { }

        return new NewsFadeParameters(
            Math.Clamp(minMinutesSinceEvent, 0, 120),
            Math.Clamp(maxMinutesSinceEvent, 1, 240),
            Math.Clamp(momentumAtrThreshold, 0m, 5m),
            fadeOnlyPositiveAge);
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

    private readonly record struct NewsFadeParameters(
        int MinMinutesSinceEvent,
        int MaxMinutesSinceEvent,
        decimal MomentumAtrThreshold,
        bool FadeOnlyPositiveAge);
}
