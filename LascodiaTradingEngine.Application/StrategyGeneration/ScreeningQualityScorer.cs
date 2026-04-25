using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Application.Services;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Shared candidate quality scorecard used by screening, audit logging, surrogate learning,
/// validation priority, and promotion gates.
/// </summary>
internal static class ScreeningQualityScorer
{
    public static double ComputeScore(
        BacktestResult? trainResult,
        BacktestResult? oosResult,
        double? equityCurveR2 = null,
        int? walkForwardPassed = null,
        int? walkForwardRequired = null,
        double? monteCarloPValue = null,
        double? shufflePValue = null,
        double? maxTradeTimeConcentration = null,
        double? marginalSharpeContribution = null,
        double? kellySharpe = null,
        double? fixedLotSharpe = null)
    {
        double raw = SegmentScore(trainResult, 0.40) + SegmentScore(oosResult, 0.60);

        if (equityCurveR2 is >= 0)
            raw += Math.Clamp(equityCurveR2.Value, 0, 1) * 0.55;

        if (walkForwardPassed.HasValue && walkForwardRequired is > 0)
            raw += Math.Clamp(walkForwardPassed.Value / walkForwardRequired.Value, 0, 1) * 0.65;

        if (monteCarloPValue is >= 0)
            raw += Math.Clamp(0.10 - monteCarloPValue.Value, -0.25, 0.10) * 2.0;

        if (shufflePValue is >= 0)
            raw += Math.Clamp(0.10 - shufflePValue.Value, -0.25, 0.10) * 1.25;

        if (maxTradeTimeConcentration is > 0)
            raw -= Math.Clamp(maxTradeTimeConcentration.Value - 0.35, 0, 0.65) * 0.75;

        if (marginalSharpeContribution.HasValue)
            raw += Math.Clamp(marginalSharpeContribution.Value, -1.0, 1.0) * 0.55;

        if (kellySharpe.HasValue && fixedLotSharpe is > 0)
        {
            double sizingRatio = kellySharpe.Value / Math.Max(0.01, fixedLotSharpe.Value);
            raw += Math.Clamp(sizingRatio - 0.80, -0.40, 0.60) * 0.35;
        }

        return Math.Round(Math.Clamp(50.0 + raw * 12.0, 0.0, 100.0), 3);
    }

    public static double ComputeCalibrationMultiplier(HaircutRatios? haircuts)
    {
        if (haircuts is not { SampleCount: >= 5 or < 0 })
            return 1.0;

        double drawdownRelief = 1.0 / Math.Max(0.25, haircuts.DrawdownInflation);
        double multiplier =
            Math.Clamp(haircuts.WinRateHaircut, 0.50, 1.20) * 0.25
            + Math.Clamp(haircuts.ProfitFactorHaircut, 0.50, 1.20) * 0.30
            + Math.Clamp(haircuts.SharpeHaircut, 0.50, 1.20) * 0.35
            + Math.Clamp(drawdownRelief, 0.50, 1.20) * 0.10;

        return Math.Round(Math.Clamp(multiplier, 0.65, 1.15), 6);
    }

    public static double ApplyCalibration(double rawScore, double calibrationMultiplier)
        => Math.Round(Math.Clamp(50.0 + (rawScore - 50.0) * calibrationMultiplier, 0.0, 100.0), 3);

    public static string ComputeBand(double score) => score switch
    {
        >= 85 => "A",
        >= 72 => "B",
        >= 60 => "C",
        >= 45 => "D",
        _ => "F",
    };

    public static bool IsNearMiss(ScreeningOutcome result)
    {
        if (result.Strategy is null || result.TrainResult is null || result.TrainResult.TotalTrades <= 0)
            return false;

        return result.Failure is ScreeningFailureReason.IsThreshold
            or ScreeningFailureReason.OosThreshold
            or ScreeningFailureReason.Degradation
            or ScreeningFailureReason.EquityCurveR2
            or ScreeningFailureReason.TimeConcentration
            or ScreeningFailureReason.WalkForward
            or ScreeningFailureReason.MonteCarloSignFlip
            or ScreeningFailureReason.MonteCarloShuffle
            or ScreeningFailureReason.MarginalSharpe
            or ScreeningFailureReason.PositionSizingSensitivity
            or ScreeningFailureReason.DeflatedSharpe
            or ScreeningFailureReason.LookaheadAudit;
    }

    private static double SegmentScore(BacktestResult? result, double weight)
    {
        if (result is null || result.TotalTrades <= 0)
            return -2.5 * weight;

        double profitFactor = Math.Max(0.01, (double)result.ProfitFactor);
        double tradeEvidence = Math.Min(Math.Log10(result.TotalTrades + 1), 2.0) * 0.20;

        return weight * (
            ((double)result.SharpeRatio * 1.25)
            + (Math.Log(profitFactor) * 0.80)
            + (((double)result.WinRate - 0.50) * 1.50)
            - ((double)result.MaxDrawdownPct * 2.00)
            + tradeEvidence);
    }
}
