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
    double TickPressureGradient,

    // ── Derived cross-pair / carry proxies (from candle history) ─────────────

    /// <summary>
    /// Carry proxy: rolling 90-bar drift of the pair's log-return normalised by its
    /// rolling volatility. Positive = pair has been drifting up relative to its
    /// volatility, which is the signature of positive carry + momentum. Clamped to [-3, 3].
    /// Computed from the pair's own candle history; serves as a stand-in for the
    /// real rate differential until a rates feed is wired up.
    /// </summary>
    double PairCarryProxy,

    /// <summary>
    /// Safe-haven risk index: z-score of (USDJPY + USDCHF)/2 divided by
    /// (AUDUSD + NZDUSD + EURUSD)/3, over the last 60 bars. Positive = risk-off
    /// (safe havens bid), negative = risk-on. A global macro regime gauge built from
    /// existing FX data. Clamped to [-3, 3]. NaN if not enough cross-pair candles.
    /// </summary>
    double SafeHavenIndex,

    /// <summary>
    /// Dollar strength composite: weighted average of log-returns across G10 USD pairs
    /// over the last 20 bars, normalised by their rolling stddev. Positive = dollar
    /// strengthening broadly; negative = dollar weakening. Functions as an internally
    /// computed DXY proxy. Clamped to [-3, 3].
    /// </summary>
    double DollarStrengthComposite,

    /// <summary>
    /// Cross-pair correlation stress: standard deviation of pairwise 20-bar rolling
    /// correlations across a basket of G10 USD pairs. When all pairs are highly
    /// correlated (low stddev) the market is in a systematic regime; when correlations
    /// diverge (high stddev) the regime is idiosyncratic/stressed. Clamped to [0, 1].
    /// </summary>
    double CrossPairCorrelationStress)
{
    public static MacroContext Empty => new(
        RegimeCode: double.NaN,
        RegimeConfidence: double.NaN,
        SpreadZScore: double.NaN,
        CotNetLongRatioBase: 0.5,
        CotNetLongRatioQuote: 0.5,
        CotPairWeekDelta: 0.0,
        EconomicImpactWeighted: 0.0,
        TickPressureGradient: 0.0,
        PairCarryProxy: double.NaN,
        SafeHavenIndex: double.NaN,
        DollarStrengthComposite: double.NaN,
        CrossPairCorrelationStress: double.NaN);

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
            score += 0.15 * sign * Math.Clamp(TickPressureGradient, -1.0, 1.0);
            usedComponents++;
        }

        // Pair carry proxy — multi-day drift relative to volatility. Positive carry is
        // a bullish signal for a long on the pair; cap magnitude at 3 stddevs.
        if (!double.IsNaN(PairCarryProxy) && Math.Abs(PairCarryProxy) > 1e-3)
        {
            score += 0.10 * sign * Math.Clamp(PairCarryProxy / 3.0, -1.0, 1.0);
            usedComponents++;
        }

        // Dollar strength composite — a strengthening dollar is a headwind for
        // EURUSD, GBPUSD, AUDUSD, NZDUSD longs and a tailwind for USDJPY, USDCHF,
        // USDCAD longs. Without knowing the direction of the trade's pair structure,
        // treat dollar-strength divergence as a raw direction input — negative
        // contribution when dollar is strengthening against the trade's quote
        // currency. We don't know base/quote semantics here, so this field contributes
        // with a smaller weight and is mostly informational until v2 training uses it.
        if (!double.IsNaN(DollarStrengthComposite) && Math.Abs(DollarStrengthComposite) > 1e-3)
        {
            score += 0.05 * sign * Math.Clamp(DollarStrengthComposite / 3.0, -1.0, 1.0);
            usedComponents++;
        }

        // Regime confidence — higher confidence amplifies the other components rather
        // than being a direction signal of its own.
        if (!double.IsNaN(RegimeConfidence))
        {
            score *= 0.5 + 0.5 * Math.Clamp(RegimeConfidence, 0.0, 1.0);
        }

        // Safe-haven index — high values (risk-off) dampen all buy signals on risk pairs
        // and amplify safe-haven pairs. We can't tell the pair's role here so use as a
        // global damper: when risk is ON (low safe-haven index), allow full alignment;
        // when risk-off (high safe-haven), discount the score.
        if (!double.IsNaN(SafeHavenIndex) && SafeHavenIndex > 1.0)
        {
            score *= Math.Max(0.6, 1.0 - 0.10 * Math.Clamp(SafeHavenIndex - 1.0, 0.0, 2.0));
        }

        // Cross-pair correlation stress — high stress means pairs are decoupling
        // (idiosyncratic moves), which makes signals less reliable. Discount the score.
        if (!double.IsNaN(CrossPairCorrelationStress) && CrossPairCorrelationStress > 0.4)
        {
            score *= Math.Max(0.7, 1.0 - 0.50 * (CrossPairCorrelationStress - 0.4));
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

            // ── Derived cross-pair / carry proxies ──────────────────────────────
            // All four of these derive from H1 candle data for the pair of interest
            // plus a small basket of G10 USD pairs. We pull 120 bars of H1 candles
            // per symbol to cover 5 days of trading — enough for 90-bar carry drift,
            // 60-bar safe-haven z-score, and 20-bar correlation computation.
            var basketSymbols = new[] { "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD" };
            var basketCandles = await db.Set<Candle>()
                .AsNoTracking()
                .Where(c => basketSymbols.Contains(c.Symbol)
                         && c.Timeframe == Timeframe.H1
                         && c.IsClosed
                         && !c.IsDeleted
                         && c.Timestamp <= asOfUtc)
                .OrderByDescending(c => c.Timestamp)
                .Take(120 * basketSymbols.Length)
                .Select(c => new { c.Symbol, c.Timestamp, c.Close })
                .ToListAsync(ct);

            var basket = basketCandles
                .GroupBy(c => c.Symbol)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.Timestamp).Select(x => (double)x.Close).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            double pairCarryProxy = ComputePairCarryProxy(basket.GetValueOrDefault(symbol, Array.Empty<double>()));
            double safeHavenIndex = ComputeSafeHavenIndex(basket);
            double dollarStrength = ComputeDollarStrengthComposite(basket);
            double correlationStress = ComputeCrossPairCorrelationStress(basket);

            return new MacroContext(
                RegimeCode: regimeSnapshot is not null ? EncodeRegime(regimeSnapshot.Regime) : double.NaN,
                RegimeConfidence: regimeSnapshot is not null ? (double)regimeSnapshot.Confidence : double.NaN,
                SpreadZScore: spreadZ,
                CotNetLongRatioBase: baseRatio,
                CotNetLongRatioQuote: quoteRatio,
                CotPairWeekDelta: cotWeekDelta,
                EconomicImpactWeighted: economicComposite,
                TickPressureGradient: tickGradient,
                PairCarryProxy: pairCarryProxy,
                SafeHavenIndex: safeHavenIndex,
                DollarStrengthComposite: dollarStrength,
                CrossPairCorrelationStress: correlationStress);
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

    // ── Derived cross-pair proxies ──────────────────────────────────────────
    //
    // These compute macro context from existing closed-candle history. None
    // require new data sources: everything is derived from what the EA is
    // already streaming. They are best-effort substitutes for the "real"
    // macro feeds (Fed Funds futures, DXY, VIX, SPX) that would ideally feed
    // into training — useful as inference-time confidence modulators until a
    // proper vendor ingestion pipeline is added.

    /// <summary>
    /// 90-bar drift of the pair's log-returns normalised by rolling volatility.
    /// Stand-in for a true carry / rate-differential indicator.
    /// </summary>
    private static double ComputePairCarryProxy(double[] closes)
    {
        if (closes is null || closes.Length < 91) return double.NaN;

        // Compute log returns for the last 90 bars.
        var logReturns = new double[90];
        int end = closes.Length - 1;
        for (int i = 0; i < 90; i++)
        {
            double prev = closes[end - 90 + i];
            double curr = closes[end - 89 + i];
            if (prev <= 0 || curr <= 0) return double.NaN;
            logReturns[i] = Math.Log(curr / prev);
        }

        double mean = 0.0;
        for (int i = 0; i < 90; i++) mean += logReturns[i];
        mean /= 90;

        double variance = 0.0;
        for (int i = 0; i < 90; i++)
        {
            double d = logReturns[i] - mean;
            variance += d * d;
        }
        variance /= 89; // sample variance
        double stddev = Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        // Drift per bar divided by per-bar stddev; scale by sqrt(N) for cumulative effect.
        return Math.Clamp((mean / stddev) * Math.Sqrt(90), -3.0, 3.0);
    }

    /// <summary>
    /// Risk-on / risk-off gauge built from the ratio of safe-haven-dollar pairs
    /// (USDJPY, USDCHF) to risk-appetite pairs (AUDUSD, NZDUSD, EURUSD). Positive
    /// z-score = risk-off regime; negative = risk-on.
    /// </summary>
    private static double ComputeSafeHavenIndex(
        Dictionary<string, double[]> basket)
    {
        if (!basket.TryGetValue("USDJPY", out var jpy) || jpy.Length < 60) return double.NaN;
        if (!basket.TryGetValue("USDCHF", out var chf) || chf.Length < 60) return double.NaN;
        if (!basket.TryGetValue("AUDUSD", out var aud) || aud.Length < 60) return double.NaN;
        if (!basket.TryGetValue("NZDUSD", out var nzd) || nzd.Length < 60) return double.NaN;
        if (!basket.TryGetValue("EURUSD", out var eur) || eur.Length < 60) return double.NaN;

        int n = Math.Min(jpy.Length, Math.Min(chf.Length, Math.Min(aud.Length, Math.Min(nzd.Length, eur.Length))));
        if (n < 60) return double.NaN;
        int start = n - 60;

        // Ratio at each bar: (USDJPY_norm + USDCHF_norm) / (AUDUSD_norm + NZDUSD_norm + EURUSD_norm)
        // where *_norm is price divided by its own bar-0 value so absolute levels are neutralised.
        double jpy0 = jpy[start], chf0 = chf[start], aud0 = aud[start], nzd0 = nzd[start], eur0 = eur[start];
        if (jpy0 <= 0 || chf0 <= 0 || aud0 <= 0 || nzd0 <= 0 || eur0 <= 0) return double.NaN;

        var series = new double[60];
        for (int i = 0; i < 60; i++)
        {
            double num = (jpy[start + i] / jpy0 + chf[start + i] / chf0) / 2.0;
            double den = (aud[start + i] / aud0 + nzd[start + i] / nzd0 + eur[start + i] / eur0) / 3.0;
            series[i] = den > 0 ? num / den : 1.0;
        }

        double mean = 0.0;
        for (int i = 0; i < 60; i++) mean += series[i];
        mean /= 60;
        double variance = 0.0;
        for (int i = 0; i < 60; i++) { double d = series[i] - mean; variance += d * d; }
        variance /= 59;
        double stddev = Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        return Math.Clamp((series[^1] - mean) / stddev, -3.0, 3.0);
    }

    /// <summary>
    /// DXY-style composite: mean 20-bar log-return across G10 USD pairs, with
    /// pair direction normalised so that "dollar stronger" is always positive.
    /// Normalised by the cross-sectional std so values are z-scored.
    /// </summary>
    private static double ComputeDollarStrengthComposite(
        Dictionary<string, double[]> basket)
    {
        // Pairs where dollar is the QUOTE currency (e.g. EURUSD) — dollar stronger
        // means price falls, so we invert the sign.
        var quoteUsd = new[] { "EURUSD", "GBPUSD", "AUDUSD", "NZDUSD" };
        // Pairs where dollar is the BASE currency — price rising = dollar stronger.
        var baseUsd = new[] { "USDJPY", "USDCHF", "USDCAD" };

        var returns = new List<double>(7);
        foreach (var sym in quoteUsd)
        {
            if (!basket.TryGetValue(sym, out var arr) || arr.Length < 21) continue;
            double prev = arr[^21]; double curr = arr[^1];
            if (prev <= 0 || curr <= 0) continue;
            returns.Add(-Math.Log(curr / prev));  // invert: dollar-stronger = positive
        }
        foreach (var sym in baseUsd)
        {
            if (!basket.TryGetValue(sym, out var arr) || arr.Length < 21) continue;
            double prev = arr[^21]; double curr = arr[^1];
            if (prev <= 0 || curr <= 0) continue;
            returns.Add(Math.Log(curr / prev));
        }
        if (returns.Count < 4) return double.NaN;

        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / Math.Max(1, returns.Count - 1);
        double stddev = Math.Sqrt(variance);

        if (stddev < 1e-12) return 0.0;
        // Z-score of the mean drift across the basket; scale to [-3, 3].
        return Math.Clamp((mean / stddev) * Math.Sqrt(returns.Count), -3.0, 3.0);
    }

    /// <summary>
    /// Std dev of pairwise Pearson correlations between G10 USD pairs' 20-bar
    /// log-return series. Low stddev = pairs all moving together (systematic
    /// regime). High stddev = pairs decoupling (idiosyncratic / stress).
    /// </summary>
    private static double ComputeCrossPairCorrelationStress(
        Dictionary<string, double[]> basket)
    {
        var series = new List<(string Sym, double[] Returns)>();
        foreach (var kvp in basket)
        {
            var arr = kvp.Value;
            if (arr.Length < 21) continue;
            var returns = new double[20];
            int tail = arr.Length;
            for (int i = 0; i < 20; i++)
            {
                double prev = arr[tail - 21 + i];
                double curr = arr[tail - 20 + i];
                if (prev <= 0 || curr <= 0) { returns = null!; break; }
                returns[i] = Math.Log(curr / prev);
            }
            if (returns is not null)
                series.Add((kvp.Key, returns));
        }
        if (series.Count < 3) return double.NaN;

        var correlations = new List<double>();
        for (int i = 0; i < series.Count; i++)
        for (int j = i + 1; j < series.Count; j++)
        {
            double corr = PearsonCorrelation(series[i].Returns, series[j].Returns);
            if (!double.IsNaN(corr)) correlations.Add(corr);
        }
        if (correlations.Count < 2) return double.NaN;

        double mean = correlations.Average();
        double variance = correlations.Sum(c => (c - mean) * (c - mean)) / (correlations.Count - 1);
        return Math.Clamp(Math.Sqrt(variance), 0.0, 1.0);
    }

    private static double PearsonCorrelation(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return double.NaN;
        double meanX = x.Average(), meanY = y.Average();
        double num = 0.0, denX = 0.0, denY = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            num += dx * dy;
            denX += dx * dx;
            denY += dy * dy;
        }
        double den = Math.Sqrt(denX * denY);
        return den > 1e-12 ? num / den : double.NaN;
    }
}
