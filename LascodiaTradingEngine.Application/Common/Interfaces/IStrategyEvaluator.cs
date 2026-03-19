using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;

namespace LascodiaTradingEngine.Application.Common.Interfaces;

public interface IStrategyEvaluator
{
    StrategyType StrategyType { get; }

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
