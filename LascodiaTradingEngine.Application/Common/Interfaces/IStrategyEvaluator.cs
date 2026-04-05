using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

/// <summary>
/// Evaluates a specific strategy type against market data to produce trade signals.
/// Implementations are resolved by <see cref="StrategyType"/> at runtime.
/// </summary>
public interface IStrategyEvaluator
{
    /// <summary>The strategy type this evaluator handles.</summary>
    StrategyType StrategyType { get; }

    /// <summary>
    /// Minimum number of closed candles required for this evaluator to produce a valid signal.
    /// The caller must fetch at least this many candles before invoking <see cref="EvaluateAsync"/>.
    /// </summary>
    int MinRequiredCandles(Strategy strategy);

    /// <summary>
    /// Evaluates the strategy against the provided candles and live price.
    /// Returns a TradeSignal if conditions are met, or null if no setup exists.
    /// </summary>
    Task<TradeSignal?> EvaluateAsync(
        Strategy strategy,
        IReadOnlyList<Candle> candles,
        (decimal Bid, decimal Ask) currentPrice,
        CancellationToken cancellationToken);
}
