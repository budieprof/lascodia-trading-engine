using System.Diagnostics;
using System.Globalization;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
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

    public StrategyScreeningEngine(IBacktestEngine backtestEngine, ILogger logger,
        Action<string>? onGateRejection = null)
    {
        _backtestEngine = backtestEngine;
        _logger = logger;
        _onGateRejection = onGateRejection;
    }

    /// <summary>
    /// Runs the full screening pipeline for a single candidate. Returns a <see cref="ScreeningOutcome"/>
    /// if the candidate passes all gates, or null if it fails any gate.
    /// </summary>
    public async Task<ScreeningOutcome?> ScreenCandidateAsync(
        StrategyType strategyType, string symbol, Timeframe timeframe,
        string enrichedParams, int templateIndex,
        List<Candle> allCandles, List<Candle> trainCandles, List<Candle> testCandles,
        BacktestOptions screeningOptions, ScreeningThresholds thresholds,
        ScreeningConfig config, MarketRegimeEnum regime, string generationSource,
        CancellationToken ct, MarketRegimeEnum? oosRegime = null,
        IReadOnlyList<(DateTime Date, decimal Equity)>? portfolioEquityCurve = null)
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
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyScreening: IS backtest failed for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("IS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return null;
        }

        gateTrace.Add(new("IS_Backtest", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Zero-trade guard ──
        if (trainResult.TotalTrades == 0) { gateTrace.Add(new("ZeroTradeGuard_IS", false, gateSw.Elapsed.TotalMilliseconds)); _onGateRejection?.Invoke("zero_trades_is"); return null; }

        // ── In-sample threshold gate ──
        if ((double)trainResult.WinRate < thresholds.MinWinRate
            || (double)trainResult.ProfitFactor < thresholds.MinProfitFactor
            || trainResult.TotalTrades < thresholds.MinTotalTrades
            || (double)trainResult.MaxDrawdownPct > thresholds.MaxDrawdownPct
            || (double)trainResult.SharpeRatio < thresholds.MinSharpe)
        {
            gateTrace.Add(new("IS_Threshold", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("is_threshold");
            return ScreeningOutcome.Failed(ScreeningFailureReason.IsThreshold, "ScreeningFailed",
                $"{strategyType} on {symbol}/{timeframe} IS gates failed");
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
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StrategyScreening: OOS backtest failed for {Type} on {Symbol}/{Tf}",
                strategyType, symbol, timeframe);
            gateTrace.Add(new("OOS_Backtest", false, gateSw.Elapsed.TotalMilliseconds));
            return null;
        }

        gateTrace.Add(new("OOS_Backtest", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        if (oosResult.TotalTrades == 0) { gateTrace.Add(new("ZeroTradeGuard_OOS", false, gateSw.Elapsed.TotalMilliseconds)); _onGateRejection?.Invoke("zero_trades_oos"); return null; }

        // ── OOS threshold gate (relaxed) ──
        int oosMinTrades = Math.Max(3, thresholds.MinTotalTrades / 3);
        if ((double)oosResult.WinRate < thresholds.MinWinRate
            || (double)oosResult.ProfitFactor < thresholds.MinProfitFactor * 0.9
            || oosResult.TotalTrades < oosMinTrades
            || (double)oosResult.MaxDrawdownPct > thresholds.MaxDrawdownPct * 1.1
            || (double)oosResult.SharpeRatio < thresholds.MinSharpe * 0.8)
        {
            gateTrace.Add(new("OOS_Threshold", false, gateSw.Elapsed.TotalMilliseconds));
            _onGateRejection?.Invoke("oos_threshold");
            return ScreeningOutcome.Failed(ScreeningFailureReason.OosThreshold, "OOSFailed",
                $"{strategyType} on {symbol}/{timeframe} OOS gates failed");
        }

        gateTrace.Add(new("OOS_Threshold", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── IS-to-OOS degradation ratio check ──
        double maxDegradation = config.MaxOosDegradationPct;
        if (oosRegime.HasValue && oosRegime.Value != regime)
            maxDegradation *= 1.5; // Relax tolerance when regimes differ
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
            return ScreeningOutcome.Failed(ScreeningFailureReason.Degradation, "DegradationFailed",
                $"{strategyType} on {symbol}/{timeframe} excessive IS->OOS degradation");
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
                return ScreeningOutcome.Failed(ScreeningFailureReason.EquityCurveR2, "EquityCurveRejected",
                    $"{strategyType} on {symbol}/{timeframe} R²={r2.Value:F3} below {config.MinEquityCurveR2:F2}");
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
                return ScreeningOutcome.Failed(ScreeningFailureReason.TimeConcentration, "TimeConcentrationRejected",
                    $"{strategyType} on {symbol}/{timeframe} concentration={maxConcentration:P1}");
            }
        }

        gateTrace.Add(new("TimeConcentration", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Walk-forward mini-validation (#10: anchored-forward windows) ──
        int walkForwardPassed = 0;
        int walkForwardMask = 0;
        if (allCandles.Count >= 200)
        {
            (walkForwardPassed, walkForwardMask) = await RunWalkForwardMiniValidationAsync(
                tempStrategy, allCandles, screeningOptions, config, thresholds, ct);

            if (walkForwardPassed < config.WalkForwardMinWindowsPass)
            {
                gateTrace.Add(new("WalkForward", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("walk_forward");
                return ScreeningOutcome.Failed(ScreeningFailureReason.WalkForward, "WalkForwardRejected",
                    $"{strategyType} on {symbol}/{timeframe} walk-forward {walkForwardPassed}/{config.WalkForwardWindowCount} windows passed (mask=0b{Convert.ToString(walkForwardMask, 2)})");
            }
        }
        else
        {
            walkForwardPassed = config.WalkForwardWindowCount; // Not enough data — don't block
            walkForwardMask = (1 << config.WalkForwardWindowCount) - 1; // All bits set
        }

        gateTrace.Add(new("WalkForward", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Monte Carlo permutation test (#6: variable seed) ──
        // Fix #10: Include date ordinal so different runs on new data produce different random trials
        int monteCarloSeed = DateTime.UtcNow.DayOfYear ^ DateTime.UtcNow.Year
            ^ symbol.GetHashCode() ^ (int)strategyType;
        double pValue = 0;
        if (config.MonteCarloEnabled && combinedTrades.Count >= 10)
        {
            pValue = RunMonteCarloPermutationTest(
                combinedTrades, config.ScreeningInitialBalance,
                config.MonteCarloPermutations, monteCarloSeed);

            if (pValue > config.MonteCarloMinPValue)
            {
                gateTrace.Add(new("MonteCarloSignFlip", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("monte_carlo_signflip");
                return ScreeningOutcome.Failed(ScreeningFailureReason.MonteCarloSignFlip, "MonteCarloRejected",
                    $"{strategyType} on {symbol}/{timeframe} p={pValue:F3} > {config.MonteCarloMinPValue:F2}");
            }
        }

        gateTrace.Add(new("MonteCarloSignFlip", true, gateSw.Elapsed.TotalMilliseconds));
        gateSw.Restart();

        // ── Monte Carlo shuffle test (#2: complementary null hypothesis) ──
        // Permutes trade ordering to test whether Sharpe depends on sequence.
        // A strategy that passes sign-flip but fails shuffle has serial autocorrelation.
        double shufflePValue = 0;
        if (config.MonteCarloShuffleEnabled && combinedTrades.Count >= 10)
        {
            shufflePValue = RunMonteCarloShuffleTest(
                combinedTrades, config.ScreeningInitialBalance,
                config.EffectiveShufflePermutations, monteCarloSeed + 1);

            if (shufflePValue > config.EffectiveShuffleMinPValue)
            {
                gateTrace.Add(new("MonteCarloShuffle", false, gateSw.Elapsed.TotalMilliseconds));
                _onGateRejection?.Invoke("monte_carlo_shuffle");
                return ScreeningOutcome.Failed(ScreeningFailureReason.MonteCarloShuffle, "MonteCarloShuffleRejected",
                    $"{strategyType} on {symbol}/{timeframe} shuffle p={shufflePValue:F3} > {config.EffectiveShuffleMinPValue:F2}");
            }
        }

        gateTrace.Add(new("MonteCarloShuffle", true, gateSw.Elapsed.TotalMilliseconds));
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
                        return ScreeningOutcome.Failed(ScreeningFailureReason.MarginalSharpe, "MarginalSharpeRejected",
                            $"{strategyType} on {symbol}/{timeframe} marginal Sharpe contribution={marginalSharpeContribution:F3} <= 0");
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
                    decimal halfKelly = Math.Clamp(kellyFull * 0.5m, 0.01m, 0.10m);

                    var kellyOptions = new BacktestOptions
                    {
                        SpreadPriceUnits = screeningOptions.SpreadPriceUnits,
                        SpreadFunction = screeningOptions.SpreadFunction,
                        CommissionPerLot = screeningOptions.CommissionPerLot,
                        SlippagePriceUnits = screeningOptions.SlippagePriceUnits,
                        SwapPerLotPerDay = screeningOptions.SwapPerLotPerDay,
                        ContractSize = screeningOptions.ContractSize,
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
                        return ScreeningOutcome.Failed(ScreeningFailureReason.PositionSizingSensitivity, "PositionSizingRejected",
                            $"{strategyType} on {symbol}/{timeframe} Kelly Sharpe={kellySharpe:F3} < 80% of fixed-lot Sharpe={fixedLotSharpe:F3}");
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
        var metrics = new ScreeningMetrics
        {
            IsWinRate = (double)trainResult.WinRate,
            IsProfitFactor = (double)trainResult.ProfitFactor,
            IsSharpeRatio = (double)trainResult.SharpeRatio,
            IsMaxDrawdownPct = (double)trainResult.MaxDrawdownPct,
            IsTotalTrades = trainResult.TotalTrades,
            OosWinRate = (double)oosResult.WinRate,
            OosProfitFactor = (double)oosResult.ProfitFactor,
            OosSharpeRatio = (double)oosResult.SharpeRatio,
            OosMaxDrawdownPct = (double)oosResult.MaxDrawdownPct,
            OosTotalTrades = oosResult.TotalTrades,
            EquityCurveR2 = r2 ?? -1.0, // -1 sentinel = unevaluated (<5 trades)
            MonteCarloPValue = pValue,
            WalkForwardWindowsPassed = walkForwardPassed,
            WalkForwardWindowsMask = walkForwardMask,
            MaxTradeTimeConcentration = maxConcentration,
            Regime = regime.ToString(),
            GenerationSource = generationSource,
            ScreenedAtUtc = DateTime.UtcNow,
            MonteCarloSeed = monteCarloSeed,
            MarginalSharpeContribution = marginalSharpeContribution ?? 0,
            KellySharpeRatio = kellySharpe,
            FixedLotSharpeRatio = fixedLotSharpe,
            GateTrace = gateTrace,
        };

        // ── Build strategy entity ──
        var inv = CultureInfo.InvariantCulture;
        var templateLabel = templateIndex > 0 ? $"-v{templateIndex + 1}" : "";
        var prefix = generationSource == "Reserve" ? "Auto-Reserve" : "Auto";
        var newStrategy = new Strategy
        {
            Name           = $"{prefix}-{strategyType}-{symbol}-{timeframe}{templateLabel}",
            Description    = string.Format(inv,
                "Auto-generated for {0} regime. IS: WR={1:P1}, PF={2:F2}, Sharpe={3:F2}. OOS: WR={4:P1}, PF={5:F2}, Sharpe={6:F2}",
                regime, trainResult.WinRate, trainResult.ProfitFactor, trainResult.SharpeRatio,
                oosResult.WinRate, oosResult.ProfitFactor, oosResult.SharpeRatio),
            StrategyType   = strategyType,
            Symbol         = symbol,
            Timeframe      = timeframe,
            ParametersJson = enrichedParams,
            Status         = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.Draft,
            CreatedAt      = DateTime.UtcNow,
            ScreeningMetricsJson = metrics.ToJson(),
        };

        return new ScreeningOutcome
        {
            Strategy = newStrategy,
            TrainResult = trainResult,
            OosResult = oosResult,
            Regime = regime,
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
        int n = allCandles.Count;
        var splitPcts = config.EffectiveSplitPcts;
        int windowCount = splitPcts.Count;
        var windows = new (int IsEnd, int OosEnd)[windowCount];
        for (int i = 0; i < windowCount; i++)
        {
            int isEnd = (int)(n * splitPcts[i]);
            int oosEnd = i + 1 < windowCount ? (int)(n * splitPcts[i + 1]) : n;
            windows[i] = (isEnd, oosEnd);
        }

        int windowsPassed = 0;
        int windowsMask = 0;
        int relaxedMinTrades = Math.Max(3, thresholds.MinTotalTrades / 3);

        for (int w = 0; w < windowCount; w++)
        {
            var (isEnd, oosEnd) = windows[w];
            if (isEnd < 40 || oosEnd - isEnd < 20) continue;

            var wfTrain = allCandles.Take(isEnd).ToList();
            var wfTest = allCandles.Skip(isEnd).Take(oosEnd - isEnd).ToList();

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
    int MinTotalTrades);

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
}

/// <summary>Result of screening a single candidate.</summary>
public sealed record ScreeningOutcome
{
    public Strategy Strategy { get; init; } = null!;
    public BacktestResult TrainResult { get; init; } = null!;
    public BacktestResult OosResult { get; init; } = null!;
    public MarketRegimeEnum Regime { get; init; }
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
}

/// <summary>Per-gate timing and pass/fail trace for screening pipeline diagnostics.</summary>
public sealed record ScreeningGateTrace(string Gate, bool Passed, double DurationMs);
