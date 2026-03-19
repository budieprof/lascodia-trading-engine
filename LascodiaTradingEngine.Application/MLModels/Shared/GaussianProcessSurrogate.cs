namespace LascodiaTradingEngine.Application.MLModels.Shared;

/// <summary>
/// Gaussian Process surrogate model with an RBF (squared-exponential) kernel and
/// UCB (Upper Confidence Bound) acquisition function.
///
/// Used by <c>TriggerMLHyperparamSearchCommandHandler</c> to select hyperparameter
/// candidates that maximise the expected improvement over past training runs (Bayesian
/// optimisation), replacing the pure random search with a guided exploration strategy.
///
/// All input vectors must be pre-normalised to [0, 1] per dimension by the caller.
/// </summary>
internal sealed class GaussianProcessSurrogate
{
    private readonly double _lengthScale;
    private readonly double _noise;
    private readonly double _kappa;

    private double[][]? _X;      // observed feature matrix (n × d)
    private double[]?   _alpha;  // (K + noise·I)⁻¹ y
    private double[][]? _L;      // lower Cholesky factor of K + noise·I

    /// <param name="lengthScale">RBF length-scale (controls how quickly correlation falls off).</param>
    /// <param name="noise">Observation noise variance (regularises the Gram matrix).</param>
    /// <param name="kappa">UCB exploration coefficient κ; larger → more exploration.</param>
    public GaussianProcessSurrogate(
        double lengthScale = 0.5,
        double noise       = 1e-4,
        double kappa       = 2.0)
    {
        _lengthScale = lengthScale;
        _noise       = noise;
        _kappa       = kappa;
    }

    // ── Fitting ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits the GP to the observed (X, y) pairs.
    /// Computes and caches the Cholesky factor and alpha for O(1) per-query prediction.
    /// </summary>
    public void Fit(double[][] X, double[] y)
    {
        int n = X.Length;
        _X = X;

        var K = new double[n][];
        for (int i = 0; i < n; i++)
        {
            K[i] = new double[n];
            for (int j = 0; j < n; j++)
            {
                K[i][j] = Rbf(X[i], X[j]);
                if (i == j) K[i][j] += _noise;
            }
        }

        _L     = CholeskyDecompose(K, n);
        _alpha = SolveCholesky(_L, y, n);
    }

    // ── Prediction ────────────────────────────────────────────────────────────

    /// <summary>Returns the GP posterior mean and variance at <paramref name="xStar"/>.</summary>
    public (double Mean, double Variance) Predict(double[] xStar)
    {
        if (_X is null || _alpha is null || _L is null)
            return (0.0, 1.0);

        int n = _X.Length;
        var kStar = new double[n];
        for (int i = 0; i < n; i++)
            kStar[i] = Rbf(xStar, _X[i]);

        double mean = 0;
        for (int i = 0; i < n; i++)
            mean += kStar[i] * _alpha[i];

        var    v    = ForwardSubstitution(_L, kStar, n);
        double vDotV = 0;
        for (int i = 0; i < n; i++) vDotV += v[i] * v[i];

        double variance = Math.Max(0.0, Rbf(xStar, xStar) - vDotV);
        return (mean, variance);
    }

    /// <summary>UCB acquisition value: μ(x) + κ √σ²(x).</summary>
    public double Ucb(double[] xStar)
    {
        var (mean, variance) = Predict(xStar);
        return mean + _kappa * Math.Sqrt(variance);
    }

    /// <summary>
    /// Given a pool of normalised candidates, returns the indices sorted descending by UCB.
    /// The caller selects the top-<c>n</c> for queueing.
    /// </summary>
    public IEnumerable<int> RankByUcb(double[][] candidates)
        => Enumerable.Range(0, candidates.Length)
                     .OrderByDescending(i => Ucb(candidates[i]));

    // ── RBF kernel ────────────────────────────────────────────────────────────

    private double Rbf(double[] a, double[] b)
    {
        double sq = 0;
        for (int j = 0; j < a.Length && j < b.Length; j++)
        {
            double d = a[j] - b[j];
            sq += d * d;
        }
        return Math.Exp(-sq / (2.0 * _lengthScale * _lengthScale));
    }

    // ── Cholesky helpers ──────────────────────────────────────────────────────

    private static double[][] CholeskyDecompose(double[][] A, int n)
    {
        var L = new double[n][];
        for (int i = 0; i < n; i++) L[i] = new double[n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = A[i][j];
                for (int k = 0; k < j; k++)
                    s -= L[i][k] * L[j][k];

                L[i][j] = i == j
                    ? Math.Sqrt(Math.Max(s, 1e-12))
                    : s / L[j][j];
            }
        }
        return L;
    }

    private static double[] ForwardSubstitution(double[][] L, double[] b, int n)
    {
        var x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int j = 0; j < i; j++) s -= L[i][j] * x[j];
            x[i] = s / L[i][i];
        }
        return x;
    }

    private static double[] BackSubstitution(double[][] L, double[] b, int n)
    {
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = b[i];
            for (int j = i + 1; j < n; j++) s -= L[j][i] * x[j];
            x[i] = s / L[i][i];
        }
        return x;
    }

    private static double[] SolveCholesky(double[][] L, double[] b, int n)
    {
        var y = ForwardSubstitution(L, b, n);
        return BackSubstitution(L, y, n);
    }
}
