using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services.ML;

/// <summary>
/// Snapshot of macro / positioning / microstructure context for a single symbol
/// at the moment of signal evaluation. All fields are normalized to a bounded range
/// so they compose safely into the downstream confidence modulator.
///
/// The values are derived entirely from data the engine already collects — no new
/// ingestion pipelines are introduced. Sources:
/// <list type="bullet">
///   <item><see cref="MarketRegimeSnapshot"/> for regime code and confidence</item>
///   <item><see cref="SpreadProfile"/> for current-spread z-score vs hour/day baseline</item>
///   <item><see cref="COTReport"/> for non-commercial positioning net-long ratio and week-over-week delta</item>
///   <item><see cref="EconomicEvent"/> for impact-weighted recent surprise composite</item>
///   <item><see cref="TickRecord"/> for tick-pressure gradient (rate-of-change of bid/ask imbalance)</item>
/// </list>
/// </summary>
public readonly record struct MacroContext(
    /// <summary>Regime encoded to [0..1]: Crisis=0.0, LowVol=0.2, Ranging=0.4, Trending=0.6, HighVol=0.8, Breakout=1.0. NaN when unknown.</summary>
    double RegimeCode,
    /// <summary>Regime classifier confidence in [0..1]. NaN when no snapshot exists for the symbol.</summary>
    double RegimeConfidence,
    /// <summary>
    /// Current spread minus the SpreadProfile hour-of-day mean, expressed as a z-score.
    /// Clamped to [-3, 3]. Positive values indicate wider-than-normal spreads (microstructure stress);
    /// negative values indicate tighter-than-normal (low friction). NaN when no profile exists.
    /// </summary>
    double SpreadZScore,
    /// <summary>Base currency's non-commercial net long ratio in [0..1]. 0.5 = flat, &gt;0.5 = net long.</summary>
    double CotNetLongRatioBase,
    /// <summary>Quote currency's non-commercial net long ratio in [0..1].</summary>
    double CotNetLongRatioQuote,
    /// <summary>
    /// Week-over-week change in the pair-level COT spread (base ratio − quote ratio).
    /// Positive = pair positioning is shifting bullish. Clamped to [-1, 1].
    /// </summary>
    double CotPairWeekDelta,
    /// <summary>
    /// Impact-weighted sum of recent economic surprises touching base or quote currency,
    /// scaled to [-1, 1]. Weights: High impact = 1.0, Medium = 0.5, Low = 0.2.
    /// </summary>
    double EconomicImpactWeighted,
    /// <summary>
    /// Tick-delta acceleration (how fast bid/ask imbalance is changing) in [-1, 1].
    /// Large positive values mean aggressive buying building; negative means selling.
    /// </summary>
    double TickPressureGradient)
{
    public static MacroContext Empty => new(
        RegimeCode: double.NaN,
        RegimeConfidence: double.NaN,
        SpreadZScore: double.NaN,
        CotNetLongRatioBase: 0.5,
        CotNetLongRatioQuote: 0.5,
        CotPairWeekDelta: 0.0,
        EconomicImpactWeighted: 0.0,
        TickPressureGradient: 0.0);

    /// <summary>
    /// Returns a signed macro-alignment score in [-1, 1] for a proposed trade direction.
    /// Aggregates regime confidence, positioning deltas, and economic momentum into a
    /// single scalar that downstream code can use to modulate signal confidence.
    /// Returns 0 when insufficient data is available (i.e. context is mostly empty).
    /// </summary>
    public double AlignmentFor(bool isBuy)
    {
        double sign = isBuy ? 1.0 : -1.0;
        double score = 0.0;
        int usedComponents = 0;

        // COT pair delta — strongest positioning signal for multi-day holds.
        if (!double.IsNaN(CotPairWeekDelta) && Math.Abs(CotPairWeekDelta) > 1e-4)
        {
            score += 0.35 * sign * Math.Clamp(CotPairWeekDelta, -1.0, 1.0);
            usedComponents++;
        }

        // Economic surprise — direct macro push.
        if (!double.IsNaN(EconomicImpactWeighted) && Math.Abs(EconomicImpactWeighted) > 1e-4)
        {
            score += 0.30 * sign * Math.Clamp(EconomicImpactWeighted, -1.0, 1.0);
            usedComponents++;
        }

        // Tick pressure gradient — microstructure momentum on the tick.
        if (!double.IsNaN(TickPressureGradient) && Math.Abs(TickPressureGradient) > 1e-4)
        {
            score += 0.20 * sign * Math.Clamp(TickPressureGradient, -1.0, 1.0);
            usedComponents++;
        }

        // Regime confidence — higher confidence amplifies the other components rather
        // than being a direction signal of its own.
        if (!double.IsNaN(RegimeConfidence))
        {
            score *= 0.5 + 0.5 * Math.Clamp(RegimeConfidence, 0.0, 1.0);
        }

        // Spread z-score — wide spreads dampen the score (execution cost penalty).
        if (!double.IsNaN(SpreadZScore) && SpreadZScore > 1.0)
        {
            score *= Math.Max(0.5, 1.0 - 0.15 * (SpreadZScore - 1.0));
        }

        if (usedComponents == 0) return 0.0;
        return Math.Clamp(score, -1.0, 1.0);
    }
}

public interface IMacroFeatureProvider
{
    /// <summary>Builds a <see cref="MacroContext"/> for a symbol at the given timestamp.</summary>
    Task<MacroContext> GetAsync(string symbol, DateTime asOfUtc, CancellationToken ct);
}

[RegisterService(ServiceLifetime.Scoped, typeof(IMacroFeatureProvider))]
internal sealed class MacroFeatureProvider : IMacroFeatureProvider
{
    private const double NetLongRatioCenter = 0.5;

    private readonly IReadApplicationDbContext _readDb;
    private readonly ILogger<MacroFeatureProvider> _logger;

    public MacroFeatureProvider(
        IReadApplicationDbContext readDb,
        ILogger<MacroFeatureProvider> logger)
    {
        _readDb = readDb;
        _logger = logger;
    }

    public async Task<MacroContext> GetAsync(string symbol, DateTime asOfUtc, CancellationToken ct)
    {
        try
        {
            var db = _readDb.GetDbContext();

            // ── Regime snapshot (D1 as dominant timeframe) ────────────────────────
            var regimeSnapshot = await db.Set<MarketRegimeSnapshot>()
                .AsNoTracking()
                .Where(r => r.Symbol == symbol && r.Timeframe == Timeframe.D1 && !r.IsDeleted)
                .OrderByDescending(r => r.DetectedAt)
                .Select(r => new { r.Regime, r.Confidence })
                .FirstOrDefaultAsync(ct);

            // ── Spread z-score (hour-of-day baseline) ─────────────────────────────
            int hour = asOfUtc.Hour;
            var spreadProfile = await db.Set<SpreadProfile>()
                .AsNoTracking()
                .Where(p => p.Symbol == symbol && p.HourUtc == hour && !p.IsDeleted)
                .OrderByDescending(p => p.ComputedAt)
                .Select(p => new { p.SpreadP50, p.SpreadP95, p.SpreadMean })
                .FirstOrDefaultAsync(ct);

            var livePrice = await db.Set<LivePrice>()
                .AsNoTracking()
                .Where(p => p.Symbol == symbol)
                .Select(p => new { p.Bid, p.Ask })
                .FirstOrDefaultAsync(ct);

            double spreadZ = double.NaN;
            if (spreadProfile is not null && livePrice is not null && spreadProfile.SpreadP95 > spreadProfile.SpreadMean)
            {
                var currentSpread = (double)(livePrice.Ask - livePrice.Bid);
                var mean = (double)spreadProfile.SpreadMean;
                // Use (p95-p50)/1.645 as a rough stddev proxy since the table doesn't store stddev directly.
                var stdProxy = Math.Max(1e-9, (double)(spreadProfile.SpreadP95 - spreadProfile.SpreadP50) / 1.645);
                spreadZ = Math.Clamp((currentSpread - mean) / stdProxy, -3.0, 3.0);
            }

            // ── COT positioning (base + quote currencies) ─────────────────────────
            string baseCcy = symbol.Length >= 3 ? symbol[..3] : symbol;
            string quoteCcy = symbol.Length >= 6 ? symbol[3..6] : string.Empty;

            var cotLookback = asOfUtc.AddDays(-21);
            var cotRows = await db.Set<COTReport>()
                .AsNoTracking()
                .Where(c => !c.IsDeleted && c.ReportDate >= cotLookback
                         && (c.Currency == baseCcy || c.Currency == quoteCcy))
                .OrderByDescending(c => c.ReportDate)
                .ToListAsync(ct);

            var baseCotCurrent = cotRows.FirstOrDefault(c => c.Currency == baseCcy);
            var baseCotPrior = cotRows.FirstOrDefault(c =>
                c.Currency == baseCcy && baseCotCurrent != null && c.ReportDate < baseCotCurrent.ReportDate);
            var quoteCotCurrent = cotRows.FirstOrDefault(c => c.Currency == quoteCcy);
            var quoteCotPrior = cotRows.FirstOrDefault(c =>
                c.Currency == quoteCcy && quoteCotCurrent != null && c.ReportDate < quoteCotCurrent.ReportDate);

            double baseRatio = ComputeNetLongRatio(baseCotCurrent);
            double quoteRatio = ComputeNetLongRatio(quoteCotCurrent);
            double basePriorRatio = ComputeNetLongRatio(baseCotPrior);
            double quotePriorRatio = ComputeNetLongRatio(quoteCotPrior);

            double currentPairDelta = baseRatio - quoteRatio;           // > 0 means base-long
            double priorPairDelta = basePriorRatio - quotePriorRatio;
            double cotWeekDelta = Math.Clamp(currentPairDelta - priorPairDelta, -1.0, 1.0);

            // ── Economic impact-weighted composite (last 24h surprise) ────────────
            var eventCutoff = asOfUtc.AddHours(-24);
            var recentEvents = await db.Set<EconomicEvent>()
                .AsNoTracking()
                .Where(e => !e.IsDeleted
                         && e.ScheduledAt >= eventCutoff && e.ScheduledAt <= asOfUtc
                         && e.Actual != null
                         && (e.Currency == baseCcy || e.Currency == quoteCcy))
                .OrderByDescending(e => e.ScheduledAt)
                .Take(20)
                .Select(e => new { e.Currency, e.Impact, e.Actual, e.Forecast, e.Previous, e.ScheduledAt })
                .ToListAsync(ct);

            double economicComposite = 0.0;
            foreach (var ev in recentEvents)
            {
                double impactWeight = ev.Impact switch
                {
                    EconomicImpact.High => 1.0,
                    EconomicImpact.Medium => 0.5,
                    EconomicImpact.Low => 0.2,
                    _ => 0.0
                };
                if (impactWeight == 0.0) continue;

                double? actual = MLModels.Shared.MLFeatureHelper.ParseEconomicValue(ev.Actual) is { } a ? (double)a : null;
                double? forecast = MLModels.Shared.MLFeatureHelper.ParseEconomicValue(ev.Forecast) is { } f ? (double)f : null;
                double? previous = MLModels.Shared.MLFeatureHelper.ParseEconomicValue(ev.Previous) is { } p ? (double)p : null;
                if (actual is null) continue;

                double baseline = forecast ?? previous ?? 0.0;
                double scale = Math.Max(1e-9, Math.Abs(baseline));
                double normalizedSurprise = Math.Clamp((actual.Value - baseline) / scale, -1.0, 1.0);

                // Sign convention: positive surprise for base currency is bullish for the
                // pair (base/quote); positive for quote currency is bearish for the pair.
                double pairSign = ev.Currency == baseCcy ? 1.0 : -1.0;

                // Time-decay: more recent events weigh more. 24h half-life.
                double hoursAgo = (asOfUtc - ev.ScheduledAt).TotalHours;
                double timeDecay = Math.Exp(-hoursAgo / 24.0);

                economicComposite += normalizedSurprise * impactWeight * timeDecay * pairSign;
            }
            economicComposite = Math.Clamp(economicComposite, -1.0, 1.0);

            // ── Tick pressure gradient (recent bid/ask imbalance acceleration) ────
            var tickCutoff = asOfUtc.AddMinutes(-10);
            var recentTicks = await db.Set<TickRecord>()
                .AsNoTracking()
                .Where(t => t.Symbol == symbol && !t.IsDeleted && t.TickTimestamp >= tickCutoff)
                .OrderByDescending(t => t.TickTimestamp)
                .Take(200)
                .Select(t => new { t.Bid, t.Ask, t.Mid, t.TickTimestamp })
                .ToListAsync(ct);

            double tickGradient = 0.0;
            if (recentTicks.Count >= 20)
            {
                int half = recentTicks.Count / 2;
                double recentImbalance = recentTicks.Take(half)
                    .Select(t => ComputeImbalance(t.Bid, t.Ask, t.Mid))
                    .Average();
                double priorImbalance = recentTicks.Skip(half)
                    .Select(t => ComputeImbalance(t.Bid, t.Ask, t.Mid))
                    .Average();
                tickGradient = Math.Clamp(recentImbalance - priorImbalance, -1.0, 1.0);
            }

            return new MacroContext(
                RegimeCode: regimeSnapshot is not null ? EncodeRegime(regimeSnapshot.Regime) : double.NaN,
                RegimeConfidence: regimeSnapshot is not null ? (double)regimeSnapshot.Confidence : double.NaN,
                SpreadZScore: spreadZ,
                CotNetLongRatioBase: baseRatio,
                CotNetLongRatioQuote: quoteRatio,
                CotPairWeekDelta: cotWeekDelta,
                EconomicImpactWeighted: economicComposite,
                TickPressureGradient: tickGradient);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MacroFeatureProvider: failed to build context for {Symbol}", symbol);
            return MacroContext.Empty;
        }
    }

    private static double ComputeNetLongRatio(COTReport? report)
    {
        if (report is null) return NetLongRatioCenter;
        double longs = report.NonCommercialLong;
        double shorts = report.NonCommercialShort;
        double total = longs + shorts;
        if (total <= 0) return NetLongRatioCenter;
        return Math.Clamp(longs / total, 0.0, 1.0);
    }

    private static double EncodeRegime(MarketRegimeEnum regime) => regime switch
    {
        MarketRegimeEnum.Crisis => 0.0,
        MarketRegimeEnum.LowVolatility => 0.2,
        MarketRegimeEnum.Ranging => 0.4,
        MarketRegimeEnum.Trending => 0.6,
        MarketRegimeEnum.HighVolatility => 0.8,
        MarketRegimeEnum.Breakout => 1.0,
        _ => double.NaN
    };

    private static double ComputeImbalance(decimal bid, decimal ask, decimal mid)
    {
        if (mid == 0) return 0.0;
        // Distance of mid from the bid/ask midpoint, normalized. When mid hugs the ask,
        // buyers are paying up (positive); when mid hugs the bid, sellers are dumping (negative).
        double midpoint = (double)(bid + ask) / 2.0;
        double halfSpread = Math.Max(1e-9, (double)(ask - bid) / 2.0);
        return Math.Clamp(((double)mid - midpoint) / halfSpread, -1.0, 1.0);
    }
}
