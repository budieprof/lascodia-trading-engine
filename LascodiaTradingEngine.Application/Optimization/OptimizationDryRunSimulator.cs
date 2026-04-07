using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Simulates what an optimization run would look like without executing it.
/// Loads config, validates, estimates candle counts, grid size, and resource requirements.
/// Used by the dry-run endpoint to let operators tune config before committing compute.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
public sealed class OptimizationDryRunSimulator
{
    private readonly OptimizationConfigProvider _configProvider;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    internal OptimizationDryRunSimulator(
        OptimizationConfigProvider configProvider,
        TimeProvider timeProvider)
    {
        _configProvider = configProvider;
        _timeProvider = timeProvider;
    }

    internal sealed record DryRunResult(
        bool ConfigValid,
        List<string> ConfigIssues,
        string PresetName,
        int EffectiveTpeBudget,
        int EstimatedCandleCount,
        int EstimatedTrainCandles,
        int EstimatedTestCandles,
        int EstimatedEmbargoCandles,
        int GridSize,
        int FreshCandidatesAfterMemory,
        string SurrogateType,
        int MaxConcurrentRuns,
        int CurrentlyRunning,
        int CurrentlyQueued,
        bool InBlackoutPeriod,
        bool InDrawdownRecovery,
        string? CurrentRegime,
        double EstimatedSpreadPoints,
        int CandleLookbackMonths,
        double EstimatedRunTimeMinutes,
        Dictionary<string, string> EffectiveConfig);

    internal async Task<DryRunResult> SimulateAsync(
        DbContext db,
        long strategyId,
        CancellationToken ct)
    {
        var strategy = await db.Set<Strategy>()
            .Where(s => s.Id == strategyId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Symbol, s.Timeframe, s.StrategyType, s.ParametersJson })
            .FirstOrDefaultAsync(ct);

        if (strategy is null)
        {
            return new DryRunResult(
                false,
                ["Strategy not found"],
                "",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "",
                0,
                0,
                0,
                false,
                false,
                null,
                0,
                0,
                0,
                new());
        }

        var config = await _configProvider.LoadAsync(db, ct);
        var configIssues = OptimizationConfigValidator.Validate(
            config.AutoApprovalImprovementThreshold,
            config.AutoApprovalMinHealthScore,
            config.MinBootstrapCILower,
            config.EmbargoRatio,
            config.TpeBudget,
            config.TpeInitialSamples,
            config.MaxParallelBacktests,
            config.ScreeningTimeoutSeconds,
            config.CorrelationParamThreshold,
            config.SensitivityPerturbPct,
            config.GpEarlyStopPatience,
            config.CooldownDays,
            config.CheckpointEveryN,
            NullLogger.Instance,
            config.SensitivityDegradationTolerance,
            config.WalkForwardMinMaxRatio,
            config.CostStressMultiplier,
            config.CpcvNFolds,
            config.CpcvTestFoldCount,
            config.MinOosCandlesForValidation,
            config.CircuitBreakerThreshold,
            config.MinCandidateTrades,
            config.SuccessiveHalvingRungs,
            config.RegimeBlendRatio,
            config.MinEquityCurveR2,
            config.MaxTradeTimeConcentration);

        int effectiveLookbackMonths = config.CandleLookbackAutoScale
            ? OptimizationDataLoader.ComputeEffectiveLookback(strategy.Timeframe, config.CandleLookbackMonths)
            : config.CandleLookbackMonths;

        var candleLookbackStart = UtcNow.AddMonths(-effectiveLookbackMonths);
        int candleCount = await db.Set<Candle>()
            .CountAsync(c => c.Symbol == strategy.Symbol
                          && c.Timeframe == strategy.Timeframe
                          && c.Timestamp >= candleLookbackStart
                          && c.Timestamp <= UtcNow
                          && c.IsClosed
                          && !c.IsDeleted, ct);

        var protocol = OptimizationGridBuilder.GetDataProtocol(candleCount, config.DataScarcityThreshold);
        int trainCandles = (int)(candleCount * protocol.TrainRatio);
        int embargoCandles = Math.Max(1, (int)(candleCount * config.EmbargoRatio));
        int testCandles = Math.Max(0, candleCount - trainCandles - embargoCandles);

        int gridSize = 0;
        try
        {
            var grid = await new OptimizationGridBuilder(NullLogger<OptimizationGridBuilder>.Instance)
                .BuildParameterGridAsync(db, strategy.StrategyType, ct);
            gridSize = grid.Count;
        }
        catch
        {
        }

        int previouslyPromoted = await db.Set<OptimizationRun>()
            .CountAsync(r => r.StrategyId == strategy.Id
                          && (r.Status == OptimizationRunStatus.Approved || r.Status == OptimizationRunStatus.Completed)
                          && r.BestParametersJson != null
                          && !r.IsDeleted, ct);
        int freshCandidates = Math.Max(0, gridSize - previouslyPromoted);

        int paramDimensions = 0;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(strategy.ParametersJson);
            paramDimensions = parsed?.Count ?? 0;
        }
        catch
        {
        }

        string surrogateType = paramDimensions >= 6 ? "GP-UCB" : "TPE";

        int running = await db.Set<OptimizationRun>()
            .CountAsync(r => r.Status == OptimizationRunStatus.Running && !r.IsDeleted, ct);
        int queued = await db.Set<OptimizationRun>()
            .CountAsync(r => r.Status == OptimizationRunStatus.Queued && !r.IsDeleted, ct);

        var currentRegime = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => s.Regime.ToString())
            .FirstOrDefaultAsync(ct);

        bool inDrawdownRecovery = await db.Set<DrawdownSnapshot>()
            .OrderByDescending(s => s.RecordedAt)
            .Where(s => !s.IsDeleted)
            .Select(s => s.RecoveryMode != RecoveryMode.Normal)
            .FirstOrDefaultAsync(ct);

        double effectiveSpread = config.ScreeningSpreadPoints;
        if (config.UseSymbolSpecificSpread)
        {
            var pair = await db.Set<CurrencyPair>()
                .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, ct);
            if (pair is not null && pair.SpreadPoints > 0)
                effectiveSpread = Math.Max(config.ScreeningSpreadPoints, pair.SpreadPoints * 1.5);
        }

        double estimatedMinutes = (double)config.TpeBudget
            / Math.Max(1, config.MaxParallelBacktests)
            * config.ScreeningTimeoutSeconds
            / 60.0
            * 1.3;

        var effectiveConfig = new Dictionary<string, string>
        {
            ["Preset"] = config.PresetName,
            ["TpeBudget"] = config.TpeBudget.ToString(),
            ["TpeInitialSamples"] = config.TpeInitialSamples.ToString(),
            ["MaxParallelBacktests"] = config.MaxParallelBacktests.ToString(),
            ["MaxRunTimeoutMinutes"] = config.MaxRunTimeoutMinutes.ToString(),
            ["CandleLookbackMonths"] = effectiveLookbackMonths.ToString(),
            ["EmbargoRatio"] = config.EmbargoRatio.ToString("F2"),
            ["DataProtocol"] = $"TrainRatio={protocol.TrainRatio:F2}, KFolds={protocol.KFolds}",
            ["SurrogateType"] = surrogateType,
        };

        return new DryRunResult(
            ConfigValid: configIssues.Count == 0,
            ConfigIssues: configIssues,
            PresetName: config.PresetName,
            EffectiveTpeBudget: config.TpeBudget,
            EstimatedCandleCount: candleCount,
            EstimatedTrainCandles: trainCandles,
            EstimatedTestCandles: testCandles,
            EstimatedEmbargoCandles: embargoCandles,
            GridSize: gridSize,
            FreshCandidatesAfterMemory: freshCandidates,
            SurrogateType: surrogateType,
            MaxConcurrentRuns: config.MaxConcurrentRuns,
            CurrentlyRunning: running,
            CurrentlyQueued: queued,
            InBlackoutPeriod: config.SeasonalBlackoutEnabled
                && OptimizationPolicyHelpers.IsInBlackoutPeriod(config.BlackoutPeriods, UtcNow),
            InDrawdownRecovery: inDrawdownRecovery,
            CurrentRegime: currentRegime,
            EstimatedSpreadPoints: effectiveSpread,
            CandleLookbackMonths: effectiveLookbackMonths,
            EstimatedRunTimeMinutes: Math.Round(estimatedMinutes, 1),
            EffectiveConfig: effectiveConfig);
    }
}
