using System.Globalization;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Application.Services;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using static LascodiaTradingEngine.Application.StrategyGeneration.StrategyGenerationHelpers;
using MarketRegimeEnum = LascodiaTradingEngine.Domain.Enums.MarketRegime;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyScreeningArtifactFactory))]
internal sealed class StrategyScreeningArtifactFactory : IStrategyScreeningArtifactFactory
{
    public int ResolveMonteCarloSeed(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string enrichedParams,
        IReadOnlyList<Candle> allCandles,
        DateTime utcNow)
        => ResolveDeterministicSeed(
            strategyType,
            symbol,
            timeframe,
            enrichedParams,
            allCandles.Count > 0 ? allCandles[^1].Timestamp.Date : utcNow.Date);

    public ScreeningMetrics BuildMetrics(
        BacktestResult trainResult,
        BacktestResult oosResult,
        double? r2,
        double pValue,
        double shufflePValue,
        int walkForwardPassed,
        int walkForwardMask,
        double maxConcentration,
        MarketRegimeEnum targetRegime,
        MarketRegimeEnum observedRegime,
        string generationSource,
        string? reserveTargetRegime,
        int monteCarloSeed,
        double? marginalSharpeContribution,
        double kellySharpe,
        double fixedLotSharpe,
        HaircutRatios? appliedHaircuts,
        IReadOnlyList<ScreeningGateTrace> gateTrace,
        DateTime screenedAtUtc)
        => new()
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
            EquityCurveR2 = r2 ?? -1.0,
            MonteCarloPValue = pValue,
            ShufflePValue = shufflePValue,
            WalkForwardWindowsPassed = walkForwardPassed,
            WalkForwardWindowsMask = walkForwardMask,
            MaxTradeTimeConcentration = maxConcentration,
            Regime = targetRegime.ToString(),
            GenerationSource = generationSource,
            ObservedRegime = observedRegime.ToString(),
            ReserveTargetRegime = reserveTargetRegime,
            ScreenedAtUtc = screenedAtUtc,
            MonteCarloSeed = monteCarloSeed,
            MarginalSharpeContribution = marginalSharpeContribution ?? 0,
            KellySharpeRatio = kellySharpe,
            FixedLotSharpeRatio = fixedLotSharpe,
            LiveHaircutApplied = appliedHaircuts is { SampleCount: >= 5 or < 0 },
            WinRateHaircutApplied = appliedHaircuts?.WinRateHaircut ?? 1.0,
            ProfitFactorHaircutApplied = appliedHaircuts?.ProfitFactorHaircut ?? 1.0,
            SharpeHaircutApplied = appliedHaircuts?.SharpeHaircut ?? 1.0,
            DrawdownInflationApplied = appliedHaircuts?.DrawdownInflation ?? 1.0,
            GateTrace = gateTrace.ToList(),
        };

    public Strategy BuildStrategy(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string enrichedParams,
        int templateIndex,
        string generationSource,
        MarketRegimeEnum targetRegime,
        MarketRegimeEnum observedRegime,
        BacktestResult trainResult,
        BacktestResult oosResult,
        ScreeningMetrics metrics,
        DateTime createdAtUtc)
    {
        var inv = CultureInfo.InvariantCulture;
        var templateLabel = templateIndex > 0 ? $"-v{templateIndex + 1}" : "";
        var prefix = generationSource == "Reserve" ? "Auto-Reserve" : "Auto";
        var description = generationSource == "Reserve"
            ? string.Format(
                inv,
                "Auto-generated reserve candidate for {0} regime while observing {1}. IS: WR={2:P1}, PF={3:F2}, Sharpe={4:F2}. OOS: WR={5:P1}, PF={6:F2}, Sharpe={7:F2}",
                targetRegime,
                observedRegime,
                trainResult.WinRate,
                trainResult.ProfitFactor,
                trainResult.SharpeRatio,
                oosResult.WinRate,
                oosResult.ProfitFactor,
                oosResult.SharpeRatio)
            : string.Format(
                inv,
                "Auto-generated for {0} regime. IS: WR={1:P1}, PF={2:F2}, Sharpe={3:F2}. OOS: WR={4:P1}, PF={5:F2}, Sharpe={6:F2}",
                targetRegime,
                trainResult.WinRate,
                trainResult.ProfitFactor,
                trainResult.SharpeRatio,
                oosResult.WinRate,
                oosResult.ProfitFactor,
                oosResult.SharpeRatio);

        return new Strategy
        {
            Name = $"{prefix}-{strategyType}-{symbol}-{timeframe}{templateLabel}",
            Description = description,
            StrategyType = strategyType,
            Symbol = symbol,
            Timeframe = timeframe,
            ParametersJson = enrichedParams,
            Status = StrategyStatus.Paused,
            LifecycleStage = StrategyLifecycleStage.Draft,
            CreatedAt = createdAtUtc,
            ScreeningMetricsJson = metrics.ToJson(),
        };
    }

    private static int ResolveDeterministicSeed(
        StrategyType strategyType,
        string symbol,
        Timeframe timeframe,
        string parametersJson,
        DateTime date)
    {
        var raw = $"{strategyType}|{symbol}|{timeframe}|{parametersJson}|{date:yyyyMMdd}";
        return raw.GetHashCode(StringComparison.Ordinal);
    }
}
