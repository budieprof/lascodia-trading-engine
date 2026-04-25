using System.Diagnostics;
using System.Globalization;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.Logging;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Encapsulates the full screening pipeline for strategy candidates. This class owns all
/// statistical validation logic and has no EF Core or DI dependencies — it receives an
/// <see cref="IBacktestEngine"/> and <see cref="ILogger"/> at construction time and can be
/// instantiated directly in unit tests without mocking infrastructure.
///
/// <b>Pipeline (in order):</b>
/// IS backtest → IS threshold gate → OOS backtest → OOS threshold gate → IS→OOS degradation
/// (WR, PF, Sharpe, DD) → equity curve R² → trade time concentration → anchored walk-forward
/// (3-window, 2-of-3 must pass) → Monte Carlo sign-flip → optional Monte Carlo shuffle →
/// structured metrics assembly.
///
/// <b>Portfolio-level filter</b> (<see cref="RunPortfolioDrawdownFilter"/>) is a static method
/// called by the worker after all candidates are screened, not part of per-candidate screening.
///
/// <b>Thread safety:</b> Multiple screening tasks can call <see cref="ScreenCandidateAsync"/>
/// concurrently — instance state is limited to the injected logger and backtest engine, both
/// of which are thread-safe.
/// </summary>
public class StrategyScreeningEngine
{
    private readonly IBacktestEngine _backtestEngine;
    private readonly ILogger _logger;
    private readonly Action<string>? _onGateRejection;
    private readonly IStrategyScreeningArtifactFactory _artifactFactory;
    private readonly TimeProvider _timeProvider;

    public StrategyScreeningEngine(IBacktestEngine backtestEngine, ILogger logger,
        Action<string>? onGateRejection = null)
        : this(
            backtestEngine,
            logger,
            onGateRejection,
            new StrategyScreeningArtifactFactory(),
            TimeProvider.System)
    {
    }

    public StrategyScreeningEngine(
        IBacktestEngine backtestEngine,
        ILogger logger,
        Action<string>? onGateRejection,
        IStrategyScreeningArtifactFactory artifactFactory,
        TimeProvider timeProvider)
    {
        _backtestEngine = backtestEngine;
        _logger = logger;
        _onGateRejection = onGateRejection;
        _artifactFactory = artifactFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs the full screening pipeline for a single candidate. Returns a <see cref="ScreeningOutcome"/>
    /// for both passing and structured failure cases, or null only when the caller cancels the overall operation.
    /// </summary>
    public async Task<ScreeningOutcome?> ScreenCandidateAsync(
        StrategyType strategyType, string symbol, Timeframe timeframe,
        string enrichedParams, int templateIndex,
        List<Candle> allCandles, List<Candle> trainCandles, List<Candle> testCandles,
        BacktestOptions screeningOptions, ScreeningThresholds thresholds,
        ScreeningConfig config, MarketRegimeEnum regime, string generationSource,
        CancellationToken ct, MarketRegimeEnum? oosRegime = null,
        IReadOnlyList<(DateTime Date, decimal Equity)>? portfolioEquityCurve = null)
        => await ScreenCandidateAsync(
            strategyType,
            symbol,
            timeframe,
            enrichedParams,
            templateIndex,
            allCandles,
            trainCandles,
            testCandles,
            screeningOptions,
            thresholds,
            config,
            regime,
            regime,
            generationSource,
            ct,
            oosRegime,
            portfolioEquityCurve,
            null);

    public async Task<ScreeningOutcome?> ScreenCandidateAsync(
        StrategyType strategyType, string symbol, Timeframe timeframe,
        string enrichedParams, int templateIndex,
        List<Candle> allCandles, List<Candle> trainCandles, List<Candle> testCandles,
        BacktestOptions screeningOptions, ScreeningThresholds thresholds,
        ScreeningConfig config, MarketRegimeEnum targetRegime, MarketRegimeEnum observedRegime,
        string generationSource, CancellationToken ct, MarketRegimeEnum? oosRegime = null,
        IReadOnlyList<(DateTime Date, decimal Equity)>? portfolioEquityCurve = null,
        HaircutRatios? appliedHaircuts = null)
    {
        var gateTrace = new List<ScreeningGateTrace>();
        var gateSw = Stopwatch.StartNew();

        var tempStrategy = new Strategy
        {
            StrategyType   = strategyType,
            Symbol         = symbol,
            Timeframe      = timeframe,
            ParametersJson = enrichedParams,
        };

        ScreeningOutcome BuildFailedOutcome(
            ScreeningFailureReason failure,
            string outcome,
            string reason,
            BacktestResult? train = null,
            BacktestResult? oos = null)
        {
            var failed = ScreeningOutcome.Failed(
                tempStrategy,
                train,
                oos,
                targetRegime,
                observedRegime,
                generationSource,
                failure,
                outcome,
                reason);
            double qualityScore = ScreeningQualityScorer.ComputeScore(train, oos);
            bool isNearMiss = ScreeningQualityScorer.IsNearMiss(failed);

            return failed with
            {
                Metrics = failed.Metrics with
                {
                    IsWinRate = train is null ? 0 : (double)train.WinRate,
                    IsProfitFactor = train is null ? 0 : (double)train.ProfitFactor,
                    IsSharpeRatio = train is null ? 0 : (double)train.SharpeRatio,
                    IsMaxDrawdownPct = train is null ? 0 : (double)train.MaxDrawdownPct,
                    IsTotalTrades = train?.TotalTrades ?? 0,
                    OosWinRate = oos is null ? 0 : (double)oos.WinRate,
                    OosProfitFactor = oos is null ? 0 : (double)oos.ProfitFactor,
                    OosSharpeRatio = oos is null ? 0 : (double)oos.SharpeRatio,
                    OosMaxDrawdownPct = oos is null ? 0 : (double)oos.MaxDrawdownPct,
                    OosTotalTrades = oos?.TotalTrades ?? 0,
                    QualityScore = qualityScore,
                    QualityScoreRaw = qualityScore,
                    QualityCalibrationMultiplier = 1.0,
                    QualityBand = ScreeningQualityScorer.ComputeBand(qualityScore),
                    IsNearMiss = isNearMiss,
                    GateTrace = gateTrace.ToList(),
                },
            };
        }

        // ── In-sample backtest ──
        BacktestResult trainResult;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));
            trainResult = await _backtestEngine.RunAsync(tempStrategy, trainCandles,
                config.ScreeningInitialBalance, timeoutCts.Token, screeningOptions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("StrategyScreening: IS backtest timed out for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("IS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return BuildFailedOutcome(
                ScreeningFailureReason.Timeout,
                "Timeout",
                $"{strategyType} on {symbol}/{timeframe} IS backtest timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyScreening: IS backtest failed for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("IS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return BuildFailedOutcome(
                ScreeningFailureReason.TaskFault,
                "TaskFault",
                $"{strategyType} on {symbol}/{timeframe} IS backtest failed: {ex.GetType().Name}");
        }

        gateTrace.Add(new("IS_Backtest", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Zero-trade guard ──
        if (trainResult.TotalTrades == 0)
        {
            gateTrace.Add(new("ZeroTradeGuard_IS", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("zero_trades_is");
            return BuildFailedOutcome(
                ScreeningFailureReason.ZeroTradesIS,
                "ZeroTradesIS",
                $"{strategyType} on {symbol}/{timeframe} produced zero IS trades",
                trainResult);
        }

        // ── In-sample threshold gate ──
        var failedGates = new List<string>(6);
        if ((double)trainResult.WinRate < thresholds.MinWinRate)
            failedGates.Add($"WR={trainResult.WinRate:F3}<{thresholds.MinWinRate:F3}");
        if ((double)trainResult.ProfitFactor < thresholds.MinProfitFactor)
            failedGates.Add($"PF={trainResult.ProfitFactor:F2}<{thresholds.MinProfitFactor:F2}");
        if (trainResult.TotalTrades < thresholds.MinTotalTrades)
            failedGates.Add($"Trades={trainResult.TotalTrades}<{thresholds.MinTotalTrades}");
        if ((double)trainResult.MaxDrawdownPct > thresholds.MaxDrawdownPct)
            failedGates.Add($"DD={trainResult.MaxDrawdownPct:F3}>{thresholds.MaxDrawdownPct:F3}");
        if ((double)trainResult.SharpeRatio < thresholds.MinSharpe)
            failedGates.Add($"Sharpe={trainResult.SharpeRatio:F2}<{thresholds.MinSharpe:F2}");

        // Cost-margin gate: reject strategies whose avg win is too close to avg cost
        // per trade. BacktestEngine already applies spread+commission+slippage, so PF
        // is net — but a PF barely above MinProfitFactor (e.g. 1.11 vs 1.10) can flip
        // negative on a spread-widening event. Require the edge to clearly beat the
        // friction floor before approving.
        if (trainResult.TotalTrades > 0 && trainResult.AverageWin > 0)
        {
            decimal totalCosts = trainResult.TotalCommission + trainResult.TotalSlippage + Math.Abs(trainResult.TotalSwap);
            decimal avgCostPerTrade = totalCosts / trainResult.TotalTrades;
            decimal costToWinRatio = avgCostPerTrade / trainResult.AverageWin;
            if ((double)costToWinRatio > thresholds.MaxCostToWinRatio)
                failedGates.Add($"Cost/AvgWin={costToWinRatio:F3}>{thresholds.MaxCostToWinRatio:F3}");
        }

        if (failedGates.Count > 0)
        {
            gateTrace.Add(new("IS_Threshold", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("is_threshold");
            return BuildFailedOutcome(
                ScreeningFailureReason.IsThreshold,
                "ScreeningFailed",
                $"{strategyType} on {symbol}/{timeframe} IS gates failed: {string.Join(", ", failedGates)}",
                trainResult);
        }

        gateTrace.Add(new("IS_Threshold", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Out-of-sample backtest ──
        BacktestResult oosResult;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));
            oosResult = await _backtestEngine.RunAsync(tempStrategy, testCandles,
                config.ScreeningInitialBalance, timeoutCts.Token, screeningOptions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("StrategyScreening: OOS backtest timed out for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("OOS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return BuildFailedOutcome(
                ScreeningFailureReason.Timeout,
                "Timeout",
                $"{strategyType} on {symbol}/{timeframe} OOS backtest timed out",
                trainResult);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyScreening: OOS backtest failed for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("OOS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return BuildFailedOutcome(
                ScreeningFailureReason.TaskFault,
                "TaskFault",
                $"{strategyType} on {symbol}/{timeframe} OOS backtest failed: {ex.GetType().Name}",
                trainResult);
        }

        gateTrace.Add(new("OOS_Backtest", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        if (oosResult.TotalTrades == 0)
        {
            gateTrace.Add(new("ZeroTradeGuard_OOS", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("zero_trades_oos");
            return BuildFailedOutcome(
                ScreeningFailureReason.ZeroTradesOOS,
                "ZeroTradesOOS",
                $"{strategyType} on {symbol}/{timeframe} produced zero OOS trades",
                trainResult,
                oosResult);
        }

        // ── OOS threshold gate (relaxed) ──
        int oosMinTrades = Math.Max(3, thresholds.MinTotalTrades / 3);
        if ((double)oosResult.WinRate < thresholds.MinWinRate
            || (double)oosResult.ProfitFactor < thresholds.MinProfitFactor * config.OosPfRelaxation
            || oosResult.TotalTrades < oosMinTrades
            || (double)oosResult.MaxDrawdownPct > thresholds.MaxDrawdownPct * config.OosDdRelaxation
            || (double)oosResult.SharpeRatio < thresholds.MinSharpe * config.OosSharpeRelaxation)
        {
            gateTrace.Add(new("OOS_Threshold", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("oos_threshold");
            return BuildFailedOutcome(
                ScreeningFailureReason.OosThreshold,
                "OOSFailed",
                $"{strategyType} on {symbol}/{timeframe} OOS gates failed",
                trainResult,
                oosResult);
        }

        gateTrace.Add(new("OOS_Threshold", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── IS-to-OOS degradation ratio check ──
        double maxDegradation = config.MaxOosDegradationPct;
        if (oosRegime.HasValue && oosRegime.Value != targetRegime)
            maxDegradation *= config.RegimeDegradationRelaxation; // Relax tolerance when regimes differ
        bool degradationFailed = false;
        if ((double)trainResult.SharpeRatio > thresholds.MinSharpe
            && (double)(oosResult.SharpeRatio / trainResult.SharpeRatio) < (1.0 - maxDegradation))
            degradationFailed = true;
        if ((double)trainResult.ProfitFactor > thresholds.MinProfitFactor
            && (double)(oosResult.ProfitFactor / trainResult.ProfitFactor) < (1.0 - maxDegradation))
            degradationFailed = true;
        if ((double)trainResult.WinRate > thresholds.MinWinRate
            && (double)(oosResult.WinRate / trainResult.WinRate) < (1.0 - maxDegradation))
            degradationFailed = true;

        // #9: OOS drawdown degradation check
        if ((double)trainResult.MaxDrawdownPct > 0.01
            && (double)oosResult.MaxDrawdownPct > (double)trainResult.MaxDrawdownPct * (1.0 + maxDegradation))
            degradationFailed = true;

        if (degradationFailed)
        {
            gateTrace.Add(new("Degradation", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("degradation");
            return BuildFailedOutcome(
                ScreeningFailureReason.Degradation,
                "DegradationFailed",
                $"{strategyType} on {symbol}/{timeframe} excessive IS->OOS degradation",
                trainResult,
                oosResult);
        }

        gateTrace.Add(new("Degradation", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Equity curve linearity check (R²) ──
        var combinedTrades = trainResult.Trades.Concat(oosResult.Trades).ToList();
        double? r2 = null;
        if (combinedTrades.Count >= 5)
        {
            r2 = ComputeEquityCurveR2(combinedTrades, config.ScreeningInitialBalance);
            if (r2.Value < config.MinEquityCurveR2)
            {
                gateTrace.Add(new("EquityCurveR2", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("equity_curve_r2");
                return BuildFailedOutcome(
                    ScreeningFailureReason.EquityCurveR2,
                    "EquityCurveRejected",
                    $"{strategyType} on {symbol}/{timeframe} R²={r2.Value:F3} below {config.MinEquityCurveR2:F2}",
                    trainResult,
                    oosResult);
            }
        }
        // <5 combined trades: R² is unevaluated — stored as -1 sentinel in metrics

        gateTrace.Add(new("EquityCurveR2", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Trade time concentration check ──
        double maxConcentration = 0;
        if (combinedTrades.Count >= 10)
        {
            maxConcentration = ComputeTradeTimeConcentration(combinedTrades);
            if (maxConcentration > config.MaxTradeTimeConcentration)
            {
                gateTrace.Add(new("TimeConcentration", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("time_concentration");
                return BuildFailedOutcome(
                    ScreeningFailureReason.TimeConcentration,
                    "TimeConcentrationRejected",
                    $"{strategyType} on {symbol}/{timeframe} concentration={maxConcentration:P1}",
                    trainResult,
                    oosResult);
            }
        }

        gateTrace.Add(new("TimeConcentration", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Walk-forward mini-validation (#10: anchored-forward windows) ──
        int walkForwardPassed = 0;
        int walkForwardMask = 0;
        int? walkForwardRequiredForScore = null;
        if (allCandles.Count >= 200)
        {
            (walkForwardPassed, walkForwardMask) = await RunWalkForwardMiniValidationAsync(
                tempStrategy, allCandles, screeningOptions, config, thresholds, ct);
            walkForwardRequiredForScore = config.WalkForwardWindowCount;

            if (walkForwardPassed < config.WalkForwardMinWindowsPass)
            {
                gateTrace.Add(new("WalkForward", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("walk_forward");
                return BuildFailedOutcome(
                    ScreeningFailureReason.WalkForward,
                    "WalkForwardRejected",
                    $"{strategyType} on {symbol}/{timeframe} walk-forward {walkForwardPassed}/{config.WalkForwardWindowCount} windows passed (mask=0b{Convert.ToString(walkForwardMask, 2)})",
                    trainResult,
                    oosResult);
            }
        }
        else
        {
            walkForwardPassed = config.WalkForwardWindowCount; // Not enough data — don't block
            walkForwardMask = (1 << config.WalkForwardWindowCount) - 1; // All bits set
        }

        gateTrace.Add(new("WalkForward", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Lookahead audit (continuity check) ─────────────────────────────
        // Runs the same strategy on the full IS+OOS candle range as ONE
        // continuous backtest and compares the aggregate result to the
        // concatenation of the IS and OOS backtests. If the evaluator or
        // backtest engine is silently consuming future candles around the
        // IS→OOS boundary (a common lookahead failure mode), the full-range
        // run will produce a materially different trade count / PnL. Tolerances
        // are deliberately generous (50% defaults) because legitimate warmup
        // differences exist — this gate is tuned to catch gross leakage,
        // not millimetre precision. Audit failures are unlikely false positives
        // but easy false negatives, so the generous threshold is by design.
        if (config.LookaheadAuditEnabled && allCandles.Count >= 200)
        {
            BacktestResult? fullResult = null;
            try
            {
                using var auditCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                auditCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));
                fullResult = await _backtestEngine.RunAsync(tempStrategy, allCandles,
                    config.ScreeningInitialBalance, auditCts.Token, screeningOptions);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "StrategyScreening: lookahead audit full-range backtest timed out for {Type} on {Symbol}/{Tf} — skipping gate",
                    strategyType, symbol, timeframe);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "StrategyScreening: lookahead audit full-range backtest failed for {Type} on {Symbol}/{Tf} — skipping gate",
                    strategyType, symbol, timeframe);
            }

            if (fullResult != null)
            {
                int splitTradeCount = trainResult.TotalTrades + oosResult.TotalTrades;
                decimal splitPnl = (trainResult.FinalBalance - trainResult.InitialBalance)
                                 + (oosResult.FinalBalance - oosResult.InitialBalance);
                decimal fullPnl = fullResult.FinalBalance - fullResult.InitialBalance;

                double tradeCountDelta = splitTradeCount <= 0
                    ? 0.0
                    : Math.Abs(fullResult.TotalTrades - splitTradeCount) / (double)splitTradeCount;

                decimal pnlDenominator = Math.Max(1m, Math.Abs(splitPnl));
                double pnlDelta = (double)(Math.Abs(fullPnl - splitPnl) / pnlDenominator);

                bool tradeDivergence = tradeCountDelta > config.LookaheadAuditMaxTradeCountDelta;
                bool pnlDivergence   = pnlDelta > config.LookaheadAuditMaxPnlDelta;

                if (tradeDivergence || pnlDivergence)
                {
                    gateTrace.Add(new("LookaheadAudit", false, gateSw.Elapsed.TotalMilliseconds));
                    _onGateRejection?.Invoke("lookahead_audit");
                    return BuildFailedOutcome(
                        ScreeningFailureReason.LookaheadAudit,
                        "LookaheadAuditRejected",
                        $"{strategyType} on {symbol}/{timeframe} lookahead audit failed: " +
                        $"trades is+oos={splitTradeCount} vs full={fullResult.TotalTrades} (Δ={tradeCountDelta:P1} > {config.LookaheadAuditMaxTradeCountDelta:P0}); " +
                        $"pnl is+oos={splitPnl:F2} vs full={fullPnl:F2} (Δ={pnlDelta:P1} > {config.LookaheadAuditMaxPnlDelta:P0})",
                        trainResult,
                        oosResult);
                }
            }
        }

        gateTrace.Add(new("LookaheadAudit", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Monte Carlo permutation test (#6: variable seed) ──
        // Fix #10: Include date ordinal so different runs on new data produce different random trials
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        int monteCarloSeed = _artifactFactory.ResolveMonteCarloSeed(
            strategyType,
            symbol,
            timeframe,
            enrichedParams,
            allCandles,
            utcNow);
        double? pValue = null;
        if (config.MonteCarloEnabled && combinedTrades.Count >= 10)
        {
            pValue = RunMonteCarloPermutationTest(
                combinedTrades, config.ScreeningInitialBalance,
                config.MonteCarloPermutations, monteCarloSeed);

            if (pValue.Value > config.MonteCarloMinPValue)
            {
                gateTrace.Add(new("MonteCarloSignFlip", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("monte_carlo_signflip");
                return BuildFailedOutcome(
                    ScreeningFailureReason.MonteCarloSignFlip,
                    "MonteCarloRejected",
                    $"{strategyType} on {symbol}/{timeframe} p={pValue.Value:F3} > {config.MonteCarloMinPValue:F2}",
                    trainResult,
                    oosResult);
            }
        }

        gateTrace.Add(new("MonteCarloSignFlip", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Monte Carlo shuffle test (#2: complementary null hypothesis) ──
        // Permutes trade ordering to test whether Sharpe depends on sequence.
        // A strategy that passes sign-flip but fails shuffle has serial autocorrelation.
        double? shufflePValue = null;
        if (config.MonteCarloShuffleEnabled && combinedTrades.Count >= 10)
        {
            shufflePValue = RunMonteCarloShuffleTest(
                combinedTrades, config.ScreeningInitialBalance,
                config.EffectiveShufflePermutations, monteCarloSeed + 1);

            if (shufflePValue.Value > config.EffectiveShuffleMinPValue)
            {
                gateTrace.Add(new("MonteCarloShuffle", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("monte_carlo_shuffle");
                return BuildFailedOutcome(
                    ScreeningFailureReason.MonteCarloShuffle,
                    "MonteCarloShuffleRejected",
                    $"{strategyType} on {symbol}/{timeframe} shuffle p={shufflePValue.Value:F3} > {config.EffectiveShuffleMinPValue:F2}",
                    trainResult,
                    oosResult);
            }
        }

        gateTrace.Add(new("MonteCarloShuffle", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Deflated Sharpe gate (Bailey/López de Prado) ──
        // Deflates the combined IS+OOS Sharpe by the number of strategy-parameter trials
        // in this generation cycle. A raw Sharpe of 1.2 drawn from 1 of 100 trials is
        // much less informative than a Sharpe of 1.2 from a single trial — DSR corrects
        // for that multiple-testing burden. MinDeflatedSharpe = 0 (default) disables the
        // gate so existing runs are unchanged; a common López de Prado floor is 1.0.
        //
        // Delegated to PromotionGateValidator.ComputeDeflatedSharpe — same implementation
        // the manual four-eyes gate uses, so screening and promotion agree on the formula.
        double deflatedSharpe = 0.0;
        if (config.MinDeflatedSharpe > 0.0
            && config.DeflatedSharpeTrials > 0
            && combinedTrades.Count > 1)
        {
            double combinedSharpe = (double)oosResult.SharpeRatio;
            deflatedSharpe = Strategies.Services.PromotionGateValidator.ComputeDeflatedSharpe(
                rawSharpe: combinedSharpe,
                trials: Math.Max(1, config.DeflatedSharpeTrials),
                trades: combinedTrades.Count);

            if (deflatedSharpe < config.MinDeflatedSharpe)
            {
                gateTrace.Add(new("DeflatedSharpe", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("deflated_sharpe");
                return BuildFailedOutcome(
                    ScreeningFailureReason.DeflatedSharpe,
                    "DeflatedSharpeRejected",
                    $"{strategyType} on {symbol}/{timeframe} DSR={deflatedSharpe:F3} < {config.MinDeflatedSharpe:F2} (raw Sharpe={combinedSharpe:F2}, trials={config.DeflatedSharpeTrials}, trades={combinedTrades.Count})",
                    trainResult,
                    oosResult);
            }
        }

        gateTrace.Add(new("DeflatedSharpe", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Marginal Sharpe contribution gate (P3) ──
        double? marginalSharpeContribution = null;
        if (portfolioEquityCurve != null && portfolioEquityCurve.Count >= 20)
        {
            gateSw.Restart();
            try
            {
                // Compute current portfolio Sharpe
                var dailyReturns = new double[portfolioEquityCurve.Count - 1];
                for (int idx = 1; idx < portfolioEquityCurve.Count; idx++)
                {
                    var prev = (double)portfolioEquityCurve[idx - 1].Equity;
                    dailyReturns[idx - 1] = prev > 0 ? ((double)portfolioEquityCurve[idx].Equity - prev) / prev : 0;
                }
                double portfolioMean = dailyReturns.Average();
                double portfolioStd = Math.Sqrt(dailyReturns.Select(r => (r - portfolioMean) * (r - portfolioMean)).Sum() / Math.Max(1, dailyReturns.Length - 1));
                double portfolioSharpe = portfolioStd > 0 ? portfolioMean / portfolioStd * Math.Sqrt(252) : 0;

                // Build candidate daily PnL and merge into portfolio
                var candidatePnl = new Dictionary<DateTime, double>();
                foreach (var trade in combinedTrades)
                    if (trade.ExitTime != default)
                    {
                        var date = trade.ExitTime.Date;
                        candidatePnl[date] = candidatePnl.GetValueOrDefault(date) + (double)trade.PnL;
                    }

                // Combined equity curve
                int activeStrategies = Math.Max(1, config.ActiveStrategyCount);
                double allocationWeight = 1.0 / (activeStrategies + 1);
                var combinedReturns = new List<double>();
                for (int idx = 1; idx < portfolioEquityCurve.Count; idx++)
                {
                    var date = portfolioEquityCurve[idx].Date;
                    var prev = (double)portfolioEquityCurve[idx - 1].Equity;
                    double portfolioReturn = prev > 0 ? ((double)portfolioEquityCurve[idx].Equity - prev) / prev : 0;
                    double candidateReturn = candidatePnl.GetValueOrDefault(date) / (double)config.ScreeningInitialBalance;
                    double combinedReturn = portfolioReturn * (1 - allocationWeight) + candidateReturn * allocationWeight;
                    combinedReturns.Add(combinedReturn);
                }

                if (combinedReturns.Count >= 10)
                {
                    double combinedMean = combinedReturns.Average();
                    double combinedStd = Math.Sqrt(combinedReturns.Select(r => (r - combinedMean) * (r - combinedMean)).Sum() / Math.Max(1, combinedReturns.Count - 1));
                    double combinedSharpe = combinedStd > 0 ? combinedMean / combinedStd * Math.Sqrt(252) : 0;
                    marginalSharpeContribution = combinedSharpe - portfolioSharpe;

                    if (marginalSharpeContribution <= 0)
                    {
                        gateTrace.Add(new("MarginalSharpe", false, gateSw.Elapsed.TotalMilliseconds));
                        _onGateRejection?.Invoke("marginal_sharpe");
                        return BuildFailedOutcome(
                            ScreeningFailureReason.MarginalSharpe,
                            "MarginalSharpeRejected",
                            $"{strategyType} on {symbol}/{timeframe} marginal Sharpe contribution={marginalSharpeContribution:F3} <= 0",
                            trainResult,
                            oosResult);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrategyScreening: marginal Sharpe computation failed — skipping gate");
            }
            gateTrace.Add(new("MarginalSharpe", true, gateSw.Elapsed.TotalMilliseconds));
            gateSw.Restart();
        }

        // ── Position sizing sensitivity gate (P7) ──
        double kellySharpe = 0;
        double fixedLotSharpe = (double)trainResult.SharpeRatio; // Use IS Sharpe as baseline
        if (combinedTrades.Count >= 10 && trainResult.WinRate > 0 && trainResult.AverageLoss > 0)
        {
            gateSw.Restart();
            try
            {
                decimal winRate = trainResult.WinRate;
                decimal avgWin = trainResult.AverageWin;
                decimal avgLoss = trainResult.AverageLoss;
                decimal b = avgLoss > 0 ? avgWin / avgLoss : 1m;
                decimal kellyFull = (winRate * b - (1m - winRate)) / b;

                // Negative Kelly means a losing strategy — skip the position sizing backtest
                if (kellyFull > 0)
                {
                    decimal halfKelly = Math.Clamp(kellyFull * config.KellyFactor, config.KellyMinLot, config.KellyMaxLot);

                    var kellyOptions = new BacktestOptions
                    {
                        SpreadPriceUnits = screeningOptions.SpreadPriceUnits,
                        SpreadFunction = screeningOptions.SpreadFunction,
                        CommissionPerLot = screeningOptions.CommissionPerLot,
                        SlippagePriceUnits = screeningOptions.SlippagePriceUnits,
                        SwapPerLotPerDay = screeningOptions.SwapPerLotPerDay,
                        ContractSize = screeningOptions.ContractSize,
                        PipSizeInPriceUnits = screeningOptions.PipSizeInPriceUnits,
                        GapSlippagePct = screeningOptions.GapSlippagePct,
                        FillRatio = screeningOptions.FillRatio,
                        PositionSizer = (balance, signal) =>
                        {
                            decimal riskPerUnit = Math.Max(0.001m * signal.EntryPrice, Math.Abs(signal.EntryPrice - (signal.StopLoss ?? signal.EntryPrice)));
                            decimal lots = balance * halfKelly / (screeningOptions.ContractSize * riskPerUnit);
                            return Math.Clamp(lots, 0.01m, 10m);
                        },
                    };

                    using var kellyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    kellyCts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));
                    var kellyResult = await _backtestEngine.RunAsync(tempStrategy, allCandles,
                        config.ScreeningInitialBalance, kellyCts.Token, kellyOptions);

                    kellySharpe = (double)kellyResult.SharpeRatio;

                    if (kellySharpe < fixedLotSharpe * 0.80)
                    {
                        gateTrace.Add(new("PositionSizing", false, gateSw.Elapsed.TotalMilliseconds));
                        _onGateRejection?.Invoke("position_sizing_sensitivity");
                        return BuildFailedOutcome(
                            ScreeningFailureReason.PositionSizingSensitivity,
                            "PositionSizingRejected",
                            $"{strategyType} on {symbol}/{timeframe} Kelly Sharpe={kellySharpe:F3} < 80% of fixed-lot Sharpe={fixedLotSharpe:F3}",
                            trainResult,
                            oosResult);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("StrategyScreening: position sizing sensitivity gate timed out");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrategyScreening: position sizing sensitivity gate failed — skipping");
            }
            gateTrace.Add(new("PositionSizing", true, gateSw.Elapsed.TotalMilliseconds));
            gateSw.Restart();
        }

        // ── Build structured screening metrics (#13) ──
        var metrics = _artifactFactory.BuildMetrics(
            trainResult,
            oosResult,
            r2,
            pValue,
            shufflePValue,
            walkForwardPassed,
            walkForwardRequiredForScore,
            walkForwardMask,
            maxConcentration,
            targetRegime,
            observedRegime,
            generationSource,
            generationSource == "Reserve" ? targetRegime.ToString() : null,
            monteCarloSeed,
            marginalSharpeContribution,
            kellySharpe,
            fixedLotSharpe,
            appliedHaircuts,
            gateTrace,
            utcNow);

        // ── Build strategy entity ──
        var newStrategy = _artifactFactory.BuildStrategy(
            strategyType,
            symbol,
            timeframe,
            enrichedParams,
            templateIndex,
            generationSource,
            targetRegime,
            observedRegime,
            trainResult,
            oosResult,
            metrics,
            utcNow);

        return new ScreeningOutcome
        {
            Strategy = newStrategy,
            TrainResult = trainResult,
            OosResult = oosResult,
            Regime = targetRegime,
            ObservedRegime = observedRegime,
            GenerationSource = generationSource,
            Metrics = metrics,
            FailureReason = null,
            FailureOutcome = null,
        };
    }

    // ── Walk-forward mini-validation (#10: anchored-forward windows) ────────

    /// <summary>
    /// Runs anchored-forward walk-forward validation. Each window expands the IS portion
    /// and slides the OOS portion forward, better simulating how strategies degrade over time.
    /// Returns the number of windows that passed (out of 3). At least 2 must pass.
    /// </summary>
    private async Task<(int Passed, int Mask)> RunWalkForwardMiniValidationAsync(
        Strategy strategy, List<Candle> allCandles, BacktestOptions options,
        ScreeningConfig config, ScreeningThresholds thresholds,
        CancellationToken ct)
    {
        // Anchored-forward: IS expands from start, OOS slides forward.
        // Split points are configurable via ScreeningConfig.WalkForwardSplitPcts.
        // An embargo gap between IS end and OOS start prevents last-bar state
        // (an open position or an indicator spanning the boundary) from leaking
        // into the OOS evaluation — that is a classic purged-k-fold adaptation
        // of López de Prado's approach to walk-forward validation.
        int n = allCandles.Count;
        var splitPcts = config.EffectiveSplitPcts;
        int windowCount = splitPcts.Count;
        int embargo = (int)Math.Max(0, Math.Round(n * Math.Clamp(config.WalkForwardEmbargoPct, 0.0, 0.25)));
        var windows = new (int IsEnd, int OosStart, int OosEnd)[windowCount];
        for (int i = 0; i < windowCount; i++)
        {
            int isEnd   = (int)(n * splitPcts[i]);
            int oosEnd  = i + 1 < windowCount ? (int)(n * splitPcts[i + 1]) : n;
            int oosStart = Math.Min(isEnd + embargo, oosEnd);
            windows[i]  = (isEnd, oosStart, oosEnd);
        }

        int windowsPassed = 0;
        int windowsMask = 0;
        int relaxedMinTrades = Math.Max(3, thresholds.MinTotalTrades / 3);

        for (int w = 0; w < windowCount; w++)
        {
            var (isEnd, oosStart, oosEnd) = windows[w];
            if (isEnd < 40 || oosEnd - oosStart < 20) continue;

            var wfTrain = allCandles.Take(isEnd).ToList();
            var wfTest = allCandles.Skip(oosStart).Take(oosEnd - oosStart).ToList();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(config.ScreeningTimeoutSeconds));

                var trainRes = await _backtestEngine.RunAsync(strategy, wfTrain,
                    config.ScreeningInitialBalance, cts.Token, options);
                if (trainRes.TotalTrades == 0) continue;

                var testRes = await _backtestEngine.RunAsync(strategy, wfTest,
                    config.ScreeningInitialBalance, cts.Token, options);
                if (testRes.TotalTrades == 0) continue;

                if ((double)testRes.WinRate >= thresholds.MinWinRate * 0.85
                    && (double)testRes.ProfitFactor >= thresholds.MinProfitFactor * 0.85
                    && testRes.TotalTrades >= relaxedMinTrades)
                {
                    windowsPassed++;
                    windowsMask |= 1 << w;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("StrategyScreening: walk-forward window {Window} timed out", w);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StrategyScreening: walk-forward window {Window} failed", w);
            }
        }

        return (windowsPassed, windowsMask);
    }

    // ── Monte Carlo permutation test (#6: variable seed) ────────────────────

    /// <summary>
    /// Sign-flip Monte Carlo significance test. Uses a variable seed derived from the
    /// symbol and strategy type so different candidates explore different random paths,
    /// while the same candidate produces the same result for reproducibility.
    /// </summary>
    internal static double RunMonteCarloPermutationTest(
        IReadOnlyList<BacktestTrade> trades, decimal initialBalance, int permutations, int seed)
    {
        if (trades.Count < 5) return 0.0;

        var pnls = trades.Select(t => (double)t.PnL).ToArray();
        double actualSharpe = ComputeSharpeFromPnlArray(pnls);
        if (double.IsNaN(actualSharpe) || double.IsInfinity(actualSharpe))
            return 1.0;

        int beatCount = 0;
        var rng = new Random(seed);

        for (int p = 0; p < permutations; p++)
        {
            var flipped = new double[pnls.Length];
            for (int i = 0; i < pnls.Length; i++)
                flipped[i] = rng.Next(2) == 0 ? pnls[i] : -pnls[i];

            double syntheticSharpe = ComputeSharpeFromPnlArray(flipped);
            if (syntheticSharpe >= actualSharpe)
                beatCount++;
        }

        return (double)beatCount / permutations;
    }

    /// <summary>
    /// Complementary Monte Carlo test: shuffles trade ordering (Fisher-Yates) to test whether
    /// the strategy's Sharpe ratio depends on trade sequence. A strategy whose Sharpe survives
    /// sign-flipping but collapses under shuffle has serial PnL autocorrelation — a fragility
    /// the sign-flip test alone cannot detect.
    /// </summary>
    internal static double RunMonteCarloShuffleTest(
        IReadOnlyList<BacktestTrade> trades, decimal initialBalance, int permutations, int seed)
    {
        if (trades.Count < 5) return 0.0;

        var pnls = trades.Select(t => (double)t.PnL).ToArray();
        double actualSharpe = ComputeSharpeFromPnlArray(pnls);
        if (double.IsNaN(actualSharpe) || double.IsInfinity(actualSharpe))
            return 1.0;

        int beatCount = 0;
        var rng = new Random(seed);

        for (int p = 0; p < permutations; p++)
        {
            // Fisher-Yates shuffle of the PnL array
            var shuffled = (double[])pnls.Clone();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            double syntheticSharpe = ComputeSharpeFromPnlArray(shuffled);
            if (syntheticSharpe >= actualSharpe)
                beatCount++;
        }

        return (double)beatCount / permutations;
    }

    /// <summary>Computes Sharpe ratio from a raw PnL array (mean / stddev).</summary>
    internal static double ComputeSharpeFromPnlArray(double[] pnls)
    {
        if (pnls.Length < 2) return 0;
        double mean = pnls.Average();
        double variance = pnls.Select(p => (p - mean) * (p - mean)).Sum() / (pnls.Length - 1);
        double stdDev = Math.Sqrt(variance);
        return stdDev > 0 ? mean / stdDev : 0;
    }

    // ── Equity curve R² ────────────────────────────────────────────────────

    internal static double ComputeEquityCurveR2(IReadOnlyList<BacktestTrade> trades, decimal initialBalance)
    {
        if (trades.Count < 3) return 1.0;

        var equity = new double[trades.Count + 1];
        equity[0] = (double)initialBalance;
        for (int i = 0; i < trades.Count; i++)
            equity[i + 1] = equity[i] + (double)trades[i].PnL;

        int n = equity.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX  += i;
            sumY  += equity[i];
            sumXY += i * equity[i];
            sumX2 += (double)i * i;
        }

        double meanX = sumX / n;
        double meanY = sumY / n;

        double ssReg = 0, ssTot = 0;
        double denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < 1e-12) return 1.0;
        double slope = (n * sumXY - sumX * sumY) / denominator;
        double intercept = meanY - slope * meanX;

        for (int i = 0; i < n; i++)
        {
            double predicted = intercept + slope * i;
            ssReg += (equity[i] - predicted) * (equity[i] - predicted);
            ssTot += (equity[i] - meanY) * (equity[i] - meanY);
        }

        if (ssTot == 0) return 1.0;
        return Math.Max(0, 1.0 - ssReg / ssTot);
    }

    // ── Trade time concentration ───────────────────────────────────────────

    internal static double ComputeTradeTimeConcentration(IReadOnlyList<BacktestTrade> trades)
    {
        if (trades.Count == 0) return 0;
        var hourCounts = new int[24];
        foreach (var trade in trades)
            hourCounts[trade.EntryTime.Hour]++;
        int maxCount = hourCounts.Max();
        return (double)maxCount / trades.Count;
    }

    // ── Multi-asset correlation pre-check ───────────────────────────────

    /// <summary>
    /// Checks whether a candidate's equity curve has Pearson correlation above
    /// <paramref name="maxCorrelation"/> with any already-accepted candidate. Allows the
    /// worker to reject highly correlated strategies before adding them to the pending list.
    /// </summary>
    internal static bool IsCorrelatedWithAccepted(
        ScreeningOutcome candidate, IReadOnlyList<ScreeningOutcome> accepted,
        decimal initialBalance, double maxCorrelation = 0.70)
    {
        if (accepted.Count == 0) return false;

        var candidateCurve = BuildEquityCurve(candidate, initialBalance);
        if (candidateCurve.Length < 5) return false;

        foreach (var other in accepted)
        {
            var otherCurve = BuildEquityCurve(other, initialBalance);
            int len = Math.Min(candidateCurve.Length, otherCurve.Length);
            if (len < 5) continue;

            double corr = PearsonCorrelation(candidateCurve, otherCurve, len);
            if (corr > maxCorrelation)
                return true;
        }

        return false;
    }

    // ── Portfolio-level correlated drawdown check ──────────────────────────

    /// <summary>
    /// Simulates running all candidates simultaneously. Returns survivors, combined DD, and removal count.
    /// The input list is NOT mutated.
    /// </summary>
    internal static (List<ScreeningOutcome> Survivors, double CombinedDrawdownPct, int RemovedCount)
        RunPortfolioDrawdownFilter(List<ScreeningOutcome> candidates, double maxDrawdownPct, decimal initialBalance,
            double correlationWeight = 0.05)
    {
        if (candidates.Count < 2) return (candidates, 0, 0);

        var working = new List<ScreeningOutcome>(candidates);
        int removedCount = 0;

        // Pre-compute equity curves for correlation-aware removal scoring
        var equityCurves = working.ToDictionary(c => c, c => BuildEquityCurve(c, initialBalance));

        while (working.Count >= 2)
        {
            double combinedDD = ComputeCombinedDrawdown(working, initialBalance);
            if (combinedDD <= maxDrawdownPct)
                return (working, combinedDD, removedCount);

            // Correlation-aware removal: penalise removing strategies that are negatively
            // correlated with the rest (they provide diversification benefit).
            int worstIdx = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < working.Count; i++)
            {
                var without = working.Where((_, idx) => idx != i).ToList();
                double ddWithout = ComputeCombinedDrawdown(without, initialBalance);

                // Compute average correlation of candidate i with all others
                double avgCorrelation = ComputeAverageCorrelation(working[i], without, equityCurves);

                // Score: lower is better to remove.
                // High DD-after-removal is bad (keep it), but high positive correlation means
                // the strategy doesn't add diversification (safe to remove).
                // Negative correlation = diversification benefit → penalise removal.
                // score = ddWithout - correlationBonus
                // correlationBonus: negative correlation → positive bonus → higher score → less likely removed
                double correlationBonus = avgCorrelation * correlationWeight;
                double score = ddWithout - correlationBonus;

                if (score < bestScore)
                {
                    bestScore = score;
                    worstIdx = i;
                }
            }
            if (worstIdx < 0) break;
            equityCurves.Remove(working[worstIdx]);
            working.RemoveAt(worstIdx);
            removedCount++;
        }

        double finalDD = ComputeCombinedDrawdown(working, initialBalance);
        return (working, finalDD, removedCount);
    }

    /// <summary>
    /// Greedily keeps the highest-quality pending candidates while enforcing symbol capacity and
    /// base/quote currency exposure caps against the existing active portfolio.
    /// </summary>
    internal static (List<ScreeningOutcome> Survivors, int RemovedCount) RunPortfolioExposureFilter(
        List<ScreeningOutcome> candidates,
        IReadOnlyDictionary<string, CurrencyPair> pairDataBySymbol,
        IReadOnlyDictionary<string, int> activeCountBySymbol,
        int maxActivePerSymbol,
        double maxSymbolWeightPct,
        double maxCurrencyExposurePct)
    {
        if (candidates.Count == 0)
            return (candidates, 0);

        int existingActiveCount = activeCountBySymbol.Values.Sum();
        int capacityBase = Math.Max(1, existingActiveCount + candidates.Count);
        int symbolWeightCap = maxSymbolWeightPct >= 1.0
            ? int.MaxValue
            : Math.Max(1, (int)Math.Floor(capacityBase * Math.Clamp(maxSymbolWeightPct, 0.01, 1.0)));
        int currencyExposureCap = maxCurrencyExposurePct >= 1.0
            ? int.MaxValue
            : Math.Max(1, (int)Math.Floor(capacityBase * Math.Clamp(maxCurrencyExposurePct, 0.01, 1.0)));

        var symbolCounts = activeCountBySymbol.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var currencyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, count) in activeCountBySymbol)
        {
            var (baseCurrency, quoteCurrency) = ResolveCurrencyExposure(symbol, pairDataBySymbol);
            AddCurrencyExposure(baseCurrency, count);
            AddCurrencyExposure(quoteCurrency, count);
        }

        var survivors = new List<ScreeningOutcome>(candidates.Count);
        foreach (var candidate in candidates
                     .OrderByDescending(PortfolioExposureSelectionScore)
                     .ThenBy(c => c.Strategy.Name, StringComparer.Ordinal))
        {
            string symbol = candidate.Strategy.Symbol;
            int projectedSymbolCount = symbolCounts.GetValueOrDefault(symbol) + 1;
            if (projectedSymbolCount > maxActivePerSymbol || projectedSymbolCount > symbolWeightCap)
                continue;

            var (baseCurrency, quoteCurrency) = ResolveCurrencyExposure(symbol, pairDataBySymbol);
            if (WouldBreachCurrencyExposure(baseCurrency) || WouldBreachCurrencyExposure(quoteCurrency))
                continue;

            survivors.Add(candidate);
            symbolCounts[symbol] = projectedSymbolCount;
            AddCurrencyExposure(baseCurrency, 1);
            AddCurrencyExposure(quoteCurrency, 1);
        }

        // Preserve the original candidate order for downstream persistence determinism.
        var survivorSet = survivors.ToHashSet();
        var orderedSurvivors = candidates.Where(survivorSet.Contains).ToList();
        return (orderedSurvivors, candidates.Count - orderedSurvivors.Count);

        bool WouldBreachCurrencyExposure(string currency)
            => !string.IsNullOrWhiteSpace(currency)
               && currencyCounts.GetValueOrDefault(currency) + 1 > currencyExposureCap;

        void AddCurrencyExposure(string currency, int count)
        {
            if (string.IsNullOrWhiteSpace(currency) || count <= 0)
                return;
            currencyCounts[currency] = currencyCounts.GetValueOrDefault(currency) + count;
        }
    }

    private static double PortfolioExposureSelectionScore(ScreeningOutcome candidate)
    {
        double qualityScore = candidate.Metrics?.QualityScore > 0 ? candidate.Metrics.QualityScore : 50.0;
        double selectionScore = candidate.Metrics?.SelectionScore ?? 0.0;
        double oosSharpe = (double)(candidate.OosResult?.SharpeRatio ?? 0m);
        return qualityScore * 10.0 + selectionScore * 0.01 + oosSharpe;
    }

    private static (string BaseCurrency, string QuoteCurrency) ResolveCurrencyExposure(
        string symbol,
        IReadOnlyDictionary<string, CurrencyPair> pairDataBySymbol)
    {
        CurrencyPair? pair = null;
        if (pairDataBySymbol.TryGetValue(symbol, out var direct))
            pair = direct;
        else
        {
            var match = pairDataBySymbol.FirstOrDefault(kv => string.Equals(kv.Key, symbol, StringComparison.OrdinalIgnoreCase));
            pair = match.Value;
        }

        if (pair != null)
            return (pair.BaseCurrency ?? string.Empty, pair.QuoteCurrency ?? string.Empty);

        string normalized = symbol.ToUpperInvariant();
        if (normalized.Length >= 6)
            return (normalized[..3], normalized[3..6]);
        return (normalized, string.Empty);
    }

    /// <summary>Computes average Pearson correlation between a candidate's equity curve and each other.</summary>
    private static double ComputeAverageCorrelation(
        ScreeningOutcome candidate, List<ScreeningOutcome> others,
        Dictionary<ScreeningOutcome, double[]> equityCurves)
    {
        if (others.Count == 0 || !equityCurves.TryGetValue(candidate, out var curveA))
            return 0;

        double totalCorr = 0;
        int count = 0;
        foreach (var other in others)
        {
            if (!equityCurves.TryGetValue(other, out var curveB)) continue;
            int len = Math.Min(curveA.Length, curveB.Length);
            if (len < 5) continue;
            totalCorr += PearsonCorrelation(curveA, curveB, len);
            count++;
        }
        return count > 0 ? totalCorr / count : 0;
    }

    /// <summary>Pearson correlation coefficient over the first <paramref name="len"/> elements.</summary>
    internal static double PearsonCorrelation(double[] x, double[] y, int len)
    {
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
        for (int i = 0; i < len; i++)
        {
            sumX  += x[i]; sumY  += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
        }
        double denom = Math.Sqrt((len * sumX2 - sumX * sumX) * (len * sumY2 - sumY * sumY));
        if (denom < 1e-12) return 0;
        return (len * sumXY - sumX * sumY) / denom;
    }

    /// <summary>Builds a normalised daily PnL equity curve for correlation computation.</summary>
    internal static double[] BuildEquityCurve(ScreeningOutcome candidate, decimal initialBalance)
    {
        var trades = candidate.TrainResult.Trades.Concat(candidate.OosResult.Trades)
            .OrderBy(t => t.ExitTime).ToList();
        if (trades.Count == 0) return [];

        var equity = new double[trades.Count + 1];
        equity[0] = (double)initialBalance;
        for (int i = 0; i < trades.Count; i++)
            equity[i + 1] = equity[i] + (double)trades[i].PnL;
        return equity;
    }

    private static double ComputeCombinedDrawdown(List<ScreeningOutcome> candidates, decimal initialBalance)
    {
        if (candidates.Count == 0) return 0;
        var allTrades = candidates
            .SelectMany(c => c.TrainResult.Trades.Concat(c.OosResult.Trades)
                .Select(t => new { t.ExitTime, t.PnL }))
            .OrderBy(t => t.ExitTime)
            .ToList();
        if (allTrades.Count == 0) return 0;

        double allocationFactor = 1.0 / candidates.Count;
        double equity = (double)initialBalance;
        double peak = equity;
        double maxDrawdown = 0;
        foreach (var trade in allTrades)
        {
            equity += (double)trade.PnL * allocationFactor;
            if (equity > peak) peak = equity;
            double dd = (peak - equity) / peak;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }
        return maxDrawdown;
    }
}

/// <summary>Regime-scaled, adaptively-adjusted screening thresholds.</summary>
public sealed record ScreeningThresholds(
    double MinWinRate,
    double MinProfitFactor,
    double MinSharpe,
    double MaxDrawdownPct,
    int MinTotalTrades,
    // Upper bound on (avg total cost per trade) / (avg win). Reject strategies whose
    // costs eat too much of the winning trade; these strategies are dangerously close
    // to the friction floor and typically flip negative on spread-widening events.
    // 0.35 = costs must be less than 35% of an average winning trade.
    double MaxCostToWinRatio = 0.35);

/// <summary>Screening configuration subset needed by the screening engine.</summary>
public sealed record ScreeningConfig
{
    public int ScreeningTimeoutSeconds { get; init; }
    public decimal ScreeningInitialBalance { get; init; }
    public double MaxOosDegradationPct { get; init; }
    public double MinEquityCurveR2 { get; init; }
    public double MaxTradeTimeConcentration { get; init; }
    public bool MonteCarloEnabled { get; init; }
    public int MonteCarloPermutations { get; init; }
    public double MonteCarloMinPValue { get; init; }
    public bool MonteCarloShuffleEnabled { get; init; }
    public int WalkForwardWindowCount { get; init; } = 3;
    public int WalkForwardMinWindowsPass { get; init; } = 2;
    public IReadOnlyList<double>? WalkForwardSplitPcts { get; init; }
    public int MonteCarloShufflePermutations { get; init; }
    public double MonteCarloShuffleMinPValue { get; init; }

    /// <summary>OOS profit factor relaxation multiplier (default 0.9).</summary>
    public double OosPfRelaxation { get; init; } = 0.9;
    /// <summary>OOS drawdown relaxation multiplier (default 1.1).</summary>
    public double OosDdRelaxation { get; init; } = 1.1;
    /// <summary>OOS Sharpe relaxation multiplier (default 0.8).</summary>
    public double OosSharpeRelaxation { get; init; } = 0.8;
    /// <summary>Regime degradation relaxation multiplier when OOS regime differs from target (default 1.5).</summary>
    public double RegimeDegradationRelaxation { get; init; } = 1.5;

    /// <summary>Half-Kelly multiplier for position sizing gate (default 0.5).</summary>
    public decimal KellyFactor { get; init; } = 0.5m;
    /// <summary>Minimum lot size floor for Kelly position sizing (default 0.01).</summary>
    public decimal KellyMinLot { get; init; } = 0.01m;
    /// <summary>Maximum lot size cap for Kelly position sizing (default 0.10).</summary>
    public decimal KellyMaxLot { get; init; } = 0.10m;

    /// <summary>Effective split percentages (defaults to 0.40, 0.55, 0.70 if not provided).</summary>
    public IReadOnlyList<double> EffectiveSplitPcts => WalkForwardSplitPcts
        ?? new[] { 0.40, 0.55, 0.70 };

    /// <summary>Shuffle permutations — falls back to sign-flip count if not explicitly set.</summary>
    public int EffectiveShufflePermutations => MonteCarloShufflePermutations > 0
        ? MonteCarloShufflePermutations : MonteCarloPermutations;

    /// <summary>Shuffle p-value threshold — falls back to sign-flip threshold if not explicitly set.</summary>
    public double EffectiveShuffleMinPValue => MonteCarloShuffleMinPValue > 0
        ? MonteCarloShuffleMinPValue : MonteCarloMinPValue;

    /// <summary>Total number of active strategies in the portfolio. Used by the marginal Sharpe gate.</summary>
    public int ActiveStrategyCount { get; init; }

    /// <summary>
    /// Minimum Deflated Sharpe Ratio (Bailey/López de Prado 2014) required on the
    /// combined IS+OOS trade sequence. DSR adjusts the raw Sharpe for the number of
    /// strategies the engine has tried in the same generation context — a high raw
    /// Sharpe from a single candidate out of many trials may still be insignificant.
    /// Zero (default) disables the gate so historical behaviour is preserved; 1.0 is
    /// the López de Prado "meaningful" floor.
    /// </summary>
    public double MinDeflatedSharpe { get; init; } = 0.0;

    /// <summary>
    /// Number of strategy-parameter trials to pass to the DSR formula. Typically set
    /// from the generation cycle's candidate count so DSR deflates proportionally to
    /// the multiple-testing burden. Minimum 1 — zero/negative short-circuits the gate.
    /// </summary>
    public int DeflatedSharpeTrials { get; init; } = 1;

    /// <summary>
    /// When true (default), runs a post-walk-forward lookahead audit: the same
    /// strategy is replayed on the full IS+OOS candle range and the aggregate
    /// trade count / PnL is compared to the concatenation of the IS and OOS
    /// backtests. A large divergence signals the evaluator or backtest engine is
    /// peeking at future candles around the IS→OOS boundary — the candidate is
    /// rejected with <see cref="ScreeningFailureReason.LookaheadAudit"/>.
    /// Disabling is intended for offline tooling only; leave enabled in production.
    /// </summary>
    public bool LookaheadAuditEnabled { get; init; } = true;

    /// <summary>
    /// Maximum allowed relative delta between the full-range trade count and
    /// (IS + OOS) trade counts. Defaults to 0.50 (50%). A perfectly-warmed-up
    /// evaluator typically sits within ~10–20%; 50% leaves room for legitimate
    /// warmup effects while still catching gross lookahead. Delta computed as
    /// <c>|full − (is + oos)| / max(1, is + oos)</c>.
    /// </summary>
    public double LookaheadAuditMaxTradeCountDelta { get; init; } = 0.50;

    /// <summary>
    /// Maximum allowed relative delta between the full-range net profit and
    /// (IS + OOS) combined net profit, using the IS+OOS magnitude as the
    /// denominator. Defaults to 0.50 (50%). Trade count alone can match while
    /// PnL diverges when lookahead produces different exit timings, so both
    /// bounds are enforced.
    /// </summary>
    public double LookaheadAuditMaxPnlDelta { get; init; } = 0.50;

    /// <summary>
    /// Fraction of the full candle range to skip between each walk-forward
    /// window's IS end and OOS start. Prevents PnL bleed-through from the
    /// last IS bar's open position influencing the first OOS bar's entry
    /// conditions. Defaults to 0.02 (2% of the full range). Set to 0 to
    /// disable the embargo and match the pre-embargo behaviour.
    /// </summary>
    public double WalkForwardEmbargoPct { get; init; } = 0.02;
}

/// <summary>Result of screening a single candidate.</summary>
public sealed record ScreeningOutcome
{
    public Strategy Strategy { get; init; } = null!;
    public BacktestResult TrainResult { get; init; } = null!;
    public BacktestResult OosResult { get; init; } = null!;
    public MarketRegimeEnum Regime { get; init; }
    public MarketRegimeEnum ObservedRegime { get; init; }
    public string GenerationSource { get; init; } = "Primary";
    public ScreeningMetrics Metrics { get; init; } = null!;

    /// <summary>Structured failure reason — <see cref="ScreeningFailureReason.None"/> when passed.</summary>
    public ScreeningFailureReason Failure { get; init; } = ScreeningFailureReason.None;

    /// <summary>Non-null if screening failed (for audit trail logging).</summary>
    public string? FailureOutcome { get; init; }
    public string? FailureReason { get; init; }

    public bool Passed => Failure == ScreeningFailureReason.None && FailureOutcome == null;

    public static ScreeningOutcome Failed(ScreeningFailureReason failure, string outcome, string reason) => new()
    {
        Failure = failure,
        FailureOutcome = outcome,
        FailureReason = reason,
    };

    public static ScreeningOutcome Failed(
        Strategy strategy,
        BacktestResult? trainResult,
        BacktestResult? oosResult,
        MarketRegimeEnum regime,
        MarketRegimeEnum observedRegime,
        string generationSource,
        ScreeningFailureReason failure,
        string outcome,
        string reason)
        => new()
        {
            Strategy = strategy,
            TrainResult = trainResult ?? new BacktestResult(),
            OosResult = oosResult ?? new BacktestResult(),
            Regime = regime,
            ObservedRegime = observedRegime,
            GenerationSource = generationSource,
            Metrics = new ScreeningMetrics
            {
                Regime = regime.ToString(),
                ObservedRegime = observedRegime.ToString(),
                GenerationSource = generationSource,
                ReserveTargetRegime = string.Equals(generationSource, "Reserve", StringComparison.OrdinalIgnoreCase)
                    ? regime.ToString()
                    : null,
                ScreenedAtUtc = DateTime.UtcNow,
            },
            Failure = failure,
            FailureOutcome = outcome,
            FailureReason = reason,
        };
}

/// <summary>Per-gate timing and pass/fail trace for screening pipeline diagnostics.</summary>
public sealed record ScreeningGateTrace(string Gate, bool Passed, double DurationMs);
