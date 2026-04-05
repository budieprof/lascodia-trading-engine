using LascodiaTradingEngine.Application.Backtesting.Models;

namespace LascodiaTradingEngine.Application.Optimization;

/// <summary>
/// Implements ParEGO (Parabolic Efficient Global Optimization) scalarization for
/// multi-objective Bayesian optimization. Each surrogate batch uses a different
/// random weight vector on the objective simplex, causing the surrogate to explore
/// different regions of the Pareto front across iterations.
///
/// Uses augmented Chebyshev scalarization which, unlike linear scalarization (the
/// current health score), can reach non-convex regions of the Pareto front.
///
/// Three objectives: Sharpe ratio (maximize), max drawdown (minimize), win rate (maximize).
/// All are normalized to [0, 1] before scalarization so weights are comparable.
/// </summary>
internal sealed class ParegoScalarizer
{
    private readonly DeterministicRandom _rng;
    private readonly double _rho;
    private readonly double _sharpeMin;
    private readonly double _sharpeMax;
    private readonly double _maxDrawdownCeiling;

    // Volatile ensures cross-thread visibility after RotateWeights().
    // The array itself is immutable after assignment (new array each rotation).
    private volatile double[] _currentWeights;

    /// <summary>Current weight vector (for diagnostics/logging).</summary>
    internal IReadOnlyList<double> CurrentWeights => _currentWeights;

    /// <summary>
    /// Creates a ParEGO scalarizer with the given seed for reproducible weight sampling.
    /// </summary>
    /// <param name="seed">Deterministic seed for weight vector generation.</param>
    /// <param name="rho">Augmentation parameter (default 0.05). Controls the small linear
    /// component added to the Chebyshev scalarization to break ties and ensure strict
    /// Pareto optimality of the solution.</param>
    /// <param name="sharpeMin">Lower bound for Sharpe normalization (default -2).</param>
    /// <param name="sharpeMax">Upper bound for Sharpe normalization (default 4).</param>
    /// <param name="maxDrawdownCeiling">Upper bound for drawdown normalization as percentage (default 50).</param>
    internal ParegoScalarizer(
        int seed,
        double rho = 0.05,
        double sharpeMin = -2.0,
        double sharpeMax = 4.0,
        double maxDrawdownCeiling = 50.0)
    {
        _rng = new DeterministicRandom(seed);
        _rho = rho;
        _sharpeMin = sharpeMin;
        _sharpeMax = sharpeMax;
        _maxDrawdownCeiling = maxDrawdownCeiling;
        _currentWeights = [1.0 / 3, 1.0 / 3, 1.0 / 3]; // Initial uniform weights
    }

    /// <summary>
    /// Samples a new random weight vector on the 3-simplex using the Dirichlet(1,1,1)
    /// distribution (uniform over the simplex). Call this before each surrogate batch
    /// to rotate the exploration direction. Returns the new weight vector so callers
    /// can capture it for use inside parallel loops (avoiding cross-thread reads).
    /// </summary>
    internal double[] RotateWeights()
    {
        // Dirichlet(1,1,1) = uniform on simplex: sample 3 Exponential(1) and normalize.
        // Exponential(1) = -ln(U) where U ~ Uniform(0,1).
        double e1 = -Math.Log(Math.Max(_rng.NextDouble(), 1e-15));
        double e2 = -Math.Log(Math.Max(_rng.NextDouble(), 1e-15));
        double e3 = -Math.Log(Math.Max(_rng.NextDouble(), 1e-15));
        double sum = e1 + e2 + e3;

        var weights = new[] { e1 / sum, e2 / sum, e3 / sum };
        _currentWeights = weights;
        return weights;
    }

    /// <summary>
    /// Scalarizes a backtest result using augmented Chebyshev with the current weights.
    /// </summary>
    internal double Scalarize(BacktestResult result)
        => ScalarizeCore(
            (double)result.SharpeRatio,
            (double)result.MaxDrawdownPct,
            (double)result.WinRate,
            _currentWeights);

    /// <summary>
    /// Scalarizes raw metric values (for use when BacktestResult is not available,
    /// e.g., during warm-start from stored scores).
    /// </summary>
    internal double Scalarize(decimal sharpeRatio, decimal maxDrawdownPct, decimal winRate)
        => ScalarizeCore(
            (double)sharpeRatio,
            (double)maxDrawdownPct,
            (double)winRate,
            _currentWeights);

    /// <summary>
    /// Thread-safe overload for raw metric values with an explicit weight vector.
    /// Useful when the caller captured the active weights before entering a parallel
    /// section or needs to replay prior observations against a known scalarization.
    /// </summary>
    internal double Scalarize(decimal sharpeRatio, decimal maxDrawdownPct, decimal winRate, double[] weights)
        => ScalarizeCore(
            (double)sharpeRatio,
            (double)maxDrawdownPct,
            (double)winRate,
            weights);

    /// <summary>
    /// Thread-safe overload that accepts an explicit weight vector (captured before
    /// a <c>Parallel.ForEachAsync</c> block). Avoids reading the volatile field from
    /// multiple threads during parallel evaluation.
    /// </summary>
    internal double Scalarize(BacktestResult result, double[] weights)
        => ScalarizeCore(
            (double)result.SharpeRatio,
            (double)result.MaxDrawdownPct,
            (double)result.WinRate,
            weights);

    /// <summary>
    /// Falls back to the fixed health score when ParEGO is disabled.
    /// This makes the caller's code identical regardless of whether ParEGO is active.
    /// </summary>
    internal static double HealthScoreFallback(BacktestResult result)
        => (double)OptimizationHealthScorer.ComputeHealthScore(result);

    /// <summary>
    /// Single implementation of augmented Chebyshev scalarization.
    ///
    /// <c>score = -max_i(w_i * (1 - f_i)) + rho * sum(w_i * f_i)</c>
    ///
    /// The max term drives optimization toward the Pareto front; the rho term
    /// (small linear component) breaks ties to ensure strict Pareto optimality.
    /// Result is oriented so that higher = better (consistent with surrogate maximization).
    /// </summary>
    private double ScalarizeCore(
        double sharpeRatio, double maxDrawdownPct, double winRate,
        double[] weights)
    {
        // Normalize objectives to [0, 1] so weights are comparable.
        // Bounds are configurable to support different asset classes.
        double sharpeRange = _sharpeMax - _sharpeMin;
        double normSharpe = sharpeRange > 0
            ? Math.Clamp((sharpeRatio - _sharpeMin) / sharpeRange, 0.0, 1.0)
            : 0.5;
        double normDD = _maxDrawdownCeiling > 0
            ? Math.Clamp(1.0 - maxDrawdownPct / _maxDrawdownCeiling, 0.0, 1.0)
            : 0.5;
        double normWR = Math.Clamp(winRate, 0.0, 1.0);

        double f0 = normSharpe, f1 = normDD, f2 = normWR;

        // Augmented Chebyshev for maximization: minimize max_i(w_i * (ideal_i - f_i))
        // with ideal = 1.0 for all objectives. Negated so higher = better.
        double chebyshev = Math.Max(
            weights[0] * (1.0 - f0),
            Math.Max(weights[1] * (1.0 - f1), weights[2] * (1.0 - f2)));
        double linear = weights[0] * f0 + weights[1] * f1 + weights[2] * f2;

        return -chebyshev + _rho * linear;
    }
}
