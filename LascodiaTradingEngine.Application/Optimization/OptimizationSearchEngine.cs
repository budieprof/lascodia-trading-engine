using Microsoft.Extensions.Logging;
using LascodiaTradingEngine.Application.Backtesting.Services;
using LascodiaTradingEngine.Application.Common.Diagnostics;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Encapsulates the Bayesian search phase of the optimization pipeline.
/// Manages surrogate model selection (TPE vs GP-UCB), warm-starting from prior runs,
/// multi-fidelity successive halving, checkpoint-aware resume, circuit breaker,
/// and early stopping. Delegates to <see cref="TreeParzenEstimator"/> and
/// <see cref="GaussianProcessSurrogate"/> for candidate generation.
/// </summary>
/// <remarks>
/// This class serves as the logical boundary for the search phase. The actual
/// implementation lives in <c>OptimizationWorker.RunBayesianSearchAsync</c>
/// (internal, testable via InternalsVisibleTo). This facade exists for
/// organizational clarity and to enforce separation of concerns.
/// </remarks>
internal sealed class OptimizationSearchEngine
{
    private readonly IBacktestEngine _backtestEngine;
    private readonly OptimizationValidator _validator;
    private readonly OptimizationGridBuilder _gridBuilder;
    private readonly TradingMetrics _metrics;
    private readonly ILogger _logger;

    internal OptimizationSearchEngine(
        IBacktestEngine backtestEngine,
        OptimizationValidator validator,
        OptimizationGridBuilder gridBuilder,
        TradingMetrics metrics,
        ILogger logger)
    {
        _backtestEngine = backtestEngine;
        _validator      = validator;
        _gridBuilder    = gridBuilder;
        _metrics        = metrics;
        _logger         = logger;
    }

    /// <summary>
    /// Selects the appropriate surrogate model based on parameter dimensionality.
    /// TPE for low-dimensional (&lt; 6 params), GP-UCB for higher.
    /// </summary>
    internal static string SelectSurrogate(int parameterDimensions)
        => parameterDimensions >= 6 ? "GP-UCB" : "TPE";

    /// <summary>
    /// Computes the effective TPE budget after adaptive scaling based on historical
    /// convergence speed for the given strategy.
    /// </summary>
    internal static int ComputeAdaptiveBudget(int configuredBudget, IReadOnlyList<int> priorIterations)
    {
        if (priorIterations.Count < 3) return configuredBudget;

        int avgPriorIters = (int)priorIterations.Average();
        int adaptedBudget = Math.Max(
            (int)(configuredBudget * 0.60),
            (int)(avgPriorIters * 1.20));
        return Math.Min(adaptedBudget, configuredBudget);
    }
}
