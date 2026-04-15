using LascodiaTradingEngine.Application.Common.Attributes;
using LascodiaTradingEngine.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace LascodiaTradingEngine.Application.StrategyGeneration;

/// <summary>
/// Resolves validation-queue priority and queue-key identity for newly generated candidates.
/// </summary>
public interface IStrategyValidationPriorityResolver
{
    bool IsEliteFastTrackCandidate(
        ScreeningOutcome candidate,
        GenerationConfig config,
        double thresholdMult,
        double minR2,
        double maxP,
        IReadOnlyList<double> walkForwardSplits);

    int ResolvePriority(
        ScreeningOutcome candidate,
        CandidateSelectionScoreBreakdown selectionScore,
        bool isElite,
        int fastTrackPriorityBoost);

    string? BuildInitialBacktestQueueKey(ScreeningOutcome candidate);
    string BuildWalkForwardQueueKey(BacktestRun run);
}

[RegisterService(ServiceLifetime.Singleton, typeof(IStrategyValidationPriorityResolver))]
/// <summary>
/// Default priority resolver for backtest and walk-forward runs created by the generator.
/// </summary>
public sealed class StrategyValidationPriorityResolver : IStrategyValidationPriorityResolver
{
    public bool IsEliteFastTrackCandidate(
        ScreeningOutcome candidate,
        GenerationConfig config,
        double thresholdMult,
        double minR2,
        double maxP,
        IReadOnlyList<double> walkForwardSplits)
    {
        if (candidate.Metrics == null)
            return false;

        int requiredWalkForwardPasses = walkForwardSplits.Count;
        return candidate.Metrics.IsSharpeRatio >= config.MinSharpe * thresholdMult
            && candidate.Metrics.OosSharpeRatio >= config.MinSharpe * thresholdMult
            && candidate.Metrics.EquityCurveR2 >= minR2
            && candidate.Metrics.MonteCarloPValue is >= 0 and var pVal && pVal <= maxP
            && candidate.Metrics.WalkForwardWindowsPassed >= requiredWalkForwardPasses;
    }

    public int ResolvePriority(
        ScreeningOutcome candidate,
        CandidateSelectionScoreBreakdown selectionScore,
        bool isElite,
        int fastTrackPriorityBoost)
    {
        // Use the richer selection score when available, but never let the resulting priority
        // fall below a simple Sharpe-derived baseline.
        int priority = (int)Math.Round(selectionScore.TotalScore, MidpointRounding.AwayFromZero);
        priority = Math.Max(priority, (int)Math.Round((double)candidate.TrainResult.SharpeRatio * 100d, MidpointRounding.AwayFromZero));

        if (isElite)
            priority += fastTrackPriorityBoost;

        return priority;
    }

    public string? BuildInitialBacktestQueueKey(ScreeningOutcome candidate)
        => string.IsNullOrWhiteSpace(candidate.Strategy.GenerationCandidateId)
            ? null
            : $"strategy-candidate:{candidate.Strategy.GenerationCandidateId}:backtest:initial";

    public string BuildWalkForwardQueueKey(BacktestRun run)
        => $"backtest:{run.Id}:walk_forward";
}
