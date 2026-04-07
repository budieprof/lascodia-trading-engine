using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Common.Diagnostics;
using LascodiaTradingEngine.Application.Common.Interfaces;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Scoped)]
internal sealed class OptimizationValidationCoordinator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OptimizationValidator _validator;
    private readonly OptimizationValidationContextLoader _contextLoader;
    private readonly OptimizationThresholdAdjustmentEvaluator _thresholdAdjustmentEvaluator;
    private readonly TradingMetrics _metrics;
    private readonly ILogger<OptimizationValidationCoordinator> _logger;
    private readonly TimeProvider _timeProvider;
    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public OptimizationValidationCoordinator(
        IServiceScopeFactory scopeFactory,
        OptimizationValidator validator,
        OptimizationValidationContextLoader contextLoader,
        OptimizationThresholdAdjustmentEvaluator thresholdAdjustmentEvaluator,
        TradingMetrics metrics,
        ILogger<OptimizationValidationCoordinator> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _validator = validator;
        _contextLoader = contextLoader;
        _thresholdAdjustmentEvaluator = thresholdAdjustmentEvaluator;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // ── Pareto candidate validation ────────────────────────────────────────

    /// <summary>
    /// Iterates Pareto candidates in rank order, validating each through all gates
    /// (sensitivity, OOS, bootstrap CI, permutation test, cost stress, degradation,
    /// walk-forward, MTF regime, parameter/temporal/portfolio correlation, CV consistency).
    /// Returns the first candidate that passes all gates, or the best candidate with
    /// failure diagnostics if none pass.
    /// </summary>
    internal async Task<CandidateValidationResult> ValidateAsync(
        List<ScoredCandidate> rankedCandidates,
        Strategy strategy, OptimizationRun run,
        List<Candle> trainCandles, List<Candle> testCandles,
        BacktestOptions screeningOptions,
        OptimizationGridBuilder.DataProtocol protocol,
        ValidationConfig config, DbContext db,
        int totalIters, decimal baselineComparisonScore, string baselineParamsJson,
        IWriteApplicationDbContext writeCtx,
        CurrencyPair? pairInfo,
        CancellationToken ct, CancellationToken runCt)
    {
        var sharedContext = await _contextLoader.LoadAsync(
            db,
            strategy,
            run,
            trainCandles,
            testCandles,
            rankedCandidates.Count,
            config.MaxRunTimeoutMinutes,
            runCt);
        var parameterBounds = sharedContext.ParameterBounds;
        var higherRegime = sharedContext.HigherRegime;
        var otherActiveParsed = sharedContext.OtherActiveParsed;
        var testWindowRegime = sharedContext.TestWindowRegime;
        var trainWindowRegime = sharedContext.TrainWindowRegime;
        bool relaxDegradationThreshold = sharedContext.RelaxDegradationThreshold;

        CandidateValidationResult? lastResult = null;
        CandidateValidationResult? bestFailedResult = null;
        var failedResults = new List<(int Rank, string Params, string Reason, decimal Score)>();
        int candidateRank = 0;

        // Per-gate timeout budget: allocate a fraction of the remaining aggregate time to
        // each candidate's validation pass. This prevents a single expensive gate (CPCV with
        // many combinations, sensitivity with high parallelism) from starving downstream
        // gates. Budget = remaining_time / (candidates * 2), floored at 60s.
        int gateTimeoutSeconds = sharedContext.GateTimeoutSeconds;

        foreach (var candidate in rankedCandidates)
        {
            candidateRank++;
            var gateTimings = new List<(string Gate, double DurationMs)>();

            // Create a per-candidate gate budget CTS linked to the run-level timeout.
            // Each candidate gets gateTimeoutSeconds; if exceeded, remaining gates for
            // this candidate are skipped and the next Pareto candidate is tried.
            using var gateBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(runCt);
            gateBudgetCts.CancelAfter(TimeSpan.FromSeconds(gateTimeoutSeconds));
            var gateCt = gateBudgetCts.Token;

            try
            {

            #region Sensitivity Analysis Gate
            var gateSw = Stopwatch.StartNew();
            var (sensitivityOk, sensitivityReport) = await _validator.SensitivityCheckAsync(
                strategy, candidate.ParamsJson, trainCandles, screeningOptions,
                config.ScreeningTimeoutSeconds, candidate.HealthScore,
                config.SensitivityPerturbPct, gateCt,
                config.SensitivityDegradationTolerance, config.MaxParallelBacktests,
                parameterBounds);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "sensitivity"));
            gateTimings.Add(("Sensitivity", gateSw.Elapsed.TotalMilliseconds));

            if (!sensitivityOk)
            {
                string failureReason = $"parameter sensitivity failure: {sensitivityReport}";
                bool sensitivityMtfCompatible = !higherRegime.HasValue || OptimizationPolicyHelpers.IsRegimeCompatibleWithStrategy(strategy.StrategyType, higherRegime.Value);
                double sensitivityCvValue = candidate.CvCoefficientOfVariation;
                bool sensitivityCvConsistent = sensitivityCvValue <= config.MaxCvCoefficientOfVariation;
                var missingOosResult = new BacktestResult();

                lastResult = CandidateValidationResult.Create(
                    false,
                    candidate,
                    0m,
                    missingOosResult,
                    false,
                    candidate.HealthScore,
                    candidate.HealthScore,
                    candidate.HealthScore,
                    1.0,
                    0.05,
                    false,
                    false,
                    sensitivityReport,
                    true,
                    candidate.HealthScore,
                    false,
                    candidate.HealthScore,
                    true,
                    sensitivityMtfCompatible,
                    true,
                    true,
                    0,
                    true,
                    0,
                    sensitivityCvConsistent,
                    sensitivityCvValue,
                    OptimizationApprovalReportParser.Serialize(new OptimizationApprovalReportParser.ApprovalReport
                    {
                        Passed = false,
                        SensitivityOk = false,
                        FailureReason = failureReason,
                        HasOosValidation = false,
                        HasSufficientOutOfSampleData = false,
                        InSampleHealthScore = candidate.HealthScore,
                    }),
                    failureReason);

                _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "sensitivity"));

                if (gateTimings.Count > 0)
                {
                    _logger.LogDebug("OptimizationWorker: gate timings — {Timings}",
                        string.Join(", ", gateTimings.Select(g => $"{g.Gate}={g.DurationMs:F0}ms")));

                    foreach (var (gate, ms) in gateTimings)
                        _metrics.OptimizationPhaseDurationMs.Record(ms, new KeyValuePair<string, object?>("gate", gate));
                }

                if (failedResults.Count < 3)
                    failedResults.Add((candidateRank, candidate.ParamsJson, failureReason, candidate.HealthScore));

                try { await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "OptimizationWorker: heartbeat renewal failed during Pareto validation for run {RunId}", run.Id); }

                _logger.LogDebug("OptimizationWorker: run {RunId} candidate #{Rank} sensitivity failed — trying next",
                    run.Id, candidateRank);
                if (candidateRank < rankedCandidates.Count) continue;

                bestFailedResult ??= lastResult;
                return bestFailedResult with { FailedCandidates = failedResults };
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
                    strategy, candidate.ParamsJson, testCandles, screeningOptions, config.ScreeningTimeoutSeconds, gateCt);
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
            var (ciLower, ciMedian, ciUpper) = ComputeBootstrapCI(
                oosResult, oosHealthScore, config.ScreeningInitialBalance,
                config.BootstrapIterations, run.Id.GetHashCode() ^ candidateRank);
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
                    config.AutoApprovalMinHealthScore, config.ScreeningTimeoutSeconds, gateCt,
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
                double effectiveDegradation = config.MaxOosDegradationPct;
                if (relaxDegradationThreshold)
                    effectiveDegradation *= 1.5;

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
                strategy, candidate.ParamsJson, testCandles, screeningOptions, config.ScreeningTimeoutSeconds, gateCt,
                config.WalkForwardMinMaxRatio, baselineParamsJson);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "walk_forward"));
            gateTimings.Add(("WalkForward", gateSw.Elapsed.TotalMilliseconds));
            #endregion

            #region MTF Regime + Correlation Gates
            gateSw.Restart();
            bool mtfCompatible = !higherRegime.HasValue || OptimizationPolicyHelpers.IsRegimeCompatibleWithStrategy(strategy.StrategyType, higherRegime.Value);
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "mtf_regime"));

            // ── Parameter correlation ───────────────────────────────
            gateSw.Restart();
            bool correlationSafe = otherActiveParsed.Count == 0 ||
                !OptimizationPolicyHelpers.AreParametersSimilarToAny(candidate.ParamsJson, otherActiveParsed, config.CorrelationParamThreshold);
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
                    config.ScreeningTimeoutSeconds, db, config.TemporalOverlapThreshold, strategy.Timeframe, gateCt);
            }
            gateSw.Stop();
            _metrics.OptimizationGateDurationMs.Record(gateSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("gate", "temporal_correlation"));

            // ── Portfolio PnL correlation ───────────────────────────
            gateSw.Restart();
            bool portfolioCorrelationSafe = true; double portfolioMaxCorrelation = 0;
            if (testCandles.Count >= config.MinOosCandlesForValidation)
            {
                (portfolioCorrelationSafe, portfolioMaxCorrelation) = await _validator.PortfolioCorrelationCheckAsync(
                    strategy, oosResult, db, config.PortfolioCorrelationThreshold, gateCt);
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
            bool cpcvSkipped = false;
            if (testCandles.Count >= config.MinOosCandlesForValidation)
            {
                // CPCV safety cap
                long totalCombinations = Binomial(config.CpcvNFolds, config.CpcvTestFoldCount);
                if (totalCombinations > 1000)
                {
                    _logger.LogWarning("OptimizationWorker: CPCV C({N},{K})={C} exceeds 1000 — skipping CPCV to prevent resource exhaustion. " +
                        "CV consistency flag will be penalized.",
                        config.CpcvNFolds, config.CpcvTestFoldCount, totalCombinations);
                    cpcvSkipped = true;
                    // Skip CPCV, use simple CV results but mark as skipped
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
                        ct: gateCt, maxParallelism: config.MaxParallelBacktests);
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

            // When CPCV was skipped due to combinatorial cap, the simple CV consistency
            // flag is the only check. Penalize by requiring a stricter CV threshold.
            if (cpcvSkipped && cvConsistent && cvValue > config.MaxCvCoefficientOfVariation * 0.75)
            {
                cvConsistent = false;
                _logger.LogDebug(
                    "OptimizationWorker: run {RunId} candidate #{Rank} — CPCV skipped, applying stricter CV threshold " +
                    "(cvValue={Cv:F3} > {Threshold:F3})",
                    run.Id, candidateRank, cvValue, config.MaxCvCoefficientOfVariation * 0.75);
            }

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
            var adj = await _thresholdAdjustmentEvaluator.EvaluateAsync(
                strategy, candidate.ParamsJson, oosResult, testCandles, screeningOptions, config, pairInfo, run.Id, gateCt);
            decimal effectiveMinScore = adj.EffectiveMinScore;
            decimal effectiveImprovementThreshold = adj.EffectiveImprovementThreshold;
            bool kellySizingOk = adj.KellySizingOk;
            double kellySharpe = adj.KellySharpe;
            double fixedLotSharpe = adj.FixedLotSharpe;
            var (_, acPF, acSh, acDD) = adj.AssetClassMultipliers; // (WR, PF, Sharpe, DD)
            bool equityCurveOk = adj.EquityCurveOk;
            bool timeConcentrationOk = adj.TimeConcentrationOk;
            bool genesisRegressionOk = adj.GenesisRegressionOk;

            foreach (var (gate, ms) in adj.GateTimings)
                _metrics.OptimizationGateDurationMs.Record(ms, new KeyValuePair<string, object?>("gate", gate));
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

            lastResult = CandidateValidationResult.Create(
                approval.Passed, candidate, oosHealthScore, oosResult, hasSufficientOosData,
                ciLower, ciMedian, ciUpper,
                permPValue, permCorrectedAlpha, permSignificant,
                sensitivityOk, sensitivityReport,
                costSensitiveOk, pessimisticScore,
                degradationFailed, wfAvgScore, wfStable, mtfCompatible,
                correlationSafe, temporalCorrelationSafe, temporalMaxOverlap,
                portfolioCorrelationSafe, portfolioMaxCorrelation,
                cvConsistent, cvValue,
                OptimizationApprovalReportParser.Serialize(approval.Report),
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
            try { await OptimizationExecutionLeasePolicy.HeartbeatRunAsync(run, writeCtx, UtcNow, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "OptimizationWorker: heartbeat renewal failed during Pareto validation for run {RunId}", run.Id); }

            if (approval.Passed)
            {
                if (candidateRank > 1)
                    _logger.LogInformation(
                        "OptimizationWorker: run {RunId} Pareto candidate #{Rank} passed (#{PrevRank} failed gates)",
                        run.Id, candidateRank, candidateRank - 1);
                return lastResult;
            }

            bestFailedResult ??= lastResult;

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
                return bestFailedResult! with { FailedCandidates = failedResults };
            }

            #endregion

            _logger.LogDebug("OptimizationWorker: run {RunId} candidate #{Rank} failed gate — trying next Pareto candidate",
                run.Id, candidateRank);

            }
            catch (OperationCanceledException) when (gateBudgetCts.IsCancellationRequested && !runCt.IsCancellationRequested)
            {
                // Per-candidate gate budget expired — skip remaining gates for this candidate
                // and try the next Pareto candidate. This prevents a single expensive gate
                // (e.g., CPCV with many combinations) from blocking the entire validation phase.
                string failureReason = $"gate budget timeout ({gateTimeoutSeconds}s)";
                var missingOosResult = new BacktestResult();
                lastResult ??= CandidateValidationResult.Create(
                    false,
                    candidate,
                    0m,
                    missingOosResult,
                    false,
                    candidate.HealthScore,
                    candidate.HealthScore,
                    candidate.HealthScore,
                    1.0,
                    0.05,
                    false,
                    true,
                    failureReason,
                    true,
                    candidate.HealthScore,
                    false,
                    candidate.HealthScore,
                    true,
                    !higherRegime.HasValue || OptimizationPolicyHelpers.IsRegimeCompatibleWithStrategy(strategy.StrategyType, higherRegime.Value),
                    true,
                    true,
                    0,
                    true,
                    0,
                    candidate.CvCoefficientOfVariation <= config.MaxCvCoefficientOfVariation,
                    candidate.CvCoefficientOfVariation,
                    OptimizationApprovalReportParser.Serialize(new OptimizationApprovalReportParser.ApprovalReport
                    {
                        Passed = false,
                        FailureReason = failureReason,
                        HasOosValidation = false,
                        HasSufficientOutOfSampleData = false,
                        InSampleHealthScore = candidate.HealthScore,
                        ExtensionData = new Dictionary<string, JsonElement>
                        {
                            ["gateBudgetTimeout"] = JsonSerializer.SerializeToElement(true)
                        }
                    }),
                    failureReason);
                _logger.LogInformation(
                    "OptimizationWorker: run {RunId} candidate #{Rank} gate budget expired ({Budget}s) — trying next candidate",
                    run.Id, candidateRank, gateTimeoutSeconds);
                _metrics.OptimizationGateRejections.Add(1, new KeyValuePair<string, object?>("gate", "gate_budget_timeout"));
                if (failedResults.Count < 3)
                    failedResults.Add((candidateRank, candidate.ParamsJson, failureReason, candidate.HealthScore));
                bestFailedResult ??= lastResult;
            }
        }

        // Should never reach here (rankedCandidates is non-empty), but satisfy the compiler
        return (bestFailedResult ?? lastResult)! with { FailedCandidates = failedResults };
    }

    internal Task<OptimizationThresholdAdjustmentEvaluator.ThresholdAdjustmentResult> ComputeThresholdAdjustmentsAsync(
        Strategy strategy,
        string candidateParamsJson,
        BacktestResult oosResult,
        List<Candle> testCandles,
        BacktestOptions screeningOptions,
        ValidationConfig config,
        CurrencyPair? pairInfo,
        long runId,
        CancellationToken ct)
        => _thresholdAdjustmentEvaluator.EvaluateAsync(
            strategy,
            candidateParamsJson,
            oosResult,
            testCandles,
            screeningOptions,
            config,
            pairInfo,
            runId,
            ct);

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

    // ── Extracted validation helpers ──────────────────────────────────────

    /// <summary>
    /// Computes bootstrap confidence interval with blending zone for low trade counts.
    /// Full empirical CI at 15+ trades, pure synthetic at 0-7, linear blend in between.
    /// </summary>
    internal static (decimal Lower, decimal Median, decimal Upper) ComputeBootstrapCI(
        BacktestResult oosResult, decimal oosHealthScore, decimal initialBalance,
        int bootstrapIterations, int seed)
    {
        int tradeCount = oosResult.Trades?.Count ?? 0;
        const int empiricalMinTrades = 15;
        const int syntheticMaxTrades = 7;

        if (tradeCount >= empiricalMinTrades)
        {
            return BootstrapAnalyzer.ComputeHealthScoreCI(
                oosResult.Trades!, initialBalance, bootstrapIterations, 0.95, seed);
        }

        if (tradeCount > syntheticMaxTrades)
        {
            // Blending zone (8-14 trades): interpolate between synthetic and empirical
            var (empLower, empMedian, empUpper) = BootstrapAnalyzer.ComputeHealthScoreCI(
                oosResult.Trades!, initialBalance, bootstrapIterations, 0.95, seed);

            double samplePenalty = 0.50 + 0.13 * Math.Min(tradeCount, 9) / 9.0;
            decimal synLower = oosHealthScore * (decimal)samplePenalty;

            decimal blendWeight = (decimal)(tradeCount - syntheticMaxTrades)
                                / (empiricalMinTrades - syntheticMaxTrades);
            return (
                synLower + blendWeight * (empLower - synLower),
                oosHealthScore + blendWeight * (empMedian - oosHealthScore),
                oosHealthScore + blendWeight * (empUpper - oosHealthScore));
        }

        // Pure synthetic CI for very low trade counts (0-7 trades)
        double penalty = 0.50 + 0.13 * Math.Min(tradeCount, syntheticMaxTrades) / (double)syntheticMaxTrades;
        return (oosHealthScore * (decimal)penalty, oosHealthScore, oosHealthScore);
    }

}
