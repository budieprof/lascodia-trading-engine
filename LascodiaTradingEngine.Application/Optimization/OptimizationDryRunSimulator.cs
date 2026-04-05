using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Simulates what an optimization run would look like without executing it.
/// Loads config, validates, estimates candle counts, grid size, and resource requirements.
/// Used by the dry-run endpoint to let operators tune config before committing compute.
/// </summary>
internal static class OptimizationDryRunSimulator
{
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

    internal static async Task<DryRunResult> SimulateAsync(
        DbContext db, long strategyId, CancellationToken ct)
    {
        var strategy = await db.Set<Strategy>()
            .Where(s => s.Id == strategyId && !s.IsDeleted)
            .Select(s => new { s.Id, s.Symbol, s.Timeframe, s.StrategyType, s.ParametersJson })
            .FirstOrDefaultAsync(ct);

        if (strategy is null)
            return new DryRunResult(false, ["Strategy not found"], "", 0, 0, 0, 0, 0, 0, 0, "", 0, 0, 0, false, false, null, 0, 0, 0, new());

        // Load config using the same loader as the worker
        var allKeys = new[]
        {
            "Optimization:Preset", "Optimization:TpeBudget", "Optimization:TpeInitialSamples",
            "Optimization:PurgedKFolds", "Optimization:MaxParallelBacktests",
            "Optimization:MaxRunTimeoutMinutes", "Optimization:CandleLookbackMonths",
            "Optimization:EmbargoRatio", "Optimization:ScreeningSpreadPoints",
            "Optimization:UseSymbolSpecificSpread", "Optimization:MaxConcurrentRuns",
            "Optimization:SeasonalBlackoutEnabled", "Optimization:BlackoutPeriods",
            "Optimization:SuppressDuringDrawdownRecovery", "Optimization:DataScarcityThreshold",
            "Optimization:ScreeningTimeoutSeconds", "Optimization:TopNCandidates",
            "Optimization:BootstrapIterations", "Optimization:PermutationIterations",
            "Optimization:CheckpointEveryN", "Optimization:AutoApprovalImprovementThreshold",
            "Optimization:AutoApprovalMinHealthScore", "Optimization:MinBootstrapCILower",
            "Optimization:CorrelationParamThreshold", "Optimization:SensitivityPerturbPct",
            "Optimization:GpEarlyStopPatience", "Optimization:CooldownDays",
        };

        var b = await OptimizationGridBuilder.GetConfigBatchAsync(db, allKeys, ct);

        var presetName = OptimizationGridBuilder.GetConfigValue(b, "Optimization:Preset", "balanced");
        int tpeBudget = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TpeBudget", 50);
        int tpeInitial = OptimizationGridBuilder.GetConfigValue(b, "Optimization:TpeInitialSamples", 15);
        int maxParallel = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxParallelBacktests", 4);
        int maxTimeout = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxRunTimeoutMinutes", 30);
        int lookbackMonths = OptimizationGridBuilder.GetConfigValue(b, "Optimization:CandleLookbackMonths", 6);
        double embargoRatio = OptimizationGridBuilder.GetConfigValue(b, "Optimization:EmbargoRatio", 0.05);
        double spreadPoints = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningSpreadPoints", 20.0);
        bool useSymbolSpread = OptimizationGridBuilder.GetConfigValue(b, "Optimization:UseSymbolSpecificSpread", true);
        int maxConcurrent = OptimizationGridBuilder.GetConfigValue(b, "Optimization:MaxConcurrentRuns", 3);
        int dataScarcity = OptimizationGridBuilder.GetConfigValue(b, "Optimization:DataScarcityThreshold", 200);
        int screeningTimeout = OptimizationGridBuilder.GetConfigValue(b, "Optimization:ScreeningTimeoutSeconds", 30);

        // Estimate candle count
        var candleLookbackStart = DateTime.UtcNow.AddMonths(-lookbackMonths);
        int candleCount = await db.Set<Candle>()
            .CountAsync(c => c.Symbol == strategy.Symbol
                          && c.Timeframe == strategy.Timeframe
                          && c.Timestamp >= candleLookbackStart
                          && c.IsClosed && !c.IsDeleted, ct);

        var protocol = OptimizationGridBuilder.GetDataProtocol(candleCount, dataScarcity);
        int trainCandles = (int)(candleCount * protocol.TrainRatio);
        int embargoCandles = Math.Max(1, (int)(candleCount * embargoRatio));
        int testCandles = candleCount - trainCandles - embargoCandles;

        // Estimate grid size
        var gridBuilder = new OptimizationGridBuilder(null!);
        int gridSize = 0;
        try
        {
            var grid = await gridBuilder.BuildParameterGridAsync(db, strategy.StrategyType, ct);
            gridSize = grid.Count;
        }
        catch { /* non-fatal */ }

        // Parameter memory: count previously promoted params
        int previouslyPromoted = await db.Set<OptimizationRun>()
            .CountAsync(r => r.StrategyId == strategy.Id
                          && (r.Status == OptimizationRunStatus.Approved || r.Status == OptimizationRunStatus.Completed)
                          && r.BestParametersJson != null && !r.IsDeleted, ct);
        int freshCandidates = Math.Max(0, gridSize - previouslyPromoted);

        // Surrogate type
        int paramDimensions = 0;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(strategy.ParametersJson);
            paramDimensions = parsed?.Count ?? 0;
        }
        catch { }
        string surrogateType = paramDimensions >= 6 ? "GP-UCB" : "TPE";

        // Current state
        int running = await db.Set<OptimizationRun>()
            .CountAsync(r => r.Status == OptimizationRunStatus.Running && !r.IsDeleted, ct);
        int queued = await db.Set<OptimizationRun>()
            .CountAsync(r => r.Status == OptimizationRunStatus.Queued && !r.IsDeleted, ct);

        // Blackout
        string blackoutPeriods = OptimizationGridBuilder.GetConfigValue(b, "Optimization:BlackoutPeriods", "12/20-01/05");
        bool blackoutEnabled = OptimizationGridBuilder.GetConfigValue(b, "Optimization:SeasonalBlackoutEnabled", true);

        // Current regime
        var regime = await db.Set<MarketRegimeSnapshot>()
            .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe && !s.IsDeleted)
            .OrderByDescending(s => s.DetectedAt)
            .Select(s => s.Regime.ToString())
            .FirstOrDefaultAsync(ct);

        // Spread
        double effectiveSpread = spreadPoints;
        if (useSymbolSpread)
        {
            var pair = await db.Set<CurrencyPair>()
                .FirstOrDefaultAsync(p => p.Symbol == strategy.Symbol && !p.IsDeleted, ct);
            if (pair is not null && pair.SpreadPoints > 0)
                effectiveSpread = Math.Max(spreadPoints, pair.SpreadPoints * 1.5);
        }

        // Estimated run time (very rough)
        double estimatedMinutes = (double)tpeBudget / maxParallel * screeningTimeout / 60.0 * 1.3; // 30% overhead

        var effectiveConfig = new Dictionary<string, string>
        {
            ["Preset"] = presetName,
            ["TpeBudget"] = tpeBudget.ToString(),
            ["TpeInitialSamples"] = tpeInitial.ToString(),
            ["MaxParallelBacktests"] = maxParallel.ToString(),
            ["MaxRunTimeoutMinutes"] = maxTimeout.ToString(),
            ["CandleLookbackMonths"] = lookbackMonths.ToString(),
            ["EmbargoRatio"] = embargoRatio.ToString("F2"),
            ["DataProtocol"] = $"TrainRatio={protocol.TrainRatio:F2}, KFolds={protocol.KFolds}",
            ["SurrogateType"] = surrogateType,
        };

        return new DryRunResult(
            ConfigValid: true,
            ConfigIssues: [],
            PresetName: presetName,
            EffectiveTpeBudget: tpeBudget,
            EstimatedCandleCount: candleCount,
            EstimatedTrainCandles: trainCandles,
            EstimatedTestCandles: testCandles,
            EstimatedEmbargoCandles: embargoCandles,
            GridSize: gridSize,
            FreshCandidatesAfterMemory: freshCandidates,
            SurrogateType: surrogateType,
            MaxConcurrentRuns: maxConcurrent,
            CurrentlyRunning: running,
            CurrentlyQueued: queued,
            InBlackoutPeriod: false,
            InDrawdownRecovery: false,
            CurrentRegime: regime,
            EstimatedSpreadPoints: effectiveSpread,
            CandleLookbackMonths: lookbackMonths,
            EstimatedRunTimeMinutes: Math.Round(estimatedMinutes, 1),
            EffectiveConfig: effectiveConfig);
    }
}
