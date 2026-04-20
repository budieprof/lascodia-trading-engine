using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Common.Options;
using LascodiaTradingEngine.Application.Common.Utilities;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Strategies.Evaluators;

/// <summary>
/// Calendar-effect evaluator capturing two well-documented institutional flow anomalies:
///
/// <list type="bullet">
///   <item>
///     <b>MonthEnd</b> — In the last N business days of a calendar month, large asset-manager
///     rebalancing programs generate one-way directional pressure that tends to fade the
///     short-horizon trend (rebalancers sell strength, buy weakness to restore target weights).
///     Fire direction is <i>opposite</i> to recent momentum.
///   </item>
///   <item>
///     <b>LondonNyOverlap</b> — The 13:00-16:00 UTC window where both London and New York are
///     active contains the highest liquidity of the day. Directional moves initiated outside
///     this window often follow-through once overlap liquidity arrives. Fire direction is
///     <i>same</i> as pre-overlap momentum.
///   </item>
/// </list>
///
/// Mode is selected via the strategy parameter JSON; a single <see cref="Strategy"/> row maps
/// to exactly one mode so the generator can tune them independently.
/// </summary>
[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyEvaluator))]
public class CalendarEffectEvaluator : IStrategyEvaluator
{
    private readonly StrategyEvaluatorOptions _options;
    private readonly ILogger<CalendarEffectEvaluator> _logger;
    private readonly TradingMetrics _metrics;

    private static readonly KeyValuePair<string, object?> EvaluatorTag = new("evaluator", "CalendarEffect");

    public CalendarEffectEvaluator(
        StrategyEvaluatorOptions options,
        ILogger<CalendarEffectEvaluator> logger,
        TradingMetrics metrics)
    {
        _options = options;
        _logger  = logger;
        _metrics = metrics;
    }

    public StrategyType StrategyType => StrategyType.CalendarEffect;

    public int MinRequiredCandles(Strategy strategy)
    {
        // Momentum lookback + ATR warmup. Cap at parameter max to avoid runaway params.
        var p = ParseParameters(strategy.ParametersJson);
        return Math.Max(_options.AtrPeriodForSlTp, p.LookbackBars) + 2;
    }

    public Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParameters(strategy.ParametersJson);

        // ── 1. Candle ordering sanity check ────────────────────────────────
        if (!IsCandleOrderValid(candles))
        {
            _logger.LogWarning(
                "Candles for {Symbol} (strategy {StrategyId}) are not in ascending timestamp order — skipping evaluation",
                strategy.Symbol, strategy.Id);
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 2. Minimum candle count ────────────────────────────────────────
        if (candles.Count < MinRequiredCandles(strategy))
            return Task.FromResult<TradeSignal?>(null);

        int lastIdx = candles.Count - 1;
        var lastCandle = candles[lastIdx];

        // ── 3. Window membership (mode-dependent) ──────────────────────────
        if (!IsInWindow(lastCandle.Timestamp, parameters))
            return Task.FromResult<TradeSignal?>(null);

        // ── 4. ATR ─────────────────────────────────────────────────────────
        decimal atr = IndicatorCalculator.WilderAtr(candles, lastIdx, _options.AtrPeriodForSlTp);
        if (atr <= 0)
        {
            LogRejection(strategy, "DegenerateATR", "ATR is zero — degenerate price data");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 5. Momentum over LookbackBars ─────────────────────────────────
        int lookbackStart = lastIdx - parameters.LookbackBars;
        if (lookbackStart < 0)
            return Task.FromResult<TradeSignal?>(null);

        decimal priorClose = candles[lookbackStart].Close;
        decimal momentum = lastCandle.Close - priorClose;
        decimal momentumAtrs = momentum / atr;
        decimal threshold = parameters.MomentumAtrThreshold;

        if (Math.Abs(momentumAtrs) < threshold)
        {
            LogRejection(strategy, "InsufficientMomentum",
                $"|momentum| {Math.Abs(momentumAtrs):F2} ATR < threshold {threshold:F2} ATR");
            return Task.FromResult<TradeSignal?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ── 6. Direction decision (mode-dependent) ─────────────────────────
        TradeDirection direction;
        switch (parameters.Mode)
        {
            case CalendarEffectMode.MonthEnd:
                // Fade: momentum up → sell; momentum down → buy.
                direction = momentumAtrs > 0 ? TradeDirection.Sell : TradeDirection.Buy;
                break;
            case CalendarEffectMode.LondonNyOverlap:
                // Continuation: momentum up → buy; momentum down → sell.
                direction = momentumAtrs > 0 ? TradeDirection.Buy : TradeDirection.Sell;
                break;
            default:
                LogRejection(strategy, "UnknownMode", $"Unhandled CalendarEffectMode {parameters.Mode}");
                return Task.FromResult<TradeSignal?>(null);
        }

        decimal entryPrice = direction == TradeDirection.Buy ? currentPrice.Ask : currentPrice.Bid;

        // ── 7. Spread safety guard ────────────────────────────────────────
        decimal spread = currentPrice.Ask - currentPrice.Bid;
        if (_options.CalendarEffectMaxSpreadAtrFraction > 0 && spread > atr * _options.CalendarEffectMaxSpreadAtrFraction)
        {
            LogRejection(strategy, "Spread",
                $"Spread {spread:F6} > {_options.CalendarEffectMaxSpreadAtrFraction:P0} of ATR ({atr:F6})");
            return Task.FromResult<TradeSignal?>(null);
        }

        // ── 8. ATR-based SL/TP ────────────────────────────────────────────
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

        // ── 9. Confidence scoring: base + momentum-magnitude boost ─────────
        decimal momentumBoost = Math.Min(
            (Math.Abs(momentumAtrs) - threshold) * _options.CalendarEffectConfidenceMomentumBoostScale,
            _options.CalendarEffectConfidenceMomentumBoostMax);
        decimal confidence = Math.Clamp(_options.CalendarEffectConfidence + momentumBoost, 0m, 1m);

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
            ExpiresAt        = now.AddMinutes(_options.CalendarEffectExpiryMinutes)
        };

        _metrics.SignalsGenerated.Add(1, EvaluatorTag, new("mode", parameters.Mode.ToString()));
        return Task.FromResult<TradeSignal?>(signal);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Window membership
    // ═════════════════════════════════════════════════════════════════════════

    private static bool IsInWindow(DateTime timestampUtc, CalendarEffectParameters p)
    {
        return p.Mode switch
        {
            CalendarEffectMode.MonthEnd         => IsWithinLastBusinessDays(timestampUtc, p.MonthEndBusinessDays),
            CalendarEffectMode.LondonNyOverlap  => IsWithinOverlap(timestampUtc, p.OverlapStartHourUtc, p.OverlapEndHourUtc),
            _                                   => false,
        };
    }

    /// <summary>
    /// True when the candle timestamp falls in the last <paramref name="businessDays"/>
    /// business days (Mon-Fri) of its calendar month. Treats Sat/Sun as non-business.
    /// </summary>
    private static bool IsWithinLastBusinessDays(DateTime timestampUtc, int businessDays)
    {
        if (businessDays <= 0) return false;

        int year = timestampUtc.Year;
        int month = timestampUtc.Month;
        int lastDay = DateTime.DaysInMonth(year, month);

        // Walk backwards from lastDay, collecting business-day day numbers.
        int collected = 0;
        int earliestBusinessDay = lastDay + 1; // sentinel above range
        for (int d = lastDay; d >= 1 && collected < businessDays; d--)
        {
            var dow = new DateTime(year, month, d).DayOfWeek;
            if (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday) continue;
            earliestBusinessDay = d;
            collected++;
        }

        return timestampUtc.Day >= earliestBusinessDay
            && timestampUtc.DayOfWeek != DayOfWeek.Saturday
            && timestampUtc.DayOfWeek != DayOfWeek.Sunday;
    }

    private static bool IsWithinOverlap(DateTime timestampUtc, int startHourUtc, int endHourUtc)
    {
        int hour = timestampUtc.Hour;
        // Handle wrap-around (e.g. 22 → 6)
        return startHourUtc <= endHourUtc
            ? hour >= startHourUtc && hour < endHourUtc
            : hour >= startHourUtc || hour < endHourUtc;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void LogRejection(Strategy strategy, string filter, string detail)
    {
        _metrics.EvaluatorRejections.Add(1, EvaluatorTag, new("filter", filter));
        if (!_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "CalendarEffect signal rejected for {Symbol} (strategy {StrategyId}) by {Filter}: {Detail}",
            strategy.Symbol, strategy.Id, filter, detail);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parameters
    // ═════════════════════════════════════════════════════════════════════════

    private static CalendarEffectParameters ParseParameters(string? json)
    {
        CalendarEffectMode mode = CalendarEffectMode.MonthEnd;
        int     lookbackBars            = 5;
        decimal momentumAtrThreshold    = 1.0m;
        int     monthEndBusinessDays    = 3;
        int     overlapStartHourUtc     = 13;
        int     overlapEndHourUtc       = 16;

        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;

            if (root.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<CalendarEffectMode>(m.GetString(), ignoreCase: true, out var parsedMode))
                    mode = parsedMode;
            }
            // TPE grids cannot carry strings, so accept an integer ModeId alias. Valid values:
            // 0 = MonthEnd, 1 = LondonNyOverlap. Explicit Mode string wins if both are present.
            else if (root.TryGetProperty("ModeId", out var mi) && mi.TryGetInt32(out var miv))
            {
                mode = miv switch
                {
                    0 => CalendarEffectMode.MonthEnd,
                    1 => CalendarEffectMode.LondonNyOverlap,
                    _ => mode,
                };
            }
            if (root.TryGetProperty("LookbackBars", out var lb) && lb.TryGetInt32(out var lbv))                          lookbackBars         = lbv;
            if (root.TryGetProperty("MomentumAtrThreshold", out var mt) && mt.TryGetDecimal(out var mtv))                momentumAtrThreshold = mtv;
            if (root.TryGetProperty("MonthEndBusinessDays", out var me) && me.TryGetInt32(out var mev))                  monthEndBusinessDays = mev;
            if (root.TryGetProperty("OverlapStartHourUtc", out var os) && os.TryGetInt32(out var osv))                   overlapStartHourUtc  = osv;
            if (root.TryGetProperty("OverlapEndHourUtc", out var oe) && oe.TryGetInt32(out var oev))                     overlapEndHourUtc    = oev;
        }
        catch (JsonException) { }

        return new CalendarEffectParameters(
            mode,
            Math.Clamp(lookbackBars, 1, 120),
            Math.Clamp(momentumAtrThreshold, 0m, 10m),
            Math.Clamp(monthEndBusinessDays, 1, 10),
            Math.Clamp(overlapStartHourUtc, 0, 23),
            Math.Clamp(overlapEndHourUtc, 0, 24));
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

    public enum CalendarEffectMode
    {
        MonthEnd        = 0,
        LondonNyOverlap = 1,
    }

    private readonly record struct CalendarEffectParameters(
        CalendarEffectMode Mode,
        int LookbackBars,
        decimal MomentumAtrThreshold,
        int MonthEndBusinessDays,
        int OverlapStartHourUtc,
        int OverlapEndHourUtc);
}
