using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Services;

/// <summary>
/// Measures how well market regime classifications agree across multiple timeframes (H1, H4, D1)
/// for a given symbol, producing a coherence score between 0.0 and 1.0.
///
/// <b>Why this matters:</b> When different timeframes classify the market differently — e.g. H1
/// detects a Trending regime while H4 sees Ranging — the market's true state is ambiguous.
/// Signals generated during such disagreement are statistically lower quality because the
/// strategy may be optimised for one regime while actually operating in another. The
/// <see cref="Workers.StrategyWorker"/> uses the coherence score as an early global filter:
/// if the score falls below <c>StrategyEvaluatorOptions.MinRegimeCoherence</c>, ALL signals
/// for that symbol are suppressed on that tick, regardless of which strategy generated them.
///
/// <b>Scoring algorithm:</b>
/// <list type="number">
///   <item>Fetch the most recent <see cref="MarketRegimeSnapshot"/> for each of the three
///         monitored timeframes (H1, H4, D1) for the given symbol.</item>
///   <item>If only 0 or 1 timeframe has data, return 1.0 (no disagreement possible — the engine
///         should not penalise a symbol just because higher-timeframe regime detection hasn't
///         run yet).</item>
///   <item>Compute the <b>majority fraction</b>: the count of timeframes that agree with the most
///         common regime, divided by the total number of timeframes with data.
///         <br/>Example: [Trending, Trending, Ranging] → majority = 2/3 = 0.667</item>
///   <item>Apply a <b>category bonus</b> (+0.10, capped at 1.0): if all regimes fall into the
///         same behavioural category — either all directional (Trending/Breakout) or all
///         non-directional (Ranging/LowVolatility) — they are conceptually compatible even if
///         not identical. For example, [Trending, Breakout, Trending] scores 2/3 + 0.10 = 0.767
///         instead of 0.667, because both Trending and Breakout favour directional strategies.</item>
/// </list>
///
/// <b>Typical score interpretation:</b>
/// <list type="bullet">
///   <item>1.0 — All timeframes agree on the exact same regime (strongest conviction).</item>
///   <item>0.77 — Majority agree, all in the same category (good — minor disagreement on intensity).</item>
///   <item>0.67 — Majority agree but categories differ (moderate — proceed with reduced confidence).</item>
///   <item>0.33 — No majority; every timeframe sees a different regime (weak — suppress signals).</item>
/// </list>
///
/// <b>DI lifetime:</b> Singleton via <see cref="RegisterServiceAttribute"/>. Uses
/// <see cref="IServiceScopeFactory"/> to create scoped DB contexts per call since the checker
/// is invoked from the singleton <see cref="Workers.StrategyWorker"/> on every price tick.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public class RegimeCoherenceChecker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RegimeCoherenceChecker> _logger;

    // Cache TTL matches RegimeDetectionWorker polling cadence (60s). Bounds staleness
    // to one detection cycle — coherence cannot shift faster than the underlying
    // snapshots are refreshed, so a value older than this is guaranteed fresh.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Regimes that indicate a directional market — price is moving with momentum in one direction.
    /// Strategies designed for trend-following or breakout capture perform well in these regimes.
    /// Trending, Breakout, and HighVolatility are grouped together because all favour directional
    /// strategies (strong moves, even if choppy in the HighVolatility case).
    /// </summary>
    private static readonly HashSet<MarketRegimeEnum> DirectionalRegimes = new()
    {
        MarketRegimeEnum.Trending,
        MarketRegimeEnum.Breakout,
        MarketRegimeEnum.HighVolatility
    };

    /// <summary>
    /// Regimes that indicate a non-directional market — price is oscillating within a range or
    /// barely moving. Mean-reversion and range-bound strategies perform well in these regimes.
    /// Ranging and LowVolatility are grouped because both favour tight SL/TP and counter-trend
    /// entries, even though volatility levels differ.
    /// </summary>
    private static readonly HashSet<MarketRegimeEnum> NonDirectionalRegimes = new()
    {
        MarketRegimeEnum.Ranging,
        MarketRegimeEnum.LowVolatility
    };

    public RegimeCoherenceChecker(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<RegimeCoherenceChecker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache        = cache;
        _logger       = logger;
    }

    /// <summary>
    /// Drops the cached coherence score for a symbol so the next call recomputes.
    /// Intended for use from <see cref="Workers.RegimeDetectionWorker"/> when a new
    /// snapshot is persisted with a different regime than the previous one.
    /// </summary>
    public void Invalidate(string symbol) => _cache.Remove(BuildCacheKey(symbol));

    private static string BuildCacheKey(string symbol) => $"regime-coherence:{symbol}";

    /// <summary>
    /// Computes the cross-timeframe regime coherence score for a symbol.
    ///
    /// Called by <see cref="Workers.StrategyWorker"/> on every price tick before the parallel
    /// strategy evaluation loop. A low score causes the worker to suppress ALL signals for
    /// the symbol on that tick.
    /// </summary>
    /// <param name="symbol">The instrument symbol (e.g. "EURUSD") to check coherence for.</param>
    /// <param name="cancellationToken">Propagated from the host's stopping token.</param>
    /// <returns>
    /// A decimal in [0.0, 1.0] where 1.0 means perfect agreement and values below
    /// <c>MinRegimeCoherence</c> (typically 0.5) trigger signal suppression.
    /// </returns>
    public async Task<decimal> GetCoherenceScoreAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var cacheKey = BuildCacheKey(symbol);
        if (_cache.TryGetValue(cacheKey, out decimal cached))
            return cached;

        // Create a scoped DB context because this singleton is called from the singleton
        // StrategyWorker — we can't inject a scoped IReadApplicationDbContext directly.
        using var scope = _scopeFactory.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IReadApplicationDbContext>();

        // ── Step 1: Fetch the latest regime per monitored timeframe ──────────
        // We check H1, H4, and D1 — these represent short-term, medium-term, and long-term
        // market structure respectively. M1/M5/M15 are excluded because intraday noise makes
        // their regime classifications too unstable for a coherence check.
        var timeframes = new[] { Timeframe.H1, Timeframe.H4, Timeframe.D1 };
        var regimes = new List<MarketRegimeEnum>();

        // Single DB query: group snapshots by timeframe, take the most recent per group,
        // and extract the regime enum.
        regimes = await readContext.GetDbContext()
                .Set<MarketRegimeSnapshot>()
                .Where(r => r.Symbol == symbol && timeframes.Contains(r.Timeframe) && !r.IsDeleted)
                .GroupBy(r => r.Timeframe)
                .Select(s => s.OrderByDescending(r => r.DetectedAt).First().Regime).ToListAsync(cancellationToken);

        // ── Step 2: Handle insufficient data ────────────────────────────────
        // If only 0 or 1 timeframe has regime data, there's nothing to compare.
        // Return 1.0 (full coherence) so the engine doesn't penalise a symbol just because
        // the RegimeDetectionWorker hasn't processed all timeframes yet (e.g. on startup
        // before H4/D1 candles have closed for the first time).
        if (regimes.Count <= 1)
        {
            _cache.Set(cacheKey, 1.0m, CacheTtl);
            return 1.0m;
        }

        // ── Step 3: Majority-based coherence calculation ────────────────────
        // Find the most common regime among the timeframes. The coherence score is the
        // fraction of timeframes that agree with this majority.
        //
        // Examples with 3 timeframes:
        //   [Trending, Trending, Trending]  → 3/3 = 1.00 (perfect agreement)
        //   [Trending, Trending, Ranging]   → 2/3 = 0.67 (majority agrees)
        //   [Trending, Ranging, Breakout]   → 1/3 = 0.33 (no agreement)
        var majority = regimes
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .First();

        decimal coherence = (decimal)majority.Count() / regimes.Count;

        // ── Step 4: Category alignment bonus ────────────────────────────────
        // Even when timeframes disagree on the exact regime, they may agree on the
        // behavioural category. For example:
        //   [Trending, Breakout, Trending] → exact majority = 2/3 = 0.67
        //   But all three are directional, so the +0.10 bonus → 0.77
        //
        // This reflects the practical reality that a Trending H1 + Breakout H4 + Trending D1
        // is a much better environment for directional strategies than a Trending H1 +
        // Ranging H4 + LowVolatility D1. The bonus bridges the gap between exact-match
        // and category-match coherence.
        bool allDirectional = regimes.All(r => DirectionalRegimes.Contains(r));
        bool allNonDirectional = regimes.All(r => NonDirectionalRegimes.Contains(r));

        if (allDirectional || allNonDirectional)
            coherence = Math.Min(1.0m, coherence + 0.1m); // Cap at 1.0 to stay within [0, 1] range

        // Crisis gets its own treatment — coherence bonus if ALL timeframes agree on Crisis
        if (regimes.All(r => r == MarketRegimeEnum.Crisis))
            coherence = Math.Min(1.0m, coherence + 0.2m); // strong consensus bonus

        _cache.Set(cacheKey, coherence, CacheTtl);
        return coherence;
    }
}
