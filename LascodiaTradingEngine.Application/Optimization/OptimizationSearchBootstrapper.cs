using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using static LascodiaTradingEngine.Application.Optimization.OptimizationSearchCoordinator;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton)]
internal sealed class OptimizationSearchBootstrapper
{
    internal sealed record PriorRunSeed(
        string? BestParametersJson,
        decimal? BestHealthScore,
        string? BaselineParametersJson,
        decimal? BaselineHealthScore,
        DateTime? CompletedAt,
        decimal? BestSharpeRatio,
        decimal? BestMaxDrawdownPct,
        decimal? BestWinRate,
        string? RunMetadataJson);

    internal sealed record SearchBootstrapState(
        List<Dictionary<string, object>> ParameterGrid,
        List<Dictionary<string, object>> FreshCandidates,
        Dictionary<string, (double Min, double Max, bool IsInteger)> TpeBounds,
        HashSet<string> PreviousParamSet,
        TreeParzenEstimator? Tpe,
        GaussianProcessSurrogate? Gp,
        EhviAcquisition? Ehvi,
        ParegoScalarizer ParegoScalarizer,
        bool UseGp,
        bool UseParegoScalarization,
        string SurrogateKind,
        string? OptimizationRegimeText,
        string CurrentDataWindowFingerprint,
        int WarmStarted,
        int PriorRunCount,
        List<ScoredCandidate> AllEvaluated,
        ConcurrentDictionary<string, byte> SeenParamSet,
        int TotalIterations,
        int InitialStagnantBatches,
        bool ResumedFromCheckpoint);

    private readonly OptimizationGridBuilder _gridBuilder;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<OptimizationSearchBootstrapper> _logger;

    public OptimizationSearchBootstrapper(
        OptimizationGridBuilder gridBuilder,
        TradingMetrics metrics,
        ILogger<OptimizationSearchBootstrapper> logger)
    {
        _gridBuilder = gridBuilder;
        _metrics = metrics;
        _logger = logger;
    }

    internal async Task<SearchBootstrapState> BootstrapAsync(
        DbContext db,
        OptimizationRun run,
        Strategy strategy,
        SearchConfig config,
        List<Candle> trainCandles,
        List<Candle> candles,
        int embargoSize,
        MarketRegimeEnum? currentRegimeForBaseline,
        IWriteApplicationDbContext writeCtx,
        CancellationToken ct,
        CancellationToken runCt)
    {
        var parameterGrid = await _gridBuilder.BuildParameterGridAsync(db, strategy.StrategyType, runCt);
        var originalTpeBounds = OptimizationGridBuilder.ExtractTpeBounds(parameterGrid);
        var tpeBounds = originalTpeBounds.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        if (config.AdaptiveBoundsEnabled)
        {
            var historicalGood = await OptimizationGridBuilder.LoadHistoricalApprovedParamsAsync(db, strategy, runCt);
            if (historicalGood.Count >= 3)
                tpeBounds = OptimizationGridBuilder.NarrowBoundsFromHistory(tpeBounds, historicalGood);
        }

        if (config.AdaptiveBoundsEnabled)
        {
            var approvedRuns = await db.Set<OptimizationRun>()
                .Where(r => r.StrategyId == strategy.Id
                         && r.Status == OptimizationRunStatus.Approved
                         && r.BestParametersJson != null
                         && r.BaselineParametersJson != null
                         && !r.IsDeleted)
                .OrderByDescending(r => r.CompletedAt)
                .Take(10)
                .Select(r => new { r.BaselineParametersJson, r.BestParametersJson })
                .ToListAsync(runCt);

            if (approvedRuns.Count >= 3)
            {
                var allDeltas = approvedRuns
                    .Select(r => ParameterImportanceTracker.ComputeParameterDeltas(r.BaselineParametersJson, r.BestParametersJson))
                    .Where(d => d.Count > 0);
                var importance = ParameterImportanceTracker.AggregateImportance(allDeltas);

                if (importance.Count > 0)
                {
                    tpeBounds = global::LascodiaTradingEngine.Application.Optimization.OptimizationSearchCoordinator.ApplyImportanceGuidedBoundAdjustments(
                        tpeBounds,
                        originalTpeBounds,
                        importance);

                    _logger.LogDebug(
                        "OptimizationWorker: parameter importance applied for strategy {Id} - {Count} param(s) adjusted from {RunCount} prior approvals",
                        strategy.Id,
                        importance.Count,
                        approvedRuns.Count);
                }
            }
        }

        var previousParams = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategy.Id
                     && r.Status == OptimizationRunStatus.Approved
                     && r.BestParametersJson != null
                     && !r.IsDeleted)
            .Select(r => r.BestParametersJson!)
            .ToListAsync(runCt);
        var previousParamSet = new HashSet<string>(
            previousParams.Select(CanonicalParameterJson.Normalize),
            StringComparer.OrdinalIgnoreCase);

        var freshCandidates = parameterGrid
            .Where(p => !previousParamSet.Contains(CanonicalParameterJson.Serialize(p)))
            .ToList();

        if (freshCandidates.Count == 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: parameter space exhausted for strategy {Id} - expanding with midpoints",
                strategy.Id);
            freshCandidates = OptimizationGridBuilder.ExpandGridWithMidpoints(parameterGrid, previousParamSet);
        }

        bool useGp = tpeBounds.Count >= 6;
        bool useEhviEarly = config.UseEhviAcquisition;
        string surrogateKind = useEhviEarly ? "EHVI" : OptimizationSearchEngine.SelectSurrogate(tpeBounds.Count);
        int searchSeed = run.DeterministicSeed;
        string? optimizationRegimeText = currentRegimeForBaseline?.ToString();
        string currentDataWindowFingerprint = OptimizationRunMetadataService.ComputeDataWindowFingerprint(
            candles,
            trainCandles.Count,
            Math.Max(0, candles.Count - trainCandles.Count - embargoSize),
            embargoSize,
            currentRegimeForBaseline);

        var checkpoint = OptimizationCheckpointStore.Restore(run.IntermediateResultsJson, _logger);
        if (!OptimizationCheckpointStore.TryValidateCompatibility(
                checkpoint,
                currentDataWindowFingerprint,
                candles[0].Timestamp,
                candles[^1].Timestamp,
                candles.Count,
                trainCandles.Count,
                Math.Max(0, candles.Count - trainCandles.Count - embargoSize),
                optimizationRegimeText,
                out var checkpointMismatchReason,
                currentSurrogateKind: surrogateKind))
        {
            _logger.LogWarning(
                "OptimizationWorker: discarding incompatible checkpoint for run {RunId} - {Reason}",
                run.Id,
                checkpointMismatchReason);
            checkpoint = OptimizationCheckpointStore.Empty;
            run.IntermediateResultsJson = null;
            run.CheckpointVersion = 0;

            try
            {
                await writeCtx.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "OptimizationWorker: failed to clear incompatible checkpoint for run {RunId} (non-fatal)",
                    run.Id);
            }
        }

        TreeParzenEstimator? tpe = null;
        GaussianProcessSurrogate? gp = null;
        if (!useEhviEarly)
        {
            ulong? restoredRandomState = checkpoint.SurrogateKind == surrogateKind && checkpoint.SurrogateRandomState != 0
                ? checkpoint.SurrogateRandomState
                : null;

            if (useGp)
            {
                var keys = tpeBounds.Keys.ToArray();
                var lower = keys.Select(k => tpeBounds[k].Min).ToArray();
                var upper = keys.Select(k => tpeBounds[k].Max).ToArray();
                var isInt = keys.Select(k => tpeBounds[k].IsInteger).ToArray();
                gp = new GaussianProcessSurrogate(keys, lower, upper, isInt, beta: 2.0, seed: searchSeed, randomState: restoredRandomState);
                _logger.LogDebug("OptimizationWorker: using GP-UCB surrogate ({Dims} dimensions)", tpeBounds.Count);
            }
            else
            {
                tpe = new TreeParzenEstimator(tpeBounds, seed: searchSeed, randomState: restoredRandomState);
                _logger.LogDebug("OptimizationWorker: using TPE surrogate ({Dims} dimensions)", tpeBounds.Count);
            }
        }

        bool useParegoScalarization = config.UseParegoScalarization;
        bool useEhvi = useEhviEarly;
        if (useEhvi)
            useParegoScalarization = false;
        var paregoScalarizer = new ParegoScalarizer(searchSeed);
        EhviAcquisition? ehvi = null;
        if (useEhvi)
        {
            var ehviKeys = tpeBounds.Keys.ToArray();
            var ehviLower = ehviKeys.Select(k => tpeBounds[k].Min).ToArray();
            var ehviUpper = ehviKeys.Select(k => tpeBounds[k].Max).ToArray();
            var ehviIsInt = ehviKeys.Select(k => tpeBounds[k].IsInteger).ToArray();
            ehvi = new EhviAcquisition(ehviKeys, ehviLower, ehviUpper, ehviIsInt, seed: searchSeed, logger: _logger, metrics: _metrics);
            _logger.LogInformation("OptimizationWorker: using EHVI multi-objective acquisition ({Dims} dimensions)", tpeBounds.Count);
        }

        if (checkpoint.Observations.Count > 0)
        {
            int checkpointSeeded = 0;
            foreach (var obs in checkpoint.Observations)
            {
                if (obs.ParamsJson is null || obs.HealthScore <= 0)
                    continue;

                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(obs.ParamsJson);
                    if (dict is null)
                        continue;

                    var dblDict = new Dictionary<string, double>();
                    foreach (var (k, v) in dict)
                    {
                        if (v.TryGetDouble(out double d) && tpeBounds.ContainsKey(k))
                            dblDict[k] = d;
                    }

                    if (dblDict.Count != tpeBounds.Count)
                        continue;

                    bool seeded = false;
                    if (ehvi is not null)
                    {
                        if (obs.Result is not null)
                        {
                            ehvi.AddObservation(dblDict, obs.Result);
                            seeded = true;
                        }
                    }
                    else
                    {
                        double surrogateScore = global::LascodiaTradingEngine.Application.Optimization.OptimizationSearchCoordinator.GetCheckpointSurrogateObservationScore(
                            useParegoScalarization,
                            paregoScalarizer,
                            obs.Result,
                            obs.HealthScore);
                        if (tpe is not null)
                        {
                            tpe.AddObservation(dblDict, surrogateScore);
                            seeded = true;
                        }

                        if (gp is not null)
                        {
                            gp.AddObservation(dblDict, surrogateScore);
                            seeded = true;
                        }
                    }

                    if (seeded)
                        checkpointSeeded++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: skipping malformed checkpoint entry during surrogate seeding");
                }
            }

            if (checkpointSeeded > 0)
            {
                _logger.LogInformation(
                    "OptimizationWorker: resumed from checkpoint - seeded surrogate with {Count} prior evaluations",
                    checkpointSeeded);
                _metrics.OptimizationCheckpointRestored.Add(1);
            }
        }

        var priorRuns = await LoadPriorRunSeedsAsync(db, strategy, runCt);
        int warmStarted = await WarmStartSurrogatesAsync(
            priorRuns,
            tpeBounds,
            currentRegimeForBaseline,
            ehvi,
            tpe,
            gp,
            paregoScalarizer,
            useParegoScalarization);

        if (warmStarted > 0)
        {
            _logger.LogDebug("OptimizationWorker: warm-started surrogate with {Count} prior observations", warmStarted);
        }
        else if (priorRuns.Count > 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: warm-start yielded 0 observations despite {PriorCount} prior run(s) - likely parameter schema change (key count mismatch). Surrogate starting cold.",
                priorRuns.Count);
        }

        var allEvaluated = checkpoint.Observations
            .OrderBy(o => o.Sequence)
            .Select(o => new ScoredCandidate(
                o.ParamsJson,
                o.HealthScore,
                OptimizationCheckpointStore.ToCheckpointResult(o.Result),
                o.CvCoefficientOfVariation))
            .ToList();
        var seenParamSet = new ConcurrentDictionary<string, byte>(
            checkpoint.SeenParameterJson
                .Concat(checkpoint.Observations.Select(o => o.ParamsJson))
                .Select(CanonicalParameterJson.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, _ => (byte)0, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        return new SearchBootstrapState(
            parameterGrid,
            freshCandidates,
            tpeBounds,
            previousParamSet,
            tpe,
            gp,
            ehvi,
            paregoScalarizer,
            useGp,
            useParegoScalarization,
            surrogateKind,
            optimizationRegimeText,
            currentDataWindowFingerprint,
            warmStarted,
            priorRuns.Count,
            allEvaluated,
            seenParamSet,
            Math.Max(checkpoint.Iterations, allEvaluated.Count),
            checkpoint.SurrogateKind == surrogateKind ? checkpoint.StagnantBatches : 0,
            checkpoint.Observations.Count > 0);
    }

    private static async Task<List<PriorRunSeed>> LoadPriorRunSeedsAsync(
        DbContext db,
        Strategy strategy,
        CancellationToken runCt)
        => await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategy.Id
                     && r.Status == OptimizationRunStatus.Approved
                     && r.BestParametersJson != null
                     && r.BestHealthScore.HasValue
                     && !r.IsDeleted)
            .OrderByDescending(r => r.CompletedAt)
            .Take(10)
            .Select(r => new PriorRunSeed(
                r.BestParametersJson,
                r.BestHealthScore,
                r.BaselineParametersJson,
                r.BaselineHealthScore,
                r.CompletedAt,
                r.BestSharpeRatio,
                r.BestMaxDrawdownPct,
                r.BestWinRate,
                r.RunMetadataJson))
            .ToListAsync(runCt);

    private async Task<int> WarmStartSurrogatesAsync(
        IReadOnlyList<PriorRunSeed> priorRuns,
        IReadOnlyDictionary<string, (double Min, double Max, bool IsInteger)> tpeBounds,
        MarketRegimeEnum? currentRegimeForBaseline,
        EhviAcquisition? ehvi,
        TreeParzenEstimator? tpe,
        GaussianProcessSurrogate? gp,
        ParegoScalarizer paregoScalarizer,
        bool useParegoScalarization)
    {
        int warmStarted = 0;
        const double decayHalfLifeDays = 30.0;
        var nowUtcForDecay = DateTime.UtcNow;
        string? currentRegimeStr = currentRegimeForBaseline?.ToString();

        foreach (var prior in priorRuns)
        {
            if (currentRegimeStr is not null && prior.RunMetadataJson is not null)
            {
                try
                {
                    string? priorRegime = OptimizationRunMetadataService.ExtractOptimizationRegime(prior.RunMetadataJson);
                    if (priorRegime is not null && priorRegime != currentRegimeStr)
                    {
                        _logger.LogDebug(
                            "OptimizationWorker: skipping warm-start from prior run (regime mismatch: prior={Prior}, current={Current})",
                            priorRegime,
                            currentRegimeStr);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: skipping warm-start entry with malformed RunMetadataJson");
                }
            }

            double ageDays = prior.CompletedAt.HasValue
                ? (nowUtcForDecay - prior.CompletedAt.Value).TotalDays
                : 90.0;
            double decayFactor = Math.Pow(0.5, ageDays / decayHalfLifeDays);

            foreach (var (json, score) in new[] { (prior.BestParametersJson, prior.BestHealthScore), (prior.BaselineParametersJson, prior.BaselineHealthScore) })
            {
                if (json is null || !score.HasValue)
                    continue;

                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (dict is null)
                        continue;

                    var dblDict = new Dictionary<string, double>();
                    foreach (var (k, v) in dict)
                    {
                        if (v.TryGetDouble(out double d) && tpeBounds.ContainsKey(k))
                            dblDict[k] = d;
                    }

                    if (dblDict.Count != tpeBounds.Count)
                        continue;

                    if (ehvi is not null)
                    {
                        if (json == prior.BestParametersJson
                            && prior.BestSharpeRatio.HasValue
                            && prior.BestMaxDrawdownPct.HasValue
                            && prior.BestWinRate.HasValue)
                        {
                            ehvi.AddWarmStartObservation(
                                dblDict,
                                prior.BestSharpeRatio.Value,
                                prior.BestMaxDrawdownPct.Value,
                                prior.BestWinRate.Value,
                                decayFactor);
                            warmStarted++;
                        }

                        continue;
                    }

                    bool canWarmStart = global::LascodiaTradingEngine.Application.Optimization.OptimizationSearchCoordinator.TryGetWarmStartSurrogateObservationScore(
                        useParegoScalarization,
                        paregoScalarizer,
                        score,
                        json == prior.BestParametersJson ? prior.BestSharpeRatio : null,
                        json == prior.BestParametersJson ? prior.BestMaxDrawdownPct : null,
                        json == prior.BestParametersJson ? prior.BestWinRate : null,
                        decayFactor,
                        out double surrogateScore);
                    if (!canWarmStart)
                        continue;

                    if (tpe is not null)
                    {
                        tpe.AddObservation(dblDict, surrogateScore);
                        warmStarted++;
                    }

                    if (gp is not null)
                    {
                        gp.AddObservation(dblDict, surrogateScore);
                        warmStarted++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: skipping malformed prior run params during warm-start");
                }
            }
        }

        await Task.CompletedTask;
        return warmStarted;
    }
}
