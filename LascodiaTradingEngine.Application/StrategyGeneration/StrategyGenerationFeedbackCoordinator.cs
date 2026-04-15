using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationFeedbackCoordinator))]
/// <summary>
/// Facade over feedback-driven generation services such as dynamic templates, historical
/// survival summaries, and adaptive threshold computation.
/// </summary>
internal sealed class StrategyGenerationFeedbackCoordinator : IStrategyGenerationFeedbackCoordinator
{
    private readonly IStrategyGenerationDynamicTemplateRefreshService _dynamicTemplateRefreshService;
    private readonly IStrategyGenerationFeedbackSummaryProvider _feedbackSummaryProvider;
    private readonly IStrategyGenerationAdaptiveThresholdService _adaptiveThresholdService;
    private readonly TradingMetrics _metrics;

    public StrategyGenerationFeedbackCoordinator(
        TradingMetrics metrics,
        IStrategyGenerationDynamicTemplateRefreshService dynamicTemplateRefreshService,
        IStrategyGenerationFeedbackSummaryProvider feedbackSummaryProvider,
        IStrategyGenerationAdaptiveThresholdService adaptiveThresholdService)
    {
        _metrics = metrics;
        _dynamicTemplateRefreshService = dynamicTemplateRefreshService;
        _feedbackSummaryProvider = feedbackSummaryProvider;
        _adaptiveThresholdService = adaptiveThresholdService;
    }

    public Task RefreshDynamicTemplatesAsync(DbContext db, CancellationToken ct)
        => _dynamicTemplateRefreshService.RefreshDynamicTemplatesAsync(db, ct);

    public Task<(Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> TypeRates, Dictionary<string, double> TemplateRates)>
        LoadPerformanceFeedbackAsync(
            DbContext db,
            IWriteApplicationDbContext writeCtx,
            double halfLifeDays,
            CancellationToken ct)
        => _feedbackSummaryProvider.LoadPerformanceFeedbackAsync(db, writeCtx, halfLifeDays, ct);

    public IReadOnlyList<StrategyType> ApplyPerformanceFeedback(
        IReadOnlyList<StrategyType> types,
        MarketRegimeEnum regime,
        Timeframe timeframe,
        Dictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates)
    {
        if (feedbackRates.Count == 0 || types.Count <= 1)
            return types;

        // Only reorder when there is actual feedback for the current regime context; otherwise
        // preserve the mapper's original ordering so unexplained drift does not creep in.
        if (!types.Any(t =>
                feedbackRates.ContainsKey((t, regime, timeframe))
                || feedbackRates.Keys.Any(k => k.Item1 == t && k.Item2 == regime)))
            return types;

        return types
            .OrderByDescending(t => feedbackRates.ContainsKey((t, regime, timeframe)) ? 2 : 1)
            .ThenByDescending(t =>
            {
                if (feedbackRates.TryGetValue((t, regime, timeframe), out var exactRate))
                {
                    // Exact regime/timeframe matches are treated as stronger evidence than
                    // same-regime fallback averages from other timeframes.
                    _metrics.StrategyGenFeedbackBoosted.Add(1,
                        new KeyValuePair<string, object?>("strategy_type", t.ToString()));
                    return exactRate;
                }

                var fallbackRates = feedbackRates
                    .Where(kv => kv.Key.Item1 == t && kv.Key.Item2 == regime)
                    .Select(kv => kv.Value)
                    .ToList();
                return fallbackRates.Count > 0 ? fallbackRates.Average() : 0.5;
            })
            .ToList();
    }

    public void DetectFeedbackAdaptiveContradictions(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates,
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adaptiveAdjustmentsByContext)
        => _adaptiveThresholdService.DetectFeedbackAdaptiveContradictions(feedbackRates, adaptiveAdjustmentsByContext);

    public Task<IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>> ComputeAdaptiveThresholdsAsync(
        DbContext db,
        GenerationConfig config,
        CancellationToken ct)
        => _adaptiveThresholdService.ComputeAdaptiveThresholdsAsync(db, config, ct);

    public static Dictionary<(StrategyType, MarketRegimeEnum), double> AggregateFeedbackRatesForMapper(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates)
        => feedbackRates
            .GroupBy(kv => (kv.Key.Item1, kv.Key.Item2))
            .ToDictionary(g => g.Key, g => g.Max(kv => kv.Value));
}
