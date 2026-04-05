using System.Text.Json;
using LascodiaTradingEngine.Application.Backtesting.Models;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Optimization;

internal static class OptimizationFollowUpQualityEvaluator
{
    internal static bool IsBacktestQualitySufficient(
        BacktestRun run,
        decimal minHealthScore,
        int minTrades,
        out string reason)
    {
        reason = string.Empty;

        if (run.Status != RunStatus.Completed)
        {
            reason = "backtest follow-up did not complete successfully";
            return false;
        }

        if (string.IsNullOrWhiteSpace(run.ResultJson))
        {
            reason = "backtest follow-up completed without persisted result metrics";
            return false;
        }

        BacktestResult? result;
        try
        {
            result = JsonSerializer.Deserialize<BacktestResult>(run.ResultJson);
        }
        catch
        {
            reason = "backtest follow-up result metrics were malformed";
            return false;
        }

        if (result is null)
        {
            reason = "backtest follow-up result metrics were missing";
            return false;
        }

        if (result.TotalTrades < minTrades)
        {
            reason = $"backtest follow-up produced too few trades ({result.TotalTrades} < {minTrades})";
            return false;
        }

        decimal healthScore = OptimizationHealthScorer.ComputeHealthScore(result);
        if (healthScore < minHealthScore)
        {
            reason = $"backtest follow-up health score too low ({healthScore:F2} < {minHealthScore:F2})";
            return false;
        }

        return true;
    }

    internal static bool IsWalkForwardQualitySufficient(
        WalkForwardRun run,
        decimal maxCoefficientOfVariation,
        out string reason)
    {
        reason = string.Empty;

        if (run.Status != RunStatus.Completed)
        {
            reason = "walk-forward follow-up did not complete successfully";
            return false;
        }

        if (!run.AverageOutOfSampleScore.HasValue)
        {
            reason = "walk-forward follow-up did not persist an average OOS score";
            return false;
        }

        decimal averageScore = run.AverageOutOfSampleScore.Value;
        if (averageScore <= 0m)
        {
            reason = $"walk-forward average OOS score must be positive ({averageScore:F2})";
            return false;
        }

        decimal scoreStdDev = run.ScoreConsistency ?? 0m;
        decimal coefficientOfVariation = scoreStdDev / Math.Abs(averageScore);
        if (coefficientOfVariation > maxCoefficientOfVariation)
        {
            reason = $"walk-forward score dispersion too high (CV={coefficientOfVariation:F2} > {maxCoefficientOfVariation:F2})";
            return false;
        }

        return true;
    }
}
