using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationFeedbackSummaryProvider))]
/// <summary>
/// Computes and caches historical survival-rate summaries used to bias future candidate
/// generation toward strategies and parameter templates that have aged well.
/// </summary>
internal sealed class StrategyGenerationFeedbackSummaryProvider : IStrategyGenerationFeedbackSummaryProvider
{
    private const string FeedbackSummaryStateKey = "feedback_summary";

    private sealed record FeedbackSummaryEntry(string StrategyType, string Regime, string Timeframe, double SurvivalRate);
    private sealed record FeedbackStrategySnapshot(
        StrategyType StrategyType,
        Timeframe Timeframe,
        string? Description,
        string? ScreeningMetricsJson,
        string? ParametersJson,
        StrategyLifecycleStage LifecycleStage,
        bool IsDeleted,
        DateTime CreatedAt,
        DateTime? PrunedAtUtc);
    private sealed record FeedbackSummaryCache(
        string SignatureFingerprint,
        int StrategyCount,
        DateTime ComputedAtUtc,
        List<FeedbackSummaryEntry> Entries);

    private readonly IStrategyGenerationFeedbackStateStore _feedbackStateStore;
    private readonly IStrategyGenerationMarketDataPolicy _marketDataPolicy;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationFeedbackSummaryProvider(
        IStrategyGenerationFeedbackStateStore feedbackStateStore,
        IStrategyGenerationMarketDataPolicy marketDataPolicy,
        TimeProvider timeProvider)
    {
        _feedbackStateStore = feedbackStateStore;
        _marketDataPolicy = marketDataPolicy;
        _timeProvider = timeProvider;
    }

    public async Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
        DbContext db,
            IWriteApplicationDbContext writeCtx,
        double halfLifeDays,
        CancellationToken ct)
    {
        // Limit the feedback horizon so the generator reacts to recent behavior instead of
        // overweighting stale candidates from older market regimes.
        var feedbackCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-180);

        var allAutoStrategies = await db.Set<Strategy>()
            .IncludingSoftDeleted()
            .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= feedbackCutoff)
            .Select(s => new FeedbackStrategySnapshot(
                s.StrategyType,
                s.Timeframe,
                s.Description,
                s.ScreeningMetricsJson,
                s.ParametersJson,
                s.LifecycleStage,
                s.IsDeleted,
                s.CreatedAt,
                s.PrunedAtUtc))
            .ToListAsync(ct);

        var evaluableStrategies = allAutoStrategies
            .Where(IsResolvedFeedbackOutcome)
            .ToList();

        if (evaluableStrategies.Count == 0)
        {
            return (
                new Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double>(),
                new Dictionary<string, double>(StringComparer.Ordinal));
        }

        int strategyCount = evaluableStrategies.Count;
        string currentFingerprint = ComputeFeedbackFingerprint(evaluableStrategies);

        var cachedState = await _feedbackStateStore.LoadAsync(db, FeedbackSummaryStateKey, ct);
        if (cachedState != null)
        {
            try
            {
                // Cache reuse is allowed only when the candidate set fingerprint still matches;
                // otherwise recompute to avoid serving rates for a different strategy population.
                var cached = JsonSerializer.Deserialize<FeedbackSummaryCache>(cachedState.PayloadJson);
                if (cached != null
                    && cached.StrategyCount == strategyCount
                    && string.Equals(cached.SignatureFingerprint, currentFingerprint, StringComparison.Ordinal)
                    && (_timeProvider.GetUtcNow().UtcDateTime - cached.ComputedAtUtc).TotalHours < 24)
                {
                    var cachedTypeRates = DeserializeFeedbackRates(cached.Entries);
                    var freshTemplateRates = ComputeTemplateFeedbackRates(evaluableStrategies, halfLifeDays);
                    return (cachedTypeRates, freshTemplateRates);
                }
            }
            catch
            {
                // Ignore stale or corrupt cache and recompute.
            }
        }

        var (rates, templateRates) = ComputeFeedbackRates(evaluableStrategies, halfLifeDays);

        try
        {
            var writeDb = writeCtx.GetDbContext();
            var summary = new FeedbackSummaryCache(
                currentFingerprint,
                strategyCount,
                _timeProvider.GetUtcNow().UtcDateTime,
                rates.Select(kv => new FeedbackSummaryEntry(
                    kv.Key.Item1.ToString(),
                    kv.Key.Item2.ToString(),
                    kv.Key.Item3.ToString(),
                    kv.Value)).ToList());
            var summaryJson = JsonSerializer.Serialize(summary);
            await _feedbackStateStore.SaveAsync(writeDb, FeedbackSummaryStateKey, summaryJson, ct);
            await writeCtx.SaveChangesAsync(ct);
        }
        catch
        {
            // Non-critical; next cycle will recompute.
        }

        return (rates, templateRates);
    }

    private static string ComputeFeedbackFingerprint(IReadOnlyList<FeedbackStrategySnapshot> strategies)
    {
        var parts = strategies
            .OrderBy(s => s.StrategyType)
            .ThenBy(s => s.Timeframe)
            .ThenBy(s => s.LifecycleStage)
            .ThenBy(s => s.IsDeleted)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Description, StringComparer.Ordinal)
            .ThenBy(s => s.ScreeningMetricsJson, StringComparer.Ordinal)
            .Select(s => string.Join("|",
                s.StrategyType,
                s.Timeframe,
                s.LifecycleStage,
                s.IsDeleted ? "1" : "0",
                s.CreatedAt.ToUniversalTime().Ticks,
                s.Description ?? string.Empty,
                s.ScreeningMetricsJson ?? string.Empty));

        using var sha = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts));
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private (Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)
        ComputeFeedbackRates(IReadOnlyList<FeedbackStrategySnapshot> allAutoStrategies, double halfLifeDays)
    {
        var rates = new Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double>();
        var templateRates = ComputeTemplateFeedbackRates(allAutoStrategies, halfLifeDays);

        if (allAutoStrategies.Count == 0)
            return (rates, templateRates);

        foreach (var group in allAutoStrategies.GroupBy(s => s.StrategyType))
        {
            var byContext = new Dictionary<(MarketRegimeEnum Regime, Timeframe Timeframe), List<(bool Survived, DateTime CreatedAt)>>();

            foreach (var strategy in group)
            {
                MarketRegimeEnum? regime = null;
                var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
                if (metrics != null && Enum.TryParse<MarketRegimeEnum>(metrics.Regime, out var parsed))
                    regime = parsed;
                else
                    regime = ParseRegimeFromDescription(strategy.Description);

                if (regime == null)
                    continue;

                if (!byContext.TryGetValue((regime.Value, strategy.Timeframe), out var list))
                {
                    list = [];
                    byContext[(regime.Value, strategy.Timeframe)] = list;
                }

                bool survived = !strategy.IsDeleted && strategy.LifecycleStage >= StrategyLifecycleStage.BacktestQualified;
                list.Add((survived, strategy.CreatedAt));
            }

            foreach (var ((regime, timeframe), strategies) in byContext)
            {
                if (strategies.Count >= 3)
                    rates[(group.Key, regime, timeframe)] = _marketDataPolicy.ComputeRecencyWeightedSurvivalRate(
                        strategies,
                        halfLifeDays,
                        _timeProvider.GetUtcNow().UtcDateTime);
            }
        }

        return (rates, templateRates);
    }

    private Dictionary<string, double> ComputeTemplateFeedbackRates(
        IReadOnlyList<FeedbackStrategySnapshot> allAutoStrategies,
        double halfLifeDays)
    {
        var templateGroups = new Dictionary<string, List<(bool Survived, DateTime CreatedAt)>>(StringComparer.Ordinal);

        foreach (var strategy in allAutoStrategies)
        {
            if (string.IsNullOrWhiteSpace(strategy.ParametersJson))
                continue;

            string normalizedParams = StrategyGenerationHelpers.NormalizeTemplateParameters(strategy.ParametersJson);
            if (string.IsNullOrWhiteSpace(normalizedParams))
                continue;

            string templateKey = StrategyGenerationHelpers.BuildTemplateFeedbackKey(strategy.StrategyType, strategy.Timeframe, normalizedParams);
            if (!templateGroups.TryGetValue(templateKey, out var list))
            {
                list = [];
                templateGroups[templateKey] = list;
            }

            bool survived = !strategy.IsDeleted && strategy.LifecycleStage >= StrategyLifecycleStage.BacktestQualified;
            list.Add((survived, strategy.CreatedAt));
        }

        var templateRates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (templateKey, samples) in templateGroups)
        {
            if (samples.Count >= 2)
                templateRates[templateKey] = _marketDataPolicy.ComputeRecencyWeightedSurvivalRate(
                    samples,
                    halfLifeDays,
                    _timeProvider.GetUtcNow().UtcDateTime);
        }

        return templateRates;
    }

    private static bool IsResolvedFeedbackOutcome(FeedbackStrategySnapshot strategy)
        => strategy.PrunedAtUtc != null || strategy.LifecycleStage >= StrategyLifecycleStage.BacktestQualified;

    private static Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> DeserializeFeedbackRates(
        List<FeedbackSummaryEntry> entries)
    {
        var rates = new Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double>();
        foreach (var entry in entries)
        {
            if (Enum.TryParse<StrategyType>(entry.StrategyType, out var strategyType)
                && Enum.TryParse<MarketRegimeEnum>(entry.Regime, out var regime)
                && Enum.TryParse<Timeframe>(entry.Timeframe, out var timeframe))
            {
                rates[(strategyType, regime, timeframe)] = entry.SurvivalRate;
            }
        }

        return rates;
    }

    private static MarketRegimeEnum? ParseRegimeFromDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;

        foreach (var regime in Enum.GetValues<MarketRegimeEnum>())
        {
            if (description.Contains(regime.ToString(), StringComparison.OrdinalIgnoreCase))
                return regime;
        }

        return null;
    }
}
