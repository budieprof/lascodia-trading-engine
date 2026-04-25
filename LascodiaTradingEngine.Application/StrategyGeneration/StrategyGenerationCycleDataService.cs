using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCycleDataService))]
/// <summary>
/// Loads the read-heavy snapshot required to start a strategy-generation cycle.
/// </summary>
internal sealed class StrategyGenerationCycleDataService : IStrategyGenerationCycleDataService
{
    private readonly IStrategyGenerationMarketDataPolicy _marketDataPolicy;

    public StrategyGenerationCycleDataService(IStrategyGenerationMarketDataPolicy marketDataPolicy)
    {
        _marketDataPolicy = marketDataPolicy;
    }

    public async Task<int> CountRecentAutoCandidatesAsync(DbContext db, DateTime createdAfterUtc, CancellationToken ct)
        => await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.Name.StartsWith("Auto-")
                     && s.CreatedAt >= createdAfterUtc
                     && (!s.IsDeleted || s.PrunedAtUtc != null))
            .CountAsync(ct);

    public async Task<bool> IsInDrawdownRecoveryAsync(DbContext db, CancellationToken ct)
    {
        var latest = await db.Set<DrawdownSnapshot>()
            .Where(s => !s.IsDeleted)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);
        return latest != null && latest.RecoveryMode != RecoveryMode.Normal;
    }

    public async Task<StrategyGenerationCycleDataSnapshot> LoadCycleDataAsync(
        DbContext db,
        GenerationConfig config,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // Currency-pair metadata anchors symbol classification, cost modeling, and currency
        // concentration checks for the rest of the screening cycle.
        var activePairEntities = await db.Set<CurrencyPair>()
            .Where(p => !p.IsDeleted && p.IsActive)
            .ToListAsync(ct);

        var activePairs = activePairEntities.Select(p => p.Symbol).Distinct().ToList();
        var pairDataBySymbol = activePairEntities
            .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var existing = await db.Set<Strategy>()
            .Where(s => !s.IsDeleted)
            .Select(s => new StrategyGenerationExistingStrategyInfo(
                s.Id,
                s.StrategyType,
                s.Symbol,
                s.Timeframe,
                s.Status,
                s.LifecycleStage))
            .ToListAsync(ct);

        var activeCountBySymbol = existing
            .Where(s => s.Status == StrategyStatus.Active)
            .GroupBy(s => s.Symbol)
            .ToDictionary(g => g.Key, g => g.Count());

        var retryCutoff = nowUtc.AddDays(-config.RetryCooldownDays);
        // Recently pruned auto strategies are tracked so the cycle can avoid recreating the same
        // losing template too soon after it was already rejected.
        var recentlyPruned = await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.IsDeleted
                     && s.Name.StartsWith("Auto-")
                     && s.PrunedAtUtc != null
                     && s.PrunedAtUtc >= retryCutoff)
            .Select(s => new
            {
                s.StrategyType,
                s.Symbol,
                s.Timeframe,
                s.ParametersJson,
            })
            .ToListAsync(ct);

        var prunedTemplates = new Dictionary<CandidateCombo, HashSet<string>>();
        var fullyPrunedCombos = new HashSet<CandidateCombo>();
        foreach (var strategy in recentlyPruned)
        {
            var combo = new CandidateCombo(strategy.StrategyType, strategy.Symbol, strategy.Timeframe);
            var normalizedParams = string.IsNullOrWhiteSpace(strategy.ParametersJson)
                ? strategy.ParametersJson
                : NormalizeTemplateParameters(strategy.ParametersJson);

            if (string.IsNullOrWhiteSpace(normalizedParams))
            {
                fullyPrunedCombos.Add(combo);
                continue;
            }

            if (!prunedTemplates.TryGetValue(combo, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                prunedTemplates[combo] = set;
            }

            set.Add(normalizedParams);
        }

        var regimeFreshnessCutoff = nowUtc.AddHours(-config.RegimeFreshnessHours);
        var recentRegimeSnapshots = await db.Set<MarketRegimeSnapshot>()
            .Where(s => !s.IsDeleted && s.DetectedAt >= regimeFreshnessCutoff)
            .OrderByDescending(s => s.DetectedAt)
            .ToListAsync(ct);

        var regimeBySymbol = new Dictionary<string, MarketRegimeEnum>(StringComparer.OrdinalIgnoreCase);
        var regimeConfidenceBySymbol = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var regimeTransitions = new Dictionary<string, MarketRegimeEnum>(StringComparer.OrdinalIgnoreCase);
        var regimeDetectedAtBySymbol = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var transitionSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowConfidenceSymbols = new List<string>();

        // Keep only the freshest regime per symbol and annotate recent transitions so the
        // downstream planners can switch into transition-aware strategy selection.
        foreach (var group in recentRegimeSnapshots.GroupBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.First();
            if ((double)latest.Confidence < config.MinRegimeConfidence)
            {
                lowConfidenceSymbols.Add(latest.Symbol);
                continue;
            }

            var secondLatest = group.Skip(1).FirstOrDefault();
            if (secondLatest != null && secondLatest.Regime != latest.Regime)
            {
                regimeTransitions[latest.Symbol] = secondLatest.Regime;
                var transitionAge = latest.DetectedAt - secondLatest.DetectedAt;
                if (transitionAge.TotalHours < config.RegimeTransitionCooldownHours)
                    transitionSymbols.Add(latest.Symbol);
            }

            regimeBySymbol[latest.Symbol] = latest.Regime;
            regimeConfidenceBySymbol[latest.Symbol] = (double)latest.Confidence;
            regimeDetectedAtBySymbol[latest.Symbol] = latest.DetectedAt;
        }

        var regimeBySymbolTf = recentRegimeSnapshots
            .GroupBy(s => (s.Symbol.ToUpperInvariant(), s.Timeframe))
            .ToDictionary(g => g.Key, g => g.First().Regime);

        var dataHealthBySymbol = await LoadDataHealthAsync(
            db,
            activePairs,
            pairDataBySymbol,
            config,
            nowUtc,
            ct);

        return new StrategyGenerationCycleDataSnapshot(
            activePairs,
            pairDataBySymbol,
            existing,
            activeCountBySymbol,
            prunedTemplates,
            fullyPrunedCombos,
            regimeBySymbol,
            regimeBySymbolTf,
            regimeConfidenceBySymbol,
            regimeTransitions,
            regimeDetectedAtBySymbol,
            transitionSymbols,
            lowConfidenceSymbols,
            dataHealthBySymbol);
    }

    private async Task<IReadOnlyDictionary<string, StrategyGenerationDataHealthSnapshot>> LoadDataHealthAsync(
        DbContext db,
        IReadOnlyList<string> activePairs,
        IReadOnlyDictionary<string, CurrencyPair> pairDataBySymbol,
        GenerationConfig config,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var result = new Dictionary<string, StrategyGenerationDataHealthSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (activePairs.Count == 0 || config.CandidateTimeframes.Count == 0)
            return result;

        int maxScaledMonths = config.CandidateTimeframes
            .Select(tf => ScaleScreeningWindowForTimeframe(config.ScreeningMonths, tf))
            .DefaultIfEmpty(config.ScreeningMonths)
            .Max();
        var earliest = nowUtc.AddMonths(-maxScaledMonths);

        var stats = await db.Set<Candle>()
            .Where(c => activePairs.Contains(c.Symbol)
                     && config.CandidateTimeframes.Contains(c.Timeframe)
                     && c.Timestamp >= earliest
                     && c.IsClosed
                     && !c.IsDeleted)
            .GroupBy(c => new { c.Symbol, c.Timeframe })
            .Select(g => new
            {
                g.Key.Symbol,
                g.Key.Timeframe,
                Count = g.Count(),
                Latest = (DateTime?)g.Max(c => c.Timestamp),
            })
            .ToListAsync(ct);

        var statsByKey = stats.ToDictionary(
            s => (s.Symbol.ToUpperInvariant(), s.Timeframe),
            s => s);

        foreach (var symbol in activePairs)
        {
            pairDataBySymbol.TryGetValue(symbol, out var pairInfo);
            var timeframeSnapshots = new List<StrategyGenerationDataHealthTimeframeSnapshot>(config.CandidateTimeframes.Count);

            foreach (var timeframe in config.CandidateTimeframes)
            {
                var key = (symbol.ToUpperInvariant(), timeframe);
                statsByKey.TryGetValue(key, out var stat);
                double? effectiveAgeHours = stat?.Latest == null
                    ? null
                    : _marketDataPolicy.ComputeEffectiveCandleAgeHours(
                        stat.Latest.Value,
                        pairInfo?.TradingHoursJson,
                        nowUtc);

                bool enoughCandles = stat?.Count >= config.DataHealthMinCandles;
                bool freshEnough = config.MaxCandleAgeHours <= 0
                    || (effectiveAgeHours.HasValue && effectiveAgeHours.Value <= config.MaxCandleAgeHours);
                bool eligible = enoughCandles && freshEnough;
                string reason = eligible
                    ? "healthy"
                    : !enoughCandles
                        ? "insufficient_candles"
                        : "stale_candles";

                timeframeSnapshots.Add(new StrategyGenerationDataHealthTimeframeSnapshot(
                    timeframe,
                    stat?.Count ?? 0,
                    stat?.Latest,
                    effectiveAgeHours,
                    eligible,
                    reason));
            }

            double score = timeframeSnapshots.Count == 0
                ? 0.0
                : timeframeSnapshots.Count(t => t.IsEligible) / (double)timeframeSnapshots.Count;
            result[symbol] = new StrategyGenerationDataHealthSnapshot(symbol, score, timeframeSnapshots);
        }

        return result;
    }
}
