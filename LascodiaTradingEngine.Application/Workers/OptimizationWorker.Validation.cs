using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Application.Optimization;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Workers;

public partial class OptimizationWorker
{
    // ── Pareto candidate validation ────────────────────────────────────────

    /// <summary>
    /// Iterates Pareto candidates in rank order, validating each through all gates
    /// (sensitivity, OOS, bootstrap CI, permutation test, cost stress, degradation,
    /// walk-forward, MTF regime, parameter/temporal/portfolio correlation, CV consistency).
    /// Returns the first candidate that passes all gates, or the best candidate with
    /// failure diagnostics if none pass.
    /// </summary>
    private async Task<CandidateValidationResult> ValidateParetoCandidatesAsync(
        List<ScoredCandidate> rankedCandidates,
        Strategy strategy, OptimizationRun run,
        List<Candle> trainCandles, List<Candle> testCandles,
        BacktestOptions screeningOptions,
        OptimizationGridBuilder.DataProtocol protocol,
        OptimizationConfig config, DbContext db,
        int totalIters, decimal baselineComparisonScore, string baselineParamsJson,
        IWriteApplicationDbContext writeCtx,
        CurrencyPair? pairInfo,
        CancellationToken ct, CancellationToken runCt)
    {
        var parameterGrid = await _gridBuilder.BuildParameterGridAsync(db, strategy.StrategyType, runCt);
        var parameterBounds = OptimizationGridBuilder.ExtractTpeBounds(parameterGrid);

        // Pre-fetch shared data (independent of which candidate is being validated)
        var higherTf = GetHigherTimeframe(strategy.Timeframe);
        MarketRegimeEnum? higherRegime = null;
        if (higherTf.HasValue)
        {
            higherRegime = await db.Set<MarketRegimeSnapshot>()
                .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == higherTf.Value && !s.IsDeleted)
                .OrderByDescending(s => s.DetectedAt)
                .Select(s => (MarketRegimeEnum?)s.Regime)
                .FirstOrDefaultAsync(runCt);
        }

        var otherActiveParamsJson = await db.Set<Strategy>()
            .Where(s => s.Id != strategy.Id
                     && s.Status == StrategyStatus.Active
                     && s.StrategyType == strategy.StrategyType
                     && s.Symbol == strategy.Symbol
                     && !s.IsDeleted)
            .Select(s => s.ParametersJson)
            .ToListAsync(runCt);

        // Pre-parse other strategies' params once — avoids re-deserializing per candidate
        var otherActiveParsed = new List<Dictionary<string, JsonElement>>();
        foreach (var json in otherActiveParamsJson)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (parsed is not null && parsed.Count > 0)
                    otherActiveParsed.Add(parsed);
            }
            catch { /* skip malformed */ }
        }

        CandidateValidationResult? lastResult = null;
        var failedResults = new List<(int Rank, string Params, string Reason, decimal Score)>();
        int candidateRank = 0;

        foreach (var candidate in rankedCandidates)
        {
            candidateRank++;
            var gateTimings = new List<(string Gate, double DurationMs)>();

            #region Sensitivity Analysis Gate
            var gateSw = Stopwatch.StartNew();
            var (sensitivityOk, sensitivityReport) = await _validator.SensitivityCheckAsync(
                strategy, candidate.ParamsJson, trainCandles, screeningOptions,
                config.ScreeningTimeoutSeconds, candidate.HealthScore,
                config.SensitivityPerturbPct, runCt,
                config.SensitivityDegradationTolerance, config.MaxParallelBacktests,
                parameterBounds);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "sensitivity"));
            gateTimings.Add(("Sensitivity", gateSw.Elapsed.TotalMilliseconds));

            if (!sensitivityOk)
            {
                _logger.LogDebug("OptimizationWorker: run {RunId} candidate #{Rank} sensitivity failed — trying next",
                    run.Id, candidateRank);
                if (candidateRank < rankedCandidates.Count) continue;
            }
            #endregion

            #region OOS Validation Gate
            gateSw.Restart();
            BacktestResult oosResult;
            decimal oosHealthScore;
            bool hasSufficientOosData = testCandles.Count >= config.MinOosCandlesForValidation;
            if (hasSufficientOosData)
            {
                oosResult = await _validator.RunWithTimeoutAsync(
                    strategy, candidate.ParamsJson, testCandles, screeningOptions, config.ScreeningTimeoutSeconds, runCt);
                oosHealthScore = OptimizationHealthScorer.ComputeHealthScore(oosResult);
            }
            else
            {
                oosResult      = candidate.Result;
                oosHealthScore = (candidate.HealthScore - protocol.ScorePenalty) * 0.85m;
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "oos_validation"));
            gateTimings.Add(("OOS", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region Bootstrap CI Gate
            gateSw.Restart();
            decimal ciLower, ciMedian, ciUpper;
            int bootstrapTradeCount = oosResult.Trades?.Count ?? 0;
            const int bootstrapMinTrades = 15; // Full empirical CI threshold
            const int syntheticMaxTrades = 7;  // Pure synthetic CI threshold
            if (bootstrapTradeCount >= bootstrapMinTrades)
            {
                (ciLower, ciMedian, ciUpper) = BootstrapAnalyzer.ComputeHealthScoreCI(
                    oosResult.Trades!, config.ScreeningInitialBalance, config.BootstrapIterations, 0.95,
                    run.Id.GetHashCode() ^ candidateRank);
            }
            else if (bootstrapTradeCount > syntheticMaxTrades)
            {
                // Blending zone (8-14 trades): interpolate between synthetic and empirical
                // CI to avoid a hard cliff at the threshold boundary. The blend weight
                // ramps linearly from 0% empirical at syntheticMaxTrades+1 to 100% at bootstrapMinTrades.
                var (empLower, empMedian, empUpper) = BootstrapAnalyzer.ComputeHealthScoreCI(
                    oosResult.Trades!, config.ScreeningInitialBalance, config.BootstrapIterations, 0.95,
                    run.Id.GetHashCode() ^ candidateRank);

                double samplePenalty = 0.50 + 0.13 * Math.Min(bootstrapTradeCount, 9) / 9.0;
                decimal synLower = oosHealthScore * (decimal)samplePenalty;

                decimal blendWeight = (decimal)(bootstrapTradeCount - syntheticMaxTrades)
                                    / (bootstrapMinTrades - syntheticMaxTrades);
                ciLower  = synLower + blendWeight * (empLower - synLower);
                ciMedian = oosHealthScore + blendWeight * (empMedian - oosHealthScore);
                ciUpper  = oosHealthScore + blendWeight * (empUpper - oosHealthScore);
            }
            else
            {
                // Pure synthetic CI for very low trade counts (0-7 trades).
                // 0 trades → 50% of score, 7 trades → 60% of score.
                double samplePenalty = 0.50 + 0.13 * Math.Min(bootstrapTradeCount, syntheticMaxTrades) / (double)syntheticMaxTrades;
                ciLower = oosHealthScore * (decimal)samplePenalty;
                ciMedian = oosHealthScore;
                ciUpper = oosHealthScore;
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "bootstrap_ci"));
            gateTimings.Add(("BootstrapCI", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region Permutation Test Gate
            gateSw.Restart();
            double permPValue = 1.0, permCorrectedAlpha = 0.05; bool permSignificant = false;
            if (oosResult.Trades is not null && oosResult.Trades.Count >= 10)
            {
                (permPValue, permCorrectedAlpha, permSignificant) = PermutationTestAnalyzer.RunPermutationTest(
                    oosResult.Trades, oosHealthScore, config.ScreeningInitialBalance,
                    candidatesEvaluated: totalIters, familyWiseAlpha: 0.05,
                    iterations: config.PermutationIterations, seed: run.Id.GetHashCode() ^ candidateRank);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "permutation_test"));
            gateTimings.Add(("Permutation", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region Cost Sensitivity Gate
            gateSw.Restart();
            decimal pessimisticScore = oosHealthScore;
            bool costSensitiveOk = true;
            if (config.CostSensitivityEnabled && testCandles.Count >= config.MinOosCandlesForValidation)
            {
                (costSensitiveOk, pessimisticScore) = await _validator.CostSensitivitySweepAsync(
                    strategy, candidate.ParamsJson, testCandles, screeningOptions,
                    config.AutoApprovalMinHealthScore, config.ScreeningTimeoutSeconds, runCt,
                    config.CostStressMultiplier);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "cost_sensitivity"));
            gateTimings.Add(("CostSensitivity", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region IS-to-OOS Degradation Gate
            gateSw.Restart();
            bool degradationFailed = false;
            if (candidate.HealthScore > 0m)
            {
                // Regime-conditional degradation: relax tolerance 1.5x if IS/OOS regimes differ
                double effectiveDegradation = config.MaxOosDegradationPct;
                try
                {
                    var testStart = testCandles.Count > 0 ? testCandles[0].Timestamp : DateTime.UtcNow;
                    var oosRegime = await db.Set<MarketRegimeSnapshot>()
                        .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe
                                 && !s.IsDeleted && s.DetectedAt <= testStart)
                        .OrderByDescending(s => s.DetectedAt)
                        .Select(s => (MarketRegimeEnum?)s.Regime)
                        .FirstOrDefaultAsync(ct);
                    var lastTrainTimestamp = trainCandles[trainCandles.Count - 1].Timestamp;
                    var isRegime = await db.Set<MarketRegimeSnapshot>()
                        .Where(s => s.Symbol == strategy.Symbol && s.Timeframe == strategy.Timeframe
                                 && !s.IsDeleted && s.DetectedAt <= lastTrainTimestamp)
                        .OrderByDescending(s => s.DetectedAt)
                        .Select(s => (MarketRegimeEnum?)s.Regime)
                        .FirstOrDefaultAsync(ct);
                    if (oosRegime.HasValue && isRegime.HasValue && oosRegime.Value != isRegime.Value)
                        effectiveDegradation *= 1.5;
                }
                catch { /* Non-critical — use default threshold */ }

                double degradationRatio = (double)(oosHealthScore / candidate.HealthScore);
                degradationFailed = degradationRatio < (1.0 - effectiveDegradation);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "degradation"));
            gateTimings.Add(("Degradation", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region Walk-Forward Stability Gate
            gateSw.Restart();
            var (wfAvgScore, wfStable) = await _validator.WalkForwardValidateAsync(
                strategy, candidate.ParamsJson, testCandles, screeningOptions, config.ScreeningTimeoutSeconds, runCt,
                config.WalkForwardMinMaxRatio, baselineParamsJson);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "walk_forward"));
            gateTimings.Add(("WalkForward", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region MTF Regime + Correlation Gates
            gateSw.Restart();
            bool mtfCompatible = !higherRegime.HasValue || IsRegimeCompatibleWithStrategy(strategy.StrategyType, higherRegime.Value);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "mtf_regime"));

            // ── Parameter correlation ───────────────────────────────
            gateSw.Restart();
            bool correlationSafe = otherActiveParsed.Count == 0 ||
                !AreParametersSimilarToAny(candidate.ParamsJson, otherActiveParsed, config.CorrelationParamThreshold);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "param_correlation"));

            // ── Temporal signal correlation ─────────────────────────
            gateSw.Restart();
            bool temporalCorrelationSafe = true; double temporalMaxOverlap = 0;
            if (testCandles.Count >= 50)
            {
                var recentCandles = testCandles.TakeLast(Math.Min(200, testCandles.Count)).ToList();
                (temporalCorrelationSafe, temporalMaxOverlap) = await _validator.TemporalSignalCorrelationCheckAsync(
                    strategy, candidate.ParamsJson, recentCandles, screeningOptions,
                    config.ScreeningTimeoutSeconds, db, config.TemporalOverlapThreshold, strategy.Timeframe, runCt);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "temporal_correlation"));

            // ── Portfolio PnL correlation ───────────────────────────
            gateSw.Restart();
            bool portfolioCorrelationSafe = true; double portfolioMaxCorrelation = 0;
            if (testCandles.Count >= config.MinOosCandlesForValidation)
            {
                (portfolioCorrelationSafe, portfolioMaxCorrelation) = await _validator.PortfolioCorrelationCheckAsync(
                    strategy, oosResult, db, config.PortfolioCorrelationThreshold, runCt);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "portfolio_correlation"));
            #endregion

            #region CV Consistency + CPCV Gate
            gateSw.Restart();
            double cvValue = candidate.CvCoefficientOfVariation;
            bool cvConsistent = cvValue <= config.MaxCvCoefficientOfVariation;
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "cv_consistency"));

            // ── CPCV (Combinatorial Purged Cross-Validation) ───────
            // Generates a distribution of OOS scores across all C(N,K) train/test
            // combinations with purging. If the CPCV CV exceeds the consistency
            // threshold, the candidate's performance is not robust across temporal
            // splits — override cvConsistent to fail the candidate.
            gateSw.Restart();
            decimal cpcvMean = 0m; double cpcvCv = 0;
            IReadOnlyList<decimal> cpcvScores = [];
            if (testCandles.Count >= config.MinOosCandlesForValidation)
            {
                // CPCV safety cap
                long totalCombinations = Binomial(config.CpcvNFolds, config.CpcvTestFoldCount);
                if (totalCombinations > 1000)
                {
                    _logger.LogWarning("OptimizationWorker: CPCV C({N},{K})={C} exceeds 1000 — skipping CPCV to prevent resource exhaustion",
                        config.CpcvNFolds, config.CpcvTestFoldCount, totalCombinations);
                    // Skip CPCV, use simple CV results
                }
                else
                try
                {
                    // Use all available candles (train+test) for CPCV since it creates
                    // its own IS/OOS splits internally
                    var cpcvCandles = trainCandles.Concat(testCandles).OrderBy(c => c.Timestamp).ToList();
                    int cpcvEmbargo = Math.Max(1, (int)(cpcvCandles.Count * config.EmbargoRatio / config.CpcvNFolds));
                    (decimal mean, decimal std, double cv, int combos, var scores) = await _validator.CpcvEvaluateAsync(
                        strategy, candidate.ParamsJson, cpcvCandles, screeningOptions,
                        config.ScreeningTimeoutSeconds,
                        nFolds: config.CpcvNFolds, testFoldCount: config.CpcvTestFoldCount,
                        embargoCandles: cpcvEmbargo,
                        minTrades: config.MinCandidateTrades, maxCombinations: config.CpcvMaxCombinations,
                        seed: run.Id.GetHashCode() ^ candidateRank,
                        ct: runCt, maxParallelism: config.MaxParallelBacktests);
                    cpcvMean = mean;
                    cpcvCv = cv;
                    cpcvScores = scores;

                    // If CPCV shows high variance, override the simpler CV gate
                    if (cv > config.MaxCvCoefficientOfVariation && cvConsistent)
                    {
                        cvConsistent = false;
                        cvValue = cv; // Use the more rigorous CPCV CV value
                        _logger.LogDebug(
                            "OptimizationWorker: run {RunId} candidate #{Rank} CPCV override — " +
                            "simple CV={SimpleCv:F3} passed but CPCV CV={CpcvCv:F3} failed ({Combos} combinations)",
                            run.Id, candidateRank, candidate.CvCoefficientOfVariation, cv, combos);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "OptimizationWorker: CPCV evaluation failed for run {RunId} candidate #{Rank} (non-fatal)",
                        run.Id, candidateRank);
                }
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "cpcv"));
            gateTimings.Add(("CPCV", gateSw.Elapsed.TotalMilliseconds));

            // ── CPCV CI override ───────────────────────────────────────
            // When bootstrap fell back to synthetic CI (< 10 OOS trades) but CPCV
            // succeeded with enough combinatorial splits, prefer the CPCV-derived
            // empirical CI. Uses percentile-based CI rather than normal assumption
            // (mean ± 2σ) because CPCV distributions with ≤15 samples can be heavily
            // skewed — a single catastrophic fold would dominate a normal-based estimate.
            bool usedSyntheticBootstrap = oosResult.Trades is null || oosResult.Trades.Count < 10;
            if (usedSyntheticBootstrap && cpcvScores.Count >= 3)
            {
                // Percentile-based 95% CI: sort scores and take the 2.5th percentile
                var sorted = cpcvScores.OrderBy(s => s).ToList();
                int lowerIdx = Math.Max(0, (int)(sorted.Count * 0.025));
                int medianIdx = sorted.Count / 2;
                decimal empiricalCILower = sorted[lowerIdx];
                decimal empiricalCIMedian = sorted[medianIdx];

                if (empiricalCILower != ciLower)
                {
                    _logger.LogDebug(
                        "OptimizationWorker: run {RunId} candidate #{Rank} — overriding synthetic bootstrap CI " +
                        "(lower={Synthetic:F3}) with CPCV percentile CI (p2.5={Empirical:F3}, median={Median:F3}, n={N})",
                        run.Id, candidateRank, ciLower, empiricalCILower, empiricalCIMedian, cpcvScores.Count);
                    ciLower = empiricalCILower;
                    ciMedian = empiricalCIMedian;
                }
            }

            #endregion

            #region Threshold Adjustments + Kelly + Equity Curve + Time Concentration + Genesis Regression
            // ── P2: Apply live haircuts to tighten approval thresholds ──
            decimal effectiveMinScore = config.AutoApprovalMinHealthScore;
            decimal effectiveImprovementThreshold = config.AutoApprovalImprovementThreshold;
            try
            {
                await using var haircutScope = _scopeFactory.CreateAsyncScope();
                var liveBenchmark = haircutScope.ServiceProvider.GetService<ILivePerformanceBenchmark>();
                if (liveBenchmark != null)
                {
                    var haircuts = await liveBenchmark.GetCachedHaircutsAsync(ct);
                    if (haircuts.SampleCount >= 5 || haircuts.SampleCount < 0)
                    {
                        // Tighten: if live Sharpe = 70% of backtest, divide threshold by 0.7
                        effectiveMinScore = config.AutoApprovalMinHealthScore /
                            (decimal)Math.Max(0.5, haircuts.SharpeHaircut);
                        _logger.LogDebug("OptimizationWorker: haircut-adjusted approval threshold: {Original:F2} → {Adjusted:F2}",
                            config.AutoApprovalMinHealthScore, effectiveMinScore);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: haircut load failed (non-fatal)");
            }

            // ── P5: Apply asset-class threshold multipliers ────────────
            var assetClass = StrategyGenerationHelpers.ClassifyAsset(strategy.Symbol, pairInfo);
            var (_, acPF, acSh, acDD) = StrategyGenerationHelpers.GetAssetClassThresholdMultipliers(assetClass);
            effectiveMinScore *= (decimal)Math.Max(acSh, acPF);

            // ── P7: Kelly position sizing sensitivity check ────────────
            bool kellySizingOk = true;
            double kellySharpe = 0, fixedLotSharpe = (double)oosResult.SharpeRatio;
            if (oosResult.TotalTrades >= 10 && oosResult.WinRate > 0 && oosResult.AverageLoss > 0)
            {
                try
                {
                    decimal b = oosResult.AverageLoss > 0 ? oosResult.AverageWin / oosResult.AverageLoss : 1m;
                    decimal kellyFull = (oosResult.WinRate * b - (1m - oosResult.WinRate)) / b;
                    if (kellyFull > 0)
                    {
                        decimal halfKelly = Math.Clamp(kellyFull * 0.5m, 0.01m, 0.10m);
                        var kellyOptions = new BacktestOptions
                        {
                            SpreadPriceUnits   = screeningOptions.SpreadPriceUnits,
                            SpreadFunction     = screeningOptions.SpreadFunction,
                            CommissionPerLot   = screeningOptions.CommissionPerLot,
                            SlippagePriceUnits = screeningOptions.SlippagePriceUnits,
                            SwapPerLotPerDay   = screeningOptions.SwapPerLotPerDay,
                            ContractSize       = screeningOptions.ContractSize,
                            GapSlippagePct     = screeningOptions.GapSlippagePct,
                            FillRatio          = screeningOptions.FillRatio,
                            PositionSizer      = (balance, signal) =>
                            {
                                decimal riskPerUnit = Math.Max(0.001m * signal.EntryPrice,
                                    Math.Abs(signal.EntryPrice - (signal.StopLoss ?? signal.EntryPrice)));
                                return Math.Clamp(balance * halfKelly / (screeningOptions.ContractSize * riskPerUnit), 0.01m, 10m);
                            },
                        };
                        var kellyResult = await _backtestEngine.RunAsync(strategy, testCandles,
                            config.ScreeningInitialBalance, runCt, kellyOptions);
                        kellySharpe = (double)kellyResult.SharpeRatio;
                        kellySizingOk = kellySharpe >= fixedLotSharpe * 0.80;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OptimizationWorker: Kelly sizing check failed (non-fatal)");
                }
            }

            // ── Equity curve linearity check (R²) ─────────────────────
            gateSw.Restart();
            bool equityCurveOk = true;
            if (oosResult.Trades is { Count: >= 5 })
            {
                double r2 = StrategyScreeningEngine.ComputeEquityCurveR2(oosResult.Trades, config.ScreeningInitialBalance);
                double minR2 = config.MinEquityCurveR2;
                if (r2 < minR2)
                {
                    equityCurveOk = false;
                    _logger.LogDebug("OptimizationWorker: equity curve R²={R2:F3} below {Min:F2}", r2, minR2);
                }
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "equity_curve_r2"));

            // ── Trade time concentration check ─────────────────────────
            gateSw.Restart();
            bool timeConcentrationOk = true;
            if (oosResult.Trades is { Count: >= 10 })
            {
                double concentration = StrategyScreeningEngine.ComputeTradeTimeConcentration(oosResult.Trades);
                double maxConcentration = config.MaxTradeTimeConcentration;
                if (concentration > maxConcentration)
                {
                    timeConcentrationOk = false;
                    _logger.LogDebug("OptimizationWorker: trade time concentration={Conc:P1} above {Max:P1}", concentration, maxConcentration);
                }
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "time_concentration"));

            // ── Genesis quality regression check ──────────────────────
            bool genesisRegressionOk = true;
            try
            {
                var screeningJson = strategy.ScreeningMetricsJson;
                if (!string.IsNullOrEmpty(screeningJson))
                {
                    var genesis = StrategyGeneration.ScreeningMetrics.FromJson(screeningJson);
                    if (genesis != null && genesis.IsSharpeRatio > 0)
                    {
                        double genesisOosSharpe = genesis.OosSharpeRatio;
                        if (genesisOosSharpe > 0 && (double)oosResult.SharpeRatio < genesisOosSharpe * 0.80)
                        {
                            genesisRegressionOk = false;
                            _logger.LogDebug(
                                "OptimizationWorker: genesis regression — OOS Sharpe {Current:F2} < 80% of original {Genesis:F2}",
                                oosResult.SharpeRatio, genesisOosSharpe);
                        }
                    }
                }
            }
            catch { /* Non-critical */ }

            #endregion

            #region Approval Evaluation
            // ── Check if this candidate passes all gates ────────────
            decimal candidateImprovement = oosHealthScore - baselineComparisonScore;
            var approval = OptimizationApprovalPolicy.Evaluate(new OptimizationApprovalPolicy.Input(
                candidateImprovement,
                oosHealthScore,
                oosResult.TotalTrades,
                oosResult.SharpeRatio,
                oosResult.MaxDrawdownPct,
                oosResult.WinRate,
                oosResult.ProfitFactor,
                ciLower,
                config.MinBootstrapCILower,
                degradationFailed,
                wfStable,
                mtfCompatible,
                correlationSafe,
                sensitivityOk,
                costSensitiveOk,
                temporalCorrelationSafe,
                portfolioCorrelationSafe,
                permSignificant,
                cvConsistent,
                temporalMaxOverlap,
                portfolioMaxCorrelation,
                permPValue,
                permCorrectedAlpha,
                cvValue,
                pessimisticScore,
                sensitivityReport,
                effectiveImprovementThreshold,
                effectiveMinScore,
                config.MinCandidateTrades,
                config.MaxCvCoefficientOfVariation,
                kellySizingOk,
                kellySharpe,
                fixedLotSharpe,
                equityCurveOk,
                timeConcentrationOk,
                acSh,
                acPF,
                acDD,
                genesisRegressionOk,
                hasSufficientOosData));

            lastResult = new CandidateValidationResult(
                approval.Passed, candidate, oosHealthScore, oosResult,
                ciLower, ciMedian, ciUpper,
                permPValue, permCorrectedAlpha, permSignificant,
                sensitivityOk, sensitivityReport,
                costSensitiveOk, pessimisticScore,
                degradationFailed, wfAvgScore, wfStable, mtfCompatible,
                correlationSafe, temporalCorrelationSafe, temporalMaxOverlap,
                portfolioCorrelationSafe, portfolioMaxCorrelation,
                cvConsistent, cvValue,
                JsonSerializer.Serialize(approval.StructuredReport),
                approval.FailureReason);

            // ── Per-gate rejection counters ─────────────────────────
            if (!sensitivityOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "sensitivity"));
            if (!costSensitiveOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "cost_sensitivity"));
            if (degradationFailed) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "degradation"));
            if (!wfStable) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "walk_forward"));
            if (!mtfCompatible) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "mtf_regime"));
            if (!correlationSafe) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "param_correlation"));
            if (!temporalCorrelationSafe) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "temporal_correlation"));
            if (!portfolioCorrelationSafe) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "portfolio_correlation"));
            if (!cvConsistent) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "cv_consistency"));
            if (!permSignificant) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "permutation_test"));
            if (!hasSufficientOosData) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "insufficient_oos_data"));
            if (!kellySizingOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "kelly_sizing"));
            if (!equityCurveOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "equity_curve_r2"));
            if (!timeConcentrationOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "time_concentration"));
            if (!genesisRegressionOk) _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "genesis_regression"));

            // Per-gate timing summary
            if (gateTimings.Count > 0)
            {
                _logger.LogDebug("OptimizationWorker: gate timings — {Timings}",
                    string.Join(", ", gateTimings.Select(g => $"{g.Gate}={g.DurationMs:F0}ms")));

                foreach (var (gate, ms) in gateTimings)
                    _metrics.OptimizationPhaseDurationMs.Record(ms, new KeyValuePair<string, object?>("gate", gate));
            }

            // Heartbeat renewal: validation of each Pareto candidate can take minutes
            // (CPCV + sensitivity + permutation test). Renew the lease to prevent
            // RequeueExpiredRunningRunsAsync from reclaiming this run mid-validation.
            try { await HeartbeatRunAsync(run, writeCtx, ct); }
            catch { /* non-fatal — lease expiry is handled gracefully */ }

            if (approval.Passed)
            {
                if (candidateRank > 1)
                    _logger.LogInformation(
                        "OptimizationWorker: run {RunId} Pareto candidate #{Rank} passed (#{PrevRank} failed gates)",
                        run.Id, candidateRank, candidateRank - 1);
                return lastResult;
            }

            // Collect failed candidate info (up to 3) for dead-letter diagnostics
            if (failedResults.Count < 3)
            {
                failedResults.Add((candidateRank, candidate.ParamsJson,
                    approval.FailureReason ?? "unknown", oosHealthScore));
            }

            // Last candidate — use it even though it failed (for reporting)
            if (candidateRank >= rankedCandidates.Count)
            {
                _logger.LogDebug(
                    "OptimizationWorker: run {RunId} all {Count} Pareto candidates failed gates — using best for reporting",
                    run.Id, rankedCandidates.Count);
                return lastResult! with { FailedCandidates = failedResults };
            }

            #endregion

            _logger.LogDebug("OptimizationWorker: run {RunId} candidate #{Rank} failed gate — trying next Pareto candidate",
                run.Id, candidateRank);
        }

        // Should never reach here (rankedCandidates is non-empty), but satisfy the compiler
        return lastResult! with { FailedCandidates = failedResults };
    }

    private static long Binomial(int n, int k)
    {
        if (k > n || k < 0) return 0;
        if (k == 0 || k == n) return 1;
        k = Math.Min(k, n - k);
        long result = 1;
        for (int i = 0; i < k; i++)
            result = result * (n - i) / (i + 1);
        return result;
    }
}
