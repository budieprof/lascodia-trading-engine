using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationCycleDataService))]
internal sealed class StrategyGenerationCycleDataService : IStrategyGenerationCycleDataService
{
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
            lowConfidenceSymbols);
    }
}
