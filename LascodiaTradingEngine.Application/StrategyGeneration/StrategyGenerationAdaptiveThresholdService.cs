using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Workers;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyGenerationAdaptiveThresholdService))]
internal sealed class StrategyGenerationAdaptiveThresholdService : IStrategyGenerationAdaptiveThresholdService
{
    private readonly ILogger<StrategyGenerationWorker> _logger;
    private readonly TradingMetrics _metrics;
    private readonly TimeProvider _timeProvider;

    public StrategyGenerationAdaptiveThresholdService(
        ILogger<StrategyGenerationWorker> logger,
        TradingMetrics metrics,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _metrics = metrics;
        _timeProvider = timeProvider;
    }

    public void DetectFeedbackAdaptiveContradictions(
        IReadOnlyDictionary<(StrategyType, MarketRegimeEnum, Timeframe), double> feedbackRates,
        IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments> adaptiveAdjustmentsByContext)
    {
        if (feedbackRates.Count == 0 || adaptiveAdjustmentsByContext.Count == 0)
            return;

        const double boostThreshold = 0.6;
        foreach (var ((strategyType, regime, timeframe), survivalRate) in feedbackRates)
        {
            if (survivalRate <= boostThreshold)
                continue;

            if (!adaptiveAdjustmentsByContext.TryGetValue((regime, timeframe), out var adaptiveAdjustments))
                continue;

            bool thresholdsTightened = adaptiveAdjustments.WinRateMultiplier < 0.95
                || adaptiveAdjustments.ProfitFactorMultiplier < 0.95
                || adaptiveAdjustments.SharpeMultiplier < 0.95;
            if (!thresholdsTightened)
                continue;

            _logger.LogWarning(
                "StrategyGenerationWorker: contradiction — {Type} in {Regime}/{Tf} has {SurvivalRate:P0} survival but adaptive thresholds are tightened (WR×{WR:F2}, PF×{PF:F2}, Sharpe×{Sh:F2}). Historical survivors are strong but recent screening candidates are weak — consider investigating regime shift or template staleness.",
                strategyType,
                regime,
                timeframe,
                survivalRate,
                adaptiveAdjustments.WinRateMultiplier,
                adaptiveAdjustments.ProfitFactorMultiplier,
                adaptiveAdjustments.SharpeMultiplier);
            _metrics.StrategyGenFeedbackAdaptiveContradictions.Add(1,
                new KeyValuePair<string, object?>("strategy_type", strategyType.ToString()));
        }
    }

    public async Task<IReadOnlyDictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>> ComputeAdaptiveThresholdsAsync(
        DbContext db,
        GenerationConfig config,
        CancellationToken ct)
    {
        var recentCutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-90);

        var recentStrategies = await db.Set<Strategy>()
            .Where(s => s.Name.StartsWith("Auto-") && s.CreatedAt >= recentCutoff && !s.IsDeleted)
            .Select(s => new { s.ScreeningMetricsJson, s.Timeframe })
            .ToListAsync(ct);

        if (recentStrategies.Count < config.AdaptiveThresholdsMinSamples)
            return new Dictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>();

        var byContext = new Dictionary<(MarketRegimeEnum, Timeframe), (List<double> WinRates, List<double> ProfitFactors, List<double> Sharpes)>();

        foreach (var strategy in recentStrategies)
        {
            var metrics = ScreeningMetrics.FromJson(strategy.ScreeningMetricsJson);
            if (metrics == null || !Enum.TryParse<MarketRegimeEnum>(metrics.Regime, out var regime))
                continue;

            var key = (regime, strategy.Timeframe);
            if (!byContext.TryGetValue(key, out var bucket))
            {
                bucket = (new List<double>(), new List<double>(), new List<double>());
                byContext[key] = bucket;
            }

            bucket.WinRates.Add(metrics.IsWinRate);
            bucket.ProfitFactors.Add(metrics.IsProfitFactor);
            bucket.Sharpes.Add(metrics.IsSharpeRatio);
        }

        var adjustments = new Dictionary<(MarketRegimeEnum, Timeframe), AdaptiveThresholdAdjustments>();
        foreach (var (key, values) in byContext)
        {
            if (values.WinRates.Count < config.AdaptiveThresholdsMinSamples)
                continue;

            double wrMult = ComputeAdaptiveMultiplier(Median(values.WinRates), config.MinWinRate);
            double pfMult = ComputeAdaptiveMultiplier(Median(values.ProfitFactors), config.MinProfitFactor);
            double shMult = ComputeAdaptiveMultiplier(Median(values.Sharpes), config.MinSharpe);

            var adjustment = new AdaptiveThresholdAdjustments(wrMult, pfMult, shMult, 1.0);
            if (adjustment == AdaptiveThresholdAdjustments.Neutral)
                continue;

            adjustments[key] = adjustment;
            _metrics.StrategyGenAdaptiveThresholdsApplied.Add(1);
            _logger.LogInformation(
                "StrategyGenerationWorker: adaptive thresholds — {Regime}/{Tf} WR×{WR:F2}, PF×{PF:F2}, Sharpe×{Sh:F2} ({N} samples)",
                key.Item1,
                key.Item2,
                wrMult,
                pfMult,
                shMult,
                values.WinRates.Count);
        }

        return adjustments;
    }
}
