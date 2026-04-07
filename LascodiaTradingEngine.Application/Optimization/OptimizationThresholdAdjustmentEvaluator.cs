using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;

namespace LascodiaTradingEngine.Application.Optimization;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class OptimizationThresholdAdjustmentEvaluator
{
    internal sealed record ThresholdAdjustmentResult(
        decimal EffectiveMinScore,
        decimal EffectiveImprovementThreshold,
        bool KellySizingOk,
        double KellySharpe,
        double FixedLotSharpe,
        (double WinRate, double ProfitFactor, double Sharpe, double Drawdown) AssetClassMultipliers,
        bool EquityCurveOk,
        bool TimeConcentrationOk,
        bool GenesisRegressionOk,
        IReadOnlyList<(string Gate, double DurationMs)> GateTimings);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OptimizationValidator _validator;
    private readonly ILogger<OptimizationThresholdAdjustmentEvaluator> _logger;

    public OptimizationThresholdAdjustmentEvaluator(
        IServiceScopeFactory scopeFactory,
        OptimizationValidator validator,
        ILogger<OptimizationThresholdAdjustmentEvaluator> logger)
    {
        _scopeFactory = scopeFactory;
        _validator = validator;
        _logger = logger;
    }

    internal async Task<ThresholdAdjustmentResult> EvaluateAsync(
        Strategy strategy,
        string candidateParamsJson,
        BacktestResult oosResult,
        List<Candle> testCandles,
        BacktestOptions screeningOptions,
        ValidationConfig config,
        CurrencyPair? pairInfo,
        long runId,
        CancellationToken ct)
    {
        var gateTimings = new List<(string Gate, double DurationMs)>();
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
                    effectiveMinScore = config.AutoApprovalMinHealthScore /
                        (decimal)Math.Max(0.5, haircuts.SharpeHaircut);
                    _logger.LogDebug(
                        "OptimizationWorker: haircut-adjusted approval threshold: {Original:F2} -> {Adjusted:F2}",
                        config.AutoApprovalMinHealthScore,
                        effectiveMinScore);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: haircut load failed (non-fatal)");
        }

        var assetClass = StrategyGenerationHelpers.ClassifyAsset(strategy.Symbol, pairInfo);
        var acMultipliers = StrategyGenerationHelpers.GetAssetClassThresholdMultipliers(assetClass);
        effectiveMinScore *= (decimal)Math.Max(acMultipliers.Item3, acMultipliers.Item2);

        bool kellySizingOk = true;
        double kellySharpe = 0;
        double fixedLotSharpe = (double)oosResult.SharpeRatio;
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
                            decimal riskPerUnit = Math.Max(
                                0.001m * signal.EntryPrice,
                                Math.Abs(signal.EntryPrice - (signal.StopLoss ?? signal.EntryPrice)));
                            return Math.Clamp(balance * halfKelly / (screeningOptions.ContractSize * riskPerUnit), 0.01m, 10m);
                        },
                    };
                    var kellyResult = await _validator.RunWithTimeoutAsync(
                        strategy,
                        candidateParamsJson,
                        testCandles,
                        kellyOptions,
                        config.ScreeningTimeoutSeconds,
                        ct);
                    kellySharpe = (double)kellyResult.SharpeRatio;
                    kellySizingOk = kellySharpe >= fixedLotSharpe * 0.80;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OptimizationWorker: Kelly sizing check failed (non-fatal)");
            }
        }

        var gateSw = Stopwatch.StartNew();
        bool equityCurveOk = true;
        if (oosResult.Trades is { Count: >= 5 })
        {
            double r2 = StrategyScreeningEngine.ComputeEquityCurveR2(oosResult.Trades, config.ScreeningInitialBalance);
            if (r2 < config.MinEquityCurveR2)
            {
                equityCurveOk = false;
                _logger.LogDebug(
                    "OptimizationWorker: equity curve R²={R2:F3} below {Min:F2}",
                    r2,
                    config.MinEquityCurveR2);
            }
        }
        gateSw.Stop();
        gateTimings.Add(("equity_curve_r2", gateSw.Elapsed.TotalMilliseconds));

        gateSw.Restart();
        bool timeConcentrationOk = true;
        if (oosResult.Trades is { Count: >= 10 })
        {
            double concentration = StrategyScreeningEngine.ComputeTradeTimeConcentration(oosResult.Trades);
            if (concentration > config.MaxTradeTimeConcentration)
            {
                timeConcentrationOk = false;
                _logger.LogDebug(
                    "OptimizationWorker: trade time concentration={Conc:P1} above {Max:P1}",
                    concentration,
                    config.MaxTradeTimeConcentration);
            }
        }
        gateSw.Stop();
        gateTimings.Add(("time_concentration", gateSw.Elapsed.TotalMilliseconds));

        bool genesisRegressionOk = true;
        try
        {
            var screeningJson = strategy.ScreeningMetricsJson;
            if (!string.IsNullOrEmpty(screeningJson))
            {
                var genesis = ScreeningMetrics.FromJson(screeningJson);
                if (genesis != null && genesis.OosSharpeRatio > 0)
                {
                    double genesisOosSharpe = genesis.OosSharpeRatio;
                    if (genesisOosSharpe > 0 && (double)oosResult.SharpeRatio < genesisOosSharpe * 0.80)
                    {
                        genesisRegressionOk = false;
                        _logger.LogDebug(
                            "OptimizationWorker: genesis regression - OOS Sharpe {Current:F2} < 80% of original {Genesis:F2}",
                            oosResult.SharpeRatio,
                            genesisOosSharpe);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OptimizationWorker: genesis regression check failed for run {RunId}", runId);
        }

        return new ThresholdAdjustmentResult(
            effectiveMinScore,
            effectiveImprovementThreshold,
            kellySizingOk,
            kellySharpe,
            fixedLotSharpe,
            acMultipliers,
            equityCurveOk,
            timeConcentrationOk,
            genesisRegressionOk,
            gateTimings);
    }
}
