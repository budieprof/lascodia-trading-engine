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

    /// <summary>
    /// Parameter keys that MUST be present on a regime-conditional
    /// <c>ParametersJson</c> payload for this evaluator to use the overridden
    /// parameter set. When the OptimizationWorker promotes a regime-specific
    /// parameter blob that omits one of these keys, the evaluator would silently
    /// inherit the strategy's default — usually not what was intended. The
    /// StrategyWorker checks this list before swapping in regime params; when
    /// any required key is missing the params blob is rejected and the
    /// parent params are used instead, with a <c>regime_param_validation</c>
    /// metric + audit entry for operator visibility.
    ///
    /// <para>Default: empty (no required keys). Evaluators with schema
    /// expectations override to tighten — e.g. the MA-crossover evaluator
    /// returns <c>[ "FastPeriod", "SlowPeriod" ]</c>.</para>
    /// </summary>
    IReadOnlyList<string> RequiredParameterKeys => Array.Empty<string>();
}
