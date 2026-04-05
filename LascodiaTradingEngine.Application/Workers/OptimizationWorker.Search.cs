using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lascodia.Trading.Engine.SharedApplication.Common.Interfaces;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

public partial class OptimizationWorker
{
    // ── Stage: Bayesian Search (Stages 5–6) ─────────────────────────────────

    internal static bool ShouldRunCoarseScreening(int candidateCount, int trainCandleCount, int coarsePhaseThreshold)
        => candidateCount >= Math.Max(2, coarsePhaseThreshold)
        && trainCandleCount >= 100;

    internal static bool ShouldContinueCoarseScreening(int candidateCount, int coarsePhaseThreshold)
        => candidateCount >= Math.Max(2, coarsePhaseThreshold);

    internal static Dictionary<string, (double Min, double Max, bool IsInteger)> ApplyImportanceGuidedBoundAdjustments(
        IReadOnlyDictionary<string, (double Min, double Max, bool IsInteger)> currentBounds,
        IReadOnlyDictionary<string, (double Min, double Max, bool IsInteger)> outerBounds,
        IReadOnlyDictionary<string, double> importance)
    {
        var adjusted = currentBounds.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var (param, imp) in importance)
        {
            if (!adjusted.TryGetValue(param, out var bounds)) continue;
            if (!outerBounds.TryGetValue(param, out var outer)) continue;

            double range = bounds.Max - bounds.Min;
            if (range <= 0) continue;

            double center = (bounds.Max + bounds.Min) / 2.0;
            double adjustFactor = imp > 0.5 ? 1.0 - 0.20 * (imp - 0.5) / 0.5
                                : imp < 0.2 ? 1.0 + 0.10 * (0.2 - imp) / 0.2
                                : 1.0;

            double newHalfRange = range / 2.0 * adjustFactor;
            adjusted[param] = (
                Math.Max(outer.Min, center - newHalfRange),
                Math.Min(outer.Max, center + newHalfRange),
                bounds.IsInteger);
        }

        return adjusted;
    }

    internal static double GetCheckpointSurrogateObservationScore(
        bool useParegoScalarization,
        ParegoScalarizer paregoScalarizer,
        BacktestResult? result,
        decimal healthScore)
        => useParegoScalarization
            ? result is not null
                ? paregoScalarizer.Scalarize(result)
                : (double)healthScore
            : (double)healthScore;

    internal static bool TryGetWarmStartSurrogateObservationScore(
        bool useParegoScalarization,
        ParegoScalarizer paregoScalarizer,
        decimal? healthScore,
        decimal? sharpeRatio,
        decimal? maxDrawdownPct,
        decimal? winRate,
        double decayFactor,
        out double surrogateScore)
    {
        if (useParegoScalarization)
        {
            if (!sharpeRatio.HasValue || !maxDrawdownPct.HasValue || !winRate.HasValue)
            {
                surrogateScore = 0;
                return false;
            }

            surrogateScore = paregoScalarizer.Scalarize(
                sharpeRatio.Value,
                maxDrawdownPct.Value,
                winRate.Value) * decayFactor;
            return true;
        }

        if (!healthScore.HasValue)
        {
            surrogateScore = 0;
            return false;
        }

        const double decayMidpoint = 0.5;
        surrogateScore = decayMidpoint + ((double)healthScore.Value - decayMidpoint) * decayFactor;
        return true;
    }

    internal static List<Dictionary<string, double>> SuggestInitialCandidates(
        TreeParzenEstimator? tpe,
        GaussianProcessSurrogate? gp,
        EhviAcquisition? ehvi,
        int count)
    {
        if (ehvi is not null)
            return ehvi.SuggestCandidates(count);

        if (tpe is not null)
            return tpe.SuggestCandidates(count);

        if (gp is not null)
            return gp.SuggestCandidates(count);

        throw new InvalidOperationException("OptimizationWorker: no surrogate available for initial candidate suggestions.");
    }

    internal static int UpdateConsecutiveFailureStreak(int currentStreak, int successfulEvaluations, int failedEvaluations)
    {
        if (failedEvaluations <= 0)
            return successfulEvaluations > 0 ? 0 : currentStreak;

        return successfulEvaluations > 0 ? 0 : currentStreak + failedEvaluations;
    }

    /// <summary>
    /// Builds the parameter grid, configures the TPE/GP surrogate, warm-starts from
    /// prior runs, runs successive halving + seed phase + surrogate-guided exploration,
    /// and returns the evaluated candidates.
    /// </summary>
    internal async Task<SearchResult> RunBayesianSearchAsync(
        DbContext db, OptimizationRun run, Strategy strategy, OptimizationConfig config,
        List<Candle> trainCandles, List<Candle> candles, BacktestOptions screeningOptions,
        OptimizationGridBuilder.DataProtocol protocol, int embargoSize,
        MarketRegimeEnum? currentRegimeForBaseline,
        IWriteApplicationDbContext writeCtx, CancellationToken ct, CancellationToken runCt)
    {
        _validator.SetInitialBalance(config.ScreeningInitialBalance);
        var parameterGrid = await _gridBuilder.BuildParameterGridAsync(db, strategy.StrategyType, runCt);
        var originalTpeBounds = OptimizationGridBuilder.ExtractTpeBounds(parameterGrid);
        var tpeBounds = originalTpeBounds.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value,
            StringComparer.OrdinalIgnoreCase);

        // Adaptive bounds: narrow based on historically approved params
        if (config.AdaptiveBoundsEnabled)
        {
            var historicalGood = await OptimizationGridBuilder.LoadHistoricalApprovedParamsAsync(db, strategy, runCt);
            if (historicalGood.Count >= 3)
                tpeBounds = OptimizationGridBuilder.NarrowBoundsFromHistory(tpeBounds, historicalGood);
        }

        // Parameter importance-guided bound tightening: compute which parameters
        // changed most across prior approved runs, then concentrate sampling on those
        // dimensions by narrowing their bounds proportionally. Low-importance params
        // keep wide bounds for exploration.
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
                    tpeBounds = ApplyImportanceGuidedBoundAdjustments(tpeBounds, originalTpeBounds, importance);

                    _logger.LogDebug(
                        "OptimizationWorker: parameter importance applied for strategy {Id} — {Count} param(s) adjusted from {RunCount} prior approvals",
                        strategy.Id, importance.Count, approvedRuns.Count);
                }
            }
        }

        // Exclude previously-promoted parameters (parameter memory)
        var previousParams = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategy.Id
                     && r.Status == OptimizationRunStatus.Approved
                     && r.BestParametersJson != null && !r.IsDeleted)
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
                "OptimizationWorker: parameter space exhausted for strategy {Id} — expanding with midpoints",
                strategy.Id);
            freshCandidates = OptimizationGridBuilder.ExpandGridWithMidpoints(parameterGrid, previousParamSet);
        }

        if (freshCandidates.Count == 0)
        {
            run.BestParametersJson = CanonicalParameterJson.Normalize(strategy.ParametersJson);
            run.BestHealthScore    = run.BaselineHealthScore;
            run.Iterations         = 0;

            _logger.LogWarning(
                "OptimizationWorker: parameter space exhausted for strategy {Id} — no fresh candidates after expansion",
                strategy.Id);
            _metrics.OptimizationParameterSpaceExhausted.Add(1);
            return new SearchResult([], 0, "N/A", 0, false);
        }

        // ── Stage 6: Bayesian search with purged K-fold ────────────
        // Load multi-objective acquisition config early so surrogate creation can branch on it.
        bool useEhviEarly = config.UseEhviAcquisition;

        // Select surrogate: EHVI (3 independent GPs) when enabled, otherwise
        // TPE for low-dimensional (< 6 params) or GP-UCB for higher.
        bool useGp = tpeBounds.Count >= 6;
        string surrogateKind = useEhviEarly ? "EHVI" : useGp ? "GP-UCB" : "TPE";
        int searchSeed = run.DeterministicSeed;
        var checkpoint = RestoreCheckpoint(run.IntermediateResultsJson);
        TreeParzenEstimator? tpe = null;
        GaussianProcessSurrogate? gp = null;

        // When EHVI is active, it manages its own 3 GPs — skip TPE/GP creation.
        if (!useEhviEarly)
        {
            ulong? restoredRandomState = checkpoint.SurrogateKind == surrogateKind && checkpoint.SurrogateRandomState != 0
                ? checkpoint.SurrogateRandomState
                : null;

            if (useGp)
            {
                var keys   = tpeBounds.Keys.ToArray();
                var lower  = keys.Select(k => tpeBounds[k].Min).ToArray();
                var upper  = keys.Select(k => tpeBounds[k].Max).ToArray();
                var isInt  = keys.Select(k => tpeBounds[k].IsInteger).ToArray();
                gp = new GaussianProcessSurrogate(keys, lower, upper, isInt,
                    beta: 2.0, seed: searchSeed, randomState: restoredRandomState);

                _logger.LogDebug("OptimizationWorker: using GP-UCB surrogate ({Dims} dimensions)", tpeBounds.Count);
            }
            else
            {
                tpe = new TreeParzenEstimator(tpeBounds,
                    seed: searchSeed,
                    randomState: restoredRandomState);

                _logger.LogDebug("OptimizationWorker: using TPE surrogate ({Dims} dimensions)", tpeBounds.Count);
            }
        }

        // Multi-objective acquisition strategy: ParEGO (lightweight) or EHVI (3 independent
        // GPs, more compute but better Pareto coverage). Both opt-in, both default off.
        // When neither is enabled, the surrogate optimizes the fixed health score composite.
        // Declared here (before checkpoint resume) so checkpoint observations can feed EHVI.
        bool useParegoScalarization = config.UseParegoScalarization;
        bool useEhvi = useEhviEarly; // Already loaded above for surrogate selection
        if (useEhvi) useParegoScalarization = false;
        var paregoScalarizer = new ParegoScalarizer(searchSeed);
        EhviAcquisition? ehvi = null;
        if (useEhvi)
        {
            var ehviKeys  = tpeBounds.Keys.ToArray();
            var ehviLower = ehviKeys.Select(k => tpeBounds[k].Min).ToArray();
            var ehviUpper = ehviKeys.Select(k => tpeBounds[k].Max).ToArray();
            var ehviIsInt = ehviKeys.Select(k => tpeBounds[k].IsInteger).ToArray();
            ehvi = new EhviAcquisition(ehviKeys, ehviLower, ehviUpper, ehviIsInt, seed: searchSeed,
                logger: _logger, metrics: _metrics);
            _logger.LogInformation("OptimizationWorker: using EHVI multi-objective acquisition ({Dims} dimensions)", tpeBounds.Count);
        }

        // Checkpoint-aware resume: feed previously evaluated candidates back into the
        // surrogate model so it doesn't re-explore already-evaluated parameter regions.
        if (checkpoint.Observations.Count > 0)
        {
            int checkpointSeeded = 0;
            foreach (var obs in checkpoint.Observations)
            {
                if (obs.ParamsJson is null || obs.HealthScore <= 0) continue;
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(obs.ParamsJson);
                    if (dict is null) continue;
                    var dblDict = new Dictionary<string, double>();
                    foreach (var (k, v) in dict)
                        if (v.TryGetDouble(out double d) && tpeBounds.ContainsKey(k)) dblDict[k] = d;
                    if (dblDict.Count != tpeBounds.Count) continue;

                    bool seeded = false;
                    if (ehvi is not null)
                    {
                        if (obs.Result is not null)
                        {
                            ehvi.AddObservation(dblDict, obs.Result);
                            seeded = true;
                        }
                        // EHVI requires BacktestResult for 3-objective decomposition.
                        // Checkpoint entries with stripped trade lists (Result=null) can't
                        // be used — the health score alone doesn't tell EHVI the individual
                        // Sharpe/DD/WR values. These entries are skipped silently.
                    }
                    else
                    {
                        double surrogateScore = GetCheckpointSurrogateObservationScore(
                            useParegoScalarization,
                            paregoScalarizer,
                            obs.Result,
                            obs.HealthScore);
                        if (tpe is not null) { tpe.AddObservation(dblDict, surrogateScore); seeded = true; }
                        if (gp is not null) { gp.AddObservation(dblDict, surrogateScore); seeded = true; }
                    }
                    if (seeded) checkpointSeeded++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "OptimizationWorker: skipping malformed checkpoint entry during surrogate seeding"); }
            }

            if (checkpointSeeded > 0)
            {
                _logger.LogInformation(
                    "OptimizationWorker: resumed from checkpoint — seeded surrogate with {Count} prior evaluations",
                    checkpointSeeded);
                _metrics.OptimizationCheckpointRestored.Add(1);
            }
        }

        // Warm-start: seed the surrogate with observations from recent completed/approved
        // runs for this strategy. Observations are weighted by recency: scores from older
        // runs are decayed toward the midpoint (0.5) to reduce their influence as market
        // conditions change. Half-life of 30 days: 30d→50% decay, 60d→75%, 90d→87.5%.
        var priorRuns = await db.Set<OptimizationRun>()
            .Where(r => r.StrategyId == strategy.Id
                     && r.Status == OptimizationRunStatus.Approved
                     && r.BestParametersJson != null && r.BestHealthScore.HasValue
                     && !r.IsDeleted)
            .OrderByDescending(r => r.CompletedAt)
            .Take(10)
            .Select(r => new { r.BestParametersJson, r.BestHealthScore, r.BaselineParametersJson, r.BaselineHealthScore, r.CompletedAt, r.BestSharpeRatio, r.BestMaxDrawdownPct, r.BestWinRate, r.RunMetadataJson })
            .ToListAsync(runCt);

        int warmStarted = 0;
        const double decayHalfLifeDays = 30.0;
        var nowUtcForDecay = DateTime.UtcNow;

        // Extract the current regime string for warm-start regime filtering
        string? currentRegimeStr = currentRegimeForBaseline?.ToString();

        foreach (var prior in priorRuns)
        {
            // Regime filter: skip warm-start candidates from a different market regime.
            // The regime is stored in RunMetadataJson.CurrentRegime. If the prior run's
            // regime differs from the current regime, its parameters are less relevant.
            if (currentRegimeStr is not null && prior.RunMetadataJson is not null)
            {
                try
                {
                    using var metaDoc = JsonDocument.Parse(prior.RunMetadataJson);
                    if (metaDoc.RootElement.TryGetProperty("CurrentRegime", out var regimeEl))
                    {
                        string? priorRegime = regimeEl.GetString();
                        if (priorRegime is not null && priorRegime != currentRegimeStr)
                        {
                            _logger.LogDebug(
                                "OptimizationWorker: skipping warm-start from prior run (regime mismatch: prior={Prior}, current={Current})",
                                priorRegime, currentRegimeStr);
                            continue;
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "OptimizationWorker: skipping warm-start entry with malformed RunMetadataJson"); }
            }

            double ageDays = prior.CompletedAt.HasValue
                ? (nowUtcForDecay - prior.CompletedAt.Value).TotalDays
                : 90.0;
            double decayFactor = Math.Pow(0.5, ageDays / decayHalfLifeDays);

            // Seed both the best and baseline from each prior run (2 observations per run)
            foreach (var (json, score) in new[] { (prior.BestParametersJson, prior.BestHealthScore), (prior.BaselineParametersJson, prior.BaselineHealthScore) })
            {
                if (json is null || !score.HasValue) continue;
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (dict is null) continue;
                    var dblDict = new Dictionary<string, double>();
                    foreach (var (k, v) in dict)
                        if (v.TryGetDouble(out double d) && tpeBounds.ContainsKey(k)) dblDict[k] = d;
                    if (dblDict.Count != tpeBounds.Count) continue; // Skip if param shape changed

                    if (ehvi is not null)
                    {
                        // EHVI warm-start: use per-objective fields when available (best params only,
                        // not baseline — baseline doesn't have per-objective decomposition).
                        if (json == prior.BestParametersJson
                            && prior.BestSharpeRatio.HasValue && prior.BestMaxDrawdownPct.HasValue && prior.BestWinRate.HasValue)
                        {
                            ehvi.AddWarmStartObservation(dblDict,
                                prior.BestSharpeRatio.Value, prior.BestMaxDrawdownPct.Value, prior.BestWinRate.Value,
                                decayFactor);
                            warmStarted++;
                        }
                        // Baseline params can't be warm-started into EHVI (no per-objective decomposition).
                        // This is expected — EHVI gets fewer warm-start points than TPE/GP.
                        continue;
                    }

                    bool canWarmStart = TryGetWarmStartSurrogateObservationScore(
                        useParegoScalarization,
                        paregoScalarizer,
                        score,
                        json == prior.BestParametersJson ? prior.BestSharpeRatio : null,
                        json == prior.BestParametersJson ? prior.BestMaxDrawdownPct : null,
                        json == prior.BestParametersJson ? prior.BestWinRate : null,
                        decayFactor,
                        out double surrogateScore);
                    if (!canWarmStart) continue;

                    if (tpe is not null) { tpe.AddObservation(dblDict, surrogateScore); warmStarted++; }
                    if (gp is not null) { gp.AddObservation(dblDict, surrogateScore); warmStarted++; }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "OptimizationWorker: skipping malformed prior run params during warm-start"); }
            }
        }

        if (warmStarted > 0)
        {
            _logger.LogDebug("OptimizationWorker: warm-started surrogate with {Count} prior observations", warmStarted);
        }
        else if (priorRuns.Count > 0)
        {
            _logger.LogWarning(
                "OptimizationWorker: warm-start yielded 0 observations despite {PriorCount} prior run(s) — " +
                "likely parameter schema change (key count mismatch). Surrogate starting cold.",
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
        var _evalLock = new object();
        var seenParamSet = new ConcurrentDictionary<string, byte>(
            checkpoint.SeenParameterJson
                .Concat(checkpoint.Observations.Select(o => o.ParamsJson))
                .Select(CanonicalParameterJson.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, _ => (byte)0, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        int totalIters     = Math.Max(checkpoint.Iterations, allEvaluated.Count);
        int consecutiveFailures = 0;
        int embargoPerFold = Math.Max(1, embargoSize / protocol.KFolds);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = config.MaxParallelBacktests,
            CancellationToken      = runCt,
        };

        // Phase 0 (multi-fidelity screening): Hyperband or legacy successive halving.
        // Hyperband runs multiple successive halving brackets simultaneously with different
        // aggressiveness levels, eliminating the need to guess the right fidelity rungs.
        // Falls back to the single-bracket SuccessiveHalvingRungs path when disabled.
        if (ShouldRunCoarseScreening(freshCandidates.Count, trainCandles.Count, config.CoarsePhaseThreshold))
        {
            decimal coarseBaseline = run.BaselineHealthScore ?? 0m;

            if (config.HyperbandEnabled)
            {
                double minFidelity = HyperbandScheduler.ComputeMinFidelity(trainCandles.Count);
                var brackets = HyperbandScheduler.ComputeBrackets(
                    config.HyperbandEta, 1.0, minFidelity,
                    budgetPerBracket: Math.Max(3, freshCandidates.Count / Math.Max(1, (int)Math.Log(1.0 / minFidelity, config.HyperbandEta) + 1)));

                if (brackets.Count > 1)
                {
                    _logger.LogInformation(
                        "OptimizationWorker: Hyperband screening with {Brackets} brackets, eta={Eta}, " +
                        "min fidelity={MinF:P1}, budget={Budget} candidates",
                        brackets.Count, config.HyperbandEta, minFidelity, freshCandidates.Count);

                    // Serialize freshCandidates to JSON once for the candidate source
                    var freshCandidateJsons = freshCandidates
                        .Select(CanonicalParameterJson.Serialize)
                        .ToList();

                    // Candidate source: distribute fresh candidates across brackets using
                    // deterministic slicing. Each bracket gets a non-overlapping slice of
                    // the grid. If a bracket needs more than its slice, overflow draws from
                    // the full grid pool (may overlap other brackets' slices).
                    var bracketCount = brackets.Count;
                    List<string> CandidateSource(int requestedCount, int bracketIndex)
                    {
                        // Slice the grid: bracket 0 gets first 1/N, bracket 1 gets second 1/N, etc.
                        int sliceStart = bracketIndex * freshCandidateJsons.Count / bracketCount;
                        int sliceEnd = (bracketIndex + 1) * freshCandidateJsons.Count / bracketCount;
                        var slice = freshCandidateJsons.GetRange(sliceStart, sliceEnd - sliceStart);

                        if (slice.Count >= requestedCount)
                            return slice.Take(requestedCount).ToList();

                        // Not enough in the slice — add from the full pool (may overlap other brackets)
                        var result = new List<string>(slice);
                        var seen = new HashSet<string>(slice);
                        foreach (var c in freshCandidateJsons)
                        {
                            if (result.Count >= requestedCount) break;
                            if (seen.Add(c)) result.Add(c);
                        }
                        return result;
                    }

                    var hyperband = new HyperbandScheduler(_logger, _metrics);
                    var hbResult = await hyperband.ExecuteAllBracketsAsync(
                        brackets, CandidateSource, trainCandles, strategy, screeningOptions,
                        _validator, coarseBaseline, config.MaxParallelBacktests,
                        config.ScreeningTimeoutSeconds, config.CircuitBreakerThreshold,
                        freshCandidates.Count, protocol.KFolds, embargoPerFold,
                        config.MinCandidateTrades, runCt);

                    totalIters += hbResult.TotalEvaluations;

                    if (hbResult.Survivors.Count > 0)
                    {
                        // Pool survivors from all brackets, dedup, take top N
                        var pooled = HyperbandScheduler.PoolSurvivors(
                            hbResult.Survivors, config.TpeInitialSamples * 2);

                        // Feed full-fidelity Hyperband survivors directly into the evaluated list
                        // (they've already been screened at the highest fidelity in their bracket)
                        foreach (var s in pooled.Where(s => s.EvaluatedAtFidelity >= 0.99))
                        {
                            var paramsJson = CanonicalParameterJson.Normalize(s.ParamsJson);
                            seenParamSet.TryAdd(paramsJson, 0);
                            lock (_evalLock) allEvaluated.Add(new ScoredCandidate(
                                paramsJson, s.HealthScore, s.Result, s.CvCoefficientOfVariation));
                        }

                        // Replace freshCandidates with pooled survivors for LHS seeding
                        var survivorParamJsons = new HashSet<string>(
                            pooled.Select(s => CanonicalParameterJson.Normalize(s.ParamsJson)),
                            StringComparer.OrdinalIgnoreCase);
                        freshCandidates = freshCandidates
                            .Where(p => survivorParamJsons.Contains(CanonicalParameterJson.Serialize(p)))
                            .ToList();

                        // If Hyperband pruned everything, fall back to original candidates
                        if (freshCandidates.Count == 0)
                            freshCandidates = parameterGrid
                                .Where(p => !previousParamSet.Contains(CanonicalParameterJson.Serialize(p)))
                                .ToList();

                        _logger.LogInformation(
                            "OptimizationWorker: Hyperband completed — {Evals} evals across {Brackets} brackets " +
                            "({Skipped} skipped), {Survivors} unique survivors",
                            hbResult.TotalEvaluations, hbResult.BracketsExecuted,
                            hbResult.BracketsSkipped, pooled.Count);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "OptimizationWorker: Hyperband produced no survivors — " +
                            "proceeding with full candidate set for LHS seeding");
                    }
                }
                else
                {
                    // Only 1 bracket computed (scarce data) — use legacy path
                    _logger.LogDebug(
                        "OptimizationWorker: Hyperband computed only 1 bracket (scarce data) — " +
                        "falling back to legacy successive halving");
                    await RunLegacySuccessiveHalvingAsync();
                }
            }
            else
            {
                await RunLegacySuccessiveHalvingAsync();
            }

            async Task RunLegacySuccessiveHalvingAsync()
            {
                // Legacy single-bracket successive halving (SuccessiveHalvingRungs config)
                var fidelityRungs = ParseFidelityRungs(config.SuccessiveHalvingRungs);
                int rungIndex = 0;

                foreach (var fidelity in fidelityRungs)
                {
                    rungIndex++;
                    if (!ShouldContinueCoarseScreening(freshCandidates.Count, config.CoarsePhaseThreshold)) break;

                    int step = Math.Max(1, (int)Math.Round(1.0 / fidelity));
                    var downsampledTrain = trainCandles.Where((_, i) => i % step == 0).ToList();
                    if (downsampledTrain.Count < 30) break;

                    int rungTimeout = Math.Max(5, (int)(config.ScreeningTimeoutSeconds * fidelity));
                    var coarseScores = new List<(int Index, decimal Score)>();
                    var coarseLock   = new object();

                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, freshCandidates.Count),
                        parallelOptions,
                        async (idx, pCt) =>
                        {
                            var paramsJson = CanonicalParameterJson.Serialize(freshCandidates[idx]);
                            try
                            {
                                var result = await _validator.RunWithTimeoutAsync(
                                    strategy, paramsJson, downsampledTrain, screeningOptions,
                                    rungTimeout, pCt);
                                lock (coarseLock) coarseScores.Add((idx, OptimizationHealthScorer.ComputeHealthScore(result)));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "OptimizationWorker: successive halving backtest failed for candidate {Idx}", idx);
                                lock (coarseLock) coarseScores.Add((idx, -1m));
                            }
                        });

                    double noiseTolerance = 0.90 + 0.05 * fidelity;
                    var survivors = coarseScores
                        .Where(c => c.Score >= coarseBaseline * (decimal)noiseTolerance)
                        .OrderByDescending(c => c.Score)
                        .ToList();

                    if (survivors.Count > freshCandidates.Count / 2)
                        survivors = survivors.Take(freshCandidates.Count / 2).ToList();

                    if (survivors.Count > 0)
                    {
                        int pruned = freshCandidates.Count - survivors.Count;
                        var survivorSet = new HashSet<int>(survivors.Select(s => s.Index));
                        freshCandidates = freshCandidates.Where((_, i) => survivorSet.Contains(i)).ToList();

                        _logger.LogDebug(
                            "OptimizationWorker: successive halving rung {Rung}/{Total} (fidelity={Fidelity:P0}) " +
                            "pruned {Pruned}/{Candidates} candidates",
                            rungIndex, fidelityRungs.Length, fidelity, pruned, pruned + survivors.Count);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "OptimizationWorker: successive halving rung {Rung} produced no survivors — " +
                            "stopping halving and proceeding with current {Count} candidates",
                            rungIndex, freshCandidates.Count);
                        break;
                    }
                }
            }
        }

        // (ParEGO/EHVI config and scalarizer initialized above, before checkpoint resume.)

        // Phase 1: Seed surrogate with Latin Hypercube Sampling for better space coverage.
        // The surrogate's SuggestCandidates falls back to LHS when no observations exist,
        // giving superior initial coverage compared to random grid shuffling.
        var lhsSuggestions = SuggestInitialCandidates(tpe, gp, ehvi, config.TpeInitialSamples);

        var seedCandidates = lhsSuggestions
            .Select(s => OptimizationGridBuilder.DoublesToParamSet(s, tpeBounds))
            .Where(p =>
            {
                var paramsJson = CanonicalParameterJson.Serialize(p);
                return !previousParamSet.Contains(paramsJson) && !seenParamSet.ContainsKey(paramsJson);
            })
            .ToList();

        // If LHS produced fewer fresh candidates than available grid points, backfill from grid
        if (seedCandidates.Count < config.TpeInitialSamples && freshCandidates.Count > seedCandidates.Count)
        {
            var seedSet = new HashSet<string>(
                seedCandidates.Select(CanonicalParameterJson.Serialize),
                StringComparer.OrdinalIgnoreCase);
            var backfill = freshCandidates
                .Where(p =>
                {
                    var paramsJson = CanonicalParameterJson.Serialize(p);
                    return !seedSet.Contains(paramsJson) && !seenParamSet.ContainsKey(paramsJson);
                })
                .Take(config.TpeInitialSamples - seedCandidates.Count);
            seedCandidates.AddRange(backfill);
        }

        int seedSuccesses = 0;
        int seedFailures = 0;
        await Parallel.ForEachAsync(seedCandidates, parallelOptions, async (paramSet, pCt) =>
        {
            if (Volatile.Read(ref consecutiveFailures) >= config.CircuitBreakerThreshold) return;

            var paramsJson = CanonicalParameterJson.Serialize(paramSet);
            if (previousParamSet.Contains(paramsJson) || !seenParamSet.TryAdd(paramsJson, 0))
                return;

            try
            {
                var (score, result, _cvValue) = await _validator.TemporalChunkedEvaluateAsync(
                    strategy, paramsJson, trainCandles, screeningOptions,
                    config.ScreeningTimeoutSeconds, protocol.KFolds, embargoPerFold,
                    config.MinCandidateTrades, pCt);
                Interlocked.Increment(ref totalIters);
                Interlocked.Increment(ref seedSuccesses);
                _metrics.OptimizationCandidatesScreened.Add(1);
                lock (_evalLock) allEvaluated.Add(new ScoredCandidate(paramsJson, score, result, _cvValue));

                var dblParams = OptimizationGridBuilder.ParamSetToDoubles(paramSet);
                // Use ParEGO scalarization for surrogate input when enabled,
                // otherwise fall back to health score. Seed phase uses initial
                // uniform weights (1/3 each) — equivalent to equal-weighted Chebyshev.
                double surrogateInput = useParegoScalarization
                    ? paregoScalarizer.Scalarize(result)
                    : (double)score;
                if (ehvi is null)
                {
                    if (tpe is not null) lock (tpe) tpe.AddObservation(dblParams, surrogateInput);
                    if (gp is not null) lock (gp) gp.AddObservation(dblParams, surrogateInput);
                }
                else
                {
                    lock (ehvi) ehvi.AddObservation(dblParams, result);
                }
            }
            catch (OperationCanceledException) when (pCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!pCt.IsCancellationRequested)
            {
                Interlocked.Increment(ref totalIters);
                Interlocked.Increment(ref seedFailures);
            }
            catch
            {
                Interlocked.Increment(ref totalIters);
                Interlocked.Increment(ref seedFailures);
            }
        });

        consecutiveFailures = UpdateConsecutiveFailureStreak(
            consecutiveFailures,
            Volatile.Read(ref seedSuccesses),
            Volatile.Read(ref seedFailures));

        if (consecutiveFailures >= config.CircuitBreakerThreshold)
        {
            _logger.LogWarning(
                "OptimizationWorker: circuit breaker tripped after {Failures} consecutive backtest failures in seed phase — aborting search",
                consecutiveFailures);
            _metrics.OptimizationCircuitBreakerTrips.Add(1);
            return new SearchResult(allEvaluated, totalIters, surrogateKind, warmStarted, checkpoint.Observations.Count > 0);
        }

        await HeartbeatRunAsync(run, writeCtx, ct);
        // Adaptive TPE budget: reduce budget for strategies that historically converge
        // quickly. Track the average early-stop savings ratio from prior runs.
        int effectiveBudget = config.TpeBudget;
        if (priorRuns.Count >= 3)
        {
            var priorIterations = await db.Set<OptimizationRun>()
                .Where(r => r.StrategyId == strategy.Id
                         && (r.Status == OptimizationRunStatus.Completed || r.Status == OptimizationRunStatus.Approved)
                         && r.Iterations > 0
                         && !r.IsDeleted)
                .OrderByDescending(r => r.CompletedAt)
                .Take(5)
                .Select(r => r.Iterations)
                .ToListAsync(runCt);

            if (priorIterations.Count >= 3)
            {
                int avgPriorIters = (int)priorIterations.Average();
                // If prior runs consistently converge with fewer iterations, shrink the budget
                // but never below 60% of configured or the average + 20% headroom.
                int adaptedBudget = Math.Max(
                    (int)(config.TpeBudget * 0.60),
                    (int)(avgPriorIters * 1.20));
                if (adaptedBudget < effectiveBudget)
                {
                    _logger.LogDebug(
                        "OptimizationWorker: adaptive TPE budget for strategy {Id}: {Adapted} (from {Original}, avg prior={Avg})",
                        strategy.Id, adaptedBudget, config.TpeBudget, avgPriorIters);
                    effectiveBudget = adaptedBudget;
                }
            }
        }

        // Phase 2: Surrogate-guided exploration rounds with early stopping.
        // ParEGO: each batch uses a different random weight vector on the (Sharpe, -DD, WR)
        // GP-UCB has exploration phases where scores intentionally dip as the
        // acquisition function probes uncertain regions, so it needs more patience.
        int remaining = Math.Max(0, effectiveBudget - totalIters);
        decimal bestSeenScore = allEvaluated.Count == 0 ? 0m : allEvaluated.Max(c => c.HealthScore);
        int stagnantBatches = checkpoint.SurrogateKind == surrogateKind
            ? checkpoint.StagnantBatches
            : 0;
        int maxStagnantBatches = useGp ? config.GpEarlyStopPatience : 2;

        while (remaining > 0 && !runCt.IsCancellationRequested)
        {
            // Circuit breaker: abort exploration if backtests are systematically failing
            if (consecutiveFailures >= config.CircuitBreakerThreshold)
            {
                _logger.LogWarning(
                    "OptimizationWorker: circuit breaker tripped after {Failures} consecutive backtest failures in exploration phase — aborting search ({Remaining} evals remaining)",
                    consecutiveFailures, remaining);
                _metrics.OptimizationCircuitBreakerTrips.Add(1);
                break;
            }

            int batch = Math.Min(config.MaxParallelBacktests, remaining);

            // ParEGO: rotate weight vector so this batch explores a different Pareto direction.
            // Capture the weights before the parallel loop to avoid cross-thread volatile reads.
            double[]? batchWeights = null;
            if (useParegoScalarization)
                batchWeights = paregoScalarizer.RotateWeights();

            List<Dictionary<string, double>> suggestions;
            if (ehvi is not null)
                lock (ehvi) suggestions = ehvi.SuggestCandidates(batch);
            else if (tpe is not null)
                lock (tpe) suggestions = tpe.SuggestCandidates(batch);
            else
                lock (gp!) suggestions = gp!.SuggestCandidates(batch);

            long batchBestScoreBits = 0L; // Atomic-friendly storage for decimal via double bits
            int batchSpent = 0;
            int batchSuccesses = 0;
            int batchFailures = 0;
            await Parallel.ForEachAsync(suggestions, parallelOptions, async (suggestion, pCt) =>
            {
                if (Volatile.Read(ref consecutiveFailures) >= config.CircuitBreakerThreshold) return;

                var paramSet   = OptimizationGridBuilder.DoublesToParamSet(suggestion, tpeBounds);
                var paramsJson = CanonicalParameterJson.Serialize(paramSet);
                if (previousParamSet.Contains(paramsJson) || !seenParamSet.TryAdd(paramsJson, 0)) return;
                Interlocked.Increment(ref batchSpent);

                try
                {
                    var (score, result, _cvVal) = await _validator.TemporalChunkedEvaluateAsync(
                        strategy, paramsJson, trainCandles, screeningOptions,
                        config.ScreeningTimeoutSeconds, protocol.KFolds, embargoPerFold,
                        config.MinCandidateTrades, pCt);
                    Interlocked.Increment(ref totalIters);
                    Interlocked.Increment(ref batchSuccesses);
                    _metrics.OptimizationCandidatesScreened.Add(1);
                    // Record the standard health score on the candidate (for downstream ranking)
                    lock (_evalLock) allEvaluated.Add(new ScoredCandidate(paramsJson, score, result, _cvVal));
                    // Feed observation to the active surrogate strategy:
                    // EHVI: record on 3 independent GPs (handles its own Pareto tracking)
                    // ParEGO: rotating Chebyshev scalar fed to single GP/TPE
                    // Default: health score fed to single GP/TPE
                    if (ehvi is not null)
                    {
                        lock (ehvi) ehvi.AddObservation(suggestion, result);
                    }
                    else
                    {
                        double surrogateScore = batchWeights is not null
                            ? paregoScalarizer.Scalarize(result, batchWeights)
                            : (double)score;
                        if (tpe is not null) lock (tpe) tpe.AddObservation(suggestion, surrogateScore);
                        if (gp is not null) lock (gp) gp.AddObservation(suggestion, surrogateScore);
                    }

                    // Thread-safe update of batch best using Interlocked on double bits
                    long scoreBits = BitConverter.DoubleToInt64Bits((double)score);
                    long current;
                do { current = Interlocked.Read(ref batchBestScoreBits); }
                    while (scoreBits > current && Interlocked.CompareExchange(ref batchBestScoreBits, scoreBits, current) != current);
                }
                catch (OperationCanceledException) when (pCt.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref totalIters);
                    Interlocked.Increment(ref batchFailures);
                }
            });
            int spentThisBatch = Volatile.Read(ref batchSpent);
            if (spentThisBatch == 0)
            {
                _logger.LogInformation(
                    "OptimizationWorker: surrogate batch produced no fresh candidates after deduplication — stopping exploration early");
                break;
            }

            consecutiveFailures = UpdateConsecutiveFailureStreak(
                consecutiveFailures,
                Volatile.Read(ref batchSuccesses),
                Volatile.Read(ref batchFailures));

            decimal batchBestScore = (decimal)BitConverter.Int64BitsToDouble(Interlocked.Read(ref batchBestScoreBits));
            remaining -= spentThisBatch;

            // Early stopping: if this batch didn't improve on the global best, increment stagnation counter
            if (batchBestScore > bestSeenScore)
            {
                double batchImprovement = (double)(batchBestScore - bestSeenScore);
                _metrics.OptimizationSurrogateImprovement.Record(batchImprovement);
                _metrics.OptimizationSurrogateBatchHits.Add(1);
                bestSeenScore = batchBestScore;
                stagnantBatches = 0;
            }
            else
            {
                _metrics.OptimizationSurrogateImprovement.Record(0.0);
                _metrics.OptimizationSurrogateBatchMisses.Add(1);
                stagnantBatches++;
                if (stagnantBatches >= maxStagnantBatches)
                {
                    _metrics.OptimizationEarlyStopSavings.Add(remaining);
                    _logger.LogDebug(
                        "OptimizationWorker: early stopping after {Batches} stagnant batches ({Remaining} evals saved)",
                        stagnantBatches, remaining);
                    break;
                }
            }

            // Incremental checkpoint: persist top candidates periodically so a crash
            // doesn't lose all progress. Stored as JSON in IntermediateResultsJson.
            if (config.CheckpointEveryN > 0 && totalIters % config.CheckpointEveryN == 0 && allEvaluated.Count > 0)
            {
                try
                {
                    List<ScoredCandidate> checkpointSnapshot;
                    lock (_evalLock) checkpointSnapshot = allEvaluated.ToList();
                    ulong surrogateRandomState = ehvi is not null
                        ? 0UL
                        : tpe?.RandomState ?? gp?.RandomState ?? 0UL;
                    var observations = checkpointSnapshot
                        .Select((c, index) => new OptimizationCheckpointStore.Observation(
                            Sequence: index + 1,
                            ParamsJson: c.ParamsJson,
                            HealthScore: c.HealthScore,
                            CvCoefficientOfVariation: c.CvCoefficientOfVariation,
                            Result: c.Result))
                        .ToList();
                    run.IntermediateResultsJson = OptimizationCheckpointStore.Serialize(
                        totalIters,
                        stagnantBatches,
                        surrogateKind,
                        surrogateRandomState,
                        observations,
                        seenParamSet.Keys,
                        _logger);
                    run.CheckpointVersion = OptimizationCheckpointStore.PayloadVersion;
                    run.Iterations = totalIters;
                    await writeCtx.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: checkpoint save failed (non-fatal)");
                }

                // Incremental memory pressure mitigation: trim trade lists from
                // low-scoring candidates at each checkpoint boundary.
                int keepCount = Math.Max(config.TopNCandidates * 2, 10);
                List<ScoredCandidate> trimSnapshot;
                lock (_evalLock) trimSnapshot = allEvaluated.OrderByDescending(c => c.HealthScore).ToList();
                for (int i = keepCount; i < trimSnapshot.Count; i++)
                {
                    if (!trimSnapshot[i].TradesTrimmed)
                    {
                        trimSnapshot[i].Result.Trades?.Clear();
                        trimSnapshot[i].TradesTrimmed = true;
                    }
                }
            }
        }

        // ── Surrogate quality diagnostics ─────────────────────────────────
        // Log GP/TPE health so operators can detect when the surrogate is struggling
        // (e.g., correlated observations causing ill-conditioned Cholesky, or gamma
        // quantile producing degenerate good/bad splits).
        if (gp is not null)
        {
            int clampCount = gp.LastCholeskyClampCount;
            if (clampCount > 0)
                _logger.LogWarning(
                    "OptimizationWorker: GP surrogate had {ClampCount} Cholesky diagonal clamp(s) — " +
                    "predictions may be unreliable due to correlated/duplicate observations",
                    clampCount);
            _metrics.OptimizationSurrogateClamps.Add(clampCount);
        }
        if (tpe is not null)
        {
            _logger.LogDebug(
                "OptimizationWorker: TPE surrogate finished with {Observations} observations",
                tpe.ObservationCount);
        }
        if (ehvi is not null)
        {
            _logger.LogDebug(
                "OptimizationWorker: EHVI finished — Pareto front size={FrontSize}, HV={HV:F4}, observations={Obs}",
                ehvi.ParetoFrontSize, ehvi.CurrentHypervolume, ehvi.ObservationCount);
        }

        return new SearchResult(
            allEvaluated,
            totalIters,
            surrogateKind,
            warmStarted,
            checkpoint.Observations.Count > 0);
    }
}
