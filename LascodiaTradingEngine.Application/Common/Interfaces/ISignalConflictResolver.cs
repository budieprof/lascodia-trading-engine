using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Candidate signal produced by a strategy evaluator, awaiting conflict resolution
/// before being published as a TradeSignal.
/// </summary>
public record PendingSignal(
    long StrategyId,
    string Symbol,
    Timeframe Timeframe,
    StrategyType StrategyType,
    TradeDirection Direction,
    decimal EntryPrice,
    decimal? StopLoss,
    decimal? TakeProfit,
    decimal SuggestedLotSize,
    decimal Confidence,
    decimal? MLConfidenceScore,
    long? MLModelId,
    decimal? EstimatedCapacityLots,
    decimal? StrategySharpeRatio,
    DateTime ExpiresAt);

/// <summary>
/// Resolves conflicts when multiple strategies fire signals for the same symbol
/// within the same tick. Same-direction: keeps highest-scoring. Opposing-direction: suppresses both.
/// </summary>
public interface ISignalConflictResolver
{
    /// <summary>
    /// Given all pending signals for a single tick across all strategies,
    /// returns only the signals that should be published (conflict-free winners).
    /// </summary>
    IReadOnlyList<PendingSignal> Resolve(IReadOnlyList<PendingSignal> pendingSignals);
}
